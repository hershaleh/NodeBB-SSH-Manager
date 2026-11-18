using NodeBB.SSHManager.Models;
using Renci.SshNet;

namespace NodeBB.SSHManager.Services;

public enum SshTestStatus
{
    Success,
    HostKeyVerificationRequired,
    Failed
}

public sealed record SshTestResult(
    SshTestStatus Status,
    string Message,
    string? Fingerprint = null,
    string? RemoteSummary = null);

public sealed class SshConnectionService
{
    public Task<SshTestResult> TestConnectionAsync(
        ServerProfile profile,
        string secret,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => TestConnection(profile, secret, cancellationToken), cancellationToken);
    }

    private static SshTestResult TestConnection(
        ServerProfile profile,
        string secret,
        CancellationToken cancellationToken)
    {
        string? observedFingerprint = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var authentication = CreateAuthentication(profile, secret);
            var connectionInfo = new ConnectionInfo(
                profile.Host,
                profile.Port,
                profile.Username,
                authentication)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            using var client = new SshClient(connectionInfo);
            client.HostKeyReceived += (_, eventArgs) =>
            {
                observedFingerprint = "SHA256:" + eventArgs.FingerPrintSHA256;
                eventArgs.CanTrust = !string.IsNullOrWhiteSpace(profile.KnownHostFingerprint)
                    && string.Equals(
                        observedFingerprint,
                        profile.KnownHostFingerprint,
                        StringComparison.Ordinal);
            };

            client.Connect();

            cancellationToken.ThrowIfCancellationRequested();
            using var command = client.RunCommand("id -un; uname -s");
            if (command.ExitStatus != 0)
            {
                return new SshTestResult(
                    SshTestStatus.Failed,
                    string.IsNullOrWhiteSpace(command.Error)
                        ? "החיבור נוצר, אך פקודת בדיקת המערכת נכשלה."
                        : command.Error,
                    observedFingerprint);
            }

            return new SshTestResult(
                SshTestStatus.Success,
                "חיבור SSH ואימות המשתמש הצליחו.",
                observedFingerprint,
                command.Result.Trim());
        }
        catch (Exception) when (observedFingerprint is not null
                                && !string.Equals(
                                    observedFingerprint,
                                    profile.KnownHostFingerprint,
                                    StringComparison.Ordinal))
        {
            return new SshTestResult(
                SshTestStatus.HostKeyVerificationRequired,
                "טביעת האצבע של שרת SSH אינה מאושרת בפרופיל זה.",
                observedFingerprint);
        }
        catch (OperationCanceledException)
        {
            return new SshTestResult(SshTestStatus.Failed, "הפעולה בוטלה.");
        }
        catch (Exception ex)
        {
            return new SshTestResult(SshTestStatus.Failed, ex.Message, observedFingerprint);
        }
    }

    private static AuthenticationMethod CreateAuthentication(ServerProfile profile, string secret)
    {
        if (profile.AuthenticationType == SshAuthenticationType.Password)
            return new PasswordAuthenticationMethod(profile.Username, secret);

        if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath) || !File.Exists(profile.PrivateKeyPath))
            throw new FileNotFoundException("לא נמצא קובץ מפתח SSH בפרופיל השרת.", profile.PrivateKeyPath);

        var keyFile = string.IsNullOrEmpty(secret)
            ? new PrivateKeyFile(profile.PrivateKeyPath)
            : new PrivateKeyFile(profile.PrivateKeyPath, secret);

        return new PrivateKeyAuthenticationMethod(profile.Username, keyFile);
    }
}
