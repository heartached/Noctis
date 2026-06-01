using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Noctis.Models;
using TagLib;

namespace Noctis.Services;

/// <summary>Result of an EBU R128 loudness measurement for a single file.</summary>
public sealed class LoudnessResult
{
    /// <summary>Integrated loudness in LUFS (a negative number, e.g. -14.2).</summary>
    public double IntegratedLufs { get; set; }
    /// <summary>True peak in dBTP.</summary>
    public double TruePeakDbtp { get; set; }
    public bool Failed { get; set; }
    public string Error { get; set; } = string.Empty;
}

/// <summary>Per-file scan progress emitted by <see cref="ReplayGainScannerService"/>.</summary>
public sealed class ScanProgress
{
    public Track Track { get; set; } = null!;
    public string Status { get; set; } = string.Empty;
    public bool Done { get; set; }
    public bool Failed { get; set; }
    public double TrackGainDb { get; set; }
    public double AlbumGainDb { get; set; }
}

/// <summary>Outcome tallies for a completed ReplayGain scan run.</summary>
public sealed class ScanSummary
{
    /// <summary>Tracks measured and tagged successfully.</summary>
    public int Scanned { get; set; }

    /// <summary>Tracks that failed to measure or whose tags could not be written.</summary>
    public int Failed { get; set; }
}

public interface IReplayGainScannerService
{
    /// <summary>True if a usable ffmpeg binary is available.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Scan tracks for ReplayGain. Each track is measured individually. When
    /// <paramref name="albumMode"/> is true, tracks are grouped by AlbumId and
    /// an album gain is computed per group (target -18 LUFS, the ReplayGain 2.0
    /// reference). All four RG tags (track/album × gain/peak) are written.
    /// </summary>
    Task<ScanSummary> ScanAsync(
        IReadOnlyList<Track> tracks,
        bool albumMode,
        IProgress<ScanProgress> progress,
        CancellationToken ct);
}

public sealed class ReplayGainScannerService : IReplayGainScannerService
{
    private const double ReferenceLufs = -18.0; // ReplayGain 2.0 reference

    private readonly IAudioConverterService _converter; // re-used for ffmpeg discovery

    public ReplayGainScannerService(IAudioConverterService converter)
    {
        _converter = converter;
    }

    public bool IsAvailable => _converter.GetFfmpegPath() != null;

    public async Task<ScanSummary> ScanAsync(
        IReadOnlyList<Track> tracks,
        bool albumMode,
        IProgress<ScanProgress> progress,
        CancellationToken ct)
    {
        var summary = new ScanSummary();

        var ffmpeg = _converter.GetFfmpegPath();
        if (ffmpeg == null)
        {
            foreach (var t in tracks)
            {
                progress.Report(new ScanProgress { Track = t, Status = "ffmpeg not found", Failed = true, Done = true });
                summary.Failed++;
            }
            return summary;
        }

        // First pass: measure every track. We need every track's LUFS+peak
        // before we can compute album-level aggregates.
        var measured = new Dictionary<Track, LoudnessResult>();
        foreach (var t in tracks)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new ScanProgress { Track = t, Status = "Measuring…" });

            var r = await MeasureAsync(ffmpeg, t.FilePath, ct);
            measured[t] = r;
            if (r.Failed)
            {
                progress.Report(new ScanProgress { Track = t, Status = "Failed: " + r.Error, Failed = true, Done = true });
                summary.Failed++;
            }
        }

        // Album aggregates — energy-weighted mean of integrated LUFS would be
        // most correct, but the per-track integrated LUFS already accounts for
        // duration weighting internally; a straight mean is the standard
        // ReplayGain 2.0 fallback when re-measuring the concatenated album is
        // impractical. (rsgain follows the same pragmatic approach.)
        var albumGain = new Dictionary<Guid, double>();
        var albumPeak = new Dictionary<Guid, double>();
        if (albumMode)
        {
            foreach (var grp in tracks.Where(t => !measured[t].Failed).GroupBy(t => t.AlbumId))
            {
                var avgLufs = grp.Average(t => measured[t].IntegratedLufs);
                var peak = grp.Max(t => measured[t].TruePeakDbtp);
                albumGain[grp.Key] = ReferenceLufs - avgLufs;
                albumPeak[grp.Key] = peak;
            }
        }

        // Second pass: compute per-track gain and write tags.
        foreach (var t in tracks)
        {
            ct.ThrowIfCancellationRequested();
            var r = measured[t];
            if (r.Failed) continue;

            var trackGainDb = ReferenceLufs - r.IntegratedLufs;
            var trackPeakLinear = DbToLinear(r.TruePeakDbtp);
            double? aGainDb = null;
            double? aPeakLinear = null;
            if (albumMode && albumGain.TryGetValue(t.AlbumId, out var ag))
            {
                aGainDb = ag;
                aPeakLinear = DbToLinear(albumPeak[t.AlbumId]);
            }

            var (ok, error) = WriteReplayGainTags(t.FilePath, trackGainDb, trackPeakLinear, aGainDb, aPeakLinear);
            progress.Report(new ScanProgress
            {
                Track = t,
                Status = ok ? "Done" : ("Tag write failed: " + error),
                Failed = !ok,
                Done = true,
                TrackGainDb = trackGainDb,
                AlbumGainDb = aGainDb ?? 0.0,
            });

            if (ok) summary.Scanned++;
            else summary.Failed++;
        }

        return summary;
    }

    private static double DbToLinear(double db) => System.Math.Pow(10.0, db / 20.0);

    private static async Task<LoudnessResult> MeasureAsync(string ffmpeg, string source, CancellationToken ct)
    {
        // ebur128 prints a summary block to stderr when `peak=true` is set.
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-nostats", "-hide_banner", "-i", source, "-map", "0:a:0",
                                  "-af", "ebur128=peak=true", "-f", "null", "-" })
            psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return new LoudnessResult { Failed = true, Error = "process start failed" };

            var stderrTask = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();

            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
                await p.WaitForExitAsync(ct);

            var stderr = await stderrTask;
            if (p.ExitCode != 0)
                return new LoudnessResult { Failed = true, Error = "exit " + p.ExitCode };

            // ebur128 prints a running "I: … LUFS" line for EVERY frame while decoding
            // (the integrated value starts near -70 and converges to the real loudness),
            // then a final Summary block:
            //   [Parsed_ebur128_0 @ ...] Summary:
            //     Integrated loudness:
            //       I:         -14.2 LUFS
            //     True peak:
            //       Peak:       -0.7 dBFS
            // We must read I:/Peak: from the Summary block — parsing the first match in
            // the whole log picks up an early frame (~-70 LUFS) and yields a bogus +50 dB
            // gain. Scope to the text after the last "Summary:".
            var summaryIdx = stderr.LastIndexOf("Summary:", StringComparison.Ordinal);
            var summary = summaryIdx >= 0 ? stderr.Substring(summaryIdx) : stderr;
            var integrated = ParseFirstNumber(summary, @"I:\s+(-?\d+(?:\.\d+)?)\s+LUFS");
            var peak = ParseFirstNumber(summary, @"Peak:\s+(-?\d+(?:\.\d+)?)\s+dBFS");
            if (integrated == null) return new LoudnessResult { Failed = true, Error = "could not parse LUFS" };

            return new LoudnessResult
            {
                IntegratedLufs = integrated.Value,
                TruePeakDbtp = peak ?? 0.0,
            };
        }
        catch (OperationCanceledException)
        {
            return new LoudnessResult { Failed = true, Error = "cancelled" };
        }
        catch (System.Exception ex)
        {
            return new LoudnessResult { Failed = true, Error = ex.Message };
        }
    }

    private static double? ParseFirstNumber(string s, string regex)
    {
        var m = Regex.Match(s, regex);
        if (!m.Success) return null;
        if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }

    private static (bool ok, string error) WriteReplayGainTags(
        string filePath, double trackGainDb, double trackPeakLinear, double? albumGainDb, double? albumPeakLinear)
    {
        // ReplayGain 2.0 standard tag keys: dB suffix on gain, linear (0..1+) peak.
        string Gain(double db) => db.ToString("F2", CultureInfo.InvariantCulture) + " dB";
        string Peak(double linear) => linear.ToString("F6", CultureInfo.InvariantCulture);

        // The file can be momentarily locked by another handle (the dialog's
        // "already scanned" tag read, the library indexer, antivirus). Those clear
        // quickly, so retry a few times before giving up. A persistent lock (e.g. the
        // track is currently playing) still surfaces the real OS message.
        const int maxAttempts = 5;
        var lastError = string.Empty;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var file = TagLib.File.Create(filePath);

                // AdvancedTagIO.WriteCustomField writes to whichever tag the file carries —
                // ID3v2 (mp3), Xiph (flac/ogg/opus), and MP4 freeform atoms (m4a/alac/aac) —
                // so ReplayGain now works for every format the app supports, not just mp3/flac.
                AdvancedTagIO.WriteCustomField(file, "REPLAYGAIN_TRACK_GAIN", Gain(trackGainDb));
                AdvancedTagIO.WriteCustomField(file, "REPLAYGAIN_TRACK_PEAK", Peak(trackPeakLinear));
                if (albumGainDb.HasValue)
                {
                    AdvancedTagIO.WriteCustomField(file, "REPLAYGAIN_ALBUM_GAIN", Gain(albumGainDb.Value));
                    AdvancedTagIO.WriteCustomField(file, "REPLAYGAIN_ALBUM_PEAK", Peak(albumPeakLinear ?? 0.0));
                }

                file.Save();
                return (true, string.Empty);
            }
            catch (System.IO.IOException ex)
            {
                // Transient sharing violation — wait briefly for the other handle to close.
                lastError = ex.Message;
                System.Threading.Thread.Sleep(150);
            }
            catch (System.Exception ex)
            {
                // Non-transient (unsupported format, permissions) — don't retry.
                return (false, ex.Message);
            }
        }
        return (false, lastError);
    }
}
