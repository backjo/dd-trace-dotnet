# Custom Instrumentation of an ASP.NET Core app with Serilog
The .NET Tracer provides out-of-the-box automatic instrumentation for ASP.NET Core and automatic trace injection, but if you would like to instrument the application yourself, this sample application serves as a starting point to easily add these features to your application.

This sample follows the [Setting up Serilog in ASP.NET Core 3](https://nblumhardt.com/2019/10/serilog-in-aspnetcore-3/) tutorial to create an ASP.NET Core 3.1 app from scratch and add automatic request logging when an endpoint is requested.

Additionally, the sample contains two middleware classes to add visibility using the `Datadog.Trace` library:
1. `DatadogTracingMiddleware` - This middleware executes after the Serilog middleware to create a span when an endpoint is requested.
1. `DatadogManualTraceLogCorrelationMiddleware` - This middleware executes after the `DatadogTracingMiddleware` and manually adds properties of the active span to the Serilog log context.

## Application setup
To run this application, run the following commands:

```
cd AspNetCoreWithSerilog
dotnet run
```

## Results
### Successful request
![Datadog UI for a successful request with one ASP.NET Core span](https://user-images.githubusercontent.com/13769665/96036749-c2cb3280-0e19-11eb-9ff1-b9771778d032.PNG)

### Failing request
![Datadog UI for a failing request with one ASP.NET Core span](https://user-images.githubusercontent.com/13769665/96036754-c5c62300-0e19-11eb-9401-5796747b9ae3.PNG)