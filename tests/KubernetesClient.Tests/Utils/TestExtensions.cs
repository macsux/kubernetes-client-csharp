using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using k8s.Informers.Notifications;
using k8s.Models;
using k8s.Tests.Mock;
using Microsoft.Extensions.Logging;
using Microsoft.Rest;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Microsoft.Reactive.Testing;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using NSubstitute;

namespace k8s.Tests.Utils
{
    public static class TestExtensions
    {
        private static TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

        public static ScheduledEvent<T> ScheduleFiring<T>(this ResourceEvent<T> obj, long fireAt)
        {
            return new ScheduledEvent<T> {Event = obj, ScheduledAt = fireAt};
        }

        public static IObservable<T> TimeoutIfNotDebugging<T>(this IObservable<T> source) =>
            source.TimeoutIfNotDebugging(DefaultTimeout);

        public static IObservable<T> TimeoutIfNotDebugging<T>(this IObservable<T> source, TimeSpan timeout) =>
            Debugger.IsAttached ? source : source.Timeout(timeout);

        public static async Task TimeoutIfNotDebugging(this Task task) => await task.TimeoutIfNotDebugging(DefaultTimeout);
        public static async Task TimeoutIfNotDebugging(this Task task, TimeSpan timeout)
        {
            async Task<bool> Wrapper()
            {
                await task;
                return true;
            }
            await Wrapper().TimeoutIfNotDebugging(timeout);
        }

        public static async Task<T> TimeoutIfNotDebugging<T>(this Task<T> task) => await task.TimeoutIfNotDebugging(DefaultTimeout);

        public static async Task<T> TimeoutIfNotDebugging<T>(this Task<T> task, TimeSpan timeout)
        {
            if (Debugger.IsAttached)
            {
                return await task;
            }

            using var timeoutCancellationTokenSource = new CancellationTokenSource();
            var completedTask = await Task.WhenAny(task, Task.Delay(timeout, timeoutCancellationTokenSource.Token));
            if (completedTask == task)
            {
                timeoutCancellationTokenSource.Cancel();
                return await task; // Very important in order to propagate exceptions
            }

            throw new TimeoutException("The operation has timed out.");
        }

        public static string ToJson(this object obj, Formatting formatting = Formatting.None)
        {
            return JsonConvert.SerializeObject(obj, formatting, new StringEnumConverter());
        }

        public static Watcher<T>.WatchEvent ToWatchEvent<T>(this T obj, WatchEventType eventType)
        {
            return new Watcher<T>.WatchEvent {Type = eventType, Object = obj};
        }

        public static HttpOperationResponse<KubernetesList<TV>> ToHttpOperationResponse<TL,TV>(this TL obj) where TL : IItems<TV> where TV : IKubernetesObject
        {
            return new HttpOperationResponse<KubernetesList<TV>>()
            {
                Body = JsonConvert.DeserializeObject<KubernetesList<TV>>(obj.ToJson()),
                Response = new HttpResponseMessage()
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(obj.ToJson())

                }
            };
        }

        public static HttpOperationResponse<KubernetesList<T>> ToHttpOperationResponse<T>(this Watcher<T>.WatchEvent obj) where T : IKubernetesObject
        {
            return new[] {obj}.ToHttpOperationResponse();
        }

        public static HttpOperationResponse<KubernetesList<T>> ToHttpOperationResponse<T>(this IEnumerable<Watcher<T>.WatchEvent> obj) where T : IKubernetesObject
        {
            var stringContent = new StringContent(string.Join("\n",obj.Select(x => x.ToJson())));
            var lineContent = new WatcherDelegatingHandler.LineSeparatedHttpContent(stringContent, CancellationToken.None);
            lineContent.LoadIntoBufferAsync().Wait();
            var httpResponse = new HttpOperationResponse<KubernetesList<T>>()
            {
                Response = new HttpResponseMessage()
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = lineContent
                }
            };
            return httpResponse;
        }

        public static IObservable<ResourceEvent<T>> ToTestObservable<T>(this ICollection<ScheduledEvent<T>> source, TestScheduler testScheduler = null, bool startOnSubscribe = true, ILogger logger = null)
        {
            if(testScheduler == null)
                testScheduler = new TestScheduler();
            return Observable.Create<ResourceEvent<T>>(o =>
            {
                var closeAt = source.Max(x => x.ScheduledAt);
                foreach (var e in source )
                {
                    testScheduler.ScheduleAbsolute(e.ScheduledAt, () => o.OnNext(e.Event));
                }
                testScheduler.ScheduleAbsolute(closeAt, async () =>
                {
                    logger?.LogTrace("Test sequence is complete");
                    // this is a bit of a hack but since in some tests observable is connected to TPL blocks which don't
                    // run on virtual scheduler, the timings come off and introduce race condition. this ensures that all messages are
                    // accepted by the receiving block before it is marked for completion
                    await Task.Delay(10);
                    o.OnCompleted();
                });
                if(startOnSubscribe)
                    testScheduler.Start();
                return Disposable.Empty;
            });
        }

        public static ResourceEvent<TestResource>[] ToBasicExpected(this IEnumerable<ScheduledEvent<TestResource>> events)
        {
            var lastKnown = new Dictionary<int, TestResource>();
            var retval = new List<ResourceEvent<TestResource>>();
            foreach (var e in events.Select(x => x.Event))
            {
                var item = e;
                if (e.EventFlags.HasFlag(EventTypeFlags.Modify) && lastKnown.TryGetValue(e.Value.Key, out var oldValue))
                {
                    item = new ResourceEvent<TestResource>(e.EventFlags, e.Value, oldValue);
                }

                if (e.EventFlags.HasFlag(EventTypeFlags.Delete))
                {
                    lastKnown.Remove(e.Value.Key);
                }
                else
                {
                    if (e.Value != null)
                        lastKnown[e.Value.Key] = e.Value;
                }

                retval.Add(item);
            }

            return retval.ToArray();
        }
    }
}

