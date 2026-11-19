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
    public bool IsFavorite { get; set; }
    public string Group { get; set; } = "";
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public SshAuthenticationType AuthenticationType { get; set; } = SshAuthenticationType.PrivateKey;
    public string PrivateKeyPath { get; set; } = "";
    public string? KnownHostFingerprint { get; set; }

    public string AuthenticationLabel => AuthenticationType == SshAuthenticationType.Password
        ? "סיסמה"
        : "מפתח SSH";

    public string HostDisplay => $"{Host}:{Port}";

    [JsonIgnore]
    public bool IsConnected { get; set; }

    [JsonIgnore]
    public string ConnectionCheckLabel { get; set; } = "טרם נבדק";

    [JsonIgnore]
    public string ConnectionStatusLabel => IsConnected ? "מחובר באמצעות SSH" : "לא מחובר";

    [JsonIgnore]
    public bool HasSavedCredential { get; set; }

    [JsonIgnore]
    public string SavedCredentialLabel => HasSavedCredential ? "סיסמה שמורה במחשב" : "לא נשמרה סיסמה";

    public ServerProfile CreateDuplicate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            throw new ArgumentException("שם הפרופיל חייב להכיל 1 עד 120 תווים.", nameof(name));

        return new ServerProfile
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Group = Group,
            Host = Host,
            Port = Port,
            Username = Username,
            AuthenticationType = AuthenticationType,
            PrivateKeyPath = PrivateKeyPath,
            KnownHostFingerprint = KnownHostFingerprint,
            IsFavorite = false
        };
    }
}
