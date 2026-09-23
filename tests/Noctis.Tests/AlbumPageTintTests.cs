using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Album page tint (Apple Music parity, revived 2026-09-03): the cover's edge colour
/// becomes the page background, the page text flips dark on light covers, and the
/// Appearance toggle clears it. The extractor is the Skia, worker-thread-safe path.
/// </summary>
public class AlbumPageTintTests
{
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
        public Task<string?> GetAlbumDescriptionAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string artistName, string albumName, string? description, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>A 120Ã—120 PNG with a solid border colour and a contrasting centre block,
    /// so edge-ring extraction is distinguishable from a whole-image average.</summary>
    private static string WriteCover(SKColor edge, SKColor centre)
    {
        using var bmp = new SKBitmap(120, 120);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(edge);
            using var paint = new SKPaint { Color = centre };
            canvas.DrawRect(new SKRect(20, 20, 100, 100), paint);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(Path.GetTempPath(), $"noctis-tint-{Guid.NewGuid():N}.png");
        using (var fs = File.Create(path)) data.SaveTo(fs);
        return path;
    }

    private static AlbumDetailViewModel MakeVm()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var album = new Album { Id = Guid.NewGuid(), Name = "A", Artist = "B", Tracks = new List<Track>() };
        return new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());
    }

    [Fact]
    public void EdgeExtractor_ReadsTheBorderNotTheCentre()
    {
        var path = WriteCover(new SKColor(0xF2, 0xC1, 0xD1), new SKColor(0x10, 0x20, 0x30));
        try
        {
            var color = DominantColorExtractor.ExtractEdgeBackgroundColorFromFile(path);
            Assert.NotNull(color);
            // Pink border wins; the dark centre block never votes (edge ring only).
            Assert.InRange(color!.Value.R, 0xE0, 0xFF);
            Assert.InRange(color.Value.G, 0xB0, 0xD0);
            Assert.InRange(color.Value.B, 0xC0, 0xE0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EdgeExtractor_MissingFile_IsNull()
        => Assert.Null(DominantColorExtractor.ExtractEdgeBackgroundColorFromFile(
            Path.Combine(Path.GetTempPath(), "does-not-exist.jpg")));

    [Fact]
    public void RelativeLuminance_BlackWhiteAndMid()
    {
        Assert.Equal(0, DominantColorExtractor.GetRelativeLuminance(Colors.Black), 6);
        Assert.Equal(1, DominantColorExtractor.GetRelativeLuminance(Colors.White), 6);
        Assert.InRange(DominantColorExtractor.GetRelativeLuminance(Color.FromRgb(0x80, 0x80, 0x80)), 0.2, 0.25);
    }

    [AvaloniaFact]
    public void LightTint_FlipsPageTextDark()
    {
        var vm = MakeVm();
        vm.ApplyTint(Color.FromRgb(0xF6, 0xD5, 0xE0)); // Lover-pink
        Assert.True(vm.IsLightTint);
        Assert.NotNull(vm.BackgroundBrush);
        Assert.Equal(Color.FromRgb(0x11, 0x11, 0x11), ((SolidColorBrush)vm.PageForegroundBrush).Color);
    }

    [AvaloniaFact]
    public void DarkTint_KeepsPageTextWhite()
    {
        var vm = MakeVm();
        vm.ApplyTint(Color.FromRgb(0x2A, 0x1B, 0x14)); // Take Care-brown
        Assert.False(vm.IsLightTint);
        Assert.NotNull(vm.BackgroundBrush);
        Assert.Same(Brushes.White, vm.PageForegroundBrush);
    }

    [AvaloniaFact]
    public void NoTint_ResetsToThemeDefaults()
    {
        var app = Avalonia.Application.Current!;
        var previous = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Dark;
            var vm = MakeVm();
            vm.ApplyTint(Color.FromRgb(0xF6, 0xD5, 0xE0));
            vm.ApplyTint(null);
            Assert.Null(vm.BackgroundBrush);
            Assert.False(vm.IsLightTint);
            Assert.Same(Brushes.White, vm.PageForegroundBrush);
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }

    /// <summary>Light theme, tint off: the page text was hard-coded white on a white
    /// surface (album title, facts, description and every track row invisible, 09-17).</summary>
    [AvaloniaFact]
    public void NoTint_OnLightTheme_UsesDarkPageText()
    {
        var app = Avalonia.Application.Current!;
        var previous = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            var vm = MakeVm();
            vm.ApplyTint(null);
            Assert.Null(vm.BackgroundBrush);
            Assert.False(vm.IsLightTint);
            Assert.Equal(Color.FromRgb(0x11, 0x11, 0x11), ((SolidColorBrush)vm.PageForegroundBrush).Color);
            Assert.Equal(Color.FromArgb(0x66, 0x00, 0x00, 0x00), ((SolidColorBrush)vm.PageSubtleForegroundBrush).Color);

            // A dark cover tint still wins over the theme.
            vm.ApplyTint(Color.FromRgb(0x2A, 0x1B, 0x14));
            Assert.Same(Brushes.White, vm.PageForegroundBrush);
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }

    [AvaloniaFact]
    public void TintBrush_IsTheFlatCoverColour()
    {
        var c = Color.FromRgb(0x40, 0x80, 0xC0);
        var brush = AlbumDetailViewModel.BuildTintBrush(c);
        Assert.Equal(c, brush.Color); // iTunes-style flat panel, no gradient
    }

    /// <summary>Real library covers are mostly 1500–3000px PNGs saved under a .jpg name
    /// (whatever the tagger/API handed us). PNG cannot decode subsampled, so asking the codec
    /// for a quarter-size decode returned null and the page silently never tinted (every
    /// cover in a 400-file sample of a real library came back null, 2026-09-07).</summary>
    [Theory]
    [InlineData(SKEncodedImageFormat.Png, 1500)]
    [InlineData(SKEncodedImageFormat.Png, 3000)]
    [InlineData(SKEncodedImageFormat.Jpeg, 1425)] // odd size: codec rounds its scaled dims
    public void EdgeExtractor_FullSizeCovers_StillTint(SKEncodedImageFormat format, int size)
    {
        using var bmp = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(0xF2, 0xC1, 0xD1));
            using var paint = new SKPaint { Color = new SKColor(0x10, 0x20, 0x30) };
            canvas.DrawRect(new SKRect(size / 6f, size / 6f, size * 5 / 6f, size * 5 / 6f), paint);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(format, 90);
        var path = Path.Combine(Path.GetTempPath(), $"noctis-tint-{Guid.NewGuid():N}.jpg");
        using (var fs = File.Create(path)) data.SaveTo(fs);
        try
        {
            var color = DominantColorExtractor.ExtractEdgeBackgroundColorFromFile(path);
            Assert.NotNull(color);
            Assert.InRange(color!.Value.R, 0xE0, 0xFF);
            Assert.InRange(color.Value.G, 0xB0, 0xD0);
            Assert.InRange(color.Value.B, 0xC0, 0xE0);
        }
        finally { File.Delete(path); }
    }

    // ── Edge picker (09-22 accuracy rework) ──

    private static byte[] Solid(int size, byte r, byte g, byte b)
    {
        var rgb = new byte[size * size * 3];
        for (int i = 0; i < size * size; i++) { rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b; }
        return rgb;
    }

    private static void Paint(byte[] rgb, int size, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            var i = (y * size + x) * 3;
            rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = b;
        }
    }

    /// <summary>A mostly black-framed cover with one coloured strip touching an edge: the
    /// old picker ALWAYS skipped near-black and tinted the page with the strip.</summary>
    [Fact]
    public void EdgePicker_DominantBlackBorder_StaysBlack()
    {
        var rgb = Solid(64, 0x05, 0x05, 0x05);
        Paint(rgb, 64, 0, 0, 64, 2, 0xE0, 0x20, 0x20); // thin red strip along the top edge
        var c = DominantColorExtractor.PickEdgeBackgroundColor(rgb, 64, 64)!.Value;
        Assert.True(c.R < 0x20 && c.G < 0x20 && c.B < 0x20, $"got {c}");
    }

    /// <summary>A noisy / gradient edge must vote as one colour and beat a smaller flat patch;
    /// in 4-bit RGB buckets its votes split and the flat patch won.</summary>
    [Fact]
    public void EdgePicker_NoisyEdge_BeatsSmallerFlatPatch()
    {
        var rgb = Solid(64, 0x20, 0x40, 0xA0);
        var rnd = new Random(7);
        for (int i = 0; i < 64 * 64; i++)
        {
            var j = rnd.Next(-20, 21);
            rgb[i * 3] = (byte)(0x20 + j / 2); rgb[i * 3 + 1] = (byte)(0x40 + j); rgb[i * 3 + 2] = (byte)(0xA0 + j);
        }
        Paint(rgb, 64, 0, 0, 64, 5, 0x30, 0xC0, 0x40); // flat green band along the top
        var c = DominantColorExtractor.PickEdgeBackgroundColor(rgb, 64, 64)!.Value;
        Assert.True(c.B > c.G && c.B > c.R, $"expected blue, got {c}");
    }

    /// <summary>Only the edge band votes: a cover whose centre is a different colour keeps
    /// the border colour.</summary>
    [Fact]
    public void EdgePicker_IgnoresTheCentre()
    {
        var rgb = Solid(64, 0x10, 0x60, 0x30);
        Paint(rgb, 64, 8, 8, 56, 56, 0xF0, 0xF0, 0x10);
        var c = DominantColorExtractor.PickEdgeBackgroundColor(rgb, 64, 64)!.Value;
        Assert.True(c.G > c.R && c.G > c.B, $"expected green, got {c}");
    }

    /// <summary>Neon covers keep their hue but not full-page highlighter chroma.</summary>
    [Fact]
    public void EdgePicker_CapsNeonChroma_KeepsHue()
    {
        var rgb = Solid(64, 0xFF, 0x00, 0xFF);
        var c = DominantColorExtractor.PickEdgeBackgroundColor(rgb, 64, 64)!.Value;
        var (_, a, b) = DominantColorExtractor.ToOkLab(c.R, c.G, c.B);
        Assert.InRange(Math.Sqrt(a * a + b * b), 0.10, DominantColorExtractor.EdgeMaxChroma + 0.01);
        Assert.True(c.R > c.G && c.B > c.G, $"expected magenta, got {c}");
    }

    /// <summary>Calm colours pass through untouched (within rounding).</summary>
    [Fact]
    public void EdgePicker_CalmColour_PassesThrough()
    {
        var rgb = Solid(64, 0x2A, 0x1B, 0x14);
        var c = DominantColorExtractor.PickEdgeBackgroundColor(rgb, 64, 64)!.Value;
        Assert.InRange(c.R, 0x28, 0x2C);
        Assert.InRange(c.G, 0x19, 0x1D);
        Assert.InRange(c.B, 0x12, 0x16);
    }

    [Fact]
    public void Setting_DefaultsOff()
        => Assert.False(new AppSettings().AlbumPageTintEnabled);
}
