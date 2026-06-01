using System.Diagnostics;
using System.Runtime.InteropServices;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Describes an output format the converter can produce: its dropdown key, the
/// file extension to write, whether it is lossless (bit-depth applies / bitrate
/// does not), and whether ffmpeg can carry an attached cover picture in it.
/// </summary>
public sealed record OutputFormatInfo(
    string Key,
    string Extension,
    bool IsLossless,
    bool ArtworkSupported);

/// <summary>Transcode target settings for a single conversion job.</summary>
public sealed class AudioConvertOptions
{
    /// <summary>Output format key — one of <see cref="AudioConverterService.OutputFormats"/>.</summary>
    public string Format { get; set; } = "mp3";

    /// <summary>Target bitrate in kbps. Ignored for lossless formats.</summary>
    public int BitrateKbps { get; set; } = 320;

    /// <summary>Output bit depth for lossless formats: "Auto", "16", or "24". Ignored for lossy formats.</summary>
    public string BitDepth { get; set; } = "Auto";

    /// <summary>Output directory. If empty, conversion runs alongside the source file.</summary>
    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>Filename pattern (without extension). Tokens accepted by <see cref="TitleFormatter"/>.</summary>
    public string FilenamePattern { get; set; } = "%artist% - %title%";

    /// <summary>Copy ID3/Vorbis tags from source to output.</summary>
    public bool CopyTags { get; set; } = true;

    /// <summary>
    /// Append " (FORMAT)" to the output's title tag (e.g. "Song (WAV)") so the
    /// converted copy is distinguishable from the original when shown in the app.
    /// Set when the result is imported into the library.
    /// </summary>
    public bool AppendFormatToTitle { get; set; }

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

/// <summary>Outcome tallies for a completed conversion run.</summary>
public sealed class ConvertSummary
{
    /// <summary>Files successfully transcoded.</summary>
    public int Converted { get; set; }

    /// <summary>Files that errored during transcode.</summary>
    public int Failed { get; set; }

    /// <summary>Files intentionally skipped (already exist, or output would be the source).</summary>
    public int Skipped { get; set; }

    /// <summary>Absolute paths of the files that were successfully written.</summary>
    public List<string> OutputPaths { get; } = new();
}

public interface IAudioConverterService
{
    /// <summary>Absolute path to a usable ffmpeg executable, or null if none was found.</summary>
    string? GetFfmpegPath();

    /// <summary>
    /// Confirms a path is a real ffmpeg binary by running it with <c>-version</c> and checking
    /// the banner. Existence alone is not enough (e.g. a renamed/unrelated file). When
    /// <paramref name="path"/> is null/blank, the resolved <see cref="GetFfmpegPath"/> is probed.
    /// Returns the reported version line on success, or null if it isn't a usable ffmpeg.
    /// </summary>
    Task<string?> ValidateFfmpegAsync(string? path = null, CancellationToken ct = default);

    /// <summary>Convert <paramref name="tracks"/> sequentially. Reports progress per file
    /// and returns outcome tallies plus the paths of successfully written files.</summary>
    Task<ConvertSummary> ConvertAsync(
        IReadOnlyList<Track> tracks,
        AudioConvertOptions options,
        IProgress<ConvertProgress> progress,
        CancellationToken ct);
}

public sealed class AudioConverterService : IAudioConverterService
{
    /// <summary>
    /// Output formats offered by the converter. Limited to the format families the
    /// app supports that ffmpeg can reliably encode (so every choice produces a
    /// valid file — e.g. Monkey's Audio/.ape is excluded as ffmpeg has no encoder).
    /// </summary>
    public static readonly IReadOnlyList<OutputFormatInfo> OutputFormats = new[]
    {
        new OutputFormatInfo("mp3",     ".mp3",  IsLossless: false, ArtworkSupported: true),
        new OutputFormatInfo("m4a",     ".m4a",  IsLossless: false, ArtworkSupported: true),
        new OutputFormatInfo("aac",     ".aac",  IsLossless: false, ArtworkSupported: false),
        new OutputFormatInfo("opus",    ".opus", IsLossless: false, ArtworkSupported: false),
        new OutputFormatInfo("ogg",     ".ogg",  IsLossless: false, ArtworkSupported: false),
        new OutputFormatInfo("wma",     ".wma",  IsLossless: false, ArtworkSupported: false),
        new OutputFormatInfo("flac",    ".flac", IsLossless: true,  ArtworkSupported: true),
        new OutputFormatInfo("alac",    ".m4a",  IsLossless: true,  ArtworkSupported: true),
        new OutputFormatInfo("wav",     ".wav",  IsLossless: true,  ArtworkSupported: false),
        new OutputFormatInfo("aiff",    ".aiff", IsLossless: true,  ArtworkSupported: false),
        new OutputFormatInfo("wavpack", ".wv",   IsLossless: true,  ArtworkSupported: false),
    };

    /// <summary>Looks up a format descriptor by key (case-insensitive), or null if unknown.</summary>
    public static OutputFormatInfo? FindFormat(string key) =>
        OutputFormats.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    private readonly Func<string> _ffmpegPathOverride;
    private readonly IMetadataService _metadata;

    /// <param name="ffmpegPathOverride">Returns the user-specified ffmpeg path, or empty for auto-detect.</param>
    /// <param name="metadata">Used to stamp the app's full tag set onto converted files.</param>
    public AudioConverterService(Func<string> ffmpegPathOverride, IMetadataService metadata)
    {
        _ffmpegPathOverride = ffmpegPathOverride;
        _metadata = metadata;
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

    public async Task<string?> ValidateFfmpegAsync(string? path = null, CancellationToken ct = default)
    {
        // Resolve: explicit path must exist; otherwise fall back to normal detection.
        string? exe;
        if (!string.IsNullOrWhiteSpace(path))
            exe = File.Exists(path) ? path : null;
        else
            exe = GetFfmpegPath();
        if (exe == null) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync(); // drain so the pipe can't fill and block

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            var output = await stdoutTask;
            // ffmpeg's first stdout line is e.g. "ffmpeg version 8.1-full_build-...".
            var firstLine = output.Split('\n', 2)[0].Trim();
            return firstLine.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase)
                ? firstLine
                : null;
        }
        catch
        {
            // Non-executable file, wrong architecture, access denied, etc. → not usable.
            return null;
        }
    }

    public async Task<ConvertSummary> ConvertAsync(
        IReadOnlyList<Track> tracks,
        AudioConvertOptions options,
        IProgress<ConvertProgress> progress,
        CancellationToken ct)
    {
        var summary = new ConvertSummary();

        var ffmpeg = GetFfmpegPath();
        if (ffmpeg == null)
        {
            foreach (var t in tracks)
            {
                progress.Report(new ConvertProgress { Track = t, Status = "ffmpeg not found", Failed = true, Done = true });
                summary.Failed++;
            }
            return summary;
        }

        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();

            var outPath = ComputeOutputPath(track, options);

            // Guard against converting a file onto itself (e.g. mp3→mp3 in place):
            // ffmpeg opens the source for reading while -y truncates the same path,
            // which corrupts the original. Skip rather than destroy the source.
            if (!string.IsNullOrWhiteSpace(track.FilePath) &&
                string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(track.FilePath), StringComparison.OrdinalIgnoreCase))
            {
                progress.Report(new ConvertProgress { Track = track, Status = "Skipped (same as source)", Done = true });
                summary.Skipped++;
                continue;
            }

            progress.Report(new ConvertProgress { Track = track, Status = "Converting…" });

            if (File.Exists(outPath) && !options.OverwriteExisting)
            {
                progress.Report(new ConvertProgress { Track = track, Status = "Skipped (exists)", Done = true, OutputPath = outPath });
                summary.Skipped++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            // Title override (e.g. "Song (WAV)") so the imported copy is distinguishable.
            var titleOverride = options.AppendFormatToTitle && !string.IsNullOrWhiteSpace(track.Title)
                ? $"{track.Title} ({options.Format.ToUpperInvariant()})"
                : null;

            var (ok, error) = await RunFfmpegAsync(ffmpeg, track.FilePath, outPath, options, titleOverride, ct);
            progress.Report(new ConvertProgress
            {
                Track = track,
                Status = ok ? "Done" : ("Failed: " + error),
                Done = true,
                Failed = !ok,
                OutputPath = ok ? outPath : string.Empty,
            });

            if (ok)
            {
                // ffmpeg's -map_metadata copies standard tags, but the app's custom
                // fields (explicit/ITUNESADVISORY, sort fields, release type, work/
                // movement, people credits…) don't round-trip reliably — especially
                // across formats. Re-stamp the output with the source's full tag set
                // using the same writers the metadata editor uses, so the converted
                // copy matches the original everywhere the app reads tags.
                if (options.CopyTags)
                    StampSourceMetadata(track, outPath, titleOverride);

                summary.Converted++;
                summary.OutputPaths.Add(outPath);
            }
            else
            {
                summary.Failed++;
            }
        }

        return summary;
    }

    /// <summary>
    /// Copies the full app-managed tag set from the source track onto the converted
    /// output: Details + extended via <see cref="IMetadataService.WriteTrackMetadata"/>,
    /// and the Advanced Details (sort fields, explicit/advisory, people, identifiers)
    /// via <see cref="AdvancedTagIO"/>. Best-effort — ffmpeg already copied the standard
    /// tags, so a failure here (e.g. a format TagLib can't write) is non-fatal.
    /// </summary>
    private void StampSourceMetadata(Track source, string outPath, string? titleOverride)
    {
        try
        {
            _metadata.WriteTrackMetadata(source, outPath, titleOverride);

            var sourceAdvanced = AdvancedTagIO.ReadAll(source.FilePath);
            var outputAdvanced = AdvancedTagIO.ReadAll(outPath);
            AdvancedTagIO.WriteAll(outPath, sourceAdvanced, outputAdvanced);
        }
        catch
        {
            // Non-fatal: the file is still valid with ffmpeg's copied tags.
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

        // Extension comes from the format descriptor, not the key — e.g. both
        // "aac" and "alac" differ from their on-disk extension (.aac vs .m4a).
        var ext = FindFormat(options.Format)?.Extension ?? ("." + options.Format);
        return Path.Combine(dir, name + ext);
    }

    private static async Task<(bool ok, string error)> RunFfmpegAsync(
        string ffmpeg, string source, string output, AudioConvertOptions options, string? titleOverride, CancellationToken ct)
    {
        var args = BuildArgs(source, output, options, titleOverride);

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

    private static List<string> BuildArgs(string source, string output, AudioConvertOptions options, string? titleOverride = null)
    {
        var key = options.Format.ToLowerInvariant();
        var info = FindFormat(key);

        // Cover-art passthrough only works for containers/codecs that carry an
        // attached picture stream (mp3 ID3 APIC, flac picture block, mp4 covr for
        // aac/alac). For the rest we strip video to avoid muxer errors instead of
        // producing a failed file. The descriptor's ArtworkSupported flag is the
        // single source of truth for which formats qualify.
        var embedArtwork = options.EmbedArtwork && (info?.ArtworkSupported ?? false);

        var args = new List<string>
        {
            "-y",            // overwrite (we already guarded the path)
            "-i", source,
        };

        if (embedArtwork)
        {
            // Keep the audio + (optional) cover image only. The "?" makes the video
            // stream optional so sources without embedded art don't fail, and mapping
            // explicitly avoids pulling subtitle/data streams the muxer can't take.
            args.AddRange(new[] { "-map", "0:a:0", "-map", "0:v:0?" });
        }
        else
        {
            args.Add("-vn"); // strip video / cover entirely
        }

        AppendAudioCodecArgs(args, key, options);

        if (embedArtwork)
        {
            // -c:v copy preserves the embedded cover; mark it as an attached picture.
            args.AddRange(new[] { "-c:v", "copy", "-disposition:v:0", "attached_pic" });
        }

        if (options.CopyTags)
            args.AddRange(new[] { "-map_metadata", "0" });

        // Override the title last so it wins over any copied source title.
        if (!string.IsNullOrWhiteSpace(titleOverride))
            args.AddRange(new[] { "-metadata", "title=" + titleOverride });

        args.Add(output);
        return args;
    }

    /// <summary>
    /// Appends the codec selection for <paramref name="key"/>. Lossy formats take a
    /// bitrate; lossless formats take an optional bit depth ("Auto" lets the encoder
    /// keep the source). FLAC/WAV/AIFF encode the exact requested depth; ALAC and
    /// WavPack only expose 16- vs 32-bit sample formats in ffmpeg, so "24" maps to
    /// their 32-bit format (still lossless).
    /// </summary>
    private static void AppendAudioCodecArgs(List<string> args, string key, AudioConvertOptions o)
    {
        var br = o.BitrateKbps + "k";
        switch (key)
        {
            case "mp3": args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", br }); break;
            case "m4a": args.AddRange(new[] { "-c:a", "aac", "-b:a", br }); break;
            case "aac": args.AddRange(new[] { "-c:a", "aac", "-b:a", br }); break;
            case "opus": args.AddRange(new[] { "-c:a", "libopus", "-b:a", br }); break;
            case "ogg": args.AddRange(new[] { "-c:a", "libvorbis", "-b:a", br }); break;
            case "wma": args.AddRange(new[] { "-c:a", "wmav2", "-b:a", br }); break;

            case "flac":
                args.AddRange(new[] { "-c:a", "flac" });
                if (o.BitDepth == "16") args.AddRange(new[] { "-sample_fmt", "s16" });
                else if (o.BitDepth == "24") args.AddRange(new[] { "-sample_fmt", "s32", "-bits_per_raw_sample", "24" });
                break;

            case "alac":
                args.AddRange(new[] { "-c:a", "alac" });
                if (o.BitDepth == "16") args.AddRange(new[] { "-sample_fmt", "s16p" });
                else if (o.BitDepth == "24") args.AddRange(new[] { "-sample_fmt", "s32p" });
                break;

            case "wavpack":
                args.AddRange(new[] { "-c:a", "wavpack" });
                if (o.BitDepth == "16") args.AddRange(new[] { "-sample_fmt", "s16p" });
                else if (o.BitDepth == "24") args.AddRange(new[] { "-sample_fmt", "s32p" });
                break;

            case "wav":
                args.AddRange(new[] { "-c:a", o.BitDepth == "24" ? "pcm_s24le" : "pcm_s16le" });
                break;

            case "aiff":
                args.AddRange(new[] { "-c:a", o.BitDepth == "24" ? "pcm_s24be" : "pcm_s16be" });
                break;

            default:
                throw new InvalidOperationException("Unsupported format: " + o.Format);
        }
    }
}
