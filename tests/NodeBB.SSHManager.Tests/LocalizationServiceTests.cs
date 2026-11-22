using System;
using System.IO;
using System.Windows;
using NodeBB.SSHManager.Localization;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class LocalizationServiceTests
{
    [Fact]
    public void DefaultsToHebrewAndRightToLeft()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json");
        var service = new LocalizationService(path);
        Assert.Equal("he", service.Language);
        Assert.Equal(FlowDirection.RightToLeft, service.FlowDirection);
        Assert.Equal("לוח בקרה", service["Dashboard_לוח_בקרה"]);
    }

    [Fact]
    public void LanguageSelectionChangesTranslationsAndPersists()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "language.json");
        try
        {
            var service = new LocalizationService(path);
            service.SetLanguage("en");
            Assert.Equal("en", service.Language);
            Assert.Equal("Confirm password", service.Translate("אימות סיסמה"));
            Assert.Equal("Encryption password (at least 12 characters)", service.Translate("סיסמת הצפנה (לפחות 12 תווים)"));
            Assert.Equal(FlowDirection.LeftToRight, service.FlowDirection);
            Assert.Equal("Dashboard", service["Dashboard_לוח_בקרה"]);
            Assert.Equal("Backup center", service["View_240265a6"]);
            Assert.Contains("\"language\":\"en\"", File.ReadAllText(path));

            var reloaded = new LocalizationService(path);
            Assert.Equal("en", reloaded.Language);
            Assert.Equal(FlowDirection.LeftToRight, reloaded.FlowDirection);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }


    [Fact]
    public void TranslateFormatTranslatesTemplatesAndFormatsArguments()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        service.SetLanguage("en");
        var count = 5;
        var action = "Start";
        var path = "/srv/nodebb";

        Assert.Equal("5 installations were found. The first installation was selected.",
            service.TranslateFormat($"נמצאו {count} התקנות. נבחרה ההתקנה הראשונה."));
        Assert.Equal("Do you want to Start NodeBB at path /srv/nodebb?",
            service.TranslateFormat($"האם {action} את NodeBB בנתיב {path}?"));
    }


    [Fact]
    public void DatabaseBackupAndRestoreMessagesTranslateIncludingFilePaths()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        service.SetLanguage("en");
        var file = "C:/Backups/nodebb.nbbbackup";

        Assert.Contains(file, service.TranslateFormat($"גיבוי מסד הנתונים נשמר כשהוא מוצפן:\n{file}\n\nשמור את סיסמת ההצפנה במקום בטוח."));
        Assert.Contains("All contents of the NodeBB MongoDB database", service.Translate("כל תוכן מסד MongoDB של NodeBB יוחלף בתוכן הגיבוי. לפני כן ייווצר גיבוי חירום של כל מסד היעד. אם הייבוא ייכשל, המערכת תנסה לשחזר את מסד הנתונים הקודם. האם להמשיך?"));
        Assert.Contains("PostgreSQL database configured for NodeBB", service.Translate("השחזור יחליף את האובייקטים הכלולים בארכיון בתוך מסד PostgreSQL שהוגדר ב-NodeBB. אובייקטים שאינם כלולים בארכיון עשויים להישאר. לפני השינוי ייווצר גיבוי חירום של מסד היעד; אם השחזור ייכשל, המערכת תנסה להחזיר את המצב הקודם. להמשיך?"));
    }


    [Fact]
    public void RestrictedManagerLabelsTranslateToEnglish()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        service.SetLanguage("en");
        Assert.Equal("Backup", service.Translate("גיבוי"));
        Assert.Equal("No plugins were detected in the server list", service.Translate("לא זוהו תוספים ברשימת השרת"));
        Assert.Equal("Active", service.Translate("פעיל"));
        Assert.Equal("CSV file (*.csv)|*.csv", service.Translate("קובץ CSV (*.csv)|*.csv"));
    }


    [Fact]
    public void BackupWindowMessagesTranslateToEnglish()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        service.SetLanguage("en");
        var messages = new[]
        {
            "סיסמת ההצפנה חייבת להכיל לפחות 12 תווים.",
            "יוצר גיבוי בשרת ומוריד אותו למחשב...",
            "אישור שחזור התקנה מלאה",
            "הגיבוי המתוזמן הושלם בהצלחה."
        };

        foreach (var message in messages)
        {
            var translated = service.Translate(message);
            Assert.False(string.IsNullOrWhiteSpace(translated));
            Assert.NotEqual(message, translated);
        }
    }

    [Fact]
    public void BackupAndUpgradeSafetyPromptsTranslateToEnglish()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        service.SetLanguage("en");
        Assert.Contains("Windows vault", service.Translate("כדי שגיבוי יפעל ללא חלון התחברות, יש להתחבר מחדש לשרת ולבחור באפשרות שמירת פרטי ההתחברות בכספת Windows. גם מפתח SSH ללא סיסמה ניתן לשמור כפרט התחברות ריק."));
        Assert.Contains("will not be restored", service.Translate("השחזור יחליף את config.json ואת תיקיית public/uploads אם היא כלולה בגיבוי. מסד הנתונים עצמו לא ישוחזר, וקובצי package/lock לא ישתנו. הגדרות חיבור מסד הנתונים יוחלפו רק אם סוג המסד והשרת/מסד היעד תואמים להתקנה הנוכחית. לפני השינוי תישמר עותק חירום של הקבצים הקיימים, והתוכנה תנסה להחזירם במקרה כשל. להמשיך?"));
        Assert.Contains("entire NodeBB installation directory", service.Translate("שחזור מלא יחליף את כל תיקיית התקנת NodeBB, כולל קוד, תוספים, ערכות נושא, קבצים והגדרות. NodeBB ייעצר וינסה לעלות מחדש לאחר ההחלפה. מסד הנתונים עצמו אינו כלול ואינו משתנה; התוכנה תוודא שסוג המסד וכתובת/שם מסד היעד תואמים להתקנה הנוכחית. עותק מלא של ההתקנה הקיימת יישמר עד שהגרסה המשוחזרת תעלה בהצלחה. אם לא ניתן יהיה לשחזר בעלות והרשאות בבטחה, הפעולה תיעצר ללא שינוי. להמשיך?"));
        Assert.Contains("Existing backup files will not be deleted", service.Translate("להסיר את התזמון ואת סיסמת ההצפנה השמורה שלו? קובצי הגיבוי שכבר נוצרו לא יימחקו."));
        var error = "network error";
        Assert.Equal("Scheduled backup failed to run: network error", service.TranslateFormat($"הפעלת הגיבוי המתוזמן נכשלה: {error}"));
        var file = "C:/backup.nbbbackup";
        var kind = "מוצפן";
        var removed = 2;
        Assert.Contains("Old backups removed: 2", service.TranslateFormat($"הגיבוי נשמר במחשב:\n{file}\n\nסוג: {kind}\nגיבויים ישנים שהוסרו: {removed}"));
    }

    [Fact]
    public void SftpMonitoringComparisonAndManagementMessagesTranslateToEnglish()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        service.SetLanguage("en");
        Assert.Equal("Directory", service["Sftp_Directory"]);
        Assert.Equal("Connecting...", service["Sftp_Connecting"]);
        Assert.Equal("Delete 'config.json' from the server? This action cannot be undone.", string.Format(service["Sftp_DeletePrompt"], "config.json"));
        Assert.Equal("Automatic refresh is on (60 seconds).", string.Format(service["Monitor_AutoOn"], "60"));
        Assert.Equal("Collecting data from connected servers...", service["Compare_Collecting"]);
        Assert.Equal("Found 4 plugins and themes.", string.Format(service["Plugin_Found"], 4));
        Assert.Equal("Service manager: systemd", string.Format(service["Service_Manager"], "systemd"));
        Assert.Equal("Do you want to start service 'nodebb' on server production?", string.Format(service["Service_Confirm"], "start", "nodebb", "production"));
    }

    [Fact]
    public void PluginAndServiceWindowCaptionsTranslateToEnglish()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        service.SetLanguage("en");
        Assert.Equal("Loading installed packages...", service["Audit_Plugin_Loading"]);
        Assert.Equal("Enter a valid package name: nodebb-plugin-... or nodebb-theme-...", service["Audit_Plugin_InvalidName"]);
        Assert.Equal("Scanning system services...", service["Audit_Service_Scanning"]);
        Assert.Equal("Select a service first.", service["Audit_Service_Select"]);
        Assert.Equal("Confirm service change", service["Audit_Service_ConfirmTitle"]);
    }

    [Fact]
    public void SharedWindowCaptionsTranslateInBothLanguages()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));

        Assert.Equal("מרכז הבקרה של NodeBB", service["Ui_NodeBB_Control_Center"]);
        Assert.Equal("סריקת יכולות השרת", service["Ui_Server_Capability_Scan"]);

        service.SetLanguage("en");
        Assert.Equal("NODEBB  /  CONTROL CENTER", service["Ui_NodeBB_Control_Center"]);
        Assert.Equal("SERVER CAPABILITY SCAN", service["Ui_Server_Capability_Scan"]);
        Assert.Equal("LIVE SERVER TELEMETRY", service["Ui_Live_Server_Telemetry"]);
        Assert.Equal("SECURE SHELL SESSION", service["Ui_Secure_Shell_Session"]);
        Assert.Equal("UTF-8 · Interactive shell", service["Ui_Interactive_Shell"]);
    }

    [Fact]
    public void HistoryAndSshPromptMessagesTranslateToEnglish()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        service.SetLanguage("en");
        Assert.Equal("Showing 3 of 10 recent actions · up to 2,000 records",
            service.TranslateFormat($"מוצגות {3} מתוך {10} פעולות אחרונות · עד 2,000 רשומות"));
        Assert.Equal("No server actions have been recorded yet.", service["History_Empty"]);
        Assert.Equal("Enter the SSH user password.", service["SshPrompt_Password"]);
        Assert.Contains("private key", service["SshPrompt_Passphrase"]);
        Assert.Equal("Checking security settings...", service["SecurityAudit_Checking"]);
    }

    [Fact]
    public void DiagnosticsSecurityAuditAndHistoryExportMessagesTranslateToEnglish()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        service.SetLanguage("en");

        Assert.Equal("Save diagnostics report", service["Diagnostics_SaveTitle"]);
        Assert.Equal("ZIP archive|*.zip", service["Diagnostics_ZipFilter"]);
        Assert.Equal("The report was saved to:\nC:/reports/report.zip",
            service.TranslateFormat($"הדוח נשמר בנתיב:\n{"C:/reports/report.zip"}"));
        Assert.Equal("Audit completed. 2 critical findings, 3 warnings.",
            service.TranslateFormat($"הבדיקה הושלמה. {2} קריטיות, {3} אזהרות."));
        Assert.Equal("Security audit failed: network timeout",
            service.TranslateFormat($"בדיקת האבטחה נכשלה: {"network timeout"}"));
        Assert.Equal("Export all-server history", service["History_ExportAllTitle"]);
        Assert.Equal("Exported 7 actions.", service.TranslateFormat($"יוצאו {7} פעולות."));
    }

    [Fact]
    public void RestrictedManagerMessagesTranslateToEnglish()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        service.SetLanguage("en");

        Assert.Equal("Rebuild", service["Restricted_RebuildTitle"]);
        Assert.Equal("Delete ZIP", service["Restricted_DeleteZipTitle"]);
        Assert.Equal("No actions match the filter", service["Restricted_HistoryNoMatches"]);
        Assert.Equal("The plugin list was refreshed.", service["Restricted_PluginListUpdated"]);
        Assert.Equal("Server connection ready", service["Restricted_ConnectedTitle"]);
        Assert.Equal("Create a full forum backup and plugin list before continuing?", service["Restricted_BackupConfirm"]);
        Assert.Equal("How many lines should be displayed? (1–500)", service["Restricted_LogLineCount"]);
        Assert.Equal("The status check completed. The response does not clearly establish whether the service is active; see the original terminal output.",
            service["Restricted_EmptyStatus"]);
        Assert.Equal("The operation succeeded for 3 of 5 packages. 2 actions failed; review the full output before retrying.",
            service.TranslateFormat($"הפעולה הצליחה עבור {3} מתוך {5} חבילות. {2} פעולות נכשלו; בדוק את הפלט המלא לפני ניסיון חוזר."));
    }

    [Fact]
    public void RestrictedManagerStatusAndErrorMessagesTranslateToEnglish()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        service.SetLanguage("en");
        Assert.Equal("2 active · 1 disabled · 3 unknown", service.TranslateFormat($"{2} פעילים · {1} מושבתים · {3} לא מזוהים"));
        Assert.Equal("The server account is not authorized to perform this action.", service.Translate("לחשבון השרת אין הרשאה לבצע את הפעולה המבוקשת."));
        Assert.Equal("The requested item was not found on the server.", service.Translate("הפריט המבוקש לא נמצא בשרת."));
        Assert.Equal("The server did not respond in time. Check the connection and try again.", service.Translate("השרת לא השיב בזמן. בדוק את החיבור ונסה שוב."));
        Assert.Equal("The server connection closed during the operation. Reconnect and try again.", service.Translate("החיבור לשרת נסגר במהלך הפעולה. יש להתחבר מחדש ולנסות שוב."));
        Assert.Equal("The server confirmed that the plugin is disabled. It can be enabled again at any time.",
            service.Translate("השרת אישר את השבתת התוסף. ניתן להפעיל אותו מחדש בכל עת."));
        Assert.Equal("Inactive · status unknown", service.Translate("לא פעיל · מצב לא ידוע"));
    }

    [Fact]
    public void RestrictedBulkAndHistoryDialogsTranslateToEnglish()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        service.SetLanguage("en");
        Assert.Contains("remove all selected packages", service.Translate("הפעולה תסיר את כל החבילות שנבחרו. השרת מבצע גיבוי אוטומטי לפני כל שינוי. לא ניתן לבטל הסרה שכבר הושלמה."));
        Assert.Equal("Perform Bulk update on 4 items?\n\nplugin-a\n\nThis action will run on all selected packages, one by one. The server creates an automatic backup before each change.",
            string.Format(service["Restricted_BulkConfirm"], service["Restricted_BulkUpdate"], 4, "plugin-a", service["Restricted_BulkSafe"]));
        Assert.Equal("Perform Bulk disable on 2 items?\n\nplugin-a\nplugin-b\n\nThis action will run on all selected packages, one by one. The server creates an automatic backup before each change.",
            string.Format(service["Restricted_BulkConfirm"], service["Restricted_BulkDisable"], 2, "plugin-a\nplugin-b", service["Restricted_BulkSafe"]));
        Assert.Equal("Perform Bulk removal on 1 items?\n\nplugin-a\n\nThis action will remove all selected packages. The server creates an automatic backup before each change. A completed removal cannot be undone.",
            string.Format(service["Restricted_BulkConfirm"], service["Restricted_BulkRemove"], 1, "plugin-a", service["Restricted_BulkDestructive"]));
        Assert.Equal("Delete this server's local action history? This does not change anything on the server.",
            service.Translate("למחוק את היסטוריית הפעולות המקומית של השרת הזה? הפעולה אינה משנה דבר בשרת עצמו."));
        Assert.Equal("CSV file (*.csv)|*.csv", service.Translate("קובץ CSV (*.csv)|*.csv"));
        Assert.Equal("Succeeded", service.Translate("הצליח"));
    }

    [Fact]
    public void RejectsUnsupportedLanguage()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetLanguage("fr"));
    }
}
