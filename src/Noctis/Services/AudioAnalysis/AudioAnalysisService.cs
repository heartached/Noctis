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
    private const int MaxSamples = SampleRate * MaxSeconds;
    private const int MaxBytes = MaxSamples * 4;

    private readonly IAudioConverterService _converter;

    // One decode at a time, reusing fixed buffers. The previous MemoryStream →
    // ToArray → BlockCopy pipeline allocated three+ copies of each track's
    // ~10 MB PCM on the large object heap; under a backfill pass that garbage
    // outpaced Gen2 collections and ballooned the heap into the GBs.
    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private byte[]? _byteBuffer;
    private float[]? _sampleBuffer;

    public AudioAnalysisService(IAudioConverterService converter) => _converter = converter;

    public bool IsAvailable => _converter.GetFfmpegPath() != null;

    public async Task<AudioAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct)
    {
        var ffmpeg = _converter.GetFfmpegPath();
        if (ffmpeg == null) return AudioAnalysisResult.Fail("ffmpeg not found");

        await _decodeGate.WaitAsync(ct);
        try
        {
            int sampleCount;
            try
            {
                sampleCount = await DecodeMonoAsync(ffmpeg, filePath, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return AudioAnalysisResult.Fail(ex.Message); }

            if (sampleCount < SampleRate) return AudioAnalysisResult.Fail("decoded audio too short");

            var (bpm, bpmConf) = BpmDetector.Detect(_sampleBuffer!, SampleRate, sampleCount);
            var (key, keyConf) = KeyDetector.Detect(_sampleBuffer!, SampleRate, sampleCount);
            return new AudioAnalysisResult(bpm, bpmConf, key, keyConf);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    /// <summary>Decodes into the reusable buffers; returns the valid sample count.</summary>
    private async Task<int> DecodeMonoAsync(string ffmpeg, string source, CancellationToken ct)
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

        _byteBuffer ??= new byte[MaxBytes];
        _sampleBuffer ??= new float[MaxSamples];

        var stderrTask = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.BaseStream;
        int total = 0;
        while (total < MaxBytes)
        {
            int n = await stdout.ReadAsync(_byteBuffer.AsMemory(total, MaxBytes - total), ct);
            if (n == 0) break;
            total += n;
        }
        // ffmpeg can emit slightly past -t; drain so it isn't blocked on a full
        // stdout pipe and can exit.
        await stdout.CopyToAsync(Stream.Null, ct);
        await stderrTask;
        await p.WaitForExitAsync(ct);

        if (p.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg exit " + p.ExitCode);

        // Truncating any trailing partial sample (< 4 bytes) is intentional.
        var samples = total / 4;
        Buffer.BlockCopy(_byteBuffer, 0, _sampleBuffer, 0, samples * 4);
        return samples;
    }
}
