namespace NodeBB.SSHManager.Services;

public sealed record RemoteServerInventory(
    string HostName,
    string OperatingSystem,
    string Kernel,
    string Architecture,
    string InitSystem,
    string PackageManager,
    string RemoteUser,
    bool IsRoot,
    string NodeVersion,
    string NpmVersion,
    string GitVersion,
    string MongoVersion,
    string RedisVersion,
    string DockerVersion,
    string PodmanVersion,
    string Pm2Version,
    bool NodeBbCliAvailable,
    DateTimeOffset DetectedAt);

public sealed class RemoteCapabilityService
{
    private readonly SshServerSession _session;

    public RemoteCapabilityService(SshServerSession session) => _session = session;

    public async Task<RemoteServerInventory> DetectAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
set +e
emit() { printf '%s=%s\n' "$1" "$2"; }
version_of() {
  if command -v "$1" >/dev/null 2>&1; then
    "$1" $2 2>/dev/null | head -n 1 | tr -d '\r'
  else
    printf 'לא מותקן'
  fi
}
OS_NAME="לא ידועה"
if [ -r /etc/os-release ]; then
  . /etc/os-release
  OS_NAME="${PRETTY_NAME:-${NAME:-Linux}}"
fi
KERNEL="$(uname -r 2>/dev/null)"
ARCH="$(uname -m 2>/dev/null)"
HOST="$(hostname 2>/dev/null)"
USER_NAME="$(id -un 2>/dev/null)"
ROOT_FLAG=לא
[ "$(id -u 2>/dev/null)" = "0" ] && ROOT_FLAG=כן
PID1="$(ps -p 1 -o comm= 2>/dev/null | tr -d ' ')"
INIT="לא ידוע"
case "$PID1" in
  systemd) INIT=systemd ;;
  init) if command -v rc-service >/dev/null 2>&1; then INIT=OpenRC; else INIT=SysV-init; fi ;;
  openrc) INIT=OpenRC ;;
  runit) INIT=runit ;;
  s6-svscan) INIT=s6 ;;
  *) 
    if command -v systemctl >/dev/null 2>&1 && systemctl is-system-running >/dev/null 2>&1; then INIT=systemd
    elif command -v rc-service >/dev/null 2>&1; then INIT=OpenRC
    elif command -v service >/dev/null 2>&1; then INIT=SysV/service
    elif command -v supervisorctl >/dev/null 2>&1; then INIT=Supervisor
    else INIT=לא-זוהה
    fi ;;
esac
PKG=לא-זוהה
for p in apt-get dnf yum zypper pacman apk emerge; do
  if command -v "$p" >/dev/null 2>&1; then PKG="$p"; break; fi
done
NODE="$(version_of node --version)"
NPM="$(version_of npm --version)"
GIT="$(git --version 2>/dev/null | head -n 1 | sed 's/^git version //')"
[ -n "$GIT" ] || GIT=לא-מותקן
MONGO="$(mongod --version 2>/dev/null | head -n 1)"
[ -n "$MONGO" ] || MONGO=לא-מותקן
REDIS="$(redis-server --version 2>/dev/null | head -n 1)"
[ -n "$REDIS" ] || REDIS=לא-מותקן
DOCKER="$(docker --version 2>/dev/null | head -n 1)"
[ -n "$DOCKER" ] || DOCKER=לא-מותקן
PODMAN="$(podman --version 2>/dev/null | head -n 1)"
[ -n "$PODMAN" ] || PODMAN=לא-מותקן
PM2="$(pm2 --version 2>/dev/null | head -n 1)"
[ -n "$PM2" ] || PM2=לא-מותקן
NODEBB=לא
command -v nodebb >/dev/null 2>&1 && NODEBB=כן
emit hostname "$HOST"
emit os "$OS_NAME"
emit kernel "$KERNEL"
emit architecture "$ARCH"
emit init "$INIT"
emit package_manager "$PKG"
emit user "$USER_NAME"
emit is_root "$ROOT_FLAG"
emit node "$NODE"
emit npm "$NPM"
emit git "$GIT"
emit mongo "$MONGO"
emit redis "$REDIS"
emit docker "$DOCKER"
emit podman "$PODMAN"
emit pm2 "$PM2"
emit nodebb_cli "$NODEBB"
""";

        DiagnosticLogService.Instance.Info("Inventory", "DetectionStarted", "Remote server capability detection started.", _session.Profile.Name);
        var result = await _session.ExecuteAsync(script, cancellationToken);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
        {
            var error = new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                ? "לא ניתן לאסוף מידע על השרת."
                : result.Error);
            DiagnosticLogService.Instance.Error("Inventory", "DetectionFailed", error, _session.Profile.Name);
            throw error;
        }

        var values = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        string Get(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : "לא זמין";
        var inventory = new RemoteServerInventory(
            Get("hostname"), Get("os"), Get("kernel"), Get("architecture"), Get("init"),
            Get("package_manager"), Get("user"), Get("is_root") == "כן",
            Get("node"), Get("npm"), Get("git"), Get("mongo"), Get("redis"),
            Get("docker"), Get("podman"), Get("pm2"), Get("nodebb_cli") == "כן",
            DateTimeOffset.UtcNow);

        DiagnosticLogService.Instance.Info("Inventory", "DetectionCompleted", "Remote server capability detection completed.", _session.Profile.Name);
        return inventory;
    }
}
