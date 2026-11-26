using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26: the "Choose Artwork" pop-up as an artist's artwork library. Real Skia only
/// (NOCTIS_TEST_SKIA=1) with NOCTIS_ARTLIB_SHOTS_DIR set: PNGs of the pop-up with fixture data.
/// </summary>
public class ArtworkLibrary1126ShotsTests
{
    private readonly ITestOutputHelper _o;
    public ArtworkLibrary1126ShotsTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    internal static Bitmap Cover(int i)
    {
        using var bmp = new SKBitmap(120, 120);
        using (var canvas = new SKCanvas(bmp))
        {
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(120, 120),
                    new[] { new SKColor((byte)(220 - 13 * (i % 12)), (byte)(60 + 11 * (i % 9)), (byte)(90 + 9 * (i % 7))), new SKColor(25, 20, 40) },
                    SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(0, 0, 120, 120, paint);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray());
        return new Bitmap(ms);
    }

    private static (MetadataViewModel vm, MetadataWindow win) Editor()
    {
        EnsureAppStyles();
        var apple = new ArtworkLibrarySearchTests.Apple();
        var service = new ITunesArtworkService(new System.Net.Http.HttpClient(
            new AnimatedArtworkChooserTests.StubHandler(apple.Respond))) { HomeStorefront = "us" };
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "OHMAMI", Artist = "Chase Atlantic", AlbumArtist = "Chase Atlantic",
            Album = "BEAUTY IN DEATH", AlbumId = Guid.NewGuid(), FilePath = "C:/m/does-not-exist/ohmami.flac",
        };
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var vm = new MetadataViewModel(track, new SearchPopupsShotsTests.OkTags(), lib, new TestPersistenceService(),
            new FakeAnimatedCoverService(), itunes: service, metadataSearch: new MetadataSearchPanelTests.FakeSearch());
        var win = new MetadataWindow(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        var load = vm.InitializeAsync();
        Pump(600);
        Assert.True(load.IsCompleted);
        var tabs = win.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "Artwork"));
        Pump(300);
        return (vm, win);
    }

    private static void Search(MetadataViewModel vm, string typed)
    {
        vm.ArtworkSearchInput = typed;
        var run = vm.SearchArtworkCommand.ExecuteAsync(null);
        var sw = Stopwatch.StartNew();
        while (!run.IsCompleted && sw.ElapsedMilliseconds < 5000) Pump(20);
        Assert.True(run.IsCompleted, "search never finished");
        // Fixture covers (the stub has no images).
        var i = 0;
        foreach (var r in vm.ArtworkSearchResults) r.Thumbnail = Cover(i++);
        Pump(700);
    }

    /// <summary>Owner 11-26: an artist's name shows their library: the header (name, count), the
    /// titled sections, five tiles a row, and the card sits inside the editor card, unclipped
    /// (its shadow was cut to the card's square bounds: the dark bottom corners).</summary>
    [AvaloniaFact]
    public void ChooseArtwork_ArtistName_ShowsTheLibrary_InsideTheEditor_Unclipped()
        => AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, LibraryProbe);

    private void LibraryProbe()
    {
        var (vm, win) = Editor();
        try
        {
            Search(vm, "CHASE ATLANTIC");
            var motion = win.ArtworkSearchMotion!;
            Assert.True(motion.IsOpen);
            var card = Assert.IsType<Noctis.Controls.ArtworkChooserCard>(motion.Content);
            var header = card.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "ChooserLibraryHeader");
            Assert.True(header.IsEffectivelyVisible);
            var texts = header.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Chase Atlantic", texts);
            Assert.Contains("28 releases", texts);
            Assert.True(card.FindControl<ScrollViewer>("ChooserSectionsScroll")!.IsEffectivelyVisible);
            Assert.False(((Control)card.FindControl<ItemsControl>("ChooserResults")!.Parent!).IsVisible);
            var titles = card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("Albums", titles);
            Assert.Contains("Singles & EPs", titles);
            Assert.Contains("Appears On", titles);

            // Five tiles a row.
            var posters = card.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("chooser-poster")).ToList();
            var firstRow = posters.Select(p => p.TranslatePoint(default, card)!.Value).Where(p => Math.Abs(p.Y - posters[0].TranslatePoint(default, card)!.Value.Y) < 1).Count();
            Assert.True(firstRow >= 5, $"{firstRow} tiles in the first row");

            // Rounded and unclipped: nothing between the card and the pop-up clips it to a square.
            var border = card.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("pill-popover"));
            Assert.Equal(new CornerRadius(24), border.CornerRadius);
            Assert.False(card.ClipToBounds, "the card clips its own shadow to a square (the dark bottom corners)");
            // Inside the editor card, with its shadow room.
            var editor = win.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "EditorCard");
            var cardBox = new Rect(border.TranslatePoint(default, win)!.Value, border.Bounds.Size);
            var editorBox = new Rect(editor.TranslatePoint(default, win)!.Value, editor.Bounds.Size);
            _o.WriteLine($"card {cardBox} editor {editorBox}");
            Assert.True(editorBox.Contains(cardBox), $"card {cardBox} runs out of the editor {editorBox}");
            // The presenter never has to scroll the card itself (that cut its bottom edge).
            var presenterScroll = card.GetVisualAncestors().OfType<ScrollViewer>().First();
            Assert.True(presenterScroll.Extent.Height <= presenterScroll.Viewport.Height + 0.5,
                $"pop-up content {presenterScroll.Extent.Height} taller than its host {presenterScroll.Viewport.Height}");

            SaveShot(win, "02-artist-library.png");
            Search(vm, "CHASE ATLANTIC DONT TRY THIS");
            Assert.Equal("DON'T TRY THIS - EP", vm.ArtworkSearchResults[0].CollectionName);
            SaveShot(win, "03-artist-and-title.png");
        }
        finally { win.Close(); Pump(300); }
    }

    private void SaveShot(Window win, string name)
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_ARTLIB_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        win.CaptureRenderedFrame()!.Save(path);
        _o.WriteLine(path);
    }
}
