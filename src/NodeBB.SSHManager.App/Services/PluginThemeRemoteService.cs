using System.Text.RegularExpressions;

namespace NodeBB.SSHManager.Services;

public sealed record NodeBbPackage(string Name, string Version, string Kind);

public enum PackageAction
{
    Install,
    Update,
    Remove,
    Activate,
    Deactivate
}

public sealed class PluginThemeRemoteService
{
    private static readonly Regex ValidPackageName = new(
        @"^(nodebb-plugin|nodebb-theme)-[a-z0-9][a-z0-9._-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SshServerSession _session;

    public PluginThemeRemoteService(SshServerSession session) => _session = session;

    public static bool IsValidPackageName(string? packageName) =>
        !string.IsNullOrWhiteSpace(packageName) && ValidPackageName.IsMatch(packageName);

    public async Task<IReadOnlyList<NodeBbPackage>> ListInstalledAsync(
        NodeBbInstallation installation, CancellationToken cancellationToken = default)
    {
        var path = ShellQuote(installation.Path);
        var script = $"""
cd {path} || exit 1
for d in node_modules/nodebb-plugin-* node_modules/nodebb-theme-*; do
  [ -d "$d" ] || continue
  name="${{d##*/}}"
  version=לא-ידועה
  if [ -r "$d/package.json" ]; then
    found="$(grep -m 1 '"version"' "$d/package.json" | sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')"
    [ -n "$found" ] && version="$found"
  fi
  printf '%s|%s\n' "$name" "$version"
done | sort
""";
        DiagnosticLogService.Instance.Info("Packages", "InventoryStarted", $"Package inventory started for {installation.Path}.", _session.Profile.Name);
        var result = await _session.ExecuteAsync(script, cancellationToken);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "לא ניתן לקרוא את רשימת התוספים." : result.Error);

        var packages = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 2))
            .Where(parts => parts.Length == 2 && IsValidPackageName(parts[0]))
            .Select(parts => new NodeBbPackage(parts[0], parts[1],
                parts[0].StartsWith("nodebb-theme-", StringComparison.Ordinal) ? "theme" : "plugin"))
            .OrderBy(item => item.Kind).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        DiagnosticLogService.Instance.Info("Packages", "InventoryCompleted", $"Found {packages.Length} plugin/theme package(s).", _session.Profile.Name);
        return packages;
    }

    public async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(
        NodeBbInstallation installation, string packageName, PackageAction action,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidPackageName(packageName))
            throw new ArgumentException("שם החבילה אינו תקין. יש להשתמש בשם nodebb-plugin-... או nodebb-theme-...", nameof(packageName));

        var isPlugin = packageName.StartsWith("nodebb-plugin-", StringComparison.Ordinal);
        if (!isPlugin && (action is PackageAction.Activate or PackageAction.Deactivate))
            throw new InvalidOperationException("הפעלה והשבתה של ערכות עיצוב תתבצע דרך ממשק הניהול של NodeBB.");

        var path = ShellQuote(installation.Path);
        var package = ShellQuote(packageName);
        var packageLatest = ShellQuote(packageName + "@latest");
        var command = action switch
        {
            PackageAction.Activate => $"cd {path} && ./nodebb activate {package}",
            PackageAction.Deactivate => $"cd {path} && ./nodebb reset -p {package}",
            PackageAction.Install => BuildPackageManagerCommand(path, package, PackageAction.Install),
            PackageAction.Update => BuildPackageManagerCommand(path, packageLatest, PackageAction.Update),
            PackageAction.Remove => BuildPackageManagerCommand(path, package, PackageAction.Remove),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        DiagnosticLogService.Instance.Info("Packages", "PackageActionStarted",
            $"Package action {action} requested for {packageName} in {installation.Path}.", _session.Profile.Name);
        var result = await _session.ExecuteAsync(command, cancellationToken);
        if (result.ExitCode == 0)
            DiagnosticLogService.Instance.Info("Packages", "PackageActionCompleted",
                $"Package action {action} completed for {packageName}.", _session.Profile.Name);
        else
            DiagnosticLogService.Instance.Error("Packages", "PackageActionFailed",
                $"Package action {action} failed for {packageName}; exit code {result.ExitCode}: {result.Error}", _session.Profile.Name);
        return result;
    }

    private static string BuildPackageManagerCommand(string path, string package, PackageAction action)
    {
        var operation = action switch
        {
            PackageAction.Install => "add",
            PackageAction.Update => "add",
            PackageAction.Remove => "remove",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return $"""
cd {path} || exit 1
if [ -f pnpm-lock.yaml ] && command -v pnpm >/dev/null 2>&1; then PM=pnpm
elif [ -f yarn.lock ] && command -v yarn >/dev/null 2>&1; then PM=yarn
elif command -v npm >/dev/null 2>&1; then PM=npm
elif command -v yarn >/dev/null 2>&1; then PM=yarn
elif command -v pnpm >/dev/null 2>&1; then PM=pnpm
else echo 'לא נמצא מנהל חבילות נתמך (npm/yarn/pnpm).' >&2; exit 127
fi
case "$PM" in
  npm)
    if [ "{operation}" = "remove" ]; then npm uninstall {package}; else npm install --save {package}; fi
    ;;
  yarn)
    if [ "{operation}" = "remove" ]; then yarn remove {package}; else yarn add {package}; fi
    ;;
  pnpm)
    if [ "{operation}" = "remove" ]; then pnpm remove {package}; else pnpm add {package}; fi
    ;;
esac
""";
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
