using System.Text.Json;

namespace NodeBB.SSHManager.Services;

public sealed record MonitoringAlertEvent(DateTimeOffset OccurredAt, string Message, bool IsActive);

public sealed class MonitoringAlertHistory
{
    private readonly int _capacity;
    private readonly List<MonitoringAlertEvent> _events = [];
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    public MonitoringAlertHistory(IEnumerable<MonitoringAlertEvent>? initialEvents = null, int capacity = 100)
    {
        if (capacity is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        if (initialEvents is null) return;
        foreach (var item in initialEvents.TakeLast(capacity))
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Message) || item.Message.Length > 500) continue;
            _events.Add(item);
            if (item.IsActive) _active.Add(item.Message);
            else _active.Remove(item.Message);
        }
    }

    public IReadOnlyList<MonitoringAlertEvent> Events => _events.AsReadOnly();

    public IReadOnlyList<MonitoringAlertEvent> Update(DateTimeOffset at, IEnumerable<string> currentAlerts)
    {
        ArgumentNullException.ThrowIfNull(currentAlerts);
        var current = currentAlerts.Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 500)
            .ToHashSet(StringComparer.Ordinal);
        var changes = new List<MonitoringAlertEvent>();
        foreach (var message in current.Except(_active, StringComparer.Ordinal))
            changes.Add(new MonitoringAlertEvent(at, message, true));
        foreach (var message in _active.Except(current, StringComparer.Ordinal))
            changes.Add(new MonitoringAlertEvent(at, message, false));
        foreach (var change in changes)
        {
            _events.Add(change);
            if (change.IsActive) _active.Add(change.Message);
            else _active.Remove(change.Message);
        }
        if (_events.Count > _capacity) _events.RemoveRange(0, _events.Count - _capacity);
        return changes;
    }
}

public sealed class MonitoringAlertHistoryStore
{
    private const int MaximumEvents = 100;
    private const long MaximumFileBytes = 128 * 1024;
    private readonly string _directory;

    public MonitoringAlertHistoryStore(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NodeBB SSH Manager", "Monitoring"));
    }

    public IReadOnlyList<MonitoringAlertEvent> Load(Guid profileId)
    {
        var path = GetPath(profileId);
        if (!File.Exists(path)) return [];
        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
                throw new InvalidDataException("Alert history is too large.");
            return (JsonSerializer.Deserialize<List<MonitoringAlertEvent>>(File.ReadAllText(path)) ?? [])
                .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Message) && x.Message.Length <= 500)
                .TakeLast(MaximumEvents).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            try { File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak", false); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return [];
        }
    }

    public void Save(Guid profileId, IEnumerable<MonitoringAlertEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var snapshot = events.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Message) && x.Message.Length <= 500)
            .TakeLast(MaximumEvents).ToArray();
        var path = GetPath(profileId);
        Directory.CreateDirectory(_directory);
        var temporary = Path.Combine(_directory, $".alerts-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, snapshot);
                stream.Flush(flushToDisk: true);
            }
            if (new FileInfo(temporary).Length > MaximumFileBytes)
                throw new InvalidDataException("Alert history exceeds the allowed size.");
            if (File.Exists(path)) File.Replace(temporary, path, null, true);
            else File.Move(temporary, path);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }

    private string GetPath(Guid profileId)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("A valid server profile ID is required.", nameof(profileId));
        return Path.Combine(_directory, profileId.ToString("N") + ".alerts.json");
    }
}
