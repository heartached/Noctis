using Noctis.Models;

namespace Noctis.Services.LyricsPage;

/// <summary>What fills the Lyrics Page video behind the lyrics.</summary>
public enum LyricsPageBackgroundKind { ArtworkWash, AnimatedCover, Custom }

/// <summary>
/// One lyric line of the Lyrics Page video, as the share dialog hands it over. A line with
/// no <see cref="StartSeconds"/> is unsynced and is left out of the video.
/// </summary>
public sealed record LyricsPageLine
{
    public required string Text { get; init; }
    public double? StartSeconds { get; init; }

    /// <summary>Line end (ELRC/Lyricsfile end tags); lets a line without words bound an interlude gap.</summary>
    public double? EndSeconds { get; init; }

    public IReadOnlyList<KaraokeWord>? Words { get; init; }

    /// <summary>Background vocals / adlibs, drawn as a smaller row under the line.</summary>
    public IReadOnlyList<KaraokeWord>? BackgroundWords { get; init; }

    public LyricVoice Voice { get; init; }

    /// <summary>The line carries only a background vocal row (no main words).</summary>
    public bool IsBackgroundOnly { get; init; }
}

/// <summary>Everything the Lyrics Page video renderer needs for one export.</summary>
public sealed record LyricsPageSpec
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string? ArtworkPath { get; init; }
    public bool IsExplicit { get; init; }
    public ShareCardFormat Format { get; init; } = ShareCardFormat.Story;
    public required IReadOnlyList<LyricsPageLine> Lines { get; init; }
    public bool SafeZones { get; init; }
    public LyricsPageBackgroundKind BackgroundKind { get; init; }
    public string? BackgroundMediaPath { get; init; }
    public int BackgroundStrength { get; init; } = 70;
    public string? FfmpegPath { get; init; }
    public double ClipStartSeconds { get; init; }
}
