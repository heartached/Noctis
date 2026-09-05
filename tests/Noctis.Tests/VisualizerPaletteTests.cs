using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Noctis.Helpers;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The visualizer's artwork colour: dark and grey covers must not produce invisible bars,
/// tips are lighter than the base, and Mirror mirrors the gradient about the centre.
/// </summary>
public class VisualizerPaletteTests
{
    [Fact]
    public void NoArtwork_ReturnsNull_SoCallerUsesFallback()
        => Assert.Null(VisualizerPalette.Build(null, VisualizerStyle.Bars));

    [Theory]
    [InlineData(0x20, 0x20, 0x20)] // near-black
    [InlineData(0x80, 0x80, 0x80)] // mid grey
    [InlineData(0xF0, 0xF0, 0xF0)] // near-white
    [InlineData(0x60, 0x62, 0x66)] // faintly tinted grey
    public void GreyCover_ReturnsNull(byte r, byte g, byte b)
        => Assert.Null(VisualizerPalette.Build(Color.FromRgb(r, g, b), VisualizerStyle.Bars));

    [AvaloniaFact] // builds a LinearGradientBrush (AvaloniaObject): UI thread only once a headless app exists
    public void DarkColourfulCover_IsLiftedToTheLightnessFloor()
    {
        // Deep navy: saturated but very dark — on the scrim it would disappear.
        var brush = VisualizerPalette.Build(Color.FromRgb(0x08, 0x10, 0x40), VisualizerStyle.Bars);
        Assert.NotNull(brush);
        var baseColor = brush!.GradientStops[0].Color;
        var (h, _, l) = VisualizerPalette.RgbToHsl(baseColor);
        Assert.InRange(l, VisualizerPalette.BaseLightnessFloor - 0.02, VisualizerPalette.BaseLightnessCeiling + 0.02);
        Assert.InRange(h, 215, 245); // still blue
    }

    [AvaloniaFact] // builds a LinearGradientBrush (AvaloniaObject): UI thread only once a headless app exists
    public void Tips_AreLighterThanBase()
    {
        var brush = VisualizerPalette.Build(Color.FromRgb(0xE7, 0x48, 0x56), VisualizerStyle.Bars)!;
        var (_, _, baseL) = VisualizerPalette.RgbToHsl(brush.GradientStops[0].Color);
        var (_, _, tipL) = VisualizerPalette.RgbToHsl(brush.GradientStops[1].Color);
        Assert.True(tipL > baseL + 0.1);
        // Bars grow upward: gradient runs bottom (base) → top (tip).
        Assert.Equal(1, brush.StartPoint.Point.Y);
        Assert.Equal(0, brush.EndPoint.Point.Y);
    }

    [AvaloniaFact] // builds a LinearGradientBrush (AvaloniaObject): UI thread only once a headless app exists
    public void Mirror_IsSymmetricAboutTheCentre()
    {
        var brush = VisualizerPalette.Build(Color.FromRgb(0x20, 0xA0, 0x60), VisualizerStyle.Mirror)!;
        Assert.Equal(3, brush.GradientStops.Count);
        Assert.Equal(brush.GradientStops[0].Color, brush.GradientStops[2].Color);
        Assert.Equal(0.5, brush.GradientStops[1].Offset);
    }

    [Fact]
    public void Hsl_RoundTrips()
    {
        var c = Color.FromRgb(0x12, 0x9A, 0xC3);
        var (h, s, l) = VisualizerPalette.RgbToHsl(c);
        var back = VisualizerPalette.HslToColor(h, s, l);
        Assert.InRange(back.R, c.R - 1, c.R + 1);
        Assert.InRange(back.G, c.G - 1, c.G + 1);
        Assert.InRange(back.B, c.B - 1, c.B + 1);
    }
}
