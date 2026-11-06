using SkiaSharp;

namespace Noctis.Services.LyricsPage;

/// <summary>
/// The Lyrics Page video's export side: the frame sequence for the clip encoder and the still
/// picture. CPU and ffmpeg-pipe work only (a clip background blocks for each frame): call off
/// the UI thread.
/// </summary>
public static class LyricsPageRenderer
{
    /// <summary>
    /// Writes the clip's frames as frame-00000.jpg… into <paramref name="frameDir"/> (the karaoke
    /// card's naming, so the same ffmpeg pattern muxes them): frame N shows track time
    /// <see cref="LyricsPageFrames.TrackTime"/>. Progress every 12 frames and on the last;
    /// <paramref name="ct"/> is checked before each frame.
    /// </summary>
    public static void RenderFrames(LyricsPageSpec spec, ShareClipTiming timing, int fps, string frameDir,
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        using var animator = new LyricsPageAnimator(spec, 1f, null, blockingBackground: true, backgroundFps: fps);
        int total = LyricsPageFrames.Count(timing, fps);
        for (int frame = 0; frame < total; frame++)
        {
            ct.ThrowIfCancellationRequested();
            animator.Draw(LyricsPageFrames.TrackTime(frame, fps, timing));

            // JPEG q92 as the card path: encoding is the per-frame bottleneck and the video
            // encode is lossy anyway.
            using var img = animator.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 92);
            using var fs = File.Create(Path.Combine(frameDir, $"frame-{frame:D5}.jpg"));
            data.SaveTo(fs);

            if (frame % 12 == 0 || frame == total - 1)
                progress?.Invoke(frame + 1, total);
        }
    }

    /// <summary>A clip background under the still decodes one frame a second: ffmpeg pipes ~t
    /// frames to reach time t, not 60 per second of it at full size.</summary>
    internal const int StillBackgroundFps = 1;

    /// <summary>The frame at track time <paramref name="tSeconds"/> as a PNG, at <paramref name="scale"/>
    /// (a clip background shows its frame at the whole second). <paramref name="ct"/> is checked
    /// before the draw and before the encode.</summary>
    public static byte[] RenderStillPng(LyricsPageSpec spec, double tSeconds, float scale = 1f, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var animator = new LyricsPageAnimator(spec, scale, null, blockingBackground: true, backgroundFps: StillBackgroundFps);
        animator.Draw(tSeconds);
        ct.ThrowIfCancellationRequested();
        using var img = animator.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// When the still is taken: halfway through the first line — up to the next line's start
    /// (<paramref name="nextLineStartSeconds"/>) or the clip end, whichever comes first — with
    /// the word lookahead, so its words are half lit.
    /// </summary>
    public static double StillTime(ShareClipTiming timing, double? nextLineStartSeconds)
    {
        double span = nextLineStartSeconds is { } next ? next - timing.StartSeconds : timing.DurationSeconds;
        return timing.StartSeconds + Math.Min(span, timing.DurationSeconds) / 2 + LyricsPageFrames.WordLookaheadSeconds;
    }
}
