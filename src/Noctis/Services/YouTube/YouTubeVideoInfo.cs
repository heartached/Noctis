using System.Globalization;

namespace Noctis.Services.YouTube;

/// <summary>One YouTube video as yt-dlp describes it (a search result or a pasted link).</summary>
public sealed record YouTubeVideoInfo(
    string Id,
    string Title,
    string? Channel,
    double? DurationSeconds,
    long? ViewCount = null,
    bool ChannelVerified = false)
{
    public string WatchUrl => YouTubeLinks.WatchUrl(Id);
    public string ThumbnailUrl => YouTubeLinks.ThumbnailUrl(Id);

    /// <summary>"3:34" / "1:02:10"; empty when YouTube gave no length.</summary>
    public string DurationText => FormatDuration(DurationSeconds);

    /// <summary>"Rick Astley · 3:34".</summary>
    public string Detail => string.Join(" · ", new[] { Channel, DurationText }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public static string FormatDuration(double? seconds)
    {
        if (seconds is not > 0) return string.Empty;
        var t = TimeSpan.FromSeconds(Math.Round(seconds.Value));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}

/// <summary>What the lyrics page's music video streaming needs from yt-dlp (a seam for tests).</summary>
public interface IYouTubeVideoSource
{
    /// <summary>yt-dlp can be run (a user-set, app-installed or PATH copy).</summary>
    bool IsAvailable { get; }

    Task<IReadOnlyList<YouTubeVideoInfo>> SearchAsync(string query, int count, CancellationToken ct);

    /// <summary>Details of one video by link; null when yt-dlp found nothing.</summary>
    Task<YouTubeVideoInfo?> GetInfoAsync(string url, CancellationToken ct);

    /// <summary>A playable video-only stream URL for the id (expires after some hours).</summary>
    Task<string> ResolveStreamUrlAsync(string videoId, int maxHeight, CancellationToken ct);
}
