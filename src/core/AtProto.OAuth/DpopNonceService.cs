using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace AtProto.OAuth;

/// <summary>
/// Issues and validates DPoP server nonces (RFC 9449 section 8 / 9). Rather than storing a nonce per
/// client, the nonce is a keyed HMAC over the current time window, so any server instance sharing the
/// secret can both issue and validate without shared state. Validation accepts a small set of
/// windows (previous, current, next) so a nonce issued just before a rotation, or under mild clock
/// skew, is still accepted. This mirrors the rolling-window design of the reference TypeScript
/// <c>@atproto/oauth-provider</c> implementation.
/// </summary>
public sealed class DpopNonceService
{
    // Domain-separation prefix so these HMACs can never collide with another use of the same secret.
    private static readonly byte[] Prefix = "atproto-dpop-nonce\u0000"u8.ToArray();

    private readonly byte[] _secret;
    private readonly int _stepSeconds;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Create a nonce service. <paramref name="stepSeconds"/> is the rotation interval; a nonce is
    /// accepted for at most one step on either side of the current window (so up to roughly two steps
    /// of tolerance overall). The atproto profile expects rotation of five minutes or less.
    /// </summary>
    public DpopNonceService(byte[] secret, int stepSeconds = 300, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length < 16)
            throw new ArgumentException("nonce secret must be at least 16 bytes", nameof(secret));
        if (stepSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(stepSeconds));
        _secret = (byte[])secret.Clone();
        _stepSeconds = stepSeconds;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The nonce a server should hand out right now (via the <c>DPoP-Nonce</c> header).</summary>
    public string Current() => ForWindow(CurrentWindow());

    /// <summary>
    /// Whether a nonce presented by a client is currently acceptable (previous, current, or next
    /// window). Comparison is constant-time.
    /// </summary>
    public bool IsAccepted(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce))
            return false;
        long window = CurrentWindow();
        return Matches(nonce, window) || Matches(nonce, window - 1) || Matches(nonce, window + 1);
    }

    private long CurrentWindow() => _clock.GetUtcNow().ToUnixTimeSeconds() / _stepSeconds;

    private bool Matches(string candidate, long window)
    {
        Span<byte> a = stackalloc byte[64];
        Span<byte> b = stackalloc byte[64];
        int an = System.Text.Encoding.ASCII.GetBytes(candidate, a);
        int bn = System.Text.Encoding.ASCII.GetBytes(ForWindow(window), b);
        return an == bn && CryptographicOperations.FixedTimeEquals(a[..an], b[..bn]);
    }

    private string ForWindow(long window)
    {
        Span<byte> message = stackalloc byte[Prefix.Length + 8];
        Prefix.CopyTo(message);
        BinaryPrimitives.WriteInt64BigEndian(message[Prefix.Length..], window);

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_secret, message, mac);
        return Base64Url.EncodeToString(mac);
    }
}
