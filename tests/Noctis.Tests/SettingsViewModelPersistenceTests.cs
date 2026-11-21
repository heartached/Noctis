using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// SaveAsync re-bases the in-memory AppSettings on the on-disk file
/// (MergeExternalSettingChangesAsync) and then relies on SyncToSettings to re-apply
/// every field this view-model owns. A VM-owned field missing from SyncToSettings is
/// therefore silently reverted to its stored value on every save: the About tab's
/// "Include pre-release updates" and "Developer Mode" toggles turned back off on the
/// next launch, and the volume pushed at shutdown never survived a restart. These
/// tests run the real PersistenceService against a temp root and simulate a restart
/// with a second view-model over the same data.
/// </summary>
public class SettingsViewModelPersistenceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    private SettingsViewModel CreateViewModel() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

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

    [AvaloniaFact]
    public async Task AboutTabToggles_SurviveSaveAndReload()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();

        vm.IncludePrereleaseUpdates = true;
        vm.AutoInstallUpdates = true;
        vm.DeveloperMode = true;
        await vm.SaveAsync();

        // "Restart": a fresh view-model loading from the same data root.
        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.True(reloaded.IncludePrereleaseUpdates);
        Assert.True(reloaded.AutoInstallUpdates);
        Assert.True(reloaded.DeveloperMode);
    }

    /// <summary>Turning "Update automatically" off must never leave an install queued for the
    /// next launch: the queue entry and its downloaded installer are both removed.</summary>
    [AvaloniaFact]
    public async Task AutoInstallUpdatesOff_DiscardsTheQueuedInstall()
    {
        var seeded = new AppSettings { AutoInstallUpdates = true };
        await new PersistenceService(_root).SaveSettingsAsync(seeded);

        var installer = Path.Combine(Path.GetTempPath(), $"Noctis-Update-{Guid.NewGuid().ToString("N")[..8]}-Setup.exe");
        File.WriteAllText(installer, "not an installer");
        try
        {
            var store = new AutoUpdateStore(_root);
            store.Save(new AutoUpdateState
            {
                Tag = "v99.0.0",
                Pending = new PendingInstall
                {
                    Tag = "v99.0.0", FromVersion = "1.0.0", ToVersion = "99.0.0",
                    InstallerPath = installer, Sha256 = new string('0', 64)
                }
            });

            var vm = CreateViewModel();
            await vm.LoadAsync();
            Assert.True(vm.AutoInstallUpdates);
            Assert.False(vm.IsReadyToInstall);

            vm.AutoInstallUpdates = false;

            Assert.Null(store.Load()!.Pending);
            Assert.False(File.Exists(installer));
            Assert.False(vm.IsAutoInstallPending);
        }
        finally
        {
            try { File.Delete(installer); } catch { }
        }
    }

    /// <summary>Discord (Mistery, 2026-09-21): the picker applied the language live but the
    /// save merged the on-disk "" back, so every restart fell back to the OS language.</summary>
    [AvaloniaFact]
    public async Task Language_SurvivesSaveAndReload()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();
        var pick = vm.LanguageOptions.FirstOrDefault(o => !string.IsNullOrEmpty(o.Code));
        Assert.NotNull(pick); // satellite resource assemblies ship with the test output
        try
        {
            vm.LanguageChoice = pick;
            await vm.SaveAsync();

            var reloaded = CreateViewModel();
            await reloaded.LoadAsync();

            Assert.Equal(pick!.Code, reloaded.GetSettings().Language);
            Assert.Equal(pick.Code, reloaded.LanguageChoice?.Code);
        }
        finally
        {
            vm.LanguageChoice = vm.LanguageOptions[0]; // back to the system language for the other tests
        }
    }

    /// <summary>GitHub #71 / #73: the import toggle and the play/pause fade are VM-owned too.</summary>
    [AvaloniaFact]
    public async Task ImportToggleAndPlayPauseFade_SurviveSaveAndReload()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();
        Assert.False(vm.ImportDroppedMedia); // off on a new install (2026-11-14)
        Assert.True(vm.PlayPauseFadeEnabled);
        Assert.Equal(300, vm.PlayPauseFadeMs);

        vm.ImportDroppedMedia = true;
        vm.PlayPauseFadeEnabled = false;
        vm.PlayPauseFadeMs = 750;
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.True(reloaded.ImportDroppedMedia);
        Assert.False(reloaded.PlayPauseFadeEnabled);
        Assert.Equal(750, reloaded.PlayPauseFadeMs);
    }

    /// <summary>GitHub #78: the lyric layer toggles ship on and survive a restart when off.</summary>
    [AvaloniaFact]
    public async Task LyricLayerToggles_DefaultOn_AndSurviveSaveAndReload()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();
        Assert.True(vm.LyricsShowTranslations);
        Assert.True(vm.LyricsShowRomanization);
        Assert.True(vm.LyricsShowBackgroundVocals);

        vm.LyricsShowTranslations = false;
        vm.LyricsShowRomanization = false;
        vm.LyricsShowBackgroundVocals = false;
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.False(reloaded.LyricsShowTranslations);
        Assert.False(reloaded.LyricsShowRomanization);
        Assert.False(reloaded.LyricsShowBackgroundVocals);
    }

    [AvaloniaFact]
    public async Task FlowingBackgroundPicker_OffersDriftWithoutTheBeat()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();

        var keys = vm.FlowingOptions.Select(o => o.Key).ToList();
        Assert.Equal(new[] { SettingsViewModel.FlowingOff, FlowingStyles.Drift, FlowingStyles.DriftCalm, FlowingStyles.Kawarp, FlowingStyles.KawarpCalm },
            keys.Take(5));

        vm.SelectedFlowingOption = vm.FlowingOptions.First(o => o.Key == FlowingStyles.DriftCalm);
        Assert.True(vm.LyricsFlowingLightEnabled);
        Assert.Equal(FlowingStyles.DriftCalm, vm.LyricsFlowingStyle);
        Assert.False(vm.IsKawarpStyle);
    }

    [AvaloniaFact]
    public async Task ShutdownVolume_SurvivesSaveAndReload()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();

        // MainWindowViewModel.ShutdownAsync pushes the player volume, then saves.
        vm.SetVolume(37);
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.Equal(37, reloaded.GetSettings().Volume);
    }

    /// <summary>A32: the volume reached disk only through the shutdown save, so a crash or
    /// kill brought back the last graceful exit's (possibly louder) level. A live change
    /// now rides the debounced settings write on its own.</summary>
    [AvaloniaFact]
    public async Task LiveVolumeChange_ReachesDiskWithoutShutdownSave()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();

        vm.PersistVolume(23); // no SaveAsync: the debounced write alone must land it

        var persistence = new PersistenceService(_root);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        int stored;
        do
        {
            await Task.Delay(100);
            stored = (await persistence.LoadSettingsAsync()).Volume;
        } while (stored != 23 && DateTime.UtcNow < deadline);

        Assert.Equal(23, stored);
    }

    [AvaloniaFact]
    public async Task SongsViewState_SurvivesSaveAndReload()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();

        // These three used to be view-model-only, so the Songs list reset to
        // Date Added ▼ / All Songs on every launch while the columns beside them
        // persisted. SyncToSettings must carry them now.
        vm.SongsSortColumn = "Artist";
        vm.SongsSortAscending = true;
        vm.SongsShowOnlyFavorites = true;
        vm.ShowTimeColumn = false;
        vm.ShowArtistColumn = false;
        vm.ShowAlbumColumn = false;
        vm.ShowFavoritesColumn = false;
        vm.ShowPlaysColumn = false;
        vm.ShowBpmColumn = true;
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.Equal("Artist", reloaded.SongsSortColumn);
        Assert.True(reloaded.SongsSortAscending);
        Assert.True(reloaded.SongsShowOnlyFavorites);
        Assert.False(reloaded.ShowTimeColumn);
        Assert.False(reloaded.ShowArtistColumn);
        Assert.False(reloaded.ShowAlbumColumn);
        Assert.False(reloaded.ShowFavoritesColumn);
        Assert.False(reloaded.ShowPlaysColumn);
        Assert.True(reloaded.ShowBpmColumn);
    }

    [AvaloniaFact]
    public async Task AlbumSort_SurvivesSaveAndReload()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();

        vm.AlbumSortMode = "title";
        vm.AlbumSortAscending = false;
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.Equal("title", reloaded.AlbumSortMode);
        Assert.False(reloaded.AlbumSortAscending);
    }

    /// <summary>GitHub #106: "Newest Albums First" is off by default and survives a restart.</summary>
    [AvaloniaFact]
    public async Task AlbumSortNewestFirst_DefaultsOff_AndSurvivesSaveAndReload()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();
        Assert.False(vm.AlbumSortNewestFirst);

        vm.AlbumSortNewestFirst = true;
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.True(reloaded.AlbumSortNewestFirst);
    }

    private static LibraryAlbumsViewModel CreateAlbumsViewModel(SettingsViewModel settings)
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        return new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
    }

    /// <summary>The Albums view's release Type filter (All/Albums/Singles/EPs/Live Albums/
    /// Compilations/Other) survives a restart.</summary>
    [AvaloniaTheory]
    [InlineData("album", ReleaseType.Album, "Albums")]
    [InlineData("single", ReleaseType.Single, "Singles")]
    [InlineData("ep", ReleaseType.EP, "EPs")]
    [InlineData("live", ReleaseType.Live, "Live Albums")]               // GitHub #122
    [InlineData("compilation", ReleaseType.Compilation, "Compilations")]
    [InlineData("other", ReleaseType.Other, "Other")]
    public async Task AlbumReleaseTypeFilter_SurvivesSaveAndReload(string key, ReleaseType expected, string label)
    {
        var vm = CreateViewModel();
        var albums = CreateAlbumsViewModel(vm);
        await vm.LoadAsync();
        Assert.Null(albums.ReleaseTypeFilter);

        albums.SetReleaseTypeFilterCommand.Execute(key);
        Assert.Equal(key, vm.AlbumReleaseTypeFilter);
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        var reloadedAlbums = CreateAlbumsViewModel(reloaded);
        await reloaded.LoadAsync();

        Assert.Equal(expected, reloadedAlbums.ReleaseTypeFilter);
        Assert.Equal(label, reloadedAlbums.ReleaseTypeFilterLabel);

        // Picking All again is remembered too.
        reloadedAlbums.SetReleaseTypeFilterCommand.Execute("all");
        await reloaded.SaveAsync();
        var again = CreateViewModel();
        var againAlbums = CreateAlbumsViewModel(again);
        await again.LoadAsync();
        Assert.Null(againAlbums.ReleaseTypeFilter);
    }

    /// <summary>A settings file from before the filter was persisted (no key), or one holding
    /// an unknown value, loads as "All".</summary>
    [AvaloniaTheory]
    [InlineData("{ \"albumSortMode\": \"title\" }")]
    [InlineData("{ \"albumReleaseTypeFilter\": \"bogus\" }")]
    [InlineData("{ \"albumReleaseTypeFilter\": null }")]
    public async Task AlbumReleaseTypeFilter_MissingOrUnknown_LoadsAsAll(string json)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), json);

        var vm = CreateViewModel();
        var albums = CreateAlbumsViewModel(vm);
        await vm.LoadAsync();

        Assert.Null(albums.ReleaseTypeFilter);
        Assert.Equal("All", albums.ReleaseTypeFilterLabel);
    }

    /// <summary>GitHub #89: the Folders track-pane sort defaults to folder order and survives a restart.</summary>
    [AvaloniaFact]
    public async Task FoldersSort_DefaultsToFolderOrder_AndSurvivesSaveAndReload()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();
        Assert.Equal("default", vm.FoldersSortMode);

        vm.FoldersSortMode = "modified-oldest";
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.Equal("modified-oldest", reloaded.FoldersSortMode);
    }

    /// <summary>GitHub #100: the artist page's Albums / Singles &amp; EPs sort defaults to newest first and survives a restart.</summary>
    [AvaloniaFact]
    public async Task ArtistReleaseSort_DefaultsToNewest_AndSurvivesSaveAndReload()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();
        Assert.Equal("newest", vm.ArtistReleaseSortMode);

        vm.ArtistReleaseSortMode = "oldest";
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.Equal("oldest", reloaded.ArtistReleaseSortMode);
    }

    [AvaloniaFact]
    public async Task SongsViewState_DefaultsMatchTheFormerStartupBehaviour()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();

        // A fresh install must land exactly where the old hardcoded defaults did,
        // so upgrading doesn't silently reorder anyone's library.
        Assert.Equal("Date Added", vm.SongsSortColumn);
        Assert.False(vm.SongsSortAscending);
        Assert.False(vm.SongsShowOnlyFavorites);
        Assert.True(vm.ShowTimeColumn);
        Assert.True(vm.ShowArtistColumn);
        Assert.True(vm.ShowAlbumColumn);
        Assert.True(vm.ShowFavoritesColumn);
        Assert.True(vm.ShowPlaysColumn);
        Assert.Equal("default", vm.AlbumSortMode);
        Assert.True(vm.AlbumSortAscending);
    }

    [AvaloniaFact]
    public async Task MediaServerConnection_SurvivesUnrelatedSaves_AndDisconnectRemovesIt()
    {
        // Seed a stored server connection (what a successful Connect persists).
        var seeded = new AppSettings();
        seeded.SourceConnections.Add(new SourceConnection
        {
            Name = "Subsonic",
            Type = SourceType.Navidrome,
            BaseUriOrPath = "https://music.example.com",
            Username = "demo",
            TokenOrPassword = "sesame",
            Enabled = true
        });
        await new PersistenceService(_root).SaveSettingsAsync(seeded);

        var vm = CreateViewModel();
        await vm.LoadAsync();
        Assert.True(vm.IsMediaServerConnected);
        Assert.Equal("https://music.example.com", vm.MediaServerUrl);
        Assert.Equal("demo", vm.MediaServerUsername);
        Assert.Equal(string.Empty, vm.MediaServerPassword); // secret never surfaces in the box

        // The trap: an unrelated save merges from disk and must not drop the connection.
        vm.IncludePrereleaseUpdates = true;
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();
        Assert.True(reloaded.IsMediaServerConnected);
        var stored = Assert.Single(reloaded.GetSettings().SourceConnections);
        Assert.Equal(SourceType.Navidrome, stored.Type);
        Assert.Equal("sesame", stored.TokenOrPassword); // DPAPI round-trip intact

        // Disconnect must remove it from disk, surviving further saves.
        await reloaded.DisconnectMediaServerCommand.ExecuteAsync(null);
        var after = CreateViewModel();
        await after.LoadAsync();
        Assert.False(after.IsMediaServerConnected);
        Assert.Empty(after.GetSettings().SourceConnections);
    }

    [AvaloniaFact]
    public async Task Shortcuts_SurviveSaveAndReload_AndStoreOnlyOverrides()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();

        vm.ShortcutService.Set(ShortcutAction.PlayPause, new Avalonia.Input.KeyGesture(Avalonia.Input.Key.P));
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.Equal(new Avalonia.Input.KeyGesture(Avalonia.Input.Key.P), reloaded.ShortcutService.Get(ShortcutAction.PlayPause));
        Assert.True(reloaded.ShortcutService.IsDefault(ShortcutAction.NextTrack));

        // Only the override is written: defaults never bloat settings.json.
        var json = await File.ReadAllTextAsync(Path.Combine(_root, "settings.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var shortcuts = doc.RootElement.GetProperty("shortcuts");
        Assert.Single(shortcuts.EnumerateObject());
        Assert.Equal("P", shortcuts.GetProperty("PlayPause").GetString());
    }

    /// <summary>
    /// The avatar picker copies the chosen image into the data root's profile folder, so
    /// "Remove" must delete that copy — it used to only blank the path and leave the file
    /// behind for good. A path outside the profile folder is the user's own file and must
    /// never be touched.
    /// </summary>
    [AvaloniaFact]
    public async Task ClearProfileAvatar_DeletesTheCopiedPicture_ButNeverAForeignFile()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();

        var profileDir = Path.Combine(_root, "profile");
        Directory.CreateDirectory(profileDir);
        var copied = Path.Combine(profileDir, "avatar.png");
        await File.WriteAllBytesAsync(copied, new byte[] { 1, 2, 3 });

        vm.ProfileAvatarPath = copied;
        await vm.ClearProfileAvatarCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.ProfileAvatarPath);
        Assert.False(File.Exists(copied));

        var foreign = Path.Combine(_root, "my-photo.png");
        await File.WriteAllBytesAsync(foreign, new byte[] { 1, 2, 3 });
        vm.ProfileAvatarPath = foreign;
        await vm.ClearProfileAvatarCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.ProfileAvatarPath);
        Assert.True(File.Exists(foreign));
    }

    /// <summary>
    /// Changing the picture used to overwrite the same "avatar.ext" file, so the bound
    /// path stayed identical (no change notification) and the path-keyed image cache kept
    /// showing the first picture. Every pick must land on a new path and drop the old copy.
    /// </summary>
    [AvaloniaFact]
    public async Task SetProfileAvatar_UsesAFreshPathPerPick_AndLogsIt()
    {
        var vm = CreateViewModel();
        await vm.LoadAsync();

        var first = Path.Combine(_root, "one.png");
        var second = Path.Combine(_root, "two.png");
        await File.WriteAllBytesAsync(first, new byte[] { 1 });
        await Task.Delay(5); // the copy name carries a millisecond stamp
        await File.WriteAllBytesAsync(second, new byte[] { 2 });

        await vm.SetProfileAvatarAsync(first);
        var firstCopy = vm.ProfileAvatarPath;
        await Task.Delay(5);
        await vm.SetProfileAvatarAsync(second);
        var secondCopy = vm.ProfileAvatarPath;

        Assert.NotEqual(firstCopy, secondCopy);
        Assert.StartsWith(vm.ProfileAvatarDirectory, secondCopy);
        Assert.True(File.Exists(secondCopy));
        Assert.False(File.Exists(firstCopy));
        Assert.Equal(new byte[] { 2 }, await File.ReadAllBytesAsync(secondCopy));

        // Profile edits are visible in the dev-mode session log.
        Assert.Contains("[Profile] Avatar set", DebugLog.Snapshot());
        await vm.ClearProfileAvatarCommand.ExecuteAsync(null);
        Assert.Contains("[Profile] Avatar removed", DebugLog.Snapshot());
    }

    /// <summary>
    /// settings.json on disk (a real PersistenceService), where a gated update from another
    /// writer lands right after the next settings read: the window between SaveAsync's merge
    /// read and its write.
    /// </summary>
    private sealed class GapWriter : TestPersistenceService, IPersistenceService
    {
        private readonly PersistenceService _real;
        public GapWriter(PersistenceService real) => _real = real;
        public Action<AppSettings>? Gap;
        public override async Task<AppSettings> LoadSettingsAsync()
        {
            var settings = await _real.LoadSettingsAsync();
            if (Interlocked.Exchange(ref Gap, null) is { } gap) await _real.UpdateSettingsAsync(gap);
            return settings;
        }
        public override Task SaveSettingsAsync(AppSettings settings) => _real.SaveSettingsAsync(settings);
        public Task UpdateSettingsAsync(Action<AppSettings> change) => _real.UpdateSettingsAsync(change);
    }

    /// <summary>
    /// SaveAsync read the file (MergeExternalSettingChangesAsync) outside the settings.json gate
    /// and saved under it, so a gated update from elsewhere (the lyrics page's background, the
    /// library's exclusions) that landed in between was overwritten by the copy read before it.
    /// </summary>
    [AvaloniaFact]
    public async Task Save_KeepsAGatedUpdateThatLandedAfterItsRead()
    {
        var real = new PersistenceService(_root);
        var persistence = new GapWriter(real);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        vm.DeveloperMode = true;
        await vm.SaveAsync();
        await Task.Delay(300); // any save a toggle queued

        persistence.Gap = s =>
        {
            s.LyricsBackgroundColorHex = "#123456";
            s.ExcludedFilePaths = new List<string> { @"D:\Music\removed.flac" };
        };
        await vm.SaveAsync();
        Assert.Null(persistence.Gap);

        var saved = await real.LoadSettingsAsync();
        Assert.Equal("#123456", saved.LyricsBackgroundColorHex);
        Assert.Equal(new[] { @"D:\Music\removed.flac" }, saved.ExcludedFilePaths);
        Assert.True(saved.DeveloperMode);
    }

    /// <summary>
    /// GitHub #245: a data folder shared by two computers, the other one on a newer Noctis. This
    /// build keeps the settings it does not know (top level, nested objects, members of list
    /// entries) when the Settings page saves a change through its changed-fields save, and when a
    /// later launch saves again, so they are still there when this computer is updated.
    /// </summary>
    [AvaloniaFact]
    public async Task Save_KeepsSettingsFromANewerVersion()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        var linkA = Guid.NewGuid();
        var linkB = Guid.NewGuid();
        await File.WriteAllTextAsync(path, $$"""
        {
          "developerMode": false,
          "futureFlag": true,
          "futureObject": { "mode": "Wide", "nested": { "deep": [1, "two"], "n": 7 } },
          "folderRules": [ { "path": "D:\\Music\\Live", "include": false, "enabled": true, "futureRuleField": "x" } ],
          "albumLinks": [ { "albumId": "{{linkA}}", "sourceAlbumId": "{{linkB}}", "futureLinkField": 3 } ],
          "writtenByVersion": "9.9.0"
        }
        """);

        var vm = CreateViewModel();
        await vm.LoadAsync();
        vm.DeveloperMode = true;
        await vm.SaveAsync();
        await Task.Delay(300); // any save a toggle queued
        // The next launch on this computer saves once more.
        var next = CreateViewModel();
        await next.LoadAsync();
        await next.SaveAsync();
        await Task.Delay(300);

        var raw = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.True(raw["developerMode"]!.GetValue<bool>());
        Assert.True(raw["futureFlag"]!.GetValue<bool>());
        Assert.Equal("""{"mode":"Wide","nested":{"deep":[1,"two"],"n":7}}""", raw["futureObject"]!.ToJsonString());
        Assert.Equal("x", raw["folderRules"]![0]!["futureRuleField"]!.GetValue<string>());
        Assert.Equal(3, raw["albumLinks"]![0]!["futureLinkField"]!.GetValue<int>());
        Assert.Equal("9.9.0", raw["writtenByVersion"]!.GetValue<string>());
    }

    /// <summary>
    /// GitHub #245, Reset Settings: a reset writes a fresh install's settings, so the settings of
    /// the newer version go back to their defaults as well (they are dropped from the file, as a
    /// fresh install never had them); the newer version's stamp goes with them.
    /// </summary>
    [AvaloniaFact]
    public async Task ResetSettings_StillWritesAFreshInstallsSettings()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(path, """{ "developerMode": true, "futureFlag": true, "writtenByVersion": "9.9.0" }""");

        var vm = CreateViewModel();
        await vm.LoadAsync();
        var defaults = vm.CreateResetSettings();
        await vm.WriteDefaultSettingsAsync(defaults);
        vm.ResetSettingsToDefaults(defaults);
        await vm.SaveAsync();
        await Task.Delay(300);

        var raw = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.False(raw["developerMode"]!.GetValue<bool>());
        Assert.False(raw.ContainsKey("futureFlag"));
        Assert.NotEqual("9.9.0", raw["writtenByVersion"]?.GetValue<string>());
    }
}
