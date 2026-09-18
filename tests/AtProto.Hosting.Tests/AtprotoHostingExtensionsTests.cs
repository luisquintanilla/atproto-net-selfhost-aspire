using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace AtProto.Hosting.Tests;

/// <summary>
/// M5 proof: the "native" Aspire hosting integration wires a self-hosted atproto stack's topology
/// correctly. Builds the application model with the semantic extensions (over fake project metadata,
/// so nothing is launched) and asserts the topology the AppHost declares: the PDS advertises
/// its address, the Relay crawls the PDS, and the AppView subscribes the Relay and indexes a chosen
/// collection — including the exact env-var contract the services read and the startup ordering.
/// </summary>
public sealed class AtprotoHostingExtensionsTests
{
    // Aspire validates the project file exists at Build(). A tiny real temp file satisfies that;
    // nothing is launched (we only inspect the model).
    private static readonly string FakeProjectPath = CreateFakeProjectFile();

    private static string CreateFakeProjectFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"atproto-fake-{Guid.NewGuid():N}.csproj");
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return path;
    }

    private sealed class FakeProject : IProjectMetadata
    {
        public string ProjectPath => FakeProjectPath;
    }

    [Fact]
    public async Task Declares_pds_relay_appview_with_the_wiring_contract()
    {
        var builder = DistributedApplication.CreateBuilder(Array.Empty<string>());

        var pds = builder.AddAtprotoPds<FakeProject>("pds");
        var relay = builder.AddAtprotoRelay<FakeProject>("relay").WithUpstream(pds);
        builder.AddAtprotoAppView<FakeProject>("appview")
            .WithFirehose(relay)
            .WithCollection("place.selfhost.status");

        using DistributedApplication app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        ProjectResource pdsRes = SingleProject(model, "pds");
        ProjectResource relayRes = SingleProject(model, "relay");
        ProjectResource appviewRes = SingleProject(model, "appview");

        // Startup ordering: relay waits for pds; appview waits for relay.
        Assert.Contains(relayRes.Annotations.OfType<WaitAnnotation>(), w => w.Resource == pdsRes);
        Assert.Contains(appviewRes.Annotations.OfType<WaitAnnotation>(), w => w.Resource == relayRes);

        // Env-var contract each service reads.
        Dictionary<string, string> pdsEnv = await EvalEnvAsync(pdsRes);
        Assert.True(pdsEnv.ContainsKey("Pds__PublicUrl"), "PDS advertises its own address");

        Dictionary<string, string> relayEnv = await EvalEnvAsync(relayRes);
        Assert.True(relayEnv.ContainsKey("Relay__Upstreams__0"), "relay crawls the pds upstream");

        Dictionary<string, string> appviewEnv = await EvalEnvAsync(appviewRes);
        Assert.True(appviewEnv.ContainsKey("Firehose__Url"), "appview subscribes the relay firehose");
        Assert.Equal("place.selfhost.status", appviewEnv["AppView__Collection"]);
    }

    private static ProjectResource SingleProject(DistributedApplicationModel model, string name) =>
        Assert.Single(model.Resources.OfType<ProjectResource>(), r => r.Name == name);

    private static async Task<Dictionary<string, string>> EvalEnvAsync(IResource resource)
    {
        var ctx = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            resource);
        foreach (EnvironmentCallbackAnnotation ann in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await ann.Callback(ctx);

        var result = new Dictionary<string, string>();
        foreach ((string key, object value) in ctx.EnvironmentVariables)
            result[key] = value.ToString() ?? string.Empty;
        return result;
    }
}
