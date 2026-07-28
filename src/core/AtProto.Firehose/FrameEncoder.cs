using AtProto.Cbor;

namespace AtProto.Firehose;

/// <summary>
/// Encodes firehose frames for the server side (PDS/Relay). A frame is two concatenated dag-cbor
/// objects: a header <c>{ op, t }</c> and a type-specific payload — the exact shape
/// <see cref="FrameDecoder"/> reads back. Deprecated fields (<c>tooBig</c>, <c>blobs</c>) are
/// emitted with their fixed Sync v1.1 values for wire compatibility.
/// </summary>
public static class FrameEncoder
{
    private const long MessageOp = 1;
    private const long ErrorOp = -1;

    /// <summary>Encode a <c>#commit</c> frame.</summary>
    public static byte[] EncodeCommit(RepoCommitEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var ops = new List<object?>(e.Ops.Count);
        foreach (RepoOp op in e.Ops)
        {
            ops.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["action"] = ActionString(op.Action),
                ["path"] = op.Path,
                ["cid"] = op.Cid is Cid c ? c : null,
                ["prev"] = op.Prev is Cid p ? p : null,
            });
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["seq"] = e.Seq,
            ["rebase"] = false,
            ["tooBig"] = false,
            ["repo"] = e.Did,
            ["commit"] = e.Commit,
            ["rev"] = e.Rev,
            ["since"] = e.Since,
            ["blocks"] = e.Blocks,
            ["ops"] = ops,
            ["blobs"] = new List<object?>(),
            ["prevData"] = e.PrevData is Cid pd ? pd : null,
            ["time"] = Iso(e.Time),
        };
        return Frame("#commit", payload);
    }

    /// <summary>Encode a <c>#sync</c> frame (current signed commit for a repo).</summary>
    public static byte[] EncodeSync(RepoSyncEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Frame("#sync", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["seq"] = e.Seq,
            ["did"] = e.Did,
            ["rev"] = e.Rev,
            ["blocks"] = e.Blocks,
            ["time"] = Iso(e.Time),
        });
    }

    /// <summary>Encode an <c>#identity</c> frame.</summary>
    public static byte[] EncodeIdentity(RepoIdentityEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Frame("#identity", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["seq"] = e.Seq,
            ["did"] = e.Did,
            ["handle"] = e.Handle,
            ["time"] = Iso(e.Time),
        });
    }

    /// <summary>Encode an <c>#account</c> frame.</summary>
    public static byte[] EncodeAccount(RepoAccountEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Frame("#account", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["seq"] = e.Seq,
            ["did"] = e.Did,
            ["active"] = e.Active,
            ["status"] = e.Status,
            ["time"] = Iso(e.Time),
        });
    }

    /// <summary>Encode an error frame (header <c>op == -1</c>).</summary>
    public static byte[] EncodeError(string error, string? message)
    {
        byte[] header = DagCbor.Encode(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["op"] = ErrorOp,
        });
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["error"] = error };
        if (message is not null)
            payload["message"] = message;
        return Concat(header, DagCbor.Encode(payload));
    }

    private static byte[] Frame(string type, Dictionary<string, object?> payload)
    {
        byte[] header = DagCbor.Encode(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["op"] = MessageOp,
            ["t"] = type,
        });
        return Concat(header, DagCbor.Encode(payload));
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, result, 0, a.Length);
        Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
        return result;
    }

    private static string ActionString(RepoOpAction action) => action switch
    {
        RepoOpAction.Create => "create",
        RepoOpAction.Update => "update",
        RepoOpAction.Delete => "delete",
        _ => "unknown",
    };

    private static string Iso(DateTimeOffset? time) =>
        (time ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}
