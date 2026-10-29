using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
/// Album Town (plugin API 1.4): who is a resident (one per album, singles their own), who comes
/// to the plaza, what they say, the plaza state machine, the frame budget for 60 creatures, and
/// the plugin through the real loader and its Release zip. The Apartments are in
/// AlbumTownApartmentsTests.
/// </summary>
[Collection("AlbumTown")] // one at a time with AlbumTownApartmentsTests: the frame budgets count GCs, process-wide
public class AlbumTownPluginTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    internal static TrackInfo T(string title, string album = "Album", string artist = "Artist", int year = 2019, int plays = 0,
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
        public double Zoom { get; set; } = 1;
        public int ZoomSaves;
        public double LoadZoom() => Zoom;
        public void SaveZoom(double zoom) { Zoom = zoom; ZoomSaves++; }
        public void RaiseNowPlaying() => NowPlayingChanged?.Invoke(this, EventArgs.Empty);
        public void RaiseSettings() => SettingsChanged?.Invoke(this, EventArgs.Empty);
        public List<PlaylistInfo> PlaylistList { get; } = new();
        public IReadOnlyList<PlaylistInfo> LoadPlaylists() => PlaylistList;
        public List<IReadOnlyList<string>> PlayedTracks { get; } = new();
        public void PlayTracks(IReadOnlyList<string> trackIds) => PlayedTracks.Add(trackIds);
        public ApartmentLayout? Layout { get; set; }
        public int LayoutSaves;
        public ApartmentLayout? LoadApartments() => ApartmentLayout.FromJson(Layout?.ToJson());
        public void SaveApartments(ApartmentLayout layout) { Layout = layout; LayoutSaves++; }
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
        Assert.Equal(300, view.Shelf.Items.Count); // every album is on the Apartments shelf

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

    // ── 1.2: frame rate, sleeping, labels that keep out of each other's way ──

    [Theory]
    [InlineData("Smooth (60 fps)", true, 60)]
    [InlineData("Balanced (30 fps)", true, 30)]
    [InlineData("Battery saver (15 fps)", true, 15)]
    [InlineData("anything else", true, 30)]
    [InlineData(null, true, 30)]
    [InlineData("Smooth (60 fps)", false, 15)] // the window is not in front
    public void Motion_LetsThroughItsRate_OfThe60HzScreen(string? setting, bool focused, int expected)
    {
        var mode = Motion.Parse(setting);
        var gate = new FrameGate();
        var passed = 0;
        var time = 0f;
        for (var i = 0; i < 600; i++) // ten seconds of a 60 Hz screen, a little jitter
        {
            var at = TimeSpan.FromSeconds(i / 60.0 + (i % 3 - 1) * 0.0015);
            if (gate.Pass(at, Motion.Interval(mode, focused), out var dt)) { passed++; time += dt; }
        }
        Assert.InRange(passed / 10.0, expected * 0.95, expected * 1.02);
        // Time-based: the steps add up to the real time, so walking speed never changes.
        Assert.InRange(time, 9.9f, 10.1f);
    }

    [AvaloniaFact]
    public void Plaza_SleepsWhenNobodyMoves_AndWakesWithItsClocksRunOn()
    {
        var (view, _) = NewView(12);
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var plaza = view.Plaza;
        plaza.ManualFrames = true;
        plaza.FocusOverride = true;
        plaza.Motion = MotionMode.Smooth;
        plaza.Wake();
        plaza.OfferFrame();
        Assert.False(plaza.IsAsleep); // the crowd just arrived: they walk and fade in

        plaza.World.Chatty = false;
        plaza.World.HoldStill(5);
        Assert.True(plaza.World.IsQuiet());
        Assert.InRange(plaza.World.NextChangeIn(), 4.9f, 5f);
        Thread.Sleep(20);
        plaza.OfferFrame();
        Assert.True(plaza.IsAsleep);
        var frames = plaza.Frames;
        for (var i = 0; i < 5; i++) { Thread.Sleep(20); plaza.OfferFrame(); }
        Assert.Equal(frames, plaza.Frames); // asleep: nothing stepped, nothing drawn

        Thread.Sleep(50);
        plaza.Wake(); // the pointer, the music, the crowd: anything that changes
        Assert.False(plaza.IsAsleep);
        Assert.All(plaza.World.Creatures, c => Assert.True(c.StateTimer < 5 - 0.05f)); // the sleep counted
        Thread.Sleep(20);
        plaza.OfferFrame();
        Assert.Equal(frames + 1, plaza.Frames);

        // Somebody moving (a click: it waves and wakes the plaza): no sleep.
        plaza.World.Wave(plaza.World.Creatures[0]);
        plaza.Wake();
        Thread.Sleep(20);
        plaza.OfferFrame();
        Assert.False(plaza.IsAsleep);
        window.Close();
    }

    [AvaloniaFact]
    public void NameTagsAndBubbles_KeepOutOfEachOthersWay_WhenTheCrowdBunchesUp()
    {
        var (view, _) = NewView(16);
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var plaza = view.Plaza;
        plaza.Step(1 / 60f);
        plaza.ShowNames = true;
        var s = (float)plaza.Painter.Size;
        var creatures = plaza.World.Creatures;
        // A tight crowd in the middle, everyone saying hello at once.
        for (var i = 0; i < creatures.Count; i++)
        {
            var c = creatures[i];
            c.Position = new Vector2(500 + i % 4 * s * 1.15f, 350 + i / 4 * s * 1.25f);
            plaza.World.Wave(c);
        }
        for (var i = 0; i < 40; i++) plaza.Sync(); // labels built over a few frames' budgets

        var bubbles = new List<Rect>();
        var names = new List<(Rect Rect, Creature Owner)>();
        foreach (var c in creatures)
        {
            var (name, bubble) = plaza.LabelsOf(c);
            if (bubble is { } b) bubbles.Add(b);
            if (name is { } n) names.Add((n, c));
        }
        Assert.True(bubbles.Count >= 4, $"{bubbles.Count} bubbles shown");
        // Tags that would sit on a neighbour in this crowd wait (they show on hover); the last row's show.
        Assert.True(names.Count >= 2, $"{names.Count} tags shown");
        var bounds = new Rect(plaza.Bounds.Size);
        for (var i = 0; i < bubbles.Count; i++)
        {
            Assert.True(bounds.Contains(bubbles[i]));
            for (var j = i + 1; j < bubbles.Count; j++)
                Assert.False(bubbles[i].Intersects(bubbles[j]), $"bubbles {bubbles[i]} and {bubbles[j]} overlap");
            foreach (var (n, _) in names) Assert.False(bubbles[i].Intersects(n), $"bubble {bubbles[i]} covers tag {n}");
        }
        foreach (var (n, owner) in names)
        {
            foreach (var (other, _) in names)
                if (other != n) Assert.False(n.Intersects(other), $"tags {n} and {other} overlap");
            foreach (var c in creatures)
            {
                if (ReferenceEquals(c, owner)) continue;
                var body = new Rect(c.Position.X - s * 0.72, c.Position.Y - s * 1.42, s * 1.44, s * 1.5);
                Assert.False(n.Intersects(body), $"{owner.Album.Title}'s tag {n} sits on {c.Album.Title}");
            }
        }

        // The hovered one's tag always shows, even in a crowd.
        var hovered = creatures[5];
        plaza.World.Cursor = PlazaWorld.BodyCenter(hovered, s);
        plaza.Step(1 / 60f);
        plaza.Sync();
        Assert.Same(hovered, plaza.Hovered);
        Assert.NotNull(plaza.LabelsOf(hovered).Name);
        window.Close();
    }

    [AvaloniaFact]
    public void Plaza_MovesOnlyWhatChanged_AndReportsIt()
    {
        var (view, _) = NewView(20);
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var plaza = view.Plaza;
        void Frame()
        {
            plaza.Step(1 / 30f);
            plaza.Sync();
            // The compositor draws what was invalidated (the tiles get built and drawn).
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        for (var i = 0; i < 60; i++) Frame();
        plaza.World.Chatty = false;
        plaza.World.HoldStill(10);
        for (var i = 0; i < 3; i++) Frame();
        // Standing still: nothing moves, nothing is repainted (blinks aside).
        Assert.True(plaza.RepaintCount <= 2, plaza.RepaintCount.ToString());
        var walker = plaza.World.Creatures[3];
        plaza.World.Wave(walker);
        Frame();
        Assert.Contains(Enumerable.Range(0, plaza.RepaintCount).Select(plaza.RepaintedAt), v => v is not null);
        Assert.True(plaza.DirtyCount >= 1);
        // The floor is never repainted for a creature: only small rectangles around it.
        for (var i = 0; i < plaza.DirtyCount; i++)
            Assert.True(plaza.DirtyAt(i).Width < plaza.Bounds.Width / 3, plaza.DirtyAt(i).ToString());
        window.Close();
    }

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

    // ── Size, zoom and the pointer (v1.1) ──

    [Fact]
    public void Size_ScalesWithTheWindow_AndTheCrowd_WithoutPilingUp()
    {
        // "Could you make them bigger?": ~96 px covers on a maximized 2560x1440 window (40 out).
        var big = PlazaCanvas.SizeFor(2560, 1352, 1, 40, 1);
        Assert.InRange(big, 90, 110);
        // At 125% scaling the cover is about as many device pixels.
        Assert.InRange(PlazaCanvas.SizeFor(2048, 1080, 1.25, 40, 1) * 1.25, 85, 110);
        // A full crowd of 60 gets a little smaller instead of piling up.
        var full = PlazaCanvas.SizeFor(2560, 1352, 1, 60, 1);
        Assert.InRange(full, 75, big);
        foreach (var (w, h, n) in new[] { (2560.0, 1352.0, 60), (1200.0, 700.0, 60), (900.0, 500.0, 60), (900.0, 500.0, 24) })
            foreach (var zoom in new[] { PlazaCanvas.MinZoom, 1, PlazaCanvas.MaxZoom })
            {
                var s = PlazaCanvas.SizeFor(w, h, 1, n, zoom);
                var footprint = n * Math.Pow(s * 1.45, 2) / (w * h);
                Assert.True(footprint <= 0.52, $"{n} creatures of {s:0} px in {w}x{h} at zoom {zoom} cover {footprint:P0}");
                Assert.True(s >= 20);
            }
        // Zoom grows them, within the crowd's room.
        Assert.True(PlazaCanvas.SizeFor(2560, 1352, 1, 24, 1.4) > PlazaCanvas.SizeFor(2560, 1352, 1, 24, 1));
        Assert.True(PlazaCanvas.SizeFor(2560, 1352, 1, 40, 0.6) < big);
    }

    [Fact]
    public void BigCreatures_KeepTheirDistance_AndStayInsideThePlaza()
    {
        var world = World(Enumerable.Range(0, 40).Select(i => A("big" + i)).ToArray());
        world.Resize(2560, 1352);
        world.BodySize = 96;
        world.Chatty = false;
        Assert.Equal(28 * 96 / 36f, world.Margin, 3);
        for (var i = 0; i < 60 * 30; i++) world.Step(1 / 60f);
        foreach (var c in world.Creatures)
        {
            Assert.InRange(c.Position.X, world.Margin - 0.01f, world.Width - world.Margin + 0.01f);
            Assert.InRange(c.Position.Y, world.TopMargin - 0.01f, world.Height - world.Margin + 0.01f);
        }
        // Spacing scales with the body: nobody stands inside a neighbour's cover.
        var closest = world.Creatures.SelectMany((a, i) => world.Creatures.Skip(i + 1).Select(b => Vector2.Distance(a.Position, b.Position))).Min();
        Assert.True(closest > 96 * 0.8f, $"two creatures {closest:0} px apart");
    }

    [Fact]
    public void TheCreatureUnderThePointer_HoldsStill_AndIsEasyToHit()
    {
        var world = World(A("a"), A("b"));
        world.BodySize = 96;
        var (a, b) = (world.Creatures[0], world.Creatures[1]);
        a.Position = new Vector2(400, 400);
        b.Position = new Vector2(700, 400);
        a.StateTimer = 0; // would set off on a stroll next step

        // The whole cover, the hands beside it and the feet under it all count.
        var center = PlazaWorld.BodyCenter(a, 96);
        Assert.Same(a, world.HitTest(center));
        Assert.Same(a, world.HitTest(center + new Vector2(70, 0)));   // a hand
        Assert.Same(a, world.HitTest(new Vector2(400, 395)));         // the feet
        Assert.Null(world.HitTest(center + new Vector2(0, -90)));     // well above the head

        world.Cursor = center;
        world.Held = a;
        var at = a.Position;
        for (var i = 0; i < 120; i++) world.Step(1 / 60f);
        Assert.Equal(at, a.Position);
        Assert.Equal(CreatureState.LookAtCursor, a.State);
        // A neighbour walking into it does not shove it away.
        b.Position = a.Position + new Vector2(30, 0);
        world.Step(1 / 60f);
        Assert.Equal(at, a.Position);

        world.Held = null;
        world.Cursor = null;
        world.Step(1 / 60f);
        Assert.Equal(CreatureState.Idle, a.State);
    }

    [Fact]
    public void HitTest_KeepsTheHoveredOne_WhileThePointerIsOnIt()
    {
        var world = World(A("a"), A("b"));
        world.BodySize = 96;
        var (a, b) = (world.Creatures[0], world.Creatures[1]);
        a.Position = new Vector2(400, 400);
        b.Position = new Vector2(500, 420); // overlapping targets, b in front
        var between = new Vector2(452, PlazaWorld.BodyCenter(a, 96).Y);
        Assert.Same(b, world.HitTest(between));
        Assert.Same(a, world.HitTest(between, prefer: a));
    }

    [AvaloniaFact]
    public void View_NowPlayingVisitors_NeverGrowThePlazaPastItsSize()
    {
        var (view, host) = NewView(200, crowd: 24);
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        host.IsPlaying = true;
        // An album shuffle: a new album starts 40 times.
        foreach (var track in host.Tracks.Where(t => view.Cast.All(m => !m.Album.TrackIds.Contains(t.Id))).Take(40).ToList())
        {
            host.NowPlaying = new NowPlayingTrack(track.Title, track.Artist, track.Album, track.Duration, track.FilePath, null, 0);
            host.RaiseNowPlaying();
            Assert.Equal(24, view.Cast.Count);
            Assert.Equal(24, view.Plaza.World.Creatures.Count);
            Assert.Contains(view.Plaza.World.Creatures, c => c.IsPlaying && c.Album.TrackIds.Contains(track.Id));
        }
        window.Close();
    }

    [AvaloniaFact]
    public void View_CreatureSize_ComesBackNextTime_AndFollowsCtrlWheel()
    {
        var (view, host) = NewView(60);
        host.Zoom = 1.3;
        view = new AlbumTownView(host, new Random(2));
        Assert.Equal(1.3, view.Plaza.Zoom, 3);
        Assert.Equal(1.3, view.ZoomSlider.Value, 3);

        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        view.ZoomSlider.Value = 0.8;
        Assert.Equal(0.8, view.Plaza.Zoom, 3);

        var plaza = view.Plaza.TranslatePoint(new Point(200, 200), window)!.Value;
        window.MouseWheel(plaza, new Avalonia.Vector(0, 1), Avalonia.Input.RawInputModifiers.Control);
        Assert.Equal(0.9, view.Plaza.Zoom, 3);
        Assert.Equal(0.9, view.ZoomSlider.Value, 3);
        window.MouseWheel(plaza, new Avalonia.Vector(0, -1)); // a plain wheel does not zoom
        Assert.Equal(0.9, view.Plaza.Zoom, 3);

        window.Close(); // saved when the window closes, even before the save timer
        Assert.Equal(0.9, host.Zoom, 3);
        Assert.True(host.ZoomSaves >= 1);
    }

    [AvaloniaFact]
    public void View_FollowsTheAppTheme_AndIsDarkWithoutOne()
    {
        var (view, _) = NewView(10);
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Assert.True(view.Palette.IsDark); // the test app has no Noctis resources: the dark default
        Assert.Same(view.Palette, view.Plaza.Palette);

        window.Resources["AppMainBackgroundColor"] = Colors.White;
        window.Resources["AccentColorBrush"] = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
        view.ApplyTheme(TownTheme.From(view));
        Assert.False(view.Palette.IsDark);
        Assert.Equal(Color.FromRgb(0x0A, 0x84, 0xFF), view.Palette.Accent);
        window.Close();
    }

    // ── Real rendering: frame time and the preview picture (NOCTIS_TEST_SKIA=1) ──

    [AvaloniaFact]
    public void Plaza_WritesThePreview_At2560x1440_DarkAndLight()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_TEST_SKIA") != "1")
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");

        var dir = Environment.GetEnvironmentVariable("NOCTIS_ALBUMTOWN_PREVIEW") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "albumtown-preview");
        Directory.CreateDirectory(dir);
        var host = new FakeTownHost { CrowdSize = 40, Pulse = 0.7 };
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

        var window = new Window { Content = view, Width = 2560, Height = 1440 };
        window.Show();
        host.RaiseNowPlaying();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(40, view.Plaza.World.Creatures.Count);

        // Let them walk and chat for a while, then a click so the card is in the shot.
        for (var i = 0; i < 30 * 14; i++) view.Plaza.Step(1 / 30f);
        var talking = view.Plaza.World.Creatures.Count(c => c.Bubble is not null);
        var w = (float)view.Plaza.Bounds.Width;
        var h = (float)view.Plaza.Bounds.Height;
        // For the picture: the dancer in the open, and a waving neighbour clear of the card.
        view.Plaza.World.Creatures.Single(c => c.IsPlaying).Position = new Vector2(w * 0.6f, h * 0.5f);
        var clicked = view.Plaza.World.Creatures.Where(c => !c.IsPlaying && c.State != CreatureState.Talk)
            .OrderBy(c => Vector2.Distance(c.Position, new Vector2(w * 0.36f, h * 0.3f))).First();

        // Big enough to read the covers: ~96 px on a maximized 2560x1440 window.
        Assert.InRange(view.Plaza.Painter.Size, 90, 110);
        using var tiny = new RenderTargetBitmap(new PixelSize(8, 8));
        for (var i = 0; i < 20; i++) { view.Plaza.Step(1 / 60f); RenderFrame(view, tiny); } // tiles built
        File.WriteAllText(Path.Combine(dir, "preview.txt"), $"{view.Plaza.World.Creatures.Count} creatures, {view.Plaza.Painter.Size:0} px covers, {talking} talking");

        view.Plaza.World.Wave(clicked);
        view.Plaza.Step(1 / 30f);
        view.ShowCard(clicked.Album, clicked.Member.Reason);
        Dispatcher.UIThread.RunJobs();

        view.Plaza.Sync();
        Capture(window, Path.Combine(dir, "preview.png"));
        view.ApplyTheme(TownTheme.Light);
        for (var i = 0; i < 20; i++) { view.Plaza.Step(1 / 60f); RenderFrame(view, tiny); }
        Dispatcher.UIThread.RunJobs();
        view.Plaza.Sync();
        Capture(window, Path.Combine(dir, "preview-light.png"));
        view.ApplyTheme(TownTheme.Dark);
        view.HideCard();
        view.GetLogicalDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Apartments")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        foreach (var a in town) view.ResidentCovers.GetNow(a.ArtworkPath);
        view.ApartmentsView.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
        Capture(window, Path.Combine(dir, "preview-apartments.png"));
        window.Close();
        Assert.True(albums > 60);
    }

    /// <summary>
    /// The live complaint ("laggy and stuttering" with 40 of 3033 albums at 2560x1440): the UI
    /// thread's share of a frame, measured the way Avalonia records it (Render into the
    /// compositor's RenderDataDrawingContext), plus the bytes allocated and GCs per frame, and
    /// the full CPU raster of a frame for scale. Numbers go to bench.txt next to the preview.
    /// </summary>
    [AvaloniaFact]
    public void Plaza_At2560x1440_60Creatures_StaysInTheFrameAndAllocationBudget()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_TEST_SKIA") != "1")
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var dir = Environment.GetEnvironmentVariable("NOCTIS_ALBUMTOWN_PREVIEW") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "albumtown-preview");
        Directory.CreateDirectory(dir);
        var (view, window, _) = BigPlaza(dir, 2560, 1440);
        try
        {
            var plaza = view.Plaza;
            var recorder = new Recorder(plaza);
            // What the compositor does each frame: record the visuals whose content changed.
            void RecordChanged()
            {
                for (var r = 0; r < plaza.RepaintCount; r++) recorder.Record(plaza.RepaintedAt(r));
            }
            for (var i = 0; i < 120; i++) { plaza.Step(1 / 60f); plaza.Sync(); RecordChanged(); } // warm up (JIT, caches)

            const int frames = 600;
            var times = new double[frames];
            // Start from a settled heap: what earlier tests left behind is not this budget's.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
            var stepBytes = 0L; var syncBytes = 0L; var renderBytes = 0L; var images = 0L;
            for (var i = 0; i < frames; i++)
            {
                var a0 = GC.GetAllocatedBytesForCurrentThread();
                var t0 = Stopwatch.GetTimestamp();
                plaza.Step(1 / 60f);
                var aStep = GC.GetAllocatedBytesForCurrentThread();
                plaza.Sync();
                var a1 = GC.GetAllocatedBytesForCurrentThread();
                syncBytes += a1 - aStep;
                RecordChanged();
                times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                renderBytes += GC.GetAllocatedBytesForCurrentThread() - a1;
                stepBytes += aStep - a0;
                images += plaza.Painter.ImagesDrawn + 1; // + the floor
            }
            var gcs = (GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2);
            string[] sampled;
            double ownShare;
            using (var listener = new AllocListener())
            {
                for (var i = 0; i < 3000; i++) { plaza.Step(1 / 60f); plaza.Sync(); RecordChanged(); }
                sampled = listener.Types.OrderByDescending(k => k.Value).Select(k => $"{k.Value} x {k.Key}").ToArray();
                ownShare = OwnShare(listener.Types);
            }
            File.WriteAllText(Path.Combine(dir, "plaza-alloc.txt"), string.Join("\n", sampled));
            Array.Sort(times);
            var mean = times.Average();
            var p95 = times[(int)(frames * 0.95)];
            var stepPer = stepBytes / (double)frames;
            var syncPer = syncBytes / (double)frames;
            var renderPer = renderBytes / (double)frames;
            var imagesPer = images / (double)frames;

            var size = new PixelSize((int)plaza.Bounds.Width, (int)plaza.Bounds.Height);
            double raster;
            using (var bmp = new RenderTargetBitmap(size))
            {
                for (var i = 0; i < 5; i++) RenderFrame(view, bmp);
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < 30; i++) { plaza.Step(1 / 60f); RenderFrame(view, bmp); }
                raster = sw.Elapsed.TotalMilliseconds / 30;
            }
            var report = $"{plaza.World.Creatures.Count} creatures, plaza {size.Width}x{size.Height}, {frames} frames\n"
                + $"UI thread (step + record): mean {mean:0.000} ms, p95 {p95:0.000} ms, max {times[^1]:0.000} ms\n"
                + $"allocated per frame: world {stepPer:0} B, moving the visuals {syncPer:0} B (Avalonia's property-change args), recording what changed {renderPer:0} B ({imagesPer:0} bitmaps)\n"
                + $"allocation samples over 3000 frames: {string.Join("; ", sampled.Take(8))}\n"
                + $"GCs over {frames} frames: gen0 {gcs.Item1}, gen1 {gcs.Item2}, gen2 {gcs.Item3}\n"
                + $"full CPU raster (headless Skia, step + render into a bitmap): {raster:0.00} ms per frame\n";
            File.WriteAllText(Path.Combine(dir, "bench.txt"), report);
            Console.WriteLine(report);

            Assert.True(p95 < 4, report);
            Assert.True(stepPer < 64, report); // the world: nothing (a new chat's lines now and then)
            Assert.True(stepPer + syncPer + renderPer < 80 * 1024, report);
            // Each changed visual's re-recording is Avalonia's own render data (~1.8 KB a visual):
            // short-lived garbage, ~34 MB over these 600 frames. In a test process full of earlier
            // tests' survivors one of its collections may reach gen1; nothing lives to gen2.
            Assert.True(gcs.Item2 <= 1 && gcs.Item3 == 0, report);
            // The samples are Avalonia's own objects. The plugin's types, closures and text layout only
            // show up when a new chat builds its two lines (once per line): a sliver at most.
            Assert.True(ownShare <= 0.02, $"plugin share of the samples {ownShare:P1}\n{report}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The owner's live measurement (1.1.1: ~61% of a core with the plaza open at 2560x1440,
    /// maximized, 40 creatures) came from repainting the whole plaza 60 times a second. This runs
    /// the plaza in real time (60 Hz screen frames, slept between) and does per frame what the app
    /// does: step, move the visuals, record what changed, and rasterize with Skia on the CPU into a
    /// 2560x1341 surface that persists between frames. The 1.1 way repaints everything every
    /// frame; 1.2 repaints only the rectangles the frame dirtied (the plaza reports them), merged
    /// to at most 8 like Avalonia's compositor. Headless Avalonia itself repaints the whole window
    /// on every frame (measured: ~18 ms either way), so it cannot show this.
    /// Numbers go to plaza-cpu.txt.
    /// </summary>
    [AvaloniaFact]
    public void Plaza_AtBalanced_CostsAThirdOrLessOfThe60fpsFullRedraw()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_TEST_SKIA") != "1")
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var dir = Environment.GetEnvironmentVariable("NOCTIS_ALBUMTOWN_PREVIEW") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "albumtown-preview");
        Directory.CreateDirectory(dir);
        var (view, window, host) = BigPlaza(dir, 2560, 1440, crowd: 40);
        try
        {
            var plaza = view.Plaza;
            plaza.FocusOverride = true;
            plaza.ManualFrames = true; // the test offers the screen's frames (headless ones stop after a few)
            var recorder = new Recorder(plaza);
            Action<DrawingContext> all = plaza.RenderAll;
            var surface = new RenderTargetBitmap(new PixelSize((int)plaza.Bounds.Width, (int)plaza.Bounds.Height));
            var rects = new List<Rect>(256);

            var parts = new double[3];
            var one = new Rect[1];
            double repaints = 0, dirtyRects = 0;
            (double BusyMs, double Frames, double Pixels) Run(bool full, MotionMode mode, double seconds, bool sprites = true)
            {
                Array.Clear(parts);
                repaints = dirtyRects = 0;
                plaza.Painter.SpriteLimbs = sprites; // false: hands, feet and eyes drawn as shapes, as 1.1 did
                plaza.ForceFullRedraw = full;
                plaza.Motion = mode;
                plaza.Wake();
                var clock = Stopwatch.StartNew();
                var busy = 0.0;
                var pixels = 0.0;
                var frames0 = plaza.Frames;
                var tick = 0;
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    var wait = TimeSpan.FromSeconds(++tick / 60.0) - clock.Elapsed;
                    if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                    var before = plaza.Frames;
                    var t0 = Stopwatch.GetTimestamp();
                    plaza.OfferFrame();
                    var t1 = Stopwatch.GetTimestamp();
                    busy += Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
                    parts[0] += Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
                    if (plaza.Frames == before && !full) continue; // let pass: nothing to do

                    if (full)
                    {
                        var t2 = Stopwatch.GetTimestamp();
                        recorder.Record(all);
                        using (var ctx = surface.CreateDrawingContext()) plaza.RenderAll(ctx);
                        busy += Stopwatch.GetElapsedTime(t2).TotalMilliseconds;
                        pixels += plaza.Bounds.Width * plaza.Bounds.Height;
                        continue;
                    }
                    rects.Clear();
                    for (var i = 0; i < plaza.DirtyCount; i++) rects.Add(plaza.DirtyAt(i).Intersect(new Rect(plaza.Bounds.Size)));
                    MergeDirty(rects, 8);
                    var t3 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < plaza.RepaintCount; i++) recorder.Record(plaza.RepaintedAt(i));
                    parts[1] += Stopwatch.GetElapsedTime(t3).TotalMilliseconds;
                    if (rects.Count > 0)
                    {
                        // Each dirty rectangle repainted under its own (cheap, pixel-aligned) clip.
                        using var ctx = surface.CreateDrawingContext(false);
                        foreach (var r in rects)
                        {
                            one[0] = r;
                            using (ctx.PushClip(r)) plaza.RenderRegion(ctx, one);
                        }
                    }
                    repaints += plaza.RepaintCount;
                    dirtyRects += rects.Count;
                    busy += Stopwatch.GetElapsedTime(t3).TotalMilliseconds;
                    parts[2] += Stopwatch.GetElapsedTime(t3).TotalMilliseconds;
                    foreach (var r in rects) pixels += r.Width * r.Height;
                }
                return (busy / seconds, (plaza.Frames - frames0) / seconds, pixels / seconds);
            }

            // What opening and closing a drawing context on the surface costs by itself (nothing drawn).
            for (var i = 0; i < 10; i++) using (surface.CreateDrawingContext(false)) { }
            var open = Stopwatch.StartNew();
            for (var i = 0; i < 100; i++) using (surface.CreateDrawingContext(false)) { }
            var perOpen = open.Elapsed.TotalMilliseconds / 100;
            File.WriteAllText(Path.Combine(dir, "dbg3.txt"), $"open/close {perOpen:0.000} ms");
            Run(full: true, MotionMode.Smooth, 1, sprites: false); // warm up every way
            Run(full: true, MotionMode.Smooth, 1);
            Run(full: false, MotionMode.Balanced, 1);
            const double secs = 3;
            // The two that are compared: three rounds each, taken in turn, the best of each kept
            // (other work on the machine only ever adds time).
            (double BusyMs, double Frames, double Pixels) old = (double.MaxValue, 0, 0), balanced = (double.MaxValue, 0, 0);
            var balancedParts = "";
            for (var round = 0; round < 3; round++)
            {
                var o = Run(full: true, MotionMode.Smooth, secs, sprites: false);
                if (o.BusyMs < old.BusyMs) old = o;
                var b = Run(full: false, MotionMode.Balanced, secs);
                if (b.BusyMs < balanced.BusyMs)
                {
                    balanced = b;
                    balancedParts = $"step and move {parts[0] / secs:0.0} ms, record {parts[1] / secs:0.0} ms, raster {(parts[2] - parts[1]) / secs:0.0} ms per second; "
                        + $"per frame {repaints / (b.Frames * secs):0.0} visuals repainted, {dirtyRects / (b.Frames * secs):0.0} dirty rects";
                }
            }
            var fullNew = Run(full: true, MotionMode.Smooth, secs);
            var smooth = Run(full: false, MotionMode.Smooth, secs);
            var battery = Run(full: false, MotionMode.BatterySaver, secs);
            plaza.FocusOverride = false;
            var away = Run(full: false, MotionMode.Smooth, secs);
            plaza.FocusOverride = true;

            // Nothing moving: the plaza sleeps and costs nothing until someone sets off.
            host.IsPlaying = false;
            host.RaiseNowPlaying();
            plaza.World.Chatty = false;
            plaza.World.HoldStill(3);
            var quiet = Run(full: false, MotionMode.Balanced, 1);

            string Line(string name, (double BusyMs, double Frames, double Pixels) r)
                => $"{name}: {r.Frames:0} frames/s, {r.BusyMs:0.0} ms of CPU per second ({r.BusyMs / 10:0.0}% of a core), {r.Pixels / 1e6:0.0} Mpx repainted per second";
            var report = $"{plaza.World.Creatures.Count} creatures, plaza {plaza.Bounds.Width:0}x{plaza.Bounds.Height:0}, real-time 60 Hz, {secs:0} s each (the 1.1 way and Balanced: best of 3), CPU Skia raster\n"
                + Line("1.1 way: 60 fps, whole plaza repainted, 1.1 drawing", old) + "\n"
                + Line("1.2 drawing, 60 fps, whole plaza repainted (for comparison)", fullNew) + "\n"
                + Line("1.2 Smooth (60 fps, only what changed)", smooth) + "\n"
                + Line("1.2 Balanced (30 fps, default)", balanced) + $" [{balancedParts}]\n"
                + Line("1.2 Battery saver (15 fps)", battery) + "\n"
                + Line("1.2 window not in front (Smooth chosen: 15 fps)", away) + "\n"
                + Line("1.2 nobody moving (asleep)", quiet) + "\n"
                + $"Balanced vs the 1.1 way: {old.BusyMs / Math.Max(0.01, balanced.BusyMs):0.0}x less CPU, {old.Pixels / Math.Max(1, balanced.Pixels):0.0}x fewer pixels; "
                + $"vs a full 60 fps repaint with the 1.2 drawing: {fullNew.BusyMs / Math.Max(0.01, balanced.BusyMs):0.0}x less CPU\n";
            File.WriteAllText(Path.Combine(dir, "plaza-cpu.txt"), report);
            Console.WriteLine(report);
            surface.Dispose();

            Assert.InRange(balanced.Frames, 24, 33);
            Assert.InRange(battery.Frames, 11, 17);
            Assert.InRange(away.Frames, 11, 17);
            Assert.True(balanced.BusyMs * 3 <= old.BusyMs, report);
            Assert.True(quiet.Frames <= 2, report);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Merges dirty rectangles: overlapping ones first, then the pair that wastes the
    /// least area, until at most <paramref name="max"/> remain (as a compositor does).</summary>
    private static void MergeDirty(List<Rect> rects, int max)
    {
        for (var merged = true; merged;)
        {
            merged = false;
            for (var i = 0; i < rects.Count && !merged; i++)
                for (var j = i + 1; j < rects.Count; j++)
                    if (rects[i].Intersects(rects[j]))
                    {
                        rects[i] = rects[i].Union(rects[j]);
                        rects.RemoveAt(j);
                        merged = true;
                        break;
                    }
        }
        while (rects.Count > max)
        {
            var (bi, bj, best) = (0, 1, double.MaxValue);
            for (var i = 0; i < rects.Count; i++)
                for (var j = i + 1; j < rects.Count; j++)
                {
                    var u = rects[i].Union(rects[j]);
                    var waste = u.Width * u.Height - rects[i].Width * rects[i].Height - rects[j].Width * rects[j].Height;
                    if (waste < best) (bi, bj, best) = (i, j, waste);
                }
            rects[bi] = rects[bi].Union(rects[bj]);
            rects.RemoveAt(bj);
        }
    }

    /// <summary>The share of allocation samples that are the plugin's own types, closures or text layout.</summary>
    internal static double OwnShare(IReadOnlyDictionary<string, int> samples)
    {
        var total = samples.Values.Sum();
        var own = samples.Where(k => k.Key.Contains("AlbumTown") || k.Key.Contains("DisplayClass") || k.Key.Contains("FormattedText") || k.Key.Contains("TextLayout"))
            .Sum(k => k.Value);
        return total == 0 ? 0 : own / (double)total;
    }

    private sealed class ImageProbe : Control
    {
        public RenderTargetBitmap? Image;
        public override void Render(DrawingContext context)
        {
            for (var i = 0; i < 100; i++) context.DrawImage(Image!, new Rect(0, 0, 16, 16), new Rect(i, 0, 16, 16));
        }
    }

    /// <summary>Bytes Avalonia allocates per DrawImage it records (the floor under any frame budget).</summary>
    internal static double AvaloniaBytesPerDrawImage(Window window)
    {
        using var image = new RenderTargetBitmap(new PixelSize(16, 16));
        var probe = new ImageProbe { Image = image };
        var old = window.Content;
        window.Content = probe;
        Dispatcher.UIThread.RunJobs();
        var recorder = new Recorder(probe);
        for (var i = 0; i < 20; i++) recorder.Record(probe);
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++) recorder.Record(probe);
        var per = (GC.GetAllocatedBytesForCurrentThread() - a0) / 50.0 / 100;
        window.Content = old;
        Dispatcher.UIThread.RunJobs();
        return per;
    }

    /// <summary>Records a control the way the compositor does on the UI thread (Avalonia's
    /// internal RenderDataDrawingContext, reused frame to frame like a visual's own).</summary>
    internal sealed class Recorder
    {
        private readonly DrawingContext _ctx;
        private readonly Func<object?> _results;

        public Recorder(Visual visual)
        {
            var compositor = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(visual)!.Compositor;
            var type = typeof(DrawingContext).Assembly.GetType("Avalonia.Rendering.Composition.Drawing.RenderDataDrawingContext", throwOnError: true)!;
            _ctx = (DrawingContext)Activator.CreateInstance(type, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null, new object[] { compositor }, null)!;
            _results = type.GetMethod("GetRenderResults", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
                .CreateDelegate<Func<object?>>(_ctx);
        }

        public void Record(Control control)
        {
            control.Render(_ctx);
            (_results() as IDisposable)?.Dispose();
        }

        public void Record(Action<DrawingContext> draw)
        {
            draw(_ctx);
            (_results() as IDisposable)?.Dispose();
        }
    }

    /// <summary>A maximized-size town: 60 residents with covers, one dancing, all walked in.</summary>
    private static (AlbumTownView View, Window Window, FakeTownHost Host) BigPlaza(string dir, int width, int height, int crowd = 60)
    {
        var host = new FakeTownHost { CrowdSize = crowd, Pulse = 0.7 };
        var covers = PreviewCovers(Path.Combine(dir, "covers"));
        for (var i = 0; i < 120; i++)
        {
            var id = Guid.NewGuid().ToString("D");
            for (var n = 0; n < 3; n++)
                host.Tracks.Add(T($"Song {i} {n}", album: $"Album number {i}", artist: $"Artist {i % 23}", year: 1990 + i % 30, plays: i % 40,
                    last: Now.AddDays(-(i % 50)), albumId: id, art: covers[i % covers.Count], number: n + 1));
        }
        var view = new AlbumTownView(host, new Random(7));
        var town = AlbumGrouping.Group(host.Tracks, out var map);
        view.SetTown(town, map, Array.Empty<string>());
        foreach (var a in town) view.Covers.GetNow(a.ArtworkPath);
        var playing = host.Tracks.First(t => view.Cast.Any(m => m.Album.TrackIds.Contains(t.Id)));
        host.NowPlaying = new NowPlayingTrack(playing.Title, playing.Artist, playing.Album, playing.Duration, playing.FilePath, playing.ArtworkPath, 120);
        host.IsPlaying = true;
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        host.RaiseNowPlaying();
        Dispatcher.UIThread.RunJobs();
        for (var i = 0; i < 60 * 10; i++) view.Plaza.Step(1 / 60f);
        return (view, window, host);
    }

    private static void RenderFrame(AlbumTownView view, RenderTargetBitmap bmp)
    {
        view.Plaza.Sync();
        using var ctx = bmp.CreateDrawingContext();
        view.Plaza.RenderAll(ctx);
    }

    /// <summary>The window as the compositor draws it (a real frame).</summary>
    internal static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        frame!.Save(path);
    }

    /// <summary>Made-up album covers (gradients and shapes) for the preview.</summary>
    internal static List<string> PreviewCovers(string dir)
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

    // ── Speech bubbles and name tags: whole lines at every size and density (NOCTIS_TEST_SKIA=1) ──

    private static readonly double[] Densities = { 1.0, 1.25, 1.5, 2.0 };
    private static readonly double[] Zooms = { 0.6, 0.8, 1.0, 1.2, 1.4, 1.6 };

    /// <summary>Every line the town can say, with the longest values the generator takes.</summary>
    private static List<string> EveryTalkLine(out HashSet<string> topics)
    {
        var longArtist = "Marlow and the Extraordinarily Long Named Orchestra";
        var longGenre = "Progressive Experimental Electronic Dance";
        var albums = new[]
        {
            A("a", artist: longArtist, year: 1975, plays: 99999, last: Now.AddDays(-400), single: true, tracks: 1, genre: longGenre, favorite: true),
            A("b", artist: longArtist, year: 1975, plays: 0, tracks: 9999, genre: longGenre),
            A("c", artist: "x", year: 2024, plays: 12, last: Now.AddDays(-1), tracks: 40, genre: "Hip-Hop/Rap"),
            A("d", artist: "y", year: 2024, plays: 1, last: Now, tracks: 3, genre: "Hip-Hop/Rap"),
            A("e", artist: "", year: 0, plays: 4, last: Now.AddDays(-2), tracks: 1, single: true, genre: ""),
            A("f", artist: "z", year: 1999, plays: 10000, last: Now, tracks: 12),
        };
        var lines = new List<string>();
        topics = new HashSet<string>();
        foreach (var x in albums)
        {
            lines.Add(TalkLines.Greeting(x, Now));
            foreach (var y in albums)
            {
                if (ReferenceEquals(x, y)) continue;
                foreach (var chat in TalkLines.Topics(x, y, Now))
                {
                    topics.Add(chat.Topic);
                    lines.Add(chat.Opener);
                    lines.Add(chat.Reply);
                }
            }
        }
        return lines.Distinct().ToList();
    }

    [Fact]
    public void TalkLines_CoverEveryTopic_AndShortenNeverSplitsASurrogatePair()
    {
        EveryTalkLine(out var topics);
        Assert.Equal(new[] { "artist", "decades", "favorite", "fresh", "genre", "hello", "lonely", "long", "plays", "single", "year" },
            topics.OrderBy(t => t, StringComparer.Ordinal));
        // A long genre is shortened like a long artist (it used to go in whole).
        var genre = TalkLines.Topics(A("a", genre: new string('g', 80)), A("b", genre: new string('g', 80), artist: "o", year: 2000), Now).Single(c => c.Topic == "genre");
        Assert.True(genre.Opener.Length < 40, genre.Opener);
        var emoji = new string('a', 22) + "\U0001F3B5\U0001F3B5\U0001F3B5";
        var cut = TalkLines.Shorten(emoji, 24);
        Assert.EndsWith("...", cut);
        Assert.False(char.IsHighSurrogate(cut[^4]), cut);
        Assert.Equal("short", TalkLines.Shorten("short", 24));
    }

    [AvaloniaFact]
    public void Bubbles_ShowTheWholeLine_AtEverySizeAndDensity()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_TEST_SKIA") != "1")
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var lines = EveryTalkLine(out _);
        var checkedCount = 0;
        foreach (var scaling in Densities)
        foreach (var zoom in Zooms)
        foreach (var (screenW, screenH) in new[] { (2560, 1340), (1100, 640) })
        {
            using var covers = new CoverCache();
            using var painter = new CreaturePainter(covers) { Scaling = scaling };
            painter.Size = PlazaCanvas.SizeFor(screenW / scaling, screenH / scaling, scaling, 40, zoom);
            painter.AreaWidth = screenW / scaling;
            foreach (var line in lines)
            {
                var m = painter.MeasureBubble(line);
                var at = $"'{line}' at {zoom:0.0}x zoom, {scaling:0.00}x density, size {painter.Size:0.0}, font {m.FontSize}, wrap {m.Wrap}";
                // Wraps to at most two lines, so nothing is trimmed...
                Assert.True(m.Lines <= CreaturePainter.BubbleMaxLines, $"{m.Lines} lines: {at}");
                Assert.True(Math.Abs(m.FullTextHeight - m.Text.Height) < 0.5, $"trimmed: {at}");
                // ...the text box (plus its padding) fits inside the bitmap...
                Assert.True(m.Text.Right + m.PadX <= m.Width + 0.01 && m.Text.Bottom + m.PadY <= m.Height + 0.01, $"text {m.Text} in {m.Width}x{m.Height}: {at}");
                // ...and the bitmap is exactly the drawn size in device pixels (blitted 1:1).
                Assert.Equal(m.Pixels.Width, (int)Math.Round(m.Width * scaling));
                Assert.Equal(m.Pixels.Height, (int)Math.Round(m.Height * scaling));
                checkedCount++;
            }
            // Drawn the way a frame draws it: the screen shows the WHOLE bitmap, not a corner of it.
            foreach (var line in lines.Where((_, i) => i % 4 == 0))
            {
                var m = painter.MeasureBubble(line);
                var px = new PixelSize((int)Math.Ceiling((m.Width + 16) * scaling), (int)Math.Ceiling((m.Height + 16) * scaling));
                using var target = new RenderTargetBitmap(px, new Avalonia.Vector(96 * scaling, 96 * scaling));
                Rect? where;
                using (var ctx = target.CreateDrawingContext())
                    where = painter.DrawBubbleLine(ctx, line, 8 + m.Width / 2, 8 + m.Height, 400, new Rect(0, 0, px.Width / scaling, px.Height / scaling));
                Assert.NotNull(where);
                var drawn = Pixels(target);
                var own = Pixels(m.Bitmap);
                int ox = (int)Math.Round(where!.Value.X * scaling), oy = (int)Math.Round(where.Value.Y * scaling);
                long diff = 0;
                for (var y = 0; y < m.Pixels.Height; y++)
                    for (var x = 0; x < m.Pixels.Width * 4; x++)
                        diff += Math.Abs(drawn[((oy + y) * px.Width + ox) * 4 + x] - own[y * m.Pixels.Width * 4 + x]);
                var mean = diff / (double)(m.Pixels.Width * m.Pixels.Height * 4);
                Assert.True(mean < 2, $"drawn bubble differs from its bitmap (mean diff {mean:0.0}): '{line}' at {scaling}x");
            }
        }
        Assert.True(checkedCount > 1000, checkedCount.ToString());
    }

    [AvaloniaFact]
    public void BubblesAndNameTags_StayInsideThePlaza_NearEveryEdge()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_TEST_SKIA") != "1")
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var world = World(A("a", title: "A Very Long Album Title That Goes On And On", plays: 12, last: DateTimeOffset.Now));
        var c = world.Creatures[0];
        world.Wave(c); // says hello: a bubble
        Assert.NotNull(c.Bubble);
        using var covers = new CoverCache();
        foreach (var scaling in Densities)
        foreach (var zoom in new[] { 0.6, 1.6 })
        {
            using var painter = new CreaturePainter(covers) { Scaling = scaling };
            var bounds = new Rect(0, 0, 1200, 700);
            painter.Size = PlazaCanvas.SizeFor(bounds.Width, bounds.Height, scaling, 40, zoom);
            painter.AreaWidth = bounds.Width;
            foreach (var (x, y) in new[] { (2f, 2f), (1198f, 2f), (2f, 698f), (1198f, 698f), (600f, 30f), (600f, 350f) })
            {
                c.Position = new Vector2(x, y);
                var bubble = painter.BubbleBounds(c, bounds)!.Value;
                var name = painter.NameBounds(c, bounds)!.Value;
                Assert.True(bounds.Contains(bubble), $"bubble {bubble} at ({x},{y}) {scaling}x");
                Assert.True(bounds.Contains(name), $"name {name} at ({x},{y}) {scaling}x");
                // Room above the head: the bubble sits above it; at the top edge it flips under the
                // feet instead of covering the face.
                if (y > 300) Assert.True(bubble.Bottom <= y - painter.Size, $"bubble {bubble} not above the head at ({x},{y})");
                if (y < 10) Assert.True(bubble.Y >= name.Bottom - 0.5 || bubble.Bottom > bounds.Bottom - 8, $"bubble {bubble} not below the tag {name}");
            }
        }
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        using var copy = new Bitmap(stream);
        var w = copy.PixelSize.Width;
        var len = w * 4 * copy.PixelSize.Height;
        var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(len);
        try
        {
            copy.CopyPixels(new PixelRect(copy.PixelSize), ptr, len, w * 4);
            var bytes = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, len);
            return bytes;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
        }
    }

    // ── The plugin itself ──

    private const string PluginId = "dev.noctis.plugins.albumtown";

    [Fact]
    public void Manifest_Is14_CreditsJafezy_AndAsksOnlyForWhatItUses()
    {
        var json = File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.AlbumTown/plugin.json"));
        var manifest = PluginManifest.Parse(json);
        Assert.Equal((PluginId, "Album Town", "1.4"), (manifest.Id, manifest.Name, manifest.ApiVersion));
        Assert.Equal(new[] { "library.read", "playback.control" }, manifest.Permissions);
        Assert.Empty(manifest.Warnings);
        // The author is Jafezy JD: Noctis is not credited as the author anywhere a user reads it.
        Assert.Equal("Jafezy JD", manifest.Author);
        Assert.Equal("1.0.1", manifest.Version);
        var plugin = new AlbumTownPlugin();
        Assert.Equal(("Jafezy JD", "1.0.1", AlbumTownPlugin.About), (plugin.Info.Author, plugin.Info.Version, plugin.Info.Description));
        // No by-line in the description: the card already says "by Jafezy JD".
        Assert.Equal(AlbumTownPlugin.About, manifest.Description);
        Assert.DoesNotContain("Jafezy", manifest.Description);
        Assert.Equal("Album Town by Jafezy JD", AlbumTownView.Credit);
        Assert.Equal("by Jafezy JD", AlbumTownView.Byline);
        foreach (var text in new[] { manifest.Description, manifest.Author, AlbumTownPlugin.About, AlbumTownView.Credit, AlbumTownView.Byline })
        {
            Assert.DoesNotContain("Noctis", text);
            Assert.DoesNotContain("idea by", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('\u2014', text);
        }
        Assert.DoesNotContain('\u2014', json);
        Assert.Equal(new AlbumTownPlugin().Info.Id, manifest.Id);
        Assert.NotNull(manifest.CheckCompatibility("1.6.5")); // before API 1.3/1.4
        Assert.Equal("1.6.6", manifest.MinAppVersion); // 1.6.6 ships API 1.4
        Assert.Null(manifest.CheckCompatibility("1.6.6"));
        Assert.Equal(new[] { "crowdSize", "showNames", "chatty", "motion" }, manifest.Settings.Select(s => s.Key));
        var motion = manifest.Settings.Single(s => s.Key == "motion");
        Assert.Equal(new[] { Motion.Smooth, Motion.Balanced, Motion.BatterySaver }, motion.Choices);
        Assert.Equal(Motion.Balanced, motion.Default);
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
        var zip = PluginSandbox.RepoFile("plugins/Noctis.Plugins.AlbumTown/bin/Release/Noctis.Plugins.AlbumTown-1.0.1.zip");
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
        Assert.Equal(("Album Town", "1.0.1", "Jafezy JD"), (plugin.Name, plugin.Version, plugin.Manifest?.Author));
        Assert.Equal(PluginStatus.Disabled, plugin.Status); // waits for the user's approval

        host.SetEnabled(plugin, true);
        Assert.Equal(PluginStatus.Running, plugin.Status);
        Assert.Equal("Album Town", Assert.Single(plugin.Views).Name);
        Assert.True(host.Remove(plugin, deleteData: true));
    }
}
