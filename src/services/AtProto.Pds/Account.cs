using System.Collections.Concurrent;
using System.Security.Cryptography;
using AtProto.Crypto;
using AtProto.Repo;

namespace AtProto.Pds;

/// <summary>A hosted account: its identity, signing key, in-memory repository, and password hash.</summary>
public sealed class Account
{
    public required string Did { get; init; }
    public required string Handle { get; init; }
    public required string AccountId { get; init; }
    public required EcKeypair SigningKey { get; init; }
    public required RepoStore Repo { get; init; }
    public required byte[] PasswordSalt { get; init; }
    public required byte[] PasswordHash { get; init; }
}

/// <summary>
/// An in-memory registry of hosted accounts, indexed by DID, handle, and account id. (SQLite-backed
/// persistence is a later concern; the write-path correctness this milestone proves is independent
/// of the store.)
/// </summary>
public sealed class AccountStore
{
    private const int PbkdfIterations = 100_000;

    private readonly ConcurrentDictionary<string, Account> _byDid = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Account> _byHandle = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Account> _byAccountId = new(StringComparer.Ordinal);

    public bool HandleExists(string handle) => _byHandle.ContainsKey(handle);

    public void Add(Account account)
    {
        _byDid[account.Did] = account;
        _byHandle[account.Handle] = account;
        _byAccountId[account.AccountId] = account;
    }

    public Account? ByDid(string did) => _byDid.GetValueOrDefault(did);

    public Account? ByHandle(string handle) => _byHandle.GetValueOrDefault(handle);

    public Account? ByAccountId(string accountId) => _byAccountId.GetValueOrDefault(accountId);

    /// <summary>Resolve a login identifier that may be a handle or a DID.</summary>
    public Account? Resolve(string identifier) =>
        identifier.StartsWith("did:", StringComparison.Ordinal) ? ByDid(identifier) : ByHandle(identifier);

    public IEnumerable<Account> All => _byDid.Values;

    /// <summary>PBKDF2-SHA256 password hash (salt + derived key).</summary>
    public static (byte[] Salt, byte[] Hash) HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PbkdfIterations, HashAlgorithmName.SHA256, 32);
        return (salt, hash);
    }

    public static bool VerifyPassword(string password, byte[] salt, byte[] expectedHash)
    {
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, PbkdfIterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }
}
