using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using FluentAssertions;
using k8s.Controllers;
using k8s.Informers.Notifications;
using k8s.Tests.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Reactive.Testing;
using Xunit;
using Xunit.Abstractions;
using static k8s.Informers.Notifications.EventTypeFlags;

namespace k8s.Tests
{
    public class DeltaFifoTests
    {
        private readonly ILogger _log;

        public DeltaFifoTests(ITestOutputHelper output)
        {
            _log = new XunitLogger<SharedInformerTests>(output);
        }



        public static IEnumerable<object[]> DistributeWorkTestData()
        {
            yield return new object[]
            {
                nameof(TestData.Events.ResetWith2_Delay_UpdateToEach), // description
                TestData.Events.ResetWith2_Delay_UpdateToEach, // events
                new [] // expected
                {
                    new[]
                    {
                        new TestResource(1).ToResourceEvent(ResetStart),
                    },
                    new[]
                    {
                        new TestResource(2).ToResourceEvent(ResetEnd),
                    },
                    new[]
                    {
                        new TestResource(1).ToResourceEvent(Modify),
                        new TestResource(1).ToResourceEvent(Modify),
                    }
                }
            };
            yield return new object[]
            {
                nameof(TestData.Events.EmptyReset_Delay_Add), // description
                TestData.Events.EmptyReset_Delay_Add, // events
                new [] // expected
                {
                    new[]
                    {
                        new TestResource(1).ToResourceEvent(Add),
                    }
                }
            };
            yield return new object[]
            {
                nameof(TestData.Events.Sync_Delay_SyncAndUpdate), // description
                TestData.Events.Sync_Delay_SyncAndUpdate, // events
                new [] // expected
                {
                    new[]
                    {
                        new TestResource(1).ToResourceEvent(Sync),
                    },
                    new[]
                    {
                        new TestResource(1).ToResourceEvent(Modify), // modify "kicks" out the other sync
                    }
                }
            };

            yield return new object[]
            {
                nameof(TestData.Events.ResetWith2_Delay_UpdateBoth_Delay_Add1), // description
                TestData.Events.ResetWith2_Delay_UpdateBoth_Delay_Add1, // events
                new [] // expected
                {
                    new[]
                    {
                        new TestResource(1).ToResourceEvent(ResetStart),
                    },
                    new[]
                    {
                        new TestResource(2).ToResourceEvent(ResetEnd),
                    },
                    new[]
                    {
                        new TestResource(1).ToResourceEvent(Modify),
                        new TestResource(1).ToResourceEvent(Modify),
                    },
                    new[]
                    {
                        new TestResource(3).ToResourceEvent(Add),
                    }
                }
            };
        }

        [Theory]
        [MemberData(nameof(DistributeWorkTestData))]

        public async Task DistributeWork(string description, ScheduledEvent<TestResource>[] events, ResourceEvent<TestResource>[][] expected)
        {
            _log.LogInformation(description);
            var sut = new ResourceEventDeltaBlock<int, TestResource>(x => x.Key);
            var testScheduler = new TestScheduler();
            var results = new List<List<ResourceEvent<TestResource>>>();
            var actionBlock = new ActionBlock<List<ResourceEvent<TestResource>>>(deltas =>
            {
                _log.LogTrace($"Worker called for {string.Join("",deltas)}");
                results.Add(deltas);
                testScheduler.Sleep(300);
            }, new ExecutionDataflowBlockOptions()
            {
                BoundedCapacity = 1,
                MaxDegreeOfParallelism = 1
            });
            sut.LinkTo(actionBlock, new DataflowLinkOptions {PropagateCompletion = true});

            events.ToTestObservable(testScheduler, logger: _log).Subscribe(sut.AsObserver());
            await actionBlock.Completion.TimeoutIfNotDebugging();

            results.Should().BeEquivalentTo(expected);
        }
    }

}
