using System.Text.Json;
using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Services;

public sealed class ServerProfileTransferService
{
    private const int MaximumProfiles = 500;
    private const int MaximumJsonBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Export(IEnumerable<ServerProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var snapshot = profiles.ToList();
        Validate(snapshot);
        // Host fingerprints are deliberately excluded: importing a file must never silently
        // establish trust in an SSH host key supplied by that same file.
        var portable = snapshot.Select(p => new ServerProfile
        {
            Name = p.Name, Group = p.Group, Host = p.Host, Port = p.Port, Username = p.Username,
            AuthenticationType = p.AuthenticationType, PrivateKeyPath = p.PrivateKeyPath
        }).ToList();
        var json = JsonSerializer.Serialize(portable, Options);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidDataException("קובץ הפרופילים שנוצר גדול מהמותר (2MB).");
        return json;
    }

    public IReadOnlyList<ServerProfile> Import(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidDataException("קובץ פרופילים גדול מהמותר (2MB).");
        List<ServerProfile>? imported;
        try { imported = JsonSerializer.Deserialize<List<ServerProfile>>(json); }
        catch (JsonException ex) { throw new InvalidDataException("קובץ הפרופילים אינו JSON תקין.", ex); }
        if (imported is null) throw new InvalidDataException("קובץ הפרופילים ריק או אינו מכיל רשימה.");
        Validate(imported);
        // IDs and trusted host keys are local security state, not portable configuration.
        foreach (var profile in imported)
        {
            profile.Id = Guid.NewGuid();
            profile.KnownHostFingerprint = null;
        }
        return imported;
    }

    private static void Validate(IReadOnlyCollection<ServerProfile> profiles)
    {
        if (profiles.Count > MaximumProfiles)
            throw new InvalidDataException($"ניתן להעביר עד {MaximumProfiles} פרופילים בקובץ.");
        var ids = new HashSet<Guid>();
        foreach (var p in profiles)
        {
            if (p is null || p.Id == Guid.Empty || !ids.Add(p.Id))
                throw new InvalidDataException("קובץ הפרופילים מכיל רשומה ריקה או מזהה כפול.");
            if (p.Group is null || p.Group.Length > 80 || p.Group.Any(char.IsControl))
                throw new InvalidDataException("אחד מפרופילי השרת מכיל שם קבוצה לא תקין.");
            if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 120 || p.Name.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(p.Host) || p.Host.Length > 253
                || p.Host.Any(char.IsControl) || p.Host.Any(char.IsWhiteSpace)
                || p.Host.IndexOfAny([';', '|', '/', '\\', '@', '?', '#']) >= 0
                || string.IsNullOrWhiteSpace(p.Username) || p.Username.Length > 128
                || p.Username.Any(char.IsControl))
                throw new InvalidDataException("אחד מפרופילי השרת מכיל שם, כתובת או משתמש לא תקינים.");
            if (p.Port is < 1 or > 65535 || !Enum.IsDefined(p.AuthenticationType))
                throw new InvalidDataException("אחד מפרופילי השרת מכיל פורט או שיטת אימות לא תקינים.");
            if (p.AuthenticationType == SshAuthenticationType.PrivateKey
                && (string.IsNullOrWhiteSpace(p.PrivateKeyPath) || p.PrivateKeyPath.Length > 2048))
                throw new InvalidDataException("בפרופיל המבוסס על מפתח SSH חסר נתיב מפתח תקין.");
        }
    }
}
