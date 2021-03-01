using System;
using System.Linq;
using System.Threading;
using Datadog.Trace.ClrProfiler.CallTarget;
using Datadog.Trace.Configuration;
using Datadog.Trace.ExtensionMethods;
using Datadog.Trace.Tagging;

namespace Datadog.Trace.ClrProfiler.AutoInstrumentation.Http.HttpClientHandler
{
    internal class HttpClientHandlerCommon
    {
        private const string IntegrationName = nameof(IntegrationIds.HttpMessageHandler);
        private static readonly IntegrationInfo IntegrationId = IntegrationRegistry.GetIntegrationInfo(IntegrationName);

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
            if (state.Scope != null)
            {
                state.Scope.Span.SetHttpStatusCode(responseMessage.StatusCode, isServer: false);
                state.Scope.ExtractHeaderTags(new HttpHeadersCollection(responseMessage.Headers), Tracer.Instance);
            }

            state.Scope.DisposeWithException(exception);
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
    }
}
