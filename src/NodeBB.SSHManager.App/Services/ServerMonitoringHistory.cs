using System.Text.Json;

namespace NodeBB.SSHManager.Services;

public sealed record ServerMonitoringSample(
    DateTimeOffset CollectedAt,
    int? CpuPercent,
    int? MemoryPercent,
    double? LoadPerCpu = null);

public sealed class ServerMonitoringHistory
{
    private readonly int _capacity;
    private readonly List<ServerMonitoringSample> _samples = [];

    public ServerMonitoringHistory(int capacity = 1440, IEnumerable<ServerMonitoringSample>? initialSamples = null)
    {
        if (capacity is < 2 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(capacity), "History capacity must be between 2 and 1440 samples.");
        _capacity = capacity;
        if (initialSamples is not null)
        {
            foreach (var sample in initialSamples.TakeLast(capacity))
            {
                if (sample is null) continue;
                _samples.Add(Sanitize(sample));
            }
        }
    }

    public IReadOnlyList<ServerMonitoringSample> Samples => _samples.AsReadOnly();

    public void Add(DateTimeOffset collectedAt, int? cpuPercent, int? memoryPercent, double? loadPerCpu = null)
    {
        _samples.Add(Sanitize(new ServerMonitoringSample(collectedAt, cpuPercent, memoryPercent, loadPerCpu)));
        if (_samples.Count > _capacity)
            _samples.RemoveRange(0, _samples.Count - _capacity);
    }

    public IReadOnlyList<ServerMonitoringSample> GetSince(DateTimeOffset since) =>
        _samples.Where(sample => sample.CollectedAt >= since).ToArray();

    private static ServerMonitoringSample Sanitize(ServerMonitoringSample sample) =>
        new(sample.CollectedAt, Validate(sample.CpuPercent), Validate(sample.MemoryPercent),
            sample.LoadPerCpu is double load && double.IsFinite(load) && load is >= 0 and <= 1000 ? load : null);

    private static int? Validate(int? value) => value is >= 0 and <= 100 ? value : null;
}

public sealed class ServerMonitoringHistoryStore
{
    public const int MaximumSamples = 1440;
    private const long MaximumFileBytes = 512 * 1024;
    private readonly string _directory;

    public ServerMonitoringHistoryStore(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NodeBB SSH Manager", "Monitoring"));
    }

    public IReadOnlyList<ServerMonitoringSample> Load(Guid profileId)
    {
        var path = GetPath(profileId);
        if (!File.Exists(path)) return [];
        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
                throw new InvalidDataException("Monitoring history file exceeds the allowed size.");
            var samples = JsonSerializer.Deserialize<List<ServerMonitoringSample>>(File.ReadAllText(path)) ?? [];
            return samples.Where(sample => sample is not null)
                .TakeLast(MaximumSamples)
                .Select(sample => new ServerMonitoringSample(sample.CollectedAt,
                    sample.CpuPercent is >= 0 and <= 100 ? sample.CpuPercent : null,
                    sample.MemoryPercent is >= 0 and <= 100 ? sample.MemoryPercent : null,
                    sample.LoadPerCpu is double load && double.IsFinite(load) && load is >= 0 and <= 1000 ? load : null))
                .ToArray();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try { File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak", false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return [];
        }
    }

    public void Save(Guid profileId, IEnumerable<ServerMonitoringSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var snapshot = samples.Where(sample => sample is not null)
            .TakeLast(MaximumSamples)
            .Select(sample => new ServerMonitoringSample(sample.CollectedAt,
                sample.CpuPercent is >= 0 and <= 100 ? sample.CpuPercent : null,
                sample.MemoryPercent is >= 0 and <= 100 ? sample.MemoryPercent : null,
                sample.LoadPerCpu is double load && double.IsFinite(load) && load is >= 0 and <= 1000 ? load : null))
            .ToArray();
        var path = GetPath(profileId);
        Directory.CreateDirectory(_directory);
        var temporary = Path.Combine(_directory, $".history-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, snapshot);
                stream.Flush(flushToDisk: true);
            }
            if (new FileInfo(temporary).Length > MaximumFileBytes)
                throw new InvalidDataException("Monitoring history exceeds the allowed size.");
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
        if (profileId == Guid.Empty)
            throw new ArgumentException("A valid server profile ID is required.", nameof(profileId));
        return Path.Combine(_directory, profileId.ToString("N") + ".history.json");
    }
}
