using System.Text.Json;
using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Services;

public sealed class ServerProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly string _filePath;

    public ServerProfileStore(string? filePath = null)
    {
        _filePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NodeBB SSH Manager",
            "servers.json"));
        _directory = Path.GetDirectoryName(_filePath)
            ?? throw new ArgumentException("The profile file must have a parent directory.", nameof(filePath));
    }

    public IReadOnlyList<ServerProfile> Load()
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            var json = File.ReadAllText(_filePath);
            var profiles = JsonSerializer.Deserialize<List<ServerProfile>>(json, JsonOptions) ?? [];
            ValidateProfiles(profiles);
            return profiles;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            var preservedPath = PreserveCorruptFile();
            var preservationMessage = preservedPath is null
                ? " לא ניתן היה ליצור עותק גיבוי אוטומטי."
                : $" עותק הקובץ הפגום נשמר כאן: {preservedPath}";
            throw new InvalidDataException(
                $"קובץ הגדרות השרתים פגום או אינו תקין: {_filePath}.{preservationMessage}", ex);
        }
    }

    public void Save(IEnumerable<ServerProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var snapshot = profiles.ToList();
        ValidateProfiles(snapshot);

        Directory.CreateDirectory(_directory);
        var temporaryPath = Path.Combine(_directory, $".servers-{Guid.NewGuid():N}.tmp");

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough
            };

            using (var stream = new FileStream(temporaryPath, options))
            {
                JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
                File.Replace(temporaryPath, _filePath, null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, _filePath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // A stale uniquely named temporary file is safer than masking the original save error.
            }
        }
    }

    private string? PreserveCorruptFile()
    {
        try
        {
            var backupPath = _filePath + $".corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak";
            File.Copy(_filePath, backupPath, overwrite: false);
            return backupPath;
        }
        catch
        {
            return null;
        }
    }

    private static void ValidateProfiles(IReadOnlyCollection<ServerProfile> profiles)
    {
        var ids = new HashSet<Guid>();
        foreach (var profile in profiles)
        {
            if (profile is null)
                throw new InvalidDataException("רשימת השרתים מכילה רשומה ריקה.");
            if (profile.Id == Guid.Empty || !ids.Add(profile.Id))
                throw new InvalidDataException("לכל שרת חייב להיות מזהה ייחודי ותקין.");
            if (string.IsNullOrWhiteSpace(profile.Name)
                || string.IsNullOrWhiteSpace(profile.Host)
                || string.IsNullOrWhiteSpace(profile.Username))
                throw new InvalidDataException("שם השרת, כתובת השרת ושם המשתמש הם שדות חובה.");
            if (profile.Host.Any(char.IsWhiteSpace) || profile.Host.Contains(';') || profile.Host.Contains('|'))
                throw new InvalidDataException("כתובת השרת מכילה תווים שאינם מותרים.");
            if (profile.Port is < 1 or > 65535)
                throw new InvalidDataException("יציאת SSH חייבת להיות בין 1 ל־65535.");
            if (!Enum.IsDefined(profile.AuthenticationType))
                throw new InvalidDataException("סוג האימות בפרופיל אינו מוכר.");
            if (profile.AuthenticationType == SshAuthenticationType.PrivateKey
                && string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
                throw new InvalidDataException("בפרופיל המבוסס על מפתח SSH חסר נתיב למפתח הפרטי.");
        }
    }
}
