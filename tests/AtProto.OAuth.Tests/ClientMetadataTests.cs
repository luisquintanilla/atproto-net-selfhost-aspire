using System.Text.Json;
using AtProto.OAuth;

namespace AtProto.OAuth.Tests;

public class ClientMetadataTests
{
    private const string ClientId = "https://app.example.com/client-metadata.json";

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static string ValidPublicDoc(
        string clientId = ClientId,
        string scope = "atproto transition:generic",
        string redirect = "https://app.example.com/callback",
        string authMethod = "none",
        bool dpop = true) =>
        $$"""
        {
          "client_id": "{{clientId}}",
          "application_type": "web",
          "grant_types": ["authorization_code", "refresh_token"],
          "response_types": ["code"],
          "scope": "{{scope}}",
          "redirect_uris": ["{{redirect}}"],
          "token_endpoint_auth_method": "{{authMethod}}",
          "dpop_bound_access_tokens": {{(dpop ? "true" : "false")}},
          "client_name": "Example App"
        }
        """;

    private static ClientMetadata Parse(string doc, string clientId = ClientId, ClientMetadataResolverOptions? options = null) =>
        ClientMetadata.Parse(Json(doc), clientId, options ?? new ClientMetadataResolverOptions());

    [Fact]
    public void Parse_ValidPublicClient_Succeeds()
    {
        ClientMetadata meta = Parse(ValidPublicDoc());

        Assert.Equal(ClientId, meta.ClientId);
        Assert.Equal("web", meta.ApplicationType);
        Assert.Contains("authorization_code", meta.GrantTypes);
        Assert.Contains("code", meta.ResponseTypes);
        Assert.True(meta.DpopBoundAccessTokens);
        Assert.False(meta.IsConfidential);
        Assert.False(meta.IsLocalhostClient);
        Assert.Contains("atproto", meta.ScopeValues);
        Assert.Contains("transition:generic", meta.ScopeValues);
    }

    [Fact]
    public void Parse_ClientIdMismatch_Throws()
    {
        string doc = ValidPublicDoc(clientId: "https://evil.example.com/x");
        var ex = Assert.Throws<ClientMetadataException>(() => Parse(doc));
        Assert.Contains("client_id", ex.Message);
    }

    [Fact]
    public void Parse_DpopFalse_Throws()
    {
        Assert.Throws<ClientMetadataException>(() => Parse(ValidPublicDoc(dpop: false)));
    }

    [Fact]
    public void Parse_ScopeMissingAtproto_Throws()
    {
        Assert.Throws<ClientMetadataException>(() => Parse(ValidPublicDoc(scope: "transition:generic")));
    }

    [Theory]
    [InlineData("http://app.example.com/callback")]  // http to a non-loopback host
    [InlineData("https://app.example.com/cb#frag")]  // fragment not allowed
    public void Parse_BadRedirectUri_Throws(string redirect)
    {
        Assert.Throws<ClientMetadataException>(() => Parse(ValidPublicDoc(redirect: redirect)));
    }

    [Fact]
    public void Parse_HttpLoopbackRedirect_IsAllowed()
    {
        ClientMetadata meta = Parse(ValidPublicDoc(redirect: "http://127.0.0.1/callback"));
        Assert.Contains("http://127.0.0.1/callback", meta.RedirectUris);
    }

    [Fact]
    public void Parse_MissingRequiredField_Throws()
    {
        string doc = """
        {
          "client_id": "https://app.example.com/client-metadata.json",
          "response_types": ["code"],
          "scope": "atproto",
          "redirect_uris": ["https://app.example.com/callback"],
          "dpop_bound_access_tokens": true
        }
        """;
        // grant_types is missing.
        Assert.Throws<ClientMetadataException>(() => Parse(doc));
    }

    [Fact]
    public void Parse_ConfidentialClient_RejectedByDefault()
    {
        string doc = $$"""
        {
          "client_id": "{{ClientId}}",
          "grant_types": ["authorization_code", "refresh_token"],
          "response_types": ["code"],
          "scope": "atproto",
          "redirect_uris": ["https://app.example.com/callback"],
          "token_endpoint_auth_method": "private_key_jwt",
          "token_endpoint_auth_signing_alg": "ES256",
          "jwks_uri": "https://app.example.com/jwks.json",
          "dpop_bound_access_tokens": true
        }
        """;
        var ex = Assert.Throws<ClientMetadataException>(() => Parse(doc));
        Assert.Equal(OAuthErrors.UnauthorizedClient, ex.ErrorCode);
    }

    [Fact]
    public void Parse_ConfidentialClient_AllowedWhenEnabled()
    {
        string doc = $$"""
        {
          "client_id": "{{ClientId}}",
          "grant_types": ["authorization_code", "refresh_token"],
          "response_types": ["code"],
          "scope": "atproto",
          "redirect_uris": ["https://app.example.com/callback"],
          "token_endpoint_auth_method": "private_key_jwt",
          "token_endpoint_auth_signing_alg": "ES256",
          "jwks_uri": "https://app.example.com/jwks.json",
          "dpop_bound_access_tokens": true
        }
        """;
        ClientMetadata meta = Parse(doc, options: new ClientMetadataResolverOptions { AllowConfidentialClients = true });
        Assert.True(meta.IsConfidential);
        Assert.Equal("https://app.example.com/jwks.json", meta.JwksUri);
    }

    [Fact]
    public void Parse_ConfidentialClient_BothJwksAndUri_Throws()
    {
        string doc = $$"""
        {
          "client_id": "{{ClientId}}",
          "grant_types": ["authorization_code"],
          "response_types": ["code"],
          "scope": "atproto",
          "redirect_uris": ["https://app.example.com/callback"],
          "token_endpoint_auth_method": "private_key_jwt",
          "jwks": { "keys": [] },
          "jwks_uri": "https://app.example.com/jwks.json",
          "dpop_bound_access_tokens": true
        }
        """;
        Assert.Throws<ClientMetadataException>(() =>
            Parse(doc, options: new ClientMetadataResolverOptions { AllowConfidentialClients = true }));
    }

    [Fact]
    public void Parse_PublicClientWithKeys_Throws()
    {
        string doc = $$"""
        {
          "client_id": "{{ClientId}}",
          "grant_types": ["authorization_code"],
          "response_types": ["code"],
          "scope": "atproto",
          "redirect_uris": ["https://app.example.com/callback"],
          "token_endpoint_auth_method": "none",
          "jwks_uri": "https://app.example.com/jwks.json",
          "dpop_bound_access_tokens": true
        }
        """;
        Assert.Throws<ClientMetadataException>(() => Parse(doc));
    }

    [Fact]
    public void Parse_ClientUriDifferentHost_Throws()
    {
        string doc = $$"""
        {
          "client_id": "{{ClientId}}",
          "grant_types": ["authorization_code"],
          "response_types": ["code"],
          "scope": "atproto",
          "redirect_uris": ["https://app.example.com/callback"],
          "token_endpoint_auth_method": "none",
          "dpop_bound_access_tokens": true,
          "client_uri": "https://elsewhere.example.net/"
        }
        """;
        Assert.Throws<ClientMetadataException>(() => Parse(doc));
    }

    [Fact]
    public void AllowsScopes_RequiresSubsetAndAtproto()
    {
        ClientMetadata meta = Parse(ValidPublicDoc(scope: "atproto transition:generic"));

        Assert.True(meta.AllowsScopes(["atproto"]));
        Assert.True(meta.AllowsScopes(["atproto", "transition:generic"]));
        Assert.False(meta.AllowsScopes(["transition:generic"]));           // missing atproto
        Assert.False(meta.AllowsScopes(["atproto", "transition:email"]));  // not declared
        Assert.False(meta.AllowsScopes([]));                               // empty
    }

    [Fact]
    public void AllowsRedirectUri_ExactMatchForWebClients()
    {
        ClientMetadata meta = Parse(ValidPublicDoc(redirect: "https://app.example.com/callback"));

        Assert.True(meta.AllowsRedirectUri("https://app.example.com/callback"));
        Assert.False(meta.AllowsRedirectUri("https://app.example.com/callback2"));
        Assert.False(meta.AllowsRedirectUri("https://app.example.com:8443/callback"));
    }
}
