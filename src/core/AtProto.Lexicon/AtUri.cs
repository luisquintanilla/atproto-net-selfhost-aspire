namespace AtProto.Lexicon;

/// <summary>
/// An AT URI: <c>at://&lt;authority&gt;[/&lt;collection&gt;[/&lt;rkey&gt;]]</c> where authority is a
/// DID or handle. Query and fragment components are not modeled (unused by the repo layer).
/// </summary>
public readonly record struct AtUri
{
    public const string Scheme = "at://";

    public string Authority { get; }
    public string? Collection { get; }
    public string? Rkey { get; }

    public AtUri(string authority, string? collection = null, string? rkey = null)
    {
        if (string.IsNullOrEmpty(authority))
            throw new FormatException("AT URI authority is required");
        Authority = authority;
        Collection = collection;
        Rkey = rkey;
    }

    public static AtUri Parse(string value)
    {
        if (!TryParse(value, out AtUri uri))
            throw new FormatException($"invalid AT URI '{value}'");
        return uri;
    }

    public static bool TryParse(string? value, out AtUri uri)
    {
        uri = default;
        if (value is null || !value.StartsWith(Scheme, StringComparison.Ordinal))
            return false;

        string rest = value[Scheme.Length..];
        int hash = rest.IndexOf('#');
        if (hash >= 0)
            rest = rest[..hash];
        int query = rest.IndexOf('?');
        if (query >= 0)
            rest = rest[..query];

        string[] parts = rest.Split('/');
        if (parts.Length == 0 || parts[0].Length == 0)
            return false;

        uri = new AtUri(
            parts[0],
            parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null,
            parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null);
        return true;
    }

    /// <summary>The <c>collection/rkey</c> repository key, or null when either part is absent.</summary>
    public string? RepoKey => Collection is not null && Rkey is not null ? $"{Collection}/{Rkey}" : null;

    public override string ToString()
    {
        string s = Scheme + Authority;
        if (Collection is not null)
            s += "/" + Collection;
        if (Rkey is not null)
            s += "/" + Rkey;
        return s;
    }
}
