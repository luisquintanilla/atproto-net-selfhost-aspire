using System.Data;
using DuckDB.NET.Data;

namespace AtProto.AnalyticsView;

/// <summary>One firehose repo operation, flattened for analytics (no record body, all collections).</summary>
public readonly record struct AnalyticsOp(string Collection, string Rkey, string Action, string? Cid);

/// <summary>Totals plus per-collection and per-action breakdowns over every ingested op.</summary>
public sealed record StatsResult(
    long Total,
    IReadOnlyList<CollectionCount> ByCollection,
    IReadOnlyList<ActionCount> ByAction);

public readonly record struct CollectionCount(string Collection, long Count);
public readonly record struct ActionCount(string Action, long Count);
public readonly record struct RepoCount(string Did, long Count);
public readonly record struct ActivityBucket(DateTime Time, long Count);
public readonly record struct CollectionBreakdown(string Collection, long Count, long Repos);

/// <summary>
/// The DuckDB-backed analytics store. Columnar OLAP over the whole firehose: GROUP BY collection,
/// per-action counts, per-repo top-N, and time-bucketed activity - the aggregate questions the
/// latest-wins presence board structurally cannot answer.
/// </summary>
/// <remarks>
/// Single-writer discipline: the ingest loop and the read endpoints share one <see cref="DuckDBConnection"/>,
/// which is not thread-safe, so every append and every query is serialized under one lock. DuckDB is fast
/// enough that this is a non-issue at self-host scale, and it keeps the store trivially correct.
/// </remarks>
public sealed class AnalyticsStore : IDisposable
{
    private readonly object _gate = new();
    private readonly DuckDBConnection _conn;

    /// <param name="dataSource">A DuckDB data source: a filesystem path, or <c>:memory:</c> for an ephemeral store.</param>
    public AnalyticsStore(string dataSource)
    {
        if (!string.Equals(dataSource, ":memory:", StringComparison.Ordinal))
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        _conn = new DuckDBConnection($"DataSource={dataSource}");
        _conn.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using DuckDBCommand cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                seq        BIGINT,
                did        VARCHAR,
                collection VARCHAR,
                rkey       VARCHAR,
                action     VARCHAR,
                cid        VARCHAR,
                ts         TIMESTAMP
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Append every op of one commit in a single Appender batch (flushed on dispose).</summary>
    public void RecordCommit(long seq, string did, DateTimeOffset time, IReadOnlyList<AnalyticsOp> ops)
    {
        if (ops.Count == 0)
            return;

        DateTime tsUtc = time.UtcDateTime;
        lock (_gate)
        {
            using DuckDBAppender appender = _conn.CreateAppender("events");
            foreach (AnalyticsOp op in ops)
            {
                IDuckDBAppenderRow row = appender.CreateRow();
                row.AppendValue(seq);
                row.AppendValue(did);
                row.AppendValue(op.Collection);
                row.AppendValue(op.Rkey);
                row.AppendValue(op.Action);
                if (op.Cid is null)
                    row.AppendNullValue();
                else
                    row.AppendValue(op.Cid);
                row.AppendValue(tsUtc);
                row.EndRow();
            }
        }
    }

    public long Total()
    {
        lock (_gate)
            return ScalarLong("SELECT count(*) FROM events");
    }

    public StatsResult GetStats()
    {
        lock (_gate)
        {
            long total = ScalarLong("SELECT count(*) FROM events");

            var byCollection = new List<CollectionCount>();
            ReadRows(
                "SELECT collection, count(*) AS c FROM events GROUP BY collection ORDER BY c DESC, collection",
                r => byCollection.Add(new CollectionCount(r.GetString(0), r.GetInt64(1))));

            var byAction = new List<ActionCount>();
            ReadRows(
                "SELECT action, count(*) AS c FROM events GROUP BY action ORDER BY c DESC, action",
                r => byAction.Add(new ActionCount(r.GetString(0), r.GetInt64(1))));

            return new StatsResult(total, byCollection, byAction);
        }
    }

    /// <summary>Time-bucketed activity, oldest bucket first. <paramref name="unit"/> is whitelisted to a DuckDB truncation part.</summary>
    public IReadOnlyList<ActivityBucket> GetActivity(string unit, int limit)
    {
        string part = unit switch
        {
            "second" => "second",
            "hour" => "hour",
            "day" => "day",
            _ => "minute",
        };
        int n = Math.Clamp(limit, 1, 1440);

        var buckets = new List<ActivityBucket>();
        lock (_gate)
        {
            ReadRows(
                $"SELECT date_trunc('{part}', ts) AS bucket, count(*) AS c " +
                $"FROM events GROUP BY bucket ORDER BY bucket DESC LIMIT {n}",
                r => buckets.Add(new ActivityBucket(r.GetDateTime(0), r.GetInt64(1))));
        }
        buckets.Reverse(); // oldest -> newest for charting
        return buckets;
    }

    public IReadOnlyList<RepoCount> GetTopRepos(int limit)
    {
        int n = Math.Clamp(limit, 1, 500);
        var repos = new List<RepoCount>();
        lock (_gate)
        {
            ReadRows(
                $"SELECT did, count(*) AS c FROM events GROUP BY did ORDER BY c DESC, did LIMIT {n}",
                r => repos.Add(new RepoCount(r.GetString(0), r.GetInt64(1))));
        }
        return repos;
    }

    public IReadOnlyList<CollectionBreakdown> GetCollections()
    {
        var rows = new List<CollectionBreakdown>();
        lock (_gate)
        {
            ReadRows(
                "SELECT collection, count(*) AS c, count(DISTINCT did) AS repos " +
                "FROM events GROUP BY collection ORDER BY c DESC, collection",
                r => rows.Add(new CollectionBreakdown(r.GetString(0), r.GetInt64(1), r.GetInt64(2))));
        }
        return rows;
    }

    private long ScalarLong(string sql)
    {
        using DuckDBCommand cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    private void ReadRows(string sql, Action<IDataRecord> onRow)
    {
        using DuckDBCommand cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        using DuckDBDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            onRow(reader);
    }

    public void Dispose() => _conn.Dispose();
}
