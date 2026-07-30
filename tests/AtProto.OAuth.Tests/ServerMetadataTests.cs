using System.Text.Json;
using AtProto.OAuth;

namespace AtProto.OAuth.Tests;

public class ServerMetadataTests
{
    [Fact]
    public void AuthorizationServerMetadata_ForIssuer_DerivesEndpoints()
    {
        var meta = AuthorizationServerMetadata.ForIssuer(new Uri("https://pds.example.com/"));

        Assert.Equal("https://pds.example.com", meta.Issuer);
        Assert.Equal("https://pds.example.com/oauth/authorize", meta.AuthorizationEndpoint);
        Assert.Equal("https://pds.example.com/oauth/token", meta.TokenEndpoint);
        Assert.Equal("https://pds.example.com/oauth/par", meta.PushedAuthorizationRequestEndpoint);
        Assert.Contains("atproto", meta.ScopesSupported);
        Assert.Contains("transition:generic", meta.ScopesSupported);
    }

    [Fact]
    public void AuthorizationServerMetadata_Json_HasProfileFields()
    {
        var meta = AuthorizationServerMetadata.ForIssuer(new Uri("https://pds.example.com"));
        using JsonDocument doc = JsonDocument.Parse(meta.ToJson());
        JsonElement root = doc.RootElement;

        Assert.Equal("https://pds.example.com", root.GetProperty("issuer").GetString());
        Assert.Equal("S256", root.GetProperty("code_challenge_methods_supported")[0].GetString());
        Assert.True(root.GetProperty("require_pushed_authorization_requests").GetBoolean());
        Assert.True(root.GetProperty("client_id_metadata_document_supported").GetBoolean());
        Assert.True(root.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
        Assert.Equal("ES256", root.GetProperty("dpop_signing_alg_values_supported")[0].GetString());
        Assert.Equal("code", root.GetProperty("response_types_supported")[0].GetString());

        var authMethods = root.GetProperty("token_endpoint_auth_methods_supported")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("none", authMethods);
        Assert.Contains("private_key_jwt", authMethods);
    }

    [Fact]
    public void ProtectedResourceMetadata_ForIssuer_PointsAtSelf()
    {
        var prm = ProtectedResourceMetadata.ForIssuer(new Uri("https://pds.example.com/"));

        Assert.Equal("https://pds.example.com", prm.Resource);
        Assert.Equal(new[] { "https://pds.example.com" }, prm.AuthorizationServers);
    }

    [Fact]
    public void ProtectedResourceMetadata_Json_OmitsScopesWhenNull()
    {
        var prm = ProtectedResourceMetadata.ForIssuer(new Uri("https://pds.example.com"));
        using JsonDocument doc = JsonDocument.Parse(prm.ToJson());

        Assert.False(doc.RootElement.TryGetProperty("scopes_supported", out _));
        Assert.Equal("https://pds.example.com",
            doc.RootElement.GetProperty("authorization_servers")[0].GetString());
    }

    [Fact]
    public void ProtectedResourceMetadata_Json_IncludesScopesWhenSet()
    {
        var prm = ProtectedResourceMetadata.ForIssuer(
            new Uri("https://pds.example.com"), ["atproto", "transition:generic"]);
        using JsonDocument doc = JsonDocument.Parse(prm.ToJson());

        var scopes = doc.RootElement.GetProperty("scopes_supported")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("atproto", scopes);
    }
}
