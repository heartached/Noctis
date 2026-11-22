using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A sync (a phone, another computer) writes playlists.json and has the sidebar read it again,
/// which replaces every <see cref="Playlist"/> object. An open playlist page kept the object it
/// was opened with: it went on showing the old songs and name, and a song removed on it was
/// removed from that orphan while the save wrote the sidebar's list, so the edit was lost.
/// </summary>
public class PlaylistPageReloadTests
{
    private sealed class FilePersistence : TestPersistenceService
    {
        public List<Playlist> Stored = new();
        // A fresh list of fresh objects each load, as reading the file gives.
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(Stored.Select(Copy).ToList());
        public override Task SavePlaylistsAsync(List<Playlist> playlists)
        {
            Stored = playlists.Select(Copy).ToList();
            return Task.CompletedTask;
        }
        private static Playlist Copy(Playlist p) => new()
        {
            Id = p.Id, Name = p.Name, Description = p.Description, Color = p.Color,
            TrackIds = p.TrackIds.ToList(), CreatedAt = p.CreatedAt, ModifiedAt = p.ModifiedAt,
        };
    }

    private static Track Trk(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = "Artist",
        FilePath = TestPaths.Primary("reload", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
    };

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<(PlaylistViewModel Page, SidebarViewModel Sidebar, FilePersistence Files, List<Track> Tracks, Guid Id)> OpenAsync()
    {
        var tracks = new[] { "A", "B", "C" }.Select(Trk).ToList();
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var files = new FilePersistence();
        var id = Guid.NewGuid();
        files.Stored.Add(new Playlist { Id = id, Name = "Mix", TrackIds = { tracks[0].Id, tracks[1].Id } });
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, files, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(files, lib);
        await sidebar.LoadPlaylistsAsync();
        var page = new PlaylistViewModel(sidebar.Playlists.Single(), player, lib, files, sidebar);
        await PumpUntil(() => page.Tracks.Count == 2);
        return (page, sidebar, files, tracks, id);
    }

    [AvaloniaFact]
    public async Task AnOpenPlaylistPage_ShowsWhatASyncChanged()
    {
        var (page, sidebar, files, t, _) = await OpenAsync();

        // The sync: the file changes, then the sidebar reads it again.
        files.Stored[0].Name = "Mix 2";
        files.Stored[0].TrackIds.Add(t[2].Id);
        await sidebar.LoadPlaylistsAsync();
        await PumpUntil(() => page.Tracks.Count == 3);

        Assert.Equal(3, page.Tracks.Count);
        Assert.Equal("Mix 2", page.Name);
    }

    [AvaloniaFact]
    public async Task AnEditOnTheOpenPage_AfterASync_IsSaved()
    {
        var (page, sidebar, files, t, _) = await OpenAsync();
        files.Stored[0].TrackIds.Add(t[2].Id);
        await sidebar.LoadPlaylistsAsync();
        await PumpUntil(() => page.Tracks.Count == 3);

        await page.RemoveTrackCommand.ExecuteAsync(t[0]);

        Assert.Equal(new[] { t[1].Id, t[2].Id }, files.Stored.Single().TrackIds);
    }

    [AvaloniaFact]
    public async Task AnOpenPlaylistPage_WhosePlaylistASyncDeleted_GoesBack()
    {
        var (page, sidebar, files, _, _) = await OpenAsync();
        var wentBack = false;
        page.BackRequested += (_, _) => wentBack = true;

        files.Stored.Clear();
        await sidebar.LoadPlaylistsAsync();
        await PumpUntil(() => wentBack, 1000);

        Assert.True(wentBack);
    }
}
