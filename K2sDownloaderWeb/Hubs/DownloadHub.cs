using K2sDownloaderWeb.Services;
using Microsoft.AspNetCore.SignalR;

namespace K2sDownloaderWeb.Hubs;

public class DownloadHub : Hub
{
    private readonly DownloadOrchestrator _orchestrator;

    public DownloadHub(DownloadOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    /// <summary>Called by the browser when the user manually solves a captcha.</summary>
    public void SubmitCaptcha(string captchaId, string answer)
    {
        _orchestrator.TryResolveCaptcha(captchaId, answer);
    }
}
