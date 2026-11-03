using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// Settings → Audio: Mono Audio and Balance (applied in the output's sample stage, see
/// ChannelMixProvider), and Settings → Player: the sleep timer's Fade Out.
/// </summary>
public partial class SettingsViewModel
{
    // ── Mono Audio / Balance ──

    /// <summary>The platforms whose output runs the sample stage (the gapless engine).</summary>
    public bool IsChannelMixSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    [ObservableProperty] private bool _monoAudio;

    /// <summary>-100 (left) … 0 (centre) … +100 (right).</summary>
    [ObservableProperty] private double _audioBalance;

    /// <summary>Within this distance of the centre the slider lands on the centre.</summary>
    internal const double BalanceSnapToCentre = 5;

    public string AudioBalanceLabel
    {
        get
        {
            var b = (int)Math.Round(AudioBalance);
            if (b == 0) return Loc.T("Settings.BalanceCenter");
            return string.Format(Loc.T(b < 0 ? "Settings.BalanceLeftFormat" : "Settings.BalanceRightFormat"), Math.Abs(b));
        }
    }

    /// <summary>Why Mono Audio and Balance are off right now; null when they apply.</summary>
    public string? ChannelMixNote =>
        ExclusiveAudioEnabled ? Loc.T("Settings.ChannelMixExclusiveNote")
        : _audioPlayer is { ChannelMixAvailable: false } ? Loc.T("Settings.ChannelMixUnavailableNote")
        : null;

    public bool IsChannelMixEnabled => ChannelMixNote == null;

    /// <summary>Re-reads the note after the output path changed (Exclusive Mode, a rebuild).</summary>
    private void RefreshChannelMixAvailability()
    {
        OnPropertyChanged(nameof(ChannelMixNote));
        OnPropertyChanged(nameof(IsChannelMixEnabled));
    }

    partial void OnMonoAudioChanged(bool value)
    {
        ApplyChannelMix();
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnAudioBalanceChanged(double value)
    {
        var snapped = SnapBalance(value);
        if (snapped != value)
        {
            AudioBalance = snapped;
            return;
        }
        OnPropertyChanged(nameof(AudioBalanceLabel));
        ApplyChannelMix();
        // Slider-driven: coalesced like the other sliders.
        if (_settingsLoaded) QueueSettingsSave();
    }

    /// <summary>Clamps to ±100 and lands near-centre values on the centre.</summary>
    internal static double SnapBalance(double value)
    {
        if (!double.IsFinite(value)) return 0;
        var v = Math.Clamp(Math.Round(value), -100, 100);
        return Math.Abs(v) < BalanceSnapToCentre ? 0 : v;
    }

    private void ApplyChannelMix() => _audioPlayer?.SetChannelMix(MonoAudio, AudioBalance / 100.0);

    // ── Sleep timer Fade Out (Player section) ──

    [ObservableProperty] private int _sleepTimerFadeSeconds = 30;

    public bool IsSleepFadeOff { get => SleepTimerFadeSeconds == 0; set { if (value) SleepTimerFadeSeconds = 0; } }
    public bool IsSleepFade10 { get => SleepTimerFadeSeconds == 10; set { if (value) SleepTimerFadeSeconds = 10; } }
    public bool IsSleepFade30 { get => SleepTimerFadeSeconds == 30; set { if (value) SleepTimerFadeSeconds = 30; } }
    public bool IsSleepFade60 { get => SleepTimerFadeSeconds == 60; set { if (value) SleepTimerFadeSeconds = 60; } }

    partial void OnSleepTimerFadeSecondsChanged(int value)
    {
        var snapped = AppSettings.SnapSleepTimerFadeSeconds(value);
        if (snapped != value)
        {
            SleepTimerFadeSeconds = snapped;
            return;
        }
        OnPropertyChanged(nameof(IsSleepFadeOff));
        OnPropertyChanged(nameof(IsSleepFade10));
        OnPropertyChanged(nameof(IsSleepFade30));
        OnPropertyChanged(nameof(IsSleepFade60));
        if (_player != null) _player.SleepTimerFadeSeconds = value;
        if (_settingsLoaded) _ = SaveAsync();
    }

    // ── Load / save (from LoadFeatureSettings / SaveFeatureSettings) ──

    private void LoadChannelMixSettings()
    {
        MonoAudio = _settings.MonoAudio;
        AudioBalance = SnapBalance(_settings.AudioBalance);
        SleepTimerFadeSeconds = AppSettings.SnapSleepTimerFadeSeconds(_settings.SleepTimerFadeSeconds);
    }

    private void SaveChannelMixSettings()
    {
        _settings.MonoAudio = MonoAudio;
        _settings.AudioBalance = (int)SnapBalance(AudioBalance);
        _settings.SleepTimerFadeSeconds = AppSettings.SnapSleepTimerFadeSeconds(SleepTimerFadeSeconds);
    }
}
