using System.Net.WebSockets;
using AtProto.Firehose;

namespace AtProto.Relay;

/// <summary>
/// Composes the Relay ASP.NET application: the aggregated <c>subscribeRepos</c> firehose plus the
/// crawl/administration surface (<c>requestCrawl</c>, <c>listHosts</c>, <c>getRepoStatus</c>, and a
/// <c>getRepo</c> redirect back to the source PDS). Kept separate from <c>Program</c> so tests can
/// build and host the same app in-process.
/// </summary>
public static class RelayHost
{
    public static WebApplication Build(string[] args, Action<RelayOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        var options = new RelayOptions();
        builder.Configuration.GetSection("Relay").Bind(options);
        configure?.Invoke(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<HostRegistry>();
        builder.Services.AddSingleton<RelayService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RelayService>());

        WebApplication app = builder.Build();
        app.UseWebSockets();
        app.MapDefaultEndpoints();
        MapSync(app);
        return app;
    }

    private static void MapSync(WebApplication app)
    {
        // Aggregated firehose: replay from ?cursor then stream live (global seq).
        app.Map("/xrpc/com.atproto.sync.subscribeRepos", async (HttpContext ctx, RelayService relay) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                return;
            }

            long? cursor = long.TryParse(ctx.Request.Query["cursor"], out long c) ? c : null;
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
            try
            {
                await foreach (SequencedFrame frame in relay.Firehose.Subscribe(cursor, ctx.RequestAborted))
                    await socket.SendAsync(frame.Frame, WebSocketMessageType.Binary, endOfMessage: true, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // client disconnected
            }
        });

        // Ask the relay to start crawling a host's firehose.
        app.MapPost("/xrpc/com.atproto.sync.requestCrawl", (RequestCrawlBody body, RelayService relay) =>
        {
            if (string.IsNullOrWhiteSpace(body.Hostname))
                return Results.BadRequest(new { error = "InvalidRequest", message = "hostname is required" });
            relay.Crawl(body.Hostname);
            return Results.Ok();
        });

        // The hosts this relay is crawling and their live status.
        app.MapGet("/xrpc/com.atproto.sync.listHosts", (HostRegistry registry) => Results.Ok(new
        {
            hosts = registry.Hosts.Select(h => new
            {
                hostname = h.Url,
                status = h.Status,
                lastUpstreamSeq = h.LastUpstreamSeq,
                lastError = h.LastError,
                connectedAt = h.ConnectedAt,
            }),
        }));

        // Latest known state for a repo (which host is authoritative, latest rev/seq).
        app.MapGet("/xrpc/com.atproto.sync.getRepoStatus", (string did, HostRegistry registry) =>
        {
            RepoState? repo = registry.GetRepo(did);
            return repo is null
                ? Results.NotFound(new { error = "RepoNotFound", message = $"unknown repo {did}" })
                : Results.Ok(new { did = repo.Did, active = repo.Active, rev = repo.Rev });
        });

        // The relay doesn't store repos — redirect getRepo to the authoritative source PDS.
        app.MapGet("/xrpc/com.atproto.sync.getRepo", (string did, HostRegistry registry) =>
        {
            RepoState? repo = registry.GetRepo(did);
            if (repo is null)
                return Results.NotFound(new { error = "RepoNotFound", message = $"unknown repo {did}" });
            string target = $"{repo.Host.TrimEnd('/')}/xrpc/com.atproto.sync.getRepo?did={Uri.EscapeDataString(did)}";
            return Results.Redirect(target);
        });
    }

    internal sealed record RequestCrawlBody(string Hostname);
}
