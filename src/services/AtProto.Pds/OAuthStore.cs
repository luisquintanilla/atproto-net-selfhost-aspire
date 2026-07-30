using System.Security.Cryptography;

namespace AtProto.Pds;

/// <summary>
/// A pushed authorization request (RFC 9126). The client POSTs the full set of authorization
/// parameters to <c>/oauth/par</c> and receives a single-use <c>request_uri</c> that stands in for
/// them at the authorization endpoint. The DPoP key thumbprint is bound here at PAR time.
/// </summary>
public sealed record ParRequest(
    string RequestUri,
    string ClientId,
    string ResponseType,
    string RedirectUri,
    string Scope,
    string CodeChallenge,
    string CodeChallengeMethod,
    string? State,
    string DpopJkt,
    string? LoginHint,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// A single-use authorization code minted once the user approves the request. It carries everything
/// the token endpoint needs to verify the exchange: the account DID (<c>sub</c>), the granted scope,
/// the PKCE challenge, and the DPoP thumbprint the eventual tokens are bound to. Reuse of a consumed
/// code must revoke the session it produced, so a consumed code remembers its <see cref="SessionId"/>.
/// </summary>
public sealed record AuthorizationCode(
    string Code,
    string ClientId,
    string Did,
    string RedirectUri,
    string Scope,
    string CodeChallenge,
    string CodeChallengeMethod,
    string DpopJkt,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt)
{
    /// <summary>True once the code has been exchanged for tokens. A second exchange is a reuse attack.</summary>
    public bool Consumed { get; init; }

    /// <summary>The session minted from this code, set when it is consumed (for reuse revocation).</summary>
    public string? SessionId { get; init; }
}

/// <summary>
/// A granted OAuth session: the durable link between a client, an account DID, and the DPoP key the
/// tokens are sender-constrained to. Refresh tokens rotate within a session; the session bounds the
/// overall lifetime (at most two weeks for a public client).
/// </summary>
public sealed record OAuthSession(
    string SessionId,
    string ClientId,
    string Did,
    string Scope,
    string DpopJkt,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// A single-use refresh token bound to a session. Presenting it rotates it (mint a new one, consume
/// this one). Presenting an already-consumed token is a reuse attack and must revoke the session.
/// </summary>
public sealed record RefreshTokenEntry(
    string Token,
    string SessionId,
    DateTimeOffset ExpiresAt)
{
    /// <summary>True once this token has been rotated away. A later presentation is a reuse attack.</summary>
    public bool Consumed { get; init; }
}

/// <summary>
/// The storage seam for OAuth server state, mirroring <see cref="IPdsPersistence"/>: the default
/// (<see cref="InMemoryOAuthStore"/>) keeps everything in process (the dev profile), and a durable
/// implementation (<see cref="SqliteOAuthStore"/>) writes through to SQLite (the production profile).
/// It holds the short-lived and session-lived state of the authorization server: pushed requests,
/// authorization codes, sessions, refresh tokens, and the DPoP <c>jti</c> replay set.
/// </summary>
/// <remarks>
/// The <c>TryConsume*</c> and <c>TryRegisterJti</c> operations are atomic compare-and-set gates: they
/// return <c>true</c> only for the caller that actually made the transition, so a race cannot exchange
/// a code or rotate a refresh token twice. <c>Get*</c> methods return the stored entry without filtering
/// on expiry; callers check <c>ExpiresAt</c> against their own clock.
/// </remarks>
public interface IOAuthStore
{
    // Pushed authorization requests.
    void SaveParRequest(ParRequest request);
    ParRequest? GetParRequest(string requestUri);
    void DeleteParRequest(string requestUri);

    // Authorization codes.
    void SaveAuthorizationCode(AuthorizationCode code);
    AuthorizationCode? GetAuthorizationCode(string code);

    /// <summary>Atomically consume an unconsumed code, recording the session it mints. False if absent or already consumed.</summary>
    bool TryConsumeAuthorizationCode(string code, string sessionId);

    // Sessions.
    void SaveSession(OAuthSession session);
    OAuthSession? GetSession(string sessionId);

    /// <summary>Revoke a session and every refresh token bound to it.</summary>
    void RevokeSession(string sessionId);

    // Refresh tokens.
    void SaveRefreshToken(RefreshTokenEntry token);
    RefreshTokenEntry? GetRefreshToken(string token);

    /// <summary>Atomically consume an unconsumed refresh token. False if absent or already consumed.</summary>
    bool TryConsumeRefreshToken(string token);

    // DPoP replay protection.
    /// <summary>Register a DPoP <c>jti</c>. Returns false if it was already seen (a replay).</summary>
    bool TryRegisterJti(string jti, DateTimeOffset expiresAt);

    /// <summary>Delete every entry whose expiry is at or before <paramref name="now"/>.</summary>
    void PurgeExpired(DateTimeOffset now);
}

/// <summary>Generates the random, prefixed identifiers the authorization server hands out.</summary>
public static class OAuthIds
{
    private const string RequestUriPrefix = "urn:ietf:params:oauth:request_uri:";

    /// <summary>A single-use PAR <c>request_uri</c> (RFC 9126 URN form).</summary>
    public static string NewRequestUri() => RequestUriPrefix + "req-" + Token();

    /// <summary>A single-use authorization code.</summary>
    public static string NewCode() => "cod-" + Token();

    /// <summary>An opaque session identifier.</summary>
    public static string NewSessionId() => "ses-" + Token();

    /// <summary>An opaque refresh token.</summary>
    public static string NewRefreshToken() => "ref-" + Token();

    private static string Token() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The default OAuth store: all state lives in process and is lost on restart (dev profile).</summary>
public sealed class InMemoryOAuthStore : IOAuthStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ParRequest> _parRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuthorizationCode> _codes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OAuthSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RefreshTokenEntry> _refreshTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _jtis = new(StringComparer.Ordinal);

    public void SaveParRequest(ParRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate) _parRequests[request.RequestUri] = request;
    }

    public ParRequest? GetParRequest(string requestUri)
    {
        lock (_gate) return _parRequests.GetValueOrDefault(requestUri);
    }

    public void DeleteParRequest(string requestUri)
    {
        lock (_gate) _parRequests.Remove(requestUri);
    }

    public void SaveAuthorizationCode(AuthorizationCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        lock (_gate) _codes[code.Code] = code;
    }

    public AuthorizationCode? GetAuthorizationCode(string code)
    {
        lock (_gate) return _codes.GetValueOrDefault(code);
    }

    public bool TryConsumeAuthorizationCode(string code, string sessionId)
    {
        lock (_gate)
        {
            if (!_codes.TryGetValue(code, out AuthorizationCode? existing) || existing.Consumed)
                return false;
            _codes[code] = existing with { Consumed = true, SessionId = sessionId };
            return true;
        }
    }

    public void SaveSession(OAuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate) _sessions[session.SessionId] = session;
    }

    public OAuthSession? GetSession(string sessionId)
    {
        lock (_gate) return _sessions.GetValueOrDefault(sessionId);
    }

    public void RevokeSession(string sessionId)
    {
        lock (_gate)
        {
            _sessions.Remove(sessionId);
            foreach (string token in _refreshTokens
                .Where(kv => kv.Value.SessionId == sessionId)
                .Select(kv => kv.Key)
                .ToArray())
            {
                _refreshTokens.Remove(token);
            }
        }
    }

    public void SaveRefreshToken(RefreshTokenEntry token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate) _refreshTokens[token.Token] = token;
    }

    public RefreshTokenEntry? GetRefreshToken(string token)
    {
        lock (_gate) return _refreshTokens.GetValueOrDefault(token);
    }

    public bool TryConsumeRefreshToken(string token)
    {
        lock (_gate)
        {
            if (!_refreshTokens.TryGetValue(token, out RefreshTokenEntry? existing) || existing.Consumed)
                return false;
            _refreshTokens[token] = existing with { Consumed = true };
            return true;
        }
    }

    public bool TryRegisterJti(string jti, DateTimeOffset expiresAt)
    {
        lock (_gate)
        {
            if (_jtis.ContainsKey(jti))
                return false;
            _jtis[jti] = expiresAt;
            return true;
        }
    }

    public void PurgeExpired(DateTimeOffset now)
    {
        lock (_gate)
        {
            Purge(_parRequests, r => r.ExpiresAt, now);
            Purge(_codes, c => c.ExpiresAt, now);
            Purge(_sessions, s => s.ExpiresAt, now);
            Purge(_refreshTokens, t => t.ExpiresAt, now);
            foreach (string jti in _jtis.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToArray())
                _jtis.Remove(jti);
        }
    }

    private static void Purge<T>(Dictionary<string, T> map, Func<T, DateTimeOffset> expiry, DateTimeOffset now)
    {
        foreach (string key in map.Where(kv => expiry(kv.Value) <= now).Select(kv => kv.Key).ToArray())
            map.Remove(key);
    }
}
