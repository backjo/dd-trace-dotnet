using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Datadog.Trace.ExtensionMethods;
using Datadog.Trace.Headers;
using Datadog.Trace.Logging;

namespace Datadog.Trace.ClrProfiler.AutoInstrumentation.Http
{
    internal static class AutoInstrumentationExtensions
    {
        private static readonly IDatadogLogger Log = DatadogLogging.GetLoggerFor(typeof(AutoInstrumentationExtensions));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ExtractHeaderTags(this Scope scope, IHeadersCollection headers, Tracer tracer)
        {
            var settings = tracer.Settings;

            if (!settings.HeaderTags.IsEmpty())
            {
                try
                {
                    // extract propagation details from http headers
                    var tagsFromHeaders = SpanContextPropagator.Instance.ExtractHeaderTags(headers, settings.HeaderTags);
                    foreach (KeyValuePair<string, string> kvp in tagsFromHeaders)
                    {
                        scope.Span.SetTag(kvp.Key, kvp.Value);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error extracting propagated HTTP headers.");
                }
            }
        }
    }
}
