using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AtProto.OAuth;

namespace AtProto.OAuth.Tests;

public class JsonWebKeySetTests
{
    [Fact]
    public void Parse_KeepsUsableP256SigningKey()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet set = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));

        Assert.Equal(1, set.Count);
        Assert.Single(set.Candidates("k1"));
    }

    [Fact]
    public void Parse_SkipsNonP256AndEncryptionKeysInAMixedSet()
    {
        using ECDsa signing = ClientAssertionTestData.NewKey();
        using ECDsa encOnly = ClientAssertionTestData.NewKey();
        EcPublicJwk enc = EcPublicJwk.FromP256(encOnly.ExportParameters(includePrivateParameters: false));
        EcPublicJwk sig = EcPublicJwk.FromP256(signing.ExportParameters(includePrivateParameters: false));

        string json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["keys"] = new object[]
            {
                new Dictionary<string, object?> { ["kty"] = "RSA", ["n"] = "abc", ["e"] = "AQAB" },
                new Dictionary<string, object?> { ["kty"] = "EC", ["crv"] = "P-384", ["x"] = enc.X, ["y"] = enc.Y },
                new Dictionary<string, object?> { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = enc.X, ["y"] = enc.Y, ["use"] = "enc" },
                new Dictionary<string, object?> { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = sig.X, ["y"] = sig.Y, ["use"] = "sig", ["kid"] = "sig-1" },
            },
        }, ClientAssertionTestData.JsonOptions);

        JsonWebKeySet set = JsonWebKeySet.Parse(json);

        Assert.Equal(1, set.Count);
        Assert.Single(set.Candidates("sig-1"));
    }

    [Fact]
    public void Candidates_FailsClosedOnUnmatchedKid()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet set = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));

        Assert.Empty(set.Candidates("other"));
    }

    [Fact]
    public void Candidates_ReturnsEveryKeyWhenNoKidNamed()
    {
        using ECDsa a = ClientAssertionTestData.NewKey();
        using ECDsa b = ClientAssertionTestData.NewKey();
        JsonWebKeySet set = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((a, "a"), (b, "b")));

        Assert.Equal(2, set.Candidates(null).Count());
    }

    [Fact]
    public void Parse_ThrowsWhenNoUsableKey()
    {
        string json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["keys"] = new object[] { new Dictionary<string, object?> { ["kty"] = "RSA", ["n"] = "abc", ["e"] = "AQAB" } },
        }, ClientAssertionTestData.JsonOptions);

        Assert.Throws<FormatException>(() => JsonWebKeySet.Parse(json));
    }

    [Fact]
    public void Parse_ThrowsWhenNotAKeySet()
    {
        Assert.Throws<FormatException>(() => JsonWebKeySet.Parse("{\"foo\":1}"));
    }
}

public class ClientAssertionValidatorTests
{
    [Fact]
    public void Validate_AcceptsAWellFormedAssertion()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        string assertion = ClientAssertionTestData.Assertion(key, kid: "k1");

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.True(result.IsValid);
        Assert.Equal("jti-1", result.Jti);
        Assert.NotNull(result.ExpiresAt);
    }

    [Fact]
    public void Validate_SelectsTheKeyByKidInAMultiKeySet()
    {
        using ECDsa a = ClientAssertionTestData.NewKey();
        using ECDsa b = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((a, "a"), (b, "b")));
        string assertion = ClientAssertionTestData.Assertion(b, kid: "b");

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_AcceptsIssuerAsAudience()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        string assertion = ClientAssertionTestData.Assertion(key, kid: "k1", aud: ClientAssertionTestData.Issuer);

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_AcceptsAudienceArrayContainingAnAcceptedValue()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        string assertion = ClientAssertionTestData.Assertion(key, kid: "k1",
            audArray: new[] { "https://someone.else", ClientAssertionTestData.TokenEndpoint });

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsNonEs256Alg()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        string assertion = ClientAssertionTestData.Assertion(key, kid: "k1", alg: "HS256");

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.False(result.IsValid);
        Assert.Contains("ES256", result.Error);
    }

    [Fact]
    public void Validate_RejectsABadSignature()
    {
        using ECDsa published = ClientAssertionTestData.NewKey();
        using ECDsa attacker = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((published, "k1")));
        string assertion = ClientAssertionTestData.Assertion(attacker, kid: "k1");

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.False(result.IsValid);
        Assert.Contains("signature", result.Error);
    }

    [Fact]
    public void Validate_RejectsAKidThatNamesNoKey()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        string assertion = ClientAssertionTestData.Assertion(key, kid: "missing");

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsIssuerThatIsNotTheClientId()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        string assertion = ClientAssertionTestData.Assertion(key, kid: "k1", iss: "https://evil.example/client");

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.False(result.IsValid);
        Assert.Contains("client_id", result.Error);
    }

    [Fact]
    public void Validate_RejectsSubjectThatIsNotTheClientId()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        string assertion = ClientAssertionTestData.Assertion(key, kid: "k1", sub: "https://evil.example/client");

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsAnUnacceptedAudience()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        string assertion = ClientAssertionTestData.Assertion(key, kid: "k1", aud: "https://another.pds.example/oauth/token");

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.False(result.IsValid);
        Assert.Contains("aud", result.Error);
    }

    [Fact]
    public void Validate_RejectsAnExpiredAssertion()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        long now = ClientAssertionTestData.NowSeconds();
        string assertion = ClientAssertionTestData.Assertion(key, kid: "k1", iat: now - 130, exp: now - 10);

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.False(result.IsValid);
        Assert.Contains("expired", result.Error);
    }

    [Fact]
    public void Validate_RejectsAFutureIssuedAt()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        long now = ClientAssertionTestData.NowSeconds();
        string assertion = ClientAssertionTestData.Assertion(key, kid: "k1", iat: now + 3600, exp: now + 3720);

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.False(result.IsValid);
        Assert.Contains("future", result.Error);
    }

    [Fact]
    public void Validate_RejectsAnOverLongLifetime()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        long now = ClientAssertionTestData.NowSeconds();
        string assertion = ClientAssertionTestData.Assertion(key, kid: "k1", iat: now, exp: now + 3600);

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.False(result.IsValid);
        Assert.Contains("lifetime", result.Error);
    }

    [Fact]
    public void Validate_RejectsAnEmptyJti()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        string assertion = ClientAssertionTestData.Assertion(key, kid: "k1", jti: "");

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.False(result.IsValid);
        Assert.Contains("jti", result.Error);
    }

    [Fact]
    public void Validate_RejectsAnAssertionWithoutAKid()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        string assertion = ClientAssertionTestData.Assertion(key, kid: null);

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.False(result.IsValid);
        Assert.Contains("kid", result.Error);
    }

    [Fact]
    public void Validate_ReturnsTheVerifyingKeyThumbprint()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));
        string expected = EcPublicJwk.FromP256(key.ExportParameters(includePrivateParameters: false)).Thumbprint();
        string assertion = ClientAssertionTestData.Assertion(key, kid: "k1");

        ClientAssertionResult result = ClientAssertionValidator.Validate(assertion, keys, ClientAssertionTestData.Options());

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.KeyThumbprint);
    }

    [Fact]
    public void Validate_RejectsGarbageThatIsNotACompactJws()
    {
        using ECDsa key = ClientAssertionTestData.NewKey();
        JsonWebKeySet keys = JsonWebKeySet.Parse(ClientAssertionTestData.Jwks((key, "k1")));

        ClientAssertionResult result = ClientAssertionValidator.Validate("not-a-jws", keys, ClientAssertionTestData.Options());

        Assert.False(result.IsValid);
    }
}

internal static class ClientAssertionTestData
{
    public const string ClientId = "https://app.example.com/client-metadata.json";
    public const string Issuer = "https://pds.example.com";
    public const string TokenEndpoint = "https://pds.example.com/oauth/token";

    /// <summary>The current time in Unix seconds, so lifetime-bounded assertions stay realistic.</summary>
    public static long NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static readonly JsonSerializerOptions JsonOptions =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static ClientAssertionOptions Options() => new()
    {
        ClientId = ClientId,
        AcceptedAudiences = new[] { Issuer, TokenEndpoint },
    };

    public static string Jwks(params (ECDsa key, string? kid)[] keys)
    {
        var entries = new List<Dictionary<string, object?>>();
        foreach ((ECDsa key, string? kid) in keys)
        {
            EcPublicJwk jwk = EcPublicJwk.FromP256(key.ExportParameters(includePrivateParameters: false));
            entries.Add(new Dictionary<string, object?>
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["x"] = jwk.X,
                ["y"] = jwk.Y,
                ["use"] = "sig",
                ["kid"] = kid,
            });
        }
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["keys"] = entries }, JsonOptions);
    }

    public static string Assertion(
        ECDsa key,
        string? kid = null,
        string iss = ClientId,
        string sub = ClientId,
        string? aud = null,
        string[]? audArray = null,
        string? jti = "jti-1",
        long? iat = null,
        long? exp = null,
        long? nbf = null,
        string alg = "ES256")
    {
        long baseNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = new Dictionary<string, object?>
        {
            ["typ"] = "jwt",
            ["alg"] = alg,
            ["kid"] = kid,
        };
        object audValue = audArray is not null ? audArray : aud ?? TokenEndpoint;
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = iss,
            ["sub"] = sub,
            ["aud"] = audValue,
            ["jti"] = jti,
            ["iat"] = iat ?? baseNow,
            ["exp"] = exp ?? baseNow + 120,
            ["nbf"] = nbf,
        };
        return JoseEs256.CreateJws(
            JsonSerializer.Serialize(header, JsonOptions),
            JsonSerializer.Serialize(payload, JsonOptions),
            key);
    }
}
