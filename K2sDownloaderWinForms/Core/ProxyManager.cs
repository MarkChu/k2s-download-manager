using System.Collections.Concurrent;
using System.Net;
using System.IO;

namespace K2sDownloaderWinForms.Core;

public static class ProxyManager
{
    private const string CachePath = "proxies.txt";
    private const string ValidationUrl = "https://keep2share.cc/api/v2/test";

    private static readonly List<string> DefaultSourceUrls = new()
    {
        "https://api.proxyscrape.com/v4/free-proxy-list/get?request=getproxies&protocol=all&timeout=10000&country=all&ssl=all&anonymity=all&limit=2000",
        "https://cdn.jsdelivr.net/gh/proxifly/free-proxy-list@main/proxies/protocols/http/data.txt"
    };
    private const int BatchSize = 50;
    private const int ValidationTimeoutSeconds = 5;
    private const int ProxyValidationAttempts = 1;
    private const int ProxyValidationRetryDelayMs = 250;

    /// <summary>
    /// Returns a list of working proxy strings (null = direct connection is always first).
    /// </summary>
    public static async Task<List<string?>> GetWorkingProxiesAsync(
        bool refresh,
        bool recheckCached,
        int? maxCandidates,
        Action<string>? statusCallback,
        CancellationToken ct,
        IEnumerable<string>? sourceUrls = null)
    {
        var cached = LoadCache();

        if (!refresh && !recheckCached && cached.Count > 0)
            return Prepend(null, cached);

        var candidates = new List<string>();

        if (recheckCached && cached.Count > 0)
        {
            statusCallback?.Invoke($"Revalidating {cached.Count} cached proxies...");
            candidates.AddRange(cached);
        }

        bool fetchRemote = recheckCached
            ? cached.Count == 0
            : (refresh || cached.Count == 0);

        if (fetchRemote)
        {
            var urls = (sourceUrls?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList()
                        is { Count: > 0 } u) ? u : DefaultSourceUrls;

            statusCallback?.Invoke($"Fetching proxy candidates from {urls.Count} source(s)...");
            candidates.AddRange(await FetchFromSourcesAsync(urls, statusCallback, ct));
        }

        // Deduplicate
        candidates = candidates
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeProxy)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();

        if (candidates.Count == 0)
        {
            statusCallback?.Invoke("No proxy candidates available.");
            return cached.Count > 0 ? Prepend(null, cached) : new List<string?> { null };
        }

        if (maxCandidates.HasValue && candidates.Count > maxCandidates.Value)
        {
            candidates = candidates.Take(maxCandidates.Value).ToList();
            statusCallback?.Invoke($"Validating first {candidates.Count} proxies (limit {maxCandidates.Value})...");
        }
        else
        {
            statusCallback?.Invoke($"Validating {candidates.Count} proxies...");
        }

        var working = await ValidateProxiesAsync(candidates, statusCallback, ct);

        if (working.Count == 0)
        {
            statusCallback?.Invoke("No proxies passed HTTPS validation.");
            SaveCache(new List<string>());
            return new List<string?> { null };
        }

        // Shuffle to avoid always starting from the same proxies
        var shuffled = working.OrderBy(_ => Random.Shared.Next()).ToList();

        // Persist normalized working proxies
        SaveCache(shuffled);
        statusCallback?.Invoke($"Found {shuffled.Count} working proxies.");
        return Prepend(null, shuffled);
    }

    private static async Task<List<string>> ValidateProxiesAsync(
        List<string> candidates,
        Action<string>? statusCallback,
        CancellationToken ct)
    {
        var working = new ConcurrentBag<string>();
        var validated = 0;
        int total = candidates.Count;

        var batches = candidates
            .Chunk(BatchSize)
            .ToList();

        int batchIndex = 0;
        foreach (var batch in batches)
        {
            if (ct.IsCancellationRequested) break;
            batchIndex++;

            var batchWorking = 0;
            var tasks = batch.Select(async proxy =>
            {
                try
                {
                    bool success = false;
                    Exception? lastEx = null;
                    for (int attempt = 1; attempt <= ProxyValidationAttempts && !success && !ct.IsCancellationRequested; attempt++)
                    {
                        try
                        {
                            var handler = new HttpClientHandler
                            {
                                Proxy = new WebProxy($"http://{proxy}"),
                                UseProxy = true,
                                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                            };
                            using var client = new HttpClient(handler)
                            {
                                Timeout = TimeSpan.FromSeconds(ValidationTimeoutSeconds)
                            };
                            using var resp = await client.PostAsync(ValidationUrl, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
                            if (resp.IsSuccessStatusCode)
                            {
                                working.Add(proxy);
                                Interlocked.Increment(ref batchWorking);
                                success = true;
                                break;
                            }
                            else
                            {
                                lastEx = new HttpRequestException($"Status code {((int)resp.StatusCode)}");
                            }
                        }
                        catch (Exception ex)
                        {
                            lastEx = ex;
                        }

                        if (!success && attempt < ProxyValidationAttempts)
                        {
                            try { await Task.Delay(ProxyValidationRetryDelayMs, ct).ConfigureAwait(false); } catch { }
                        }
                    }

                    // keep per-proxy failures silent to avoid verbose logs
                }
                finally
                {
                    Interlocked.Increment(ref validated);
                }
            });

            await Task.WhenAll(tasks);

            // Report progress after every batch
            int pct = (int)Math.Round(validated * 100.0 / total);
            statusCallback?.Invoke(
                $"Validating... {validated}/{total} ({pct}%) — {working.Count} working so far");

            var delay = Random.Shared.Next(200, 400);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        return working.ToList();
    }

    /// <summary>
    /// Fetches the remote proxy list, skips any in <paramref name="existing"/>,
    /// validates the remainder, and returns only the newly-working proxies.
    /// Used by the background auto-refresh service.
    /// </summary>
    public static async Task<List<string>> FetchAndValidateNewAsync(
        HashSet<string> existing,
        int? maxCandidates,
        Action<string>? statusCallback,
        CancellationToken ct,
        IEnumerable<string>? sourceUrls = null)
    {
        var urls = (sourceUrls?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList()
                    is { Count: > 0 } u) ? u : DefaultSourceUrls;

        var fetched = await FetchFromSourcesAsync(urls, statusCallback, ct);
        if (fetched.Count == 0) return new List<string>();

        var existingNormalized = new HashSet<string>(existing.Select(NormalizeProxy));
        var candidates = fetched
            .Where(s => !string.IsNullOrWhiteSpace(s) && !existingNormalized.Contains(s))
            .Distinct()
            .ToList();

        if (maxCandidates.HasValue && candidates.Count > maxCandidates.Value)
            candidates = candidates.Take(maxCandidates.Value).ToList();

        if (candidates.Count == 0) return new List<string>();

        statusCallback?.Invoke($"Validating {candidates.Count} new proxy candidates...");
        return await ValidateProxiesAsync(candidates, statusCallback, ct);
    }

    /// <summary>Fetches and merges proxy lines from all given URLs.</summary>
    private static async Task<List<string>> FetchFromSourcesAsync(
        List<string> urls,
        Action<string>? statusCallback,
        CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var candidates = new List<string>();

        foreach (var url in urls)
        {
            try
            {
                var text = await client.GetStringAsync(url, ct);
                if (string.IsNullOrWhiteSpace(text) ||
                    text.Contains("Invalid API request", StringComparison.OrdinalIgnoreCase))
                {
                    statusCallback?.Invoke($"[Source] {url} returned no usable data.");
                    continue;
                }
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeProxy)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();
                candidates.AddRange(lines);
                statusCallback?.Invoke($"[Source] {url} → {lines.Count} proxies");
            }
            catch (Exception ex)
            {
                statusCallback?.Invoke($"[Source] {url} failed: {ex.Message}");
            }
        }

        return candidates
            .Select(NormalizeProxy)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();
    }

    public static List<string> LoadCache()
    {
        if (!File.Exists(CachePath)) return new List<string>();
        return File.ReadAllLines(CachePath)
            .Select(NormalizeProxy)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Persist the given proxies to the cache file (`proxies.txt`).
    /// Overwrites the existing cache with the provided list (one per line).
    /// </summary>
    public static void SaveCache(IEnumerable<string> proxies)
    {
        try
        {
            var lines = proxies
                .Select(NormalizeProxy)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct();
            File.WriteAllText(CachePath, string.Join('\n', lines));
        }
        catch
        {
            // Best-effort persistence; ignore failures here to avoid crashing background refresh.
        }
    }

    private static string NormalizeProxy(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        s = s.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(7);
        else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(8);
        // Strip any trailing path or slash
        int idx = s.IndexOfAny(new[] { '/', '\\', '?', '#' });
        if (idx >= 0) s = s.Substring(0, idx);
        return s.Trim();
    }

    private static List<string?> Prepend(string? first, IEnumerable<string> rest)
    {
        var result = new List<string?> { first };
        result.AddRange(rest.Cast<string?>());
        return result;
    }
}
