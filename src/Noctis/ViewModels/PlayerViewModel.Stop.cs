using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Discord (Major D ark): an optional Stop button on the island, right after Play/Pause
/// (Settings → Player → Player island buttons, off by default). Stop halts playback, keeps the
/// song and the queue loaded at the song's start so Play starts it again, and lets go of the
/// audio device: in Exclusive Mode other apps can use it again until the next Play.
/// </summary>
public partial class PlayerViewModel
{
    /// <summary>The Stop button (Settings → Player).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandStopButtonVisible))]
    private bool _islandShowStop;

    /// <summary>Show the Stop button only while Exclusive Mode is switched on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandStopButtonVisible))]
    private bool _islandStopOnlyInExclusiveMode;

    /// <summary>Settings → Audio → Exclusive Mode is on. The saved switch, not whether the
    /// device is held right now: Stop itself lets go of the device, and the button must not
    /// vanish under the pointer when it does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandStopButtonVisible))]
    private bool _exclusiveModeEnabled;

    /// <summary>The island's Stop button is on screen.</summary>
    public bool IslandStopButtonVisible =>
        IslandShowStop && (!IslandStopOnlyInExclusiveMode || ExclusiveModeEnabled);

    /// <summary>
    /// Stops playback: the engine unloads the song and closes the output (the Exclusive Mode
    /// device included), the song stays loaded at its start with the queue untouched, so
    /// Play starts it from the beginning. The same halt as the end of the queue
    /// (<see cref="StopAtQueueEnd"/>); SMTC, the tray, the taskbar and Discord follow
    /// <see cref="State"/> as they do there.
    /// </summary>
    [RelayCommand]
    private void StopPlayback()
    {
        DebugLogger.Info(DebugLogger.Category.Playback, "Stop", $"state={State}, track={CurrentTrack?.Id}");
        if (CurrentTrack == null) return;

        _seekDebounceTimer?.Dispose();
        _seekDebounceTimer = null;
        _hasPendingSeekTarget = false;
        // A restored session's resume point (or a seek made while stopped) would otherwise
        // send the next Play back there instead of to the start.
        _resumePositionMs = -1;
        CancelAutoMixTransition("user stopped");
        CancelNaturalEndFallback();
        _audioPlayer.Stop();
        State = PlaybackState.Stopped;
        Position = TimeSpan.Zero;
        PositionFraction = 0;
        PositionText = "0:00";
        RemainingTimeText = FormatTime(Duration);
        // A song that remembers its position starts over too: PlayTrack saves the outgoing
        // position (now 0) before it reads it back.
        SaveQueueStateInBackground();
    }
}
