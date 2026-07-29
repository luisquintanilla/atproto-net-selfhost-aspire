using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// "Native" Aspire hosting integration for a self-hosted atproto stack. Instead of the AppHost
/// knowing the magic env-var contract (<c>Pds__PublicUrl</c>, <c>Relay__Upstreams__0</c>,
/// <c>Firehose__Url</c>, <c>AppView__Collection</c>) and the correct wiring order, it declares the
/// topology semantically — <c>AddAtprotoPds</c> / <c>AddAtprotoRelay</c> / <c>AddAtprotoAppView</c>
/// plus <c>WithUpstream</c> / <c>WithFirehose</c> / <c>WithCollection</c> — and this library encodes
/// the contract. So the relay auto-crawls the PDS and the AppView auto-subscribes the relay, wired
/// through Aspire service discovery.
/// </summary>
/// <remarks>
/// The methods are generic over the AppHost-generated <c>Projects.*</c> metadata
/// (<see cref="IProjectMetadata"/>) so this library needs no reference to the service projects and
/// can ship independently. Each returns the underlying <see cref="ProjectResource"/> builder, so you
/// can keep chaining stock Aspire calls (replicas, extra env, etc.).
/// </remarks>
public static class AtprotoHostingExtensions
{
    /// <summary>
    /// Add a self-hosted PDS. It advertises its own address (<c>Pds:PublicUrl</c>) so its
    /// <c>did:web</c> identity and service endpoint resolve to where Aspire actually hosts it.
    /// </summary>
    public static IResourceBuilder<ProjectResource> AddAtprotoPds<TProject>(
        this IDistributedApplicationBuilder builder, string name)
        where TProject : IProjectMetadata, new()
    {
        IResourceBuilder<ProjectResource> pds = builder.AddProject<TProject>(name, launchProfileName: null)
            .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS");
        return pds.WithEnvironment("Pds__PublicUrl", pds.GetEndpoint("http"));
    }

    /// <summary>Add a self-hosted Relay. Attach upstream(s) with <see cref="WithUpstream"/>.</summary>
    public static IResourceBuilder<ProjectResource> AddAtprotoRelay<TProject>(
        this IDistributedApplicationBuilder builder, string name)
        where TProject : IProjectMetadata, new()
        => builder.AddProject<TProject>(name, launchProfileName: null)
            .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS");

    /// <summary>
    /// Add a self-hosted AppView. Point it at a firehose with <see cref="WithFirehose"/> and choose
    /// the indexed collection with <see cref="WithCollection"/>.
    /// </summary>
    public static IResourceBuilder<ProjectResource> AddAtprotoAppView<TProject>(
        this IDistributedApplicationBuilder builder, string name)
        where TProject : IProjectMetadata, new()
        => builder.AddProject<TProject>(name, launchProfileName: null)
            .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS");

    /// <summary>
    /// Make the relay crawl an upstream PDS (or another relay) firehose. Wires service discovery,
    /// a startup dependency, and the <c>Relay:Upstreams:{index}</c> config the relay reads to
    /// auto-crawl on startup. Call once per upstream with distinct <paramref name="index"/> values.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithUpstream(
        this IResourceBuilder<ProjectResource> relay,
        IResourceBuilder<ProjectResource> upstream,
        int index = 0)
        => relay
            .WithReference(upstream)
            .WaitFor(upstream)
            .WithEnvironment($"Relay__Upstreams__{index}", upstream.GetEndpoint("http"));

    /// <summary>
    /// Subscribe a firehose consumer (an AppView, or a relay chained onto another) to a
    /// <paramref name="source"/> firehose (a relay or a PDS). Wires service discovery, a startup
    /// dependency, and the <c>Firehose:Url</c> the consumer subscribes to (the client normalizes
    /// <c>http</c>→<c>ws</c>).
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithFirehose(
        this IResourceBuilder<ProjectResource> consumer,
        IResourceBuilder<ProjectResource> source)
        => consumer
            .WithReference(source)
            .WaitFor(source)
            .WithEnvironment("Firehose__Url", source.GetEndpoint("http"));

    /// <summary>Set the collection (NSID) an AppView indexes (<c>AppView:Collection</c>).</summary>
    public static IResourceBuilder<ProjectResource> WithCollection(
        this IResourceBuilder<ProjectResource> appview, string nsid)
        => appview.WithEnvironment("AppView__Collection", nsid);

    /// <summary>
    /// Give an AppView a PDS to read from and write to for inspection + compose (<c>AppView:PdsUrl</c>).
    /// The firehose tells the AppView <em>what</em> changed; this tells it <em>where</em> to fetch the
    /// full records, resolve identities, and (for the demo compose flow) write "your" status. Wires
    /// service discovery + a startup dependency so the PDS is reachable before the AppView reads it.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithPds(
        this IResourceBuilder<ProjectResource> appview,
        IResourceBuilder<ProjectResource> pds)
        => appview
            .WithReference(pds)
            .WaitFor(pds)
            .WithEnvironment("AppView__PdsUrl", pds.GetEndpoint("http"));

    /// <summary>
    /// Switch a PDS to durable SQLite storage (the "production" profile). Accounts, repositories,
    /// and blobs are written through to a SQLite database at <paramref name="path"/> and rehydrated
    /// on startup, so state survives a restart. Omit this (the default) to keep the zero-config
    /// in-memory store. The SQLite dependency lives only inside the PDS service; this just flips the
    /// <c>Pds:Storage</c> config the service reads.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithSqliteStorage(
        this IResourceBuilder<ProjectResource> pds, string path)
        => pds
            .WithEnvironment("Pds__Storage", "sqlite")
            .WithEnvironment("Pds__SqlitePath", path);

    /// <summary>Advertise this PDS as a named self-hosted instance.</summary>
    public static IResourceBuilder<ProjectResource> WithInstance(        this IResourceBuilder<ProjectResource> pds,
        string name,
        string? description = null)
    {
        pds = pds.WithEnvironment("Pds__InstanceName", name);
        if (!string.IsNullOrWhiteSpace(description))
            pds = pds.WithEnvironment("Pds__InstanceDescription", description);
        return pds;
    }

    /// <summary>Add relay/appview URLs to the PDS instance advertisement and ask the relay to crawl it.</summary>
    public static IResourceBuilder<ProjectResource> WithInstanceEndpoints(
        this IResourceBuilder<ProjectResource> pds,
        IResourceBuilder<ProjectResource> relay,
        IResourceBuilder<ProjectResource> appview,
        int announceIndex = 0)
        => pds
            .WithEnvironment("Pds__InstanceRelayUrl", relay.GetEndpoint("http"))
            .WithEnvironment("Pds__InstanceAppViewUrl", appview.GetEndpoint("http"))
            .WithEnvironment($"Pds__AnnounceRelayUrls__{announceIndex}", relay.GetEndpoint("http"));

    /// <summary>Let the AppView query a relay's host directory (<c>AppView:RelayUrl</c>).</summary>
    public static IResourceBuilder<ProjectResource> WithDirectoryRelay(
        this IResourceBuilder<ProjectResource> appview,
        IResourceBuilder<ProjectResource> relay)
        => appview
            .WithReference(relay)
            .WaitFor(relay)
            .WithEnvironment("AppView__RelayUrl", relay.GetEndpoint("http"));
}
