var builder = DistributedApplication.CreateBuilder(args);

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

// Demo traffic: register accounts on the PDS and write live status records so the board populates.
builder.AddProject<Projects.AtProto_StatusSeeder>("statusseeder")
    .WithReference(pds)
    .WaitFor(pds)
    .WithEnvironment("Pds__Url", pds.GetEndpoint("http"));

builder.Build().Run();
