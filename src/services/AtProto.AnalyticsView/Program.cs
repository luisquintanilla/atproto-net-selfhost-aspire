using System.Data.Common;
using AtProto.AnalyticsView;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Resolve the DuckDB data source. Aspire's production profile injects ConnectionStrings__analytics
// (the AddDuckDB resource); standalone runs fall back to Analytics:DuckDbPath, then to an in-memory
// store so `dotnet run` works with zero configuration.
string dataSource = ResolveDataSource(builder.Configuration);

builder.Services.AddSingleton(new AnalyticsStore(dataSource));
builder.Services.AddHostedService<AnalyticsIngestService>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseDefaultFiles();
app.UseStaticFiles();

// XRPC-style analytical queries: aggregates over the whole firehose that the presence board cannot answer.
app.MapGet("/xrpc/place.selfhost.getFirehoseStats",
    (AnalyticsStore store) => Results.Json(store.GetStats()));

app.MapGet("/xrpc/place.selfhost.getActivity",
    (AnalyticsStore store, string? unit, int? limit) =>
        Results.Json(store.GetActivity(unit ?? "minute", limit ?? 60)));

app.MapGet("/xrpc/place.selfhost.getTopRepos",
    (AnalyticsStore store, int? limit) => Results.Json(store.GetTopRepos(limit ?? 10)));

app.MapGet("/xrpc/place.selfhost.getCollections",
    (AnalyticsStore store) => Results.Json(store.GetCollections()));

app.Run();

static string ResolveDataSource(IConfiguration config)
{
    string? connectionString = config.GetConnectionString("analytics");
    if (!string.IsNullOrWhiteSpace(connectionString))
        return ExtractDataSource(connectionString);

    string? explicitPath = config["Analytics:DuckDbPath"];
    if (!string.IsNullOrWhiteSpace(explicitPath))
        return explicitPath;

    return ":memory:";
}

// The DuckDB Aspire resource may hand us a bare path or a `Key=Value` connection string; normalize
// both to the data source DuckDB.NET expects.
static string ExtractDataSource(string connectionString)
{
    if (!connectionString.Contains('='))
        return connectionString.Trim();

    try
    {
        var parsed = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (string key in new[] { "Data Source", "DataSource", "Path", "Database" })
        {
            if (parsed.TryGetValue(key, out object? value) &&
                value is string source && !string.IsNullOrWhiteSpace(source))
            {
                return source;
            }
        }
    }
    catch (ArgumentException)
    {
        // Not a parseable connection string; treat the whole thing as the data source.
    }

    return connectionString;
}

public partial class Program;
