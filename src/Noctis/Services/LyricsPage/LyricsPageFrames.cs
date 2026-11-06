namespace Noctis.Services.LyricsPage;

/// <summary>
/// The Lyrics Page video's virtual clock. Every time is computed from the frame index
/// (never accumulated), so frame N always shows the same moment and long clips don't
/// drift against the audio cut on the same <see cref="ShareClipTiming"/>.
/// </summary>
public static class LyricsPageFrames
{
    public const int Fps = 60;

    /// <summary>Words light this far ahead of the audio, as on the card path (ShareCardRenderer's 0.08 s lookahead).</summary>
    public const double WordLookaheadSeconds = 0.08;

    /// <summary>Frames needed to cover the clip; at least one so a zero-length clip still renders.</summary>
    public static int Count(ShareClipTiming timing, int fps) =>
        Math.Max(1, (int)Math.Ceiling(fps * timing.DurationSeconds));

    /// <summary>Song position drawn on <paramref name="frame"/>, including the word lookahead.</summary>
    public static double TrackTime(int frame, int fps, ShareClipTiming timing) =>
        timing.StartSeconds + (double)frame / fps + WordLookaheadSeconds;

    /// <summary>Seconds since the clip began at <paramref name="frame"/>.</summary>
    public static double ClipTime(int frame, int fps) => (double)frame / fps;
}
