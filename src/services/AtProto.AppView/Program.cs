using AtProto.AppView;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton<PresenceStore>();
builder.Services.AddSingleton<PresenceProjection>();
builder.Services.AddSingleton<FirehoseIngestService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FirehoseIngestService>());

// Read-through inspection + compose: the AppView resolves a DID to its PDS and proxies the real
// repo reads/writes single-origin (browser needs no CORS/creds). Singletons because ComposeService
// caches a session; all share one HttpClient factory.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<PdsResolver>();
builder.Services.AddSingleton<InspectService>();
builder.Services.AddSingleton<ComposeService>();
builder.Services.AddSingleton<DirectoryService>();

// Live push: SignalR carries the Rx projection's deltas + stats to connected boards.
builder.Services.AddSignalR();
builder.Services.AddHostedService<PresenceBroadcaster>();

var app = builder.Build();

// Serve the presence board (wwwroot/index.html) + the vendored SignalR browser client.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapDefaultEndpoints();

const string StatusNsid = "place.selfhost.status";

// Directory: relay host list + each PDS's place.selfhost.instance/self record.
app.MapGet("/directory", async (DirectoryService directory, CancellationToken ct) =>
{
    if (!directory.Enabled)
        return Results.Problem("Directory is unavailable: no relay configured (AppView:RelayUrl or Firehose:Url).", statusCode: 503);
    IReadOnlyList<DirectoryInstance> instances = await directory.ListAsync(ct);
    return Results.Ok(new { count = instances.Count, instances });
});

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

// --- Inspection: read the real records behind a board row (single-origin PDS proxy) ---

// One record's full value + CID + AT-URI.
app.MapGet("/inspect/record", async (InspectService inspect, string did, string? collection, string? rkey, CancellationToken ct) =>
{
    if (!inspect.Enabled)
        return Results.Problem("Inspection is unavailable: no PDS configured (AppView:PdsUrl).", statusCode: 503);
    return await inspect.GetRecordAsync(did, collection, rkey, ct);
});

// A repo overview: identity/DID document, latest signed commit, and the account's records.
app.MapGet("/inspect/repo", async (InspectService inspect, string did, CancellationToken ct) =>
{
    if (!inspect.Enabled)
        return Results.Problem("Inspection is unavailable: no PDS configured (AppView:PdsUrl).", statusCode: 503);
    return await inspect.GetRepoAsync(did, ct);
});

// The whole repository as a CARv1 download.
app.MapGet("/inspect/car", async (InspectService inspect, string did, CancellationToken ct) =>
{
    if (!inspect.Enabled)
        return Results.Problem("Inspection is unavailable: no PDS configured (AppView:PdsUrl).", statusCode: 503);
    return await inspect.GetCarAsync(did, ct);
});

// --- Compose: write "your" status to the PDS and watch it flow back to the board ---
app.MapPost("/compose", async (ComposeService compose, ComposeRequest request, CancellationToken ct) =>
{
    if (!compose.Enabled)
        return Results.Problem("Compose is unavailable: no PDS configured (AppView:PdsUrl).", statusCode: 503);
    if (string.IsNullOrWhiteSpace(request.Status))
        return Results.BadRequest(new { error = "InvalidRequest", message = "status is required" });

    ComposeResult result = await compose.SetStatusAsync(request.Status.Trim(), ct);
    return Results.Ok(new { uri = result.Uri, cid = result.Cid, did = result.Did, handle = result.Handle, status = result.Status });
});

// Live board deltas + stats stream (Rx projection → SignalR → browser).
app.MapHub<PresenceHub>("/hub/presence");

app.Run();
