using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NodeBB.SSHManager.Services;

public sealed record DiagnosticLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Component,
    string Event,
    string? Server,
    string Message,
    string? Exception);

public sealed class DiagnosticLogService
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private const int RetainedFiles = 5;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly Regex UriCredentialPattern = new(@"(?i)([a-z][a-z0-9+.-]*://)[^/@\s]+:[^/@\s]+@([^/\s]+)", RegexOptions.Compiled);
    private static readonly Regex[] SecretPatterns =
    [
        new(@"(?i)([""']?(?:password|passwd|passphrase|token|api[_-]?key|secret)[""']?\s*[=:]\s*)[""']?[^,\s;""']+[""']?", RegexOptions.Compiled),
        new(@"(?i)(--(?:password|passwd|passphrase|token|api[-_]?key|secret)(?:=|\s+))(?:""[^""]*""|'[^']*'|[^\s]+)", RegexOptions.Compiled),
        new(@"(?i)(authorization\s*:\s*bearer\s+)[A-Za-z0-9._~+/-]+=*", RegexOptions.Compiled),
        new(@"(?i)((?:mongodb(?:\+srv)?|postgres(?:ql)?|redis|mysql)://)[^\s""']+", RegexOptions.Compiled),
        new(@"(?i)(-----BEGIN [A-Z ]*PRIVATE KEY-----)[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----)", RegexOptions.Compiled)
    ];

    public static DiagnosticLogService Instance { get; } = new();

    public string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NodeBB SSH Manager", "Logs");

    private string CurrentLogPath => Path.Combine(LogDirectory, $"application-{DateTime.UtcNow:yyyyMMdd}.jsonl");

    private DiagnosticLogService() { }

    public void Info(string component, string eventName, string message, string? server = null) =>
        Write("INFO", component, eventName, message, null, server);

    public void Warning(string component, string eventName, string message, Exception? exception = null, string? server = null) =>
        Write("WARNING", component, eventName, message, exception, server);

    public void Error(string component, string eventName, Exception exception, string? server = null) =>
        Write("ERROR", component, eventName, exception.Message, exception, server);

    public void Error(string component, string eventName, string message, string? server = null) =>
        Write("ERROR", component, eventName, message, null, server);

    private void Write(string level, string component, string eventName, string message, Exception? exception, string? server)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                var entry = new DiagnosticLogEntry(
                    DateTimeOffset.UtcNow, level, Clean(component), Clean(eventName),
                    server is null ? null : Clean(server), Redact(message),
                    exception is null ? null : Redact(exception.ToString()));
                File.AppendAllText(CurrentLogPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never crash or block the application.
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(CurrentLogPath) || new FileInfo(CurrentLogPath).Length < MaxLogBytes) return;
        var archivePath = Path.Combine(LogDirectory, $"application-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.jsonl");
        File.Move(CurrentLogPath, archivePath);
        var oldFiles = new DirectoryInfo(LogDirectory).GetFiles("application-*.jsonl")
            .OrderByDescending(f => f.LastWriteTimeUtc).Skip(RetainedFiles).ToArray();
        foreach (var file in oldFiles)
        {
            try { file.Delete(); } catch { }
        }
    }

    public async Task ExportReportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(LogDirectory);
        var tempPath = Path.Combine(Path.GetTempPath(), $"nodebb-ssh-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPath);
        try
        {
            var logFiles = Directory.GetFiles(LogDirectory, "application-*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(RetainedFiles).ToArray();
            foreach (var file in logFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = await File.ReadAllTextAsync(file, cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(tempPath, Path.GetFileName(file)),
                    SanitizeExportLog(content), new UTF8Encoding(false), cancellationToken);
            }

            var assembly = Assembly.GetExecutingAssembly();
            var manifest = new
            {
                generatedUtc = DateTimeOffset.UtcNow,
                application = assembly.GetName().Name,
                version = assembly.GetName().Version?.ToString(),
                operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                logFilesIncluded = logFiles.Length,
                privacy = "Credentials, private-key contents, environment variables and remote file contents are not included. Server profile labels are omitted from exported log entries."
            };
            await File.WriteAllTextAsync(Path.Combine(tempPath, "diagnostic-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellationToken);

            var fullDestinationPath = Path.GetFullPath(destinationPath);
            var destinationDirectory = Path.GetDirectoryName(fullDestinationPath)
                ?? throw new InvalidOperationException("נתיב דוח האבחון אינו תקין.");
            Directory.CreateDirectory(destinationDirectory);
            var temporaryArchivePath = Path.Combine(destinationDirectory,
                $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                ZipFile.CreateFromDirectory(tempPath, temporaryArchivePath, CompressionLevel.Optimal, false);
                if (File.Exists(fullDestinationPath))
                    File.Replace(temporaryArchivePath, fullDestinationPath, null, ignoreMetadataErrors: true);
                else
                    File.Move(temporaryArchivePath, fullDestinationPath);
            }
            finally
            {
                try { if (File.Exists(temporaryArchivePath)) File.Delete(temporaryArchivePath); } catch { }
            }
            Info("Diagnostics", "ReportExported", $"Diagnostic report exported ({logFiles.Length} log files).");
        }
        finally
        {
            try { Directory.Delete(tempPath, true); } catch { }
        }
    }

    internal static string SanitizeExportLog(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var output = new StringBuilder();
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<DiagnosticLogEntry>(line, JsonOptions);
                if (entry is null)
                {
                    output.AppendLine(Redact(line));
                    continue;
                }

                var sanitized = entry with
                {
                    Server = null,
                    Message = Redact(entry.Message),
                    Exception = entry.Exception is null ? null : Redact(entry.Exception)
                };
                output.AppendLine(JsonSerializer.Serialize(sanitized, JsonOptions));
            }
            catch (JsonException)
            {
                output.AppendLine(Redact(line));
            }
        }
        return output.ToString();
    }

    private static string Clean(string value) => Redact(value.Replace("\r", " ").Replace("\n", " "));

    public static string Redact(string value)
    {
        var result = UriCredentialPattern.Replace(value, "$1[REDACTED]@$2");
        foreach (var pattern in SecretPatterns)
            result = pattern.Replace(result, "$1[REDACTED]");
        return result;
    }
}
