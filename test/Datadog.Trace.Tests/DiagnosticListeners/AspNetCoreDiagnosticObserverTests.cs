#if !NETFRAMEWORK
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Datadog.Trace.Agent;
using Datadog.Trace.Configuration;
using Datadog.Trace.DiagnosticListeners;
using Datadog.Trace.Sampling;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
#if NETCOREAPP2_1
using Microsoft.AspNetCore.Hosting.Internal;
#else
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
#endif
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Newtonsoft.Json;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Datadog.Trace.Tests.DiagnosticListeners
{
    [CollectionDefinition(nameof(AspNetCoreDiagnosticObserverTests), DisableParallelization = true)]
    [TracerRestorer]
    public class AspNetCoreDiagnosticObserverTests
    {
        private const string IndexEndpointName = "Datadog.Trace.Tests.DiagnosticListeners.HomeController.Index (Datadog.Trace.Tests)";
        private const string ErrorEndpointName = "Datadog.Trace.Tests.DiagnosticListeners.HomeController.Error (Datadog.Trace.Tests)";
        private const string MyTestEndpointName = "Datadog.Trace.Tests.DiagnosticListeners.MyTestController.Index (Datadog.Trace.Tests)";
        private const string StatusCodeEndpointName = "Datadog.Trace.Tests.DiagnosticListeners.MyTestController.SetStatusCode (Datadog.Trace.Tests)";

        public static TheoryData<string, bool, string, ExpectedTags> MvcData =>
            new TheoryData<string, bool, string, ExpectedTags>
            {
                { "/", false, "GET /home/index", ConventionalRouteTags() },
                { "/Home", false, "GET /home/index", ConventionalRouteTags() },
                { "/Home/Index", false, "GET /home/index", ConventionalRouteTags() },
                { "/Home/Error", true, "GET /home/error", ConventionalRouteTags(action: "error") },
                { "/MyTest", false, "GET /mytest/index", ConventionalRouteTags(controller: "mytest") },
                { "/MyTest/index", false, "GET /mytest/index", ConventionalRouteTags(controller: "mytest") },
                { "/statuscode", false, "GET /statuscode/{value}", StatusCodeTags() },
                { "/statuscode/100", false, "GET /statuscode/{value}", StatusCodeTags() },
                { "/statuscode/Oops", false, "GET /statuscode/{value}", StatusCodeTags() },
                { "/statuscode/200", false, "GET /statuscode/{value}", StatusCodeTags() },
            };

        public static TheoryData<string, bool, string, ExpectedTags> EndpointRoutingData =>
            new TheoryData<string, bool, string, ExpectedTags>
            {
                { "/", false, "GET /home/index", ConventionalRouteTags(endpoint: IndexEndpointName) },
                { "/Home", false, "GET /home/index", ConventionalRouteTags(endpoint: IndexEndpointName) },
                { "/Home/Index", false, "GET /home/index", ConventionalRouteTags(endpoint: IndexEndpointName) },
                { "/Home/Error", true, "GET /home/error", ConventionalRouteTags(action: "error", endpoint: ErrorEndpointName) },
                { "/MyTest", false, "GET /mytest/index", ConventionalRouteTags(controller: "mytest", endpoint: MyTestEndpointName) },
                { "/MyTest/index", false, "GET /mytest/index", ConventionalRouteTags(controller: "mytest", endpoint: MyTestEndpointName) },
                { "/statuscode", false, "GET /statuscode/{value}", StatusCodeTags(endpoint: StatusCodeEndpointName) },
                { "/statuscode/100", false, "GET /statuscode/{value}", StatusCodeTags(endpoint: StatusCodeEndpointName) },
                { "/statuscode/Oops", false, "GET /statuscode/{value}", StatusCodeTags(endpoint: StatusCodeEndpointName) },
                { "/statuscode/200", false, "GET /statuscode/{value}", StatusCodeTags(endpoint: StatusCodeEndpointName) },
                { "/healthz", false, "GET /healthz", HealthCheckTags() },
                { "/echo", false, "GET /echo", EchoTags() },
                { "/echo/123", false, "GET /echo/{value?}", EchoTags() },
                { "/echo/false", true, "GET /echo/false", null },
            };

        [Fact]
        public async Task<string> CompleteDiagnosticObserverTest()
        {
            var tracer = GetTracer();

            var builder = new WebHostBuilder()
                .UseStartup<Startup>();

            var testServer = new TestServer(builder);
            var client = testServer.CreateClient();
            var observers = new List<DiagnosticObserver> { new AspNetCoreDiagnosticObserver(tracer) };
            string retValue = null;

            using (var diagnosticManager = new DiagnosticManager(observers))
            {
                diagnosticManager.Start();
                DiagnosticManager.Instance = diagnosticManager;
                retValue = await client.GetStringAsync("/Home");
                try
                {
                    await client.GetStringAsync("/Home/error");
                }
                catch { }
                DiagnosticManager.Instance = null;
            }

            return retValue;
        }

#if !NETCOREAPP2_1
        [Theory]
        [MemberData(nameof(EndpointRoutingData))]
        public async Task DiagnosticObserver_ForEndpointRouting_SubmitsSpans(string path, bool isError, string resourceName, ExpectedTags expectedTags)
        {
            var writer = new AgentWriterStub();
            var tracer = GetTracer(writer);

            var builder = new WebHostBuilder()
               .UseStartup<EndpointRoutingStartup>();

            var testServer = new TestServer(builder);
            var client = testServer.CreateClient();
            var observers = new List<DiagnosticObserver> { new AspNetCoreDiagnosticObserver(tracer) };

            using (var diagnosticManager = new DiagnosticManager(observers))
            {
                diagnosticManager.Start();
                try
                {
                    await client.GetStringAsync(path);
                }
                catch (Exception ex)
                {
                    Assert.True(isError, $"Unexpected error calling endpoint: {ex}");
                }

                // The diagnostic observer runs on a separate thread
                // This gives time for the Stop event to run and to be flushed to the writer
                var iterations = 10;
                while (iterations > 0)
                {
                    if (writer.Traces.Count > 0)
                    {
                        break;
                    }

                    Thread.Sleep(10);
                    iterations--;
                }
            }

            var trace = Assert.Single(writer.Traces);
            var span = Assert.Single(trace);

            AssertSpan(span, resourceName, expectedTags);
        }
#endif

        [Theory]
        [MemberData(nameof(MvcData))]
        public async Task DiagnosticObserver_SubmitsSpans(string path, bool isError, string resourceName, ExpectedTags expectedTags)
        {
            var writer = new AgentWriterStub();
            var tracer = GetTracer(writer);

            var builder = new WebHostBuilder()
                .UseStartup<Startup>();

            var testServer = new TestServer(builder);
            var client = testServer.CreateClient();
            var observers = new List<DiagnosticObserver> { new AspNetCoreDiagnosticObserver(tracer) };

            using (var diagnosticManager = new DiagnosticManager(observers))
            {
                diagnosticManager.Start();
                try
                {
                    await client.GetStringAsync(path);
                }
                catch (Exception ex)
                {
                    Assert.True(isError, $"Unexpected error calling endpoint: {ex}");
                }

                // The diagnostic observer runs on a separate thread
                // This gives time for the Stop event to run and to be flushed to the writer
                var iterations = 10;
                while (iterations > 0)
                {
                    if (writer.Traces.Count > 0)
                    {
                        break;
                    }

                    Thread.Sleep(10);
                    iterations--;
                }
            }

            var trace = Assert.Single(writer.Traces);
            var span = Assert.Single(trace);

            AssertSpan(span, resourceName, expectedTags);
        }

        [Fact]
        public void HttpRequestIn_PopulateSpan()
        {
            var tracer = GetTracer();

            IObserver<KeyValuePair<string, object>> observer = new AspNetCoreDiagnosticObserver(tracer);

#if NETCOREAPP2_1
            var context = new HostingApplication.Context { HttpContext = GetHttpContext() };
#else
            var context = new { HttpContext = GetHttpContext() };
#endif

            observer.OnNext(new KeyValuePair<string, object>("Microsoft.AspNetCore.Hosting.HttpRequestIn.Start", context));

            var scope = tracer.ActiveScope;

            Assert.NotNull(scope);

            var span = scope.Span;

            Assert.NotNull(span);

            AssertSpan(span, resourceName: "GET /home/?/action", expectedTags: null);
            Assert.Equal("GET", span.GetTag(Tags.HttpMethod));
            Assert.Equal("localhost", span.GetTag(Tags.HttpRequestHeadersHost));
            Assert.Equal("http://localhost/home/1/action", span.GetTag(Tags.HttpUrl));
        }

        private static Tracer GetTracer(IAgentWriter writer = null)
        {
            var settings = new TracerSettings();
            var agentWriter = writer ?? new Mock<IAgentWriter>().Object;
            var samplerMock = new Mock<ISampler>();

            return new Tracer(settings, agentWriter, samplerMock.Object, scopeManager: null, statsd: null);
        }

        private static HttpContext GetHttpContext()
        {
            var httpContext = new DefaultHttpContext();

            httpContext.Request.Headers.Add("hello", "hello");
            httpContext.Request.Headers.Add("world", "world");

            httpContext.Request.Host = new HostString("localhost");
            httpContext.Request.Scheme = "http";
            httpContext.Request.Path = "/home/1/action";
            httpContext.Request.Method = "GET";

            return httpContext;
        }

        private static void AssertSpan(
            Span span,
            string resourceName,
            ExpectedTags expectedTags)
        {
            Assert.Equal("aspnet_core.request", span.OperationName);
            Assert.Equal("aspnet_core", span.GetTag(Tags.InstrumentationName));
            Assert.Equal(SpanTypes.Web, span.Type);
            Assert.Equal(resourceName, span.ResourceName);
            Assert.Equal(SpanKinds.Server, span.GetTag(Tags.SpanKind));
            Assert.Equal(TracerConstants.Language, span.GetTag(Tags.Language));

            if (expectedTags is not null)
            {
                foreach (var expectedTag in expectedTags.Tags)
                {
                    Assert.Equal(expectedTag.Value, span.Tags.GetTag(expectedTag.Key));
                }
            }
        }

        private static ExpectedTags ConventionalRouteTags(
            string action = "index",
            string controller = "home",
            string endpoint = null) =>
            new ExpectedTags
            {
                { Tags.AspNetRoute, "{controller=home}/{action=index}/{id?}" },
                { Tags.AspNetController, controller },
                { Tags.AspNetAction, action },
                { Tags.AspNetEndpoint, endpoint },
            };

        private static ExpectedTags StatusCodeTags(string endpoint = null) =>
            new ExpectedTags
            {
                { Tags.AspNetRoute, "statuscode/{value=200}" },
                { Tags.AspNetController, "mytest" },
                { Tags.AspNetAction, "setstatuscode" },
                { Tags.AspNetEndpoint, endpoint },
            };

        private static ExpectedTags HealthCheckTags() =>
            new ExpectedTags
            {
                { Tags.AspNetRoute, "/healthz" },
                { Tags.AspNetEndpoint, "Custom Health Check" },
            };

        private static ExpectedTags EchoTags() =>
            new ExpectedTags
            {
                { Tags.AspNetRoute, "/echo/{value:int?}" },
                { Tags.AspNetEndpoint, "/echo/{value:int?} HTTP: GET" },
            };

        public class ExpectedTags : IXunitSerializable, IEnumerable<KeyValuePair<string, string>>
        {
            public Dictionary<string, string> Tags { get; private set; } = new Dictionary<string, string>();

            public void Add(string key, string value) => Tags.Add(key, value);

            public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Tags.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => Tags.GetEnumerator();

            public void Deserialize(IXunitSerializationInfo info)
            {
                Tags = JsonConvert.DeserializeObject<Dictionary<string, string>>(info.GetValue<string>(nameof(Tags)));
            }

            public void Serialize(IXunitSerializationInfo info)
            {
                info.AddValue(nameof(Tags), JsonConvert.SerializeObject(Tags));
            }
        }

        private class Startup
        {
            public void ConfigureServices(IServiceCollection services)
            {
#if NETCOREAPP2_1
                services.AddMvc();
#else
                services.AddMvc(options => options.EnableEndpointRouting = false);
#endif
            }

            public void Configure(IApplicationBuilder builder)
            {
                builder.UseMvc(routes =>
                {
                    routes.MapRoute("custom", "Test/{action=Index}", new { Controller = "MyTest" });
                    routes.MapRoute("default", "{controller=Home}/{action=Index}/{id?}");
                });
            }
        }

#if !NETCOREAPP2_1
        private class EndpointRoutingStartup
        {
            public void ConfigureServices(IServiceCollection services)
            {
                services.AddControllers();
                services.AddHealthChecks();
                services.AddAuthorization();
            }

            public void Configure(IApplicationBuilder app)
            {
                app.UseRouting();
                app.UseAuthorization();

                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                    endpoints.MapDefaultControllerRoute();
                    endpoints.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false })
                             .WithDisplayName("Custom Health Check");
                    endpoints.MapGet(
                        "/echo/{value:int?}",
                        context =>
                        {
                            var value = context.GetRouteValue("value")?.ToString();
                            return context.Response.WriteAsync(value ?? "No value");
                        });
                });
            }
        }
#endif

        [AttributeUsage(AttributeTargets.Class, Inherited = true)]
        private class TracerRestorerAttribute : BeforeAfterTestAttribute
        {
            private Tracer _tracer;

            public override void Before(MethodInfo methodUnderTest)
            {
                _tracer = Tracer.Instance;
                base.Before(methodUnderTest);
            }

            public override void After(MethodInfo methodUnderTest)
            {
                Tracer.Instance = _tracer;
                base.After(methodUnderTest);
            }
        }

        private class AgentWriterStub : IAgentWriter
        {
            public List<Span[]> Traces { get; } = new List<Span[]>();

            public Task FlushAndCloseAsync() => Task.CompletedTask;

            public Task FlushTracesAsync() => Task.CompletedTask;

            public Task<bool> Ping() => Task.FromResult(true);

            public void WriteTrace(Span[] trace) => Traces.Add(trace);
        }
    }

    /// <summary>
    /// Simple controller used for the aspnetcore test
    /// </summary>
#pragma warning disable SA1402 // File may only contain a single class
    public class HomeController : Controller
    {
        public async Task<string> Index()
        {
            await Task.Yield();
            return "Hello world";
        }

        public void Error()
        {
            throw new Exception();
        }
    }

    public class MyTestController : Controller
    {
        public async Task<string> Index()
        {
            await Task.Yield();
            return "Hello world";
        }

        [HttpGet("/statuscode/{value=200}")]
        public string SetStatusCode(int value) => value.ToString();
    }
}

#endif
