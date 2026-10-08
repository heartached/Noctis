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
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.MetadataSearch;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: Search metadata revamp — the Find online panel inside the real metadata
/// window: it opens over the editor and lays out (always), and under real Skia
/// (NOCTIS_TEST_SKIA=1) PNGs of the single-track compare, the album track table and the
/// applied banner for review.
/// </summary>
public class MetadataSearchPanelShotsTests
{
    private readonly ITestOutputHelper _o;
    public MetadataSearchPanelShotsTests(ITestOutputHelper o) => _o = o;

    private const string ShotsDir = @"D:\NoctisLyricsLab\metadata-search\shots";

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

    /// <summary>A cover-like JPEG: diagonal gradient with a soft disc.</summary>
    private static byte[] Cover(int size, SKColor a, SKColor b)
    {
        using var bmp = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bmp))
        {
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(size, size),
                    new[] { a, b }, SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(0, 0, size, size, paint);
            using var disc = new SKPaint { Color = new SKColor(255, 255, 255, 60), IsAntialias = true };
            canvas.DrawCircle(size * 0.62f, size * 0.4f, size * 0.22f, disc);
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

    private static Track T(string title, int n, Guid albumId, int seconds) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
        Album = "nadie sabe", AlbumId = albumId, TrackNumber = n, TrackCount = 0, DiscNumber = 1,
        Genre = "Latin", Year = 2023, Duration = TimeSpan.FromSeconds(seconds),
        FilePath = "C:/m/does-not-exist/" + title + ".flac",
    };

    private static readonly (SKColor, SKColor)[] Palette =
    {
        (new SKColor(0xE7, 0x48, 0x56), new SKColor(0x3A, 0x0C, 0x2E)),
        (new SKColor(0x2D, 0x7D, 0xD2), new SKColor(0x10, 0x1E, 0x3C)),
        (new SKColor(0xF4, 0xD3, 0x5E), new SKColor(0xB0, 0x48, 0x1E)),
        (new SKColor(0x3B, 0xB2, 0x73), new SKColor(0x0E, 0x2E, 0x22)),
        (new SKColor(0x7B, 0x2C, 0xBF), new SKColor(0x1C, 0x0A, 0x30)),
    };

    private static MetadataCandidate Release(string provider, double conf, string album, int year, string[] notes, IReadOnlyList<CandidateTrack> tracks) => new()
    {
        Provider = provider, ProviderId = provider + conf, Confidence = conf, MatchNotes = notes,
        Title = album, Album = album, Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
        ReleaseDate = $"{year}-10-13", Year = year, Genre = "Música Urbana", TrackCount = tracks.Count, DiscCount = 1,
        Label = "Rimas Entertainment LLC", Copyright = "℗ 2023 Rimas Entertainment LLC", Barcode = "197190537925",
        Explicit = true, ArtworkUrl = new Uri("https://example.test/a.jpg"), ArtworkSize = 3000, Tracks = tracks,
    };

    private static MetadataSearchResult TrackResults() => new()
    {
        Candidates = new[]
        {
            new MetadataCandidate
            {
                Provider = "Deezer", ProviderId = "1", Confidence = 0.94, MatchNotes = new[] { "Duration ±1 s", "Same ISRC" },
                Title = "MONACO", Artist = "Bad Bunny", Album = "nadie sabe lo que va a pasar mañana", AlbumArtist = "Bad Bunny",
                ReleaseDate = "2023-10-13", Year = 2023, Genre = "Música Urbana", TrackNumber = 2, TrackCount = 22, DiscNumber = 1, DiscCount = 1,
                Composer = "Benito Antonio Martínez Ocasio", Label = "Rimas Entertainment LLC",
                Copyright = "℗ 2023 Rimas Entertainment LLC", Isrc = "QM6MZ2370002", Explicit = true, Bpm = 136,
                ArtworkUrl = new Uri("https://example.test/a.jpg"), ArtworkThumbUrl = new Uri("https://example.test/a-thumb.jpg"), ArtworkSize = 1400,
            },
            new MetadataCandidate
            {
                Provider = "Apple Music", ProviderId = "2", Confidence = 0.88, MatchNotes = new[] { "Duration ±2 s" },
                Title = "MONACO", Artist = "Bad Bunny", Album = "nadie sabe lo que va a pasar mañana", Year = 2023,
                Genre = "Latin", TrackNumber = 2, ArtworkUrl = new Uri("https://example.test/b.jpg"), ArtworkSize = 3000,
            },
            new MetadataCandidate
            {
                Provider = "MusicBrainz", ProviderId = "3", Confidence = 0.67, MatchNotes = new[] { "Different length (+31 s)" },
                Title = "Monaco (Live)", Artist = "Bad Bunny", Album = "Most Wanted Tour (Live)", Year = 2024,
                ArtworkUrl = new Uri("https://example.test/c.jpg"),
            },
            new MetadataCandidate
            {
                Provider = "Deezer", ProviderId = "4", Confidence = 0.41, MatchNotes = new[] { "Title only" },
                Title = "Monaco", Artist = "Charles Aznavour", Album = "Monaco", Year = 1976,
                ArtworkUrl = new Uri("https://example.test/d.jpg"),
            },
        },
        Providers = new[]
        {
            new ProviderStatus("Deezer", ProviderOutcome.Ok, 2),
            new ProviderStatus("MusicBrainz", ProviderOutcome.Ok, 1),
            new ProviderStatus("Apple Music", ProviderOutcome.Ok, 1),
            new ProviderStatus("Discogs", ProviderOutcome.TimedOut, 0),
        },
    };

    private (MetadataViewModel vm, MetadataWindow win) OpenWindow(MetadataSearchPanelTests.FakeSearch search, bool album, byte[]? currentCover)
    {
        var albumId = Guid.NewGuid();
        var tracks = new List<Track>
        {
            T("NADIE SABE", 1, albumId, 377), T("monaco", 2, albumId, 267), T("fina", 3, albumId, 216),
            T("HIBIKI", 4, albumId, 208), T("Mr. October", 5, albumId, 190), T("CYBERTRUCK", 6, albumId, 189),
            T("VOU 787", 7, albumId, 157), T("Seda", 8, albumId, 182),
        };
        var p = new TestPersistenceService();
        if (currentCover != null)
        {
            var path = p.GetArtworkPath(albumId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, currentCover);
        }
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var vm = album
            ? new MetadataViewModel(tracks[0], new NullTags(), lib, p, new FakeAnimatedCoverService(), albumScoped: true, albumTracks: tracks, metadataSearch: search)
            : new MetadataViewModel(tracks[1], new NullTags(), lib, p, new FakeAnimatedCoverService(), metadataSearch: search);
        var win = new MetadataWindow(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1000, Height = 860 };
        win.Show();
        var load = vm.InitializeAsync();
        Assert.True(PumpUntil(() => load.IsCompleted));
        return (vm, win);
    }

    private static void GiveThumbnails(MetadataViewModel vm)
    {
        var i = 0;
        foreach (var c in vm.SearchPanel.Candidates)
        {
            var (a, b) = Palette[i++ % Palette.Length];
            c.Thumbnail = Decode(Cover(240, a, b));
        }
        // The selected card's thumbnail stands in as the "after" cover until downloaded.
        vm.SearchPanel.RebuildComparison();
    }

    [AvaloniaFact]
    public void Panel_OpensOverTheEditor_AndClosesBack()
    {
        EnsureAppStyles();
        var search = new MetadataSearchPanelTests.FakeSearch { Result = TrackResults() };
        var (vm, win) = OpenWindow(search, album: false, currentCover: null);
        try
        {
            var view = win.GetVisualDescendants().OfType<MetadataSearchPanel>().Single();
            Assert.False(view.IsVisible);
            vm.OpenSearchPanelCommand.Execute(null);
            Assert.True(PumpUntil(() => view.IsVisible && view.Opacity > 0.99));
            Assert.True(view.Bounds.Width > 800 && view.Bounds.Height > 500, $"panel {view.Bounds}");
            var list = view.GetVisualDescendants().OfType<ListBox>().First(l => l.Classes.Contains("ms-results"));
            Assert.Equal(4, list.ItemCount);
            Assert.Equal(0, list.SelectedIndex);
            // The comparison lists the changed rows.
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "nadie sabe lo que va a pasar mañana" && t.IsEffectivelyVisible);

            vm.SearchPanel.CloseCommand.Execute(null);
            Assert.True(PumpUntil(() => !view.IsVisible, 2000), "panel never collapsed");
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void Probe_SavesThePanelShots()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        Directory.CreateDirectory(ShotsDir);
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            // 1. Single track, small current cover, results with thumbnails, compare open.
            var search = new MetadataSearchPanelTests.FakeSearch { Result = TrackResults(), Artwork = Cover(1400, Palette[0].Item1, Palette[0].Item2) };
            var (vm, win) = OpenWindow(search, album: false, currentCover: Cover(500, new SKColor(90, 90, 100), new SKColor(30, 30, 36)));
            try
            {
                vm.OpenSearchPanelCommand.Execute(null);
                PumpUntil(() => false, 400);
                GiveThumbnails(vm);
                vm.SearchPanel.UseArtwork = true;
                PumpUntil(() => false, 400);
                Save(win, "01-track-compare.png");

                vm.SearchPanel.ShowUnchanged = true;
                vm.SearchPanel.OnlyFillEmpty = true;
                PumpUntil(() => false, 300);
                Save(win, "02-track-only-fill-empty-show-unchanged.png");

                vm.SearchPanel.ShowUnchanged = false;
                vm.SearchPanel.OnlyFillEmpty = false;
                vm.SearchPanel.ApplyCommand.Execute(null);
                PumpUntil(() => false, 500);
                Save(win, "03-applied-banner.png");
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }

            // 2. Album scope: fields, then the track table.
            var albumTracks = new[]
            {
                ("NADIE SABE", 1, 378), ("MONACO", 2, 267), ("FINA", 3, 216), ("HIBIKI", 4, 208),
                ("MR. OCTOBER", 5, 190), ("CYBERTRUCK", 6, 189), ("VOU 787", 7, 157), ("SEDA", 8, 182), ("GRACIAS POR NADA", 9, 162),
            }.Select(t => new CandidateTrack { Title = t.Item1, TrackNumber = t.Item2, DiscNumber = 1, Duration = TimeSpan.FromSeconds(t.Item3) }).ToList();
            var albumSearch = new MetadataSearchPanelTests.FakeSearch
            {
                Result = new MetadataSearchResult
                {
                    Candidates = new[]
                    {
                        Release("Deezer + MusicBrainz", 0.96, "nadie sabe lo que va a pasar mañana", 2023, new[] { "8 of 9 tracks", "Durations ±1 s" }, albumTracks),
                        Release("Apple Music", 0.81, "nadie sabe lo que va a pasar mañana", 2023, new[] { "Clean version" }, albumTracks.Take(8).ToList()),
                        Release("MusicBrainz", 0.52, "nadie sabe (Japan edition)", 2024, new[] { "Extra tracks" }, albumTracks.Concat(albumTracks.Take(2)).ToList()),
                    },
                    Providers = new[]
                    {
                        new ProviderStatus("Deezer", ProviderOutcome.Ok, 1), new ProviderStatus("MusicBrainz", ProviderOutcome.Ok, 2),
                        new ProviderStatus("Apple Music", ProviderOutcome.Ok, 1), new ProviderStatus("Discogs", ProviderOutcome.Failed, 0),
                    },
                },
                Artwork = Cover(3000, Palette[4].Item1, Palette[4].Item2),
            };
            (vm, win) = OpenWindow(albumSearch, album: true, currentCover: Cover(600, new SKColor(70, 70, 80), new SKColor(20, 20, 26)));
            try
            {
                vm.OpenSearchPanelCommand.Execute(null);
                PumpUntil(() => false, 400);
                GiveThumbnails(vm);
                PumpUntil(() => false, 300);
                Save(win, "04-album-fields.png");
                vm.SearchPanel.ShowTracksPaneCommand.Execute(null);
                PumpUntil(() => false, 300);
                Save(win, "05-album-tracks.png");
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }

            // 3. Searching skeleton and the unavailable state.
            var slow = new MetadataSearchPanelTests.FakeSearch { Gate = true };
            (vm, win) = OpenWindow(slow, album: false, currentCover: null);
            try
            {
                vm.OpenSearchPanelCommand.Execute(null);
                PumpUntil(() => false, 400);
                Save(win, "06-searching.png");
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }

            (vm, win) = OpenWindow(null!, album: false, currentCover: null);
            try
            {
                vm.OpenSearchPanelCommand.Execute(null);
                PumpUntil(() => false, 400);
                Save(win, "07-unavailable.png");
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    private void Save(Window win, string name)
    {
        var path = Path.Combine(ShotsDir, name);
        win.CaptureRenderedFrame()!.Save(path, PngBitmapEncoderOptions.Default);
        _o.WriteLine(path);
    }

    private sealed class NullTags : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => true;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => true;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => true;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields, AdvancedTagIO.AdvancedFields original) => true;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => true;
    }
}
