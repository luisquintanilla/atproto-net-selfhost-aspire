using System.Security.Cryptography;
using AtProto.OAuth;

namespace AtProto.OAuth.Tests;

public class AccessTokenTests
{
    private const string Issuer = "https://pds.example";

    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static AccessTokenClaims Claims(long expiresAt) => new()
    {
        Subject = "did:web:alice.pds.example",
        Scope = "atproto transition:generic",
        Issuer = Issuer,
        ConfirmationJkt = "0ZcOCORZNYy-DWpqq30jZyJGHTN0d2HglBV3uiguA4I",
        IssuedAt = At.ToUnixTimeSeconds(),
        ExpiresAt = expiresAt,
        TokenId = "tok-123",
    };

    [Fact]
    public void IssueThenValidate_RoundTrips()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        var clock = new FixedClock(At);
        string token = AccessToken.Issue(Claims(At.ToUnixTimeSeconds() + 900), secret);

        Assert.True(AccessToken.TryValidate(token, secret, Issuer, out AccessTokenClaims claims, clock));
        Assert.Equal("did:web:alice.pds.example", claims.Subject);
        Assert.Equal("atproto transition:generic", claims.Scope);
        Assert.Equal("0ZcOCORZNYy-DWpqq30jZyJGHTN0d2HglBV3uiguA4I", claims.ConfirmationJkt);
        Assert.Equal("tok-123", claims.TokenId);
    }

    [Fact]
    public void ExpiredToken_FailsValidation()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        var clock = new FixedClock(At);
        string token = AccessToken.Issue(Claims(At.ToUnixTimeSeconds() - 1), secret);

        Assert.False(AccessToken.TryValidate(token, secret, Issuer, out _, clock));
    }

    [Fact]
    public void WrongIssuer_FailsValidation()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        var clock = new FixedClock(At);
        string token = AccessToken.Issue(Claims(At.ToUnixTimeSeconds() + 900), secret);

        Assert.False(AccessToken.TryValidate(token, secret, "https://evil.example", out _, clock));
    }

    [Fact]
    public void WrongSecret_FailsValidation()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        byte[] other = RandomNumberGenerator.GetBytes(32);
        var clock = new FixedClock(At);
        string token = AccessToken.Issue(Claims(At.ToUnixTimeSeconds() + 900), secret);

        Assert.False(AccessToken.TryValidate(token, other, Issuer, out _, clock));
    }

    [Fact]
    public void TamperedPayload_FailsValidation()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        var clock = new FixedClock(At);
        string token = AccessToken.Issue(Claims(At.ToUnixTimeSeconds() + 900), secret);

        string[] parts = token.Split('.');
        // Re-encode a payload with an escalated scope but keep the original signature.
        string forgedPayload = System.Buffers.Text.Base64Url.EncodeToString(
            System.Text.Encoding.UTF8.GetBytes(
                "{\"sub\":\"did:web:alice.pds.example\",\"scope\":\"atproto admin\",\"iss\":\"" + Issuer +
                "\",\"iat\":1,\"exp\":9999999999,\"jti\":\"x\",\"cnf\":{\"jkt\":\"z\"}}"));
        string forged = $"{parts[0]}.{forgedPayload}.{parts[2]}";

        Assert.False(AccessToken.TryValidate(forged, secret, Issuer, out _, clock));
    }
}
