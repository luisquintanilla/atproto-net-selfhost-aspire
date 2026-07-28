namespace AtProto.Repo;

/// <summary>
/// A signed AT Protocol repository commit (dag-cbor map):
/// <c>{ did, version:3, data:&lt;MST root CID&gt;, rev:&lt;TID&gt;, prev:CID|null, sig:bytes }</c>.
/// </summary>
public sealed record Commit(
    string Did,
    long Version,
    Cid Data,
    string Rev,
    Cid? Prev,
    byte[] Sig);

/// <summary>A single record entry in a repository: its <c>collection/rkey</c> key and value CID.</summary>
public readonly record struct RepoRecord(string Key, Cid Cid)
{
    private int Slash => Key.IndexOf('/');

    /// <summary>The collection NSID portion of the key.</summary>
    public string Collection => Slash >= 0 ? Key[..Slash] : Key;

    /// <summary>The record key (rkey) portion.</summary>
    public string Rkey => Slash >= 0 ? Key[(Slash + 1)..] : string.Empty;
}
