var builder = DistributedApplication.CreateBuilder(args);

// Two profiles, in-memory stays the default (see docs/storage.md):
//   dev (default)        - in-memory PDS stores, presence board only. Zero-config, nothing on disk,
//                          no native deps to run the simple path. This is what `aspire run` gives.
//   production           - durable SQLite-backed PDS (accounts/repos/blobs survive a restart) plus
//                          the DuckDB analytics AppView. Opt in with ATPROTO_PROFILE=production.
// Nothing is removed; production layers durability + a second projection onto the same topology.
string profile = (builder.Configuration["ATPROTO_PROFILE"] ?? "dev").Trim().ToLowerInvariant();
bool production = profile is "production" or "prod";

builder.AddProject<Projects.AtProto_FirehoseProbe>("firehoseprobe");

// A self-hosted atproto stack, declared with the "native" AtProto.Hosting integration: each PDS
// advertises its own address; the Relay crawls both PDS firehoses (WithUpstream); the AppView
// subscribes the Relay's aggregated firehose (WithFirehose), indexes the demo status collection,
// and exposes a directory from the PDS instance advertisements.
// The env-var wiring contract lives in AtProto.Hosting.Atproto, not here.
var pds = builder.AddAtprotoPds<Projects.AtProto_Pds>("pds")
    .WithInstance("Evil Atproto Alpha", "A tiny self-hosted PDS advertising itself through the federation demo.");

var relay = builder.AddAtprotoRelay<Projects.AtProto_Relay>("relay")
    .WithUpstream(pds, 0);

var pdsB = builder.AddAtprotoPds<Projects.AtProto_Pds>("pds-b")
    .WithInstance("Evil Atproto Beta", "A second small self-hosted PDS discovered through the shared relay.");

relay.WithUpstream(pdsB, 1);

var appview = builder.AddAtprotoAppView<Projects.AtProto_AppView>("appview")
    .WithFirehose(relay)
    .WithPds(pds)
    .WithDirectoryRelay(relay)
    .WithCollection("place.selfhost.status");

pds.WithInstanceEndpoints(relay, appview);
pdsB.WithInstanceEndpoints(relay, appview);

if (production)
{
    // Durable PDS storage: each PDS writes accounts/repos/blobs through to its own SQLite file, so
    // state survives a restart. Paths are per-instance and stable across runs.
    string dataRoot = Path.Combine(AppContext.BaseDirectory, "atproto-data");
    pds.WithSqliteStorage(Path.Combine(dataRoot, "pds", "pds.db"));
    pdsB.WithSqliteStorage(Path.Combine(dataRoot, "pds-b", "pds-b.db"));

    // A second projection over the same firehose: the DuckDB analytics view (columnar OLAP over
    // every op from every collection - aggregates the latest-wins presence board can't answer). The
    // DuckDB file path is injected as the `analytics` connection string; the view opens the embedded
    // database in-process (DuckDB.NET.Data.Full ships the native lib), so no extra Aspire resource
    // is needed and the whole stack stays on one Aspire version.
    string analyticsDb = Path.Combine(dataRoot, "analytics", "analytics.duckdb");
    builder.AddAtprotoAppView<Projects.AtProto_AnalyticsView>("analyticsview")
        .WithFirehose(relay)
        .WithEnvironment("ConnectionStrings__analytics", $"DataSource={analyticsDb}");
}

// Demo traffic: register accounts on the PDS and write live status records so the board populates.
builder.AddProject<Projects.AtProto_StatusSeeder>("statusseeder")
    .WithReference(pds)
    .WaitFor(pds)
    .WithEnvironment("Pds__Url", pds.GetEndpoint("http"));

builder.Build().Run();
