using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AtProto.Pds;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AtProto.Pds.Tests;

public sealed class InstanceAdvertisementTests
{
    [Fact]
    public async Task Startup_writes_instance_record_and_announces_to_configured_relay()
    {
        int relayPort = FreePort();
        string relayUrl = $"http://127.0.0.1:{relayPort}";
        var announced = new ConcurrentBag<string>();

        WebApplication relay = WebApplication.CreateBuilder([]).Build();
        relay.MapPost("/xrpc/com.atproto.sync.requestCrawl", async (HttpContext ctx) =>
        {
            JsonElement body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            announced.Add(body.GetProperty("hostname").GetString()!);
            return Results.Ok();
        });
        relay.Urls.Add(relayUrl);
        await relay.StartAsync();

        int pdsPort = FreePort();
        string pdsUrl = $"http://127.0.0.1:{pdsPort}";
        WebApplication pds = PdsHost.Build([], options =>
        {
            options.PublicUrl = pdsUrl;
            options.InstanceName = "Test Instance";
            options.InstanceDescription = "A PDS under test";
            options.InstanceRelayUrl = relayUrl;
            options.InstanceAppViewUrl = "http://127.0.0.1:5999";
            options.AnnounceRelayUrls.Add(relayUrl);
        });
        pds.Urls.Add(pdsUrl);

        using var http = new HttpClient();
        try
        {
            await pds.StartAsync();

            string did = $"did:web:127.0.0.1%3A{pdsPort}";
            JsonElement record = await Eventually(async () => await http.GetFromJsonAsync<JsonElement>(
                $"{pdsUrl}/xrpc/com.atproto.repo.getRecord?repo={Uri.EscapeDataString(did)}&collection=place.selfhost.instance&rkey=self"));

            JsonElement value = record.GetProperty("value");
            Assert.Equal("Test Instance", value.GetProperty("name").GetString());
            Assert.Equal("A PDS under test", value.GetProperty("description").GetString());
            Assert.Equal(pdsUrl, value.GetProperty("pds").GetString());
            Assert.Equal(relayUrl, value.GetProperty("relay").GetString());
            Assert.Equal("http://127.0.0.1:5999", value.GetProperty("appview").GetString());

            await Eventually(async () =>
            {
                string host = Assert.Single(announced);
                Assert.Equal(pdsUrl, host);
                return true;
            });
        }
        finally
        {
            await pds.StopAsync();
            await pds.DisposeAsync();
            await relay.StopAsync();
            await relay.DisposeAsync();
        }
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> action)
    {
        Exception? last = null;
        for (int i = 0; i < 40; i++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or Xunit.Sdk.XunitException)
            {
                last = ex;
                await Task.Delay(100);
            }
        }
        throw last ?? new TimeoutException();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
