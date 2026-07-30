using System.Net;
using AtProto.OAuth;

namespace AtProto.OAuth.Tests;

public class SsrfGuardTests
{
    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]   // example.com
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:1.1.1.1")]  // IPv4-mapped, public
    public void PublicAddresses_AreRoutable(string ip)
    {
        Assert.True(SsrfGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]     // link-local
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]      // carrier-grade NAT
    [InlineData("192.0.2.10")]      // TEST-NET-1
    [InlineData("198.18.0.1")]      // benchmarking
    [InlineData("198.51.100.7")]    // TEST-NET-2
    [InlineData("203.0.113.7")]     // TEST-NET-3
    [InlineData("224.0.0.1")]       // multicast
    [InlineData("255.255.255.255")]
    [InlineData("::1")]             // IPv6 loopback
    [InlineData("::")]              // unspecified
    [InlineData("fe80::1")]         // link-local
    [InlineData("fc00::1")]         // unique-local
    [InlineData("fd12:3456::1")]    // unique-local
    [InlineData("2001:db8::1")]     // documentation
    [InlineData("ff02::1")]         // multicast
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped loopback
    [InlineData("::ffff:10.0.0.1")]  // IPv4-mapped private
    [InlineData("64:ff9b::a00:1")]   // NAT64-embedded 10.0.0.1
    public void PrivateAndReservedAddresses_AreBlocked(string ip)
    {
        Assert.False(SsrfGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Nat64_EmbeddedPublicV4_IsRoutable()
    {
        // 64:ff9b::/96 with an embedded public IPv4 (8.8.8.8) is routable.
        Assert.True(SsrfGuard.IsPubliclyRoutable(IPAddress.Parse("64:ff9b::808:808")));
    }
}
