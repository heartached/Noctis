namespace Noctis.Plugins.Chords.Analysis;

/// <summary>Mono PCM in, chord estimate out: <see cref="Chromagram"/> then <see cref="ChordRecognizer"/>. Pure, no I/O.</summary>
public static class ChordAnalyzer
{
    /// <summary>Bumped whenever the analysis changes what it finds: cached results from another version are redone.</summary>
    public const int EngineVersion = 1;

    /// <summary>The rate the plugin asks Noctis to decode at: enough for chords (spectrum up to 5.5 kHz) and light on memory.</summary>
    public const int SampleRate = Chromagram.DefaultRate;

    public static ChordAnalysis Analyze(ReadOnlySpan<float> samples, int sampleRate, ChordRecognizerOptions? options = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        options ??= new ChordRecognizerOptions();
        var chroma = Chromagram.Compute(samples, sampleRate, options.Harmonics, progress, ct);
        var result = ChordRecognizer.Recognize(chroma, options, ct);
        progress?.Report(1);
        return result;
    }
}
