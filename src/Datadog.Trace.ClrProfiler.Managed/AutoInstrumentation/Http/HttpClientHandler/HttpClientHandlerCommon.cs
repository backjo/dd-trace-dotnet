using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Datadog.Trace.ClrProfiler.CallTarget;
using Datadog.Trace.Configuration;
using Datadog.Trace.ExtensionMethods;
using Datadog.Trace.Logging;
using Datadog.Trace.Tagging;

namespace Datadog.Trace.ClrProfiler.AutoInstrumentation.Http.HttpClientHandler
{
    internal class HttpClientHandlerCommon
    {
        private const string IntegrationName = nameof(IntegrationIds.HttpMessageHandler);
        private static readonly IntegrationInfo IntegrationId = IntegrationRegistry.GetIntegrationInfo(IntegrationName);

        private static readonly IDatadogLogger Log = DatadogLogging.GetLoggerFor(typeof(HttpClientHandlerCommon));

        public static CallTargetState OnMethodBegin<TTarget, TRequest>(TTarget instance, TRequest requestMessage, CancellationToken cancellationToken)
            where TRequest : IHttpRequestMessage
        {
            if (IsTracingEnabled(requestMessage.Headers))
            {
                Scope scope = ScopeFactory.CreateOutboundHttpScope(Tracer.Instance, requestMessage.Method.Method, requestMessage.RequestUri, IntegrationId, out HttpTags tags);
                if (scope != null)
                {
                    tags.HttpClientHandlerType = instance.GetType().FullName;

                    // add distributed tracing headers to the HTTP request
                    SpanContextPropagator.Instance.Inject(scope.Span.Context, new HttpHeadersCollection(requestMessage.Headers));

                    return new CallTargetState(scope);
                }
            }

            return CallTargetState.GetDefault();
        }

        public static TResponse OnMethodEnd<TTarget, TResponse>(TTarget instance, TResponse responseMessage, Exception exception, CallTargetState state)
            where TResponse : IHttpResponseMessage
        {
            Scope scope = state.Scope;

            if (scope is null)
            {
                return responseMessage;
            }

            try
            {
                scope.Span.SetHttpStatusCode(responseMessage.StatusCode, isServer: false);

                var tagsFromHeaders = ExtractHeaderTags(responseMessage.Headers, Tracer.Instance);
                foreach (KeyValuePair<string, string> kvp in tagsFromHeaders)
                {
                    scope.Span.SetTag(kvp.Key, kvp.Value);
                }

                if (exception != null)
                {
                    scope.Span.SetException(exception);
                }
            }
            finally
            {
                scope.Dispose();
            }

            return responseMessage;
        }

        private static bool IsTracingEnabled(IHeaders headers)
        {
            if (headers.TryGetValues(HttpHeaderNames.TracingEnabled, out var headerValues))
            {
                if (headerValues is string[] arrayValues)
                {
                    for (var i = 0; i < arrayValues.Length; i++)
                    {
                        if (string.Equals(arrayValues[i], "false", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                if (headerValues != null && headerValues.Any(s => string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)))
                {
                    // tracing is disabled for this request via http header
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<KeyValuePair<string, string>> ExtractHeaderTags(IHeaders responseHeaders, IDatadogTracer tracer)
        {
            var settings = tracer.Settings;

            if (!settings.HeaderTags.IsEmpty())
            {
                try
                {
                    // extract propagation details from http headers
                    if (responseHeaders != null)
                    {
                        return SpanContextPropagator.Instance.ExtractHeaderTags(new HttpHeadersCollection(responseHeaders), settings.HeaderTags);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error extracting propagated HTTP headers.");
                }
            }

            return Enumerable.Empty<KeyValuePair<string, string>>();
        }
    }
}
