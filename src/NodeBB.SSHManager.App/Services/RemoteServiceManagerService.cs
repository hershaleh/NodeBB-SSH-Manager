using System.Text.RegularExpressions;

namespace NodeBB.SSHManager.Services;

public sealed record RemoteServiceEntry(string Name, string State, string Details);

public sealed class RemoteServiceManagerService
{
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9_.@:-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly SshServerSession _session;
    public RemoteServiceManagerService(SshServerSession session) => _session = session;
    public static bool IsSafeServiceName(string? name) => !string.IsNullOrWhiteSpace(name) && SafeName.IsMatch(name);
    public static bool IsSupportedManager(string? manager) => manager is "SYSTEMD" or "OPENRC" or "SYSV";

    public async Task<(string Manager, IReadOnlyList<RemoteServiceEntry> Services)> ListAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
  printf 'MANAGER|SYSTEMD\n'
  systemctl list-units --type=service --all --no-legend --no-pager 2>/dev/null |
    while read -r unit load active sub rest; do
      [ -n "$unit" ] || continue
      printf '%s|%s|%s\n' "$unit" "$active" "$sub"
    done
elif command -v rc-service >/dev/null 2>&1 && [ -d /etc/init.d ]; then
  printf 'MANAGER|OPENRC\n'
  for file in /etc/init.d/*; do
    [ -x "$file" ] || continue
    name="${file##*/}"
    state="$(rc-service "$name" status 2>&1 | head -n 1 | tr '|' '/')"
    [ -n "$state" ] || state=unknown
    printf '%s|%s|\n' "$name" "$state"
  done
elif command -v service >/dev/null 2>&1 && [ -d /etc/init.d ]; then
  printf 'MANAGER|SYSV\n'
  for file in /etc/init.d/*; do
    [ -x "$file" ] || continue
    name="${file##*/}"
    state="$(service "$name" status 2>&1 | head -n 1 | tr '|' '/')"
    [ -n "$state" ] || state=unknown
    printf '%s|%s|\n' "$name" "$state"
  done
else
  printf 'MANAGER|UNSUPPORTED\n'
fi
""";
        DiagnosticLogService.Instance.Info("SystemServices", "InventoryStarted", "Remote system service inventory started.", _session.Profile.Name);
        var result = await _session.ExecuteAsync(script, cancellationToken);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "לא ניתן לקרוא את רשימת שירותי המערכת." : result.Error);
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var managerLine = lines.FirstOrDefault(line => line.StartsWith("MANAGER|", StringComparison.Ordinal));
        var manager = managerLine?.Split('|', 2).ElementAtOrDefault(1) ?? "UNSUPPORTED";
        if (!IsSupportedManager(manager)) throw new NotSupportedException("לא זוהה מנהל שירותים נתמך (systemd, OpenRC או SysV).");
        var services = lines.Where(line => !line.StartsWith("MANAGER|", StringComparison.Ordinal))
            .Select(line => line.Split('|', 3)).Where(parts => parts.Length >= 2 && IsSafeServiceName(parts[0]))
            .Select(parts => new RemoteServiceEntry(parts[0], parts[1], parts.Length == 3 ? parts[2] : ""))
            .DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Take(500).ToArray();
        DiagnosticLogService.Instance.Info("SystemServices", "InventoryCompleted", $"Found {services.Length} services using {manager}.", _session.Profile.Name);
        return (manager, services);
    }

    public async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(string manager, string serviceName, string action, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedManager(manager)) throw new NotSupportedException("מנהל השירותים אינו נתמך.");
        if (!IsSafeServiceName(serviceName)) throw new ArgumentException("שם השירות אינו תקין.", nameof(serviceName));
        if (action is not ("start" or "stop" or "restart")) throw new ArgumentOutOfRangeException(nameof(action));
        var quoted = ShellQuote(serviceName);
        var command = manager switch
        {
            "SYSTEMD" => $"systemctl {action} {quoted}",
            "OPENRC" => $"rc-service {quoted} {action}",
            "SYSV" => $"if command -v service >/dev/null 2>&1; then service {quoted} {action}; else /etc/init.d/{quoted} {action}; fi",
            _ => throw new NotSupportedException()
        };
        var script = $$"""
if [ "$(id -u)" = "0" ]; then
  {{command}}
elif command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then
  sudo -n {{command}}
else
  echo 'הפעולה דורשת root או sudo ללא סיסמה. לא נעשה ניסיון לבקש סיסמה או לשנות הרשאות.' >&2
  exit 77
fi
""";
        DiagnosticLogService.Instance.Info("SystemServices", "ActionStarted", $"Requested {action} for service {serviceName} using {manager}.", _session.Profile.Name);
        var result = await _session.ExecuteAsync(script, cancellationToken);
        if (result.ExitCode == 0)
            DiagnosticLogService.Instance.Info("SystemServices", "ActionCompleted", $"Service {action} completed for {serviceName}.", _session.Profile.Name);
        else
            DiagnosticLogService.Instance.Error("SystemServices", "ActionFailed", $"Service {action} failed for {serviceName}; exit code {result.ExitCode}: {result.Error}", _session.Profile.Name);
        return result;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
