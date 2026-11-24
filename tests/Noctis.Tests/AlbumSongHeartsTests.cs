using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Album page › Heart on every song (Discord 11-24, sina): favoriting a song from an
/// album took the "..." menu. With the setting on, every album row shows a heart, faint until
/// favorited, and a click favorites that song only. On for a new install; a settings.json saved
/// before the setting existed keeps the old look (heart on favorites only, not clickable).
/// </summary>
public class AlbumSongHeartsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public AlbumSongHeartsTests() => Directory.CreateDirectory(_root);

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

    private sealed class FakeLastFm : ILastFmService
    {
        public bool IsAuthenticated => false;
        public string? Username => null;
        public void Configure(string? sessionKey) { }
        public Task<string> GetAuthUrlAsync() => Task.FromResult(string.Empty);
        public Task<bool> CompleteAuthAsync() => Task.FromResult(false);
        public string? GetSessionKey() => null;
        public void Logout() { }
        public Task ScrobbleAsync(Track track, DateTime startedAt) => Task.CompletedTask;
        public Task UpdateNowPlayingAsync(Track track) => Task.CompletedTask;
        public Task<string?> GetAlbumDescriptionAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string a, string b, string? d, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string a, string b, CancellationToken ct = default) => Task.CompletedTask;
    }

    private SettingsViewModel CreateSettings() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    private async Task<AppSettings> LoadFromAsync(string settingsJson)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), settingsJson);
        return await new PersistenceService(_root).LoadSettingsAsync();
    }

    // ── Default and upgrade ────────────────────────────────────────

    [Fact]
    public void NewInstall_ShowsTheHearts()
    {
        Assert.True(new AppSettings().AlbumShowSongHearts);
    }

    [Fact]
    public async Task FileFromBeforeTheSetting_KeepsTheOldLook()
    {
        var s = await LoadFromAsync("""{ "volume": 42 }""");
        Assert.Equal(42, s.Volume);
        Assert.False(s.AlbumShowSongHearts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitValueInTheFile_IsKept(bool on)
    {
        var s = await LoadFromAsync($$"""{ "albumShowSongHearts": {{(on ? "true" : "false")}} }""");
        Assert.Equal(on, s.AlbumShowSongHearts);
    }

    // ── Settings toggle ────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Toggle_SurvivesSaveAndReload()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.True(vm.AlbumShowSongHearts); // no settings file yet: a new install

        vm.AlbumShowSongHearts = false;
        await vm.SaveAsync();

        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.False(reloaded.AlbumShowSongHearts);
        Assert.False(reloaded.GetSettings().AlbumShowSongHearts);
    }

    // ── Row heart click ────────────────────────────────────────────

    private static (AlbumDetailViewModel Vm, List<Track> Tracks) OpenAlbum()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var albumId = Guid.NewGuid();
        var tracks = Enumerable.Range(1, 3).Select(n => new Track
        {
            Id = Guid.NewGuid(),
            Title = $"Song {n}",
            Artist = "B",
            Album = "A",
            AlbumId = albumId,
            TrackNumber = n,
            DiscNumber = 1,
            FilePath = TestPaths.Primary("Music", "A", $"{n:00}.mp3"),
            Duration = TimeSpan.FromMinutes(3),
        }).ToList();
        var album = new Album { Id = albumId, Name = "A", Artist = "B", Tracks = tracks };
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());
        Dispatcher.UIThread.RunJobs();
        return (vm, tracks);
    }

    [AvaloniaFact]
    public void RowHeart_TogglesThatSongOnly()
    {
        var (vm, tracks) = OpenAlbum();

        vm.ToggleFavoriteSingleCommand.Execute(tracks[1]);

        Assert.False(tracks[0].IsFavorite);
        Assert.True(tracks[1].IsFavorite);
        Assert.False(tracks[2].IsFavorite);

        vm.ToggleFavoriteSingleCommand.Execute(tracks[1]);
        Assert.False(tracks[1].IsFavorite);
    }

    [AvaloniaFact]
    public void RowHeart_UpdatesTheAlbumHeart()
    {
        var (vm, tracks) = OpenAlbum();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        foreach (var t in tracks)
            vm.ToggleFavoriteSingleCommand.Execute(t);

        Assert.Contains(nameof(AlbumDetailViewModel.IsAlbumFavorited), changed);
    }

    [AvaloniaFact]
    public void RowHeart_StaysEnabledWhileSaving()
    {
        // Synchronous like the playlist heart: an async command would grey out every row's heart.
        var (vm, tracks) = OpenAlbum();
        vm.ToggleFavoriteSingleCommand.Execute(tracks[0]);
        Assert.True(vm.ToggleFavoriteSingleCommand.CanExecute(tracks[2]));
    }

    // ── Press on the heart leaves the row selection alone ──────────

    [AvaloniaFact]
    public void PressOnHeart_IsRecognised_OtherPressesAreNot()
    {
        var glyph = new HeartIcon();
        var heart = new Button { Content = glyph };
        heart.Classes.Add("row-heart");
        var dots = new Button();
        dots.Classes.Add("row-more-dots");
        var title = new TextBlock();
        var row = new ListBoxItem { Content = new StackPanel { Children = { title, heart, dots } } };

        Assert.True(AlbumDetailView.IsRowHeartPress(glyph));
        Assert.True(AlbumDetailView.IsRowHeartPress(heart));
        Assert.False(AlbumDetailView.IsRowHeartPress(dots));
        Assert.False(AlbumDetailView.IsRowHeartPress(title));
        Assert.False(AlbumDetailView.IsRowHeartPress(row));
        Assert.False(AlbumDetailView.IsRowHeartPress(null));
    }
}
