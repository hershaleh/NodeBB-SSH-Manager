using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Services;

public sealed class ScheduledBackupStore
{
    private static readonly byte[] Entropy = "NodeBB.SSHManager.ScheduledBackups.v1"u8.ToArray();
    private const long MaximumScheduleFileBytes = 64 * 1024;
    private readonly string _directory;

    public ScheduledBackupStore(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NodeBB SSH Manager", "ScheduledBackups"));
    }

    public IReadOnlyList<ScheduledBackupDefinition> LoadAll()
    {
        if (!Directory.Exists(_directory)) return [];
        var schedules = new List<ScheduledBackupDefinition>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.schedule", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > MaximumScheduleFileBytes)
                    throw new InvalidDataException("קובץ התזמון גדול מהמותר או ריק.");
                var protectedBytes = File.ReadAllBytes(path);
                byte[]? clear = null;
                try
                {
                    clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                    var schedule = JsonSerializer.Deserialize<ScheduledBackupDefinition>(clear)
                        ?? throw new InvalidDataException("קובץ תזמון ריק.");
                    schedule.Validate();
                    schedules.Add(schedule);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                    if (clear is not null) CryptographicOperations.ZeroMemory(clear);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException)
            {
                DiagnosticLogService.Instance.Warning("ScheduledBackup", "ScheduleFileSkipped",
                    "A scheduled-backup definition could not be read and was skipped.", ex);
            }
        }
        return schedules.OrderBy(x => x.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.LocalTime, StringComparer.Ordinal).ToArray();
    }

    public ScheduledBackupDefinition Load(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("מזהה תזמון אינו תקין.", nameof(id));
        var path = GetPath(id);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumScheduleFileBytes)
            throw new InvalidDataException("קובץ התזמון גדול מהמותר או ריק.");
        var protectedBytes = File.ReadAllBytes(path);
        byte[]? clear = null;
        try
        {
            clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var schedule = JsonSerializer.Deserialize<ScheduledBackupDefinition>(clear)
                ?? throw new InvalidDataException("קובץ התזמון ריק.");
            schedule.Validate();
            if (schedule.Id != id) throw new InvalidDataException("מזהה התזמון בקובץ אינו תואם לשם הקובץ.");
            return schedule;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    public void Save(ScheduledBackupDefinition schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        schedule.Validate();
        Directory.CreateDirectory(_directory);
        var clear = JsonSerializer.SerializeToUtf8Bytes(schedule);
        if (clear.Length > MaximumScheduleFileBytes)
        {
            CryptographicOperations.ZeroMemory(clear);
            throw new InvalidDataException("קובץ התזמון גדול מהמותר.");
        }
        byte[]? protectedBytes = null;
        var destination = GetPath(schedule.Id);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(protectedBytes);
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public void Delete(Guid id)
    {
        var path = GetPath(id);
        if (File.Exists(path)) File.Delete(path);
    }

    private string GetPath(Guid id) => Path.Combine(_directory, id.ToString("N") + ".schedule");
}

public sealed class ScheduledBackupService
{
    private readonly ScheduledBackupStore _store;
    private readonly CredentialVaultService _vault;
    private readonly ServerProfileStore _profiles;

    public ScheduledBackupService(ScheduledBackupStore? store = null, CredentialVaultService? vault = null, ServerProfileStore? profiles = null)
    {
        _store = store ?? new ScheduledBackupStore();
        _vault = vault ?? new CredentialVaultService();
        _profiles = profiles ?? new ServerProfileStore();
    }

    public IReadOnlyList<ScheduledBackupDefinition> GetSchedules(Guid profileId) =>
        _store.LoadAll().Where(x => x.ProfileId == profileId).ToArray();

    public IReadOnlyList<ScheduledBackupDefinition> GetSchedules(Guid profileId, string installationPath) =>
        _store.LoadAll().Where(x => x.ProfileId == profileId &&
            string.Equals(x.InstallationPath, installationPath, StringComparison.Ordinal)).ToArray();

    public void Create(ScheduledBackupDefinition schedule, string encryptionPassword, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        schedule.Validate();
        if (string.IsNullOrWhiteSpace(encryptionPassword) || encryptionPassword.Length < 12)
            throw new ArgumentException("סיסמת גיבוי מתוזמן חייבת להכיל לפחות 12 תווים.", nameof(encryptionPassword));
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new FileNotFoundException("לא ניתן לזהות את קובץ ההפעלה של מנהל NodeBB SSH.", executablePath);

        var profile = _profiles.Load().SingleOrDefault(x => x.Id == schedule.ProfileId)
            ?? throw new InvalidOperationException("פרופיל השרת של התזמון אינו קיים עוד.");
        var duplicate = _store.LoadAll().Any(existing =>
            existing.ProfileId == schedule.ProfileId &&
            string.Equals(existing.InstallationPath, schedule.InstallationPath, StringComparison.Ordinal) &&
            string.Equals(existing.DestinationDirectory, schedule.DestinationDirectory, StringComparison.OrdinalIgnoreCase) &&
            existing.BackupKind == schedule.BackupKind &&
            existing.Frequency == schedule.Frequency &&
            string.Equals(existing.LocalTime, schedule.LocalTime, StringComparison.Ordinal) &&
            (schedule.Frequency != ScheduledBackupFrequency.Weekly || existing.DayOfWeek == schedule.DayOfWeek));
        if (duplicate)
            throw new InvalidOperationException("כבר קיים תזמון זהה לאותה התקנה, סוג גיבוי ושעה. אפשר להסיר אותו או לבחור שעה אחרת.");

        if (string.IsNullOrWhiteSpace(profile.KnownHostFingerprint))
            throw new InvalidOperationException("לא ניתן לתזמן גיבוי לפני אימות ושמירת טביעת האצבע של שרת SSH.");
        if (_vault.Get(profile.Id) is null)
            throw new InvalidOperationException("כדי שגיבוי יפעל ללא חלון התחברות, יש להתחבר לשרת ולבחור בשמירת פרטי ההתחברות בכספת Windows. גם מפתח SSH ללא סיסמה ניתן לשמור כפרט התחברות ריק.");

        Directory.CreateDirectory(schedule.DestinationDirectory);
        _vault.Set(schedule.Id, encryptionPassword);
        try
        {
            _store.Save(schedule);
            WindowsScheduledTaskService.Register(schedule, executablePath);
        }
        catch
        {
            try { WindowsScheduledTaskService.Remove(schedule.Id); } catch { }
            try { _store.Delete(schedule.Id); } catch { }
            try { _vault.Remove(schedule.Id); } catch { }
            throw;
        }

        DiagnosticLogService.Instance.Info("ScheduledBackup", "ScheduleCreated",
            "An encrypted scheduled backup task was registered.", schedule.ProfileName);
    }

    public (int Removed, int Failed) RemoveForProfile(Guid profileId)
    {
        var ids = _store.LoadAll().Where(x => x.ProfileId == profileId).Select(x => x.Id).ToArray();
        var removed = 0;
        var failed = 0;
        foreach (var id in ids)
        {
            try { Remove(id); removed++; }
            catch (Exception ex)
            {
                failed++;
                DiagnosticLogService.Instance.Error("ScheduledBackup", "ProfileScheduleCleanupFailed", ex, profileId.ToString());
            }
        }
        return (removed, failed);
    }

    public void Remove(Guid scheduleId)
    {
        var schedule = _store.Load(scheduleId);
        WindowsScheduledTaskService.Remove(scheduleId);
        _store.Delete(scheduleId);
        _vault.Remove(scheduleId);
        DiagnosticLogService.Instance.Info("ScheduledBackup", "ScheduleRemoved",
            "A scheduled backup task and its protected settings were removed.", schedule.ProfileName);
    }
}

internal static class WindowsScheduledTaskService
{
    private static string GetTaskName(Guid id) => "NodeBB SSH Manager\\ScheduledBackup-" + id.ToString("N");

    public static void Register(ScheduledBackupDefinition schedule, string executablePath)
    {
        var action = "\"" + Path.GetFullPath(executablePath) + "\" --scheduled-backup " + schedule.Id.ToString("N");
        var args = new List<string>
        {
            "/Create", "/SC", schedule.Frequency == ScheduledBackupFrequency.Daily ? "DAILY" : "WEEKLY",
            "/ST", schedule.LocalTime, "/TN", GetTaskName(schedule.Id), "/TR", action, "/F", "/IT"
        };
        if (schedule.Frequency == ScheduledBackupFrequency.Weekly)
        {
            var day = schedule.DayOfWeek switch
            {
                DayOfWeek.Sunday => "SUN",
                DayOfWeek.Monday => "MON",
                DayOfWeek.Tuesday => "TUE",
                DayOfWeek.Wednesday => "WED",
                DayOfWeek.Thursday => "THU",
                DayOfWeek.Friday => "FRI",
                DayOfWeek.Saturday => "SAT",
                _ => throw new ArgumentOutOfRangeException()
            };
            args.Add("/D");
            args.Add(day);
        }
        Run(args);
    }

    public static void Remove(Guid id)
    {
        var taskName = GetTaskName(id);
        var query = Run(["/Query", "/TN", taskName], false);
        if (query.ExitCode != 0)
        {
            var message = (query.Error + " " + query.Output).Trim();
            if (message.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("could not be found", StringComparison.OrdinalIgnoreCase))
                return;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? "לא ניתן לבדוק את משימת הגיבוי המתוזמן ב-Windows."
                : message);
        }
        Run(["/Delete", "/TN", taskName, "/F"]);
    }

    private static (int ExitCode, string Output, string Error) Run(IEnumerable<string> arguments, bool throwOnError = true)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("לא ניתן להפעיל את מתזמן המשימות של Windows.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("מתזמן המשימות של Windows לא הגיב בתוך 30 שניות.");
        }
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (throwOnError && process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        return (process.ExitCode, output, error);
    }
}

public static class ScheduledBackupRunner
{
    public static async Task RunAsync(Guid scheduleId)
    {
        var scheduledDefinition = new ScheduledBackupStore().Load(scheduleId);
        // Serialize all scheduled backups for the same server profile. Different NodeBB
        // installations may share one database service, so per-schedule locks are insufficient.
        using var gate = new Semaphore(1, 1, "Local\\NodeBB-SSHManager-ProfileBackup-" + scheduledDefinition.ProfileId.ToString("N"));
        var acquired = gate.WaitOne(0);
        if (!acquired)
            throw new InvalidOperationException("גיבוי מתוזמן זה כבר פועל. לא הופעלה ריצה מקבילה כדי למנוע התנגשות בגיבויים ובמדיניות השמירה.");

        try
        {
            RecordRunStatus(scheduleId, ScheduledBackupRunStatus.Running, "הגיבוי פועל כעת.");
            await RunCoreAsync(scheduleId);
            RecordRunStatus(scheduleId, ScheduledBackupRunStatus.Succeeded, "הגיבוי הושלם בהצלחה.");
        }
        catch
        {
            RecordRunStatus(scheduleId, ScheduledBackupRunStatus.Failed, "הגיבוי נכשל. יש לעיין ביומן האירועים של התוכנה.");
            throw;
        }
        finally { gate.Release(); }
    }

    private static void RecordRunStatus(Guid scheduleId, ScheduledBackupRunStatus status, string summary)
    {
        try
        {
            var store = new ScheduledBackupStore();
            var schedule = store.Load(scheduleId);
            schedule.LastRunUtc = DateTime.UtcNow;
            schedule.LastRunStatus = status;
            schedule.LastRunSummary = summary;
            store.Save(schedule);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Warning("ScheduledBackup", "RunStatusUpdateFailed",
                "Could not persist scheduled-backup run status.", ex);
        }
    }

    private static async Task RunCoreAsync(Guid scheduleId)
    {
        var schedule = new ScheduledBackupStore().Load(scheduleId);
        var profile = new ServerProfileStore().Load().SingleOrDefault(x => x.Id == schedule.ProfileId)
            ?? throw new InvalidOperationException("פרופיל השרת של הגיבוי המתוזמן אינו קיים.");
        if (string.IsNullOrWhiteSpace(profile.KnownHostFingerprint))
            throw new InvalidOperationException("גיבוי מתוזמן נעצר: טביעת האצבע של שרת SSH אינה מוגדרת.");

        var vault = new CredentialVaultService();
        var sshSecret = vault.Get(profile.Id)
            ?? throw new InvalidOperationException("גיבוי מתוזמן נעצר: פרטי ההתחברות לשרת אינם שמורים בכספת Windows.");
        var encryptionPassword = vault.Get(schedule.Id)
            ?? throw new InvalidOperationException("סיסמת ההצפנה של הגיבוי המתוזמן אינה זמינה בכספת Windows.");

        var prefix = LocalBackupRetentionService.BuildFilePrefix(profile.Name, profile.Id, schedule.BackupKind);
        var destination = Path.Combine(schedule.DestinationDirectory,
            prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".nbbbackup");
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        Directory.CreateDirectory(tempRoot);
        var plaintext = Path.Combine(tempRoot, "scheduled-" + Guid.NewGuid().ToString("N") + ".tar.gz");

        using var sessions = new SshSessionManager();
        try
        {
            var session = await sessions.ConnectAsync(profile, sshSecret);
            var installation = new NodeBbInstallation(schedule.InstallationPath, "scheduled");
            await new RemoteBackupService(session).DownloadBackupAsync(installation, schedule.BackupKind, plaintext);
            await BackupEncryptionService.EncryptFileAsync(plaintext, destination, encryptionPassword);
            var removed = schedule.RetentionCount > 0
                ? LocalBackupRetentionService.ApplyRetention(schedule.DestinationDirectory, prefix, schedule.RetentionCount)
                : 0;
            DiagnosticLogService.Instance.Info("ScheduledBackup", "InstallationBackupCompleted",
                "Encrypted NodeBB file backup completed; older retained files removed: " + removed, profile.Name);

            if (schedule.IncludeDatabase)
            {
                var databaseInfo = await new NodeBbDatabaseService(session).ReadConfigurationAsync(installation);
                var databaseType = databaseInfo.Type.Trim().ToLowerInvariant();
                var databasePrefix = LocalBackupRetentionService.BuildDatabaseFilePrefix(profile.Name, profile.Id, databaseType);
                var databasePlain = Path.Combine(tempRoot, "scheduled-db-" + Guid.NewGuid().ToString("N") + ".dump");
                var databaseDestination = Path.Combine(schedule.DestinationDirectory,
                    databasePrefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".nbbbackup");
                try
                {
                    switch (databaseType)
                    {
                        case "mongo":
                            await new MongoDatabaseBackupService(session).DownloadBackupAsync(installation, databasePlain);
                            break;
                        case "postgres":
                            await new PostgresDatabaseBackupService(session).DownloadBackupAsync(installation, databasePlain);
                            break;
                        case "redis":
                            await new RedisDatabaseBackupService(session).DownloadBackupAsync(installation, databasePlain);
                            break;
                        default:
                            throw new InvalidOperationException("סוג מסד הנתונים שהוגדר ב-NodeBB אינו נתמך לגיבוי מתוזמן: " + databaseInfo.Type);
                    }

                    await BackupEncryptionService.EncryptFileAsync(databasePlain, databaseDestination, encryptionPassword);
                    var databaseRemoved = schedule.RetentionCount > 0
                        ? LocalBackupRetentionService.ApplyRetention(schedule.DestinationDirectory, databasePrefix, schedule.RetentionCount)
                        : 0;
                    DiagnosticLogService.Instance.Info("ScheduledBackup", "DatabaseBackupCompleted",
                        "Encrypted " + databaseType + " database backup completed; older retained files removed: " + databaseRemoved,
                        profile.Name);
                }
                finally
                {
                    try { if (File.Exists(databasePlain)) File.Delete(databasePlain); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("ScheduledBackup", "ScheduledBackupFailed", ex, profile.Name);
            throw;
        }
        finally
        {
            try { if (File.Exists(plaintext)) File.Delete(plaintext); } catch { }
        }
    }
}