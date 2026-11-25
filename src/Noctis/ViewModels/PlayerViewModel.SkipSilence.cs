using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.AudioAnalysis;

namespace Noctis.ViewModels;

/// <summary>
/// Skip Silence (Discord, Spark 11-23: "many of my songs have unnecessary blank starts and
/// ends ... detect the silence of the beginning and end to make the transitions a lot
/// smoother", with an adjustable level). A song starts at its first sound and ends after its
/// last one. The silence is measured in the background (<see cref="SilenceScanService"/>);
/// files are never changed, and a start or stop time set by hand in Edit Info always wins.
/// </summary>
public partial class PlayerViewModel
{
    [ObservableProperty] private bool _skipSilenceEnabled;

    /// <summary>Anything quieter than this (dBFS peak) counts as silence.</summary>
    [ObservableProperty] private double _skipSilenceThresholdDb = AppSettings.SkipSilenceThresholdDefault;

    /// <summary>Set once at start-up (MainWindowViewModel); null = the feature can't run.</summary>
    internal SilenceScanService? SilenceScanner { get; set; }

    // Results per song for this session, read on the UI thread (no disk access there).
    private readonly Dictionary<Guid, SilenceBounds> _silenceByTrack = new();
    private const int SilenceMemoLimit = 20000;
    // Bumped per song start; a scan that finishes late only acts on the start it was made for.
    private int _silencePlayGeneration;
    // The start in which something seeked: a late jump never overrides the listener.
    private int _silenceSeekGeneration = -1;
    private Guid _silenceNextScanId;

    /// <summary>Whether Skip Silence looks at <paramref name="track"/> at all: a local song
    /// file (not a stream), and not an audiobook-style song that remembers its position.</summary>
    private bool SilenceApplies(Track? track) =>
        SkipSilenceEnabled && SilenceScanner != null && track != null &&
        !track.RememberPlaybackPosition && SilenceScanService.CanScan(track);

    private SilenceBounds? KnownSilence(Track track) =>
        SilenceApplies(track) && _silenceByTrack.TryGetValue(track.Id, out var b) ? b : null;

    /// <summary>Where <paramref name="track"/> should start to skip its leading silence, in ms;
    /// 0 when unknown, none, or a start time set by hand.</summary>
    internal long SilenceStartMs(Track track) =>
        track.StartTimeMs > 0 ? 0 : (long)(KnownSilence(track)?.Leading.TotalMilliseconds ?? 0);

    /// <summary>Where the playing song ends: the stop time set by hand, else (Skip Silence)
    /// its length minus the trailing silence; zero = it plays to the end. Not while a music
    /// video's audio plays: the silence was measured on the song file, not the clip.</summary>
    internal TimeSpan EffectiveStopTime(Track track, TimeSpan duration)
    {
        if (track.StopTimeMs > 0) return TimeSpan.FromMilliseconds(track.StopTimeMs);
        if (_musicVideoAudioPath != null || duration <= TimeSpan.Zero) return TimeSpan.Zero;
        var trailing = KnownSilence(track)?.Trailing ?? TimeSpan.Zero;
        if (trailing <= TimeSpan.Zero) return TimeSpan.Zero;
        var stop = duration - trailing;
        return stop > TimeSpan.FromSeconds(1) ? stop : TimeSpan.Zero;
    }

    /// <summary>The planner's view of a song's measured silence (Song Transitions): null when
    /// Skip Silence doesn't know it.</summary>
    private AudioSilenceProfile? DetectedSilenceProfile(Track track) =>
        KnownSilence(track) is { } b ? new AudioSilenceProfile(b.Leading, b.Trailing, false) : null;

    /// <summary>Called once per song start, after the player was told to play. Scans the song
    /// if it isn't known yet (when the result lands while the song is still in its leading
    /// silence, it jumps to the first sound: inaudible, it is silence) and the next song.</summary>
    private void OnSilenceTrackStarted(Track track, string seekSource)
    {
        var generation = ++_silencePlayGeneration;
        if (!SilenceApplies(track)) { EnsureNextSilenceScan(); return; }
        if (!_silenceByTrack.ContainsKey(track.Id))
        {
            var lateJump = seekSource == "none" && track.StartTimeMs <= 0 && _musicVideoAudioPath == null;
            RequestSilenceScan(track, generation, lateJump);
        }
        EnsureNextSilenceScan();
    }

    /// <summary>Scans the first song in Up Next ahead of time, so its start (gapless staging,
    /// Song Transitions) and its own end are known before it plays.</summary>
    private void EnsureNextSilenceScan()
    {
        if (!SkipSilenceEnabled || UpNext.Count == 0) return;
        var next = UpNext[0];
        if (next.Id == _silenceNextScanId) return;
        _silenceNextScanId = next.Id;
        if (SilenceApplies(next) && !_silenceByTrack.ContainsKey(next.Id))
            RequestSilenceScan(next, -1, lateJump: false);
    }

    private void RequestSilenceScan(Track track, int generation, bool lateJump)
    {
        var scanner = SilenceScanner;
        if (scanner == null) return;
        var threshold = SkipSilenceThresholdDb;
        _ = scanner.ScanAsync(track, threshold, urgent: lateJump).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            var bounds = t.Result;
            Dispatcher.UIThread.Post(() => OnSilenceScanned(track, generation, lateJump, threshold, bounds));
        }, TaskScheduler.Default);
    }

    private void OnSilenceScanned(Track track, int generation, bool lateJump, double threshold, SilenceBounds bounds)
    {
        // The level moved while this scan ran: its result belongs to the old level.
        if (!SkipSilenceEnabled || Math.Abs(threshold - SkipSilenceThresholdDb) > 0.5) return;
        if (_silenceByTrack.Count >= SilenceMemoLimit) _silenceByTrack.Clear();
        _silenceByTrack[track.Id] = bounds;

        if (lateJump) TryLateSilenceJump(track, generation, bounds, 0);
    }

    /// <summary>The song is still in its leading silence: jump to its first sound. The player
    /// drops a seek while it doesn't know the song's length yet (still opening), and a fast scan
    /// (libVLC, live 11-24) landed there: the song jumped, then went back to 0:00. So it waits
    /// for the length, a few times briefly.</summary>
    private void TryLateSilenceJump(Track track, int generation, SilenceBounds bounds, int attempt)
    {
        if (generation != _silencePlayGeneration || CurrentTrack?.Id != track.Id) return;
        if (_silenceSeekGeneration == generation || State == PlaybackState.Stopped) return;
        if (bounds.Leading <= TimeSpan.Zero || Position >= bounds.Leading - TimeSpan.FromMilliseconds(300)) return;
        if (_audioPlayer.Duration <= TimeSpan.Zero && attempt < 20)
        {
            DispatcherTimer.RunOnce(() => TryLateSilenceJump(track, generation, bounds, attempt + 1), TimeSpan.FromMilliseconds(100));
            return;
        }
        DebugLogger.Info(DebugLogger.Category.Playback, "SkipSilence.LateStart",
            $"title={track.Title}, positionMs={Position.TotalMilliseconds:F0}, leadingMs={bounds.Leading.TotalMilliseconds:F0}, waits={attempt}");
        SeekTo(bounds.Leading);
    }

    partial void OnSkipSilenceEnabledChanged(bool value)
    {
        _silenceNextScanId = Guid.Empty;
        if (value) RescanSilenceAroundNow();
    }

    partial void OnSkipSilenceThresholdDbChanged(double value)
    {
        // Measured at the old level: forget them, and measure the playing and next song again
        // once the slider rests (a drag must not queue a scan per step).
        _silenceByTrack.Clear();
        _silenceNextScanId = Guid.Empty;
        CancelAutoMixTransition("settings changed");
        _silenceRescanTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _silenceRescanTimer?.Stop();
            RescanSilenceAroundNow();
        });
        _silenceRescanTimer.Stop();
        _silenceRescanTimer.Start();
    }

    private DispatcherTimer? _silenceRescanTimer;

    private void RescanSilenceAroundNow()
    {
        if (!SkipSilenceEnabled) return;
        // No late jump: the song is already playing where the listener is.
        if (SilenceApplies(CurrentTrack) && !_silenceByTrack.ContainsKey(CurrentTrack!.Id))
            RequestSilenceScan(CurrentTrack, -1, lateJump: false);
        EnsureNextSilenceScan();
    }
}
