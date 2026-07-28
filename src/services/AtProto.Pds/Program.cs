using AtProto.Pds;

WebApplication app = PdsHost.Build(args);
app.Run();

/// <summary>Exposed so integration tests can host the PDS in-process.</summary>
public partial class Program;
