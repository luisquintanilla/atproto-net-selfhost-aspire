using System.Collections.Concurrent;

namespace AtProto.Relay;

/// <summary>Live state for one upstream host the relay is crawling.</summary>
public sealed class HostState(string url)
{
    public string Url { get; } = url;
    public string Status { get; set; } = "idle";
    public long LastUpstreamSeq { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
}

/// <summary>Latest known state for one repo (account) the relay has seen a commit for.</summary>
public sealed record RepoState(string Did, string Host, string Rev, long GlobalSeq, bool Active);

/// <summary>
/// Tracks the relay's view of the network: which upstream hosts it crawls, and per-repo the
/// authoritative host + latest rev/seq. Also enforces lenient per-repo <c>rev</c> monotonicity
/// (revs are TIDs, hence lexicographically sortable). Thread-safe: crawl loops for different hosts
/// update it concurrently while the XRPC endpoints read it.
/// </summary>
public sealed class HostRegistry
{
    private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RepoState> _repos = new(StringComparer.Ordinal);

    public HostState GetOrAddHost(string url) => _hosts.GetOrAdd(url, u => new HostState(u));

    public IReadOnlyCollection<HostState> Hosts => _hosts.Values.ToList();

    public RepoState? GetRepo(string did) => _repos.TryGetValue(did, out RepoState? s) ? s : null;

    /// <summary>
    /// Lenient rev check: accept only if <paramref name="rev"/> is strictly greater than the last
    /// rev seen for this repo (or it is the first). Rejects replays / out-of-order commits without
    /// needing the full MST. Does not mutate state — call <see cref="RecordCommit"/> on accept.
    /// </summary>
    public bool IsRevMonotonic(string did, string rev) =>
        !_repos.TryGetValue(did, out RepoState? existing)
        || string.CompareOrdinal(rev, existing.Rev) > 0;

    /// <summary>Record an accepted commit's repo state (authoritative host + latest rev/seq).</summary>
    public void RecordCommit(string did, string host, string rev, long globalSeq) =>
        _repos[did] = new RepoState(did, host, rev, globalSeq, Active: true);

    /// <summary>Apply an account activation/status change to a repo's active flag.</summary>
    public void RecordAccount(string did, string host, bool active)
    {
        _repos.AddOrUpdate(
            did,
            _ => new RepoState(did, host, string.Empty, 0, active),
            (_, existing) => existing with { Active = active });
    }
}
