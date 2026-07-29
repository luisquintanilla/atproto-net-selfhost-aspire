namespace AtProto.Relay.Tests;

internal static class Fixtures
{
    public static string Dir { get; } = Locate("tests", "fixtures");

    public static string ProjectDir { get; } = Locate("tests", "AtProto.Relay.Tests");

    public static byte[] Bytes(string relative) => File.ReadAllBytes(Path.Combine(Dir, relative));

    private static string Locate(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "atproto-net-selfhost-aspire.slnx")))
                return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root (atproto-net-selfhost-aspire.slnx)");
    }
}
