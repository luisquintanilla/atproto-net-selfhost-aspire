namespace AtProto.Firehose;

/// <summary>Base type for events emitted by <c>com.atproto.sync.subscribeRepos</c>.</summary>
public abstract record RepoEvent(long Seq);

/// <summary>The write action carried by a commit op.</summary>
public enum RepoOpAction
{
    Create,
    Update,
    Delete,
    Unknown,
}

/// <summary>One mutation in a commit: <c>action</c> at <c>collection/rkey</c> (<c>path</c>).</summary>
public sealed record RepoOp(RepoOpAction Action, string Path, Cid? Cid, Cid? Prev)
{
    private int Slash => Path.IndexOf('/');
    public string Collection => Slash >= 0 ? Path[..Slash] : Path;
    public string Rkey => Slash >= 0 ? Path[(Slash + 1)..] : string.Empty;
}

/// <summary>
/// A <c>#commit</c> event (Sync v1.1): a repository update with a CAR slice of changed blocks
/// and the list of record ops. <see cref="PrevData"/> is the MST root before this commit.
/// </summary>
public sealed record RepoCommitEvent(
    long Seq,
    string Did,
    string Rev,
    Cid Commit,
    string? Since,
    Cid? PrevData,
    byte[] Blocks,
    IReadOnlyList<RepoOp> Ops,
    DateTimeOffset? Time) : RepoEvent(Seq);

/// <summary>A <c>#sync</c> event carrying the current signed commit for a repo.</summary>
public sealed record RepoSyncEvent(
    long Seq,
    string Did,
    string Rev,
    byte[] Blocks,
    DateTimeOffset? Time) : RepoEvent(Seq);

/// <summary>An <c>#identity</c> event: a handle/DID-document change for an account.</summary>
public sealed record RepoIdentityEvent(
    long Seq,
    string Did,
    string? Handle,
    DateTimeOffset? Time) : RepoEvent(Seq);

/// <summary>An <c>#account</c> event: account activation/status change.</summary>
public sealed record RepoAccountEvent(
    long Seq,
    string Did,
    bool Active,
    string? Status,
    DateTimeOffset? Time) : RepoEvent(Seq);

/// <summary>Any event type we do not model explicitly (still carries its seq for cursoring).</summary>
public sealed record UnknownEvent(long Seq, string Type) : RepoEvent(Seq);
