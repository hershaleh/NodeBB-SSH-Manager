using NodeBB.SSHManager.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using System.Net.Sockets;

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
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
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
            // Keep probe commands simple and separate. Some restricted SSH gateways reject
            // shell command separators (such as ';') even when each command is permitted.
            using var identityCommand = client.RunCommand("id -un");
            if (identityCommand.ExitStatus != 0)
            {
                return new SshTestResult(
                    SshTestStatus.Failed,
                    string.IsNullOrWhiteSpace(identityCommand.Error)
                        ? "החיבור נוצר, אך בדיקת זיהוי המשתמש נכשלה."
                        : identityCommand.Error,
                    observedFingerprint);
            }

            using var systemCommand = client.RunCommand("uname -s");
            if (systemCommand.ExitStatus != 0)
            {
                return new SshTestResult(
                    SshTestStatus.Failed,
                    string.IsNullOrWhiteSpace(systemCommand.Error)
                        ? "החיבור נוצר, אך בדיקת זיהוי מערכת ההפעלה נכשלה."
                        : systemCommand.Error,
                    observedFingerprint);
            }

            DiagnosticLogService.Instance.Info("SSH", "ConnectionTestSucceeded", "SSH connection and identity check succeeded.", profile.Name);
            return new SshTestResult(
                SshTestStatus.Success,
                "חיבור SSH ואימות המשתמש הצליחו.",
                observedFingerprint,
                $"User: {identityCommand.Result.Trim()}\nSystem: {systemCommand.Result.Trim()}");
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
            DiagnosticLogService.Instance.Error("SSH", "ConnectionTestFailed", ex, profile.Name);
            return new SshTestResult(SshTestStatus.Failed, GetFriendlyErrorMessage(ex), observedFingerprint);
        }
    }

    public static string GetFriendlyErrorMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            SshAuthenticationException => "האימות נדחה על ידי השרת. בדוק את שם המשתמש, הסיסמה או מפתח SSH והרשאותיו.",
            SocketException socket when socket.SocketErrorCode == SocketError.ConnectionRefused => "השרת דחה את החיבור. בדוק שכתובת השרת ויציאת SSH נכונות וששירות SSH פועל.",
            SocketException socket when socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "לא ניתן למצוא את שם המתחם. בדוק את שם המארח ואת הגדרות DNS.",
            SocketException socket when socket.SocketErrorCode == SocketError.TimedOut => "החיבור לשרת חרג ממגבלת הזמן. בדוק זמינות רשת, חומת אש ויציאת SSH.",
            SocketException socket when socket.SocketErrorCode is SocketError.NetworkUnreachable or SocketError.HostUnreachable => "אין נתיב רשת אל השרת. בדוק את החיבור לרשת, ניתוב וחומת אש.",
            TimeoutException => "הפעולה מול שרת SSH חרגה ממגבלת הזמן. בדוק זמינות שרת ורשת.",
            SshConnectionException => "חיבור SSH נותק או נכשל במהלך יצירתו. בדוק את שירות SSH, הרשת והגדרות השרת.",
            IOException => "אירעה שגיאת תקשורת או קובץ במהלך החיבור. בדוק הרשאות וגישה לרשת.",
            _ => string.IsNullOrWhiteSpace(exception.Message) ? "החיבור נכשל מסיבה לא ידועה." : exception.Message
        };
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
