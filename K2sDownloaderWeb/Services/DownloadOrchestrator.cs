using K2sDownloaderWeb.Hubs;
using K2sDownloaderWeb.Models;
using K2sDownloaderWinForms.Core;
using Microsoft.AspNetCore.SignalR;

namespace K2sDownloaderWeb.Services;

public class DownloadOrchestrator : BackgroundService
{
    private readonly QueueService _queue;
    private readonly IHubContext<DownloadHub> _hub;
    private readonly ILogger<DownloadOrchestrator> _logger;

    private volatile bool _isProcessing = false;
    private CancellationTokenSource? _currentCts;
    private Guid? _currentItemId;

    private readonly Dictionary<string, TaskCompletionSource<string>> _pendingCaptchas = new();
    private readonly object _captchaLock = new();

    // Throttle progress pushes to avoid flooding SignalR
    private long _lastProgressTick;
    private const int ProgressThrottleMs = 250;

    public bool IsProcessing  => _isProcessing;
    public Guid? CurrentItemId => _currentItemId;

    public DownloadOrchestrator(
        QueueService queue,
        IHubContext<DownloadHub> hub,
        ILogger<DownloadOrchestrator> logger)
    {
        _queue  = queue;
        _hub    = hub;
        _logger = logger;
    }

    public void StartProcessing()
    {
        _isProcessing = true;
        _ = _hub.Clients.All.SendAsync("ProcessingStateChanged", true);
    }

    public void StopProcessing()
    {
        _isProcessing = false;
        _currentCts?.Cancel();
        _ = _hub.Clients.All.SendAsync("ProcessingStateChanged", false);
    }

    public void CancelCurrent() => _currentCts?.Cancel();

    public bool TryResolveCaptcha(string captchaId, string answer)
    {
        lock (_captchaLock)
        {
            if (!_pendingCaptchas.TryGetValue(captchaId, out var tcs)) return false;
            tcs.TrySetResult(answer);
            _pendingCaptchas.Remove(captchaId);
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_isProcessing)
            {
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                continue;
            }

            var item = _queue.GetNextPending();
            if (item is null)
            {
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                continue;
            }

            _currentItemId = item.Id;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _currentCts = cts;
            _lastProgressTick = 0;

            await DownloadItemAsync(item, cts.Token).ConfigureAwait(false);

            _currentCts = null;
            _currentItemId = null;
        }
    }

    private async Task DownloadItemAsync(QueueItem item, CancellationToken ct)
    {
        var settings = AppSettings.Load();
        var downloader = new Downloader();

        downloader.StatusChanged += msg =>
            _ = _hub.Clients.All.SendAsync("Log", item.Id, msg, CancellationToken.None);

        downloader.ProgressChanged += (downloaded, total, done, totalParts) =>
        {
            var now = Environment.TickCount64;
            if (now - _lastProgressTick < ProgressThrottleMs) return;
            _lastProgressTick = now;
            _ = _hub.Clients.All.SendAsync("Progress", item.Id, downloaded, total, done, totalParts, CancellationToken.None);
        };

        _queue.UpdateStatus(item.Id, QueueStatus.Downloading);
        await _hub.Clients.All.SendAsync("ItemStatusChanged", item.Id, "Downloading", (string?)null, (string?)null, CancellationToken.None);

        CaptchaCallback captchaCallback = async (imageBytes, challenge, captchaUrl) =>
        {
            // 1. Try Gemini auto-solve
            if (!string.IsNullOrWhiteSpace(settings.GeminiApiKey))
            {
                for (int attempt = 1; attempt <= settings.AutoSolveAttempts; attempt++)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var result = await GeminiClient.SolveCaptchaAsync(imageBytes, settings.GeminiApiKey, ct);
                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            await _hub.Clients.All.SendAsync("Log", item.Id, $"[Gemini] Auto-solved captcha (attempt {attempt})", CancellationToken.None);
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        await _hub.Clients.All.SendAsync("Log", item.Id, $"[Gemini] Attempt {attempt} failed: {ex.Message}", CancellationToken.None);
                    }
                    if (attempt < settings.AutoSolveAttempts)
                        await Task.Delay(settings.AutoSolveBaseDelayMs, ct);
                }
            }

            // 2. Fall back to manual solve via web UI
            var captchaId    = Guid.NewGuid().ToString("N");
            var tcs          = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_captchaLock) _pendingCaptchas[captchaId] = tcs;

            var imageBase64 = Convert.ToBase64String(imageBytes);
            await _hub.Clients.All.SendAsync("CaptchaRequired", item.Id, captchaId, imageBase64, captchaUrl, CancellationToken.None);

            using var reg = ct.Register(() => tcs.TrySetCanceled());
            try   { return await tcs.Task; }
            finally { lock (_captchaLock) _pendingCaptchas.Remove(captchaId); }
        };

        try
        {
            var outFile   = await downloader.DownloadAsync(
                item.Url, item.Filename,
                settings.Threads, settings.SplitSizeMb * 1024 * 1024,
                settings.FfmpegCheck, captchaCallback, ct);

            var shortName = Path.GetFileName(outFile);
            _queue.UpdateStatus(item.Id, QueueStatus.Done, outputFile: shortName);
            await _hub.Clients.All.SendAsync("ItemStatusChanged", item.Id, "Done", shortName, (string?)null, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _queue.UpdateStatus(item.Id, QueueStatus.Cancelled);
            await _hub.Clients.All.SendAsync("ItemStatusChanged", item.Id, "Cancelled", (string?)null, (string?)null, CancellationToken.None);
        }
        catch (DownloadCancelledException)
        {
            _queue.UpdateStatus(item.Id, QueueStatus.Cancelled);
            await _hub.Clients.All.SendAsync("ItemStatusChanged", item.Id, "Cancelled", (string?)null, (string?)null, CancellationToken.None);
        }
        catch (PermanentException ex)
        {
            _logger.LogWarning("Permanent error for {Url}: {Message}", item.Url, ex.Message);
            _queue.UpdateStatus(item.Id, QueueStatus.Failed, ex.Message);
            await _hub.Clients.All.SendAsync("ItemStatusChanged", item.Id, "Failed", (string?)null, ex.Message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed for {Url}", item.Url);
            _queue.UpdateStatus(item.Id, QueueStatus.Failed, ex.Message);
            await _hub.Clients.All.SendAsync("ItemStatusChanged", item.Id, "Failed", (string?)null, ex.Message, CancellationToken.None);
        }
    }
}
