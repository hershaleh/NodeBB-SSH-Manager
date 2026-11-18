using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class UnixPermissionAssessmentTests
{
    [Theory]
    [InlineData("600", false, "תקין")]
    [InlineData("0600", false, "תקין")]
    [InlineData("000", false, "בדיקה נדרשת")]
    [InlineData("400", false, "תקין")]
    [InlineData("200", false, "בדיקה נדרשת")]
    [InlineData("4755", false, "קריטי")]
    [InlineData("2755", false, "קריטי")]
    [InlineData("640", false, "אזהרה")]
    [InlineData("644", false, "אזהרה")]
    [InlineData("660", false, "קריטי")]
    [InlineData("777", false, "קריטי")]
    [InlineData("755", true, "תקין")]
    [InlineData("775", true, "אזהרה")]
    [InlineData("777", true, "אזהרה")]
    [InlineData("000", true, "בדיקה נדרשת")]
    [InlineData("1777", true, "אזהרה")]
    [InlineData("4755", true, "תקין")]
    [InlineData("invalid", false, "בדיקה נדרשת")]
    [InlineData("88", false, "בדיקה נדרשת")]
    [InlineData("7555", false, "קריטי")]
    [InlineData("0755", true, "תקין")]
    public void Assess_ClassifiesUnixPermissions(string mode, bool isDirectory, string expectedSeverity)
    {
        var result = UnixPermissionAssessment.Assess(mode, isDirectory);
        Assert.Equal(expectedSeverity, result.Severity);
        Assert.False(string.IsNullOrWhiteSpace(result.Recommendation));
    }
}
