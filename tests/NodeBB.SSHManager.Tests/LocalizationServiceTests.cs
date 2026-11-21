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
            Assert.Equal(FlowDirection.LeftToRight, service.FlowDirection);
            Assert.Equal("Dashboard", service["Dashboard_לוח_בקרה"]);
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
    public void RejectsUnsupportedLanguage()
    {
        var service = new LocalizationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "language.json"));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetLanguage("fr"));
    }
}
