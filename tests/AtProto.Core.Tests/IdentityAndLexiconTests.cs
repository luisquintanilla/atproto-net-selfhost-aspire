using AtProto.Identity;
using AtProto.Lexicon;

namespace AtProto.Core.Tests;

public sealed class IdentityAndLexiconTests
{
    // A representative did:plc document (shape as served by plc.directory).
    private const string DidDocJson = """
    {
      "@context": ["https://www.w3.org/ns/did/v1"],
      "id": "did:plc:ewvi7nxzyoun6zhxrhs64oiz",
      "alsoKnownAs": ["at://atproto.com"],
      "verificationMethod": [{
        "id": "did:plc:ewvi7nxzyoun6zhxrhs64oiz#atproto",
        "type": "Multikey",
        "controller": "did:plc:ewvi7nxzyoun6zhxrhs64oiz",
        "publicKeyMultibase": "zQ3shunBKsXixLxKtC5qeSG9E4J5RkGN57im31pcTzbNQnm5w"
      }],
      "service": [{
        "id": "#atproto_pds",
        "type": "AtprotoPersonalDataServer",
        "serviceEndpoint": "https://enoki.us-east.host.bsky.network"
      }]
    }
    """;

    [Fact]
    public void DidDocument_parses_handle_pds_and_signing_key()
    {
        DidDocument doc = DidDocument.Parse(DidDocJson);

        Assert.Equal("did:plc:ewvi7nxzyoun6zhxrhs64oiz", doc.Did);
        Assert.Equal("atproto.com", doc.Handle);
        Assert.Equal("https://enoki.us-east.host.bsky.network", doc.PdsEndpoint);
        Assert.Equal("zQ3shunBKsXixLxKtC5qeSG9E4J5RkGN57im31pcTzbNQnm5w", doc.SigningKeyMultibase);
    }

    [Theory]
    [InlineData("app.bsky.feed.post", "app.bsky.feed", "post")]
    [InlineData("com.example.status", "com.example", "status")]
    public void Nsid_splits_authority_and_name(string value, string authority, string name)
    {
        var nsid = new Nsid(value);
        Assert.Equal(authority, nsid.Authority);
        Assert.Equal(name, nsid.Name);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("only.two")]
    [InlineData("app.bsky.feed.")]
    public void Nsid_rejects_malformed(string value) =>
        Assert.False(Nsid.TryParse(value, out _));

    [Fact]
    public void AtUri_parses_authority_collection_and_rkey()
    {
        Assert.True(AtUri.TryParse("at://did:plc:abc/app.bsky.feed.post/3xyz", out AtUri uri));
        Assert.Equal("did:plc:abc", uri.Authority);
        Assert.Equal("app.bsky.feed.post", uri.Collection);
        Assert.Equal("3xyz", uri.Rkey);
        Assert.Equal("app.bsky.feed.post/3xyz", uri.RepoKey);
    }

    [Fact]
    public void AtUri_handles_authority_only()
    {
        Assert.True(AtUri.TryParse("at://atproto.com", out AtUri uri));
        Assert.Equal("atproto.com", uri.Authority);
        Assert.Null(uri.Collection);
        Assert.Null(uri.RepoKey);
    }
}
