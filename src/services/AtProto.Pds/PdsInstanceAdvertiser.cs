using System.Net.Http.Json;
using AtProto.Crypto;
using AtProto.Repo;

namespace AtProto.Pds;

/// <summary>Publishes this PDS's instance record and asks configured relays to crawl it.</summary>
public sealed class PdsInstanceAdvertiser(
    PdsOptions options,
    PdsIdentity identity,
    PdsService pds,
    IHttpClientFactory httpClientFactory,
    ILogger<PdsInstanceAdvertiser> logger) : BackgroundService
{
    private const string InstanceCollection = "place.selfhost.instance";
    private const string InstanceRkey = "self";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.IsNullOrWhiteSpace(options.InstanceName))
            WriteInstanceRecord();

        if (options.AnnounceRelayUrls.Count > 0)
            await AnnounceRelaysAsync(stoppingToken).ConfigureAwait(false);
    }

    private void WriteInstanceRecord()
    {
        Account account = pds.Accounts.ByDid(identity.ServiceDid) ?? CreateServiceAccount();
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["$type"] = InstanceCollection,
            ["name"] = options.InstanceName!.Trim(),
            ["pds"] = identity.PublicUrl.ToString().TrimEnd('/'),
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        if (!string.IsNullOrWhiteSpace(options.InstanceDescription))
            record["description"] = options.InstanceDescription.Trim();
        if (!string.IsNullOrWhiteSpace(options.InstanceRelayUrl))
            record["relay"] = options.InstanceRelayUrl.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(options.InstanceAppViewUrl))
            record["appview"] = options.InstanceAppViewUrl.TrimEnd('/');

        pds.Commit(account, [RepoWrite.Update(InstanceCollection, InstanceRkey, record)]);
        logger.LogInformation("pds: advertised instance {Name} as {Did}", options.InstanceName, identity.ServiceDid);
    }

    private Account CreateServiceAccount()
    {
        var key = EcKeypair.Generate(EcKeyType.Secp256k1);
        RepoStore repo = RepoStore.CreateEmpty(key, identity.ServiceDid);
        (byte[] salt, byte[] hash) = AccountStore.HashPassword(Guid.NewGuid().ToString("N"));
        var account = new Account
        {
            Did = identity.ServiceDid,
            Handle = identity.PublicUrl.Host,
            AccountId = "self",
            SigningKey = key,
            Repo = repo,
            PasswordSalt = salt,
            PasswordHash = hash,
        };
        pds.Accounts.Add(account);
        return account;
    }

    private async Task AnnounceRelaysAsync(CancellationToken ct)
    {
        HttpClient http = httpClientFactory.CreateClient();
        string hostname = identity.PublicUrl.ToString().TrimEnd('/');
        foreach (string relay in options.AnnounceRelayUrls.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            string url = relay.TrimEnd('/') + "/xrpc/com.atproto.sync.requestCrawl";
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    HttpResponseMessage response = await http.PostAsJsonAsync(url, new { hostname }, ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        logger.LogInformation("pds: announced {Hostname} to relay {Relay}", hostname, relay);
                        break;
                    }
                    logger.LogDebug("pds: relay announce to {Relay} returned {Status}", relay, response.StatusCode);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    if (ct.IsCancellationRequested)
                        throw;
                    logger.LogDebug("pds: relay announce attempt {Attempt} to {Relay} failed: {Message}", attempt, relay, ex.Message);
                }

                if (attempt < 5)
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
            }
        }
    }
}
