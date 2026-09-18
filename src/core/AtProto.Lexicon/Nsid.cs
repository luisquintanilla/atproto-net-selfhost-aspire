namespace AtProto.Lexicon;

/// <summary>
/// A Namespaced Identifier (e.g. <c>app.bsky.feed.post</c>): a reverse-DNS authority plus a
/// final name segment. Validation is pragmatic (structure + charset + length), not exhaustive.
/// </summary>
public readonly record struct Nsid
{
    public string Value { get; }

    public Nsid(string value)
    {
        if (!IsValid(value, out string? error))
            throw new FormatException($"invalid NSID '{value}': {error}");
        Value = value;
    }

    /// <summary>The authority (all segments except the last), in reverse-DNS order.</summary>
    public string Authority => Value[..Value.LastIndexOf('.')];

    /// <summary>The final name segment.</summary>
    public string Name => Value[(Value.LastIndexOf('.') + 1)..];

    public static bool TryParse(string? value, out Nsid nsid)
    {
        if (value is not null && IsValid(value, out _))
        {
            nsid = new Nsid(value);
            return true;
        }
        nsid = default;
        return false;
    }

    private static bool IsValid(string value, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(value) || value.Length > 317)
        {
            error = "empty or longer than 317 chars";
            return false;
        }

        string[] segments = value.Split('.');
        if (segments.Length < 3)
        {
            error = "must have at least 3 segments";
            return false;
        }

        for (int i = 0; i < segments.Length; i++)
        {
            string seg = segments[i];
            if (seg.Length == 0 || seg.Length > 63)
            {
                error = "segment empty or too long";
                return false;
            }
            bool isName = i == segments.Length - 1;
            foreach (char c in seg)
            {
                bool ok = char.IsAsciiLetterOrDigit(c) || (!isName && c == '-');
                if (!ok)
                {
                    error = $"illegal character '{c}'";
                    return false;
                }
            }
            if (!isName && (seg[0] == '-' || seg[^1] == '-'))
            {
                error = "authority segment cannot start or end with a hyphen";
                return false;
            }
            if (char.IsAsciiDigit(seg[0]) && !isName)
            {
                error = "authority segment cannot start with a digit";
                return false;
            }
            if (!char.IsAsciiLetter(seg[0]) && isName)
            {
                error = "name segment must start with a letter";
                return false;
            }
        }

        return true;
    }

    public override string ToString() => Value;
    public static implicit operator string(Nsid nsid) => nsid.Value;
}
