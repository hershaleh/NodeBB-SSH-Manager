using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace NodeBB.SSHManager.Services;

public sealed record AppUpdateInfo(
    Version Version,
    string TagName,
    Uri ReleasePageUri,
    Uri InstallerUri,
    Uri ChecksumsUri);

public sealed record AppUpdateCheckResult(
    Version CurrentVersion,
    Version? LatestVersion,
    bool UpdateAvailable,
    AppUpdateInfo? Update,
    string Message);

/// <summary>
/// Checks stable GitHub releases and installs only a per-user installer whose SHA-256
/// matches the checksum manifest published with that release.
/// </summary>
public sealed class AppUpdateService
{
    public const string InstallerFileName = "NodeBB-SSH-Manager-Setup-win-x64.exe";
    public const string ChecksumsFileName = "SHA256SUMS.txt";
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private const int MaximumReleaseMetadataBytes = 1024 * 1024;
    private const int MaximumChecksumManifestBytes = 64 * 1024;
    private const string LatestReleaseApi = "https://api.github.com/repos/hershaleh/NodeBB-SSH-Manager/releases/latest";
    private static readonly HttpClient SharedHttp = CreateHttpClient();
    private readonly HttpClient _http;

    public AppUpdateService(HttpClient? httpClient = null) =>
        _http = httpClient ?? SharedHttp;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NodeBB-SSH-Manager-UpdateClient/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = GetCurrentVersion();
        using var response = await _http.GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureTrustedApiResponse(response);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new AppUpdateCheckResult(current, null, false, null, "עדיין לא פורסמה גרסה יציבה של התוכנה.");

        response.EnsureSuccessStatusCode();
        var json = await ReadBoundedTextAsync(response.Content, MaximumReleaseMetadataBytes, "פרטי הגרסה ב-GitHub", cancellationToken);
        return ParseReleaseMetadata(json, current);
    }

    public static Version GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
        return NormalizeVersion(version);
    }

    public static AppUpdateCheckResult ParseReleaseMetadata(string json, Version currentVersion)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(currentVersion);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidDataException("פרטי הגרסה ב-GitHub אינם מכילים תגית גרסה.");

        var normalizedTag = tag.Trim();
        if (normalizedTag.StartsWith('v') || normalizedTag.StartsWith('V'))
            normalizedTag = normalizedTag[1..];
        if (!Version.TryParse(normalizedTag, out var latest))
            throw new InvalidDataException("תגית הגרסה שפורסמה אינה מספר גרסה תקין.");

        var current = NormalizeVersion(currentVersion);
        latest = NormalizeVersion(latest);
        var releaseUrl = GetTrustedReleaseUri(root, normalizedTag);
        var prerelease = root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
        if (prerelease || latest <= current)
        {
            var message = prerelease
                ? "לא נמצאה גרסה יציבה חדשה יותר; גרסת קדם-הפצה לא תותקן אוטומטית."
                : $"התוכנה מעודכנת. הגרסה המותקנת היא {current}.";
            return new AppUpdateCheckResult(current, latest, false, null, message);
        }

        var assets = root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array
            ? assetsElement.EnumerateArray().ToArray()
            : [];
        var installer = assets.FirstOrDefault(x =>
            x.TryGetProperty("name", out var name) &&
            string.Equals(name.GetString(), InstallerFileName, StringComparison.Ordinal));
        var checksums = assets.FirstOrDefault(x =>
            x.TryGetProperty("name", out var name) &&
            string.Equals(name.GetString(), ChecksumsFileName, StringComparison.Ordinal));
        if (installer.ValueKind != JsonValueKind.Object || checksums.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("הגרסה החדשה חסרה מתקין או קובץ SHA256SUMS.txt; העדכון נעצר.");

        var installerUri = GetTrustedAssetUri(installer, normalizedTag, InstallerFileName);
        var checksumsUri = GetTrustedAssetUri(checksums, normalizedTag, ChecksumsFileName);
        var info = new AppUpdateInfo(latest, tag, releaseUrl, installerUri, checksumsUri);
        return new AppUpdateCheckResult(current, latest, true, info,
            $"נמצאה גרסה חדשה {latest}; הגרסה המותקנת היא {current}.");
    }

    public async Task<string> DownloadInstallerAsync(AppUpdateInfo update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!IsTrustedGitHubUri(update.ChecksumsUri) || !IsTrustedGitHubUri(update.InstallerUri))
            throw new InvalidDataException("כתובות קובצי העדכון חייבות להיות כתובות GitHub HTTPS מאושרות.");
        using var manifestResponse = await _http.GetAsync(update.ChecksumsUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureTrustedDownloadResponse(manifestResponse);
        manifestResponse.EnsureSuccessStatusCode();
        var manifest = await ReadBoundedTextAsync(manifestResponse.Content, MaximumChecksumManifestBytes, "קובץ סכומי הביקורת", cancellationToken);
        var expectedHash = ParseChecksumManifest(manifest, InstallerFileName);

        using var response = await _http.GetAsync(update.InstallerUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureTrustedDownloadResponse(response);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumInstallerBytes)
            throw new InvalidDataException("קובץ מתקין העדכון גדול מהמותר.");

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NodeBB SSH Manager", "Updates");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"NodeBB-SSH-Manager-Setup-{update.Version}.exe");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";

        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072];
                long totalBytes = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > MaximumInstallerBytes)
                        throw new InvalidDataException("קובץ מתקין העדכון גדול מהמותר.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
                if (output.Length <= 0)
                    throw new InvalidDataException("קובץ מתקין העדכון ריק.");
            }

            byte[] actualHash;
            await using (var verifyStream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
                actualHash = await SHA256.HashDataAsync(verifyStream, cancellationToken);
            var expectedBytes = Convert.FromHexString(expectedHash);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedBytes))
                throw new CryptographicException("אימות SHA-256 של מתקין העדכון נכשל; הקובץ לא יופעל.");

            await using (var signatureStream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 2, true))
            {
                var signature = new byte[2];
                await signatureStream.ReadExactlyAsync(signature, cancellationToken);
                if (signature[0] != (byte)'M' || signature[1] != (byte)'Z')
                    throw new InvalidDataException("הקובץ שהורד אינו מתקין Windows תקין.");
            }

            File.Move(temporary, destination, true);
            DiagnosticLogService.Instance.Info("Updates", "InstallerDownloadedAndVerified",
                $"Verified installer for version {update.Version} using SHA-256.", update.TagName);
            return destination;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static string ParseChecksumManifest(string manifest, string fileName)
    {
        if (string.IsNullOrWhiteSpace(manifest) || string.IsNullOrWhiteSpace(fileName))
            throw new InvalidDataException("קובץ סכומי הביקורת ריק או לא תקין.");

        string? match = null;
        foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var listedName = parts[^1].TrimStart('*');
            if (!string.Equals(listedName, fileName, StringComparison.Ordinal)) continue;
            if (match is not null)
                throw new InvalidDataException("קובץ סכומי הביקורת מכיל רשומה כפולה למתקין.");
            if (parts[0].Length != 64 || parts[0].Any(ch => !Uri.IsHexDigit(ch)))
                throw new InvalidDataException("ערך SHA-256 בקובץ סכומי הביקורת אינו תקין.");
            match = parts[0].ToLowerInvariant();
        }

        return match ?? throw new InvalidDataException("לא נמצא SHA-256 למתקין בקובץ SHA256SUMS.txt.");
    }

    private static Uri GetTrustedReleaseUri(JsonElement root, string tag)
    {
        if (!root.TryGetProperty("html_url", out var element) || element.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(element.GetString(), UriKind.Absolute, out var uri) || !IsTrustedGitHubUri(uri) ||
            !string.Equals(uri.AbsolutePath, $"/hershaleh/NodeBB-SSH-Manager/releases/tag/{Uri.EscapeDataString(tag)}", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("כתובת דף הגרסה אינה תואמת למאגר ולתגית הגרסה הרשמיים.");
        return uri;
    }

    private static Uri GetTrustedAssetUri(JsonElement asset, string tag, string expectedFileName)
    {
        if (!asset.TryGetProperty("browser_download_url", out var element) ||
            element.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(element.GetString(), UriKind.Absolute, out var uri) || !IsTrustedGitHubUri(uri) ||
            !string.Equals(uri.AbsolutePath,
                $"/hershaleh/NodeBB-SSH-Manager/releases/download/{Uri.EscapeDataString(tag)}/{expectedFileName}",
                StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("כתובת קובץ העדכון אינה תואמת למאגר, לתגית ולשם הקובץ הרשמיים.");
        return uri;
    }

    private static bool IsTrustedGitHubUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

    private static void EnsureTrustedApiResponse(HttpResponseMessage response)
    {
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || !finalUri.IsAbsoluteUri ||
            finalUri.Scheme != Uri.UriSchemeHttps || !finalUri.IsDefaultPort ||
            !string.IsNullOrEmpty(finalUri.UserInfo) ||
            !string.Equals(finalUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("בדיקת העדכונים הופנתה לשרת שאינו GitHub API המאושר.");
    }

    private static void EnsureTrustedDownloadResponse(HttpResponseMessage response)
    {
        var finalUri = response.RequestMessage?.RequestUri;
        var trustedHost = finalUri is not null && finalUri.IsAbsoluteUri &&
            finalUri.Scheme == Uri.UriSchemeHttps && finalUri.IsDefaultPort &&
            string.IsNullOrEmpty(finalUri.UserInfo) &&
            (string.Equals(finalUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(finalUri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(finalUri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(finalUri.Host, "github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase));
        if (!trustedHost)
            throw new InvalidDataException("השרת שאליו הופנה הורדת העדכון אינו שרת GitHub/CDN מאושר.");
    }

    private static async Task<string> ReadBoundedTextAsync(HttpContent content, int maximumBytes, string description,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException($"{description} גדול מהמותר.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException($"{description} גדול מהמותר.");
            output.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build));
}
