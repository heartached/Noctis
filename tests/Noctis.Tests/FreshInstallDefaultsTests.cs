using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Locks the out-of-the-box settings a new install starts with. These were chosen
/// deliberately (2026-07-25), so a change here should be a decision, not a drive-by.
///
/// Existing users are unaffected by any of this: PersistenceService serializes every
/// property (it only sets WhenWritingNull, not WhenWritingDefault), so a stored
/// settings.json always wins over the values below.
/// </summary>
public class FreshInstallDefaultsTests
{
    private static readonly AppSettings Fresh = new();

    [Fact]
    public void NothingIsOnUnlessItDoesSomething()
    {
        // Scrobbling is gated on an authenticated account; both connect paths switch
        // their own toggle on. Shipping them enabled only made Settings look active.
        Assert.False(Fresh.LastFmScrobblingEnabled);
        Assert.False(Fresh.ListenBrainzScrobblingEnabled);

        // ReplayGain does nothing until files carry REPLAYGAIN_* tags.
        Assert.Equal("Off", Fresh.ReplayGainMode);
    }

    [Fact]
    public void TheSignalPathStartsClean()
    {
        // Every one of these counts as DSP in PlayerViewModel.RefreshSignalPath. If any
        // ships on, a fresh install can never show "Lossless" or "Bit-perfect".
        Assert.False(Fresh.SoundCheckEnabled);
        Assert.False(Fresh.CrossfadeEnabled);
        Assert.False(Fresh.SongTransitionsEnabled);
        Assert.Equal("Off", Fresh.ReplayGainMode);

        // The equalizer is the exception: it ships on, but parked on a flat curve that
        // the player bypasses, so presets apply the moment they're picked without the
        // toggle counting as DSP. See SignalPathEqualizerTests.
        Assert.True(Fresh.EqualizerEnabled);
    }

    [Fact]
    public void FirstRunDoesNoWorkTheUserDidNotAskFor()
    {
        // Real CPU across the whole library on the first scan; its consumers
        // (AutoMix beat-matching, Track Radio) are off by default anyway.
        Assert.False(Fresh.BpmKeyAnalysisEnabled);
        Assert.False(Fresh.WriteAnalysisToTags);

        // Finding the user's music, though, is the whole point.
        Assert.True(Fresh.ScanOnStartup);
        Assert.True(Fresh.WatchFoldersEnabled);
    }

    [Fact]
    public void WindowAndStartupBehaviourStaysOutOfTheWay()
    {
        Assert.False(Fresh.MinimizeToTray);
        Assert.False(Fresh.CloseToTray);
        Assert.False(Fresh.StartMinimizedToTray);
        Assert.False(Fresh.SidebarHoverExpand);
        Assert.False(Fresh.WebRemoteEnabled);
        Assert.False(Fresh.DiscordRichPresenceEnabled);
        Assert.False(Fresh.DeveloperMode);
        Assert.False(Fresh.IncludePrereleaseUpdates);
        Assert.False(Fresh.CollapseAlbumEditions);
        Assert.False(Fresh.ExclusiveAudioEnabled);

        // Reopens with the last track in the playbar, paused — it never auto-plays.
        Assert.True(Fresh.RestoreLastTrackOnStartup);
    }

    [Fact]
    public void LookAndFeelMatchesTheShippedDesign()
    {
        Assert.Equal("Gray", Fresh.Theme);
        Assert.Equal("Crimson", Fresh.AccentPresetName);
        Assert.Equal("#E74856", Fresh.AccentColorHex);
        Assert.True(Fresh.EnableAnimatedCovers);
        Assert.True(Fresh.LyricsFlowingLightEnabled);
        Assert.Equal(0.4, Fresh.PlaybackBarBackgroundOpacity);
        Assert.True(Fresh.GaplessPlaybackEnabled);
    }

    [Fact]
    public void EveryTextAnimationIsOn()
    {
        Assert.True(Fresh.TrackTitleMarqueeEnabled);
        Assert.True(Fresh.ArtistMarqueeEnabled);
        Assert.True(Fresh.CoverFlowMarqueeEnabled);
        Assert.True(Fresh.CoverFlowArtistMarqueeEnabled);
        Assert.True(Fresh.CoverFlowAlbumMarqueeEnabled);
        Assert.True(Fresh.LyricsTitleMarqueeEnabled);
        Assert.True(Fresh.LyricsArtistMarqueeEnabled);
        Assert.True(Fresh.MiniPlayerTitleMarqueeEnabled);
        Assert.True(Fresh.MiniPlayerAlbumMarqueeEnabled);
    }

    [Fact]
    public void EveryLyricsAndMetadataProviderIsOn()
    {
        Assert.True(Fresh.LrcLibEnabled);
        Assert.True(Fresh.NetEaseEnabled);
        Assert.True(Fresh.DeezerEnabled);
        Assert.True(Fresh.MusicBrainzEnabled);
    }
}
