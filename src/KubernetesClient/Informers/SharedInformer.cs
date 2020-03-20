using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using k8s.Informers.Cache;
using k8s.Informers.Notifications;
using Microsoft.Extensions.Logging;

namespace k8s.Informers
{

    /// <summary>
    /// Wraps a single master informer (such as Kubernetes API connection) for rebroadcast to multiple internal subscribers
    /// and is responsible for managing and synchronizing cache
    /// </summary>
    /// <remarks>
    /// <para>
    /// Allows rebroadcasting of single informer provided by masterInformer to multiple internal subscribers.
    /// Lazy loading semantics apply where subscription to master informer is only established when there's at least one attached observer, and it is closed if all observers disconnect
    ///</para>
    /// <para>
    /// <see cref="SharedInformer{TKey,TResource}"/> is considered the sole owner of managing the cache. Since cache is used as the source of truth for "List" operations of any downstream subscribers,
    /// any attempt to modify cache externally will result in desynchronization. Shared informer will only start emitting events downstream after cache has been synchronized
    /// (after first List).
    /// </para>
    /// </remarks>
    /// <seealso cref="IInformer{TResource}"/>
    public class SharedInformer<TKey, TResource> : IInformer<TResource>
    {
        private readonly ICache<TKey, TResource> _cache;
        private readonly ILogger _logger;
        private readonly Func<TResource, TKey> _keySelector;
        private int _subscribers;

        private IDisposable _masterSubscription;
        private TaskCompletionSource<bool> _cacheSynchronized = new TaskCompletionSource<bool>();
        readonly CountdownEvent _waitingSubscribers = new CountdownEvent(0);
        private readonly object _lock = new object();
        private IScheduler _masterScheduler;
        private IConnectableObservable<CacheSynchronized<ResourceEvent<TResource>>> _masterObservable;

        private IScheduler _masterProcessScheduler = new EventLoopScheduler();
        public SharedInformer(IInformer<TResource> masterInformer, ILogger logger, Func<TResource, TKey> keySelector)
            : this(masterInformer, logger, keySelector,new SimpleCache<TKey, TResource>(), null)
        {

        }
        public SharedInformer(IInformer<TResource> masterInformer, ILogger logger, Func<TResource, TKey> keySelector, ICache<TKey,TResource> cache, IScheduler scheduler = null)
        {

            _cache = cache;
            _masterScheduler = scheduler ?? new EventLoopScheduler();
            _logger = logger;
            _keySelector = keySelector;
            _masterObservable = masterInformer
                .GetResource(ResourceStreamType.ListWatch)

                .ObserveOn(_masterScheduler)
                .Do(x => _logger.LogTrace($"Received message from upstream {x}"))
                .SynchronizeCache(_cache, _keySelector)
                .Do(msg =>
                {
                    // cache is synchronized as soon as we get at least one message past this point
                    _logger.LogTrace($"Cache v{cache.Version} synchronized: {msg} ");
                    _cacheSynchronized.TrySetResult(true);
                    _logger.LogTrace("_cacheSynchronized.TrySetResult(true)");
                })
                .Do(_ => YieldToWaitingSubscribers())
                .ObserveOn(Scheduler.Immediate) // immediate ensures that all caches operations are done atomically
                .ObserveOn(_masterScheduler)
                .Catch<CacheSynchronized<ResourceEvent<TResource>>, Exception>(e =>
                {
                    _cacheSynchronized.TrySetException(e);
                    // _cacheSynchronized.OnError(e);
                    return Observable.Throw<CacheSynchronized<ResourceEvent<TResource>>>(e);
                })
                .Finally(() => _cacheSynchronized.TrySetResult(false))
                // .SubscribeOn(_masterScheduler)
                .Publish();
        }


        [DebuggerStepThrough]
        void YieldToWaitingSubscribers()
        {
            _logger.LogTrace("Waiting for subscribers to attach to stream");
            while (_waitingSubscribers.CurrentCount > 0)
            {
                // give a chance to any joining subscribers to realign with the broadcast stream

                _waitingSubscribers.Wait(100);
            }
            _logger.LogTrace("Finished yielding to subscribers");

        }


        public IObservable<ResourceEvent<TResource>> GetResource(ResourceStreamType type)
        {
            var childScheduler = new EventLoopScheduler(); // dedicated thread for the child on which all messages are syncrhonized
            return Observable.Defer(async () =>
                {
                    AddSubscriber();
                    _logger.LogTrace("Subscriber awaiting cache synchronization before attaching");

                    var isCacheSynchronized = await _cacheSynchronized.Task;
                    if (!isCacheSynchronized) // really this only happens if the reset is the master completes before first reset, in which case the downstream subscriber gets nothing
                        return Observable.Empty<ResourceEvent<TResource>>();
                    // we use lock to pause any processing of the broadcaster while we're attaching to the stream so proper alignment can be made

                    _logger.LogTrace("Subscriber attaching to broadcaster");
                    return Observable.Create<ResourceEvent<TResource>>(observer =>
                    {
                        var broadcasterAttachment = Disposable.Empty;
                        var cacheSnapshot = _cache.Snapshot();
                        if (type.HasFlag(ResourceStreamType.List))
                        {
                            _logger.LogTrace($"Flushing contents of cache version {cacheSnapshot.Version}");
                            _cache.Values
                                .ToReset(type == ResourceStreamType.ListWatch)
                                .ToObservable()
                                .Concat(Observable.Never<ResourceEvent<TResource>>())
                                .ObserveOn(Scheduler.Immediate)
                                .Subscribe(observer);
                        }

                        if (type.HasFlag(ResourceStreamType.Watch))
                        {
                            broadcasterAttachment = _masterObservable
                                // we could be ahead of broadcaster because we initialized from cache which gets updated before the message are sent to broadcaster
                                // this logic realigns us at the correct point with the broadcaster
                                .Do(x => _logger.LogTrace($"Received from broadcaster {x}"))
                                .SkipWhile(x => x.MessageNumber <= cacheSnapshot.Version)
                                .Select(x => x.Value)
                                .Do(x => _logger.LogTrace($"Aligned with broadcaster {x}"))
                                .SubscribeOn(_masterScheduler)
                                .ObserveOn(childScheduler)
                                .Subscribe(observer, () =>
                                {
                                    _logger.LogTrace("Child OnComplete");
                                    RemoveSubscriber();
                                });
                        }
                        else
                        {
                            observer.OnCompleted();
                        }

                        // let broadcaster know we're done attaching to stream so it can resume it's regular work
                        _logger.LogTrace("Finished attaching to stream - signalling to resume");
                        lock (_lock)
                        {
                            _waitingSubscribers.Signal();
                        }

                        return broadcasterAttachment;
                    })
                    .ObserveOn(childScheduler)
                    .SubscribeOn(childScheduler);
                })
                .SubscribeOn(childScheduler) // ensures that when we attach master observer it's done on child thread, as we plan on awaiting cache synchronization
                .Do(_ => _logger.LogTrace($"Shared informer out: {_}"));

        }

        private void AddSubscriber()
        {
            // when child subscribers attach they need to be synchronized to the master stream
            // this is allowed outside of "reset" event boundary.
            // the broadcaster will yield to any _waitingSubscribers before resuming work
            lock (_lock)
            {
                // need to do this under lock because we can't just increment if the lock is already set, and there's a
                // risk of collision of two threads resetting to 1 at the same time
                if (!_waitingSubscribers.TryAddCount())
                {
                    _waitingSubscribers.Reset(1);
                }
            }

            if (_subscribers == 0)
            {
                _masterSubscription = _masterObservable.Connect();
            }
            _subscribers++;
        }

        private void RemoveSubscriber()
        {
            _logger.LogTrace("Removing Subscriber!");
            _subscribers--;
            if (_subscribers == 0)
            {
                _cacheSynchronized = new TaskCompletionSource<bool>(false);
                _masterSubscription.Dispose();
            }
        }
    }

}
