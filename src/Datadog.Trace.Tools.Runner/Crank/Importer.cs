using System;
using System.IO;
using System.Threading;
using Datadog.Trace.Ci;
using Datadog.Trace.Configuration;
using Datadog.Trace.ExtensionMethods;
using Newtonsoft.Json;
#pragma warning disable SA1201 // Elements should appear in the correct order

namespace Datadog.Trace.Tools.Runner.Crank
{
    internal class Importer
    {
        private static readonly IResultConverter[] Converters = new IResultConverter[]
        {
            new MsTimeResultConverter("benchmarks/start-time"),
            new MsTimeResultConverter("benchmarks/build-time"),
            new KbSizeResultConverter("benchmarks/published-size"),
            new MbSizeResultConverter("benchmarks/working-set"),
            new PercentageResultConverter("benchmarks/cpu"),

            new PercentageResultConverter("runtime-counter/cpu-usage"),
            new MbSizeResultConverter("runtime-counter/working-set"),
            new MbSizeResultConverter("runtime-counter/gc-heap-size"),
            new NumberResultConverter("runtime-counter/gen-0-gc-count"),
            new NumberResultConverter("runtime-counter/gen-1-gc-count"),
            new NumberResultConverter("runtime-counter/gen-2-gc-count"),
            new NumberResultConverter("runtime-counter/exception-count"),
            new NumberResultConverter("runtime-counter/threadpool-thread-count"),
            new NumberResultConverter("runtime-counter/monitor-lock-contention-count"),
            new NumberResultConverter("runtime-counter/threadpool-queue-length"),
            new NumberResultConverter("runtime-counter/threadpool-completed-items-count"),
            new PercentageResultConverter("runtime-counter/time-in-gc"),
            new ByteSizeResultConverter("runtime-counter/gen-0-size"),
            new ByteSizeResultConverter("runtime-counter/gen-1-size"),
            new ByteSizeResultConverter("runtime-counter/gen-2-size"),
            new ByteSizeResultConverter("runtime-counter/loh-size"),
            new NumberResultConverter("runtime-counter/alloc-rate"),
            new NumberResultConverter("runtime-counter/assembly-count"),
            new NumberResultConverter("runtime-counter/active-timer-count"),

            new NumberResultConverter("aspnet-counter/requests-per-second"),
            new NumberResultConverter("aspnet-counter/total-requests"),
            new NumberResultConverter("aspnet-counter/current-requests"),
            new NumberResultConverter("aspnet-counter/failed-requests"),

            new MsTimeResultConverter("http/firstrequest"),

            new NumberResultConverter("bombardier/requests"),
            new NumberResultConverter("bombardier/badresponses"),
            new UsTimeResultConverter("bombardier/latency/mean"),
            new UsTimeResultConverter("bombardier/latency/max"),
            new NumberResultConverter("bombardier/rps/max"),
            new NumberResultConverter("bombardier/rps/mean"),
            new NumberResultConverter("bombardier/throughput"),
            new BombardierRawConverter("bombardier/raw"),
        };

        public static int Process(string jsonFilePath)
        {
            Console.WriteLine("Importing Crank json result file...");
            try
            {
                string jsonContent = File.ReadAllText(jsonFilePath);
                var result = JsonConvert.DeserializeObject<Models.ExecutionResult>(jsonContent);

                if (result?.JobResults?.Jobs?.Count > 0)
                {
                    DateTimeOffset startTime = DateTimeOffset.UtcNow;

                    var fileName = Path.GetFileName(jsonFilePath);

                    var tracerSettings = new TracerSettings();
                    tracerSettings.ServiceName = "crank";
                    Tracer tracer = new Tracer(tracerSettings);

                    foreach (var jobItem in result.JobResults.Jobs)
                    {
                        Span span = tracer.StartSpan("crank.test", startTime: startTime);

                        span.SetTraceSamplingPriority(SamplingPriority.AutoKeep);
                        span.Type = SpanTypes.Test;
                        span.ResourceName = $"{fileName}.{jobItem.Key}";
                        CIEnvironmentValues.DecorateSpan(span);

                        span.SetTag(TestTags.Name, jobItem.Key);
                        span.SetTag(TestTags.Type, TestTags.TypeBenchmark);
                        span.SetTag(TestTags.Suite, fileName);
                        span.SetTag(TestTags.Framework, $"Crank");
                        span.SetTag(TestTags.Status, result.ReturnCode == 0 ? TestTags.StatusPass : TestTags.StatusFail);

                        if (result.JobResults.Properties?.Count > 0)
                        {
                            foreach (var propItem in result.JobResults.Properties)
                            {
                                span.SetTag("test.properties." + propItem.Key, propItem.Value);
                            }
                        }

                        var jobResult = jobItem.Value;

                        try
                        {
                            if (jobResult.Results?.Count > 0)
                            {
                                foreach (var resultItem in jobResult.Results)
                                {
                                    if (string.IsNullOrEmpty(resultItem.Key))
                                    {
                                        continue;
                                    }

                                    if (resultItem.Value is string valueString)
                                    {
                                        span.SetTag("test.results." + resultItem.Key.Replace("/", ".").Replace("-", "_"), valueString);
                                    }
                                    else
                                    {
                                        // bool converted = false;
                                        foreach (var converter in Converters)
                                        {
                                            if (converter.CanConvert(resultItem.Key))
                                            {
                                                converter.SetToSpan(span, "test.results." + resultItem.Key.Replace("/", ".").Replace("-", "_"), resultItem.Value);
                                                // converted = true;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }

                            if (jobResult.Environment?.Count > 0)
                            {
                                foreach (var envItem in jobResult.Environment)
                                {
                                    span.SetTag("environment." + envItem.Key, envItem.Value?.ToString() ?? "(null)");
                                }
                            }
                        }
                        finally
                        {
                            // var duration = TimeSpan.FromTicks((long)(durationNanoseconds / TimeConstants.NanoSecondsPerTick));
                            // span.Finish(startTime.Add(duration));
                            span.Finish();
                        }
                    }

                    // Ensure all the spans gets flushed before we report the success.
                    // In some cases the process finishes without sending the traces in the buffer.
                    SynchronizationContext context = SynchronizationContext.Current;
                    try
                    {
                        SynchronizationContext.SetSynchronizationContext(null);
                        tracer.FlushAsync().GetAwaiter().GetResult();
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(context);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return 1;
            }

            Console.WriteLine("The result file was imported successfully.");
            return 0;
        }
    }
}
