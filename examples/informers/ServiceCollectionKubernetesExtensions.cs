using System;
using System.Linq;
using k8s;
using k8s.Informers;
using k8s.Informers.Cache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Rest;
using Microsoft.Rest.TransientFaultHandling;

namespace informers
{
    public static class Extensions
    {
        public static IServiceCollection AddKubernetes(this IServiceCollection services)
        {
            services.AddHostedService<ControllerService>();
            var controllers = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(x => x.DefinedTypes)
                .Where(x => x.IsClass && !x.IsAbstract && typeof(IController).IsAssignableFrom(x))
                .ToList();
            foreach (var controller in controllers)
            {
                services.AddSingleton(typeof(IController), controller);
            }
            var config = KubernetesClientConfiguration.BuildDefaultConfig();

            services.AddSingleton(config);
            services.AddSingleton<RetryDelegatingHandler>();
            services.AddHttpClient("DefaultName")
                .AddTypedClient<IKubernetes>((httpClient, serviceProvider) =>
                    new Kubernetes(
                        serviceProvider.GetRequiredService<KubernetesClientConfiguration>(),
                        httpClient))
                .AddHttpMessageHandler(() => new RetryDelegatingHandler()
                {
                    RetryPolicy = new RetryPolicy<HttpStatusCodeErrorDetectionStrategy>(new ExponentialBackoffRetryStrategy())
                })
                .AddHttpMessageHandler(KubernetesClientConfiguration.CreateWatchHandler)
                .ConfigurePrimaryHttpMessageHandler(config.CreateDefaultHttpClientHandler);
            var kubernetesResources = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(x => x.DefinedTypes)
                .Where(x => typeof(IKubernetesObject).IsAssignableFrom(x))
                .ToList();
            services.AddTransient(typeof(KubernetesInformer<>));
            services.AddSingleton(typeof(IKubernetesInformer<>), typeof(SharedKubernetesInformer<>));
            return services;
        }
    }
}
