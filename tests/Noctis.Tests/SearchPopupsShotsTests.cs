using System;
using System.Collections.Generic;
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
/// Owner 10-08: same UI + animation for search metadata. Real Skia only (NOCTIS_TEST_SKIA=1):
/// PNGs of every "search metadata" surface — the Find Metadata tool, the editor's Find online
/// panel, the artwork search pop-up — next to the Metadata editor they should match.
/// NOCTIS_SHOTS_PHASE picks the folder (before / after) so the two sets line up by name.
/// </summary>
public class SearchPopupsShotsTests
{
    private readonly ITestOutputHelper _o;
    public SearchPopupsShotsTests(ITestOutputHelper o) => _o = o;

    private static string ShotsDir => Path.Combine(@"D:\NoctisLyricsLab\search-popups",
        Environment.GetEnvironmentVariable("NOCTIS_SHOTS_PHASE") is { Length: > 0 } phase ? phase : "after");

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

    /// <summary>A busy owner, so the backdrop (blurred or not) shows what sits behind.</summary>
    private static Window Owner()
    {
        var stripes = new StackPanel();
        var colors = new[] { "#E74856", "#2D7DD2", "#F4D35E", "#3BB273", "#7B2CBF", "#FF8C42" };
        for (var i = 0; i < 18; i++)
            stripes.Children.Add(new Border
            {
                Height = 50,
                Background = new SolidColorBrush(Color.Parse(colors[i % colors.Length])),
                Child = new TextBlock { Text = $"Library row {i}", FontSize = 22, Margin = new Thickness(24, 8), Foreground = Brushes.White },
            });
        var owner = new Window { Width = 1100, Height = 820, Content = stripes, RequestedThemeVariant = ThemeVariant.Dark };
        owner.Show();
        PumpUntil(() => false, 100);
        return owner;
    }

    private static byte[] Cover(int size, SKColor a, SKColor b)
    {
        using var bmp = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bmp))
        {
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(size, size), new[] { a, b }, SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(0, 0, size, size, paint);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static Bitmap Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return Bitmap.DecodeToWidth(ms, 120);
    }

    internal static MetadataFinderViewModel FinderVm(IMetadataService? tags = null, FakeLibraryService? library = null)
    {
        var tracks = new[]
        {
            ("Track 01", "Unknown Artist", "Unknown Album"), ("monaco", "", "Unknown Album"),
            ("", "Bad Bunny", ""), ("Seda", "Unknown Artist", "nadie sabe"),
            ("Track 05", "", ""), ("hibiki", "Bad Bunny", "Unknown Album"),
        }.Select((t, i) => new Track
        {
            Id = Guid.NewGuid(), Title = t.Item1, Artist = t.Item2, Album = t.Item3,
            FilePath = TestPaths.Primary("Music", $"song{i}.flac"), Duration = TimeSpan.FromSeconds(200),
        }).ToList();
        var vm = new MetadataFinderViewModel(tracks, null!, tags ?? new OkTags(), library ?? new FakeLibraryService());
        vm.Rows[0].ApplyProposal(new TagSuggestion("NADIE SABE", "Bad Bunny", "nadie sabe lo que va a pasar mañana", 2023, 0.94, "Deezer"));
        vm.Rows[1].ApplyProposal(new TagSuggestion("MONACO", "Bad Bunny", "nadie sabe lo que va a pasar mañana", 2023, 0.88, "MusicBrainz"));
        vm.Rows[2].ApplyProposal(new TagSuggestion("Fina", "Bad Bunny", "nadie sabe lo que va a pasar mañana", 2023, 0.55, "Deezer"));
        vm.Rows[3].Status = "No match";
        return vm;
    }

    [AvaloniaFact]
    public void Probe_SavesEverySearchMetadataSurface()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        Directory.CreateDirectory(ShotsDir);
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var owner = Owner();
            try
            {
                // 1. Find Metadata (Settings → Tools → Find Metadata).
                var finder = new MetadataFinderDialog(FinderVm()) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
                _ = finder.ShowDialog(owner);
                PumpUntil(() => false, 700);
                Save(finder, "01-find-metadata-tool.png");
                finder.Close();
                PumpUntil(() => !finder.IsVisible, 2000);

                // 2. The Metadata editor (the reference), then its Find online panel.
                var search = new MetadataSearchPanelTests.FakeSearch { Result = MetadataSearchPanelShotsTests.TrackResults() };
                var (vm, win) = OpenEditor(owner, search);
                try
                {
                    PumpUntil(() => false, 600);
                    Save(win, "02-metadata-editor.png");

                    vm.OpenSearchPanelCommand.Execute(null);
                    PumpUntil(() => false, 500);
                    var i = 0;
                    foreach (var c in vm.SearchPanel.Candidates)
                        c.Thumbnail = Decode(Cover(240, new SKColor((byte)(60 + 40 * i), 80, (byte)(200 - 30 * i++)), new SKColor(20, 20, 30)));
                    vm.SearchPanel.RebuildComparison();
                    PumpUntil(() => false, 300);
                    Save(win, "03-find-online-panel.png");
                    vm.SearchPanel.CloseCommand.Execute(null);
                    PumpUntil(() => false, 400);

                    // 3. Artwork tab's search pop-up: the status card, then results.
                    var tabs = win.GetVisualDescendants().OfType<TabControl>().Single();
                    tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "Artwork"));
                    PumpUntil(() => false, 400);
                    vm.ArtworkSearchStatus = "Searching…";
                    vm.IsSearchingArtwork = true;
                    vm.IsArtworkSearchOpen = true;
                    PumpUntil(() => false, 400);
                    Save(win, "04-artwork-search-status.png");

                    for (var k = 0; k < 4; k++)
                        vm.ArtworkSearchResults.Add(new ArtworkSearchResult(
                            new ITunesArtworkService.ArtworkCandidate(k, $"nadie sabe {k + 1}", "Bad Bunny", "", "", "", ""),
                            Decode(Cover(240, new SKColor((byte)(220 - 40 * k), 70, 90), new SKColor(30, 20, 40)))));
                    vm.IsSearchingArtwork = false;
                    vm.HasArtworkSearchResults = true;
                    PumpUntil(() => false, 400);
                    Save(win, "05-artwork-search-results.png");
                    vm.IsArtworkSearchOpen = false;
                    PumpUntil(() => false, 200);
                }
                finally { win.Close(); PumpUntil(() => !win.IsVisible); }
            }
            finally { owner.Close(); }
        });
    }

    /// <summary>The artwork search pop-up's card is the dialog card's material (fill, rim) via
    /// the shared pill-popover class, and its Standard / Max are the dialog's pill buttons.</summary>
    [AvaloniaFact]
    public void ArtworkSearchPopup_UsesThePillPopoverCard()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var owner = new Window { Width = 1100, Height = 820, RequestedThemeVariant = ThemeVariant.Dark };
            owner.Show();
            var (vm, win) = OpenEditor(owner, new MetadataSearchPanelTests.FakeSearch());
            try
            {
                var host = win.GetVisualDescendants().OfType<Noctis.Controls.PillDialogHost>().Single();
                var tabs = win.GetVisualDescendants().OfType<TabControl>().Single();
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "Artwork"));
                PumpUntil(() => false, 300);
                vm.ArtworkSearchResults.Add(new ArtworkSearchResult(
                    new ITunesArtworkService.ArtworkCandidate(1, "nadie sabe", "Bad Bunny", "", "", "", ""), null));
                vm.HasArtworkSearchResults = true;
                vm.IsArtworkSearchOpen = true;
                PumpUntil(() => false, 300);

                var cards = win.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("pill-popover")).ToList();
                var card = cards.Single(b => b.IsEffectivelyVisible);
                Assert.Equal(new CornerRadius(24), card.CornerRadius);
                Assert.Equal(AccentTestHarness.ColorOf(host.Background), AccentTestHarness.ColorOf(card.Background));
                Assert.NotNull(card.BorderBrush);
                var buttons = card.GetVisualDescendants().OfType<Button>().ToList();
                Assert.Contains(buttons, b => b.Classes.Contains("pill-secondary") && Equals(b.Content, "Standard"));
                Assert.Contains(buttons, b => b.Classes.Contains("pill-primary") && Equals(b.Content, "Max"));
                vm.IsArtworkSearchOpen = false;
                PumpUntil(() => false, 100);
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); owner.Close(); }
        });
    }

    /// <summary>The empty artwork preview the search pop-up opens over was painted in the
    /// theme's BaseLow, the card's own colour on Dark (#252525 on #252525): no visible box.</summary>
    [AvaloniaFact]
    public void EmptyArtworkPreview_StandsOutFromTheCard()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var owner = new Window { Width = 1100, Height = 820, RequestedThemeVariant = ThemeVariant.Dark };
            owner.Show();
            var (vm, win) = OpenEditor(owner, new MetadataSearchPanelTests.FakeSearch());
            try
            {
                var host = win.GetVisualDescendants().OfType<Noctis.Controls.PillDialogHost>().Single();
                var tabs = win.GetVisualDescendants().OfType<TabControl>().Single();
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "Artwork"));
                PumpUntil(() => false, 300);
                Assert.False(vm.HasArtwork);
                var preview = win.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ArtworkPreviewAnchor");
                _o.WriteLine($"card {AccentTestHarness.ColorOf(host.Background)} preview {AccentTestHarness.ColorOf(preview.Background)}");
                Assert.NotEqual(AccentTestHarness.ColorOf(host.Background), AccentTestHarness.ColorOf(preview.Background));
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(preview.Background));
                Assert.Equal(new CornerRadius(24), preview.CornerRadius);
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); owner.Close(); }
        });
    }

    private static (MetadataViewModel vm, MetadataWindow win) OpenEditor(Window owner, MetadataSearchPanelTests.FakeSearch search)
    {
        var albumId = Guid.NewGuid();
        var tracks = new List<Track>
        {
            new() { Id = Guid.NewGuid(), Title = "monaco", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny", Album = "nadie sabe", AlbumId = albumId,
                TrackNumber = 2, Genre = "Latin", Year = 2023, Duration = TimeSpan.FromSeconds(267), FilePath = "C:/m/does-not-exist/monaco.flac" },
        };
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var vm = new MetadataViewModel(tracks[0], new OkTags(), lib, new TestPersistenceService(), new FakeAnimatedCoverService(), metadataSearch: search);
        var win = new MetadataWindow(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        _ = win.ShowDialog(owner);
        var load = vm.InitializeAsync();
        Assert.True(PumpUntil(() => load.IsCompleted));
        return (vm, win);
    }

    private void Save(Window win, string name)
    {
        var path = Path.Combine(ShotsDir, name);
        win.CaptureRenderedFrame()!.Save(path, PngBitmapEncoderOptions.Default);
        _o.WriteLine(path);
    }

    internal sealed class OkTags : IMetadataService
    {
        public Func<Track, bool> Write { get; set; } = _ => true;
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => Write(track);
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => true;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => true;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields, AdvancedTagIO.AdvancedFields original) => true;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => true;
    }
}
