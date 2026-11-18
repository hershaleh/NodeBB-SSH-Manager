using System.Text.Json;
using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Services;

public sealed class ServerProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly string _filePath;

    public ServerProfileStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NodeBB SSH Manager");
        _filePath = Path.Combine(_directory, "servers.json");
    }

    public IReadOnlyList<ServerProfile> Load()
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<ServerProfile>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            throw new InvalidDataException(
                $"קובץ הגדרות השרתים פגום או אינו תקין: {_filePath}");
        }
    }

    public void Save(IEnumerable<ServerProfile> profiles)
    {
        Directory.CreateDirectory(_directory);
        var temporaryPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(profiles, JsonOptions);
        File.WriteAllText(temporaryPath, json);

        if (File.Exists(_filePath))
            File.Replace(temporaryPath, _filePath, null);
        else
            File.Move(temporaryPath, _filePath);
    }
}
