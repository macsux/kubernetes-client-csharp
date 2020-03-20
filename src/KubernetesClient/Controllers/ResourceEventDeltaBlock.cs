using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using k8s.Informers.Notifications;
using Microsoft.Extensions.Logging;

namespace k8s.Controllers
{

	/// <summary>
	/// A TPL Dataflow block exposes queuing semantics that attaches to infromer observable streams.
	/// It groups delta changes per key, sending each group to downstream blocks as individual messages
	/// This allows "Per resource ID" processing semantics
	/// Any sync events are only propagated if no other changes are queued up
	/// </summary>
	/// <typeparam name="TKey">The type of key used to identity resource</typeparam>
	/// <typeparam name="TResource">The resource type</typeparam>
	public class ResourceEventDeltaBlock<TKey, TResource> : ISourceBlock<List<ResourceEvent<TResource>>>, ITargetBlock<ResourceEvent<TResource>>
	{
		private readonly Func<TResource, TKey> _keyFunc;
		private readonly bool _skipTransient;
		private readonly Queue<List<ResourceEvent<TResource>>> _queue = new Queue<List<ResourceEvent<TResource>>>();
		private readonly List<TargetLink> _targets = new List<TargetLink>();
		private bool _isCompleting;
		private long _msgId;
		private readonly object _lock = new object();
		private readonly TaskCompletionSource<object> _taskCompletionSource = new TaskCompletionSource<object>();
		private readonly Dictionary<TKey, List<ResourceEvent<TResource>>> _items = new Dictionary<TKey, List<ResourceEvent<TResource>>>();

		/// <param name="keyFunc">The key selector function</param>
		/// <param name="skipTransient">If <see langword="true">, removes any batches in which resource was created (first msg=Add) and removed (last msg=Delete)</param>
		public ResourceEventDeltaBlock(Func<TResource, TKey> keyFunc, bool skipTransient = true)
		{
			_keyFunc = keyFunc;
			_skipTransient = skipTransient;
		}

		private TKey KeyOf(ResourceEvent<TResource> obj) => KeyOf(obj.Value);

		private TKey KeyOf(TResource obj) => _keyFunc(obj);




		/// <summary>
		/// Checks if block is marked for completion and marks itself as completed after queue is drained
		/// </summary>
		private void SetCompletedIfNeeded()
		{
			lock (_lock)
			{
				if (!_isCompleting || _queue.Count != 0) return;
				foreach (var link in _targets.Where(x => x.LinkOptions.PropagateCompletion))
				{
					link.Target.Complete();
				}
			}

			_taskCompletionSource.TrySetResult(null);
		}
		public DataflowMessageStatus OfferMessage(DataflowMessageHeader messageHeader, ResourceEvent<TResource> resourceEvent, ISourceBlock<ResourceEvent<TResource>> source,
			bool consumeToAccept)
		{
			if (_isCompleting)
				return DataflowMessageStatus.DecliningPermanently;
			if (consumeToAccept)
			{
				resourceEvent = source.ConsumeMessage(messageHeader, this, out var consumed);
				if (!consumed)
					return DataflowMessageStatus.NotAvailable;
			}

			if (resourceEvent.EventFlags.HasFlag(EventTypeFlags.ResetEmpty)
			    || resourceEvent.Value == null)
				return DataflowMessageStatus.Declined;


			lock (_lock)
			{
				// decline any syncs if we're already queued up to process this resource
				if(resourceEvent.EventFlags.HasFlag(EventTypeFlags.Sync) && _items.ContainsKey(_keyFunc(resourceEvent.Value)))
				{
					return DataflowMessageStatus.Declined;
				}
				QueueActionLocked(resourceEvent);
			}

			return DataflowMessageStatus.Accepted;
		}


		private void CombineDeltas(List<ResourceEvent<TResource>> deltas)
		{
			if (deltas.Count < 2) return;
			if (deltas.First().EventFlags.HasFlag(EventTypeFlags.Sync)) // if we had a sync item queued up and got something else, get rid of sync
				deltas.RemoveAt(0);
			if (deltas.Count < 2) return;

			// if the entire object was created and removed before worker got a chance to touch it and worker has not chose to see these
			// types of events, we can just get rid of this "transient" object and not even notify worker of its existence
			if(_skipTransient && deltas[0].EventFlags.HasFlag(EventTypeFlags.Add) && deltas[deltas.Count - 1].EventFlags.HasFlag(EventTypeFlags.Delete))
				deltas.Clear();
		}

		private void QueueActionLocked(ResourceEvent<TResource> obj)
		{
			var id = KeyOf(obj);

			var exists = _items.TryGetValue(id, out var deltas);
			if (!exists)
			{
				deltas = new List<ResourceEvent<TResource>>();
				_items[id] = deltas;
				_queue.Enqueue(deltas);
			}

			deltas.Add(obj);
			CombineDeltas(deltas);
			if (_queue.Count == 1) // we've just added to empty queue, kick off processing
				OfferMessagesToLinks();

		}

		public void Complete()
		{
			_isCompleting = true;
			SetCompletedIfNeeded();
		}

		public void Fault(Exception exception)
		{
			_taskCompletionSource.SetException(exception);
            foreach (var link in _targets.Where(x => x.LinkOptions.PropagateCompletion))
            {
                link.Target.Fault(exception);
            }
		}

		public Task Completion => _taskCompletionSource.Task;

		public List<ResourceEvent<TResource>> ConsumeMessage(DataflowMessageHeader messageHeader, ITargetBlock<List<ResourceEvent<TResource>>> target, out bool messageConsumed)
		{
			lock (_lock)
            {
				var link = _targets.FirstOrDefault(x => x.Target == target);
                if (link != null)
                {
                    // doesn't matter what they told us before, they are potentially ready to receive more messages
                    link.LastOfferedMessageReply = DataflowMessageStatus.NotAvailable;
                }

                while (true)
				{
					if (!_queue.TryDequeue(out var deltas)) // queue is empty, nothing left to do
					{
						messageConsumed = false;
						return null;
					}
					// this can happen if the entire lifecycle of the object started and ended before worker even touched it
					if(!deltas.Any())
						continue;
					var id = KeyOf(deltas.First());
					try
					{
						// some condition caused this queued item to be expired, go to next one
						if (!_items.Remove(id, out var item))
						{
							continue;
						}

						messageConsumed = true;
                        if(link != null) // only offer more messages if it's an actual linked block and not someone just asking to send em messages
						    Task.Run(() => OfferMessageToLink(link)); // avoid stack recursion
						return item;
					}
					finally
					{
						SetCompletedIfNeeded();
					}
				}
			}
		}

		private void OfferMessagesToLinks()
		{
			lock (_lock)
			{
				foreach (var link in _targets.ToList().Where(x => x.LastOfferedMessageReply != DataflowMessageStatus.Postponed))
				{
					do // keep feeding the link messages until queue is either empty or it tells us that it can't handle any more
					{
						OfferMessageToLink(link);
					} while (_queue.Count > 0 && link.LastOfferedMessageReply == DataflowMessageStatus.Accepted);
				}
			}
		}

		private void OfferMessageToLink(TargetLink link)
		{
			List<ResourceEvent<TResource>> msg;
			lock (_lock)
			{
				if (!_queue.TryPeek(out msg))
				{
					return; // queue is empty
				}
			}

			var header = new DataflowMessageHeader(++_msgId);
			link.LastOfferedMessageReply = link.Target.OfferMessage(header, msg, this, true);
			if (link.LastOfferedMessageReply == DataflowMessageStatus.DecliningPermanently)
				_targets.Remove(link);
		}

		public IDisposable LinkTo(ITargetBlock<List<ResourceEvent<TResource>>> target, DataflowLinkOptions linkOptions)
		{
			//todo: add support for max messages
			lock (_lock)
			{
				var link = new TargetLink
				{
					Target = target,
					LinkOptions = linkOptions,
					LastOfferedMessageReply = DataflowMessageStatus.NotAvailable
				};
				if (linkOptions.Append)
					_targets.Add(link);
				else
					_targets.Insert(0, link);

				OfferMessageToLink(link);
				return Disposable.Create(() => _targets.Remove(link));
			}
		}

		public void ReleaseReservation(DataflowMessageHeader messageHeader, ITargetBlock<List<ResourceEvent<TResource>>> target)
		{
			// don't support reservations
		}

		public bool ReserveMessage(DataflowMessageHeader messageHeader, ITargetBlock<List<ResourceEvent<TResource>>> target)
		{
			return false;
		}


		class TargetLink
		{
			internal ITargetBlock<List<ResourceEvent<TResource>>> Target;
			internal DataflowLinkOptions LinkOptions;
			internal DataflowMessageStatus LastOfferedMessageReply;
		}
	}

}
