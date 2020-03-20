using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using k8s;
using k8s.Informers;
using k8s.Informers.Notifications;
using k8s.Models;
using KellermanSoftware.CompareNetObjects;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace informers
{
    // this sample demos both informer and controller
    // there are two loggers:
    //   _informerLogger lets you see raw data coming out of informer stream
    //   _reconcilerLogger lets you see batches of object transitions that object went through since last time we did work on it
    // reconciler is purposely slowed down to show accumulation of events between worker actions

    // try creating and deleting some pods in "default" namespace and watch the output

    public class DeltaChangesQueryingController : IController
    {
        private readonly IKubernetesInformer<V1Pod> _podInformer;
        private readonly ILogger _reconcilerLogger;
        private ILogger _informerLogger;
        CompareLogic _objectCompare = new CompareLogic();
        // private readonly ActionBlock<List<ResourceEvent<V1Pod>>> _reconciler;
        private readonly CompositeDisposable _subscription = new CompositeDisposable();

        public DeltaChangesQueryingController(IKubernetesInformer<V1Pod> podInformer, ILoggerFactory loggerFactory)
        {
            _podInformer = podInformer;
            _reconcilerLogger = loggerFactory.CreateLogger("Reconciler");
            _informerLogger = loggerFactory.CreateLogger("Informer");
            _objectCompare.Config.MaxDifferences = 100;
            // the commented sections show how to use advanced syntax to work with TPL dataflows. most scenarios can get away with .ProcessWith helper method

            // _reconciler = new ActionBlock<List<ResourceEvent<V1Pod>>>(Reconcile,
            //     new ExecutionDataflowBlockOptions()
            //     {
            //         BoundedCapacity = 2,
            //         MaxDegreeOfParallelism = 2,
            //     });
        }

        // public Task Completion => _reconciler.Completion;

        public Task Initialize(CancellationToken cancellationToken)
        {
            var workerQueue = _podInformer
                .GetResource(ResourceStreamType.ListWatch, new KubernetesInformerOptions() { Namespace = "default"})
                // .Resync(TimeSpan.FromSeconds(5))
                .Do(item =>
                {
                    _informerLogger.LogInformation($"\n EventType: {item.EventFlags} \n Name: {item.Value.Metadata.Name} \n Version: {item.Value.Metadata.ResourceVersion}");
                })
                .Catch<ResourceEvent<V1Pod>,Exception>(e =>
                {
                    _informerLogger.LogCritical(e, e.Message);
                    return Observable.Throw<ResourceEvent<V1Pod>>(e);
                })
                .ToResourceEventDeltaBlock(x => x.Metadata.Name, out var informerSubscription);
            informerSubscription.DisposeWith(_subscription);
            workerQueue
                //.LinkTo(_reconciler) // working with action blocks directly for fine grained control
                .ProcessWith(Reconcile, _reconcilerLogger) // simplified syntax
                .DisposeWith(_subscription);
            return Task.CompletedTask;
        }

        private async Task Reconcile(List<ResourceEvent<V1Pod>> changes)
        {
            // invoke reconcilation here

            var obj = changes.First().Value;
            var sb = new StringBuilder();
            // sb.AppendLine($"Received changes for object with ID {KubernetesObject.KeySelector(obj)} with {changes.Count} items");
            sb.AppendLine($"Received changes for object with ID {KubernetesObject.KeySelector(obj)} with {changes.Count} items");
            sb.AppendLine($"Last known state was {changes.Last().EventFlags}");
            foreach (var item in changes)
            {
                sb.AppendLine($"==={item.EventFlags}===");
                sb.AppendLine($"Name: {item.Value.Metadata.Name}");
                sb.AppendLine($"Version: {item.Value.Metadata.ResourceVersion}");
                if (item.EventFlags.HasFlag(EventTypeFlags.Modify))
                {
                    var updateDelta = _objectCompare.Compare(item.OldValue, item.Value);
                    foreach (var difference in updateDelta.Differences)
                    {
                        sb.AppendLine($"{difference.PropertyName}: {difference.Object1} -> {difference.Object2}");
                    }

                }
                // sb.AppendLine(JsonConvert.SerializeObject(item, Formatting.Indented, new StringEnumConverter()));
            }
            _reconcilerLogger.LogInformation(sb.ToString());

            await Task.Delay(TimeSpan.FromSeconds(10)); // simulate
            await Task.CompletedTask;

        }
    }
}
