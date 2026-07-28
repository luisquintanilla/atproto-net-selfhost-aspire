var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.AtProto_FirehoseProbe>("firehoseprobe");

// AppView presence board over the PUBLIC relay firehose (zero credentials). Default the projection
// to app.bsky.feed.post so the live board is populated for the M2 demo; override AppView__Collection
// to place.selfhost.status once our own PDS (M3) writes real status records.
builder.AddProject<Projects.AtProto_AppView>("appview")
    .WithEnvironment("AppView__Collection", "app.bsky.feed.post");

builder.Build().Run();
