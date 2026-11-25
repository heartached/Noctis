using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Mobile.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Animated covers on Now Playing (Discord beta 11-23, "no animated cover support"): the setting
/// (Settings › Appearance › Animated Covers, saved with the player bar's settings as
/// MobileAnimatedCovers, Wi-Fi Only by default) and the song's loop (<see cref="Motion"/>).
/// </summary>
public sealed partial class ShellViewModel
{
    [ObservableProperty] private AnimatedCoversSetting _animatedCovers = AnimatedCoversSetting.WifiOnly;
    partial void OnAnimatedCoversChanged(AnimatedCoversSetting value) => SavePlayerBarSettings(nameof(AnimatedCovers));

    private NowPlayingMotion? _motion;

    /// <summary>Where the loops come from (the phone's folders, the signed-in computer); null in
    /// tests unless injected, and then Now Playing keeps the still cover.</summary>
    public AnimatedCoverSource? AnimatedCoverSource { get; init; }

    /// <summary>The playing song's loop for Now Playing; null without a <see cref="AnimatedCoverSource"/>.</summary>
    public NowPlayingMotion? Motion => AnimatedCoverSource is { } source ? _motion ??= new NowPlayingMotion(this, source) : null;
}
