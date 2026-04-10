namespace K2sDownloaderWinForms.Core;

/// <summary>
/// Runs a background loop that periodically fetches and validates new proxies from
/// the public proxy source, then merges any newly-working ones into the active
/// <see cref="Downloader"/> instance (and keeps them in <see cref="KnownProxies"/>
/// for the next download session).
/// </summary>
public sealed class ProxyRefreshService : IDisposable
{
    public event Action<string>? StatusChanged;

    private readonly Func<Downloader?> _getDownloader;
    private readonly HashSet<string>  _knownProxies = new();
    private readonly object           _knownLock    = new();
    private CancellationTokenSource?  _cts;
    private Task?                     _loopTask;

    public ProxyRefreshService(Func<Downloader?> getDownloader)
    {
        _getDownloader = getDownloader;
    }

    /// <summary>All proxies ever validated by this service (non-null, no sentinel).</summary>
    public IReadOnlyList<string> KnownProxies
    {
        get { lock (_knownLock) return _knownProxies.ToList(); }
    }

    /// <summary>Starts the background loop. If already running, restarts it.</summary>
    public void Start(int intervalMinutes)
    {
        if (intervalMinutes <= 0) return;
        Stop();
        _cts      = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(intervalMinutes, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunLoopAsync(int intervalMinutes, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), ct); }
            catch (OperationCanceledException) { break; }

            await RefreshOnceAsync(ct);
        }
    }

    /// <summary>Runs a single fetch-validate-merge cycle immediately.</summary>
    public async Task RefreshOnceAsync(CancellationToken ct)
    {
        try
        {
            // Build the set of proxies we already know about.
            HashSet<string> existing;
            lock (_knownLock) existing = new HashSet<string>(_knownProxies);

            var dl = _getDownloader();
            if (dl != null)
                foreach (var p in dl.Proxies)
                    if (p != null) existing.Add(p);

            StatusChanged?.Invoke(
                $"[AutoRefresh] Scanning for new proxies (already known: {existing.Count})...");

            var newOnes = await ProxyManager.FetchAndValidateNewAsync(
                existing,
                AppSettings.Current.MaxProxies,
                msg => StatusChanged?.Invoke($"[AutoRefresh] {msg}"),
                ct,
                sourceUrls: AppSettings.Current.ProxySourceUrls);

            if (newOnes.Count == 0)
            {
                StatusChanged?.Invoke("[AutoRefresh] No new proxies found.");
                return;
            }

            lock (_knownLock)
                foreach (var p in newOnes) _knownProxies.Add(p);
            int merged = dl?.MergeProxies(newOnes) ?? 0;
            StatusChanged?.Invoke(merged > 0
                ? $"[AutoRefresh] +{newOnes.Count} new proxy(ies) discovered; {merged} merged into active download."
                : $"[AutoRefresh] +{newOnes.Count} new proxy(ies) saved for next download.");

            // Persist the expanded known-proxies set to disk so background discoveries survive restarts.
            try
            {
                List<string> toSave;
                lock (_knownLock) toSave = _knownProxies.ToList();
                ProxyManager.SaveCache(toSave);
                StatusChanged?.Invoke($"[AutoRefresh] Persisted {toSave.Count} proxy(ies) to proxies.txt");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"[AutoRefresh] Failed to persist proxies: {ex.Message}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"[AutoRefresh] Error: {ex.Message}");
        }
    }

    public void Dispose() => Stop();
}
