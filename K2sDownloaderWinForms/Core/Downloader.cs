using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace K2sDownloaderWinForms.Core;

public class DownloadCancelledException : Exception
{
    public DownloadCancelledException() : base("Download cancelled.") { }
}

/// <summary>
/// Thrown for errors that are permanent and should not be retried
/// (e.g. premium-only file, file not found).
/// </summary>
public class PermanentException : Exception
{
    public PermanentException(string message) : base(message) { }
}

public class ChunkActivity
{
    public int    Index;
    public long   BytesTotal;
    public long   ExistingBytes;  // bytes already on disk before this attempt (set once)
    public string? Proxy;
    public DateTime StartTime;
    public long   BytesDone;      // new bytes written in this attempt (Interlocked)
}

public class Downloader
{
    // ── Events ───────────────────────────────────────────────────────────────
    public event Action<string>? StatusChanged;
    public event Action<long, long, int, int>? ProgressChanged;  // downloaded, total, done, totalParts
    public event Action<List<string?>, List<int>>? ProxyStateChanged;  // proxies, activeIndexes

    // ── State ────────────────────────────────────────────────────────────────
    public List<string?> Proxies { get; private set; } = new();
    private readonly List<int> _workingProxyIndexes = new();
    private readonly HashSet<int> _activeProxyIndexes = new();
    private readonly object _proxyLock = new();

    private long _bytesDownloaded;
    private long _totalBytes;
    private int _doneCount;
    private int _rangesTotal;

    private const string TmpDir = "tmp";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/107.0.0.0 Safari/537.36";
    private const int MaxChunkRetries = 3;
    // If no response/data is received for this many seconds, consider the proxy unresponsive and switch.
    private const int NoResponseTimeoutSeconds = 5;
    // Retry limits for higher-level operations (proxy refresh happens between each attempt).
    private const int GetFileNameMaxRetries = 3;
    private const int UrlGenMaxRetries      = 3;
    private const int CoreDownloadRetries   = 3;

    public ConcurrentDictionary<int, ChunkActivity> ActiveChunks { get; } = new();

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".flv", ".wmv", ".webm",
        ".mpg", ".mpeg", ".m4v", ".mp3", ".aac", ".wav", ".flac", ".ogg"
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the proxy list before the first <see cref="DownloadAsync"/> call so the
    /// downloader skips its own fetch. Call this immediately after construction.
    /// </summary>
    public void SetInitialProxies(IEnumerable<string> proxies)
    {
        var list = new List<string?> { null };
        list.AddRange(proxies.Select(p => (string?)p));
        Proxies = list;
        lock (_proxyLock)
        {
            _workingProxyIndexes.Clear();
            _activeProxyIndexes.Clear();
        }
    }

    /// <summary>
    /// Thread-safe. Adds proxies not already present in <see cref="Proxies"/>.
    /// Returns the count of proxies actually added.
    /// </summary>
    public int MergeProxies(IEnumerable<string> newProxies)
    {
        lock (_proxyLock)
        {
            var existing = new HashSet<string?>(Proxies);
            int added = 0;
            foreach (var p in newProxies)
            {
                if (!string.IsNullOrWhiteSpace(p) && !existing.Contains(p))
                {
                    Proxies.Add(p);
                    existing.Add(p);
                    added++;
                }
            }
            if (added > 0) NotifyProxyState();
            return added;
        }
    }

    public async Task RefreshProxiesAsync(
        bool refresh = false, bool recheckCached = false,
        int? maxCandidates = null, CancellationToken ct = default)
    {
        Proxies = await ProxyManager.GetWorkingProxiesAsync(
            refresh, recheckCached, maxCandidates,
            msg => StatusChanged?.Invoke(msg), ct);
        lock (_proxyLock)
        {
            _workingProxyIndexes.Clear();
            _activeProxyIndexes.Clear();
        }
        NotifyProxyState();
    }

    public async Task<string> DownloadAsync(
        string url,
        string? filename,
        int threads,
        int splitSizeBytes,
        bool ensureMediaCheck,
        CaptchaCallback captchaCallback,
        CancellationToken ct)
    {
        if (splitSizeBytes < 5 * 1024 * 1024)
            throw new ArgumentException("Split size must be at least 5 MB.");

        if (Proxies.Count == 0)
            await RefreshProxiesAsync(ct: ct);  // refresh=false: uses proxies.txt cache if available

        var fileId = ExtractFileId(url);

        // ── 1. File info — retry on transient network errors ──────────────────
        StatusChanged?.Invoke("Fetching file info...");
        var originalName = await RetryAsync(
            () => K2sClient.GetFileNameAsync(fileId, ct),
            maxAttempts: GetFileNameMaxRetries, baseDelayMs: 2000,
            label: "GetFileName", ct);
        var resolvedName = ResolveFilename(filename, originalName);

        // ── 2. URL generation — retry with proxy refresh on each failure ──────
        var urlTuples = await FetchUrlsWithRetryAsync(fileId, threads, captchaCallback, ct);

        // Summarize URL->proxy mapping for UI
        try
        {
            var map = new Dictionary<string, int?>();
            foreach (var (_, p) in urlTuples)
            {
                var key = p ?? "LOCAL";
                if (!map.ContainsKey(key)) map[key] = 0;
                map[key] = map[key] + 1;
            }
            var parts = map.Select(kv => kv.Key == "LOCAL" ? $"LOCAL:{kv.Value}" : $"{kv.Key}:{kv.Value}");
            StatusChanged?.Invoke($"Generated {urlTuples.Count} URLs — mapping: {string.Join(", ", parts)}");
        }
        catch { }

        // ── 3. Download core — retry with proxy refresh + URL re-gen on failure
        bool mediaCorrectionRetried = false;
        int currentSplit = splitSizeBytes;

        for (int coreAttempt = 1; ; coreAttempt++)
        {
            ct.ThrowIfCancellationRequested();
            Exception? coreEx = null;

            try
            {
                await DownloadCoreAsync(urlTuples, resolvedName, threads, currentSplit, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (DownloadCancelledException) { throw; }
            catch (Exception ex) { coreEx = ex; }

            if (coreEx != null)
            {
                if (coreAttempt >= CoreDownloadRetries)
                    throw coreEx;

                int delay = 5000 * coreAttempt;
                StatusChanged?.Invoke(
                    $"Download attempt {coreAttempt} failed: {coreEx.Message}. " +
                    $"Refreshing proxies and retrying in {delay / 1000}s...");
                await RefreshProxiesAsync(refresh: true, ct: ct);
                await Task.Delay(delay, ct);
                urlTuples = await FetchUrlsWithRetryAsync(fileId, threads, captchaCallback, ct);
                continue;
            }

            // Core succeeded — optional media integrity check
            if (ct.IsCancellationRequested)
                throw new DownloadCancelledException();

            if (ensureMediaCheck && IsMediaFile(resolvedName) && FfmpegAvailable())
            {
                if (!CheckMedia(resolvedName))
                {
                    if (!mediaCorrectionRetried)
                    {
                        StatusChanged?.Invoke("Video appears corrupted. Retrying with a larger chunk size...");
                        mediaCorrectionRetried = true;
                        currentSplit *= 2;
                        continue;
                    }
                    StatusChanged?.Invoke("Video is still corrupted after retry.");
                }
            }
            break;
        }

        return resolvedName;
    }

    // ── Retry helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="operation"/> up to <paramref name="maxAttempts"/> times
    /// with exponential back-off starting at <paramref name="baseDelayMs"/> ms.
    /// </summary>
    private async Task<T> RetryAsync<T>(
        Func<Task<T>> operation, int maxAttempts, int baseDelayMs,
        string label, CancellationToken ct)
    {
        Exception? last = null;
        for (int i = 1; i <= maxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await operation(); }
            catch (OperationCanceledException) { throw; }
            catch (PermanentException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (i < maxAttempts)
                {
                    int delay = baseDelayMs * (1 << (i - 1));
                    StatusChanged?.Invoke(
                        $"{label}: attempt {i}/{maxAttempts} failed ({ex.Message}), retrying in {delay}ms...");
                    await Task.Delay(delay, ct);
                }
            }
        }
        throw last!;
    }

    /// <summary>
    /// Calls <see cref="K2sClient.GenerateDownloadUrlsAsync"/>, refreshing the proxy pool
    /// between attempts on failure.
    /// </summary>
    private async Task<List<(string Url, string? Proxy)>> FetchUrlsWithRetryAsync(
        string fileId, int threads, CaptchaCallback captchaCallback, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= UrlGenMaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                StatusChanged?.Invoke(attempt == 1
                    ? "Generating download URLs..."
                    : $"Re-generating download URLs (attempt {attempt}/{UrlGenMaxRetries})...");
                return await K2sClient.GenerateDownloadUrlsAsync(
                    fileId, threads, Proxies, captchaCallback,
                    msg => StatusChanged?.Invoke(msg), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < UrlGenMaxRetries)
                {
                    int delay = 3000 * attempt;
                    StatusChanged?.Invoke(
                        $"URL generation attempt {attempt} failed: {ex.Message}. " +
                        $"Refreshing proxies, retrying in {delay / 1000}s...");
                    await RefreshProxiesAsync(refresh: true, ct: ct);
                    await Task.Delay(delay, ct);
                }
            }
        }
        throw last!;
    }

    // ── Core download ─────────────────────────────────────────────────────────

    private async Task DownloadCoreAsync(
        List<(string Url, string? Proxy)> urls, string filename, int threads,
        int splitSizeBytes, CancellationToken ct)
    {
        // Determine file size via HEAD
        StatusChanged?.Invoke("Getting file size...");
        long totalSize = await GetFileSizeFromCandidatesAsync(urls, ct);
        _totalBytes = totalSize;
        _bytesDownloaded = 0;
        _doneCount = 0;

        int splitCount = Math.Max(1, (int)Math.Ceiling((double)totalSize / splitSizeBytes));
        var chunks = BuildChunks(totalSize, splitCount);
        _rangesTotal = chunks.Count;

        Directory.CreateDirectory(TmpDir);

        StatusChanged?.Invoke("Checking whether server supports Range requests...");
        var supportsRange = await SupportsRangeAsync(urls[0].Url, ct);
        StatusChanged?.Invoke(supportsRange
            ? "Server supports Range requests. Partial chunk resumes enabled."
            : "Server does not support Range requests. Partial chunks will be re-downloaded.");

        // Restore already-downloaded chunks
        int padWidth = splitCount.ToString().Length;
        foreach (var chunk in chunks)
        {
            var partPath = ChunkPath(filename, chunk.Index, padWidth);
            if (File.Exists(partPath))
            {
                long existing = new FileInfo(partPath).Length;
                if (existing >= chunk.ExpectedBytes)
                {
                    chunk.Done = true;
                    Interlocked.Add(ref _bytesDownloaded, chunk.ExpectedBytes);
                    Interlocked.Increment(ref _doneCount);
                }
                else if (!supportsRange || existing <= 0)
                {
                    File.Delete(partPath);
                }
                else
                {
                    Interlocked.Add(ref _bytesDownloaded, existing);
                }
            }
        }

        // Work queue
        var pending = new ConcurrentQueue<ChunkInfo>(chunks.Where(c => !c.Done));
        // Use int[] so async methods can share the counter without ref
        int[] pendingCount = { pending.Count };

        if (pendingCount[0] == 0)
        {
            AssembleFile(chunks, filename, padWidth);
            return;
        }

        StatusChanged?.Invoke($"Downloading {chunks.Count} parts using {urls.Count} connections...");

        int threadCount = Math.Min(urls.Count, threads);
        var workerTasks = Enumerable.Range(0, threadCount).Select(idx =>
            Task.Run(() => WorkerLoopAsync(urls[idx], idx, pending, pendingCount,
                filename, padWidth, ct), ct)).ToArray();

        await Task.WhenAll(workerTasks);

        if (ct.IsCancellationRequested)
            throw new DownloadCancelledException();

        var incompleteChunks = chunks.Where(c => !c.Done).ToList();
        if (incompleteChunks.Count > 0)
        {
            var missingParts = string.Join(", ", incompleteChunks.Select(c => c.Index));
            throw new Exception($"Download failed: {incompleteChunks.Count} part(s) could not be downloaded. Missing part index(es): {missingParts}");
        }

        // Reset active proxies display
        lock (_proxyLock) _activeProxyIndexes.Clear();
        NotifyProxyState();

        StatusChanged?.Invoke("Assembling file...");
        AssembleFile(chunks, filename, padWidth);
        StatusChanged?.Invoke($"Finished writing {filename}");
        StatusChanged?.Invoke($"File size: {HumanReadableBytes(new FileInfo(filename).Length)}");
    }

    private async Task WorkerLoopAsync(
        (string Url, string? Proxy) urlTuple, int workerIdx,
        ConcurrentQueue<ChunkInfo> queue,
        int[] pendingCount,
        string filename, int padWidth,
        CancellationToken ct)
    {
        while (Volatile.Read(ref pendingCount[0]) > 0 && !ct.IsCancellationRequested)
        {
            if (!queue.TryDequeue(out var chunk))
            {
                await Task.Delay(30, ct).ConfigureAwait(false);
                continue;
            }

            // Distribute workers across the proxy pool by worker index.
            // The proxy in urlTuple was used only to obtain the CDN URL via the API;
            // K2S CDN URLs are not IP-locked, so the actual download can go through any proxy.
            var sel = SelectProxy(workerIdx);
            int proxyIdx = sel.idx;
            string? proxy = sel.proxy;

            // If this chunk has already failed on some proxies, skip to a fresh one.
            if (chunk.FailedProxies.Count > 0)
            {
                lock (_proxyLock)
                {
                    for (int offset = 0; offset < Proxies.Count; offset++)
                    {
                        int tryIdx = (proxyIdx + offset) % Proxies.Count;
                        if (!chunk.FailedProxies.Contains(Proxies[tryIdx] ?? "LOCAL"))
                        {
                            proxyIdx = tryIdx;
                            proxy = Proxies[tryIdx];
                            break;
                        }
                    }
                }
            }

            lock (_proxyLock) _activeProxyIndexes.Add(proxyIdx);
            NotifyProxyState();

            ActiveChunks[chunk.Index] = new ChunkActivity
            {
                Index      = chunk.Index,
                BytesTotal = chunk.ExpectedBytes,
                Proxy      = proxy,
                StartTime  = DateTime.UtcNow,
            };

            bool success;
            try
            {
                success = await TryDownloadChunkAsync(urlTuple.Url, proxy, chunk, filename, padWidth, ct);
            }
            finally
            {
                ActiveChunks.TryRemove(chunk.Index, out _);
                lock (_proxyLock) _activeProxyIndexes.Remove(proxyIdx);
                NotifyProxyState();
            }

            if (success)
            {
                // Mark the chunk as completed so assembly logic recognizes it.
                chunk.Done = true;
                StatusChanged?.Invoke($"Part {chunk.Index}: completed (proxy={proxy ?? "LOCAL"})");
                if (proxy != null)
                {
                    lock (_proxyLock)
                    {
                        if (!_workingProxyIndexes.Contains(proxyIdx))
                        {
                            _workingProxyIndexes.Add(proxyIdx);
                            NotifyProxyState();
                        }
                    }
                }
                Interlocked.Decrement(ref pendingCount[0]);
                var done = Interlocked.Increment(ref _doneCount);
                ProgressChanged?.Invoke(
                    Volatile.Read(ref _bytesDownloaded), _totalBytes, done, _rangesTotal);
            }
            else if (!ct.IsCancellationRequested)
            {
                chunk.RetryCount++;
                if (chunk.RetryCount < MaxChunkRetries)
                {
                    StatusChanged?.Invoke($"Part {chunk.Index}: failed, retry {chunk.RetryCount}/{MaxChunkRetries} (proxy={proxy ?? "LOCAL"})");
                    queue.Enqueue(chunk);
                }
                else
                {
                    // Exhausted retries on this proxy — blacklist it and try the next one.
                    chunk.FailedProxies.Add(proxy ?? "LOCAL");
                    int freshCount = Proxies.Count(p => !chunk.FailedProxies.Contains(p ?? "LOCAL"));
                    if (freshCount > 0)
                    {
                        StatusChanged?.Invoke($"Part {chunk.Index}: exhausted {MaxChunkRetries} retries on {proxy ?? "LOCAL"}, rotating to next proxy ({chunk.FailedProxies.Count} proxy(ies) blacklisted)");
                        chunk.RetryCount = 0;
                        queue.Enqueue(chunk);
                    }
                    else
                    {
                        StatusChanged?.Invoke($"Part {chunk.Index}: failed on all {chunk.FailedProxies.Count} available proxy(ies), abandoning");
                        Interlocked.Decrement(ref pendingCount[0]);
                    }
                }
            }
            else
            {
                Interlocked.Decrement(ref pendingCount[0]);
            }
        }
    }

    private async Task<bool> TryDownloadChunkAsync(
        string url, string? proxy, ChunkInfo chunk,
        string filename, int padWidth, CancellationToken ct)
    {
        var partPath = ChunkPath(filename, chunk.Index, padWidth);

        HttpClientHandler? handler = null;
        if (proxy != null)
        {
            handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://{proxy}"),
                UseProxy = true,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
        }
        using var client = handler != null
            ? new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) }
            : new HttpClient { Timeout = TimeSpan.FromSeconds(25) };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        ActiveChunks.TryGetValue(chunk.Index, out var chunkAct);

        long existingBytes = 0;
        bool resume = false;
        if (File.Exists(partPath))
        {
            existingBytes = new FileInfo(partPath).Length;
            if (existingBytes >= chunk.ExpectedBytes)
                return true;
            if (existingBytes > 0 && existingBytes < chunk.ExpectedBytes)
                resume = true;
        }

        if (chunkAct != null)
            Volatile.Write(ref chunkAct.ExistingBytes, existingBytes);

        var requestStart = resume ? chunk.RangeStart + existingBytes : chunk.RangeStart;
        client.DefaultRequestHeaders.Range = new RangeHeaderValue(requestStart, chunk.RangeEnd);

        try
        {
            // Request headers and start response stream. If there's no response within
            // `NoResponseTimeoutSeconds`, cancel and treat as proxy failure to rotate.
            HttpResponseMessage response;
            try
            {
                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(TimeSpan.FromSeconds(NoResponseTimeoutSeconds));
                response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, reqCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                StatusChanged?.Invoke($"Part {chunk.Index}: no response from server within {NoResponseTimeoutSeconds}s (proxy={proxy ?? "LOCAL"})");
                return false;
            }

            if (resume)
            {
                if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    StatusChanged?.Invoke($"Part {chunk.Index}: expected PartialContent for resume but got {(int)response.StatusCode} {response.ReasonPhrase} (proxy={proxy ?? "LOCAL"})");
                    return false;
                }
            }
            else if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                StatusChanged?.Invoke($"Part {chunk.Index}: unexpected HTTP {(int)response.StatusCode} {response.ReasonPhrase} (proxy={proxy ?? "LOCAL"})");
                return false;
            }

            var mode = resume ? FileMode.Append : FileMode.Create;
            using var fileStream = new FileStream(partPath, mode, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);
            if (!resume)
                fileStream.SetLength(0);

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var readBuf = new byte[32 * 1024];
            int bytesRead;
            long downloaded = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(TimeSpan.FromSeconds(NoResponseTimeoutSeconds));
                    bytesRead = await stream.ReadAsync(readBuf.AsMemory(0, readBuf.Length), readCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Read timed out (no data within NoResponseTimeoutSeconds)
                    StatusChanged?.Invoke($"Part {chunk.Index}: no data for {NoResponseTimeoutSeconds}s, switching proxy (proxy={proxy ?? "LOCAL"})");
                    if (downloaded > 0)
                    {
                        Interlocked.Add(ref _bytesDownloaded, -downloaded);
                        if (chunkAct != null)
                            Interlocked.Add(ref chunkAct.BytesDone, -downloaded);
                    }
                    return false;
                }

                if (bytesRead <= 0) break;

                await fileStream.WriteAsync(readBuf, 0, bytesRead, ct);
                downloaded += bytesRead;
                Interlocked.Add(ref _bytesDownloaded, bytesRead);
                if (chunkAct != null)
                    Interlocked.Add(ref chunkAct.BytesDone, bytesRead);
                ProgressChanged?.Invoke(
                    Volatile.Read(ref _bytesDownloaded), _totalBytes,
                    Volatile.Read(ref _doneCount), _rangesTotal);
            }

            await fileStream.FlushAsync(ct);
            long finalLength = new FileInfo(partPath).Length;
            if (Math.Abs(finalLength - chunk.ExpectedBytes) > 1)
            {
                StatusChanged?.Invoke($"Part {chunk.Index}: final length mismatch: got {finalLength}, expected {chunk.ExpectedBytes} (proxy={proxy ?? "LOCAL"})");
                Interlocked.Add(ref _bytesDownloaded, -downloaded);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke($"Part {chunk.Index}: cancelled during download (proxy={proxy ?? "LOCAL"})");
            return false;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Part {chunk.Index}: error: {ex.Message} (proxy={proxy ?? "LOCAL"})");
            return false;
        }
    }

    // ── File assembly ─────────────────────────────────────────────────────────

    private static void AssembleFile(List<ChunkInfo> chunks, string filename, int padWidth)
    {
        if (File.Exists(filename)) File.Delete(filename);

        using var output = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: false);

        foreach (var chunk in chunks)
        {
            var partPath = ChunkPath(filename, chunk.Index, padWidth);
            if (!File.Exists(partPath))
                throw new Exception($"Missing part file: {partPath}");

            using var part = File.OpenRead(partPath);
            part.CopyTo(output);
        }

        // Cleanup parts
        foreach (var chunk in chunks)
        {
            var p = ChunkPath(filename, chunk.Index, padWidth);
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static string ChunkPath(string filename, int index, int padWidth)
    {
        var safeName = Path.GetFileName(filename);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "download";

        var hash = ComputeFilenameHash(filename);
        return Path.Combine(TmpDir, $"{safeName}.{hash}.part{index.ToString().PadLeft(padWidth, '0')}");
    }

    private static string ComputeFilenameHash(string filename)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(filename.ToLowerInvariant());
        var hash = sha.ComputeHash(bytes);
        return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
    }

    // ── Chunk range building ──────────────────────────────────────────────────

    private static List<ChunkInfo> BuildChunks(long total, int splitCount)
    {
        var chunks = new List<ChunkInfo>(splitCount);
        for (int i = 0; i < splitCount; i++)
        {
            long start = total * i / splitCount;
            long end = total * (i + 1) / splitCount - 1;
            chunks.Add(new ChunkInfo { Index = i, RangeStart = start, RangeEnd = end });
        }
        return chunks;
    }

    // ── Proxy helpers ──────────────────────────────────────────────────────────

    private (int idx, string? proxy) SelectProxy(int workerIdx)
    {
        lock (_proxyLock)
        {
            if (_workingProxyIndexes.Count > 0)
            {
                int idx = _workingProxyIndexes[workerIdx % _workingProxyIndexes.Count];
                return (idx, Proxies[idx]);
            }
            int fallback = workerIdx % Proxies.Count;
            return (fallback, Proxies[fallback]);
        }
    }

    private void NotifyProxyState()
    {
        List<string?> proxies;
        List<int> active;
        lock (_proxyLock)
        {
            proxies = new List<string?>(Proxies);
            active = new List<int>(_activeProxyIndexes);
        }
        ProxyStateChanged?.Invoke(proxies, active);
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private async Task<long> GetFileSizeFromCandidatesAsync(List<(string Url, string? Proxy)> urls, CancellationToken ct)
    {
        Exception? lastEx = null;
        foreach (var (url, proxy) in urls)
        {
            var label = proxy ?? "LOCAL";
            StatusChanged?.Invoke($"Getting file size from {label}...");
            try
            {
                var size = await GetFileSizeForUrlAsync(url, proxy, ct);
                StatusChanged?.Invoke($"Determined file size: {HumanReadableBytes(size)} (source={label})");
                return size;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Failed to get size from {label}: {ex.Message}");
                lastEx = ex;
            }
        }

        throw lastEx ?? new Exception("Could not determine file size from any URL.");
    }

    private async Task<long> GetFileSizeForUrlAsync(string url, string? proxy, CancellationToken ct)
    {
        HttpClient? client = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy($"http://{proxy}"),
                    UseProxy = true,
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                };
                client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            }
            else
            {
                client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            }

            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            // Try HEAD first
            try
            {
                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(TimeSpan.FromSeconds(15));
                var head = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, reqCts.Token);
                if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.PartialContent)
                {
                    if (resp.Content.Headers.ContentLength.HasValue && resp.Content.Headers.ContentLength.Value > 0)
                        return resp.Content.Headers.ContentLength.Value;
                    if (resp.Content.Headers.ContentRange != null && resp.Content.Headers.ContentRange.Length.HasValue)
                        return resp.Content.Headers.ContentRange.Length.Value;
                    if (resp.Headers.TryGetValues("Content-Range", out var crs) && ParseContentRange(crs.FirstOrDefault(), out var len))
                        return len;
                }
                else
                {
                    throw new Exception($"HEAD returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new Exception("No response (timeout) during HEAD");
            }

            // Fallback to GET range 0-0 to read Content-Range
            try
            {
                using var reqCts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts2.CancelAfter(TimeSpan.FromSeconds(15));
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(0, 0);
                using var resp2 = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts2.Token);
                if (resp2.IsSuccessStatusCode || resp2.StatusCode == System.Net.HttpStatusCode.PartialContent)
                {
                    if (resp2.Content.Headers.ContentRange != null && resp2.Content.Headers.ContentRange.Length.HasValue)
                        return resp2.Content.Headers.ContentRange.Length.Value;
                    if (resp2.Headers.TryGetValues("Content-Range", out var crs2) && ParseContentRange(crs2.FirstOrDefault(), out var len2))
                        return len2;
                    if (resp2.Content.Headers.ContentLength.HasValue && resp2.Content.Headers.ContentLength.Value > 0)
                        return resp2.Content.Headers.ContentLength.Value;
                }
                throw new Exception($"GET(range) returned {(int)resp2.StatusCode} {resp2.ReasonPhrase}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new Exception("No response (timeout) during GET range");
            }
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static bool ParseContentRange(string? header, out long length)
    {
        length = -1;
        if (string.IsNullOrWhiteSpace(header)) return false;
        try
        {
            // Expect format: bytes start-end/total
            var slash = header.IndexOf('/');
            if (slash < 0) return false;
            var total = header[(slash + 1)..].Trim();
            if (total == "*") return false;
            if (long.TryParse(total, out length)) return true;
        }
        catch { }
        return false;
    }

    private static async Task<long> GetFileSizeAsync(string url, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        var msg = new HttpRequestMessage(HttpMethod.Head, url);
        var resp = await client.SendAsync(msg, ct);
        if (resp.Content.Headers.ContentLength is long len && len > 0)
            return len;
        if (resp.Headers.TryGetValues("Content-Length", out var vals)
            && long.TryParse(vals.FirstOrDefault(), out var parsed))
            return parsed < 0 ? parsed + (long)Math.Pow(2, 32) : parsed;
        throw new Exception("Could not determine file size.");
    }

    private static async Task<bool> SupportsRangeAsync(string url, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        var msg = new HttpRequestMessage(HttpMethod.Head, url);
        msg.Headers.Range = new RangeHeaderValue(0, 0);

        try
        {
            using var resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.PartialContent)
                return true;
            if (resp.Headers.TryGetValues("Accept-Ranges", out var vals)
                && vals.Any(v => v.Contains("bytes", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        catch { }

        return false;
    }

    private static string ExtractFileId(string url)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            url, @"https?://(?:k2s\.cc|keep2share\.cc)/file/(.*?)(?:\?|/|$)");
        if (!match.Success) throw new ArgumentException("Invalid K2S URL.");
        return match.Groups[1].Value;
    }

    private static string ResolveFilename(string? userFilename, string originalName)
    {
        string name;
        if (string.IsNullOrWhiteSpace(userFilename))
        {
            name = originalName;
        }
        else
        {
            var ext = Path.GetExtension(userFilename);
            if (!string.IsNullOrEmpty(ext))
                name = userFilename;
            else
            {
                var origExt = Path.GetExtension(originalName);
                name = string.IsNullOrEmpty(origExt) ? userFilename : userFilename + origExt;
            }
        }

        // If the name is already a rooted path (user typed a full path), use as-is.
        if (Path.IsPathRooted(name)) return name;

        var dir = AppSettings.Current.EffectiveDownloadDirectory;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static bool IsMediaFile(string filename) =>
        MediaExtensions.Contains(Path.GetExtension(filename));

    private static bool FfmpegAvailable() =>
        !string.IsNullOrEmpty(FindInPath("ffmpeg"));

    private static bool CheckMedia(string filePath)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            Arguments = $"-i \"{filePath}\" -c copy -f null - -v warning",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = Process.Start(psi);
        if (proc == null) return false;
        proc.WaitForExit();
        return proc.ExitCode == 0 && proc.StandardError.ReadToEnd().Length == 0;
    }

    private static string? FindInPath(string exe)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        foreach (var dir in paths)
        {
            // Windows uses .exe extension; Linux/macOS use the bare executable name.
            var candidates = OperatingSystem.IsWindows()
                ? new[] { Path.Combine(dir, exe + ".exe"), Path.Combine(dir, exe) }
                : new[] { Path.Combine(dir, exe) };
            foreach (var full in candidates)
                if (File.Exists(full)) return full;
        }
        return null;
    }

    public static string HumanReadableBytes(long num)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double value = num;
        foreach (var unit in units)
        {
            if (value < 1024.0) return $"{value:0.000} {unit}";
            value /= 1024.0;
        }
        return $"{value:0.000} PB";
    }
}

// ── Data ──────────────────────────────────────────────────────────────────────

public class ChunkInfo
{
    public int Index { get; set; }
    public long RangeStart { get; set; }
    public long RangeEnd { get; set; }
    public long ExpectedBytes => RangeEnd - RangeStart + 1;
    public int RetryCount { get; set; }
    public bool Done { get; set; }
    /// <summary>Proxies (or "LOCAL") that have already exhausted all retries for this chunk.</summary>
    public HashSet<string> FailedProxies { get; } = new();
}
