using AtProto.Crypto;
using AtProto.Repo;
using Microsoft.Data.Sqlite;

namespace AtProto.Pds;

/// <summary>
/// A durable <see cref="IPdsPersistence"/> backed by a single SQLite database (the production
/// profile). Accounts, repository blocks/records/head, and blobs are written through on every
/// change and replayed on startup. One database holds every account this PDS instance hosts (a
/// pragmatic simplification of atproto's per-actor store; still durable and inspectable with any
/// SQLite tool). All access is serialized through one connection, which is enough for a
/// single-node self-hosted PDS and keeps the write path simple.
/// </summary>
public sealed class SqlitePdsPersistence : IPdsPersistence, IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;

    public SqlitePdsPersistence(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        _connection.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS accounts (
                did              TEXT PRIMARY KEY,
                handle           TEXT NOT NULL,
                account_id       TEXT NOT NULL,
                key_type         INTEGER NOT NULL,
                signing_key_sec1 BLOB NOT NULL,
                pw_salt          BLOB NOT NULL,
                pw_hash          BLOB NOT NULL,
                created_at       TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS repo_blocks (
                did   TEXT NOT NULL,
                cid   TEXT NOT NULL,
                bytes BLOB NOT NULL,
                PRIMARY KEY (did, cid)
            );
            CREATE TABLE IF NOT EXISTS repo_records (
                did  TEXT NOT NULL,
                path TEXT NOT NULL,
                cid  TEXT NOT NULL,
                PRIMARY KEY (did, path)
            );
            CREATE TABLE IF NOT EXISTS repo_head (
                did      TEXT PRIMARY KEY,
                head_cid TEXT NOT NULL,
                rev      TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS blobs (
                cid          TEXT PRIMARY KEY,
                content_type TEXT NOT NULL,
                bytes        BLOB NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void SaveAccount(PersistedAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();
            using (SqliteCommand cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO accounts (did, handle, account_id, key_type, signing_key_sec1, pw_salt, pw_hash, created_at)
                    VALUES ($did, $handle, $accountId, $keyType, $key, $salt, $hash, $createdAt)
                    ON CONFLICT(did) DO UPDATE SET
                        handle = $handle, account_id = $accountId, key_type = $keyType,
                        signing_key_sec1 = $key, pw_salt = $salt, pw_hash = $hash;
                    """;
                cmd.Parameters.AddWithValue("$did", account.Did);
                cmd.Parameters.AddWithValue("$handle", account.Handle);
                cmd.Parameters.AddWithValue("$accountId", account.AccountId);
                cmd.Parameters.AddWithValue("$keyType", (int)account.KeyType);
                cmd.Parameters.AddWithValue("$key", account.SigningKeySec1);
                cmd.Parameters.AddWithValue("$salt", account.PasswordSalt);
                cmd.Parameters.AddWithValue("$hash", account.PasswordHash);
                cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            WriteRepo(tx, account.Did, account.Repo);
            tx.Commit();
        }
    }

    public void SaveRepo(string did, RepoSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrEmpty(did);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();
            WriteRepo(tx, did, snapshot);
            tx.Commit();
        }
    }

    public void SaveBlob(StoredBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO blobs (cid, content_type, bytes) VALUES ($cid, $contentType, $bytes)
                ON CONFLICT(cid) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$cid", blob.Cid.ToString());
            cmd.Parameters.AddWithValue("$contentType", blob.ContentType);
            cmd.Parameters.AddWithValue("$bytes", blob.Bytes);
            cmd.ExecuteNonQuery();
        }
    }

    // Replace the persisted repository slice for one did with the current snapshot. Blocks are the
    // reachable set (commit + MST nodes + record blocks), so a straight replace keeps the table equal
    // to the live repository with no orphaned blocks.
    private void WriteRepo(SqliteTransaction tx, string did, RepoSnapshot snapshot)
    {
        Delete(tx, "DELETE FROM repo_blocks WHERE did = $did", did);
        Delete(tx, "DELETE FROM repo_records WHERE did = $did", did);

        using (SqliteCommand blocks = _connection.CreateCommand())
        {
            blocks.Transaction = tx;
            blocks.CommandText = "INSERT INTO repo_blocks (did, cid, bytes) VALUES ($did, $cid, $bytes);";
            SqliteParameter didParam = blocks.Parameters.Add("$did", SqliteType.Text);
            SqliteParameter cidParam = blocks.Parameters.Add("$cid", SqliteType.Text);
            SqliteParameter bytesParam = blocks.Parameters.Add("$bytes", SqliteType.Blob);
            didParam.Value = did;
            foreach (KeyValuePair<Cid, byte[]> block in snapshot.Blocks)
            {
                cidParam.Value = block.Key.ToString();
                bytesParam.Value = block.Value;
                blocks.ExecuteNonQuery();
            }
        }

        using (SqliteCommand records = _connection.CreateCommand())
        {
            records.Transaction = tx;
            records.CommandText = "INSERT INTO repo_records (did, path, cid) VALUES ($did, $path, $cid);";
            SqliteParameter didParam = records.Parameters.Add("$did", SqliteType.Text);
            SqliteParameter pathParam = records.Parameters.Add("$path", SqliteType.Text);
            SqliteParameter cidParam = records.Parameters.Add("$cid", SqliteType.Text);
            didParam.Value = did;
            foreach (KeyValuePair<string, Cid> record in snapshot.Records)
            {
                pathParam.Value = record.Key;
                cidParam.Value = record.Value.ToString();
                records.ExecuteNonQuery();
            }
        }

        using SqliteCommand head = _connection.CreateCommand();
        head.Transaction = tx;
        head.CommandText = """
            INSERT INTO repo_head (did, head_cid, rev) VALUES ($did, $head, $rev)
            ON CONFLICT(did) DO UPDATE SET head_cid = $head, rev = $rev;
            """;
        head.Parameters.AddWithValue("$did", did);
        head.Parameters.AddWithValue("$head", snapshot.Head.ToString());
        head.Parameters.AddWithValue("$rev", snapshot.Rev);
        head.ExecuteNonQuery();
    }

    private void Delete(SqliteTransaction tx, string sql, string did)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$did", did);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<PersistedAccount> LoadAccounts()
    {
        lock (_gate)
        {
            var rows = new List<(string Did, string Handle, string AccountId, EcKeyType KeyType, byte[] Key, byte[] Salt, byte[] Hash)>();
            using (SqliteCommand cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT did, handle, account_id, key_type, signing_key_sec1, pw_salt, pw_hash FROM accounts;";
                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        (EcKeyType)reader.GetInt32(3),
                        (byte[])reader.GetValue(4),
                        (byte[])reader.GetValue(5),
                        (byte[])reader.GetValue(6)));
                }
            }

            var accounts = new List<PersistedAccount>(rows.Count);
            foreach (var row in rows)
            {
                RepoSnapshot snapshot = LoadSnapshot(row.Did);
                accounts.Add(new PersistedAccount(
                    row.Did, row.Handle, row.AccountId, row.KeyType, row.Key, row.Salt, row.Hash, snapshot));
            }
            return accounts;
        }
    }

    private RepoSnapshot LoadSnapshot(string did)
    {
        var records = new Dictionary<string, Cid>(StringComparer.Ordinal);
        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT path, cid FROM repo_records WHERE did = $did;";
            cmd.Parameters.AddWithValue("$did", did);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
                records[reader.GetString(0)] = Cid.Decode(reader.GetString(1));
        }

        var blocks = new Dictionary<Cid, byte[]>();
        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT cid, bytes FROM repo_blocks WHERE did = $did;";
            cmd.Parameters.AddWithValue("$did", did);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
                blocks[Cid.Decode(reader.GetString(0))] = (byte[])reader.GetValue(1);
        }

        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT head_cid, rev FROM repo_head WHERE did = $did;";
            cmd.Parameters.AddWithValue("$did", did);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException($"Account {did} has no persisted repository head.");
            return new RepoSnapshot(Cid.Decode(reader.GetString(0)), reader.GetString(1), records, blocks);
        }
    }

    public IReadOnlyList<StoredBlob> LoadBlobs()
    {
        lock (_gate)
        {
            var blobs = new List<StoredBlob>();
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT cid, content_type, bytes FROM blobs;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
                blobs.Add(new StoredBlob(Cid.Decode(reader.GetString(0)), (byte[])reader.GetValue(2), reader.GetString(1)));
            return blobs;
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
