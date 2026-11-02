using System.Diagnostics;
using Noctis.Plugins.Chords.Analysis;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Chords plugin: accuracy measured on synthesized audio with a known chord at every instant
/// (<see cref="ChordSynth"/>). Frame accuracy = share of 10 ms instants where the estimate names
/// the true chord. The numbers are written to the test output; docs/PLUGINS.md (Chords) quotes them.
/// Synthetic audio is far easier than a mixed record (no vocals, no passing notes, exact chords),
/// so these are upper bounds of what to expect on real songs, and the plugin calls its result an estimate.
/// </summary>
public class ChordsAccuracyTests
{
    private readonly ITestOutputHelper _out;

    public ChordsAccuracyTests(ITestOutputHelper output) => _out = output;

    private double Measure(string name, List<(Chord Chord, double Seconds)> progression, ChordSynth.Options synth,
        ChordRecognizerOptions? options = null, bool triadsOnly = true)
    {
        var audio = ChordSynth.Render(progression, synth);
        var watch = Stopwatch.StartNew();
        var analysis = ChordAnalyzer.Analyze(audio, synth.SampleRate, options);
        watch.Stop();
        var accuracy = ChordSynth.Accuracy(analysis, progression, triadsOnly);
        _out.WriteLine($"{name}: frame accuracy {accuracy:P1} over {audio.Length / (double)synth.SampleRate:0} s; key {analysis.Key?.Name ?? "-"}; " +
                       $"tuning {Math.Round(analysis.TuningCents):+0;-0;0} cents; {analysis.Segments.Count} segments; {watch.ElapsedMilliseconds} ms");
        _out.WriteLine("  " + string.Join(" ", analysis.Segments.Take(12).Select(s => $"{s.Chord.Name(analysis.Flats)}@{s.Start:0.0}")));
        return accuracy;
    }

    [Fact]
    public void PopProgression_Pad_C_G_Am_F()
    {
        var p = ChordSynth.Progression("C G Am F", 2, times: 4);
        Assert.True(Measure("C G Am F, pad", p, new ChordSynth.Options()) >= 0.95);
    }

    [Fact]
    public void PopProgression_Piano_Drums_Noise_Detuned()
    {
        var p = ChordSynth.Progression("C G Am F", 2, times: 4);
        var o = new ChordSynth.Options { Voice = ChordSynth.Voice.Piano, Drums = true, NoiseSnrDb = 15, TuningCents = 30 };
        Assert.True(Measure("C G Am F, piano + drums + noise 15 dB SNR, +30 cents", p, o) >= 0.90);
    }

    /// <summary>The hardest synthetic case: a melody of scale notes over the chords (most not in
    /// the chord), inversions in the bass, drums and noise.</summary>
    [Fact]
    public void PopProgression_Melody_Inversions_Drums_Noise()
    {
        var p = ChordSynth.Progression("C G Am F", 2, times: 4);
        var o = new ChordSynth.Options { Voice = ChordSynth.Voice.Piano, Drums = true, NoiseSnrDb = 20, MelodyScale = 0, Inversions = true };
        Assert.True(Measure("C G Am F, piano + melody + inversions + drums + noise 20 dB", p, o) >= 0.80);
    }

    [Fact]
    public void MinorProgression_WithItsMajorDominant()
    {
        var p = ChordSynth.Progression("Am Dm E Am F G E Am", 2, times: 2);
        Assert.True(Measure("Am Dm E Am F G E Am, piano", p, new ChordSynth.Options { Voice = ChordSynth.Voice.Piano }) >= 0.92);
    }

    [Fact]
    public void AllTwentyFourTriads_OneSecondEach()
    {
        var random = new Random(3);
        var chords = Enumerable.Range(0, 12).SelectMany(r => new[] { new Chord(r, ChordQuality.Major), new Chord(r, ChordQuality.Minor) })
            .OrderBy(_ => random.Next()).ToList();
        var p = chords.Select(c => (c, 1.0)).ToList();
        Assert.True(Measure("all 24 triads, 1 s each, pad", p, new ChordSynth.Options()) >= 0.85);
    }

    [Fact]
    public void Sevenths_ii_V_I_vi()
    {
        var p = ChordSynth.Progression("Dm7 G7 Cmaj7 Am7", 2, times: 4);
        var sevenths = Measure("Dm7 G7 Cmaj7 Am7, sevenths on (exact chord)", p, new ChordSynth.Options(),
            new ChordRecognizerOptions { Sevenths = true }, triadsOnly: false);
        var triads = Measure("Dm7 G7 Cmaj7 Am7, sevenths off (scored as Dm G C Am)", p, new ChordSynth.Options());
        Assert.True(sevenths >= 0.80);
        Assert.True(triads >= 0.80);
    }

    [Fact]
    public void Silence_IsNoChord_AndKeyIsFound()
    {
        var p = new List<(Chord, double)> { (Chord.NoChord, 2) };
        p.AddRange(ChordSynth.Progression("G D Em C", 2, times: 3));
        p.Add((Chord.NoChord, 2));
        var accuracy = Measure("silence, G D Em C, silence", p, new ChordSynth.Options());
        Assert.True(accuracy >= 0.93);
        var analysis = ChordAnalyzer.Analyze(ChordSynth.Render(p), ChordAnalyzer.SampleRate);
        Assert.True(analysis.ChordAt(0.5).IsNone);
        Assert.True(analysis.ChordAt(analysis.Duration - 0.5).IsNone);
        Assert.Equal("G major", analysis.Key?.Name);
    }

    [Theory]
    [InlineData("C G Am F", "C major")]
    [InlineData("Am Dm E Am", "A minor")]
    [InlineData("F Bb C F", "F major")]
    [InlineData("Em C G D Em", "E minor")]
    [InlineData("Eb Ab Bb Eb", "Eb major")]
    public void Key_IsEstimated(string chords, string key)
    {
        var p = ChordSynth.Progression(chords, 2, times: 3);
        var analysis = ChordAnalyzer.Analyze(ChordSynth.Render(p, new ChordSynth.Options { Voice = ChordSynth.Voice.Piano }), ChordAnalyzer.SampleRate);
        _out.WriteLine($"{chords}: key {analysis.Key?.Name}");
        Assert.Equal(key, analysis.Key?.Name);
    }

    [Fact]
    public void FlatKeys_SpellChordsWithFlats()
    {
        var p = ChordSynth.Progression("Eb Ab Bb Eb", 2, times: 3);
        var analysis = ChordAnalyzer.Analyze(ChordSynth.Render(p), ChordAnalyzer.SampleRate);
        Assert.True(analysis.Flats);
        Assert.Contains("Ab", analysis.Segments.Select(s => s.Chord.Name(analysis.Flats)));
        Assert.DoesNotContain("G#", analysis.Segments.Select(s => s.Chord.Name(analysis.Flats)));
    }

    /// <summary>CPU: a 4-minute song analyses in well under the time it takes to play (measured, printed).</summary>
    [Fact]
    public void FourMinuteSong_AnalysesQuickly()
    {
        var p = ChordSynth.Progression("C G Am F Dm Em", 2, times: 20); // 240 s
        var audio = ChordSynth.Render(p);
        var watch = Stopwatch.StartNew();
        var analysis = ChordAnalyzer.Analyze(audio, ChordAnalyzer.SampleRate);
        watch.Stop();
        _out.WriteLine($"240 s of audio analysed in {watch.ElapsedMilliseconds} ms, {analysis.Segments.Count} segments");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Analysis_IsCancellable()
    {
        var audio = ChordSynth.Render(ChordSynth.Progression("C G", 2, times: 10));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ChordAnalyzer.Analyze(audio, ChordAnalyzer.SampleRate, ct: cts.Token));
    }

    [Fact]
    public void Segments_CoverTheSongWithoutGaps()
    {
        var p = ChordSynth.Progression("C G Am F", 1.5, times: 3);
        var analysis = ChordAnalyzer.Analyze(ChordSynth.Render(p, new ChordSynth.Options { Voice = ChordSynth.Voice.Piano, Drums = true }), ChordAnalyzer.SampleRate);
        Assert.Equal(0, analysis.Segments[0].Start);
        Assert.Equal(analysis.Duration, analysis.Segments[^1].End, 3);
        for (var i = 1; i < analysis.Segments.Count; i++)
        {
            Assert.Equal(analysis.Segments[i - 1].End, analysis.Segments[i].Start);
            Assert.NotEqual(analysis.Segments[i - 1].Chord, analysis.Segments[i].Chord);
        }
        Assert.All(analysis.Segments, s => Assert.InRange(s.Confidence, 0, 1));
    }

    [Fact]
    public void EmptyAndTinyInputs_DoNotThrow()
    {
        Assert.Empty(ChordAnalyzer.Analyze(Array.Empty<float>(), ChordAnalyzer.SampleRate).Segments);
        var tiny = ChordAnalyzer.Analyze(new float[100], ChordAnalyzer.SampleRate);
        Assert.All(tiny.Segments, s => Assert.True(s.Chord.IsNone));
        Assert.Null(tiny.Key);
    }

    [Theory]
    [InlineData("C")]
    [InlineData("F#m")]
    [InlineData("Bb7")]
    [InlineData("Ebmaj7")]
    [InlineData("C#m7")]
    [InlineData("N.C.")]
    public void ChordNames_RoundTrip(string name)
    {
        Assert.True(Chord.TryParse(name, out var chord));
        Assert.Equal(name, chord.Name(name.Contains('b')));
    }
}
