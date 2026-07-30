using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AtProto.Pds.Tests;

// The atproto OAuth discovery documents served by the PDS: the protected-resource metadata points a
// client at the authorization server, and the authorization-server metadata advertises the endpoints
// and the profile requirements (PAR mandatory, PKCE S256, DPoP ES256, URL client_id documents). Both
// are served with permissive CORS so browser apps can fetch them cross-origin.
public sealed class OAuthMetadataTests : IClassFixture<PdsServerFixture>
{
    private readonly PdsServerFixture _fixture;

    public OAuthMetadataTests(PdsServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProtectedResourceMetadata_PointsAtTheAuthorizationServer()
    {
        JsonElement doc = await _fixture.Http.GetFromJsonAsync<JsonElement>(
            $"{_fixture.BaseUrl}/.well-known/oauth-protected-resource");

        Assert.Equal(_fixture.BaseUrl, doc.GetProperty("resource").GetString());
        Assert.Equal(_fixture.BaseUrl, doc.GetProperty("authorization_servers")[0].GetString());
    }

    [Fact]
    public async Task AuthorizationServerMetadata_AdvertisesTheProfile()
    {
        JsonElement doc = await _fixture.Http.GetFromJsonAsync<JsonElement>(
            $"{_fixture.BaseUrl}/.well-known/oauth-authorization-server");

        Assert.Equal(_fixture.BaseUrl, doc.GetProperty("issuer").GetString());
        Assert.Equal($"{_fixture.BaseUrl}/oauth/authorize", doc.GetProperty("authorization_endpoint").GetString());
        Assert.Equal($"{_fixture.BaseUrl}/oauth/token", doc.GetProperty("token_endpoint").GetString());
        Assert.Equal($"{_fixture.BaseUrl}/oauth/par", doc.GetProperty("pushed_authorization_request_endpoint").GetString());

        Assert.True(doc.GetProperty("require_pushed_authorization_requests").GetBoolean());
        Assert.True(doc.GetProperty("client_id_metadata_document_supported").GetBoolean());
        Assert.True(doc.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
        Assert.Equal("S256", doc.GetProperty("code_challenge_methods_supported")[0].GetString());
        Assert.Equal("ES256", doc.GetProperty("dpop_signing_alg_values_supported")[0].GetString());
        Assert.Contains("atproto", doc.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Metadata_IsCorsEnabledForBrowserApps()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_fixture.BaseUrl}/.well-known/oauth-authorization-server");
        request.Headers.Add("Origin", "https://client.example");

        HttpResponseMessage response = await _fixture.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
}
