using System.Text.Json;
using System.Text.Json.Serialization;

namespace K2sDownloaderWinForms.Core;

public class AppSettings
{
    // ── Gemini API Key ────────────────────────────────────────────────────────

    public string GeminiApiKey { get; set; } = string.Empty;

    // ── Wit.ai API Key (for reCaptcha audio solving on KatFile) ───────────────

    public string WitAiApiKey { get; set; } = string.Empty;

    // ── Download settings ─────────────────────────────────────────────────────

    public int  Threads           { get; set; } = 20;
    public int  SplitSizeMb       { get; set; } = 20;
    public bool FfmpegCheck       { get; set; } = true;
    public int  MaxProxies        { get; set; } = 1000;
    public bool RevalidateProxies { get; set; } = false;

    // ── Download retry ────────────────────────────────────────────────────────
    /// <summary>How many times the CLI outer loop retries the whole download session on failure.</summary>
    public int DownloadMaxRetries { get; set; } = 3;

    // ── Proxy auto-refresh ────────────────────────────────────────────────────
    /// <summary>How often (in minutes) the background proxy refresh runs. 0 = disabled.</summary>
    public int ProxyRefreshIntervalMin { get; set; } = 5;

    // ── Auto-solve (captcha) settings
    public int AutoSolveAttempts { get; set; } = 3;
    public int AutoSolvePerAttemptTimeoutSec { get; set; } = 60;
    public int AutoSolveBaseDelayMs { get; set; } = 600;
    public int AutoSolveMaxDelayMs { get; set; } = 5000;

    // ── Download directory ────────────────────────────────────────────────────

    /// <summary>
    /// Directory where downloaded files are saved.
    /// Defaults to the application's base directory when empty or unset.
    /// </summary>
    public string DownloadDirectory { get; set; } = string.Empty;

    [JsonIgnore]
    public string EffectiveDownloadDirectory =>
        string.IsNullOrWhiteSpace(DownloadDirectory)
            ? AppContext.BaseDirectory
            : DownloadDirectory;

    // ── Proxy source URLs ─────────────────────────────────────────────────────

    /// <summary>
    /// URLs to fetch raw proxy lists from (one proxy per line, host:port format).
    /// If empty, built-in defaults are used.
    /// </summary>
    public List<string> ProxySourceUrls { get; set; } = new()
    {
        "https://api.proxyscrape.com/v4/free-proxy-list/get?request=getproxies&protocol=all&timeout=10000&country=all&ssl=all&anonymity=all&limit=2000",
        "https://cdn.jsdelivr.net/gh/proxifly/free-proxy-list@main/proxies/protocols/http/data.txt"
    };

    // ── Recent URLs (last 3) ──────────────────────────────────────────────────

    public List<string> RecentUrls { get; set; } = new();

    /// <summary>Prepends <paramref name="url"/> to the recent list and saves.</summary>
    public void AddRecentUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        RecentUrls.Remove(url);
        RecentUrls.Insert(0, url);
        if (RecentUrls.Count > 3)
            RecentUrls.RemoveRange(3, RecentUrls.Count - 3);
        Save();
    }

    // ── Singleton ─────────────────────────────────────────────────────────────

    public static AppSettings Current { get; private set; } = Load();

    private static IEnumerable<string> GetCandidateSettingsPaths()
    {
        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            yield return Path.Combine(AppContext.BaseDirectory, "settings.json");

        if (!string.IsNullOrWhiteSpace(AppDomain.CurrentDomain.BaseDirectory))
            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        var currentDir = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(currentDir))
            yield return Path.Combine(currentDir, "settings.json");
    }

    private static string? FindSettingsPath()
    {
        foreach (var path in GetCandidateSettingsPaths())
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    public static AppSettings Load()
    {
        var settingsPath = FindSettingsPath();
        if (settingsPath == null) return new AppSettings();
        try
        {
            var json = File.ReadAllText(settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        var settingsPath = FindSettingsPath() ?? GetCandidateSettingsPaths().First();
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(settingsPath, json);
        Current = this;
    }

}
