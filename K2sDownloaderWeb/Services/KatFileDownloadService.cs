using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using K2sDownloaderWeb.Hubs;
using K2sDownloaderWeb.Models;
using K2sDownloaderWinForms.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Playwright;

namespace K2sDownloaderWeb.Services;

public class KatFileDownloadService
{
    private readonly IHubContext<DownloadHub> _hub;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly Regex _urlPattern =
        new(@"https?://(?:www\.)?katfile\.(com|vip)/\w", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public KatFileDownloadService(IHubContext<DownloadHub> hub)
    {
        _hub = hub;
    }

    public static bool IsKatFileUrl(string url) => _urlPattern.IsMatch(url);

    // ── Entry point ───────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads a KatFile item using Playwright (headless Chromium) to solve
    /// the reCaptcha v2 audio challenge via wit.ai, then downloads the file
    /// using the existing chunked HTTP engine.  Returns the local output path.
    /// </summary>
    public async Task<string> DownloadAsync(
        QueueItem item, Downloader downloader, AppSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.WitAiApiKey))
            throw new PermanentException(
                "WitAiApiKey is not configured. " +
                "Set it in Settings (get a free key at wit.ai) to download KatFile files.");

        void Log(string msg) =>
            _ = _hub.Clients.All.SendAsync("Log", item.Id, msg, CancellationToken.None);

        var (fileName, downloadUrl) = await GetDownloadUrlAsync(
            item.Url, settings.WitAiApiKey, Log, ct);

        var dir = settings.EffectiveDownloadDirectory;
        Directory.CreateDirectory(dir);
        var baseName = string.IsNullOrWhiteSpace(item.Filename) ? fileName : item.Filename;
        var outputPath = Path.Combine(dir, Path.GetFileName(baseName));

        await downloader.DownloadFromDirectUrlAsync(
            downloadUrl, outputPath, settings.SplitSizeMb * 1024 * 1024, ct);

        return outputPath;
    }

    // ── Playwright flow ───────────────────────────────────────────────────────

    private static async Task<(string FileName, string DownloadUrl)> GetDownloadUrlAsync(
        string pageUrl, string witAiApiKey, Action<string> log, CancellationToken ct)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
        });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                        "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
        });
        var page = await context.NewPageAsync();

        try
        {
            log("[KatFile] Opening page...");
            await page.GotoAsync(pageUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30_000
            });

            var fileName = await ExtractFileNameAsync(page);
            log($"[KatFile] File: {fileName}");

            // ── Step 1: Submit download1 form ("Free Download") ───────────────
            var freeBtn = page.Locator("input[name='method_free']").First;
            bool hasStep1;
            try
            {
                await freeBtn.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 4_000
                });
                hasStep1 = true;
            }
            catch { hasStep1 = false; }

            if (hasStep1)
            {
                log("[KatFile] Requesting free download...");
                await freeBtn.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                    new PageWaitForLoadStateOptions { Timeout = 20_000 });
            }

            // ── Step 2: Wait for countdown timer ──────────────────────────────
            await WaitForCountdownAsync(page, log, ct);

            // ── Step 3: Solve reCaptcha (optional — not all pages require it) ──
            bool hasCaptcha;
            try
            {
                await page.WaitForSelectorAsync(
                    "iframe[src*='recaptcha'], iframe[title*='recaptcha'], iframe[src*='hcaptcha']",
                    new PageWaitForSelectorOptions { Timeout = 6_000 });
                hasCaptcha = true;
            }
            catch { hasCaptcha = false; }

            if (hasCaptcha)
            {
                log("[KatFile] Solving reCaptcha...");
                var token = await SolveReCaptchaAsync(page, witAiApiKey, log, ct);

                // Inject token into the hidden textarea
                await page.EvaluateAsync(
                    "(t) => { " +
                    "  var el = document.querySelector('textarea[name=\"g-recaptcha-response\"]') " +
                    "         || document.querySelector('[name=\"g-recaptcha-response\"]'); " +
                    "  if (el) { el.value = t; el.dispatchEvent(new Event('change')); } " +
                    "}",
                    token);
            }
            else
            {
                log("[KatFile] No captcha detected, proceeding directly...");
            }

            // ── Step 4: Submit download2 form, capture URL ────────────────────
            var downloadUrl = await SubmitAndCaptureDownloadUrlAsync(page, log, ct);
            log("[KatFile] Download URL obtained.");
            return (fileName, downloadUrl);
        }
        catch (Exception) when (true)
        {
            // 截圖方便除錯，存到 /data/katfile-debug.png
            try { await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/data/katfile-debug.png" }); }
            catch { /* ignore screenshot errors */ }
            throw;
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    // ── Countdown ─────────────────────────────────────────────────────────────

    private static async Task WaitForCountdownAsync(IPage page, Action<string> log, CancellationToken ct)
    {
        var timer = page.Locator("#countdown, #ctimer, .countdown-timer, [id*='timer']").First;
        try
        {
            await timer.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 3_000
            });
        }
        catch { return; }

        log("[KatFile] Waiting for countdown...");
        for (int i = 0; i < 90 && !ct.IsCancellationRequested; i++)
        {
            try
            {
                var text = await timer.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1_000 });
                if (int.TryParse(text.Trim(), out var secs) && secs > 0)
                {
                    log($"[KatFile] {secs}s remaining...");
                    await Task.Delay(1_200, ct);
                    continue;
                }
            }
            catch { /* element gone or not a number */ }
            break;
        }
    }

    // ── reCaptcha solver ──────────────────────────────────────────────────────

    private static async Task<string> SolveReCaptchaAsync(
        IPage page, string witAiApiKey, Action<string> log, CancellationToken ct)
    {
        // Wait for reCaptcha iframe
        await page.WaitForSelectorAsync("iframe[src*='recaptcha']",
            new PageWaitForSelectorOptions { Timeout = 20_000 });

        // Find anchor frame (checkbox)
        var anchorFrame = await WaitForFrameAsync(page, "recaptcha/api2/anchor", 10_000, ct);
        if (anchorFrame is null)
            throw new Exception("reCaptcha anchor frame not found.");

        log("[reCaptcha] Clicking checkbox...");
        await anchorFrame.ClickAsync("#recaptcha-anchor",
            new FrameClickOptions { Timeout = 5_000 });
        await Task.Delay(2_000, ct);

        // Check if it passed without an image challenge
        var token = await GetRecaptchaTokenAsync(page);
        if (!string.IsNullOrEmpty(token))
        {
            log("[reCaptcha] Passed without challenge.");
            return token;
        }

        // Locate challenge bframe
        var bFrame = await WaitForFrameAsync(page, "recaptcha/api2/bframe", 8_000, ct);
        if (bFrame is null)
            throw new Exception("reCaptcha challenge frame not found.");

        // Try audio challenge up to 3 times
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                log($"[reCaptcha] Audio attempt {attempt}/3...");

                // Switch to audio challenge
                try
                {
                    await bFrame.ClickAsync("#recaptcha-audio-button",
                        new FrameClickOptions { Timeout = 4_000 });
                    await Task.Delay(1_500, ct);
                }
                catch { /* Already on audio challenge */ }

                // Wait for audio download link to appear
                await bFrame.WaitForSelectorAsync(".rc-audiochallenge-tdownload-link",
                    new FrameWaitForSelectorOptions { Timeout = 10_000 });

                var audioUrl = await bFrame.GetAttributeAsync(
                    ".rc-audiochallenge-tdownload-link", "href");
                if (string.IsNullOrEmpty(audioUrl))
                    throw new Exception("Audio challenge URL is empty.");

                log("[reCaptcha] Downloading audio...");
                var audioBytes = await _http.GetByteArrayAsync(audioUrl, ct);

                log("[reCaptcha] Transcribing via wit.ai...");
                var text = await TranscribeWithWitAiAsync(audioBytes, witAiApiKey, ct);
                log($"[reCaptcha] Heard: \"{text}\"");

                await bFrame.FillAsync("#audio-response", text,
                    new FrameFillOptions { Timeout = 5_000 });
                await bFrame.ClickAsync("#recaptcha-verify-button",
                    new FrameClickOptions { Timeout = 5_000 });
                await Task.Delay(2_500, ct);

                token = await GetRecaptchaTokenAsync(page);
                if (!string.IsNullOrEmpty(token))
                {
                    log("[reCaptcha] Solved!");
                    return token;
                }

                // Request new audio for next attempt
                if (attempt < 3)
                {
                    log("[reCaptcha] Wrong answer, requesting new audio...");
                    try
                    {
                        await bFrame.ClickAsync("#recaptcha-reload-button",
                            new FrameClickOptions { Timeout = 3_000 });
                        await Task.Delay(1_500, ct);
                    }
                    catch { }
                }
            }
            catch (Exception ex) when (attempt < 3)
            {
                log($"[reCaptcha] Attempt {attempt} error: {ex.Message}");
                await Task.Delay(1_000, ct);
            }
        }

        throw new Exception("reCaptcha solving failed after 3 audio attempts.");
    }

    // ── Submit & capture download URL ────────────────────────────────────────

    private static async Task<string> SubmitAndCaptureDownloadUrlAsync(
        IPage page, Action<string> log, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Browser-initiated download (Content-Disposition: attachment)
        page.Download += (_, dl) =>
        {
            tcs.TrySetResult(dl.Url);
            _ = dl.CancelAsync();
        };

        // Response-level detection (CDN redirect or inline attachment)
        page.Response += (_, resp) =>
        {
            if (tcs.Task.IsCompleted) return;
            var url = resp.Url;
            if (Regex.IsMatch(url, @"/(dl[-_]\w{4,}|get/\w{8,}|d/\w{8,})", RegexOptions.IgnoreCase) ||
                (resp.Headers.TryGetValue("content-disposition", out var cd) &&
                 cd.Contains("attachment", StringComparison.OrdinalIgnoreCase)))
            {
                tcs.TrySetResult(url);
            }
        };

        log("[KatFile] Submitting download form...");
        var submitBtn = page.Locator(
            "input[name='method_free'][type='submit'], " +
            "#btn_download, " +
            "input[value*='Download'][type='submit'], " +
            "form[method='post'] input[type='submit']"
        ).Last;

        await submitBtn.ClickAsync(new LocatorClickOptions { Timeout = 8_000 });

        // Wait up to 20 s for URL
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(20_000);
        try
        {
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Fallback: scan resulting page for a download link
            log("[KatFile] Scanning page for download link...");
            var link = page.Locator(
                "a[href*='/dl-'], a[href*='/get/'], " +
                ".btn-download, a[class*='download'], " +
                "a:has-text('Download File'), a:has-text('Click here')"
            ).First;

            try
            {
                await link.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 4_000
                });
                var href = await link.GetAttributeAsync("href");
                if (!string.IsNullOrEmpty(href)) return href;
            }
            catch { }

            throw new Exception(
                "Could not obtain KatFile download URL. " +
                "The page may have changed structure or the download link expired.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> ExtractFileNameAsync(IPage page)
    {
        try
        {
            var h1 = page.Locator("h1, .file-info h2, .filename").First;
            var text = await h1.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 3_000 });
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        catch { }

        var title = await page.TitleAsync();
        if (!string.IsNullOrWhiteSpace(title))
            return Regex.Replace(title, @"\s*[|–\-]\s*.+$", "").Trim();

        return "download";
    }

    private static async Task<IFrame?> WaitForFrameAsync(
        IPage page, string urlSubstring, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var frame = page.Frames.FirstOrDefault(
                f => f.Url.Contains(urlSubstring, StringComparison.OrdinalIgnoreCase));
            if (frame is not null) return frame;
            await Task.Delay(300, ct);
        }
        return null;
    }

    private static async Task<string?> GetRecaptchaTokenAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string>(
                "() => document.querySelector('[name=\"g-recaptcha-response\"]')?.value ?? ''");
        }
        catch { return null; }
    }

    private static async Task<string> TranscribeWithWitAiAsync(
        byte[] audioMp3, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post, "https://api.wit.ai/speech?v=20240304");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new ByteArrayContent(audioMp3);
        req.Content.Headers.ContentType =
            new MediaTypeHeaderValue("audio/mpeg");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception(
                $"wit.ai HTTP {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");

        // wit.ai returns NDJSON — take last complete JSON object with a "text" field
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                var json = JsonNode.Parse(line.TrimStart('\r'));
                var text = json?["text"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim().ToLower();
            }
            catch { }
        }

        throw new Exception(
            $"wit.ai returned no text. Response: {body[..Math.Min(500, body.Length)]}");
    }
}
