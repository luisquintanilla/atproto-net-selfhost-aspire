using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AtProto.StatusSeeder;

/// <summary>
/// A demo traffic generator for the self-hosted stack: it registers a handful of accounts on our
/// PDS and, on a timer, writes <c>place.selfhost.status</c> records (latest-wins per account) so the
/// AppView presence board shows live, self-authored data flowing PDS → firehose → AppView under
/// <c>aspire run</c>. Not part of the protocol — purely to make the loop visible.
/// </summary>
public sealed class SeederWorker(IConfiguration config, ILogger<SeederWorker> logger) : BackgroundService
{
    private const string StatusNsid = "place.selfhost.status";
    private static readonly string[] Emojis =
        ["😀", "🚀", "🌤", "🌧", "🎧", "📚", "☕", "🛠", "🧪", "🌙", "🔥", "🥑", "🎯", "💤", "🧠"];

    private readonly Random _random = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? pdsUrl = config["Pds:Url"];
        if (string.IsNullOrEmpty(pdsUrl))
        {
            logger.LogWarning("StatusSeeder: no Pds:Url configured; seeder idle.");
            return;
        }

        int accountCount = config.GetValue("Seeder:Accounts", 6);
        var interval = TimeSpan.FromMilliseconds(config.GetValue("Seeder:IntervalMs", 2000));
        using var http = new HttpClient { BaseAddress = new Uri(pdsUrl) };

        await WaitForPds(http, stoppingToken);

        List<Seeded> accounts = await EnsureAccounts(http, accountCount, stoppingToken);
        if (accounts.Count == 0)
        {
            logger.LogWarning("StatusSeeder: could not provision any accounts; seeder idle.");
            return;
        }

        logger.LogInformation("StatusSeeder: seeding {Count} accounts every {Interval}.", accounts.Count, interval);
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            Seeded account = accounts[_random.Next(accounts.Count)];
            string emoji = Emojis[_random.Next(Emojis.Length)];
            try
            {
                await PutStatus(http, account, emoji, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug("StatusSeeder: write failed for {Handle}: {Message}", account.Handle, ex.Message);
            }
        }
    }

    private async Task WaitForPds(HttpClient http, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                HttpResponseMessage resp = await http.GetAsync("/xrpc/com.atproto.server.describeServer", ct);
                if (resp.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // PDS not up yet
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task<List<Seeded>> EnsureAccounts(HttpClient http, int count, CancellationToken ct)
    {
        var accounts = new List<Seeded>();
        for (int i = 1; i <= count; i++)
        {
            string handle = $"demo{i}.pds.localhost";
            const string password = "seed-password";
            try
            {
                Seeded? account = await CreateOrLogin(http, handle, password, ct);
                if (account is not null)
                    accounts.Add(account);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug("StatusSeeder: provisioning {Handle} failed: {Message}", handle, ex.Message);
            }
        }
        return accounts;
    }

    private static async Task<Seeded?> CreateOrLogin(HttpClient http, string handle, string password, CancellationToken ct)
    {
        HttpResponseMessage created = await http.PostAsJsonAsync(
            "/xrpc/com.atproto.server.createAccount", new { handle, password }, ct);
        if (created.IsSuccessStatusCode)
            return await ToSeeded(created, handle, ct);

        HttpResponseMessage session = await http.PostAsJsonAsync(
            "/xrpc/com.atproto.server.createSession", new { identifier = handle, password }, ct);
        return session.IsSuccessStatusCode ? await ToSeeded(session, handle, ct) : null;
    }

    private static async Task<Seeded> ToSeeded(HttpResponseMessage response, string handle, CancellationToken ct)
    {
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Seeded(handle, body.GetProperty("did").GetString()!, body.GetProperty("accessJwt").GetString()!);
    }

    private static async Task PutStatus(HttpClient http, Seeded account, string emoji, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/xrpc/com.atproto.repo.putRecord")
        {
            Content = JsonContent.Create(new Dictionary<string, object?>
            {
                ["repo"] = account.Did,
                ["collection"] = StatusNsid,
                ["rkey"] = "self",
                ["record"] = new Dictionary<string, object?>
                {
                    ["$type"] = StatusNsid,
                    ["status"] = emoji,
                    ["createdAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessJwt);
        (await http.SendAsync(request, ct)).EnsureSuccessStatusCode();
    }

    private sealed record Seeded(string Handle, string Did, string AccessJwt);
}
