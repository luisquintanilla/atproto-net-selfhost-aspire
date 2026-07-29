using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AtProto.Cbor;

namespace AtProto.Pds.Tests;

public sealed class BlobTests : IClassFixture<PdsServerFixture>
{
    private const string PostNsid = "place.selfhost.post";
    private readonly PdsServerFixture _fx;

    public BlobTests(PdsServerFixture fixture) => _fx = fixture;

    [Fact]
    public async Task UploadBlob_returns_a_raw_sha256_blob_ref_and_getBlob_returns_the_bytes()
    {
        (string did, string jwt) = await CreateAccount("blobber.pds.localhost", "hunter2");
        byte[] bytes = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x01, 0x02, 0x03];
        string expectedCid = ComputeRawCid(bytes).ToString();

        JsonElement first = await UploadBlob(jwt, bytes, "image/png");
        JsonElement blob = first.GetProperty("blob");
        Assert.Equal("blob", blob.GetProperty("$type").GetString());
        Assert.Equal(expectedCid, blob.GetProperty("ref").GetProperty("$link").GetString());
        Assert.Equal("image/png", blob.GetProperty("mimeType").GetString());
        Assert.Equal(bytes.Length, blob.GetProperty("size").GetInt32());

        HttpResponseMessage get = await _fx.Http.GetAsync(
            $"{_fx.BaseUrl}/xrpc/com.atproto.sync.getBlob?did={Uri.EscapeDataString(did)}&cid={Uri.EscapeDataString(expectedCid)}");
        get.EnsureSuccessStatusCode();
        Assert.Equal("image/png", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await get.Content.ReadAsByteArrayAsync());

        JsonElement second = await UploadBlob(jwt, bytes, "image/png");
        Assert.Equal(expectedCid, second.GetProperty("blob").GetProperty("ref").GetProperty("$link").GetString());
    }

    [Fact]
    public async Task GetBlob_returns_BlobNotFound_for_unknown_cid()
    {
        Cid cid = ComputeRawCid([1, 2, 3]);
        HttpResponseMessage missing = await _fx.Http.GetAsync(
            $"{_fx.BaseUrl}/xrpc/com.atproto.sync.getBlob?did=did%3Aweb%3Aexample.com&cid={Uri.EscapeDataString(cid.ToString())}");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        JsonElement error = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BlobNotFound", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Record_embedding_a_blob_ref_round_trips_through_dag_cbor_and_repo_storage()
    {
        (string did, string jwt) = await CreateAccount("blobrecord.pds.localhost", "hunter2");
        byte[] bytes = [10, 20, 30, 40, 50];
        JsonElement blob = (await UploadBlob(jwt, bytes, "image/jpeg")).GetProperty("blob");
        string cid = blob.GetProperty("ref").GetProperty("$link").GetString()!;

        var record = new Dictionary<string, object?>
        {
            ["$type"] = PostNsid,
            ["text"] = "post with image",
            ["image"] = BlobRef(cid, "image/jpeg", bytes.Length),
            ["createdAt"] = "2026-07-29T14:00:00.000Z",
        };

        var dagRecord = new Dictionary<string, object?>
        {
            ["$type"] = PostNsid,
            ["text"] = "post with image",
            ["image"] = new Dictionary<string, object?>
            {
                ["$type"] = "blob",
                ["ref"] = Cid.Decode(cid),
                ["mimeType"] = "image/jpeg",
                ["size"] = (long)bytes.Length,
            },
            ["createdAt"] = "2026-07-29T14:00:00.000Z",
        };
        object? decoded = DagCbor.Decode(DagCbor.Encode(dagRecord));
        IReadOnlyDictionary<string, object?> decodedBlob = decoded.RequiredField("image").AsMap();

        Assert.Equal("blob", decodedBlob["$type"]);
        Assert.Equal(Cid.Decode(cid), decodedBlob["ref"]);
        Assert.Equal("image/jpeg", decodedBlob["mimeType"]);
        Assert.Equal((long)bytes.Length, decodedBlob["size"].AsInt64());

        await CreateRecord(jwt, did, PostNsid, "with-blob", record);
        JsonElement got = await _fx.Http.GetFromJsonAsync<JsonElement>(
            $"{_fx.BaseUrl}/xrpc/com.atproto.repo.getRecord?repo={Uri.EscapeDataString(did)}&collection={PostNsid}&rkey=with-blob");
        JsonElement gotBlob = got.GetProperty("value").GetProperty("image");
        Assert.Equal(cid, gotBlob.GetProperty("ref").GetProperty("$link").GetString());
        Assert.Equal("image/jpeg", gotBlob.GetProperty("mimeType").GetString());
        Assert.Equal(bytes.Length, gotBlob.GetProperty("size").GetInt32());
    }

    private async Task<(string Did, string AccessJwt)> CreateAccount(string handle, string password)
    {
        HttpResponseMessage resp = await _fx.Http.PostAsJsonAsync(
            $"{_fx.BaseUrl}/xrpc/com.atproto.server.createAccount",
            new { handle, password });
        resp.EnsureSuccessStatusCode();
        JsonElement account = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (account.GetProperty("did").GetString()!, account.GetProperty("accessJwt").GetString()!);
    }

    private async Task<JsonElement> UploadBlob(string jwt, byte[] bytes, string contentType)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_fx.BaseUrl}/xrpc/com.atproto.repo.uploadBlob");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Content = new ByteArrayContent(bytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        HttpResponseMessage resp = await _fx.Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task CreateRecord(string jwt, string did, string collection, string rkey, object record)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_fx.BaseUrl}/xrpc/com.atproto.repo.createRecord");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["repo"] = did,
            ["collection"] = collection,
            ["rkey"] = rkey,
            ["record"] = record,
        });
        (await _fx.Http.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private static Cid ComputeRawCid(byte[] bytes)
    {
        byte[] binary = new byte[36];
        binary[0] = 0x01;
        binary[1] = 0x55;
        binary[2] = 0x12;
        binary[3] = 0x20;
        SHA256.HashData(bytes).CopyTo(binary.AsSpan(4));
        return Cid.FromBytes(binary);
    }

    private static Dictionary<string, object?> BlobRef(string cid, string mimeType, int size) => new()
    {
        ["$type"] = "blob",
        ["ref"] = new Dictionary<string, object?>
        {
            ["$link"] = cid,
        },
        ["mimeType"] = mimeType,
        ["size"] = size,
    };
}
