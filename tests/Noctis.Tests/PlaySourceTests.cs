using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "Playing from …" (Discord 11-24, sina 4.3.7 / 4.3.4): a page that starts a queue names itself
/// (PlayerViewModel.WithSource); Add to Queue / Play Next leave it as it is, a new page start
/// replaces it and a start without a source clears it. The island title opens the album, or with
/// Song Title Opens Where It Plays From the source. Saved with the queue.
/// </summary>
public class PlaySourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-playsource-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static PlayerViewModel CreateVm(ILibraryService? library = null, IPersistenceService? persistence = null) =>
        new(new FakeAudioPlayer(), library ?? new FakeLibraryService(), persistence ?? new TestPersistenceService(),
            new FakeAnimatedCoverService());

    private static List<Track> Songs(string name, int count) => Enumerable.Range(1, count).Select(n => new Track
    {
        Id = Guid.NewGuid(),
        Title = $"{name} {n}",
        Artist = "A",
        Album = name,
        TrackNumber = n,
        FilePath = TestPaths.Primary("Music", name, $"{n:00}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
    }).ToList();

    private static PlaySource AlbumSource(string name) => new(PlaySourceKind.Album, name, Guid.NewGuid().ToString());

    [AvaloniaFact]
    public void PageStart_SetsTheSource_AndTheLabel()
    {
        var vm = CreateVm();
        var source = AlbumSource("Abbey Road");

        vm.WithSource(source).ReplaceQueueAndPlay(Songs("Abbey Road", 5), 2);

        Assert.Equal(source, vm.PlaySource);
        Assert.True(vm.HasPlaySource);
        Assert.Equal("Playing from Abbey Road", vm.PlayingFromText);
    }

    [AvaloniaFact]
    public void Section_IsNamedInTheUiLanguage()
    {
        var vm = CreateVm();
        vm.WithSource(PlaySource.Section(PlaySourceKind.Songs)).PlayFromRow(Songs("S", 3), 0);
        Assert.Equal("Songs", vm.PlaySourceName);
    }

    [AvaloniaFact]
    public void EveryStartKind_TakesTheSource()
    {
        var vm = CreateVm();
        var a = AlbumSource("A");
        vm.WithSource(a).PlayAll(Songs("A", 4));
        Assert.Equal(a, vm.PlaySource);

        var b = AlbumSource("B");
        vm.WithSource(b).PlayShuffled(Songs("B", 4));
        Assert.Equal(b, vm.PlaySource);

        vm.KeepShuffleOn = true; // Shuffle is on: the Keep Shuffle On path goes through PlayShuffled
        var c = AlbumSource("C");
        vm.WithSource(c).ReplaceQueueAndPlay(Songs("C", 4), 1);
        Assert.Equal(c, vm.PlaySource);
    }

    [AvaloniaFact]
    public void AddToQueue_AndPlayNext_LeaveTheSource_ButAnAddedSongIsNotFromIt()
    {
        var vm = CreateVm();
        var source = AlbumSource("Abbey Road");
        vm.WithSource(source).ReplaceQueueAndPlay(Songs("Abbey Road", 3), 0);
        var other = Songs("Other", 2);

        vm.AddToQueue(other[0]);
        vm.AddNext(other[1]);

        Assert.Equal(source, vm.PlaySource);
        Assert.True(vm.HasPlaySource);

        vm.PlayFromUpNextAt(0); // the Play Next song
        Assert.Equal(other[1].Id, vm.CurrentTrack!.Id);
        Assert.Equal(source, vm.PlaySource);
        Assert.False(vm.IsCurrentFromPlaySource);
        Assert.False(vm.HasPlaySource);
    }

    [AvaloniaFact]
    public void NewPageStart_ReplacesIt_AndAStartWithoutASource_ClearsIt()
    {
        var vm = CreateVm();
        vm.WithSource(AlbumSource("First")).PlayAll(Songs("First", 3));

        var playlist = new PlaySource(PlaySourceKind.Playlist, "Road Trip", Guid.NewGuid().ToString());
        vm.WithSource(playlist).PlayAll(Songs("Trip", 3));
        Assert.Equal(playlist, vm.PlaySource);

        vm.ReplaceQueueAndPlay(Songs("Palette", 1), 0); // command palette, radio, a dropped file…
        Assert.Null(vm.PlaySource);
        Assert.False(vm.HasPlaySource);
        Assert.Equal(string.Empty, vm.PlayingFromText);
    }

    [AvaloniaFact]
    public void AStartThatPlaysNothing_DoesNotLeaveItsSourceForTheNextStart()
    {
        var vm = CreateVm();
        vm.WithSource(AlbumSource("Empty")).PlayAll(new List<Track>());
        vm.ReplaceQueueAndPlay(Songs("Next", 2), 0);
        Assert.Null(vm.PlaySource);
    }

    [AvaloniaFact]
    public void ShowPlaySourceOff_HidesTheLabel_ButKeepsTheSource()
    {
        var vm = CreateVm();
        Assert.True(vm.ShowPlaySource); // the default
        vm.WithSource(AlbumSource("A")).PlayAll(Songs("A", 2));

        vm.ShowPlaySource = false;

        Assert.False(vm.HasPlaySource);
        Assert.NotNull(vm.PlaySource);
    }

    [AvaloniaFact]
    public void TitleClick_OpensTheAlbum_ByDefault_AndTheSourceWhenSetTo()
    {
        var vm = CreateVm();
        var opened = new List<string>();
        vm.SetViewAlbumAction(_ => opened.Add("album"));
        vm.SetOpenPlaySourceAction((s, _) => opened.Add("source:" + s.Name));
        vm.WithSource(new PlaySource(PlaySourceKind.Playlist, "Road Trip", Guid.NewGuid().ToString()))
            .PlayAll(Songs("Trip", 3));

        Assert.False(vm.TitleOpensPlaySource); // the default: today's behaviour
        Assert.Equal("View Album", vm.TitleClickTip);
        vm.OpenTitleTargetCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "album" }, opened);

        vm.TitleOpensPlaySource = true;
        Assert.Equal("Playing from Road Trip", vm.TitleClickTip);
        vm.OpenTitleTargetCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "album", "source:Road Trip" }, opened);

        // No source: the title still opens the album.
        vm.ReplaceQueueAndPlay(Songs("Loose", 1), 0);
        vm.OpenTitleTargetCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "album", "source:Road Trip", "album" }, opened);
    }

    [AvaloniaFact]
    public async Task TheSource_IsSavedWithTheQueue()
    {
        var persistence = new PersistenceService(_root);
        var library = new FakeLibraryService();
        var songs = Songs("Abbey Road", 3);
        library.TrackList.AddRange(songs);
        var source = AlbumSource("Abbey Road");

        var before = CreateVm(library, persistence);
        before.WithSource(source).ReplaceQueueAndPlay(songs, 1);
        await before.SaveQueueStateAsync();

        var after = CreateVm(library, persistence);
        await after.RestoreQueueStateAsync();

        Assert.Equal(source, after.PlaySource);
        Assert.True(after.HasPlaySource);
    }

    [AvaloniaFact]
    public async Task Settings_ReachThePlayer_WithTheirDefaults()
    {
        var settings = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new NoOpPlayHistory());
        var player = CreateVm();
        settings.SetPlayer(player);
        player.SetSettingsViewModel(settings);
        await settings.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(settings.ShowPlaySource);
        Assert.False(settings.TitleOpensPlaySource);
        Assert.True(player.ShowPlaySource);
        Assert.False(player.TitleOpensPlaySource);

        settings.ShowPlaySource = false;
        settings.TitleOpensPlaySource = true;
        Assert.False(player.ShowPlaySource);
        Assert.True(player.TitleOpensPlaySource);
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
