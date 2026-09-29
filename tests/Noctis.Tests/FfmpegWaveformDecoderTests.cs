using System.Diagnostics;
using Noctis.Services;
using Noctis.Services.Waveform;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #93: the real ffmpeg decode path. Needs ffmpeg (bundled with every release build;
/// on a dev box it comes from PATH) and is a no-op where there is none, like the
/// spectrogram decode test. A WAV with 2 s of silence then 2 s of tone is written, then
/// transcoded to the other formats the player plays; each must decode to a waveform whose
/// first half is silent and second half loud.
/// </summary>
public sealed class FfmpegWaveformDecoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "waveform-dec-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public FfmpegWaveformDecoderTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string? Ffmpeg() =>
        new AudioConverterService(() => string.Empty, new MetadataService()).GetFfmpegPath();

    [Theory]
    [InlineData("wav", null)]
    [InlineData("flac", null)]
    [InlineData("mp3", "libmp3lame")]
    [InlineData("m4a", "aac")]
    [InlineData("ogg", "libvorbis")]
    [InlineData("opus", "libopus")]
    public void SilenceThenTone_DecodesToAQuietHalfAndALoudHalf(string ext, string? codec)
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg == null) return; // no ffmpeg on this machine — nothing to decode with

        var wav = Path.Combine(_dir, "src.wav");
        WriteSilenceThenToneWav(wav, 44100, silentSeconds: 2, toneSeconds: 2);
        var path = wav;
        if (ext != "wav")
        {
            path = Path.Combine(_dir, "src." + ext);
            var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", wav };
            if (codec != null) { args.Add("-c:a"); args.Add(codec); }
            args.Add(path);
            if (!Run(ffmpeg, args))
            {
                _out.WriteLine($"{ext}: this ffmpeg build cannot encode it ({codec}) — skipped");
                return;
            }
        }

        var data = new FfmpegWaveformDecoder(new AudioConverterService(() => string.Empty, new MetadataService()))
            .Decode(path, CancellationToken.None);

        Assert.NotNull(data);
        var n = data!.BucketCount;
        Assert.InRange(n, 100, WaveformAccumulator.DefaultBucketCount);
        // Leave a margin around the 50% edge (encoder delay / padding shifts it slightly).
        var quiet = Enumerable.Range(0, (int)(n * 0.4)).Max(i => data.LevelAt(i));
        var loud = Enumerable.Range((int)(n * 0.6), (int)(n * 0.35)).Min(i => data.LevelAt(i));
        _out.WriteLine($"{ext}: buckets={n} quietMax={quiet:0.000} loudMin={loud:0.000}");
        Assert.True(quiet < 0.05, $"{ext}: silent half reads {quiet}");
        Assert.True(loud > 0.8, $"{ext}: tone half reads {loud}");
    }

    [Fact]
    public void Undecodable_IsNullNotAnException()
    {
        if (Ffmpeg() == null) return;
        var junk = Path.Combine(_dir, "junk.mp3");
        File.WriteAllBytes(junk, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray());
        var data = new FfmpegWaveformDecoder(new AudioConverterService(() => string.Empty, new MetadataService()))
            .Decode(junk, CancellationToken.None);
        Assert.Null(data);
    }

    [Fact]
    public void Cancellation_StopsTheDecode()
    {
        if (Ffmpeg() == null) return;
        var wav = Path.Combine(_dir, "long.wav");
        WriteSilenceThenToneWav(wav, 44100, silentSeconds: 1, toneSeconds: 60);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new FfmpegWaveformDecoder(new AudioConverterService(() => string.Empty, new MetadataService()))
                .Decode(wav, cts.Token));
    }

    [Fact]
    public void PrimeFileCache_ReadsTheWholeFile_InSlices()
    {
        var path = Path.Combine(_dir, "prime.bin");
        File.WriteAllBytes(path, new byte[3 * 1024 * 1024 + 17]);
        var sw = Stopwatch.StartNew();
        FfmpegWaveformDecoder.PrimeFileCache(path, CancellationToken.None, chunkBytes: 1 << 20, pauseMs: 30);
        // 4 slices, each followed by a pause: the read is spread out, not one burst.
        Assert.True(sw.ElapsedMilliseconds >= 4 * 30 - 10, $"primed in {sw.ElapsedMilliseconds} ms");
    }

    private static bool Run(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return p.HasExited && p.ExitCode == 0;
    }

    private static void WriteSilenceThenToneWav(string path, int rate, int silentSeconds, int toneSeconds)
    {
        var samples = rate * (silentSeconds + toneSeconds);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        var dataBytes = samples * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        for (var i = 0; i < samples; i++)
        {
            var v = i < rate * silentSeconds ? 0 : Math.Sin(2 * Math.PI * 440 * i / rate) * 0.5;
            w.Write((short)(v * short.MaxValue));
        }
    }
}
