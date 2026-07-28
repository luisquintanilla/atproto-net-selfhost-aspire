using System.Text.Json;

namespace AtProto.Core.Tests;

/// <summary>Locates and loads the frozen golden-vector fixtures (see tests/fixtures/README.md).</summary>
internal static class Fixtures
{
    public static string Dir { get; } = Locate();

    public static JsonDocument Manifest { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "manifest.json")));

    public static byte[] Bytes(string relative) => File.ReadAllBytes(Path.Combine(Dir, relative));

    public static JsonElement Head => Manifest.RootElement.GetProperty("head");

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "atproto-net-selfhost-aspire.slnx")))
                return Path.Combine(dir.FullName, "tests", "fixtures");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root (atproto-net-selfhost-aspire.slnx)");
    }
}
