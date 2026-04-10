using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace K2sDownloaderWinForms.Core;

public static class GeminiClient
{
    private const string ApiBase = "https://generativelanguage.googleapis.com/v1beta/models";
    private const string Model = "gemma-4-31b-it";

    // Increase timeout to allow slower model responses; per-call timeouts are
    // still respected by passing a CancellationToken to SolveCaptchaAsync.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // Common English words to skip when scanning for the captcha answer
    private static readonly HashSet<string> _skipWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","user","wants","captcha","image","char","from","with","that",
        "this","look","like","more","each","just","only","let","check","final",
        "clear","looks","these","first","second","third","fourth","fifth","sixth",
        "color","blue","pink","red","green","yellow","orange","purple","brown",
        "uppercase","lowercase","sequence","answer","output","verify","analyze",
        "close","based","font","style","line","across","more","common","usually",
        "could","there","might","have","them","they","then","some","very","also"
    };

    /// <summary>
    /// Sends <paramref name="imageBytes"/> to Gemini and returns the captcha text,
    /// or null if the call failed / returned nothing useful.
    /// </summary>
    public static async Task<string?> SolveCaptchaAsync(
        byte[] imageBytes, string apiKey, CancellationToken ct = default)
    {
        var mime = DetectMimeType(imageBytes);
        var b64  = Convert.ToBase64String(imageBytes);

        var body = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["text"] = "Output ONLY the alphanumeric characters shown in this captcha image. " +
                                       "No spaces. No punctuation. No explanation. Just the characters."
                        },
                        new JsonObject
                        {
                            ["inline_data"] = new JsonObject
                            {
                                ["mime_type"] = mime,
                                ["data"]      = b64
                            }
                        }
                    }
                }
            },
            // Constrain output length so reasoning models can't dump long explanations
            ["generationConfig"] = new JsonObject
            {
                ["maxOutputTokens"] = 20,
                ["temperature"]     = 0.0
            }
        };

        var url = $"{ApiBase}/{Model}:generateContent?key={apiKey}";
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(url, content, ct);

        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode} — {raw}");

        var json = JsonNode.Parse(raw);

        // Surface finish-reason or error block if present
        var finishReason = json?["candidates"]?[0]?["finishReason"]?.GetValue<string>();
        if (finishReason is "ERROR" or "SAFETY" or "RECITATION")
            throw new Exception($"Gemini finishReason={finishReason}. Response: {raw}");

        var text = json?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]
                        ?.GetValue<string>()
                        ?.Trim();

        if (string.IsNullOrWhiteSpace(text))
            throw new Exception($"Gemini response had no text. Full response: {raw}");

        return ExtractCaptchaChars(text);
    }

    /// <summary>
    /// Extracts the captcha answer from the model's raw response.
    /// Strategy: scan lines bottom-up for a short alphanumeric token that looks like a captcha
    /// (mixed case or contains a digit, not a common English word).
    /// </summary>
    private static string ExtractCaptchaChars(string raw)
    {
        // Try lines from bottom to top — models tend to put the final answer last
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines.Reverse())
        {
            // Strip markdown, quotes and common label prefixes ("Answer: ", "Sequence: ", etc.)
            var stripped = Regex.Replace(line, @"^[\w\s]+:\s*", "").Trim('*', '_', '`', '"', '\'', '.', ' ');

            if (LooksCaptcha(stripped))
                return stripped;
        }

        // Fallback: scan all word tokens in the full text
        var matches = Regex.Matches(raw, @"\b([a-zA-Z0-9]{4,10})\b");
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var candidate = matches[i].Groups[1].Value;
            if (LooksCaptcha(candidate))
                return candidate;
        }

        // Last resort — return trimmed raw and let the server reject it
        return raw.Trim();
    }

    private static bool LooksCaptcha(string s)
    {
        if (!Regex.IsMatch(s, @"^[a-zA-Z0-9]{4,10}$")) return false;
        if (_skipWords.Contains(s)) return false;
        bool hasMixedCase = s.Any(char.IsUpper) && s.Any(char.IsLower);
        bool hasDigit     = s.Any(char.IsDigit);
        return hasMixedCase || hasDigit;
    }

    private static string DetectMimeType(byte[] b)
    {
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50) return "image/png";
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49) return "image/gif";
        return "image/png";
    }
}
