using Noctis.Services;
using Noctis.Services.AudioAnalysis;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Spek-style spectrogram. The palette/paint tests run everywhere; the decode test needs
/// ffmpeg and is a no-op where it isn't installed (CI legs without it).
/// </summary>
public class SpectrogramRendererTests
{
    [Fact]
    public void Palette_is_monotonic_from_black_to_white()
    {
        var lut = SpectrogramRenderer.Palette;
        Assert.Equal(256, lut.Length);
        Assert.Equal(0xFF000000u, lut[0]);
        Assert.Equal(0xFFFFFFFFu, lut[255]);

        // Brightness never decreases as level rises: quiet stays dark, loud stays bright.
        static int Lum(uint px) => (int)((px >> 16) & 0xFF) + (int)((px >> 8) & 0xFF) + (int)(px & 0xFF);
        for (int i = 1; i < lut.Length; i++)
            Assert.True(Lum(lut[i]) >= Lum(lut[i - 1]) - 2, $"palette dips at {i}");
    }

    [Fact]
    public void Paint_puts_the_loud_bin_at_the_right_height()
    {
        // One column; bin 256 of 1024 (a quarter of Nyquist) is loud, the rest silent.
        var db = new float[SpectrogramRenderer.Bins];
        Array.Fill(db, (float)SpectrogramRenderer.MinDb);
        db[256] = 0;
        var data = new SpectrogramData
        {
            Columns = 1, Bins = SpectrogramRenderer.Bins, Db = db, SampleRate = 44100, Duration = TimeSpan.FromSeconds(1),
        };

        // PaintPixels is the platform-free half of Paint (the WriteableBitmap upload
        // needs a render platform, which a plain xunit test doesn't have).
        var pixels = SpectrogramRenderer.PaintPixels(data, 256);
        Assert.Equal(256, pixels.Length);

        // Row 0 is Nyquist, the bottom row is DC: a quarter-Nyquist tone lands three
        // quarters of the way down (row 191 of 256 when each row covers 4 bins).
        int brightest = -1, best = -1;
        for (int y = 0; y < 256; y++)
        {
            var px = unchecked((uint)pixels[y]);
            int lum = (int)((px >> 16) & 0xFF) + (int)((px >> 8) & 0xFF) + (int)(px & 0xFF);
            if (lum > best) { best = lum; brightest = y; }
        }
        Assert.Equal(191, brightest);
        Assert.Equal(0xFFFFFFFFu, unchecked((uint)pixels[191]));
        Assert.Equal(0xFF000000u, unchecked((uint)pixels[0]));
    }

    [Fact]
    public async Task Sine_wave_peaks_at_its_frequency_when_ffmpeg_is_available()
    {
        var ffmpeg = new AudioConverterService(() => string.Empty, new MetadataService()).GetFfmpegPath();
        if (ffmpeg == null) return; // no ffmpeg on this machine — nothing to decode with

        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const int rate = 8000;
            const double toneHz = 1000;
            var path = Path.Combine(dir, "tone.wav");
            WriteSineWav(path, rate, seconds: 2, toneHz);

            var data = await SpectrogramRenderer.ComputeAsync(
                ffmpeg, path, rate, TimeSpan.FromSeconds(2), columns: 32, progress: null, CancellationToken.None);

            Assert.Equal(rate, data.SampleRate);
            Assert.InRange(data.Columns, 16, 32);

            // Middle column: loudest bin sits at 1 kHz (bin = f / (rate / FftSize)).
            var col = data.Db.AsSpan((data.Columns / 2) * data.Bins, data.Bins);
            int peak = 0;
            for (int k = 1; k < col.Length; k++) if (col[k] > col[peak]) peak = k;
            var expectedBin = (int)Math.Round(toneHz / (rate / (double)SpectrogramRenderer.FftSize));
            Assert.InRange(peak, expectedBin - 2, expectedBin + 2);
            // Full-scale sine reads ≈ 0 dB; -6 dB tolerance covers window leakage.
            Assert.InRange(col[peak], -6, 0.5);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void WriteSineWav(string path, int rate, int seconds, double hz)
    {
        int samples = rate * seconds;
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int dataBytes = samples * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        for (int i = 0; i < samples; i++)
            w.Write((short)Math.Round(Math.Sin(2 * Math.PI * hz * i / rate) * short.MaxValue));
    }
}
