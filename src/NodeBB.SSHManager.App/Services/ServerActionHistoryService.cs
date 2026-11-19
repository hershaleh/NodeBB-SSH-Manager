using System.Text.Json;

namespace NodeBB.SSHManager.Services;

public sealed record ServerActionHistoryEntry(
    DateTimeOffset Timestamp,
    string ServerName,
    string ServerHost,
    int ServerPort,
    string Action,
    string Target,
    bool Succeeded,
    string Summary);

public sealed class ServerActionHistoryService
{
    private const int MaxEntriesPerServer = 100;
    private const int MaxEntriesTotal = 2000;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string StoragePath { get; }

    public ServerActionHistoryService(string? storagePath = null)
    {
        StoragePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NodeBB SSH Manager", "action-history.json");
    }

    public IReadOnlyList<ServerActionHistoryEntry> GetForServer(string host, int port)
    {
        lock (Sync)
            return ReadEntries()
                .Where(entry => SameServer(entry, host, port))
                .OrderByDescending(entry => entry.Timestamp)
                .Take(MaxEntriesPerServer)
                .ToArray();
    }

    public void Add(ServerActionHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.ServerHost) || entry.ServerPort is < 1 or > 65535)
            throw new ArgumentException("פרטי השרת בהיסטוריה אינם תקינים.", nameof(entry));

        lock (Sync)
        {
            var entries = ReadEntries();
            entries.Add(entry with
            {
                ServerName = Limit(entry.ServerName, 120),
                ServerHost = Limit(entry.ServerHost, 255),
                Action = Limit(entry.Action, 100),
                Target = Limit(entry.Target, 2000),
                Summary = Limit(DiagnosticLogService.Redact(entry.Summary ?? string.Empty), 1000)
            });
            var retained = entries
                .GroupBy(item => $"{item.ServerHost.ToUpperInvariant()}:{item.ServerPort}", StringComparer.Ordinal)
                .SelectMany(group => group.OrderByDescending(item => item.Timestamp).Take(MaxEntriesPerServer))
                .OrderByDescending(item => item.Timestamp)
                .Take(MaxEntriesTotal)
                .ToList();
            WriteEntries(retained);
        }
    }

    public void ClearForServer(string host, int port)
    {
        lock (Sync)
        {
            var retained = ReadEntries().Where(entry => !SameServer(entry, host, port)).ToList();
            WriteEntries(retained);
        }
    }

    private List<ServerActionHistoryEntry> ReadEntries()
    {
        try
        {
            if (!File.Exists(StoragePath)) return [];
            return JsonSerializer.Deserialize<List<ServerActionHistoryEntry>>(
                File.ReadAllText(StoragePath), JsonOptions) ?? [];
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private void WriteEntries(List<ServerActionHistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(StoragePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = StoragePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temporaryPath, StoragePath, true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static bool SameServer(ServerActionHistoryEntry entry, string host, int port) =>
        string.Equals(entry.ServerHost, host, StringComparison.OrdinalIgnoreCase) && entry.ServerPort == port;

    private static string Limit(string? value, int maxLength)
    {
        value ??= string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
