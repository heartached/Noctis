using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Plugins;
using Noctis.Plugins.Chords.Analysis;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Plugin API 1.6, "audio.decode" (the Chords plugin's way to the song's sound): gated by its
/// permission, says when there is no ffmpeg, refuses missing files, bounds rate and length, goes
/// inert when the plugin stops, and (with an ffmpeg on this machine) decodes a WAV and an MP3 of
/// synthesized chords to PCM that the chord analysis reads correctly.
/// </summary>
public class PluginAudioDecodeTests : IDisposable
{
    private readonly PluginSandbox _box = new();
    private readonly ITestOutputHelper _out;
    private readonly string _files = Path.Combine(Path.GetTempPath(), "noctis-decode-api-" + Guid.NewGuid().ToString("N")[..8]);

    public PluginAudioDecodeTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_files);
    }

    public void Dispose()
    {
        _box.Dispose();
        try { Directory.Delete(_files, true); } catch { }
    }

    private const string Id = "dev.test.decode";

    private static string M(params string[] permissions) => PluginSandbox.Manifest(id: Id, apiVersion: "1.6", minAppVersion: "1.6.6", permissions: permissions);

    private (ScriptedPlugin Script, LoadedPlugin Plugin, PluginHost Host) Start(Func<string?>? ffmpeg, params string[] permissions)
    {
        _box.Approve((Id, permissions));
        var host = _box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.6");
        host.FfmpegPathSource = ffmpeg;
        var script = new ScriptedPlugin();
        var plugin = _box.AddInProcess(host, script, M(permissions));
        Assert.Equal(PluginStatus.Running, plugin.Status);
        return (script, plugin, host);
    }

    /// <summary>ffmpeg on PATH (the CI and dev machines have one), else null.</summary>
    internal static string? FindFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    [Fact]
    public void Kit_Is16_AndKnowsTheNewPermission()
    {
        Assert.Equal("1.6", PluginApi.Version);
        Assert.Equal((1, 6), (typeof(PluginApi).Assembly.GetName().Version!.Major, typeof(PluginApi).Assembly.GetName().Version!.Minor));
        Assert.Contains("audio.decode", PluginPermissions.All);
        var manifest = PluginManifest.Parse(M("audio.decode"));
        Assert.Equal(new[] { "audio.decode" }, manifest.Permissions);
        Assert.Empty(manifest.Warnings);
        Assert.Null(manifest.CheckCompatibility("1.6.6"));
        Assert.Equal(Loc.T("Plugins.PermShort.AudioDecode"), PluginPermissionText.Short("audio.decode"));
        Assert.NotEqual("audio.decode", PluginPermissionText.Short("audio.decode"));
        Assert.NotEqual("audio.decode", PluginPermissionText.Verb("audio.decode"));
        Assert.Contains("analyze", PluginPermissionText.Describe("audio.decode"));
    }

    [Fact]
    public void OlderHosts_StillCompile_AndSayWhichApiIsMissing()
    {
        IPluginHost old = new OldHost();
        Assert.Contains("1.6", Assert.Throws<NotSupportedException>(() => old.AudioDecoder).Message);
    }

    private sealed class OldHost : IPluginHost
    {
        public string AppVersion => "1.6.5";
        public string DataDirectory => "";
        public INowPlaying NowPlaying => null!;
        public IBeatSource Beat => null!;
        public ISpectrumSource Spectrum => null!;
        public void Log(string message) { }
        public void RegisterVisualLayer(IVisualLayerProvider provider) { }
    }

    [AvaloniaFact]
    public void AudioDecoder_ThrowsWithoutItsPermission()
    {
        var (script, _, _) = Start(() => "ffmpeg", "playback.control");
        Assert.Equal("audio.decode", Assert.Throws<PluginPermissionException>(() => script.Host!.AudioDecoder).Permission);
    }

    [AvaloniaFact]
    public async Task NoFfmpeg_IsNotAvailable_AndDecodeSaysWhy()
    {
        var (script, _, _) = Start(null, "audio.decode");
        var decoder = script.Host!.AudioDecoder;
        Assert.False(decoder.IsAvailable);
        var file = Path.Combine(_files, "a.wav");
        File.WriteAllBytes(file, new byte[] { 1 });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => decoder.DecodeMonoAsync(file, 11025, TimeSpan.FromMinutes(1)));
        Assert.Contains("ffmpeg", ex.Message);
    }

    [AvaloniaFact]
    public async Task MissingOrRelativeFile_IsRefused()
    {
        var (script, _, _) = Start(() => "ffmpeg", "audio.decode");
        var decoder = script.Host!.AudioDecoder;
        await Assert.ThrowsAsync<FileNotFoundException>(() => decoder.DecodeMonoAsync(Path.Combine(_files, "gone.mp3"), 11025, TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<ArgumentException>(() => decoder.DecodeMonoAsync("relative.mp3", 11025, TimeSpan.FromMinutes(1)));
    }

    [AvaloniaFact]
    public async Task StoppedPlugin_CannotDecode()
    {
        var (script, plugin, host) = Start(() => "ffmpeg", "audio.decode");
        var decoder = script.Host!.AudioDecoder;
        host.SetEnabled(plugin, false);
        var file = Path.Combine(_files, "b.wav");
        File.WriteAllBytes(file, new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => decoder.DecodeMonoAsync(file, 11025, TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(100, 0, 8000, 1200)]
    [InlineData(11025, 60, 11025, 60)]
    [InlineData(96000, 3600, 48000, 1200)]
    [InlineData(44100, -5, 44100, 1200)]
    public void RateAndLength_AreBounded(int rate, double seconds, int expectedRate, double expectedSeconds)
    {
        var (r, s) = PluginAudioDecoder.Clamp(rate, TimeSpan.FromSeconds(seconds));
        Assert.Equal((expectedRate, expectedSeconds), (r, s));
        var args = PluginAudioDecoder.Arguments(@"C:\music\song.flac", r, s);
        Assert.Equal(new[] { "-map", "0:a:0", "-ac", "1", "-ar", expectedRate.ToString() }, args.SkipWhile(a => a != "-map").Take(6));
        Assert.Equal("-", args[^1]);
        Assert.Contains("-nostdin", args);
    }

    /// <summary>
    /// End to end through the real host and a real ffmpeg: synthesized C G Am F (piano, 44.1 kHz
    /// 16-bit WAV, and the same as an MP3), decoded by the host to 11025 Hz mono, analysed by the
    /// Chords engine. Prints the frame accuracy of each.
    /// </summary>
    [AvaloniaFact]
    public async Task RealFfmpeg_DecodesWavAndMp3_ThatTheChordAnalysisReads()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) Assert.Skip("no ffmpeg on PATH");
        var (script, _, _) = Start(() => ffmpeg, "audio.decode");
        var decoder = script.Host!.AudioDecoder;
        Assert.True(decoder.IsAvailable);

        var progression = ChordSynth.Progression("C G Am F", 2, times: 3);
        var pcm = ChordSynth.Render(progression, new ChordSynth.Options { SampleRate = 44100, Voice = ChordSynth.Voice.Piano });
        var wav = Path.Combine(_files, "song.wav");
        WriteWav(wav, pcm, 44100);
        var mp3 = Path.Combine(_files, "song.mp3");
        var encoded = await RunAsync(ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-i", wav, "-b:a", "128k", mp3);

        foreach (var file in encoded ? new[] { wav, mp3 } : new[] { wav })
        {
            var audio = await decoder.DecodeMonoAsync(file, ChordAnalyzer.SampleRate, TimeSpan.FromMinutes(5));
            Assert.Equal(ChordAnalyzer.SampleRate, audio.SampleRate);
            Assert.InRange(audio.Duration.TotalSeconds, 23.5, 24.2); // MP3 adds encoder padding
            Assert.All(audio.Samples, s => Assert.InRange(s, -1.01f, 1.01f));
            var analysis = ChordAnalyzer.Analyze(audio.Samples, audio.SampleRate);
            var accuracy = ChordSynth.Accuracy(analysis, progression, triadsOnly: true);
            _out.WriteLine($"{Path.GetExtension(file)} via host ffmpeg: frame accuracy {accuracy:P1}, key {analysis.Key?.Name}");
            Assert.True(accuracy >= 0.9, $"{file}: {accuracy:P1}");
            Assert.Equal("C major", analysis.Key?.Name);
        }

        // Asking for less gets less.
        var short2 = await decoder.DecodeMonoAsync(wav, 8000, TimeSpan.FromSeconds(2));
        Assert.InRange(short2.Samples.Length, 15900, 16100);

        // Cancelling stops it.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoder.DecodeMonoAsync(wav, 11025, TimeSpan.FromMinutes(1), cts.Token));

        // Not audio: a clear error, not garbage.
        var text = Path.Combine(_files, "notes.mp3");
        File.WriteAllText(text, "not audio at all");
        await Assert.ThrowsAsync<InvalidOperationException>(() => decoder.DecodeMonoAsync(text, 11025, TimeSpan.FromMinutes(1)));
    }

    internal static void WriteWav(string path, float[] samples, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + samples.Length * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples.Length * 2);
        foreach (var s in samples) w.Write((short)Math.Clamp(Math.Round(s * 32767), short.MinValue, short.MaxValue));
    }

    private static async Task<bool> RunAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return p.ExitCode == 0;
    }
}
