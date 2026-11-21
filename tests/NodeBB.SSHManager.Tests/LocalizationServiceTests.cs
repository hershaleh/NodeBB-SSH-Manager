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
    public void BackupWindowMessagesAndFormattedTemplatesTranslateToEnglish()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json");
        var service = new LocalizationService(path);
        service.SetLanguage("en");

        Assert.Equal("Scheduled backup failed: ", service.Translate("הפעלת הגיבוי המתוזמן נכשלה: "));
        Assert.Equal("File restore failed: ", service.Translate("שחזור הקבצים נכשל: "));
        Assert.Equal("Full installation restore failed: ", service.Translate("שחזור ההתקנה המלאה נכשל: "));
        Assert.Contains("Continue?", service.Translate("שחזור מלא יחליף את כל תיקיית התקנת NodeBB, כולל קוד, תוספים, ערכות נושא, קבצים והגדרות. NodeBB ייעצר וינסה לעלות מחדש לאחר ההחלפה. מסד הנתונים עצמו אינו כלול ואינו משתנה; התוכנה תוודא שסוג המסד וכתובת/שם מסד היעד תואמים להתקנה הנוכחית. עותק מלא של ההתקנה הקיימת יישמר עד שהגרסה המשוחזרת תעלה בהצלחה. אם לא ניתן יהיה לשחזר בעלות והרשאות בבטחה, הפעולה תיעצר ללא שינוי. להמשיך?"));
        Assert.Contains("{0}", service.Translate(" הוסרו {0} גיבויים ישנים לפי מדיניות השמירה."));
        Assert.Contains("Backup saved on this computer", service.Translate("הגיבוי נשמר במחשב:\\n{0}\\n\\nסוג: {1}\\nגיבויים ישנים שהוסרו: {2}"));
    }

    [Fact]
    public void RejectsUnsupportedLanguage()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetLanguage("fr"));
    }
}
