using System.ComponentModel;
using System.Diagnostics;

namespace Noctis.Services;

/// <summary>
/// The desktop's ALAC → FLAC transcode for the Noctis server's <c>format=flac</c> (phones
/// without an ALAC decoder): ffmpeg, found the way the converter finds it
/// (<see cref="IAudioConverterService.GetFfmpegPath"/>, so the user's override applies), writes
/// the first audio stream as FLAC. Lossless both ways, and ffmpeg keeps the sample rate,
/// channels and bit depth (a 24-bit ALAC decodes to s32 with 24 raw bits, which the FLAC
/// encoder writes as 24-bit). Caching and single-flight live in
/// <see cref="Server.FlacTranscodeCache"/>, which calls <see cref="TranscodeAsync"/>.
///
/// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>: only the library's own
/// source path and the cache's target path, never request data. ffmpeg is killed after
/// <see cref="Timeout"/> or on cancel.
/// </summary>
public sealed class ServerFlacTranscoder
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private readonly Func<string?> _ffmpegPath;

    /// <param name="ffmpegPath">Resolved per transcode, so a newly set or installed ffmpeg is picked up; null → no transcode.</param>
    public ServerFlacTranscoder(Func<string?> ffmpegPath) => _ffmpegPath = ffmpegPath;

    /// <summary>The ffmpeg command line. The target has no .flac extension (it is a .part), hence -f flac.</summary>
    public static IReadOnlyList<string> BuildArgs(string source, string target) => new[]
    {
        "-nostdin", "-hide_banner", "-v", "error",
        "-i", source,
        "-map", "0:a:0",
        "-c:a", "flac", "-compression_level", "5",
        "-f", "flac", "-y", target,
    };

    /// <summary>True when ffmpeg wrote a complete FLAC to <paramref name="target"/>; false when it is missing, fails or times out.</summary>
    public async Task<bool> TranscodeAsync(string source, string target, CancellationToken ct)
    {
        var ffmpeg = _ffmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            DebugLogger.Warn(DebugLogger.Category.State, "Server", "flac transcode skipped: ffmpeg not found");
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in BuildArgs(source, target)) psi.ArgumentList.Add(arg);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return false;
            // Drained so a chatty ffmpeg cannot block on a full pipe. Not logged: its lines name the file.
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                DebugLogger.Warn(DebugLogger.Category.State, "Server",
                    ct.IsCancellationRequested ? "flac transcode cancelled" : $"flac transcode timed out after {Timeout.TotalSeconds:0} s");
                return false;
            }
            await Task.WhenAll(stderr, stdout).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                DebugLogger.Warn(DebugLogger.Category.State, "Server", $"flac transcode failed: ffmpeg exit {process.ExitCode}");
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "Server", $"flac transcode failed: {ex.GetType().Name}");
            return false;
        }
    }
}
