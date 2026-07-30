using AtProto.Pds;

namespace AtProto.Pds.Tests;

// One suite of behavioural tests for the IOAuthStore seam, run against both the in-memory (dev) and
// SQLite (production) implementations so the two stay behaviourally identical. Covers round-trips, the
// atomic single-use consume gates (codes + refresh tokens), reuse detection, session revocation
// cascading to refresh tokens, DPoP jti replay rejection, and expiry purging.
public abstract class OAuthStoreTestsBase : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    protected abstract IOAuthStore Store { get; }

    private static ParRequest Par(string requestUri = "urn:req-1", string jkt = "jkt-a") => new(
        requestUri, "https://app.example.com/client-metadata.json", "code",
        "https://app.example.com/cb", "atproto transition:generic", "challenge", "S256",
        "state-1", jkt, "alice.pds.localhost", Now, Now.AddMinutes(5));

    private static AuthorizationCode Code(string code = "cod-1", string jkt = "jkt-a") => new(
        code, "https://app.example.com/client-metadata.json", "did:web:pds%3A5100:pds:alice",
        "https://app.example.com/cb", "atproto", "challenge", "S256", jkt, Now, Now.AddMinutes(5));

    private static OAuthSession Session(string id = "ses-1", string jkt = "jkt-a") => new(
        id, "https://app.example.com/client-metadata.json", "did:web:pds%3A5100:pds:alice",
        "atproto", jkt, Now, Now.AddDays(14));

    private static RefreshTokenEntry Refresh(string token = "ref-1", string sessionId = "ses-1") =>
        new(token, sessionId, Now.AddDays(14));

    [Fact]
    public void ParRequest_RoundTripsAndDeletes()
    {
        ParRequest par = Par();
        Store.SaveParRequest(par);

        ParRequest? loaded = Store.GetParRequest(par.RequestUri);
        Assert.Equal(par, loaded);

        Store.DeleteParRequest(par.RequestUri);
        Assert.Null(Store.GetParRequest(par.RequestUri));
    }

    [Fact]
    public void GetMissing_ReturnsNull()
    {
        Assert.Null(Store.GetParRequest("urn:nope"));
        Assert.Null(Store.GetAuthorizationCode("cod-nope"));
        Assert.Null(Store.GetSession("ses-nope"));
        Assert.Null(Store.GetRefreshToken("ref-nope"));
    }

    [Fact]
    public void AuthorizationCode_IsSingleUse()
    {
        AuthorizationCode code = Code();
        Store.SaveAuthorizationCode(code);

        AuthorizationCode? loaded = Store.GetAuthorizationCode(code.Code);
        Assert.NotNull(loaded);
        Assert.False(loaded!.Consumed);

        Assert.True(Store.TryConsumeAuthorizationCode(code.Code, "ses-1"));
        Assert.False(Store.TryConsumeAuthorizationCode(code.Code, "ses-2"));

        AuthorizationCode? after = Store.GetAuthorizationCode(code.Code);
        Assert.True(after!.Consumed);
        Assert.Equal("ses-1", after.SessionId); // remembers the first session for reuse revocation
    }

    [Fact]
    public void RefreshToken_IsSingleUse()
    {
        RefreshTokenEntry token = Refresh();
        Store.SaveRefreshToken(token);

        Assert.True(Store.TryConsumeRefreshToken(token.Token));
        Assert.False(Store.TryConsumeRefreshToken(token.Token));
        Assert.True(Store.GetRefreshToken(token.Token)!.Consumed);
    }

    [Fact]
    public void RevokeSession_RemovesSessionAndItsRefreshTokens()
    {
        Store.SaveSession(Session("ses-1"));
        Store.SaveRefreshToken(Refresh("ref-1", "ses-1"));
        Store.SaveRefreshToken(Refresh("ref-2", "ses-1"));
        Store.SaveRefreshToken(Refresh("ref-other", "ses-2"));

        Store.RevokeSession("ses-1");

        Assert.Null(Store.GetSession("ses-1"));
        Assert.Null(Store.GetRefreshToken("ref-1"));
        Assert.Null(Store.GetRefreshToken("ref-2"));
        Assert.NotNull(Store.GetRefreshToken("ref-other")); // a different session is untouched
    }

    [Fact]
    public void Jti_IsRegisteredOnceThenRejected()
    {
        Assert.True(Store.TryRegisterJti("jti-1", Now.AddMinutes(5)));
        Assert.False(Store.TryRegisterJti("jti-1", Now.AddMinutes(5)));
        Assert.True(Store.TryRegisterJti("jti-2", Now.AddMinutes(5)));
    }

    [Fact]
    public void PurgeExpired_DropsOnlyExpiredEntries()
    {
        Store.SaveParRequest(Par("urn:expired") with { ExpiresAt = Now.AddMinutes(-1) });
        Store.SaveParRequest(Par("urn:live") with { ExpiresAt = Now.AddMinutes(5) });
        Store.SaveAuthorizationCode(Code("cod-expired") with { ExpiresAt = Now.AddMinutes(-1) });
        Store.SaveSession(Session("ses-live"));
        Store.SaveRefreshToken(Refresh("ref-expired", "ses-live") with { ExpiresAt = Now.AddMinutes(-1) });
        Store.TryRegisterJti("jti-expired", Now.AddMinutes(-1));
        Store.TryRegisterJti("jti-live", Now.AddMinutes(5));

        Store.PurgeExpired(Now);

        Assert.Null(Store.GetParRequest("urn:expired"));
        Assert.NotNull(Store.GetParRequest("urn:live"));
        Assert.Null(Store.GetAuthorizationCode("cod-expired"));
        Assert.NotNull(Store.GetSession("ses-live"));
        Assert.Null(Store.GetRefreshToken("ref-expired"));
        // The purged jti can be registered again; the live one still cannot.
        Assert.True(Store.TryRegisterJti("jti-expired", Now.AddMinutes(5)));
        Assert.False(Store.TryRegisterJti("jti-live", Now.AddMinutes(5)));
    }

    [Fact]
    public void Timestamps_SurviveRoundTrip()
    {
        ParRequest par = Par("urn:ts");
        Store.SaveParRequest(par);
        ParRequest loaded = Store.GetParRequest("urn:ts")!;
        Assert.Equal(par.CreatedAt, loaded.CreatedAt);
        Assert.Equal(par.ExpiresAt, loaded.ExpiresAt);
    }

    public virtual void Dispose() => GC.SuppressFinalize(this);
}

public sealed class InMemoryOAuthStoreTests : OAuthStoreTestsBase
{
    protected override IOAuthStore Store { get; } = new InMemoryOAuthStore();
}

public sealed class SqliteOAuthStoreTests : OAuthStoreTestsBase
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atproto-oauth-store-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteOAuthStore _store;

    public SqliteOAuthStoreTests() => _store = new SqliteOAuthStore(Path.Combine(_dir, "oauth.db"));

    protected override IOAuthStore Store => _store;

    public override void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        base.Dispose();
    }
}
