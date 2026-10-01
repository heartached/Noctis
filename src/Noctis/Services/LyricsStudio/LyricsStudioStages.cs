namespace Noctis.Services.LyricsStudio;

/// <summary>The steps of one song's run, in order.</summary>
public enum LyricsStudioStage { FindingLyrics, Decoding, Listening, Aligning, Done }

/// <summary>
/// How much of a song's progress bar each step fills. Listening (the speech model) is nearly all
/// of a run — seconds to minutes per song on Medium — while ffmpeg decodes a song in about a
/// second and alignment is quicker still, so they get thin slices at either end.
/// </summary>
public static class LyricsStudioStages
{
    private static readonly (double Start, double Span)[] Spans =
    {
        (0.00, 0.02), // FindingLyrics
        (0.02, 0.08), // Decoding
        (0.10, 0.85), // Listening
        (0.95, 0.05), // Aligning
        (1.00, 0.00), // Done
    };

    /// <summary>The whole song's progress, 0–1, for a step and how far into it the run is.</summary>
    public static double Overall(LyricsStudioStage stage, double stageFraction)
    {
        var (start, span) = Spans[(int)stage];
        return start + span * (double.IsFinite(stageFraction) ? Math.Clamp(stageFraction, 0, 1) : 0);
    }
}
