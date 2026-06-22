using System.Diagnostics;
using System.Globalization;

namespace Noctis.Services;

/// <summary>
/// Renders a lyric share clip: the still card frame plus a trimmed slice of the source
/// audio, muxed to MP4 (H.264 + AAC) by ffmpeg. CPU/process work only — call off the UI
/// thread. ffmpeg is located by the caller (via <see cref="IAudioConverterService"/>).
/// </summary>
public static class ShareClipRenderer
{
    /// <summary>
    /// Builds the ffmpeg argument list: loop the still frame, seek/trim the audio to the
    /// clip window, encode broadly-compatible H.264 yuv420p + AAC, and pin the output to
    /// the audio window so the video ends exactly with the audio — no trailing silence and
    /// no fade. Pure — unit-tested.
    /// </summary>
    public static List<string> BuildFfmpegArgs(string framePngPath, string audioPath, string outputPath, ShareClipTiming timing)
    {
        string start = timing.StartSeconds.ToString(CultureInfo.InvariantCulture);
        string dur = timing.DurationSeconds.ToString(CultureInfo.InvariantCulture);

        return new List<string>
        {
            "-y",
            "-loop", "1", "-framerate", "2", "-i", framePngPath,
            "-ss", start, "-t", dur, "-i", audioPath,
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "libx264", "-tune", "stillimage", "-pix_fmt", "yuv420p", "-r", "30",
            "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart",
            // -shortest alone does NOT clamp a looped still-image video to the audio — it
            // overshoots ~2-3s of trailing silence. The output-side -t pins every stream to
            // the clip window; -shortest still trims earlier when the song ends inside it
            // (audio EOF before dur). Together: the video ends exactly with the audio.
            "-t", dur,
            "-shortest",
            outputPath,
        };
    }

    /// <summary>
    /// Writes <paramref name="framePng"/> to a temp file and runs ffmpeg to produce the
    /// clip. Returns (true, "") on success or (false, lastStderrLine) on failure. Mirrors
    /// <c>AudioConverterService.RunFfmpegAsync</c>: async stderr drain, kill on cancel.
    /// </summary>
    public static async Task<(bool ok, string error)> RenderAsync(
        string ffmpegPath, byte[] framePng, string audioPath, string outputPath,
        ShareClipTiming timing, CancellationToken ct = default)
    {
        var tempFrame = Path.Combine(Path.GetTempPath(), $"noctis-clip-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(tempFrame, framePng, ct);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in BuildFfmpegArgs(tempFrame, audioPath, outputPath, timing))
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return (false, "process start failed");

            var stderrTask = p.StandardError.ReadToEndAsync();
            var stdoutTask = p.StandardOutput.ReadToEndAsync();

            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
                await p.WaitForExitAsync(ct);

            var stderr = await stderrTask;
            await stdoutTask;

            if (p.ExitCode == 0) return (true, string.Empty);
            var last = stderr.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? $"exit {p.ExitCode}";
            return (false, last);
        }
        catch (OperationCanceledException) { return (false, "cancelled"); }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { File.Delete(tempFrame); } catch { /* best effort */ } }
    }
}
