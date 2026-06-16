// src/Noctis/Services/UpdateService.cs
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Noctis.Services;

public sealed class UpdateService
{
    // Fetch the recent releases list (instead of /releases/latest) so pre-releases are included.
    // GitHub excludes pre-releases from /releases/latest, which would hide prerelease builds from
    // the in-app updater. We pick the highest-version release that has this platform's installer asset.
    private const string ReleaseUrl = "https://api.github.com/repos/heartached/Noctis/releases?per_page=10";

    private readonly HttpClient _http;

    public UpdateService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Current assembly version as a comparable Version object.</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Current version formatted for display, e.g. "Version 1.1.1".</summary>
    public static string CurrentVersionDisplay
    {
        get
        {
            var v = CurrentVersion;
            return $"Version {v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>
    /// True when THIS installed build is a pre-release. Detected from the
    /// assembly's informational version, which carries a SemVer pre-release
    /// suffix (e.g. "1.1.15-prerelease") set in the csproj for pre-release
    /// builds; stable builds have no suffix. Build metadata ("+sha") is ignored.
    /// This reflects the running build, not whatever the latest GitHub release is.
    /// </summary>
    public static bool IsPrereleaseBuild
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(info)) return false;
            var plus = info.IndexOf('+');          // strip "+<build metadata>"
            if (plus >= 0) info = info[..plus];
            return info.Contains('-');             // SemVer pre-release segment present
        }
    }

    // ── Install-source detection ──
    // The in-app updater on Windows always runs the Inno Setup .exe, which
    // installs to the Inno location. That's correct only when THIS copy is the
    // Inno install (also how winget/Chocolatey install — they wrap the same
    // setup and upgrade in place). A Scoop or manually-extracted (portable)
    // copy would instead get a second, parallel install, so for those we steer
    // the user to their own update path. Inno writes its uninstall entry under
    // AppId + "_is1"; per-user installs (PrivilegesRequired=lowest) land in
    // HKCU, elevated ones in HKLM.
    private const string InnoUninstallSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{E8A3B5F1-7C2D-4A9E-B6F0-1D3E5A7C9B2F}_is1";

    private static InstallSource? _cachedSource;

    /// <summary>How this running copy was installed (computed once per process).</summary>
    public static InstallSource Source => _cachedSource ??= DetectSource();

    /// <summary>
    /// True when the in-app installer is the right update mechanism: any
    /// non-Windows platform (macOS .dmg / Linux have no Inno install to clash
    /// with) or a Windows copy installed by the Inno setup. False for Scoop /
    /// portable copies, where running the setup would create a second install.
    /// </summary>
    public static bool SupportsInAppUpdate => Source == InstallSource.Installed;

    /// <summary>
    /// Short guidance for updating a package-manager / portable copy, or null
    /// when the in-app updater should be used instead.
    /// </summary>
    public static string? ExternalUpdateHint => Source switch
    {
        InstallSource.Scoop => "Update with: scoop update noctis",
        InstallSource.Portable => "Download the new version from GitHub.",
        _ => null
    };

    private static InstallSource DetectSource()
    {
        if (!OperatingSystem.IsWindows())
            return InstallSource.Installed;

        return ClassifyInstall(AppContext.BaseDirectory, TryGetInnoInstallLocation());
    }

    /// <summary>
    /// Pure classification from the running directory and the install path the
    /// Inno uninstaller recorded (null when no entry matches this AppId).
    /// Extracted so it can be unit-tested without touching the registry.
    /// </summary>
    public static InstallSource ClassifyInstall(string appDirectory, string? innoInstallLocation)
    {
        static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        var appDir = Norm(appDirectory);

        if (!string.IsNullOrEmpty(innoInstallLocation) && Norm(innoInstallLocation) == appDir)
            return InstallSource.Installed;

        // Scoop lays apps out under ...\scoop\apps\<name>\<version>\.
        if (appDir.Contains(@"\scoop\apps\"))
            return InstallSource.Scoop;

        return InstallSource.Portable;
    }

    private static string? TryGetInnoInstallLocation()
    {
        try
        {
            foreach (var root in new[]
                     {
                         Microsoft.Win32.Registry.CurrentUser,
                         Microsoft.Win32.Registry.LocalMachine
                     })
            {
                using var key = root.OpenSubKey(InnoUninstallSubKey);
                if (key?.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                    return loc;
            }
        }
        catch
        {
            // Registry unreadable — treat as "no Inno entry" (portable).
        }
        return null;
    }

    /// <summary>
    /// Checks the latest GitHub release. Returns null if up-to-date or on error.
    /// Pre-releases are only considered when <paramref name="includePrereleases"/> is true.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(bool includePrereleases = false, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseUrl);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var releases = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(ct);
        if (releases is null || releases.Count == 0)
            return null;

        // Candidate releases newer than the current build, highest version first.
        var candidates = releases
            .Where(r => !r.Draft && !string.IsNullOrEmpty(r.TagName))
            .Where(r => includePrereleases || !r.Prerelease)
            .Select(r => (Release: r, Version: ParseTag(r.TagName!)))
            .Where(x => x.Version is not null && x.Version > CurrentVersion)
            .OrderByDescending(x => x.Version)
            .ToList();

        if (candidates.Count == 0)
            return null;

        // Prefer the highest-version release that actually ships this platform's
        // installer asset, so a newer release missing the asset never masks an
        // installable one. Fall back to the highest version overall so platforms
        // without an in-app installer (Linux) still surface that an update exists
        // via the "visit GitHub" path.
        var best = candidates.FirstOrDefault(x => FindInstallerAsset(x.Release) is not null);
        if (best.Release is null)
            best = candidates[0];

        var installerAsset = FindInstallerAsset(best.Release);

        return new UpdateInfo
        {
            TagName = best.Release.TagName!,
            Version = best.Version!,
            IsPrerelease = best.Release.Prerelease,
            InstallerApiUrl = installerAsset?.Url,
            InstallerUrl = installerAsset?.BrowserDownloadUrl,
            InstallerSize = installerAsset?.Size ?? 0,
            ReleaseUrl = best.Release.HtmlUrl ?? $"https://github.com/heartached/Noctis/releases/tag/{best.Release.TagName}"
        };
    }

    /// <summary>
    /// Picks the release asset the in-app updater can install on this platform:
    /// Windows gets the Inno Setup exe ("Noctis-v1.2.3-Setup.exe"), macOS gets the
    /// per-architecture disk image ("Noctis-1.2.3-osx-arm64.dmg"). Linux has no
    /// in-app installer (AppImage/tar.gz are updated manually), so no asset matches
    /// and the UI falls back to pointing at the GitHub release page.
    /// </summary>
    private static GitHubAsset? FindInstallerAsset(GitHubRelease release)
    {
        if (release.Assets is null) return null;

        if (OperatingSystem.IsWindows())
        {
            return release.Assets.FirstOrDefault(a =>
                a.Name != null &&
                a.Name.StartsWith("Noctis-v", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase));
        }

        if (OperatingSystem.IsMacOS())
        {
            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            return release.Assets.FirstOrDefault(a =>
                a.Name != null &&
                a.Name.StartsWith("Noctis-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith($"-osx-{arch}.dmg", StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    /// <summary>
    /// Downloads the installer to %TEMP%. Reports progress 0-100.
    /// Returns the path to the downloaded file.
    /// </summary>
    public Task<string> DownloadInstallerAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (update.InstallerApiUrl is null)
            throw new InvalidOperationException("In-app updates require the GitHub release asset API URL.");

        return DownloadInstallerAsync(update.InstallerApiUrl, update.InstallerSize, progress, ct);
    }

    private async Task<string> DownloadInstallerAsync(
        string url, long expectedSize,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(),
            OperatingSystem.IsMacOS() ? "Noctis-Update.dmg" : "Noctis-Update-Setup.exe");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/octet-stream"));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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
    /// Launches the downloaded installer and returns true. Windows runs the Inno
    /// Setup exe with /SILENT; macOS opens the downloaded .dmg so the user drags
    /// the new Noctis.app over the old one. The caller should exit the app after
    /// this returns so the bundle/files can be replaced.
    /// </summary>
    public bool LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
            return false;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/SILENT",
                    UseShellExecute = true  // triggers UAC elevation prompt
                });

                if (proc is null)
                {
                    Debug.WriteLine("[UpdateService] Process.Start returned null for installer.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                // Most common cause: user declined the UAC prompt.
                Debug.WriteLine($"[UpdateService] LaunchInstaller failed: {ex.Message}");
                return false;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { installerPath },
                    UseShellExecute = false
                });
                return proc is not null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] LaunchInstaller (open .dmg) failed: {ex.Message}");
                return false;
            }
        }

        // Linux: no in-app installer; users update via AppImage/tar.gz.
        return false;
    }

    private static Version? ParseTag(string tag)
    {
        // Strip leading 'v'/'V', then any semver pre-release/build suffix
        // (e.g. "1.1.11-beta.1" or "1.1.11+build") so prerelease tags still parse.
        var raw = tag.TrimStart('v', 'V');
        int cut = raw.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) raw = raw.Substring(0, cut);
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

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

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
    /// <summary>True if GitHub marked this release as a pre-release.</summary>
    public bool IsPrerelease { get; init; }
    /// <summary>GitHub API asset URL used by the in-app updater.</summary>
    public string? InstallerApiUrl { get; init; }
    /// <summary>Browser download URL reserved for manual downloads and website links.</summary>
    public string? InstallerUrl { get; init; }
    public long InstallerSize { get; init; }
    public required string ReleaseUrl { get; init; }
}

/// <summary>How the running copy of Noctis was installed.</summary>
public enum InstallSource
{
    /// <summary>Installed by the Inno Setup installer — also how winget and
    /// Chocolatey install (they wrap the same setup). The in-app updater applies.</summary>
    Installed,
    /// <summary>Running from a Scoop-managed directory; update via <c>scoop update</c>.</summary>
    Scoop,
    /// <summary>Portable / manually-extracted copy with no installer.</summary>
    Portable
}
