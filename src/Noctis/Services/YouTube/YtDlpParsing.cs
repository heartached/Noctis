using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Noctis.Services.YouTube;

/// <summary>
/// Pure helpers around the yt-dlp command line: argument lists, versions and progress, for the
/// lyrics background videos. No process or network I/O lives here so every rule is
/// unit-testable. (Search, audio download, tag inference and file naming moved with "Add from
/// YouTube" to the official plugin, plugins/Noctis.Plugins.YouTubeImport, 10-29.)
/// </summary>
public static partial class YtDlpParsing
{
    public const string ReleaseBaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";

    /// <summary>Official release asset for this OS/architecture.</summary>
    public static string ReleaseAssetName(OSPlatform? platform = null, Architecture? arch = null)
    {
        var isWindows = platform is null ? OperatingSystem.IsWindows() : platform == OSPlatform.Windows;
        var isMac = platform is null ? OperatingSystem.IsMacOS() : platform == OSPlatform.OSX;
        var a = arch ?? RuntimeInformation.OSArchitecture;
        if (isWindows) return "yt-dlp.exe";
        if (isMac) return "yt-dlp_macos";
        return a == Architecture.Arm64 ? "yt-dlp_linux_aarch64" : "yt-dlp_linux";
    }

    public static string ReleaseDownloadUrl() => ReleaseBaseUrl + ReleaseAssetName();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.|m\.|music\.)?(?:youtube\.com/(?:watch\?.*v=|shorts/|embed/)|youtu\.be/)([A-Za-z0-9_-]{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeUrlRegex();

    public static bool LooksLikeYouTubeUrl(string? text) =>
        !string.IsNullOrWhiteSpace(text) && YouTubeUrlRegex().IsMatch(text.Trim());

    // ── Arguments ─────────────────────────────────────────────────────────────

    public static IReadOnlyList<string> VersionArgs() => new[] { "--version" };

    /// <summary>
    /// Lyrics-backdrop download: video only (the backdrop plays with :no-audio, so an audio
    /// stream is dead weight), mp4 first, capped at <paramref name="maxHeight"/> (0 = best).
    /// With ffmpeg a non-mp4 pick is remuxed so the file is always an .mp4.
    /// </summary>
    public static IReadOnlyList<string> VideoDownloadArgs(string url, string outputTemplate, string? ffmpegPath, int maxHeight, string? jsRuntime = null)
    {
        var cap = maxHeight > 0 ? $"[height<={maxHeight}]" : string.Empty;
        var args = new List<string>
        {
            "--no-playlist", "--no-warnings", "--newline", "--no-mtime", "--no-part",
            "-f", $"bestvideo{cap}[ext=mp4]/bestvideo{cap}/best{cap}[ext=mp4]/best{cap}",
            "-o", outputTemplate,
        };
        if (!string.IsNullOrWhiteSpace(ffmpegPath))
            args.AddRange(new[] { "--ffmpeg-location", ffmpegPath, "--remux-video", "mp4" });
        AddJsRuntime(args, jsRuntime);
        args.Add(url);
        return args;
    }

    private static void AddJsRuntime(List<string> args, string? jsRuntime)
    {
        if (!string.IsNullOrWhiteSpace(jsRuntime)) args.AddRange(new[] { "--js-runtimes", jsRuntime });
    }

    // ── JS runtime ────────────────────────────────────────────────────────────

    /// <summary>First yt-dlp release that understands <c>--js-runtimes</c>; older copies reject it.</summary>
    public const string JsRuntimesMinVersion = "2025.11.12";

    /// <summary>
    /// yt-dlp only enables deno by default and warns that YouTube extraction without a JS
    /// runtime is deprecated. When deno is missing but node is on PATH, name node explicitly.
    /// Null = pass nothing (deno found, no node, or a yt-dlp too old/unknown for the flag).
    /// </summary>
    public static string? PickJsRuntime(bool hasDeno, bool hasNode, string? ytDlpVersion)
    {
        if (hasDeno || !hasNode) return null;
        if (ParseVersion(ytDlpVersion) is null || CompareVersions(ytDlpVersion, JsRuntimesMinVersion) < 0) return null;
        return "node";
    }

    // ── Versions / updates ────────────────────────────────────────────────────

    public const string LatestReleaseApiUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    public static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    /// <summary>"2026.08.19" / "2026.08.19.1" (also a leading "v" or a trailing nightly suffix) → numeric parts; null when not a yt-dlp version.</summary>
    public static int[]? ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var m = VersionRegex().Match(version.Trim());
        if (!m.Success) return null;
        var parts = new List<int>();
        foreach (var p in m.Groups[1].Value.Split('.'))
            if (int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) parts.Add(n);
            else return null;
        return parts.Count >= 3 ? parts.ToArray() : null;
    }

    [GeneratedRegex(@"^v?(\d{4}\.\d{1,2}\.\d{1,2}(?:\.\d+)?)")]
    private static partial Regex VersionRegex();

    /// <summary>Orders yt-dlp versions; an unparseable/missing version sorts below any real one.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var x = ParseVersion(a);
        var y = ParseVersion(b);
        if (x is null) return y is null ? 0 : -1;
        if (y is null) return 1;
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var xi = i < x.Length ? x[i] : 0;
            var yi = i < y.Length ? y[i] : 0;
            if (xi != yi) return xi.CompareTo(yi);
        }
        return 0;
    }

    /// <summary>True when <paramref name="latest"/> is a real version newer than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string? latest, string? installed) =>
        ParseVersion(latest) is not null && CompareVersions(latest, installed) > 0;

    /// <summary>24 h throttle for the automatic check; a forced check (after a 403) always runs.</summary>
    public static bool ShouldCheckForUpdate(DateTime? lastCheckUtc, DateTime nowUtc, bool force) =>
        force || lastCheckUtc is not { } last || nowUtc - last >= UpdateCheckInterval || last > nowUtc;

    /// <summary><c>tag_name</c> of GitHub's releases/latest JSON, when it is a yt-dlp version.</summary>
    public static string? ParseLatestTag(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, "tag_name") : null;
            return ParseVersion(tag) is null ? null : tag!.Trim().TrimStart('v');
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// YouTube refused the request (403, bot check, formats withheld). A stale yt-dlp is the
    /// usual cause, so these failures are worth an update and one retry.
    /// </summary>
    public static bool IsBlockedError(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        return stderr.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)
               || stderr.Contains("403: Forbidden", StringComparison.OrdinalIgnoreCase)
               || stderr.Contains("Sign in to confirm you", StringComparison.OrdinalIgnoreCase)
               || stderr.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What a still-blocked download says instead of yt-dlp's raw ERROR line. The wording
    /// depends on whether an update could still help.
    /// </summary>
    public static string BlockedMessage(string? installedVersion, string? latestVersion)
    {
        var installed = string.IsNullOrWhiteSpace(installedVersion) ? "?" : installedVersion.Trim();
        if (IsNewer(latestVersion, installedVersion))
            return Localization.Loc.T("YtDlp.BlockedOutdated", installed, latestVersion!);
        if (ParseVersion(latestVersion) is not null)
            return Localization.Loc.T("YtDlp.BlockedUpToDate", installed);
        return Localization.Loc.T("YtDlp.BlockedUnknown", installed);
    }

    /// <summary>
    /// Last <paramref name="maxLines"/> non-empty stderr lines, for the log. Stream URLs
    /// (googlevideo.com) carry the user's IP, so they are replaced before the text can leave
    /// the machine via "Copy Logs"; each line is capped.
    /// </summary>
    public static string StderrTail(string? stderr, int maxLines = 8)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return string.Empty;
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => StreamUrlRegex().Replace(l, "<stream-url>"))
            .Select(l => l.Length > 300 ? l[..300] + "…" : l)
            .ToArray();
        return string.Join(" | ", lines.Skip(Math.Max(0, lines.Length - maxLines)));
    }

    [GeneratedRegex(@"https?://[^\s""']*googlevideo\.com[^\s""']*", RegexOptions.IgnoreCase)]
    private static partial Regex StreamUrlRegex();

    /// <summary>"yt-dlp 2026.08.19" plus " · update available" when a newer release is known.</summary>
    public static string VersionLabel(string? installedVersion, string? latestVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion)) return string.Empty;
        var text = Localization.Loc.T("YtDlp.Version", installedVersion.Trim());
        return IsNewer(latestVersion, installedVersion) ? text + " · " + Localization.Loc.T("YtDlp.UpdateAvailable") : text;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    // ── Progress ──────────────────────────────────────────────────────────────

    [GeneratedRegex(@"\[download\]\s+(\d{1,3}(?:\.\d+)?)%")]
    private static partial Regex ProgressRegex();

    /// <summary>Percent from a <c>[download]  45.3% of 3.21MiB at 1.2MiB/s</c> line, else null.</summary>
    public static double? ParseProgressPercent(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = ProgressRegex().Match(line);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
            ? Math.Clamp(p, 0, 100)
            : null;
    }
}
