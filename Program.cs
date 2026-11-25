using System.Net;
using System.Text.Json;
using HomeNet;
using Microsoft.Data.Sqlite;

var root = Argument("--root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HomeNetMonitor");
var config = Argument("--config") ?? Path.Combine(root, "config.json");
var data = new DataPath(Path.Combine(root, "data"));
var settings = File.Exists(config) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(config), Settings.Json)
    ?? throw new InvalidDataException("Empty configuration.") : new Settings();
settings.Validate();

if (Argument("--backup") is { } destination)
{
    new Store(data).Backup(Path.GetFullPath(destination));
    return;
}
if (Argument("--write-default-config") is { } output)
{
    using var file = new FileStream(output, FileMode.CreateNew);
    JsonSerializer.Serialize(file, new Settings(), Settings.Json);
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, Args = [] });
builder.Host.UseWindowsService(o => o.ServiceName = "HomeNetMonitor");
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(o =>
{
    o.Listen(IPAddress.Loopback, settings.Port);
    o.Limits.MaxRequestBodySize = 8192;
});
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(data);
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<Probes>();
builder.Services.AddSingleton<SpeedTests>();
builder.Services.AddSingleton<Collector>();
builder.Services.AddHostedService(s => s.GetRequiredService<Collector>());
builder.Services.AddSingleton<NestMonitor>();
builder.Services.AddHostedService(s => s.GetRequiredService<NestMonitor>());

var app = builder.Build();
var origin = $"http://127.0.0.1:{settings.Port}";
app.Use(async (context, next) =>
{
    var req = context.Request;
    if (!string.Equals(req.Host.Value, $"127.0.0.1:{settings.Port}", StringComparison.Ordinal) ||
        HttpMethods.IsPost(req.Method) &&
        (!req.Headers.Origin.ToString().Equals(origin, StringComparison.Ordinal) || !req.HasJsonContentType()))
    {
        context.Response.StatusCode = 403;
        return;
    }
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (Exception ex) when (!context.Response.HasStarted && ex is SqliteException or UnauthorizedAccessException or IOException)
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { error = "storage-unavailable" });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (Collector monitor, SpeedTests speed, NestMonitor nest) => new { live = monitor.Live, nest = nest.Live, speedEnabled = speed.Enabled, speedRunning = speed.Loaded, pingSeconds = settings.PingSeconds });
app.MapGet("/api/history", (long? from, long? to, string? series, string? network, Store db, NestMonitor nest) =>
{
    if (!Range(from, to, 7, out var start, out var end)) return Results.BadRequest();
    var now = Settings.Now;
    if (series?.Length > 256 || !WifiIdentity.Valid(network)) return Results.BadRequest();
    var points = end - start <= 43200000 && start > now - (settings.RawDays - 0.01) * 86400000L
        ? db.Points(start, end, settings, series: series ?? "gateway/icmp", network: network)
        : db.Points(start, end, settings, summary: true, series: series ?? "gateway/icmp", network: network);
    return Results.Ok(new
    {
        points, series = db.Series(), incidents = db.Records<Incident>("incident", start, end).Where(i => network == null || i.Network == network),
        wifi = db.Records<WifiChange>("wifi", start, end).Where(w => network == null || w.Previous == network || w.Current == network),
        comparison = Reports.Compare(db, settings, start, end), nest = Reports.Heating(db, nest.Device, start, end),
        annotations = db.Records<Annotation>("annotation", start, end), gaps = db.Records<Gap>("gap", start, end),
        speeds = db.Records<SpeedRun>("speed", start, end).Where(s => network == null || s.Network == network).Select(s => s.End == null && now - s.Start > 60000 ? s with { Status = "interrupted" } : s)
    });
});
app.MapGet("/api/incident/{id:long}", (long id, Store db) =>
    db.Records<Snapshot>("snapshot", id - 1, Settings.Now + 1).Where(s => s.IncidentId == id).ToArray());
app.MapPost("/api/annotations", (NewAnnotation form, Store db) =>
{
    if (form.Kind is not ("hvac-start" or "hvac-stop" or "equipment" or "ups" or "note") ||
        form.At < Settings.Now - settings.HistoryDays * 86400000L || form.At > Settings.Now + 60000 ||
        form.Note?.Length > 500) return Results.BadRequest(new { error = "invalid-annotation" });
    return Results.Ok(db.Add(form.At, form.Kind, form.Note?.Trim() ?? ""));
});
app.MapGet("/api/correlation", (long? from, long? to, string? network, Store db, NestMonitor nest) =>
    WifiIdentity.Valid(network) && Range(from, to, 7, out var start, out var end) ? Results.Ok(Reports.Correlate(db, settings, start, end, network, nest.Device)) : Results.BadRequest());
app.MapPost("/api/speed/run", (SpeedTests speed) =>
{
    var error = speed.Manual();
    return error == null ? Results.Accepted() : Results.Conflict(new { error });
});
app.MapGet("/export/{kind}.csv", (string kind, long? from, long? to, string? network, Store db, NestMonitor nest) =>
    WifiIdentity.Valid(network) && Range(from, to, 31, out var start, out var end) && Reports.CsvKinds.Contains(kind)
        ? Results.Stream(stream => Reports.Csv(stream, db, settings, kind, start, end, network, nest.Device), "text/csv; charset=utf-8", kind + ".csv")
        : Results.BadRequest());
app.MapGet("/report", (long? from, long? to, string? network, Store db, NestMonitor nest) =>
    WifiIdentity.Valid(network) && Range(from, to, 31, out var start, out var end)
        ? Results.Content(Reports.Html(db, settings, start, end, network, nest.Device), "text/html; charset=utf-8")
        : Results.BadRequest());
await app.RunAsync();

string? Argument(string key)
{
    var index = Array.IndexOf(args, key);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static bool Range(long? from, long? to, int maxDays, out long start, out long end)
{
    var now = Settings.Now;
    end = to ?? now + 1;
    start = from ?? end - 3600000;
    return start >= 946684800000 && end <= now + 60000 && end > start && end - start <= maxDays * 86400000L;
}

public sealed record NewAnnotation(long At, string Kind, string? Note);
