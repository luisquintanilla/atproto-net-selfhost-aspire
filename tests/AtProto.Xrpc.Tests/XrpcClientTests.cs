using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AtProto;

namespace AtProto.Xrpc.Tests;

public sealed class XrpcClientTests
{
    [Fact]
    public async Task Query_encodes_nsid_and_parameters_in_the_request_uri()
    {
        var handler = new RecordingHandler(_ =>
            JsonResponse("""{"ok":true}"""));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test/")
        };
        var client = new XrpcClient(httpClient);

        Dictionary<string, object?> parameters = new()
        {
            ["actor"] = "did:plc:abc/def",
            ["limit"] = 25,
            ["include"] = true,
            ["tag"] = new[] { "one", "two" }
        };

        _ = await client.QueryAsync<JsonElement>("app.bsky.feed.getTimeline", parameters);

        Assert.NotNull(handler.Request);
        Assert.Equal(
            "/xrpc/app.bsky.feed.getTimeline?actor=did%3Aplc%3Aabc%2Fdef&limit=25&include=true&tag=one&tag=two",
            handler.Request!.RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Get, handler.Request.Method);
    }

    [Fact]
    public async Task Procedure_posts_a_json_request_body()
    {
        var handler = new RecordingHandler(_ =>
            JsonResponse("""{"uri":"at://did:plc:abc/app.bsky.feed.post/1"}"""));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test/")
        };
        var client = new XrpcClient(httpClient);

        var result = await client.ProcedureAsync<CreatePost, CreatedPost>(
            "com.example.feed.create",
            new CreatePost("hello", true));

        Assert.Equal("at://did:plc:abc/app.bsky.feed.post/1", result!.Uri);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/xrpc/com.example.feed.create", handler.Request.RequestUri!.PathAndQuery);
        Assert.Equal("application/json", handler.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(
            """{"text":"hello","published":true}""",
            handler.RequestBody);
    }

    [Fact]
    public async Task Query_deserializes_json_and_exposes_response_metadata()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = JsonResponse("""{"count":3}""");
            response.Headers.Add("x-request-id", "abc123");
            return response;
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new XrpcClient(httpClient);

        XrpcResponse<CountResponse> result = await client.QueryWithMetadataAsync<CountResponse>(
            "com.example.count");

        Assert.Equal(3, result.Value!.Count);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(["abc123"], result.GetHeaderValues("X-Request-ID"));
    }

    [Fact]
    public async Task QueryBinary_returns_binary_response_bytes()
    {
        byte[] expected = [0, 1, 2, 255];
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expected)
            });
        using var httpClient = CreateHttpClient(handler);
        var client = new XrpcClient(httpClient);

        byte[] actual = await client.QueryBinaryAsync("com.example.bytes");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Structured_xrpc_error_is_mapped_to_XrpcException()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(new
                {
                    error = "InvalidRequest",
                    message = "The actor is required."
                })
            });
        using var httpClient = CreateHttpClient(handler);
        var client = new XrpcClient(httpClient);

        XrpcException exception = await Assert.ThrowsAsync<XrpcException>(() =>
            client.QueryAsync<JsonElement>("com.example.query"));

        Assert.Equal("InvalidRequest", exception.ErrorCode);
        Assert.Equal("The actor is required.", exception.ErrorMessage);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("InvalidRequest", exception.Message);
    }

    [Fact]
    public async Task Unstructured_http_error_is_mapped_to_XrpcHttpException()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                ReasonPhrase = "Unavailable",
                Content = new StringContent("try again")
            });
        using var httpClient = CreateHttpClient(handler);
        var client = new XrpcClient(httpClient);

        XrpcHttpException exception = await Assert.ThrowsAsync<XrpcHttpException>(() =>
            client.QueryAsync<JsonElement>("com.example.query"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal("try again", exception.ResponseBody);
    }

    [Fact]
    public async Task Cancellation_is_forwarded_to_the_handler()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new XrpcClient(httpClient);
        using var cancellation = new CancellationTokenSource();

        Task request = client.QueryAsync<JsonElement>(
            "com.example.query",
            cancellationToken: cancellation.Token);
        await requestStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task Configured_request_timeout_cancels_a_slow_handler()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new XrpcClient(httpClient, new XrpcClientOptions
        {
            RequestTimeout = TimeSpan.FromMilliseconds(50)
        });

        Task request = client.QueryAsync<JsonElement>("com.example.query");
        await requestStarted.Task;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task Response_size_limit_is_enforced_before_deserialization()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":"too large"}""", Encoding.UTF8, "application/json")
            });
        using var httpClient = CreateHttpClient(handler);
        var client = new XrpcClient(httpClient, new XrpcClientOptions { MaxResponseBytes = 5 });

        XrpcResponseLimitExceededException exception =
            await Assert.ThrowsAsync<XrpcResponseLimitExceededException>(() =>
                client.QueryAsync<JsonElement>("com.example.query"));

        Assert.Equal(5, exception.MaximumBytes);
        Assert.True(exception.ContentLength > exception.MaximumBytes);
    }

    [Fact]
    public async Task Authentication_hook_can_inject_request_headers()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{"ok":true}"""));
        using var httpClient = CreateHttpClient(handler);
        var client = new XrpcClient(httpClient, new XrpcClientOptions
        {
            Authenticator = new DelegateXrpcAuthenticator((request, _) =>
            {
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token");
                return ValueTask.CompletedTask;
            })
        });

        _ = await client.QueryAsync<JsonElement>("com.example.query");

        Assert.Equal("Bearer token", handler.Request!.Headers.Authorization!.ToString());
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("com.example.")]
    [InlineData("-com.example.query")]
    [InlineData("com.-example.query")]
    [InlineData("com.example.query-qux")]
    [InlineData("com.example.query/name")]
    public async Task Invalid_nsid_is_rejected_before_sending(string nsid)
    {
        var handler = new RecordingHandler(_ => JsonResponse("{}"));
        using var httpClient = CreateHttpClient(handler);
        var client = new XrpcClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.QueryAsync<JsonElement>(nsid));

        Assert.Null(handler.Request);
    }

    private static HttpClient CreateHttpClient(RecordingHandler handler) =>
        new(handler)
        {
            BaseAddress = new Uri("https://example.test/")
        };

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed record CreatePost(string Text, bool Published);

    private sealed record CreatedPost(string Uri);

    private sealed record CountResponse(int Count);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public HttpRequestMessage? Request { get; private set; }

        public string? RequestBody { get; private set; }

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            ArgumentNullException.ThrowIfNull(responder);
            _responder = (request, _) => Task.FromResult(responder(request));
        }

        public RecordingHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null)
            {
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return await _responder(request, cancellationToken);
        }
    }
}
