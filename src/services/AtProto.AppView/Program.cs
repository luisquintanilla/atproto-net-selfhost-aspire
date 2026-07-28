using AtProto.AppView;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton<PresenceStore>();
builder.Services.AddSingleton<PresenceProjection>();
builder.Services.AddSingleton<FirehoseIngestService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FirehoseIngestService>());

var app = builder.Build();

app.MapDefaultEndpoints();

const string StatusNsid = "place.selfhost.status";

// XRPC query: latest status per account, most recent first.
app.MapGet("/xrpc/place.selfhost.getPresence", (PresenceStore store, int? limit) =>
{
    int take = Math.Clamp(limit ?? 50, 1, 500);
    var entries = store.Snapshot(take).Select(e => new
    {
        did = e.Did,
        rkey = e.Rkey,
        cid = e.Cid,
        seq = e.Seq,
        updatedAt = e.UpdatedAt,
    });
    return Results.Ok(new { collection = StatusNsid, count = store.UniqueUsers, presence = entries });
});

// XRPC query: a single account's latest status.
app.MapGet("/xrpc/place.selfhost.getStatus", (PresenceStore store, string did) =>
{
    PresenceEntry? entry = store.Get(did);
    return entry is null
        ? Results.NotFound(new { error = "NotFound", message = $"no status for {did}" })
        : Results.Ok(new { did = entry.Did, rkey = entry.Rkey, cid = entry.Cid, seq = entry.Seq, updatedAt = entry.UpdatedAt });
});

// Live board statistics (from the Rx windowed projection).
app.MapGet("/xrpc/place.selfhost.getStats", (PresenceProjection projection) =>
{
    BoardStats s = projection.Current;
    return Results.Ok(new
    {
        totalUpdates = s.TotalUpdates,
        uniqueUsers = s.UniqueUsers,
        updatesPerSecond = s.UpdatesPerSecond,
        lastSeq = s.LastSeq,
        at = s.At,
    });
});

app.MapGet("/", () => Results.Content(BoardPage.Html, "text/html"));

app.Run();

internal static class BoardPage
{
    public const string Html = """
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <title>atproto self-host — presence board</title>
      <style>
        body { font: 14px system-ui, sans-serif; margin: 2rem; color: #111; }
        h1 { font-size: 1.2rem; }
        .stats { color: #555; margin-bottom: 1rem; }
        table { border-collapse: collapse; width: 100%; max-width: 900px; }
        th, td { text-align: left; padding: 4px 8px; border-bottom: 1px solid #eee; font-variant-numeric: tabular-nums; }
        code { font-size: 12px; }
      </style>
    </head>
    <body>
      <h1>presence board <small>(self-hosted atproto AppView)</small></h1>
      <div class="stats" id="stats">connecting…</div>
      <table>
        <thead><tr><th>#</th><th>did</th><th>rkey</th><th>seq</th><th>updated</th></tr></thead>
        <tbody id="rows"></tbody>
      </table>
      <script>
        async function tick() {
          try {
            const [p, s] = await Promise.all([
              fetch('/xrpc/place.selfhost.getPresence?limit=25').then(r => r.json()),
              fetch('/xrpc/place.selfhost.getStats').then(r => r.json()),
            ]);
            document.getElementById('stats').textContent =
              `${s.uniqueUsers} users · ${s.totalUpdates} updates · ${s.updatesPerSecond}/s · seq ${s.lastSeq}`;
            document.getElementById('rows').innerHTML = p.presence.map((e, i) =>
              `<tr><td>${i + 1}</td><td><code>${e.did}</code></td><td><code>${e.rkey}</code></td>` +
              `<td>${e.seq}</td><td>${new Date(e.updatedAt).toLocaleTimeString()}</td></tr>`).join('');
          } catch (e) { /* transient */ }
        }
        tick(); setInterval(tick, 1000);
      </script>
    </body>
    </html>
    """;
}
