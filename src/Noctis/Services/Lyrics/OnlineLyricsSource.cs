using Noctis.Models;

namespace Noctis.Services.Lyrics;

/// <summary>
/// An online lyrics source past LRCLIB and NetEase (issue #113): the name the lyrics page
/// shows ("Try Kugou"), its Settings switch for automatic lookups, and its search.
/// </summary>
public sealed record OnlineLyricsSource(
    string Name,
    Func<AppSettings, bool> IsEnabled,
    Func<string, string, double, CancellationToken, Task<LrcLibResult?>> SearchAsync)
{
    public const string Musixmatch = "Musixmatch";
    public const string Kugou = "Kugou";
    public const string YouTubeMusic = "YouTube Music";

    /// <summary>The built-in extra sources in Auto's priority order: on a format tie the earlier wins,
    /// so Musixmatch's hand-made richsync beats Kugou's KRC, and both beat YouTube's line sync.</summary>
    public static IReadOnlyList<OnlineLyricsSource> BuiltIns(
        IMusixmatchService musixmatch, IKugouLyricsService kugou, IYouTubeMusicLyricsService youTubeMusic) =>
    [
        new(Musixmatch, s => s.MusixmatchEnabled, musixmatch.SearchLyricsAsync),
        new(Kugou, s => s.KugouEnabled, kugou.SearchLyricsAsync),
        new(YouTubeMusic, s => s.YouTubeMusicLyricsEnabled, youTubeMusic.SearchLyricsAsync),
    ];
}

