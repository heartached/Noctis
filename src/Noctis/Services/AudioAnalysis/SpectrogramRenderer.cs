using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Noctis.Services.AudioAnalysis;

/// <summary>
/// Column-major spectrogram: <c>Db[column * Bins + bin]</c>, bin 0 = DC, bin Bins-1 just
/// below Nyquist (<c>SampleRate / 2</c>). Values are dBFS in [-120, 0], 0 dB = full-scale sine.
/// </summary>
public sealed class SpectrogramData
{
    public required int Columns { get; init; }
    public required int Bins { get; init; }
    public required float[] Db { get; init; }
    public required int SampleRate { get; init; }
    public required TimeSpan Duration { get; init; }

    public double NyquistHz => SampleRate / 2.0;
}

/// <summary>
/// Spek-style acoustic spectrum analysis: decodes a file to mono float PCM through
/// ffmpeg (same out-of-process pattern as <see cref="AudioAnalysisService"/>), runs a
/// streaming STFT (2048-point Hann, one frame per output column) and paints the result
/// with Spek's colour ramp. Nothing is buffered beyond a few FFT windows, so a 20-minute
/// 192 kHz file costs the same memory as a 3-minute MP3.
/// </summary>
public static class SpectrogramRenderer
{
    public const int FftSize = 2048;
    public const int Bins = FftSize / 2;
    public const double MinDb = -120;

    private const int ReadChunkBytes = 1 << 18; // 256 KB of f32le per read

    public static async Task<SpectrogramData> ComputeAsync(
        string ffmpegPath, string filePath, int sampleRate, TimeSpan duration, int columns,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (columns < 16) columns = 16;
        if (sampleRate <= 0) sampleRate = 44100;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
        {
            "-nostats", "-hide_banner", "-loglevel", "error", "-i", filePath,
            "-map", "0:a:0", "-ac", "1", "-ar", sampleRate.ToString(),
            "-f", "f32le", "-"
        }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg start failed");
        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
        var stderrTask = p.StandardError.ReadToEndAsync();

        // One STFT frame per column when the duration is known; otherwise a fixed hop
        // and the frames are merged down to `columns` afterwards.
        long expectedSamples = duration > TimeSpan.Zero ? (long)(duration.TotalSeconds * sampleRate) : 0;
        long hop = expectedSamples > 0 ? Math.Max(FftSize / 4, expectedSamples / columns) : FftSize * 2;
        double expectedBytes = expectedSamples > 0 ? expectedSamples * 4.0 : 0;

        var frames = new List<float[]>(expectedSamples > 0 ? columns + 2 : 1024);
        var window = BuildHann();
        var re = new double[FftSize];
        var im = new double[FftSize];
        const double fullScale = FftSize / 4.0; // |X| of a 0 dBFS sine under a Hann window

        var pending = new float[FftSize + ReadChunkBytes / 4 + 4];
        int count = 0;                 // valid floats in pending
        long consumed = 0;             // absolute sample index of pending[0]
        long nextFrameStart = 0;       // absolute sample index the next frame starts at
        var bytes = new byte[ReadChunkBytes];
        int carry = 0;                 // bytes of a partial trailing sample kept from the last read
        long totalBytes = 0;

        var stdout = p.StandardOutput.BaseStream;
        while (true)
        {
            int n = await stdout.ReadAsync(bytes.AsMemory(carry, bytes.Length - carry), ct);
            if (n == 0) break;
            totalBytes += n;
            int avail = carry + n;
            int whole = avail / 4;

            // Append whole samples to the pending buffer (grow if a huge read lands).
            if (count + whole > pending.Length)
                Array.Resize(ref pending, Math.Max(pending.Length * 2, count + whole));
            MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, whole * 4)).CopyTo(pending.AsSpan(count));
            count += whole;

            carry = avail - whole * 4;
            if (carry > 0) Buffer.BlockCopy(bytes, whole * 4, bytes, 0, carry);

            // Emit every frame that is now fully inside the buffer.
            while (nextFrameStart + FftSize <= consumed + count)
            {
                int offset = (int)(nextFrameStart - consumed);
                frames.Add(ComputeFrame(pending, offset, window, re, im, fullScale));
                nextFrameStart += hop;
                ct.ThrowIfCancellationRequested();
            }

            // Drop samples no future frame can use.
            long drop = Math.Min(nextFrameStart - consumed, count);
            if (drop > 0)
            {
                int d = (int)drop;
                Array.Copy(pending, d, pending, 0, count - d);
                count -= d;
                consumed += d;
            }

            if (expectedBytes > 0)
                progress?.Report(Math.Min(0.98, totalBytes / expectedBytes));
        }

        await stderrTask;
        await p.WaitForExitAsync(ct);
        // ffmpeg reports non-zero for some truncated files after emitting all the audio
        // it could; a spectrogram of what decoded is still useful, so only an empty
        // result is treated as failure.
        if (frames.Count == 0)
            throw new InvalidOperationException(p.ExitCode != 0
                ? $"ffmpeg exit {p.ExitCode}: {stderrTask.Result.Trim()}"
                : "No audio decoded");

        var actualSamples = consumed + count;
        var actualDuration = TimeSpan.FromSeconds(actualSamples / (double)sampleRate);
        var data = MergeToColumns(frames, columns, sampleRate,
            duration > TimeSpan.Zero ? duration : actualDuration);
        progress?.Report(1);
        return data;
    }

    private static float[] BuildHann()
    {
        var w = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
        return w;
    }

    private static float[] ComputeFrame(float[] samples, int offset, float[] window, double[] re, double[] im, double fullScale)
    {
        for (int i = 0; i < FftSize; i++)
        {
            re[i] = samples[offset + i] * window[i];
            im[i] = 0;
        }
        Fft.Forward(re, im);

        var frame = new float[Bins];
        for (int k = 0; k < Bins; k++)
        {
            var mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / fullScale;
            var db = 20 * Math.Log10(mag + 1e-12);
            frame[k] = (float)Math.Clamp(db, MinDb, 0);
        }
        return frame;
    }

    /// <summary>Max-merges frames into exactly <paramref name="columns"/> columns (or fewer when the file is shorter).</summary>
    private static SpectrogramData MergeToColumns(List<float[]> frames, int columns, int sampleRate, TimeSpan duration)
    {
        int outCols = Math.Min(columns, frames.Count);
        var db = new float[outCols * Bins];
        for (int c = 0; c < outCols; c++)
        {
            int from = (int)((long)c * frames.Count / outCols);
            int to = (int)((long)(c + 1) * frames.Count / outCols);
            if (to <= from) to = from + 1;
            var dest = db.AsSpan(c * Bins, Bins);
            dest.Fill((float)MinDb);
            for (int f = from; f < to && f < frames.Count; f++)
            {
                var src = frames[f];
                for (int k = 0; k < Bins; k++)
                    if (src[k] > dest[k]) dest[k] = src[k];
            }
        }
        return new SpectrogramData
        {
            Columns = outCols,
            Bins = Bins,
            Db = db,
            SampleRate = sampleRate,
            Duration = duration,
        };
    }

    // ── Painting ──

    private static readonly (double Db, byte R, byte G, byte B)[] PaletteStops =
    {
        (-120, 0, 0, 0),
        (-105, 12, 0, 60),
        (-90, 40, 0, 140),
        (-75, 110, 0, 190),
        (-60, 200, 20, 100),
        (-45, 240, 60, 40),
        (-30, 255, 140, 0),
        (-15, 255, 225, 40),
        (0, 255, 255, 255),
    };

    private static uint[]? _lut;

    /// <summary>256-entry BGRA lookup over [-120, 0] dB using Spek's colour ramp.</summary>
    public static uint[] Palette
    {
        get
        {
            if (_lut != null) return _lut;
            var lut = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                double db = MinDb + (0 - MinDb) * i / 255.0;
                var (r, g, b) = SamplePalette(db);
                lut[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
            }
            return _lut = lut;
        }
    }

    public static (byte R, byte G, byte B) SamplePalette(double db)
    {
        db = Math.Clamp(db, MinDb, 0);
        for (int i = 1; i < PaletteStops.Length; i++)
        {
            var (d1, r1, g1, b1) = PaletteStops[i];
            if (db > d1) continue;
            var (d0, r0, g0, b0) = PaletteStops[i - 1];
            var t = (db - d0) / (d1 - d0);
            return ((byte)(r0 + (r1 - r0) * t), (byte)(g0 + (g1 - g0) * t), (byte)(b0 + (b1 - b0) * t));
        }
        var last = PaletteStops[^1];
        return (last.R, last.G, last.B);
    }

    /// <summary>
    /// Paints the spectrogram into a bitmap of <c>Columns × height</c> pixels: row 0 is
    /// Nyquist, the bottom row is DC, frequency is linear, and each pixel row shows the
    /// loudest bin it covers so narrow tones survive the downscale.
    /// </summary>
    public static WriteableBitmap Paint(SpectrogramData data, int height)
    {
        int w = data.Columns, h = Math.Max(1, height);
        var pixels = PaintPixels(data, h);
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        for (int y = 0; y < h; y++)
            Marshal.Copy(pixels, y * w, fb.Address + y * fb.RowBytes, w);
        return bmp;
    }

    /// <summary>
    /// The pixel rows <see cref="Paint"/> uploads, as BGRA ints (row-major, <c>Columns × height</c>).
    /// Pure and platform-free, so it is unit-testable and can run on a worker thread.
    /// </summary>
    public static int[] PaintPixels(SpectrogramData data, int height)
    {
        var lut = Palette;
        int w = data.Columns, h = Math.Max(1, height);
        var pixels = new int[w * h];
        for (int y = 0; y < h; y++)
        {
            // Row y covers bins [binLo, binHi): top row = highest frequencies.
            int binHi = data.Bins - (int)((long)y * data.Bins / h);
            int binLo = data.Bins - (int)((long)(y + 1) * data.Bins / h);
            if (binLo < 0) binLo = 0;
            if (binHi <= binLo) binHi = binLo + 1;
            int rowStart = y * w;
            for (int x = 0; x < w; x++)
            {
                var col = data.Db.AsSpan(x * data.Bins, data.Bins);
                float best = (float)MinDb;
                for (int k = binLo; k < binHi && k < data.Bins; k++)
                    if (col[k] > best) best = col[k];
                int idx = (int)((best - MinDb) / (0 - MinDb) * 255);
                pixels[rowStart + x] = unchecked((int)lut[Math.Clamp(idx, 0, 255)]);
            }
        }
        return pixels;
    }
}
