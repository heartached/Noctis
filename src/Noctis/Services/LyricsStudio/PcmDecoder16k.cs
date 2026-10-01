using System.Diagnostics;

namespace Noctis.Services.LyricsStudio;

/// <summary>
/// Decodes a track to 16 kHz mono float PCM — the only input Whisper takes — with ffmpeg
/// out of process, the same idiom the BPM/key analyser uses (see AudioAnalysisService).
/// </summary>
public static class PcmDecoder16k
{
    public const int SampleRate = 16000;

    /// <summary>Longest stretch decoded (songs longer than this are aligned on their first 20 minutes).</summary>
    public const int MaxSeconds = 20 * 60;

    public static IReadOnlyList<string> BuildArgs(string source, int maxSeconds = MaxSeconds) => new[]
    {
        "-nostats", "-hide_banner", "-loglevel", "error",
        "-t", maxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-i", source,
        "-map", "0:a:0", "-ac", "1", "-ar", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-f", "f32le", "-",
    };

    /// <param name="progress">0–1 as samples arrive, measured against <paramref name="duration"/> (the tag's length; no reports without it).</param>
    public static async Task<float[]> DecodeAsync(string ffmpegPath, string source, CancellationToken ct,
        IProgress<double>? progress = null, TimeSpan? duration = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in BuildArgs(source)) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg could not be started");
        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });

        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        var expected = duration is { } d && d > TimeSpan.Zero ? (long)(Math.Min(d.TotalSeconds, MaxSeconds) * SampleRate) * 4 : 0;
        await using var pcm = new MemoryStream(expected is > 0 and < int.MaxValue - (1 << 20) ? (int)expected + (1 << 16) : 0);
        await CopyWithProgressAsync(p.StandardOutput.BaseStream, pcm, expected, progress, ct).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0 && pcm.Length == 0)
            throw new InvalidOperationException($"ffmpeg could not decode this file ({stderr.Trim().Split('\n').LastOrDefault()?.Trim()})");

        progress?.Report(1);
        var bytes = pcm.GetBuffer();
        var samples = (int)(pcm.Length / 4);
        var result = new float[samples];
        Buffer.BlockCopy(bytes, 0, result, 0, samples * 4);
        return result;
    }

    /// <summary>Copies ffmpeg's output, reporting each whole half-percent of <paramref name="expected"/> bytes.</summary>
    internal static async Task CopyWithProgressAsync(Stream from, Stream to, long expected, IProgress<double>? progress, CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        var reported = 0.0;
        int n;
        while ((n = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            if (progress is null || expected <= 0) continue;
            var f = Math.Min(1, to.Length / (double)expected);
            if (f - reported < 0.005) continue;
            reported = f;
            progress.Report(f);
        }
    }
}
