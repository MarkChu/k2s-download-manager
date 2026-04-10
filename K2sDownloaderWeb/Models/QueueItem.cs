using System.Text.Json.Serialization;

namespace K2sDownloaderWeb.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueueStatus { Pending, Downloading, Done, Failed, Cancelled }

public class QueueItem
{
    public Guid    Id           { get; set; } = Guid.NewGuid();
    public string  Url          { get; set; } = string.Empty;
    public string? Filename     { get; set; }
    public QueueStatus Status   { get; set; } = QueueStatus.Pending;
    public string? ErrorMessage { get; set; }
    public string? OutputFile   { get; set; }
    public DateTime AddedAt     { get; set; } = DateTime.UtcNow;
}
