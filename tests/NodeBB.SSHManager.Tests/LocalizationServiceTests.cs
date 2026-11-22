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
        Assert.Equal("The encryption password must contain at least 12 characters.", service.Translate("סיסמת ההצפנה חייבת להכיל לפחות 12 תווים."));
        Assert.Equal("Creating backup on server and downloading it...", service.Translate("יוצר גיבוי בשרת ומוריד אותו למחשב..."));
        Assert.Equal("Confirm full installation restore", service.Translate("אישור שחזור התקנה מלאה"));
        Assert.Equal("The scheduled backup completed successfully.", service.Translate("הגיבוי המתוזמן הושלם בהצלחה."));
    }

    [Fact]
    public void RejectsUnsupportedLanguage()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetLanguage("fr"));
    }
}
