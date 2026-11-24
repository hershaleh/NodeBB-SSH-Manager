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

public sealed record TcpEndpointProbeResult(bool Success, string Message, SocketError? ErrorCode = null);

public sealed class SshConnectionService
{
    public const int TcpProbeTimeoutSeconds = 8;
    public const int SshHandshakeTimeoutSeconds = 45;

    public async Task<TcpEndpointProbeResult> ProbeTcpEndpointAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TcpProbeTimeoutSeconds));

        try
        {
            await client.ConnectAsync(profile.Host, profile.Port, timeout.Token);
            return new TcpEndpointProbeResult(true, "TCP endpoint is reachable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TcpEndpointProbeResult(false,
                $"לא התקבלה תשובה מ-{profile.Host}:{profile.Port} בתוך {TcpProbeTimeoutSeconds} שניות. בדוק כתובת, פורט, חומת אש וניתוב.");
        }
        catch (SocketException ex)
        {
            return new TcpEndpointProbeResult(false, GetFriendlyErrorMessage(ex), ex.SocketErrorCode);
        }
    }

    public async Task<SshTestResult> TestConnectionAsync(
        ServerProfile profile,
        string secret,
        CancellationToken cancellationToken = default)
    {
        var endpoint = await ProbeTcpEndpointAsync(profile, cancellationToken);
        if (!endpoint.Success)
            return new SshTestResult(SshTestStatus.Failed, endpoint.Message);

        return await Task.Run(() => TestConnection(profile, secret, cancellationToken), cancellationToken);
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
                Timeout = TimeSpan.FromSeconds(SshHandshakeTimeoutSeconds)
            };

            using var client = new SshClient(connectionInfo);
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            client.HostKeyReceived += (_, eventArgs) =>
            {
                observedFingerprint = "SHA256:" + eventArgs.FingerPrintSHA256;
                eventArgs.CanTrust = IsHostKeyTrusted(profile.KnownHostFingerprint, observedFingerprint);
            };

            client.Connect();

            cancellationToken.ThrowIfCancellationRequested();

            // A successful SSH authentication is sufficient for the connection test.
            // Some hosting providers expose a restricted SSH command interface rather
            // than a POSIX shell, so commands such as id/uname are not portable probes.
            DiagnosticLogService.Instance.Info("SSH", "ConnectionTestSucceeded", "SSH authentication succeeded; remote shell capabilities were not probed.", profile.Name);
            return new SshTestResult(
                SshTestStatus.Success,
                "האימות והחיבור לשרת SSH הצליחו. בדיקת פקודות שרת תתבצע בעת שימוש בכלי הניהול.",
                observedFingerprint,
                "SSH authentication successful.");
        }
        catch (Exception) when (observedFingerprint is not null
                                && !IsHostKeyTrusted(profile.KnownHostFingerprint, observedFingerprint))
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

    public static bool IsHostKeyTrusted(string? expectedFingerprint, string? observedFingerprint)
    {
        return !string.IsNullOrWhiteSpace(expectedFingerprint)
            && !string.IsNullOrWhiteSpace(observedFingerprint)
            && string.Equals(expectedFingerprint, observedFingerprint, StringComparison.Ordinal);
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
            SshConnectionException ssh when ssh.Message.Contains("Connection failed to establish within", StringComparison.OrdinalIgnoreCase)
                => $"פורט TCP נגיש, אך שרת SSH לא השלים את שלב יצירת החיבור בתוך {SshHandshakeTimeoutSeconds} שניות. בדוק ששירות SSH פועל בפורט זה, את עומס השרת ואת חומת האש או הגדרות SSH.",
            TimeoutException => "הפעולה מול שרת SSH חרגה ממגבלת הזמן. בדוק זמינות שרת ורשת.",
            SshConnectionException => "חיבור SSH נותק או נכשל במהלך יצירתו. בדוק את שירות SSH, הרשת והגדרות השרת.",
            IOException => "אירעה שגיאת תקשורת או קובץ במהלך החיבור. בדוק הרשאות וגישה לרשת.",
            _ => string.IsNullOrWhiteSpace(exception.Message) ? "החיבור נכשל מסיבה לא ידועה." : exception.Message
        };
    }

    private static AuthenticationMethod CreateAuthentication(ServerProfile profile, string secret)
        => SshSessionManager.CreateAuthentication(profile, secret);
}
