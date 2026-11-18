using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class DiagnosticLogServiceTests
{
    [Theory]
    [InlineData("password=TopSecret123", "password=[REDACTED]")]
    [InlineData("API_KEY:abc123", "API_KEY:[REDACTED]")]
    [InlineData("{\"password\":\"TopSecret123\"}", "{\"password\":[REDACTED]}")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9", "Authorization: Bearer [REDACTED]")]
    public void Redact_RemovesCredentialValues(string input, string expected)
    {
        var actual = DiagnosticLogService.Redact(input);
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("TopSecret123", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", actual, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nodebb-plugin-example", true)]
    [InlineData("nodebb-theme-example", true)]
    [InlineData("nodebb-plugin-abc_123.test", true)]
    [InlineData("nodebb-plugin-; rm -rf /", false)]
    [InlineData("ordinary-package", false)]
    [InlineData("../nodebb-plugin-escape", false)]
    public void PackageNameValidation_RejectsUnsafeNames(string packageName, bool expected)
    {
        Assert.Equal(expected, PluginThemeRemoteService.IsValidPackageName(packageName));
    }

    [Theory]
    [InlineData("SYSTEMD", "nodebb.service", true)]
    [InlineData("OPENRC", "nodebb", true)]
    [InlineData("SYSV", "nodebb.service", true)]
    [InlineData("DOCKER", "0123456789abcdef", true)]
    [InlineData("DOCKER", "nodebb; rm -rf /", false)]
    [InlineData("SYSTEMD", "nodebb;id", false)]
    [InlineData("MANUAL", "CLI", true)]
    [InlineData("MANUAL", "anything", false)]
    public void RuntimeIdentifierValidation_RejectsUnsafeIdentifiers(string manager, string identifier, bool expected)
    {
        Assert.Equal(expected, NodeBbRuntimeService.IsSafeIdentifier(manager, identifier));
    }

    [Fact]
    public void Redact_PreservesOrdinaryText()
    {
        const string input = "SSH connection failed with timeout after 15 seconds.";
        Assert.Equal(input, DiagnosticLogService.Redact(input));
    }

    [Fact]
    public void Redact_RemovesPrivateKeyBody()
    {
        const string input = "-----BEGIN OPENSSH PRIVATE KEY-----\nprivate-material\n-----END OPENSSH PRIVATE KEY-----";
        var actual = DiagnosticLogService.Redact(input);
        Assert.DoesNotContain("private-material", actual, StringComparison.Ordinal);
    }
}
