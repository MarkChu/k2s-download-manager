using K2sDownloaderWeb.Hubs;
using K2sDownloaderWinForms.Core;
using Microsoft.AspNetCore.SignalR;

namespace K2sDownloaderWeb.Services;

public class ProxyService
{
    private readonly IHubContext<DownloadHub> _hub;
    private volatile bool _isRefreshing = false;
    public bool IsRefreshing => _isRefreshing;

    public ProxyService(IHubContext<DownloadHub> hub)
    {
        _hub = hub;
    }

    public List<string> GetCached() => ProxyManager.LoadCache();

    /// <summary>
    /// Fetches and validates proxies in the background, streaming progress via SignalR.
    /// No-op if a refresh is already running.
    /// </summary>
    public Task StartRefreshAsync(CancellationToken ct)
    {
        if (_isRefreshing) return Task.CompletedTask;
        _ = RunRefreshAsync(ct);
        return Task.CompletedTask;
    }

    private async Task RunRefreshAsync(CancellationToken ct)
    {
        _isRefreshing = true;
        await _hub.Clients.All.SendAsync("ProxyStateChanged", true, 0, CancellationToken.None);
        try
        {
            var settings = AppSettings.Load();
            var proxies = await ProxyManager.GetWorkingProxiesAsync(
                refresh: true,
                recheckCached: false,
                maxCandidates: null,
                statusCallback: msg => _ = _hub.Clients.All.SendAsync("ProxyLog", msg, CancellationToken.None),
                ct,
                sourceUrls: settings.ProxySourceUrls);

            var workingCount = proxies.Count(p => p != null);
            await _hub.Clients.All.SendAsync("ProxyStateChanged", false, workingCount, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await _hub.Clients.All.SendAsync("ProxyLog", "Proxy refresh cancelled.", CancellationToken.None);
            await _hub.Clients.All.SendAsync("ProxyStateChanged", false, GetCached().Count, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await _hub.Clients.All.SendAsync("ProxyLog", $"Proxy refresh error: {ex.Message}", CancellationToken.None);
            await _hub.Clients.All.SendAsync("ProxyStateChanged", false, GetCached().Count, CancellationToken.None);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>Overwrites the cache with the provided list (used for manual edits).</summary>
    public void Save(IEnumerable<string> proxies) => ProxyManager.SaveCache(proxies);

    public void Clear() => ProxyManager.SaveCache(Array.Empty<string>());
}
