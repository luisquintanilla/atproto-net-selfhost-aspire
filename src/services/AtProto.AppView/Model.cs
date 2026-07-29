using AtProto.Firehose;

namespace AtProto.AppView;

/// <summary>A normalized record change flowing into the presence projection.</summary>
public sealed record StatusUpdate(
    string Did,
    string Collection,
    string Rkey,
    string? Cid,
    string? Status,
    long Seq,
    DateTimeOffset UpdatedAt,
    RepoOpAction Action);

/// <summary>The latest known presence for one account (the materialized read-model entry).</summary>
public sealed record PresenceEntry(
    string Did,
    string Rkey,
    string? Cid,
    string? Status,
    long Seq,
    DateTimeOffset UpdatedAt);

/// <summary>A windowed activity sample emitted by the Rx projection for the live view.</summary>
public sealed record BoardStats(
    long TotalUpdates,
    int UniqueUsers,
    double UpdatesPerSecond,
    long LastSeq,
    DateTimeOffset At);

/// <summary>
/// A single applied change to the presence read-model, published live on
/// <see cref="PresenceProjection.Changes"/> and forwarded to connected boards by the broadcaster.
/// <see cref="Entry"/> is the resulting latest-wins entry for the account, or <c>null</c> when the
/// change was a delete (see <see cref="Removed"/>). This is the <c>GroupBy(did)</c>→latest
/// projection surfaced as an incremental event feed.
/// </summary>
public sealed record PresenceChange(
    string Did,
    PresenceEntry? Entry,
    bool Removed,
    long Seq,
    DateTimeOffset UpdatedAt);

/// <summary>One op inside a raw firehose commit (for the live "firehose ticker" view).</summary>
public sealed record OpInfo(string Action, string Collection, string Rkey, string? Cid);

/// <summary>
/// A raw <c>#commit</c> summary surfaced from ingest for the firehose ticker: the exact ops the
/// firehose carried, independent of whether they matched the indexed collection. Lets the UI show
/// the unprocessed event stream next to the materialized board.
/// </summary>
public sealed record CommitInfo(long Seq, string Did, DateTimeOffset Time, IReadOnlyList<OpInfo> Ops);

/// <summary>The body of a <c>POST /compose</c> request: the emoji "you" want to broadcast.</summary>
public sealed record ComposeRequest(string Status);
