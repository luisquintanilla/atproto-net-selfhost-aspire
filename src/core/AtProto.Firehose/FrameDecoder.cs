using System.Globalization;
using AtProto.Cbor;

namespace AtProto.Firehose;

/// <summary>Raised when the firehose delivers an error frame (header <c>op == -1</c>).</summary>
public sealed class FirehoseErrorException(string error, string? message)
    : Exception($"firehose error '{error}': {message}")
{
    public string Error { get; } = error;
    public string? ErrorMessage { get; } = message;
}

/// <summary>
/// Decodes a single firehose frame. Each frame is two concatenated DAG-CBOR objects: a header
/// <c>{ op, t }</c> followed by a type-specific payload. Returns null for frames we skip
/// (<c>#info</c>).
/// </summary>
public static class FrameDecoder
{
    public static RepoEvent? Decode(ReadOnlyMemory<byte> frame)
    {
        object? header = DagCbor.DecodeFirst(frame, out int consumed);
        long op = header.RequiredField("op").AsInt64();
        string? type = header.Field("t") as string;

        object? payload = DagCbor.Decode(frame.Span[consumed..]);

        if (op == -1)
        {
            string err = payload.Field("error") as string ?? "unknown";
            string? msg = payload.Field("message") as string;
            throw new FirehoseErrorException(err, msg);
        }

        return type switch
        {
            "#commit" => DecodeCommit(payload),
            "#sync" => DecodeSync(payload),
            "#identity" => DecodeIdentity(payload),
            "#account" => DecodeAccount(payload),
            "#info" => null,
            null => null,
            _ => new UnknownEvent(payload.Field("seq").AsInt64OrDefault(), type),
        };
    }

    private static RepoCommitEvent DecodeCommit(object? p)
    {
        var ops = new List<RepoOp>();
        if (p.Field("ops") is List<object?> rawOps)
        {
            foreach (object? o in rawOps)
            {
                ops.Add(new RepoOp(
                    ParseAction(o.Field("action") as string),
                    o.RequiredField("path").AsString(),
                    o.Field("cid") is Cid c ? c : null,
                    o.Field("prev") is Cid pr ? pr : null));
            }
        }

        return new RepoCommitEvent(
            Seq: p.RequiredField("seq").AsInt64(),
            Did: p.RequiredField("repo").AsString(),
            Rev: p.RequiredField("rev").AsString(),
            Commit: p.RequiredField("commit").AsCid(),
            Since: p.Field("since") as string,
            PrevData: p.Field("prevData") is Cid pd ? pd : null,
            Blocks: p.Field("blocks") as byte[] ?? [],
            Ops: ops,
            Time: ParseTime(p.Field("time") as string));
    }

    private static RepoSyncEvent DecodeSync(object? p) => new(
        p.RequiredField("seq").AsInt64(),
        p.RequiredField("did").AsString(),
        p.Field("rev") as string ?? string.Empty,
        p.Field("blocks") as byte[] ?? [],
        ParseTime(p.Field("time") as string));

    private static RepoIdentityEvent DecodeIdentity(object? p) => new(
        p.RequiredField("seq").AsInt64(),
        p.RequiredField("did").AsString(),
        p.Field("handle") as string,
        ParseTime(p.Field("time") as string));

    private static RepoAccountEvent DecodeAccount(object? p) => new(
        p.RequiredField("seq").AsInt64(),
        p.RequiredField("did").AsString(),
        p.Field("active") as bool? ?? false,
        p.Field("status") as string,
        ParseTime(p.Field("time") as string));

    private static RepoOpAction ParseAction(string? action) => action switch
    {
        "create" => RepoOpAction.Create,
        "update" => RepoOpAction.Update,
        "delete" => RepoOpAction.Delete,
        _ => RepoOpAction.Unknown,
    };

    private static DateTimeOffset? ParseTime(string? time) =>
        time is not null && DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset t)
            ? t
            : null;
}

internal static class FieldExtensions
{
    public static long AsInt64OrDefault(this object? value) => value switch
    {
        long l => l,
        ulong u => (long)u,
        int i => i,
        _ => 0,
    };
}
