using System.Collections.Generic;
using Avalonia.Media;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ThemeDerivationTests
{
    [Theory]
    [InlineData("#000000", 0.0)]
    [InlineData("#FFFFFF", 1.0)]
    [InlineData("#808080", 0.21586, 0.001)]
    public void RelativeLuminance_MatchesWcag(string hex, double expected, double tolerance = 0.0001)
    {
        var color = Color.Parse(hex);
        var actual = ThemeDerivation.RelativeLuminance(color);
        Assert.InRange(actual, expected - tolerance, expected + tolerance);
    }

    [Fact]
    public void Contrast_BlackOnWhite_Is21()
    {
        var c = ThemeDerivation.ContrastRatio(Color.Parse("#000000"), Color.Parse("#FFFFFF"));
        Assert.InRange(c, 20.9, 21.1);
    }

    [Fact]
    public void Contrast_Symmetric()
    {
        var a = ThemeDerivation.ContrastRatio(Color.Parse("#101010"), Color.Parse("#E0E0E0"));
        var b = ThemeDerivation.ContrastRatio(Color.Parse("#E0E0E0"), Color.Parse("#101010"));
        Assert.Equal(a, b, 4);
    }

    [Theory]
    [InlineData("#000000", 4.5)]
    [InlineData("#1E1E1E", 4.5)]
    [InlineData("#0E1322", 4.5)]
    [InlineData("#F5F5F5", 4.5)]
    [InlineData("#888888", 4.5)]
    public void PickReadableText_MeetsTarget(string bgHex, double minRatio)
    {
        var bg = Color.Parse(bgHex);
        var fg = ThemeDerivation.PickReadableText(bg, minRatio);
        Assert.True(
            ThemeDerivation.ContrastRatio(bg, fg) >= minRatio,
            $"got ratio {ThemeDerivation.ContrastRatio(bg, fg)} for bg {bgHex}");
    }

    [Fact]
    public void PickReadableText_PrefersWhiteOnDark_BlackOnLight()
    {
        var onDark = ThemeDerivation.PickReadableText(Color.Parse("#101010"), 4.5);
        var onLight = ThemeDerivation.PickReadableText(Color.Parse("#F5F5F5"), 4.5);
        Assert.True(onDark.R > 200);
        Assert.True(onLight.R < 80);
    }

    private static readonly string[] RequiredKeys =
    {
        "SystemAccentColor", "AccentColorBrush",
        "AppSidebarBackground", "AppMainBackground", "AppMainBackgroundColor",
        "TrackListStripeBrush", "TrackListHoverBrush",
        "SystemChromeMediumColor", "SystemChromeMediumLowColor", "SystemChromeLowColor",
        "SystemChromeHighColor", "SystemAltHighColor", "SystemAltMediumColor",
        "SystemAltMediumHighColor", "SystemAltMediumLowColor", "SystemAltLowColor",
        "SystemRegionColor", "RegionColor",
        "SystemControlBackgroundAltHighBrush", "SystemControlBackgroundAltMediumBrush",
        "SystemControlBackgroundAltMediumHighBrush", "SystemControlBackgroundAltMediumLowBrush",
        "SystemControlBackgroundBaseLowBrush", "SystemControlBackgroundBaseMediumBrush",
        "SystemControlBackgroundChromeMediumBrush", "SystemControlBackgroundChromeMediumLowBrush",
        "SystemControlBackgroundChromeWhiteBrush",
        "SystemControlPageBackgroundAltHighBrush", "SystemControlPageBackgroundBaseLowBrush",
        "SystemControlPageBackgroundBaseMediumBrush", "SystemControlPageBackgroundChromeLowBrush",
        "SystemControlPageBackgroundChromeMediumLowBrush",
        "SystemChromeMediumBrush", "SystemChromeMediumLowBrush", "SystemChromeLowBrush",
        "SystemChromeHighBrush", "SystemAltHighBrush", "SystemAltMediumBrush",
        "SystemAltMediumHighBrush", "SystemAltMediumLowBrush", "SystemAltLowBrush",
        "HomeCardBackground", "HomeCardHoverBackground",
        "IslandBackground", "IslandForeground", "IslandForegroundSecondary",
        "IslandForegroundTertiary", "IslandIconFill", "IslandSliderFilled",
        "IslandSliderUnfilled", "IslandAlbumArtPlaceholder", "IslandExplicitBadge",
        "IslandIconAccent",
        "SidebarSelectedBrush", "SidebarSelectedHoverBrush", "SidebarHoverBrush",
        "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver",
        "ToggleSwitchFillOnPressed", "ToggleSwitchFillOnDragging",
        "SystemControlHighlightAccentBrush", "SystemControlHighlightAccentBrush2",
    };

    [Fact]
    public void Derive_EmitsAllRequiredKeys_DarkBase()
    {
        var def = new Noctis.Models.CustomThemeDefinition
        {
            BaseMode = "Dark",
            MainBackgroundHex = "#101820",
            SidebarBackgroundHex = "#18222C",
            AccentHex = "#4C6EF5",
        };
        var dict = ThemeDerivation.Derive(def);
        foreach (var key in RequiredKeys)
            Assert.True(dict.ContainsKey(key), $"missing key: {key}");
    }

    [Fact]
    public void Derive_EmitsAllRequiredKeys_LightBase()
    {
        var def = new Noctis.Models.CustomThemeDefinition
        {
            BaseMode = "Light",
            MainBackgroundHex = "#FAFAFA",
            SidebarBackgroundHex = "#EFEFEF",
            AccentHex = "#E74856",
        };
        var dict = ThemeDerivation.Derive(def);
        foreach (var key in RequiredKeys)
            Assert.True(dict.ContainsKey(key), $"missing key: {key}");
    }

    public static IEnumerable<object[]> ReadabilityPalettes()
    {
        yield return new object[] { "Dark", "#000000", "#0A0A0A", "#FF0000" };       // very dark
        yield return new object[] { "Light", "#FFFFFF", "#F5F5F5", "#1A73E8" };      // very light
        yield return new object[] { "Dark", "#444444", "#3A3A3A", "#888888" };       // low-contrast mid
        yield return new object[] { "Dark", "#0E1322", "#1A2238", "#7C8CFF" };       // midnight
        yield return new object[] { "Light", "#FAF1E4", "#F0E5D2", "#B5651D" };      // pastel
        yield return new object[] { "Dark", "#202020", "#181818", "#A0A0A0" };       // monochrome
    }

    [Theory]
    [MemberData(nameof(ReadabilityPalettes))]
    public void Derive_PrimaryTextContrastIsReadable(string mode, string mainBg, string sidebarBg, string accent)
    {
        var dict = ThemeDerivation.Derive(new Noctis.Models.CustomThemeDefinition
        {
            BaseMode = mode,
            MainBackgroundHex = mainBg,
            SidebarBackgroundHex = sidebarBg,
            AccentHex = accent,
        });

        var primary = ((SolidColorBrush)dict["PrimaryTextBrush"]).Color;
        var bg = Color.Parse(mainBg);
        Assert.True(
            ThemeDerivation.ContrastRatio(bg, primary) >= 4.5,
            $"primary text contrast too low for bg {mainBg}");

        var islandBg = ((SolidColorBrush)dict["IslandBackground"]).Color;
        var islandFg = ((SolidColorBrush)dict["IslandForeground"]).Color;
        var islandBgOpaque = Color.FromRgb(islandBg.R, islandBg.G, islandBg.B);
        Assert.True(
            ThemeDerivation.ContrastRatio(islandBgOpaque, islandFg) >= 4.5,
            $"island foreground contrast too low for {mainBg}");
    }
}
