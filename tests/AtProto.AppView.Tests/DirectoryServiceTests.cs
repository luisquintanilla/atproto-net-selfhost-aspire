using System.Net;

namespace AtProto.AppView.Tests;

public sealed class DirectoryServiceTests
{
    [Fact]
    public async Task ListAsync_aggregates_relay_hosts_and_instance_records()
    {
        const string relay = "http://relay.local";
        const string pdsA = "http://alpha.local:5100";
        const string pdsB = "http://beta.local:5200";

        var handler = new StubHttpMessageHandler(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.RequestUri!.Host == "relay.local" && path.EndsWith("/xrpc/com.atproto.sync.listHosts", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json($$$"""{"hosts":[{"hostname":"{{{pdsB}}}","status":"active"},{"hostname":"{{{pdsA}}}","status":"active"}]}""");

            if (req.RequestUri!.Host == "alpha.local" && path.EndsWith("/xrpc/com.atproto.repo.getRecord", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json($$$"""
                    {"uri":"at://did:web:alpha.local%3A5100/place.selfhost.instance/self","cid":"bafya","value":{"$type":"place.selfhost.instance","name":"Alpha","description":"First node","pds":"{{{pdsA}}}","relay":"{{{relay}}}","appview":"http://app.local","createdAt":"2026-07-29T12:00:00Z"}}
                    """);

            if (req.RequestUri!.Host == "beta.local" && path.EndsWith("/xrpc/com.atproto.repo.getRecord", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json($$$"""
                    {"uri":"at://did:web:beta.local%3A5200/place.selfhost.instance/self","cid":"bafyb","value":{"$type":"place.selfhost.instance","name":"Beta","description":"Second node","pds":"{{{pdsB}}}","relay":"{{{relay}}}","appview":"http://app.local","createdAt":"2026-07-29T12:01:00Z"}}
                    """);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var svc = new DirectoryService(new StubHttpClientFactory(handler), TestConfig.InMemory(("AppView:RelayUrl", relay)));

        IReadOnlyList<DirectoryInstance> instances = await svc.ListAsync();

        Assert.Equal(2, instances.Count);
        Assert.Equal("Alpha", instances[0].Name);
        Assert.Equal("did:web:alpha.local%3A5100", instances[0].Did);
        Assert.Equal(pdsA, instances[0].Pds);
        Assert.Equal(relay, instances[0].Relay);
        Assert.Equal("http://app.local", instances[0].AppView);
        Assert.Equal("Beta", instances[1].Name);
        Assert.Equal("did:web:beta.local%3A5200", instances[1].Did);

        Assert.Contains(handler.Requests, r => r.RequestUri!.Query.Contains($"repo={Uri.EscapeDataString("did:web:alpha.local%3A5100")}"));
        Assert.Contains(handler.Requests, r => r.RequestUri!.Query.Contains("collection=place.selfhost.instance"));
    }

    [Fact]
    public void ServiceDid_encodes_non_default_ports_for_did_web()
    {
        Assert.Equal("did:web:localhost%3A5100", DirectoryService.ServiceDid("http://localhost:5100"));
        Assert.Equal("did:web:example.com", DirectoryService.ServiceDid("https://example.com"));
    }
}
