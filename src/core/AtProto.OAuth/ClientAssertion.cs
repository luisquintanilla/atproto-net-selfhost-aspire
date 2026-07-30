using System.Text.Json;

namespace AtProto.OAuth;

/// <summary>
/// A JSON Web Key Set (RFC 7517) restricted to the atproto profile's signing keys: EC P-256
/// (<c>ES256</c>). Non-EC, non-P-256, and encryption-only keys are skipped so a client may publish a
/// mixed set; the usable signing keys are kept with their optional <c>kid</c> for selection.
/// </summary>
public sealed class JsonWebKeySet
{
    /// <summary>One usable signing key and its key id, if the client labeled it.</summary>
    public sealed record Entry(EcPublicJwk Key, string? Kid);

    private readonly IReadOnlyList<Entry> _keys;

    private JsonWebKeySet(IReadOnlyList<Entry> keys) => _keys = keys;

    /// <summary>The number of usable P-256 signing keys.</summary>
    public int Count => _keys.Count;

    /// <summary>Parse a JWKS from its JSON text.</summary>
    public static JsonWebKeySet Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    /// <summary>Parse a JWKS from a JSON object with a <c>keys</c> array.</summary>
    public static JsonWebKeySet Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("keys", out JsonElement keys)
            || keys.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("a JWKS must be a JSON object with a 'keys' array");
        }

        var entries = new List<Entry>();
        foreach (JsonElement element in keys.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;
            // The profile signs with EC P-256 only; ignore other key types in a mixed set.
            if (OptionalString(element, "kty") != "EC" || OptionalString(element, "crv") != "P-256")
                continue;
            // Skip keys explicitly published for something other than signing.
            if (OptionalString(element, "use") is string use && use != "sig")
                continue;

            EcPublicJwk key;
            try
            {
                key = EcPublicJwk.Parse(element);
            }
            catch (FormatException)
            {
                continue;
            }
            entries.Add(new Entry(key, OptionalString(element, "kid")));
        }

        if (entries.Count == 0)
            throw new FormatException("the JWKS contains no usable P-256 signing keys");
        return new JsonWebKeySet(entries);
    }

    /// <summary>
    /// The candidate verification keys for an assertion header's <c>kid</c>. When a <c>kid</c> is
    /// named, only exactly-matching keys are returned (fail closed if none match); when it is absent,
    /// every key is a candidate.
    /// </summary>
    public IEnumerable<EcPublicJwk> Candidates(string? kid)
    {
        if (kid is null)
        {
            foreach (Entry entry in _keys)
                yield return entry.Key;
            yield break;
        }
        foreach (Entry entry in _keys)
            if (string.Equals(entry.Kid, kid, StringComparison.Ordinal))
                yield return entry.Key;
    }

    private static string? OptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Inputs for validating a confidential client's <c>private_key_jwt</c> assertion.</summary>
public sealed class ClientAssertionOptions
{
    /// <summary>The <c>client_id</c> the assertion must be issued by and about (<c>iss</c>=<c>sub</c>).</summary>
    public required string ClientId { get; init; }

    /// <summary>The audience values the assertion's <c>aud</c> may match (the issuer and token endpoint URLs).</summary>
    public required IReadOnlyCollection<string> AcceptedAudiences { get; init; }

    /// <summary>The maximum allowed assertion lifetime (<c>exp - iat</c>), in seconds.</summary>
    public int MaxLifetimeSeconds { get; init; } = 300;

    /// <summary>Allowed clock skew for <c>iat</c>/<c>nbf</c>, in seconds.</summary>
    public int MaxIatSkewSeconds { get; init; } = 30;
}

/// <summary>The outcome of validating a client assertion.</summary>
public sealed record ClientAssertionResult(bool IsValid, string? Jti, long? ExpiresAt, string? Error, string? KeyThumbprint)
{
    internal static ClientAssertionResult Ok(string jti, long expiresAt, string keyThumbprint) =>
        new(true, jti, expiresAt, null, keyThumbprint);
    internal static ClientAssertionResult Fail(string error) => new(false, null, null, error, null);
}

/// <summary>
/// Validates a confidential client's <c>private_key_jwt</c> assertion (RFC 7523, the OAuth profile's
/// asymmetric client authentication), adapted to atproto (ES256 only). The signature is checked
/// against the client's JWKS; the claims are checked for <c>iss</c>=<c>sub</c>=<c>client_id</c>, an
/// audience naming this server, freshness, and a bounded lifetime. Replay detection of the <c>jti</c>
/// is left to the caller (the store), exactly like DPoP.
/// </summary>
public static class ClientAssertionValidator
{
    /// <summary>The <c>client_assertion_type</c> for a <c>private_key_jwt</c> assertion.</summary>
    public const string JwtBearerAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>Validate a client assertion against the client's keys and this server's identity.</summary>
    public static ClientAssertionResult Validate(
        string? assertion,
        JsonWebKeySet keys,
        ClientAssertionOptions options,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(options);
        clock ??= TimeProvider.System;

        if (!JwsParts.TryParse(assertion, out JwsParts jws))
            return ClientAssertionResult.Fail("client assertion is not a compact JWS");

        string? kid;
        try
        {
            using JsonDocument header = JsonDocument.Parse(jws.HeaderJson());
            JsonElement root = header.RootElement;
            if (GetString(root, "alg") != "ES256")
                return ClientAssertionResult.Fail("client assertion alg must be ES256");
            kid = OptionalString(root, "kid");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return ClientAssertionResult.Fail("invalid client assertion header");
        }

        // The atproto profile requires a kid so the verification key is selected unambiguously from
        // the client's JWKS (the reference oauth-provider rejects an assertion without one).
        if (kid is null)
            return ClientAssertionResult.Fail("client assertion header must include a kid");

        EcPublicJwk? verifiedKey = null;
        foreach (EcPublicJwk key in keys.Candidates(kid))
        {
            if (JoseEs256.Verify(jws, key))
            {
                verifiedKey = key;
                break;
            }
        }
        if (verifiedKey is null)
            return ClientAssertionResult.Fail("client assertion signature verification failed");

        try
        {
            using JsonDocument payload = JsonDocument.Parse(jws.PayloadJson());
            JsonElement root = payload.RootElement;

            if (!string.Equals(GetString(root, "iss"), options.ClientId, StringComparison.Ordinal)
                || !string.Equals(GetString(root, "sub"), options.ClientId, StringComparison.Ordinal))
            {
                return ClientAssertionResult.Fail("client assertion iss and sub must equal the client_id");
            }
            if (!AudienceMatches(root, options.AcceptedAudiences))
                return ClientAssertionResult.Fail("client assertion aud does not name this authorization server");

            string jti = GetString(root, "jti");
            if (jti.Length == 0)
                return ClientAssertionResult.Fail("client assertion jti is empty");

            long now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (!TryGetInt64(root, "exp", out long exp))
                return ClientAssertionResult.Fail("client assertion exp missing or invalid");
            if (exp <= now)
                return ClientAssertionResult.Fail("client assertion has expired");

            if (TryGetInt64(root, "iat", out long iat))
            {
                if (iat > now + options.MaxIatSkewSeconds)
                    return ClientAssertionResult.Fail("client assertion iat is in the future");
                if (exp - iat > options.MaxLifetimeSeconds)
                    return ClientAssertionResult.Fail("client assertion lifetime exceeds the allowed maximum");
            }
            if (TryGetInt64(root, "nbf", out long nbf) && nbf > now + options.MaxIatSkewSeconds)
                return ClientAssertionResult.Fail("client assertion is not yet valid");

            return ClientAssertionResult.Ok(jti, exp, verifiedKey.Thumbprint());
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return ClientAssertionResult.Fail("invalid client assertion payload");
        }
    }

    private static bool AudienceMatches(JsonElement payload, IReadOnlyCollection<string> accepted)
    {
        if (!payload.TryGetProperty("aud", out JsonElement aud))
            return false;
        if (aud.ValueKind == JsonValueKind.String)
            return aud.GetString() is string value && accepted.Contains(value);
        if (aud.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in aud.EnumerateArray())
                if (element.ValueKind == JsonValueKind.String && element.GetString() is string value && accepted.Contains(value))
                    return true;
        }
        return false;
    }

    private static string GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new FormatException($"missing string claim '{name}'");
        return value.GetString() ?? throw new FormatException($"null claim '{name}'");
    }

    private static string? OptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetInt64(JsonElement obj, string name, out long result)
    {
        result = 0;
        return obj.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out result);
    }
}
