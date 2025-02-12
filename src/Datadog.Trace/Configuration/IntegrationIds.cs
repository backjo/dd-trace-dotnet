// ReSharper disable InconsistentNaming - Name is used for integration names
namespace Datadog.Trace.Configuration
{
    internal enum IntegrationIds
    {
        HttpMessageHandler,
        HttpSocketsHandler,
        WinHttpHandler,
        CurlHandler,
        AspNetCore,
        AdoNet,
        AspNet,
        AspNetMvc,
        AspNetWebApi2,
        GraphQL,
        MongoDb,
        XUnit,
        NUnit,
        MsTestV2,
        Wcf,
        WebRequest,
        ElasticsearchNet5,
        ElasticsearchNet, // NOTE: keep this name without the 6 to avoid breaking changes
        ServiceStackRedis,
        StackExchangeRedis,
        ServiceRemoting,
        RabbitMQ,
    }
}
