namespace NodeBB.SSHManager.Services;

public static class RemoteMonitoringOutputParser
{
    public static RemoteServerMetrics Parse(string output, DateTimeOffset? collectedAt = null)
    {
        var values = (output ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.OrdinalIgnoreCase);

        string Get(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : "לא זמין";

        var memoryPercent = ParsePercent(Get("MEMORY_PERCENT"));
        var cpuCount = int.TryParse(Get("CPU_COUNT"), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var cpuValue) && cpuValue is > 0 and <= 4096
            ? cpuValue : 1;

        return new RemoteServerMetrics(
            Get("UPTIME"), Get("LOAD"), Get("MEMORY"), memoryPercent,
            Get("DISK"), Get("PROCESSES"), cpuCount, collectedAt ?? DateTimeOffset.UtcNow,
            Get("SWAP"), ParsePercent(Get("SWAP_PERCENT")), Get("DISK_INODES"));
    }

    private static int? ParsePercent(string value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 100
            ? parsed : null;
    }
}
