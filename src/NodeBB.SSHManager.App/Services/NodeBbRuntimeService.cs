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
        "SYSTEMD" or "OPENRC" or "SYSV" or "SUPERVISOR" => ServiceIdentifier.IsMatch(identifier),
        "DOCKER" or "PODMAN" => ContainerIdentifier.IsMatch(identifier),
        "PM2" => Regex.IsMatch(identifier, @"^\d{1,8}$"),
        "MANUAL" => identifier == "CLI",
        _ => false
    };

    public async Task<NodeBbRuntimeInfo> DetectAsync(
        NodeBbInstallation installation, CancellationToken cancellationToken = default)
    {
        var script = $$$"""
INSTALL_PATH={{{ShellQuote(installation.Path)}}}
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
if command -v supervisorctl >/dev/null 2>&1; then
  for f in /etc/supervisor/supervisord.conf /etc/supervisor/conf.d/*.conf /etc/supervisord.conf /etc/supervisord.d/*.ini; do
    [ -f "$f" ] || continue
    PROGRAM="$(awk -v path="$INSTALL_PATH" '
      /^\[program:/ { section=$0 }
      index($0,path)>0 && section ~ /^\[program:/ {
        name=section
        sub(/^\[program:/,"",name)
        sub(/\].*/,"",name)
        print name
        exit
      }' "$f")"
    if [ -n "$PROGRAM" ]; then
      printf 'SUPERVISOR|%s\n' "$PROGRAM"
      exit 0
    fi
  done
fi
if command -v pm2 >/dev/null 2>&1 && command -v node >/dev/null 2>&1; then
  pm2 jlist 2>/dev/null | node -e 'let s="";process.stdin.on("data",d=>s+=d);process.stdin.on("end",()=>{try{const p=JSON.parse(s);const x=p.find(v=>v.pm2_env&&v.pm2_env.pm_cwd===process.argv[1]);if(x)console.log("PM2|"+x.pm_id)}catch(e){}})' "$INSTALL_PATH"
fi
if command -v docker >/dev/null 2>&1; then
  for id in $(docker ps -aq 2>/dev/null); do
    if docker inspect "$id" 2>/dev/null | grep -Fq -- "$INSTALL_PATH"; then
      printf 'DOCKER|%s\n' "$id"
      exit 0
    fi
  done
fi
if command -v podman >/dev/null 2>&1; then
  for id in $(podman ps -aq 2>/dev/null); do
    if podman inspect "$id" 2>/dev/null | grep -Fq -- "$INSTALL_PATH"; then
      printf 'PODMAN|%s\n' "$id"
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
            "SUPERVISOR" => PrivilegedCommand($"supervisorctl {action} {identifier}"),
            "PM2" => $"pm2 {action} {identifier}",
            "DOCKER" => $"docker {action} {identifier}",
            "PODMAN" => $"podman {action} {identifier}",
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




    /// <summary>
    /// Confirms that no process is listening on NodeBB's configured port after stop.
    /// </summary>
    public async Task<bool> VerifyHttpStoppedAsync(
        NodeBbInstallation installation, CancellationToken cancellationToken = default)
    {
        var result = await _session.ExecuteAsync(BuildHttpStoppedCheckScript(installation.Path), cancellationToken);
        return result.ExitCode == 0 &&
            result.Output.Contains("NODEBB_STOPPED", StringComparison.Ordinal);
    }

    internal static string BuildHttpStoppedCheckScript(string installationPath)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
        return """
set -eu
INSTALL=__INSTALL__
[ -f "$INSTALL/config.json" ] && [ ! -L "$INSTALL/config.json" ] || { echo 'config.json חסר; לא ניתן לאמת שהשירות נעצר.' >&2; exit 1; }
node - "$INSTALL/config.json" <<'NODE'
const fs = require('fs');
const { spawnSync } = require('child_process');
let config;
try {
  config = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
} catch {
  console.error('לא ניתן לקרוא את config.json לצורך אימות עצירת NodeBB.');
  process.exit(1);
}
const port = Number(config.port || 4567);
if (!Number.isInteger(port) || port < 1 || port > 65535) {
  console.error('יציאת NodeBB אינה תקינה.');
  process.exit(1);
}
function hasListener() {
  let result = spawnSync('ss', ['-ltnH'], { encoding: 'utf8' });
  if (result.status === 0) {
    return result.stdout.split(/\\r?\\n/).some(line => {
      const fields = line.trim().split(/\\s+/);
      return fields[0] === 'LISTEN' && fields[3] && fields[3].endsWith(':' + port);
    });
  }
  result = spawnSync('netstat', ['-ltn'], { encoding: 'utf8' });
  if (result.status === 0) {
    return result.stdout.split(/\\r?\\n/).some(line =>
      /\\bLISTEN\\b/.test(line) && new RegExp(':' + port + '\\\\s*$').test(line.trim()));
  }
  console.error('לא נמצאו ss או netstat; לא ניתן לאמת בבטחה שהפורט של NodeBB פנוי.');
  process.exit(1);
}
let attempts = 0;
function check() {
  attempts++;
  if (!hasListener()) {
    console.log('NODEBB_STOPPED');
    process.exit(0);
    return;
  }
  if (attempts >= 10) {
    console.error('פורט NodeBB עדיין מאזין לאחר פקודת העצירה; ייתכן שהתהליך הופעל מחדש אוטומטית.');
    process.exit(1);
    return;
  }
  setTimeout(check, 1000);
}
check();
NODE
""".Replace("__INSTALL__", ShellQuote(installationPath), StringComparison.Ordinal);
    }

    /// <summary>
    /// Confirms that the restored NodeBB instance answers HTTP requests locally.
    /// </summary>
    public async Task<bool> VerifyHttpHealthAsync(
        NodeBbInstallation installation, CancellationToken cancellationToken = default)
    {
        var result = await _session.ExecuteAsync(BuildHttpHealthCheckScript(installation.Path), cancellationToken);
        return result.ExitCode == 0 &&
            result.Output.Contains("NODEBB_HEALTHY", StringComparison.Ordinal);
    }

    internal static string BuildHttpHealthCheckScript(string installationPath)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
        return """
set -eu
INSTALL=__INSTALL__
[ -f "$INSTALL/config.json" ] && [ ! -L "$INSTALL/config.json" ] || { echo 'קובץ config.json חסר לאחר ההפעלה.' >&2; exit 1; }
node - "$INSTALL/config.json" <<'NODE'
const fs = require('fs');
const http = require('http');
let config;
try {
  config = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
} catch {
  console.error('לא ניתן לקרוא את config.json לצורך בדיקת תקינות NodeBB.');
  process.exit(1);
}
const port = Number(config.port || 4567);
let host = typeof config.bind_address === 'string' && config.bind_address
  ? config.bind_address : '127.0.0.1';
if (host === '0.0.0.0') host = '127.0.0.1';
if (host === '::') host = '::1';
if (!Number.isInteger(port) || port < 1 || port > 65535) {
  console.error('יציאת NodeBB אינה תקינה.');
  process.exit(1);
}
let attempts = 0;
const maxAttempts = 30;
function retry() {
  if (attempts >= maxAttempts) {
    console.error('NodeBB לא התחיל להשיב לבקשות HTTP בתוך 60 שניות.');
    process.exit(1);
    return;
  }
  setTimeout(check, 2000);
}
function check() {
  attempts++;
  const request = http.get({ hostname: host, port, path: '/', timeout: 3000 }, response => {
    response.resume();
    if (response.statusCode >= 200 && response.statusCode < 400) {
      console.log('NODEBB_HEALTHY');
      process.exit(0);
      return;
    }
    retry();
  });
  request.on('timeout', () => request.destroy(new Error('timeout')));
  request.on('error', retry);
}
check();
NODE
""".Replace("__INSTALL__", ShellQuote(installationPath), StringComparison.Ordinal);
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
