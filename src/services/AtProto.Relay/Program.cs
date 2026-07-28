using AtProto.Relay;

WebApplication app = RelayHost.Build(args);
app.Run();

/// <summary>Exposed so integration tests can host the Relay in-process.</summary>
public partial class Program;
