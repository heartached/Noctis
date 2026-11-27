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
    /// <summary>Least spacing between two requests of a bulk run to this source; null = the run's default.</summary>
    public TimeSpan? MinInterval { get; init; }

    public const string Musixmatch = "Musixmatch";
    public const string Kugou = "Kugou";
    public const string YouTubeMusic = "YouTube Music";
    public const string LrcRed = "lrc.red";
    public const string Unison = "Unison";
    public const string LrcMux = "lrcmux";
    public const string Genius = "Genius";

    /// <summary>The built-in extra sources in Auto's priority order: on a format tie the earlier wins,
    /// so Musixmatch's hand-made richsync beats lrc.red's and Unison's TTML, which keep duet voices
    /// and background vocals and so beat Kugou's KRC; all beat YouTube's line sync. lrcmux (GitHub #219)
    /// comes last: it relays sources asked before it, so it mostly fills what they missed, and its
    /// 60-requests-a-minute limit keeps a bulk run's requests to it a second apart. Genius (GitHub #265)
    /// has plain lyrics only, so any timed answer beats it; it sits before lrcmux so its curated text
    /// wins a plain-only tie.</summary>
    public static IReadOnlyList<OnlineLyricsSource> BuiltIns(
        IMusixmatchService musixmatch, IKugouLyricsService kugou, IYouTubeMusicLyricsService youTubeMusic,
        ILrcRedLyricsService lrcRed, IUnisonLyricsService unison, ILrcMuxLyricsService? lrcMux = null,
        IGeniusLyricsService? genius = null)
    {
        var list = new List<OnlineLyricsSource>
        {
            new(Musixmatch, s => s.MusixmatchEnabled, musixmatch.SearchLyricsAsync),
            new(LrcRed, s => s.LrcRedEnabled, lrcRed.SearchLyricsAsync),
            new(Unison, s => s.UnisonEnabled, unison.SearchLyricsAsync),
            new(Kugou, s => s.KugouEnabled, kugou.SearchLyricsAsync),
            new(YouTubeMusic, s => s.YouTubeMusicLyricsEnabled, youTubeMusic.SearchLyricsAsync),
        };
        if (genius is not null)
            list.Add(new(Genius, s => s.GeniusEnabled, genius.SearchLyricsAsync));
        if (lrcMux is not null)
            list.Add(new(LrcMux, s => s.LrcMuxEnabled, lrcMux.SearchLyricsAsync) { MinInterval = TimeSpan.FromSeconds(1.1) });
        return list;
    }
}

/// <summary>One source's answer in a lyrics search: what it found (null = nothing), or that it could not
/// answer — <paramref name="Busy"/> when it said it was busy (503/429) rather than being unreachable.</summary>
public sealed record LyricsSourceHit(string Source, LrcLibResult? Result, bool Errored, bool Busy = false);
