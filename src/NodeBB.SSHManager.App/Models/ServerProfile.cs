using System.Text.Json.Serialization;

namespace NodeBB.SSHManager.Models;

public enum SshAuthenticationType
{
    Password,
    PrivateKey
}

public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public SshAuthenticationType AuthenticationType { get; set; } = SshAuthenticationType.PrivateKey;
    public string PrivateKeyPath { get; set; } = "";
    public string? KnownHostFingerprint { get; set; }

    public string AuthenticationLabel => AuthenticationType == SshAuthenticationType.Password
        ? "סיסמה"
        : "מפתח SSH";

    public string HostDisplay => $"{Host}:{Port}";

    [JsonIgnore]
    public bool HasSavedCredential { get; set; }

    [JsonIgnore]
    public string SavedCredentialLabel => HasSavedCredential ? "סיסמה שמורה במחשב" : "לא נשמרה סיסמה";
}
