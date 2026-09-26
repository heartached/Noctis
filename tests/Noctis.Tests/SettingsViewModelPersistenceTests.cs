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
        vm.DeveloperMode = true;
        await vm.SaveAsync();

        // "Restart": a fresh view-model loading from the same data root.
        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.True(reloaded.IncludePrereleaseUpdates);
        Assert.True(reloaded.DeveloperMode);
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
        Assert.True(vm.ImportDroppedMedia);
        Assert.False(vm.PlayPauseFadeEnabled);
        Assert.Equal(300, vm.PlayPauseFadeMs);

        vm.ImportDroppedMedia = false;
        vm.PlayPauseFadeEnabled = true;
        vm.PlayPauseFadeMs = 750;
        await vm.SaveAsync();

        var reloaded = CreateViewModel();
        await reloaded.LoadAsync();

        Assert.False(reloaded.ImportDroppedMedia);
        Assert.True(reloaded.PlayPauseFadeEnabled);
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
}
