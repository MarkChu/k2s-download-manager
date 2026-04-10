using System.Text.Json.Serialization;
using K2sDownloaderWeb.Hubs;
using K2sDownloaderWeb.Models;
using K2sDownloaderWeb.Services;
using K2sDownloaderWinForms.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<QueueService>();
builder.Services.AddSingleton<ProxyService>();
builder.Services.AddSingleton<DownloadOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DownloadOrchestrator>());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ── Queue API ─────────────────────────────────────────────────────────────────

app.MapGet("/api/queue", (QueueService q) => q.GetAll());

app.MapPost("/api/queue", (QueueService q, AddUrlRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Url))
        return Results.BadRequest("URL is required.");
    return Results.Ok(q.Add(req.Url, req.Filename));
});

app.MapDelete("/api/queue/{id:guid}", (QueueService q, Guid id) =>
    q.Remove(id) ? Results.Ok() : Results.BadRequest("Item is currently downloading or not found."));

app.MapPost("/api/queue/{id:guid}/retry", (QueueService q, Guid id) =>
    q.Retry(id) ? Results.Ok() : Results.BadRequest("Item cannot be retried in its current state."));

// ── Control API ───────────────────────────────────────────────────────────────

app.MapPost("/api/start", (DownloadOrchestrator d) => { d.StartProcessing(); return Results.Ok(); });
app.MapPost("/api/stop",  (DownloadOrchestrator d) => { d.StopProcessing();  return Results.Ok(); });
app.MapPost("/api/cancel-current", (DownloadOrchestrator d) => { d.CancelCurrent(); return Results.Ok(); });

app.MapGet("/api/status", (DownloadOrchestrator d) =>
    Results.Ok(new { d.IsProcessing, d.CurrentItemId }));

// ── Proxy API ─────────────────────────────────────────────────────────────────

app.MapGet("/api/proxies", (ProxyService p) => p.GetCached());

app.MapGet("/api/proxies/status", (ProxyService p) =>
    Results.Ok(new { p.IsRefreshing, count = p.GetCached().Count }));

app.MapPost("/api/proxies/refresh", (ProxyService p, HttpContext ctx) =>
{
    p.StartRefreshAsync(ctx.RequestAborted);
    return Results.Ok();
});

app.MapPut("/api/proxies", async (ProxyService p, HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    var lines = body.Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0);
    p.Save(lines);
    return Results.Ok(new { count = p.GetCached().Count });
});

app.MapDelete("/api/proxies", (ProxyService p) => { p.Clear(); return Results.Ok(); });

app.MapGet("/api/proxies/sources", () =>
{
    var s = AppSettings.Load();
    return Results.Ok(s.ProxySourceUrls);
});

app.MapPut("/api/proxies/sources", (List<string> urls) =>
{
    var s = AppSettings.Load();
    s.ProxySourceUrls = urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
    s.Save();
    return Results.Ok(new { count = s.ProxySourceUrls.Count });
});

// ── Version API ───────────────────────────────────────────────────────────────

app.MapGet("/api/version", () =>
{
    var ver = typeof(Program).Assembly.GetName().Version;
    var label = ver is null ? "dev" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
    return Results.Ok(new { version = label });
});

// ── Settings API ──────────────────────────────────────────────────────────────

app.MapGet("/api/settings", () =>
{
    var s = AppSettings.Load();
    return Results.Ok(new SettingsDto(
        s.GeminiApiKey, s.DownloadDirectory,
        s.Threads, s.SplitSizeMb, s.FfmpegCheck,
        s.MaxProxies, s.RevalidateProxies,
        s.DownloadMaxRetries, s.ProxyRefreshIntervalMin,
        s.AutoSolveAttempts, s.AutoSolvePerAttemptTimeoutSec,
        s.AutoSolveBaseDelayMs, s.AutoSolveMaxDelayMs));
});

app.MapPut("/api/settings", (SettingsDto dto) =>
{
    var s = AppSettings.Load();
    s.GeminiApiKey                  = dto.GeminiApiKey ?? s.GeminiApiKey;
    s.DownloadDirectory             = dto.DownloadDirectory ?? s.DownloadDirectory;
    s.Threads                       = dto.Threads ?? s.Threads;
    s.SplitSizeMb                   = dto.SplitSizeMb ?? s.SplitSizeMb;
    s.FfmpegCheck                   = dto.FfmpegCheck ?? s.FfmpegCheck;
    s.MaxProxies                    = dto.MaxProxies ?? s.MaxProxies;
    s.RevalidateProxies             = dto.RevalidateProxies ?? s.RevalidateProxies;
    s.DownloadMaxRetries            = dto.DownloadMaxRetries ?? s.DownloadMaxRetries;
    s.ProxyRefreshIntervalMin       = dto.ProxyRefreshIntervalMin ?? s.ProxyRefreshIntervalMin;
    s.AutoSolveAttempts             = dto.AutoSolveAttempts ?? s.AutoSolveAttempts;
    s.AutoSolvePerAttemptTimeoutSec = dto.AutoSolvePerAttemptTimeoutSec ?? s.AutoSolvePerAttemptTimeoutSec;
    s.AutoSolveBaseDelayMs          = dto.AutoSolveBaseDelayMs ?? s.AutoSolveBaseDelayMs;
    s.AutoSolveMaxDelayMs           = dto.AutoSolveMaxDelayMs ?? s.AutoSolveMaxDelayMs;
    s.Save();
    return Results.Ok();
});

// ── SignalR Hub ───────────────────────────────────────────────────────────────

app.MapHub<DownloadHub>("/hub");

app.Run();

record AddUrlRequest(string Url, string? Filename);

record SettingsDto(
    string? GeminiApiKey,
    string? DownloadDirectory,
    int?    Threads,
    int?    SplitSizeMb,
    bool?   FfmpegCheck,
    int?    MaxProxies,
    bool?   RevalidateProxies,
    int?    DownloadMaxRetries,
    int?    ProxyRefreshIntervalMin,
    int?    AutoSolveAttempts,
    int?    AutoSolvePerAttemptTimeoutSec,
    int?    AutoSolveBaseDelayMs,
    int?    AutoSolveMaxDelayMs);
