namespace NodeBB.SSHManager.Services;

public sealed record ServerMonitoringSample(DateTimeOffset CollectedAt, int? CpuPercent, int? MemoryPercent);

public sealed class ServerMonitoringHistory
{
    private readonly int _capacity;
    private readonly List<ServerMonitoringSample> _samples = [];

    public ServerMonitoringHistory(int capacity = 60)
    {
        if (capacity is < 2 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(capacity), "History capacity must be between 2 and 1440 samples.");
        _capacity = capacity;
    }

    public IReadOnlyList<ServerMonitoringSample> Samples => _samples.AsReadOnly();

    public void Add(DateTimeOffset collectedAt, int? cpuPercent, int? memoryPercent)
    {
        _samples.Add(new ServerMonitoringSample(collectedAt, Validate(cpuPercent), Validate(memoryPercent)));
        if (_samples.Count > _capacity)
            _samples.RemoveRange(0, _samples.Count - _capacity);
    }

    private static int? Validate(int? value) => value is >= 0 and <= 100 ? value : null;
}
