using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Services;

public static class SshCommandFormatter
{
    public static string FormatPowerShell(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Host)
            || string.IsNullOrWhiteSpace(profile.Username)
            || profile.Port is < 1 or > 65535)
            throw new ArgumentException("פרטי החיבור אינם תקינים.", nameof(profile));

        var command = $"ssh -p {profile.Port}";
        if (profile.AuthenticationType == SshAuthenticationType.PrivateKey)
        {
            if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
                throw new ArgumentException("לא הוגדר נתיב למפתח SSH.", nameof(profile));
            command += $" -i {QuotePowerShell(profile.PrivateKeyPath)}";
        }

        return command + $" {QuotePowerShell(profile.Username + "@" + profile.Host)}";
    }

    private static string QuotePowerShell(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
