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
    private int _disposed;
    public ServerProfile Profile { get; }
    public SshClient Client { get; }
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && Client.IsConnected;

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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!Client.IsConnected) throw new InvalidOperationException("אין חיבור SSH פעיל.");
            cancellationToken.ThrowIfCancellationRequested();

            // Once an SSH command starts, keep the session lock until SSH.NET has finished
            // reading its result. Cancelling Task.Run itself would not stop the remote command
            // and could otherwise allow Dispose to disconnect the client underneath it.
            return await Task.Run(() =>
            {
                using var cmd = Client.RunCommand(command);
                return (cmd.ExitStatus ?? -1, cmd.Result, cmd.Error);
            });
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
        var client = new SftpClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        client.HostKeyReceived += (_, e) =>
        {
            var actual = "SHA256:" + e.FingerPrintSHA256;
            e.CanTrust = string.Equals(actual, Profile.KnownHostFingerprint, StringComparison.Ordinal);
        };
        return client;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Wait for any in-flight command before disconnecting. Do not dispose the semaphore:
        // callers already waiting on it must be able to wake up and observe _disposed safely.
        _commandLock.Wait();
        try
        {
            try { if (Client.IsConnected) Client.Disconnect(); } catch { }
            Client.Dispose();
        }
        finally
        {
            _commandLock.Release();
        }
    }
}

public sealed class SshSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, SshServerSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _profileLocks = new();
    private int _disposed;

    public IReadOnlyCollection<SshServerSession> Sessions => _sessions.Values.ToArray();
    public int ConnectedCount => _sessions.Values.Count(s => s.IsConnected);

    public async Task<SshServerSession> ConnectAsync(ServerProfile profile, string secret,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var profileLock = _profileLocks.GetOrAdd(profile.Id, static _ => new SemaphoreSlim(1, 1));
        await profileLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_sessions.TryGetValue(profile.Id, out var existing))
            {
                if (existing.IsConnected)
                {
                    DiagnosticLogService.Instance.Info("SSH", "SessionReused", "An existing SSH session was reused.", profile.Name);
                    return existing;
                }
                if (_sessions.TryRemove(profile.Id, out var stale))
                {
                    stale.Dispose();
                    DiagnosticLogService.Instance.Warning("SSH", "StaleSessionReplaced", "A disconnected SSH session was cleaned up before reconnecting.", profile.Name);
                }
            }

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var auth = CreateAuthentication(profile, secret);
                var info = new ConnectionInfo(profile.Host, profile.Port, profile.Username, auth)
                {
                    Timeout = TimeSpan.FromSeconds(20)
                };
                string? fingerprint = null;
                var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
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
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    var session = new SshServerSession(profile, secret, info, client);
                    _sessions[profile.Id] = session;
                    DiagnosticLogService.Instance.Info("SSH", "SessionConnected", "Persistent SSH session established.", profile.Name);
                    return session;
                }
                catch (Exception ex)
                {
                    DiagnosticLogService.Instance.Error("SSH", "SessionConnectFailed", ex, profile.Name);
                    client.Dispose();
                    if (fingerprint is not null &&
                        !string.Equals(fingerprint, profile.KnownHostFingerprint, StringComparison.Ordinal))
                        throw new SshHostKeyException(fingerprint);
                    throw;
                }
            }, cancellationToken);
        }
        finally
        {
            profileLock.Release();
        }
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
        var profileLock = _profileLocks.GetOrAdd(profileId, static _ => new SemaphoreSlim(1, 1));
        profileLock.Wait();
        try
        {
            if (_sessions.TryRemove(profileId, out var session))
            {
                session.Dispose();
                DiagnosticLogService.Instance.Info("SSH", "SessionDisconnected", "SSH session disconnected.", session.Profile.Name);
            }
        }
        finally
        {
            profileLock.Release();
        }
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var id in _profileLocks.Keys) Disconnect(id);
    }
}
