using System.Collections.Concurrent;
using NodeBB.SSHManager.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NodeBB.SSHManager.Services;

public sealed class SshHostKeyException(string fingerprint) : Exception("שרת SSH דורש אימות טביעת אצבע.")
{
    public string Fingerprint { get; } = fingerprint;
}

public sealed class SshServerSession : IDisposable
{
    private readonly string _secret;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    public ServerProfile Profile { get; }
    public SshClient Client { get; }
    public bool IsConnected => Client.IsConnected;

    internal SshServerSession(ServerProfile profile, string secret, ConnectionInfo info, SshClient client)
    {
        Profile = profile;
        _secret = secret;
        Client = client;
        ConnectionInfo = info;
    }

    internal ConnectionInfo ConnectionInfo { get; }

    public async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(
        string command, CancellationToken cancellationToken = default)
    {
        if (!Client.IsConnected) throw new InvalidOperationException("אין חיבור SSH פעיל.");
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                using var cmd = Client.RunCommand(command);
                return (cmd.ExitStatus, cmd.Result, cmd.Error);
            }, cancellationToken);
        }
        finally { _commandLock.Release(); }
    }

    public SftpClient CreateSftpClient()
    {
        var auth = SshSessionManager.CreateAuthentication(Profile, _secret);
        var info = new ConnectionInfo(Profile.Host, Profile.Port, Profile.Username, auth)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        var client = new SftpClient(info);
        client.HostKeyReceived += (_, e) =>
        {
            var actual = "SHA256:" + e.FingerPrintSHA256;
            e.CanTrust = string.Equals(actual, Profile.KnownHostFingerprint, StringComparison.Ordinal);
        };
        return client;
    }

    public void Dispose()
    {
        try { if (Client.IsConnected) Client.Disconnect(); } catch { }
        Client.Dispose();
        _commandLock.Dispose();
    }
}

public sealed class SshSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, SshServerSession> _sessions = new();

    public IReadOnlyCollection<SshServerSession> Sessions => _sessions.Values.ToArray();
    public int ConnectedCount => _sessions.Values.Count(s => s.IsConnected);

    public async Task<SshServerSession> ConnectAsync(ServerProfile profile, string secret,
        CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(profile.Id, out var existing) && existing.IsConnected)
            return existing;

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var auth = CreateAuthentication(profile, secret);
            var info = new ConnectionInfo(profile.Host, profile.Port, profile.Username, auth)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            string? fingerprint = null;
            var client = new SshClient(info);
            client.HostKeyReceived += (_, e) =>
            {
                fingerprint = "SHA256:" + e.FingerPrintSHA256;
                e.CanTrust = !string.IsNullOrWhiteSpace(profile.KnownHostFingerprint)
                    && string.Equals(fingerprint, profile.KnownHostFingerprint, StringComparison.Ordinal);
            };

            try
            {
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();
                var session = new SshServerSession(profile, secret, info, client);
                if (_sessions.TryGetValue(profile.Id, out var old)) old.Dispose();
                _sessions[profile.Id] = session;
                return session;
            }
            catch
            {
                client.Dispose();
                if (fingerprint is not null &&
                    !string.Equals(fingerprint, profile.KnownHostFingerprint, StringComparison.Ordinal))
                    throw new SshHostKeyException(fingerprint);
                throw;
            }
        }, cancellationToken);
    }

    public bool TryGet(Guid profileId, out SshServerSession? session)
    {
        if (_sessions.TryGetValue(profileId, out var found) && found.IsConnected)
        {
            session = found;
            return true;
        }
        session = null;
        return false;
    }

    public void Disconnect(Guid profileId)
    {
        if (_sessions.TryRemove(profileId, out var session)) session.Dispose();
    }

    public static AuthenticationMethod CreateAuthentication(ServerProfile profile, string secret)
    {
        if (profile.AuthenticationType == SshAuthenticationType.Password)
            return new PasswordAuthenticationMethod(profile.Username, secret);
        if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath) || !File.Exists(profile.PrivateKeyPath))
            throw new FileNotFoundException("לא נמצא קובץ מפתח SSH בפרופיל השרת.", profile.PrivateKeyPath);
        var key = string.IsNullOrEmpty(secret)
            ? new PrivateKeyFile(profile.PrivateKeyPath)
            : new PrivateKeyFile(profile.PrivateKeyPath, secret);
        return new PrivateKeyAuthenticationMethod(profile.Username, key);
    }

    public void Dispose()
    {
        foreach (var id in _sessions.Keys) Disconnect(id);
    }
}
