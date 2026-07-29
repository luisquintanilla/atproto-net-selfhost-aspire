using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace AtProto.AppView.Tests;

/// <summary>
/// The identity layer in miniature: a DID resolves to the PDS that hosts its repo. These pin the
/// did:web → did.json URL computation (path-based and bare-authority forms), the happy path that
/// reads the <c>#atproto_pds</c> endpoint from the document, and the configured-PDS fallback that
/// keeps a single-PDS demo working when the document can't be fetched.
/// </summary>
public class PdsResolverTests
{
    private const string Pds = "http://localhost:5271";
    private const string PathDid = "did:web:localhost%3A5271:pds:demo1";

    private static string DidDoc(string did, string handle, string endpoint) => $$"""
        {
          "id": "{{did}}",
          "alsoKnownAs": ["at://{{handle}}"],
          "verificationMethod": [
            { "id": "{{did}}#atproto", "type": "Multikey", "publicKeyMultibase": "zTestKey" }
          ],
          "service": [
            { "id": "#atproto_pds", "type": "AtprotoPersonalDataServer", "serviceEndpoint": "{{endpoint}}" }
          ]
        }
        """;

    private static PdsResolver Build(StubHttpMessageHandler handler, string? configuredPds = Pds) =>
        new(new StubHttpClientFactory(handler),
            TestConfig.InMemory(("AppView:PdsUrl", configuredPds)),
            NullLogger<PdsResolver>.Instance);

    [Fact]
    public async Task Resolves_via_did_document_at_path_based_url()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(DidDoc(PathDid, "demo1.pds.localhost", Pds)));
        PdsResolver resolver = Build(handler);

        ResolvedIdentity id = await resolver.ResolveAsync(PathDid);

        Assert.Equal("http://localhost:5271/pds/demo1/did.json", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal(Pds, id.PdsBase);
        Assert.Equal("demo1.pds.localhost", id.Handle);
        Assert.NotNull(id.Document);
        Assert.Equal(PathDid, id.Document!.Did);
        Assert.Equal("zTestKey", id.Document.SigningKeyMultibase);
    }

    [Fact]
    public async Task Uses_the_documents_endpoint_when_it_differs_from_the_configured_pds()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(DidDoc(PathDid, "demo1.pds.localhost", "http://pds-b.localhost:9000/")));
        PdsResolver resolver = Build(handler);

        ResolvedIdentity id = await resolver.ResolveAsync(PathDid);

        // The advertised endpoint wins (trailing slash trimmed); this is how federation generalizes.
        Assert.Equal("http://pds-b.localhost:9000", id.PdsBase);
    }

    [Fact]
    public async Task Computes_well_known_url_for_a_bare_authority_did()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(DidDoc("did:web:localhost%3A5271", "root.localhost", Pds)));
        PdsResolver resolver = Build(handler);

        await resolver.ResolveAsync("did:web:localhost%3A5271");

        Assert.Equal("http://localhost:5271/.well-known/did.json", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Falls_back_to_configured_pds_when_document_is_unreachable()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        PdsResolver resolver = Build(handler);

        ResolvedIdentity id = await resolver.ResolveAsync(PathDid);

        Assert.Equal(Pds, id.PdsBase);
        Assert.Null(id.Handle);
        Assert.Null(id.Document);
    }

    [Fact]
    public async Task Non_did_web_makes_no_request_and_falls_back()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("should not be called for a non-did:web DID"));
        PdsResolver resolver = Build(handler);

        ResolvedIdentity id = await resolver.ResolveAsync("did:plc:abc123");

        Assert.Empty(handler.Requests);
        Assert.Equal(Pds, id.PdsBase);
    }

    [Fact]
    public async Task Throws_when_no_document_and_no_configured_pds()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        PdsResolver resolver = Build(handler, configuredPds: null);

        Assert.False(resolver.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync("did:web:example.com"));
    }

    [Fact]
    public void Enabled_reflects_configured_pds()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.True(Build(handler).Enabled);
        Assert.False(Build(handler, configuredPds: null).Enabled);
    }
}
