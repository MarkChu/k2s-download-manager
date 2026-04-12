using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using K2sDownloaderWinForms.Core;
using Microsoft.Playwright;

namespace K2sDownloaderWeb.Services;

/// <summary>
/// Downloads files from KatFile (katfile.com/vip/ws/cloud/online) using:
///   • Pure HTTP for page fetching and form submission (like JDownloader)
///   • Playwright only when reCaptcha v2 needs to be solved
/// </summary>
public class KatFileDownloadService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // All known KatFile domains
    private static readonly Regex _urlPattern =
        new(@"https?://(?:www\.)?katfile\.(com|vip|ws|cloud|online)/\w",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    public static bool IsKatFileUrl(string url) => _urlPattern.IsMatch(url);

    // ── Entry point ───────────────────────────────────────────────────────────

    public static async Task<string> DownloadAsync(
        string url, string? preferredFilename, Downloader downloader,
        AppSettings settings, Action<string> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.WitAiApiKey))
            throw new PermanentException(
                "WitAiApiKey is not configured. " +
                "Set it in Settings (get a free key at wit.ai) to download KatFile files.");

        // Pick first working proxy from Downloader (null = direct)
        var proxyHost = downloader.Proxies.FirstOrDefault(p => p != null);
        if (proxyHost != null)
            log($"[KatFile] Using proxy: {proxyHost}");

        var (fileName, downloadUrl) = await GetDownloadUrlAsync(
            url, settings.WitAiApiKey, proxyHost, log, ct);

        var dir = settings.EffectiveDownloadDirectory;
        Directory.CreateDirectory(dir);
        var baseName = string.IsNullOrWhiteSpace(preferredFilename) ? fileName : preferredFilename;
        var outputPath = Path.Combine(dir, Path.GetFileName(baseName));

        await downloader.DownloadFromDirectUrlAsync(
            downloadUrl, outputPath, settings.SplitSizeMb * 1024 * 1024, ct);

        return outputPath;
    }

    // ── Main HTTP flow (mirrors JDownloader's doFree) ─────────────────────────

    private static async Task<(string FileName, string DownloadUrl)> GetDownloadUrlAsync(
        string pageUrl, string witAiApiKey, string? proxyHost, Action<string> log, CancellationToken ct)
    {
        var cookies = new CookieContainer();
        using var http = MakeHttpClient(cookies, proxyHost);

        // ── GET initial page ──────────────────────────────────────────────────
        log("[KatFile] Fetching page...");
        var (html, finalUrl) = await GetPageAsync(http, pageUrl, ct);

        CheckPermanentErrors(html, finalUrl);
        var fileName = ExtractFileName(html, finalUrl);
        log($"[KatFile] File: {fileName}");

        // ── Submit download1 form ─────────────────────────────────────────────
        var form1 = FindForm(html, "download1");
        if (form1 is not null)
        {
            log("[KatFile] Submitting step 1 (free download request)...");
            http.DefaultRequestHeaders.Remove("Referer");
            http.DefaultRequestHeaders.Add("Referer", finalUrl);
            (html, finalUrl) = await PostFormAsync(http, finalUrl, form1, ct);
            DumpHtml(html, "step2-page", log);
        }
        else
        {
            DumpHtml(html, "step1-page", log);
        }

        // ── Cloudflare Turnstile → hand off entirely to Playwright ────────────
        if (html.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase))
        {
            log("[KatFile] Cloudflare Turnstile detected — using Playwright for step 2...");
            var dlUrl = await PlaywrightStep2Async(finalUrl, cookies, proxyHost, log, ct);
            return (fileName, dlUrl);
        }

        // ── Wait (estimated_time is tenths-of-seconds per JDownloader) ────────
        var waitTenths = ExtractWaitTime(html);
        if (waitTenths > 0)
        {
            var waitSecs = waitTenths / 10;
            log($"[KatFile] Waiting {waitSecs}s...");
            await Task.Delay(waitSecs * 1000 + 1_000, ct);
        }

        // ── reCaptcha v2 (audio solve via wit.ai) ────────────────────────────
        string captchaToken = "";
        if (html.Contains("g-recaptcha", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("without reCAPTCHA", StringComparison.OrdinalIgnoreCase))
        {
            log("[KatFile] reCaptcha v2 detected, solving via audio (wit.ai)...");
            captchaToken = await SolveCaptchaWithPlaywrightAsync(
                finalUrl, cookies, proxyHost, witAiApiKey, log, ct);
        }
        else if (html.Contains("h-captcha", StringComparison.OrdinalIgnoreCase))
        {
            log("[KatFile] hCaptcha detected — not supported, attempting without token...");
        }

        // ── Submit download2 form ─────────────────────────────────────────────
        var form2 = FindForm(html, "download2")
                 ?? FindForm(html, "download_orig")
                 ?? FindFormByName(html, "F1");

        if (form2 is null)
        {
            DumpHtml(html, "step2-no-form", log);
            throw new Exception("Could not find download2 form in page.");
        }

        if (!string.IsNullOrEmpty(captchaToken))
            form2["g-recaptcha-response"] = captchaToken;

        log("[KatFile] Submitting step 2 (download form)...");
        http.DefaultRequestHeaders.Remove("Referer");
        http.DefaultRequestHeaders.Add("Referer", finalUrl);
        var (html2, afterPostUrl) = await PostFormAsync(http, finalUrl, form2, ct);
        DumpHtml(html2, "step2-response", log);

        if (IsDownloadUrl(afterPostUrl))
            return (fileName, afterPostUrl);

        var dl = ExtractDownloadUrl(html2);
        if (!string.IsNullOrEmpty(dl))
            return (fileName, dl);

        var ext = Regex.Match(html2,
            @"https?://[a-zA-Z0-9.\-]+/[^\s""'<>]{10,}\.(?:rar|zip|7z|tar|gz|mp4|mkv|avi|pdf|exe|iso)\b",
            RegexOptions.IgnoreCase);
        if (ext.Success)
            return (fileName, ext.Value);

        throw new Exception(
            "Could not obtain download URL from KatFile. " +
            "Check /tmp/katfile-step2-response.html for the server response.");
    }

    // ── Playwright step 2: Turnstile solve + form submit ─────────────────────

    private static async Task<string> PlaywrightStep2Async(
        string step2Url, CookieContainer cookies, string? proxyHost,
        Action<string> log, CancellationToken ct)
    {
        using var playwright = await Playwright.CreateAsync();

        var launchOpts = new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
        };
        if (proxyHost != null)
            launchOpts.Proxy = new Microsoft.Playwright.Proxy { Server = $"http://{proxyHost}" };

        await using var browser = await playwright.Chromium.LaunchAsync(launchOpts);

        var pwCookies = cookies.GetAllCookies()
            .Cast<System.Net.Cookie>()
            .Select(c => new Microsoft.Playwright.Cookie
            {
                Name   = c.Name,  Value  = c.Value,
                Domain = c.Domain, Path   = c.Path, Secure = c.Secure,
            }).ToList();

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = UserAgent
        });
        if (pwCookies.Count > 0)
            await context.AddCookiesAsync(pwCookies);

        var page = await context.NewPageAsync();

        // Intercept CDN redirect responses
        string? interceptedUrl = null;
        page.Response += (_, resp) =>
        {
            if (resp.Status is 200 or 206 && IsDownloadUrl(resp.Url))
                interceptedUrl = resp.Url;
        };

        try
        {
            log("[KatFile] Opening page in browser...");
            await page.GotoAsync(step2Url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 30_000
            });

            // Dismiss Register / promo popup if present
            await DismissPopupAsync(page, ct);

            var html = await page.ContentAsync();

            // GET always shows step 1; detect by presence of method_free submit button
            if (IsStep1Page(html))
            {
                log("[KatFile] Step 1 form found, submitting...");
                try
                {
                    await page.RunAndWaitForNavigationAsync(
                        async () =>
                        {
                            var btn = page.Locator("input[name='method_free'][type='submit'], input[name='method_free']:not([type='hidden'])").First;
                            if (await btn.IsVisibleAsync())
                                await btn.ClickAsync();
                            else
                                await page.EvaluateAsync(
                                    "() => { const f = document.querySelector('form input[name=\"op\"][value=\"download1\"]')?.closest('form'); if (f) f.submit(); }");
                        },
                        new PageRunAndWaitForNavigationOptions
                        {
                            WaitUntil = WaitUntilState.NetworkIdle,
                            Timeout   = 25_000
                        });
                }
                catch (TimeoutException)
                {
                    log("[KatFile] Step 1 navigation timed out, continuing anyway...");
                }

                // Dismiss popup on step 2 as well
                await DismissPopupAsync(page, ct);
                html = await page.ContentAsync();
                DumpHtml(html, "playwright-after-step1", log);
            }

            // Wait for Turnstile iframe to be injected by JS, then wait for auto-solve
            try
            {
                log("[KatFile] Waiting for Turnstile widget (JS inject)...");
                await page.WaitForSelectorAsync(
                    "iframe[src*='turnstile'], div[data-sitekey]",
                    new PageWaitForSelectorOptions { Timeout = 15_000 });

                log("[KatFile] Waiting for Cloudflare Turnstile to auto-solve...");
                await page.WaitForFunctionAsync(
                    "() => { const el = document.querySelector('[name=\"cf-turnstile-response\"]'); return el && el.value && el.value.length > 0; }",
                    null, new PageWaitForFunctionOptions { Timeout = 30_000 });
                log("[KatFile] Turnstile solved!");
            }
            catch (TimeoutException ex)
            {
                DumpHtml(await page.ContentAsync(), "turnstile-timeout", log);
                // If no widget at all, maybe captcha isn't required — log and continue
                if (ex.Message.Contains("Waiting for selector"))
                    log("[KatFile] No Turnstile widget found, submitting without token...");
                else
                    throw new Exception(
                        "Cloudflare Turnstile did not auto-solve within 30s. " +
                        "Your IP may be flagged — proxies are fetched automatically on startup.");
            }

            // Set device_id via localStorage (mirrors page JS)
            await page.EvaluateAsync(@"() => {
                if (!localStorage['katfileDeviceId'])
                    localStorage['katfileDeviceId'] = crypto.randomUUID();
                const el = document.getElementById('deviceId');
                if (el) el.value = localStorage['katfileDeviceId'];
            }");

            log("[KatFile] Submitting download2 form...");
            try
            {
                await page.RunAndWaitForNavigationAsync(
                    async () => await page.EvaluateAsync(
                        "() => { const f = document.getElementById('_mform') ?? document.querySelector('form[name=\"F1\"]'); if (f) f.submit(); }"),
                    new PageRunAndWaitForNavigationOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout   = 30_000
                    });
            }
            catch (TimeoutException)
            {
                log("[KatFile] No navigation after form submit, checking page...");
            }

            await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 10_000 }).ConfigureAwait(false);

            // 1. CDN URL captured by response interceptor
            if (interceptedUrl != null)
            {
                log($"[KatFile] CDN URL from interceptor: {interceptedUrl}");
                return interceptedUrl;
            }

            // 2. Current URL is already a CDN link
            if (IsDownloadUrl(page.Url))
                return page.Url;

            // 3. Extract from page HTML
            var finalHtml = await page.ContentAsync();
            DumpHtml(finalHtml, "playwright-response", log);

            var dlUrl = ExtractDownloadUrl(finalHtml);
            if (!string.IsNullOrEmpty(dlUrl)) return dlUrl;

            var ext = Regex.Match(finalHtml,
                @"https?://[a-zA-Z0-9.\-]+/[^\s""'<>]{10,}\.(?:rar|zip|7z|tar|gz|mp4|mkv|avi|pdf|exe|iso)\b",
                RegexOptions.IgnoreCase);
            if (ext.Success) return ext.Value;

            throw new Exception(
                "Could not find download URL after Turnstile solve. " +
                "Check /tmp/katfile-playwright-response.html");
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private static HttpClient MakeHttpClient(CookieContainer cookies, string? proxyHost = null)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer   = cookies,
            UseCookies        = true,
            AllowAutoRedirect = false,   // we handle redirects manually
        };
        if (proxyHost != null)
        {
            handler.Proxy    = new WebProxy($"http://{proxyHost}");
            handler.UseProxy = true;
        }
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
        client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
        return client;
    }

    private static async Task<(string Html, string FinalUrl)> GetPageAsync(
        HttpClient http, string url, CancellationToken ct)
    {
        for (int hop = 0; hop < 12; hop++)
        {
            using var resp = await http.GetAsync(url, ct);
            var html = await resp.Content.ReadAsStringAsync(ct);

            // Follow HTTP 3xx redirects
            if ((int)resp.StatusCode is >= 300 and < 400)
            {
                var loc = resp.Headers.Location;
                url = loc is not null ? ResolveUrl(url, loc.ToString()) : url;
                continue;
            }

            // Follow <meta http-equiv="refresh" ...> redirects
            var metaM = Regex.Match(html,
                @"<meta[^>]+http-equiv=[""']refresh[""'][^>]+content=[""'][^;]*;\s*url=([^""'\s>]+)",
                RegexOptions.IgnoreCase);
            if (metaM.Success)
            {
                url = ResolveUrl(url, metaM.Groups[1].Value.Trim('\'', '"'));
                continue;
            }

            return (html, url);
        }
        throw new Exception("Too many redirects fetching KatFile page.");
    }

    private static async Task<(string Html, string FinalUrl)> PostFormAsync(
        HttpClient http, string baseUrl, Dictionary<string, string> fields,
        CancellationToken ct)
    {
        // Resolve action URL
        var action = fields.TryGetValue("__action__", out var a) && !string.IsNullOrEmpty(a)
            ? ResolveUrl(baseUrl, a) : baseUrl;

        var postFields = fields
            .Where(kv => kv.Key != "__action__")
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value));

        using var content = new FormUrlEncodedContent(postFields);
        using var resp = await http.PostAsync(action, content, ct);

        // Follow redirects (POST → GET for 301/302/303, standard browser behaviour)
        if ((int)resp.StatusCode is >= 300 and < 400)
        {
            var loc = resp.Headers.Location;
            var redirectUrl = loc is not null ? ResolveUrl(action, loc.ToString()) : action;
            return await GetPageAsync(http, redirectUrl, ct);
        }

        var html = await resp.Content.ReadAsStringAsync(ct);
        return (html, action);
    }

    // ── HTML form parser ──────────────────────────────────────────────────────

    /// <summary>Finds a form whose op field value contains <paramref name="opContains"/>.</summary>
    private static Dictionary<string, string>? FindForm(string html, string opContains)
    {
        foreach (Match formMatch in Regex.Matches(html,
            @"<form(?:\s[^>]*)?>[\s\S]*?</form>", RegexOptions.IgnoreCase))
        {
            var form = formMatch.Value;

            // Check op value
            var opVal = GetInputValue(form, "op");
            if (opVal is null ||
                !opVal.Contains(opContains, StringComparison.OrdinalIgnoreCase))
                continue;

            return ExtractFormFields(form);
        }
        return null;
    }

    /// <summary>Finds a form by its name= attribute (e.g. name="F1").</summary>
    private static Dictionary<string, string>? FindFormByName(string html, string formName)
    {
        var m = Regex.Match(html,
            $@"<form(?:\s[^>]*)?name=[""']{Regex.Escape(formName)}[""'][^>]*>[\s\S]*?</form>",
            RegexOptions.IgnoreCase);
        return m.Success ? ExtractFormFields(m.Value) : null;
    }

    private static Dictionary<string, string> ExtractFormFields(string formHtml)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Extract form action
        var actionM = Regex.Match(formHtml,
            @"<form(?:\s[^>]*)?action=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        if (actionM.Success)
            fields["__action__"] = HtmlDecode(actionM.Groups[1].Value);

        // Extract all <input> fields
        foreach (Match inp in Regex.Matches(formHtml,
            @"<input(?:\s[^>]*)?>", RegexOptions.IgnoreCase))
        {
            var name  = GetAttr(inp.Value, "name");
            var value = GetAttr(inp.Value, "value") ?? "";
            if (string.IsNullOrEmpty(name)) continue;
            if (name.Equals("method_premium", StringComparison.OrdinalIgnoreCase)) continue;
            fields[name] = HtmlDecode(value);
        }

        // Ensure method_free has a value
        if (fields.TryGetValue("method_free", out var mf) && string.IsNullOrEmpty(mf))
            fields["method_free"] = "Free Download";

        return fields;
    }

    private static string? GetInputValue(string formHtml, string fieldName)
    {
        var m = Regex.Match(formHtml,
            $@"<input(?=[^>]*name=[""']{Regex.Escape(fieldName)}[""'])[^>]*value=[""']([^""']*)[""']",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(formHtml,
            $@"<input(?=[^>]*value=[""']([^""']*)[""'])[^>]*name=[""']{Regex.Escape(fieldName)}[""']",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? GetAttr(string tag, string attr)
    {
        var m = Regex.Match(tag,
            $@"{Regex.Escape(attr)}\s*=\s*(?:[""']([^""']*)[""']|(\S+))",
            RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value) : null;
    }

    // ── Wait time ─────────────────────────────────────────────────────────────

    /// <summary>Returns tenths-of-seconds (divide by 10 for actual seconds).</summary>
    private static int ExtractWaitTime(string html)
    {
        var m = Regex.Match(html, @"var\s+estimated_time\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    // ── Error detection ───────────────────────────────────────────────────────

    private static void CheckPermanentErrors(string html, string url)
    {
        if (url.Contains("op=registration", StringComparison.OrdinalIgnoreCase))
            throw new PermanentException(
                "KatFile requires an account to download this file.");
        if (html.Contains("This file is available for Premium", StringComparison.OrdinalIgnoreCase))
            throw new PermanentException(
                "This KatFile is premium-only and cannot be downloaded for free.");
        if (Regex.IsMatch(html, @"/404-remove|>The file expired|>The file was deleted",
            RegexOptions.IgnoreCase))
            throw new PermanentException("File not found or has been deleted.");
    }

    // ── Download URL extraction ───────────────────────────────────────────────

    private static bool IsDownloadUrl(string url) =>
        Regex.IsMatch(url, @"/(dl[-_]\w{4,}|get/\w{8,}|d/\w{8,})",
            RegexOptions.IgnoreCase);

    private static string? ExtractDownloadUrl(string html)
    {
        // Look for direct download links in the response
        foreach (Match m in Regex.Matches(html,
            @"href=[""'](https?://[^""']+)[""']", RegexOptions.IgnoreCase))
        {
            var href = m.Groups[1].Value;
            if (IsDownloadUrl(href)) return href;
        }
        // Broader: any https link that looks like a file CDN
        var m2 = Regex.Match(html,
            @"""(https?://[^""]+\.\w{2,4}(?:\?[^""]*)?)""\s*(?:class=""[^""]*download|title=""[^""]*download)",
            RegexOptions.IgnoreCase);
        if (m2.Success) return m2.Groups[1].Value;

        return null;
    }

    // ── File name extraction ──────────────────────────────────────────────────

    private static string ExtractFileName(string html, string url)
    {
        // Try <h1> / common filename selectors
        var m = Regex.Match(html,
            @"<h1[^>]*>\s*([^<]{3,}?)\s*</h1>", RegexOptions.IgnoreCase);
        if (m.Success) return Sanitize(HtmlDecode(m.Groups[1].Value));

        // Try <title>
        m = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var title = Regex.Replace(HtmlDecode(m.Groups[1].Value),
                @"\s*[|–\-]\s*.+$", "").Trim();
            if (!string.IsNullOrWhiteSpace(title)) return Sanitize(title);
        }

        // Fall back to URL filename
        return Path.GetFileName(new Uri(url).AbsolutePath) is { Length: > 0 } fn
            ? fn : "download";
    }

    // ── reCaptcha solving via Playwright (audio + wit.ai) ─────────────────────

    private static async Task<string> SolveCaptchaWithPlaywrightAsync(
        string pageUrl, CookieContainer cookies, string? proxyHost, string witAiApiKey,
        Action<string> log, CancellationToken ct)
    {
        using var playwright = await Playwright.CreateAsync();
        var launchOpts = new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
        };
        if (proxyHost != null)
            launchOpts.Proxy = new Microsoft.Playwright.Proxy { Server = $"http://{proxyHost}" };
        await using var browser = await playwright.Chromium.LaunchAsync(launchOpts);

        // Copy cookies from HTTP session into Playwright context
        var pwCookies = cookies.GetAllCookies()
            .Cast<System.Net.Cookie>()
            .Select(c => new Microsoft.Playwright.Cookie
            {
                Name   = c.Name,
                Value  = c.Value,
                Domain = c.Domain,
                Path   = c.Path,
                Secure = c.Secure,
            }).ToList();

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = UserAgent
        });
        if (pwCookies.Count > 0)
            await context.AddCookiesAsync(pwCookies);

        var page = await context.NewPageAsync();

        try
        {
            log("[reCaptcha] Opening page in browser...");
            await page.GotoAsync(pageUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 30_000
            });

            return await SolveReCaptchaAsync(page, witAiApiKey, log, ct);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    // ── reCaptcha audio solver ────────────────────────────────────────────────

    private static async Task<string> SolveReCaptchaAsync(
        IPage page, string witAiApiKey, Action<string> log, CancellationToken ct)
    {
        await page.WaitForSelectorAsync("iframe[src*='recaptcha']",
            new PageWaitForSelectorOptions { Timeout = 20_000 });

        var anchorFrame = await WaitForFrameAsync(page, "recaptcha/api2/anchor", 10_000, ct)
            ?? throw new Exception("reCaptcha anchor frame not found.");

        log("[reCaptcha] Clicking checkbox...");
        await anchorFrame.ClickAsync("#recaptcha-anchor",
            new FrameClickOptions { Timeout = 5_000 });
        await Task.Delay(2_000, ct);

        var token = await GetRecaptchaTokenAsync(page);
        if (!string.IsNullOrEmpty(token))
        {
            log("[reCaptcha] Passed without challenge.");
            return token;
        }

        var bFrame = await WaitForFrameAsync(page, "recaptcha/api2/bframe", 8_000, ct)
            ?? throw new Exception("reCaptcha challenge frame not found.");

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                log($"[reCaptcha] Audio attempt {attempt}/3...");
                try
                {
                    await bFrame.ClickAsync("#recaptcha-audio-button",
                        new FrameClickOptions { Timeout = 4_000 });
                    await Task.Delay(1_500, ct);
                }
                catch { /* Already on audio */ }

                await bFrame.WaitForSelectorAsync(".rc-audiochallenge-tdownload-link",
                    new FrameWaitForSelectorOptions { Timeout = 10_000 });

                var audioUrl = await bFrame.GetAttributeAsync(
                    ".rc-audiochallenge-tdownload-link", "href")
                    ?? throw new Exception("Audio URL is empty.");

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

    // ── wit.ai transcription ──────────────────────────────────────────────────

    private static async Task<string> TranscribeWithWitAiAsync(
        byte[] audioMp3, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post, "https://api.wit.ai/speech?v=20240304");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new ByteArrayContent(audioMp3);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception(
                $"wit.ai HTTP {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");

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

    // ── Playwright helpers ────────────────────────────────────────────────────

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

    // ── Misc helpers ──────────────────────────────────────────────────────────

    private static string ResolveUrl(string baseUrl, string relative)
    {
        if (Uri.TryCreate(relative, UriKind.Absolute, out _)) return relative;
        var b = new Uri(baseUrl);
        return new Uri(b, relative).ToString();
    }

    private static string HtmlDecode(string s) =>
        System.Net.WebUtility.HtmlDecode(s);

    /// <summary>True when the page is the step-1 download form (method_free is a submit button).</summary>
    private static bool IsStep1Page(string html) =>
        Regex.IsMatch(html,
            @"<input[^>]*name=[""']method_free[""'][^>]*type=[""']submit[""']|" +
            @"<input[^>]*type=[""']submit[""'][^>]*name=[""']method_free[""']",
            RegexOptions.IgnoreCase);

    /// <summary>Tries to close any visible modal/popup (Register, promo, etc.).</summary>
    private static async Task DismissPopupAsync(IPage page, CancellationToken ct)
    {
        try
        {
            // Escape key closes most Bootstrap modals
            await page.Keyboard.PressAsync("Escape");
            await Task.Delay(400, ct);

            // Try explicit close / dismiss buttons
            foreach (var sel in new[]
            {
                ".modal .close",
                ".modal [data-dismiss='modal']",
                ".modal-footer .btn-default",
                ".modal-footer button:not(.btn-primary)",
            })
            {
                try
                {
                    var el = page.Locator(sel).First;
                    if (await el.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 600 }))
                    {
                        await el.ClickAsync(new LocatorClickOptions { Timeout = 2_000 });
                        await Task.Delay(300, ct);
                        break;
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static string Sanitize(string name) =>
        Regex.Replace(name, @"[\\/:*?""<>|]", "_").Trim();

    /// <summary>Saves HTML to /tmp/katfile-{label}.html for debugging.</summary>
    private static void DumpHtml(string html, string label, Action<string> log)
    {
        try
        {
            var path = $"/tmp/katfile-{label}.html";
            File.WriteAllText(path, html);
            log($"[KatFile] [debug] HTML saved → {path} ({html.Length} chars)");
        }
        catch { }
    }
}
