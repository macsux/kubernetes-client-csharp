using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using k8s.Models;
using Microsoft.Rest;
using Microsoft.Rest.Serialization;
using Newtonsoft.Json;

namespace k8s
{
    public partial class Kubernetes
    {
        /// <inheritdoc cref="IKubernetes"/>
        public async Task<HttpOperationResponse<KubernetesList<T>>> ListWithHttpMessagesAsync<T>(string namespaceParameter = default(string),
            bool? allowWatchBookmarks = default(bool?),
            string continueParameter = default(string),
            string fieldSelector = default(string),
            string labelSelector = default(string),
            int? limit = default(int?),
            string resourceVersion = default(string),
            int? timeoutSeconds = default(int?),
            bool? watch = default(bool?),
            string pretty = default(string),
            Dictionary<string, List<string>> customHeaders = null,
            CancellationToken cancellationToken = default(CancellationToken)) where T : IKubernetesObject
        {

            var entityAttribute = KubernetesObject.GetTypeMetadata<T>();
            if(entityAttribute?.PluralName == null)
                throw new InvalidOperationException($"{typeof(T)} doesn't have a plural name set via {typeof(KubernetesEntityAttribute)}");

            // Tracing
            var shouldTrace = ServiceClientTracing.IsEnabled;
            string invocationId = null;
            if (shouldTrace)
            {
                invocationId = ServiceClientTracing.NextInvocationId.ToString();
                var tracingParameters = new Dictionary<string, object>();
                tracingParameters.Add("allowWatchBookmarks", allowWatchBookmarks);
                tracingParameters.Add("continueParameter", continueParameter);
                tracingParameters.Add("fieldSelector", fieldSelector);
                tracingParameters.Add("labelSelector", labelSelector);
                tracingParameters.Add("limit", limit);
                tracingParameters.Add("resourceVersion", resourceVersion);
                tracingParameters.Add("timeoutSeconds", timeoutSeconds);
                tracingParameters.Add("watch", watch);
                tracingParameters.Add("namespaceParameter", namespaceParameter);
                tracingParameters.Add("pretty", pretty);
                tracingParameters.Add("cancellationToken", cancellationToken);
                ServiceClientTracing.Enter(invocationId, this, "ListNamespacedPod", tracingParameters);
            }
            // Construct URL
            var isLegacy = string.IsNullOrEmpty(entityAttribute.Group);
            var segments = new List<string>();
            segments.Add((BaseUri.AbsoluteUri.Trim('/')));
            if (isLegacy)
            {
                segments.Add("api");
                segments.Add("v1");
            }
            else
            {
                segments.Add("apis");
                segments.Add(entityAttribute.Group);
            }

            if (!string.IsNullOrEmpty(namespaceParameter))
            {
                segments.Add("namespaces");
                segments.Add(System.Uri.EscapeDataString(namespaceParameter));
            }
            segments.Add(entityAttribute.PluralName);

            var url = string.Join("/", segments);
            var queryParameters = new List<string>();
            if (allowWatchBookmarks != null)
            {
                queryParameters.Add(string.Format("allowWatchBookmarks={0}", System.Uri.EscapeDataString(SafeJsonConvert.SerializeObject(allowWatchBookmarks, SerializationSettings).Trim('"'))));
            }
            if (continueParameter != null)
            {
                queryParameters.Add(string.Format("continue={0}", System.Uri.EscapeDataString(continueParameter)));
            }
            if (fieldSelector != null)
            {
                queryParameters.Add(string.Format("fieldSelector={0}", System.Uri.EscapeDataString(fieldSelector)));
            }
            if (labelSelector != null)
            {
                queryParameters.Add(string.Format("labelSelector={0}", System.Uri.EscapeDataString(labelSelector)));
            }
            if (limit != null)
            {
                queryParameters.Add(string.Format("limit={0}", System.Uri.EscapeDataString(SafeJsonConvert.SerializeObject(limit, SerializationSettings).Trim('"'))));
            }
            if (resourceVersion != null)
            {
                queryParameters.Add(string.Format("resourceVersion={0}", System.Uri.EscapeDataString(resourceVersion)));
            }
            if (timeoutSeconds != null)
            {
                queryParameters.Add(string.Format("timeoutSeconds={0}", System.Uri.EscapeDataString(SafeJsonConvert.SerializeObject(timeoutSeconds, SerializationSettings).Trim('"'))));
            }
            if (watch != null)
            {
                queryParameters.Add(string.Format("watch={0}", System.Uri.EscapeDataString(SafeJsonConvert.SerializeObject(watch, SerializationSettings).Trim('"'))));
            }
            if (pretty != null)
            {
                queryParameters.Add(string.Format("pretty={0}", System.Uri.EscapeDataString(pretty)));
            }
            if (queryParameters.Count > 0)
            {
                url += "?" + string.Join("&", queryParameters);
            }
            // Create HTTP transport objects
            var httpRequest = new HttpRequestMessage();
            HttpResponseMessage httpResponse = null;
            httpRequest.Method = new HttpMethod("GET");
            httpRequest.RequestUri = new System.Uri(url);
            // Set Headers


            if (customHeaders != null)
            {
                foreach(var header in customHeaders)
                {
                    if (httpRequest.Headers.Contains(header.Key))
                    {
                        httpRequest.Headers.Remove(header.Key);
                    }
                    httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            // Serialize Request
            string requestContent = null;
            // Set Credentials
            if (Credentials != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Credentials.ProcessHttpRequestAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            }
            // Send Request
            if (shouldTrace)
            {
                ServiceClientTracing.SendRequest(invocationId, httpRequest);
            }
            cancellationToken.ThrowIfCancellationRequested();
            httpResponse = await HttpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            if (shouldTrace)
            {
                ServiceClientTracing.ReceiveResponse(invocationId, httpResponse);
            }
            var statusCode = httpResponse.StatusCode;
            cancellationToken.ThrowIfCancellationRequested();
            string responseContent = null;
            if ((int)statusCode != 200 && (int)statusCode != 401)
            {
                var ex = new HttpOperationException(string.Format("Operation returned an invalid status code '{0}'", statusCode));
                if (httpResponse.Content != null) {
                    responseContent = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                else {
                    responseContent = string.Empty;
                }
                ex.Request = new HttpRequestMessageWrapper(httpRequest, requestContent);
                ex.Response = new HttpResponseMessageWrapper(httpResponse, responseContent);
                if (shouldTrace)
                {
                    ServiceClientTracing.Error(invocationId, ex);
                }
                httpRequest.Dispose();
                if (httpResponse != null)
                {
                    httpResponse.Dispose();
                }
                throw ex;
            }
            // Create Result
            var result = new HttpOperationResponse<KubernetesList<T>>();
            result.Request = httpRequest;
            result.Response = httpResponse;
            // Deserialize Response
            if ((int)statusCode == 200)
            {
                responseContent = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                try
                {
                    result.Body = SafeJsonConvert.DeserializeObject<KubernetesList<T>>(responseContent, DeserializationSettings);
                }
                catch (JsonException ex)
                {
                    httpRequest.Dispose();
                    if (httpResponse != null)
                    {
                        httpResponse.Dispose();
                    }
                    throw new SerializationException("Unable to deserialize the response.", responseContent, ex);
                }
            }
            if (shouldTrace)
            {
                ServiceClientTracing.Exit(invocationId, result);
            }
            return result;
        }
    }
}
