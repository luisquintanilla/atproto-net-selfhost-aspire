using AtProto.AppView;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton<PresenceStore>();
builder.Services.AddSingleton<PresenceProjection>();
builder.Services.AddSingleton<FirehoseIngestService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FirehoseIngestService>());

// Live push: SignalR carries the Rx projection's deltas + stats to connected boards.
builder.Services.AddSignalR();
builder.Services.AddHostedService<PresenceBroadcaster>();

var app = builder.Build();

// Serve the presence board (wwwroot/index.html) + the vendored SignalR browser client.
app.UseDefaultFiles();
app.UseStaticFiles();

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
        status = e.Status,
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
        : Results.Ok(new { did = entry.Did, rkey = entry.Rkey, cid = entry.Cid, status = entry.Status, seq = entry.Seq, updatedAt = entry.UpdatedAt });
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

// Live board deltas + stats stream (Rx projection → SignalR → browser).
app.MapHub<PresenceHub>("/hub/presence");

app.Run();
