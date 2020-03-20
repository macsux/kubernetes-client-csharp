namespace k8s.Informers
{
    public class KubernetesInformerOptions // theoretically this could be done with QObservable, but parsing expression trees is too much overhead at this point
    {
        /// <summary>
        /// The default options for kubernetes informer, without any server side filters
        /// </summary>
        public static KubernetesInformerOptions Default { get; } = new KubernetesInformerOptions();
        /// <summary>
        /// The namespace to which observable stream should be filtered
        /// </summary>
        public string Namespace { get; set; }
        // todo: add label selector. needs a proper builder as there are many permutations

    }
}
