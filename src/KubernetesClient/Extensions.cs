using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using k8s.Controllers;
using k8s.Informers;
using k8s.Informers.Cache;
using k8s.Informers.Notifications;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Rest.TransientFaultHandling;
using YamlDotNet.Serialization.NodeTypeResolvers;

namespace k8s
{
    public static class Extensions
    {
        /// <summary>
        /// Removes an item from the dictionary
        /// </summary>
        /// <param name="source">The source dictionary</param>
        /// <param name="key">The key for which item should be removed</param>
        /// <param name="result">The value of the object that was removed, or <see langword="null"/> if value was not present in dictionary</param>
        /// <typeparam name="TKey">The type of key</typeparam>
        /// <typeparam name="TValue">The type of value</typeparam>
        /// <returns><see langword="true"/> if the object was removed from dictionry, or <see langword="false"/> if the specific key was not present in dictionary</returns>
        public static bool Remove<TKey,TValue>(this IDictionary<TKey,TValue> source, TKey key, out TValue result)
        {
            result = default;
            if (!source.TryGetValue(key, out result))
                return false;
            source.Remove(key);
            return true;
        }
        /// <summary>
        /// Tries to remove item from the queue
        /// </summary>
        /// <param name="queue">Source queue</param>
        /// <param name="result">The result if dequeue was successful, other <see langword="null"/></param>
        /// <typeparam name="T">The type of items in queue</typeparam>
        /// <returns><see langword="true"/> if dequeue was successful, otherwise <see langward="false"/></returns>
        public static bool TryDequeue<T>(this Queue<T> queue, out T result)
        {
            result = default;
            if (queue.Count == 0)
                return false;
            try
            {
                result = queue.Dequeue();
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            return true;
        }
        /// <summary>
        /// Tries to look at the first item of the queue without removing it
        /// </summary>
        /// <param name="queue">The source queue</param>
        /// <param name="result">The item at the top of the queue, or <see langword="null"/> if queue is empty</param>
        /// <typeparam name="T">The type of the items in the queue</typeparam>
        /// <returns><see langword="true"/> if the operation was successful, or <see langword="false"/> if the queue is empty</returns>
        public static bool TryPeek<T>(this Queue<T> queue, out T result)
        {
            result = default;
            if (queue.Count == 0)
                return false;
            try
            {
                result = queue.Peek();
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            return true;
        }
        /// <summary>
        /// Creates a <see cref="HashSet{T}"/> for <see cref="IEnumerable{T}"/>
        /// </summary>
        /// <param name="source">The source enumerable</param>
        /// <typeparam name="T">The type of elements</typeparam>
        /// <returns>The produced hashset</returns>
        public static HashSet<T> ToHashSet<T>(this IEnumerable<T> source)
        {
            return source.ToHashSet(null);
        }
        /// <summary>
        /// Creates a <see cref="HashSet{T}"/> for <see cref="IEnumerable{T}"/>
        /// </summary>
        /// <param name="source">The source enumerable</param>
        /// <param name="comparer">The comparer to use</param>
        /// <typeparam name="T">The type of elements</typeparam>
        /// <returns>The produced hashset</returns>
        public static HashSet<T> ToHashSet<T>(
            this IEnumerable<T> source,
            IEqualityComparer<T> comparer)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            return new HashSet<T>(source, comparer);
        }


        /// <summary>
        /// Converts the source sequence to <see cref="ResourceEventDeltaBlock{TKey,TResource}"/>. This transitions from
        /// observable into TPL Dataflow monad. The resulting block allows queue processing semantics where each resulting item
        /// is the collection of the given <see cref="ResourceEvent{TResource}"/> for grouped by <see cref="V1ObjectMeta.Name"/>
        /// </summary>
        /// <param name="source">The resource observable</param>
        /// <typeparam name="TResource">The type of resource</typeparam>
        /// <returns>The <see cref="ResourceEventDeltaBlock{TKey,TResource}"/> connected to the observable</returns>
        public static ISourceBlock<List<ResourceEvent<TResource>>> ToResourceEventDeltaBlock<TResource>(this IObservable<ResourceEvent<TResource>> source,
            out IDisposable subscription) where TResource : IKubernetesObject, IMetadata<V1ObjectMeta> =>
            source.ToResourceEventDeltaBlock(x => KubernetesObject.KeySelector, out subscription);

        /// <summary>
        /// Converts the source sequence to <see cref="ResourceEventDeltaBlock{TKey,TResource}"/>. This transitions from
        /// observable into TPL Dataflow monad. The resulting block allows queue processing semantics where each resulting item
        /// is the collection of the given <see cref="ResourceEvent{TResource}"/> for grouped by resource object identified by the
        /// <paramref name="keySelector"/> parameter
        /// </summary>
        /// <param name="source">The resource observable</param>
        /// <param name="keySelector">Key selector function that uniquely identifies objects in the resource collection being observed</param>
        /// <typeparam name="TResource">The type of resource</typeparam>
        /// <typeparam name="TKey">The key for the resource</typeparam>
        /// <returns>The <see cref="ResourceEventDeltaBlock{TKey,TResource}"/> connected to the observable</returns>
        public static ISourceBlock<List<ResourceEvent<TResource>>> ToResourceEventDeltaBlock<TKey,TResource>(this IObservable<ResourceEvent<TResource>> source, Func<TResource,TKey> keySelector, out IDisposable subscription)
        {
            var deltaBlock = new ResourceEventDeltaBlock<TKey, TResource>(keySelector);
            subscription = source.Subscribe(deltaBlock.AsObserver());
            return deltaBlock;
        }

        /// <summary>
        /// Attaches the source <see cref="IDisposable"/> to the target <see cref="CompositeDisposable"/>
        /// </summary>
        /// <param name="source">The original <see cref="IDisposable"/></param>
        /// <param name="composite">The <see cref="CompositeDisposable"/> to attach to</param>
        /// <returns>The original disposable passed as <paramref name="source"/> </returns>
        public static IDisposable DisposeWith(this IDisposable source, CompositeDisposable composite)
        {
            composite.Add(source);
            return source;
        }

        /// <summary>
        /// Combines the source disposable with another into a single disposable
        /// </summary>
        /// <param name="source">The original <see cref="IDisposable"/></param>
        /// <param name="composite">The <see cref="IDisposable"/> to combine with</param>
        /// <returns>Composite disposable made up of <paramref name="source"/> and <paramref name="other"/> </returns>
        public static IDisposable CombineWith(this IDisposable source, IDisposable other)
        {
            return new CompositeDisposable(source,other);
        }

        public static IDisposable Subscribe<T>(this IObservable<T> source, IObserver<T> observer, Action onFinished = null)
        {
            return source.Subscribe(observer, _ => { },x => onFinished(), onFinished);
        }

        public static IDisposable Subscribe<T>(this IObservable<T> source, IObserver<T> observer, Action<T> onNext = null, Action<Exception> onError = null, Action onCompleted = null)
        {
            onNext ??= obj => { };
            onError ??= obj => { };
            onCompleted ??= () => { };
            return source.Subscribe(x =>
                {
                    observer.OnNext(x);
                    onNext(x);
                },
                error =>
                {
                    observer.OnError(error);
                    onError(error);
                },
                () =>
                {
                    observer.OnCompleted();
                    onCompleted();
                });
        }



    }


}
