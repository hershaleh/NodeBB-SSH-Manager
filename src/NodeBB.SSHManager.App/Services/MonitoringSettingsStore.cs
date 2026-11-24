using System.Text.Json;

namespace NodeBB.SSHManager.Services;

public sealed class MonitoringSettingsStore
{
    private const long MaximumFileBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _directory;

    public MonitoringSettingsStore(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NodeBB SSH Manager", "Monitoring"));
    }

    public MonitoringAlertThresholds Load(Guid profileId)
    {
        var path = GetPath(profileId);
        if (!File.Exists(path)) return new MonitoringAlertThresholds();
        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
                throw new InvalidDataException("Monitoring settings file exceeds the allowed size.");
            var settings = JsonSerializer.Deserialize<MonitoringAlertThresholds>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException("Monitoring settings are empty.");
            settings.Validate();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            PreserveCorruptFile(path);
            return new MonitoringAlertThresholds();
        }
    }

    public void Save(Guid profileId, MonitoringAlertThresholds settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var path = GetPath(profileId);
        Directory.CreateDirectory(_directory);
        var temporary = Path.Combine(_directory, $".monitoring-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, Options);
                stream.Flush(flushToDisk: true);
            }
            if (new FileInfo(temporary).Length > MaximumFileBytes)
                throw new InvalidDataException("Monitoring settings exceed the allowed size.");
            if (File.Exists(path)) File.Replace(temporary, path, null, true);
            else File.Move(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    private string GetPath(Guid profileId)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("A valid server profile ID is required.", nameof(profileId));
        return Path.Combine(_directory, profileId.ToString("N") + ".json");
    }

    private static void PreserveCorruptFile(string path)
    {
        try { File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak", false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
