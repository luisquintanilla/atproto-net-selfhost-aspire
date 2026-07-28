using AtProto.Firehose;

namespace AtProto.AppView;

/// <summary>A normalized record change flowing into the presence projection.</summary>
public sealed record StatusUpdate(
    string Did,
    string Collection,
    string Rkey,
    string? Cid,
    long Seq,
    DateTimeOffset UpdatedAt,
    RepoOpAction Action);

/// <summary>The latest known presence for one account (the materialized read-model entry).</summary>
public sealed record PresenceEntry(
    string Did,
    string Rkey,
    string? Cid,
    long Seq,
    DateTimeOffset UpdatedAt);

/// <summary>A windowed activity sample emitted by the Rx projection for the live view.</summary>
public sealed record BoardStats(
    long TotalUpdates,
    int UniqueUsers,
    double UpdatesPerSecond,
    long LastSeq,
    DateTimeOffset At);
