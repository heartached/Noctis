namespace Noctis.Models;

public enum AutoMixTransitionMode
{
    Off,
    Crossfade,
    AutoMix
}

public enum AutoMixStrength
{
    Subtle,
    Balanced,
    Extended
}

public enum AutoMixTransitionType
{
    None,
    SilenceTrim,
    SimpleCrossfade,
    BeatMatchedCrossfade,
    SafeFade
}

public enum AutoMixFadeCurve
{
    SmoothEase,
    EqualPower
}

public sealed record AudioSilenceProfile(
    TimeSpan StartSilence,
    TimeSpan EndSilence,
    bool IsEstimated);

public sealed record AutoMixPlannerOptions(
    AutoMixTransitionMode Mode,
    AutoMixStrength Strength,
    bool RemoveSilenceBetweenSongs,
    bool AvoidAutoMixForAlbums,
    bool BeatMatchWhenMetadataAvailable,
    RepeatMode RepeatMode,
    bool ShuffleEnabled,
    bool ManualTransition);

public sealed record AutoMixTransitionPlan(
    AutoMixTransitionType TransitionType,
    TimeSpan Duration,
    TimeSpan CurrentTrackStartFadePosition,
    TimeSpan NextTrackStartPosition,
    AutoMixFadeCurve FadeCurve,
    string Reason,
    bool UseSilenceTrim,
    bool UsedBpmData,
    bool UsedKeyData,
    bool MissingBpmData,
    bool MissingKeyData,
    AudioSilenceProfile CurrentSilence,
    AudioSilenceProfile NextSilence)
{
    public bool IsEnabled => TransitionType != AutoMixTransitionType.None;

    public static AutoMixTransitionPlan None(
        string reason,
        AudioSilenceProfile? currentSilence = null,
        AudioSilenceProfile? nextSilence = null) =>
        new(
            AutoMixTransitionType.None,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            AutoMixFadeCurve.SmoothEase,
            reason,
            false,
            false,
            false,
            false,
            false,
            currentSilence ?? new AudioSilenceProfile(TimeSpan.Zero, TimeSpan.Zero, true),
            nextSilence ?? new AudioSilenceProfile(TimeSpan.Zero, TimeSpan.Zero, true));
}
