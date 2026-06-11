using System.Diagnostics;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>Generates and caches waveform peak data for the seekbar.</summary>
public interface IWaveformService
{
    /// <summary>
    /// Returns normalized peaks (0..1) for the track, decoding and caching on
    /// first request. Null when ffmpeg is unavailable, the track is not a local
    /// file, or decoding fails.
    /// </summary>
    Task<float[]?> GetPeaksAsync(Track track, CancellationToken ct = default);
}

/// <summary>
/// Decodes the full track to low-rate mono PCM via ffmpeg (same out-of-process
/// pattern as AudioAnalysisService), reduces it to a fixed number of peak
/// buckets, and caches the result as one byte per bucket under the data dir.
/// </summary>
public sealed class WaveformService : IWaveformService
{
    /// <summary>Bar count rendered by the seekbar. One cache byte per bucket.</summary>
    public const int Buckets = 240;

    private const int SampleRate = 8000;

    private readonly IAudioConverterService _converter;
    private readonly string _cacheDir;

    public WaveformService(IAudioConverterService converter, string? cacheDir = null)
    {
        _converter = converter;
        _cacheDir = cacheDir ?? Path.Combine(AppPaths.DataRoot, "waveform_cache");
    }

    public async Task<float[]?> GetPeaksAsync(Track track, CancellationToken ct = default)
    {
        if (track.SourceType != SourceType.Local || !File.Exists(track.FilePath))
            return null;

        var cachePath = Path.Combine(_cacheDir, $"{track.Id:N}.wf");
        try
        {
            if (File.Exists(cachePath))
            {
                var cached = await File.ReadAllBytesAsync(cachePath, ct);
                if (cached.Length == Buckets)
                    return cached.Select(b => b / 255f).ToArray();
            }
        }
        catch { /* corrupt cache — regenerate below */ }

        var ffmpeg = _converter.GetFfmpegPath();
        if (ffmpeg == null)
            return null;

        try
        {
            var samples = await DecodeMonoAsync(ffmpeg, track.FilePath, ct);
            if (samples.Length < SampleRate)
                return null;

            var peaks = BuildPeaks(samples, Buckets);

            Directory.CreateDirectory(_cacheDir);
            var bytes = peaks.Select(p => (byte)Math.Clamp((int)Math.Round(p * 255), 0, 255)).ToArray();
            await File.WriteAllBytesAsync(cachePath, bytes, ct);
            return peaks;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Playback, "Waveform.Decode", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reduces raw samples to per-bucket max-abs peaks, normalized so the
    /// loudest bucket is 1.0. Quiet floors are lifted slightly so bars stay visible.
    /// </summary>
    public static float[] BuildPeaks(ReadOnlySpan<float> samples, int buckets)
    {
        var peaks = new float[buckets];
        if (samples.Length == 0 || buckets <= 0)
            return peaks;

        for (int i = 0; i < buckets; i++)
        {
            int start = (int)((long)i * samples.Length / buckets);
            int end = (int)((long)(i + 1) * samples.Length / buckets);
            float max = 0;
            for (int s = start; s < end; s++)
            {
                var v = Math.Abs(samples[s]);
                if (v > max) max = v;
            }
            peaks[i] = max;
        }

        var overall = peaks.Max();
        if (overall > 0)
        {
            for (int i = 0; i < buckets; i++)
                peaks[i] = Math.Max(peaks[i] / overall, 0.04f);
        }
        return peaks;
    }

    private static async Task<float[]> DecodeMonoAsync(string ffmpeg, string source, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
        {
            "-nostats", "-hide_banner", "-i", source,
            "-map", "0:a:0", "-ac", "1", "-ar", SampleRate.ToString(),
            "-f", "f32le", "-"
        }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg start failed");
        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });

        using var ms = new MemoryStream();
        var copyTask = p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var stderrTask = p.StandardError.ReadToEndAsync();
        await copyTask;
        await stderrTask;
        await p.WaitForExitAsync(ct);

        if (p.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg exit " + p.ExitCode);

        var bytes = ms.ToArray();
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return samples;
    }
}
