using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Noctis.Services.Waveform;

/// <summary>Decodes a local audio file and reduces it to a <see cref="WaveformData"/>.</summary>
public interface IWaveformDecoder
{
    /// <summary>
    /// Blocking decode (call it on a background thread). Null when the file cannot be
    /// decoded — no ffmpeg, unsupported format, decode error — so the seek bar silently
    /// stays the plain line. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> fires.
    /// </summary>
    WaveformData? Decode(string path, CancellationToken ct);
}

/// <summary>
/// ffmpeg as the waveform decoder — the same bundled binary (<see cref="IAudioConverterService.GetFfmpegPath"/>)
/// the BPM/key analysis and the Linux visualizer side feed already decode with, so no new
/// dependency (FfmpegWaveformDecoderTests exercise wav, flac, mp3, m4a/aac, ogg/vorbis and
/// opus). Without ffmpeg, or on a file it cannot decode, the result is null: plain bar.
///
/// Playback must never stall for it (field report 2026-09-23: a hard-disk library under
/// heavy I/O lost audio when reads stalled past VLC's read-ahead). So:
/// <list type="bullet">
/// <item>the file is first read into the OS file cache in 1 MB slices with a pause after
/// each — the disk never serves one long burst, so the player's own small reads slot in
/// between — and on Windows from a thread in background mode (very low I/O priority, set
/// by <see cref="WaveformService"/>'s worker);</item>
/// <item>ffmpeg then decodes from that cache at BelowNormal priority with one thread, to
/// mono f32 at <see cref="SampleRate"/> (plenty for bar-level peaks/RMS).</item>
/// </list>
/// </summary>
public sealed class FfmpegWaveformDecoder : IWaveformDecoder
{
    public const int SampleRate = 11025;

    /// <summary>Largest file worth a waveform; beyond this (hour-long WAV/DSD) the plain bar
    /// stays rather than paging a gigabyte through the cache.</summary>
    public const long MaxFileBytes = 1L << 30;

    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromMinutes(5);
    internal const int PrimeChunkBytes = 1 << 20;
    internal const int PrimePauseMs = 20;

    private readonly IAudioConverterService _ffmpeg;
    private bool _loggedMissingFfmpeg;

    public FfmpegWaveformDecoder(IAudioConverterService ffmpeg) => _ffmpeg = ffmpeg;

    public WaveformData? Decode(string path, CancellationToken ct)
    {
        var exe = _ffmpeg.GetFfmpegPath();
        if (exe == null)
        {
            if (!_loggedMissingFfmpeg)
            {
                _loggedMissingFfmpeg = true;
                DebugLogger.Warn(DebugLogger.Category.Playback, "Waveform.NoFfmpeg",
                    "waveform seek bar needs ffmpeg (Settings → Advanced → Helper programs)");
            }
            return null;
        }

        PrimeFileCache(path, ct);
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
        {
            "-nostdin", "-nostats", "-hide_banner", "-loglevel", "error",
            "-threads", "1",
            "-i", path,
            "-map", "0:a:0", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "f32le", "-"
        }) psi.ArgumentList.Add(a);

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(DecodeTimeout);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg start failed");
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* not permitted here: decode at normal priority */ }
        using var reg = watchdog.Token.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
        });
        var stderr = p.StandardError.ReadToEndAsync();

        var acc = new WaveformAccumulator();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var stdout = p.StandardOutput.BaseStream;
            var carry = 0; // bytes of a float split across two reads
            while (true)
            {
                var n = stdout.Read(buffer, carry, 64 * 1024 - carry);
                if (n <= 0) break;
                var valid = carry + n;
                var whole = valid / sizeof(float) * sizeof(float);
                acc.Add(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, whole)));
                carry = valid - whole;
                if (carry > 0) Buffer.BlockCopy(buffer, whole, buffer, 0, carry);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        if (watchdog.IsCancellationRequested)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "Waveform.DecodeTimeout", System.IO.Path.GetFileName(path));
            return null;
        }
        if (p.ExitCode != 0)
        {
            var err = stderr.IsCompletedSuccessfully ? stderr.Result : string.Empty;
            if (err.Length > 200) err = err[..200];
            DebugLogger.Warn(DebugLogger.Category.Playback, "Waveform.DecodeFailed",
                $"exit={p.ExitCode} file={System.IO.Path.GetFileName(path)} {err.Trim()}");
            return null;
        }
        return acc.Build();
    }

    /// <summary>
    /// Reads the file once, sequentially, in <see cref="PrimeChunkBytes"/> slices with a
    /// <see cref="PrimePauseMs"/> pause after each, leaving it in the OS file cache for
    /// ffmpeg. Unbuffered FileStream + SequentialScan; shared for read/write/delete so the
    /// player, tag writers and file moves are never blocked.
    /// </summary>
    internal static void PrimeFileCache(string path, CancellationToken ct,
        int chunkBytes = PrimeChunkBytes, int pauseMs = PrimePauseMs)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(chunkBytes);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
            while (!ct.IsCancellationRequested)
            {
                if (fs.Read(buffer, 0, chunkBytes) <= 0) break;
                if (pauseMs > 0) ct.WaitHandle.WaitOne(pauseMs);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ffmpeg reports (or survives) the same problem; priming is only an I/O shaper.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
