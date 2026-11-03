using Avalonia;
using Avalonia.Headless.XUnit;
using Noctis.Plugins;
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
}
