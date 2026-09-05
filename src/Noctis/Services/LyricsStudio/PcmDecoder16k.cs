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

    public static async Task<float[]> DecodeAsync(string ffmpegPath, string source, CancellationToken ct)
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
        await using var pcm = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(pcm, 1 << 16, ct).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0 && pcm.Length == 0)
            throw new InvalidOperationException($"ffmpeg could not decode this file ({stderr.Trim().Split('\n').LastOrDefault()?.Trim()})");

        var bytes = pcm.GetBuffer();
        var samples = (int)(pcm.Length / 4);
        var result = new float[samples];
        Buffer.BlockCopy(bytes, 0, result, 0, samples * 4);
        return result;
    }
}
