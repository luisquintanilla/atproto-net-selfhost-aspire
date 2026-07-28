using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AtProto.AppView;
using AtProto.Pds;

namespace AtProto.Pds.Tests;

/// <summary>
/// The capstone: the whole self-hosted loop wired the way <c>aspire run</c> wires it — our PDS on a
/// real socket, and the REAL AppView ingest (<see cref="FirehoseIngestService"/> → Rx
/// <see cref="PresenceProjection"/> → <see cref="PresenceStore"/>) subscribed to it via an explicit
/// <c>Firehose:Url</c>. Proves a status write on the PDS flows through the firehose and lands on the
/// presence board with its emoji decoded from the commit's CAR slice, latest-wins per account.
/// </summary>
public sealed class SelfHostedLoopTests : IClassFixture<PdsServerFixture>
{
    private const string StatusNsid = "place.selfhost.status";
    private readonly PdsServerFixture _fx;

    public SelfHostedLoopTests(PdsServerFixture fixture) => _fx = fixture;

    [Fact]
    public async Task Status_write_on_the_pds_lands_on_the_appview_board_with_its_emoji()
    {
        string cursorPath = Path.Combine(Path.GetTempPath(), $"appview-cursor-{Guid.NewGuid():N}.txt");
        // Seed the cursor at 0 so the ingest replays the PDS backfill deterministically (no
        // live-tail registration race between StartAsync and the writes below).
        await File.WriteAllTextAsync(cursorPath, "0");
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Firehose:Url"] = _fx.BaseUrl, // http:// — the client normalizes to ws://
                ["AppView:Collection"] = StatusNsid,
                ["Firehose:CursorPath"] = cursorPath,
            })
            .Build();

        var store = new PresenceStore();
        using var projection = new PresenceProjection(store);
        var ingest = new FirehoseIngestService(projection, config, NullLogger<FirehoseIngestService>.Instance);

        await ingest.StartAsync(CancellationToken.None);
        try
        {
            (string did, string jwt) = await CreateAccount("dave.pds.localhost", "hunter2");

            // First status, then an update — the board must reflect the LATEST emoji.
            await PutStatus(jwt, did, "🚀");
            await PutStatus(jwt, did, "🌙");

            PresenceEntry entry = await WaitForStatus(store, did, "🌙");
            Assert.Equal("self", entry.Rkey);
            Assert.Equal("🌙", entry.Status);
            Assert.True(store.UniqueUsers >= 1);
            Assert.True(store.TotalUpdates >= 2);
        }
        finally
        {
            await ingest.StopAsync(CancellationToken.None);
            ingest.Dispose();
            if (File.Exists(cursorPath))
                File.Delete(cursorPath);
        }
    }

    private static async Task<PresenceEntry> WaitForStatus(PresenceStore store, string did, string expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!cts.IsCancellationRequested)
        {
            PresenceEntry? entry = store.Get(did);
            if (entry?.Status == expected)
                return entry;
            await Task.Delay(100, cts.Token);
        }
        throw new Xunit.Sdk.XunitException(
            $"presence board never showed status '{expected}' for {did} (last: {store.Get(did)?.Status ?? "none"})");
    }

    private async Task<(string Did, string AccessJwt)> CreateAccount(string handle, string password)
    {
        HttpResponseMessage resp = await _fx.Http.PostAsJsonAsync(
            $"{_fx.BaseUrl}/xrpc/com.atproto.server.createAccount", new { handle, password });
        resp.EnsureSuccessStatusCode();
        JsonElement account = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (account.GetProperty("did").GetString()!, account.GetProperty("accessJwt").GetString()!);
    }

    private async Task PutStatus(string jwt, string did, string emoji)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_fx.BaseUrl}/xrpc/com.atproto.repo.putRecord");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["repo"] = did,
            ["collection"] = StatusNsid,
            ["rkey"] = "self",
            ["record"] = new Dictionary<string, object?>
            {
                ["$type"] = StatusNsid,
                ["status"] = emoji,
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            },
        });
        (await _fx.Http.SendAsync(req)).EnsureSuccessStatusCode();
    }
}
