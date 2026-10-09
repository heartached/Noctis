using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Noctis Wrap revamp (owner 10-09): the dialog as a rounded pill pop-up, the report built
/// off the UI thread, and the share card with the album artwork.
/// </summary>
public class WrapDialogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wrap_dialog_test_{Guid.NewGuid():N}");
    private string ArchiveFile => Path.Combine(_dir, "wrap_archive.json");

    public WrapDialogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    private sealed class History : IPlayHistoryService
    {
        public History(IReadOnlyList<PlayHistoryEvent> events) => Events = events;
        public IReadOnlyList<PlayHistoryEvent> Events { get; }
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>A play log whose reads wait for the test, and that records which thread read it.</summary>
    private sealed class GatedEvents : IReadOnlyList<PlayHistoryEvent>
    {
        private readonly List<PlayHistoryEvent> _items;
        public readonly ManualResetEventSlim Gate = new(false);
        public readonly List<int> ReadThreads = new();
        public GatedEvents(List<PlayHistoryEvent> items) => _items = items;

        private void Wait()
        {
            lock (ReadThreads) ReadThreads.Add(Environment.CurrentManagedThreadId);
            Gate.Wait(TimeSpan.FromSeconds(10));
        }

        public int Count { get { Wait(); return _items.Count; } }
        public PlayHistoryEvent this[int index] { get { Wait(); return _items[index]; } }
        public IEnumerator<PlayHistoryEvent> GetEnumerator() { Wait(); return _items.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static Track Song(string title, string artist, string album, string genre = "Hip-Hop/Rap", string? artwork = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        AlbumId = Track.ComputeAlbumId(artist, album),
        Genre = genre,
        Duration = TimeSpan.FromMinutes(3),
        Codec = "FLAC",
        FilePath = $"A:/music/{artist}/{album}/{title}.flac",
        AlbumArtworkPath = artwork,
    };

    private static PlayHistoryEvent Play(Track t, DateTime local) => new()
    {
        TrackId = t.Id, Title = t.Title, Artist = t.Artist, PlayedAtUtc = local.ToUniversalTime(),
    };

    /// <summary>Six artists with an album each, played this year (and one last year so the
    /// archive has a year to offer).</summary>
    private static (FakeLibraryService Library, List<PlayHistoryEvent> Events) SampleData()
    {
        var lib = new FakeLibraryService();
        var events = new List<PlayHistoryEvent>();
        var year = DateTime.Now.Year;
        for (var a = 0; a < 6; a++)
        {
            var t = Song($"Song {a}", $"Artist {a}", $"Album {a}");
            lib.TrackList.Add(t);
            for (var p = 0; p < 10 - a; p++)
                events.Add(Play(t, new DateTime(year, 1, 1 + p, 9 + a, 0, 0, DateTimeKind.Local)));
        }
        events.Insert(0, Play(lib.TrackList[0], new DateTime(year - 1, 6, 1, 12, 0, 0, DateTimeKind.Local)));
        return (lib, events);
    }

    /// <summary>
    /// The build runs on the thread pool. The old view-model ran the archive check and the
    /// whole build inside its constructor, on the UI thread, before the dialog could even
    /// open: with a play log that reads slowly this constructor would block until the log
    /// answered. Now the dialog opens straight away with a spinner and the report lands after.
    /// </summary>
    [AvaloniaFact]
    public void Build_RunsOffTheUiThread_AndLandsAfterwards()
    {
        var (lib, events) = SampleData();
        var gated = new GatedEvents(events);
        var ui = Environment.CurrentManagedThreadId;

        var sw = Stopwatch.StartNew();
        var vm = new WrapViewModel(new History(gated), lib, new WrapArchiveService(ArchiveFile), _ => null);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"constructor waited {sw.ElapsedMilliseconds} ms on the play log");
        Assert.True(vm.IsLoading);
        Assert.True(vm.ShowSpinner);
        Assert.False(vm.HasData);

        gated.Gate.Set();
        Assert.True(PumpUntil(() => vm.Loading.IsCompleted && vm.HasData));
        Assert.False(vm.IsLoading);
        Assert.NotEmpty(gated.ReadThreads);
        Assert.DoesNotContain(ui, gated.ReadThreads);
        Assert.Equal("Artist 0", vm.TopArtists[0].Name);
        vm.Dispose();
    }

    /// <summary>
    /// Hosted in PillDialogHost (blurred backdrop + the shared animation), shows the report
    /// with its covers and portraits, the year picker is a filled pill (the old one carried a
    /// white 1.5px outline), "Hip-Hop/Rap" is shown whole (it was cut to "Hip-Ho…"), and Esc
    /// animates the dialog out exactly once.
    /// </summary>
    [AvaloniaFact]
    public void Dialog_IsAPillPopUp_ShowsTheReport_AndEscClosesOnce()
    {
        EnsureAppStyles();
        var (lib, events) = SampleData();
        var vm = new WrapViewModel(new History(events), lib, new WrapArchiveService(ArchiveFile),
            name => name == "Artist 0" ? "C:/portraits/artist0.jpg" : null);
        var win = new WrapDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1300, Height = 900 };
        var closed = 0;
        win.Closed += (_, _) => closed++;
        win.Show();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();

        Assert.True(PumpUntil(() => CardSettledOpen(host) && vm.HasData && !vm.IsLoading));
        Assert.True(PumpUntil(() => win.FindControl<ItemsControl>("AlbumItems")!.ItemCount == 5));
        Assert.Equal(5, win.FindControl<ItemsControl>("ArtistItems")!.ItemCount);
        Assert.Equal(5, win.FindControl<ItemsControl>("SongItems")!.ItemCount);
        Assert.Equal("Album 0", vm.TopAlbum?.Name);
        Assert.True(vm.TopArtists[0].HasImage);
        Assert.False(vm.TopArtists[1].HasImage);

        // Both years offered, the live one still selected after the archive answered.
        var yearBox = win.FindControl<ComboBox>("YearBox")!;
        Assert.True(PumpUntil(() => vm.AvailableYears.Count == 2));
        Assert.Equal(DateTime.Now.Year, yearBox.SelectedItem);
        Assert.Contains("pill-field", yearBox.Classes);
        Assert.True(yearBox.BorderBrush is null or ISolidColorBrush { Color.A: 0 },
            $"year picker outline: {yearBox.BorderBrush}");

        var genre = win.FindControl<TextBlock>("TopGenreValue")!;
        Assert.Equal("Hip-Hop/Rap", genre.Text);
        Assert.True(PumpUntil(() => genre.Bounds.Width > 0));
        Assert.DoesNotContain(genre.TextLayout.TextLines, line => line.HasCollapsed);

        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.True(host.IsClosing);
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
    }

    /// <summary>Owner 10-09: switching the share card between 1:1 and 9:16 left the old card
    /// on screen. Clicking a segment must re-render the preview at that shape.</summary>
    [AvaloniaFact]
    public void ShareCard_FormatSegments_RerenderThePreview()
    {
        EnsureAppStyles();
        var (lib, events) = SampleData();
        var vm = new WrapViewModel(new History(events), lib, new WrapArchiveService(ArchiveFile), _ => null);
        var win = new WrapDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1300, Height = 900 };
        win.Show();
        try
        {
            // The rendered PNG's own height (IHDR); headless bitmaps don't decode real pixels.
            static int PngHeight(byte[]? png) => png is { Length: > 24 }
                ? (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23] : 0;

            Assert.True(PumpUntil(() => vm.HasData && !vm.IsLoading && PngHeight(vm.CurrentPng) == 1080, 5000),
                $"1:1 card is {PngHeight(vm.CurrentPng)} tall");

            win.FindControl<RadioButton>("StorySegment")!.IsChecked = true;
            Assert.True(PumpUntil(() => PngHeight(vm.CurrentPng) == 1920, 5000),
                $"9:16 card is {PngHeight(vm.CurrentPng)} tall");

            win.FindControl<RadioButton>("SquareSegment")!.IsChecked = true;
            Assert.True(PumpUntil(() => PngHeight(vm.CurrentPng) == 1080, 5000),
                $"1:1 card is {PngHeight(vm.CurrentPng)} tall");

            // Both shapes are rendered now: a switch is instant (no re-render to wait for).
            win.FindControl<RadioButton>("StorySegment")!.IsChecked = true;
            Assert.Equal(1920, PngHeight(vm.CurrentPng));
            win.FindControl<RadioButton>("SquareSegment")!.IsChecked = true;
            Assert.Equal(1080, PngHeight(vm.CurrentPng));
        }
        finally
        {
            win.Close();
            vm.Dispose();
        }
    }

    /// <summary>Saved years are whole-year snapshots: picking one hides "This month" and shows
    /// that year's recap; the share card follows.</summary>
    [AvaloniaFact]
    public void PickingASavedYear_ShowsItsRecap()
    {
        var (lib, events) = SampleData();
        var vm = new WrapViewModel(new History(events), lib, new WrapArchiveService(ArchiveFile), _ => null);
        Assert.True(PumpUntil(() => vm.Loading.IsCompleted && vm.AvailableYears.Count == 2));
        Assert.Equal(10 + 9 + 8 + 7 + 6 + 5, vm.Stats.TotalPlays);
        Assert.True(vm.IsCurrentYear);

        vm.SelectedYear = DateTime.Now.Year - 1;
        Assert.True(PumpUntil(() => vm.Loading.IsCompleted && vm.Stats.TotalPlays == 1));
        Assert.False(vm.IsCurrentYear);
        Assert.Equal((DateTime.Now.Year - 1).ToString(), vm.PeriodLabel);
        vm.Dispose();
    }

    // ── Share card ──

    private string Cover(string name, SKColor color)
    {
        var path = Path.Combine(_dir, name + ".png");
        using var bmp = new SKBitmap(64, 64);
        bmp.Erase(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static SKColor PixelAt(byte[] png, int x, int y)
    {
        using var bmp = SKBitmap.Decode(png);
        return bmp.GetPixel(x, y);
    }

    private static bool Near(SKColor a, SKColor b) =>
        Math.Abs(a.Red - b.Red) < 12 && Math.Abs(a.Green - b.Green) < 12 && Math.Abs(a.Blue - b.Blue) < 12;

    private WrapCardSpec CardSpec(ShareCardFormat format, string? hero, params string?[] covers) => new()
    {
        PeriodLabel = "2026",
        TopArtists = new[] { "Taylor Swift", "Juice WRLD", "Chase Atlantic", "Bad Bunny", "twenty one pilots" },
        TopTracks = new[] { "ANGELS", "INTRO", "Un Ratito", "Graduation", "Volví" },
        TotalMinutes = 20_891,
        TotalPlays = 8_470,
        LosslessPercent = 100,
        TopGenre = "Hip-Hop/Rap",
        ArtworkPath = hero,
        TopAlbum = "An Evening With Silk Sonic, the very long deluxe edition with bonus tracks",
        TopAlbumArtist = "Bruno Mars, Anderson .Paak & Silk Sonic",
        TopAlbumPlays = 17,
        AlbumCoverPaths = covers,
        Format = format,
    };

    /// <summary>The top album's cover is drawn as the 1:1 card's big tile.</summary>
    [Fact]
    public void SquareCard_DrawsTheTopAlbumCover()
    {
        var red = Cover("red", new SKColor(220, 30, 30));
        var png = ShareCardRenderer.RenderWrapCard(CardSpec(ShareCardFormat.Square, red, red));

        using var bmp = SKBitmap.Decode(png);
        Assert.Equal((1080, 1080), (bmp.Width, bmp.Height));
        Assert.True(Near(PixelAt(png, 230, 372), new SKColor(220, 30, 30)), $"hero tile: {PixelAt(png, 230, 372)}");
    }

    /// <summary>The 9:16 card has the big cover and the strip of the next albums' covers, and
    /// keeps everything above the wordmark inside the frame.</summary>
    [Fact]
    public void StoryCard_DrawsTheCoverAndTheStrip()
    {
        var red = Cover("red", new SKColor(220, 30, 30));
        var blue = Cover("blue", new SKColor(30, 60, 220));
        var png = ShareCardRenderer.RenderWrapCard(CardSpec(ShareCardFormat.Story, red, red, blue, blue, blue, blue));

        using var bmp = SKBitmap.Decode(png);
        Assert.Equal((1080, 1920), (bmp.Width, bmp.Height));
        Assert.True(Near(bmp.GetPixel(540, 568), new SKColor(220, 30, 30)), $"hero: {bmp.GetPixel(540, 568)}");
        // The album name wraps to two lines here, so the strip sits one line (58px) lower.
        Assert.True(Near(bmp.GetPixel(198, 1196), new SKColor(30, 60, 220)), $"strip: {bmp.GetPixel(198, 1196)}");
    }

    /// <summary>Without any album (nothing could be placed) the top artist leads and the card
    /// still renders in both formats.</summary>
    [Theory]
    [InlineData(ShareCardFormat.Square)]
    [InlineData(ShareCardFormat.Story)]
    public void Card_WithoutAlbums_StillRenders(ShareCardFormat format)
    {
        var spec = CardSpec(format, null) with { TopAlbum = null, TopAlbumArtist = null, TopAlbumPlays = 0 };
        var png = ShareCardRenderer.RenderWrapCard(spec);
        Assert.Equal(0x89, png[0]);
    }
}
