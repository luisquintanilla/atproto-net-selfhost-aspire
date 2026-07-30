using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace AtProto.OAuth;

/// <summary>Tunables for <see cref="ClientMetadataResolver"/>.</summary>
public sealed class ClientMetadataResolverOptions
{
    /// <summary>Whether to accept confidential (<c>private_key_jwt</c>) clients. Off for the public-client MVP.</summary>
    public bool AllowConfidentialClients { get; init; }

    /// <summary>Whether to honor the <c>http://localhost</c> development-client exception. On by default.</summary>
    public bool AllowLocalhostClient { get; init; } = true;

    /// <summary>Maximum client-metadata response size in bytes.</summary>
    public int MaxResponseBytes { get; init; } = 256 * 1024;

    /// <summary>Overall timeout for a metadata fetch.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>TCP connect timeout for a metadata fetch.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Resolves an atproto <c>client_id</c> URL into a validated <see cref="ClientMetadata"/> document.
/// For real clients this performs an SSRF-hardened HTTPS <c>GET</c> (no redirects, public IPs only,
/// size and time capped, <c>application/json</c> only) and validates the returned document. For the
/// <c>http://localhost</c> development client it synthesizes the virtual document from the URL's query
/// parameters without any network access, exactly as the atproto OAuth spec describes.
/// </summary>
public sealed class ClientMetadataResolver : IDisposable
{
    private readonly HttpClient _http;
    private readonly ClientMetadataResolverOptions _options;

    /// <param name="handler">
    /// An optional message handler (for tests). When omitted, a hardened <see cref="SocketsHttpHandler"/>
    /// with SSRF-guarded connections and no auto-redirect is used.
    /// </param>
    public ClientMetadataResolver(ClientMetadataResolverOptions? options = null, HttpMessageHandler? handler = null)
    {
        _options = options ?? new ClientMetadataResolverOptions();
        _http = new HttpClient(handler ?? CreateHardenedHandler(_options), disposeHandler: true)
        {
            Timeout = _options.RequestTimeout,
        };
    }

    /// <summary>Resolve and validate the metadata for the given <c>client_id</c>.</summary>
    public async Task<ClientMetadata> ResolveAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ClientMetadataException("client_id is required");
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out Uri? uri))
            throw new ClientMetadataException("client_id must be an absolute URL");
        if (!string.IsNullOrEmpty(uri.Fragment))
            throw new ClientMetadataException("client_id must not contain a fragment");

        if (uri.Scheme == Uri.UriSchemeHttp && string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            if (!_options.AllowLocalhostClient)
                throw new ClientMetadataException("localhost client_id is not allowed");
            if (HasExplicitPort(clientId))
                throw new ClientMetadataException("localhost client_id must not specify a port");
            if (uri.AbsolutePath is not ("/" or ""))
                throw new ClientMetadataException("localhost client_id path must be empty");
            return SynthesizeLocalhost(clientId, uri);
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ClientMetadataException("client_id must use https (except the http://localhost dev exception)");
        if (HasExplicitPort(clientId))
            throw new ClientMetadataException("client_id must not include a port number");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ClientMetadataException("client_id must not include user info");

        return await FetchAsync(clientId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetch and parse a confidential client's JWKS from its <c>jwks_uri</c>, using the same
    /// SSRF-hardened HTTPS client as metadata resolution (no redirects, public IPs only, size and
    /// time capped). Inline <c>jwks</c> never reaches this path.
    /// </summary>
    public async Task<JsonWebKeySet> FetchJwksAsync(string jwksUri, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(jwksUri, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ClientMetadataException("jwks_uri must be an https URL");

        using var request = new HttpRequestMessage(HttpMethod.Get, jwksUri);
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ClientMetadataException($"could not fetch jwks: {e.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ClientMetadataException("jwks fetch timed out");
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new ClientMetadataException($"jwks fetch returned HTTP {(int)response.StatusCode} (must be 200)");

            byte[] body = await ReadCappedAsync(response.Content, _options.MaxResponseBytes, ct).ConfigureAwait(false);
            try
            {
                using JsonDocument json = JsonDocument.Parse(body);
                return JsonWebKeySet.Parse(json.RootElement);
            }
            catch (JsonException e)
            {
                throw new ClientMetadataException($"jwks is not valid JSON: {e.Message}");
            }
            catch (FormatException e)
            {
                throw new ClientMetadataException($"jwks is invalid: {e.Message}");
            }
        }
    }

    private async Task<ClientMetadata> FetchAsync(string clientId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, clientId);
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ClientMetadataException($"could not fetch client metadata: {e.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ClientMetadataException("client metadata fetch timed out");
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new ClientMetadataException($"client metadata fetch returned HTTP {(int)response.StatusCode} (must be 200)");

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw new ClientMetadataException($"client metadata Content-Type must be application/json, got '{mediaType ?? "none"}'");

            byte[] body = await ReadCappedAsync(response.Content, _options.MaxResponseBytes, ct).ConfigureAwait(false);
            try
            {
                using JsonDocument json = JsonDocument.Parse(body);
                return ClientMetadata.Parse(json.RootElement, clientId, _options);
            }
            catch (JsonException e)
            {
                throw new ClientMetadataException($"client metadata is not valid JSON: {e.Message}");
            }
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(rented.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    throw new ClientMetadataException($"client metadata exceeds the {maxBytes}-byte limit");
                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
        return buffer.ToArray();
    }

    private ClientMetadata SynthesizeLocalhost(string clientId, Uri uri)
    {
        var redirectUris = new List<string>();
        string? scope = null;

        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = Uri.UnescapeDataString(eq >= 0 ? pair[..eq] : pair);
            string value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..]) : string.Empty;

            switch (key)
            {
                case "redirect_uri":
                    ValidateLocalhostRedirect(value);
                    redirectUris.Add(value);
                    break;
                case "scope":
                    if (scope is not null)
                        throw new ClientMetadataException("localhost client_id allows only one scope parameter");
                    scope = value;
                    break;
            }
        }

        if (redirectUris.Count == 0)
        {
            redirectUris.Add("http://127.0.0.1/");
            redirectUris.Add("http://[::1]/");
        }

        scope ??= "atproto";
        if (!scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("atproto"))
            throw new ClientMetadataException("localhost client scope must include 'atproto'");

        return new ClientMetadata
        {
            ClientId = clientId,
            ApplicationType = "native",
            GrantTypes = ["authorization_code", "refresh_token"],
            ResponseTypes = ["code"],
            Scope = scope,
            RedirectUris = redirectUris,
            TokenEndpointAuthMethod = "none",
            DpopBoundAccessTokens = true,
            ClientName = "Development client",
            IsLocalhostClient = true,
        };
    }

    private static void ValidateLocalhostRedirect(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            throw new ClientMetadataException($"localhost redirect_uri '{value}' is not an absolute URI");
        if (!string.IsNullOrEmpty(uri.Fragment))
            throw new ClientMetadataException("localhost redirect_uri must not contain a fragment");
        if (uri.Scheme != Uri.UriSchemeHttp || !ClientMetadata.IsLoopbackHost(uri.Host))
            throw new ClientMetadataException("localhost redirect_uri must be an http loopback URL (127.0.0.1 or [::1])");
    }

    /// <summary>
    /// True when the raw URL string includes an explicit port in its authority. <see cref="Uri"/>
    /// silently strips default ports, so the raw string is inspected to enforce the "no port" rule.
    /// </summary>
    internal static bool HasExplicitPort(string url)
    {
        int schemeIdx = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx < 0) return false;
        int start = schemeIdx + 3;

        int end = url.Length;
        foreach (char delimiter in stackalloc[] { '/', '?', '#' })
        {
            int i = url.IndexOf(delimiter, start);
            if (i >= 0 && i < end) end = i;
        }

        string authority = url[start..end];
        int at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];

        if (authority.StartsWith('['))
        {
            int close = authority.IndexOf(']');
            return close >= 0 && close + 1 < authority.Length && authority[close + 1] == ':';
        }
        return authority.Contains(':');
    }

    private static SocketsHttpHandler CreateHardenedHandler(ClientMetadataResolverOptions options) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseProxy = false,
        UseCookies = false,
        MaxResponseHeadersLength = 64,
        ConnectTimeout = options.ConnectTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectCallback = SsrfGuardedConnectAsync,
    };

    /// <summary>
    /// A <see cref="SocketsHttpHandler.ConnectCallback"/> that resolves the target host, rejects any
    /// non-publicly-routable address, and connects to a validated IP directly (mitigating DNS
    /// rebinding, since TLS is still negotiated against the original request host afterwards).
    /// </summary>
    private static async ValueTask<Stream> SsrfGuardedConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        IReadOnlyList<IPAddress> allowed = await ResolveAllowedAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            foreach (IPAddress address in allowed)
            {
                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) when (allowed.Count > 1)
                {
                    // Try the next validated address.
                }
            }
            throw new ClientMetadataException($"could not connect to any address for '{context.DnsEndPoint.Host}'");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Resolve a host to its publicly routable addresses, throwing if none qualify.</summary>
    internal static async Task<IReadOnlyList<IPAddress>> ResolveAllowedAddressesAsync(string host, CancellationToken ct)
    {
        IPAddress[] candidates = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

        var allowed = new List<IPAddress>(candidates.Length);
        foreach (IPAddress address in candidates)
            if (SsrfGuard.IsPubliclyRoutable(address))
                allowed.Add(address);

        if (allowed.Count == 0)
            throw new ClientMetadataException($"host '{host}' does not resolve to a publicly routable address");
        return allowed;
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
