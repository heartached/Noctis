namespace Noctis.Services.AudioAnalysis;

/// <summary>What the frequency content suggests about where a file came from. Likelihoods,
/// never proof: a quiet, old or band-limited recording can look like any of these.</summary>
public enum SourceQualityVerdict
{
    CantTell,
    /// <summary>Lossless format, content reaches (near) Nyquist or rolls off gently.</summary>
    LooksGenuine,
    /// <summary>Lossless format with a steep shelf and an empty band above it, where MP3/AAC cut.</summary>
    ProbablyLossySource,
    /// <summary>Hi-res sample rate with nothing above CD/48 kHz bandwidth.</summary>
    ProbablyUpsampled,
    /// <summary>Lossy format whose cutoff fits its stated bitrate.</summary>
    MatchesBitrate,
    /// <summary>Lossy format whose cutoff is far below what its stated bitrate keeps.</summary>
    ProbablyReencoded,
}

/// <summary>Which encoder setting a shelf frequency is typical of (only for a lossy-source verdict).</summary>
public enum LossyCutoffClass
{
    None,
    /// <summary>≈ 15–17.5 kHz: MP3 128 kbps (and the classic 16 kHz sfb21 gap).</summary>
    Mp3Low,
    /// <summary>≈ 17.5–19 kHz: MP3 160–192 kbps.</summary>
    Mp3Mid,
    /// <summary>≈ 19–20.8 kHz: MP3 320 kbps or AAC.</summary>
    Mp3High,
}

/// <summary>The stream facts the detector weighs next to the spectrum.</summary>
/// <param name="IsLosslessFormat">The container/codec claims lossless (Track.IsLossless).</param>
/// <param name="Format">Short codec name ("MP3", "AAC", "Opus", "Vorbis", "FLAC", …).</param>
/// <param name="BitsPerSample">Stated bit depth, 0 when unknown.</param>
/// <param name="BitrateKbps">Stated bitrate, 0 when unknown.</param>
public readonly record struct SourceQualityInput(bool IsLosslessFormat, string Format, int BitsPerSample, int BitrateKbps);

/// <summary>The verdict plus the numbers behind it.</summary>
public sealed record SourceQualityResult(
    SourceQualityVerdict Verdict,
    double? CutoffHz,
    double DropDbPerKhz,
    bool BandAboveEmpty,
    bool Has16kGap,
    LossyCutoffClass CutoffClass,
    int ActiveColumns,
    double NoiseFloorDb,
    double NyquistHz)
{
    /// <summary>True when the cutoff is worth drawing: found, and well below Nyquist.</summary>
    public bool CutoffWellBelowNyquist =>
        CutoffHz is { } c && c <= NyquistHz - SourceQualityDetector.MarkerMinGapBelowNyquistHz;
}

/// <summary>
/// "Is this really lossless?" from a spectrogram the window already computed. Pure and
/// UI-free: per frequency bin it takes the level 70 % of the sounding columns exceed, finds
/// the highest frequency still clearly above the noise floor, measures how steeply the
/// spectrum falls just above it and whether the band from there to Nyquist is empty.
/// A steep shelf with nothing above it at the frequencies MP3/AAC encoders low-pass at is
/// the classic sign of a lossy file converted to a lossless container.
/// </summary>
public static class SourceQualityDetector
{
    // ── Thresholds (all in one place) ──

    /// <summary>A column whose loudest bin is below this is treated as silence and skipped.</summary>
    public const double SilentColumnPeakDb = -60;
    /// <summary>Fewer sounding columns than this (or than <see cref="MinActiveFraction"/>) → can't tell.</summary>
    public const int MinActiveColumns = 24;
    public const double MinActiveFraction = 0.2;
    /// <summary>Per bin, the level this share of the sounding columns stays above (≈ 70 %).</summary>
    public const double PresenceFraction = 0.70;
    /// <summary>Content counts when it is this far above the floor…</summary>
    public const double ClearMarginDb = 12;
    /// <summary>…or above this absolute level, whichever is lower (a loud, flat spectrum has a high "floor").</summary>
    public const double AbsoluteContentDb = -85;
    /// <summary>Moving-average width (bins) before looking for the cutoff, so a lone tone doesn't count.</summary>
    public const int SmoothBins = 5;
    /// <summary>Content this close to Nyquist reaches the top: nothing was cut.</summary>
    public const double NearNyquistHz = 1000;
    /// <summary>The drop is measured between a window this far below and one this far above the cutoff.</summary>
    public const double SlopeWindowNearHz = 300;
    public const double SlopeWindowFarHz = 1000;
    /// <summary>A shelf steeper than this (dB per kHz) is a filter, not a natural roll-off.</summary>
    public const double SteepDbPerKhz = 20;
    /// <summary>The band above the cutoff is "empty" when its average is within this of the floor.</summary>
    public const double EmptyMarginDb = 6;
    /// <summary>Lossy encoders low-pass in this range; a shelf here in a lossless file is suspicious.</summary>
    public const double LossyCutoffMinHz = 15000;
    public const double LossyCutoffMaxHz = 20800;
    public const double Mp3LowMaxHz = 17500;
    public const double Mp3MidMaxHz = 19000;
    /// <summary>The classic MP3 16 kHz gap (scalefactor band 21 dropped).</summary>
    public const double Gap16kMinHz = 15500;
    public const double Gap16kMaxHz = 16500;
    /// <summary>A shelf must sit at least this far below Nyquist to count as a cut.</summary>
    public const double MinGapBelowNyquistHz = 1500;
    /// <summary>The marker on the plot is drawn only for a cutoff this far below Nyquist.</summary>
    public const double MarkerMinGapBelowNyquistHz = 1500;
    /// <summary>Hi-res: a sample rate at or above this…</summary>
    public const int HiResSampleRate = 88200;
    /// <summary>…whose content stops by here was probably upsampled: CD 22.05 / 48 kHz 24 kHz plus
    /// the resampler filter's leak past the old Nyquist (measured on full-band noise with FFmpeg's
    /// default resampler: 44.1→96 kHz reads ≈ 25.3 kHz, 48→96 kHz ≈ 27.5 kHz).</summary>
    public const double UpsampledMaxCutoffHz = 30000;
    /// <summary>A gentle roll-off ending at or above this counts as genuine.</summary>
    public const double GentleGenuineMinHz = 17000;
    /// <summary>Lossy files: a shelf this far below the bitrate's usual cutoff means re-encoded.</summary>
    public const double ReencodeMarginHz = 1000;

    /// <summary>
    /// The lowest cutoff a normal encoder keeps at this bitrate (LAME / FFmpeg AAC / Vorbis
    /// low-pass defaults, rounded down). 0 = no expectation (low bitrates, Opus fullband).
    /// </summary>
    public static double ExpectedMinCutoffHz(string format, int bitrateKbps)
    {
        if (bitrateKbps <= 0) return 0;
        switch (format)
        {
            case "MP3":
            case "AAC":
            case "Vorbis":
                if (bitrateKbps >= 256) return 19000;
                if (bitrateKbps >= 192) return 17500;
                if (bitrateKbps >= 160) return 16500;
                if (bitrateKbps >= 128) return 15000;
                return 0;
            case "Opus":
                // Opus is fullband (20 kHz) from ~64 kbps up.
                return bitrateKbps >= 96 ? 19000 : 0;
            default:
                return 0;
        }
    }

    public static SourceQualityResult Analyze(SpectrogramData data, SourceQualityInput info)
    {
        double nyquist = data.NyquistHz;
        int bins = data.Bins;
        double binHz = nyquist / bins;

        // DSD (MHz rates) carries shaped ultrasonic noise; the shelf logic means nothing there.
        if (data.SampleRate > 384000 || bins < 64 || data.Columns <= 0)
            return Empty(nyquist, 0);

        // 1. Sounding columns only.
        var active = new List<int>(data.Columns);
        for (int c = 0; c < data.Columns; c++)
        {
            var col = data.Db.AsSpan(c * bins, bins);
            float peak = (float)SpectrogramRenderer.MinDb;
            for (int k = 1; k < bins; k++) if (col[k] > peak) peak = col[k];
            if (peak >= SilentColumnPeakDb) active.Add(c);
        }
        if (active.Count < MinActiveColumns || active.Count < data.Columns * MinActiveFraction)
            return Empty(nyquist, active.Count);

        // 2. Per bin: the level 70 % of the sounding columns stay above.
        var level = new double[bins];
        var scratch = new float[active.Count];
        int rank = (int)Math.Floor((1 - PresenceFraction) * (active.Count - 1));
        for (int k = 0; k < bins; k++)
        {
            for (int i = 0; i < active.Count; i++) scratch[i] = data.Db[active[i] * bins + k];
            Array.Sort(scratch);
            level[k] = scratch[rank];
        }
        var smooth = Smooth(level, SmoothBins);

        // 3. Noise floor: the quietest level above 1 kHz; content = clearly above it.
        int from1k = Math.Min(bins - 1, (int)Math.Ceiling(1000 / binHz));
        double floor = double.MaxValue;
        for (int k = from1k; k < bins; k++) floor = Math.Min(floor, smooth[k]);
        double threshold = Math.Min(floor + ClearMarginDb, AbsoluteContentDb);

        int cutBin = -1;
        for (int k = bins - 1; k >= from1k; k--)
            if (smooth[k] > threshold) { cutBin = k; break; }
        if (cutBin < 0)
            return Empty(nyquist, active.Count) with { NoiseFloorDb = floor };

        double cutoff = (cutBin + 1) * binHz;

        // 4. Steepness just above the cutoff, and is the band above it empty?
        double below = Mean(smooth, BinOf(cutoff - SlopeWindowFarHz, binHz, bins), BinOf(cutoff - SlopeWindowNearHz, binHz, bins));
        int aboveLo = BinOf(cutoff + SlopeWindowNearHz, binHz, bins);
        int aboveHi = BinOf(cutoff + SlopeWindowFarHz, binHz, bins);
        double above = aboveLo < bins ? Mean(level, aboveLo, aboveHi) : floor;
        double centreGapKhz = (SlopeWindowNearHz + SlopeWindowFarHz) / 1000.0; // centre-to-centre
        double drop = Math.Max(0, (below - above) / centreGapKhz);
        bool steep = drop >= SteepDbPerKhz;

        int bandLo = BinOf(cutoff + 2 * SlopeWindowNearHz, binHz, bins);
        bool bandAboveEmpty = bandLo >= bins - 1 || Mean(level, bandLo, bins - 1) <= floor + EmptyMarginDb;
        bool gap16k = steep && cutoff >= Gap16kMinHz && cutoff <= Gap16kMaxHz;

        bool reachesTop = cutoff >= nyquist - NearNyquistHz;
        bool wellBelowNyquist = cutoff <= nyquist - MinGapBelowNyquistHz;
        bool shelf = steep && (bandAboveEmpty || gap16k) && wellBelowNyquist;

        var cls = LossyCutoffClass.None;
        SourceQualityVerdict verdict;
        if (info.IsLosslessFormat)
        {
            if (reachesTop)
                verdict = SourceQualityVerdict.LooksGenuine;
            else if (shelf && cutoff >= LossyCutoffMinHz && cutoff <= LossyCutoffMaxHz)
            {
                verdict = SourceQualityVerdict.ProbablyLossySource;
                cls = cutoff < Mp3LowMaxHz ? LossyCutoffClass.Mp3Low
                    : cutoff < Mp3MidMaxHz ? LossyCutoffClass.Mp3Mid
                    : LossyCutoffClass.Mp3High;
            }
            else if (data.SampleRate >= HiResSampleRate && bandAboveEmpty && cutoff <= UpsampledMaxCutoffHz)
                verdict = SourceQualityVerdict.ProbablyUpsampled;
            else if (!steep && cutoff >= GentleGenuineMinHz)
                verdict = SourceQualityVerdict.LooksGenuine;
            else
                verdict = SourceQualityVerdict.CantTell;
        }
        else
        {
            double expected = ExpectedMinCutoffHz(info.Format, info.BitrateKbps);
            if (info.BitrateKbps <= 0)
                verdict = SourceQualityVerdict.CantTell;
            else if (shelf && expected > 0 && cutoff < expected - ReencodeMarginHz)
                verdict = SourceQualityVerdict.ProbablyReencoded;
            else if (reachesTop || cutoff >= expected - ReencodeMarginHz)
                verdict = SourceQualityVerdict.MatchesBitrate;
            else
                verdict = SourceQualityVerdict.CantTell;
        }

        return new SourceQualityResult(verdict, cutoff, drop, bandAboveEmpty, gap16k, cls, active.Count, floor, nyquist);
    }

    private static SourceQualityResult Empty(double nyquist, int active) =>
        new(SourceQualityVerdict.CantTell, null, 0, false, false, LossyCutoffClass.None, active, SpectrogramRenderer.MinDb, nyquist);

    private static int BinOf(double hz, double binHz, int bins) => Math.Clamp((int)Math.Round(hz / binHz), 0, bins);

    private static double Mean(double[] v, int lo, int hi)
    {
        lo = Math.Clamp(lo, 0, v.Length - 1);
        hi = Math.Clamp(hi, lo, v.Length - 1);
        double sum = 0;
        for (int k = lo; k <= hi; k++) sum += v[k];
        return sum / (hi - lo + 1);
    }

    private static double[] Smooth(double[] v, int width)
    {
        var o = new double[v.Length];
        int half = width / 2;
        for (int k = 0; k < v.Length; k++)
        {
            int lo = Math.Max(0, k - half), hi = Math.Min(v.Length - 1, k + half);
            double s = 0;
            for (int j = lo; j <= hi; j++) s += v[j];
            o[k] = s / (hi - lo + 1);
        }
        return o;
    }
}
