using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Noctis.Plugins;
using Noctis.Services.Plugins;
using Noctis.Plugins.AlbumTown.Town;
using Noctis.Plugins.AlbumTown.Views;
using Xunit;
using static Noctis.Tests.AlbumTownPluginTests;

namespace Noctis.Tests;

/// <summary>
/// Album Town, the Discord "Album Town Feedback" thread (Jafezy JD and Mistery, 11-02): snoozed
/// songs, New crowd, stuck pets, album artists, room names, and the features asked for there.
/// </summary>
[Collection("AlbumTown")]
public class AlbumTownFeedbackTests
{
    private static (AlbumTownView View, FakeTownHost Host) NewView(IEnumerable<TrackInfo> tracks, int crowd = 40, IEnumerable<PlaylistInfo>? playlists = null)
    {
        var host = new FakeTownHost { CrowdSize = crowd };
        host.Tracks.AddRange(tracks);
        if (playlists is not null) host.PlaylistList.AddRange(playlists);
        var view = new AlbumTownView(host, new Random(5));
        var town = AlbumGrouping.Group(host.Tracks, out var byFile, out var byTrack);
        view.SetTown(town, byFile, Array.Empty<string>());
        view.SetApartments(host.Tracks, host.PlaylistList, host.LoadApartments(), byTrack);
        return (view, host);
    }

    private static IEnumerable<TrackInfo> Albums(int count, int songs = 1)
    {
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid().ToString("D");
            for (var n = 0; n < songs; n++)
                yield return T($"Song {i}-{n}", album: "Album " + i, artist: "Artist " + i, plays: i % 9, albumId: id, art: null, number: n + 1, id: $"t{i}-{n}");
        }
    }

    // ── B1: snoozed songs stay out of the queue ──

    [Fact]
    public void Queue_LeavesOutSnoozedSongs_UnlessAllAreSnoozed()
    {
        var now = DateTimeOffset.UtcNow;
        var songs = new Dictionary<string, TrackInfo>
        {
            ["a"] = T("A", id: "a"),
            ["b"] = T("B", id: "b") with { SnoozedUntil = now.AddDays(10) },
            ["c"] = T("C", id: "c") with { SnoozedUntil = now.AddDays(-1) }, // the snooze ran out
        };
        TrackInfo? Find(string id) => songs.GetValueOrDefault(id);

        Assert.Equal(new[] { "a", "c", "x" }, TownQueue.WithoutSnoozed(new[] { "a", "b", "c", "x" }, Find, now));
        var none = new[] { "a", "c" };
        Assert.Same(none, TownQueue.WithoutSnoozed(none, Find, now));
        var all = new[] { "b" };
        Assert.Same(all, TownQueue.WithoutSnoozed(all, Find, now)); // a click on Play still plays it
    }

    [AvaloniaFact]
    public void Play_FromTheCardAndTheRoomMenus_SkipsSnoozedSongs()
    {
        var id = Guid.NewGuid().ToString("D");
        var tracks = new[]
        {
            T("One", albumId: id, number: 1, id: "s1"),
            T("Two", albumId: id, number: 2, id: "s2") with { SnoozedUntil = DateTimeOffset.UtcNow.AddDays(30) },
            T("Three", albumId: id, number: 3, id: "s3"),
        };
        var playlist = new PlaylistInfo("pl", "Mix", null, new[] { "s2", "s3" });
        var (view, host) = NewView(tracks, playlists: new[] { playlist });
        var album = Assert.Single(view.Town);

        view.ShowCard(album, CastReason.Random);
        view.PlayAlbum(album);
        Assert.Equal(new[] { "s1", "s3" }, Assert.Single(host.Played).TrackIds);

        var room = view.Building.TryAdd(RoomKind.Playlist, "pl", 0, 0)!;
        view.RoomMenu(room).Single(m => m.Label == "Play playlist").Run!();
        Assert.Equal(new[] { "s3" }, Assert.Single(host.PlayedTracks));
    }

    // ── B2: New crowd brings new residents ──

    [Fact]
    public void NewCrowd_PicksOthersThanTheLastCrowd_KeepingCalledAndPlaying()
    {
        var town = AlbumGrouping.Group(Albums(120).ToList());
        var first = PlazaCast.Choose(town, 40, new Random(1)).Select(m => m.Album.Key).ToList();
        var called = new[] { first[3] };
        var playing = town[100].Key;
        var next = PlazaCast.ChooseFresh(town, 40, new Random(2), first, playing, called);
        Assert.Equal(40, next.Count);
        Assert.Equal(called[0], next[0].Album.Key);
        Assert.Contains(next, m => m.Album.Key == playing && m.Reason == CastReason.NowPlaying);
        // Nobody else from the last crowd: 80 others live in town.
        Assert.DoesNotContain(next, m => m.Reason == CastReason.Random && first.Contains(m.Album.Key));

        // A small town: newcomers first, then the rest of the old crowd.
        var small = AlbumGrouping.Group(Albums(50).ToList());
        var before = PlazaCast.Choose(small, 40, new Random(1)).Select(m => m.Album.Key).ToList();
        var after = PlazaCast.ChooseFresh(small, 40, new Random(3), before);
        Assert.Equal(40, after.Count);
        Assert.Equal(40, after.Select(m => m.Album.Key).Distinct().Count());
        Assert.Equal(10, after.Count(m => !before.Contains(m.Album.Key)));
    }

    [AvaloniaFact]
    public void NewCrowdButton_ChangesTheCrowd_NotTheSameOrderAgain()
    {
        var (view, _) = NewView(Albums(150), crowd: 40);
        var before = view.Cast.Select(m => m.Album.Key).ToList();
        view.Recast(fresh: true);
        var after = view.Cast.Select(m => m.Album.Key).ToList();
        Assert.Empty(after.Intersect(before));
        view.Recast(fresh: true);
        Assert.Empty(view.Cast.Select(m => m.Album.Key).Intersect(after));
    }

    // ── B3: pets in a narrow room never stand stuck ──

    [Fact]
    public void Pets_InANarrowRoom_PassEachOther_AndReachWhereTheyWalk()
    {
        var grid = new ApartmentGrid();
        var room = grid.TryAdd(RoomKind.Playlist, "p", 3, 3)!;
        var map = ApartmentMap.Build(grid.Rooms);
        var plan = Enumerable.Range(0, 5).Select(i => new PetPlan(room, A("song:" + i), true, "x", "t" + i)).ToList();
        var life = new ApartmentLife(new Random(4)) { Chatty = false, Wanderlust = 0 };
        life.SetLayout(map, plan);
        var walking = new double[5];
        var longest = 0.0;
        var arrivals = 0;
        for (var f = 0; f < 30 * 120; f++)
        {
            life.Step(1f / 30);
            for (var i = 0; i < 5; i++)
            {
                if (life.Pets[i].Creature.State == CreatureState.Wander) walking[i] += 1.0 / 30;
                else if (walking[i] > 0) { arrivals++; walking[i] = 0; }
                longest = Math.Max(longest, walking[i]);
            }
        }
        // A walk across a one-cell room takes about two seconds.
        Assert.True(longest < 6, $"a pet walked {longest:0.0} s without arriving");
        Assert.True(arrivals > 50, $"only {arrivals} walks ended");
    }

    private static TownAlbum A(string key)
        => new(key, key, "Artist", 2020, "Pop", new[] { key }, "", null, 0, null, null, true, false);

    // ── B4: one artist per album ──

    [Fact]
    public void AlbumArtist_NamesTheAlbumAndItsSongs()
    {
        var id = Guid.NewGuid().ToString("D");
        TrackInfo Song(string title, string artist, string albumArtist, int n)
            => new TrackInfo(Guid.NewGuid().ToString("D"), title, artist, "Soundtrack", albumArtist, TimeSpan.FromMinutes(3), 2020, "Game", n, $"C:/m/{title}.mp3", false, 0, 0) { AlbumId = id };
        var tracks = new[]
        {
            Song("One", "Guest A", "Composer", 1),
            Song("Two", "Guest B", "", 2), // no Album Artist tag on this one
            Song("Three", "Guest B", "", 3),
        };
        var album = Assert.Single(AlbumGrouping.Group(tracks));
        Assert.Equal("Composer", album.Artist); // the tag wins over the songs' own artists
        Assert.Equal("Composer", AlbumGrouping.DisplayArtist(tracks[0]));
        Assert.Equal("Guest B", AlbumGrouping.DisplayArtist(tracks[1]));
    }

    // ── B5: room names fit, scroll, or (one cell) stay out ──

    [AvaloniaFact]
    public void RoomNames_Fit_ScrollWhenLong_AndOneCellRoomsHaveNone()
    {
        var theme = TownTheme.Dark;
        var cell = 96.0;
        var wide = new Size(cell * 3 - 6, cell - 6);
        Assert.Null(RoomPainter.Text(new RoomLook("Album", "Artist", Array.Empty<string>(), null), new Size(cell - 6, cell - 6), cell, theme, oneCell: true));

        var fits = RoomPainter.Text(new RoomLook("Album", "Artist", Array.Empty<string>(), null), wide, cell, theme, oneCell: false)!;
        Assert.False(fits.Scrolls);
        Assert.True(fits.Title.WidthIncludingTrailingWhitespace <= fits.Width);

        var longTitle = RoomPainter.Text(new RoomLook("Super Mario Galaxy Original Soundtrack Platinum Version", "Nintendo", Array.Empty<string>(), null),
            new Size(cell * 2 - 6, cell - 6), cell, theme, oneCell: false)!;
        Assert.True(longTitle.TitleScrolls);
        Assert.False(longTitle.SubScrolls);

        // The scroll rests at the start, moves left, and comes round to the start again.
        var w = 300.0;
        Assert.Equal(0, RoomPainter.MarqueeOffset(0.5, w));
        Assert.True(RoomPainter.MarqueeOffset(RoomPainter.MarqueePause + 2, w) > 40);
        var period = RoomPainter.MarqueePause + (w + RoomPainter.MarqueeGap) / RoomPainter.MarqueeSpeed;
        Assert.Equal(0, RoomPainter.MarqueeOffset(period + 0.3, w));
    }
    // ── F3/F4: edge handles fade in, any size ──

    private static (AlbumTownView View, Window Window, FakeTownHost Host) Shown(int albums = 8)
    {
        var (view, host) = NewView(Albums(albums, songs: 2));
        var window = new Window { Content = view, Width = 1600, Height = 1000 };
        window.Show();
        view.GetLogicalDescendants().OfType<Button>()
            .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Apartments")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        view.ApartmentsView.Step(1 / 60f);
        return (view, window, host);
    }

    [AvaloniaFact]
    public void RoomEdges_ShowHandlesOnHover_AndResizeToAnyShape()
    {
        var (view, window, _) = Shown();
        var canvas = view.ApartmentsView;
        var grid = view.Building;
        var room = grid.TryAdd(RoomKind.Album, view.Town[0].Key, 1, 1)!;
        canvas.Step(1 / 60f);

        // Hover: the handles fade in; away: they fade out.
        var rect = canvas.RoomRect(room.Cells);
        canvas.Move(new Point(rect.X + 10, rect.Y + rect.Height / 2));
        for (var i = 0; i < 20; i++) canvas.Step(1 / 60f);
        Assert.Equal(1, canvas.HandleOpacity, 3);
        canvas.Move(canvas.ToPx(10.5, 5.5));
        for (var i = 0; i < 40; i++) canvas.Step(1 / 60f);
        Assert.Equal(0, canvas.HandleOpacity, 3);

        // The right edge makes it wider only: three cells, a shape the old rooms could not have.
        var right = RoomPainter.Handle(rect.Size, canvas.Cell, RoomEdge.Right).Center + new Vector(rect.X, rect.Y);
        Assert.Equal(RoomEdge.Right, canvas.HandleAt(room, right));
        Assert.True(canvas.Press(right));
        canvas.Move(canvas.ToPx(4.2, 1.9));
        Assert.Equal((new Cells(1, 1, 3, 1), true), canvas.Ghost);
        canvas.Release(canvas.ToPx(4.2, 1.9));
        Assert.Equal(new Cells(1, 1, 3, 1), room.Cells);

        // The bottom edge makes it taller only.
        rect = canvas.RoomRect(room.Cells);
        var bottom = RoomPainter.Handle(rect.Size, canvas.Cell, RoomEdge.Bottom).Center + new Vector(rect.X, rect.Y);
        Assert.Equal(RoomEdge.Bottom, canvas.HandleAt(room, bottom));
        Assert.True(canvas.Press(bottom));
        canvas.Move(canvas.ToPx(9, 4.3));
        canvas.Release(canvas.ToPx(9, 4.3));
        Assert.Equal(new Cells(1, 1, 3, 3), room.Cells);
        window.Close();
    }

    // ── F5/F6: the building's size, Clear building ──

    [AvaloniaFact]
    public void BuildingSize_SlidesUpTo18By9_NeverCutsARoom_AndIsSaved()
    {
        var (view, window, host) = Shown();
        Assert.Equal((14, 7), (view.Building.Columns, view.Building.Rows));
        Assert.Equal((4, 18), (view.ColumnsSlider.Minimum, view.ColumnsSlider.Maximum));
        Assert.Equal((2, 9), (view.RowsSlider.Minimum, view.RowsSlider.Maximum));
        view.ColumnsSlider.Value = 18;
        view.RowsSlider.Value = 9;
        Assert.Equal((18, 9), (view.Building.Columns, view.Building.Rows));
        var room = view.Building.TryAdd(RoomKind.Album, view.Town[1].Key, 16, 8)!;
        Assert.NotNull(room);
        view.ColumnsSlider.Value = 5;
        view.RowsSlider.Value = 3;
        Assert.Equal((17, 9), (view.Building.Columns, view.Building.Rows)); // the room at (16, 8) stays inside
        Assert.Equal(17, view.ColumnsSlider.Value);
        view.SaveApartmentsNow();
        Assert.Equal((17, 9), (host.Layout!.Columns, host.Layout.Rows));
        var back = ApartmentGrid.FromLayout(host.Layout, (_, _) => true, out var dropped);
        Assert.Equal((17, 9, 0), (back.Columns, back.Rows, dropped));
        window.Close();
    }

    [AvaloniaFact]
    public void ClearBuilding_AsksOnce_ThenEmptiesTheBuilding()
    {
        var (view, window, _) = Shown();
        view.Building.TryAdd(RoomKind.Album, view.Town[0].Key, 0, 0);
        view.Building.TryAdd(RoomKind.Album, view.Town[1].Key, 2, 0);
        var reset = view.GetLogicalDescendants().OfType<Button>().Single(b => b.Content is TextBlock { Text: "Clear building" });
        Assert.True(reset.IsVisible);
        reset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(view.IsResetArmed);
        Assert.Equal(2, view.Building.Rooms.Count);
        reset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(view.Building.Rooms);
        Assert.Empty(view.Life.Pets);
        Assert.False(view.IsResetArmed);
        window.Close();
    }

    // ── F1/F2: full screen, the menu bar along the top or down the side, folded away ──

    [AvaloniaFact]
    public void MenuBar_GoesTopOrSide_FoldsAway_AndFullScreenToggles()
    {
        var (view, host) = NewView(Albums(12));
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.IsMenuVertical);
        Assert.Equal(Dock.Top, DockPanel.GetDock(view.MenuBar));
        Assert.Equal(VerticalAlignment.Top, view.MenuHandle.VerticalAlignment);

        view.MenuHandle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(view.IsMenuCollapsed);
        Assert.False(view.MenuBar.IsVisible);
        Assert.True(view.MenuHandle.IsVisible); // the triangle stays to bring it back
        view.MenuHandle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(view.MenuBar.IsVisible);

        host.MenuBar = AlbumTownView.MenuSide;
        host.RaiseSettings();
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.IsMenuVertical);
        Assert.Equal(Dock.Left, DockPanel.GetDock(view.MenuBar));
        Assert.Equal(HorizontalAlignment.Left, view.MenuHandle.HorizontalAlignment);
        window.UpdateLayout();
        Assert.True(view.MenuBar.Bounds.Height > view.MenuBar.Bounds.Width); // a side bar
        Assert.True(view.Plaza.Bounds.Width > 1000);

        var full = view.GetLogicalDescendants().OfType<Button>().Single(b => b.Content is TextBlock { Text: "Full screen" });
        full.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WindowState.FullScreen, window.WindowState);
        Assert.Equal("Exit full screen", ((TextBlock)full.Content!).Text);
        view.ToggleFullScreen();
        Assert.Equal(WindowState.Normal, window.WindowState);
        window.Close();
    }

    // ── F7: a called album walks in from an edge and says so; someone walks out ──

    [Fact]
    public void Called_WalksInFromAnEdge_Announces_AndMakesSomeoneLeave()
    {
        var world = new PlazaWorld(800, 500, new Random(8));
        var town = AlbumGrouping.Group(Albums(30).ToList());
        world.SetCast(town.Take(10).Select(a => new CastMember(a, CastReason.Random)).ToList());
        var edges = new HashSet<string>();
        for (var i = 0; i < 12; i++)
        {
            var c = world.Call(new CastMember(town[10 + i], CastReason.Called));
            var p = c.Position;
            Assert.True(p.X < 0 || p.X > world.Width || p.Y < 0 || p.Y > world.Height, $"started inside at {p}");
            edges.Add(p.X < 0 ? "left" : p.X > world.Width ? "right" : p.Y < 0 ? "top" : "bottom");
            Assert.StartsWith("Hi everyone!", c.Bubble);
        }
        Assert.True(edges.Count >= 3, string.Join(",", edges));
        Assert.True(world.SendAway(town[0].Key));
        Assert.Equal(1, world.LeavingCount);
        var count = world.Creatures.Count;
        for (var i = 0; i < 30 * 20; i++) world.Step(1f / 30);
        Assert.Equal(count - 1, world.Creatures.Count);
        Assert.DoesNotContain(world.Creatures, c => c.Album.Key == town[0].Key);
        // Everyone who walked in is inside now.
        Assert.All(world.Creatures, c => Assert.True(c.Position.X >= 0 && c.Position.X <= world.Width && c.Position.Y >= 0 && c.Position.Y <= world.Height));
    }

    [AvaloniaFact]
    public void CallAnAlbum_InAFullPlaza_SendsOneHome_AndOffersSuggestions()
    {
        var (view, _) = NewView(Albums(80), crowd: 24);
        Assert.Equal(24, view.Cast.Count);
        var newcomer = view.Town.First(a => view.Cast.All(m => m.Album.Key != a.Key));
        view.Call(newcomer);
        Assert.Equal(24, view.Cast.Count);
        Assert.Equal(1, view.Plaza.World.LeavingCount);
        var here = view.Cast.Select(m => m.Album.Key).ToHashSet();
        var offered = PlazaCast.Suggest(view.Town, here, 6);
        Assert.Equal(6, offered.Count);
        Assert.DoesNotContain(offered, a => here.Contains(a.Key));
    }

    // ── F8: ladders and stairs between rooms ──

    [Fact]
    public void StackedAndSteppedRooms_JoinByLaddersAndStairs_AndPetsClimbThem()
    {
        var grid = new ApartmentGrid();
        var high = grid.TryAdd(RoomKind.Album, "high", 0, 0)!;      // floor at 1
        var low = grid.TryAdd(RoomKind.Album, "low", 1, 0)!;        // 1x2 beside it: floor at 2
        grid.TryResize(low, 1, 2);
        var under = grid.TryAdd(RoomKind.Album, "under", 0, 1)!;   // below "high": a trap door
        var map = ApartmentMap.Build(grid.Rooms);
        Area Of(Room r) => map.AreasOf(r)[0];

        var stairs = map.Exits(Of(high)).Single(x => x.Kind == PortalKind.Stairs);
        Assert.Same(low, stairs.To.Room);
        Assert.Equal(3, stairs.Path.Count);
        Assert.Equal(1, stairs.Path[0].Y); // along the higher floor...
        Assert.Equal(2, stairs.Path[2].Y); // ...and down the wall to the lower one
        Assert.Contains(map.Exits(Of(low)), x => x.Kind == PortalKind.Stairs && ReferenceEquals(x.To.Room, high));
        Assert.Contains(map.Exits(Of(high)), x => x.Kind == PortalKind.TrapDoor && ReferenceEquals(x.To.Room, under));

        var plan = new[] { high, low, under }.Select(r => new PetPlan(r, A(r.Source), false, r.Source, null)).ToList();
        var life = new ApartmentLife(new Random(6)) { Chatty = false, Wanderlust = 1 };
        life.SetLayout(map, plan);
        var pet = life.Pets.Single(p => p.Home == high);
        var visited = new HashSet<string>();
        var climbed = false;
        for (var f = 0; f < 30 * 240; f++)
        {
            life.Step(1f / 30);
            visited.Add(pet.Area.Room.Source);
            climbed |= pet.Climbing && pet.Y > 1.1 && pet.Y < 1.9;
        }
        Assert.Contains("low", visited);
        Assert.Contains("under", visited);
        Assert.True(climbed);
    }

    // ── F9/F10: games, teases and lyric quotes ──

    [Fact]
    public void Meetings_AreSometimesGames_Teases_OrLyrics()
    {
        var random = new Random(11);
        var a = A("a");
        var b = A("b");
        var acts = new HashSet<ChatAct>();
        var topics = new HashSet<string>();
        for (var i = 0; i < 400; i++)
        {
            var chat = TalkLines.Meet(a, b, Now, random, _ => "I can see the stars from here tonight");
            acts.Add(chat.Act);
            topics.Add(chat.Topic);
            Assert.DoesNotContain('—', chat.Opener + chat.Reply);
            if (chat.Act == ChatAct.RockPaperScissors) Assert.StartsWith("Rock, paper, scissors...", chat.Opener);
        }
        Assert.Equal(4, acts.Count);
        Assert.Contains("tease", topics);
        Assert.Contains("lyric", topics);

        Assert.Null(TalkLines.PickLyric(new[] { "[Chorus]", "Oh", "(yeah yeah)" }, random));
        Assert.Equal("Walking down the empty street", TalkLines.PickLyric(new[] { "[Verse 1]", "Walking down the empty street" }, random));
    }

    [AvaloniaFact]
    public void Residents_QuoteTheirOwnLyrics_ReadInTheBackground()
    {
        var host = new FakeTownHost();
        host.Tracks.AddRange(Albums(3, songs: 2));
        host.Lyrics["t1-0"] = new[] { "[Intro]", "Every light in this town is mine" };
        var view = new AlbumTownView(host, new Random(1));
        var town = AlbumGrouping.Group(host.Tracks, out var byFile, out var byTrack);
        view.SetTown(town, byFile, Array.Empty<string>());
        var album = town.Single(x => x.TrackIds.Contains("t1-0"));
        Assert.Null(view.LyricFor(album)); // asked now, read off the UI thread
        var until = DateTime.UtcNow.AddSeconds(5);
        while (view.LyricsOf(album.Key) is null && DateTime.UtcNow < until) Dispatcher.UIThread.RunJobs();
        Assert.Equal("Every light in this town is mine", view.LyricFor(album));
        var said = Enumerable.Range(0, 20).Select(i => TalkLines.Hello(album, Now, new Random(i), view.LyricFor)).ToList();
        Assert.Contains("\"Every light in this town is mine\"", said);
    }
}

/// <summary>Plugin API 1.7: <see cref="ILibraryReader.GetLyricLines"/> (Album Town's quotes).</summary>
public class PluginLyricLinesApiTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public void OlderReaders_SayWhichApiIsMissing()
    {
        ILibraryReader old = new OldReader();
        Assert.Contains("1.7", Assert.Throws<NotSupportedException>(() => old.GetLyricLines("x")).Message);
    }

    private sealed class OldReader : ILibraryReader
    {
        public int TrackCount => 0;
        public IReadOnlyList<TrackInfo> Search(string query, int limit = 50) => Array.Empty<TrackInfo>();
    }

    [AvaloniaFact]
    public void GetLyricLines_ReadsTheSidecar_WithoutTimestamps()
    {
        const string id = "dev.test.lyriclines";
        var dir = Directory.CreateTempSubdirectory("noctis-lyriclines").FullName;
        try
        {
            var song = Path.Combine(dir, "song.mp3");
            File.WriteAllBytes(song, new byte[16]);
            File.WriteAllText(Path.Combine(dir, "song.lrc"), "[00:01.00]First line here\n[00:04.50]Second line here\n");
            var library = new FakeLibraryService();
            var withLyrics = new Noctis.Models.Track { Title = "s", Artist = "a", Album = "b", FilePath = song, Duration = TimeSpan.FromSeconds(60) };
            var without = new Noctis.Models.Track { Title = "n", Artist = "a", Album = "b", FilePath = Path.Combine(dir, "none.mp3"), Duration = TimeSpan.FromSeconds(60) };
            library.TrackList.AddRange(new[] { withLyrics, without });
            _box.Approve((id, new[] { "library.read" }));
            var host = _box.NewHost(null, library, appVersion: "1.6.6");
            var script = new ScriptedPlugin();
            _box.AddInProcess(host, script, PluginSandbox.Manifest(id: id, apiVersion: "1.4", minAppVersion: "1.6.6", permissions: new[] { "library.read" }));

            var reader = script.Host!.Library;
            Assert.Equal(new[] { "First line here", "Second line here" }, reader.GetLyricLines(withLyrics.Id.ToString("D")));
            Assert.Empty(reader.GetLyricLines(without.Id.ToString("D")));
            Assert.Empty(reader.GetLyricLines("not an id"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void GetLyricLines_SongMarkedNoLyrics_OrInstrumentalNotice_QuotesNothing()
    {
        const string id = "dev.test.lyriclines.none";
        var dir = Directory.CreateTempSubdirectory("noctis-lyriclines").FullName;
        try
        {
            var song = Path.Combine(dir, "song.mp3");
            File.WriteAllBytes(song, new byte[16]);
            File.WriteAllText(Path.Combine(dir, "song.lrc"), "[00:01.00]First line here\n");
            var library = new FakeLibraryService();
            // GitHub #162: marked as having no lyrics — whatever lies beside it is not shown.
            var marked = new Noctis.Models.Track { Title = "s", Artist = "a", Album = "b", FilePath = song, Duration = TimeSpan.FromSeconds(60), NoLyrics = true };
            // Lyrics that only say "instrumental, please enjoy" (NetEase/Kugou placeholder).
            var notice = new Noctis.Models.Track { Title = "n", Artist = "a", Album = "b", FilePath = Path.Combine(dir, "none.mp3"), Duration = TimeSpan.FromSeconds(60), Lyrics = "纯音乐，请欣赏" };
            library.TrackList.AddRange(new[] { marked, notice });
            _box.Approve((id, new[] { "library.read" }));
            var host = _box.NewHost(null, library, appVersion: "1.6.6");
            var script = new ScriptedPlugin();
            _box.AddInProcess(host, script, PluginSandbox.Manifest(id: id, apiVersion: "1.4", minAppVersion: "1.6.6", permissions: new[] { "library.read" }));

            var reader = script.Host!.Library;
            Assert.Empty(reader.GetLyricLines(marked.Id.ToString("D")));
            Assert.Empty(reader.GetLyricLines(notice.Id.ToString("D")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
