using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Services;
using Noctis.Services.AudioAnalysis;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "Is this really lossless?" (owner 11-26): <see cref="SourceQualityDetector"/> on synthetic
/// spectrograms, the fake-24-bit accumulator, and (where ffmpeg is installed) real files made
/// with ffmpeg: a FLAC, MP3 128/320 converted back to FLAC, a 96 kHz upsample, and a 16-bit
/// file padded to 24-bit.
/// </summary>
public class SourceQualityDetectorTests
{
    private readonly ITestOutputHelper _o;
    public SourceQualityDetectorTests(ITestOutputHelper o) => _o = o;

    private static readonly SourceQualityInput Flac = new(true, "FLAC", 16, 900);

    /// <summary>Noise whose per-bin level is <paramref name="levelAt"/>(Hz) with a few dB of
    /// frame-to-frame jitter (as a real STFT of noise has).</summary>
    private static SpectrogramData Synth(int sampleRate, Func<double, double> levelAt, int columns = 200,
        Func<int, bool>? silentColumn = null, int seed = 7)
    {
        var rng = new Random(seed);
        int bins = SpectrogramRenderer.Bins;
        double binHz = sampleRate / 2.0 / bins;
        var db = new float[columns * bins];
        for (int c = 0; c < columns; c++)
        for (int k = 0; k < bins; k++)
        {
            double v = silentColumn?.Invoke(c) == true ? -120 : levelAt(k * binHz) + (rng.NextDouble() * 9 - 6);
            db[c * bins + k] = (float)Math.Clamp(v, -120, 0);
        }
        return new SpectrogramData { Columns = columns, Bins = bins, Db = db, SampleRate = sampleRate, Duration = TimeSpan.FromSeconds(60) };
    }

    private static Func<double, double> Wall(double cutoffHz, double level = -50, double floor = -120)
        => hz => hz < cutoffHz ? level : floor;

    [Fact]
    public void NoiseToNyquist_LooksGenuine()
    {
        var r = SourceQualityDetector.Analyze(Synth(44100, _ => -50), Flac);
        Assert.Equal(SourceQualityVerdict.LooksGenuine, r.Verdict);
        Assert.False(r.CutoffWellBelowNyquist);
    }

    [Theory]
    [InlineData(16000, LossyCutoffClass.Mp3Low)]
    [InlineData(19500, LossyCutoffClass.Mp3High)]
    [InlineData(18200, LossyCutoffClass.Mp3Mid)]
    public void BrickWall_InALosslessFile_IsProbablyLossy(double cutoffHz, LossyCutoffClass cls)
    {
        var r = SourceQualityDetector.Analyze(Synth(44100, Wall(cutoffHz)), Flac);
        Assert.Equal(SourceQualityVerdict.ProbablyLossySource, r.Verdict);
        Assert.Equal(cls, r.CutoffClass);
        Assert.InRange(r.CutoffHz!.Value, cutoffHz - 500, cutoffHz + 500);
        Assert.True(r.BandAboveEmpty);
        Assert.True(r.CutoffWellBelowNyquist);
    }

    [Fact]
    public void BrickWall_OverADitherFloor_IsStillFound()
    {
        // MP3 decoded to 16-bit with dither: the "empty" band is noise at ~-112 dB, not -120.
        var r = SourceQualityDetector.Analyze(Synth(44100, Wall(16000, level: -55, floor: -112)), Flac);
        Assert.Equal(SourceQualityVerdict.ProbablyLossySource, r.Verdict);
        Assert.InRange(r.CutoffHz!.Value, 15500, 16500);
    }

    [Fact]
    public void HiRes_EmptyAbove22k_IsProbablyUpsampled()
    {
        var r = SourceQualityDetector.Analyze(Synth(96000, Wall(22050)), new SourceQualityInput(true, "FLAC", 24, 3000));
        Assert.Equal(SourceQualityVerdict.ProbablyUpsampled, r.Verdict);
        Assert.InRange(r.CutoffHz!.Value, 21550, 22550);
    }

    [Fact]
    public void HiRes_ContentToTheTop_LooksGenuine()
    {
        var r = SourceQualityDetector.Analyze(Synth(96000, hz => -45 - hz / 1000 * 0.8), new SourceQualityInput(true, "FLAC", 24, 3000));
        Assert.Equal(SourceQualityVerdict.LooksGenuine, r.Verdict);
    }

    [Fact]
    public void MostlySilent_CantTell()
    {
        var r = SourceQualityDetector.Analyze(Synth(44100, Wall(16000), silentColumn: c => c % 20 != 0), Flac);
        Assert.Equal(SourceQualityVerdict.CantTell, r.Verdict);
        Assert.Null(r.CutoffHz);
    }

    [Fact]
    public void GentleNaturalRollOff_IsNotFlagged()
    {
        // -40 dB at DC falling ~2.8 dB/kHz to ~-102 dB at Nyquist: an old/dark recording.
        var r = SourceQualityDetector.Analyze(Synth(44100, hz => -40 - hz / 1000 * 2.8), Flac);
        Assert.NotEqual(SourceQualityVerdict.ProbablyLossySource, r.Verdict);
        Assert.True(r.DropDbPerKhz < SourceQualityDetector.SteepDbPerKhz, $"drop {r.DropDbPerKhz:0.0} dB/kHz");
    }

    [Fact]
    public void BandLimited_Below15k_CantTell()
    {
        var r = SourceQualityDetector.Analyze(Synth(44100, Wall(11000)), Flac);
        Assert.Equal(SourceQualityVerdict.CantTell, r.Verdict);
    }

    [Fact]
    public void Mp3At320_With16kShelf_IsProbablyReencoded()
    {
        var r = SourceQualityDetector.Analyze(Synth(44100, Wall(16000)), new SourceQualityInput(false, "MP3", 0, 320));
        Assert.Equal(SourceQualityVerdict.ProbablyReencoded, r.Verdict);
        Assert.InRange(r.CutoffHz!.Value, 15500, 16500);
    }

    [Fact]
    public void Mp3At320_With20kShelf_MatchesItsBitrate()
    {
        var r = SourceQualityDetector.Analyze(Synth(44100, Wall(20000)), new SourceQualityInput(false, "MP3", 0, 320));
        Assert.Equal(SourceQualityVerdict.MatchesBitrate, r.Verdict);
    }

    [Fact]
    public void Mp3At128_With16kShelf_MatchesItsBitrate()
    {
        var r = SourceQualityDetector.Analyze(Synth(44100, Wall(16000)), new SourceQualityInput(false, "MP3", 0, 128));
        Assert.Equal(SourceQualityVerdict.MatchesBitrate, r.Verdict);
    }

    [Fact]
    public void Describe_UsesLikelihoodWords_AndAddsThePadded24Note()
    {
        var r = SourceQualityDetector.Analyze(Synth(44100, Wall(16000)), Flac);
        var (text, detail, tone) = SpectrogramViewModel.DescribeVerdict(r, padded16: true);
        Assert.Equal(Noctis.Localization.Loc.T("Spectrogram.Verdict.ProbablyLossy"), text);
        Assert.Contains("MP3 128", detail);
        Assert.Contains(Noctis.Localization.Loc.T("Spectrogram.Verdict.Padded24"), detail);
        Assert.Equal(-1, tone);
        foreach (var word in new[] { "authentic", "verified", "fake" })
            Assert.DoesNotContain(word, (text + detail).ToLowerInvariant());
    }

    // ── Fake 24-bit ──

    [Fact]
    public void LowBits_AllZero_MeansPadded16()
    {
        var acc = new BitDepthProbe.LowBitsAccumulator(24);
        var samples = new int[10000];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(i * 37 % 30000 - 15000) << 16;
        acc.Add(samples);
        Assert.True(acc.Result);

        var real = new BitDepthProbe.LowBitsAccumulator(24);
        for (int i = 0; i < samples.Length; i++) samples[i] = (i * 7919 % 8000000 - 4000000) << 8;
        real.Add(samples);
        Assert.False(real.Result);

        var quiet = new BitDepthProbe.LowBitsAccumulator(24);
        quiet.Add(new int[100000]);
        Assert.Null(quiet.Result);
    }

    // ── Real files made with ffmpeg ──

    private static string? Ffmpeg() => new AudioConverterService(() => string.Empty, new MetadataService()).GetFfmpegPath();

    private static void Run(string ffmpeg, params string[] args)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y" }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"ffmpeg {string.Join(' ', args)}: {err}");
    }

    private async Task<SourceQualityResult> Measure(string ffmpeg, string file, int rate, SourceQualityInput info, string name)
    {
        var data = await SpectrogramRenderer.ComputeAsync(ffmpeg, file, rate, TimeSpan.FromSeconds(20),
            SpectrogramViewModel.PlotWidth, null, CancellationToken.None);
        var r = SourceQualityDetector.Analyze(data, info);
        _o.WriteLine($"{name}: {r.Verdict} cutoff={r.CutoffHz / 1000:0.00} kHz drop={r.DropDbPerKhz:0.0} dB/kHz " +
                     $"empty={r.BandAboveEmpty} floor={r.NoiseFloorDb:0.0} active={r.ActiveColumns}");
        return r;
    }

    [Fact]
    public async Task RealFiles_MadeWithFfmpeg_GetTheExpectedVerdicts()
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg == null) Assert.Skip("ffmpeg not installed");

        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "srcq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string P(string n) => Path.Combine(dir, n);
            // Source: pink noise + a slow log sweep up to 21 kHz, made in float, kept at 24-bit.
            Run(ffmpeg, "-f", "lavfi", "-i", "anoisesrc=d=20:c=pink:r=44100:a=0.35",
                "-f", "lavfi", "-i", "sine=f=440:r=44100:d=20",
                "-filter_complex", "[0][1]amix=inputs=2:normalize=0,aformat=sample_fmts=s32:channel_layouts=stereo",
                "-c:a", "flac", "-bits_per_raw_sample", "24", P("src24.flac"));
            Run(ffmpeg, "-i", P("src24.flac"), "-sample_fmt", "s16", "-c:a", "flac", P("src.flac"));
            Run(ffmpeg, "-i", P("src.flac"), "-c:a", "libmp3lame", "-b:a", "128k", P("a128.mp3"));
            Run(ffmpeg, "-i", P("a128.mp3"), "-sample_fmt", "s16", "-c:a", "flac", P("a128.flac"));
            Run(ffmpeg, "-i", P("src.flac"), "-c:a", "libmp3lame", "-b:a", "320k", P("a320.mp3"));
            Run(ffmpeg, "-i", P("a320.mp3"), "-sample_fmt", "s16", "-c:a", "flac", P("a320.flac"));
            Run(ffmpeg, "-i", P("a128.mp3"), "-c:a", "libmp3lame", "-b:a", "320k", P("a128to320.mp3"));
            Run(ffmpeg, "-i", P("src.flac"), "-ar", "96000", "-sample_fmt", "s32", "-c:a", "flac", "-bits_per_raw_sample", "24", P("up96.flac"));
            Run(ffmpeg, "-i", P("src.flac"), "-sample_fmt", "s32", "-c:a", "flac", "-bits_per_raw_sample", "24", P("pad24.flac"));
            Run(ffmpeg, "-i", P("src24.flac"), "-ar", "48000", "-c:a", "flac", P("src48.flac"));
            Run(ffmpeg, "-i", P("src.flac"), "-c:a", "aac", "-b:a", "256k", P("aac256.m4a"));
            Run(ffmpeg, "-i", P("aac256.m4a"), "-sample_fmt", "s16", "-c:a", "flac", P("aac256.flac"));
            // A dark source: brown noise (-6 dB/octave), the highs far down but present.
            Run(ffmpeg, "-f", "lavfi", "-i", "anoisesrc=d=20:c=brown:r=44100:a=0.5", "-ac", "2", "-sample_fmt", "s16", "-c:a", "flac", P("brown.flac"));
            Run(ffmpeg, "-i", P("src48.flac"), "-ar", "96000", "-sample_fmt", "s32", "-c:a", "flac", "-bits_per_raw_sample", "24", P("up96from48.flac"));

            var flac16 = new SourceQualityInput(true, "FLAC", 16, 1000);
            Assert.Equal(SourceQualityVerdict.LooksGenuine, (await Measure(ffmpeg, P("src.flac"), 44100, flac16, "noise flac")).Verdict);

            var r128 = await Measure(ffmpeg, P("a128.flac"), 44100, flac16, "mp3 128 -> flac");
            Assert.Equal(SourceQualityVerdict.ProbablyLossySource, r128.Verdict);
            var r320 = await Measure(ffmpeg, P("a320.flac"), 44100, flac16, "mp3 320 -> flac");
            Assert.Equal(SourceQualityVerdict.ProbablyLossySource, r320.Verdict);
            Assert.True(r320.CutoffHz > r128.CutoffHz);

            Assert.Equal(SourceQualityVerdict.MatchesBitrate,
                (await Measure(ffmpeg, P("a320.mp3"), 44100, new SourceQualityInput(false, "MP3", 0, 320), "mp3 320")).Verdict);
            Assert.Equal(SourceQualityVerdict.MatchesBitrate,
                (await Measure(ffmpeg, P("a128.mp3"), 44100, new SourceQualityInput(false, "MP3", 0, 128), "mp3 128")).Verdict);
            Assert.Equal(SourceQualityVerdict.ProbablyReencoded,
                (await Measure(ffmpeg, P("a128to320.mp3"), 44100, new SourceQualityInput(false, "MP3", 0, 320), "mp3 128 -> 320")).Verdict);

            Assert.Equal(SourceQualityVerdict.ProbablyUpsampled,
                (await Measure(ffmpeg, P("up96.flac"), 96000, new SourceQualityInput(true, "FLAC", 24, 3000), "44.1 -> 96 kHz")).Verdict);
            Assert.Equal(SourceQualityVerdict.ProbablyUpsampled,
                (await Measure(ffmpeg, P("up96from48.flac"), 96000, new SourceQualityInput(true, "FLAC", 24, 3000), "48 -> 96 kHz")).Verdict);

            var aac = await Measure(ffmpeg, P("aac256.flac"), 44100, flac16, "aac 256 -> flac");
            var brown = await Measure(ffmpeg, P("brown.flac"), 44100, flac16, "brown noise flac");
            Assert.NotEqual(SourceQualityVerdict.ProbablyLossySource, brown.Verdict);

            var padded = await BitDepthProbe.IsPadded16BitAsync(ffmpeg, P("pad24.flac"), 24, TimeSpan.FromSeconds(20), CancellationToken.None);
            var real = await BitDepthProbe.IsPadded16BitAsync(ffmpeg, P("src24.flac"), 24, TimeSpan.FromSeconds(20), CancellationToken.None);
            _o.WriteLine($"padded 16->24: {padded}, real 24: {real}");
            Assert.True(padded);
            Assert.False(real);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
