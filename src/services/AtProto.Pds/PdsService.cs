using System.Text.RegularExpressions;
using AtProto.Car;
using AtProto.Crypto;
using AtProto.Firehose;
using AtProto.Repo;
using RepoOp = AtProto.Firehose.RepoOp;
using StoreOp = AtProto.Repo.RepoOp;

namespace AtProto.Pds;

/// <summary>Raised for a client error that maps to an XRPC error response.</summary>
public sealed class XrpcException(int status, string error, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Error { get; } = error;
}

/// <summary>
/// The PDS core: creates accounts (mints a secp256k1 signing key + an empty repository, emits
/// <c>#account</c>/<c>#identity</c>), applies repository writes, and — for every commit — encodes a
/// Sync v1.1 <c>#commit</c> frame (with a CAR slice of the changed blocks) and publishes it on the
/// shared <see cref="FirehoseBroadcaster"/>. This is where a self-authored record becomes a signed,
/// verifiable firehose event.
/// </summary>
public sealed partial class PdsService(PdsIdentity identity, AccountStore accounts, FirehoseBroadcaster firehose)
{
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(2);
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(90);
    private readonly Lock _writeGate = new();

    public PdsIdentity Identity => identity;
    public AccountStore Accounts => accounts;
    public FirehoseBroadcaster Firehose => firehose;

    /// <summary>Create a new hosted account with a fresh signing key and genesis repository.</summary>
    public Account CreateAccount(string handle, string password)
    {
        string normalized = NormalizeHandle(handle);
        if (accounts.HandleExists(normalized))
            throw new XrpcException(400, "HandleNotAvailable", $"Handle already taken: {normalized}");
        if (password.Length < 1)
            throw new XrpcException(400, "InvalidRequest", "A password is required.");

        string accountId = AllocateAccountId(normalized);
        string did = identity.DidFor(accountId);
        var key = EcKeypair.Generate(EcKeyType.Secp256k1);
        RepoStore repo = RepoStore.CreateEmpty(key, did);
        (byte[] salt, byte[] hash) = AccountStore.HashPassword(password);

        var account = new Account
        {
            Did = did,
            Handle = normalized,
            AccountId = accountId,
            SigningKey = key,
            Repo = repo,
            PasswordSalt = salt,
            PasswordHash = hash,
        };
        accounts.Add(account);

        PublishAccount(did, active: true);
        PublishIdentity(did, normalized);
        return account;
    }

    /// <summary>Issue an access/refresh token pair for a session.</summary>
    public (string Access, string Refresh) IssueSession(Account account) => (
        Jwt.Issue(account.Did, "com.atproto.access", AccessLifetime, identity.JwtSecret),
        Jwt.Issue(account.Did, "com.atproto.refresh", RefreshLifetime, identity.JwtSecret));

    /// <summary>Authenticate a Bearer access token to its account.</summary>
    public Account Authenticate(string? authorizationHeader)
    {
        string? token = authorizationHeader is not null && authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader["Bearer ".Length..].Trim()
            : null;
        if (!Jwt.TryValidate(token, identity.JwtSecret, out string did))
            throw new XrpcException(401, "AuthenticationRequired", "Invalid or expired token.");
        return accounts.ByDid(did) ?? throw new XrpcException(401, "AuthenticationRequired", "Unknown account.");
    }

    /// <summary>Apply a batch of writes as one commit and publish the resulting <c>#commit</c>.</summary>
    public CommitResult Commit(Account account, IReadOnlyList<RepoWrite> writes)
    {
        lock (_writeGate)
        {
            // Capture pre-commit state for the Sync v1.1 since/prevData fields.
            string sinceRev = account.Repo.Commit.Rev;
            Cid prevData = account.Repo.Root;

            CommitResult result = account.Repo.ApplyWrites(writes);
            byte[] slice = CarWriter.Write(
                new[] { result.Commit },
                result.NewBlocks.Select(b => new CarBlock(b.Key, b.Value)));

            var commitEvent = new RepoCommitEvent(
                Seq: firehose.NextSeq(),
                Did: account.Did,
                Rev: result.Rev,
                Commit: result.Commit,
                Since: sinceRev,
                PrevData: prevData,
                Blocks: slice,
                Ops: result.Ops.Select(ToFirehoseOp).ToList(),
                Time: DateTimeOffset.UtcNow);

            firehose.Publish(new SequencedFrame(commitEvent.Seq, FrameEncoder.EncodeCommit(commitEvent)));
            return result;
        }
    }

    private void PublishAccount(string did, bool active) =>
        firehose.Publish(WithSeq(seq => FrameEncoder.EncodeAccount(
            new RepoAccountEvent(seq, did, active, active ? "active" : "inactive", DateTimeOffset.UtcNow))));

    private void PublishIdentity(string did, string handle) =>
        firehose.Publish(WithSeq(seq => FrameEncoder.EncodeIdentity(
            new RepoIdentityEvent(seq, did, handle, DateTimeOffset.UtcNow))));

    private SequencedFrame WithSeq(Func<long, byte[]> encode)
    {
        long seq = firehose.NextSeq();
        return new SequencedFrame(seq, encode(seq));
    }

    private static RepoOp ToFirehoseOp(StoreOp op) => new(
        op.Action switch
        {
            WriteAction.Create => RepoOpAction.Create,
            WriteAction.Update => RepoOpAction.Update,
            WriteAction.Delete => RepoOpAction.Delete,
            _ => RepoOpAction.Unknown,
        },
        op.Path,
        op.Cid,
        op.Prev);

    private string NormalizeHandle(string handle)
    {
        string h = handle.Trim().ToLowerInvariant().TrimStart('@');
        if (h.Length == 0)
            throw new XrpcException(400, "InvalidHandle", "A handle is required.");
        if (!h.Contains('.'))
            h = $"{h}.{identity.HandleDomain}";
        if (!HandlePattern().IsMatch(h))
            throw new XrpcException(400, "InvalidHandle", $"Invalid handle: {h}");
        return h;
    }

    private string AllocateAccountId(string handle)
    {
        string local = handle.Split('.')[0];
        string baseId = SanitizePattern().Replace(local, "");
        if (baseId.Length == 0)
            baseId = "user";

        string candidate = baseId;
        int suffix = 1;
        while (accounts.ByAccountId(candidate) is not null)
            candidate = $"{baseId}{++suffix}";
        return candidate;
    }

    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+[a-z]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex HandlePattern();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex SanitizePattern();
}
