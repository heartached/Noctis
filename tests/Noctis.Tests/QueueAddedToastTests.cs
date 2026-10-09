using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: adding to the queue shows an "Added to Queue" / "Playing Next" pill.
/// PlayerViewModel.TracksQueued fires once per user add (one track or a whole batch) and
/// never for restore / shuffle / reorder; the toast text, the hold and the coalescing of
/// rapid repeats live in QueueToastViewModel.
/// </summary>
public class QueueAddedToastTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static Track Trk(string name) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = $"C:/t/{name}.mp3",
        Duration = TimeSpan.FromMinutes(3),
    };

    private static (PlayerViewModel vm, List<QueueAddedEventArgs> raised) CreateVm(FakeLibraryService? lib = null, IPersistenceService? persistence = null)
    {
        var vm = new PlayerViewModel(new FakeAudioPlayer(), lib ?? new FakeLibraryService(),
            persistence ?? new TestPersistenceService(), new FakeAnimatedCoverService());
        var raised = new List<QueueAddedEventArgs>();
        vm.TracksQueued += (_, e) => raised.Add(e);
        return (vm, raised);
    }

    // ── PlayerViewModel.TracksQueued ──

    [Fact]
    public void AddToQueue_RaisesOnce_ForThatTrack()
    {
        var (vm, raised) = CreateVm();
        var a = Trk("a");

        vm.AddToQueue(a);

        var e = Assert.Single(raised);
        Assert.Equal(1, e.Count);
        Assert.Same(a, e.FirstTrack);
        Assert.False(e.PlayNext);
    }

    [Fact]
    public void AddNext_RaisesOnce_AsPlayNext()
    {
        var (vm, raised) = CreateVm();

        vm.AddNext(Trk("a"));

        Assert.True(Assert.Single(raised).PlayNext);
    }

    [Fact]
    public void AddRangeToQueue_RaisesOnce_ForTheWholeBatch()
    {
        var (vm, raised) = CreateVm();
        var batch = Enumerable.Range(0, 5).Select(i => Trk($"t{i}")).ToList();

        vm.AddRangeToQueue(batch, "Album X");

        var e = Assert.Single(raised);
        Assert.Equal(5, e.Count);
        Assert.Equal("Album X", e.SourceName);
        Assert.False(e.PlayNext);
    }

    [Fact]
    public void AddNextRange_RaisesOnce_AndKeepsBatchOrderAheadOfTheQueue()
    {
        var (vm, raised) = CreateVm();
        var playing = Trk("playing");
        var queued = Trk("queued");
        vm.ReplaceQueueAndPlay(new[] { playing, queued }, 0);
        var batch = Enumerable.Range(0, 4).Select(i => Trk($"n{i}")).ToList();

        vm.AddNextRange(batch, "Playlist P");

        var e = Assert.Single(raised);
        Assert.Equal(4, e.Count);
        Assert.True(e.PlayNext);
        Assert.Same(batch[0], e.FirstTrack);
        Assert.Equal(batch.Select(t => t.Id).Append(queued.Id), vm.UpNext.Select(t => t.Id));
    }

    [Fact]
    public void EmptyBatch_RaisesNothing()
    {
        var (vm, raised) = CreateVm();

        vm.AddRangeToQueue(new List<Track>());
        vm.AddNextRange(new List<Track>());

        Assert.Empty(raised);
    }

    [Fact]
    public void AnnounceFalse_RaisesNothing_ButStillQueues()
    {
        var (vm, raised) = CreateVm();

        vm.AddToQueue(Trk("a"), announce: false);
        vm.AddNext(Trk("b"), announce: false);
        vm.AddRangeToQueue(new[] { Trk("c"), Trk("d") }, announce: false);
        vm.AddNextRange(new[] { Trk("e"), Trk("f") }, announce: false);

        Assert.Empty(raised);
        Assert.Equal(6, vm.UpNext.Count);
    }

    [Fact]
    public void QueueRebuilds_RaiseNothing()
    {
        var (vm, raised) = CreateVm();
        var tracks = Enumerable.Range(0, 6).Select(i => Trk($"t{i}")).ToList();

        vm.ReplaceQueueAndPlay(tracks, 0);
        vm.ToggleShuffleCommand.Execute(null);
        vm.ToggleShuffleCommand.Execute(null);
        vm.MoveInQueue(0, 2);
        vm.RemoveFromQueue(0);
        vm.NextCommand.Execute(null);
        vm.PreviousCommand.Execute(null);
        vm.ClearQueue();

        Assert.Empty(raised);
    }

    [AvaloniaFact]
    public async Task RestoreAtStartup_RaisesNothing()
    {
        var lib = new FakeLibraryService();
        var tracks = Enumerable.Range(0, 4).Select(i => Trk($"r{i}")).ToList();
        lib.TrackList.AddRange(tracks);
        var persistence = new PersistenceService(Path.Combine(_root, "data"));

        var (before, _) = CreateVm(lib, persistence);
        before.ReplaceQueueAndPlay(tracks, 0);
        await before.SaveQueueStateAsync();

        var (after, raised) = CreateVm(lib, persistence);
        await after.RestoreQueueStateAsync();

        Assert.NotEmpty(after.UpNext); // the restore did refill the queue...
        Assert.Empty(raised);          // ...without announcing it
    }

    // ── Callers: a bulk add is ONE confirmation ──

    [AvaloniaFact]
    public void AlbumsGrid_PlayNextAndAddToQueue_RaiseOnceEach_WithTheAlbumName()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var (player, raised) = CreateVm(lib, persistence);
        var sidebar = new SidebarViewModel(persistence, lib);
        var vm = new LibraryAlbumsViewModel(lib, player, sidebar, new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService()));
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = "Blue", Artist = "Artist",
            Tracks = new List<Track> { Trk("b1"), Trk("b2"), Trk("b3") },
        };

        vm.PlayNextCommand.Execute(album);
        vm.AddToQueueCommand.Execute(album);

        Assert.Equal(2, raised.Count);
        Assert.All(raised, e => Assert.Equal(("Blue", 3), (e.SourceName, e.Count)));
        Assert.True(raised[0].PlayNext);
        Assert.False(raised[1].PlayNext);
        // Play Next kept album order at the front; Add to Queue appended it after.
        var ids = album.Tracks.Select(t => t.Id).ToList();
        Assert.Equal(ids.Concat(ids), player.UpNext.Select(t => t.Id));
    }

    // ── QueueToastViewModel ──

    private sealed class ManualClock
    {
        public readonly List<(Action Action, Cancel Handle)> Pending = new();
        public sealed class Cancel : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }
        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var handle = new Cancel();
            Pending.Add((action, handle));
            return handle;
        }
        /// <summary>Fires every scheduled action that was not cancelled (a timer firing).</summary>
        public void Elapse()
        {
            foreach (var (action, handle) in Pending.ToList())
                if (!handle.Disposed) { handle.Disposed = true; action(); }
        }
    }

    private static QueueAddedEventArgs Args(int count, string title = "Song", string? source = null, bool next = false)
        => new(count, Trk(title), source, next);

    [Fact]
    public void Describe_OneTrack_NamesTheTrack()
    {
        var (headline, detail) = QueueToastViewModel.Describe(Args(1, "Midnight City"));
        Assert.Equal("Added to Queue", headline);
        Assert.Equal("Midnight City", detail);
    }

    [Fact]
    public void Describe_Batch_NamesTheAlbum_OrCountsTheSongs()
    {
        Assert.Equal("Hurry Up, We're Dreaming", QueueToastViewModel.Describe(Args(22, source: "Hurry Up, We're Dreaming")).Detail);
        Assert.Equal("3 songs", QueueToastViewModel.Describe(Args(3)).Detail);
    }

    [Fact]
    public void Describe_PlayNext_SaysPlayingNext()
    {
        Assert.Equal("Playing Next", QueueToastViewModel.Describe(Args(1, next: true)).Headline);
    }

    [AvaloniaFact]
    public void Toast_HidesAfterTheHold()
    {
        var clock = new ManualClock();
        var toast = new QueueToastViewModel(clock.Schedule);

        toast.Show(Args(1, "a"));
        Assert.True(toast.IsShown);

        clock.Elapse();
        Assert.False(toast.IsShown);
    }

    [AvaloniaFact]
    public void Toast_RapidRepeats_Coalesce_RestartHold_AndShowTheLatest()
    {
        var clock = new ManualClock();
        var toast = new QueueToastViewModel(clock.Schedule);
        var shownFlips = 0;
        toast.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(QueueToastViewModel.IsShown)) shownFlips++; };

        toast.Show(Args(1, "first"));
        toast.Show(Args(1, "second"));
        toast.Show(Args(4, source: "Album Z", next: true));

        Assert.Equal(1, shownFlips);                     // one pill: never re-hidden in between
        Assert.Equal(3, toast.ShowCount);                 // the view pulses on each repeat
        Assert.Equal("Playing Next", toast.Headline);
        Assert.Equal("Album Z", toast.Detail);
        Assert.Equal(2, clock.Pending.Count(p => p.Handle.Disposed)); // the older holds were cancelled
        Assert.Single(clock.Pending, p => !p.Handle.Disposed);

        clock.Elapse();                                   // only the latest hold ends it
        Assert.False(toast.IsShown);
    }

    [AvaloniaFact]
    public void Toast_FollowsThePlayer_OncePerUserAdd()
    {
        var clock = new ManualClock();
        var toast = new QueueToastViewModel(clock.Schedule);
        var (player, _) = CreateVm();
        toast.Attach(player);

        player.AddRangeToQueue(new[] { Trk("x"), Trk("y") }, "Mixtape");

        Assert.True(toast.IsShown);
        Assert.Equal(1, toast.ShowCount);
        Assert.Equal("Mixtape", toast.Detail);
    }

    // ── QueueToastView ──

    [AvaloniaFact]
    public void View_ShowsPill_DrawsTick_AndStaysOutOfHitTesting()
    {
        var clock = new ManualClock();
        var toast = new QueueToastViewModel(clock.Schedule);
        var view = new QueueToastView { DataContext = toast };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        try
        {
            Assert.False(view.IsPillOnScreen);
            Assert.False(view.IsHitTestVisible);
            Assert.False(view.Focusable);

            toast.Show(Args(1, "a"));
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.IsPillOnScreen);
            Assert.True(view.IsTickDrawing);

            toast.Show(Args(1, "b"));                     // repeat: pulse, no re-entry
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.IsPillOnScreen);
            Assert.True(view.IsTickDrawing);
        }
        finally { window.Close(); }
    }

    /// <summary>Drives the animation clock: the pill lands at rest, fully opaque, and the
    /// tick's stroke has drawn all the way in; after the hold it fades and leaves the tree.</summary>
    [AvaloniaFact]
    public async Task View_MotionRunsToRest_ThenCollapsesAfterTheHold()
    {
        var clock = new ManualClock();
        var toast = new QueueToastViewModel(clock.Schedule);
        var view = new QueueToastView { DataContext = toast };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        try
        {
            Assert.Equal(8, view.Tick.StrokeDashOffset);  // hidden before any add

            toast.Show(Args(1, "a"));
            await Frames(45);                             // ~750 ms of 16 ms frames

            Assert.Equal(1, view.Pill.Opacity, 3);
            Assert.Equal(0, view.Tick.StrokeDashOffset, 3);
            var m = view.Pill.RenderTransform!.Value;
            Assert.Equal(1, m.M11, 3);                    // scale back to 1
            Assert.Equal(0, m.M32, 3);                    // no vertical offset left

            clock.Elapse();                               // the 2 s hold ends
            await Frames(30);
            var deadline = Environment.TickCount64 + 3000;
            while (view.IsPillOnScreen && Environment.TickCount64 < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            Assert.False(view.IsPillOnScreen);
            Assert.Equal(0, view.Pill.Opacity, 3);
        }
        finally { window.Close(); }
    }

    private static async Task Frames(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Delay(16);
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
