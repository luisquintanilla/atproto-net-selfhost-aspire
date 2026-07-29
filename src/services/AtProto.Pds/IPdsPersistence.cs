using AtProto.Crypto;
using AtProto.Repo;

namespace AtProto.Pds;

/// <summary>
/// The durable slice of one hosted account: its identity, signing key material, password hash, and
/// a <see cref="RepoSnapshot"/> of its repository. Enough to reconstruct a live <see cref="Account"/>
/// after a restart.
/// </summary>
public sealed record PersistedAccount(
    string Did,
    string Handle,
    string AccountId,
    EcKeyType KeyType,
    byte[] SigningKeySec1,
    byte[] PasswordSalt,
    byte[] PasswordHash,
    RepoSnapshot Repo);

/// <summary>
/// The storage seam behind the PDS's in-memory read models. The default
/// (<see cref="NullPdsPersistence"/>) keeps everything in process (the dev profile). A durable
/// implementation (see <see cref="SqlitePdsPersistence"/>) writes each account, commit, and blob
/// through to disk and replays them on startup, so accounts and repositories survive a restart.
/// </summary>
public interface IPdsPersistence
{
    /// <summary>Load every persisted account (identity + key + repository snapshot) on startup.</summary>
    IReadOnlyList<PersistedAccount> LoadAccounts();

    /// <summary>Load every persisted blob on startup.</summary>
    IReadOnlyList<StoredBlob> LoadBlobs();

    /// <summary>Persist a newly created account together with its genesis repository.</summary>
    void SaveAccount(PersistedAccount account);

    /// <summary>Persist a repository after a commit (its new head, revision, records, and blocks).</summary>
    void SaveRepo(string did, RepoSnapshot snapshot);

    /// <summary>Persist an uploaded blob.</summary>
    void SaveBlob(StoredBlob blob);
}

/// <summary>The default storage seam: nothing is persisted (the in-memory dev profile).</summary>
public sealed class NullPdsPersistence : IPdsPersistence
{
    public IReadOnlyList<PersistedAccount> LoadAccounts() => Array.Empty<PersistedAccount>();

    public IReadOnlyList<StoredBlob> LoadBlobs() => Array.Empty<StoredBlob>();

    public void SaveAccount(PersistedAccount account) { }

    public void SaveRepo(string did, RepoSnapshot snapshot) { }

    public void SaveBlob(StoredBlob blob) { }
}

/// <summary>Maps between a live <see cref="Account"/> and its persisted form.</summary>
public static class AccountPersistence
{
    /// <summary>Capture the durable slice of a live account.</summary>
    public static PersistedAccount ToPersisted(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new PersistedAccount(
            account.Did,
            account.Handle,
            account.AccountId,
            account.SigningKey.KeyType,
            account.SigningKey.ExportPrivateKey(),
            account.PasswordSalt,
            account.PasswordHash,
            account.Repo.Snapshot());
    }

    /// <summary>Reconstruct a live account from its persisted slice, rehydrating key and repository.</summary>
    public static Account ToAccount(PersistedAccount persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        var key = EcKeypair.ImportPrivateKey(persisted.KeyType, persisted.SigningKeySec1);
        RepoStore repo = RepoStore.Load(key, persisted.Did, persisted.Repo);
        return new Account
        {
            Did = persisted.Did,
            Handle = persisted.Handle,
            AccountId = persisted.AccountId,
            SigningKey = key,
            Repo = repo,
            PasswordSalt = persisted.PasswordSalt,
            PasswordHash = persisted.PasswordHash,
        };
    }
}
