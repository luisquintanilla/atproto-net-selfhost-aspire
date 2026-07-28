namespace AtProto.Relay;

/// <summary>
/// Relay configuration. Bound from the <c>Relay</c> config section (see <c>appsettings.json</c> or
/// Aspire env like <c>Relay__Upstreams__0</c>).
/// </summary>
public sealed class RelayOptions
{
    /// <summary>The relay's own public base URL (advertised address; informational for MVP).</summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Upstream PDS/relay base URLs to crawl on startup (any scheme; normalized to a firehose URL).
    /// Additional hosts can be added at runtime via <c>com.atproto.sync.requestCrawl</c>.
    /// </summary>
    public List<string> Upstreams { get; set; } = new();

    /// <summary>Directory for per-host upstream cursors and the persisted global seq.</summary>
    public string? CursorDir { get; set; }
}
