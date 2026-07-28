var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.AtProto_FirehoseProbe>("firehoseprobe");

// Our self-hosted PDS. It advertises its own address (drives did:web + the service endpoint).
var pds = builder.AddProject<Projects.AtProto_Pds>("pds");
pds.WithEnvironment("Pds__PublicUrl", pds.GetEndpoint("http"));

// Our self-hosted Relay. It crawls the PDS firehose, assigns a global seq, and re-emits an
// aggregated subscribeRepos stream. Upstreams are injected via config (auto-crawl on startup).
var relay = builder.AddProject<Projects.AtProto_Relay>("relay")
    .WithReference(pds)
    .WaitFor(pds)
    .WithEnvironment("Relay__Upstreams__0", pds.GetEndpoint("http"));

// AppView presence board over OUR Relay firehose, indexing the demo status collection. Point it at
// the Relay's subscribeRepos endpoint via service discovery (http is normalized to ws by the client).
builder.AddProject<Projects.AtProto_AppView>("appview")
    .WithReference(relay)
    .WaitFor(relay)
    .WithEnvironment("AppView__Collection", "place.selfhost.status")
    .WithEnvironment("Firehose__Url", relay.GetEndpoint("http"));

// Demo traffic: register accounts on the PDS and write live status records so the board populates.
builder.AddProject<Projects.AtProto_StatusSeeder>("statusseeder")
    .WithReference(pds)
    .WaitFor(pds)
    .WithEnvironment("Pds__Url", pds.GetEndpoint("http"));

builder.Build().Run();
