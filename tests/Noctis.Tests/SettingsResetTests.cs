using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Reset everything promises every setting returns to its default. The reset
/// writes a default settings.json, but the next save re-applies the view-model's
/// properties on top of it (SyncToSettings), so any property the reset did not put back
/// (upmix, shortcuts, accent-follows-artwork, music videos, per-song lyrics clips, the
/// Songs columns, Lyrics Studio, ...) was written straight back over the defaults.
/// </summary>
public class SettingsResetTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static List<string> DifferingProperties(AppSettings actual, AppSettings expected) =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Where(p => JsonSerializer.Serialize(p.GetValue(actual)) != JsonSerializer.Serialize(p.GetValue(expected)))
            .Select(p => p.Name)
            .ToList();

    /// <summary>What a fresh install writes on its first save.</summary>
    private async Task<AppSettings> FreshInstallSaveAsync()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "fresh"));
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        await vm.SaveAsync();
        return await persistence.LoadSettingsAsync();
    }

    [AvaloniaFact]
    public async Task Reset_ThenNextSave_WritesTheSameFileAsAFreshInstall()
    {
        var dataDir = Path.Combine(_root, "used");
        Directory.CreateDirectory(dataDir);
        var clip = Path.Combine(dataDir, "clip.mp4");
        File.WriteAllText(clip, "x");

        var persistence = new PersistenceService(dataDir);
        await persistence.SaveSettingsAsync(new AppSettings
        {
            CommunityPluginsEnabled = false,
            AccentFollowsArtwork = true,
            AccentTrackLinks = false,
            AppIcon = "ruby",
            UpmixMode = "Surround",
            HeadphoneSurround = true,
            Shortcuts = new Dictionary<string, string> { [nameof(ShortcutAction.PlayPause)] = "P" },
            LyricsBackgroundMediaOverrides = new Dictionary<string, string> { ["track:1"] = clip },
            LyricsBackgroundPausesWithPlayback = true,
            MusicVideosEnabled = false,
            MusicVideoRoundedCorners = false,
            MusicVideoUseVideoAudio = true,
            MusicVideoAsBackground = true,
            DiscordShowAlbum = false,
            DiscordStatusDisplay = "Title",
            NoctisServerPort = 5050,
            ShowGenreColumn = false,
            ShowBpmColumn = true,
            ShowPlaysColumn = false,
            SyncEnabled = true,
            SyncDeviceName = "Desk",
            YouTubeDownloadFolder = Path.Combine(dataDir, "yt"),
            YtDlpPath = Path.Combine(dataDir, "yt-dlp.exe"),
            LyricsStudioModel = "Small",
            LyricsStudioLanguage = "en",
            LyricsStudioWordTimings = false,
            LyricsStudioSkipAlreadyTimed = false,
            LyricsStudioEmbedTags = true,
            LyricsStudioOnlineLyrics = false,
            IgnoreLeadingWordsInArtistSort = false,
            AlbumPageTintEnabled = false,
            PlayPauseFadeEnabled = false,
            ArtistSortIgnoredWords = new List<string> { "The", "Los" },
            PlayCountThresholdPercent = 75,
        });

        var audio = new FakeAudioPlayer();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        vm.SetAudioPlayer(audio);
        await vm.LoadAsync();
        Assert.True(vm.AccentFollowsArtwork);
        Assert.False(vm.AccentTrackLinks);
        Assert.Equal("ruby", vm.AppIcon);
        Assert.False(vm.ShortcutService.IsDefault(ShortcutAction.PlayPause));
        Assert.True(vm.HasLyricsBackgroundOverride("track:1"));
        Assert.Equal("Surround", audio.UpmixMode);
        Assert.True(vm.HeadphoneSurround);

        // ConfirmResetLibrary's settings half, without its OS and data-folder side effects.
        var syncIdBefore = vm.GetSettings().SyncDeviceId;
        var defaults = vm.CreateResetSettings();
        await vm.WriteDefaultSettingsAsync(defaults);
        vm.ResetSettingsToDefaults(defaults);

        Assert.True(vm.ShortcutService.IsDefault(ShortcutAction.PlayPause));
        Assert.False(vm.HasLyricsBackgroundOverride("track:1"));
        Assert.Equal("Off", audio.UpmixMode);
        Assert.False(vm.HeadphoneSurround);
        Assert.Equal("classic", vm.AppIcon);

        // Any toggle, or the unconditional save when the window closes.
        await vm.SaveAsync();
        var afterReset = await persistence.LoadSettingsAsync();
        // The defaults that went on for new installs (2026-10-18) come back on.
        Assert.True(afterReset.AlbumPageTintEnabled);
        Assert.True(afterReset.PlayPauseFadeEnabled);
        Assert.True(afterReset.IgnoreLeadingWordsInArtistSort);

        // Except the sync id: made once per install, at random (a fresh install has its own), and
        // kept through a reset so phones still see the same computer in the sync ledger.
        var fresh = await FreshInstallSaveAsync();
        Assert.Equal(new[] { nameof(AppSettings.SyncDeviceId) }, DifferingProperties(afterReset, fresh));
        Assert.False(string.IsNullOrWhiteSpace(syncIdBefore));
        // 11-14: the reset handed the view-model an empty id and the load step made a new one.
        Assert.Equal(syncIdBefore, afterReset.SyncDeviceId);
        Assert.False(string.IsNullOrWhiteSpace(fresh.SyncDeviceId));
    }


    /// <summary>A valid value that is not the default, for settings a bare flip or "zz" does not
    /// reach: the load normalises an unknown name back to the default, which hid any setting
    /// the reset missed (11-14 audit). A new setting of a type <see cref="Seed"/> cannot make
    /// fails <see cref="Reset_EveryFieldChanged_ComesBackAsAFreshInstall_ExceptTheKeptOnes"/>
    /// until it gets one here.</summary>
    private static Dictionary<string, Func<string, object?>> CuratedSeeds() => new()
    {
        [nameof(AppSettings.Theme)] = _ => "Dark",
        [nameof(AppSettings.Language)] = _ => "de",
        [nameof(AppSettings.AppIcon)] = _ => "ruby",
        [nameof(AppSettings.PlayCountThresholdPercent)] = _ => 75,
        [nameof(AppSettings.QueuePanelWidth)] = _ => 400.0,
        [nameof(AppSettings.SidebarExpandedWidth)] = _ => 300.0, // the default * 0.9 + 0.01 is not on a whole pixel
        [nameof(AppSettings.QueueRowLine1)] = _ => nameof(QueueRowField.Album),
        [nameof(AppSettings.QueueRowLine1Extra)] = _ => nameof(QueueRowField.Composer),
        [nameof(AppSettings.QueueRowLine2)] = _ => nameof(QueueRowField.AlbumArtist),
        [nameof(AppSettings.QueueRowLine2Extra)] = _ => nameof(QueueRowField.Title),
        [nameof(AppSettings.QueueRowRight)] = _ => nameof(QueueRowSide.Duration),
        [nameof(AppSettings.AnimatedArtworkCacheMb)] = _ => 2048,
        [nameof(AppSettings.PlaylistNewBadgeDays)] = _ => 14,
        [nameof(AppSettings.AnimatedArtworkQuality)] = _ => AnimatedArtworkQualities.P2160,
        [nameof(AppSettings.AlbumPageTintStrength)] = _ => 50,
        [nameof(AppSettings.UpmixMode)] = _ => "Surround",
        [nameof(AppSettings.MusicVideoCookiesBrowser)] = _ => "edge",
        [nameof(AppSettings.AudioBalance)] = _ => 40,
        [nameof(AppSettings.SleepTimerFadeSeconds)] = _ => 60,
        [nameof(AppSettings.LyricsStudioLanguage)] = _ => "en",
        [nameof(AppSettings.SmallArtistMinSongs)] = _ => 5,
        [nameof(AppSettings.HomeRecentAlbumsCount)] = _ => 10,
        [nameof(AppSettings.HomeNewAlbumsCount)] = _ => 20,
        [nameof(AppSettings.UiScalePercent)] = _ => 125,
        [nameof(AppSettings.BackgroundBlurAmount)] = _ => 0.5, // 0.1 * 0.9 + 0.01 is the default again
        [nameof(AppSettings.NowPlayingArtworkStyle)] = _ => nameof(ArtworkMedium.Vinyl),
        [nameof(AppSettings.MiniPlayerStyle)] = _ => nameof(MiniPlayerStyle.Pill),
        [nameof(AppSettings.CoverFlowLayout)] = _ => nameof(CoverFlowLayout.Cascade),
        [nameof(AppSettings.AlbumCarouselStyle)] = _ => nameof(AlbumCarouselStyle.Vinyl),
        [nameof(AppSettings.LyricsVisualizerStyle)] = _ => nameof(VisualizerStyle.Wave),
        [nameof(AppSettings.LyricsFlowingStyle)] = _ => FlowingStyles.Kawarp,
        [nameof(AppSettings.PlaybackBarSkipSeconds)] = _ => 30,
        [nameof(AppSettings.PlaybackBarArtworkClick)] = _ => nameof(IslandArtworkClick.LyricsPage),
        [nameof(AppSettings.PlaybackBarQuickPlaylistIds)] = _ => new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() },
        [nameof(AppSettings.SidebarPageOrder)] = _ => new List<string> { "songs", "home", "albums", "artists", "genres", "folders",
            "inbox", "playlists", "visualizer", "lyricsstudio", "server", "cd", "settings" },
        [nameof(AppSettings.SidebarHiddenPages)] = _ => new List<string> { "artists" },
        [nameof(AppSettings.StartPage)] = _ => "albums",
        [nameof(AppSettings.ArtistGroupMode)] = _ => nameof(ArtistGroupMode.AlbumArtist),
        [nameof(AppSettings.DiscordStatusDisplay)] = _ => nameof(DiscordStatusDisplay.Title),
        [nameof(AppSettings.PlayCountImportMode)] = _ => nameof(PlayCountImportMode.Replace),
        [nameof(AppSettings.LyricsSaveTarget)] = _ => nameof(LyricsSaveTarget.Embedded),
        [nameof(AppSettings.LyricsStudioFiles)] = _ => nameof(LyricsStudioFiles.ElrcOnly),
        [nameof(AppSettings.LyricsPreferredType)] = _ => nameof(LyricsPreferredType.LineSynced),
        [nameof(AppSettings.Shortcuts)] = _ => new Dictionary<string, string> { [nameof(ShortcutAction.PlayPause)] = "P" },
        [nameof(AppSettings.LyricsBackgroundMediaPath)] = clip => clip,
        [nameof(AppSettings.LyricsBackgroundMediaOverrides)] = clip => new Dictionary<string, string> { ["track:1"] = clip },
        [nameof(AppSettings.FolderRules)] = _ => new List<FolderRule> { new() { Path = Path.GetTempPath(), Include = false } },
        [nameof(AppSettings.CustomThemes)] = _ => new List<CustomThemeDefinition> { new() { Name = "Mine" } },
        [nameof(AppSettings.UserEqPresets)] = _ => new List<UserEqPreset>
            { new() { Name = "MyEq", Bands = new() { new ParametricEqBand { FrequencyHz = 100, GainDb = 3, Q = 1 } } } },
        [nameof(AppSettings.ParametricEqBands)] = _ => new List<ParametricEqBand> { new() { FrequencyHz = 250, GainDb = -2, Q = 1 } },
        [nameof(AppSettings.SelectedUserEqPreset)] = _ => "MyEq",
        [nameof(AppSettings.HiddenEqPresets)] = _ => new List<string> { "Rock" },
        [nameof(AppSettings.AlbumLinks)] = _ => new List<AlbumLink> { new() { AlbumId = Guid.NewGuid(), SourceAlbumId = Guid.NewGuid() } },
        [nameof(AppSettings.SourceConnections)] = _ => new List<SourceConnection> { new() { Name = "nas", Type = SourceType.Smb, BaseUriOrPath = "nas/music" } },
        // These start a network listener in the test; their reset is a plain assignment.
        [nameof(AppSettings.WebRemoteEnabled)] = _ => false,
        [nameof(AppSettings.LocalApiEnabled)] = _ => false,
    };

    /// <summary>Settings a load or save rewrites by design, so a seeded value does not come back:
    /// derived from other settings, stamped, or only stored with a live connection.</summary>
    private static readonly HashSet<string> NormalizedOnLoad = new()
    {
        nameof(AppSettings.CrossfadeEnabled),       // mirror of SongTransitionsEnabled + style
        nameof(AppSettings.LyricsStudioModel),      // one model (Medium) since 10-2026
        nameof(AppSettings.EqualizerPresetIndex),   // follows SelectedUserEqPreset
        nameof(AppSettings.EqualizerBands),         // downgrade mirror of the parametric curve
        nameof(AppSettings.ParametricEqBands),      // rebuilt from the curve
        nameof(AppSettings.OtherComputerSecrets),   // GitHub #230 bookkeeping
        nameof(AppSettings.ListenBrainzToken),      // stored only while connected
        nameof(AppSettings.LyricsRenderer),         // a plugin renderer that is not installed
        nameof(AppSettings.WrittenByVersion),       // stamped on every save
        nameof(AppSettings.OpenedFromNewerVersion), // not saved
        nameof(AppSettings.WebRemoteEnabled), nameof(AppSettings.LocalApiEnabled), // not seeded (above)
    };

    private static object? Seed(PropertyInfo p, object? baseline)
    {
        var t = p.PropertyType;
        if (t == typeof(bool)) return !(bool)baseline!;
        if (t == typeof(bool?)) return false;
        if (t == typeof(int)) return (int)baseline! + 1;
        if (t == typeof(double)) return (double)baseline! * 0.9 + 0.01;
        if (t == typeof(double?)) return 333.0;
        if (t == typeof(string)) return "zz";
        if (t == typeof(DateTime?)) return new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        if (t == typeof(float[])) return Enumerable.Range(0, 10).Select(i => (float)i).ToArray();
        if (t == typeof(List<string>)) return new List<string> { "zz" };
        if (t == typeof(List<Guid>)) return new List<Guid> { Guid.NewGuid() };
        if (t == typeof(Dictionary<string, string>)) return new Dictionary<string, string> { ["zz"] = "zz" };
        if (t == typeof(Dictionary<string, List<string>>)) return new Dictionary<string, List<string>> { ["zz"] = new() { "zz" } };
        if (t == typeof(Dictionary<string, Dictionary<string, string>>))
            return new Dictionary<string, Dictionary<string, string>> { ["zz"] = new() { ["a"] = "b" } };
        return null;
    }

    private static IEnumerable<PropertyInfo> SavedProperties() =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                        && p.Name != nameof(AppSettings.ExtensionData));

    private static string J(object? v) => JsonSerializer.Serialize(v);

    /// <summary>Public scalar properties that differ between two view-models (what the page and
    /// everything bound to it shows), skipping what describes this session rather than a setting.</summary>
    private static List<string> DifferingLiveState(object reset, object fresh, params string[] sessionOnly) =>
        reset.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !sessionOnly.Contains(p.Name))
            .Where(p => p.PropertyType.IsPrimitive || p.PropertyType.IsEnum || p.PropertyType == typeof(string)
                        || Nullable.GetUnderlyingType(p.PropertyType) is { IsPrimitive: true })
            .Where(p => !Equals(p.GetValue(reset), p.GetValue(fresh)))
            .Select(p => $"{p.Name}: after reset {p.GetValue(reset)}, fresh {p.GetValue(fresh)}")
            .ToList();

    /// <summary>
    /// 11-03 audit, made stricter 11-14: every setting changed to a value the load keeps, then a
    /// reset. The file the next save writes is a fresh install's except the kept fields
    /// (<see cref="SettingsViewModel.ResetKeptSettings"/>), and the live view-model and player
    /// show what a fresh install shows. Before: the per-album carousel styles and the "don't
    /// scrobble" list survived the reset (SyncToSettings wrote them back), the sync id changed,
    /// and the 11-03 version seeded "zz" into ~40 named settings, which the load turned back
    /// into defaults, so a missed reset there could not fail.
    /// </summary>
    [AvaloniaFact]
    public async Task Reset_EveryFieldChanged_ComesBackAsAFreshInstall_ExceptTheKeptOnes()
    {
        var dataDir = Path.Combine(_root, "every");
        Directory.CreateDirectory(dataDir);
        var clip = Path.Combine(dataDir, "clip.mp4");
        File.WriteAllText(clip, "x");
        var curated = CuratedSeeds();
        var seeded = new AppSettings();
        var baseline = new AppSettings();
        var unseedable = new List<string>();
        foreach (var p in SavedProperties())
        {
            var value = curated.TryGetValue(p.Name, out var make) ? make(clip) : Seed(p, p.GetValue(baseline));
            if (value is null) unseedable.Add(p.Name);
            else p.SetValue(seeded, value);
        }
        Assert.Empty(unseedable); // a new settings type: teach Seed or CuratedSeeds about it
        // A seed equal to the default tests nothing (a default changed under a curated seed).
        Assert.Empty(SavedProperties()
            .Where(p => !NormalizedOnLoad.Contains(p.Name) && J(p.GetValue(seeded)) == J(p.GetValue(baseline)))
            .Select(p => p.Name));

        // Every seed must survive a load and a save, or it never tested the reset.
        var roundTrip = new PersistenceService(Path.Combine(_root, "roundtrip"));
        await roundTrip.SaveSettingsAsync(seeded);
        var rvm = new SettingsViewModel(roundTrip, new FakeLibraryService(), new NoOpPlayHistoryService());
        rvm.SetAudioPlayer(new FakeAudioPlayer());
        await rvm.LoadAsync();
        await rvm.SaveAsync();
        var back = await roundTrip.LoadSettingsAsync();
        Assert.Empty(SavedProperties()
            .Where(p => !NormalizedOnLoad.Contains(p.Name) && J(p.GetValue(back)) != J(p.GetValue(seeded)))
            .Select(p => $"{p.Name}: seeded {J(p.GetValue(seeded))}, loaded {J(p.GetValue(back))}"));

        var persistence = new PersistenceService(dataDir);
        await persistence.SaveSettingsAsync(seeded);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        vm.SetAudioPlayer(new FakeAudioPlayer());
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        vm.SetPlayer(player);
        await vm.LoadAsync();

        // ConfirmResetLibrary's settings half.
        var defaults = vm.CreateResetSettings();
        await vm.WriteDefaultSettingsAsync(defaults);
        vm.ResetSettingsToDefaults(defaults);
        await vm.SaveAsync();
        var afterReset = await persistence.LoadSettingsAsync();

        var freshPersistence = new PersistenceService(Path.Combine(_root, "fresh"));
        var fvm = new SettingsViewModel(freshPersistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        fvm.SetAudioPlayer(new FakeAudioPlayer());
        var freshPlayer = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), freshPersistence, new FakeAnimatedCoverService());
        fvm.SetPlayer(freshPlayer);
        await fvm.LoadAsync();
        await fvm.SaveAsync();
        var fresh = await freshPersistence.LoadSettingsAsync();

        Assert.Equal(SettingsViewModel.ResetKeptSettings.OrderBy(n => n), DifferingProperties(afterReset, fresh).OrderBy(n => n));
        foreach (var kept in SettingsViewModel.ResetKeptSettings)
        {
            var p = typeof(AppSettings).GetProperty(kept)!;
            Assert.Equal(J(p.GetValue(seeded)), J(p.GetValue(afterReset)));
        }

        Assert.Empty(DifferingLiveState(vm, fvm, SettingsViewModel.ResetKeptSettings.Concat(new[]
        {
            nameof(SettingsViewModel.ProfileAvatarDirectory), nameof(SettingsViewModel.LocalApiDiscoveryPath),
            nameof(SettingsViewModel.ScanStatusText), nameof(SettingsViewModel.DevLogText),
            nameof(SettingsViewModel.StorageStatusText),
        }).ToArray()));
        Assert.Empty(DifferingLiveState(player, freshPlayer));
        Assert.Equal(fvm.SidebarPageOrderKeys, vm.SidebarPageOrderKeys);
        Assert.Equal(fvm.SidebarHiddenPageKeys, vm.SidebarHiddenPageKeys);
        Assert.Empty(vm.CustomThemes);
        Assert.Empty(vm.AlbumCarouselStyleOverrideMap());
        Assert.False(vm.IsScrobbleExcluded("zz"));
    }

    /// <summary>
    /// The plugins stay installed through a reset. The community switch went off only in the
    /// file (the host's own switch then saw "already off"), so community plugins kept running
    /// while Settings still showed the switch on; and the verified-official records were
    /// dropped, so from the next launch the official plugins counted as community ones.
    /// </summary>
    [AvaloniaFact]
    public async Task Reset_StopsCommunityPluginsLive_AndKeepsThePluginRecords()
    {
        const string id = "dev.test.plugin";
        var dataDir = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(dataDir);
        var persistence = new PersistenceService(dataDir);
        await persistence.SaveSettingsAsync(new AppSettings
        {
            CommunityPluginsEnabled = true,
            PluginPermissionGrants = new() { [id] = new List<string>() },
            VerifiedOfficialPlugins = new() { ["noctis.official"] = "1.0.0|abc" },
            DisabledPlugins = new() { "noctis.other" },
        });
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        using var box = new PluginSandbox();
        var host = new Noctis.Services.Plugins.PluginHost(null, dataDir, () => vm.GetSettings(), () => _ = vm.SaveAsync(), "1.5.3");
        vm.Plugins = host;
        var plugin = box.AddInProcess(host, new ScriptedPlugin(), PluginSandbox.Manifest(id: id));
        Assert.Equal(Noctis.Services.Plugins.PluginStatus.Running, plugin.Status);
        Assert.True(vm.CommunityPluginsEnabled);

        var defaults = vm.CreateResetSettings();
        await vm.WriteDefaultSettingsAsync(defaults);
        vm.ResetSettingsToDefaults(defaults);
        await vm.SaveAsync();

        Assert.Equal(Noctis.Services.Plugins.PluginStatus.Restricted, plugin.Status);
        Assert.False(vm.CommunityPluginsEnabled);
        var saved = await persistence.LoadSettingsAsync();
        Assert.False(saved.CommunityPluginsEnabled);
        Assert.Equal("1.0.0|abc", saved.VerifiedOfficialPlugins["noctis.official"]);
        Assert.True(saved.PluginPermissionGrants.ContainsKey(id));
        Assert.Equal(new[] { "noctis.other" }, saved.DisabledPlugins);
        host.UnloadAll();
    }

    /// <summary>
    /// "Add from YouTube" became a plugin (10-29): a settings.json that still carries its folder and
    /// a custom yt-dlp loads, and a save keeps both as they were (legacy fields nothing writes).
    /// </summary>
    [AvaloniaFact]
    public async Task SavedYouTubeSettings_FromBeforeThePlugin_LoadAndSurviveASave()
    {
        var dataDir = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "settings.json"),
            """{ "youTubeDownloadFolder": "D:\\Music\\YT", "ytDlpPath": "C:\\tools\\yt-dlp.exe", "musicFolders": ["D:\\Music"] }""");
        var persistence = new PersistenceService(dataDir);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());

        await vm.LoadAsync();
        await vm.SaveAsync();

        var saved = await persistence.LoadSettingsAsync();
        Assert.Equal(@"D:\Music\YT", saved.YouTubeDownloadFolder);
        Assert.Equal(@"C:\tools\yt-dlp.exe", saved.YtDlpPath);
    }
}
