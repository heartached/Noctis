# Custom Themes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users create, save, edit, and delete named custom themes in Settings → Themes, with derived palettes that guarantee readable text on every page.

**Architecture:** A `CustomThemeDefinition` (Name, BaseMode, MainBg, SidebarBg, Accent) is persisted in `AppSettings`. A pure `ThemeDerivation.Derive(def)` function expands those 5 inputs into the same ~50-key `ResourceDictionary` that today's `Dark.axaml` / `Midnight.axaml` overlays define, with WCAG contrast-clamped text colors. `App.SetTheme("Custom:<id>")` merges the derived dictionary at runtime, then re-applies the accent (same flow as built-in themes). A new editor dialog edits one theme with a live preview surface.

**Tech Stack:** Avalonia 11, C# 12, CommunityToolkit.Mvvm. Tests live in `tests/Noctis.Tests/` using xUnit.

**Spec:** [docs/superpowers/specs/2026-05-17-custom-themes-design.md](../specs/2026-05-17-custom-themes-design.md)

---

## File Structure

**New files:**
- `src/Noctis/Models/CustomThemeDefinition.cs` — DTO persisted in `AppSettings`.
- `src/Noctis/Services/ThemeDerivation.cs` — pure derivation: 5 inputs → ~50-key `ResourceDictionary`. Holds WCAG contrast helpers.
- `src/Noctis/ViewModels/ThemeEditorViewModel.cs` — VM for the editor dialog (name/base/colors/preview brushes).
- `src/Noctis/Views/ThemeEditorDialog.axaml` + `.axaml.cs` — modal editor with live preview.
- `tests/Noctis.Tests/ThemeDerivationTests.cs` — contrast + key-coverage tests.
- `tests/Noctis.Tests/CustomThemePersistenceTests.cs` — round-trip + missing-id fallback.

**Modified files:**
- `src/Noctis/Models/AppSettings.cs` — add `CustomThemes` list.
- `src/Noctis/App.axaml.cs` — extend `SetTheme` to handle `"Custom:<id>"`.
- `src/Noctis/ViewModels/SettingsViewModel.cs` — custom-theme tiles, commands, persistence.
- `src/Noctis/Views/SettingsView.axaml` — themes row becomes `ItemsControl` + add tile + context menu.

---

## Task 1: `CustomThemeDefinition` model + `AppSettings` field

**Files:**
- Create: `src/Noctis/Models/CustomThemeDefinition.cs`
- Modify: `src/Noctis/Models/AppSettings.cs`
- Test: `tests/Noctis.Tests/CustomThemePersistenceTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Noctis.Tests/CustomThemePersistenceTests.cs`:

```csharp
using System.Text.Json;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

public class CustomThemePersistenceTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var settings = new AppSettings
        {
            Theme = "Custom:abc123",
            CustomThemes = new List<CustomThemeDefinition>
            {
                new()
                {
                    Id = "abc123",
                    Name = "My Sunset",
                    BaseMode = "Dark",
                    MainBackgroundHex = "#1A0E14",
                    SidebarBackgroundHex = "#221319",
                    AccentHex = "#FF8547"
                }
            }
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal("Custom:abc123", restored.Theme);
        Assert.Single(restored.CustomThemes);
        var t = restored.CustomThemes[0];
        Assert.Equal("abc123", t.Id);
        Assert.Equal("My Sunset", t.Name);
        Assert.Equal("Dark", t.BaseMode);
        Assert.Equal("#1A0E14", t.MainBackgroundHex);
        Assert.Equal("#221319", t.SidebarBackgroundHex);
        Assert.Equal("#FF8547", t.AccentHex);
    }

    [Fact]
    public void EmptySettings_HasNoCustomThemes()
    {
        var settings = new AppSettings();
        Assert.NotNull(settings.CustomThemes);
        Assert.Empty(settings.CustomThemes);
    }
}
```

- [ ] **Step 2: Run test, expect compile failure**

```
dotnet test tests/Noctis.Tests/Noctis.Tests.csproj --filter "FullyQualifiedName~CustomThemePersistenceTests" -v minimal
```

Expected: build error — `CustomThemeDefinition` does not exist, `AppSettings.CustomThemes` does not exist.

- [ ] **Step 3: Create `CustomThemeDefinition.cs`**

```csharp
namespace Noctis.Models;

public sealed class CustomThemeDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string BaseMode { get; set; } = "Dark";
    public string MainBackgroundHex { get; set; } = "#121212";
    public string SidebarBackgroundHex { get; set; } = "#1C1C1C";
    public string AccentHex { get; set; } = "#E74856";
}
```

- [ ] **Step 4: Add `CustomThemes` to `AppSettings`**

In `src/Noctis/Models/AppSettings.cs`, immediately after the `AccentColorHex` property (around line 44), add:

```csharp
/// <summary>User-defined custom themes selectable from the Themes row.</summary>
public List<CustomThemeDefinition> CustomThemes { get; set; } = new();
```

- [ ] **Step 5: Run tests, expect pass**

```
dotnet test tests/Noctis.Tests/Noctis.Tests.csproj --filter "FullyQualifiedName~CustomThemePersistenceTests" -v minimal
```

Expected: both tests pass.

- [ ] **Step 6: Commit**

```
git add src/Noctis/Models/CustomThemeDefinition.cs src/Noctis/Models/AppSettings.cs tests/Noctis.Tests/CustomThemePersistenceTests.cs
git commit -m "feat(themes): add CustomThemeDefinition model and AppSettings field"
```

---

## Task 2: `ThemeDerivation` — color math helpers

**Files:**
- Create: `src/Noctis/Services/ThemeDerivation.cs` (helpers only this task)
- Test: `tests/Noctis.Tests/ThemeDerivationTests.cs`

Background: the derivation engine needs three primitives — parse hex, compute WCAG relative luminance, and compute contrast ratio. We TDD those first.

- [ ] **Step 1: Write the failing test**

Create `tests/Noctis.Tests/ThemeDerivationTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run test, expect failure**

```
dotnet test tests/Noctis.Tests/Noctis.Tests.csproj --filter "FullyQualifiedName~ThemeDerivationTests" -v minimal
```

Expected: build error — `ThemeDerivation` does not exist.

- [ ] **Step 3: Implement the helpers**

Create `src/Noctis/Services/ThemeDerivation.cs`:

```csharp
using Avalonia.Media;

namespace Noctis.Services;

public static class ThemeDerivation
{
    public static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var lighter = Math.Max(la, lb);
        var darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    internal static Color ParseHex(string hex, Color fallback)
    {
        try { return Color.Parse(hex.Trim()); }
        catch { return fallback; }
    }
}
```

- [ ] **Step 4: Run test, expect pass**

```
dotnet test tests/Noctis.Tests/Noctis.Tests.csproj --filter "FullyQualifiedName~ThemeDerivationTests" -v minimal
```

Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```
git add src/Noctis/Services/ThemeDerivation.cs tests/Noctis.Tests/ThemeDerivationTests.cs
git commit -m "feat(themes): add WCAG luminance/contrast helpers"
```

---

## Task 3: `ThemeDerivation` — contrast-clamped text color picker

The "every theme is readable" guarantee lives here. Given a background, pick a foreground at the right end of the white↔black axis and clamp until contrast ≥ target.

- [ ] **Step 1: Add failing tests** (append to `ThemeDerivationTests.cs`)

```csharp
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
```

- [ ] **Step 2: Run, expect failure** (`PickReadableText` undefined)

```
dotnet test tests/Noctis.Tests/Noctis.Tests.csproj --filter "FullyQualifiedName~ThemeDerivationTests" -v minimal
```

- [ ] **Step 3: Implement `PickReadableText`** (add to `ThemeDerivation.cs`)

```csharp
    /// <summary>
    /// Returns a foreground color that meets the WCAG contrast target against <paramref name="bg"/>.
    /// Picks the white-or-black pole that already has more contrast, then darkens/lightens until
    /// the target is met. Always converges (black-on-white = 21:1 covers any target ≤ 21).
    /// </summary>
    public static Color PickReadableText(Color bg, double minRatio)
    {
        var startWhite = ContrastRatio(bg, Colors.White) >= ContrastRatio(bg, Colors.Black);
        var fg = startWhite ? Colors.White : Colors.Black;

        if (ContrastRatio(bg, fg) >= minRatio) return fg;

        // Move 16 steps toward the opposite pole; one of them must satisfy.
        for (var i = 1; i <= 16; i++)
        {
            var t = i / 16.0;
            var candidate = startWhite
                ? Mix(Colors.White, Colors.Black, t)
                : Mix(Colors.Black, Colors.White, t);
            if (ContrastRatio(bg, candidate) >= minRatio) return candidate;
        }
        return startWhite ? Colors.White : Colors.Black;
    }

    internal static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte r = (byte)(a.R + (b.R - a.R) * t);
        byte g = (byte)(a.G + (b.G - a.G) * t);
        byte bl = (byte)(a.B + (b.B - a.B) * t);
        return Color.FromRgb(r, g, bl);
    }

    internal static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
```

- [ ] **Step 4: Run tests, expect pass**

Expected: previous 3 + new 6 (5 theory + 1 fact) = 9 tests pass.

- [ ] **Step 5: Commit**

```
git add src/Noctis/Services/ThemeDerivation.cs tests/Noctis.Tests/ThemeDerivationTests.cs
git commit -m "feat(themes): add contrast-clamped text-color picker"
```

---

## Task 4: `ThemeDerivation.Derive` — full dictionary

Produces every key the built-in overlays produce. Authoritative key list comes from `src/Noctis/Assets/Themes/Dark.axaml` (94 lines) and `Midnight.axaml` (91 lines).

- [ ] **Step 1: Add failing tests** (append to `ThemeDerivationTests.cs`)

```csharp
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
```

- [ ] **Step 2: Run, expect failure**

```
dotnet test tests/Noctis.Tests/Noctis.Tests.csproj --filter "FullyQualifiedName~ThemeDerivationTests" -v minimal
```

Expected: build error — `Derive` does not exist.

- [ ] **Step 3: Implement `Derive`** (add to `ThemeDerivation.cs`)

```csharp
    /// <summary>
    /// Expands a CustomThemeDefinition into the full ResourceDictionary the app expects.
    /// Caller merges this onto Application.Resources; App.SetAccent runs after to overlay
    /// the active accent palette (Dark1/2/3, Light1/2/3, etc.).
    /// </summary>
    public static IDictionary<string, object> Derive(Noctis.Models.CustomThemeDefinition def)
    {
        var isDark = !string.Equals(def.BaseMode, "Light", StringComparison.OrdinalIgnoreCase);
        var main = ParseHex(def.MainBackgroundHex, isDark ? Color.Parse("#121212") : Color.Parse("#F5F5F5"));
        var sidebar = ParseHex(def.SidebarBackgroundHex, isDark ? Color.Parse("#1C1C1C") : Color.Parse("#EAEAEA"));
        var accent = ParseHex(def.AccentHex, Color.Parse("#E74856"));

        // Foregrounds clamped against the main background.
        var primary    = PickReadableText(main, 4.5);
        var secondary  = PickReadableText(main, 3.0);
        var tertiary   = PickReadableText(main, 2.0);

        // Opaque island base = main bg; we encode the 0xF0 alpha later.
        var islandBase = main;
        var islandFg          = PickReadableText(islandBase, 4.5);
        var islandFgSecondary = PickReadableText(islandBase, 3.0);
        var islandFgTertiary  = PickReadableText(islandBase, 2.0);

        // Surface ramp — alpha overlays of the foreground polarity, matching Dark.axaml steps.
        var overlayColor = isDark ? Colors.White : Colors.Black;
        var stripe        = WithAlpha(overlayColor, 0x08);
        var hover         = WithAlpha(overlayColor, 0x14);
        var homePill      = WithAlpha(overlayColor, 0x14);
        var homePillHover = WithAlpha(overlayColor, 0x26);
        var sidebarHover  = WithAlpha(overlayColor, 0x14);

        // Sidebar selected = lift the sidebar background a notch toward foreground polarity.
        var sidebarSelected     = Mix(sidebar, overlayColor, 0.08);
        var sidebarSelectedHov  = Mix(sidebar, overlayColor, 0.14);

        var dict = new Dictionary<string, object>
        {
            // ── Sentinel read by App.SetTheme ──
            ["__BaseVariant"] = isDark ? "Dark" : "Light",

            // ── Accent (a stub; App.SetAccent overlays the real palette right after) ──
            ["SystemAccentColor"] = accent,
            ["AccentColorBrush"]  = new SolidColorBrush(accent),
            ["SystemControlHighlightAccentBrush"]  = new SolidColorBrush(accent),
            ["SystemControlHighlightAccentBrush2"] = new SolidColorBrush(accent),
            ["ToggleSwitchFillOn"]            = new SolidColorBrush(accent),
            ["ToggleSwitchFillOnPointerOver"] = new SolidColorBrush(accent),
            ["ToggleSwitchFillOnPressed"]     = new SolidColorBrush(accent),
            ["ToggleSwitchFillOnDragging"]    = new SolidColorBrush(accent),

            // ── App backgrounds ──
            ["AppSidebarBackground"]   = new SolidColorBrush(sidebar),
            ["AppMainBackground"]      = new SolidColorBrush(main),
            ["AppMainBackgroundColor"] = main,
            ["TrackListStripeBrush"]   = new SolidColorBrush(stripe),
            ["TrackListHoverBrush"]    = new SolidColorBrush(hover),

            // ── Primary/secondary/tertiary text (extra keys used by views; safe additions) ──
            ["PrimaryTextBrush"]   = new SolidColorBrush(primary),
            ["SecondaryTextBrush"] = new SolidColorBrush(secondary),
            ["TertiaryTextBrush"]  = new SolidColorBrush(tertiary),

            // ── Home cards ──
            ["HomeCardBackground"]      = new SolidColorBrush(homePill),
            ["HomeCardHoverBackground"] = new SolidColorBrush(homePillHover),

            // ── Sidebar highlight ──
            ["SidebarSelectedBrush"]      = new SolidColorBrush(sidebarSelected),
            ["SidebarSelectedHoverBrush"] = new SolidColorBrush(sidebarSelectedHov),
            ["SidebarHoverBrush"]         = new SolidColorBrush(sidebarHover),

            // ── Playback Island ──
            ["IslandBackground"]           = new SolidColorBrush(WithAlpha(islandBase, 0xF0)),
            ["IslandForeground"]           = new SolidColorBrush(islandFg),
            ["IslandForegroundSecondary"]  = new SolidColorBrush(islandFgSecondary),
            ["IslandForegroundTertiary"]   = new SolidColorBrush(islandFgTertiary),
            ["IslandIconFill"]             = new SolidColorBrush(islandFg),
            ["IslandSliderFilled"]         = new SolidColorBrush(islandFg),
            ["IslandSliderUnfilled"]       = new SolidColorBrush(Mix(islandBase, overlayColor, 0.22)),
            ["IslandAlbumArtPlaceholder"]  = new SolidColorBrush(WithAlpha(overlayColor, 0x33)),
            ["IslandExplicitBadge"]        = new SolidColorBrush(WithAlpha(overlayColor, 0x88)),
            ["IslandIconAccent"]           = new SolidColorBrush(accent),
        };

        // System chrome keys all resolve to the main background (matches the built-in pattern).
        string[] chromeColorKeys =
        {
            "SystemChromeMediumColor", "SystemChromeMediumLowColor", "SystemChromeLowColor",
            "SystemChromeHighColor", "SystemAltHighColor", "SystemAltMediumColor",
            "SystemAltMediumHighColor", "SystemAltMediumLowColor", "SystemAltLowColor",
            "SystemRegionColor", "RegionColor",
        };
        foreach (var k in chromeColorKeys) dict[k] = main;

        string[] chromeBrushKeys =
        {
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
        };
        // BaseLow gets the sidebar tint (matches Midnight pattern for popups/menus that
        // need to differ slightly from the main page).
        foreach (var k in chromeBrushKeys)
            dict[k] = new SolidColorBrush(k == "SystemControlBackgroundBaseLowBrush" ? sidebar : main);

        return dict;
    }
```

- [ ] **Step 4: Run tests, expect pass**

```
dotnet test tests/Noctis.Tests/Noctis.Tests.csproj --filter "FullyQualifiedName~ThemeDerivationTests" -v minimal
```

Expected: all ThemeDerivation tests pass (incl. 6 readability palettes × 2 assertions).

- [ ] **Step 5: Commit**

```
git add src/Noctis/Services/ThemeDerivation.cs tests/Noctis.Tests/ThemeDerivationTests.cs
git commit -m "feat(themes): derive full ResourceDictionary from 5-input definition"
```

---

## Task 5: Wire `App.SetTheme` to handle `Custom:<id>`

**Files:**
- Modify: `src/Noctis/App.axaml.cs:96-127`

A registration hook lets `SettingsViewModel` provide the custom-theme lookup (we keep `App` independent of `IPersistenceService`).

- [ ] **Step 1: Add the lookup hook + extend `SetTheme`**

In `src/Noctis/App.axaml.cs`, immediately above `public void SetTheme(string themeName)` (around line 95), add:

```csharp
    /// <summary>
    /// Callback the SettingsViewModel registers so App can resolve a Custom:<id> theme name
    /// to a concrete definition without taking a hard dependency on the settings service.
    /// </summary>
    public Func<string, Noctis.Models.CustomThemeDefinition?>? CustomThemeResolver { get; set; }
```

Then replace the body of `SetTheme` (lines 96-127) with:

```csharp
    public void SetTheme(string themeName)
    {
        if (_activeThemeOverlay != null)
        {
            Resources.MergedDictionaries.Remove(_activeThemeOverlay);
            _activeThemeOverlay = null;
        }
        if (_activeCustomOverlay != null)
        {
            Resources.MergedDictionaries.Remove(_activeCustomOverlay);
            _activeCustomOverlay = null;
        }

        // Custom themes
        if (themeName != null && themeName.StartsWith("Custom:", StringComparison.Ordinal))
        {
            var id = themeName.Substring("Custom:".Length);
            var def = CustomThemeResolver?.Invoke(id);
            if (def != null)
            {
                var derived = Noctis.Services.ThemeDerivation.Derive(def);

                RequestedThemeVariant =
                    derived.TryGetValue("__BaseVariant", out var v) && (string)v == "Light"
                        ? Avalonia.Styling.ThemeVariant.Light
                        : Avalonia.Styling.ThemeVariant.Dark;

                var rd = new Avalonia.Controls.ResourceDictionary();
                foreach (var (key, value) in derived)
                {
                    if (key.StartsWith("__")) continue;
                    rd[key] = value;
                }
                Resources.MergedDictionaries.Add(rd);
                _activeCustomOverlay = rd;

                // Custom themes drive their own accent — push it through the SetAccent
                // path so the accent palette (Dark1/2/3, Light1/2/3, MenuFlyout hovers,
                // etc.) is generated and overlaid the same way as for built-in themes.
                SetAccent(def.AccentHex);
                return;
            }
            // Unknown id falls through to default handling (Gray).
            themeName = ThemeGray;
        }

        RequestedThemeVariant = themeName == ThemeLight
            ? Avalonia.Styling.ThemeVariant.Light
            : Avalonia.Styling.ThemeVariant.Dark;

        var overlayUri = themeName switch
        {
            ThemeDark => "avares://Noctis/Assets/Themes/Dark.axaml",
            ThemeMidnight => "avares://Noctis/Assets/Themes/Midnight.axaml",
            _ => null
        };

        if (overlayUri != null)
        {
            var include = new ResourceInclude((Uri?)null) { Source = new Uri(overlayUri) };
            Resources.MergedDictionaries.Add(include);
            _activeThemeOverlay = include;
        }

        if (_activeAccentHex != null)
            SetAccent(_activeAccentHex);
    }
```

Add the new field next to `_activeThemeOverlay` (around line 88):

```csharp
    private Avalonia.Controls.ResourceDictionary? _activeCustomOverlay;
```

- [ ] **Step 2: Build the app**

```
dotnet build src/Noctis/Noctis.csproj -v minimal
```

Expected: build succeeds. If `using` for `Avalonia.Controls.ResourceDictionary` is missing, the fully-qualified name above already covers it.

- [ ] **Step 3: Commit**

```
git add src/Noctis/App.axaml.cs
git commit -m "feat(themes): route Custom:<id> through ThemeDerivation in SetTheme"
```

---

## Task 6: `SettingsViewModel` — custom-theme state, persistence, commands

**Files:**
- Modify: `src/Noctis/ViewModels/SettingsViewModel.cs`

This task threads the new state through the existing load/save flow. Editor-opening commands are stubbed; the actual dialog is wired in Task 8.

- [ ] **Step 1: Add the tile DTO + collection**

Near the bottom of `SettingsViewModel.cs` (just above the closing brace of the class), add:

```csharp
    public sealed partial class CustomThemeTile : ObservableObject
    {
        public string Id { get; init; } = "";
        [ObservableProperty] private string _name = "";
        [ObservableProperty] private string _accentHex = "#E74856";
        [ObservableProperty] private string _sidebarHex = "#1C1C1C";
        [ObservableProperty] private string _mainHex = "#121212";
        [ObservableProperty] private bool _isActive;
    }
```

Note: `CustomThemeTile` must be a *separate* type, not nested. Place it in a new file `src/Noctis/ViewModels/CustomThemeTile.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.ViewModels;

public sealed partial class CustomThemeTile : ObservableObject
{
    public string Id { get; init; } = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _accentHex = "#E74856";
    [ObservableProperty] private string _sidebarHex = "#1C1C1C";
    [ObservableProperty] private string _mainHex = "#121212";
    [ObservableProperty] private bool _isActive;
}
```

- [ ] **Step 2: Add observable collection + active-id property in `SettingsViewModel`**

In `SettingsViewModel.cs`, immediately after the existing `_isMidnightTheme` observable property (around line 61), add:

```csharp
    /// <summary>User-created themes shown in the Themes row alongside the built-ins.</summary>
    public ObservableCollection<CustomThemeTile> CustomThemes { get; } = new();

    [ObservableProperty] private string? _activeCustomThemeId;
```

- [ ] **Step 3: Extend load to hydrate `CustomThemes` and set active state**

Inside the load block where `storedTheme` is read (around line 359-374), after `SetActiveThemeFlags(storedTheme);` add:

```csharp
            // Hydrate user-created themes.
            CustomThemes.Clear();
            foreach (var def in _settings.CustomThemes)
                CustomThemes.Add(MapDefToTile(def));

            // If active theme is Custom:<id>, mark the matching tile and clear built-in flags.
            if (storedTheme.StartsWith("Custom:", StringComparison.Ordinal))
            {
                var id = storedTheme.Substring("Custom:".Length);
                ActiveCustomThemeId = id;
                foreach (var t in CustomThemes) t.IsActive = t.Id == id;
                if (!CustomThemes.Any(t => t.Id == id))
                {
                    // Stale reference — fall back to Gray and persist.
                    ActiveCustomThemeId = null;
                    SetActiveThemeFlags("Gray");
                    _settings.Theme = "Gray";
                }
                else
                {
                    SetActiveThemeFlags("__Custom"); // clears all five built-in flags
                }
            }
```

Add this private helper somewhere in the class (e.g. near `SetActiveThemeFlags`):

```csharp
    private static CustomThemeTile MapDefToTile(CustomThemeDefinition def) => new()
    {
        Id = def.Id,
        Name = def.Name,
        AccentHex = def.AccentHex,
        SidebarHex = def.SidebarBackgroundHex,
        MainHex = def.MainBackgroundHex,
    };
```

- [ ] **Step 4: Extend `SetActiveThemeFlags` to support `"__Custom"`**

Replace the body of `SetActiveThemeFlags` (around line 737-748) with:

```csharp
    private void SetActiveThemeFlags(string themeKey)
    {
        IsGrayTheme = themeKey == "Gray";
        IsDarkTheme = themeKey == "Dark";
        IsLightTheme = themeKey == "Light";
        IsSystemTheme = themeKey == "System";
        IsMidnightTheme = themeKey == "Midnight";

        if (themeKey == "__Custom") return; // custom-theme active: all built-in flags stay false

        // Default-safety: if no flag matched, fall back to Gray.
        if (!IsGrayTheme && !IsDarkTheme && !IsLightTheme && !IsSystemTheme && !IsMidnightTheme)
            IsGrayTheme = true;
    }
```

- [ ] **Step 5: Extend `ResolveActiveThemeKey` to emit `Custom:<id>`**

Replace the body of `ResolveActiveThemeKey` (around line 754-762) with:

```csharp
    private string ResolveActiveThemeKey()
    {
        if (!string.IsNullOrEmpty(ActiveCustomThemeId)) return "Custom:" + ActiveCustomThemeId;
        if (IsLightTheme) return "Light";
        if (IsDarkTheme) return "Dark";
        if (IsMidnightTheme) return "Midnight";
        if (IsSystemTheme) return IsSystemDarkMode() ? "Gray" : "Light";
        return "Gray";
    }
```

- [ ] **Step 6: Persist `CustomThemes` on save**

Find the save method that writes theme/accent (around line 513-522). Inside the block, after the `_settings.Theme = …` assignments and before `_settings.AccentColorHex = ActiveAccentHex;`, add:

```csharp
        if (!string.IsNullOrEmpty(ActiveCustomThemeId)) _settings.Theme = "Custom:" + ActiveCustomThemeId;

        _settings.CustomThemes = CustomThemes.Select(t => new CustomThemeDefinition
        {
            Id = t.Id,
            Name = t.Name,
            BaseMode = ResolveBaseModeForTile(t),
            MainBackgroundHex = t.MainHex,
            SidebarBackgroundHex = t.SidebarHex,
            AccentHex = t.AccentHex,
        }).ToList();
```

`ResolveBaseModeForTile` needs the canonical `BaseMode` we stored on the definition; the tile doesn't carry it. Store it on the tile so we can round-trip without loss. Update `CustomThemeTile` (the file from Step 1) to add `BaseMode`:

```csharp
    [ObservableProperty] private string _baseMode = "Dark";
```

Update `MapDefToTile` to copy `BaseMode = def.BaseMode`. Replace the projection above with `BaseMode = t.BaseMode,` and delete the placeholder `ResolveBaseModeForTile`.

- [ ] **Step 7: Add commands to apply/delete a custom theme**

Add inside the class (next to `SetGrayTheme` etc., around line 649-653):

```csharp
    [RelayCommand]
    private void ApplyCustomTheme(string id)
    {
        var tile = CustomThemes.FirstOrDefault(t => t.Id == id);
        if (tile == null) return;

        foreach (var t in CustomThemes) t.IsActive = t.Id == id;
        ActiveCustomThemeId = id;
        SetActiveThemeFlags("__Custom");

        ActiveAccentHex = tile.AccentHex;
        AccentChanged?.Invoke(this, tile.AccentHex);
        ThemeChanged?.Invoke(this, ResolveActiveThemeKey());

        if (_settingsLoaded) _ = SaveAsync();
    }

    [RelayCommand]
    private void DeleteCustomTheme(string id)
    {
        var tile = CustomThemes.FirstOrDefault(t => t.Id == id);
        if (tile == null) return;
        CustomThemes.Remove(tile);

        if (ActiveCustomThemeId == id)
        {
            ActiveCustomThemeId = null;
            SetActiveThemeFlags("Gray");
            ThemeChanged?.Invoke(this, ResolveActiveThemeKey());
        }

        if (_settingsLoaded) _ = SaveAsync();
    }
```

- [ ] **Step 8: Add command to register App's resolver during construction**

Find the `SettingsViewModel` constructor. At its end (after the existing initialization), add:

```csharp
        if (Avalonia.Application.Current is App app)
        {
            app.CustomThemeResolver = id =>
            {
                var t = CustomThemes.FirstOrDefault(x => x.Id == id);
                if (t == null) return null;
                return new CustomThemeDefinition
                {
                    Id = t.Id,
                    Name = t.Name,
                    BaseMode = t.BaseMode,
                    MainBackgroundHex = t.MainHex,
                    SidebarBackgroundHex = t.SidebarHex,
                    AccentHex = t.AccentHex,
                };
            };
        }
```

- [ ] **Step 9: Build and run the persistence test**

```
dotnet build src/Noctis/Noctis.csproj -v minimal
dotnet test tests/Noctis.Tests/Noctis.Tests.csproj -v minimal
```

Expected: build succeeds; all tests including the existing `CustomThemePersistenceTests` still pass.

- [ ] **Step 10: Commit**

```
git add src/Noctis/ViewModels/SettingsViewModel.cs src/Noctis/ViewModels/CustomThemeTile.cs
git commit -m "feat(themes): wire custom themes through SettingsViewModel"
```

---

## Task 7: `ThemeEditorViewModel` — editor state with live derivation

**Files:**
- Create: `src/Noctis/ViewModels/ThemeEditorViewModel.cs`

- [ ] **Step 1: Create the VM**

```csharp
using System.Collections.Generic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

public partial class ThemeEditorViewModel : ObservableObject
{
    private readonly System.Collections.Generic.IReadOnlyCollection<string> _existingNames;
    private readonly bool _isEdit;

    public ThemeEditorViewModel(
        CustomThemeDefinition? existing,
        IEnumerable<string> existingNamesExcludingThis)
    {
        _existingNames = new HashSet<string>(existingNamesExcludingThis, StringComparer.OrdinalIgnoreCase);
        _isEdit = existing != null;

        var def = existing ?? new CustomThemeDefinition();
        Id = def.Id;
        Name = def.Name;
        IsDarkMode = def.BaseMode != "Light";
        MainHex = def.MainBackgroundHex;
        SidebarHex = def.SidebarBackgroundHex;
        AccentHex = def.AccentHex;
        RebuildPreview();
    }

    public string Id { get; }
    public bool IsEdit => _isEdit;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isDarkMode = true;
    [ObservableProperty] private string _mainHex = "#121212";
    [ObservableProperty] private string _sidebarHex = "#1C1C1C";
    [ObservableProperty] private string _accentHex = "#E74856";

    // ── Preview brushes consumed by the dialog ──
    [ObservableProperty] private IBrush? _previewMain;
    [ObservableProperty] private IBrush? _previewSidebar;
    [ObservableProperty] private IBrush? _previewAccent;
    [ObservableProperty] private IBrush? _previewPrimaryText;
    [ObservableProperty] private IBrush? _previewSecondaryText;
    [ObservableProperty] private IBrush? _previewIslandBg;
    [ObservableProperty] private IBrush? _previewIslandFg;
    [ObservableProperty] private IBrush? _previewIslandFgSecondary;
    [ObservableProperty] private IBrush? _previewSidebarHover;

    // ── Validation ──
    public bool HasInvalidName => string.IsNullOrWhiteSpace(Name);
    public bool HasDuplicateName => !HasInvalidName && _existingNames.Contains(Name.Trim());
    public bool CanSave => !HasInvalidName && !HasDuplicateName;

    public event Action<CustomThemeDefinition>? Saved;
    public event Action? Cancelled;

    partial void OnNameChanged(string value)        { OnPropertyChanged(nameof(HasInvalidName)); OnPropertyChanged(nameof(HasDuplicateName)); OnPropertyChanged(nameof(CanSave)); SaveCommand.NotifyCanExecuteChanged(); }
    partial void OnIsDarkModeChanged(bool value)    => RebuildPreview();
    partial void OnMainHexChanged(string value)     => RebuildPreview();
    partial void OnSidebarHexChanged(string value)  => RebuildPreview();
    partial void OnAccentHexChanged(string value)   => RebuildPreview();

    private void RebuildPreview()
    {
        var def = ToDefinition();
        var dict = ThemeDerivation.Derive(def);
        PreviewMain               = (IBrush)dict["AppMainBackground"];
        PreviewSidebar            = (IBrush)dict["AppSidebarBackground"];
        PreviewAccent             = (IBrush)dict["AccentColorBrush"];
        PreviewPrimaryText        = (IBrush)dict["PrimaryTextBrush"];
        PreviewSecondaryText      = (IBrush)dict["SecondaryTextBrush"];
        PreviewIslandBg           = (IBrush)dict["IslandBackground"];
        PreviewIslandFg           = (IBrush)dict["IslandForeground"];
        PreviewIslandFgSecondary  = (IBrush)dict["IslandForegroundSecondary"];
        PreviewSidebarHover       = (IBrush)dict["SidebarHoverBrush"];
    }

    private CustomThemeDefinition ToDefinition() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        BaseMode = IsDarkMode ? "Dark" : "Light",
        MainBackgroundHex = MainHex,
        SidebarBackgroundHex = SidebarHex,
        AccentHex = AccentHex,
    };

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => Saved?.Invoke(ToDefinition());

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();
}
```

- [ ] **Step 2: Build**

```
dotnet build src/Noctis/Noctis.csproj -v minimal
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```
git add src/Noctis/ViewModels/ThemeEditorViewModel.cs
git commit -m "feat(themes): add ThemeEditorViewModel with live preview brushes"
```

---

## Task 8: `ThemeEditorDialog` view + Settings integration

**Files:**
- Create: `src/Noctis/Views/ThemeEditorDialog.axaml` + `.axaml.cs`
- Modify: `src/Noctis/ViewModels/SettingsViewModel.cs` (`OpenThemeEditor` command + dialog launcher)

- [ ] **Step 1: Create the dialog**

`src/Noctis/Views/ThemeEditorDialog.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:Noctis.ViewModels"
        x:Class="Noctis.Views.ThemeEditorDialog"
        x:DataType="vm:ThemeEditorViewModel"
        Title="Theme Editor"
        Width="780" Height="540"
        CanResize="False"
        SystemDecorations="None"
        TransparencyLevelHint="Transparent"
        Background="Transparent"
        ShowInTaskbar="False"
        WindowStartupLocation="CenterOwner">

    <Border Background="#50000000">
        <Border CornerRadius="20"
                ClipToBounds="True"
                Background="{DynamicResource AppMainBackground}"
                HorizontalAlignment="Center" VerticalAlignment="Center"
                Width="760" Height="520"
                BoxShadow="0 8 32 0 #60000000">
            <DockPanel>

                <!-- Bottom buttons -->
                <Border DockPanel.Dock="Bottom" Padding="20,10,20,16">
                    <Grid ColumnDefinitions="*,Auto,Auto">
                        <Button Grid.Column="1"
                                Content="Save"
                                Command="{Binding SaveCommand}"
                                Width="100" Padding="0,8"
                                Background="{DynamicResource AccentColorBrush}"
                                Foreground="White" FontWeight="SemiBold"
                                CornerRadius="999" Margin="0,0,8,0" Cursor="Hand"/>
                        <Button Grid.Column="2"
                                Content="Cancel"
                                Command="{Binding CancelCommand}"
                                Width="100" Padding="0,8"
                                CornerRadius="999" Cursor="Hand"/>
                    </Grid>
                </Border>

                <Grid ColumnDefinitions="320,*" Margin="20,16">

                    <!-- Left: inputs -->
                    <StackPanel Grid.Column="0" Spacing="14" Margin="0,0,20,0">
                        <TextBlock Text="Custom Theme" FontSize="20" FontWeight="Bold"/>

                        <StackPanel Spacing="4">
                            <TextBlock Text="Name" FontSize="13" FontWeight="SemiBold"/>
                            <TextBox Text="{Binding Name, Mode=TwoWay}"
                                     Watermark="My Theme" CornerRadius="999"/>
                            <TextBlock Text="A theme with this name already exists"
                                       FontSize="11"
                                       Foreground="{DynamicResource AccentColorBrush}"
                                       IsVisible="{Binding HasDuplicateName}"/>
                        </StackPanel>

                        <StackPanel Spacing="4">
                            <TextBlock Text="Base mode" FontSize="13" FontWeight="SemiBold"/>
                            <StackPanel Orientation="Horizontal" Spacing="8">
                                <ToggleButton Content="Dark"
                                              IsChecked="{Binding IsDarkMode, Mode=TwoWay}"
                                              CornerRadius="999" Padding="16,6"/>
                                <ToggleButton Content="Light"
                                              IsChecked="{Binding !IsDarkMode, Mode=TwoWay}"
                                              CornerRadius="999" Padding="16,6"/>
                            </StackPanel>
                        </StackPanel>

                        <StackPanel Spacing="4">
                            <TextBlock Text="Main background" FontSize="13" FontWeight="SemiBold"/>
                            <TextBox Text="{Binding MainHex, Mode=TwoWay}"
                                     Watermark="#121212" CornerRadius="999"/>
                        </StackPanel>

                        <StackPanel Spacing="4">
                            <TextBlock Text="Sidebar background" FontSize="13" FontWeight="SemiBold"/>
                            <TextBox Text="{Binding SidebarHex, Mode=TwoWay}"
                                     Watermark="#1C1C1C" CornerRadius="999"/>
                        </StackPanel>

                        <StackPanel Spacing="4">
                            <TextBlock Text="Accent" FontSize="13" FontWeight="SemiBold"/>
                            <TextBox Text="{Binding AccentHex, Mode=TwoWay}"
                                     Watermark="#E74856" CornerRadius="999"/>
                        </StackPanel>
                    </StackPanel>

                    <!-- Right: live preview -->
                    <Border Grid.Column="1"
                            CornerRadius="14" ClipToBounds="True"
                            BorderBrush="#1FFFFFFF" BorderThickness="1">
                        <Grid ColumnDefinitions="120,*" RowDefinitions="*,Auto">
                            <!-- Fake sidebar -->
                            <Border Grid.Column="0" Grid.Row="0"
                                    Background="{Binding PreviewSidebar}">
                                <StackPanel Margin="10,16" Spacing="8">
                                    <Border CornerRadius="8" Padding="10,8"
                                            Background="{Binding PreviewSidebarHover}">
                                        <TextBlock Text="Home" FontSize="12"
                                                   Foreground="{Binding PreviewPrimaryText}"/>
                                    </Border>
                                    <TextBlock Text="Library" FontSize="12" Margin="10,0"
                                               Foreground="{Binding PreviewSecondaryText}"/>
                                    <TextBlock Text="Playlists" FontSize="12" Margin="10,0"
                                               Foreground="{Binding PreviewSecondaryText}"/>
                                </StackPanel>
                            </Border>

                            <!-- Fake main -->
                            <Border Grid.Column="1" Grid.Row="0"
                                    Background="{Binding PreviewMain}">
                                <StackPanel Margin="20,18" Spacing="12">
                                    <TextBlock Text="Preview" FontSize="18" FontWeight="Bold"
                                               Foreground="{Binding PreviewPrimaryText}"/>
                                    <Border CornerRadius="8" Padding="12,10"
                                            Background="{Binding PreviewSidebarHover}">
                                        <Grid ColumnDefinitions="Auto,*,Auto" >
                                            <Border Grid.Column="0" Width="34" Height="34"
                                                    CornerRadius="4" Background="{Binding PreviewAccent}"/>
                                            <StackPanel Grid.Column="1" Margin="10,0" VerticalAlignment="Center">
                                                <TextBlock Text="Track title"
                                                           Foreground="{Binding PreviewPrimaryText}" FontWeight="SemiBold"/>
                                                <TextBlock Text="Artist · Album"
                                                           Foreground="{Binding PreviewSecondaryText}" FontSize="12"/>
                                            </StackPanel>
                                            <TextBlock Grid.Column="2" Text="3:24"
                                                       Foreground="{Binding PreviewSecondaryText}" VerticalAlignment="Center"/>
                                        </Grid>
                                    </Border>
                                </StackPanel>
                            </Border>

                            <!-- Fake Island -->
                            <Border Grid.ColumnSpan="2" Grid.Row="1"
                                    Margin="20,0,20,16"
                                    Padding="14,8" CornerRadius="999"
                                    Background="{Binding PreviewIslandBg}">
                                <Grid ColumnDefinitions="Auto,*,Auto,Auto">
                                    <Border Grid.Column="0" Width="28" Height="28"
                                            CornerRadius="4" Background="{Binding PreviewAccent}"/>
                                    <StackPanel Grid.Column="1" Margin="10,0">
                                        <TextBlock Text="Now Playing"
                                                   Foreground="{Binding PreviewIslandFg}" FontWeight="SemiBold"/>
                                        <TextBlock Text="Artist"
                                                   Foreground="{Binding PreviewIslandFgSecondary}" FontSize="11"/>
                                    </StackPanel>
                                    <Border Grid.Column="2" Width="28" Height="28" CornerRadius="14"
                                            Background="{Binding PreviewIslandFg}" VerticalAlignment="Center"/>
                                </Grid>
                            </Border>
                        </Grid>
                    </Border>
                </Grid>
            </DockPanel>
        </Border>
    </Border>
</Window>
```

- [ ] **Step 2: Create the code-behind**

`src/Noctis/Views/ThemeEditorDialog.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class ThemeEditorDialog : Window
{
    public CustomThemeDefinition? Result { get; private set; }

    public ThemeEditorDialog()
    {
        InitializeComponent();
    }

    public ThemeEditorDialog(ThemeEditorViewModel vm) : this()
    {
        DataContext = vm;
        vm.Saved += def => { Result = def; Close(); };
        vm.Cancelled += () => { Result = null; Close(); };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
```

- [ ] **Step 3: Add the dialog-opening command to `SettingsViewModel`**

In `SettingsViewModel.cs`, near `ApplyCustomThemeCommand`, add:

```csharp
    [RelayCommand]
    private async Task OpenThemeEditorAsync(string? existingId)
    {
        var existingTile = string.IsNullOrEmpty(existingId)
            ? null
            : CustomThemes.FirstOrDefault(t => t.Id == existingId);

        CustomThemeDefinition? existingDef = null;
        if (existingTile != null)
        {
            existingDef = new CustomThemeDefinition
            {
                Id = existingTile.Id,
                Name = existingTile.Name,
                BaseMode = existingTile.BaseMode,
                MainBackgroundHex = existingTile.MainHex,
                SidebarBackgroundHex = existingTile.SidebarHex,
                AccentHex = existingTile.AccentHex,
            };
        }

        var nameBlocklist = CustomThemes
            .Where(t => existingTile == null || t.Id != existingTile.Id)
            .Select(t => t.Name);

        var vm = new ThemeEditorViewModel(existingDef, nameBlocklist);
        var dialog = new Views.ThemeEditorDialog(vm);

        var owner = (Avalonia.Application.Current?.ApplicationLifetime
                      as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner != null) await dialog.ShowDialog(owner);
        else dialog.Show();

        if (dialog.Result == null) return;

        var result = dialog.Result;
        if (existingTile != null)
        {
            existingTile.Name = result.Name;
            existingTile.BaseMode = result.BaseMode;
            existingTile.MainHex = result.MainBackgroundHex;
            existingTile.SidebarHex = result.SidebarBackgroundHex;
            existingTile.AccentHex = result.AccentHex;
        }
        else
        {
            CustomThemes.Add(new CustomThemeTile
            {
                Id = result.Id,
                Name = result.Name,
                BaseMode = result.BaseMode,
                MainHex = result.MainBackgroundHex,
                SidebarHex = result.SidebarBackgroundHex,
                AccentHex = result.AccentHex,
            });
        }

        if (_settingsLoaded) _ = SaveAsync();
        ApplyCustomTheme(result.Id);
    }
```

- [ ] **Step 4: Build**

```
dotnet build src/Noctis/Noctis.csproj -v minimal
```

Expected: build succeeds.

- [ ] **Step 5: Commit**

```
git add src/Noctis/Views/ThemeEditorDialog.axaml src/Noctis/Views/ThemeEditorDialog.axaml.cs src/Noctis/ViewModels/SettingsViewModel.cs
git commit -m "feat(themes): add ThemeEditorDialog and OpenThemeEditor command"
```

---

## Task 9: Themes row UI — custom tiles + add tile + context menu

**Files:**
- Modify: `src/Noctis/Views/SettingsView.axaml`

- [ ] **Step 1: Add the custom-tile section and add-tile**

In `src/Noctis/Views/SettingsView.axaml`, locate the closing `</WrapPanel>` at line 624 (end of the existing theme buttons WrapPanel). Immediately before that `</WrapPanel>`, insert:

```xml
                        <!-- User-created themes -->
                        <ItemsControl ItemsSource="{Binding CustomThemes}">
                            <ItemsControl.ItemsPanel>
                                <ItemsPanelTemplate>
                                    <WrapPanel Orientation="Horizontal"/>
                                </ItemsPanelTemplate>
                            </ItemsControl.ItemsPanel>
                            <ItemsControl.ItemTemplate>
                                <DataTemplate x:DataType="vm:CustomThemeTile">
                                    <Button Classes="theme-btn"
                                            Classes.active="{Binding IsActive}"
                                            Command="{Binding $parent[UserControl].((vm:SettingsViewModel)DataContext).ApplyCustomThemeCommand}"
                                            CommandParameter="{Binding Id}"
                                            Margin="0,0,10,10">
                                        <Button.ContextMenu>
                                            <ContextMenu>
                                                <MenuItem Header="Edit"
                                                          Command="{Binding $parent[UserControl].((vm:SettingsViewModel)DataContext).OpenThemeEditorCommand}"
                                                          CommandParameter="{Binding Id}"/>
                                                <MenuItem Header="Delete"
                                                          Command="{Binding $parent[UserControl].((vm:SettingsViewModel)DataContext).DeleteCustomThemeCommand}"
                                                          CommandParameter="{Binding Id}"/>
                                            </ContextMenu>
                                        </Button.ContextMenu>
                                        <StackPanel Spacing="8" HorizontalAlignment="Center">
                                            <Border Width="64" Height="40" CornerRadius="6"
                                                    BorderBrush="#33000000" BorderThickness="1"
                                                    ClipToBounds="True">
                                                <Grid ColumnDefinitions="22,*">
                                                    <Border Grid.Column="0"
                                                            Background="{Binding SidebarHex}"/>
                                                    <Border Grid.Column="1"
                                                            Background="{Binding MainHex}">
                                                        <Border Height="3" Width="30" CornerRadius="1.5"
                                                                Margin="6,6,0,0"
                                                                Background="{Binding AccentHex}"
                                                                HorizontalAlignment="Left"
                                                                VerticalAlignment="Top"/>
                                                    </Border>
                                                </Grid>
                                            </Border>
                                            <TextBlock Text="{Binding Name}" FontSize="12"
                                                       HorizontalAlignment="Center"
                                                       MaxWidth="64" TextTrimming="CharacterEllipsis"/>
                                        </StackPanel>
                                    </Button>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>

                        <!-- "+ Custom" add tile -->
                        <Button Classes="theme-btn"
                                Command="{Binding OpenThemeEditorCommand}"
                                CommandParameter="{x:Null}"
                                Margin="0,0,10,10"
                                ToolTip.Tip="Create custom theme">
                            <StackPanel Spacing="8" HorizontalAlignment="Center">
                                <Border Width="64" Height="40" CornerRadius="6"
                                        BorderBrush="#55FFFFFF" BorderThickness="1"
                                        BorderDashArray="3,3">
                                    <TextBlock Text="+" FontSize="22" Opacity="0.7"
                                               HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                </Border>
                                <TextBlock Text="Custom" FontSize="12"
                                           HorizontalAlignment="Center"/>
                            </StackPanel>
                        </Button>
```

- [ ] **Step 2: Build and run the app**

```
dotnet build src/Noctis/Noctis.csproj -v minimal
```

Expected: build succeeds.

- [ ] **Step 3: Manual sanity-check (no automated coverage available)**

Run the app, open Settings → Themes:
- Confirm the "+ Custom" tile appears at the end of the row.
- Click "+ Custom" → dialog opens with default values, preview renders.
- Type a name, change main bg to `#1F1233`, accent to `#FFAB40`, click Save → tile appears in the row, theme applies to the rest of the app live.
- Click another built-in theme → custom tile loses the active ring; built-in tile gets the ring.
- Click the custom tile again → app re-applies. Right-click it → Edit / Delete options. Edit re-opens the dialog with the values populated.

- [ ] **Step 4: Commit**

```
git add src/Noctis/Views/SettingsView.axaml
git commit -m "feat(themes): show custom tiles and add-button in Settings"
```

---

## Task 10: Add the stale-Custom fallback persistence test

**Files:**
- Modify: `tests/Noctis.Tests/CustomThemePersistenceTests.cs`

This guards the load path that survives a stale `Theme = "Custom:<id>"` with no matching entry.

- [ ] **Step 1: Append the test**

```csharp
    [Fact]
    public void StaleCustomThemeReference_DeserializesCleanly()
    {
        var json = """
        {
            "Theme": "Custom:nonexistent-id",
            "CustomThemes": []
        }
        """;
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal("Custom:nonexistent-id", restored.Theme);
        Assert.Empty(restored.CustomThemes);
        // The fallback-to-Gray happens at load time in SettingsViewModel (Task 6 Step 3);
        // this test only confirms the JSON shape stays valid.
    }
```

- [ ] **Step 2: Run tests**

```
dotnet test tests/Noctis.Tests/Noctis.Tests.csproj --filter "FullyQualifiedName~CustomThemePersistenceTests" -v minimal
```

Expected: all three persistence tests pass.

- [ ] **Step 3: Commit**

```
git add tests/Noctis.Tests/CustomThemePersistenceTests.cs
git commit -m "test(themes): cover stale Custom:<id> reference fallback"
```

---

## Task 11: Final verification

- [ ] **Step 1: Full build**

```
dotnet build src/Noctis/Noctis.csproj -v minimal
```

Expected: 0 errors.

- [ ] **Step 2: Full test suite**

```
dotnet test tests/Noctis.Tests/Noctis.Tests.csproj -v minimal
```

Expected: all tests pass.

- [ ] **Step 3: Manual end-to-end script**

Launch the app and run this checklist; for each step, confirm text is readable (no near-invisible labels) on every visible surface (sidebar, main area, Island, dropdowns, dialogs):

1. Open Settings → Themes. Built-in tiles render unchanged. "+ Custom" tile is last.
2. Create a `dark` custom theme: `main=#101820`, `sidebar=#18222C`, `accent=#4C6EF5`. Save.
3. Navigate Home → Library Songs → Library Albums → Library Artists → Library Playlists → Favorites → an Album → Lyrics → Settings. Confirm readability everywhere.
4. Hover sidebar items, hover/select track rows, open a context menu, open the Playback Island. All accents reflect `#4C6EF5`.
5. Create a `light` custom theme: `main=#F8F8F4`, `sidebar=#ECECE6`, `accent=#B5651D`. Save. Repeat the navigation pass.
6. Right-click the dark tile → Edit → change main to `#0A0A0A` → Save. Theme updates live.
7. Right-click the light tile → Delete. Confirm Settings falls back to Gray (since light was active after step 6 → re-apply light first if needed).
8. Restart the app. The last-applied custom theme is restored.
9. Manually corrupt `settings.json`: set `"Theme": "Custom:fake"`. Launch app — falls back to Gray cleanly.

Report any unreadable surface — that is a derivation gap to fix in `ThemeDerivation.Derive`.

- [ ] **Step 4: Final commit if any small fixes**

If the verification pass uncovered any tweaks (e.g. an additional resource key the app uses that the derivation didn't emit), add it to `Derive`, add a unit test in `ThemeDerivationTests.RequiredKeys`, and commit:

```
git add -p
git commit -m "fix(themes): emit <key> key from ThemeDerivation"
```

---

## Self-review notes

**Spec coverage:**
- `CustomThemeDefinition` model → Task 1.
- 5-input derivation with WCAG contrast → Tasks 2-4.
- `App.SetTheme` wiring → Task 5.
- ViewModel state, commands, persistence → Task 6.
- Editor VM + dialog → Tasks 7-8.
- Settings row UI (tiles, add tile, context menu) → Task 9.
- Stale fallback → Task 6 Step 3 (load-time) + Task 10 (test).
- Manual readability sweep across every page → Task 11.

**Type/name consistency check:**
- `CustomThemeDefinition` field order matches across model, VM, dialog, and tile-mapper.
- `CustomThemeTile.BaseMode` added explicitly in Task 6 Step 6 (initial Step 1 stub was missing it — corrected inline before Tasks 7/8 use it).
- `Saved` event signature `Action<CustomThemeDefinition>` matches `Result = def` consumer in the dialog code-behind.
- `OpenThemeEditorCommand` takes `string?` parameter; `CommandParameter="{x:Null}"` in XAML for the add button and `CommandParameter="{Binding Id}"` for edit menu items both bind correctly.

**Placeholder scan:** No "TBD" / "implement later" / "add error handling" / "similar to Task N" patterns. Every code step ships the actual code.
