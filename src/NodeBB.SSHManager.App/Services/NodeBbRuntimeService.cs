using System.Text.RegularExpressions;

namespace NodeBB.SSHManager.Services;

public sealed record NodeBbRuntimeInfo(string Manager, string Identifier);

public sealed class NodeBbRuntimeService
{
    private static readonly Regex ServiceIdentifier = new(@"^[A-Za-z0-9_.@:-]{1,128}$", RegexOptions.Compiled);
    private static readonly Regex ContainerIdentifier = new(@"^[A-Fa-f0-9]{12,64}$", RegexOptions.Compiled);
    private readonly SshServerSession _session;

    public NodeBbRuntimeService(SshServerSession session) => _session = session;

    public static bool IsSafeIdentifier(string manager, string identifier) => manager switch
    {
        "SYSTEMD" or "OPENRC" or "SYSV" => ServiceIdentifier.IsMatch(identifier),
        "DOCKER" => ContainerIdentifier.IsMatch(identifier),
        "PM2" => !string.IsNullOrWhiteSpace(identifier) && identifier.Length <= 256 && !identifier.Any(char.IsControl),
        "MANUAL" => identifier == "CLI",
        _ => false
    };

    public async Task<NodeBbRuntimeInfo> DetectAsync(
        NodeBbInstallation installation, CancellationToken cancellationToken = default)
    {
        var script = $$"""
INSTALL_PATH={{ShellQuote(installation.Path)}}
if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
  for f in /etc/systemd/system/*.service /lib/systemd/system/*.service /usr/lib/systemd/system/*.service; do
    [ -f "$f" ] || continue
    if grep -Fq -- "$INSTALL_PATH" "$f"; then
      printf 'SYSTEMD|%s\n' "$(basename "$f")"
      exit 0
    fi
  done
fi
for f in /etc/init.d/*; do
  [ -f "$f" ] || continue
  if grep -Fq -- "$INSTALL_PATH" "$f"; then
    SERVICE_NAME="$(basename "$f")"
    if command -v rc-service >/dev/null 2>&1; then printf 'OPENRC|%s\n' "$SERVICE_NAME"
    else printf 'SYSV|%s\n' "$SERVICE_NAME"
    fi
    exit 0
  fi
done
if command -v pm2 >/dev/null 2>&1 && command -v node >/dev/null 2>&1; then
  pm2 jlist 2>/dev/null | node -e 'let s="";process.stdin.on("data",d=>s+=d);process.stdin.on("end",()=>{try{const p=JSON.parse(s);const x=p.find(v=>v.pm2_env&&v.pm2_env.pm_cwd===process.argv[1]);if(x)console.log("PM2|"+x.name)}catch(e){}})' "$INSTALL_PATH"
fi
if command -v docker >/dev/null 2>&1; then
  for id in $(docker ps -q 2>/dev/null); do
    if docker inspect "$id" 2>/dev/null | grep -Fq -- "$INSTALL_PATH"; then
      printf 'DOCKER|%s\n' "$id"
      exit 0
    fi
  done
fi
printf 'MANUAL|CLI\n'
""";

        DiagnosticLogService.Instance.Info("NodeBBRuntime", "DetectionStarted", "Detecting NodeBB process manager.", _session.Profile.Name);
        var result = await _session.ExecuteAsync(script, cancellationToken);
        var line = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(value => value.Contains('|'));
        if (line is null)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "לא ניתן לזהות את שיטת ההפעלה של NodeBB." : result.Error);

        var parts = line.Split('|', 2);
        if (parts.Length != 2 || !IsSafeIdentifier(parts[0], parts[1]))
            throw new InvalidOperationException("השרת החזיר מזהה מנהל שירותים לא תקין; הפעולה נעצרה מטעמי בטיחות.");

        var runtime = new NodeBbRuntimeInfo(parts[0], parts[1]);
        DiagnosticLogService.Instance.Info("NodeBBRuntime", "DetectionCompleted",
            $"Detected NodeBB runtime manager: {runtime.Manager}.", _session.Profile.Name);
        return runtime;
    }

    public async Task<(int ExitCode, string Output, string Error)> ExecuteLifecycleAsync(
        NodeBbInstallation installation, NodeBbRuntimeInfo runtime, string action,
        CancellationToken cancellationToken = default)
    {
        if (action is not ("start" or "stop" or "restart"))
            throw new ArgumentOutOfRangeException(nameof(action));
        if (!IsSafeIdentifier(runtime.Manager, runtime.Identifier))
            throw new InvalidOperationException("מזהה מנהל השירותים אינו תקין.");

        var path = ShellQuote(installation.Path);
        var identifier = ShellQuote(runtime.Identifier);
        var command = runtime.Manager switch
        {
            "SYSTEMD" => PrivilegedCommand($"systemctl {action} {identifier}"),
            "OPENRC" => PrivilegedCommand($"rc-service {identifier} {action}"),
            "SYSV" => PrivilegedCommand($"if command -v service >/dev/null 2>&1; then service {identifier} {action}; else /etc/init.d/{identifier} {action}; fi"),
            "PM2" => $"pm2 {(action == "start" ? "restart" : action)} {identifier}",
            "DOCKER" => $"docker {action} {identifier}",
            "MANUAL" => $"cd {path} && ./nodebb {action}",
            _ => throw new InvalidOperationException("מנהל ההפעלה אינו נתמך.")
        };

        DiagnosticLogService.Instance.Info("NodeBBRuntime", "LifecycleStarted",
            $"NodeBB {action} requested through {runtime.Manager}.", _session.Profile.Name);
        var result = await _session.ExecuteAsync(command, cancellationToken);
        if (result.ExitCode == 0)
            DiagnosticLogService.Instance.Info("NodeBBRuntime", "LifecycleCompleted",
                $"NodeBB {action} completed through {runtime.Manager}.", _session.Profile.Name);
        else
            DiagnosticLogService.Instance.Error("NodeBBRuntime", "LifecycleFailed",
                $"NodeBB {action} failed through {runtime.Manager}; exit code {result.ExitCode}: {result.Error}", _session.Profile.Name);
        return result;
    }

    private static string PrivilegedCommand(string command) => $"""
if [ "$(id -u)" = "0" ]; then
  {command}
elif command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then
  sudo -n {command}
else
  echo 'הפעולה דורשת root או sudo ללא סיסמה; לא נעשה ניסיון לבקש סיסמה או לשנות הרשאות.' >&2
  exit 77
fi
""";

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
