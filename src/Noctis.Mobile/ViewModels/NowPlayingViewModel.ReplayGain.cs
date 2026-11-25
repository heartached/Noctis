using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Settings › Listening › ReplayGain: Off, Track or Album, the pre-amp and Prevent Clipping. They live
/// here, beside the player they drive, so they reach it at start without Settings ever opening
/// (ShellViewModel.InitializeAsync loads them before the queue restores). Saved under the desktop's
/// names (ReplayGainMode, ReplayGainPreampDb) plus ReplayGainPreventClipping; every change reaches the
/// player at once (it glides the playing song to its new level), and a pre-amp drag is written once
/// it settles.
/// </summary>
public sealed partial class NowPlayingViewModel
{
    [ObservableProperty] private ReplayGainMode _replayGainMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReplayGainPreampLabel))]
    private double _replayGainPreampDb;

    [ObservableProperty] private bool _replayGainPreventClipping = true;

    /// <summary>The pre-amp as the desktop's Settings writes it: "+2.5 dB", "0.0 dB".</summary>
    public string ReplayGainPreampLabel => ReplayGainPreampDb.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " dB";

    private bool _replayGainLoaded, _applyingReplayGain;
    // Changed before the saved values were read (Settings opened during a cold start): kept.
    private readonly HashSet<string> _replayGainSetBeforeLoad = new();
    private Task _replayGainSaveChain = Task.CompletedTask;
    private int _preampSaveGeneration;

    /// <summary>The tail of the ReplayGain saves; tests await it.</summary>
    internal Task PendingReplayGainSave { get; private set; } = Task.CompletedTask;

    partial void OnReplayGainModeChanged(ReplayGainMode value) => ReplayGainChanged(nameof(ReplayGainMode));
    partial void OnReplayGainPreventClippingChanged(bool value) => ReplayGainChanged(nameof(ReplayGainPreventClipping));

    partial void OnReplayGainPreampDbChanged(double value)
    {
        var clamped = double.IsFinite(value) ? Math.Clamp(value, ReplayGainLevel.MinPreampDb, ReplayGainLevel.MaxPreampDb) : 0;
        if (clamped != value)
        {
            ReplayGainPreampDb = clamped;
            return;
        }
        ReplayGainChanged(nameof(ReplayGainPreampDb), slider: true);
    }

    /// <summary>Reads the saved choices and hands them to the player. Cold start, before the queue restores.</summary>
    public async Task LoadReplayGainAsync(AppSettings? saved = null)
    {
        try
        {
            var settings = saved ?? await _persistence.LoadSettingsAsync();
            _applyingReplayGain = true;
            try
            {
                if (!_replayGainSetBeforeLoad.Contains(nameof(ReplayGainMode))) ReplayGainMode = ReplayGainLevel.ParseMode(settings.ReplayGainMode);
                if (!_replayGainSetBeforeLoad.Contains(nameof(ReplayGainPreampDb)))
                    ReplayGainPreampDb = double.IsFinite(settings.ReplayGainPreampDb)
                        ? Math.Clamp(settings.ReplayGainPreampDb, ReplayGainLevel.MinPreampDb, ReplayGainLevel.MaxPreampDb) : 0;
                if (!_replayGainSetBeforeLoad.Contains(nameof(ReplayGainPreventClipping))) ReplayGainPreventClipping = settings.ReplayGainPreventClipping;
            }
            finally
            {
                _applyingReplayGain = false;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("Settings", $"ReplayGain settings load failed: {ex.Message}");
        }
        _replayGainLoaded = true;
        ApplyReplayGainToPlayer();
        if (_replayGainSetBeforeLoad.Count > 0) PendingReplayGainSave = SaveReplayGain();
    }

    private void ReplayGainChanged(string setting, bool slider = false)
    {
        if (_applyingReplayGain) return;
        ApplyReplayGainToPlayer();
        if (!_replayGainLoaded)
        {
            _replayGainSetBeforeLoad.Add(setting);
            return;
        }
        if (!slider)
        {
            PendingReplayGainSave = SaveReplayGain();
            return;
        }
        var generation = ++_preampSaveGeneration;
        PendingReplayGainSave = SaveAfterPreampSettlesAsync(generation);
    }

    private void ApplyReplayGainToPlayer() =>
        _player.ApplyReplayGain(ReplayGainLevel.ModeName(ReplayGainMode), ReplayGainPreampDb, ReplayGainPreventClipping);

    private async Task SaveAfterPreampSettlesAsync(int generation)
    {
        await Task.Delay(LyricsPageViewModel.SliderSaveDelay);
        if (generation != Volatile.Read(ref _preampSaveGeneration)) return;   // a later step saves
        await SaveReplayGain();
    }

    /// <summary>One save after another, so a quick change never lands out of order.</summary>
    private Task SaveReplayGain()
    {
        var mode = ReplayGainLevel.ModeName(ReplayGainMode);
        var preamp = ReplayGainPreampDb;
        var clipping = ReplayGainPreventClipping;
        return _replayGainSaveChain = _replayGainSaveChain.ContinueWith(async _ =>
        {
            try
            {
                await _persistence.UpdateSettingsAsync(settings =>
                {
                    settings.ReplayGainMode = mode;
                    settings.ReplayGainPreampDb = preamp;
                    settings.ReplayGainPreventClipping = clipping;
                });
            }
            catch (Exception ex)
            {
                DebugLog.Write("Settings", $"ReplayGain settings save failed: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
    }
}
