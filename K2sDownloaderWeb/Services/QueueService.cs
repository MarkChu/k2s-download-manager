using System.Text.Json;
using System.Text.Json.Serialization;
using K2sDownloaderWeb.Models;

namespace K2sDownloaderWeb.Services;

public class QueueService
{
    private readonly string _queuePath = "queue.json";
    private readonly List<QueueItem> _items;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public QueueService()
    {
        _items = Load();
        // Reset any items stuck in Downloading state from a previous run
        foreach (var item in _items.Where(i => i.Status == QueueStatus.Downloading))
            item.Status = QueueStatus.Pending;
        Save();
    }

    public List<QueueItem> GetAll()
    {
        lock (_lock) return _items.ToList();
    }

    public QueueItem Add(string url, string? filename)
    {
        var item = new QueueItem { Url = url.Trim(), Filename = string.IsNullOrWhiteSpace(filename) ? null : filename.Trim() };
        lock (_lock)
        {
            _items.Add(item);
            Save();
        }
        return item;
    }

    public bool Remove(Guid id)
    {
        lock (_lock)
        {
            var removed = _items.RemoveAll(i => i.Id == id && i.Status != QueueStatus.Downloading) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public bool Retry(Guid id)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null || item.Status is not (QueueStatus.Failed or QueueStatus.Cancelled))
                return false;
            item.Status = QueueStatus.Pending;
            item.ErrorMessage = null;
            item.OutputFile = null;
            Save();
            return true;
        }
    }

    public QueueItem? GetNextPending()
    {
        lock (_lock) return _items.FirstOrDefault(i => i.Status == QueueStatus.Pending);
    }

    public void UpdateStatus(Guid id, QueueStatus status, string? error = null, string? outputFile = null)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null) return;
            item.Status = status;
            item.ErrorMessage = error;
            if (outputFile is not null) item.OutputFile = outputFile;
            Save();
        }
    }

    private List<QueueItem> Load()
    {
        if (!File.Exists(_queuePath)) return new();
        try
        {
            var json = File.ReadAllText(_queuePath);
            return JsonSerializer.Deserialize<List<QueueItem>>(json, _jsonOpts) ?? new();
        }
        catch { return new(); }
    }

    private void Save()
    {
        try { File.WriteAllText(_queuePath, JsonSerializer.Serialize(_items, _jsonOpts)); }
        catch { }
    }
}
