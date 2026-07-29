using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AtProto.AppView;
using AtProto.Relay;

namespace AtProto.Pds.Tests;

/// <summary>
/// M4 proof: the full <b>PDS → Relay → AppView</b> loop in-process. Our PDS on a real socket, our
/// Relay crawling it (re-encoding each commit with a fresh <i>global</i> seq), and the REAL AppView
/// ingest subscribed to the <b>relay</b> (not the PDS). A status write on the PDS must flow through
/// the relay and land on the presence board with its emoji, latest-wins — and the relay must have
/// tracked the repo (so <c>getRepoStatus</c>/<c>listHosts</c> report it). No mocks, no test-host.
/// </summary>
public sealed class RelayLoopTests : IClassFixture<PdsServerFixture>
{
    private const string StatusNsid = "place.selfhost.status";
    private readonly PdsServerFixture _fx;

    public RelayLoopTests(PdsServerFixture fixture) => _fx = fixture;

    [Fact]
    public async Task Status_write_flows_pds_through_relay_to_the_appview_board()
    {
        string cursorDir = Path.Combine(Path.GetTempPath(), $"relay-{Guid.NewGuid():N}");
        string appviewCursor = Path.Combine(Path.GetTempPath(), $"appview-cursor-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(appviewCursor, "0");

        // Our Relay, crawling the PDS as an upstream, on its own loopback port.
        string relayUrl = $"http://127.0.0.1:{FreePort()}";
        WebApplication relay = RelayHost.Build(Array.Empty<string>(), o =>
        {
            o.PublicUrl = relayUrl;
            o.CursorDir = cursorDir;
            o.Upstreams.Add(_fx.BaseUrl);
        });
        relay.Urls.Clear();
        relay.Urls.Add(relayUrl);
        await relay.StartAsync();

        // The REAL AppView ingest, subscribed to the RELAY's aggregated firehose.
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Firehose:Url"] = relayUrl, // http:// — the client normalizes to ws://
                ["AppView:Collection"] = StatusNsid,
                ["Firehose:CursorPath"] = appviewCursor,
            })
            .Build();

        var store = new PresenceStore();
        using var projection = new PresenceProjection(store);
        var ingest = new FirehoseIngestService(projection, config, NullLogger<FirehoseIngestService>.Instance);
        await ingest.StartAsync(CancellationToken.None);

        try
        {
            (string did, string jwt) = await CreateAccount("erin.pds.localhost", "hunter2");
            await PutStatus(jwt, did, "🛰️");
            await PutStatus(jwt, did, "🌌");

            PresenceEntry entry = await WaitForStatus(store, did, "🌌");
            Assert.Equal("self", entry.Rkey);
            Assert.Equal("🌌", entry.Status);

            // The relay itself must have tracked the repo (proves it's really in the path, and the
            // did→host map that backs getRepo redirects is populated).
            using var http = new HttpClient();
            JsonElement status = await http.GetFromJsonAsync<JsonElement>(
                $"{relayUrl}/xrpc/com.atproto.sync.getRepoStatus?did={Uri.EscapeDataString(did)}");
            Assert.Equal(did, status.GetProperty("did").GetString());
            Assert.True(status.GetProperty("active").GetBoolean());

            JsonElement hosts = await http.GetFromJsonAsync<JsonElement>(
                $"{relayUrl}/xrpc/com.atproto.sync.listHosts");
            Assert.Contains(
                hosts.GetProperty("hosts").EnumerateArray(),
                h => h.GetProperty("hostname").GetString() == _fx.BaseUrl);
        }
        finally
        {
            await ingest.StopAsync(CancellationToken.None);
            ingest.Dispose();
            await relay.StopAsync();
            await relay.DisposeAsync();
            if (File.Exists(appviewCursor)) File.Delete(appviewCursor);
            if (Directory.Exists(cursorDir)) Directory.Delete(cursorDir, recursive: true);
        }
    }

    private static async Task<PresenceEntry> WaitForStatus(PresenceStore store, string did, string expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!cts.IsCancellationRequested)
        {
            PresenceEntry? entry = store.Get(did);
            if (entry?.Status == expected)
                return entry;
            await Task.Delay(100, cts.Token);
        }
        throw new Xunit.Sdk.XunitException(
            $"presence board never showed '{expected}' for {did} (last: {store.Get(did)?.Status ?? "none"})");
    }

    private async Task<(string Did, string AccessJwt)> CreateAccount(string handle, string password)
    {
        HttpResponseMessage resp = await _fx.Http.PostAsJsonAsync(
            $"{_fx.BaseUrl}/xrpc/com.atproto.server.createAccount", new { handle, password });
        resp.EnsureSuccessStatusCode();
        JsonElement account = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (account.GetProperty("did").GetString()!, account.GetProperty("accessJwt").GetString()!);
    }

    [Fact]
    public async Task Relay_resumes_global_seq_and_host_cursor_after_restart()
    {
        string cursorDir = Path.Combine(Path.GetTempPath(), $"relay-restart-{Guid.NewGuid():N}");
        try
        {
            (string did, string jwt) = await CreateAccount("frank.pds.localhost", "hunter2");
            await PutStatus(jwt, did, "🌞"); // one commit exists before any relay starts

            // Relay #1: crawls the PDS from 0 (backfill), assigns global seqs, persists cursors.
            long seqAfterFirst;
            string relay1Url = $"http://127.0.0.1:{FreePort()}";
            WebApplication relay1 = BuildRelay(relay1Url, cursorDir);
            await relay1.StartAsync();
            try
            {
                var rs1 = relay1.Services.GetRequiredService<RelayService>();
                // Wait for the backfill to fully drain (seq stops advancing), so seqAfterFirst is the
                // stable high-water mark that gets persisted - not a mid-backfill snapshot that would
                // race the cursor file below.
                seqAfterFirst = await WaitForStableSeq(rs1);
            }
            finally
            {
                await relay1.StopAsync();
                await relay1.DisposeAsync();
            }

            // Cursors persisted: the global seq equals what relay #1 reached; a per-host cursor exists.
            string globalSeqFile = Path.Combine(cursorDir, "global-seq.txt");
            Assert.True(File.Exists(globalSeqFile));
            Assert.Equal(seqAfterFirst, long.Parse((await File.ReadAllTextAsync(globalSeqFile)).Trim()));
            Assert.NotEmpty(Directory.GetFiles(cursorDir, "host-*.txt"));

            // Relay #2: same CursorDir. Must resume the global seq (NOT reset to 0) and the per-host
            // upstream cursor (so it does NOT reprocess the already-relayed commit).
            string relay2Url = $"http://127.0.0.1:{FreePort()}";
            WebApplication relay2 = BuildRelay(relay2Url, cursorDir);
            await relay2.StartAsync();
            try
            {
                var rs2 = relay2.Services.GetRequiredService<RelayService>();
                // The broadcaster is seeded from the persisted global seq at construction, so the
                // resumed relay continues the sequence instead of resetting to 0. Re-emit is
                // at-least-once by design (see RelayService): after a restart the in-memory
                // rev-monotonic dedupe is empty, so the resumed crawl may replay the single boundary
                // commit, which downstream latest-wins consumers absorb. The durable invariant is a
                // bounded resume, not exactly-once - the seq never regresses below what relay #1
                // persisted and never re-backfills from scratch.
                await Task.Delay(1000); // let any at-least-once boundary re-emit settle
                long afterResume = rs2.Firehose.CurrentSeq;
                Assert.True(afterResume >= seqAfterFirst,
                    $"resumed relay regressed below the persisted seq (reset instead of resume): {afterResume} < {seqAfterFirst}");
                Assert.True(afterResume <= seqAfterFirst + 1,
                    $"resumed relay re-backfilled from scratch instead of resuming its cursor: {afterResume} > {seqAfterFirst} + 1");

                // A NEW write advances the global seq monotonically past where relay #1 left off
                // (no gap, no reset).
                await PutStatus(jwt, did, "🌚");
                long seqAfterSecond = await WaitForSeq(rs2, atLeast: afterResume + 1);
                Assert.True(seqAfterSecond > seqAfterFirst,
                    $"a post-restart write did not advance the resumed global seq: {seqAfterSecond} <= {seqAfterFirst}");
            }
            finally
            {
                await relay2.StopAsync();
                await relay2.DisposeAsync();
            }
        }
        finally
        {
            if (Directory.Exists(cursorDir)) Directory.Delete(cursorDir, recursive: true);
        }
    }

    private WebApplication BuildRelay(string url, string cursorDir)
    {
        WebApplication relay = RelayHost.Build(Array.Empty<string>(), o =>
        {
            o.PublicUrl = url;
            o.CursorDir = cursorDir;
            o.Upstreams.Add(_fx.BaseUrl);
        });
        relay.Urls.Clear();
        relay.Urls.Add(url);
        return relay;
    }

    private static async Task<long> WaitForSeq(RelayService relay, long atLeast)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!cts.IsCancellationRequested)
        {
            long seq = relay.Firehose.CurrentSeq;
            if (seq >= atLeast)
                return seq;
            await Task.Delay(50, cts.Token);
        }
        throw new Xunit.Sdk.XunitException($"relay global seq never reached {atLeast} (last: {relay.Firehose.CurrentSeq})");
    }

    /// <summary>
    /// Wait until the relay's global seq has advanced at least once and then held steady (the
    /// backfill drained), returning the settled high-water mark. This makes the persisted
    /// <c>global-seq.txt</c> deterministic - a plain "at least 1" wait can snapshot a mid-backfill
    /// value that the still-running crawl then races past before shutdown.
    /// </summary>
    private static async Task<long> WaitForStableSeq(RelayService relay)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        long last = -1;
        int stable = 0;
        while (!cts.IsCancellationRequested)
        {
            long seq = relay.Firehose.CurrentSeq;
            if (seq >= 1 && seq == last)
            {
                if (++stable >= 4) // ~400ms with no advance => backfill drained
                    return seq;
            }
            else
            {
                stable = 0;
                last = seq;
            }
            await Task.Delay(100, cts.Token);
        }
        throw new Xunit.Sdk.XunitException($"relay global seq never settled (last: {relay.Firehose.CurrentSeq})");
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

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
