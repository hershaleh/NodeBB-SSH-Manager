using System.Net;
using System.Net.Sockets;
using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;
using Renci.SshNet.Common;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class SshConnectionServiceTests
{


    [Fact]
    public async Task ProbeTcpEndpointAsync_ReturnsSuccessForListeningEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var profile = new ServerProfile { Host = IPAddress.Loopback.ToString(), Port = endpoint.Port };

        var result = await new SshConnectionService().ProbeTcpEndpointAsync(profile);

        Assert.True(result.Success);
        Assert.Contains("reachable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeTcpEndpointAsync_ReportsRefusedEndpointWithoutTryingAuthentication()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var profile = new ServerProfile { Host = IPAddress.Loopback.ToString(), Port = port };

        var result = await new SshConnectionService().ProbeTcpEndpointAsync(profile);

        Assert.False(result.Success);
        Assert.Contains("דחה את החיבור", result.Message);
        Assert.Equal(SocketError.ConnectionRefused, result.ErrorCode);
    }

    [Fact]
    public void SshConnectionTimeoutIsLongerThanTcpProbeTimeout()
    {
        Assert.True(SshConnectionService.SshHandshakeTimeoutSeconds > SshConnectionService.TcpProbeTimeoutSeconds);
        Assert.Equal(45, SshConnectionService.SshHandshakeTimeoutSeconds);
        Assert.Equal(8, SshConnectionService.TcpProbeTimeoutSeconds);
    }

    [Theory]
    [InlineData("SHA256:trusted-fingerprint", "SHA256:trusted-fingerprint", true)]
    [InlineData(null, "SHA256:observed-fingerprint", false)]
    [InlineData("", "SHA256:observed-fingerprint", false)]
    [InlineData("SHA256:trusted-fingerprint", null, false)]
    [InlineData("SHA256:trusted-fingerprint", "", false)]
    [InlineData("SHA256:trusted-fingerprint", "SHA256:changed-fingerprint", false)]
    [InlineData("sha256:trusted-fingerprint", "SHA256:trusted-fingerprint", false)]
    public void IsHostKeyTrusted_RequiresExactPreviouslyApprovedFingerprint(
        string? expected, string? observed, bool trusted)
    {
        Assert.Equal(trusted, SshConnectionService.IsHostKeyTrusted(expected, observed));
    }

    [Fact]
    public void GetFriendlyErrorMessage_AuthenticationFailure_IsActionable()
    {
        var message = SshConnectionService.GetFriendlyErrorMessage(new SshAuthenticationException("denied"));
        Assert.Contains("האימות נדחה", message);
        Assert.Contains("מפתח SSH", message);
    }

    [Fact]
    public void GetFriendlyErrorMessage_ConnectionRefused_ExplainsLikelyCause()
    {
        var message = SshConnectionService.GetFriendlyErrorMessage(new SocketException((int)SocketError.ConnectionRefused));
        Assert.Contains("דחה את החיבור", message);
        Assert.Contains("יציאת SSH", message);
    }

    [Fact]
    public void GetFriendlyErrorMessage_DnsFailure_IdentifiesDns()
    {
        var message = SshConnectionService.GetFriendlyErrorMessage(new SocketException((int)SocketError.HostNotFound));
        Assert.Contains("DNS", message);
    }

    [Fact]
    public void GetFriendlyErrorMessage_Timeout_ExplainsNetworkCheck()
    {
        var message = SshConnectionService.GetFriendlyErrorMessage(new TimeoutException());
        Assert.Contains("מגבלת הזמן", message);
        Assert.Contains("רשת", message);
    }

    [Fact]
    public void GetFriendlyErrorMessage_UnknownFailure_PreservesOriginalMessage()
    {
        var message = SshConnectionService.GetFriendlyErrorMessage(new InvalidOperationException("specific failure"));
        Assert.Equal("specific failure", message);
    }
}
