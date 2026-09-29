using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Opt-in end-to-end check of the per-song progress plumbing (09-29): real ffmpeg decode and a
/// real Whisper model through <see cref="LyricsStudioEngine"/>, on a generated tone (no music
/// files involved). Set NOCTIS_STUDIO_REALRUN_MODEL to a ggml-medium.bin; the test data dir must
/// be on the model's drive (run with TMP pointing there) since the model is hard-linked in.
/// Writes the report timeline to NOCTIS_RENDER_OUT (or the temp dir) as realrun-progress.txt.
/// </summary>
public class LyricsStudioRealRunProbeTests
{
    private readonly ITestOutputHelper _out;
    public LyricsStudioRealRunProbeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Probe_RealRun_ReportsEveryStepInOrder()
    {
        var model = Environment.GetEnvironmentVariable("NOCTIS_STUDIO_REALRUN_MODEL");
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model)) return;
        var ffmpeg = new AudioConverterService(() => string.Empty, new MetadataService());
        var ffmpegPath = ffmpeg.GetFfmpegPath();
        if (ffmpegPath is null) return;

        using var persistence = new TestPersistenceService();
        var engine = new LyricsStudioEngine(ffmpeg, new NoLrcLib(), persistence);
        Directory.CreateDirectory(engine.Models.Directory);
        Run("cmd.exe", $"/c mklink /H \"{engine.Models.ModelPath}\" \"{model}\"");
        Assert.True(engine.Models.IsInstalled(), "hard link failed: is the temp dir on the model's drive?");

        var audio = Path.Combine(persistence.DataDirectory, "tone.flac");
        Run(ffmpegPath, $"-hide_banner -loglevel error -f lavfi -i \"sine=frequency=220:duration=95\" -ar 44100 \"{audio}\"");
        var track = new Track { Title = "tone", Artist = "probe", FilePath = audio, Duration = TimeSpan.FromSeconds(95) };

        var clock = Stopwatch.StartNew();
        var reports = new List<(TimeSpan At, LyricsStudioStage Stage, double Fraction)>();
        var meter = new SongProgressMeter(track.Duration, TimeSpan.Zero);
        var progress = new InlineProgress<LyricsStudioProgress>(p =>
        {
            lock (reports) reports.Add((clock.Elapsed, p.Stage, p.StageFraction));
            meter.Report(p.Stage, p.StageFraction, clock.Elapsed);
        });
        var readings = new List<(TimeSpan At, LyricsStudioStage Stage, double Overall)>();
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                var (stage, overall) = meter.Read(clock.Elapsed);
                lock (readings) readings.Add((clock.Elapsed, stage, overall));
                try { await Task.Delay(66, sampling.Token); } catch (OperationCanceledException) { }
            }
        });

        var open = Stopwatch.StartNew();
        using (engine.OpenSession(WhisperModelSize.Medium))
        {
            open.Stop();
            var options = new LyricsStudioOptions(WhisperModelSize.Medium, "en", AllowOnlineLyrics: false,
                SourceLines: new[] { "probe line one", "probe line two" }, AllowTranscription: false);
            await engine.ProcessAsync(track, options, progress, CancellationToken.None);
        }
        sampling.Cancel();
        await sampler;
        var final = meter.Read(clock.Elapsed);

        var lines = new List<string> { $"reports={reports.Count} readings={readings.Count} total={clock.Elapsed.TotalSeconds:0.0}s openSession={open.Elapsed.TotalSeconds:0.0}s" };
        lines.AddRange(reports.Select(r => $"report  {r.At.TotalSeconds,7:0.00}s  {r.Stage,-13} {r.Fraction:0.000}"));
        lines.AddRange(readings.Where((_, i) => i % 5 == 0).Select(r => $"reading {r.At.TotalSeconds,7:0.00}s  {r.Stage,-13} {r.Overall:0.000}"));
        var outDir = Environment.GetEnvironmentVariable("NOCTIS_RENDER_OUT");
        File.WriteAllLines(Path.Combine(string.IsNullOrWhiteSpace(outDir) ? Path.GetTempPath() : outDir, "realrun-progress.txt"), lines);
        foreach (var l in lines.Take(80)) _out.WriteLine(l);

        // Steps arrive in order, each step's fraction only climbs, and every step reports.
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].Stage >= reports[i - 1].Stage, $"step went back at report {i}");
            if (reports[i].Stage == reports[i - 1].Stage)
                Assert.True(reports[i].Fraction >= reports[i - 1].Fraction, $"{reports[i].Stage} went back at report {i}");
        }
        Assert.True(reports.Count(r => r.Stage == LyricsStudioStage.Decoding) > 5, "decode progress");
        Assert.True(reports.Count(r => r.Stage == LyricsStudioStage.Listening) >= 2, "listening progress");
        Assert.Contains(reports, r => r.Stage == LyricsStudioStage.Aligning);
        Assert.Equal(LyricsStudioStage.Done, reports[^1].Stage);
        // What the bar shows never steps back and ends full.
        Assert.True(readings.Zip(readings.Skip(1)).All(p => p.Second.Overall >= p.First.Overall));
        Assert.Equal(1, final.Overall);
    }

    private static void Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit(60_000);
        Assert.True(p.ExitCode == 0, $"{Path.GetFileName(file)} failed: {p.StandardError.ReadToEnd()}");
    }

    private sealed class NoLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }
}
