using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Localization;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// The sleep timer (Discord #mobile-beta, Duckyvictus), the desktop's (PlayerViewModel's Sleep
/// timer): 15/30/45/60 minutes or the end of the current song, then playback pauses; the last
/// <see cref="SleepTimerFadeSeconds"/> fade out first (the desktop's quadratic curve and default).
///
/// Android: a UI DispatcherTimer stops whenever the app draws nothing, screen off included
/// (Media3AudioPlayer's position poll moved to a Handler for exactly that, device run 2026-09-22),
/// so nothing here waits on one. The deadline is a wall-clock time; it is checked by
/// <see cref="ScheduleSleepTick"/> (a thread-pool timer, posted to the UI thread through the same
/// marshal the player's events use, which the notification's Next proves works in the
/// background) and by every player position tick (the Handler's, 4 Hz while playing), which also
/// drives the fade. A deadline that passes while paused (or the CPU asleep) only ends the timer.
/// </summary>
public sealed partial class NowPlayingViewModel
{
    /// <summary>The desktop's Fade Out default (AppSettings.SleepTimerFadeSeconds); 0 = no fade.</summary>
    public int SleepTimerFadeSeconds { get; set; } = 30;

    /// <summary>Wall clock for the sleep timer; tests replace it.</summary>
    internal Func<DateTime> SleepClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Starts the timer's one-second check (<paramref name="tick"/> on the UI thread)
    /// and returns what stops it. Tests replace it to tick by hand.</summary>
    internal Func<Action, IDisposable> ScheduleSleepTick { get; set; }

    private DateTime _sleepEndsAtUtc;
    private IDisposable? _sleepTicker;
    // End of Current Song: the song it was set on; a song started otherwise ends it.
    private Track? _stopAfterTrack;
    private double _appliedFadeGain = 1.0;

    /// <summary>A minutes timer is counting down.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSleepTimer), nameof(SleepTimerStatus))]
    private bool _isSleepTimerActive;

    /// <summary>The running timer's length (15/30/45/60) for the menu's tick; 0 when none.</summary>
    [ObservableProperty] private int _sleepTimerMinutes;

    /// <summary>End of Current Song is set: playback stops when this song ends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSleepTimer), nameof(SleepTimerStatus))]
    private bool _stopAfterCurrentSong;

    /// <summary>What is left: "29:54" of the timer, or of the song for End of Current Song.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SleepTimerStatus))]
    private string _sleepTimerRemainingText = string.Empty;

    /// <summary>Either kind of timer is set (Now Playing shows its pill).</summary>
    public bool HasSleepTimer => IsSleepTimerActive || StopAfterCurrentSong;

    /// <summary>"Pauses in 29:54" / "Pauses after this song": the menu's line under Sleep Timer
    /// and what a screen reader says for Now Playing's timer pill. Empty when no timer is set.</summary>
    public string SleepTimerStatus =>
        IsSleepTimerActive ? Loc.T("Mobile.SleepTimer.Left", SleepTimerRemainingText)
        : StopAfterCurrentSong ? Loc.T("Mobile.SleepTimer.AfterSong")
        : string.Empty;

    private IDisposable DefaultSleepTick(Action tick)
    {
        var timer = new Timer(_ => _marshal(tick), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return timer;
    }

    /// <summary>Starts (or restarts) a minutes timer; End of Current Song gives way to it.</summary>
    [RelayCommand]
    public void StartSleepTimer(int minutes)
    {
        if (minutes <= 0) return;
        var hadStopAfter = StopAfterCurrentSong;
        ClearStopAfter();
        _sleepEndsAtUtc = SleepClock().AddMinutes(minutes);
        SleepTimerMinutes = minutes;
        IsSleepTimerActive = true;
        _sleepTicker ??= ScheduleSleepTick(SleepTick);
        UpdateSleepRemaining();
        UpdateSleepFade();   // a restart mid-fade lifts the level straight back
        if (hadStopAfter) PrepareUpcoming();   // the next song may follow gaplessly again
        DebugLog.Write("Audio", $"Sleep timer: {minutes} min");
    }

    /// <summary>End of Current Song: the queue does not go on after the playing song.</summary>
    [RelayCommand]
    public void StopAfterThisSong()
    {
        if (CurrentTrack == null) return;
        EndMinutesTimer();
        _stopAfterTrack = CurrentTrack;
        StopAfterCurrentSong = true;
        _sleepTicker ??= ScheduleSleepTick(SleepTick);
        // Media3 must not chain the next song gaplessly behind this one: its end has to reach us.
        PrepareUpcoming();
        UpdateSleepRemaining();
        UpdateSleepFade();   // from a minutes timer mid-fade: the song's own end decides now
        DebugLog.Write("Audio", "Sleep timer: end of current song");
    }

    /// <summary>Turns the timer off; a fade under way comes straight back up.</summary>
    [RelayCommand]
    public void CancelSleepTimer()
    {
        var hadStopAfter = StopAfterCurrentSong;
        EndMinutesTimer();
        ClearStopAfter();
        StopSleepTicker();
        UpdateSleepFade();
        if (hadStopAfter) PrepareUpcoming();
    }

    private void EndMinutesTimer()
    {
        IsSleepTimerActive = false;
        SleepTimerMinutes = 0;
        if (!StopAfterCurrentSong) SleepTimerRemainingText = string.Empty;
    }

    private void ClearStopAfter()
    {
        _stopAfterTrack = null;
        StopAfterCurrentSong = false;
        if (!IsSleepTimerActive) SleepTimerRemainingText = string.Empty;
    }

    private void StopSleepTicker()
    {
        _sleepTicker?.Dispose();
        _sleepTicker = null;
    }

    /// <summary>The one-second check (and the position ticks'): fires a due timer, else
    /// refreshes the label.</summary>
    private void SleepTick()
    {
        if (_disposed) return;
        if (IsSleepTimerActive && SleepClock() >= _sleepEndsAtUtc)
        {
            FireSleepTimer();
            return;
        }
        if (!HasSleepTimer)
        {
            StopSleepTicker();
            return;
        }
        UpdateSleepRemaining();
    }

    /// <summary>The minutes timer ran out: pause if playing (the fade has reached silence by now).</summary>
    private void FireSleepTimer()
    {
        var playing = _player.State == PlaybackState.Playing;
        EndMinutesTimer();
        if (!StopAfterCurrentSong) StopSleepTicker();
        if (playing)
        {
            _player.Pause();
            IsPlaying = false;
            SaveStateNow();
            // The fade has the level at silence: it stays there until playback starts again,
            // when the player puts it back itself (Media3AudioPlayer.ResetFade on Play/Resume),
            // so the pause cannot let a blip through at full level (the desktop's hold).
            _appliedFadeGain = 1.0;
        }
        else UpdateSleepFade();
        DebugLog.Write("Audio", playing ? "Sleep timer: paused playback" : "Sleep timer: ended, nothing playing");
    }

    /// <summary>
    /// A natural end of the song End of Current Song was set on: playback stops there, the queue
    /// moved on to the next song (paused at its start, so Play goes on from it). True when it
    /// handled the end.
    /// </summary>
    private bool TryStopAfterSong()
    {
        if (!StopAfterCurrentSong || !ReferenceEquals(_stopAfterTrack, CurrentTrack)) return false;
        ClearStopAfter();
        if (!IsSleepTimerActive) StopSleepTicker();
        var next = AdvancePlayable(QueueAdvance.Natural, out _);
        FlushListened();
        _player.Stop();
        IsPlaying = false;
        Position = TimeSpan.Zero;
        if (next != null)
        {
            CurrentTrack = next;
            Duration = next.Duration;
            ErrorText = string.Empty;
        }
        SyncUpNext();
        SaveStateNow();
        UpdateSleepFade();
        DebugLog.Write("Audio", "Sleep timer: stopped after the song");
        return true;
    }

    /// <summary>A song was started: End of Current Song belonged to another one, so it ends.</summary>
    private void OnSleepTrackStarted(Track track)
    {
        if (StopAfterCurrentSong && !ReferenceEquals(track, _stopAfterTrack))
        {
            ClearStopAfter();
            if (!IsSleepTimerActive) StopSleepTicker();
        }
    }

    /// <summary>Each position tick: a due timer fires on the 4 Hz clock (not a second late),
    /// the fade follows, the label moves.</summary>
    private void OnSleepPositionTick()
    {
        if (!HasSleepTimer && _appliedFadeGain == 1.0) return;
        if (IsSleepTimerActive && SleepClock() >= _sleepEndsAtUtc)
        {
            FireSleepTimer();
            return;
        }
        UpdateSleepFade();
        if (HasSleepTimer) UpdateSleepRemaining();
    }

    /// <summary>The desktop's fade curve (PlayerViewModel.SleepFadeGain): x² of the share of
    /// the fade left, 1 before it, 0 at the end, 1 throughout with the fade off.</summary>
    internal static double SleepFadeGain(double remainingSeconds, int fadeSeconds)
    {
        if (fadeSeconds <= 0 || double.IsNaN(remainingSeconds)) return 1.0;
        var x = Math.Clamp(remainingSeconds / fadeSeconds, 0.0, 1.0);
        return x * x;
    }

    /// <summary>The fade gain the timer calls for now.</summary>
    internal double DesiredSleepFadeGain()
    {
        var fade = SleepTimerFadeSeconds;
        var gain = 1.0;
        if (IsSleepTimerActive)
            gain = Math.Min(gain, SleepFadeGain((_sleepEndsAtUtc - SleepClock()).TotalSeconds, fade));
        if (StopAfterCurrentSong && Duration > TimeSpan.Zero)
            gain = Math.Min(gain, SleepFadeGain((Duration - Position).TotalSeconds, fade));
        return gain;
    }

    private void UpdateSleepFade()
    {
        var gain = DesiredSleepFadeGain();
        if (gain == _appliedFadeGain || (Math.Abs(gain - _appliedFadeGain) < 0.0005 && gain is > 0.0 and < 1.0)) return;
        _appliedFadeGain = gain;
        _player.SetPlaybackFadeGain(gain, 250);
    }

    private void UpdateSleepRemaining()
    {
        TimeSpan remaining;
        if (IsSleepTimerActive) remaining = _sleepEndsAtUtc - SleepClock();
        else if (StopAfterCurrentSong) remaining = Duration > Position ? Duration - Position : TimeSpan.Zero;
        else
        {
            SleepTimerRemainingText = string.Empty;
            return;
        }
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        // Rounded up, so a fresh 15-minute timer reads 15:00 and the last second reads 0:01.
        remaining = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        SleepTimerRemainingText = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes}:{remaining.Seconds:00}";
    }
}
