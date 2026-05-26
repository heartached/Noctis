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
    Task ScanAsync(
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

    public async Task ScanAsync(
        IReadOnlyList<Track> tracks,
        bool albumMode,
        IProgress<ScanProgress> progress,
        CancellationToken ct)
    {
        var ffmpeg = _converter.GetFfmpegPath();
        if (ffmpeg == null)
        {
            foreach (var t in tracks)
                progress.Report(new ScanProgress { Track = t, Status = "ffmpeg not found", Failed = true, Done = true });
            return;
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

            var ok = WriteReplayGainTags(t.FilePath, trackGainDb, trackPeakLinear, aGainDb, aPeakLinear);
            progress.Report(new ScanProgress
            {
                Track = t,
                Status = ok ? "Done" : "Tag write failed",
                Failed = !ok,
                Done = true,
                TrackGainDb = trackGainDb,
                AlbumGainDb = aGainDb ?? 0.0,
            });
        }
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

            // ebur128 summary block at end of stderr:
            //   [Parsed_ebur128_0 @ ...] Summary:
            //     Integrated loudness:
            //       I:         -14.2 LUFS
            //     True peak:
            //       Peak:       -0.7 dBFS
            var integrated = ParseFirstNumber(stderr, @"I:\s+(-?\d+(?:\.\d+)?)\s+LUFS");
            var peak = ParseFirstNumber(stderr, @"Peak:\s+(-?\d+(?:\.\d+)?)\s+dBFS");
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

    private static bool WriteReplayGainTags(
        string filePath, double trackGainDb, double trackPeakLinear, double? albumGainDb, double? albumPeakLinear)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            // ReplayGain 2.0 standard tag keys, with the dB suffix on gain
            // values and a linear (0..1+) value for peak.
            string Gain(double db) => db.ToString("F2", CultureInfo.InvariantCulture) + " dB";
            string Peak(double linear) => linear.ToString("F6", CultureInfo.InvariantCulture);

            // Try the modern Vorbis/MP4/Xiph path via Tag's user-text fields
            // when available. TagLib# exposes "replaygain_*" custom fields on
            // Ogg/FLAC tags directly; for ID3v2 we set TXXX frames.
            var trackGainTag = Gain(trackGainDb);
            var trackPeakTag = Peak(trackPeakLinear);

            // ID3v2 (mp3): set TXXX frames keyed by description.
            if (file.GetTag(TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
            {
                SetTxxx(id3, "REPLAYGAIN_TRACK_GAIN", trackGainTag);
                SetTxxx(id3, "REPLAYGAIN_TRACK_PEAK", trackPeakTag);
                if (albumGainDb.HasValue)
                {
                    SetTxxx(id3, "REPLAYGAIN_ALBUM_GAIN", Gain(albumGainDb.Value));
                    SetTxxx(id3, "REPLAYGAIN_ALBUM_PEAK", Peak(albumPeakLinear ?? 0.0));
                }
            }

            // Vorbis / FLAC / Opus / Ogg: SetField on the Xiph tag.
            if (file.GetTag(TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            {
                xiph.SetField("REPLAYGAIN_TRACK_GAIN", trackGainTag);
                xiph.SetField("REPLAYGAIN_TRACK_PEAK", trackPeakTag);
                if (albumGainDb.HasValue)
                {
                    xiph.SetField("REPLAYGAIN_ALBUM_GAIN", Gain(albumGainDb.Value));
                    xiph.SetField("REPLAYGAIN_ALBUM_PEAK", Peak(albumPeakLinear ?? 0.0));
                }
            }

            file.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SetTxxx(TagLib.Id3v2.Tag id3, string desc, string value)
    {
        // Remove any prior TXXX with this description so we don't duplicate.
        var existing = id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>()
            .Where(f => string.Equals(f.Description, desc, System.StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var f in existing) id3.RemoveFrame(f);

        var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, desc, true);
        frame.Text = new[] { value };
    }
}
