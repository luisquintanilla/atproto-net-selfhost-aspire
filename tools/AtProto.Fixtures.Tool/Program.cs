using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

// atproto golden-vectors fixture harness (T0).
//
// Captures a frozen, self-contained set of AT Protocol test vectors from the PUBLIC
// network (no credentials) so the codec libraries built in M1+ can verify themselves
// against real-world data with no network in the test loop:
//   - the full repository CAR (com.atproto.sync.getRepo)
//   - a single-record proof CAR (com.atproto.sync.getRecord)
//   - authoritative CIDs from JSON (getLatestCommit / describeRepo / listRecords)
//   - the DID document (signing key + PDS endpoint)
//   - a short firehose capture (com.atproto.sync.subscribeRepos frames)
// Everything is written under the output directory with a manifest.json (incl. SHA-256).
//
// Usage: atproto-fixtures [--handle <handle>] [--out <dir>] [--frames <n>] [--collections <n>]

var opts = ParseArgs(args);
Console.WriteLine($"atproto fixtures: handle={opts.Handle} out={opts.OutDir} frames={opts.Frames}");

Directory.CreateDirectory(opts.OutDir);
Directory.CreateDirectory(Path.Combine(opts.OutDir, "car"));
Directory.CreateDirectory(Path.Combine(opts.OutDir, "json"));
Directory.CreateDirectory(Path.Combine(opts.OutDir, "firehose"));

var indented = new JsonSerializerOptions { WriteIndented = true };

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("atproto-net-selfhost-aspire-fixtures/0.1");

var files = new JsonArray();

// 1. handle -> did
var did = await ResolveHandleAsync(http, opts.Handle);
Console.WriteLine($"  did = {did}");

// 2. did -> did document (signing key + pds endpoint)
var (didDocJson, pds, signingKey) = await ResolveDidDocAsync(http, did);
await SaveTextAsync(Path.Combine(opts.OutDir, "json", "did-doc.json"), didDocJson, "json/did-doc.json", files);
Console.WriteLine($"  pds = {pds}");
Console.WriteLine($"  signingKey = {signingKey}");

// 3. authoritative head + repo description
var latestCommit = await GetJsonAsync(http, $"{pds}/xrpc/com.atproto.sync.getLatestCommit?did={Uri.EscapeDataString(did)}");
await SaveTextAsync(Path.Combine(opts.OutDir, "json", "latest-commit.json"), latestCommit.ToJsonString(indented), "json/latest-commit.json", files);
var headCid = latestCommit["cid"]?.GetValue<string>() ?? "";
var headRev = latestCommit["rev"]?.GetValue<string>() ?? "";
Console.WriteLine($"  head commit = {headCid} (rev {headRev})");

var describe = await GetJsonAsync(http, $"{pds}/xrpc/com.atproto.repo.describeRepo?repo={Uri.EscapeDataString(did)}");
await SaveTextAsync(Path.Combine(opts.OutDir, "json", "describe-repo.json"), describe.ToJsonString(indented), "json/describe-repo.json", files);
var collections = (describe["collections"] as JsonArray)?.Select(c => c!.GetValue<string>()).ToList() ?? new();
Console.WriteLine($"  collections ({collections.Count}): {string.Join(", ", collections)}");

// 4. sample records (+ one record proof CAR) from the first few collections
var sampleRecords = new JsonArray();
JsonNode? firstRecord = null;
string? firstCollection = null, firstRkey = null;
foreach (var collection in collections.Take(opts.Collections))
{
    var list = await GetJsonAsync(http, $"{pds}/xrpc/com.atproto.repo.listRecords?repo={Uri.EscapeDataString(did)}&collection={Uri.EscapeDataString(collection)}&limit=3");
    var safe = collection.Replace('.', '_');
    await SaveTextAsync(Path.Combine(opts.OutDir, "json", $"listRecords.{safe}.json"), list.ToJsonString(indented), $"json/listRecords.{safe}.json", files);
    foreach (var rec in (list["records"] as JsonArray) ?? new())
    {
        var uri = rec!["uri"]?.GetValue<string>() ?? "";
        var cid = rec["cid"]?.GetValue<string>() ?? "";
        sampleRecords.Add(new JsonObject { ["uri"] = uri, ["cid"] = cid, ["collection"] = collection });
        if (firstRecord is null)
        {
            firstRecord = rec;
            firstCollection = collection;
            firstRkey = uri.Split('/').LastOrDefault();
        }
    }
}
Console.WriteLine($"  sampled {sampleRecords.Count} records");

// record proof CAR (small MST-path proof for one record)
string? proofFile = null;
if (firstCollection is not null && firstRkey is not null)
{
    var proofUrl = $"{pds}/xrpc/com.atproto.sync.getRecord?did={Uri.EscapeDataString(did)}&collection={Uri.EscapeDataString(firstCollection)}&rkey={Uri.EscapeDataString(firstRkey)}";
    try
    {
        var proof = await http.GetByteArrayAsync(proofUrl);
        proofFile = $"car/record-proof.{firstCollection.Replace('.', '_')}.{firstRkey}.car";
        await SaveBytesAsync(Path.Combine(opts.OutDir, proofFile), proof, proofFile, files);
        Console.WriteLine($"  record proof CAR = {proof.Length} bytes ({firstCollection}/{firstRkey})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  record proof CAR unavailable: {ex.Message}");
    }
}

// 5. full repository CAR
var repoCar = await http.GetByteArrayAsync($"{pds}/xrpc/com.atproto.sync.getRepo?did={Uri.EscapeDataString(did)}");
await SaveBytesAsync(Path.Combine(opts.OutDir, "car", "repo.car"), repoCar, "car/repo.car", files);
Console.WriteLine($"  full repo CAR = {repoCar.Length} bytes");

// 6. short firehose capture (public relay)
var frames = await CaptureFirehoseAsync(opts.RelayWss, opts.Frames, TimeSpan.FromSeconds(40));
var frameEntries = new JsonArray();
for (int i = 0; i < frames.Count; i++)
{
    var name = $"firehose/frame-{i:000}.bin";
    await SaveBytesAsync(Path.Combine(opts.OutDir, name), frames[i], name, files);
    frameEntries.Add(new JsonObject { ["file"] = name, ["bytes"] = frames[i].Length });
}
Console.WriteLine($"  captured {frames.Count} firehose frames");

// 7. manifest
var manifest = new JsonObject
{
    ["schema"] = "atproto-net-selfhost-aspire/fixtures/1",
    ["capturedUtc"] = DateTime.UtcNow.ToString("O"),
    ["source"] = new JsonObject
    {
        ["handle"] = opts.Handle,
        ["did"] = did,
        ["pds"] = pds,
        ["signingKeyMultibase"] = signingKey,
        ["relay"] = opts.RelayWss,
    },
    ["head"] = new JsonObject { ["commitCid"] = headCid, ["rev"] = headRev },
    ["collections"] = new JsonArray(collections.Select(c => (JsonNode)c!).ToArray()),
    ["sampleRecords"] = sampleRecords,
    ["recordProofCar"] = proofFile,
    ["repoCar"] = "car/repo.car",
    ["firehoseFrames"] = frameEntries,
    ["files"] = files,
    ["notes"] = "Public data captured for codec conformance testing. Regenerate with: dotnet run --project tools/AtProto.Fixtures.Tool -- --out tests/fixtures",
};
var manifestPath = Path.Combine(opts.OutDir, "manifest.json");
await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(indented));
Console.WriteLine($"wrote {manifestPath}");
Console.WriteLine("done.");
return 0;

// ---- helpers ----

static async Task<string> ResolveHandleAsync(HttpClient http, string handle)
{
    // did:web / did:plc handles both resolve here on the public appview
    var doc = await GetJsonAsync(http, $"https://public.api.bsky.app/xrpc/com.atproto.identity.resolveHandle?handle={Uri.EscapeDataString(handle)}");
    return doc["did"]?.GetValue<string>() ?? throw new InvalidOperationException($"could not resolve handle {handle}");
}

static async Task<(string DidDocJson, string Pds, string SigningKey)> ResolveDidDocAsync(HttpClient http, string did)
{
    string url = did.StartsWith("did:plc:", StringComparison.Ordinal)
        ? $"https://plc.directory/{did}"
        : did.StartsWith("did:web:", StringComparison.Ordinal)
            ? $"https://{did["did:web:".Length..].Replace(':', '/')}/.well-known/did.json"
            : throw new InvalidOperationException($"unsupported DID method: {did}");

    var json = await http.GetStringAsync(url);
    var doc = JsonNode.Parse(json)!;

    string pds = "";
    foreach (var svc in (doc["service"] as JsonArray) ?? new())
    {
        var id = svc!["id"]?.GetValue<string>() ?? "";
        if (id.EndsWith("#atproto_pds", StringComparison.Ordinal))
            pds = svc["serviceEndpoint"]?.GetValue<string>() ?? "";
    }

    string key = "";
    foreach (var vm in (doc["verificationMethod"] as JsonArray) ?? new())
    {
        var id = vm!["id"]?.GetValue<string>() ?? "";
        if (id.EndsWith("#atproto", StringComparison.Ordinal))
            key = vm["publicKeyMultibase"]?.GetValue<string>() ?? "";
    }

    if (string.IsNullOrEmpty(pds))
        throw new InvalidOperationException("no #atproto_pds service endpoint in DID document");

    return (json, pds.TrimEnd('/'), key);
}

static async Task<JsonNode> GetJsonAsync(HttpClient http, string url)
{
    var s = await http.GetStringAsync(url);
    return JsonNode.Parse(s) ?? throw new InvalidOperationException($"empty JSON from {url}");
}

static async Task<List<byte[]>> CaptureFirehoseAsync(string wss, int count, TimeSpan timeout)
{
    var result = new List<byte[]>();
    using var cts = new CancellationTokenSource(timeout);
    using var ws = new ClientWebSocket();
    try
    {
        await ws.ConnectAsync(new Uri(wss), cts.Token);
        var buffer = new byte[64 * 1024];
        while (result.Count < count && !cts.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult recv;
            do
            {
                recv = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (recv.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                    return result;
                }
                ms.Write(buffer, 0, recv.Count);
            } while (!recv.EndOfMessage);
            result.Add(ms.ToArray());
        }
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("  firehose capture timed out (kept what we got)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  firehose capture error: {ex.Message}");
    }
    return result;
}

static async Task SaveBytesAsync(string path, byte[] bytes, string relName, JsonArray files)
{
    await File.WriteAllBytesAsync(path, bytes);
    files.Add(FileEntry(relName, bytes));
}

static async Task SaveTextAsync(string path, string text, string relName, JsonArray files)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
    await File.WriteAllBytesAsync(path, bytes);
    files.Add(FileEntry(relName, bytes));
}

static JsonObject FileEntry(string relName, byte[] bytes) => new()
{
    ["file"] = relName,
    ["bytes"] = bytes.Length,
    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
};

static Options ParseArgs(string[] args)
{
    var o = new Options();
    for (int i = 0; i < args.Length - 1; i++)
    {
        switch (args[i])
        {
            case "--handle": o.Handle = args[++i]; break;
            case "--out": o.OutDir = args[++i]; break;
            case "--frames": o.Frames = int.Parse(args[++i]); break;
            case "--collections": o.Collections = int.Parse(args[++i]); break;
        }
    }
    o.OutDir = Path.GetFullPath(o.OutDir);
    return o;
}

sealed class Options
{
    public string Handle { get; set; } = "atproto.com";
    public string OutDir { get; set; } = "tests/fixtures";
    public int Frames { get; set; } = 12;
    public int Collections { get; set; } = 4;
    public string RelayWss { get; set; } = "wss://relay1.us-west.bsky.network/xrpc/com.atproto.sync.subscribeRepos";
}
