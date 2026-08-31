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
}
