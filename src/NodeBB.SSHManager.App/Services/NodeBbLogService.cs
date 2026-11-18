namespace NodeBB.SSHManager.Services;

public sealed class NodeBbLogService
{
    private readonly SshServerSession _session;

    public NodeBbLogService(SshServerSession session) => _session = session;

    public async Task<string> ReadRecentLogsAsync(
        NodeBbInstallation installation, int lineCount = 500,
        CancellationToken cancellationToken = default)
    {
        if (lineCount is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(lineCount), "ניתן לקרוא בין שורה אחת ל-2,000 שורות.");

        var path = ShellQuote(installation.Path);
        var script = $"""
cd {path} || exit 1
LOG_PATH="logs/output.log"
if command -v node >/dev/null 2>&1 && [ -r config.json ]; then
  configured="$(node -p "require('path').resolve('.', require('./config.json').logFile || 'logs/output.log')" 2>/dev/null)"
  [ -n "$configured" ] && LOG_PATH="$configured"
fi
if [ ! -r "$LOG_PATH" ]; then
  echo "לא נמצא קובץ לוג קריא: $LOG_PATH" >&2
  exit 4
fi
tail -n {lineCount} "$LOG_PATH"
""";

        DiagnosticLogService.Instance.Info("NodeBBLogs", "ReadStarted",
            $"Reading the latest {lineCount} NodeBB log lines from {installation.Path}.", _session.Profile.Name);
        var result = await _session.ExecuteAsync(script, cancellationToken);
        if (result.ExitCode != 0)
        {
            var error = new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                ? $"קריאת הלוגים נכשלה (קוד {result.ExitCode})."
                : result.Error);
            DiagnosticLogService.Instance.Error("NodeBBLogs", "ReadFailed", error, _session.Profile.Name);
            throw error;
        }

        DiagnosticLogService.Instance.Info("NodeBBLogs", "ReadCompleted",
            $"Read {result.Output.Split('\n').Length} log line(s).", _session.Profile.Name);
        return result.Output;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
