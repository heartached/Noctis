using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Models;
using Noctis.Services;

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

    // ── Album ⋯ › Choose / Remove Animated Cover (Discord #mobile-beta 11-27) ──

    /// <summary>The loops chosen on the phone; null where there is no store or no picker.</summary>
    internal ChosenAnimatedCovers? ChosenCovers => PlaylistFiles != null ? AnimatedCoverSource?.Chosen : null;

    /// <summary>A video from the phone's picker becomes the album's animated cover (copied into the
    /// app, so it plays offline). Says what happened: set (and where it shows, or that the setting
    /// is Off), not a video, too big, or not saved.</summary>
    public async Task ChooseAnimatedCoverAsync(Album album)
    {
        if (PlaylistFiles is not { } picker || ChosenCovers is not { } chosen) return;
        PickedVideoFile? video;
        try
        {
            video = await picker.PickVideoAsync();
        }
        catch (Exception ex)
        {
            DebugLog.Write("AnimatedCover", $"Picking a video failed: {ex.GetType().Name}");
            return;
        }
        if (video is null) return;
        var result = await Task.Run(async () =>
        {
            try
            {
                await using var stream = video.Open();
                return await chosen.SaveAsync(album.Id, stream).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DebugLog.Write("AnimatedCover", $"Reading the picked video failed: {ex.GetType().Name}");
                return ChosenCoverResult.Failed;
            }
        });
        Player.Notice = result switch
        {
            ChosenCoverResult.Saved when AnimatedCovers == AnimatedCoversSetting.Off => Loc.T("Mobile.AnimatedCoverSetButOff"),
            ChosenCoverResult.Saved => Loc.T("Mobile.AnimatedCoverSet"),
            ChosenCoverResult.NotVideo => Loc.T("Mobile.AnimatedCoverNotVideo"),
            ChosenCoverResult.TooBig => Loc.T("Mobile.AnimatedCoverTooBig"),
            _ => Loc.T("Mobile.AnimatedCoverFailed"),
        };
        if (result == ChosenCoverResult.Saved) _motion?.Reload();
    }

    /// <summary>The album's chosen loop goes; a cover.mp4 beside its music, or the computer's,
    /// shows again.</summary>
    public void RemoveAnimatedCover(Album album)
    {
        if (ChosenCovers is not { } chosen) return;
        chosen.Remove(album.Id);
        _motion?.Reload();
    }
}
