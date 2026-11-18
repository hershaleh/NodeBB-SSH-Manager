using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class UnixPermissionAssessmentTests
{
    [Theory]
    [InlineData("600", false, "תקין")]
    [InlineData("640", false, "אזהרה")]
    [InlineData("644", false, "אזהרה")]
    [InlineData("660", false, "קריטי")]
    [InlineData("777", false, "קריטי")]
    [InlineData("755", true, "תקין")]
    [InlineData("775", true, "אזהרה")]
    [InlineData("777", true, "אזהרה")]
    [InlineData("invalid", false, "בדיקה נדרשת")]
    [InlineData("88", false, "בדיקה נדרשת")]
    public void Assess_ClassifiesUnixPermissions(string mode, bool isDirectory, string expectedSeverity)
    {
        var result = UnixPermissionAssessment.Assess(mode, isDirectory);
        Assert.Equal(expectedSeverity, result.Severity);
        Assert.False(string.IsNullOrWhiteSpace(result.Recommendation));
    }
}
