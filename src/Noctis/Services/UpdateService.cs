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

    /// <summary>Null = <see cref="ResumableDownload.Options.Default"/>; tests shorten the retry delays.</summary>
    internal ResumableDownload.Options? DownloadOptions { get; set; }

    public UpdateService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Current assembly version as a comparable Version object.</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Current version formatted for display, e.g. "Version 1.1.1".</summary>
    /// <summary>Header lines for the session log: product + version, then install source.</summary>
    public static string DescribeBuild()
    {
        var v = CurrentVersion;
        return $"Noctis {v.Major}.{v.Minor}.{v.Build}" + (IsPrereleaseBuild ? " (pre-release)" : "")
             + $"\nInstall source: {Source}";
    }

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
    /// installed by the Inno setup, a macOS .dmg build, a Linux AppImage (which swaps
    /// itself in place), or a user-writable Linux tar.gz copy (which extracts the new
    /// build over itself). False for Scoop / Windows-portable / non-writable copies,
    /// where the in-app installer would create a second, parallel install or fail.
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

#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    private static AutoUpdateMode? _autoMode;

    /// <summary>What "Update automatically" may do on this copy (computed once per process):
    /// install at next launch, download only (macOS), or nothing (toggle hidden).</summary>
    public static AutoUpdateMode AutoMode => AutoModeOverride ?? (_autoMode ??= ResolveAutoMode());

    /// <summary>Tests only: the Debug builds tests run resolve <see cref="AutoMode"/> to Off.</summary>
    internal static AutoUpdateMode? AutoModeOverride { get; set; }

    private static AutoUpdateMode ResolveAutoMode()
    {
        var appImage = OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("APPIMAGE") : null;
        var isAppImage = !string.IsNullOrEmpty(appImage) && File.Exists(appImage);
        var appImageDirWritable = isAppImage && IsDirectoryWritable(Path.GetDirectoryName(appImage) ?? "/");
        return AutoUpdatePolicy.ResolveMode(
            OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(),
            Source, isAppImage, appImageDirWritable,
            RuntimeInformation.OSArchitecture == Architecture.X64, IsDebugBuild);
    }

    private static InstallSource DetectSource()
    {
        if (OperatingSystem.IsWindows())
            return ClassifyInstall(AppContext.BaseDirectory, TryGetInnoInstallLocation());

        // Linux: an AppImage launch self-updates by swapping the single AppImage file
        // (path exposed in $APPIMAGE). Everything else is a tar.gz / manually-extracted
        // copy — the CI tarballs are flat archives of the publish folder, so the updater
        // can extract the new build straight over BaseDirectory, provided this user can
        // actually write there (an extract into e.g. /opt owned by root must keep
        // steering to GitHub instead of failing the apply script halfway).
        if (OperatingSystem.IsLinux())
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrEmpty(appImage) && File.Exists(appImage))
                return InstallSource.Installed;

            return IsDirectoryWritable(AppContext.BaseDirectory)
                ? InstallSource.Installed
                : InstallSource.Portable;
        }

        // macOS .dmg drag-install always applies.
        return InstallSource.Installed;
    }

    /// <summary>Probes write access by creating (and auto-deleting) a zero-byte file —
    /// permission bits alone can't answer this on Unix (ACLs, read-only mounts, sandboxes).</summary>
    private static bool IsDirectoryWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".noctis-write-probe-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
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
            ReleaseUrl = best.Release.HtmlUrl ?? $"https://github.com/heartached/Noctis/releases/tag/{best.Release.TagName}",
            PublishedAt = best.Release.PublishedAt,
            WarningText = ExtractReleaseWarning(best.Release.Body)
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
    /// Picks the release that wears the version manager's "Latest" pill: the
    /// highest-version non-prerelease, matching GitHub's own "Latest" badge
    /// (which never sits on a pre-release). Returns null when every release is
    /// a pre-release — GitHub shows no badge then either.
    /// </summary>
    internal static ReleaseListItem? PickLatestRelease(IEnumerable<ReleaseListItem> releases) =>
        releases.Where(r => !r.Info.IsPrerelease).MaxBy(r => r.Version);

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
    /// per-architecture disk image ("Noctis-osx-arm64.dmg"). Linux picks by how this
    /// copy runs: an AppImage launch gets "Noctis-x86_64.AppImage" (swapped in place;
    /// x64 only — CI packages no arm64 AppImage), any other launch gets the flat
    /// per-architecture tarball ("Noctis-linux-x64.tar.gz" / "Noctis-linux-arm64.tar.gz")
    /// that the updater extracts over the install directory.
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
            // An AppImage launch updates by swapping the AppImage file (x64 is the only
            // arch CI packages one for). Every other Linux copy updates by extracting
            // the matching flat tarball over itself — which is also the only update
            // path that exists on arm64.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE")))
            {
                if (RuntimeInformation.OSArchitecture != Architecture.X64)
                    return null;

                // Pinned to the exact CI asset name so a future release shipping multiple
                // AppImages can't ambiguously match the wrong one.
                return release.Assets.FirstOrDefault(a =>
                    string.Equals(a.Name, "Noctis-x86_64.AppImage", StringComparison.OrdinalIgnoreCase));
            }

            var tarball = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "Noctis-linux-x64.tar.gz",
                Architecture.Arm64 => "Noctis-linux-arm64.tar.gz",
                _ => null
            };
            if (tarball is null)
                return null;

            return release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, tarball, StringComparison.OrdinalIgnoreCase));
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
    /// <summary>Temp-file extension for the Linux download — must match what
    /// FindInstallerAsset selected: AppImage when running as one, tarball otherwise.</summary>
    private static string LinuxInstallerExtension()
        => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE")) ? ".tar.gz" : ".AppImage";

    private static GitHubAsset? FindChecksumsAsset(GitHubRelease release)
        => release.Assets?.FirstOrDefault(a =>
            a.Name != null &&
            (a.Name.Contains("SHA256", StringComparison.OrdinalIgnoreCase) ||
             a.Name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase)));

    /// <param name="update">Release to download.</param>
    /// <param name="progress">Receives 0-100 download progress.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <param name="destinationPath">Where to save the installer; defaults to a new
    /// file in <see cref="InstallerDirectory"/>. The Developer Mode version manager passes the
    /// user's Downloads folder here.</param>
    public Task<string> DownloadInstallerAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        string? destinationPath = null,
        bool requireChecksums = false)
    {
        if (update.InstallerApiUrl is null)
            throw new InvalidOperationException("In-app updates require the GitHub release asset API URL.");

        return DownloadInstallerAsync(
            update.InstallerApiUrl, update.InstallerSize,
            update.ChecksumsApiUrl, update.InstallerAssetName, progress, ct,
            requireChecksums, destinationPath);
    }

    /// <summary>
    /// Where installer downloads land. Linux: a private (0700) folder in the user's data dir, because
    /// /tmp is shared with every local user (a vanished queued file could be recreated there by someone
    /// else) and tmpfs distros empty it at every boot, which lost a queued update before the next launch.
    /// Windows and macOS: the per-user temp folder, as before.
    /// </summary>
    internal static string InstallerDirectory => OperatingSystem.IsLinux()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Noctis", "updates")
        : Path.GetTempPath();

    private async Task<string> DownloadInstallerAsync(
        string url, long expectedSize,
        string? checksumsUrl, string? assetName,
        IProgress<double>? progress,
        CancellationToken ct,
        bool requireChecksums,
        string? destinationPath = null)
    {
        // The installer is launched with elevation, so only ever pull it from GitHub over
        // HTTPS — never from a host smuggled into a tampered API response.
        if (!IsTrustedGitHubUrl(url))
            throw new InvalidOperationException("Refusing to download an update from an untrusted (non-GitHub) URL.");

        // Normal updates fail closed without a SHA256SUMS manifest (every release
        // ships one per the release process) — otherwise integrity silently
        // degrades to a size-only check. The dev Version Manager opts out so
        // pre-manifest releases stay installable.
        if (requireChecksums && string.IsNullOrEmpty(checksumsUrl))
            throw new InvalidOperationException(
                "This release has no SHA256SUMS manifest — refusing to install an unverifiable update.");

        // Installable downloads also pin the repo, not just the host: IsTrustedGitHubUrl accepts
        // any GitHub repo's assets, and an unattended install must only ever run our own releases.
        if (requireChecksums && (!IsConfiguredRepoAssetUrl(url) || !IsConfiguredRepoAssetUrl(checksumsUrl)))
            throw new InvalidOperationException("Refusing an update asset from outside heartached/Noctis.");

        if (destinationPath is null && OperatingSystem.IsLinux())
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(InstallerDirectory, ownerOnly);
            File.SetUnixFileMode(InstallerDirectory, ownerOnly); // a folder that already existed too
        }

        // Random per-run filename: a fixed predictable path invited a same-user
        // verify-then-launch swap (TOCTOU) on the elevated installer.
        var runTag = Guid.NewGuid().ToString("N")[..8];
        var tempPath = destinationPath ?? Path.Combine(InstallerDirectory,
            OperatingSystem.IsMacOS() ? $"Noctis-Update-{runTag}.dmg"
            : OperatingSystem.IsLinux() ? $"Noctis-Update-{runTag}{LinuxInstallerExtension()}"
            : $"Noctis-Update-{runTag}-Setup.exe");

        try
        {
            // Retried and resumed (HTTP Range) within this call when the connection drops or
            // stalls; the size + SHA-256 checks below still gate the finished file.
            await ResumableDownload.DownloadAsync(
                _http,
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Accept.Add(
                        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/octet-stream"));
                    return request;
                },
                tempPath,
                resumeExisting: false,
                (done, total) =>
                {
                    var totalBytes = total > 0 ? total : expectedSize;
                    if (totalBytes > 0)
                        progress?.Report((double)done / totalBytes * 100.0);
                },
                "Update.Installer",
                ct,
                DownloadOptions);

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
            // Single-quote paths for the shell, escaping any embedded single quotes.
            static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";
            var pid = Environment.ProcessId;

            // AppImage self-update: the running file's absolute path is exposed in
            // $APPIMAGE. Replace it with the freshly downloaded (already size/SHA-256
            // verified) build and relaunch. A detached shell waits for THIS process to
            // exit first so the file isn't swapped mid-run — mirroring how the Windows
            // installer waits for the app to close before replacing it.
            var target = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrEmpty(target) && File.Exists(target))
            {
                try
                {
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

            // Flat tar.gz install: extract the verified tarball straight over this copy
            // once the process has exited, then relaunch the new binary. CI builds these
            // archives with `tar -C publish/<rid> .`, so entries sit at the archive root
            // and land directly in BaseDirectory. DetectSource has already probed the
            // directory for write access before any UI offered this path.
            var installDir = AppContext.BaseDirectory.TrimEnd('/');
            var exe = Path.Combine(installDir, "Noctis");
            if (!File.Exists(exe))
            {
                Debug.WriteLine("[UpdateService] tar.gz self-update: no Noctis binary beside this process; cannot self-update.");
                return false;
            }

            try
            {
                var script =
                    $"while kill -0 {pid} 2>/dev/null; do sleep 0.2; done; " +
                    $"tar -xzf {Sh(installerPath)} -C {Sh(installDir)} && rm -f {Sh(installerPath)} && " +
                    $"chmod +x {Sh(exe)} && nohup {Sh(exe)} >/dev/null 2>&1 &";

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
                Debug.WriteLine($"[UpdateService] LaunchInstaller (tar.gz extract) failed: {ex.Message}");
                return false;
            }
        }

        // Unknown platform — no in-app installer.
        return false;
    }

    // ── Automatic updates (opt-in) ──

    /// <summary>Set at launch when this process is the version a queued auto-update installed.</summary>
    public static PendingInstall? CompletedAutoUpdate { get; private set; }

    /// <summary>Set at launch when a queued auto-update was given up; About shows it.</summary>
    public static string? LaunchInstallNote { get; private set; }

    /// <summary>
    /// Queues a verified download for the next launch (or, on macOS, for Install &amp; Restart).
    /// The file has just passed the SHA256SUMS check, so the hash recorded here is the verified
    /// one; the launch install re-hashes the file and refuses anything that differs.
    /// </summary>
    public async Task ScheduleAutoInstallAsync(UpdateInfo update, string installerPath, AutoUpdateStore store)
    {
        var sha = await ComputeSha256Async(installerPath, CancellationToken.None);

        var state = store.Load() ?? new AutoUpdateState();
        if (state.Pending is { } older && older.InstallerPath != installerPath && IsOwnedInstallerFile(older.InstallerPath))
        {
            try { File.Delete(older.InstallerPath); } catch { /* best effort */ }
        }

        if (state.Tag != update.TagName)
        {
            // A new release starts its own count. The same release keeps it across re-queues, so one
            // whose installer never starts (or whose file keeps vanishing) still ends up blocked.
            state.Tag = update.TagName;
            state.Failures = 0;
            state.LastAttemptUtc = null;
            state.Blocked = false;
        }
        state.Pending = new PendingInstall
        {
            Tag = update.TagName,
            FromVersion = CurrentVersion.ToString(3),
            ToVersion = update.Version.ToString(3),
            InstallerPath = installerPath,
            Sha256 = sha,
            IsPrerelease = update.IsPrerelease,
            ReleaseUrl = update.ReleaseUrl,
            DownloadedUtc = DateTimeOffset.UtcNow,
            LaunchAttempts = 0
        };
        store.Save(state);

        DebugLog.Write("Updater", $"Auto-update: {update.TagName} verified (sha256 {sha[..12]}), " +
            (AutoMode == AutoUpdateMode.InstallAtLaunch ? "installs at next launch." : "waiting for Install & Restart."));
    }

    /// <summary>
    /// Program.Main, before the audio engine or any window exists: installs a queued, verified
    /// update and returns true (the caller then exits; the installer relaunches Noctis), or
    /// settles the queue (completed / discarded / postponed) and returns false. Never throws.
    /// </summary>
    public bool TryInstallPendingUpdateAtLaunch(bool hasFilesToOpen) =>
        TryInstallPendingUpdateAtLaunch(hasFilesToOpen, new AutoUpdateStore(Noctis.Helpers.AppPaths.DataRoot));

    /// <param name="modeOverride">Tests only; the app uses <see cref="AutoMode"/>, resolved only
    /// when something is queued so an ordinary launch pays for one missing-file check.</param>
    internal bool TryInstallPendingUpdateAtLaunch(bool hasFilesToOpen, AutoUpdateStore store, AutoUpdateMode? modeOverride = null)
    {
        try
        {
            var state = store.Load();
            if (state?.Pending is not { } pending) return false;

            var mode = modeOverride ?? AutoMode;
            var path = pending.InstallerPath;
            var owned = IsOwnedInstallerFile(path);
            var now = DateTimeOffset.UtcNow;
            var action = AutoUpdatePolicy.DecideLaunchAction(pending, CurrentVersion, mode, hasFilesToOpen, owned, now, out var reason);

            switch (action)
            {
                case LaunchAction.Completed:
                    CompletedAutoUpdate = pending;
                    // Windows: the Inno loader may still hold its Setup.exe; the VM deletes it later.
                    if (owned) state.StaleInstallerPath = path;
                    state.Pending = null;
                    state.Failures = 0;
                    store.Save(state);
                    DebugLog.Write("Updater", $"Auto-update: now running {CurrentVersion.ToString(3)} (from {pending.FromVersion}).");
                    return false;

                case LaunchAction.Discard:
                    var exhausted = pending.LaunchAttempts >= AutoUpdatePolicy.MaxLaunchAttempts;
                    // A file that vanished (temp cleanup) counts as a failed download, so one that
                    // keeps vanishing ends up blocked instead of re-downloading after every launch.
                    var vanished = !owned && mode != AutoUpdateMode.Off;
                    if (vanished) state = AutoUpdatePolicy.RecordDownloadFailure(state, pending.Tag, now, hard: false);
                    DiscardPending(store, state, pending, owned, reason, block: exhausted, notify: exhausted || (vanished && state.Blocked));
                    return false;

                case LaunchAction.Postponed:
                    DebugLog.Write("Updater", $"Auto-update: install postponed until {pending.PostponedUntilUtc?.LocalDateTime:g}.");
                    return false;

                case LaunchAction.None:
                    DebugLog.Write("Updater", $"Auto-update: launch install skipped ({reason}).");
                    return false;
            }

            // Install. Re-verify the exact bytes about to run against the hash recorded after the
            // manifest check, so nothing swapped in since the download can execute.
            string actual;
            using (var fs = File.OpenRead(path))
                actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
            if (!string.Equals(actual, pending.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                DiscardPending(store, state, pending, owned: true, "failed SHA-256 re-check", block: true, notify: true);
                return false;
            }

            // Counted before the launch, so an installer that keeps failing can't retry forever.
            pending.LaunchAttempts++;
            pending.LastLaunchUtc = now;
            store.Save(state);
            DebugLog.Write("Updater",
                $"Auto-update: installing {pending.Tag} at launch (attempt {pending.LaunchAttempts}, from {CurrentVersion.ToString(3)}).");

            if (LaunchInstaller(path)) return true;

            DebugLog.Write("Updater", $"Auto-update: installer for {pending.Tag} did not start.");
            state = AutoUpdatePolicy.RecordDownloadFailure(state, pending.Tag, now, hard: false);
            DiscardPending(store, state, pending, owned: true, "installer did not start", block: false, notify: true);
            return false;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Updater", ex);
            return false;
        }
    }

    private static void DiscardPending(
        AutoUpdateStore store, AutoUpdateState state, PendingInstall pending,
        bool owned, string reason, bool block, bool notify)
    {
        if (owned)
        {
            try { File.Delete(pending.InstallerPath); } catch { /* best effort */ }
        }
        state.Pending = null;
        if (block)
        {
            state.Tag = pending.Tag;
            state.Blocked = true;
        }
        if (notify)
            LaunchInstallNote = $"Automatic install of {pending.Tag} didn't complete. Use the Update button.";
        store.Save(state);
        DebugLog.Write("Updater", $"Auto-update: pending {pending.Tag} discarded ({reason}).");
    }

    /// <summary>Deletes the installer a completed auto-update left in temp. While the Inno loader
    /// still holds it the delete fails and the path stays recorded for the next launch. On Linux
    /// it also clears downloads earlier sessions left in <see cref="InstallerDirectory"/>, which
    /// nothing else empties (one never installed, or kept for Install &amp; Restart and not used).</summary>
    public static void DeleteStaleInstaller(AutoUpdateStore store)
    {
        try
        {
            var state = store.Load();
            if (OperatingSystem.IsLinux() && Directory.Exists(InstallerDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(InstallerDirectory))
                {
                    if (file != state?.Pending?.InstallerPath && IsOwnedInstallerFile(file))
                    {
                        try { File.Delete(file); } catch { /* best effort */ }
                    }
                }
            }

            if (state?.StaleInstallerPath is not { } stale) return;
            if (AutoUpdatePolicy.IsOwnedUpdateFile(stale, InstallerDirectory) && File.Exists(stale))
                File.Delete(stale);
            state.StaleInstallerPath = null;
            store.Save(state);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Updater", $"Auto-update: leftover installer not deleted yet ({ex.GetType().Name}).");
        }
    }

    /// <summary>A queued installer may be hashed, launched or deleted only when it is one of the
    /// updater's own temp files and a regular file (never a symlink or other reparse point).</summary>
    internal static bool IsOwnedInstallerFile(string? path)
    {
        if (!AutoUpdatePolicy.IsOwnedUpdateFile(path, InstallerDirectory)) return false;
        try
        {
            var info = new FileInfo(path!);
            return info.Exists
                && (info.Attributes & FileAttributes.ReparsePoint) == 0
                && info.LinkTarget is null;
        }
        catch
        {
            return false;
        }
    }

    internal static Version? ParseTag(string tag)
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

    private const string RepoAssetPathPrefix = "/repos/heartached/Noctis/releases/assets/";

    /// <summary>
    /// True only for this repo's release-asset API URL
    /// (https://api.github.com/repos/heartached/Noctis/releases/assets/&lt;id&gt;, no query):
    /// what CheckForUpdateAsync reads from the pinned releases endpoint. Checked on top of
    /// <see cref="IsTrustedGitHubUrl"/> for every download that may be installed.
    /// </summary>
    internal static bool IsConfiguredRepoAssetUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return false;
        if (!uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.Query.Length > 0) return false;

        var path = uri.AbsolutePath;
        if (!path.StartsWith(RepoAssetPathPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var id = path[RepoAssetPathPrefix.Length..];
        return id.Length > 0 && id.All(char.IsAsciiDigit);
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
    /// <summary>When GitHub published the release; auto-update waits out a soak period from here.</summary>
    public DateTimeOffset? PublishedAt { get; init; }
    /// <summary>Warning from the release notes' "[!WARNING]" admonition; such releases never auto-install.</summary>
    public string? WarningText { get; init; }
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
