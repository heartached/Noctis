using System.Diagnostics;
using Noctis.Services;

namespace Noctis.Services.AudioAnalysis;

/// <summary>
/// Decodes audio to mono 22.05 kHz float PCM via ffmpeg (out-of-process, same
/// pattern as <see cref="ReplayGainScannerService"/>) and runs the managed
/// BPM + key detectors. Caps analysis to the first ~120 s for speed.
/// </summary>
public sealed class AudioAnalysisService : IAudioAnalysisService
{
    private const int SampleRate = 22050;
    private const int MaxSeconds = 120;

    private readonly IAudioConverterService _converter;

    public AudioAnalysisService(IAudioConverterService converter) => _converter = converter;

    public bool IsAvailable => _converter.GetFfmpegPath() != null;

    public async Task<AudioAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct)
    {
        var ffmpeg = _converter.GetFfmpegPath();
        if (ffmpeg == null) return AudioAnalysisResult.Fail("ffmpeg not found");

        float[] mono;
        try
        {
            mono = await DecodeMonoAsync(ffmpeg, filePath, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return AudioAnalysisResult.Fail(ex.Message); }

        if (mono.Length < SampleRate) return AudioAnalysisResult.Fail("decoded audio too short");

        var (bpm, bpmConf) = BpmDetector.Detect(mono, SampleRate);
        var (key, keyConf) = KeyDetector.Detect(mono, SampleRate);
        return new AudioAnalysisResult(bpm, bpmConf, key, keyConf);
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
            "-nostats", "-hide_banner", "-t", MaxSeconds.ToString(), "-i", source,
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
        // Truncating any trailing partial sample (< 4 bytes) is intentional.
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return samples;
    }
}
