using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media;
using Noctis.Mobile.ViewModels;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>PageTint for the album hero: the colour comes from the cover's bottom rows, and the
/// page text is black or white by WCAG contrast with secondary text kept at 4.5:1.</summary>
public class PageTintContrastTests : IDisposable
{
    private readonly List<string> _files = new();
    public void Dispose() { foreach (var f in _files) try { File.Delete(f); } catch { } }

    /// <summary>Black or white, whichever reads better, clears 4.5:1 on every grey and on a
    /// sweep of hues; secondary text never drops under 4.5:1 (or is the primary colour).</summary>
    [Fact]
    public void TextOnAnyTint_ClearsWcagAA()
    {
        var samples = new List<Color>();
        for (var v = 0; v <= 255; v += 5) samples.Add(Color.FromRgb((byte)v, (byte)v, (byte)v));
        var rnd = new Random(7);
        for (var i = 0; i < 400; i++) samples.Add(Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));

        foreach (var bg in samples)
        {
            var text = PageTint.PrefersDarkText(bg) ? Colors.Black : Colors.White;
            Assert.True(PageTint.ContrastRatio(text, bg) >= 4.5, $"{text} on {bg}");
            var subtle = PageTint.SubtleTextOn(bg, text);
            Assert.True(PageTint.ContrastRatio(subtle, bg) >= 4.5 || subtle == text, $"subtle {subtle} on {bg}");
        }
    }

    /// <summary>On a dark tint there is contrast to spare: secondary text dims to the 60% floor.</summary>
    [Fact]
    public void SubtleText_DimsWhereTheTintAllows()
    {
        var bg = Color.FromRgb(0x20, 0x18, 0x14);
        var subtle = PageTint.SubtleTextOn(bg, Colors.White);
        Assert.Equal(PageTint.Mix(bg, Colors.White, PageTint.SubtleTextMinOpacity), subtle);
    }

    private string WriteSkyOverSand()
    {
        using var bmp = new SKBitmap(200, 200);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(0x8C, 0xB8, 0xE8));
            using var paint = new SKPaint { Color = new SKColor(0xC8, 0xA4, 0x78) };
            canvas.DrawRect(new SKRect(0, 150, 200, 200), paint);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(Path.GetTempPath(), $"noctis-sky-sand-{Guid.NewGuid():N}.png");
        using (var fs = File.Create(path)) data.SaveTo(fs);
        _files.Add(path);
        return path;
    }

    /// <summary>The album page's default extractor reads the cover's bottom (sand); other
    /// pages' PageTint keeps the four-edge colour (sky wins three of the four edges).</summary>
    [Fact]
    public async Task HeroFadeTint_ReadsTheBottom_OtherPagesKeepTheEdges()
    {
        var path = WriteSkyOverSand();
        var hero = new PageTint(runBackground: work => Task.FromResult(work())) { HeroFade = true };
        hero.Load(path);
        await hero.Ready;
        var edges = new PageTint(runBackground: work => Task.FromResult(work()));
        edges.Load(path);
        await edges.Ready;

        var sand = hero.TintColor!.Value;
        Assert.InRange(sand.R, 0xC0, 0xD0);
        Assert.InRange(sand.B, 0x70, 0x80);
        Assert.True(edges.TintColor!.Value.B > edges.TintColor.Value.R, $"edge colour {edges.TintColor}");
    }
}
