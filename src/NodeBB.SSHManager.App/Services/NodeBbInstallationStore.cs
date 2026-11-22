using System.Text;
using System.Text.Json;

namespace NodeBB.SSHManager.Services;

/// <summary>
/// Persists manually supplied NodeBB installation paths separately for each SSH profile.
/// Paths are revalidated over SFTP before being shown as available installations.
/// </summary>
public sealed class NodeBbInstallationStore
{
    private const int MaximumInstallationsPerProfile = 100;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public NodeBbInstallationStore(Guid profileId, string? directory = null)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("מזהה פרופיל השרת אינו תקין.", nameof(profileId));

        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NodeBB SSH Manager", "Installations");
        _filePath = Path.Combine(Path.GetFullPath(root), profileId.ToString("N") + ".json");
    }

    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(_filePath)) return [];

        List<string>? paths;
        try
        {
            paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_filePath), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("רשימת נתיבי התקנות NodeBB השמורה פגומה.", ex);
        }

        if (paths is null || paths.Count > MaximumInstallationsPerProfile)
            throw new InvalidDataException("רשימת נתיבי התקנות NodeBB השמורה אינה תקינה.");

        var normalized = new List<string>();
        foreach (var path in paths)
        {
            var value = NormalizeAndValidate(path);
            if (!normalized.Contains(value, StringComparer.Ordinal))
                normalized.Add(value);
        }
        return normalized;
    }

    public void Add(string installationPath)
    {
        var normalized = NormalizeAndValidate(installationPath);
        var paths = Load().ToList();
        if (paths.Contains(normalized, StringComparer.Ordinal)) return;
        if (paths.Count >= MaximumInstallationsPerProfile)
            throw new InvalidOperationException($"ניתן לשמור עד {MaximumInstallationsPerProfile} נתיבי התקנה לכל שרת.");

        paths.Add(normalized);
        Save(paths);
    }

    private void Save(IReadOnlyCollection<string> paths)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, paths, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
                File.Replace(temporaryPath, _filePath, null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, _filePath);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static string NormalizeAndValidate(string? path)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(path);
        if (path!.Length > 4096)
            throw new ArgumentException("נתיב ההתקנה ארוך מהמותר.", nameof(path));
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join("/", segments);
    }
}
