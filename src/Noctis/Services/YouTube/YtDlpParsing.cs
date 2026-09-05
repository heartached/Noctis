using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Noctis.Services.YouTube;

/// <summary>One YouTube video as yt-dlp describes it, reduced to what a music library needs.</summary>
public sealed record YouTubeTrackInfo(
    string Id,
    string Url,
    string Title,
    string Channel,
    TimeSpan? Duration,
    string? ThumbnailUrl,
    string? Track,
    string? Artist,
    string? Album,
    int? Year,
    string? Ext)
{
    public string DurationText => Duration is { } d
        ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"))
        : string.Empty;
}

/// <summary>
/// Pure helpers around the yt-dlp command line and its JSON: argument lists, result
/// parsing, tag inference and file naming. No process or network I/O lives here so every
/// rule is unit-testable.
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

    public static string? ExtractVideoId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = YouTubeUrlRegex().Match(url.Trim());
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string WatchUrl(string id) => "https://www.youtube.com/watch?v=" + id;

    // ── Arguments ─────────────────────────────────────────────────────────────

    public static IReadOnlyList<string> VersionArgs() => new[] { "--version" };

    public static IReadOnlyList<string> SearchArgs(string query, int limit)
    {
        limit = Math.Clamp(limit, 1, 50);
        return new[]
        {
            "--dump-json", "--flat-playlist", "--no-warnings", "--skip-download", "--ignore-errors",
            "--no-playlist",
            $"ytsearch{limit}:{query.Trim()}",
        };
    }

    public static IReadOnlyList<string> InfoArgs(string url) =>
        new[] { "--dump-json", "--no-playlist", "--no-warnings", "--skip-download", url };

    /// <summary>
    /// Download args: best m4a stream, else best audio. With ffmpeg available the result is
    /// remuxed/converted to m4a so every download plays and tags the same way; yt-dlp copies
    /// the stream when it is already AAC-in-m4a.
    /// </summary>
    public static IReadOnlyList<string> DownloadArgs(string url, string outputTemplate, string? ffmpegPath)
    {
        var args = new List<string>
        {
            "--no-playlist", "--no-warnings", "--newline", "--no-mtime", "--no-part",
            "-f", "bestaudio[ext=m4a]/bestaudio/best",
            "-o", outputTemplate,
        };
        if (!string.IsNullOrWhiteSpace(ffmpegPath))
        {
            args.AddRange(new[] { "--ffmpeg-location", ffmpegPath, "-x", "--audio-format", "m4a", "--audio-quality", "0" });
        }
        args.Add(url);
        return args;
    }

    // ── JSON ──────────────────────────────────────────────────────────────────

    /// <summary>Parses one <c>--dump-json</c> object; null when it is not a video entry.</summary>
    public static YouTubeTrackInfo? ParseInfo(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FromElement(doc.RootElement);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Parses newline-delimited <c>--dump-json</c> output (one entry per line).</summary>
    public static List<YouTubeTrackInfo> ParseSearch(string ndjson)
    {
        var list = new List<YouTubeTrackInfo>();
        if (string.IsNullOrWhiteSpace(ndjson)) return list;
        foreach (var raw in ndjson.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            var info = ParseInfo(line);
            if (info is not null && list.All(x => x.Id != info.Id)) list.Add(info);
        }
        return list;
    }

    private static YouTubeTrackInfo? FromElement(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = Str(e, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var title = Str(e, "title") ?? id;
        var url = Str(e, "webpage_url") ?? Str(e, "original_url") ?? WatchUrl(id);
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = WatchUrl(id);
        var channel = Str(e, "channel") ?? Str(e, "uploader") ?? Str(e, "creator") ?? string.Empty;
        TimeSpan? duration = null;
        if (e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var secs) && secs > 0)
            duration = TimeSpan.FromSeconds(secs);

        var artist = Str(e, "artist") ?? FirstOfArray(e, "artists") ?? Str(e, "creator") ?? FirstOfArray(e, "creators");
        int? year = null;
        if (e.TryGetProperty("release_year", out var ry) && ry.ValueKind == JsonValueKind.Number && ry.TryGetInt32(out var y) && y > 1800) year = y;
        year ??= YearFromDate(Str(e, "release_date")) ?? YearFromDate(Str(e, "upload_date"));

        return new YouTubeTrackInfo(
            Id: id,
            Url: url,
            Title: title,
            Channel: channel,
            Duration: duration,
            ThumbnailUrl: PickThumbnail(e, id),
            Track: Str(e, "track"),
            Artist: artist,
            Album: Str(e, "album"),
            Year: year,
            Ext: Str(e, "ext"));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static string? FirstOfArray(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var parts = arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    internal static int? YearFromDate(string? yyyymmdd) =>
        yyyymmdd is { Length: >= 4 } && int.TryParse(yyyymmdd.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y > 1800 ? y : null;

    /// <summary>Largest JPEG thumbnail up to 1280px; falls back to the single <c>thumbnail</c> field, then YouTube's hqdefault.</summary>
    internal static string? PickThumbnail(JsonElement e, string id)
    {
        if (e.TryGetProperty("thumbnails", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            string? best = null;
            var bestScore = -1;
            foreach (var t in arr.EnumerateArray())
            {
                var url = Str(t, "url");
                if (url is null) continue;
                var height = t.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out var hh) ? hh : 0;
                var isJpg = url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) || url.Contains("/vi/", StringComparison.OrdinalIgnoreCase) && !url.Contains(".webp", StringComparison.OrdinalIgnoreCase);
                if (height > 1280) continue;
                var score = (isJpg ? 10000 : 0) + height;
                if (score > bestScore) { bestScore = score; best = url; }
            }
            if (best is not null) return best;
        }
        var single = Str(e, "thumbnail");
        if (single is not null) return single;
        return $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";
    }

    // ── Tag inference ─────────────────────────────────────────────────────────

    [GeneratedRegex(@"\s*[\(\[\{](?:[^\)\]\}]*?)(?:official|video|audio|lyric|lyrics|visuali[sz]er|hd|hq|4k|explicit|clean version|music video|mv|remaster(?:ed)?(?:\s+\d{4})?|live|prod\.?)(?:[^\)\]\}]*?)[\)\]\}]", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseBracketsRegex();

    [GeneratedRegex(@"\s*[\|•·]\s*.*$")]
    private static partial Regex TrailingPipeRegex();

    [GeneratedRegex(@"\s+(?:-|–|—)\s+(?:official\s+)?(?:music\s+)?(?:video|audio|lyrics?|visuali[sz]er)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingDashNoiseRegex();

    /// <summary>Strips "(Official Video)", "[Lyrics]", "| Official Audio" and friends from a video title.</summary>
    public static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var s = title.Trim();
        s = NoiseBracketsRegex().Replace(s, string.Empty);
        s = TrailingPipeRegex().Replace(s, string.Empty);
        s = TrailingDashNoiseRegex().Replace(s, string.Empty);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim().TrimEnd('-', '–', '—').Trim();
        return s.Length == 0 ? title.Trim() : s;
    }

    /// <summary>"Artist - Topic" (auto-generated music channels) and "ArtistVEVO" → "Artist".</summary>
    public static string CleanChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return string.Empty;
        var s = channel.Trim();
        s = Regex.Replace(s, @"\s*-\s*Topic$", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"VEVO$", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+Official$", string.Empty, RegexOptions.IgnoreCase);
        return s.Trim();
    }

    /// <summary>
    /// Artist/title for the file tags. Prefers YouTube Music's own <c>artist</c>/<c>track</c>
    /// fields, then "Artist - Title" split from the video title, then channel + cleaned title.
    /// </summary>
    public static (string Artist, string Title) InferTags(YouTubeTrackInfo info)
    {
        var cleanTitle = CleanTitle(info.Title);
        var artistTag = string.IsNullOrWhiteSpace(info.Artist) ? null : info.Artist.Trim();
        var trackTag = string.IsNullOrWhiteSpace(info.Track) ? null : info.Track.Trim();
        if (artistTag is not null && trackTag is not null) return (artistTag, trackTag);

        // "Artist - Title" (also en/em dash). Only split once, on the first separator.
        var m = Regex.Match(cleanTitle, @"^(?<a>.+?)\s+[-–—]\s+(?<t>.+)$");
        if (m.Success)
        {
            var a = m.Groups["a"].Value.Trim();
            var t = m.Groups["t"].Value.Trim();
            if (a.Length > 0 && t.Length > 0)
                return (artistTag ?? a, trackTag ?? t);
        }

        var channel = CleanChannel(info.Channel);
        return (artistTag ?? (channel.Length > 0 ? channel : "Unknown Artist"), trackTag ?? cleanTitle);
    }

    // ── File names ────────────────────────────────────────────────────────────

    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "untitled";
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();
        var chars = name.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.', ' ');
        s = Regex.Replace(s, @"\s{2,}", " ");
        if (s.Length > 120) s = s[..120].TrimEnd();
        return s.Length == 0 ? "untitled" : s;
    }

    public static string BuildFileName(string artist, string title, string ext)
    {
        var e = string.IsNullOrWhiteSpace(ext) ? ".m4a" : (ext.StartsWith('.') ? ext : "." + ext);
        var stem = string.IsNullOrWhiteSpace(artist) ? SanitizeFileName(title) : SanitizeFileName($"{artist} - {title}");
        return stem + e.ToLowerInvariant();
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
