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

    // Developer Mode version manager fetches the full recent history (GitHub caps per_page at 100).
    private const string ReleaseListUrl = "https://api.github.com/repos/heartached/Noctis/releases?per_page=100";

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
    /// True when the in-app installer is the right update mechanism: a Windows copy
    /// installed by the Inno setup, a macOS .dmg build, or a Linux AppImage (which can
    /// swap itself in place). False for Scoop / portable / tar.gz copies, where the
    /// in-app installer would create a second, parallel install.
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
        if (OperatingSystem.IsWindows())
            return ClassifyInstall(AppContext.BaseDirectory, TryGetInnoInstallLocation());

        // Linux: only an AppImage launch can self-update (the updater swaps the single
        // AppImage file, whose path the runtime exposes in $APPIMAGE). A tar.gz /
        // manually-extracted copy has no single artifact to replace, so treat it as
        // portable and steer the user to GitHub.
        if (OperatingSystem.IsLinux())
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            return !string.IsNullOrEmpty(appImage) && File.Exists(appImage)
                ? InstallSource.Installed
                : InstallSource.Portable;
        }

        // macOS .dmg drag-install always applies.
        return InstallSource.Installed;
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

        var releasesJson = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
        var releases = System.Text.Json.JsonSerializer.Deserialize<List<GitHubRelease>>(releasesJson);
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
        var checksumsAsset = FindChecksumsAsset(best.Release);

        return new UpdateInfo
        {
            TagName = best.Release.TagName!,
            Version = best.Version!,
            IsPrerelease = best.Release.Prerelease,
            InstallerApiUrl = installerAsset?.Url,
            InstallerUrl = installerAsset?.BrowserDownloadUrl,
            InstallerSize = installerAsset?.Size ?? 0,
            InstallerAssetName = installerAsset?.Name,
            ChecksumsApiUrl = checksumsAsset?.Url,
            ReleaseUrl = best.Release.HtmlUrl ?? $"https://github.com/heartached/Noctis/releases/tag/{best.Release.TagName}"
        };
    }

    /// <summary>
    /// Lists recent GitHub releases for the Developer Mode version manager,
    /// newest first. Each entry carries the same verified-download fields the
    /// updater uses, so any listed version can be installed via
    /// <see cref="DownloadInstallerAsync(UpdateInfo, IProgress{double}?, CancellationToken)"/>.
    /// </summary>
    public async Task<List<ReleaseListItem>> ListReleasesAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseListUrl);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var releasesJson = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
        var releases = System.Text.Json.JsonSerializer.Deserialize<List<GitHubRelease>>(releasesJson);
        if (releases is null) return new List<ReleaseListItem>();

        var items = new List<ReleaseListItem>();
        foreach (var release in releases.Where(r => !r.Draft && !string.IsNullOrEmpty(r.TagName)))
        {
            var version = ParseTag(release.TagName!);
            if (version is null) continue;

            var installerAsset = FindInstallerAsset(release);
            var checksumsAsset = FindChecksumsAsset(release);
            var releaseUrl = release.HtmlUrl ?? $"https://github.com/heartached/Noctis/releases/tag/{release.TagName}";

            items.Add(new ReleaseListItem
            {
                Version = version,
                PublishedAt = release.PublishedAt,
                WarningText = ExtractReleaseWarning(release.Body),
                Info = new UpdateInfo
                {
                    TagName = release.TagName!,
                    Version = version,
                    IsPrerelease = release.Prerelease,
                    InstallerApiUrl = installerAsset?.Url,
                    InstallerUrl = installerAsset?.BrowserDownloadUrl,
                    InstallerSize = installerAsset?.Size ?? 0,
                    InstallerAssetName = installerAsset?.Name,
                    ChecksumsApiUrl = checksumsAsset?.Url,
                    ReleaseUrl = releaseUrl
                }
            });
        }

        return items.OrderByDescending(i => i.Version).ToList();
    }

    /// <summary>
    /// Pulls the warning text out of a GitHub release body's "[!WARNING]" admonition
    /// (the blockquote lines following the marker), so releases flagged in their notes
    /// — e.g. v1.2.0's startup crash — surface a warning in the version manager
    /// without hardcoding version numbers. The result is trimmed for a one-line UI:
    /// markdown is stripped and only the first sentence is kept.
    /// Returns null when the notes carry no warning.
    /// </summary>
    internal static string? ExtractReleaseWarning(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;

        var lines = body.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("[!WARNING]", StringComparison.OrdinalIgnoreCase)) continue;

            var text = new List<string>();
            for (int j = i + 1; j < lines.Length; j++)
            {
                var line = lines[j].Trim();
                if (!line.StartsWith('>')) break;
                var content = line.TrimStart('>', ' ').Trim();
                if (content.Length > 0) text.Add(content);
            }

            return text.Count > 0
                ? ShortenWarning(string.Join(" ", text))
                : "This release has a known issue — see the release notes.";
        }
        return null;
    }

    /// <summary>Strips markdown links/emphasis and truncates to the first sentence.</summary>
    private static string ShortenWarning(string text)
    {
        // "[label](url)" → "label", drop bold/code markers.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
        text = text.Replace("**", "").Replace("`", "").Trim();

        // First sentence only. A period counts as a sentence end only when followed
        // by whitespace or end-of-text, so version numbers like "1.2.1" don't cut it.
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?' &&
                (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
                return text[..(i + 1)];
        }
        return text;
    }

    /// <summary>
    /// Picks the release asset the in-app updater can install on this platform:
    /// Windows gets the Inno Setup exe ("Noctis-Setup.exe"), macOS gets the
    /// per-architecture disk image ("Noctis-osx-arm64.dmg"), and Linux x64 gets the
    /// AppImage ("Noctis-x86_64.AppImage"), which the updater swaps in place. Linux
    /// arm64 ships only a tar.gz (no AppImage), so no asset matches and the UI falls
    /// back to pointing at the GitHub release page.
    /// </summary>
    private static GitHubAsset? FindInstallerAsset(GitHubRelease release)
    {
        if (release.Assets is null) return null;

        if (OperatingSystem.IsWindows())
        {
            return release.Assets.FirstOrDefault(a =>
                a.Name != null &&
                a.Name.StartsWith("Noctis-", StringComparison.OrdinalIgnoreCase) &&
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

        if (OperatingSystem.IsLinux())
        {
            // Only x64 ships a self-contained AppImage the updater can swap in place;
            // arm64 ships a tar.gz only, so it falls back to the "visit GitHub" path.
            if (RuntimeInformation.OSArchitecture != Architecture.X64)
                return null;

            // Pinned to the exact CI asset name so a future release shipping multiple
            // AppImages can't ambiguously match the wrong one.
            return release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, "Noctis-x86_64.AppImage", StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    /// <summary>
    /// Downloads the installer to %TEMP%. Reports progress 0-100.
    /// Returns the path to the downloaded file.
    /// </summary>
    /// <summary>
    /// Locates a SHA-256 checksums manifest asset on the release, if one is published
    /// (e.g. "SHA256SUMS" or "checksums.txt"). When present, the downloaded installer is
    /// verified against it before launch; when absent, the updater falls back to size-only.
    /// </summary>
    private static GitHubAsset? FindChecksumsAsset(GitHubRelease release)
        => release.Assets?.FirstOrDefault(a =>
            a.Name != null &&
            (a.Name.Contains("SHA256", StringComparison.OrdinalIgnoreCase) ||
             a.Name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase)));

    /// <param name="update">Release to download.</param>
    /// <param name="progress">Receives 0-100 download progress.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <param name="destinationPath">Where to save the installer; defaults to the
    /// updater's fixed temp path. The Developer Mode version manager passes the
    /// user's Downloads folder here.</param>
    public Task<string> DownloadInstallerAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        string? destinationPath = null)
    {
        if (update.InstallerApiUrl is null)
            throw new InvalidOperationException("In-app updates require the GitHub release asset API URL.");

        return DownloadInstallerAsync(
            update.InstallerApiUrl, update.InstallerSize,
            update.ChecksumsApiUrl, update.InstallerAssetName, progress, ct, destinationPath);
    }

    private async Task<string> DownloadInstallerAsync(
        string url, long expectedSize,
        string? checksumsUrl, string? assetName,
        IProgress<double>? progress,
        CancellationToken ct,
        string? destinationPath = null)
    {
        // The installer is launched with elevation, so only ever pull it from GitHub over
        // HTTPS — never from a host smuggled into a tampered API response.
        if (!IsTrustedGitHubUrl(url))
            throw new InvalidOperationException("Refusing to download an update from an untrusted (non-GitHub) URL.");

        var tempPath = destinationPath ?? Path.Combine(Path.GetTempPath(),
            OperatingSystem.IsMacOS() ? "Noctis-Update.dmg"
            : OperatingSystem.IsLinux() ? "Noctis-Update.AppImage"
            : "Noctis-Update-Setup.exe");

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
            await fileStream.DisposeAsync(); // release the handle before validating / hashing

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

            // Hash verification: when the release ships a checksums manifest, verify the
            // installer's SHA-256 before it is ever launched with elevation. Fail closed if
            // the manifest is present but lacks (or contradicts) this file's entry. Releases
            // without a manifest fall back to the size check above.
            if (!string.IsNullOrEmpty(checksumsUrl))
            {
                if (!IsTrustedGitHubUrl(checksumsUrl))
                    throw new InvalidOperationException("Refusing to fetch update checksums from an untrusted URL.");

                var manifest = await DownloadTextAsync(checksumsUrl, ct);
                var expectedHash = ParseSha256FromChecksums(manifest, assetName ?? Path.GetFileName(tempPath));
                var actualHash = await ComputeSha256Async(tempPath, ct);

                if (expectedHash == null || !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempPath);
                    throw new InvalidOperationException(
                        "Update failed SHA-256 verification — the download may be incomplete or tampered.");
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
            // A bundle launched from Finder/LaunchServices usually inherits little or no
            // PATH, so a bare "open" can't be resolved and Process.Start throws — use the
            // absolute path. Fall back to shell-execute (also routed through LaunchServices'
            // `open`) if the direct spawn fails for any reason.
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    ArgumentList = { installerPath },
                    UseShellExecute = false
                });
                if (proc is not null)
                    return true;
                Debug.WriteLine("[UpdateService] /usr/bin/open returned null.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] LaunchInstaller (/usr/bin/open .dmg) failed: {ex.Message}");
            }

            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true   // macOS routes this through LaunchServices (`open`)
                });
                return proc is not null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] LaunchInstaller (shell-execute .dmg) failed: {ex.Message}");
                return false;
            }
        }

        if (OperatingSystem.IsLinux())
        {
            // AppImage self-update: the running file's absolute path is exposed in
            // $APPIMAGE. Replace it with the freshly downloaded (already size/SHA-256
            // verified) build and relaunch. A detached shell waits for THIS process to
            // exit first so the file isn't swapped mid-run — mirroring how the Windows
            // installer waits for the app to close before replacing it.
            var target = Environment.GetEnvironmentVariable("APPIMAGE");
            if (string.IsNullOrEmpty(target) || !File.Exists(target))
            {
                Debug.WriteLine("[UpdateService] Not running as an AppImage ($APPIMAGE unset); cannot self-update.");
                return false;
            }

            try
            {
                // Single-quote paths for the shell, escaping any embedded single quotes.
                static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

                var pid = Environment.ProcessId;
                var script =
                    $"while kill -0 {pid} 2>/dev/null; do sleep 0.2; done; " +
                    $"mv -f {Sh(installerPath)} {Sh(target)} && chmod +x {Sh(target)} && " +
                    $"nohup {Sh(target)} >/dev/null 2>&1 &";

                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    ArgumentList = { "-c", script },
                    UseShellExecute = false
                });
                return proc is not null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] LaunchInstaller (AppImage swap) failed: {ex.Message}");
                return false;
            }
        }

        // Unknown platform — no in-app installer.
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

    // ── Security helpers ──

    /// <summary>
    /// True only for HTTPS URLs whose host is GitHub (or a GitHub asset CDN). The in-app
    /// updater downloads and then launches an elevated installer, so every URL it fetches
    /// must be pinned to GitHub — never a host smuggled in via a tampered API response.
    /// </summary>
    internal static bool IsTrustedGitHubUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the expected lowercase SHA-256 for <paramref name="fileName"/> from a
    /// standard sha256sum-format manifest ("&lt;hex&gt;  &lt;name&gt;" or "&lt;hex&gt; *&lt;name&gt;").
    /// Returns null when no line matches that file name.
    /// </summary>
    internal static string? ParseSha256FromChecksums(string? content, string? fileName)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(fileName)) return null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var sep = line.IndexOfAny(new[] { ' ', '\t' });
            if (sep <= 0) continue;

            var hash = line[..sep].Trim();
            var name = line[(sep + 1)..].TrimStart('*', ' ', '\t').Trim();

            if (hash.Length == 64 && hash.All(Uri.IsHexDigit) &&
                name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return hash.ToLowerInvariant();
        }
        return null;
    }

    private async Task<string> DownloadTextAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
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

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
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
    /// <summary>Installer asset file name, used to match its line in the checksums manifest.</summary>
    public string? InstallerAssetName { get; init; }
    /// <summary>GitHub API asset URL of the SHA-256 checksums manifest, when the release publishes one.</summary>
    public string? ChecksumsApiUrl { get; init; }
    public required string ReleaseUrl { get; init; }
}

/// <summary>One release row in the Developer Mode version manager.</summary>
public sealed class ReleaseListItem
{
    public required Version Version { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    /// <summary>Warning pulled from the release notes' "[!WARNING]" admonition, if any.</summary>
    public string? WarningText { get; init; }
    /// <summary>Download/install fields, shaped like a normal update so the
    /// existing verified download + launch pipeline applies unchanged.</summary>
    public required UpdateInfo Info { get; init; }
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
