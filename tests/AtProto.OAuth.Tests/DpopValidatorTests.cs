using System.Security.Cryptography;
using AtProto.OAuth;

namespace AtProto.OAuth.Tests;

public class DpopValidatorTests
{
    private const string Htu = "https://pds.example/oauth/token";
    private const string Htm = "POST";

    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static (DpopNonceService nonce, FixedClock clock) NewNonce()
    {
        var clock = new FixedClock(At);
        var nonce = new DpopNonceService(RandomNumberGenerator.GetBytes(32), stepSeconds: 300, clock);
        return (nonce, clock);
    }

    private static DpopValidationOptions Options(string? accessToken = null) => new()
    {
        ExpectedHtm = Htm,
        ExpectedHtu = Htu,
        AccessToken = accessToken,
    };

    [Fact]
    public void ValidProof_Passes()
    {
        var (nonce, clock) = NewNonce();
        using ECDsa key = TestDpop.NewKey();
        string proof = TestDpop.Proof(key, Htm, Htu, At.ToUnixTimeSeconds(), nonce.Current());

        DpopValidationResult result = DpopValidator.Validate(proof, Options(), nonce, clock);

        Assert.True(result.IsValid);
        Assert.Equal(TestDpop.Jkt(key), result.Proof!.Jkt);
    }

    [Fact]
    public void MissingNonce_RequiresNonce()
    {
        var (nonce, clock) = NewNonce();
        using ECDsa key = TestDpop.NewKey();
        string proof = TestDpop.Proof(key, Htm, Htu, At.ToUnixTimeSeconds(), nonce: null);

        DpopValidationResult result = DpopValidator.Validate(proof, Options(), nonce, clock);

        Assert.Equal(DpopValidationStatus.NonceRequired, result.Status);
    }

    [Fact]
    public void StaleNonce_RequiresNonce()
    {
        var (nonce, clock) = NewNonce();
        using ECDsa key = TestDpop.NewKey();
        string proof = TestDpop.Proof(key, Htm, Htu, At.ToUnixTimeSeconds(), "stale-or-forged-nonce");

        DpopValidationResult result = DpopValidator.Validate(proof, Options(), nonce, clock);

        Assert.Equal(DpopValidationStatus.NonceRequired, result.Status);
    }

    [Fact]
    public void WrongMethod_IsInvalid()
    {
        var (nonce, clock) = NewNonce();
        using ECDsa key = TestDpop.NewKey();
        string proof = TestDpop.Proof(key, "GET", Htu, At.ToUnixTimeSeconds(), nonce.Current());

        DpopValidationResult result = DpopValidator.Validate(proof, Options(), nonce, clock);

        Assert.Equal(DpopValidationStatus.Invalid, result.Status);
    }

    [Fact]
    public void WrongUri_IsInvalid()
    {
        var (nonce, clock) = NewNonce();
        using ECDsa key = TestDpop.NewKey();
        string proof = TestDpop.Proof(key, Htm, "https://evil.example/oauth/token", At.ToUnixTimeSeconds(), nonce.Current());

        DpopValidationResult result = DpopValidator.Validate(proof, Options(), nonce, clock);

        Assert.Equal(DpopValidationStatus.Invalid, result.Status);
    }

    [Fact]
    public void TamperedSignature_IsInvalid()
    {
        var (nonce, clock) = NewNonce();
        using ECDsa key = TestDpop.NewKey();
        string proof = TestDpop.Proof(key, Htm, Htu, At.ToUnixTimeSeconds(), nonce.Current());
        string tampered = proof[..^2] + (proof[^1] == 'A' ? "BB" : "AA");

        DpopValidationResult result = DpopValidator.Validate(tampered, Options(), nonce, clock);

        Assert.Equal(DpopValidationStatus.Invalid, result.Status);
    }

    [Fact]
    public void FutureIat_IsInvalid()
    {
        var (nonce, clock) = NewNonce();
        using ECDsa key = TestDpop.NewKey();
        string proof = TestDpop.Proof(key, Htm, Htu, At.ToUnixTimeSeconds() + 600, nonce.Current());

        DpopValidationResult result = DpopValidator.Validate(proof, Options(), nonce, clock);

        Assert.Equal(DpopValidationStatus.Invalid, result.Status);
    }

    [Fact]
    public void OldIat_IsInvalid()
    {
        var (nonce, clock) = NewNonce();
        using ECDsa key = TestDpop.NewKey();
        string proof = TestDpop.Proof(key, Htm, Htu, At.ToUnixTimeSeconds() - 3600, nonce.Current());

        DpopValidationResult result = DpopValidator.Validate(proof, Options(), nonce, clock);

        Assert.Equal(DpopValidationStatus.Invalid, result.Status);
    }

    [Fact]
    public void ResourceRequest_WithMatchingAth_Passes()
    {
        var (nonce, clock) = NewNonce();
        using ECDsa key = TestDpop.NewKey();
        const string accessToken = "an.access.token";
        string ath = DpopValidator.AccessTokenHash(accessToken);
        string proof = TestDpop.Proof(key, Htm, Htu, At.ToUnixTimeSeconds(), nonce.Current(), ath: ath);

        DpopValidationResult result = DpopValidator.Validate(proof, Options(accessToken), nonce, clock);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ResourceRequest_WithWrongAth_IsInvalid()
    {
        var (nonce, clock) = NewNonce();
        using ECDsa key = TestDpop.NewKey();
        string proof = TestDpop.Proof(key, Htm, Htu, At.ToUnixTimeSeconds(), nonce.Current(), ath: "wrong-hash");

        DpopValidationResult result = DpopValidator.Validate(proof, Options("an.access.token"), nonce, clock);

        Assert.Equal(DpopValidationStatus.Invalid, result.Status);
    }

    [Fact]
    public void ResourceRequest_WithMissingAth_IsInvalid()
    {
        var (nonce, clock) = NewNonce();
        using ECDsa key = TestDpop.NewKey();
        string proof = TestDpop.Proof(key, Htm, Htu, At.ToUnixTimeSeconds(), nonce.Current());

        DpopValidationResult result = DpopValidator.Validate(proof, Options("an.access.token"), nonce, clock);

        Assert.Equal(DpopValidationStatus.Invalid, result.Status);
    }

    [Fact]
    public void HtuComparison_IgnoresQueryAndPort()
    {
        var (nonce, clock) = NewNonce();
        using ECDsa key = TestDpop.NewKey();
        // Proof carries a query string and default port; both should normalize away.
        string proof = TestDpop.Proof(key, Htm, "https://pds.example:443/oauth/token?x=1", At.ToUnixTimeSeconds(), nonce.Current());

        DpopValidationResult result = DpopValidator.Validate(proof, Options(), nonce, clock);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Malformed_IsInvalid()
    {
        var (nonce, clock) = NewNonce();
        DpopValidationResult result = DpopValidator.Validate("not.a.jwt", Options(), nonce, clock);
        Assert.Equal(DpopValidationStatus.Invalid, result.Status);
    }
}
