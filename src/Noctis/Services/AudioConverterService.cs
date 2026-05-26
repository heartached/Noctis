using System.Diagnostics;
using System.Runtime.InteropServices;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>Transcode target settings for a single conversion job.</summary>
public sealed class AudioConvertOptions
{
    /// <summary>"mp3", "flac", "opus", "aac", "wav".</summary>
    public string Format { get; set; } = "mp3";

    /// <summary>Target bitrate in kbps. Ignored for lossless formats (flac, wav).</summary>
    public int BitrateKbps { get; set; } = 320;

    /// <summary>Output directory. If empty, conversion runs alongside the source file.</summary>
    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>Filename pattern (without extension). Tokens accepted by <see cref="TitleFormatter"/>.</summary>
    public string FilenamePattern { get; set; } = "%artist% - %title%";

    /// <summary>Copy ID3/Vorbis tags from source to output.</summary>
    public bool CopyTags { get; set; } = true;

    /// <summary>Embed album art from source into output.</summary>
    public bool EmbedArtwork { get; set; } = true;

    /// <summary>Overwrite the output file if it already exists. Otherwise, skip.</summary>
    public bool OverwriteExisting { get; set; }
}

/// <summary>Status updates emitted by <see cref="AudioConverterService"/> per file.</summary>
public sealed class ConvertProgress
{
    public Track Track { get; set; } = null!;
    public string Status { get; set; } = string.Empty;
    public bool Done { get; set; }
    public bool Failed { get; set; }
    public string OutputPath { get; set; } = string.Empty;
}

public interface IAudioConverterService
{
    /// <summary>Absolute path to a usable ffmpeg executable, or null if none was found.</summary>
    string? GetFfmpegPath();

    /// <summary>Convert <paramref name="tracks"/> sequentially. Reports progress per file.</summary>
    Task ConvertAsync(
        IReadOnlyList<Track> tracks,
        AudioConvertOptions options,
        IProgress<ConvertProgress> progress,
        CancellationToken ct);
}

public sealed class AudioConverterService : IAudioConverterService
{
    private readonly Func<string> _ffmpegPathOverride;

    /// <param name="ffmpegPathOverride">Returns the user-specified ffmpeg path, or empty for auto-detect.</param>
    public AudioConverterService(Func<string> ffmpegPathOverride)
    {
        _ffmpegPathOverride = ffmpegPathOverride;
    }

    public string? GetFfmpegPath()
    {
        var configured = _ffmpegPathOverride();
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

        // 1) Alongside the app — where CI-bundled binaries will live.
        var appDir = AppContext.BaseDirectory;
        var bundled = Path.Combine(appDir, exeName);
        if (File.Exists(bundled)) return bundled;

        // 2) PATH.
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        foreach (var dir in path.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* invalid PATH segment — skip */ }
        }
        return null;
    }

    public async Task ConvertAsync(
        IReadOnlyList<Track> tracks,
        AudioConvertOptions options,
        IProgress<ConvertProgress> progress,
        CancellationToken ct)
    {
        var ffmpeg = GetFfmpegPath();
        if (ffmpeg == null)
        {
            foreach (var t in tracks)
                progress.Report(new ConvertProgress { Track = t, Status = "ffmpeg not found", Failed = true, Done = true });
            return;
        }

        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();

            var outPath = ComputeOutputPath(track, options);
            progress.Report(new ConvertProgress { Track = track, Status = "Converting…" });

            if (File.Exists(outPath) && !options.OverwriteExisting)
            {
                progress.Report(new ConvertProgress { Track = track, Status = "Skipped (exists)", Done = true, OutputPath = outPath });
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            var (ok, error) = await RunFfmpegAsync(ffmpeg, track.FilePath, outPath, options, ct);
            progress.Report(new ConvertProgress
            {
                Track = track,
                Status = ok ? "Done" : ("Failed: " + error),
                Done = true,
                Failed = !ok,
                OutputPath = ok ? outPath : string.Empty,
            });
        }
    }

    private static string ComputeOutputPath(Track track, AudioConvertOptions options)
    {
        var pattern = string.IsNullOrWhiteSpace(options.FilenamePattern) ? "%title%" : options.FilenamePattern;
        var name = TitleFormatter.Expand(pattern, track, sanitizeForFilename: true);
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(track.FilePath);

        var dir = string.IsNullOrWhiteSpace(options.OutputFolder)
            ? Path.GetDirectoryName(track.FilePath) ?? AppContext.BaseDirectory
            : options.OutputFolder;

        return Path.Combine(dir, name + "." + options.Format);
    }

    private static async Task<(bool ok, string error)> RunFfmpegAsync(
        string ffmpeg, string source, string output, AudioConvertOptions options, CancellationToken ct)
    {
        var args = BuildArgs(source, output, options);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (false, "process start failed");

            // Drain stderr asynchronously so ffmpeg doesn't block on a full pipe.
            var stderrTask = p.StandardError.ReadToEndAsync();
            var stdoutTask = p.StandardOutput.ReadToEndAsync();

            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
            {
                await p.WaitForExitAsync(ct);
            }

            var stderr = await stderrTask;
            await stdoutTask;

            if (p.ExitCode == 0) return (true, string.Empty);
            // Surface only the last error line — full ffmpeg log is too noisy for UI.
            var lastLine = stderr.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? $"exit {p.ExitCode}";
            return (false, lastLine);
        }
        catch (OperationCanceledException)
        {
            return (false, "cancelled");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static List<string> BuildArgs(string source, string output, AudioConvertOptions options)
    {
        var args = new List<string>
        {
            "-y",            // overwrite (we already guarded the path)
            "-i", source,
            "-vn",           // strip video — embedded artwork is re-added below if requested
        };

        // Codec + bitrate per format.
        switch (options.Format.ToLowerInvariant())
        {
            case "mp3":
                args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", options.BitrateKbps + "k" });
                break;
            case "flac":
                args.AddRange(new[] { "-c:a", "flac" });
                break;
            case "opus":
                args.AddRange(new[] { "-c:a", "libopus", "-b:a", options.BitrateKbps + "k" });
                break;
            case "aac":
                args.AddRange(new[] { "-c:a", "aac", "-b:a", options.BitrateKbps + "k" });
                break;
            case "wav":
                args.AddRange(new[] { "-c:a", "pcm_s16le" });
                break;
            default:
                throw new InvalidOperationException("Unsupported format: " + options.Format);
        }

        if (options.CopyTags)
            args.AddRange(new[] { "-map_metadata", "0" });

        if (options.EmbedArtwork)
        {
            // -c:v copy preserves the embedded cover stream when supported.
            args.AddRange(new[] { "-map", "0", "-c:v", "copy", "-disposition:v:0", "attached_pic" });
        }

        args.Add(output);
        return args;
    }
}
