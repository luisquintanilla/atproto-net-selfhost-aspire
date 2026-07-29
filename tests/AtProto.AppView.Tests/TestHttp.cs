using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AtProto.AppView.Tests;

/// <summary>
/// A programmable <see cref="HttpMessageHandler"/> for the AppView's outbound XRPC calls. Routes on
/// (method, path) so a test can stand in for a PDS without any network, and records every request so
/// assertions can check the exact URL/headers/body the service produced.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _route;
    public List<HttpRequestMessage> Requests { get; } = new();

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> route) => _route = route;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer the body up front so assertions can read it after the handler returns.
        if (request.Content is not null)
            await request.Content.LoadIntoBufferAsync();
        Requests.Add(request);
        return _route(request);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] body, string contentType, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> that hands out clients bound to a single stub handler.</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly StubHttpMessageHandler _handler;
    public StubHttpClientFactory(StubHttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

internal static class TestConfig
{
    public static IConfiguration InMemory(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}

/// <summary>The materialized result of executing a minimal-API <see cref="IResult"/> in-memory.</summary>
internal sealed record ExecutedResult(int Status, string? ContentType, byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);
}

internal static class ResultRunner
{
    // Minimal-API results resolve ILoggerFactory / JSON options from RequestServices when executed.
    private static readonly IServiceProvider Services = new ServiceCollection()
        .AddLogging()
        .AddOptions()
        .BuildServiceProvider();

    /// <summary>Run an <see cref="IResult"/> against an in-memory <see cref="HttpContext"/> and capture what it wrote.</summary>
    public static async Task<ExecutedResult> RunAsync(IResult result)
    {
        var context = new DefaultHttpContext { RequestServices = Services };
        var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        return new ExecutedResult(context.Response.StatusCode, context.Response.ContentType, body.ToArray());
    }
}
