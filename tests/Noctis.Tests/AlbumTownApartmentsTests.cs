using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctis.Plugins;
using Noctis.Plugins.AlbumTown.Town;
using Noctis.Plugins.AlbumTown.Views;
using Xunit;
using static Noctis.Tests.AlbumTownPluginTests;

namespace Noctis.Tests;

/// <summary>
/// Album Town 1.2, the Apartments to Jafezy JD's design: the building grid (allowed sizes,
/// never overlapping, move and resize rules), saving and restoring it, playlist rooms (five
/// pets at most, chosen songs, divided into up to four rooms), doors between rooms that touch,
/// life in the rooms, the view's shelf, menu and picker, and the frame budget for 30 rooms and
/// about 50 pets.
/// </summary>
[Collection("AlbumTown")] // one at a time with AlbumTownPluginTests: the frame budgets count GCs, process-wide
public class AlbumTownApartmentsTests
{
    // ── The grid ──

    [Theory]
    [InlineData(1.2, 0.9, 1, 1)]
    [InlineData(2.3, 1.1, 2, 1)]
    [InlineData(0.8, 2.2, 1, 2)]
    [InlineData(1.9, 1.7, 2, 2)]
    [InlineData(3.4, 2.4, 4, 2)]
    [InlineData(2.1, 3.8, 2, 4)]
    [InlineData(9, 9, 4, 2)] // never past eight cells
    [InlineData(0.2, 0.2, 1, 1)]
    public void Shapes_SnapToOneTwoFourOrEightCells(double w, double h, int sw, int sh)
    {
        Assert.Equal((sw, sh), RoomShapes.Snap(w, h));
        foreach (var (x, y) in RoomShapes.All)
        {
            Assert.Contains(x * y, new[] { 1, 2, 4, 8 });
            Assert.True(x * y <= RoomShapes.MaxCells);
        }
        Assert.False(RoomShapes.IsValid(3, 1));
        Assert.False(RoomShapes.IsValid(4, 4));
        Assert.False(RoomShapes.IsValid(8, 1));
    }

    [Fact]
    public void Grid_AddMoveResize_FollowTheRules()
    {
        var grid = new ApartmentGrid(6, 4);
        var a = grid.TryAdd(RoomKind.Album, "a:1", 0, 0)!;
        Assert.Equal(new Cells(0, 0, 1, 1), a.Cells);
        Assert.Null(grid.TryAdd(RoomKind.Album, "a:2", 0, 0)); // taken
        Assert.Null(grid.TryAdd(RoomKind.Album, "a:2", 6, 0)); // outside
        var b = grid.TryAdd(RoomKind.Album, "a:2", 2, 0)!;

        // Resize: snaps, keeps the top-left, blocked by a neighbour or the edge.
        Assert.Equal((new Cells(0, 0, 2, 1), true), grid.ResizeTarget(a, 2.2, 1.1));
        Assert.Equal((new Cells(0, 0, 4, 2), false), grid.ResizeTarget(a, 3.8, 2.2)); // b is in the way
        Assert.False(grid.TryResize(a, 4, 2));
        Assert.False(grid.TryResize(a, 3, 1)); // not an allowed size
        Assert.True(grid.TryResize(a, 2, 2));
        Assert.Equal(new Cells(0, 0, 2, 2), a.Cells);

        // Move: snapped cells, blocked where it would overlap or leave the grid.
        Assert.False(grid.TryMove(b, 1, 1));
        Assert.False(grid.TryMove(b, 6, 0));
        Assert.True(grid.TryMove(b, 5, 3));
        Assert.Equal(new Cells(5, 3, 1, 1), b.Cells);
        Assert.False(grid.TryResize(b, 2, 1)); // past the edge

        // One room per album: dropping it again moves its room.
        Assert.Same(b, grid.TryAdd(RoomKind.Album, "a:2", 3, 0));
        Assert.Equal(new Cells(3, 0, 1, 1), b.Cells);
        Assert.Equal(2, grid.Rooms.Count);
        // The same id as a playlist is another room.
        Assert.NotNull(grid.TryAdd(RoomKind.Playlist, "a:2", 4, 0));

        Assert.True(grid.Remove(a));
        Assert.False(grid.Remove(a));
        Assert.Equal((0, 0), grid.FirstFree());
    }

    [Fact]
    public void Grid_NeverOverlaps_AfterThousandsOfRandomEdits()
    {
        var random = new Random(5);
        var grid = new ApartmentGrid();
        var changes = 0;
        grid.Changed += (_, _) => changes++;
        for (var i = 0; i < 5000; i++)
        {
            var rooms = grid.Rooms;
            switch (random.Next(5))
            {
                case 0:
                case 1:
                    grid.TryAdd(random.Next(2) == 0 ? RoomKind.Album : RoomKind.Playlist, "s" + random.Next(60), random.Next(-1, grid.Columns + 1), random.Next(-1, grid.Rows + 1));
                    break;
                case 2 when rooms.Count > 0:
                    grid.TryMove(rooms[random.Next(rooms.Count)], random.Next(-2, grid.Columns + 1), random.Next(-2, grid.Rows + 1));
                    break;
                case 3 when rooms.Count > 0:
                    var r = rooms[random.Next(rooms.Count)];
                    var (t, _) = grid.ResizeTarget(r, random.NextDouble() * 5, random.NextDouble() * 5);
                    grid.TryResize(r, random.Next(2) == 0 ? t.W : random.Next(1, 5), random.Next(2) == 0 ? t.H : random.Next(1, 5));
                    break;
                case 4 when rooms.Count > 0 && random.Next(4) == 0:
                    grid.Remove(rooms[random.Next(rooms.Count)]);
                    break;
            }
            AssertValid(grid);
        }
        Assert.True(changes > 500);
        Assert.True(grid.Rooms.Count > 10);
    }

    private static void AssertValid(ApartmentGrid grid)
    {
        var rooms = grid.Rooms;
        for (var i = 0; i < rooms.Count; i++)
        {
            var c = rooms[i].Cells;
            Assert.True(RoomShapes.IsValid(c.W, c.H), $"{c} is not an allowed size");
            Assert.True(grid.InBounds(c), $"{c} is outside");
            for (var j = i + 1; j < rooms.Count; j++)
            {
                Assert.False(c.Overlaps(rooms[j].Cells), $"{c} overlaps {rooms[j].Cells}");
                Assert.False(rooms[i].Kind == rooms[j].Kind && rooms[i].Source == rooms[j].Source, "two rooms for one source");
            }
        }
    }

    // ── Saving ──

    [Fact]
    public void Layout_RoundTripsThroughJson()
    {
        var grid = new ApartmentGrid();
        var album = grid.TryAdd(RoomKind.Album, "a:x", 0, 0)!;
        grid.TryResize(album, 2, 1);
        var list = grid.TryAdd(RoomKind.Playlist, "p1", 3, 2)!;
        grid.TryResize(list, 4, 2);
        grid.TrySplit(list, 3);
        grid.SetPicks(list, new[] { "t1", "t2", "t2", "", "t3" });

        var json = grid.ToLayout().ToJson();
        var back = ApartmentGrid.FromLayout(ApartmentLayout.FromJson(json), (_, _) => true, out var dropped);

        Assert.Equal(0, dropped);
        Assert.Equal(grid.Rooms.Select(r => (r.Id, r.Kind, r.Source, r.Cells, r.Splits, string.Join(",", r.Picks))),
            back.Rooms.Select(r => (r.Id, r.Kind, r.Source, r.Cells, r.Splits, string.Join(",", r.Picks))));
        Assert.Equal(new[] { "t1", "t2", "t3" }, back.Find(list.Id)!.Picks);
        Assert.Equal(json, back.ToLayout().ToJson());
        Assert.DoesNotContain('—', json);
    }

    [Fact]
    public void Layout_LeavesOutWhatIsGone_OrBroken_AndKeepsTheRest()
    {
        var layout = new ApartmentLayout
        {
            Rooms =
            {
                new SavedRoom { Id = "1", Kind = "album", Source = "a:kept", X = 0, Y = 0 },
                new SavedRoom { Id = "2", Kind = "album", Source = "a:deleted", X = 1, Y = 0 },
                new SavedRoom { Id = "3", Kind = "playlist", Source = "gone", X = 2, Y = 0 },
                new SavedRoom { Id = "4", Kind = "playlist", Source = "p", X = 3, Y = 0, W = 2, H = 2, Splits = 9, Picks = new() { "a", "b", "c", "d", "e", "f" } },
                new SavedRoom { Id = "5", Kind = "album", Source = "a:overlap", X = 4, Y = 1 }, // inside room 4
                new SavedRoom { Id = "6", Kind = "album", Source = "a:shape", X = 6, Y = 0, W = 3, H = 1 },
                new SavedRoom { Id = "7", Kind = "album", Source = "a:out", X = 40, Y = 0 },
                new SavedRoom { Id = "1", Kind = "album", Source = "a:kept", X = 9, Y = 0 }, // a second room for one album
                new SavedRoom { Id = "1", Kind = "album", Source = "a:other", X = 10, Y = 0 }, // repeated id
            },
        };
        var known = new HashSet<string> { "a:kept", "p", "a:overlap", "a:shape", "a:out", "a:other" };

        var grid = ApartmentGrid.FromLayout(layout, (_, s) => known.Contains(s), out var dropped);

        Assert.Equal(6, dropped);
        Assert.Equal(new[] { "a:kept", "p", "a:other" }, grid.Rooms.Select(r => r.Source));
        var p = grid.Rooms[1];
        Assert.Equal(RoomShapes.MaxSplits, p.Splits);
        Assert.Equal(PlaylistPicks.Max, p.Picks.Count);
        Assert.Equal(3, grid.Rooms.Select(r => r.Id).Distinct().Count());
        Assert.Empty(ApartmentGrid.FromLayout(ApartmentLayout.FromJson("not json"), (_, _) => true, out _).Rooms);
        Assert.Empty(ApartmentGrid.FromLayout(null, (_, _) => true, out _).Rooms);
    }

    // ── Playlist rooms ──

    [Fact]
    public void Picks_DefaultToTheFiveMostPlayed_AndNeverPassFive()
    {
        var plays = new Dictionary<string, int> { ["a"] = 3, ["b"] = 9, ["c"] = 9, ["d"] = 0, ["e"] = 1, ["f"] = 5, ["g"] = 2 };
        int Plays(string id) => plays.TryGetValue(id, out var n) ? n : -1;
        var tracks = new[] { "a", "b", "c", "d", "e", "f", "g", "gone" };

        Assert.Equal(new[] { "b", "c", "f", "a", "g" }, PlaylistPicks.Default(tracks, Plays)); // ties: playlist order

        var picks = new List<string>();
        foreach (var id in new[] { "a", "b", "c", "d", "e" }) Assert.True(PlaylistPicks.Toggle(picks, id));
        Assert.False(PlaylistPicks.Toggle(picks, "f")); // a sixth
        Assert.Equal(5, picks.Count);
        Assert.True(PlaylistPicks.Toggle(picks, "a")); // untick one...
        Assert.True(PlaylistPicks.Toggle(picks, "f")); // ...to swap it
        Assert.Equal(new[] { "b", "c", "d", "e", "f" }, picks);

        var room = new Room("r", RoomKind.Playlist, "p", new Cells(0, 0, 1, 1));
        room.Picks.AddRange(new[] { "d", "gone", "zzz" });
        Assert.Equal(new[] { "d" }, PlaylistPicks.Resolve(room, tracks, Plays)); // removed songs leave quietly
        room.Picks.Clear();
        room.Picks.AddRange(new[] { "gone" });
        Assert.Equal(PlaylistPicks.Default(tracks, Plays), PlaylistPicks.Resolve(room, tracks, Plays)); // none left: the default
        Assert.Empty(PlaylistPicks.Resolve(room, Array.Empty<string>(), Plays));
    }

    [Fact]
    public void Splits_DivideTheRoomExactly_UpToFour_OnlyForPlaylists()
    {
        foreach (var (w, h) in RoomShapes.All)
            for (var n = 1; n <= RoomShapes.MaxSplits; n++)
            {
                var parts = RoomShapes.SubRooms(w, h, n);
                Assert.Equal(n, parts.Count);
                Assert.Equal(w * h, parts.Sum(p => p.W * p.H), 6);
                foreach (var p in parts) Assert.True(p.X >= 0 && p.Y >= 0 && p.Right <= w + 1e-9 && p.Bottom <= h + 1e-9);
                for (var i = 0; i < n; i++)
                    for (var j = i + 1; j < n; j++)
                        Assert.False(parts[i].X < parts[j].Right - 1e-9 && parts[j].X < parts[i].Right - 1e-9 && parts[i].Y < parts[j].Bottom - 1e-9 && parts[j].Y < parts[i].Bottom - 1e-9);
            }

        var grid = new ApartmentGrid();
        var album = grid.TryAdd(RoomKind.Album, "a", 0, 0)!;
        var list = grid.TryAdd(RoomKind.Playlist, "p", 2, 0)!;
        Assert.False(grid.TrySplit(album, 2));
        Assert.Equal(1, album.Splits);
        Assert.False(grid.TrySplit(list, 5));
        Assert.False(grid.TrySplit(list, 0));
        Assert.True(grid.TrySplit(list, 4));
        Assert.Equal(4, ApartmentMap.Build(grid.Rooms).AreasOf(list).Count);
    }

    [Fact]
    public void DividedRooms_AreConnected_SoPetsCanWanderAmongThem()
    {
        foreach (var (w, h) in RoomShapes.All)
            for (var n = 1; n <= RoomShapes.MaxSplits; n++)
            {
                var grid = new ApartmentGrid();
                var room = grid.TryAdd(RoomKind.Playlist, "p", 0, 0)!;
                Assert.True(grid.TryResize(room, w, h));
                grid.TrySplit(room, n);
                var map = ApartmentMap.Build(grid.Rooms);
                var areas = map.AreasOf(room);
                var reached = new HashSet<Area> { areas[0] };
                var queue = new Queue<Area>(reached);
                while (queue.Count > 0)
                    foreach (var exit in map.Exits(queue.Dequeue()))
                        if (reached.Add(exit.To)) queue.Enqueue(exit.To);
                Assert.True(areas.Count == reached.Count, $"{w}x{h} in {n}: {reached.Count} of {areas.Count} reachable");
                Assert.All(map.Portals, p => Assert.False(p.BetweenRooms));
            }
    }

    // ── Doors ──

    [Fact]
    public void Doors_JoinRoomsThatTouch_OnTheFloorTheyShare()
    {
        var grid = new ApartmentGrid();
        var a = grid.TryAdd(RoomKind.Album, "a", 0, 1)!;
        var b = grid.TryAdd(RoomKind.Album, "b", 1, 1)!;           // right of a: a door
        var c = grid.TryAdd(RoomKind.Album, "c", 0, 0)!;           // on top of a: a trap door (not in v1)
        var d = grid.TryAdd(RoomKind.Album, "d", 6, 1)!;           // apart: nothing
        var p = grid.TryAdd(RoomKind.Playlist, "p", 2, 0)!;        // right of b, 2x2: floor at y = 2
        grid.TryResize(p, 2, 2);
        grid.TrySplit(p, 4);

        var map = ApartmentMap.Build(grid.Rooms);
        Area A(Room r, int part = 0) => map.AreasOf(r)[part];

        var ab = map.Exits(A(a)).Single(x => x.BetweenRooms);
        Assert.Same(A(b), ab.To);
        Assert.Equal(PortalKind.Door, ab.Kind);
        Assert.Equal(1, ab.Line);
        Assert.Equal(2, ab.FromPoint.Y);
        Assert.Contains(map.Exits(A(b)), x => ReferenceEquals(x.To, A(a))); // both ways

        // b (floor 2) meets the playlist's lower-left room (floor 2), not its upper one (floor 1).
        var bp = map.Exits(A(b)).Where(x => x.BetweenRooms && ReferenceEquals(x.To.Room, p)).ToList();
        Assert.Equal(new[] { 2 }, bp.Select(x => x.To.Part));

        // Stacked rooms: found as a trap door for later, never used in v1.
        var trap = map.Portals.Single(x => x.Kind == PortalKind.TrapDoor && ReferenceEquals(x.From.Room, c));
        Assert.Same(a, trap.To.Room);
        Assert.DoesNotContain(map.Exits(A(c)), x => x.Kind == PortalKind.TrapDoor);
        Assert.Empty(map.Exits(A(c)));
        Assert.Empty(map.Exits(A(d)));
        Assert.Equal(2, map.RoomDoors().Count()); // a|b and b|p
    }

    // ── Life ──

    private static TownAlbum Al(string key, string title = "Album", string artist = "Artist", int tracks = 8)
        => new(key, title, artist, 2020, "Pop", Enumerable.Range(0, tracks).Select(i => key + "#" + i).ToArray(), "", null, 3, Now.AddDays(-3), Now.AddYears(-1), false, false);

    [Fact]
    public void Pets_StayInTheirRooms_VisitNeighboursThroughDoors_ChatAndGoHome()
    {
        var grid = new ApartmentGrid();
        var rooms = Enumerable.Range(0, 4).Select(i => grid.TryAdd(RoomKind.Album, "a" + i, i, 2)!).ToList();
        var lone = grid.TryAdd(RoomKind.Album, "lone", 8, 5)!;
        var list = grid.TryAdd(RoomKind.Playlist, "p", 0, 4)!;
        grid.TryResize(list, 2, 2);
        grid.TrySplit(list, 4);
        var map = ApartmentMap.Build(grid.Rooms);
        var plan = rooms.Concat(new[] { lone }).Select(r => new PetPlan(r, Al(r.Source, "Album " + r.Source), false, r.Source, null)).ToList();
        for (var i = 0; i < 5; i++) plan.Add(new PetPlan(list, Al("song:" + i, "Song " + i, tracks: 1), true, "x", "t" + i));
        var life = new ApartmentLife(new Random(3)) { Wanderlust = 0.6 };
        life.SetLayout(map, plan);
        Assert.Equal(10, life.Pets.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 0 }, life.Pets.Where(p => p.IsSong).Select(p => p.Area.Part)); // spread over the rooms

        var visits = 0;
        var visitTalks = 0;
        var homecomings = 0;
        var songAreas = new HashSet<int>();
        var wasVisiting = life.Pets.ToDictionary(p => p, _ => false);
        for (var step = 0; step < 60 * 400; step++)
        {
            life.Step(1 / 60f);
            foreach (var p in life.Pets)
            {
                // Always in an area of its own room or a room next door.
                Assert.True(ReferenceEquals(p.Area.Room, p.Home) || map.Portals.Any(x => x.BetweenRooms && ReferenceEquals(x.From.Room, p.Home) && ReferenceEquals(x.To, p.Area)),
                    $"{p.Album.Title} wandered off to {p.Area.Room.Source}");
                var r = p.Area.Rect;
                if (p.Goal is null) Assert.InRange(p.X, r.X, r.Right);
                Assert.InRange(p.Y, r.Y, r.Bottom);
                if (p.Visiting && !wasVisiting[p]) visits++;
                if (!p.Visiting && wasVisiting[p]) homecomings++;
                if (p.Visiting && p.Creature.State == CreatureState.Talk) visitTalks++;
                wasVisiting[p] = p.Visiting;
                if (p.IsSong) songAreas.Add(p.Area.Part);
            }
        }
        Assert.True(visits > 5, $"{visits} visits");
        Assert.True(homecomings > 3, $"{homecomings} homecomings");
        Assert.True(visitTalks > 0, "visitors never said hello");
        Assert.Equal(4, songAreas.Count); // songs wandered all four rooms of their apartment
        var alone = life.Pets.Single(p => p.Home == lone);
        Assert.False(alone.Visiting); // no neighbours, no visits

        // A click waves; the album playing dances.
        var first = life.Pets[0];
        life.Wave(first);
        Assert.Equal(CreatureState.Wave, first.Creature.State);
        Assert.NotNull(first.Creature.Bubble);
        life.SetNowPlaying("a1", "t2", playing: true);
        Assert.Equal(new[] { "Album a1", "Song 2" }, life.Pets.Where(p => p.Creature.IsPlaying).Select(p => p.Album.Title));
        life.SetNowPlaying(null, null, playing: false);
        Assert.DoesNotContain(life.Pets, p => p.Creature.IsPlaying);
    }

    [Fact]
    public void MovingARoom_TakesItsPetAlong()
    {
        var grid = new ApartmentGrid();
        var room = grid.TryAdd(RoomKind.Album, "a", 0, 0)!;
        var life = new ApartmentLife(new Random(1));
        var plan = new[] { new PetPlan(room, Al("a"), false, "a", null) };
        life.SetLayout(ApartmentMap.Build(grid.Rooms), plan);
        var pet = life.Pets.Single();
        var before = (pet.X - room.Cells.X, pet.Y);
        grid.TryMove(room, 5, 3);
        life.SetLayout(ApartmentMap.Build(grid.Rooms), plan);
        Assert.Same(pet, life.Pets.Single());
        Assert.Equal(before.Item1, pet.X - 5, 3);
        Assert.Equal(before.Y + 3, pet.Y, 3);
        grid.Remove(room);
        life.SetLayout(ApartmentMap.Build(grid.Rooms), Array.Empty<PetPlan>());
        Assert.Empty(life.Pets);
    }

    // ── The view ──

    private sealed record Library(List<TrackInfo> Tracks, List<PlaylistInfo> Playlists);

    /// <summary><paramref name="albums"/> albums of 3 songs (genres alternate), and playlists of their songs.</summary>
    private static Library MakeLibrary(int albums, int playlists, List<string>? covers = null)
    {
        var tracks = new List<TrackInfo>();
        var genres = new[] { "Jazz Fusion", "Rock", "Pop", "" };
        for (var i = 0; i < albums; i++)
        {
            var id = Guid.NewGuid().ToString("D");
            for (var n = 0; n < 3; n++)
                tracks.Add(T($"Song {i}-{n}", album: $"Album {i}", artist: $"Artist {i % 9}", plays: (i * 7 + n * 3) % 40, albumId: id,
                    art: covers is null ? null : covers[i % covers.Count], genre: genres[i % genres.Length], number: n + 1, id: $"t{i}-{n}"));
        }
        var lists = new List<PlaylistInfo>();
        for (var p = 0; p < playlists; p++)
            lists.Add(new PlaylistInfo("pl" + p, "Mix " + p, null, tracks.Where((_, i) => i % (p + 2) == 0).Take(12).Select(t => t.Id).ToArray())
            {
                Color = "#4ECDC4",
            });
        return new Library(tracks, lists);
    }

    private static (AlbumTownView View, FakeTownHost Host) NewView(Library lib, ApartmentLayout? layout = null)
    {
        var host = new FakeTownHost();
        host.Tracks.AddRange(lib.Tracks);
        host.PlaylistList.AddRange(lib.Playlists);
        host.Layout = layout;
        var view = new AlbumTownView(host, new Random(9));
        var town = AlbumGrouping.Group(host.Tracks, out var byFile, out var byTrack);
        view.SetTown(town, byFile, Array.Empty<string>());
        view.SetApartments(host.Tracks, host.PlaylistList, host.LoadApartments(), byTrack);
        return (view, host);
    }

    [AvaloniaFact]
    public void Shelf_GroupsPlaylistsThenGenres_AndSearches()
    {
        var (view, _) = NewView(MakeLibrary(12, 2));
        var items = view.Shelf.Items;
        // Same-sized genres in name order, "Other" (no genre) last.
        Assert.Equal(new[] { "Playlists", "Jazz Fusion", "Pop", "Rock", "Other" }, items.Select(i => i.Group).Distinct());
        Assert.Equal(2, items.Count(i => i.Kind == RoomKind.Playlist));
        Assert.Equal(12, items.Count(i => i.Kind == RoomKind.Album));

        var found = ShelfStrip.Arrange(items.Where(i => i.Kind == RoomKind.Playlist), items.Where(i => i.Kind == RoomKind.Album), "rock artist 1");
        Assert.All(found, i => Assert.Equal("Rock", i.Group));
        Assert.Contains(found, i => i.Title == "Album 1");
        var lists = items.Where(i => i.Kind == RoomKind.Playlist).ToList();
        Assert.Contains("Mix 1", ShelfStrip.Arrange(lists, Array.Empty<ShelfItem>(), "mix 1").Select(i => i.Title));
        Assert.Equal(new[] { "Mix 0", "Mix 1" }, ShelfStrip.Arrange(lists, Array.Empty<ShelfItem>(), "MIX").Select(i => i.Title));
        Assert.Empty(ShelfStrip.Arrange(items.Where(i => i.Kind == RoomKind.Playlist), Array.Empty<ShelfItem>(), "nothing like it"));
    }

    [AvaloniaFact]
    public void DroppingFromTheShelf_BuildsARoom_ThatIsSavedAndComesBack()
    {
        var lib = MakeLibrary(10, 2);
        var (view, host) = NewView(lib);
        var window = new Window { Content = view, Width = 1600, Height = 1000 };
        window.Show();
        ClickTab(view, "Apartments");
        Dispatcher.UIThread.RunJobs();
        var canvas = view.ApartmentsView;
        canvas.Step(1 / 60f);

        var album = view.Shelf.Items.First(i => i.Kind == RoomKind.Album);
        var list = view.Shelf.Items.First(i => i.Kind == RoomKind.Playlist);
        canvas.BeginDrop(album.Kind, album.Source);
        var target = Center(canvas, 3, 2);
        canvas.UpdateDrop(target);
        Assert.Equal((new Cells(3, 2, 1, 1), true), canvas.Ghost);
        var room = canvas.EndDrop(target)!;
        Assert.Equal(new Cells(3, 2, 1, 1), room.Cells);
        Assert.False(canvas.IsDragging);

        // A drop onto a taken cell does nothing; outside the grid neither.
        canvas.BeginDrop(list.Kind, list.Source);
        Assert.Null(canvas.EndDrop(target));
        canvas.BeginDrop(list.Kind, list.Source);
        Assert.Null(canvas.EndDrop(new Point(-50, -50)));
        canvas.BeginDrop(list.Kind, list.Source);
        var plRoom = canvas.EndDrop(Center(canvas, 4, 2))!;

        // One pet in the album room, up to five song pets in the playlist room.
        Assert.Single(view.Life.Pets, p => p.Home == room);
        Assert.Equal(5, view.Life.Pets.Count(p => p.Home == plRoom && p.IsSong));
        Assert.Equal("2 rooms · 6 pets", StatusText(view));

        view.SaveApartmentsNow();
        Assert.Equal(2, host.Layout!.Rooms.Count);

        // Reload: the same building; a playlist deleted meanwhile moves out quietly.
        var (again, _) = NewView(lib, host.Layout);
        Assert.Equal(view.Building.Rooms.Select(r => (r.Id, r.Cells)), again.Building.Rooms.Select(r => (r.Id, r.Cells)));
        var (fewer, _) = NewView(new Library(lib.Tracks, new List<PlaylistInfo>()), host.Layout);
        Assert.Equal(new[] { room.Id }, fewer.Building.Rooms.Select(r => r.Id));
        // An empty library (not loaded yet) never saves over the building.
        var (empty, emptyHost) = NewView(new Library(new List<TrackInfo>(), new List<PlaylistInfo>()), host.Layout);
        empty.SaveApartmentsNow();
        Assert.Equal(2, emptyHost.Layout!.Rooms.Count);
        Assert.Equal(0, emptyHost.LayoutSaves);
        window.Close();
    }

    [AvaloniaFact]
    public void DraggingARoom_MovesIt_ItsCornerResizesIt_AndOverlapsAreRefused()
    {
        var (view, _) = NewView(MakeLibrary(6, 0));
        var window = new Window { Content = view, Width = 1600, Height = 1000 };
        window.Show();
        ClickTab(view, "Apartments");
        Dispatcher.UIThread.RunJobs();
        var canvas = view.ApartmentsView;
        canvas.Step(1 / 60f);
        var grid = view.Building;
        var keys = view.Town.Select(a => a.Key).ToList();
        var a = grid.TryAdd(RoomKind.Album, keys[0], 1, 1)!;
        var b = grid.TryAdd(RoomKind.Album, keys[1], 4, 1)!;

        // Move a by dragging its body (not a pet, not the button or the grip).
        var rect = canvas.RoomRect(a.Cells);
        var grab = new Point(rect.X + rect.Width * 0.12, rect.Y + rect.Height * 0.5);
        canvas.Life.Pets.ToList().ForEach(p => p.X = p.Area.Rect.Right - 0.1);
        Assert.True(canvas.Press(grab));
        canvas.Move(grab + new Vector(canvas.Cell * 2, 0));
        Assert.True(canvas.IsDragging);
        Assert.Equal((new Cells(3, 1, 1, 1), true), canvas.Ghost);
        canvas.Move(grab + new Vector(canvas.Cell * 3, 0)); // onto b
        Assert.False(canvas.Ghost.Fits);
        canvas.Release(grab + new Vector(canvas.Cell * 3, 0));
        Assert.Equal(new Cells(1, 1, 1, 1), a.Cells); // refused
        Assert.True(canvas.Press(grab));
        canvas.Move(grab + new Vector(canvas.Cell * 2, canvas.Cell));
        canvas.Release(grab + new Vector(canvas.Cell * 2, canvas.Cell));
        Assert.Equal(new Cells(3, 2, 1, 1), a.Cells);

        // Resize a from its corner grip: 2x2 fits, 4x2 would reach b's column... b sits at (4,1).
        rect = canvas.RoomRect(a.Cells);
        var grip = new Point(rect.Right - 4, rect.Bottom - 4);
        Assert.True(canvas.Press(grip));
        canvas.Move(canvas.ToPx(5.1, 4.1));
        Assert.Equal((new Cells(3, 2, 2, 2), true), canvas.Ghost);
        canvas.Release(canvas.ToPx(5.1, 4.1));
        Assert.Equal(new Cells(3, 2, 2, 2), a.Cells);
        Assert.True(canvas.Press(new Point(canvas.RoomRect(a.Cells).Right - 4, canvas.RoomRect(a.Cells).Bottom - 4)));
        canvas.Move(canvas.ToPx(7, 2.5));
        canvas.Release(canvas.ToPx(7, 2.5));
        Assert.Equal(new Cells(3, 2, 4, 2), a.Cells); // 4x2 fits beside b (b is on row 1)
        window.Close();
    }

    [AvaloniaFact]
    public void RoomMenus_PlayRemoveSplit_AndThePickerKeepsFive()
    {
        var lib = MakeLibrary(20, 1);
        var (view, host) = NewView(lib);
        var grid = view.Building;
        var album = view.Town.First(a => a.AlbumId.Length > 0);
        var aRoom = grid.TryAdd(RoomKind.Album, album.Key, 0, 0)!;
        var playlist = lib.Playlists[0];
        var pRoom = grid.TryAdd(RoomKind.Playlist, playlist.Id, 2, 0)!;
        grid.TryResize(pRoom, 2, 2);

        var albumMenu = view.RoomMenu(aRoom);
        Assert.Equal(new[] { "Play album", "Go to album", "Remove room" }, albumMenu.Select(m => m.Label));
        albumMenu[0].Run!();
        albumMenu[1].Run!();
        Assert.Equal(album, Assert.Single(host.Played));
        Assert.Equal(album, Assert.Single(host.Shown));

        var menu = view.RoomMenu(pRoom);
        Assert.Equal(new[] { "Play playlist", "Choose songs...", "One room", "Two rooms", "Three rooms", "Four rooms", "Remove room" }, menu.Select(m => m.Label));
        Assert.True(menu.Single(m => m.Label == "One room").Checked);
        menu[0].Run!();
        Assert.Equal(playlist.TrackIds, Assert.Single(host.PlayedTracks));
        menu.Single(m => m.Label == "Four rooms").Run!();
        Assert.Equal(4, pRoom.Splits);
        Assert.Equal(4, view.Life.Map.AreasOf(pRoom).Count);
        Assert.Equal(5, view.Life.Pets.Count(p => p.Home == pRoom)); // five songs over four rooms

        // Choose songs: starts from the five most played, a sixth is refused, swaps work.
        var plays = lib.Tracks.ToDictionary(t => t.Id, t => t.PlayCount);
        var defaults = PlaylistPicks.Default(playlist.TrackIds, id => plays[id]);
        Assert.Equal(defaults, view.PetsOf(pRoom));
        view.RoomMenu(pRoom)[1].Run!();
        Assert.True(view.IsPickerOpen);
        var other = playlist.TrackIds.First(id => !defaults.Contains(id));
        Assert.False(view.TogglePick(other));
        Assert.True(view.TogglePick(defaults[0]));
        Assert.True(view.TogglePick(other));
        Assert.Equal(defaults.Skip(1).Append(other), pRoom.Picks);
        Assert.Contains(view.Life.Pets, p => p.TrackId == other);
        Assert.DoesNotContain(view.Life.Pets, p => p.TrackId == defaults[0]);

        // Remove: the room and its pets go, the picker closes.
        view.RoomMenu(pRoom).Last().Run!();
        Assert.False(view.IsPickerOpen);
        Assert.DoesNotContain(view.Life.Pets, p => p.Home == pRoom);
        Assert.Equal(new[] { aRoom }, grid.Rooms);
    }

    [AvaloniaFact]
    public void ClickingAPet_OpensTheCard_AndAPlaylistSongSaysWhereItLives()
    {
        var lib = MakeLibrary(8, 1);
        var (view, host) = NewView(lib);
        var window = new Window { Content = view, Width = 1600, Height = 1000 };
        window.Show();
        ClickTab(view, "Apartments");
        Dispatcher.UIThread.RunJobs();
        var canvas = view.ApartmentsView;
        view.Building.TryAdd(RoomKind.Playlist, lib.Playlists[0].Id, 0, 0);
        canvas.Step(1 / 60f);
        var pet = view.Life.Pets.First();
        var body = canvas.ToPx(pet.X, pet.Y - pet.Size * 0.8);

        Assert.True(canvas.Press(body));

        Assert.True(view.IsCardOpen);
        Assert.Equal(pet.Album, view.CardAlbum);
        Assert.Equal(CreatureState.Wave, pet.Creature.State);
        Assert.Contains(view.GetLogicalDescendantsText(), t => t == "Lives in the playlist Mix 0");
        window.Close();
    }

    [AvaloniaFact]
    public void TheApartments_PauseWhenHidden_AndSleepWithNobodyHome()
    {
        var (view, _) = NewView(MakeLibrary(4, 0));
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var canvas = view.ApartmentsView;
        Assert.True(canvas.ShouldPause()); // the plaza tab is showing
        ClickTab(view, "Apartments");
        Dispatcher.UIThread.RunJobs();
        Assert.False(canvas.ShouldPause());
        Assert.False(canvas.IsAlive); // empty: nothing to animate, no frames
        view.Building.TryAdd(RoomKind.Album, view.Town[0].Key, 0, 0);
        Assert.True(canvas.IsAlive);
        window.WindowState = WindowState.Minimized;
        Assert.True(canvas.ShouldPause());
        window.Close();
    }

    private static void ClickTab(AlbumTownView view, string name)
        => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(view).OfType<Button>()
            .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == name)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private static Point Center(ApartmentsCanvas canvas, int x, int y) => canvas.ToPx(x + 0.5, y + 0.5);

    private static string StatusText(AlbumTownView view)
        => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(view).OfType<TextBlock>()
            .Select(t => t.Text ?? "").First(t => t.Contains(" rooms") || t.Contains(" room "));

    // ── Rendering: the frame budget and the preview (NOCTIS_TEST_SKIA=1) ──

    private static string PreviewDir()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_ALBUMTOWN_PREVIEW") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "albumtown-preview");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// A full example building: album rooms of several sizes side by side (doors between them),
    /// playlist rooms divided into two, three and four rooms, five songs each where they have them.
    /// About 30 rooms and 50 pets: the realistic most.
    /// </summary>
    private static (AlbumTownView View, Window Window, FakeTownHost Host) ExampleBuilding(string dir, double width, double height, double scaling, bool big)
    {
        var covers = PreviewCovers(Path.Combine(dir, "covers"));
        var names = new[] { "Midnight Arcade", "Paper Moons", "Glasshouse", "Velvet Static", "Northern Lights", "Saltwater", "Neon Garden", "Low Tide",
            "Echo Park", "Golden Hour", "Small Hours", "Polaroid Summer", "Blue Room", "Satellite Hearts", "Wildflower", "Afterglow" };
        var artists = new[] { "Nova Lane", "Juniper Hollow", "Kite Season", "The Moth Choir", "Ada Vale", "Pixel Harbor", "Marlow and Finch", "Sunroom" };
        var genres = new[] { "Jazz Fusion", "Rock", "Dream Pop", "Electronic" };
        var host = new FakeTownHost { Pulse = 0.6 };
        for (var i = 0; i < 40; i++)
        {
            var id = Guid.NewGuid().ToString("D");
            for (var n = 0; n < 4; n++)
                host.Tracks.Add(T($"{names[(i + n * 5) % names.Length]} {(n == 0 ? "" : "Part " + n)}".Trim(), album: names[i % names.Length] + (i >= names.Length ? " " + (i / names.Length + 1) : ""),
                    artist: artists[i % artists.Length], plays: (i * 13 + n * 7) % 70, last: Now.AddDays(-(i % 30)), albumId: id, art: covers[i % covers.Count],
                    genre: genres[i % genres.Length], number: n + 1, id: $"t{i:D2}{n}") with { FilePath = $"C:/music/{i:D2}-{n}.mp3" });
        }
        var ids = host.Tracks.Select(t => t.Id).ToList();
        host.PlaylistList.Add(new PlaylistInfo("road", "Road Trip", null, ids.Where((_, i) => i % 3 == 0).ToArray()) { Color = "#FF6B6B" });
        host.PlaylistList.Add(new PlaylistInfo("rain", "Rainy Day", covers[3], ids.Where((_, i) => i % 5 == 1).ToArray()) { Color = "#45B7D1" });
        host.PlaylistList.Add(new PlaylistInfo("late", "Late Night", null, ids.Where((_, i) => i % 7 == 2).ToArray()) { Color = "#BB8FCE" });
        host.PlaylistList.Add(new PlaylistInfo("gym", "Gym", covers[8], ids.Where((_, i) => i % 4 == 3).ToArray()) { Color = "#52B788" });
        host.PlaylistList.Add(new PlaylistInfo("focus", "Focus", null, ids.Where((_, i) => i % 6 == 4).ToArray()) { Color = "#F8B500" });
        host.PlaylistList.Add(new PlaylistInfo("sun", "Sunday", covers[12], ids.Where((_, i) => i % 9 == 5).ToArray()) { Color = "#E07A5F" });

        var view = new AlbumTownView(host, new Random(21));
        var town = AlbumGrouping.Group(host.Tracks, out var byFile, out var byTrack);
        view.SetTown(town, byFile, Array.Empty<string>());
        var keys = town.Select(a => a.Key).ToList();
        var layout = new ApartmentLayout();
        void Room(string kind, string source, int x, int y, int w = 1, int h = 1, int splits = 1)
            => layout.Rooms.Add(new SavedRoom { Id = "r" + layout.Rooms.Count, Kind = kind, Source = source, X = x, Y = y, W = w, H = h, Splits = splits });
        if (big)
        {
            // 6 playlist rooms (rows 0-1 and 5-6) and 24 album rooms (rows 2-4): 30 rooms, ~50 pets.
            Room("playlist", "road", 0, 0, 2, 2, 4);
            Room("playlist", "rain", 2, 0, 4, 2, 3);
            Room("playlist", "late", 6, 0, 2, 2, 2);
            Room("playlist", "gym", 8, 0, 2, 1);
            Room("playlist", "focus", 10, 0, 2, 2, 4);
            Room("playlist", "sun", 12, 0, 2, 2, 2);
            var k = 0;
            for (var x = 0; x < 14 && k < 24; x++, k++) Room("album", keys[k], x, 2);
            Room("album", keys[k++], 0, 3, 2, 2);
            for (var x = 2; x < 12 && k < 24; x++, k++) Room("album", keys[k], x, 3);
        }
        else
        {
            // The picture: a few album rooms in several sizes, doors between them, and a playlist divided into four.
            Room("playlist", "road", 1, 1, 2, 2, 4);
            Room("album", keys[0], 3, 1, 2, 1);
            Room("album", keys[1], 5, 1);
            Room("album", keys[2], 3, 2);
            Room("album", keys[3], 4, 2, 2, 2);
            Room("album", keys[4], 6, 2);
            Room("playlist", "rain", 7, 1, 2, 1, 2);
            Room("album", keys[5], 9, 1, 2, 2);
            Room("album", keys[6], 6, 3);
            Room("album", keys[7], 7, 2);
            Room("album", keys[8], 8, 2);
            Room("album", keys[9], 1, 3, 2, 1);
            Room("playlist", "late", 11, 1, 2, 2, 3);
            Room("album", keys[10], 7, 3, 2, 1);
            Room("album", keys[11], 9, 3);
            Room("album", keys[12], 10, 3);
        }
        view.SetApartments(host.Tracks, host.PlaylistList, layout, byTrack);
        foreach (var a in town)
        {
            view.ResidentCovers.GetNow(a.ArtworkPath);
            view.SoftCovers.GetNow(a.ArtworkPath);
        }
        foreach (var c in covers) { view.SoftCovers.GetNow(c); view.ResidentCovers.GetNow(c); }
        var playing = host.Tracks.First(t => view.Life.Pets.Any(p => !p.IsSong && p.Album.TrackIds.Contains(t.Id)));
        host.NowPlaying = new NowPlayingTrack(playing.Title, playing.Artist, playing.Album, playing.Duration, playing.FilePath, playing.ArtworkPath, 120);
        host.IsPlaying = true;

        // 125% the way a screen at that setting shows it: the view is laid out at the logical
        // size and everything is drawn 1.25x bigger (the headless window itself is always 1x);
        // the building's bitmaps are made at 1.25x so they land 1:1 on the pixels.
        Window window;
        if (Math.Abs(scaling - 1) < 1e-9) window = new Window { Content = view, Width = width, Height = height };
        else
        {
            var zoom = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scaling, scaling), Child = view };
            window = new Window { Content = zoom, Width = Math.Round(width * scaling), Height = Math.Round(height * scaling) };
        }
        view.ApartmentsView.ScalingOverride = scaling;
        window.Show();
        host.RaiseNowPlaying();
        ClickTab(view, "Apartments");
        Dispatcher.UIThread.RunJobs();
        return (view, window, host);
    }

    [AvaloniaFact]
    public void Apartments_WriteThePreview_At2560x1440_125Percent_DarkAndLight()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_TEST_SKIA") != "1")
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var dir = PreviewDir();
        const double scaling = 1.25;
        var (view, window, _) = ExampleBuilding(dir, 2560 / scaling, 1440 / scaling, scaling, big: false);
        try
        {
            var canvas = view.ApartmentsView;
            using var tiny = new RenderTargetBitmap(new PixelSize(8, 8));
            for (var i = 0; i < 60 * 16; i++) canvas.Step(1 / 60f);
            for (var i = 0; i < 30; i++) { canvas.Step(1 / 60f); Render(canvas, tiny); }
            Assert.Equal(view.Building.Rooms.Count, canvas.RoomSpriteCount);
            Assert.Contains(view.Life.Pets, p => p.Creature.IsPlaying);

            // A pet clicked, so the card is in the shot.
            var clicked = view.Life.Pets.First(p => p.IsSong);
            canvas.Press(canvas.ToPx(clicked.X, clicked.Y - clicked.Size * 0.8));
            Render(canvas, tiny);
            Dispatcher.UIThread.RunJobs();
            Shot(window, Path.Combine(dir, "apartments-v12-dark.png"));

            view.ApplyTheme(TownTheme.Light);
            for (var i = 0; i < 30; i++) { canvas.Step(1 / 60f); Render(canvas, tiny); }
            Dispatcher.UIThread.RunJobs();
            Shot(window, Path.Combine(dir, "apartments-v12-light.png"));
            Assert.True(File.Exists(Path.Combine(dir, "apartments-v12-light.png")));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The window as the compositor draws it (a real frame, 2560x1440 for the preview).</summary>
    private static void Shot(Window window, string path)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize((int)window.Width, (int)window.Height), frame!.PixelSize);
        frame.Save(path);
    }

    private static void Render(Control control, RenderTargetBitmap bmp)
    {
        using var ctx = bmp.CreateDrawingContext();
        control.Render(ctx);
    }

    /// <summary>
    /// 30 rooms and ~50 pets at 2560x1440 (125%): the UI thread's share of a frame (step + record
    /// the way the compositor does), bytes allocated per frame and GCs, like the plaza's budget.
    /// Numbers go to apartments-bench.txt next to the preview.
    /// </summary>
    [AvaloniaFact]
    public void Apartments_30Rooms50Pets_StayInTheFrameAndAllocationBudget()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_TEST_SKIA") != "1")
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var dir = PreviewDir();
        const double scaling = 1.25;
        var (view, window, _) = ExampleBuilding(dir, 2560 / scaling, 1440 / scaling, scaling, big: true);
        try
        {
            var canvas = view.ApartmentsView;
            Assert.Equal(30, view.Building.Rooms.Count);
            Assert.InRange(view.Life.Pets.Count, 45, 56);
            var recorder = new Recorder(canvas);
            for (var i = 0; i < 60 * 8; i++) { canvas.Step(1 / 60f); recorder.Record(canvas); } // warm up: room bitmaps, tiles, labels
            Assert.Equal(30, canvas.RoomSpriteCount);

            const int frames = 600;
            var times = new double[frames];
            // Start from a settled heap: what earlier tests left behind is not this budget's.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
            var bytes = 0L; var stepBytes = 0L; var images = 0L;
            for (var i = 0; i < frames; i++)
            {
                var a0 = GC.GetAllocatedBytesForCurrentThread();
                var t0 = Stopwatch.GetTimestamp();
                canvas.Step(1 / 60f);
                var a1 = GC.GetAllocatedBytesForCurrentThread();
                recorder.Record(canvas);
                times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                bytes += GC.GetAllocatedBytesForCurrentThread() - a0;
                stepBytes += a1 - a0;
                images += canvas.ImagesDrawn;
            }
            var gcs = (GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2);
            Array.Sort(times);
            var p95 = times[(int)(frames * 0.95)];
            var per = bytes / (double)frames;
            var imagesPer = images / (double)frames;
            var lifePer = stepBytes / (double)frames;
            var layers = canvas.LayerBuilds;

            // What allocates in the steady state, sampled by the runtime (one sample per ~100 KB):
            // only Avalonia's own recording may show up, never the plugin's types, closures or text.
            string[] sampled;
            double ownShare;
            using (var listener = new AllocListener())
            {
                for (var i = 0; i < 3000; i++) { canvas.Step(1 / 60f); recorder.Record(canvas); }
                sampled = listener.Types.OrderByDescending(k => k.Value).Select(k => $"{k.Value} x {k.Key}").ToArray();
                ownShare = OwnShare(listener.Types);
            }
            var layersAfter = canvas.LayerBuilds;

            double raster;
            using (var bmp = new RenderTargetBitmap(new PixelSize(2560, 1440), new Vector(120, 120)))
            {
                for (var i = 0; i < 5; i++) Render(canvas, bmp);
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < 30; i++) { canvas.Step(1 / 60f); Render(canvas, bmp); }
                raster = sw.Elapsed.TotalMilliseconds / 30;
            }
            var report = $"{view.Building.Rooms.Count} rooms, {view.Life.Pets.Count} pets, canvas {canvas.Bounds.Width:0}x{canvas.Bounds.Height:0} at {scaling:0.00}x, {frames} frames\n"
                + $"UI thread (step + record): mean {times.Average():0.000} ms, p95 {p95:0.000} ms, max {times[^1]:0.000} ms\n"
                + $"allocated per frame: {per:0} B in all, life {lifePer:0} B (a new chat builds its two lines), the rest Avalonia recording {imagesPer:0} bitmaps\n"
                + $"building layer redrawn {layersAfter - layers + 0} times in 3000 steady frames\n"
                + $"GCs over {frames} frames: gen1 {gcs.Item1}, gen2 {gcs.Item2}\n"
                + $"allocation samples over 3000 frames: {string.Join("; ", sampled)}\n"
                + $"full CPU raster (headless Skia, step + render into a 2560x1440 bitmap): {raster:0.00} ms per frame\n";
            File.WriteAllText(Path.Combine(dir, "apartments-bench.txt"), report);
            Console.WriteLine(report);

            Assert.True(p95 < 4, report);
            Assert.True(lifePer < 64, report);
            Assert.True(per < 16 * 1024, report);
            Assert.True(gcs.Item1 + gcs.Item2 == 0, report);
            Assert.Equal(layers, layersAfter); // the rooms are not redrawn while nothing changes
            // Avalonia's own objects; the plugin's only when a new chat builds its lines (a sliver).
            Assert.True(ownShare <= 0.02, $"plugin share of the samples {ownShare:P1}\n{report}");
        }
        finally
        {
            window.Close();
        }
    }
}

internal static class AlbumTownTestText
{
    /// <summary>Every TextBlock's text under <paramref name="root"/>.</summary>
    public static IEnumerable<string> GetLogicalDescendantsText(this Control root)
        => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<TextBlock>().Select(t => t.Text ?? "");
}

/// <summary>
/// Samples what the thread that creates it allocates (the runtime's allocation ticks, one per
/// ~100 KB, with the type). Other threads (cover decodes, the test host) are left out.
/// </summary>
internal sealed class AllocListener : System.Diagnostics.Tracing.EventListener
{
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Types = new();
    private readonly long? _thread;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public AllocListener() : this(OperatingSystem.IsWindows() ? GetCurrentThreadId() : null) { }

    private AllocListener(long? thread) : base() => _thread = thread;

    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
    }

    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
    {
        if (e.EventName?.StartsWith("GCAllocationTick") != true || e.Payload is null) return;
        if (_thread is { } t && e.OSThreadId != t) return;
        var i = e.PayloadNames!.IndexOf("TypeName");
        if (i >= 0) Types.AddOrUpdate(e.Payload[i]?.ToString() ?? "?", 1, (_, n) => n + 1);
    }
}
