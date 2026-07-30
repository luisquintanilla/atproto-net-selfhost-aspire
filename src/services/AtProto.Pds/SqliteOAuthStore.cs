using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AtProto.Pds;

/// <summary>
/// A durable <see cref="IOAuthStore"/> backed by a single SQLite database (the production profile),
/// the OAuth counterpart to <see cref="SqlitePdsPersistence"/>. Pushed requests, authorization codes,
/// sessions, refresh tokens, and the DPoP <c>jti</c> replay set are written through on every change so
/// active OAuth sessions survive a restart. All access is serialized through one connection, which is
/// enough for a single-node self-hosted PDS and keeps the atomic consume/rotate gates simple.
/// </summary>
public sealed class SqliteOAuthStore : IOAuthStore, IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;

    public SqliteOAuthStore(string databasePath)
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
            CREATE TABLE IF NOT EXISTS oauth_par (
                request_uri           TEXT PRIMARY KEY,
                client_id             TEXT NOT NULL,
                response_type         TEXT NOT NULL,
                redirect_uri          TEXT NOT NULL,
                scope                 TEXT NOT NULL,
                code_challenge        TEXT NOT NULL,
                code_challenge_method TEXT NOT NULL,
                state                 TEXT NULL,
                dpop_jkt              TEXT NOT NULL,
                login_hint            TEXT NULL,
                created_at            TEXT NOT NULL,
                expires_at            TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS oauth_codes (
                code                  TEXT PRIMARY KEY,
                client_id             TEXT NOT NULL,
                did                   TEXT NOT NULL,
                redirect_uri          TEXT NOT NULL,
                scope                 TEXT NOT NULL,
                code_challenge        TEXT NOT NULL,
                code_challenge_method TEXT NOT NULL,
                dpop_jkt              TEXT NOT NULL,
                created_at            TEXT NOT NULL,
                expires_at            TEXT NOT NULL,
                consumed              INTEGER NOT NULL DEFAULT 0,
                session_id            TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS oauth_sessions (
                session_id TEXT PRIMARY KEY,
                client_id  TEXT NOT NULL,
                did        TEXT NOT NULL,
                scope      TEXT NOT NULL,
                dpop_jkt   TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS oauth_refresh (
                token      TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                consumed   INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_oauth_refresh_session ON oauth_refresh (session_id);
            CREATE TABLE IF NOT EXISTS oauth_jti (
                jti        TEXT PRIMARY KEY,
                expires_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void SaveParRequest(ParRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO oauth_par
                    (request_uri, client_id, response_type, redirect_uri, scope, code_challenge,
                     code_challenge_method, state, dpop_jkt, login_hint, created_at, expires_at)
                VALUES
                    ($requestUri, $clientId, $responseType, $redirectUri, $scope, $codeChallenge,
                     $codeChallengeMethod, $state, $dpopJkt, $loginHint, $createdAt, $expiresAt)
                ON CONFLICT(request_uri) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$requestUri", request.RequestUri);
            cmd.Parameters.AddWithValue("$clientId", request.ClientId);
            cmd.Parameters.AddWithValue("$responseType", request.ResponseType);
            cmd.Parameters.AddWithValue("$redirectUri", request.RedirectUri);
            cmd.Parameters.AddWithValue("$scope", request.Scope);
            cmd.Parameters.AddWithValue("$codeChallenge", request.CodeChallenge);
            cmd.Parameters.AddWithValue("$codeChallengeMethod", request.CodeChallengeMethod);
            cmd.Parameters.AddWithValue("$state", (object?)request.State ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dpopJkt", request.DpopJkt);
            cmd.Parameters.AddWithValue("$loginHint", (object?)request.LoginHint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$createdAt", Iso(request.CreatedAt));
            cmd.Parameters.AddWithValue("$expiresAt", Iso(request.ExpiresAt));
            cmd.ExecuteNonQuery();
        }
    }

    public ParRequest? GetParRequest(string requestUri)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT request_uri, client_id, response_type, redirect_uri, scope, code_challenge,
                       code_challenge_method, state, dpop_jkt, login_hint, created_at, expires_at
                FROM oauth_par WHERE request_uri = $requestUri;
                """;
            cmd.Parameters.AddWithValue("$requestUri", requestUri);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;
            return new ParRequest(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                ParseIso(reader.GetString(10)), ParseIso(reader.GetString(11)));
        }
    }

    public void DeleteParRequest(string requestUri)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM oauth_par WHERE request_uri = $requestUri;";
            cmd.Parameters.AddWithValue("$requestUri", requestUri);
            cmd.ExecuteNonQuery();
        }
    }

    public void SaveAuthorizationCode(AuthorizationCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO oauth_codes
                    (code, client_id, did, redirect_uri, scope, code_challenge, code_challenge_method,
                     dpop_jkt, created_at, expires_at, consumed, session_id)
                VALUES
                    ($code, $clientId, $did, $redirectUri, $scope, $codeChallenge, $codeChallengeMethod,
                     $dpopJkt, $createdAt, $expiresAt, $consumed, $sessionId)
                ON CONFLICT(code) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$code", code.Code);
            cmd.Parameters.AddWithValue("$clientId", code.ClientId);
            cmd.Parameters.AddWithValue("$did", code.Did);
            cmd.Parameters.AddWithValue("$redirectUri", code.RedirectUri);
            cmd.Parameters.AddWithValue("$scope", code.Scope);
            cmd.Parameters.AddWithValue("$codeChallenge", code.CodeChallenge);
            cmd.Parameters.AddWithValue("$codeChallengeMethod", code.CodeChallengeMethod);
            cmd.Parameters.AddWithValue("$dpopJkt", code.DpopJkt);
            cmd.Parameters.AddWithValue("$createdAt", Iso(code.CreatedAt));
            cmd.Parameters.AddWithValue("$expiresAt", Iso(code.ExpiresAt));
            cmd.Parameters.AddWithValue("$consumed", code.Consumed ? 1 : 0);
            cmd.Parameters.AddWithValue("$sessionId", (object?)code.SessionId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public AuthorizationCode? GetAuthorizationCode(string code)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT code, client_id, did, redirect_uri, scope, code_challenge, code_challenge_method,
                       dpop_jkt, created_at, expires_at, consumed, session_id
                FROM oauth_codes WHERE code = $code;
                """;
            cmd.Parameters.AddWithValue("$code", code);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;
            return new AuthorizationCode(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                ParseIso(reader.GetString(8)), ParseIso(reader.GetString(9)))
            {
                Consumed = reader.GetInt32(10) != 0,
                SessionId = reader.IsDBNull(11) ? null : reader.GetString(11),
            };
        }
    }

    public bool TryConsumeAuthorizationCode(string code, string sessionId)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE oauth_codes SET consumed = 1, session_id = $sessionId
                WHERE code = $code AND consumed = 0;
                """;
            cmd.Parameters.AddWithValue("$code", code);
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public void SaveSession(OAuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO oauth_sessions (session_id, client_id, did, scope, dpop_jkt, created_at, expires_at)
                VALUES ($sessionId, $clientId, $did, $scope, $dpopJkt, $createdAt, $expiresAt)
                ON CONFLICT(session_id) DO UPDATE SET
                    client_id = $clientId, did = $did, scope = $scope, dpop_jkt = $dpopJkt,
                    expires_at = $expiresAt;
                """;
            cmd.Parameters.AddWithValue("$sessionId", session.SessionId);
            cmd.Parameters.AddWithValue("$clientId", session.ClientId);
            cmd.Parameters.AddWithValue("$did", session.Did);
            cmd.Parameters.AddWithValue("$scope", session.Scope);
            cmd.Parameters.AddWithValue("$dpopJkt", session.DpopJkt);
            cmd.Parameters.AddWithValue("$createdAt", Iso(session.CreatedAt));
            cmd.Parameters.AddWithValue("$expiresAt", Iso(session.ExpiresAt));
            cmd.ExecuteNonQuery();
        }
    }

    public OAuthSession? GetSession(string sessionId)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT session_id, client_id, did, scope, dpop_jkt, created_at, expires_at
                FROM oauth_sessions WHERE session_id = $sessionId;
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;
            return new OAuthSession(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), ParseIso(reader.GetString(5)), ParseIso(reader.GetString(6)));
        }
    }

    public void RevokeSession(string sessionId)
    {
        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();
            using (SqliteCommand refresh = _connection.CreateCommand())
            {
                refresh.Transaction = tx;
                refresh.CommandText = "DELETE FROM oauth_refresh WHERE session_id = $sessionId;";
                refresh.Parameters.AddWithValue("$sessionId", sessionId);
                refresh.ExecuteNonQuery();
            }
            using (SqliteCommand session = _connection.CreateCommand())
            {
                session.Transaction = tx;
                session.CommandText = "DELETE FROM oauth_sessions WHERE session_id = $sessionId;";
                session.Parameters.AddWithValue("$sessionId", sessionId);
                session.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void SaveRefreshToken(RefreshTokenEntry token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO oauth_refresh (token, session_id, expires_at, consumed)
                VALUES ($token, $sessionId, $expiresAt, $consumed)
                ON CONFLICT(token) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$token", token.Token);
            cmd.Parameters.AddWithValue("$sessionId", token.SessionId);
            cmd.Parameters.AddWithValue("$expiresAt", Iso(token.ExpiresAt));
            cmd.Parameters.AddWithValue("$consumed", token.Consumed ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public RefreshTokenEntry? GetRefreshToken(string token)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT token, session_id, expires_at, consumed FROM oauth_refresh WHERE token = $token;";
            cmd.Parameters.AddWithValue("$token", token);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;
            return new RefreshTokenEntry(reader.GetString(0), reader.GetString(1), ParseIso(reader.GetString(2)))
            {
                Consumed = reader.GetInt32(3) != 0,
            };
        }
    }

    public bool TryConsumeRefreshToken(string token)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE oauth_refresh SET consumed = 1 WHERE token = $token AND consumed = 0;";
            cmd.Parameters.AddWithValue("$token", token);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public bool TryRegisterJti(string jti, DateTimeOffset expiresAt)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO oauth_jti (jti, expires_at) VALUES ($jti, $expiresAt)
                ON CONFLICT(jti) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$jti", jti);
            cmd.Parameters.AddWithValue("$expiresAt", Iso(expiresAt));
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public void PurgeExpired(DateTimeOffset now)
    {
        lock (_gate)
        {
            string cutoff = Iso(now);
            using SqliteTransaction tx = _connection.BeginTransaction();
            foreach (string table in new[] { "oauth_par", "oauth_codes", "oauth_sessions", "oauth_refresh", "oauth_jti" })
            {
                using SqliteCommand cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {table} WHERE expires_at <= $cutoff;";
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ISO 8601 round-trip in UTC keeps the stored strings lexicographically ordered, so the
    // expires_at <= cutoff comparisons in PurgeExpired are correct as plain string comparisons.
    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
