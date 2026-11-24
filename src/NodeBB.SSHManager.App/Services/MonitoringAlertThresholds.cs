namespace NodeBB.SSHManager.Services;

public sealed record MonitoringAlertThresholds
{
    public int CpuPercent { get; init; } = 90;
    public int MemoryPercent { get; init; } = 90;
    public int SwapPercent { get; init; } = 90;
    public int DiskPercent { get; init; } = 90;
    public int InodePercent { get; init; } = 90;
    public double LoadPerCpu { get; init; } = 1.5;

    public void Validate()
    {
        ValidatePercent(CpuPercent, nameof(CpuPercent));
        ValidatePercent(MemoryPercent, nameof(MemoryPercent));
        ValidatePercent(SwapPercent, nameof(SwapPercent));
        ValidatePercent(DiskPercent, nameof(DiskPercent));
        ValidatePercent(InodePercent, nameof(InodePercent));
        if (!double.IsFinite(LoadPerCpu) || LoadPerCpu is < 0.5 or > 10)
            throw new ArgumentOutOfRangeException(nameof(LoadPerCpu));
    }

    private static void ValidatePercent(int value, string name)
    {
        if (value is < 50 or > 100)
            throw new ArgumentOutOfRangeException(name, "Alert percentages must be between 50 and 100.");
    }
}
