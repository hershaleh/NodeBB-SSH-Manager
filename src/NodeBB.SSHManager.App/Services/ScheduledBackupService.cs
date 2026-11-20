using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Services;

public sealed class ScheduledBackupStore
{
    private static readonly byte[] Entropy = "NodeBB.SSHManager.ScheduledBackups.v1"u8.ToArray();
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
                var protectedBytes = File.ReadAllBytes(path);
                var clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    var schedule = JsonSerializer.Deserialize<ScheduledBackupDefinition>(clear)
                        ?? throw new InvalidDataException("קובץ תזמון ריק.");
                    schedule.Validate();
                    schedules.Add(schedule);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                    CryptographicOperations.ZeroMemory(clear);
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
        var protectedBytes = File.ReadAllBytes(GetPath(id));
        var clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var schedule = JsonSerializer.Deserialize<ScheduledBackupDefinition>(clear)
                ?? throw new InvalidDataException("קובץ התזמון ריק.");
            schedule.Validate();
            if (schedule.Id != id) throw new InvalidDataException("מזהה התזמון בקובץ אינו תואם לשם הקובץ.");
            return schedule;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public void Save(ScheduledBackupDefinition schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        schedule.Validate();
        Directory.CreateDirectory(_directory);
        var clear = JsonSerializer.SerializeToUtf8Bytes(schedule);
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
        if (Run(["/Query", "/TN", taskName], false).ExitCode != 0) return;
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
            DiagnosticLogService.Instance.Info("ScheduledBackup", "ScheduledBackupCompleted",
                "An encrypted scheduled backup completed; older retained files removed: " + removed, profile.Name);
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