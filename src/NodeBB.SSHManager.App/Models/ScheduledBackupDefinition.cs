using System.Globalization;
using System.Text.Json.Serialization;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Models;

public enum ScheduledBackupFrequency { Daily, Weekly }

public sealed class ScheduledBackupDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public string ProfileName { get; set; } = "";
    public string InstallationPath { get; set; } = "";
    public string DestinationDirectory { get; set; } = "";
    public RemoteBackupKind BackupKind { get; set; }
    public int RetentionCount { get; set; }
    public bool IncludeDatabase { get; set; }
    public ScheduledBackupFrequency Frequency { get; set; } = ScheduledBackupFrequency.Daily;
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;
    public string LocalTime { get; set; } = "03:00";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string DisplayLabel
    {
        get
        {
            var cadence = Frequency == ScheduledBackupFrequency.Daily ? "כל יום" : "כל שבוע · " + DayOfWeek;
            var kind = BackupKind == RemoteBackupKind.FullInstallation ? "קובצי התקנה מלאים" : "הגדרות והעלאות";
            return cadence + " בשעה " + LocalTime + " · " + kind + (IncludeDatabase ? " · כולל מסד נתונים" : "");
        }
    }

    public void Validate()
    {
        if (Id == Guid.Empty || ProfileId == Guid.Empty)
            throw new InvalidDataException("מזהה תזמון או פרופיל שרת אינו תקין.");
        if (string.IsNullOrWhiteSpace(ProfileName) || ProfileName.Length > 120 || ProfileName.Any(char.IsControl))
            throw new InvalidDataException("שם פרופיל השרת בתזמון אינו תקין.");
        if (string.IsNullOrWhiteSpace(InstallationPath) || InstallationPath.Length > 2048 || InstallationPath.Any(char.IsControl))
            throw new InvalidDataException("נתיב התקנת NodeBB בתזמון אינו תקין.");
        if (string.IsNullOrWhiteSpace(DestinationDirectory) || !Path.IsPathFullyQualified(DestinationDirectory) || DestinationDirectory.Length > 2048)
            throw new InvalidDataException("יש לבחור תיקיית יעד מקומית תקינה לגיבויים המתוזמנים.");
        if (!Enum.IsDefined(BackupKind) || !Enum.IsDefined(Frequency) || !Enum.IsDefined(DayOfWeek))
            throw new InvalidDataException("הגדרות סוג הגיבוי או התזמון אינן תקינות.");
        if (RetentionCount is not (0 or 5 or 10 or 20))
            throw new InvalidDataException("מדיניות שמירת הגיבויים אינה תקינה.");
        if (!TimeOnly.TryParseExact(LocalTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidDataException("יש להזין שעה בפורמט 24 שעות, למשל 03:00.");
    }
}