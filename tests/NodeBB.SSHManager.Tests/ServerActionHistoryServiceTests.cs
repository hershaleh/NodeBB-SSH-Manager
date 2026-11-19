using System.IO;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class ServerActionHistoryServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nodebb-history-tests", Guid.NewGuid().ToString("N") + ".json");
    private readonly ServerActionHistoryService _service;

    public ServerActionHistoryServiceTests() => _service = new ServerActionHistoryService(_path);

    [Fact]
    public void AddAndRead_PersistsEntriesForOnlyTheRequestedServer()
    {
        _service.Add(Entry("host-a", 22, "עדכון", true));
        _service.Add(Entry("host-b", 22, "גיבוי", true));

        var entries = _service.GetForServer("HOST-A", 22);

        var entry = Assert.Single(entries);
        Assert.Equal("עדכון", entry.Action);
        Assert.Empty(_service.GetForServer("host-a", 23));
    }

    [Fact]
    public void ClearForServer_RemovesOnlyMatchingServerHistory()
    {
        _service.Add(Entry("host-a", 22, "עדכון", true));
        _service.Add(Entry("host-b", 22, "גיבוי", true));

        _service.ClearForServer("host-a", 22);

        Assert.Empty(_service.GetForServer("host-a", 22));
        Assert.Single(_service.GetForServer("host-b", 22));
    }

    [Fact]
    public void Add_RedactsSecretsAndLimitsHistoryPerServer()
    {
        for (var i = 0; i < 105; i++)
            _service.Add(Entry("host-a", 22, "פעולה", true) with { Summary = "token=secret-" + i });

        var entries = _service.GetForServer("host-a", 22);

        Assert.Equal(100, entries.Count);
        Assert.All(entries, entry => Assert.DoesNotContain("secret-", entry.Summary, StringComparison.Ordinal));
        Assert.Contains("[REDACTED]", entries[0].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_RejectsInvalidServerPort()
    {
        Assert.Throws<ArgumentException>(() => _service.Add(Entry("host-a", 0, "פעולה", true)));
    }


    [Fact]
    public void ExportCsv_QuotesAndEscapesFieldsAndKeepsHebrewHeaders()
    {
        var entry = Entry("host-a", 22, "עדכון", false) with
        {
            ServerName = "שרת, ראשי",
            Target = "pkg\"quoted\"",
            Summary = "שורה ראשונה, שורה שנייה"
        };

        var csv = ServerActionHistoryService.ExportCsv([entry]);

        Assert.Contains("תאריך ושעה,שרת,כתובת,פורט,פעולה,יעד,תוצאה,סיכום", csv, StringComparison.Ordinal);
        Assert.Contains("\"שרת, ראשי\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"pkg\"\"quoted\"\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"שורה ראשונה, שורה שנייה\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"נכשל\"", csv, StringComparison.Ordinal);
    }

    private static ServerActionHistoryEntry Entry(string host, int port, string action, bool succeeded) =>
        new(DateTimeOffset.UtcNow, "שרת בדיקה", host, port, action, "", succeeded, "בוצע");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }
}
