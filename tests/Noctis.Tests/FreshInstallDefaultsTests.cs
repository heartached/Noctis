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

        // Autoplay keeps playing past the end of the queue — continuing playback
        // the user didn't queue is opt-in.
        Assert.False(Fresh.AutoplayEnabled);

        // Keep Played Songs (GitHub #124) changes what the queue shows and what the queue
        // end does — opt-in.
        Assert.False(Fresh.KeepPlayedInQueue);

        // Taskbar progress (GitHub #53) changes what the Windows taskbar shows — opt-in.
        Assert.False(Fresh.TaskbarProgressEnabled);
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
        Assert.True(Fresh.UseEmbeddedArtwork);
    }

    [Fact]
    public void WindowAndStartupBehaviourStaysOutOfTheWay()
    {
        Assert.False(Fresh.MinimizeToTray);
        Assert.False(Fresh.CloseToTray);
        Assert.False(Fresh.StartMinimizedToTray);
        Assert.False(Fresh.SidebarHoverExpand);
        Assert.False(Fresh.SidebarAlwaysExpanded);
        Assert.True(string.IsNullOrEmpty(Fresh.ExternalOpenAppPath));
        Assert.False(Fresh.WebRemoteEnabled);
        Assert.False(Fresh.DiscordRichPresenceEnabled);
        Assert.False(Fresh.DeveloperMode);
        Assert.False(Fresh.IncludePrereleaseUpdates);
        Assert.False(Fresh.AutoInstallUpdates);
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
        // Animated artwork is the only Appearance extra that ships on; liquid glass,
        // flowing lyrics, fullscreen focus and join-split-words are all opt-in.
        Assert.True(Fresh.EnableAnimatedCovers);
        Assert.False(Fresh.LiquidGlassEnabled);
        Assert.False(Fresh.LyricsFlowingLightEnabled);
        Assert.False(Fresh.LyricsFullScreenFocusEnabled);
        // GitHub #211: the full-screen lyrics page keeps its island unless asked not to.
        Assert.False(Fresh.LyricsFullScreenHideIsland);
        // Discord (f7uh): the lyrics page shows its lyrics unless the user hides them.
        Assert.False(Fresh.LyricsPageHideLyrics);
        Assert.False(Fresh.LyricsJoinSplitWords);
        // Minimum line opacity ships ON (15%) so every lyric line can be clicked to seek
        // (Discord, Mistery 09-20); the constant is what the slider's double-tap restores.
        Assert.Equal(15, AppSettings.LyricsMinLineOpacityDefault);
        Assert.Equal(AppSettings.LyricsMinLineOpacityDefault, Fresh.LyricsMinLineOpacity);
        Assert.Equal(0.4, Fresh.PlaybackBarBackgroundOpacity);
        Assert.True(Fresh.GaplessPlaybackEnabled);
        // Songs rows ship with album art thumbnails (issue #23); the column chooser
        // can switch them off for the compact text-only look.
        Assert.True(Fresh.ShowArtworkColumn);
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

    [Fact]
    public void AddedLyricsSources_OnlyKugouSearchesAutomatically()
    {
        // Issue #113: Kugou answered every test lookup; Musixmatch's token endpoint went to a
        // captcha during testing and YouTube Music costs ~1 MB per lookup, so those two are
        // picker-only until switched on.
        Assert.True(Fresh.KugouEnabled);
        Assert.False(Fresh.MusixmatchEnabled);
        Assert.False(Fresh.YouTubeMusicLyricsEnabled);
    }

    [Fact]
    public void AlbumColourPlayPauseFadeAndArtistSort_ShipOn()
    {
        // Turned on for new installs 2026-10-18. Settings files that predate a toggle keep it
        // off (AppSettings.OnDeserializing, ChangedDefaultsUpgradeTests).
        Assert.True(Fresh.AlbumPageTintEnabled);
        Assert.True(Fresh.PlayPauseFadeEnabled);
        Assert.Equal(300, Fresh.PlayPauseFadeMs);
        Assert.True(Fresh.IgnoreLeadingWordsInArtistSort);
        Assert.Equal(new[] { "The" }, Fresh.ArtistSortIgnoredWords);
    }

    [Fact]
    public void LyricsAutoDownload_IsOptIn()
    {
        // Its online pick goes through the Search Lyrics path, which writes .lrc/.elrc files
        // beside the song (or into its tags, per Save lyrics to) with no click of the user's.
        Assert.False(Fresh.LyricsAutoDownload);
    }

    [Fact]
    public void Unison_SearchesAutomatically_LrcRed_IsOptIn()
    {
        // Unison: keyless, documented for third-party reads (2026-10-17). lrc.red's word-synced
        // files look Apple-sourced, so it waits for the user to switch it on.
        Assert.False(Fresh.LrcRedEnabled);
        Assert.True(Fresh.UnisonEnabled);
    }
}
