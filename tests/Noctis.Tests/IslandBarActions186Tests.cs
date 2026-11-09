using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #186: optional Move to Trash and Quick Add to Playlist buttons in the player island
/// (Settings → Player → Player island buttons, both off by default).
/// </summary>
[Collection("MetadataServiceStatics")]
public class IslandBarActions186Tests : IDisposable
{
    private const double IslandBaseWidth = 536;
    private const double ExtraButtonWidth = 36;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", "186-" + Guid.NewGuid().ToString("N"));

    public IslandBarActions186Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Dir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Put(string dir, string name, string content = "x")
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static readonly Func<string, string, bool> SameVolume = (_, _) => true;
    private static readonly Func<string, string, bool> OtherVolume = (_, _) => false;

    // ── Move to the Trash folder ──

    [Fact]
    public void Move_KeepsTheName_AndTakesTheLyricSidecarsAlong()
    {
        var music = Dir("music");
        var trash = Dir("trash");
        var song = Put(music, "01 Song.mp3", "audio");
        Put(music, "01 Song.lrc", "lrc");
        Put(music, "01 Song.ELRC", "elrc"); // any letter case, as the lyrics page reads them
        Put(music, "01 Song.ttml", "ttml");
        var other = Put(music, "02 Other.lrc", "other");

        var result = TrashFolderMover.TryMove(song, trash, SameVolume);

        Assert.Equal(TrashFolderMover.Outcome.Moved, result.Outcome);
        Assert.Equal(Path.Combine(trash, "01 Song.mp3"), result.Destination);
        Assert.Equal("audio", File.ReadAllText(Path.Combine(trash, "01 Song.mp3")));
        Assert.Equal("lrc", File.ReadAllText(Path.Combine(trash, "01 Song.lrc")));
        Assert.Equal("elrc", File.ReadAllText(Path.Combine(trash, "01 Song.ELRC")));
        Assert.Equal("ttml", File.ReadAllText(Path.Combine(trash, "01 Song.ttml")));
        Assert.Equal(3, result.Sidecars.Count);
        Assert.False(File.Exists(song));
        Assert.Empty(Directory.GetFiles(music, "01 Song.*"));
        Assert.True(File.Exists(other)); // another song's sidecar stays
    }

    [Fact]
    public void Move_OnANameClash_AppendsANumber_AndNeverOverwrites()
    {
        var music = Dir("music");
        var trash = Dir("trash");
        var song = Put(music, "Song.flac", "new");
        Put(music, "Song.lrc", "new lrc");
        Put(trash, "Song.flac", "old");
        // "Song (2)" is free for the audio but its sidecar name is taken: skip to (3), so
        // the moved .lrc still pairs with the moved song.
        Put(trash, "Song (2).lrc", "old lrc 2");

        var result = TrashFolderMover.TryMove(song, trash, SameVolume);

        Assert.True(result.Moved);
        Assert.Equal(Path.Combine(trash, "Song (3).flac"), result.Destination);
        Assert.Equal("new", File.ReadAllText(Path.Combine(trash, "Song (3).flac")));
        Assert.Equal("new lrc", File.ReadAllText(Path.Combine(trash, "Song (3).lrc")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(trash, "Song.flac")));
        Assert.Equal("old lrc 2", File.ReadAllText(Path.Combine(trash, "Song (2).lrc")));
        Assert.False(File.Exists(Path.Combine(trash, "Song (2).flac")));
    }

    [Fact]
    public void FreeStem_CountsUpPastEveryTakenName()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("T", "a.mp3"), Path.Combine("T", "a (2).mp3"), Path.Combine("T", "a (3).ttml"),
        };
        Assert.Equal("a (4)", TrashFolderMover.FreeStem("T", "a", ".mp3", new[] { ".ttml" }, taken.Contains));
        Assert.Equal("a (3)", TrashFolderMover.FreeStem("T", "a", ".mp3", Array.Empty<string>(), taken.Contains));
        Assert.Equal("b", TrashFolderMover.FreeStem("T", "b", ".mp3", new[] { ".lrc" }, taken.Contains));
    }

    [Fact]
    public void Move_ToAnotherVolume_CopiesThenDeletesTheOriginal()
    {
        var music = Dir("music");
        var trash = Dir("trash");
        var song = Put(music, "Song.mp3", "audio bytes");
        Put(music, "Song.txt", "plain lyrics");

        var result = TrashFolderMover.TryMove(song, trash, OtherVolume);

        Assert.True(result.Moved);
        Assert.Equal("audio bytes", File.ReadAllText(Path.Combine(trash, "Song.mp3")));
        Assert.Equal("plain lyrics", File.ReadAllText(Path.Combine(trash, "Song.txt")));
        Assert.False(File.Exists(song));
        Assert.False(File.Exists(Path.Combine(music, "Song.txt")));
    }

    [Theory]
    [InlineData(@"C:\Music\a.mp3", @"C:\Trash\a.mp3", true)]
    [InlineData(@"C:\Music\a.mp3", @"c:\Trash\a.mp3", true)]
    [InlineData(@"C:\Music\a.mp3", @"D:\Trash\a.mp3", false)]
    [InlineData(@"\\nas\music\a.mp3", @"C:\Trash\a.mp3", false)]
    public void SameVolume_ComparesDriveRoots(string a, string b, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return; // drive letters are Windows-only
        Assert.Equal(expected, TrashFolderMover.SameVolume(a, b));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Move_WhileTheFileIsOpen_ReportsInUse_AndLeavesEverythingInPlace(bool sameVolume)
    {
        if (!OperatingSystem.IsWindows()) return; // only Windows refuses to move an open file
        var music = Dir("music");
        var trash = Dir("trash");
        var song = Put(music, "Song.mp3", "audio");

        using (new FileStream(song, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = TrashFolderMover.TryMove(song, trash, (_, _) => sameVolume);

            Assert.Equal(TrashFolderMover.Outcome.InUse, result.Outcome);
            Assert.True(File.Exists(song));
            // The cross-volume copy is taken back when the original can't be deleted.
            Assert.Empty(Directory.GetFiles(trash));
        }
    }

    [Fact]
    public async Task MoveWithRetries_WaitsForThePlayerToLetGo()
    {
        if (!OperatingSystem.IsWindows()) return;
        var music = Dir("music");
        var trash = Dir("trash");
        var song = Put(music, "Song.mp3", "audio");

        var handle = new FileStream(song, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Delay(150).ContinueWith(_ => handle.Dispose());

        var result = await TrashFolderMover.MoveWithRetriesAsync(song, trash, new[] { 0, 400, 800 }, SameVolume);
        await release;

        Assert.True(result.Moved);
        Assert.True(File.Exists(Path.Combine(trash, "Song.mp3")));
        Assert.False(File.Exists(song));
    }

    [Fact]
    public async Task MoveWithRetries_GivesUpWhileStillHeld()
    {
        if (!OperatingSystem.IsWindows()) return;
        var music = Dir("music");
        var trash = Dir("trash");
        var song = Put(music, "Song.mp3", "audio");

        using var handle = new FileStream(song, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await TrashFolderMover.MoveWithRetriesAsync(song, trash, new[] { 0, 10, 10 }, SameVolume);

        Assert.Equal(TrashFolderMover.Outcome.InUse, result.Outcome);
        Assert.True(File.Exists(song));
    }

    [Fact]
    public void Move_ToAFolderThatIsGone_Fails_AndTheFolderIsNotUsable()
    {
        var music = Dir("music");
        var song = Put(music, "Song.mp3");
        var gone = Path.Combine(_root, "unplugged");

        Assert.False(TrashFolderMover.IsUsable(gone));
        Assert.False(TrashFolderMover.IsUsable(""));
        Assert.False(TrashFolderMover.IsUsable(null));
        Assert.Equal(TrashFolderMover.Outcome.Failed, TrashFolderMover.TryMove(song, gone, SameVolume).Outcome);
        Assert.True(File.Exists(song));
        Assert.False(Directory.Exists(gone)); // never created behind the user's back
    }

    [Fact]
    public void Move_OfASongAlreadyInTheFolder_LeavesItBe()
    {
        var trash = Dir("trash");
        var song = Put(trash, "Song.mp3");

        var result = TrashFolderMover.TryMove(song, trash + Path.DirectorySeparatorChar, SameVolume);

        Assert.True(result.Moved);
        Assert.Equal(song, result.Destination);
        Assert.Equal(new[] { song }, Directory.GetFiles(trash));
    }

    [Fact]
    public void Move_OfAMissingFile_Fails()
        => Assert.Equal(TrashFolderMover.Outcome.Failed,
            TrashFolderMover.TryMove(Path.Combine(_root, "nope.mp3"), Dir("trash"), SameVolume).Outcome);

    // ── The Move to Trash button ──

    private static Track Song(string title, SourceType source = SourceType.Local) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = "A",
        FilePath = source == SourceType.Navidrome ? $"http://server/rest/stream?id={title}" : TestPaths.Primary("m", $"{title}.mp3"),
        SourceType = source,
        Duration = TimeSpan.FromMinutes(3),
    };

    private sealed class Rig
    {
        public readonly FakeLibraryService Library = new();
        public readonly PlayerViewModel Player;
        public readonly List<PlayerToast> Toasts = new();
        public readonly List<Track> Recycled = new();
        public readonly List<(string File, string Folder)> Moves = new();
        public bool RecycleSucceeds = true;
        public TrashFolderMover.Outcome MoveOutcome = TrashFolderMover.Outcome.Moved;

        public Rig()
        {
            Player = new PlayerViewModel(new FakeAudioPlayer(), Library, new TestPersistenceService(), new FakeAnimatedCoverService());
            Player.ToastRequested += (_, t) => Toasts.Add(t);
            Player.RecycleTrackFile = t => { Recycled.Add(t); return Task.FromResult(RecycleSucceeds); };
            Player.MoveToTrashFolder = (file, folder) =>
            {
                Moves.Add((file, folder));
                return Task.FromResult(new TrashFolderMover.Result(MoveOutcome,
                    MoveOutcome == TrashFolderMover.Outcome.Moved ? Path.Combine(folder, Path.GetFileName(file)) : null,
                    Array.Empty<string>()));
            };
        }

        public (Track First, Track Second) PlayTwo()
        {
            var a = Song("First");
            var b = Song("Second");
            Library.TrackList.AddRange(new[] { a, b });
            Player.ReplaceQueueAndPlay(new[] { a, b }, 0);
            return (a, b);
        }
    }

    private static string BinName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    [AvaloniaFact]
    public async Task Trash_WithNoFolderSet_UsesTheRecycleBin_SkipsToTheNextSong_AndDropsItFromTheLibrary()
    {
        var rig = new Rig();
        var (first, second) = rig.PlayTwo();

        await rig.Player.TrashCurrentTrackCommand.ExecuteAsync(null);

        Assert.Equal(new[] { first }, rig.Recycled);
        Assert.Empty(rig.Moves);
        Assert.Equal(new[] { first.Id }, rig.Library.RemovedTrackIds);
        Assert.Same(second, rig.Player.CurrentTrack);
        var toast = Assert.Single(rig.Toasts);
        Assert.Equal($"Moved to {BinName}", toast.Headline);
        Assert.Equal("First", toast.Detail);
    }

    [AvaloniaFact]
    public async Task Trash_WithAFolderThatIsGone_FallsBackToTheRecycleBin()
    {
        var rig = new Rig();
        var (first, _) = rig.PlayTwo();
        rig.Player.IslandTrashFolder = Path.Combine(_root, "unplugged");

        await rig.Player.TrashCurrentTrackCommand.ExecuteAsync(null);

        Assert.Equal(new[] { first }, rig.Recycled);
        Assert.Empty(rig.Moves);
        Assert.Equal($"Moved to {BinName}", Assert.Single(rig.Toasts).Headline);
    }

    [AvaloniaFact]
    public async Task Trash_WithAFolderSet_MovesTheFileThere_AndNamesItInThePill()
    {
        var rig = new Rig();
        var (first, second) = rig.PlayTwo();
        var trash = Dir("Keep or Toss");
        rig.Player.IslandTrashFolder = trash;
        Assert.Equal("Move to Keep or Toss", rig.Player.TrashButtonTip);

        await rig.Player.TrashCurrentTrackCommand.ExecuteAsync(null);

        Assert.Equal(new[] { (first.FilePath, trash) }, rig.Moves);
        Assert.Empty(rig.Recycled);
        Assert.Equal(new[] { first.Id }, rig.Library.RemovedTrackIds);
        Assert.Same(second, rig.Player.CurrentTrack);
        var toast = Assert.Single(rig.Toasts);
        Assert.Equal("Moved to Keep or Toss", toast.Headline);
        Assert.Equal("First", toast.Detail);
    }

    [AvaloniaFact]
    public async Task Trash_ThatCannotMoveTheFile_KeepsTheSongInTheLibrary_AndSaysSo()
    {
        var rig = new Rig { MoveOutcome = TrashFolderMover.Outcome.InUse };
        rig.PlayTwo();
        rig.Player.IslandTrashFolder = Dir("trash");

        await rig.Player.TrashCurrentTrackCommand.ExecuteAsync(null);

        Assert.Empty(rig.Library.RemovedTrackIds);
        Assert.Equal("Couldn't move to trash", Assert.Single(rig.Toasts).Headline);

        // Same for a Recycle Bin that refused (No at the permanent-delete prompt).
        var binRig = new Rig { RecycleSucceeds = false };
        binRig.PlayTwo();
        await binRig.Player.TrashCurrentTrackCommand.ExecuteAsync(null);
        Assert.Empty(binRig.Library.RemovedTrackIds);
        Assert.Equal($"Couldn't move to {BinName}", Assert.Single(binRig.Toasts).Headline);
    }

    [AvaloniaFact]
    public void Trash_IsDisabled_ForStreamsAndWithNothingPlaying()
    {
        var rig = new Rig();
        Assert.False(rig.Player.TrashCurrentTrackCommand.CanExecute(null));

        var stream = Song("Streamed", SourceType.Navidrome);
        rig.Library.TrackList.Add(stream);
        rig.Player.ReplaceQueueAndPlay(new[] { stream }, 0);
        Assert.False(rig.Player.TrashCurrentTrackCommand.CanExecute(null));

        var local = Song("Local");
        rig.Library.TrackList.Add(local);
        rig.Player.ReplaceQueueAndPlay(new[] { local }, 0);
        Assert.True(rig.Player.TrashCurrentTrackCommand.CanExecute(null));

        Assert.False(PlayerViewModel.CanTrash(new Track { FilePath = "cdda:///D:/track01", SourceType = SourceType.AudioCd }));
        Assert.False(PlayerViewModel.CanTrash(new Track { FilePath = "content://media/1", SourceType = SourceType.Local }));
    }

    // ── Quick playlists ──

    private static Playlist Pl(string name, bool smart = false) => new() { Name = name, IsSmartPlaylist = smart };

    [Fact]
    public void Resolve_FollowsRenames_DropsDeletedAndSmartPlaylists_AndKeepsSlotOrder()
    {
        var mix = Pl("Mix");
        var keep = Pl("Keepers");
        var smart = Pl("Top 25", smart: true);
        var deletedId = Guid.NewGuid();
        var playlists = new List<Playlist> { mix, keep, smart };

        var resolved = QuickPlaylists.Resolve(new[] { keep.Id, deletedId, mix.Id }, playlists);
        Assert.Equal(new[] { keep, mix }, resolved);

        keep.Name = "Keepers 2026"; // renamed: same id, the pop-up reads the live name
        Assert.Equal("Keepers 2026", QuickPlaylists.Resolve(new[] { keep.Id }, playlists)[0].Name);

        playlists.Remove(mix); // deleted: gone from the pop-up
        Assert.Equal(new[] { keep }, QuickPlaylists.Resolve(new[] { keep.Id, mix.Id }, playlists));

        Assert.Empty(QuickPlaylists.Resolve(new[] { smart.Id, Guid.Empty }, playlists));
        Assert.Equal(new[] { keep }, QuickPlaylists.Resolve(new[] { keep.Id, keep.Id }, playlists));
        Assert.Empty(QuickPlaylists.Resolve(null, playlists));
        Assert.Empty(QuickPlaylists.Resolve(new[] { keep.Id }, null));
    }

    [Fact]
    public void Resolve_ReadsOnlyThreeSlots_AndSlotsPadsOrCuts()
    {
        var p = Enumerable.Range(0, 4).Select(i => Pl($"P{i}")).ToList();
        Assert.Equal(p.Take(3), QuickPlaylists.Resolve(p.Select(x => x.Id), p));

        Assert.Equal(new[] { Guid.Empty, Guid.Empty, Guid.Empty }, QuickPlaylists.Slots(null));
        Assert.Equal(new[] { p[0].Id, Guid.Empty, Guid.Empty }, QuickPlaylists.Slots(new[] { p[0].Id }));
        Assert.Equal(p.Take(3).Select(x => x.Id), QuickPlaylists.Slots(p.Select(x => x.Id)));
    }

    [AvaloniaFact]
    public async Task QuickAdd_AddsTheSongOnce_AndThePillSaysAddedThenAlreadyIn()
    {
        var rig = new Rig();
        var (first, _) = rig.PlayTwo();
        var sidebar = new SidebarViewModel(new TestPersistenceService(), rig.Library);
        var keepers = await sidebar.CreatePlaylistFromTracksAsync("Keepers", Array.Empty<Track>());
        rig.Player.SetSidebar(sidebar);
        rig.Player.IslandQuickPlaylistIds = new[] { keepers.Id, Guid.Empty, Guid.Empty };
        Assert.Equal(new[] { keepers }, rig.Player.ResolvedQuickPlaylists);

        await rig.Player.AddCurrentTrackToQuickPlaylistCommand.ExecuteAsync(keepers);
        await rig.Player.AddCurrentTrackToQuickPlaylistCommand.ExecuteAsync(keepers);

        Assert.Equal(new[] { first.Id }, keepers.TrackIds);
        Assert.Equal(new[] { "Added to Keepers", "Already in Keepers" }, rig.Toasts.Select(t => t.Headline));
        Assert.All(rig.Toasts, t => Assert.Equal("First", t.Detail));
    }

    [AvaloniaFact]
    public async Task QuickAdd_ToAPlaylistDeletedSinceTheMenuOpened_DoesNothing()
    {
        var rig = new Rig();
        rig.PlayTwo();
        var sidebar = new SidebarViewModel(new TestPersistenceService(), rig.Library);
        rig.Player.SetSidebar(sidebar);

        await rig.Player.AddCurrentTrackToQuickPlaylistCommand.ExecuteAsync(Pl("Gone"));

        Assert.Empty(rig.Toasts);
    }

    // ── The island ──

    [AvaloniaFact]
    public void BothButtons_AreHiddenByDefault_AndEachAddsItsWidthWhenShown()
    {
        var s = new AppSettings();
        Assert.False(s.PlaybackBarShowTrash);
        Assert.False(s.PlaybackBarShowQuickPlaylist);
        Assert.Equal(string.Empty, s.PlaybackBarTrashFolder);
        Assert.Empty(s.PlaybackBarQuickPlaylistIds);

        var rig = new Rig();
        var bar = new PlaybackBarView { DataContext = rig.Player };
        var win = new Window { Width = 1000, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            var island = bar.FindControl<Border>("IslandBorder")!;
            var trash = bar.FindControl<Button>("TrashButton")!;
            var quick = bar.FindControl<Button>("QuickPlaylistButton")!;

            // Stock island (mini player button on by default): unchanged for everyone.
            Assert.False(trash.IsVisible);
            Assert.False(quick.IsVisible);
            Assert.Equal(IslandBaseWidth + ExtraButtonWidth, island.Width);

            rig.Player.IslandShowTrash = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(trash.IsEffectivelyVisible);
            Assert.Equal(IslandBaseWidth + 2 * ExtraButtonWidth, island.Width);

            rig.Player.IslandShowQuickPlaylist = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(quick.IsEffectivelyVisible);
            Assert.Equal(IslandBaseWidth + 3 * ExtraButtonWidth, island.Width);

            rig.Player.IslandShowTrash = false;
            rig.Player.IslandShowQuickPlaylist = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(IslandBaseWidth + ExtraButtonWidth, island.Width);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public void Buttons_SayWhatTheyDo_InTheirTooltipAndAccessibleName()
    {
        var rig = new Rig();
        rig.Player.IslandShowTrash = true;
        rig.Player.IslandShowQuickPlaylist = true;
        var bar = new PlaybackBarView { DataContext = rig.Player };
        var win = new Window { Width = 1000, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            var trash = bar.FindControl<Button>("TrashButton")!;
            var quick = bar.FindControl<Button>("QuickPlaylistButton")!;

            Assert.Equal($"Move to {BinName}", ToolTip.GetTip(trash));
            Assert.Equal($"Move to {BinName}", AutomationProperties.GetName(trash));
            Assert.Equal("Quick Add to Playlist", ToolTip.GetTip(quick));
            Assert.Equal("Quick Add to Playlist", AutomationProperties.GetName(quick));
            // Nothing playing: neither can act.
            Assert.False(trash.IsEffectivelyEnabled);
            Assert.False(quick.IsEffectivelyEnabled);

            rig.Player.IslandTrashFolder = Dir("Sort Later");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Move to Sort Later", ToolTip.GetTip(trash));
            Assert.Equal("Move to Sort Later", AutomationProperties.GetName(trash));

            rig.PlayTwo();
            Dispatcher.UIThread.RunJobs();
            Assert.True(trash.IsEffectivelyEnabled);
            Assert.True(quick.IsEffectivelyEnabled);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task QuickPlaylistMenu_ListsThePickedPlaylists_OrOffersTheDialogAndThePickers()
    {
        var rig = new Rig();
        rig.PlayTwo();
        var sidebar = new SidebarViewModel(new TestPersistenceService(), rig.Library);
        var a = await sidebar.CreatePlaylistFromTracksAsync("Chill", Array.Empty<Track>());
        var b = await sidebar.CreatePlaylistFromTracksAsync("Gym", Array.Empty<Track>());
        rig.Player.SetSidebar(sidebar);
        var host = new Border();

        // None picked: the full Add to Playlist dialog, and the way to the pickers.
        var empty = PlaybackBarView.BuildQuickPlaylistItems(host, rig.Player);
        var rows = empty.OfType<MenuItem>().ToList();
        Assert.Equal(new[] { "Add to Playlist", "Choose Quick Playlists…" }, rows.Select(r => (string)r.Header!));
        Assert.Same(rig.Player.AddCurrentTrackToNewPlaylistCommand, rows[0].Command);
        Assert.Same(rig.Player.ChooseQuickPlaylistsCommand, rows[1].Command);

        var asked = 0;
        rig.Player.QuickPlaylistSettingsRequested += (_, _) => asked++;
        rows[1].Command!.Execute(null);
        Assert.Equal(1, asked);

        // Picked (slot 2 empty): one row each, in slot order, by the live name.
        rig.Player.IslandQuickPlaylistIds = new[] { b.Id, Guid.Empty, a.Id };
        await sidebar.RenamePlaylist(b.Id, "Gym 2026");
        var picked = PlaybackBarView.BuildQuickPlaylistItems(host, rig.Player).Cast<MenuItem>().ToList();
        Assert.Equal(new[] { "Gym 2026", "Chill" }, picked.Select(r => (string)r.Header!));
        Assert.All(picked, r => Assert.Same(rig.Player.AddCurrentTrackToQuickPlaylistCommand, r.Command));
        Assert.Equal(new object[] { b, a }, picked.Select(r => r.CommandParameter!));

        // Deleted since: gone.
        sidebar.Playlists.Remove(b);
        Assert.Equal(new[] { "Chill" },
            PlaybackBarView.BuildQuickPlaylistItems(host, rig.Player).Cast<MenuItem>().Select(r => (string)r.Header!));
    }

    private static void Pump(int ms = 60)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(2);
        } while (sw.ElapsedMilliseconds < ms);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Interface Size (GitHub #145): the buttons sit inside the window's scale host, and
    /// the quick playlist menu's popup takes the button's scale like every other menu. Also the
    /// menu really shows its rows: opened empty, a MenuFlyout never showed the rows its Opening
    /// handler added (presenter ItemCount 0), hence the hidden placeholder row in the XAML.</summary>
    [AvaloniaFact]
    public void QuickPlaylistMenu_ShowsItsRows_AndFollowsTheInterfaceSize()
    {
        UiScale.Install(); // App.Initialize does this at startup
        var rig = new Rig();
        rig.Player.IslandShowQuickPlaylist = true;
        rig.Player.IslandShowTrash = true;
        rig.PlayTwo();
        var bar = new PlaybackBarView { DataContext = rig.Player };
        var win = new Window { Width = 1600, Height = 1200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            var quick = bar.FindControl<Button>("QuickPlaylistButton")!;
            var trash = bar.FindControl<Button>("TrashButton")!;
            var menu = (MenuFlyout)quick.Flyout!;

            static double PopupScale(Avalonia.Visual inside)
                => inside.GetVisualAncestors().OfType<LayoutTransformControl>()
                       .FirstOrDefault(l => l is not Noctis.Controls.UiScaleHost)?.LayoutTransform?.Value.M11 ?? 1.0;

            foreach (var f in new[] { 1.0, 1.5 })
            {
                UiScale.SetFactor(f);
                Pump();
                Assert.Equal(f, UiScale.GetApplied(win));
                Assert.Equal(f, trash.TransformToVisual(win)!.Value.M11, 3);

                menu.ShowAt(quick);
                Pump();
                var row = menu.Items.OfType<MenuItem>().First();
                Assert.Equal("Add to Playlist", row.Header);
                Assert.True(row.IsAttachedToVisualTree());
                Assert.True(row.IsEffectivelyVisible);
                Assert.Equal(f, PopupScale(row), 3);
                menu.Hide();
                Pump(400); // let the close animation finish before the next open
            }
        }
        finally
        {
            UiScale.SetFactor(1.0); // process-wide
            win.Close();
        }
    }

    [AvaloniaFact]
    public void ThePill_ShowsAnyConfirmation()
    {
        var toast = new QueueToastViewModel((_, _) => new NoopDisposable());
        var rig = new Rig();
        toast.Attach(rig.Player);

        toast.Show("Moved to Trash", "Song");
        Assert.True(toast.IsShown);
        Assert.Equal("Moved to Trash", toast.Headline);
        Assert.Equal("Song", toast.Detail);
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }

    // ── Settings ──

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private SettingsViewModel CreateSettings() => new(
        new PersistenceService(Path.Combine(_root, "data")), new FakeLibraryService(), new NoOpPlayHistoryService());

    [AvaloniaFact]
    public async Task Settings_MirrorOntoThePlayer_PickersFollowRenamesAndDeletes()
    {
        var vm = CreateSettings();
        var rig = new Rig();
        var sidebar = new SidebarViewModel(new TestPersistenceService(), rig.Library);
        var chill = await sidebar.CreatePlaylistFromTracksAsync("Chill", Array.Empty<Track>());
        var smart = new Playlist { Name = "Smart", IsSmartPlaylist = true };
        sidebar.Playlists.Add(smart);
        rig.Player.SetSidebar(sidebar);
        vm.SetPlayer(rig.Player);

        vm.PlaybackBarShowTrash = true;
        vm.PlaybackBarShowQuickPlaylist = true; // refreshes the pickers
        var folder = Dir("bin");
        vm.PickTrashFolder = () => Task.FromResult<string?>(folder);
        await vm.BrowseTrashFolderCommand.ExecuteAsync(null);
        Assert.True(rig.Player.IslandShowTrash);
        Assert.True(rig.Player.IslandShowQuickPlaylist);
        Assert.Equal(folder, rig.Player.IslandTrashFolder);
        Assert.False(vm.ShowTrashFolderMissingHint);

        // None + manual playlists only.
        Assert.Equal(new[] { "None", "Chill" }, vm.QuickPlaylistOptions.Select(o => o.Label));
        Assert.Equal("None", vm.SelectedQuickPlaylist1!.Label);

        vm.SelectedQuickPlaylist2 = vm.QuickPlaylistOptions[1];
        Assert.Equal(new[] { Guid.Empty, chill.Id, Guid.Empty }, rig.Player.IslandQuickPlaylistIds);
        Assert.Equal(new[] { chill }, rig.Player.ResolvedQuickPlaylists);

        // A ComboBox nulls its selection while items are rebuilt: the pick stays.
        vm.SelectedQuickPlaylist2 = null;
        Assert.Equal(chill.Id, vm.QuickPlaylistSlots[1]);

        await sidebar.RenamePlaylist(chill.Id, "Chill Out");
        vm.RefreshQuickPlaylistOptions();
        Assert.Equal("Chill Out", vm.SelectedQuickPlaylist2!.Label);

        sidebar.Playlists.Remove(chill);
        vm.RefreshQuickPlaylistOptions();
        Assert.Equal("None", vm.SelectedQuickPlaylist2!.Label);

        vm.ClearTrashFolderCommand.Execute(null);
        Assert.Equal(string.Empty, rig.Player.IslandTrashFolder);

        vm.PlaybackBarTrashFolder = Path.Combine(_root, "unplugged");
        Assert.True(vm.ShowTrashFolderMissingHint);
    }

    [AvaloniaFact]
    public async Task Settings_SurviveSaveAndReload_AndResetTurnsThemOff()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.False(vm.PlaybackBarShowTrash);
        Assert.False(vm.PlaybackBarShowQuickPlaylist);

        var folder = Dir("bin");
        var id = Guid.NewGuid();
        vm.PlaybackBarShowTrash = true;
        vm.PlaybackBarShowQuickPlaylist = true;
        vm.PlaybackBarTrashFolder = folder;
        await vm.SaveAsync();

        // Slots go through the loader like a stored file would.
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var stored = await persistence.LoadSettingsAsync();
        stored.PlaybackBarQuickPlaylistIds = new List<Guid> { Guid.Empty, id };
        await persistence.SaveSettingsAsync(stored);

        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.True(reloaded.PlaybackBarShowTrash);
        Assert.True(reloaded.PlaybackBarShowQuickPlaylist);
        Assert.Equal(folder, reloaded.PlaybackBarTrashFolder);
        Assert.Equal(new[] { Guid.Empty, id, Guid.Empty }, reloaded.QuickPlaylistSlots);
        await reloaded.SaveAsync(); // a save writes all three slots back
        Assert.Equal(new[] { Guid.Empty, id, Guid.Empty }, reloaded.GetSettings().PlaybackBarQuickPlaylistIds);

        reloaded.ResetSettingsToDefaults(new AppSettings());
        Assert.False(reloaded.PlaybackBarShowTrash);
        Assert.False(reloaded.PlaybackBarShowQuickPlaylist);
        Assert.Equal(string.Empty, reloaded.PlaybackBarTrashFolder);
        Assert.Equal(new[] { Guid.Empty, Guid.Empty, Guid.Empty }, reloaded.QuickPlaylistSlots);
    }
}
