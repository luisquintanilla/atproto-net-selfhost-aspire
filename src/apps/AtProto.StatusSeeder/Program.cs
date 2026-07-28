using AtProto.StatusSeeder;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHostedService<SeederWorker>();

builder.Build().Run();
