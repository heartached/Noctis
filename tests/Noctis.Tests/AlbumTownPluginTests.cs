using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctis.Plugins;
using Noctis.Plugins.AlbumTown;
using Noctis.Plugins.AlbumTown.Town;
using Noctis.Plugins.AlbumTown.Views;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Album Town (plugin API 1.3): who is a resident (one per album, singles their own), who comes
/// to the plaza, what they say, the plaza state machine, the apartment sizes, the frame budget
/// for 60 creatures, and the plugin through the real loader and its Release zip.
/// </summary>
public class AlbumTownPluginTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static TrackInfo T(string title, string album = "Album", string artist = "Artist", int year = 2019, int plays = 0,
        DateTimeOffset? last = null, string? art = "C:/art/a.jpg", string albumId = "", string releaseType = "Album",
        string genre = "Pop", int number = 1, string? id = null, bool favorite = false)
        => new(id ?? Guid.NewGuid().ToString("D"), title, artist, album, artist, TimeSpan.FromMinutes(3), year, genre, number,
            "C:/music/" + title + ".mp3", favorite, plays, 0)
        {
            AlbumId = albumId, ArtworkPath = art, ReleaseType = releaseType, LastPlayed = last, DateAdded = Now.AddYears(-1),
        };

    private static TownAlbum A(string key, string title = "Album", string artist = "Artist", int year = 2019, int plays = 0,
        DateTimeOffset? last = null, bool single = false, int tracks = 10, string genre = "Pop", bool favorite = false)
        => new(key, title, artist, year, genre, Enumerable.Range(0, tracks).Select(i => key + "#" + i).ToArray(), "", null,
            plays, last, Now.AddYears(-2), single, favorite);

    // ── Who lives in town ──

    [Fact]
    public void Grouping_SongsSharingACover_AreOneCreature()
    {
        var id = Guid.NewGuid().ToString("D");
        var tracks = new[]
        {
            T("One", albumId: id, plays: 3, last: Now.AddDays(-3), number: 1),
            T("Two", albumId: id, plays: 4, last: Now.AddDays(-1), number: 2),
            T("Three", albumId: id, plays: 0, number: 3),
        };
        var town = AlbumGrouping.Group(tracks, out var byFile);

        var album = Assert.Single(town);
        Assert.Equal(("Album", "Artist", 2019, 3, 7), (album.Title, album.Artist, album.Year, album.TrackCount, album.PlayCount));
        Assert.Equal(Now.AddDays(-1), album.LastPlayed);
        Assert.Equal(id, album.AlbumId);
        Assert.Equal(tracks.Select(t => t.Id), album.TrackIds); // in track order: Play starts at track 1
        Assert.False(album.IsSingle);
        Assert.All(tracks, t => Assert.Equal(album.Key, byFile[t.FilePath]));
    }

    [Fact]
    public void Grouping_ASongWithItsOwnCover_MovesOutAsASingle()
    {
        var id = Guid.NewGuid().ToString("D");
        var town = AlbumGrouping.Group(new[]
        {
            T("One", albumId: id, art: "C:/art/album.jpg"),
            T("Two", albumId: id, art: "C:/art/album.jpg"),
            T("Lead single", albumId: id, art: "C:/art/single.jpg"),
            T("No art", albumId: id, art: null), // no cover never splits
        });

        Assert.Equal(2, town.Count);
        var album = town.Single(a => !a.IsSingle);
        var single = town.Single(a => a.IsSingle);
        Assert.Equal(3, album.TrackCount);
        Assert.Equal("C:/art/album.jpg", album.ArtworkPath);
        Assert.Equal(("Lead single", 1, "C:/art/single.jpg"), (single.Title, single.TrackCount, single.ArtworkPath));
    }

    [Fact]
    public void Grouping_Singles_AreTheirOwnCreature()
    {
        var town = AlbumGrouping.Group(new[]
        {
            T("Loose song", album: ""),
            T("Another loose song", album: " "),
            T("Radio edit", album: "Radio edit", releaseType: "Single", albumId: Guid.NewGuid().ToString("D")),
            T("Only track", album: "Tiny", albumId: Guid.NewGuid().ToString("D")),
        });

        Assert.Equal(4, town.Count);
        Assert.All(town, a => Assert.True(a.IsSingle));
        Assert.Contains(town, a => a.Title == "Loose song"); // a song without an album is named after itself
    }

    [Fact]
    public void Grouping_WithoutAlbumIds_UsesAlbumArtistAndName()
    {
        var town = AlbumGrouping.Group(new[]
        {
            T("a", album: "Greatest Hits", artist: "Queen"),
            T("b", album: "greatest hits ", artist: "queen"),
            T("c", album: "Greatest Hits", artist: "ABBA"),
        });
        Assert.Equal(2, town.Count);
        Assert.Equal(2, town.Single(a => a.Artist == "Queen").TrackCount);
    }

    // ── Who comes to the plaza ──

    [Fact]
    public void Cast_NeverBringsTheWholeTown_AtMost60()
    {
        var town = Enumerable.Range(0, 2000).Select(i => A("k" + i.ToString("D4"), plays: i % 50)).ToList();
        Assert.Equal(60, PlazaCast.Choose(town, 500, new Random(1)).Count);
        Assert.Equal(1, PlazaCast.Choose(town, 0, new Random(1)).Count);
        var cast = PlazaCast.Choose(town, 40, new Random(1));
        Assert.Equal(40, cast.Count);
        Assert.Equal(40, cast.Select(m => m.Album.Key).Distinct().Count());
    }

    [Fact]
    public void Cast_Called_ThenPlaying_ThenRecent_ThenMostPlayed_ThenRandom()
    {
        var town = new List<TownAlbum>();
        for (var i = 0; i < 30; i++) town.Add(A("r" + i.ToString("D2"), last: Now.AddDays(-i), plays: 1));
        for (var i = 0; i < 30; i++) town.Add(A("m" + i.ToString("D2"), plays: 100 + i));
        for (var i = 0; i < 100; i++) town.Add(A("x" + i.ToString("D3")));

        var cast = PlazaCast.Choose(town, 30, new Random(7), nowPlayingKey: "x050", called: new[] { "x007", "missing" });

        Assert.Equal(("x007", CastReason.Called), (cast[0].Album.Key, cast[0].Reason));
        Assert.Equal(("x050", CastReason.NowPlaying), (cast[1].Album.Key, cast[1].Reason));
        var recent = cast.Where(m => m.Reason == CastReason.Recent).Select(m => m.Album.Key).ToList();
        Assert.Equal(new[] { "r00", "r01", "r02", "r03", "r04", "r05", "r06", "r07", "r08", "r09" }, recent);
        var most = cast.Where(m => m.Reason == CastReason.MostPlayed).Select(m => m.Album.Key).ToList();
        Assert.Equal(10, most.Count);
        Assert.Equal("m29", most[0]);
        Assert.Equal(8, cast.Count(m => m.Reason == CastReason.Random));
        // Same seed, same crowd.
        Assert.Equal(cast.Select(m => m.Album.Key), PlazaCast.Choose(town, 30, new Random(7), "x050", new[] { "x007", "missing" }).Select(m => m.Album.Key));
    }

    [Fact]
    public void Cast_SmallTown_EveryoneComes()
    {
        var town = new[] { A("a"), A("b"), A("c") };
        Assert.Equal(3, PlazaCast.Choose(town, 40, new Random(1)).Count);
        Assert.Empty(PlazaCast.Choose(Array.Empty<TownAlbum>(), 40, new Random(1)));
    }

    [Fact]
    public void Find_MatchesEveryWord_InTitleOrArtist()
    {
        var town = new[] { A("1", "Blue Train", "John Coltrane"), A("2", "Blue", "Joni Mitchell"), A("3", "Kind of Blue", "Miles Davis") };
        Assert.Equal(new[] { "2", "1", "3" }, PlazaCast.Find(town, "blue").Select(a => a.Key));
        Assert.Equal("3", Assert.Single(PlazaCast.Find(town, "blue miles")).Key);
        Assert.Empty(PlazaCast.Find(town, "  "));
    }

    // ── What they say ──

    [Fact]
    public void Talk_UsesRealMetadata()
    {
        var a = A("a", artist: "Mitski", year: 2019, plays: 47, last: Now.AddDays(-2));
        Assert.Equal("Same artist, cousin!", TalkLines.Topics(a, A("b", artist: "mitski", year: 2018), Now).First().Opener);
        Assert.Equal("We're both from 2019!", TalkLines.Topics(a, A("b", artist: "Other", year: 2019), Now).First().Opener);
        Assert.Contains(TalkLines.Topics(a, A("b", artist: "Other", year: 2001, plays: 3), Now), c => c.Opener == "I've been played 47 times.");
        var lonely = A("l", artist: "Other", year: 1999, plays: 5, last: Now.AddDays(-90));
        Assert.Equal("You haven't played me in a while...", TalkLines.Topics(lonely, A("b", artist: "x", year: 2010), Now).First().Opener);
        Assert.Contains(TalkLines.Topics(A("s", single: true, tracks: 1, year: 0), A("b", year: 0, artist: "x"), Now), c => c.Topic == "single");
    }

    [Fact]
    public void Talk_AlwaysHasSomethingToSay_AndNoEmDashes()
    {
        var random = new Random(3);
        var samples = new[]
        {
            A("a"), A("b", artist: "", year: 0, genre: ""), A("c", single: true, tracks: 1, plays: 99, last: Now.AddYears(-1), favorite: true),
            A("d", tracks: 22, year: 1971, genre: "Jazz"), A("e", plays: 0, last: null),
        };
        foreach (var x in samples)
            foreach (var y in samples)
            {
                var topics = TalkLines.Topics(x, y, Now).ToList();
                Assert.NotEmpty(topics);
                Assert.Equal("hello", topics[^1].Topic);
                Assert.All(topics, t => Assert.DoesNotContain('\u2014', t.Opener + t.Reply));
                Assert.Contains(TalkLines.Between(x, y, Now, random), topics);
            }
        Assert.All(samples, s => Assert.DoesNotContain('\u2014', TalkLines.Greeting(s, Now)));
        Assert.Equal("Hi! 47 plays and counting.", TalkLines.Greeting(A("p", plays: 47, last: Now.AddDays(-1)), Now));
        Assert.Equal("Hi! Play me sometime?", TalkLines.Greeting(A("z"), Now));
    }

    // ── The plaza state machine ──

    private static PlazaWorld World(params TownAlbum[] albums)
    {
        var world = new PlazaWorld(800, 500, new Random(5));
        world.SetCast(albums.Select(a => new CastMember(a, CastReason.Random)).ToList());
        return world;
    }

    private static void Run(PlazaWorld world, float seconds, float pulse = 0)
    {
        for (var t = 0f; t < seconds; t += 1 / 30f) world.Step(1 / 30f, pulse);
    }

    [Fact]
    public void Creatures_Wander_AndStayInsideThePlaza()
    {
        var world = World(Enumerable.Range(0, 20).Select(i => A("w" + i)).ToArray());
        world.Chatty = false;
        var start = world.Creatures.Select(c => c.Position).ToList();
        var states = new HashSet<CreatureState>();
        for (var i = 0; i < 30 * 60; i++)
        {
            world.Step(1 / 30f);
            foreach (var c in world.Creatures)
            {
                states.Add(c.State);
                Assert.InRange(c.Position.X, world.Margin - 0.01f, world.Width - world.Margin + 0.01f);
                Assert.InRange(c.Position.Y, world.Margin - 0.01f, world.Height - world.Margin + 0.01f);
            }
        }
        Assert.Contains(CreatureState.Wander, states);
        Assert.Contains(CreatureState.Idle, states);
        Assert.DoesNotContain(CreatureState.Talk, states); // not chatty
        Assert.True(world.Creatures.Select((c, i) => Vector2.Distance(c.Position, start[i])).Count(d => d > 20) >= 15);
    }

    [Fact]
    public void Neighbours_StopAndTalk_OpenerFirstThenTheReply()
    {
        var world = World(A("a", artist: "Same", year: 2001), A("b", artist: "Same", year: 2002));
        var (a, b) = (world.Creatures[0], world.Creatures[1]);
        a.Position = new Vector2(300, 250);
        b.Position = new Vector2(350, 250);
        foreach (var c in world.Creatures) { c.TalkCooldown = 0; c.StateTimer = 0; }

        world.Step(1 / 30f);

        Assert.Equal((CreatureState.Talk, CreatureState.Talk), (a.State, b.State));
        Assert.Same(b, a.Partner);
        Assert.Same(a, b.Partner);
        var opener = a.Opened ? a : b;
        var other = a.Opened ? b : a;
        Assert.False(other.Opened);
        Assert.Same(opener.Chat, other.Chat);
        world.Step(1 / 30f);
        Assert.Equal(opener.Chat!.Opener, opener.Bubble);
        Assert.Null(other.Bubble);
        Assert.Equal(Math.Sign(other.Position.X - opener.Position.X), (int)opener.Facing); // they face each other
        Assert.Equal(Math.Sign(opener.Position.X - other.Position.X), (int)other.Facing);

        Run(world, 3f);
        Assert.Null(opener.Bubble);
        Assert.Equal(opener.Chat!.Reply, other.Bubble);

        Run(world, 3f);
        Assert.NotEqual(CreatureState.Talk, a.State);
        Assert.NotEqual(CreatureState.Talk, b.State);
        Assert.Null(a.Partner);
        Assert.True(a.TalkCooldown > 0 && b.TalkCooldown > 0);
    }

    [Fact]
    public void IdleCreatures_LookAtTheCursor()
    {
        var world = World(A("a"));
        var c = world.Creatures[0];
        c.Position = new Vector2(400, 250);
        c.StateTimer = 10;
        world.Cursor = new Vector2(460, 250);
        world.Step(1 / 30f);
        world.Step(1 / 30f);
        Assert.Equal(CreatureState.LookAtCursor, c.State);
        Assert.True(c.Gaze.X > 0.9f);
        Assert.Equal(1, c.Facing);

        world.Cursor = new Vector2(50, 50);
        world.Step(1 / 30f);
        Assert.Equal(CreatureState.Idle, c.State);
    }

    [Fact]
    public void Click_Waves_AndSaysHello_ThenCarriesOn()
    {
        var world = World(A("a", plays: 12, last: DateTimeOffset.Now));
        var c = world.Creatures[0];
        c.Position = new Vector2(400, 250);
        Assert.Same(c, world.HitTest(new Vector2(400, 235)));
        Assert.Null(world.HitTest(new Vector2(100, 100)));

        world.Wave(c);
        Assert.Equal(CreatureState.Wave, c.State);
        Assert.Equal("Hi! 12 plays and counting.", c.Bubble);

        Run(world, 2f);
        Assert.NotEqual(CreatureState.Wave, c.State);
        Assert.Null(c.Bubble);
    }

    [Fact]
    public void ThePlayingAlbum_Dances_AndNobodyInterruptsIt()
    {
        var world = World(A("a"), A("b"));
        var (a, b) = (world.Creatures[0], world.Creatures[1]);
        a.Position = new Vector2(300, 250);
        b.Position = new Vector2(340, 250);
        world.SetNowPlaying("a", playing: true);
        Assert.Equal(CreatureState.Dance, a.State);
        Assert.True(a.IsPlaying);

        b.TalkCooldown = 0;
        b.StateTimer = 0;
        var phase = a.WalkPhase;
        world.Step(1 / 30f, pulse: 1);
        Assert.Equal(CreatureState.Dance, a.State);
        Assert.NotEqual(CreatureState.Talk, b.State);
        Assert.Equal(1, a.Pulse);
        Assert.True(a.WalkPhase > phase);

        world.SetNowPlaying("a", playing: false); // paused: back to normal life
        Assert.Equal(CreatureState.Idle, a.State);
        Assert.False(a.IsPlaying);
    }

    [Fact]
    public void Call_WalksInFromTheEdge_AndAKnownOneWaves()
    {
        var world = World(A("a"));
        var newcomer = world.Call(new CastMember(A("b"), CastReason.Called));
        Assert.Equal(2, world.Creatures.Count);
        Assert.Equal(CreatureState.Wander, newcomer.State);
        Assert.True(newcomer.Position.Y <= world.Margin + 24.1f || newcomer.Position.Y >= world.Height - world.Margin - 0.1f);

        var again = world.Call(new CastMember(A("a"), CastReason.Called));
        Assert.Equal(2, world.Creatures.Count);
        Assert.Equal(CreatureState.Wave, again.State);
    }

    [Fact]
    public void SixtyCreatures_StepWellWithinTheFrameBudget()
    {
        var world = new PlazaWorld(1000, 640, new Random(9));
        world.SetCast(Enumerable.Range(0, 60).Select(i => new CastMember(A("p" + i, year: 2000 + i % 25, plays: i), CastReason.Random)).ToList());
        world.SetNowPlaying("p3", true);
        world.Cursor = new Vector2(500, 300);
        for (var i = 0; i < 300; i++) world.Step(1 / 30f, 0.5f); // warm up (JIT)

        const int frames = 3000; // 100 s of plaza at 30 fps
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++) world.Step(1 / 30f, (i % 15) / 15f);
        sw.Stop();

        var perFrame = sw.Elapsed.TotalMilliseconds / frames;
        // A 30 fps frame is 33 ms; the simulation must be a sliver of it.
        Assert.True(perFrame < 2.0, $"60 creatures took {perFrame:0.000} ms per step");
        Assert.Contains(world.Creatures, c => c.State == CreatureState.Talk || c.TalkCooldown > 0);
    }

    // ── Apartments ──

    [Theory]
    [InlineData(1, 1, "Studio")]
    [InlineData(2, 2, "Flat")]
    [InlineData(3, 2, "Flat")]
    [InlineData(4, 3, "Family home")]
    [InlineData(6, 3, "Family home")]
    [InlineData(7, 4, "Loft")]
    [InlineData(12, 6, "Penthouse")]
    [InlineData(80, 6, "Penthouse")]
    public void Apartment_GrowsWithTheAlbums(int albums, int units, string tier)
        => Assert.Equal((units, tier), (Apartments.UnitsFor(albums), Apartments.TierFor(albums)));

    [Fact]
    public void Building_GivesEveryArtistAHome_AndNoFloorOverflows()
    {
        var town = new List<TownAlbum>();
        var random = new Random(4);
        for (var artist = 0; artist < 40; artist++)
            for (var i = random.Next(1, 15); i > 0; i--)
                town.Add(A($"{artist}-{i}", title: "T" + i, artist: "Artist " + artist));
        town.Add(A("u", artist: ""));

        var building = Apartments.Build(town);

        Assert.Equal(41, building.Apartments.Count);
        Assert.Equal(town.Count, building.Apartments.Sum(a => a.Albums.Count));
        Assert.Contains(building.Apartments, a => a.Artist == "Unknown artist");
        foreach (var floor in building.Apartments.GroupBy(a => a.Floor))
        {
            Assert.True(floor.Sum(a => a.Units) <= building.UnitsPerFloor);
            var columns = floor.OrderBy(a => a.Column).ToList();
            for (var i = 1; i < columns.Count; i++) Assert.Equal(columns[i - 1].Column + columns[i - 1].Units, columns[i].Column);
        }
        Assert.Equal(building.Apartments.Max(a => a.Floor) + 1, building.Floors);
        // Biggest households move in first (top floors).
        Assert.Equal(building.Apartments.Max(a => a.Albums.Count), building.Apartments[0].Albums.Count);
        Assert.Equal(0, Apartments.Build(Array.Empty<TownAlbum>()).Floors);
    }

    // ── The view ──

    internal sealed class FakeTownHost : ITownHost
    {
        public List<TrackInfo> Tracks { get; } = new();
        public NowPlayingTrack? NowPlaying { get; set; }
        public bool IsPlaying { get; set; }
        public event EventHandler? NowPlayingChanged;
        public double? Pulse { get; set; }
        public List<TownAlbum> Played { get; } = new();
        public List<TownAlbum> Shown { get; } = new();
        public int CrowdSize { get; set; } = 40;
        public bool ShowNames { get; set; }
        public bool Chatty { get; set; } = true;
        public event EventHandler? SettingsChanged;
        public List<string> Called { get; set; } = new();
        public int Saves;

        public IReadOnlyList<TrackInfo> LoadTracks() => Tracks;
        public bool TryReadBeat(out double pulse) { pulse = Pulse ?? 0; return Pulse is not null; }
        public void Play(TownAlbum album) => Played.Add(album);
        public bool ShowAlbum(TownAlbum album) { Shown.Add(album); return true; }
        public IReadOnlyList<string> LoadCalled() => Called;
        public void SaveCalled(IReadOnlyList<string> keys) { Called = keys.ToList(); Saves++; }
        public void RaiseNowPlaying() => NowPlayingChanged?.Invoke(this, EventArgs.Empty);
        public void RaiseSettings() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static (AlbumTownView View, FakeTownHost Host) NewView(int albums, int crowd = 40)
    {
        var host = new FakeTownHost { CrowdSize = crowd };
        for (var i = 0; i < albums; i++)
            host.Tracks.Add(T("Song " + i, album: "Album " + i, artist: "Artist " + i % 7, year: 1990 + i % 30, plays: i % 13,
                albumId: Guid.NewGuid().ToString("D"), art: null));
        var view = new AlbumTownView(host, new Random(2));
        var town = AlbumGrouping.Group(host.Tracks, out var map);
        view.SetTown(town, map, host.Called);
        return (view, host);
    }

    [AvaloniaFact]
    public void View_CallsACrowd_AndCallingKeepsItWithinTheLimit()
    {
        var (view, host) = NewView(300, crowd: 60);
        Assert.Equal(300, view.Town.Count);
        Assert.Equal(60, view.Cast.Count);
        Assert.Equal(60, view.Plaza.World.Creatures.Count);

        var outsider = view.Town.First(a => view.Cast.All(m => m.Album.Key != a.Key));
        view.Call(outsider);

        Assert.Equal(60, view.Cast.Count);
        Assert.Contains(view.Cast, m => m.Album.Key == outsider.Key && m.Reason == CastReason.Called);
        Assert.Equal(new[] { outsider.Key }, host.Called);
        Assert.Equal(7, view.ApartmentsView.Building.Apartments.Count);

        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        host.CrowdSize = 24;
        host.RaiseSettings();
        Assert.Equal(24, view.Cast.Count);
        Assert.Contains(view.Cast, m => m.Album.Key == outsider.Key); // called ones stay
        window.Close();
    }

    [AvaloniaFact]
    public void View_TheAlbumYouStart_WalksInAndDances()
    {
        var (view, host) = NewView(200, crowd: 24);
        var track = host.Tracks.First(t => view.Cast.All(m => !m.Album.TrackIds.Contains(t.Id)));
        host.NowPlaying = new NowPlayingTrack(track.Title, track.Artist, track.Album, track.Duration, track.FilePath, null, 0);
        host.IsPlaying = true;
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        host.RaiseNowPlaying();

        var dancer = Assert.Single(view.Plaza.World.Creatures, c => c.IsPlaying);
        Assert.Contains(track.Id, dancer.Album.TrackIds);
        Assert.Equal(CreatureState.Dance, dancer.State);
        Assert.Equal(24, view.Cast.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void View_Card_PlaysAndGoesToTheAlbum()
    {
        var (view, host) = NewView(10);
        var album = view.Town[3];
        view.ShowCard(album, CastReason.Recent);
        Assert.True(view.IsCardOpen);
        Assert.Same(album, view.CardAlbum);

        var buttons = view.GetLogicalDescendants().OfType<Button>().Where(b => b.IsVisible).ToList();
        Click(buttons.Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Play"));
        Click(buttons.Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Go to album"));
        Assert.Same(album, Assert.Single(host.Played));
        Assert.Same(album, Assert.Single(host.Shown));

        Click(buttons.Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Close"));
        Assert.False(view.IsCardOpen);
    }

    private static void Click(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void Plaza_PausesWhileHiddenOrMinimized()
    {
        var (view, _) = NewView(30);
        Assert.True(view.Plaza.ShouldPause()); // not on screen yet
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        view.Plaza.Tick();
        Assert.False(view.Plaza.IsPaused);
        var frames = view.Plaza.Frames;
        view.Plaza.Tick();
        Assert.Equal(frames + 1, view.Plaza.Frames);

        window.WindowState = WindowState.Minimized;
        view.Plaza.Tick();
        Assert.True(view.Plaza.IsPaused);
        view.Plaza.Tick();
        Assert.Equal(frames + 1, view.Plaza.Frames); // nothing stepped or drawn

        window.WindowState = WindowState.Normal;
        view.Plaza.Tick();
        Assert.False(view.Plaza.IsPaused);

        view.Plaza.IsVisible = false; // the Apartments tab
        view.Plaza.Tick();
        Assert.True(view.Plaza.IsPaused);
        window.Close();
    }

    // ── Real rendering: frame time and the preview picture (NOCTIS_TEST_SKIA=1) ──

    [AvaloniaFact]
    public void Plaza_Renders60Creatures_WithinTheFrameBudget_AndWritesThePreview()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_TEST_SKIA") != "1")
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");

        var dir = Environment.GetEnvironmentVariable("NOCTIS_ALBUMTOWN_PREVIEW") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "albumtown-preview");
        Directory.CreateDirectory(dir);
        var host = new FakeTownHost { CrowdSize = 60, Pulse = 0.7 };
        var covers = PreviewCovers(Path.Combine(dir, "covers"));
        var names = new[] { "Midnight Arcade", "Paper Moons", "Glasshouse", "Velvet Static", "Northern Lights", "Saltwater", "Neon Garden", "Low Tide",
            "Echo Park", "Golden Hour", "Small Hours", "Polaroid Summer", "Blue Room", "Satellite Hearts", "Wildflower", "Afterglow" };
        var artists = new[] { "Nova Lane", "Juniper Hollow", "Kite Season", "The Moth Choir", "Ada Vale", "Pixel Harbor", "Marlow and Finch",
            "Sunroom", "Odd Orchard", "Lo Tide", "Cassette Ghosts", "June Arcade", "Paper Lanterns", "Static Bloom" };
        var albums = 0;
        for (var i = 0; i < 90; i++)
        {
            var id = Guid.NewGuid().ToString("D");
            var tracks = i % 9 == 0 ? 1 : 3 + i % 8;
            for (var n = 0; n < tracks; n++)
                host.Tracks.Add(T($"{names[i % names.Length]} {n}", album: $"{names[i % names.Length]}{(i >= names.Length ? " " + (i / names.Length + 1) : "")}",
                    artist: artists[(int)(artists.Length * Math.Pow(i * 37 % 90 / 90.0, 2))], year: 1995 + i % 28, plays: (i * 7) % 60, last: Now.AddDays(-(i % 40)),
                    albumId: id, art: covers[i % covers.Count], releaseType: tracks == 1 ? "Single" : "Album", number: n + 1));
            albums++;
        }
        var view = new AlbumTownView(host, new Random(11));
        var town = AlbumGrouping.Group(host.Tracks, out var map);
        view.SetTown(town, map, Array.Empty<string>());
        foreach (var a in town) view.Covers.GetNow(a.ArtworkPath);
        var playing = host.Tracks.First(t => view.Cast.Any(m => m.Album.TrackIds.Contains(t.Id) && m.Reason == CastReason.Recent));
        host.NowPlaying = new NowPlayingTrack(playing.Title, playing.Artist, playing.Album, playing.Duration, playing.FilePath, playing.ArtworkPath, 120);
        host.IsPlaying = true;

        var window = new Window { Content = view, Width = 1200, Height = 760 };
        window.Show();
        host.RaiseNowPlaying();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(60, view.Plaza.World.Creatures.Count);

        // Let them walk and chat for a while, then a click so the card is in the shot.
        for (var i = 0; i < 30 * 14; i++) view.Plaza.Step(1 / 30f);
        var talking = view.Plaza.World.Creatures.Count(c => c.Bubble is not null);
        var w = (float)view.Plaza.Bounds.Width;
        var h = (float)view.Plaza.Bounds.Height;
        // For the picture: the dancer in the open, and a waving neighbour clear of the card.
        view.Plaza.World.Creatures.Single(c => c.IsPlaying).Position = new Vector2(w * 0.6f, h * 0.5f);
        var clicked = view.Plaza.World.Creatures.Where(c => !c.IsPlaying && c.State != CreatureState.Talk)
            .OrderBy(c => Vector2.Distance(c.Position, new Vector2(w * 0.36f, h * 0.3f))).First();

        var size = new PixelSize((int)view.Plaza.Bounds.Width, (int)view.Plaza.Bounds.Height);
        using (var bmp = new RenderTargetBitmap(size))
        {
            const int frames = 120;
            for (var i = 0; i < 10; i++) RenderFrame(view, bmp); // warm up
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < frames; i++)
            {
                view.Plaza.Step(1 / 30f);
                RenderFrame(view, bmp);
            }
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds / frames;
            File.WriteAllText(Path.Combine(dir, "frame-time.txt"), $"60 creatures, {size.Width}x{size.Height}: {ms:0.00} ms per step+render (CPU raster, headless), {talking} talking");
            Assert.True(ms < 33, $"a frame took {ms:0.0} ms");
        }

        view.Plaza.World.Wave(clicked);
        view.Plaza.Step(1 / 30f);
        view.ShowCard(clicked.Album, clicked.Member.Reason);
        Dispatcher.UIThread.RunJobs();

        using (var shot = new RenderTargetBitmap(new PixelSize(1200, 760)))
        {
            shot.Render(window);
            shot.Save(Path.Combine(dir, "preview.png"));
        }
        view.HideCard();
        view.GetLogicalDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Apartments")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        foreach (var a in town) view.Covers.GetNow(a.ArtworkPath);
        view.ApartmentsView.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
        using (var shot = new RenderTargetBitmap(new PixelSize(1200, 760)))
        {
            shot.Render(window);
            shot.Save(Path.Combine(dir, "preview-apartments.png"));
        }
        window.Close();
        Assert.True(albums > 60);
    }

    private static void RenderFrame(AlbumTownView view, RenderTargetBitmap bmp)
    {
        using var ctx = bmp.CreateDrawingContext();
        view.Plaza.Render(ctx);
    }

    /// <summary>Made-up album covers (gradients and shapes) for the preview.</summary>
    private static List<string> PreviewCovers(string dir)
    {
        Directory.CreateDirectory(dir);
        var palettes = new[]
        {
            ("#FF6B6B", "#4D1A6B"), ("#FFD166", "#EF476F"), ("#06D6A0", "#118AB2"), ("#8338EC", "#3A86FF"), ("#F72585", "#7209B7"),
            ("#FB8500", "#023047"), ("#90BE6D", "#277DA1"), ("#F9C74F", "#F3722C"), ("#E0AAFF", "#5A189A"), ("#2EC4B6", "#011627"),
            ("#FFBE0B", "#FB5607"), ("#CDB4DB", "#FFAFCC"), ("#FF006E", "#1B1B3A"), ("#A8DADC", "#1D3557"), ("#E9C46A", "#264653"),
            ("#D62828", "#003049"),
        };
        var list = new List<string>();
        for (var i = 0; i < palettes.Length; i++)
        {
            var path = Path.Combine(dir, $"cover{i:D2}.png");
            list.Add(path);
            if (File.Exists(path)) continue;
            var (c1, c2) = palettes[i];
            using var bmp = new RenderTargetBitmap(new PixelSize(256, 256));
            using (var ctx = bmp.CreateDrawingContext())
            {
                var bg = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse(c1), 0), new GradientStop(Color.Parse(c2), 1) },
                };
                ctx.FillRectangle(bg, new Rect(0, 0, 256, 256));
                var ink = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
                switch (i % 4)
                {
                    case 0: ctx.DrawEllipse(ink, null, new Point(128, 150), 70, 70); break;
                    case 1: ctx.FillRectangle(ink, new Rect(40, 170, 176, 26)); ctx.FillRectangle(ink, new Rect(40, 120, 120, 26)); break;
                    case 2: ctx.DrawEllipse(null, new Pen(ink, 14), new Point(128, 128), 80, 80); break;
                    default: ctx.DrawGeometry(ink, null, Geometry.Parse("M 128,40 L 220,210 L 36,210 Z")); break;
                }
            }
            bmp.Save(path);
        }
        return list;
    }

    // ── The plugin itself ──

    private const string PluginId = "dev.noctis.plugins.albumtown";

    [Fact]
    public void Manifest_Is13_CreditsJafezy_AndAsksOnlyForWhatItUses()
    {
        var json = File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.AlbumTown/plugin.json"));
        var manifest = PluginManifest.Parse(json);
        Assert.Equal((PluginId, "Album Town", "1.3"), (manifest.Id, manifest.Name, manifest.ApiVersion));
        Assert.Equal(new[] { "library.read", "playback.control" }, manifest.Permissions);
        Assert.Empty(manifest.Warnings);
        Assert.Contains("Jafezy JD", manifest.Description);
        Assert.Contains("Jafezy JD", manifest.Author);
        Assert.Contains("Jafezy JD", AlbumTownPlugin.About);
        Assert.Contains("Jafezy JD", AlbumTownView.Credit);
        Assert.DoesNotContain('\u2014', json);
        Assert.Equal(new AlbumTownPlugin().Info.Id, manifest.Id);
        Assert.NotNull(manifest.CheckCompatibility("1.6.5")); // before API 1.3
        Assert.Null(manifest.CheckCompatibility("1.6.6"));
        Assert.Equal(new[] { "crowdSize", "showNames", "chatty" }, manifest.Settings.Select(s => s.Key));
    }

    [AvaloniaFact]
    public void Plugin_ThroughTheRealLoader_OffersItsViewAndOpensIt()
    {
        using var box = new PluginSandbox();
        var folder = box.WriteFolder(PluginId, File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.AlbumTown/plugin.json")));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Noctis.Plugins.AlbumTown.dll"), Path.Combine(folder, "Noctis.Plugins.AlbumTown.dll"));
        box.Approve((PluginId, new[] { "library.read", "playback.control" }));
        var host = box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.6");
        Control? shown = null;
        host.ViewPresenter = (_, c) => { shown = c; return true; };
        host.LoadAll();

        var plugin = host.Plugins.Single();
        Assert.Equal(PluginStatus.Running, plugin.Status);
        var entry = Assert.Single(plugin.Views);
        Assert.Equal("Album Town", entry.Name);
        Assert.True(entry.Open());
        // Loaded in its own context: the control is the plugin's type, not the test's copy.
        Assert.Equal("Noctis.Plugins.AlbumTown.Views.AlbumTownView", shown!.GetType().FullName);

        var window = new Window { Content = shown, Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PluginStatus.Running, plugin.Status);
        window.Close();

        host.SetEnabled(plugin, false);
        Assert.True(entry.IsWithdrawn);
        Assert.True(host.Remove(plugin, deleteData: true)); // loaded from memory: nothing locked
    }

    [AvaloniaFact]
    public void ReleaseZip_InstallsFromFile_AndRuns()
    {
        var zip = PluginSandbox.RepoFile("plugins/Noctis.Plugins.AlbumTown/bin/Release/Noctis.Plugins.AlbumTown-1.0.0.zip");
        if (!File.Exists(zip)) Assert.Skip("build it first: dotnet build plugins/Noctis.Plugins.AlbumTown -c Release");

        using (var archive = ZipFile.OpenRead(zip))
        {
            var names = archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Contains("Noctis.Plugins.AlbumTown.dll", names);
            Assert.Contains("plugin.json", names);
            Assert.DoesNotContain(names, n => n.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Noctis.Plugins.Abstractions", StringComparison.OrdinalIgnoreCase));
        }

        using var box = new PluginSandbox();
        box.Settings.CommunityPluginsEnabled = true;
        var host = box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.6");
        host.LoadAll();
        var result = host.InstallPackage(zip, allowUpdate: false);
        Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        var plugin = result.Plugin!;
        Assert.Equal(("Album Town", "1.0.0"), (plugin.Name, plugin.Version));
        Assert.Equal(PluginStatus.Disabled, plugin.Status); // waits for the user's approval

        host.SetEnabled(plugin, true);
        Assert.Equal(PluginStatus.Running, plugin.Status);
        Assert.Equal("Album Town", Assert.Single(plugin.Views).Name);
        Assert.True(host.Remove(plugin, deleteData: true));
    }
}
