using System;
using System.Collections.Generic;
using System.Linq;
using Noctis.Services.LyricsStudio;

namespace Noctis.ViewModels;

/// <summary>One moment of the model download as the Studio's banner shows it.</summary>
internal readonly record struct DownloadReading(
    ModelDownloadPhase Phase,
    long BytesDone,
    long BytesTotal,
    /// <summary>0–1, never lower than an earlier reading unless the download started over from zero.</summary>
    double Fraction,
    /// <summary>Null until the speed has settled.</summary>
    double? BytesPerSecond,
    TimeSpan? Remaining,
    int Attempt,
    TimeSpan RetryIn,
    double VerifyFraction,
    string? Error);

/// <summary>
/// Turns the model manager's download reports into a steady banner (09-29): a fraction that
/// never steps back, a speed measured over the last few seconds (so it neither jitters per
/// chunk nor lags a change of link), a time left, and the retry countdown. Sampled on the
/// Studio's UI tick, so it needs no locking.
/// </summary>
internal sealed class DownloadMeter
{
    /// <summary>Speed is measured over this much recent download.</summary>
    internal static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(5);
    /// <summary>No speed or time left before this much download was seen (the first second is warm-up).</summary>
    internal static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1.5);

    private readonly Queue<(TimeSpan At, long Bytes)> _samples = new();
    private ModelDownloadProgress? _last;
    private TimeSpan _retrySeenAt;
    private double _fraction;

    public void Sample(ModelDownloadProgress? report, TimeSpan now)
    {
        if (report is null) return;
        if (report.Phase == ModelDownloadPhase.Downloading)
        {
            if (_samples.Count > 0 && report.BytesDone < _samples.Last().Bytes)
            {
                // The server ignored the resume and the file started over: say so honestly.
                _samples.Clear();
                _fraction = 0;
            }
            if (_samples.Count == 0 || _samples.Last().Bytes != report.BytesDone)
                _samples.Enqueue((now, report.BytesDone));
            while (_samples.Count > 2 && now - _samples.Peek().At > SpeedWindow)
                _samples.Dequeue();
        }
        else if (report.Phase is ModelDownloadPhase.Connecting or ModelDownloadPhase.Retrying)
        {
            // A retry wait is not throughput: measure afresh once bytes flow again.
            _samples.Clear();
        }
        if (report.Phase == ModelDownloadPhase.Retrying && (_last is not { Phase: ModelDownloadPhase.Retrying } || _last.Attempt != report.Attempt))
            _retrySeenAt = now;
        _last = report;
    }

    public DownloadReading Read(TimeSpan now)
    {
        var r = _last ?? new ModelDownloadProgress(ModelDownloadPhase.Connecting, 0, 0);
        var total = r.BytesTotal;
        var raw = r.Phase is ModelDownloadPhase.Verifying or ModelDownloadPhase.Done ? 1
            : total > 0 ? Math.Clamp(r.BytesDone / (double)total, 0, 1) : 0;
        _fraction = Math.Max(_fraction, raw);

        double? speed = null;
        TimeSpan? remaining = null;
        if (r.Phase == ModelDownloadPhase.Downloading && _samples.Count >= 2)
        {
            var first = _samples.Peek();
            var last = _samples.Last();
            // Measured up to now, not the last sample: a stalled link shows its speed falling.
            var seconds = (now - first.At).TotalSeconds;
            if (seconds >= SettleTime.TotalSeconds && last.Bytes > first.Bytes)
            {
                speed = (last.Bytes - first.Bytes) / seconds;
                if (total > r.BytesDone) remaining = TimeSpan.FromSeconds((total - r.BytesDone) / speed.Value);
            }
        }
        var retryIn = r.Phase == ModelDownloadPhase.Retrying ? r.RetryIn - (now - _retrySeenAt) : TimeSpan.Zero;
        if (retryIn < TimeSpan.Zero) retryIn = TimeSpan.Zero;
        return new DownloadReading(r.Phase, r.BytesDone, total, _fraction, speed, remaining, r.Attempt, retryIn, r.VerifyFraction, r.Error);
    }

    /// <summary>"about 40 s left" / "about 3 min left" / "about 1 h 5 min left": rounded so it does not flicker.</summary>
    internal static string RemainingText(TimeSpan t)
    {
        if (t.TotalSeconds < 50)
            return Localization.Loc.T("LyricsStudio.EtaSeconds", Math.Max(5, (int)Math.Ceiling(t.TotalSeconds / 5) * 5));
        if (t.TotalMinutes < 59.5)
            return Localization.Loc.T("LyricsStudio.EtaMinutes", Math.Max(1, (int)Math.Round(t.TotalMinutes, MidpointRounding.AwayFromZero)));
        var minutes = (int)Math.Round(t.TotalMinutes, MidpointRounding.AwayFromZero);
        return Localization.Loc.T("LyricsStudio.EtaHours", minutes / 60, minutes % 60);
    }

    /// <summary>"850 KB/s", "4.2 MB/s", "12 MB/s".</summary>
    internal static string RateText(double bytesPerSecond)
    {
        var c = System.Globalization.CultureInfo.CurrentCulture;
        var mb = bytesPerSecond / (1 << 20);
        if (mb >= 10) return mb.ToString("0", c) + " MB/s";
        if (mb >= 1) return mb.ToString("0.0", c) + " MB/s";
        return Math.Max(1, bytesPerSecond / 1024).ToString("0", c) + " KB/s";
    }

    /// <summary>Whole percent, rounded down so it only says 100% once the work is done.</summary>
    internal static string PercentText(double fraction) =>
        ((int)Math.Floor(Math.Clamp(fraction, 0, 1) * 100)).ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";
}

/// <summary>
/// One song's progress bar (09-29). The engine reports a step and how far into it; this weights
/// the steps into one bar (<see cref="LyricsStudioStages"/>), keeps it from stepping back when a
/// late report lands, and moves it on between reports. Whisper reports once per 30 s window, so
/// a Medium run on a three-minute song otherwise stood still for several seconds, then jumped a
/// sixth of the way: here each window is paced by how long the earlier ones took (before the
/// first, by the previous song's pace, or on the first song an ease toward most of one window)
/// and held short of the next report. Written from the run's thread, read on the UI tick.
/// </summary>
internal sealed class SongProgressMeter
{
    /// <summary>Whisper's input window.</summary>
    private const double WindowSeconds = 30;
    /// <summary>Pace of the ease before Whisper's first report (Medium on a desktop CPU takes several seconds a window).</summary>
    internal const double FirstWindowSeconds = 6;
    /// <summary>The creep never covers more than this share of the gap to the next expected report.</summary>
    private const double CreepCap = 0.9;

    private readonly object _gate = new();
    private readonly double _windowStep;
    private readonly double? _seedSecondsPerWindow;
    private double? _secondsPerWindow;
    private LyricsStudioStage _stage = LyricsStudioStage.FindingLyrics;
    private double _reported;
    private TimeSpan _reportedAt;
    private TimeSpan _stageStartedAt;
    private double _shown;

    /// <param name="songLength">The track's length (zero when unknown); sets how much of Listening one window is.</param>
    /// <param name="secondsPerWindow">How long a window took on the previous song of the run, if any: paces this song's first window.</param>
    public SongProgressMeter(TimeSpan songLength, TimeSpan now = default, double? secondsPerWindow = null)
    {
        var seconds = Math.Min(songLength.TotalSeconds, PcmDecoder16k.MaxSeconds);
        _windowStep = seconds > 0 ? Math.Clamp(WindowSeconds / seconds, 0.02, 1) : 0.15;
        _reportedAt = _stageStartedAt = now;
        _seedSecondsPerWindow = secondsPerWindow is > 0 ? secondsPerWindow : null;
    }

    /// <summary>Seconds Whisper took per 30 s window on this song (null until its first report); seeds the next song.</summary>
    public double? SecondsPerWindow
    {
        get { lock (_gate) return _secondsPerWindow; }
    }

    public void Report(LyricsStudioStage stage, double stageFraction, TimeSpan now)
    {
        lock (_gate)
        {
            if (stage < _stage) return; // a late report from a step already left
            if (stage > _stage)
            {
                _stage = stage;
                _reported = 0;
                _stageStartedAt = _reportedAt = now;
            }
            if (stageFraction > _reported)
            {
                _reported = Math.Min(1, stageFraction);
                _reportedAt = now;
                var elapsed = (now - _stageStartedAt).TotalSeconds;
                if (_stage == LyricsStudioStage.Listening && _reported > 0.001 && elapsed > 0)
                    _secondsPerWindow = elapsed / (_reported / _windowStep);
            }
        }
    }

    /// <summary>The step and the whole song's progress to show now: never lower than the last reading, and below 100% until Done.</summary>
    public (LyricsStudioStage Stage, double Overall) Read(TimeSpan now)
    {
        lock (_gate)
        {
            var fraction = Math.Min(1, _reported + Creep(now));
            var overall = LyricsStudioStages.Overall(_stage, fraction);
            if (_stage != LyricsStudioStage.Done) overall = Math.Min(overall, 0.995);
            _shown = Math.Max(_shown, overall);
            return (_stage, _shown);
        }
    }

    private double Creep(TimeSpan now)
    {
        var since = (now - _reportedAt).TotalSeconds;
        if (since <= 0) return 0;
        switch (_stage)
        {
            case LyricsStudioStage.Listening:
                var step = Math.Min(_windowStep, 1 - _reported);
                // Seconds per window so far (the current window is assumed to take as long), or
                // the previous song's; with neither, ease toward most of the window.
                if ((_secondsPerWindow ?? _seedSecondsPerWindow) is { } perWindow)
                    return step * Math.Min(CreepCap, since / perWindow);
                return step * CreepCap * (1 - Math.Exp(-since / FirstWindowSeconds));
            case LyricsStudioStage.FindingLyrics or LyricsStudioStage.Aligning:
                // No reports inside these (an online lookup, the aligner): ease most of the way.
                return (1 - _reported) * 0.8 * (1 - Math.Exp(-since / 1.5));
            default:
                return 0; // Decoding reports every half percent itself
        }
    }
}
