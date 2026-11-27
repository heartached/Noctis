using System.Text.RegularExpressions;

namespace Noctis.Services.YouTube;

/// <summary>
/// YouTube links as people paste them: youtu.be/ID, youtube.com/watch?v=ID (with &amp;t=,
/// &amp;list=, &amp;si= or v= not first), music.youtube.com, m.youtube.com, /shorts/ID,
/// /embed/ID, /live/ID, with or without the scheme. Only the video id is kept: a playlist or
/// start time in the link must never turn a "use this video" into a playlist download.
/// </summary>
public static partial class YouTubeLinks
{
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    /// <summary>A bare 11-character video id.</summary>
    public static bool IsVideoId(string? id) => !string.IsNullOrEmpty(id) && VideoIdRegex().IsMatch(id);

    /// <summary>The video id of a YouTube link, or null when the text is not one.</summary>
    public static string? TryGetVideoId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var raw = text.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (host is "youtu.be" or "www.youtu.be")
            return segments.Length >= 1 && IsVideoId(segments[0]) ? segments[0] : null;

        if (host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com"
            or "youtube-nocookie.com" or "www.youtube-nocookie.com"))
            return null;

        if (segments.Length >= 1 && segments[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
        {
            var v = QueryValue(uri.Query, "v");
            return IsVideoId(v) ? v : null;
        }
        if (segments.Length >= 2 && segments[0].ToLowerInvariant() is "shorts" or "embed" or "live" or "v")
            return IsVideoId(segments[1]) ? segments[1] : null;
        return null;
    }

    /// <summary>The canonical watch link for an id (what yt-dlp is handed: no playlist, no time).</summary>
    public static string WatchUrl(string videoId) => "https://www.youtube.com/watch?v=" + videoId;

    /// <summary>YouTube's always-present 320×180 thumbnail for an id.</summary>
    public static string ThumbnailUrl(string videoId) => $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

    private static string? QueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            if (!key.Equals(name, StringComparison.Ordinal)) continue;
            return eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }
}
