namespace NodeBB.SSHManager.Services;

public sealed record NodeBbDatabaseInfo(
    string Type,
    string Host,
    string Port,
    string DatabaseName,
    bool UsernameConfigured,
    bool PasswordConfigured,
    bool UriConfigured);

public sealed class NodeBbDatabaseService
{
    private readonly SshServerSession _session;

    public NodeBbDatabaseService(SshServerSession session) => _session = session;

    public async Task<NodeBbDatabaseInfo> ReadConfigurationAsync(
        NodeBbInstallation installation, CancellationToken cancellationToken = default)
    {
        var path = ShellQuote(installation.Path);
        var script = $"""
cd {path} || exit 1
[ -r config.json ] || exit 2
command -v node >/dev/null 2>&1 || exit 127
DBTYPE="$(node -p "require('./config.json').database || 'unknown'" 2>/dev/null)"
case "$DBTYPE" in
  mongo) DEFAULT_PORT=27017 ;;
  redis) DEFAULT_PORT=6379 ;;
  postgres) DEFAULT_PORT=5432 ;;
  *) DEFAULT_PORT="" ;;
esac
DBHOST="$(node -p "require('./config.json')[require('./config.json').database]?.host || (require('./config.json')[require('./config.json').database]?.uri ? new URL(require('./config.json')[require('./config.json').database].uri).hostname : 'localhost')" 2>/dev/null)"
DBPORT="$(node -p "require('./config.json')[require('./config.json').database]?.port || (require('./config.json')[require('./config.json').database]?.uri ? new URL(require('./config.json')[require('./config.json').database].uri).port : '')" 2>/dev/null)"
DBNAME="$(node -p "require('./config.json')[require('./config.json').database]?.database || require('./config.json')[require('./config.json').database]?.db || ''" 2>/dev/null)"
DBUSER="$(node -p "!!(require('./config.json')[require('./config.json').database]?.username || require('./config.json')[require('./config.json').database]?.user)" 2>/dev/null)"
DBPASS="$(node -p "!!require('./config.json')[require('./config.json').database]?.password" 2>/dev/null)"
DBURI="$(node -p "!!require('./config.json')[require('./config.json').database]?.uri" 2>/dev/null)"
[ -n "$DBPORT" ] || DBPORT="$DEFAULT_PORT"
printf 'TYPE|%s\n' "$DBTYPE"
printf 'HOST|%s\n' "$DBHOST"
printf 'PORT|%s\n' "$DBPORT"
printf 'NAME|%s\n' "$DBNAME"
printf 'USERNAME|%s\n' "$DBUSER"
printf 'PASSWORD|%s\n' "$DBPASS"
printf 'URI|%s\n' "$DBURI"
""";
        DiagnosticLogService.Instance.Info("Database", "ConfigurationReadStarted",
            $"Reading database configuration for {installation.Path}.", _session.Profile.Name);
        var result = await _session.ExecuteAsync(script, cancellationToken);
        if (result.ExitCode != 0)
        {
            var error = new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                ? "לא ניתן לקרוא את תצורת מסד הנתונים."
                : result.Error);
            DiagnosticLogService.Instance.Error("Database", "ConfigurationReadFailed", error, _session.Profile.Name);
            throw error;
        }

        var values = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        string Get(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : "לא זמין";
        bool IsTrue(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase);

        var info = new NodeBbDatabaseInfo(Get("TYPE"), Get("HOST"), Get("PORT"), Get("NAME"),
            IsTrue("USERNAME"), IsTrue("PASSWORD"), IsTrue("URI"));
        DiagnosticLogService.Instance.Info("Database", "ConfigurationReadCompleted",
            $"Database configuration read; type {info.Type}. Credentials were not retrieved.", _session.Profile.Name);
        return info;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
