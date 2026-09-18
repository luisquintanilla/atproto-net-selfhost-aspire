using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AtProto;

/// <summary>Configures an <see cref="XrpcClient"/>.</summary>
public sealed class XrpcClientOptions
{
    /// <summary>
    /// Gets the server base address. When omitted, the address configured on the
    /// supplied <see cref="HttpClient"/> is used.
    /// </summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>
    /// Gets the maximum duration for an individual request. The infinite value
    /// delegates timeout behavior to the supplied <see cref="HttpClient"/>.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Gets the maximum number of response bytes to buffer. Set to <see langword="null"/>
    /// to disable the client-side limit.
    /// </summary>
    public long? MaxResponseBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Gets the JSON serializer settings used for request and response bodies.</summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; init; }

    /// <summary>Gets the optional hook that adds authentication to each request.</summary>
    public IXrpcAuthenticator? Authenticator { get; init; }
}

/// <summary>Provides authentication for an outgoing XRPC request.</summary>
public interface IXrpcAuthenticator
{
    /// <summary>Authenticates <paramref name="request"/> before it is sent.</summary>
    ValueTask AuthenticateAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default);
}

/// <summary>Adapts a delegate to <see cref="IXrpcAuthenticator"/>.</summary>
public sealed class DelegateXrpcAuthenticator : IXrpcAuthenticator
{
    private readonly Func<HttpRequestMessage, CancellationToken, ValueTask> _handler;

    /// <summary>Creates an authenticator backed by <paramref name="handler"/>.</summary>
    public DelegateXrpcAuthenticator(
        Func<HttpRequestMessage, CancellationToken, ValueTask> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <inheritdoc />
    public ValueTask AuthenticateAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default) =>
        _handler(request, cancellationToken);
}

/// <summary>Contains a decoded XRPC error returned by a server.</summary>
public sealed class XrpcError
{
    internal XrpcError(
        string errorCode,
        string? message,
        HttpStatusCode statusCode,
        string? responseBody)
    {
        ErrorCode = errorCode;
        Message = message;
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <summary>Gets the protocol error identifier, such as <c>InvalidRequest</c>.</summary>
    public string ErrorCode { get; }

    /// <summary>Gets the optional server-provided error message.</summary>
    public string? Message { get; }

    /// <summary>Gets the HTTP status associated with the protocol error.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Gets the original response body, when it was available.</summary>
    public string? ResponseBody { get; }
}

/// <summary>Indicates that a server returned a structured XRPC error.</summary>
public class XrpcException : Exception
{
    internal XrpcException(XrpcError error)
        : base(error.Message is null
            ? error.ErrorCode
            : $"{error.ErrorCode}: {error.Message}")
    {
        Error = error;
    }

    /// <summary>Gets the decoded protocol error.</summary>
    public XrpcError Error { get; }

    /// <summary>Gets the protocol error identifier.</summary>
    public string ErrorCode => Error.ErrorCode;

    /// <summary>Gets the server-provided protocol error message.</summary>
    public string? ErrorMessage => Error.Message;

    /// <summary>Gets the HTTP status associated with the error.</summary>
    public HttpStatusCode StatusCode => Error.StatusCode;

    /// <summary>Gets the original response body.</summary>
    public string? ResponseBody => Error.ResponseBody;
}

/// <summary>Indicates a non-success HTTP response without a structured XRPC error.</summary>
public sealed class XrpcHttpException : HttpRequestException
{
    internal XrpcHttpException(
        HttpStatusCode statusCode,
        string? reasonPhrase,
        string? responseBody)
        : base(CreateMessage(statusCode, reasonPhrase), inner: null, statusCode)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <summary>Gets the returned HTTP status.</summary>
    public new HttpStatusCode StatusCode { get; }

    /// <summary>Gets the original response body, when it was available.</summary>
    public string? ResponseBody { get; }

    private static string CreateMessage(HttpStatusCode statusCode, string? reasonPhrase) =>
        $"XRPC request failed with HTTP {(int)statusCode} ({reasonPhrase ?? statusCode.ToString()}).";
}

/// <summary>Indicates that a response exceeded the configured client-side limit.</summary>
public sealed class XrpcResponseLimitExceededException : Exception
{
    internal XrpcResponseLimitExceededException(long maximumBytes, long? contentLength)
        : base(contentLength is null
            ? $"The XRPC response exceeded the configured {maximumBytes}-byte limit."
            : $"The XRPC response is {contentLength} bytes, exceeding the configured {maximumBytes}-byte limit.")
    {
        MaximumBytes = maximumBytes;
        ContentLength = contentLength;
    }

    /// <summary>Gets the configured response limit.</summary>
    public long MaximumBytes { get; }

    /// <summary>Gets the declared content length, when the server supplied one.</summary>
    public long? ContentLength { get; }
}

/// <summary>Represents a successful XRPC result together with response metadata.</summary>
/// <typeparam name="T">The decoded response type.</typeparam>
public sealed class XrpcResponse<T>
{
    internal XrpcResponse(
        T? value,
        HttpStatusCode statusCode,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers)
    {
        Value = value;
        StatusCode = statusCode;
        Headers = headers;
    }

    /// <summary>Gets the decoded response value.</summary>
    public T? Value { get; }

    /// <summary>Gets the HTTP status returned by the server.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Gets a case-insensitive snapshot of response and content headers.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

    /// <summary>Gets all values for a response header, or an empty sequence when absent.</summary>
    public IReadOnlyList<string> GetHeaderValues(string name) =>
        Headers.TryGetValue(name, out IReadOnlyList<string>? values)
            ? values
            : Array.Empty<string>();
}

/// <summary>
/// Sends framework-neutral HTTP requests to an AT Protocol XRPC endpoint.
/// The client does not own or dispose the supplied <see cref="HttpClient"/>.
/// </summary>
public sealed class XrpcClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;
    private readonly TimeSpan _requestTimeout;
    private readonly long? _maxResponseBytes;
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly IXrpcAuthenticator? _authenticator;

    /// <summary>Creates a client using the base address on <paramref name="httpClient"/>.</summary>
    public XrpcClient(HttpClient httpClient, XrpcClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        options ??= new XrpcClientOptions();

        Uri baseAddress = options.BaseAddress ?? httpClient.BaseAddress
            ?? throw new ArgumentException(
                "A base address must be supplied in the options or HttpClient.",
                nameof(httpClient));

        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("The base address must be absolute.", nameof(options));
        }

        if (!string.IsNullOrEmpty(baseAddress.Query) || !string.IsNullOrEmpty(baseAddress.Fragment))
        {
            throw new ArgumentException(
                "The base address must not contain a query or fragment.",
                nameof(options));
        }

        if (options.RequestTimeout != Timeout.InfiniteTimeSpan &&
            (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "RequestTimeout must be positive, infinite, or no greater than Int32.MaxValue milliseconds.");
        }

        if (options.MaxResponseBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxResponseBytes must be positive or null.");
        }

        _httpClient = httpClient;
        _baseAddress = EnsureTrailingSlash(baseAddress);
        _requestTimeout = options.RequestTimeout;
        _maxResponseBytes = options.MaxResponseBytes;
        _jsonSerializerOptions = options.JsonSerializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _authenticator = options.Authenticator;
    }

    /// <summary>Executes an XRPC query and deserializes its JSON response.</summary>
    /// <typeparam name="TResponse">The expected response type.</typeparam>
    /// <param name="nsid">The procedure NSID, for example <c>app.bsky.feed.getTimeline</c>.</param>
    /// <param name="parameters">
    /// An anonymous object, dictionary, or sequence of key/value pairs used as query parameters.
    /// Enumerable values are encoded as repeated parameters.
    /// </param>
    public async Task<TResponse?> QueryAsync<TResponse>(
        string nsid,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        XrpcResponse<TResponse> response = await QueryWithMetadataAsync<TResponse>(
            nsid,
            parameters,
            cancellationToken).ConfigureAwait(false);
        return response.Value;
    }

    /// <summary>Executes an XRPC query and returns its value plus response metadata.</summary>
    public Task<XrpcResponse<TResponse>> QueryWithMetadataAsync<TResponse>(
        string nsid,
        object? parameters = null,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<TResponse>(
            HttpMethod.Get,
            nsid,
            parameters,
            body: null,
            hasBody: false,
            cancellationToken);

    /// <summary>Executes an XRPC query and returns its raw response bytes.</summary>
    public async Task<byte[]> QueryBinaryAsync(
        string nsid,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        RawResponse response = await SendAsync(
            HttpMethod.Get,
            nsid,
            parameters,
            body: null,
            hasBody: false,
            cancellationToken).ConfigureAwait(false);
        return response.Body;
    }

    /// <summary>Executes an XRPC procedure with a JSON request and deserializes its JSON response.</summary>
    public async Task<TResponse?> ProcedureAsync<TRequest, TResponse>(
        string nsid,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        XrpcResponse<TResponse> response = await ProcedureWithMetadataAsync<TRequest, TResponse>(
            nsid,
            request,
            cancellationToken).ConfigureAwait(false);
        return response.Value;
    }

    /// <summary>Executes an XRPC procedure and returns its value plus response metadata.</summary>
    public Task<XrpcResponse<TResponse>> ProcedureWithMetadataAsync<TRequest, TResponse>(
        string nsid,
        TRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<TResponse>(
            HttpMethod.Post,
            nsid,
            parameters: null,
            request,
            hasBody: true,
            cancellationToken);

    /// <summary>Executes an XRPC procedure with a JSON request and ignores its response value.</summary>
    public async Task ProcedureAsync<TRequest>(
        string nsid,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = await SendAsync(
            HttpMethod.Post,
            nsid,
            parameters: null,
            request,
            hasBody: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Executes an XRPC procedure and returns its raw response bytes.</summary>
    public async Task<byte[]> ProcedureBinaryAsync<TRequest>(
        string nsid,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        RawResponse response = await SendAsync(
            HttpMethod.Post,
            nsid,
            parameters: null,
            request,
            hasBody: true,
            cancellationToken).ConfigureAwait(false);
        return response.Body;
    }

    private async Task<XrpcResponse<TResponse>> SendJsonAsync<TResponse>(
        HttpMethod method,
        string nsid,
        object? parameters,
        object? body,
        bool hasBody,
        CancellationToken cancellationToken)
    {
        RawResponse response = await SendAsync(
            method,
            nsid,
            parameters,
            body,
            hasBody,
            cancellationToken).ConfigureAwait(false);

        TResponse? value = response.Body.Length == 0
            ? default
            : JsonSerializer.Deserialize<TResponse>(response.Body, _jsonSerializerOptions);

        return new XrpcResponse<TResponse>(value, response.StatusCode, response.Headers);
    }

    private async Task<RawResponse> SendAsync(
        HttpMethod method,
        string nsid,
        object? parameters,
        object? body,
        bool hasBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BuildUri(nsid, parameters));
        if (hasBody)
        {
            string json = JsonSerializer.Serialize(body, _jsonSerializerOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_requestTimeout != Timeout.InfiniteTimeSpan)
        {
            timeoutSource.CancelAfter(_requestTimeout);
        }

        CancellationToken requestCancellation = timeoutSource.Token;
        if (_authenticator is not null)
        {
            await _authenticator.AuthenticateAsync(request, requestCancellation).ConfigureAwait(false);
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            requestCancellation).ConfigureAwait(false);

        byte[] responseBody = await ReadResponseBodyAsync(response, requestCancellation).ConfigureAwait(false);
        string? bodyText = null;

        if (!response.IsSuccessStatusCode)
        {
            bodyText = DecodeBody(responseBody);
            if (TryReadXrpcError(bodyText, response.StatusCode, out XrpcError? error) &&
                error is not null)
            {
                throw new XrpcException(error);
            }

            throw new XrpcHttpException(response.StatusCode, response.ReasonPhrase, bodyText);
        }

        return new RawResponse(
            response.StatusCode,
            responseBody,
            CaptureHeaders(response));
    }

    private async Task<byte[]> ReadResponseBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        long? contentLength = response.Content.Headers.ContentLength;
        if (_maxResponseBytes is long maximumBytes &&
            contentLength is long declaredLength &&
            declaredLength > maximumBytes)
        {
            throw new XrpcResponseLimitExceededException(maximumBytes, declaredLength);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream(
            contentLength is long length && length <= int.MaxValue
                ? (int)length
                : 0);
        byte[] readBuffer = new byte[81920];

        while (true)
        {
            int read = await stream.ReadAsync(readBuffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (_maxResponseBytes is long maximum && buffer.Length > maximum - read)
            {
                throw new XrpcResponseLimitExceededException(maximum, null);
            }

            buffer.Write(readBuffer, 0, read);
        }

        return buffer.ToArray();
    }

    private Uri BuildUri(string nsid, object? parameters)
    {
        ValidateNsid(nsid);
        string relativePath = $"xrpc/{nsid}";
        string query = BuildQueryString(parameters);
        if (query.Length != 0)
        {
            relativePath += "?" + query;
        }

        return new Uri(_baseAddress, relativePath);
    }

    private static void ValidateNsid(string nsid)
    {
        if (string.IsNullOrWhiteSpace(nsid))
        {
            throw new ArgumentException("An NSID is required.", nameof(nsid));
        }

        string[] segments = nsid.Split('.');
        if (nsid.Length > 317
            || segments.Length < 3
            || segments.Any(static segment => segment.Length == 0 || segment.Length > 63))
        {
            throw new ArgumentException("The NSID has an invalid structure.", nameof(nsid));
        }

        for (var index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            bool isName = index == segments.Length - 1;
            if (!IsAsciiLetter(segment[0])
                || segment.Any(character => isName
                    ? !IsAsciiLetterOrDigit(character)
                    : !IsAsciiLetterOrDigit(character) && character != '-'))
            {
                throw new ArgumentException("The NSID contains invalid characters.", nameof(nsid));
            }

            if (!isName && (segment[0] == '-' || segment[^1] == '-'))
            {
                throw new ArgumentException("An NSID authority segment cannot start or end with a hyphen.", nameof(nsid));
            }
        }
    }

    private static bool IsAsciiLetter(char value) =>
        (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

    private static bool IsAsciiLetterOrDigit(char value) =>
        IsAsciiLetter(value) || (value >= '0' && value <= '9');

    private static string BuildQueryString(object? parameters)
    {
        if (parameters is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach ((string name, object? value) in EnumerateParameters(parameters))
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("Query parameter names cannot be empty.", nameof(parameters));
            }

            AppendQueryValues(builder, name, value);
        }

        return builder.ToString();
    }

    private static IEnumerable<(string Name, object? Value)> EnumerateParameters(object parameters)
    {
        if (parameters is IEnumerable<KeyValuePair<string, object?>> objectDictionary)
        {
            foreach (KeyValuePair<string, object?> pair in objectDictionary)
            {
                yield return (pair.Key, pair.Value);
            }

            yield break;
        }

        if (parameters is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                yield return (Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty, entry.Value);
            }

            yield break;
        }

        if (parameters is IEnumerable enumerable && parameters is not string)
        {
            bool foundPairs = false;
            foreach (object? item in enumerable)
            {
                if (item is null)
                {
                    continue;
                }

                PropertyInfo? keyProperty = item.GetType().GetProperty("Key");
                PropertyInfo? valueProperty = item.GetType().GetProperty("Value");
                if (keyProperty?.GetValue(item) is string key)
                {
                    foundPairs = true;
                    yield return (key, valueProperty?.GetValue(item));
                }
            }

            if (foundPairs)
            {
                yield break;
            }
        }

        foreach (PropertyInfo property in parameters.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetMethod is not null && property.GetIndexParameters().Length == 0)
            {
                yield return (property.Name, property.GetValue(parameters));
            }
        }
    }

    private static void AppendQueryValues(StringBuilder builder, string name, object? value)
    {
        if (value is IEnumerable enumerable &&
            value is not string &&
            value is not byte[] &&
            value is not JsonElement)
        {
            foreach (object? item in enumerable)
            {
                AppendQueryValue(builder, name, item);
            }

            return;
        }

        AppendQueryValue(builder, name, value);
    }

    private static void AppendQueryValue(StringBuilder builder, string name, object? value)
    {
        if (value is null)
        {
            return;
        }

        string text = ConvertQueryValue(value);
        if (builder.Length != 0)
        {
            builder.Append('&');
        }

        builder.Append(Uri.EscapeDataString(name));
        builder.Append('=');
        builder.Append(Uri.EscapeDataString(text));
    }

    private static string ConvertQueryValue(object value) =>
        value switch
        {
            string text => text,
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            JsonElement element => element.ToString(),
            _ => value.ToString() ?? string.Empty
        };

    private static bool TryReadXrpcError(
        string? body,
        HttpStatusCode statusCode,
        out XrpcError? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? code = null;
            string? message = null;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("error") || property.NameEquals("Error"))
                {
                    code = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : null;
                }
                else if (property.NameEquals("message") || property.NameEquals("Message"))
                {
                    message = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : null;
                }
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            error = new XrpcError(code, message, statusCode, body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? DecodeBody(byte[] body) =>
        body.Length == 0 ? null : Encoding.UTF8.GetString(body);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CaptureHeaders(
        HttpResponseMessage response)
    {
        var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        AddHeaders(headers, response.Headers);
        AddHeaders(headers, response.Content.Headers);
        return new ReadOnlyDictionary<string, IReadOnlyList<string>>(headers);
    }

    private static void AddHeaders(
        Dictionary<string, IReadOnlyList<string>> target,
        HttpHeaders source)
    {
        foreach ((string name, IEnumerable<string> values) in source)
        {
            target[name] = values.ToArray();
        }
    }

    private static Uri EnsureTrailingSlash(Uri address)
    {
        if (address.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            return address;
        }

        return new Uri(address.AbsoluteUri + "/", UriKind.Absolute);
    }

    private sealed record RawResponse(
        HttpStatusCode StatusCode,
        byte[] Body,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Headers);
}
