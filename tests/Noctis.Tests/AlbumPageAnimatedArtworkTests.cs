using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;
using static Noctis.Services.ITunesArtworkService;

namespace Noctis.Tests;

/// <summary>
/// GitHub #167 on the album page: with the setting on, opening the page fetches the loop into
/// the cache and shows it; Remove Animated Artwork deletes it and the page never fetches it
/// again. With the setting off (the default) nothing is asked. No network: the lookup is faked.
/// </summary>
public class AlbumPageAnimatedArtworkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "albumanim-" + Guid.NewGuid().ToString("N"));
    private int _finds;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AnimatedArtworkAutoFetcher Fetcher(PersistenceService persistence)
    {
        var covers = new AnimatedCoverService(persistence);
        var lookup = new AnimatedArtworkBulkService(
            (q, _) =>
            {
                _finds++;
                return Task.FromResult<IReadOnlyList<RankedAlbumCandidate>>(new[]
                {
                    new RankedAlbumCandidate(new ArtworkCandidate(1, q.Album, q.Artist, "", "", "", "https://music.apple.com/us/album/x/1"), 140),
                });
            },
            (_, _) => Task.FromResult<AnimatedArtworkAlbum?>(new AnimatedArtworkAlbum(1, "t", "", "", null, null,
                new[] { new AnimatedArtworkVariant("1080p", "https://mvod.itunes.apple.com/a.m3u8", 1080, 1080, "hvc1", 1, true) },
                null, HasSquare: true, HasTall: false)),
            async (_, path, ct) => { await File.WriteAllTextAsync(path, "loop", ct); return true; },
            covers, ledgerPath: null);
        return new AnimatedArtworkAutoFetcher(lookup, covers, new AnimatedArtworkCache(persistence, null), null,
            delay: (_, _) => Task.CompletedTask);
    }

    private async Task<(AlbumDetailViewModel Page, AnimatedArtworkAutoFetcher Fetcher, PersistenceService Persistence)> OpenAsync(bool autoFetch)
    {
        var persistence = new PersistenceService(_dir);
        var settings = new AppSettings { AnimatedArtworkAutoFetch = autoFetch };
        await persistence.SaveSettingsAsync(settings);
        var lib = new FakeLibraryService();
        var settingsVm = new SettingsViewModel(persistence, lib, new FakeHistoryLog());
        await settingsVm.LoadAsync();

        var albumId = Guid.NewGuid();
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "Ship of Fools", Album = "Wings of Love", AlbumArtist = "Erasure", Artist = "Erasure",
            AlbumId = albumId, SourceType = SourceType.Local, FilePath = Path.Combine(_dir, "music", "01.mp3"),
        };
        var album = new Album { Id = albumId, Name = "Wings of Love", Artist = "Erasure", Tracks = new List<Track> { track } };
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var page = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new NullLastFm(), settingsVm);
        var fetcher = Fetcher(persistence);
        page.AutoFetcherProvider = () => fetcher;
        Dispatcher.UIThread.RunJobs();
        return (page, fetcher, persistence);
    }

    [AvaloniaFact]
    public async Task SettingOn_OpeningThePage_FetchesAndShowsTheLoop_AndRemoveKeepsItGone()
    {
        var (page, fetcher, persistence) = await OpenAsync(autoFetch: true);
        Assert.Null(page.AnimatedCoverPath);
        Assert.False(page.CanRemoveAnimatedArtwork);

        page.StartAutoFetch();
        await page.AutoFetchTask;

        var cached = persistence.GetCachedAnimatedCoverPath(page.Album.Id);
        Assert.Equal(cached, page.AnimatedCoverPath);
        Assert.True(page.CanRemoveAnimatedArtwork);

        await page.RemoveAnimatedArtworkCommand.ExecuteAsync(null);

        Assert.Null(page.AnimatedCoverPath);
        Assert.False(File.Exists(cached));
        Assert.False(page.CanRemoveAnimatedArtwork);
        Assert.True(fetcher.Cache.IsBlocked(page.Album.Id));

        // Opening the page again does not bring the wrong loop back.
        page.StartAutoFetch();
        await page.AutoFetchTask;
        Assert.Null(page.AnimatedCoverPath);
        Assert.Equal(1, _finds);
    }

    [AvaloniaFact]
    public async Task SettingOff_ByDefault_NothingIsAsked()
    {
        Assert.False(new AppSettings().AnimatedArtworkAutoFetch);
        var (page, _, _) = await OpenAsync(autoFetch: false);
        page.StartAutoFetch();
        await page.AutoFetchTask;
        Assert.Equal(0, _finds);
        Assert.Null(page.AnimatedCoverPath);
    }

    private sealed class NullLastFm : ILastFmService
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
}
