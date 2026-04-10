using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace K2sDownloaderWinForms.Core;

/// <summary>Callback: receives (imageBytes, challenge, captchaUrl), returns user response string.</summary>
public delegate Task<string> CaptchaCallback(byte[] imageBytes, string challenge, string captchaUrl);

public static class K2sClient
{
    private static readonly string[] Domains = { "k2s.cc" };
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(25) };

    private static string RandomDomain() => Domains[Random.Shared.Next(Domains.Length)];

    // ── File info ────────────────────────────────────────────────────────────

    public static async Task<string> GetFileNameAsync(string fileId, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { ids = new[] { fileId } });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await SharedClient.PostAsync(
            $"https://{RandomDomain()}/api/v2/getFilesInfo", content, ct);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        var file = json!["files"]![0]!;

        var isAvailable = file["is_available"]?.GetValue<bool>() ?? false;
        if (!isAvailable)
            throw new PermanentException("File not found or has been removed.");

        var isAvailableForFree = file["isAvailableForFree"]?.GetValue<bool>() ?? false;
        if (!isAvailableForFree)
            throw new PermanentException("This file requires a premium account and cannot be downloaded for free.");

        return file["name"]!.GetValue<string>();
    }

    // ── Captcha ──────────────────────────────────────────────────────────────

    public record CaptchaInfo(string CaptchaUrl, string Challenge);

    public static async Task<CaptchaInfo> FetchCaptchaAsync(
        Action<string>? status, CancellationToken ct = default)
    {
        status?.Invoke("Requesting captcha challenge...");
        var resp = await SharedClient.PostAsync(
            $"https://{RandomDomain()}/api/v2/requestCaptcha",
            new StringContent("{}", Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
        return new CaptchaInfo(
            json["captcha_url"]!.GetValue<string>(),
            json["challenge"]!.GetValue<string>());
    }

    public static async Task<byte[]> DownloadCaptchaImageAsync(
        string url, CancellationToken ct = default)
    {
        return await SharedClient.GetByteArrayAsync(url, ct);
    }

    // ── URL generation ───────────────────────────────────────────────────────

    public static async Task<List<(string Url, string? Proxy)>> GenerateDownloadUrlsAsync(
        string fileId,
        int count,
        List<string?> proxies,
        CaptchaCallback captchaCallback,
        Action<string>? status,
        CancellationToken ct)
    {
        // Fetch captcha once (robustly log failures)
        CaptchaInfo captcha;
        byte[] imageBytes;
        string captchaResponse;
        try
        {
            captcha = await FetchCaptchaAsync(status, ct);
            try
            {
                imageBytes = await DownloadCaptchaImageAsync(captcha.CaptchaUrl, ct);
            }
            catch (Exception ex)
            {
                status?.Invoke($"Failed to download captcha image: {ex.Message}");
                throw;
            }
            try
            {
                captchaResponse = await captchaCallback(imageBytes, captcha.Challenge, captcha.CaptchaUrl);
            }
            catch (Exception ex)
            {
                status?.Invoke($"Captcha callback error: {ex.Message}");
                throw;
            }
        }
        catch (Exception ex)
        {
            status?.Invoke($"Failed to obtain captcha: {ex.Message}");
            throw;
        }

        string freeDownloadKey = string.Empty;

        const int MaxUrlGenAttemptsPerProxy = 6;

        // Try each proxy until one works
        foreach (var proxy in proxies)
        {
            if (ct.IsCancellationRequested) break;
            var label = proxy ?? "LOCAL";
            status?.Invoke($"Trying proxy {label}");

            HttpClient client = BuildClient(proxy);

            // Acquire a free download key for this proxy
            bool haveKey = false;
            while (!haveKey)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var (node, text, httpStatus) = await PostJsonRawAsync(client,
                        $"https://{RandomDomain()}/api/v2/getUrl",
                        new
                        {
                            file_id = fileId,
                            captcha_challenge = captcha.Challenge,
                            captcha_response = captchaResponse
                        }, ct);

                    if (node == null)
                    {
                        status?.Invoke($"Request error: Non-JSON response while obtaining key: {GetSnippet(text)} (proxy={label})");
                        break;
                    }

                    if (node["status"]?.GetValue<string>() == "error")
                    {
                        var msg = node["message"]?.GetValue<string>() ?? string.Empty;
                        if (msg == "Invalid captcha code")
                        {
                            status?.Invoke("Captcha invalid, requesting a new one.");
                            captcha = await FetchCaptchaAsync(status, ct);
                            imageBytes = await DownloadCaptchaImageAsync(captcha.CaptchaUrl, ct);
                            captchaResponse = await captchaCallback(imageBytes, captcha.Challenge, captcha.CaptchaUrl);
                            continue;
                        }
                        if (msg == "File not found")
                            throw new Exception("File not found on K2S.");
                        // Unhandled error — move to next proxy
                        status?.Invoke($"Proxy {label} returned error while obtaining key: {msg}");
                        break;
                    }

                    if (node["time_wait"] != null)
                    {
                        int wait = node["time_wait"]!.GetValue<int>();
                        if (wait > 30) break;

                        freeDownloadKey = node["free_download_key"]?.GetValue<string>() ?? string.Empty;
                        for (int i = wait - 1; i > 0; i--)
                        {
                            status?.Invoke($"[{label}] Waiting {i} seconds...");
                            await Task.Delay(1000, ct);
                        }
                        haveKey = true;
                    }
                    else
                    {
                        freeDownloadKey = node["free_download_key"]?.GetValue<string>() ?? string.Empty;
                        haveKey = true;
                    }
                }
                catch (Exception ex)
                {
                    status?.Invoke($"Request error: {ex.Message} (proxy={label})");
                    break;
                }
            }

            if (!haveKey) continue;

            // Generate `count` download URLs (sequentially) with handling for captcha expiry during generation
            var urls = new List<(string Url, string? Proxy)>();
            int genAttempts = 0;
            bool moveToNextProxy = false;

            while (urls.Count < count && !ct.IsCancellationRequested && !moveToNextProxy)
            {
                genAttempts++;
                if (genAttempts > MaxUrlGenAttemptsPerProxy)
                {
                    status?.Invoke($"[{label}] URL generation attempts exhausted ({MaxUrlGenAttemptsPerProxy}). Moving to next proxy.");
                    break;
                }

                int needed = count - urls.Count;
                for (int i = 0; i < needed; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var (rnode, rtext, rstatus) = await PostJsonRawAsync(client,
                            $"https://{RandomDomain()}/api/v2/getUrl",
                            new { file_id = fileId, free_download_key = freeDownloadKey }, ct);

                        if (rnode == null)
                        {
                            status?.Invoke($"[{label}] Non-JSON response while generating URL: {GetSnippet(rtext)}");
                            continue;
                        }

                        if (rnode["status"]?.GetValue<string>() == "error")
                        {
                            var msg = rnode["message"]?.GetValue<string>() ?? string.Empty;
                            if (msg == "Invalid captcha code")
                            {
                                status?.Invoke($"[{label}] Invalid captcha while generating URL — requesting new captcha and restarting key acquisition.");
                                captcha = await FetchCaptchaAsync(status, ct);
                                imageBytes = await DownloadCaptchaImageAsync(captcha.CaptchaUrl, ct);
                                captchaResponse = await captchaCallback(imageBytes, captcha.Challenge, captcha.CaptchaUrl);
                                // Force re-acquire freeDownloadKey for this proxy
                                haveKey = false;
                                break; // break the for-loop to re-run key acquisition
                            }
                            if (msg == "Download is not available")
                            {
                                status?.Invoke($"[{label}] Download is not available for this proxy; moving to next proxy.");
                                moveToNextProxy = true;
                                break;
                            }
                            // Other error — log and continue
                            status?.Invoke($"[{label}] Error while generating URL: {msg}");
                            continue;
                        }

                        var u = rnode["url"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(u)) urls.Add((u, proxy));
                    }
                    catch (Exception ex)
                    {
                        status?.Invoke($"Request error while generating URL: {ex.Message} (proxy={label})");
                    }
                }

                if (!haveKey && !moveToNextProxy)
                {
                    // Will attempt to re-acquire key in the outer while loop
                    break;
                }

                if (urls.Count == 0 && genAttempts < MaxUrlGenAttemptsPerProxy && !moveToNextProxy)
                {
                    var delayMs = 500 * genAttempts; // incremental backoff
                    status?.Invoke($"[{label}] No URLs obtained yet, retrying after {delayMs}ms (attempt {genAttempts}/{MaxUrlGenAttemptsPerProxy})");
                    await Task.Delay(delayMs, ct);
                }
            }

            if (urls.Count > 0)
                return urls.Take(count).ToList();

            if (moveToNextProxy) continue;
        }

        throw new Exception("Could not obtain any working download URLs.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HttpClient BuildClient(string? proxy)
    {
        if (proxy == null) return SharedClient;
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{proxy}"),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }

    private static async Task<JsonNode?> PostJsonAsync(
        HttpClient client, string url, object payload, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(url, content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        // If response is not successful, include snippet for diagnostics
        if (!resp.IsSuccessStatusCode)
        {
            var snippet = GetSnippet(text);
            throw new Exception($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {snippet}");
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException je)
        {
            var start = string.IsNullOrWhiteSpace(text) ? "<empty>" : text.TrimStart()[0].ToString();
            var snippet = GetSnippet(text);
            throw new Exception($"Invalid JSON response: {je.Message}; startsWith='{start}'; snippet={snippet}");
        }
    }

    private static async Task<(JsonNode? node, string text, System.Net.HttpStatusCode status)> PostJsonRawAsync(
        HttpClient client, string url, object payload, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(url, content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? node = null;
        try { node = JsonNode.Parse(text); } catch { }
        return (node, text, resp.StatusCode);
    }

    private static string GetSnippet(string? s, int max = 256)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var cleaned = s.Replace("\r", "\\r").Replace("\n", "\\n");
        if (cleaned.Length <= max) return cleaned;
        return cleaned.Substring(0, max) + "...";
    }
}
