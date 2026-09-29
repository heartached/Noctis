using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Helpers;
using Noctis.Services.Waveform;

namespace Noctis.ViewModels;

/// <summary>
/// Waveform seek bar (GitHub #93): the island and mini player seek bars draw the current
/// track's waveform when Settings → Player → Waveform seek bar is on. This part only
/// feeds the <see cref="WaveformService"/> its plan (current + next queued tracks) and
/// publishes the current track's result; the views bind <see cref="CurrentWaveform"/>.
/// </summary>
public partial class PlayerViewModel
{
    /// <summary>Settings → Player → Waveform seek bar (off by default).</summary>
    [ObservableProperty] private bool _waveformSeekBarEnabled;

    /// <summary>The current track's waveform, or null while it is not ready (the seek bars
    /// then draw their plain line) — never another track's.</summary>
    [ObservableProperty] private WaveformData? _currentWaveform;

    private WaveformService? _waveforms;
    private string? _currentWaveformPath;
    // The current track's waveform as delivered, kept while music video audio hides it.
    private WaveformData? _currentWaveformData;

    /// <summary>Attaches the background waveform service (the app wires it once at startup;
    /// without it the feature stays inert).</summary>
    public void SetWaveformService(WaveformService service)
    {
        if (_waveforms != null) return;
        _waveforms = service;
        service.WaveformReady += OnWaveformReady;
        UpNext.CollectionChanged += (_, _) => UpdateWaveformPlan();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CurrentTrack)) UpdateWaveformPlan();
        };
        UpdateWaveformPlan();
    }

    partial void OnWaveformSeekBarEnabledChanged(bool value) => UpdateWaveformPlan();

    private void UpdateWaveformPlan()
    {
        var current = CurrentTrack;
        if (_currentWaveformPath != null &&
            (current == null || !string.Equals(current.FilePath, _currentWaveformPath, PathComparison.Comparison)))
        {
            // Track changed: the old waveform must not linger under the new title. A cached
            // one for the new track comes straight back from the service's memory tier.
            _currentWaveformPath = null;
            _currentWaveformData = null;
            CurrentWaveform = null;
        }

        if (_waveforms == null) return;
        if (!WaveformSeekBarEnabled)
        {
            _currentWaveformPath = null;
            _currentWaveformData = null;
            CurrentWaveform = null;
            _waveforms.SetPlan(Array.Empty<string>());
            return;
        }
        _waveforms.SetPlan(WaveformPlanner.Plan(current, UpNext));
    }

    private void OnWaveformReady(string path, WaveformData data)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!WaveformSeekBarEnabled) return;
            var current = CurrentTrack;
            if (current == null || !string.Equals(current.FilePath, path, PathComparison.Comparison)) return;
            if (ReferenceEquals(CurrentWaveform, data)) return;
            _currentWaveformPath = path;
            _currentWaveformData = data;
            CurrentWaveform = IsPlayingMusicVideoAudio ? null : data;
        });
    }

    /// <summary>Music video audio: the song file's waveform is not what is heard, so the seek
    /// bars draw their plain line while the clip's audio plays, and get it back if the engine
    /// falls back to the song file.</summary>
    private void UpdateWaveformForMusicVideoAudio() =>
        CurrentWaveform = IsPlayingMusicVideoAudio ? null : _currentWaveformData;
}
