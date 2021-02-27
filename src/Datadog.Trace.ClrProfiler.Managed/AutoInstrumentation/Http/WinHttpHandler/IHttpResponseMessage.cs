namespace Datadog.Trace.ClrProfiler.AutoInstrumentation.Http.WinHttpHandler
{
    /// <summary>
    /// HttpResponseMessage interface for ducktyping
    /// </summary>
    public interface IHttpResponseMessage
    {
        /// <summary>
        /// Gets the status code of the http response
        /// </summary>
        int StatusCode { get; }

        /// <summary>
        /// Gets the response headers
        /// </summary>
        IHeaders Headers { get; }
    }
}
