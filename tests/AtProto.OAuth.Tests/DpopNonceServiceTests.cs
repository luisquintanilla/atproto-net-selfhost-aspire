using System.Security.Cryptography;
using AtProto.OAuth;

namespace AtProto.OAuth.Tests;

public class DpopNonceServiceTests
{
    private static byte[] Secret() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void CurrentNonce_IsAccepted()
    {
        var clock = new FixedClock(DateTimeOffset.FromUnixTimeSeconds(1_000_000));
        var svc = new DpopNonceService(Secret(), stepSeconds: 300, clock);
        Assert.True(svc.IsAccepted(svc.Current()));
    }

    [Fact]
    public void Nonce_AcceptedOneStepLater_RejectedTwoStepsLater()
    {
        var clock = new FixedClock(DateTimeOffset.FromUnixTimeSeconds(1_000_000));
        var svc = new DpopNonceService(Secret(), stepSeconds: 300, clock);
        string nonce = svc.Current();

        clock.Now = DateTimeOffset.FromUnixTimeSeconds(1_000_300); // +1 window
        Assert.True(svc.IsAccepted(nonce));

        clock.Now = DateTimeOffset.FromUnixTimeSeconds(1_000_600); // +2 windows
        Assert.False(svc.IsAccepted(nonce));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-nonce")]
    public void GarbageNonce_IsRejected(string? nonce)
    {
        var svc = new DpopNonceService(Secret(), stepSeconds: 300);
        Assert.False(svc.IsAccepted(nonce));
    }

    [Fact]
    public void ShortSecret_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new DpopNonceService(new byte[8]));
    }
}
