namespace Noctis.Services.LyricsPage;

/// <summary>
/// The track time the share dialog's live Lyrics Page preview shows: the playback position
/// while it follows playback (Sync on, inside the clip), otherwise the clip looping from its start. Both carry the word
/// lookahead, as every exported frame does (<see cref="LyricsPageFrames.TrackTime"/>).
/// The caller owns the clock (a Stopwatch in the view-model); this stays deterministic.
/// </summary>
public static class PagePreviewClock
{
    public static double TrackTime(bool followPlayback, double playbackSeconds, double elapsedSeconds, ShareClipTiming timing)
    {
        if (followPlayback) return playbackSeconds + LyricsPageFrames.WordLookaheadSeconds;
        double duration = timing.DurationSeconds;
        double into = duration > 0 ? elapsedSeconds % duration : 0;
        if (into < 0) into += duration;
        return timing.StartSeconds + into + LyricsPageFrames.WordLookaheadSeconds;
    }
}
