// src/Noctis/Services/UpdateService.cs
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Noctis.Services;

public sealed class UpdateService
{
    private const string ReleaseUrl = "https://api.github.com/repos/heartached/Noctis/releases/latest";
    private const string AssetPrefix = "Noctis-v";
    private const string AssetSuffix = "-Setup.exe";

    private readonly HttpClient _http;

    public UpdateService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Current assembly version as a comparable Version object.</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Current version formatted for display, e.g. "Version 1.0.2".</summary>
    public static string CurrentVersionDisplay
    {
        get
        {
            var v = CurrentVersion;
            return $"Version {v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>
    /// Checks the latest GitHub release. Returns null if up-to-date or on error.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseUrl);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(ct);
        if (release is null || string.IsNullOrEmpty(release.TagName))
            return null;

        // Parse tag like "v1.0.3" -> Version(1, 0, 3)
        var tagVersion = ParseTag(release.TagName);
        if (tagVersion is null || tagVersion <= CurrentVersion)
            return null;

        // Find the Setup.exe asset
        var installerAsset = release.Assets?.FirstOrDefault(a =>
            a.Name != null &&
            a.Name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase));

        return new UpdateInfo
        {
            TagName = release.TagName,
            Version = tagVersion,
            InstallerUrl = installerAsset?.BrowserDownloadUrl,
            InstallerSize = installerAsset?.Size ?? 0,
            ReleaseUrl = release.HtmlUrl ?? $"https://github.com/heartached/Noctis/releases/tag/{release.TagName}"
        };
    }

    /// <summary>
    /// Downloads the installer to %TEMP%. Reports progress 0-100.
    /// Returns the path to the downloaded file.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        string url, long expectedSize,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "Noctis-Update-Setup.exe");

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

            var buffer = new byte[81920];
            long bytesRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                bytesRead += read;

                if (totalBytes > 0)
                    progress?.Report((double)bytesRead / totalBytes * 100.0);
            }

            await fileStream.FlushAsync(ct);

            // Validate file size if GitHub reported one
            if (expectedSize > 0)
            {
                var actualSize = new FileInfo(tempPath).Length;
                if (actualSize != expectedSize)
                {
                    File.Delete(tempPath);
                    throw new InvalidOperationException(
                        $"Download corrupted: expected {expectedSize} bytes, got {actualSize}.");
                }
            }

            return tempPath;
        }
        catch
        {
            // Clean up partial download
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Launches the downloaded Inno Setup installer with /SILENT and returns true.
    /// The caller should exit the app after this returns.
    /// </summary>
    public bool LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
            return false;

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/SILENT",
            UseShellExecute = true  // triggers UAC elevation prompt
        });

        return true;
    }

    private static Version? ParseTag(string tag)
    {
        // Strip leading 'v' or 'V'
        var raw = tag.TrimStart('v', 'V');
        return Version.TryParse(raw, out var v) ? v : null;
    }

    // ── GitHub API DTOs ──

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

public sealed class UpdateInfo
{
    public required string TagName { get; init; }
    public required Version Version { get; init; }
    public string? InstallerUrl { get; init; }
    public long InstallerSize { get; init; }
    public required string ReleaseUrl { get; init; }
}
