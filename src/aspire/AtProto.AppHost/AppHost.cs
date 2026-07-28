var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.AtProto_FirehoseProbe>("firehoseprobe");

// Our self-hosted PDS. It advertises its own address (drives did:web + the service endpoint).
var pds = builder.AddProject<Projects.AtProto_Pds>("pds");
pds.WithEnvironment("Pds__PublicUrl", pds.GetEndpoint("http"));

// AppView presence board over OUR PDS firehose, indexing the demo status collection. Point it at
// the PDS's subscribeRepos endpoint via service discovery (http is normalized to ws by the client).
builder.AddProject<Projects.AtProto_AppView>("appview")
    .WithReference(pds)
    .WaitFor(pds)
    .WithEnvironment("AppView__Collection", "place.selfhost.status")
    .WithEnvironment("Firehose__Url", pds.GetEndpoint("http"));

// Demo traffic: register accounts on the PDS and write live status records so the board populates.
builder.AddProject<Projects.AtProto_StatusSeeder>("statusseeder")
    .WithReference(pds)
    .WaitFor(pds)
    .WithEnvironment("Pds__Url", pds.GetEndpoint("http"));

builder.Build().Run();
