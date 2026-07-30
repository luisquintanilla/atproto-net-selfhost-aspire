using System.Net;
using System.Net.Sockets;

namespace AtProto.OAuth;

/// <summary>
/// Address-level SSRF hardening for the client-metadata / JWKS fetcher. The atproto OAuth spec
/// requires Authorization Servers to fetch <c>client_id</c> documents from the public web with a
/// "hardened HTTP client" that cannot be pointed at internal infrastructure. This classifies an
/// <see cref="IPAddress"/> as publicly routable or not so the fetcher only connects to global unicast
/// addresses (blocking loopback, private, link-local, unique-local, carrier-grade NAT, documentation,
/// multicast and other reserved ranges, for both IPv4 and IPv6).
/// </summary>
public static class SsrfGuard
{
    /// <summary>
    /// Returns true only when <paramref name="address"/> is a normal, publicly routable global-unicast
    /// address. Any private, loopback, link-local, reserved, multicast or unspecified address returns
    /// false. IPv4-mapped and NAT64-embedded IPv6 addresses are unwrapped and checked as IPv4.
    /// </summary>
    public static bool IsPubliclyRoutable(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicV6(address),
            _ => false,
        };
    }

    private static bool IsPublicV4(byte[] b)
    {
        int a0 = b[0], a1 = b[1], a2 = b[2];

        if (a0 == 0) return false;                              // 0.0.0.0/8 "this network"
        if (a0 == 10) return false;                             // 10.0.0.0/8 private
        if (a0 == 127) return false;                            // 127.0.0.0/8 loopback
        if (a0 == 169 && a1 == 254) return false;               // 169.254.0.0/16 link-local
        if (a0 == 172 && a1 >= 16 && a1 <= 31) return false;    // 172.16.0.0/12 private
        if (a0 == 192 && a1 == 168) return false;               // 192.168.0.0/16 private
        if (a0 == 192 && a1 == 0 && a2 == 0) return false;      // 192.0.0.0/24 IETF protocol
        if (a0 == 192 && a1 == 0 && a2 == 2) return false;      // 192.0.2.0/24 TEST-NET-1
        if (a0 == 198 && (a1 == 18 || a1 == 19)) return false;  // 198.18.0.0/15 benchmarking
        if (a0 == 198 && a1 == 51 && a2 == 100) return false;   // 198.51.100.0/24 TEST-NET-2
        if (a0 == 203 && a1 == 0 && a2 == 113) return false;    // 203.0.113.0/24 TEST-NET-3
        if (a0 == 100 && a1 >= 64 && a1 <= 127) return false;   // 100.64.0.0/10 carrier-grade NAT
        if (a0 >= 224) return false;                            // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved
        return true;
    }

    private static bool IsPublicV6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any))
            return false;

        byte[] b = address.GetAddressBytes();

        if ((b[0] & 0xFE) == 0xFC) return false;                                   // fc00::/7 unique-local
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // 2001:db8::/32 docs
        if (b[0] == 0x01 && b[1] == 0x00 && AllZero(b, 2, 6)) return false;        // 100::/64 discard-only

        // NAT64 well-known prefix 64:ff9b::/96 embeds an IPv4 address in the last four bytes.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && AllZero(b, 4, 8))
            return IsPublicV4(new[] { b[12], b[13], b[14], b[15] });

        return true;
    }

    private static bool AllZero(byte[] b, int start, int count)
    {
        for (int i = start; i < start + count; i++)
            if (b[i] != 0) return false;
        return true;
    }
}
