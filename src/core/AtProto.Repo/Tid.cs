using System.Security.Cryptography;

namespace AtProto.Repo;

/// <summary>
/// Timestamp Identifiers (TIDs) — the sortable 13-char <c>base32-sortable</c> keys atproto uses for
/// record keys and commit revisions. A TID is a 64-bit integer: top bit 0, next 53 bits are
/// microseconds since the UNIX epoch, low 10 bits are a random clock identifier.
/// See <see href="https://atproto.com/specs/tid"/>.
/// </summary>
public static class Tid
{
    /// <summary>The base32-sortable alphabet (ASCII-sorted so string order matches integer order).</summary>
    public const string Alphabet = "234567abcdefghijklmnopqrstuvwxyz";

    private const int Length = 13;
    private const ulong TimestampMask = (1UL << 53) - 1;
    private const ulong ClockIdMask = (1UL << 10) - 1;

    /// <summary>Encode a 64-bit TID value as its 13-character string form.</summary>
    public static string Encode(ulong value)
    {
        Span<char> chars = stackalloc char[Length];
        for (int i = Length - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 0x1f)];
            value >>= 5;
        }
        return new string(chars);
    }

    /// <summary>Parse a TID string to its 64-bit integer value, validating syntax.</summary>
    public static ulong Decode(string tid)
    {
        if (!IsValid(tid))
            throw new FormatException($"'{tid}' is not a valid TID.");
        ulong value = 0;
        foreach (char c in tid)
            value = (value << 5) | (uint)Alphabet.IndexOf(c);
        return value;
    }

    /// <summary>True if <paramref name="tid"/> is syntactically a valid TID.</summary>
    public static bool IsValid(string? tid)
    {
        if (tid is null || tid.Length != Length)
            return false;
        // First char encodes the top 5 bits; the top bit is always 0, so it is one of 234567abcdefghij.
        if (Alphabet.IndexOf(tid[0]) is < 0 or > 15)
            return false;
        foreach (char c in tid)
        {
            if (Alphabet.IndexOf(c) < 0)
                return false;
        }
        return true;
    }

    /// <summary>The timestamp (UTC) encoded in a TID value.</summary>
    public static DateTimeOffset ToTimestamp(ulong value)
    {
        long micros = (long)((value >> 10) & TimestampMask);
        return DateTimeOffset.UnixEpoch.AddTicks(micros * 10);
    }

    /// <summary>Compose a TID value from a microsecond timestamp and a 10-bit clock identifier.</summary>
    public static ulong Compose(ulong microsSinceEpoch, int clockId) =>
        ((microsSinceEpoch & TimestampMask) << 10) | ((ulong)clockId & ClockIdMask);
}

/// <summary>
/// A monotonic TID generator. Each instance carries a random clock identifier and guarantees the
/// stream of emitted TIDs is strictly increasing, even when several are produced within one
/// microsecond or the wall clock steps backwards.
/// </summary>
public sealed class TidClock
{
    private readonly int _clockId;
    private readonly object _gate = new();
    private ulong _last;

    /// <summary>Create a clock with a random 10-bit identifier (or a fixed one for tests).</summary>
    public TidClock(int? clockId = null)
    {
        _clockId = (clockId ?? RandomNumberGenerator.GetInt32(0, 1 << 10)) & 0x3ff;
    }

    /// <summary>The 10-bit clock identifier embedded in every TID this instance emits.</summary>
    public int ClockId => _clockId;

    /// <summary>Emit the next monotonically increasing TID value.</summary>
    public ulong NextValue()
    {
        lock (_gate)
        {
            ulong micros = (ulong)((DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10);
            ulong value = Tid.Compose(micros, _clockId);
            if (value <= _last)
                value = _last + 1;
            _last = value;
            return value;
        }
    }

    /// <summary>Emit the next monotonically increasing TID string.</summary>
    public string Next() => Tid.Encode(NextValue());

    /// <summary>
    /// Advance the clock so the next emitted TID is strictly greater than <paramref name="value"/>.
    /// Used when rehydrating a persisted repository so revisions stay monotonic across a restart.
    /// </summary>
    public void EnsureAfter(ulong value)
    {
        lock (_gate)
        {
            if (_last < value)
                _last = value;
        }
    }
}
