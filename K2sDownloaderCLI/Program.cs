using System.Text;
using K2sDownloaderWinForms.Core;

namespace K2sDownloaderCLI;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // ── Argument parsing ──────────────────────────────────────────────────
        string? url       = null;
        string? filename  = null;
        string? outputDir = null;
        string? geminiKey = null;
        int?    threads   = null;
        int?    splitMb   = null;
        int?    retries   = null;
        bool    noProxy   = false;
        bool    showHelp  = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h": case "--help":    showHelp  = true; break;
                case "--no-proxy":           noProxy   = true; break;
                case "-f": case "--filename":   filename  = Next(args, ref i); break;
                case "-o": case "--output-dir": outputDir = Next(args, ref i); break;
                case "-g": case "--gemini-key": geminiKey = Next(args, ref i); break;
                case "-t": case "--threads":
                    if (int.TryParse(Next(args, ref i), out var t)) threads = t; break;
                case "-s": case "--split-mb":
                    if (int.TryParse(Next(args, ref i), out var s)) splitMb = s; break;
                case "-r": case "--retries":
                    if (int.TryParse(Next(args, ref i), out var r)) retries = r; break;
                default:
                    if (!args[i].StartsWith('-') && url == null)
                        url = args[i];
                    break;
            }
        }

        if (showHelp || url == null)
        {
            PrintHelp();
            return url == null && !showHelp ? 1 : 0;
        }

        // ── Load settings ─────────────────────────────────────────────────────
        var settings = AppSettings.Load();
        if (!string.IsNullOrWhiteSpace(geminiKey))
            settings.GeminiApiKey = geminiKey;
        if (!string.IsNullOrWhiteSpace(outputDir))
            settings.DownloadDirectory = outputDir;

        int threadCount = threads  ?? settings.Threads;
        int splitBytes  = (splitMb ?? settings.SplitSizeMb) * 1024 * 1024;

        // ── Cancellation ──────────────────────────────────────────────────────
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nCancelling...");
            cts.Cancel();
        };

        // ── Downloader + proxy refresh service ────────────────────────────────
        var downloader = new Downloader();
        downloader.StatusChanged   += msg => { if (IsNoisyStatus(msg)) Status(msg); else Log(msg); };
        downloader.ProgressChanged += OnProgress;

        var refreshService = new ProxyRefreshService(() => downloader);
        refreshService.StatusChanged += msg => Log(msg);

        if (!noProxy && settings.ProxyRefreshIntervalMin > 0)
        {
            refreshService.Start(settings.ProxyRefreshIntervalMin);
            Log($"[AutoRefresh] Background proxy refresh started (every {settings.ProxyRefreshIntervalMin} min).");
        }

        // ── Captcha callback ──────────────────────────────────────────────────
        CaptchaCallback captchaCallback = async (imageBytes, challenge, captchaUrl) =>
        {
            // Try Gemini auto-solve first if a key is configured.
            if (!string.IsNullOrWhiteSpace(settings.GeminiApiKey))
            {
                for (int attempt = 1; attempt <= settings.AutoSolveAttempts; attempt++)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    try
                    {
                        Log($"[Gemini] Auto-solve attempt {attempt}/{settings.AutoSolveAttempts}...");
                        var result = await GeminiClient.SolveCaptchaAsync(
                            imageBytes, settings.GeminiApiKey, cts.Token);
                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            Log($"[Gemini] Answer: {result}");
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Gemini] Attempt {attempt} failed: {ex.Message}");
                    }
                    if (attempt < settings.AutoSolveAttempts)
                        await Task.Delay(settings.AutoSolveBaseDelayMs, cts.Token);
                }
                Log("[Gemini] All auto-solve attempts failed. Falling back to manual entry.");
            }

            // Save image to a temp file so the user can open it.
            var tmpPath = Path.Combine(Path.GetTempPath(), $"k2s_captcha_{Guid.NewGuid():N}.png");
            try { await File.WriteAllBytesAsync(tmpPath, imageBytes, cts.Token); }
            catch { tmpPath = "(could not save image)"; }

            Console.WriteLine();
            Console.WriteLine($"  Captcha image : {tmpPath}");
            Console.WriteLine($"  Captcha URL   : {captchaUrl}");
            Console.Write("  Enter captcha text: ");

            return Console.ReadLine()?.Trim() ?? string.Empty;
        };

        // ── Download (outer retry loop) ───────────────────────────────────────
        // Inner retries (GetFileName, URL gen, core chunks) are handled inside
        // Downloader.DownloadAsync. This outer loop covers whole-session failures
        // (e.g. unrecoverable proxy exhaustion, captcha service down) so the CLI
        // can recover without human intervention.
        int maxRetries = retries ?? settings.DownloadMaxRetries;
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Log($"Starting: {url}" + (attempt > 1 ? $" (session retry {attempt}/{maxRetries + 1})" : ""));
                    var outFile = await downloader.DownloadAsync(
                        url, filename, threadCount, splitBytes,
                        settings.FfmpegCheck, captchaCallback, cts.Token);

                    ResetProgress();
                    Log($"Done! Saved to: {outFile}");
                    return 0;
                }
                catch (OperationCanceledException) { throw; }
                catch (DownloadCancelledException) { throw; }
                catch (PermanentException ex)
                {
                    ResetProgress();
                    Log($"Error: {ex.Message}");
                    return 1;
                }
                catch (Exception ex)
                {
                    ResetProgress();
                    if (attempt > maxRetries)
                    {
                        Log($"Error (all {maxRetries} session retries exhausted): {ex.Message}");
                        return 1;
                    }
                    int delaySec = Math.Min(30 * attempt, 120);
                    Log($"Session attempt {attempt} failed: {ex.Message}");
                    Log($"Retrying in {delaySec}s... ({attempt}/{maxRetries} session retries used)");
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            ResetProgress();
            Log("Download cancelled.");
            return 2;
        }
        catch (DownloadCancelledException)
        {
            ResetProgress();
            Log("Download cancelled.");
            return 2;
        }
        finally
        {
            refreshService.Dispose();
        }
    }

    // ── Progress / status display ─────────────────────────────────────────────
    //
    // Two-line display:
    //   Line 1: progress bar  (always visible)
    //   Line 2: last status   (noisy per-chunk messages; updated in-place, not scrolled)
    //
    // Important messages scroll normally above these two lines.
    // ANSI escape codes are used on non-redirected output (modern terminals).

    private static long   _lastBytes;
    private static long   _lastTick;
    private static double _speedBps;
    private static string   _progressLine = string.Empty;
    private static int      _drawnLines   = 0;           // how many lines are currently on screen
    private const  int      StatusRows    = 5;
    private static readonly Queue<string> _statusLines = new();
    private static readonly object        _consoleLock = new();
    private static readonly bool          _ansi        = !Console.IsOutputRedirected;

    // Clears all drawn lines (progress + status rows) so text can be written above.
    private static void ClearDisplay()
    {
        if (_drawnLines == 0) return;
        if (_ansi)
        {
            Console.Write("\r\x1b[2K");                        // clear current (bottom) line
            for (int i = 1; i < _drawnLines; i++)
                Console.Write("\x1b[1A\r\x1b[2K");            // move up + clear each line
        }
        else
        {
            // Fallback: can only clear the single bottom line reliably without ANSI
            Console.Write("\r" + new string(' ', 120) + "\r");
        }
        _drawnLines = 0;
    }

    // Draws progress bar + up to StatusRows status lines. Cursor ends on the bottom line.
    private static void DrawDisplay()
    {
        if (_progressLine.Length == 0) return;
        int maxW = _ansi && !Console.IsOutputRedirected
            ? Math.Max(Console.WindowWidth - 1, 20) : 120;

        Console.Write(_progressLine);
        _drawnLines = 1;

        foreach (var line in _statusLines)
        {
            var text = line.Length > maxW ? line[..maxW] : line;
            Console.Write('\n' + "\r" + text);
            _drawnLines++;
        }
    }

    private static void OnProgress(long downloaded, long total, int done, int totalParts)
    {
        long now   = Environment.TickCount64;
        long delta = now - _lastTick;
        if (delta >= 400)
        {
            double bps = (downloaded - _lastBytes) * 1000.0 / Math.Max(delta, 1);
            _speedBps  = bps * 0.3 + _speedBps * 0.7;
            _lastBytes = downloaded;
            _lastTick  = now;
        }

        double pct = total > 0 ? downloaded * 100.0 / total : 0;
        string bar  = ProgressBar(pct, 28);
        string spd  = HumanSpeed(_speedBps);
        string eta  = _speedBps > 1 && total > downloaded
            ? $"ETA {TimeSpan.FromSeconds((total - downloaded) / _speedBps):mm\\:ss}"
            : "ETA --:--";

        var line =
            $"[{bar}] {pct,5:0.0}%  " +
            $"{Downloader.HumanReadableBytes(downloaded)}/{Downloader.HumanReadableBytes(total)}  " +
            $"{spd,10}  {eta}  ({done}/{totalParts})";

        lock (_consoleLock)
        {
            ClearDisplay();
            _progressLine = line;
            DrawDisplay();
        }
    }

    private static string ProgressBar(double pct, int width)
    {
        int filled = Math.Clamp((int)(pct / 100.0 * width), 0, width);
        return new string('#', filled) + new string('-', width - filled);
    }

    private static string HumanSpeed(double bps) =>
        bps >= 1024 * 1024 ? $"{bps / 1024 / 1024:0.0} MB/s" :
        bps >= 1024         ? $"{bps / 1024:0.0} KB/s"        :
                              $"{bps:0} B/s";

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Scrolls an important message above the progress display.</summary>
    private static void Log(string msg)
    {
        lock (_consoleLock)
        {
            ClearDisplay();
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {msg}");
            DrawDisplay();
        }
    }

    /// <summary>Appends a noisy message to the rolling status window (max StatusRows lines).</summary>
    private static void Status(string msg)
    {
        lock (_consoleLock)
        {
            ClearDisplay();
            _statusLines.Enqueue(msg);
            while (_statusLines.Count > StatusRows)
                _statusLines.Dequeue();
            DrawDisplay();
        }
    }

    /// <summary>Resets the progress display after a download finishes.</summary>
    private static void ResetProgress()
    {
        lock (_consoleLock)
        {
            ClearDisplay();
            _progressLine = string.Empty;
            _statusLines.Clear();
            _lastBytes = 0; _lastTick = 0; _speedBps = 0;
        }
    }

    /// <summary>Returns true for transient per-chunk messages that should not scroll the log.</summary>
    private static bool IsNoisyStatus(string msg) =>
        msg.Contains(": failed, retry ")         ||
        msg.Contains("no response from server within") ||
        (msg.Contains(": error:") && msg.Contains("(proxy="));

    private static string? Next(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : null;

    private static void PrintHelp() => Console.WriteLine("""
        K2S Downloader CLI  (cross-platform, runs on Windows / Linux / macOS)

        Usage:
          k2s-cli <url> [options]

        Options:
          -f, --filename   <name>   Output filename  (default: original name from K2S)
          -o, --output-dir <dir>    Output directory (default: current dir or settings.json)
          -t, --threads    <n>      Worker thread count           (default: 20)
          -s, --split-mb   <mb>     Split chunk size MB           (default: 20)
          -r, --retries    <n>      Max outer session retries     (default: 3)
          -g, --gemini-key <key>    Gemini API key for automatic captcha solving
              --no-proxy            Skip proxy fetch / auto-refresh
          -h, --help                Show this help

        Settings are also read from settings.json next to the executable.
        Press Ctrl+C to cancel an in-progress download.

        Build for Linux:
          dotnet publish K2sDownloaderCLI -r linux-x64 -c Release --self-contained
        """);
}
