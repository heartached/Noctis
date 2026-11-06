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
            // CRF 18 (near-transparent) instead of the x264 default 23 — the default
            // visibly muddies the card's text and the logo on a mostly-static frame.
            "-c:v", "libx264", "-tune", "stillimage", "-crf", "18", "-pix_fmt", "yuv420p", "-r", "30",
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

    /// <summary>Audio fade at each end of the Lyrics Page video, in seconds. Card clips cut clean.</summary>
    public const double PageFadeSeconds = 0.3;

    /// <summary>
    /// Builds the ffmpeg argument list for the karaoke path: a numbered PNG frame
    /// sequence at <paramref name="fps"/> instead of a looped still. Same audio trim
    /// and output clamp semantics as <see cref="BuildFfmpegArgs"/>. A positive
    /// <paramref name="fadeSeconds"/> fades the audio in from the clip start and out to its
    /// end (the Lyrics Page video); 0 adds no audio filter. Pure — unit-tested.
    /// </summary>
    public static List<string> BuildFfmpegFrameArgs(string framesPattern, int fps, string audioPath, string outputPath,
        ShareClipTiming timing, double fadeSeconds = 0)
    {
        string start = timing.StartSeconds.ToString(CultureInfo.InvariantCulture);
        string dur = timing.DurationSeconds.ToString(CultureInfo.InvariantCulture);

        var args = new List<string>
        {
            "-y",
            "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-i", framesPattern,
            "-ss", start, "-t", dur, "-i", audioPath,
            "-map", "0:v:0", "-map", "1:a:0",
        };
        if (fadeSeconds > 0)
        {
            string d = fadeSeconds.ToString(CultureInfo.InvariantCulture);
            // Rounded so float noise in the subtraction never reaches the filter string.
            string outStart = Math.Round(Math.Max(0, timing.DurationSeconds - fadeSeconds), 6).ToString(CultureInfo.InvariantCulture);
            args.Add("-af");
            args.Add($"afade=t=in:st=0:d={d},afade=t=out:st={outStart}:d={d}");
        }
        args.AddRange(new[]
        {
            // Same CRF 18 as the still path: keeps lyric text crisp through the encode.
            "-c:v", "libx264", "-crf", "18", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart",
            // Same clamp contract as BuildFfmpegArgs: the output -t pins every stream to
            // the clip window; -shortest trims earlier when the song ends inside it.
            "-t", dur,
            "-shortest",
            outputPath,
        });
        return args;
    }

    /// <summary>
    /// Writes <paramref name="framePng"/> to a temp file and runs ffmpeg to produce the
    /// clip. Returns (true, "") on success or (false, lastStderrLine) on failure.
    /// </summary>
    public static async Task<(bool ok, string error)> RenderAsync(
        string ffmpegPath, byte[] framePng, string audioPath, string outputPath,
        ShareClipTiming timing, CancellationToken ct = default)
    {
        var tempFrame = Path.Combine(Path.GetTempPath(), $"noctis-clip-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(tempFrame, framePng, ct);
            return await RunFfmpegAsync(ffmpegPath, BuildFfmpegArgs(tempFrame, audioPath, outputPath, timing), ct);
        }
        catch (OperationCanceledException) { return (false, "cancelled"); }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { File.Delete(tempFrame); } catch { /* best effort */ } }
    }

    /// <summary>
    /// Runs ffmpeg over an already-rendered frame sequence (the karaoke and Lyrics Page clip
    /// paths), with the audio fade of <see cref="BuildFfmpegFrameArgs"/>. Same process handling
    /// and result contract as <see cref="RenderAsync"/>.
    /// </summary>
    public static Task<(bool ok, string error)> RenderFramesAsync(
        string ffmpegPath, string framesPattern, int fps, string audioPath, string outputPath,
        ShareClipTiming timing, CancellationToken ct = default, double fadeSeconds = 0)
        => RunFfmpegAsync(ffmpegPath, BuildFfmpegFrameArgs(framesPattern, fps, audioPath, outputPath, timing, fadeSeconds), ct);

    /// <summary>
    /// Runs ffmpeg with <paramref name="args"/>: (true, "") on exit 0 or
    /// (false, lastStderrLine) on failure. Mirrors <c>AudioConverterService.RunFfmpegAsync</c>:
    /// async stderr drain, kill on cancel.
    /// </summary>
    private static async Task<(bool ok, string error)> RunFfmpegAsync(
        string ffmpegPath, List<string> args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return (false, "process start failed");

            var stderrTask = p.StandardError.ReadToEndAsync();
            var stdoutTask = p.StandardOutput.ReadToEndAsync();

            try
            {
                using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
                    await p.WaitForExitAsync(ct);
            }
            finally
            {
                // Always drain the readers before `using var p` disposes the Process and
                // its redirected pipes. On cancellation WaitForExitAsync throws, so these
                // were left outstanding against streams that were about to be disposed —
                // faulting unobserved.
                try { await Task.WhenAll(stderrTask, stdoutTask); } catch { }
            }

            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;

            if (p.ExitCode == 0) return (true, string.Empty);
            var last = stderr.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? $"exit {p.ExitCode}";
            return (false, last);
        }
        catch (OperationCanceledException) { return (false, "cancelled"); }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
