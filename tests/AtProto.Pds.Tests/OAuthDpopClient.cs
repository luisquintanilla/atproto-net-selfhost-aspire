using System.Security.Cryptography;
using System.Text.Json;
using AtProto.OAuth;

namespace AtProto.Pds.Tests;

// A minimal atproto OAuth client key for tests: it holds one P-256 key and mints DPoP proof JWTs the
// way a real client would (RFC 9449), so the integration tests can drive PAR, token, and resource
// requests without a browser or an external client. The public thumbprint is the jkt tokens bind to.
internal sealed class OAuthDpopClient : IDisposable
{
    private readonly ECDsa _key;

    public EcPublicJwk PublicJwk { get; }

    public string Jkt => PublicJwk.Thumbprint();

    public OAuthDpopClient() : this(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { }

    // Take ownership of an existing key, so a test can bind the DPoP proof and a client assertion to
    // the same key (to exercise the "keys must differ" rule).
    public OAuthDpopClient(ECDsa key)
    {
        _key = key;
        PublicJwk = EcPublicJwk.FromP256(key.ExportParameters(false));
    }

    // Build a DPoP proof for a request. A null nonce/ath omits that claim; iat defaults to now, and an
    // explicit iat lets a test forge a stale or future proof.
    public string Proof(string htm, string htu, string? nonce = null, string? ath = null, long? iat = null)
    {
        string header = JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["typ"] = "dpop+jwt",
            ["alg"] = "ES256",
            ["jwk"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["crv"] = PublicJwk.Crv,
                ["kty"] = "EC",
                ["x"] = PublicJwk.X,
                ["y"] = PublicJwk.Y,
            },
        });

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["htm"] = htm,
            ["htu"] = htu,
            ["iat"] = iat ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        if (nonce is not null) payload["nonce"] = nonce;
        if (ath is not null) payload["ath"] = ath;

        return JoseEs256.CreateJws(header, JsonSerializer.Serialize(payload), _key);
    }

    public void Dispose() => _key.Dispose();
}
