var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.AtProto_FirehoseProbe>("firehoseprobe");

// A self-hosted atproto stack, declared with the "native" AtProto.Hosting integration: the PDS
// advertises its own address; the Relay crawls the PDS firehose (WithUpstream); the AppView
// subscribes the Relay's aggregated firehose (WithFirehose) and indexes the demo status collection.
// The env-var wiring contract lives in AtProto.Hosting.Atproto, not here.
var pds = builder.AddAtprotoPds<Projects.AtProto_Pds>("pds");

var relay = builder.AddAtprotoRelay<Projects.AtProto_Relay>("relay")
    .WithUpstream(pds);

builder.AddAtprotoAppView<Projects.AtProto_AppView>("appview")
    .WithFirehose(relay)
    .WithPds(pds)
    .WithCollection("place.selfhost.status");

// Demo traffic: register accounts on the PDS and write live status records so the board populates.
builder.AddProject<Projects.AtProto_StatusSeeder>("statusseeder")
    .WithReference(pds)
    .WaitFor(pds)
    .WithEnvironment("Pds__Url", pds.GetEndpoint("http"));

builder.Build().Run();
