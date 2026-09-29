// src/Noctis/Services/AutoUpdatePolicy.cs
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Noctis.Services;

/// <summary>What the opt-in "Update automatically" setting may do on this copy of Noctis.</summary>
public enum AutoUpdateMode
{
    /// <summary>Not offered: Scoop / portable / read-only copies and Debug builds (toggle hidden).</summary>
    Off,
    /// <summary>Background download only; the user finishes with Install &amp; Restart (macOS .dmg).</summary>
    DownloadOnly,
    /// <summary>Background download, installed at the next launch (Windows Inno, Linux AppImage / tarball).</summary>
    InstallAtLaunch
}

/// <summary>What Program.Main does with a queued install at launch.</summary>
internal enum LaunchAction { None, Install, Completed, Discard, Postponed }

/// <summary>A verified installer queued for the next launch.</summary>
public sealed class PendingInstall
{
    public string Tag { get; set; } = "";
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    public string InstallerPath { get; set; } = "";
    /// <summary>SHA-256 recorded right after the SHA256SUMS check; re-checked before launch.</summary>
    public string Sha256 { get; set; } = "";
    public bool IsPrerelease { get; set; }
    public string? ReleaseUrl { get; set; }
    public DateTimeOffset DownloadedUtc { get; set; }
    public DateTimeOffset? PostponedUntilUtc { get; set; }
    public int LaunchAttempts { get; set; }
    /// <summary>When the last launch install started; a relaunch soon after leaves it running alone.</summary>
    public DateTimeOffset? LastLaunchUtc { get; set; }
}

/// <summary>Auto-update bookkeeping: the queued install plus per-tag failure/backoff state.</summary>
public sealed class AutoUpdateState
{
    /// <summary>Release the failure counters below belong to.</summary>
    public string? Tag { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    /// <summary>No more automatic downloads of <see cref="Tag"/>; the manual button still works.</summary>
    public bool Blocked { get; set; }
    public PendingInstall? Pending { get; set; }
    /// <summary>Installer left behind by a completed update, deleted once nothing holds it.</summary>
    public string? StaleInstallerPath { get; set; }
}

/// <summary>
/// <c>&lt;data&gt;/auto-update.json</c>. Kept out of settings.json because Program.Main reads it
/// before settings load, and SaveAsync re-bases settings from disk. Same file discipline as
/// LocalApiTokenStore: temp + move, chmod 600 on Unix, a corrupt file reads as absent.
/// </summary>
public sealed class AutoUpdateStore
{
    public const string FileName = "auto-update.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public AutoUpdateStore(string dataDirectory) =>
        FilePath = Path.Combine(dataDirectory, FileName);

    public string FilePath { get; }

    public AutoUpdateState? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<AutoUpdateState>(File.ReadAllText(FilePath));
        }
        catch
        {
            return null; // corrupt → treated as absent; the next save repairs it
        }
    }

    public void Save(AutoUpdateState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, WriteOptions));
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* best effort on filesystems without POSIX modes */ }
        }
        File.Move(tmp, FilePath, overwrite: true);
    }

    public void Delete()
    {
        try { File.Delete(FilePath); } catch { /* best effort */ }
    }
}

/// <summary>Pure rules behind automatic updates, kept apart from IO so every branch is testable.</summary>
internal static class AutoUpdatePolicy
{
    /// <summary>A release must be public this long before it downloads unattended, so a bad or
    /// pulled build (v1.5.3 was replaced within 2 h) never reaches anyone automatically.</summary>
    public static readonly TimeSpan SoakPeriod = TimeSpan.FromHours(24);
    public const int MaxDownloadFailures = 3;
    public const int MaxLaunchAttempts = 2;
    public static readonly TimeSpan PostponeFor = TimeSpan.FromHours(24);
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(24);
    /// <summary>Nothing shows until Inno's window appears (or, on Linux, the silent swap ends), so a
    /// second click this soon after an install started must not start another one over it.</summary>
    public static readonly TimeSpan InstallRunningWindow = TimeSpan.FromMinutes(5);

    // The names DownloadInstallerAsync gives its temp files (random lowercase 8-hex run tag).
    private static readonly Regex OwnedName = new(
        @"^Noctis-Update-[0-9a-f]{8}(-Setup\.exe|\.dmg|\.AppImage|\.tar\.gz)$",
        RegexOptions.CultureInvariant);

    /// <summary>Three-part version. Assembly versions are 4-part (1.5.4.0) while Version("1.5.4")
    /// has Revision -1 and compares LESS than 1.5.4.0, so both sides go through here.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static AutoUpdateMode ResolveMode(
        bool isWindows, bool isMacOS, bool isLinux, InstallSource source,
        bool isAppImage, bool appImageDirWritable, bool isX64, bool isDebugBuild)
    {
        // A dev build run from a writable bin folder would otherwise be "Installed" on Linux.
        if (isDebugBuild) return AutoUpdateMode.Off;
        if (source != InstallSource.Installed) return AutoUpdateMode.Off;
        if (isWindows) return AutoUpdateMode.InstallAtLaunch;
        if (isMacOS) return AutoUpdateMode.DownloadOnly;
        if (isLinux)
        {
            // The AppImage swap is `mv` in a detached shell: it fails silently in a folder the
            // user can't write, and CI ships no arm64 AppImage.
            if (isAppImage && (!isX64 || !appImageDirWritable)) return AutoUpdateMode.Off;
            return AutoUpdateMode.InstallAtLaunch;
        }
        return AutoUpdateMode.Off;
    }

    public static bool ShouldAutoDownload(AutoUpdateState? s, UpdateInfo u, DateTimeOffset now, out string reason)
    {
        if (u.InstallerApiUrl is null) { reason = "no installer asset"; return false; }
        // Not a failure: an upload in progress attaches SHA256SUMS last; the next check sees it.
        if (u.ChecksumsApiUrl is null) { reason = "no SHA256SUMS yet"; return false; }
        if (u.WarningText is not null) { reason = "release notes carry a warning"; return false; }
        if (u.PublishedAt is not { } published || now - published < SoakPeriod)
        {
            reason = "published less than 24 h ago";
            return false;
        }

        if (s is not null && s.Tag == u.TagName)
        {
            if (s.Blocked) { reason = "blocked after failures"; return false; }
            if (s.Failures > 0 && s.LastAttemptUtc is { } last && now < last + BackoffAfter(s.Failures))
            {
                reason = $"backing off until {(last + BackoffAfter(s.Failures)).LocalDateTime:g}";
                return false;
            }
        }

        reason = "eligible";
        return true;
    }

    public static TimeSpan BackoffAfter(int failures) =>
        failures <= 1 ? TimeSpan.FromHours(1) : TimeSpan.FromHours(6);

    /// <summary>Counts a failed automatic download. <paramref name="hard"/> (a SHA-256 mismatch)
    /// blocks the tag at once; soft failures block it after <see cref="MaxDownloadFailures"/>.</summary>
    public static AutoUpdateState RecordDownloadFailure(AutoUpdateState? s, string tag, DateTimeOffset now, bool hard)
    {
        var state = s ?? new AutoUpdateState();
        if (state.Tag != tag)
        {
            // New release: its own count. The queued install and stale path are kept.
            state.Tag = tag;
            state.Failures = 0;
            state.Blocked = false;
        }
        state.Failures++;
        state.LastAttemptUtc = now;
        state.Blocked = state.Blocked || hard || state.Failures >= MaxDownloadFailures;
        return state;
    }

    public static LaunchAction DecideLaunchAction(
        PendingInstall? p, Version current, AutoUpdateMode mode,
        bool hasFilesToOpen, bool fileOk, DateTimeOffset now, out string reason)
    {
        if (p is null) { reason = "nothing queued"; return LaunchAction.None; }
        if (!Version.TryParse(p.ToVersion, out var to)) { reason = "unreadable version"; return LaunchAction.Discard; }

        var target = Normalize(to);
        var running = Normalize(current);
        if (target == running) { reason = "update completed"; return LaunchAction.Completed; }
        if (target < running) { reason = "older than this build"; return LaunchAction.Discard; }

        if (mode == AutoUpdateMode.Off) { reason = "automatic updates are off for this copy"; return LaunchAction.Discard; }
        // Before the file check: the Linux AppImage swap moves the file away while it runs.
        if (p.LastLaunchUtc is { } started && now >= started && now - started < InstallRunningWindow)
        {
            reason = $"an install started at {started.LocalDateTime:T}";
            return LaunchAction.None;
        }
        // Before DownloadOnly too: a macOS .dmg purged from temp would otherwise stay queued forever.
        if (!fileOk) { reason = "installer file missing or not ours"; return LaunchAction.Discard; }
        if (mode == AutoUpdateMode.DownloadOnly) { reason = "waiting for Install & Restart"; return LaunchAction.None; }
        if (p.LaunchAttempts >= MaxLaunchAttempts) { reason = $"{p.LaunchAttempts} launch attempts"; return LaunchAction.Discard; }
        // The installer's relaunch passes no arguments, so files handed to this launch would be lost.
        if (hasFilesToOpen) { reason = "files to open"; return LaunchAction.None; }
        if (p.PostponedUntilUtc is { } until && until > now) { reason = $"postponed until {until.LocalDateTime:g}"; return LaunchAction.Postponed; }

        reason = "ready";
        return LaunchAction.Install;
    }

    /// <summary>
    /// True only for a file the updater itself downloaded: a direct child of
    /// <paramref name="tempDir"/> with the updater's random temp name. A hand-edited
    /// auto-update.json therefore can't point Noctis at any other program or file.
    /// </summary>
    public static bool IsOwnedUpdateFile(string? path, string tempDir)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(tempDir)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            // Already canonical: any "..", "." or doubled separator is not one of ours.
            var full = Path.GetFullPath(path);
            if (!string.Equals(full, path, comparison)) return false;

            var dir = Path.GetDirectoryName(full);
            if (dir is null || !string.Equals(TrimSeparators(dir), TrimSeparators(Path.GetFullPath(tempDir)), comparison))
                return false;

            return OwnedName.IsMatch(Path.GetFileName(full));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A release tag safe to put in a github.com URL: parses as a version and holds only
    /// letters, digits, '.', '-' and '+'.</summary>
    public static bool IsSafeTag(string? tag) =>
        !string.IsNullOrEmpty(tag)
        && tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+')
        && UpdateService.ParseTag(tag) is not null;

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
