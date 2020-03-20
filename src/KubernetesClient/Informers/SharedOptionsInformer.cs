using System;
using System.Collections.Generic;
using k8s.Informers.Notifications;

namespace k8s.Informers
{
    /// <summary>
    /// Manages multiple <see cref="SharedInformer{TKey,TResource}"/> for each unique set of <typeparamref name="TOptions"/> and ensures subscriptions are attached to correct one
    /// </summary>
    /// <typeparam name="TResource"></typeparam>
    /// <typeparam name="TOptions"></typeparam>
    public class SharedOptionsInformer<TResource, TOptions> : IInformer<TResource,TOptions>
    {
        private readonly IInformer<TResource, TOptions> _masterInformer;
        private readonly Func<IInformer<TResource>, IInformer<TResource>> _sharedInformerFactory;
        private readonly Dictionary<TOptions, IInformer<TResource>> _sharedInformers = new Dictionary<TOptions, IInformer<TResource>>();

        public SharedOptionsInformer(
           IInformer<TResource, TOptions> masterInformer,
            Func<IInformer<TResource>, IInformer<TResource>> sharedInformerFactory)
        {
            _masterInformer = masterInformer;
            _sharedInformerFactory = sharedInformerFactory;
        }



        public IObservable<ResourceEvent<TResource>> GetResource(ResourceStreamType type, TOptions options)
        {
            if (!_sharedInformers.TryGetValue(options, out var sharedInformer))
            {
                var optionLockedMasterInformer = _masterInformer.WithOptions(options);
                sharedInformer = _sharedInformerFactory(optionLockedMasterInformer);
                _sharedInformers.Add(options, sharedInformer);
            }
            return sharedInformer.GetResource(type);
        }
    }
}
