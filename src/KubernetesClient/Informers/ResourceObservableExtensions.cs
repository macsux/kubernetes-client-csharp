using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using k8s.Informers.Cache;
using k8s.Informers.Notifications;
using Microsoft.Extensions.Logging;

namespace k8s.Informers
{
    public static class ResourceObservableExtensions
    {

        /// <summary>
        /// Connects source block which publishes list of <see cref="ResourceEvent{TResource}"/> to action block
        /// which invokes processing function specified by <paramref name="action"/> for each received item.
        /// </summary>
        /// <param name="workerQueue">The source action block to attach to</param>
        /// <param name="action">The action to invoke for each received batch of <see cref="ResourceEvent{TResource}"/></param>
        /// <param name="parallelWorkers">Number of allowed parallel invocations of <paramref name="action"/>. Default is 1</param>
        /// <typeparam name="TResource">The resource type</typeparam>
        /// <returns>The disposable that disconnects from the <paramref name="workerQueue"/> when disposed of</returns>
        public static IDisposable ProcessWith<TResource>(this ISourceBlock<List<ResourceEvent<TResource>>> workerQueue, Func<List<ResourceEvent<TResource>>,Task> action, ILogger logger, int parallelWorkers = 1)
        {
            var actionBlock = new ActionBlock<List<ResourceEvent<TResource>>>(action, new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = parallelWorkers, // don't buffer more messages then we are actually able to work on
                MaxDegreeOfParallelism = parallelWorkers
            });
            actionBlock.Completion.ContinueWith(x =>
            {
                if (x.IsFaulted)
                {
                    logger.LogCritical(x.Exception.Flatten(), "Controller encountered a critical error");
                }
            });
            return workerQueue.LinkTo(actionBlock, new DataflowLinkOptions { PropagateCompletion = true});

        }

        public static IObservable<ResourceEvent<TResource>> DetectResets<TResource>(this IObservable<ResourceEvent<TResource>> source, IObserver<List<ResourceEvent<TResource>>> resetSubject)
        {
            var resetBuffer = new List<ResourceEvent<TResource>>();

            return Observable.Create<ResourceEvent<TResource>>(observer =>
            {
                void FlushBuffer()
                {
                    if (resetBuffer.Any())
                    {
                        resetSubject.OnNext(resetBuffer.ToList());
                        resetBuffer.Clear();
                    }
                }
                void OnComplete()
                {
                    FlushBuffer();
                    observer.OnCompleted();
                    resetSubject.OnCompleted();
                }
                void OnError(Exception e)
                {
                    observer.OnError(e);
                    resetSubject.OnError(e);
                }
                var upstreamSubscription = source
                    .Do(notification =>
                    {
                        if (notification.EventFlags.HasFlag(EventTypeFlags.Reset))
                        {
                            resetBuffer.Add(notification);
                            if (!(notification.EventFlags.HasFlag(EventTypeFlags.ResetEnd))) // continue buffering till we reach the end of list window
                                return;
                        }

                        if (notification.EventFlags.HasFlag(EventTypeFlags.ResetEnd) || (!notification.EventFlags.HasFlag(EventTypeFlags.Reset) && resetBuffer.Count > 0))
                        {
                            FlushBuffer();
                        }
                    })
                    .Subscribe(observer.OnNext, OnError, OnComplete);
                return new CompositeDisposable(upstreamSubscription, Disposable.Create(OnComplete));
            })
                .ObserveOn(Scheduler.Immediate);
        }

        /// <summary>
        /// Synchronizes the specified cache with resource event stream such that cache is maintained up to date.
        /// </summary>
        /// <param name="source">The source sequence</param>
        /// <param name="cache">The cache to synchronize</param>
        /// <param name="keySelector">The key selector function</param>
        /// <typeparam name="TKey">The type of key</typeparam>
        /// <typeparam name="TResource">The type of resource</typeparam>
        /// <returns>Source sequence wrapped into <see cref="CacheSynchronized{T}"/>, which allows downstream consumers to synchronize themselves with cache version</returns>
        public static IObservable<CacheSynchronized<ResourceEvent<TResource>>> SynchronizeCache<TKey, TResource>(
            this IObservable<ResourceEvent<TResource>> source,
            ICache<TKey, TResource> cache,
            Func<TResource, TKey> keySelector)
        {
            // long cacheVersion = 0;
            var resetSubject = new Subject<List<ResourceEvent<TResource>>>();


            List<CacheSynchronized<ResourceEvent<TResource>>> UpdateBlock(List<ResourceEvent<TResource>> events)
            {
                // cacheVersion += events.Count;
                var reset = events
                    .Select(x => x.Value)
                    .Where(x => x != null)
                    .ToDictionary(keySelector, x => x);

                cache.Reset(reset);
                var acc = cache.Version + 1;
                cache.Version += events.Count;
                return events
                    .Select(x => new CacheSynchronized<ResourceEvent<TResource>>(acc++, cache.Version, x))
                    .ToList();
            }

            CacheSynchronized<ResourceEvent<TResource>> UpdateSingle(ResourceEvent<TResource> notification)
            {
                cache.Version++;
                if (!notification.EventFlags.HasFlag(EventTypeFlags.Delete))
                {
                    if (notification.EventFlags.HasFlag(EventTypeFlags.Modify) && cache.TryGetValue(keySelector(notification.Value), out var oldValue))
                    {
                        notification = new ResourceEvent<TResource>(notification.EventFlags, notification.Value, oldValue);
                    }

                    if (notification.Value != null)
                    {
                        cache[keySelector(notification.Value)] = notification.Value;
                    }
                }
                else
                {
                    cache.Remove(keySelector(notification.OldValue));
                }

                return new CacheSynchronized<ResourceEvent<TResource>>(cache.Version, cache.Version, notification);
            }

            return Observable.Create<CacheSynchronized<ResourceEvent<TResource>>>(obs =>
            {
                var a = resetSubject
                    .SelectMany(UpdateBlock)
                    .ObserveOn(Scheduler.Immediate)
                    .Subscribe(obs);
                var b = source
                    .DetectResets(resetSubject)
                    .Where(x => !x.EventFlags.HasFlag(EventTypeFlags.Reset)) // hold back resets, they'll be reinjected as batches via resetSubject after cache sync
                    .Select(UpdateSingle)
                    .ObserveOn(Scheduler.Immediate)
                    .Subscribe(obs);
                return new CompositeDisposable(a, b);
            })
                .ObserveOn(Scheduler.Immediate);


        }


        public static IObservable<ResourceEvent<TResource>> ComputeMissedEventsBetweenResets<TKey,TResource>(this IObservable<ResourceEvent<TResource>> source, Func<TResource,TKey> keySelector, IEqualityComparer<TResource> comparer)
        {
            Dictionary<TKey,TResource> cacheSnapshot;
            var cache = new SimpleCache<TKey,TResource>();
            var cacheSynchronized = false;
            return Observable.Create<ResourceEvent<TResource>>(observer =>
                {
                    return source
                        .DetectResets(Observer.Create<List<ResourceEvent<TResource>>>(resetBuffer =>
                        {
                            if (!cacheSynchronized)
                            {
                                resetBuffer
                                    .ToObservable()
                                    .Concat(Observable.Never<ResourceEvent<TResource>>())
                                    .ObserveOn(Scheduler.Immediate)
                                    .Subscribe(observer);
                                return;
                            }

                            cacheSnapshot = new Dictionary<TKey, TResource>(cache);
                            var newKeys = resetBuffer
                                .Where(x => x.Value != null)
                                .Select(x => keySelector(x.Value))
                                .ToHashSet();

                            var addedEntities = resetBuffer
                                .Select(x => x.Value)
                                .Where(x => x != null && !cacheSnapshot.ContainsKey(keySelector(x)))
                                .Select(x => x.ToResourceEvent(EventTypeFlags.Add | EventTypeFlags.Computed))
                                .ToList();
                            var addedKeys = addedEntities
                                .Select(x => keySelector(x.Value))
                                .ToHashSet();

                            var deletedEntities = cacheSnapshot
                                .Where(x => !newKeys.Contains(x.Key))
                                .Select(x => x.Value.ToResourceEvent(EventTypeFlags.Delete | EventTypeFlags.Computed))
                                .ToList();
                            var deletedKeys = deletedEntities
                                .Select(x => keySelector(x.Value))
                                .ToHashSet();

                            // we can only compute updates if we are given a proper comparer to determine equality between objects
                            // if not provided, will be sent downstream as just part of reset
                            var updatedEntities = new List<ResourceEvent<TResource>>();
                            if (comparer != null)
                            {
                                var previouslyKnownEntitiesInResetWindowKeys = cacheSnapshot
                                    .Keys
                                    .Intersect(resetBuffer.Select(x => keySelector(x.Value)));

                                updatedEntities = resetBuffer
                                    .Where(x => previouslyKnownEntitiesInResetWindowKeys.Contains(keySelector(x.Value)))
                                    .Select(x => x.Value) // stuff in buffer that also existed in cache (by key)
                                    .Except(cacheSnapshot.Select(x => x.Value), comparer)
                                    .Select(x => x.ToResourceEvent(EventTypeFlags.Modify | EventTypeFlags.Computed))
                                    .ToList();
                            }

                            var updatedKeys = updatedEntities
                                .Select(x => keySelector(x.Value))
                                .ToHashSet();

                            var resetEntities = resetBuffer
                                .Select(x => x.Value)
                                .Where(x => x != null &&
                                            !addedKeys.Contains(keySelector(x)) &&
                                            !deletedKeys.Contains(keySelector(x)) &&
                                            !updatedKeys.Contains(keySelector(x)))
                                .ToReset()
                                .ToList();

                            deletedEntities
                                .Union(addedEntities)
                                .Union(updatedEntities)
                                .Union(resetEntities)
                                .ToList()
                                .ToObservable()
                                .Concat(Observable.Never<ResourceEvent<TResource>>())
                                .ObserveOn(Scheduler.Immediate)
                                .Subscribe(observer);
                        }))
                        .SynchronizeCache(cache, keySelector)
                        .Do(msg =>
                        {
                            cacheSynchronized = true;
                        })
                        .Select(x => x.Value)
                        .Where(x => !x.EventFlags.HasFlag(EventTypeFlags.Reset)) // any resets are split off higher
                        .ObserveOn(Scheduler.Immediate)
                        .Subscribe(observer);
                });
        }
                /// <summary>
        /// Injects a <see cref="ResourceEvent{T}"/> of type <see cref="ResourceStreamType.Sync"/> into the observable for each item produced
        /// by the <see cref="ResourceStreamType.List"/> operation from <paramref name="source"/>
        /// </summary>
        /// <param name="source">The source sequence that will have sync messages appended</param>
        /// <param name="timeSpan">The timespan interval at which the messages should be produced</param>
        /// <typeparam name="T">The type of resource</typeparam>
        /// <returns>Original sequence with resync applied</returns>
        public static IObservable<ResourceEvent<T>> Resync<T>(this IObservable<ResourceEvent<T>> source, TimeSpan timeSpan)
        {
            return Observable.Create<ResourceEvent<T>>( observer =>
            {
                var timerSubscription = Observable
                    .Interval(timeSpan)
                    .SelectMany(_ => source
                        .TakeUntil(x => x.EventFlags.HasFlag(EventTypeFlags.ResetEnd))
                        .Do(x =>
                        {
                            if(!x.EventFlags.HasFlag(EventTypeFlags.Reset))
                                throw new InvalidOperationException("Resync was applied to an observable sequence that does not issue a valid List event block when subscribed to");
                        })
                        .Select(x => x.Value.ToResourceEvent(EventTypeFlags.Sync)))
                    .Subscribe(observer);
                // this ensures that both timer and upstream subscription is closed when subscriber disconnects
                var sourceSubscription =  source.Subscribe(
                    observer.OnNext,
                    observer.OnError,
                    () =>
                    {
                        observer.OnCompleted();
                        timerSubscription.Dispose();
                    });
                return new CompositeDisposable(timerSubscription, sourceSubscription);
            });
        }

        /// <summary>
        /// Wraps an instance of <see cref="IInformer{TResource,TOptions}"/> as <see cref="IInformer{TResource}"/> by using the same
        /// set of <see cref="TOptions"/> for every subscription
        /// </summary>
        /// <param name="optionedInformer">The original instance of <see cref="IInformer{TResource,TOptions}"/></param>
        /// <param name="options">The options to use</param>
        /// <typeparam name="TResource">The type of resource</typeparam>
        /// <typeparam name="TOptions"></typeparam>
        /// <returns></returns>
        public static IInformer<TResource> WithOptions<TResource, TOptions>(this IInformer<TResource, TOptions> optionedInformer, TOptions options) =>
            new WrappedOptionsInformer<TResource,TOptions>(optionedInformer, options);

        private class WrappedOptionsInformer<TResource,TOptions> : IInformer<TResource>
        {
            private readonly IInformer<TResource, TOptions> _informer;
            private readonly TOptions _options;

            public WrappedOptionsInformer(IInformer<TResource,TOptions> informer, TOptions options)
            {
                _informer = informer;
                _options = options;
            }

            public IObservable<ResourceEvent<TResource>> GetResource(ResourceStreamType type) => _informer.GetResource(type, _options);
        }
    }
}
