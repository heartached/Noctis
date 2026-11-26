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

    // ── Music video search / streaming (Discord #feedback 11-25) ──────────────

    /// <summary>Highest stream the lyrics page asks for: the backdrop decodes at 960 px and the
    /// full-size view at 1920, so more only costs bandwidth.</summary>
    public const int StreamMaxHeight = 1080;

    /// <summary>
    /// A YouTube search without downloading anything: one JSON object per result line
    /// (<c>--flat-playlist</c> keeps it to the search page, no per-video page fetch).
    /// </summary>
    public static IReadOnlyList<string> SearchArgs(string query, int count, string? jsRuntime = null)
    {
        var args = new List<string> { "--no-warnings", "--flat-playlist", "--dump-json", "--ignore-errors" };
        AddJsRuntime(args, jsRuntime);
        args.Add($"ytsearch{Math.Clamp(count, 1, 20)}:{query}");
        return args;
    }

    /// <summary>One video's details (title, channel, length) for a pasted link; no download.</summary>
    public static IReadOnlyList<string> InfoArgs(string url, string? jsRuntime = null)
    {
        var args = new List<string> { "--no-warnings", "--no-playlist", "--skip-download", "--dump-json" };
        AddJsRuntime(args, jsRuntime);
        args.Add(url);
        return args;
    }

    /// <summary>
    /// The direct URL of a video-only stream (the song's own file is the audio), capped at
    /// <paramref name="maxHeight"/>: H.264 in MP4 first — the backdrop decodes in software, and
    /// that is the cheapest — then any MP4, then anything, then a muxed file as a last resort.
    /// </summary>
    public static IReadOnlyList<string> StreamUrlArgs(string url, int maxHeight = StreamMaxHeight, string? jsRuntime = null)
    {
        var cap = maxHeight > 0 ? $"[height<={maxHeight}]" : string.Empty;
        var args = new List<string>
        {
            "--no-warnings", "--no-playlist", "-g",
            "-f", $"bestvideo{cap}[vcodec^=avc1]/bestvideo{cap}[ext=mp4]/bestvideo{cap}/best{cap}",
        };
        AddJsRuntime(args, jsRuntime);
        args.Add(url);
        return args;
    }

    /// <summary>
    /// Search results (or one video's <c>--dump-json</c>) from yt-dlp's JSON lines. Lines that
    /// are not a video object are skipped, as are live streams (nothing to keep in step) and
    /// duplicates. Order is kept: it is YouTube's relevance order, the matcher's tie-break.
    /// </summary>
    public static IReadOnlyList<YouTubeVideoInfo> ParseVideoInfos(string? stdout)
    {
        var list = new List<YouTubeVideoInfo>();
        if (string.IsNullOrWhiteSpace(stdout)) return list;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = Str(e, "id");
                if (!YouTubeLinks.IsVideoId(id) || !seen.Add(id!)) continue;
                var live = Str(e, "live_status");
                if (live is "is_live" or "is_upcoming" || e.TryGetProperty("is_live", out var il) && il.ValueKind == JsonValueKind.True)
                    continue;
                double? duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
                long? views = e.TryGetProperty("view_count", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
                var verified = e.TryGetProperty("channel_is_verified", out var cv) && cv.ValueKind == JsonValueKind.True;
                list.Add(new YouTubeVideoInfo(
                    id!,
                    Str(e, "title") ?? id!,
                    Str(e, "channel") ?? Str(e, "uploader"),
                    duration is > 0 ? duration : null,
                    views,
                    verified));
            }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>The first http(s) line of a <c>-g</c> run, or null.</summary>
    public static string? ParseStreamUrl(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return line;
        }
        return null;
    }

    [GeneratedRegex(@"(?:[?&]expire=|/expire/)(\d{9,11})(?:[&/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExpireRegex();

    /// <summary>
    /// When a googlevideo stream URL stops working: its <c>expire=</c> (or <c>/expire/N/</c>)
    /// Unix time, or null when the URL does not say.
    /// </summary>
    public static DateTime? StreamUrlExpiry(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var m = ExpireRegex().Match(url);
        if (!m.Success || !long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>yt-dlp said the video itself is gone (removed, private, blocked here), so a
    /// remembered automatic match for it should be dropped and searched again.</summary>
    public static bool IsVideoGoneError(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        return stderr.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
               || stderr.Contains("Private video", StringComparison.OrdinalIgnoreCase)
               || stderr.Contains("This video has been removed", StringComparison.OrdinalIgnoreCase)
               || stderr.Contains("not available in your country", StringComparison.OrdinalIgnoreCase);
    }

    // ── Bot check / browser sign-in (11-25) ──────────────────────────────────

    /// <summary>
    /// YouTube's "Sign in to confirm you're not a bot": this computer's address was flagged.
    /// Not a stale yt-dlp (an update does not help) and not the video's fault (a saved match
    /// stays): only the user's YouTube sign-in, or time, gets past it.
    /// </summary>
    public static bool IsBotCheckError(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        var text = stderr.Replace('’', '\'');
        return text.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase)
               || text.Contains("confirm you are not a bot", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase);
    }

    public const string DefaultCookieBrowser = "firefox";

    /// <summary>
    /// Browsers yt-dlp's <c>--cookies-from-browser</c> reads, in the order Settings lists them.
    /// Firefox first: Chromium browsers on Windows lock and encrypt their cookie store while
    /// they run, so Firefox is the one that reads reliably there. Safari is macOS only.
    /// </summary>
    public static IReadOnlyList<string> CookieBrowsers(OSPlatform? platform = null)
    {
        var isMac = platform is null ? OperatingSystem.IsMacOS() : platform == OSPlatform.OSX;
        var list = new List<string> { "firefox", "chrome", "edge", "brave", "opera", "vivaldi", "chromium", "whale" };
        if (isMac) list.Add("safari");
        return list;
    }

    /// <summary>A browser yt-dlp knows (any case), lower-cased; null otherwise.</summary>
    public static string? NormalizeCookieBrowser(string? browser)
    {
        var b = browser?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(b) && CookieBrowsers(OSPlatform.OSX).Contains(b) ? b : null;
    }

    /// <summary>The browser's own name ("Firefox", "Chrome"…), as Settings and messages show it.</summary>
    public static string CookieBrowserName(string? browser) => NormalizeCookieBrowser(browser) switch
    {
        "firefox" => "Firefox",
        "chrome" => "Chrome",
        "edge" => "Edge",
        "brave" => "Brave",
        "opera" => "Opera",
        "vivaldi" => "Vivaldi",
        "chromium" => "Chromium",
        "whale" => "Whale",
        "safari" => "Safari",
        _ => browser?.Trim() ?? string.Empty,
    };

    /// <summary>
    /// <paramref name="args"/> with <c>--cookies-from-browser &lt;browser&gt;</c> before the last
    /// argument (the URL or the search), when a known browser is given; unchanged otherwise.
    /// </summary>
    public static IReadOnlyList<string> WithCookiesFromBrowser(IReadOnlyList<string> args, string? browser)
    {
        var b = NormalizeCookieBrowser(browser);
        if (b is null || args.Count == 0 || args.Contains("--cookies-from-browser")) return args;
        var list = new List<string>(args.Count + 2);
        list.AddRange(args.Take(args.Count - 1));
        list.Add("--cookies-from-browser");
        list.Add(b);
        list.Add(args[^1]);
        return list;
    }

    /// <summary>
    /// yt-dlp could not read the browser's cookies, in the wordings yt-dlp uses today (it stops
    /// before asking YouTube anything): <see cref="YtDlpFailureKind.CookiesNotFound"/> when the
    /// browser or its profile is not there, <see cref="YtDlpFailureKind.CookiesUnreadable"/> when
    /// the store is locked (the browser is open), encrypted or not permitted, else
    /// <see cref="YtDlpFailureKind.Other"/>. Other cookie wordings are left to
    /// <see cref="ClassifyFailure"/>'s context rule. The bot-check line itself mentions
    /// "--cookies" and is not a cookie failure.
    /// </summary>
    public static YtDlpFailureKind ClassifyCookieError(string? stderr)
    {
        foreach (var line in ErrorLines(stderr))
        {
            if (IsBotCheckError(line)) continue;
            var l = line.ToLowerInvariant();
            if (l.Contains("unsupported browser")
                || l.Contains("could not find") && (l.Contains("cookies database") || l.Contains("cookie database") || l.Contains("local state")))
                return YtDlpFailureKind.CookiesNotFound;
            if (l.Contains("could not copy") && l.Contains("cookie")
                || l.Contains("failed to decrypt with dpapi")
                || l.Contains("cookie") && (l.Contains("permission denied") || l.Contains("database is locked")))
                return YtDlpFailureKind.CookiesUnreadable;
        }
        return YtDlpFailureKind.Other;
    }

    /// <summary>
    /// The kind of failure a yt-dlp run's stderr shows. With the browser sign-in on
    /// (<paramref name="cookiesFromBrowser"/>), cookie trouble comes first: it stops a run before
    /// YouTube is asked anything. A wording yt-dlp may use one day still counts by its context
    /// (<see cref="YtDlpFailureKind.CookiesOther"/>): an ERROR line about cookies, a keyring,
    /// DPAPI, decrypting or a browser profile, or a run that failed before any YouTube request.
    /// With the sign-in off nothing is a cookie problem. <paramref name="stdout"/> is the run's
    /// stdout, where a download prints its "[youtube]" lines.
    /// </summary>
    public static YtDlpFailureKind ClassifyFailure(string? stderr, bool cookiesFromBrowser = false, string? stdout = null)
    {
        if (cookiesFromBrowser)
        {
            var cookies = ClassifyCookieError(stderr);
            if (cookies != YtDlpFailureKind.Other) return cookies;
        }
        if (IsBotCheckError(stderr)) return YtDlpFailureKind.BotCheck;
        if (cookiesFromBrowser && (ErrorLines(stderr).Any(IsCookieRelatedLine) || FailedBeforeYouTube(stderr, stdout)))
            return YtDlpFailureKind.CookiesOther;
        return YtDlpFailureKind.Other;
    }

    private static readonly string[] CookieContext = { "cookie", "keyring", "dpapi", "decrypt", "local state" };

    private static bool IsCookieRelatedLine(string line)
    {
        if (IsBotCheckError(line)) return false;
        var l = line.ToLowerInvariant();
        return CookieContext.Any(l.Contains) || l.Contains("browser") && l.Contains("profile");
    }

    /// <summary>
    /// The run failed before yt-dlp asked YouTube anything: none of its lines names the YouTube
    /// extractor ("[youtube]", "[youtube:search]"), a download, or an answer from YouTube.
    /// </summary>
    private static bool FailedBeforeYouTube(string? stderr, string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        if (IsBlockedError(stderr) || IsVideoGoneError(stderr)) return false;
        var l = (stderr + "\n" + stdout).ToLowerInvariant();
        return !l.Contains("[youtube") && !l.Contains("[download]") && !l.Contains("http error")
               && !l.Contains("googlevideo") && !l.Contains("[info]");
    }

    /// <summary>yt-dlp's ERROR lines, trimmed, in order.</summary>
    private static IEnumerable<string> ErrorLines(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) yield break;
        foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) yield return line;
    }

    /// <summary>Longest yt-dlp line a message quotes.</summary>
    private const int MaxQuotedLine = 240;

    /// <summary>
    /// yt-dlp's first ERROR line (else its last line), for a message that quotes it: without the
    /// "ERROR:" in front, the user's home folder shown as "~", and at most 240 characters. Null
    /// when yt-dlp printed nothing.
    /// </summary>
    public static string? FirstErrorLine(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;
        var line = ErrorLines(stderr).FirstOrDefault()
                   ?? stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            line = line[5..].TrimStart(':', ' ', '\t');
        line = WithoutHomeFolder(line).Trim();
        if (line.Length == 0) return null;
        return line.Length > MaxQuotedLine ? line[..MaxQuotedLine] + "…" : line;
    }

    /// <summary>The user's home folder (this one, or any "C:\Users\name", "/home/name", "/Users/name") as "~".</summary>
    private static string WithoutHomeFolder(string text)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
            if (home.Length > 3) text = text.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return HomeFolderPattern().Replace(text, "~");
    }

    [GeneratedRegex(@"\b[A-Za-z]:\\(?:Users|Documents and Settings)\\[^\\/:*?""<>|\r\n]+|/(?:home|Users)/[^/\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex HomeFolderPattern();

    /// <summary>The plain-language message for a bot check / cookie failure (empty for <see cref="YtDlpFailureKind.Other"/>).
    /// <paramref name="cookieBrowser"/> is the browser whose sign-in was used, or null; <paramref name="detail"/>
    /// is yt-dlp's own line (<see cref="FirstErrorLine"/>) for <see cref="YtDlpFailureKind.CookiesOther"/>.</summary>
    public static string FailureMessage(YtDlpFailureKind kind, string? cookieBrowser, string? detail = null)
    {
        var name = CookieBrowserName(cookieBrowser);
        return kind switch
        {
            YtDlpFailureKind.BotCheck when NormalizeCookieBrowser(cookieBrowser) is not null
                => Localization.Loc.T("YtDlp.BotCheckSignedIn", name),
            YtDlpFailureKind.BotCheck => Localization.Loc.T("YtDlp.BotCheck"),
            YtDlpFailureKind.CookiesNotFound => Localization.Loc.T("YtDlp.CookiesNotFound", name),
            YtDlpFailureKind.CookiesUnreadable => Localization.Loc.T("YtDlp.CookiesUnreadable", name),
            YtDlpFailureKind.CookiesOther => Localization.Loc.T("YtDlp.CookiesFailed", name,
                string.IsNullOrWhiteSpace(detail) ? "?" : detail),
            _ => string.Empty,
        };
    }

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

/// <summary>A yt-dlp failure that needs its own handling (see <see cref="YtDlpParsing.ClassifyFailure"/>).</summary>
public enum YtDlpFailureKind
{
    Other,
    /// <summary>"Sign in to confirm you're not a bot".</summary>
    BotCheck,
    /// <summary>The chosen browser's cookie store is locked, encrypted or not permitted.</summary>
    CookiesUnreadable,
    /// <summary>The chosen browser (or its profile) is not on this computer.</summary>
    CookiesNotFound,
    /// <summary>Another failure reading the browser sign-in (a wording yt-dlp may use one day),
    /// shown with yt-dlp's own line.</summary>
    CookiesOther,
}
