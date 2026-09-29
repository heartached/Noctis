using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Artwork › Album Page Colour › Tint strength (Discord ask 09-24: "I confuse
/// which is the background and which is the button"). The slider blends the cover colour
/// into the theme page; 100 % is the page exactly as before. On a tint the header pills,
/// heart and "…" are kept at 3:1 or more against the page, whatever the slider says.
/// </summary>
public class AlbumPageTintStrengthTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class FakeLastFm : ILastFmService
    {
        public bool IsAuthenticated => false;
        public string? Username => null;
        public void Configure(string? sessionKey) { }
        public Task<string> GetAuthUrlAsync() => Task.FromResult(string.Empty);
        public Task<bool> CompleteAuthAsync() => Task.FromResult(false);
        public string? GetSessionKey() => null;
        public void Logout() { }
        public Task ScrobbleAsync(Track track, DateTime startedAt) => Task.CompletedTask;
        public Task UpdateNowPlayingAsync(Track track) => Task.CompletedTask;
        public Task<string?> GetAlbumDescriptionAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string a, string b, string? d, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string a, string b, CancellationToken ct = default) => Task.CompletedTask;
    }

    private SettingsViewModel CreateSettings() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    private static AlbumDetailViewModel MakeAlbumVm(SettingsViewModel? settings, int trackCount = 0)
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var album = new Album { Id = Guid.NewGuid(), Name = "A", Artist = "B", Tracks = new List<Track>() };
        for (var i = 1; i <= trackCount; i++)
            album.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(),
                FilePath = TestPaths.Primary("tintstrength", "A", $"{i:00} Song {i}.flac"),
                Title = $"Song {i}", Artist = "B", AlbumArtist = "B", Album = "A", TrackNumber = i,
            });
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        return new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm(), settings);
    }

    /// <summary>Cover edge colours the owner asked about.</summary>
    public static readonly Color[] SampleCovers =
    {
        Color.FromRgb(0x13, 0x2A, 0x5E), // dark blue (ILLIT "bomb"-like)
        Color.FromRgb(0x13, 0x50, 0x9E), // mid blue (DAYS BEFORE RODEO)
        Color.FromRgb(0xF4, 0xD0, 0x3A), // bright yellow
        Color.FromRgb(0x80, 0x80, 0x80), // grey
        Color.FromRgb(0x08, 0x08, 0x0A), // near-black
        Color.FromRgb(0xF7, 0xF5, 0xF2), // near-white
        Color.FromRgb(0xC8, 0x14, 0x1E), // saturated red
        Color.FromRgb(0xA6, 0x33, 0x4E), // Red (Taylor's Version)-like
        Color.FromRgb(0xFF, 0xE7, 0xC7), // Lover cream
    };

    private static readonly Color[] ThemePages =
    {
        Color.FromRgb(0x0F, 0x0F, 0x0F), // Ink
        Color.FromRgb(0x25, 0x25, 0x25), // default dark
        Color.FromRgb(0x00, 0x00, 0x00), // Dark
        Color.FromRgb(0xFF, 0xFF, 0xFF), // Light
    };

    private static readonly int[] Strengths = { 100, 80, 60, 40, 25, 10, 0 };

    // ── Setting ──

    [Fact]
    public void FreshInstall_IsFullStrength()
    {
        Assert.Equal(100, AppSettings.AlbumPageTintStrengthDefault);
        Assert.Equal(AppSettings.AlbumPageTintStrengthDefault, new AppSettings().AlbumPageTintStrength);
    }

    [AvaloniaFact]
    public async Task TintStrength_SurvivesSaveAndReload()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.Equal(AppSettings.AlbumPageTintStrengthDefault, vm.AlbumPageTintStrength);

        vm.AlbumPageTintStrength = 35;
        await vm.SaveAsync();

        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.Equal(35, reloaded.AlbumPageTintStrength);
        Assert.Equal(35, reloaded.GetSettings().AlbumPageTintStrength);
    }

    /// <summary>Raises the routed event a double click raises (as LyricsMinLineOpacitySliderTests).</summary>
    private static void DoubleTap(Control target, Visual root)
    {
        var pointer = new Avalonia.Input.Pointer(1, Avalonia.Input.PointerType.Mouse, true);
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root) ?? default;
        var press = new Avalonia.Input.PointerPressedEventArgs(
            target, pointer, root, point, 0,
            new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
            Avalonia.Input.KeyModifiers.None, 2);
        target.RaiseEvent(new Avalonia.Input.TappedEventArgs(Avalonia.Input.InputElement.DoubleTappedEvent, press));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Settings › Appearance › Artwork: the slider sits under the Album Page Colour
    /// toggle and only opens while the toggle is on; double-tap restores 100 %.</summary>
    [AvaloniaFact]
    public async Task SettingsSlider_OpensWithTheToggle_AndDoubleTapRestoresFullStrength()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.False(vm.AlbumPageTintEnabled);
        vm.SelectedSettingsTab = SettingsViewModel.TabAppearance;
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1000, Height = 720, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout();
            var slider = view.FindControl<Slider>("AlbumPageTintStrengthSlider")!;
            Assert.NotNull(slider);
            Assert.Equal(0, slider.Minimum);
            Assert.Equal(100, slider.Maximum);
            Assert.True(slider.IsSnapToTickEnabled);
            var fold = slider.GetLogicalAncestors().OfType<CollapsibleContent>().First();
            Assert.False(fold.IsOpen);

            vm.AlbumPageTintEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(fold.IsOpen);
            window.UpdateLayout();
            Assert.Equal(100, slider.Value, 3);

            vm.AlbumPageTintStrength = 42;
            window.UpdateLayout();
            Assert.Equal(42, slider.Value, 3);
            var label = slider.GetLogicalSiblings().OfType<TextBlock>().First();
            Assert.Equal("42%", label.Text);

            DoubleTap(slider, window);
            Assert.Equal(AppSettings.AlbumPageTintStrengthDefault, vm.AlbumPageTintStrength);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Page colour ──

    [Fact]
    public void FullStrength_IsTheCoverColourExactly()
    {
        foreach (var edge in SampleCovers)
        foreach (var page in ThemePages)
            Assert.Equal(edge, AlbumTintPalette.PageColor(edge, page, 100));
    }

    [Fact]
    public void Strength_BlendsIntoTheThemePage()
    {
        var edge = Color.FromRgb(0xC8, 0x14, 0x1E);
        var ink = Color.FromRgb(0x0F, 0x0F, 0x0F);
        Assert.Equal(ink, AlbumTintPalette.PageColor(edge, ink, 0));
        Assert.Equal(Color.FromRgb(0x6C, 0x12, 0x16), AlbumTintPalette.PageColor(edge, ink, 50));
        // Clamped, never extrapolated.
        Assert.Equal(edge, AlbumTintPalette.PageColor(edge, ink, 180));
        Assert.Equal(ink, AlbumTintPalette.PageColor(edge, ink, -5));
    }

    /// <summary>At the default the album VM paints exactly the pre-slider page: the cover
    /// colour itself and the old 0.55-luminance text flip, mid-tone covers included.</summary>
    [AvaloniaFact]
    public void DefaultStrength_ReproducesTodaysPage()
    {
        var vm = MakeAlbumVm(CreateSettings());
        var midTones = new[] { Color.FromRgb(0x9A, 0x9A, 0x9A), Color.FromRgb(0x6F, 0xA8, 0xDC) };
        foreach (var edge in SampleCovers.Concat(midTones))
        {
            vm.ApplyTint(edge);
            Assert.Equal(edge, ((ISolidColorBrush)vm.BackgroundBrush!).Color);
            var legacyLight = DominantColorExtractor.GetRelativeLuminance(edge) > AlbumDetailViewModel.LightTintThreshold;
            Assert.Equal(legacyLight, vm.IsLightTint);
        }
    }

    [AvaloniaFact]
    public void MovingTheSlider_ReblendsAnOpenPageLive()
    {
        var settings = CreateSettings();
        var vm = MakeAlbumVm(settings);
        var edge = Color.FromRgb(0x13, 0x50, 0x9E);
        vm.ApplyTint(edge);
        Assert.Equal(edge, ((ISolidColorBrush)vm.BackgroundBrush!).Color);

        settings.AlbumPageTintStrength = 40;
        Dispatcher.UIThread.RunJobs();
        var expected = AlbumTintPalette.PageColor(edge, AlbumDetailViewModel.ThemePageColor(), 40);
        Assert.NotEqual(edge, expected);
        Assert.Equal(expected, ((ISolidColorBrush)vm.BackgroundBrush!).Color);

        settings.AlbumPageTintStrength = 100;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(edge, ((ISolidColorBrush)vm.BackgroundBrush!).Color);

        // Tint off: the slider has nothing to blend.
        vm.ApplyTint(null);
        settings.AlbumPageTintStrength = 30;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.BackgroundBrush);
    }

    /// <summary>The Light theme blends dark covers into mid greys; below 100 % the page text
    /// goes dark as soon as white would read under 3:1.</summary>
    [Fact]
    public void BlendedPages_KeepThePageTextReadable()
    {
        foreach (var edge in SampleCovers)
        foreach (var theme in ThemePages)
        foreach (var s in Strengths.Where(x => x < 100))
        {
            var page = AlbumTintPalette.PageColor(edge, theme, s);
            var dark = AlbumTintPalette.UsesDarkText(page, s, AlbumDetailViewModel.LightTintThreshold);
            var text = dark ? AlbumTintPalette.DarkText : Colors.White;
            Assert.True(AlbumTintPalette.Contrast(text, page) >= 3.0,
                $"text {text} on {page} (cover {edge}, theme {theme}, {s}%) = {AlbumTintPalette.Contrast(text, page):0.00}");
        }
    }

    // ── Header colours ──

    public static IEnumerable<object[]> Accents()
    {
        yield return new object[] { "#E74856" }; // Crimson (default)
        yield return new object[] { "#0FA3B1" }; // Teal
        yield return new object[] { "#FFFFFF" }; // white accent
        yield return new object[] { "#101010" }; // near-black accent
    }

    [Theory]
    [MemberData(nameof(Accents))]
    public void HeaderControls_StandApartFromEveryPage(string accentHex)
    {
        var fixedAccent = Color.Parse(accentHex);
        foreach (var edge in SampleCovers)
        foreach (var theme in ThemePages)
        foreach (var s in Strengths)
        {
            var page = AlbumTintPalette.PageColor(edge, theme, s);
            // Worst case too: "Accent follows album art" makes the accent the cover colour.
            foreach (var accent in new[] { fixedAccent, edge })
            {
                var label = accent == Colors.White ? Colors.Black : Colors.White;
                var c = AlbumTintPalette.ForPage(page, accent, label, Colors.Transparent, accent);
                var where = $"accent {accent} on page {page} (cover {edge}, theme {theme}, {s}%)";
                Assert.True(AlbumTintPalette.Contrast(c.Fill, page) >= AlbumTintPalette.MinFillContrast, $"pill {c.Fill}: {where}");
                Assert.True(AlbumTintPalette.Contrast(c.Label, c.Fill) >= AlbumTintPalette.MinLabelContrast, $"label {c.Label} on {c.Fill}: {where}");
                if (c.Label != label)
                    Assert.True(AlbumTintPalette.Contrast(c.Label, c.Fill) >= 4.5, $"swapped label {c.Label} on {c.Fill}: {where}");
                Assert.True(AlbumTintPalette.Contrast(c.Icon, page) >= AlbumTintPalette.MinFillContrast, $"dots {c.Icon}: {where}");
                Assert.True(AlbumTintPalette.Contrast(c.Heart, page) >= AlbumTintPalette.MinFillContrast, $"heart {c.Heart}: {where}");
            }
        }
    }

    /// <summary>An accent that already stands apart from the page is left exactly as it is
    /// (fill, label and rim), so most pages look as they always did.</summary>
    [Fact]
    public void ClearAccent_IsKeptAsIs()
    {
        var page = Color.FromRgb(0x08, 0x08, 0x0A);
        var teal = Color.Parse("#0FA3B1");
        var rim = Color.Parse("#0B7A85");
        var c = AlbumTintPalette.ForPage(page, teal, Colors.White, rim, teal);
        Assert.Equal(teal, c.Fill);
        Assert.Equal(Colors.White, c.Label);
        Assert.Equal(rim, c.Border);
        Assert.Equal(teal, c.Icon);
        Assert.Equal(AlbumTintPalette.HeartRed, c.Heart);
    }

    /// <summary>The reported case: the accent follows the cover, so pill and page were the
    /// same colour. The pill keeps the hue but moves away from the page.</summary>
    [Fact]
    public void AccentFromTheSameCover_KeepsItsHueButSeparates()
    {
        var page = Color.FromRgb(0xA6, 0x33, 0x4E);
        var accent = Color.FromRgb(0xBA, 0x1C, 0x4B);
        Assert.True(AlbumTintPalette.Contrast(accent, page) < 1.5); // what the user saw
        var c = AlbumTintPalette.ForPage(page, accent, Colors.White, Colors.Transparent, accent);
        Assert.True(AlbumTintPalette.Contrast(c.Fill, page) >= 3.0);
        Assert.True(c.Fill.R > c.Fill.G && c.Fill.R > c.Fill.B, $"expected a red/pink pill, got {c.Fill}");
        Assert.Equal(Colors.Transparent, c.Border);
    }

    // ── Mounted view ──

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    /// <summary>The pill's settled fill: Button carries a global 60 ms Background transition,
    /// so the live value can still be mid-lerp right after a change.</summary>
    private static Color PillFill(Button b) => ((ISolidColorBrush)b.GetBaseValue(Button.BackgroundProperty).Value!).Color;

    /// <summary>The view really paints the VM's colours on a tint, and the accent
    /// resources on the plain page.</summary>
    [AvaloniaFact]
    public void MountedHeader_UsesTheTintColoursOnlyOnATint()
    {
        EnsureAppStyles();
        var settings = CreateSettings();
        var vm = MakeAlbumVm(settings, trackCount: 2);
        var view = new AlbumDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var play = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("album-header-action"));
            var dots = view.GetVisualDescendants().OfType<PathIcon>().First(i => i.Classes.Contains("album-dots"));
            var heart = view.GetVisualDescendants().OfType<HeartIcon>().First(h => h.Classes.Contains("album-heart"));

            // Plain page: the accent resources, untouched.
            var accent = (ISolidColorBrush)view.FindResource(Application.Current!.ActualThemeVariant, "AccentButtonBackground")!;
            Assert.Null(vm.BackgroundBrush);
            Assert.Equal(accent.Color, PillFill(play));

            // A tint the accent melts into: the pill, its label, the dots and the heart
            // take the contrast-checked colours.
            vm.ApplyTint(accent.Color);
            Dispatcher.UIThread.RunJobs();
            var page = ((ISolidColorBrush)vm.BackgroundBrush!).Color;
            var fill = PillFill(play);
            Assert.Equal(((ISolidColorBrush)vm.TintButtonBackground).Color, fill);
            Assert.Equal(((ISolidColorBrush)vm.TintButtonForeground).Color, ((ISolidColorBrush)play.Foreground!).Color);
            Assert.True(AlbumTintPalette.Contrast(fill, page) >= 3.0, $"pill {fill} on {page}");
            Assert.Equal(((ISolidColorBrush)vm.TintIconBrush).Color, ((ISolidColorBrush)dots.Foreground!).Color);
            Assert.Same(vm.TintHeartBrush, heart.OnBrush);

            // The slider moves the page and the pills follow.
            settings.AlbumPageTintStrength = 25;
            Dispatcher.UIThread.RunJobs();
            var page25 = ((ISolidColorBrush)vm.BackgroundBrush!).Color;
            Assert.NotEqual(page, page25);
            Assert.True(AlbumTintPalette.Contrast(PillFill(play), page25) >= 3.0);

            // Back to the plain page: the accent again.
            vm.ApplyTint(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(accent.Color, PillFill(play));
        }
        finally
        {
            win.Close();
        }
    }
}
