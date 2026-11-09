using System.Diagnostics;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Plugins;
using Noctis.Plugins.AlbumRoulette;
using Noctis.Plugins.AlbumRoulette.Roulette;
using Noctis.Plugins.AlbumRoulette.Views;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Album Roulette (official, plugin API 1.4): the filter chips, Album of the Day (once a day, a
/// stable pick, the anniversary line), the empty state, a spin landing on a match, the reel's
/// frame budget (no allocations per frame), and the Release zip through the real installer.
/// Pictures (NOCTIS_TEST_SKIA=1): the window at 125% to NOCTIS_PLUGIN_PREVIEW (default: temp).
/// </summary>
[Collection("AlbumTown")] // the frame budget counts allocations; keep it away from the other budget tests
public class AlbumRoulettePluginTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    internal static TrackInfo T(string title, string album = "Album", string artist = "Artist", int year = 2006, int plays = 0,
        DateTimeOffset? last = null, string genre = "Pop", string releaseType = "Album", string? albumId = null, string? art = null,
        string albumArtist = "", int number = 1)
        => new(Guid.NewGuid().ToString("D"), title, artist, album, albumArtist, TimeSpan.FromMinutes(3), year, genre, number,
            "C:/music/" + title + ".flac", false, plays, 0)
        {
            AlbumId = albumId ?? "", ReleaseType = releaseType, LastPlayed = last, ArtworkPath = art,
        };

    private static readonly string NightfallId = Guid.NewGuid().ToString();

    internal static IReadOnlyList<RouletteAlbum> Library() => AlbumCatalog.Group(new[]
    {
        T("a1", "Nightfall", "Nova", 2006, plays: 3, last: Now.AddDays(-3), genre: "Electronic", albumId: NightfallId),
        T("a2", "Nightfall", "Nova", 2006, plays: 1, last: Now.AddDays(-10), genre: "Electronic", number: 2, albumId: NightfallId),
        T("b1", "Paper Moons", "Kite", 1996, genre: "Rock", releaseType: "EP"),
        T("c1", "Glasshouse", "Ada", 2019, plays: 5, last: Now.AddMonths(-8), genre: "Pop", releaseType: "Single"),
        T("d1", "Hits of the 90s", "Various Artists", 1999, genre: "Pop", releaseType: "Compilation"),
        T("e1", "Low Tide", "Lo", 0, plays: 2, last: Now.AddMonths(-7), genre: "Jazz"),
        T("f1", "Saltwater", "Nova", 2016, genre: "Electronic", albumArtist: "Nova"),
        T("g1", "Feature", "Nova", 2016, genre: "Rock", albumArtist: "Kite"),
    });

    // ── Grouping and filters ──

    [Fact]
    public void Catalog_GroupsTracksIntoAlbums_WithKindsYearsAndPlays()
    {
        var lib = Library();
        Assert.Equal(7, lib.Count);
        var night = lib.Single(a => a.Title == "Nightfall");
        Assert.Equal(2, night.TrackIds.Count);
        Assert.Equal(4, night.PlayCount);
        Assert.Equal(Now.AddDays(-3), night.LastPlayed);
        Assert.Equal(2000, night.Decade);
        Assert.Equal(ReleaseKind.EP, lib.Single(a => a.Title == "Paper Moons").Kind);
        Assert.Equal(ReleaseKind.Single, lib.Single(a => a.Title == "Glasshouse").Kind);
        Assert.True(lib.Single(a => a.Title == "Hits of the 90s").IsCompilation);
        Assert.Equal(ReleaseKind.Album, lib.Single(a => a.Title == "Hits of the 90s").Kind); // a compilation counts as an album
        Assert.Contains("Nova", lib.Single(a => a.Title == "Feature").Artists);
    }

    [Fact]
    public void Filters_Genre_Decade_Kinds_Played_Artist_AndCompilations()
    {
        var lib = Library();
        string[] Titles(RouletteFilter f) => f.Apply(lib, Now).Select(a => a.Title).OrderBy(t => t).ToArray();

        var f = new RouletteFilter();
        Assert.Equal(7, Titles(f).Length);
        Assert.False(f.IsActive);

        f.Genre = "electronic"; // case-insensitive
        Assert.Equal(new[] { "Nightfall", "Saltwater" }, Titles(f));
        f.Genre = null;

        f.Decade = 1990;
        Assert.Equal(new[] { "Hits of the 90s", "Paper Moons" }, Titles(f));
        f.Decade = null;

        f.EPs = true;
        f.Singles = true;
        Assert.Equal(new[] { "Glasshouse", "Paper Moons" }, Titles(f));
        f.EPs = f.Singles = false;
        f.Albums = true;
        Assert.DoesNotContain("Paper Moons", Titles(f));
        f.Albums = false;

        f.Played = PlayedFilter.NeverPlayed;
        Assert.Equal(new[] { "Feature", "Hits of the 90s", "Paper Moons", "Saltwater" }, Titles(f));
        // Never played counts as "not played in 6 months" too; 7 and 8 months ago do, 3 days ago does not.
        f.Played = PlayedFilter.NotIn6Months;
        Assert.Equal(new[] { "Feature", "Glasshouse", "Hits of the 90s", "Low Tide", "Paper Moons", "Saltwater" }, Titles(f));
        f.Played = PlayedFilter.Any;

        f.Artist = "nova"; // album artist or a track artist
        Assert.Equal(new[] { "Feature", "Nightfall", "Saltwater" }, Titles(f));
        f.Artist = null;

        f.SkipCompilations = true;
        Assert.DoesNotContain("Hits of the 90s", Titles(f));
        Assert.False(f.IsActive); // the setting is not a chip

        f.Genre = "Rock";
        f.Played = PlayedFilter.NeverPlayed;
        Assert.True(f.IsActive);
        f.Clear();
        Assert.False(f.IsActive);
        Assert.True(f.SkipCompilations); // Clear filters leaves the setting alone
        Assert.Equal(new[] { "Pop", "Electronic", "Rock", "Jazz" }.OrderBy(x => x), RouletteFilter.Genres(lib).OrderBy(x => x));
        Assert.Equal(new[] { 2010, 2000, 1990 }, RouletteFilter.Decades(lib));
    }

    [Fact]
    public void GenreChip_SplitsSeveralGenres_AndMatchesAnyOfThem()
    {
        var lib = AlbumCatalog.Group(new[]
        {
            T("a", "Mixed", "Nova", genre: "Alternative Rock; Indie", albumId: "m"),
            T("b", "Plain", "Kite", genre: "indie", albumId: "p"),
            T("c", "Slash", "Ada", genre: "Hip-Hop/Rap", albumId: "s"),
        });
        // Most albums first; "indie" and "Indie" are one genre; "/" is not a separator.
        Assert.Equal(new[] { "Indie", "Alternative Rock", "Hip-Hop/Rap" }, RouletteFilter.Genres(lib));
        var f = new RouletteFilter { Genre = "INDIE" };
        Assert.Equal(new[] { "Mixed", "Plain" }, f.Apply(lib, Now).Select(a => a.Title).OrderBy(t => t));
        f.Genre = "Alternative Rock";
        Assert.Equal("Mixed", Assert.Single(f.Apply(lib, Now)).Title);
        f.Genre = "Rap";
        Assert.Empty(f.Apply(lib, Now));

        var songs = SongCatalog.Build(new[] { T("x", genre: "Malayalam; Pop"), T("y", genre: "Pop") }, Now);
        Assert.Equal(new[] { "Pop", "Malayalam" }, RouletteFilter.Genres(songs));
        Assert.Equal("x", Assert.Single(new RouletteFilter { Genre = "malayalam" }.Apply(songs, Now)).Title);
    }

    // ── Album of the Day ──

    [Fact]
    public void AlbumOfTheDay_PickIsStable_ForTheDay_AndChangesAcrossDays()
    {
        var lib = Library();
        var shuffled = lib.Reverse().ToList();
        var a = AlbumOfTheDay.Pick(lib, Now, skipCompilations: true);
        var b = AlbumOfTheDay.Pick(shuffled, Now.AddHours(5), skipCompilations: true);
        Assert.NotNull(a);
        Assert.Equal(a!.Key, b!.Key); // same day, any order, any hour
        Assert.False(a.IsCompilation);
        var days = Enumerable.Range(0, 30).Select(d => AlbumOfTheDay.Pick(lib, Now.AddDays(d), true)!.Key).Distinct().Count();
        Assert.True(days >= 4, $"{days} different albums over 30 days");
        Assert.Null(AlbumOfTheDay.Pick(Array.Empty<RouletteAlbum>(), Now, true));
    }

    [Fact]
    public void AlbumOfTheDay_AnniversaryComesFromTheYear()
    {
        var album = Library().Single(a => a.Title == "Nightfall");
        Assert.Equal("Today's album: Nightfall by Nova. It turns 20 this year.", AlbumOfTheDay.Message(album, 2026));
        Assert.Equal("It turns 1 this year.", AlbumOfTheDay.Anniversary(2025, 2026));
        Assert.Null(AlbumOfTheDay.Anniversary(2026, 2026));
        Assert.Null(AlbumOfTheDay.Anniversary(0, 2026));
        var noYear = Library().Single(a => a.Title == "Low Tide");
        Assert.Equal("Today's album: Low Tide by Lo.", AlbumOfTheDay.Message(noYear, 2026));
        Assert.DoesNotContain('\u2014', AlbumOfTheDay.Message(album, 2026));
    }

    [Fact]
    public async Task DailyPicker_NotifiesOncePerDay_FromTheHour()
    {
        var host = new FakeRouletteHost();
        host.Tracks = new List<TrackInfo>
        {
            T("x", "Nightfall", "Nova", 2006), T("y", "Paper Moons", "Kite", 1996), T("z", "Glasshouse", "Ada", 2019),
        };
        var clock = new DateTimeOffset(2026, 10, 29, 8, 30, 0, TimeSpan.Zero);
        using var picker = new DailyPicker(host, () => clock);

        host.Enabled = false;
        clock = clock.AddHours(2);
        Assert.Null(await picker.CheckAsync()); // off: nothing
        host.Enabled = true;

        clock = new DateTimeOffset(2026, 10, 29, 8, 30, 0, TimeSpan.Zero);
        Assert.Null(await picker.CheckAsync()); // before 9:00
        clock = clock.AddMinutes(31);
        var first = await picker.CheckAsync();
        Assert.NotNull(first);
        Assert.StartsWith("Today's album: ", first);
        Assert.Matches(@"It turns \d+ this year\.$", first);
        Assert.Null(await picker.CheckAsync()); // once a day
        clock = clock.AddHours(10);
        Assert.Null(await picker.CheckAsync());
        Assert.Single(host.Notices);
        Assert.Equal("2026-10-29", host.Daily!.Day);

        // A restart the same day: the saved state still says done.
        using (var again = new DailyPicker(host, () => clock)) Assert.Null(await again.CheckAsync());

        clock = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
        Assert.NotNull(await picker.CheckAsync()); // the next day
        Assert.Equal(2, host.Notices.Count);

        // The same day always gives the same album.
        var expected = AlbumOfTheDay.Pick(AlbumCatalog.Group(host.Tracks), clock, true)!.Title;
        Assert.Contains(expected, host.Notices[1]);
    }

    [Fact]
    public void AlbumOfTheDay_NextCheck_WaitsForTheHour_AtMostAnHour()
    {
        var at = new DateTimeOffset(2026, 10, 29, 8, 50, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMinutes(10), AlbumOfTheDay.UntilNextCheck(at, 9, doneToday: false));
        Assert.Equal(TimeSpan.FromHours(1), AlbumOfTheDay.UntilNextCheck(at.AddHours(-5), 9, doneToday: false));
        Assert.Equal(TimeSpan.FromSeconds(5), AlbumOfTheDay.UntilNextCheck(at.AddHours(1), 9, doneToday: false)); // due now
        Assert.Equal(TimeSpan.FromHours(1), AlbumOfTheDay.UntilNextCheck(at.AddHours(1), 9, doneToday: true));
        Assert.True(AlbumOfTheDay.IsDue(at.AddMinutes(10), 9, null));
        Assert.False(AlbumOfTheDay.IsDue(at.AddMinutes(10), 9, new DailyState { Day = "2026-10-29" }));
    }

    // ── The window ──

    [AvaloniaFact]
    public void View_EmptyState_WhenNothingMatches_ClearFiltersBringsTheAlbumsBack()
    {
        var host = new FakeRouletteHost();
        var view = new RouletteView(host, new Random(3), () => Now);
        var window = new Window { Content = view, Width = 440, Height = 700 };
        window.Show();
        view.SetAlbums(Library());
        Assert.False(view.IsEmptyStateShown);
        Assert.True(view.SpinEnabled);
        Assert.Equal("6 albums", view.CountText); // the compilation is left out by default

        view.Filter.Genre = "Classical";
        view.ClearFilters();
        Assert.False(view.IsEmptyStateShown);
        view.Filter.Genre = "Classical";
        view.ApplyFilters();
        Assert.True(view.IsEmptyStateShown);
        Assert.False(view.SpinEnabled);
        Assert.Equal("0 albums", view.CountText);
        view.ClearFilters();
        Assert.False(view.IsEmptyStateShown);
        Assert.True(view.SpinEnabled);

        // An empty library is its own message, without a Clear filters button.
        var empty = new RouletteView(host, new Random(3), () => Now);
        empty.SetAlbums(Array.Empty<RouletteAlbum>());
        Assert.True(empty.IsEmptyStateShown);
        window.Close();
    }

    [AvaloniaFact]
    public void View_Spin_FlicksPast_AndLandsOnAMatch_ThenPlayShuffleOpen()
    {
        var host = new FakeRouletteHost();
        var view = new RouletteView(host, new Random(7), () => Now);
        var window = new Window { Content = view, Width = 440, Height = 700 };
        window.Show();
        view.Reel.ManualFrames = true;
        view.SetAlbums(Library());
        view.Filter.Genre = "Electronic";
        view.ApplyFilters();
        Assert.Equal("Spin", view.SpinText);
        Assert.False(view.PillsEnabled);

        view.Spin();
        Assert.True(view.Reel.Motion.IsSpinning);
        Assert.Equal(ReelMotion.FullSlots - 1, view.Reel.Motion.TargetSlot);
        Assert.Equal(ReelMotion.FullSlots + ReelMotion.Tail, view.Reel.Motion.Length);
        var centres = new HashSet<int>();
        var lastOffset = -1.0;
        var frames = 0;
        while (view.Reel.Motion.IsSpinning && frames < 1000)
        {
            view.Reel.AdvanceFrame(1 / 60.0);
            Assert.True(view.Reel.Motion.Offset >= lastOffset); // only forward, slowing down
            lastOffset = view.Reel.Motion.Offset;
            centres.Add((int)Math.Round(lastOffset));
            frames++;
        }
        Assert.InRange(frames, 185, 200); // 3.2 s at 60 fps
        Assert.True(centres.Count >= ReelMotion.FullSlots - 1, $"{centres.Count} covers passed the centre");
        Assert.NotNull(view.Pick);
        Assert.Equal("Electronic", view.Pick!.Genre);
        Assert.Equal("Spin again", view.SpinText);
        Assert.True(view.PillsEnabled);
        Assert.Equal(view.Pick.Title, view.TitleText);

        Dispatcher.UIThread.RunJobs();
        var play = Find<Button>(view, "Play");
        play.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(view.Pick.TrackIds, host.Played.Single());
        Find<Button>(view, "Shuffle").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(view.Pick.TrackIds.OrderBy(x => x), host.Played[1].OrderBy(x => x));
        if (view.OpenAlbumEnabled)
        {
            Find<Button>(view, "Open album").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(view.Pick.AlbumId, host.Shown.Single());
        }

        // Reduced animation: a short move over a few covers.
        host.Reduced = true;
        var before = view.Pick.Key;
        view.Spin();
        Assert.Equal(ReelMotion.ReducedSlots - 1, view.Reel.Motion.TargetSlot);
        for (var i = 0; i < 60 && view.Reel.Motion.IsSpinning; i++) view.Reel.AdvanceFrame(1 / 60.0);
        Assert.False(view.Reel.Motion.IsSpinning);
        Assert.NotEqual(before, view.Pick!.Key); // two matches: a spin never lands on the same one twice
        window.Close();
    }

    [AvaloniaFact]
    public void View_SpinFromArtist_BeforeTheLibraryLoads_SpinsWhenItHas()
    {
        var host = new FakeRouletteHost();
        var view = new RouletteView(host, new Random(1), () => Now);
        view.Reel.ManualFrames = true;
        view.SpinFromArtist("Kite");
        Assert.False(view.Reel.Motion.IsSpinning);
        view.SetAlbums(Library());
        Assert.True(view.Reel.Motion.IsSpinning);
        Assert.Equal("Kite", view.Filter.Artist);
        Assert.All(view.Matches, a => Assert.Contains("Kite", a.Artists));
        view.Spin(); // a second press lands it right away
        Assert.False(view.Reel.Motion.IsSpinning);
        Assert.Contains("Kite", view.Pick!.Artists);
    }

    [AvaloniaFact]
    public void View_ShowsTodaysAlbum_OnTheStage()
    {
        var lib = Library();
        var pick = AlbumOfTheDay.Pick(lib, Now, true)!;
        var host = new FakeRouletteHost { Enabled = true, Daily = new DailyState { Day = AlbumOfTheDay.DayKey(Now), AlbumKey = pick.Key, Title = pick.Title } };
        var view = new RouletteView(host, new Random(1), () => Now);
        view.SetAlbums(lib);
        Assert.Equal(pick.Key, view.Pick!.Key);
        Assert.Equal("Album of the Day", view.CaptionText);
    }

    [AvaloniaFact]
    public void Reel_PausesWhileMinimized()
    {
        var view = new RouletteView(new FakeRouletteHost(), new Random(1), () => Now);
        Assert.True(view.Reel.ShouldPause()); // not on screen yet
        var window = new Window { Content = view, Width = 440, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.Reel.ShouldPause());
        window.WindowState = WindowState.Minimized;
        Assert.True(view.Reel.ShouldPause());
        window.WindowState = WindowState.Normal;
        Assert.False(view.Reel.ShouldPause());
        window.Close();
    }

    /// <summary>
    /// The reel's frame: stepping the motion (and asking for a redraw) allocates nothing, and
    /// drawing the stage with pre-scaled covers into a drawing context stays small and fast.
    /// </summary>
    [AvaloniaFact]
    public void Reel_FrameBudget_NoAllocationsPerFrame()
    {
        var reel = new ReelCanvas { ManualFrames = true };
        var window = new Window { Content = reel, Width = 440, Height = 420 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        // Covers as the cache holds them: decoded once, at the drawn size (320 px for ~256 DIP at 125%).
        var bitmaps = Enumerable.Range(0, 8).Select(_ => new WriteableBitmap(new PixelSize(320, 320), new Vector(96, 96))).ToArray();
        for (var i = 0; i < ReelMotion.MaxSlots; i++)
            reel.SetSlot(i, i % 9 == 8 ? null : new Cover(bitmaps[i % 8]), RouletteTheme.Dark.Placeholders[i % 8]);

        var size = reel.Bounds.Size;
        using var target = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
        void Spin() => reel.Motion.Start(24, 5, ReelMotion.FullSlots, ReelMotion.FullSeconds, new Random(2));

        Spin();
        for (var i = 0; i < 60; i++) // warm up (JIT)
        {
            reel.AdvanceFrame(1 / 60.0);
            using var ctx = target.CreateDrawingContext();
            reel.Draw(ctx, size);
        }
        Spin();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long stepBytes = 0, drawBytes = 0;
        var times = new List<double>();
        var frames = 0;
        while (reel.Motion.IsMoving && frames < 400)
        {
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var t0 = Stopwatch.GetTimestamp();
            reel.AdvanceFrame(1 / 60.0);
            var a1 = GC.GetAllocatedBytesForCurrentThread();
            using (var ctx = target.CreateDrawingContext())
            {
                var a2 = GC.GetAllocatedBytesForCurrentThread();
                reel.Draw(ctx, size);
                drawBytes += GC.GetAllocatedBytesForCurrentThread() - a2;
            }
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            stepBytes += a1 - a0;
            frames++;
        }
        times.Sort();
        // Who allocates while drawing (sampled every ~100 KB, so many frames).
        string[] sampled;
        using (var listener = new AllocListener())
        {
            for (var i = 0; i < 6000; i++)
            {
                if (!reel.Motion.IsMoving) Spin();
                reel.AdvanceFrame(1 / 60.0);
                using var ctx = target.CreateDrawingContext();
                reel.Draw(ctx, size);
            }
            sampled = listener.Types.OrderByDescending(k => k.Value).Select(k => $"{k.Value} x {k.Key}").ToArray();
        }
        var own = sampled.Where(s => s.Contains("AlbumRoulette", StringComparison.Ordinal)).ToArray();
        var report = $"{frames} frames: step {stepBytes} B total, draw {drawBytes / (double)frames:0} B per frame, "
                     + $"frame p50 {times[times.Count / 2]:0.000} ms, p95 {times[(int)(times.Count * 0.95)]:0.000} ms\n"
                     + $"allocation samples over 6000 drawn frames: {(sampled.Length == 0 ? "none" : string.Join("; ", sampled.Take(8)))}";
        Console.WriteLine("Album Roulette reel: " + report);
        File.WriteAllText(Path.Combine(PreviewDir(), "roulette-reel-budget.txt"), report + "\n");
        Assert.InRange(frames, 190, 230); // spin + landing
        Assert.Equal(0, stepBytes);
        // Drawing allocates nothing either: immutable brushes, covers rounded and the glow drawn
        // once into bitmaps, no pushed clips or opacity (each push costs the context an object),
        // struct rects. (The samples are the test's own drawing context per frame; in the app the
        // compositor records the frame into its render data.)
        Assert.Empty(own);
        Assert.Equal(0, drawBytes);
        Assert.True(times[(int)(times.Count * 0.95)] < 8, report);
        window.Close();
    }

    // ── Pictures (real Skia) ──

    [AvaloniaFact]
    public void Preview_TheWindowAt125Percent()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_TEST_SKIA") != "1")
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var dir = PreviewDir();
        var paths = PreviewCovers(Path.Combine(dir, "covers"));
        var names = new[] { "Midnight Arcade", "Paper Moons", "Glasshouse", "Velvet Static", "Northern Lights", "Saltwater", "Neon Garden", "Low Tide" };
        var artists = new[] { "Nova Lane", "Juniper Hollow", "Kite Season", "The Moth Choir", "Ada Vale", "Pixel Harbor", "Sunroom", "Odd Orchard" };
        var tracks = new List<TrackInfo>();
        for (var i = 0; i < 32; i++)
            tracks.Add(T($"t{i}", names[i % names.Length] + (i >= names.Length ? " " + (i / names.Length + 1) : ""), artists[i % artists.Length],
                1986 + i % 37, genre: i % 3 == 0 ? "Electronic" : "Pop", albumId: Guid.NewGuid().ToString(), art: paths[i % paths.Count],
                releaseType: i % 7 == 0 ? "EP" : "Album"));
        var host = new FakeRouletteHost();
        var view = new RouletteView(host, new Random(4), () => Now);
        // Dark, like Noctis by default (the test app has no Noctis page colour, so the palette follows the variant).
        var window = new Window { Content = view, Width = RouletteView.WindowWidth, Height = RouletteView.WindowHeight, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        view.Reel.ManualFrames = true;
        view.SetAlbums(AlbumCatalog.Group(tracks));
        view.Filter.Played = PlayedFilter.NotIn6Months;
        view.ApplyFilters();
        foreach (var t in tracks) view.Covers.GetNow(t.ArtworkPath);
        view.Spin();
        while (view.Reel.Motion.IsSpinning) view.Reel.AdvanceFrame(1 / 60.0);
        for (var i = 0; i < 40; i++) view.Reel.AdvanceFrame(1 / 60.0); // past the landing pop
        Dispatcher.UIThread.RunJobs();
        Render(view, Path.Combine(dir, "roulette-preview.png"), 1.25);
        window.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();
        Render(view, Path.Combine(dir, "roulette-preview-light.png"), 1.25);
        window.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();

        // Mid-spin, for the reel.
        view.Spin();
        for (var i = 0; i < 40; i++) view.Reel.AdvanceFrame(1 / 60.0);
        Render(view, Path.Combine(dir, "roulette-preview-spinning.png"), 1.25);

        // The empty state.
        view.Reel.Motion.Finish();
        view.Filter.Genre = "Polka";
        view.ApplyFilters();
        Render(view, Path.Combine(dir, "roulette-preview-empty.png"), 1.25);
        window.Close();
    }

    internal static string PreviewDir()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_PLUGIN_PREVIEW") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "noctis-plugin-preview");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The control as drawn at <paramref name="scaling"/> (125%: 120 dpi).</summary>
    internal static void Render(Control control, string path, double scaling)
    {
        control.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var size = control.Bounds.Size;
        using var bmp = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling)),
            new Vector(96 * scaling, 96 * scaling));
        bmp.Render(control);
        bmp.Save(path);
    }

    /// <summary>Width and height from a PNG's IHDR chunk.</summary>
    internal static (int, int) PngSize(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        var head = new byte[24];
        s.ReadExactly(head);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, head[..4]);
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20)));
    }

    internal static List<string> PreviewCovers(string dir) => AlbumTownPluginTests.PreviewCovers(dir);

    private static T Find<T>(Control root, string name) where T : Control
        => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<T>()
            .First(c => Avalonia.Automation.AutomationProperties.GetName(c) == name);

    // ── Song Roulette (GitHub #198) ──

    internal static IReadOnlyList<TrackInfo> SongTracks() => new[]
    {
        T("Glow", "Nightfall", "Nova", 2006, genre: "Electronic", albumId: NightfallId, albumArtist: "Nova"),
        T("Ember", "Nightfall", "Nova", 2006, plays: 4, last: Now.AddDays(-2), genre: "Electronic", albumId: NightfallId, albumArtist: "Nova", number: 2),
        T("Duet", "Paper Moons", "Nova", 1996, genre: "Rock", releaseType: "EP", albumArtist: "Kite"),
        T("Solo", "", "Ada", 2019, genre: "Pop", releaseType: "Single"),
        T("Meh", "Paper Moons", "Kite", 1996, genre: "Rock", releaseType: "EP") with { IsDisliked = true },
        T("Skipped", "Paper Moons", "Kite", 1996, genre: "Rock", releaseType: "EP") with { SkipWhenShuffling = true },
        T("Snoozed", "Paper Moons", "Kite", 1996, genre: "Rock", releaseType: "EP") with { SnoozedUntil = Now.AddDays(3) },
        T("Woke", "Paper Moons", "Kite", 1996, genre: "Rock", releaseType: "EP") with { SnoozedUntil = Now.AddDays(-3) },
    };

    [Fact]
    public void SongCatalog_EverySongIsItsOwnEntry_WithoutTheOnesShuffleLeavesOut()
    {
        var songs = SongCatalog.Build(SongTracks(), Now);
        Assert.Equal(new[] { "Glow", "Ember", "Duet", "Solo", "Woke" }, songs.Select(s => s.Title));
        Assert.All(songs, s => Assert.True(s.IsSong));
        Assert.All(songs, s => Assert.Single(s.TrackIds));
        Assert.False(Library()[0].IsSong);

        var duet = songs.Single(s => s.Title == "Duet");
        Assert.Equal(("Nova", "Paper Moons", 1996, ReleaseKind.EP), (duet.Artist, duet.AlbumTitle, duet.Year, duet.Kind));
        Assert.Equal(new[] { "Nova", "Kite" }, duet.Artists);
        var ember = songs.Single(s => s.Title == "Ember");
        Assert.Equal((4, Now.AddDays(-2), NightfallId), (ember.PlayCount, ember.LastPlayed, ember.AlbumId));

        var f = new RouletteFilter { Artist = "nova" };
        Assert.Equal(new[] { "Glow", "Ember", "Duet" }, f.Apply(songs, Now).Select(s => s.Title));
        f.Clear();
        f.Singles = true; // Albums / EPs / Singles sort albums, not songs
        Assert.Equal(5, f.Apply(songs, Now).Count);
        f.Clear();
        f.Genre = "rock";
        Assert.Equal(new[] { "Duet", "Woke" }, f.Apply(songs, Now).Select(s => s.Title));
        f.Genre = null;
        f.Played = PlayedFilter.NeverPlayed;
        Assert.DoesNotContain("Ember", f.Apply(songs, Now).Select(s => s.Title));
    }

    [AvaloniaFact]
    public void View_SongRoulette_SpinsASong_ThenPlayPlayNextAddToQueue()
    {
        var host = new FakeRouletteHost { Enabled = false };
        var view = new RouletteView(host, new Random(5), () => Now);
        var window = new Window { Content = view, Width = 440, Height = 700 };
        window.Show();
        view.Reel.ManualFrames = true;
        var tracks = SongTracks();
        view.SetLibrary(AlbumCatalog.Group(tracks), SongCatalog.Build(tracks, Now));
        Assert.Equal(RouletteMode.Albums, view.Mode);
        Assert.Equal("3 albums", view.CountText);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find<Button>(view, "Shuffle").IsVisible);
        Assert.False(Find<Button>(view, "Play next").IsVisible);
        Assert.True(Find<Button>(view, "EPs").IsVisible);

        Find<Button>(view, "Song Roulette").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(RouletteMode.Songs, view.Mode);
        Assert.Equal("5 songs", view.CountText);
        Assert.Equal("Press Spin to let the reel pick a song", view.HintText);
        Assert.False(Find<Button>(view, "Shuffle").IsVisible);
        Assert.True(Find<Button>(view, "Play next").IsVisible);
        Assert.True(Find<Button>(view, "Add to queue").IsVisible);
        Assert.False(Find<Button>(view, "EPs").IsVisible);

        view.Filter.Genre = "Electronic";
        view.ApplyFilters();
        Assert.Equal("2 songs", view.CountText);
        view.Spin();
        view.Spin(); // lands right away
        var pick = view.Pick!;
        Assert.True(pick.IsSong);
        Assert.Contains(pick.Title, new[] { "Glow", "Ember" });
        Assert.Equal("Nightfall · 2006", view.SubtitleText);
        Assert.True(view.PillsEnabled);

        Find<Button>(view, "Play").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Find<Button>(view, "Play next").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Find<Button>(view, "Add to queue").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Find<Button>(view, "Open album").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(pick.TrackIds, host.Played.Single());
        Assert.Equal(new[] { (pick.TrackIds[0], true), (pick.TrackIds[0], false) }, host.Queued.Select(q => (q.Ids.Single(), q.PlayNext)));
        Assert.Equal(NightfallId, host.Shown.Single());

        // Nothing matches: the empty state speaks of songs.
        view.Filter.Genre = "Classical";
        view.ApplyFilters();
        Assert.Equal("No songs match these filters", view.EmptyText);

        // Back to albums: the stage clears (an album is not a song), the chips stay.
        view.Filter.Genre = "Electronic";
        view.SetMode(RouletteMode.Albums);
        Assert.Null(view.Pick);
        Assert.Equal("1 album", view.CountText);
        window.Close();
    }

    [AvaloniaFact]
    public void View_SpinFor_SongsOfAnArtist_BeforeTheLibraryLoads()
    {
        var host = new FakeRouletteHost { Enabled = false };
        var view = new RouletteView(host, new Random(2), () => Now);
        view.Reel.ManualFrames = true;
        view.Filter.Genre = "Pop";
        view.SpinFor(RouletteMode.Songs, "Nova");
        Assert.Equal(RouletteMode.Songs, view.Mode);
        Assert.Null(view.Filter.Genre); // a fresh spin clears the chips
        var tracks = SongTracks();
        view.SetLibrary(AlbumCatalog.Group(tracks), SongCatalog.Build(tracks, Now));
        Assert.True(view.Reel.Motion.IsSpinning);
        Assert.Equal(new[] { "Glow", "Ember", "Duet" }, view.Matches.Select(m => m.Title));
        view.Spin();
        Assert.Contains("Nova", view.Pick!.Artists);

        // The Songs page: the whole library, artist chip gone.
        view.SpinFor(RouletteMode.Songs, null);
        Assert.Null(view.Filter.Artist);
        Assert.Equal(5, view.Matches.Count);
        // "Spin from this artist" keeps the mode the window is in.
        view.SpinFromArtist("Ada");
        Assert.Equal(RouletteMode.Songs, view.Mode);
        Assert.Equal("Solo", Assert.Single(view.Matches).Title);
    }

    // ── Through the real loader ──

    [AvaloniaFact]
    public void ReleaseZip_InstallsFromFile_AndRuns()
    {
        var zip = PluginSandbox.RepoFile($"plugins/Noctis.Plugins.AlbumRoulette/bin/Release/Noctis.Plugins.AlbumRoulette-{AlbumRoulettePlugin.Version}.zip");
        if (!File.Exists(zip)) Assert.Skip("build it first: dotnet build plugins/Noctis.Plugins.AlbumRoulette -c Release");

        using (var archive = ZipFile.OpenRead(zip))
        {
            var names = archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "Noctis.Plugins.AlbumRoulette.deps.json", "Noctis.Plugins.AlbumRoulette.dll", "icon.png", "plugin.json" }, names);
            Assert.Equal((256, 256), PngSize(archive.GetEntry("icon.png")!));
        }

        using var box = new PluginSandbox();
        box.Settings.CommunityPluginsEnabled = true;
        var host = box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.9");
        host.LoadAll();
        var result = host.InstallPackage(zip, allowUpdate: false);
        Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        var plugin = result.Plugin!;
        Assert.Equal(("Album Roulette", AlbumRoulettePlugin.Version, "Noctis"), (plugin.Name, plugin.Version, plugin.Manifest?.Author));
        Assert.Empty(plugin.Manifest!.Warnings); // "icon" is tolerated, permissions all known
        Assert.Equal(new[] { "library.read", "playback.control", "notifications", "menu.commands" }, plugin.Manifest.Permissions);
        Assert.Equal(new[] { "albumOfTheDay", "dailyHour", "skipCompilations", "animation" }, plugin.Manifest.Settings.Select(s => s.Key));

        host.SetEnabled(plugin, true);
        Assert.Equal(PluginStatus.Running, plugin.Status);
        Assert.Equal("Album Roulette", Assert.Single(plugin.Views).Name);
        Assert.Contains(host.TrackCommands, c => c.Label == AlbumRoulettePlugin.SpinFromArtistLabel);
        // API 1.7: Album Roulette and Song Roulette on the artist page, Song Roulette on the Songs page.
        Assert.Equal(new[] { AlbumRoulettePlugin.AlbumRouletteLabel, AlbumRoulettePlugin.SongRouletteLabel },
            host.LibraryCommands(LibraryCommandPlaces.ArtistPage).Select(c => c.Label));
        var songsButton = Assert.Single(host.LibraryCommands(LibraryCommandPlaces.SongsPage));
        Assert.Equal(AlbumRoulettePlugin.SongRouletteLabel, songsButton.Label);
        Assert.False(string.IsNullOrEmpty(songsButton.Icon)); // the top-bar button is only its icon
        Assert.True(host.Remove(plugin, deleteData: true));
    }

    [AvaloniaFact]
    public void Plugin_EntryPoints_OpenTheWindow_InTheirMode_AndAnOlderNoctisStillRunsIt()
    {
        var host = new MiniHost(withLibraryCommands: true);
        var plugin = new AlbumRoulettePlugin();
        plugin.Initialize(host);
        Assert.Equal(new[] { "Album Roulette", "Song Roulette" }, host.Library.Select(c => c.Label));
        Assert.Equal(new[] { LibraryCommandPlaces.ArtistPage, LibraryCommandPlaces.ArtistPage | LibraryCommandPlaces.SongsPage },
            host.Library.Select(c => c.Places));

        // Song Roulette from an artist page: the window opens scoped to that artist, in songs.
        host.Library[1].Handler(new LibraryCommandContext(LibraryCommandPlaces.ArtistPage, "Nova"));
        var view = Assert.IsType<RouletteView>(host.Opened.Single());
        Assert.Equal(RouletteMode.Songs, view.Mode);
        Assert.Equal("Nova", view.Filter.Artist);
        // The window is open: another entry switches it.
        host.Library[0].Handler(new LibraryCommandContext(LibraryCommandPlaces.ArtistPage, "Kite"));
        Assert.Single(host.Opened); // brought forward, not a second window
        Assert.Equal((RouletteMode.Albums, "Kite"), (view.Mode, view.Filter.Artist));
        host.Library[1].Handler(new LibraryCommandContext(LibraryCommandPlaces.SongsPage, null));
        Assert.Equal((RouletteMode.Songs, (string?)null), (view.Mode, view.Filter.Artist));
        plugin.Shutdown();
        Assert.Empty(host.Library);

        // Noctis before API 1.7: no library commands, but the plugin starts.
        var old = new MiniHost(withLibraryCommands: false);
        var oldPlugin = new AlbumRoulettePlugin();
        oldPlugin.Initialize(old);
        Assert.Single(old.TrackCommandLabels);
        oldPlugin.Shutdown();
    }

    /// <summary>Just enough of a Noctis for the plugin's Initialize: a view, a track command,
    /// settings, and (when <c>withLibraryCommands</c>) API 1.7's library commands.</summary>
    private sealed class MiniHost(bool withLibraryCommands) : IPluginHost, IPluginSettings
    {
        public readonly List<(string Label, LibraryCommandPlaces Places, Action<LibraryCommandContext> Handler)> Library = new();
        public readonly List<string> TrackCommandLabels = new();
        public readonly List<Control> Opened = new();
        private IPluginViewProvider? _view;

        public string AppVersion => "1.6.8";
        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "noctis-roulette-mini-" + Guid.NewGuid().ToString("N"));
        public INowPlaying NowPlaying => null!;
        public IBeatSource Beat => null!;
        public ISpectrumSource Spectrum => null!;
        public void Log(string message) { }
        public void RegisterVisualLayer(IVisualLayerProvider provider) { }
        public IPluginSettings Settings => this;

        public IDisposable RegisterView(IPluginViewProvider provider) { _view = provider; return new Off(() => _view = null); }
        public bool OpenView(IPluginViewProvider provider)
        {
            if (Opened.Count == 0) Opened.Add(provider.CreateView());
            return true;
        }
        public IDisposable RegisterTrackCommand(string label, string? icon, Action<TrackInfo> handler)
        {
            TrackCommandLabels.Add(label);
            return new Off(() => TrackCommandLabels.Remove(label));
        }
        public IDisposable RegisterLibraryCommand(string label, string? icon, LibraryCommandPlaces places, Action<LibraryCommandContext> handler)
        {
            if (!withLibraryCommands) throw new NotSupportedException("This Noctis build does not implement plugin API 1.7.");
            var entry = (label, places, handler);
            Library.Add(entry);
            return new Off(() => Library.Remove(entry));
        }

        public string? GetString(string key) => null;
        public bool GetBool(string key, bool fallback = false) => key == "albumOfTheDay" ? false : fallback;
        public double GetNumber(string key, double fallback = 0) => fallback;
        public event EventHandler<string>? Changed { add { } remove { } }

        private sealed class Off(Action off) : IDisposable { public void Dispose() => off(); }
    }

    [Fact]
    public void Manifest_IsOfficial_AndMatchesTheCode()
    {
        var manifest = PluginManifest.TryLoad(PluginSandbox.RepoFile("plugins/Noctis.Plugins.AlbumRoulette"))!;
        Assert.Equal((AlbumRoulettePlugin.Id, AlbumRoulettePlugin.Version, "Noctis"), (manifest.Id, manifest.Version, manifest.Author));
        Assert.Equal(AlbumRoulettePlugin.About, manifest.Description);
        Assert.Equal("1.5", manifest.ApiVersion);
        Assert.DoesNotContain('\u2014', File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.AlbumRoulette/plugin.json")));
        Assert.True(File.Exists(PluginSandbox.RepoFile("plugins/Noctis.Plugins.AlbumRoulette/icon.png")));
    }

    internal sealed class FakeRouletteHost : IRouletteHost
    {
        public List<TrackInfo> Tracks = new();
        public readonly List<IReadOnlyList<string>> Played = new();
        public readonly List<(IReadOnlyList<string> Ids, bool PlayNext)> Queued = new();
        public readonly List<string> Shown = new();
        public readonly List<string> Notices = new();
        public DailyState? Daily;
        public bool Enabled = true;
        public bool Reduced;

        public IReadOnlyList<TrackInfo> LoadTracks() => Tracks;
        public void PlayTracks(IReadOnlyList<string> trackIds) => Played.Add(trackIds.ToArray());
        public void QueueTracks(IReadOnlyList<string> trackIds, bool playNext) => Queued.Add((trackIds.ToArray(), playNext));
        public bool ShowAlbum(string albumId) { Shown.Add(albumId); return true; }
        public void Notify(string message) { lock (Notices) Notices.Add(message); }
        public void Log(string message) { }
        public bool AlbumOfTheDayEnabled => Enabled;
        public int DailyHour => 9;
        public bool SkipCompilations => true;
        public bool ReducedMotion => Reduced;
        public event EventHandler? SettingsChanged { add { } remove { } }
        public DailyState? LoadDaily() => Daily is null ? null : new DailyState { Day = Daily.Day, AlbumKey = Daily.AlbumKey, Title = Daily.Title };
        public void SaveDaily(DailyState state) => Daily = state;
    }
}
