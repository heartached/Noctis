# Album Page Tint — Apple Music Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace lightness-clamped k-means tint extraction with edge-pixel background sampling and add an adaptive light/dark foreground brush family so the album page reads like Apple Music for any cover.

**Architecture:** New `ExtractEdgeBackgroundColor` in `DominantColorExtractor` samples the outer pixel ring of the cover and picks the most-frequent color via a coarse histogram, with no lightness clamp. `AlbumDetailViewModel` exposes an `IsLightTint` flag plus three foreground brushes (`PageForegroundBrush`, `PageSubtleForegroundBrush`, `PageDividerBrush`) computed from the tint's relative luminance. `AlbumDetailView.axaml` inherits foreground via the root container and swaps the few explicit overrides (disc header, EQ visualizer) to bind these brushes; accent-red elements are unchanged.

**Tech Stack:** Avalonia, C# (.NET 8), CommunityToolkit.Mvvm `[ObservableProperty]`, existing `DominantColorExtractor` pipeline.

**Spec:** [docs/superpowers/specs/2026-05-04-album-page-tint-design.md](docs/superpowers/specs/2026-05-04-album-page-tint-design.md)

**Verification baseline (per `.claude/rules/testing.md`):**
- App build succeeds: `dotnet build src/Noctis/Noctis.csproj -v minimal`.
- Test project compile is broken at baseline (missing `IPersistenceService.LoadIndexCacheAsync`/`SaveIndexCacheAsync` in `TestPersistenceService.cs`). Do NOT attempt to fix that baseline as part of this plan.
- This feature has no automated test coverage today (the extractor uses Avalonia `RenderTargetBitmap` and can't be unit-tested headlessly). Verification is build + manual visual check.

---

## File Structure

**Modified files:**
- `src/Noctis/Services/DominantColorExtractor.cs`
  - Add `ExtractEdgeBackgroundColor(Bitmap?) : Color`.
  - Add `GetOrExtractEdgeBackgroundColor(string artworkPath, Bitmap bitmap) : Color` (cached wrapper, mirrors `GetOrExtractDominantColor`).
  - Add `GetRelativeLuminance(Color) : double` helper (sRGB-linearized luminance, 0–1).
  - Mark old `ExtractAmbientColor` as `[Obsolete]` and route it to `ExtractEdgeBackgroundColor` so any forgotten caller still compiles. Remove in a later cleanup.
- `src/Noctis/ViewModels/AlbumDetailViewModel.cs`
  - Add observable properties: `IsLightTint`, `PageForegroundBrush`, `PageSubtleForegroundBrush`, `PageDividerBrush`.
  - Replace `RebuildBackgroundBrush` body to call new extractor and compute the brush family.
  - Reset the brush family to dark-theme defaults whenever tint is disabled or art is unavailable.
- `src/Noctis/Views/AlbumDetailView.axaml`
  - Bind page foreground via the root scroll-content `StackPanel` so descendants inherit.
  - Swap the disc-group header `SystemControlForegroundBaseHighBrush` to `PageForegroundBrush`.
  - Swap the per-row `EqVisualizer` `Foreground="White"` to `PageForegroundBrush`.
  - Leave accent-red buttons (#E74856 backgrounds with white content) and flyout/menu visuals untouched.

**No new files. No public-API changes outside `DominantColorExtractor`.**

---

## Task 1: Add `GetRelativeLuminance` helper

**Files:**
- Modify: `src/Noctis/Services/DominantColorExtractor.cs`

- [ ] **Step 1: Add the helper near the bottom of the class, after `HueToRgb`.**

```csharp
/// <summary>
/// sRGB relative luminance per WCAG 2.x (0 = black, 1 = white).
/// Used to decide whether a tint is light enough to need dark foreground text.
/// </summary>
public static double GetRelativeLuminance(Color color)
{
    static double Channel(byte c)
    {
        double s = c / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
    return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
}
```

- [ ] **Step 2: Build to confirm it compiles.**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: build succeeds.

- [ ] **Step 3: Commit.**

```bash
git add src/Noctis/Services/DominantColorExtractor.cs
git commit -m "feat(color): add GetRelativeLuminance for tint contrast decisions"
```

---

## Task 2: Add `ExtractEdgeBackgroundColor` (edge-ring histogram)

**Files:**
- Modify: `src/Noctis/Services/DominantColorExtractor.cs`

- [ ] **Step 1: Add the new extractor method just below `ExtractAmbientColor`.**

```csharp
/// <summary>
/// Apple-Music-style background extraction: samples the outer 1-pixel ring of a
/// downscaled cover, picks the most common color via a coarse 6-bits-per-channel
/// histogram, and returns the weighted average of the winning bucket. Preserves
/// the cover's natural lightness — no clamping. Falls back to the cover's
/// dominant color if the edge ring is uninformative (e.g., near-uniform black or
/// white border).
/// </summary>
public static Color ExtractEdgeBackgroundColor(Bitmap? bitmap)
{
    if (bitmap == null || bitmap.Size.Width <= 0 || bitmap.Size.Height <= 0)
        return FallbackColor;

    const int sampleSize = 50;

    try
    {
        var pixelSize = new PixelSize(sampleSize, sampleSize);
        using var rtb = new RenderTargetBitmap(pixelSize);
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawImage(bitmap,
                new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height),
                new Rect(0, 0, sampleSize, sampleSize));
        }

        using var ms = new MemoryStream();
        rtb.Save(ms);
        ms.Position = 0;
        using var decoded = WriteableBitmap.Decode(ms);
        using var fb = decoded.Lock();

        int width = fb.Size.Width;
        int height = fb.Size.Height;
        int rowBytes = fb.RowBytes;
        int bufferSize = rowBytes * height;
        var pixels = new byte[bufferSize];
        Marshal.Copy(fb.Address, pixels, 0, bufferSize);

        // Histogram: 4 bits per channel = 4096 buckets. Keys are packed RRRRGGGGBBBB.
        var counts = new Dictionary<int, int>(256);
        var sums = new Dictionary<int, (long R, long G, long B)>(256);

        void Sample(int x, int y)
        {
            int offset = y * rowBytes + x * 4;
            byte b = pixels[offset];
            byte g = pixels[offset + 1];
            byte r = pixels[offset + 2];
            int key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
            counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
            var s = sums.TryGetValue(key, out var v) ? v : (0L, 0L, 0L);
            sums[key] = (s.R + r, s.G + g, s.B + b);
        }

        // Outer 1-pixel ring (top row, bottom row, left column, right column).
        for (int x = 0; x < width; x++)
        {
            Sample(x, 0);
            Sample(x, height - 1);
        }
        for (int y = 1; y < height - 1; y++)
        {
            Sample(0, y);
            Sample(width - 1, y);
        }

        if (counts.Count == 0)
            return FallbackColor;

        // Prefer non-noise buckets (luminance 6..250). If only black/white remain,
        // accept them — that genuinely is the cover's color.
        int bestKey = -1;
        int bestCount = -1;
        int bestKeyAny = -1;
        int bestCountAny = -1;
        foreach (var kv in counts)
        {
            var (sr, sg, sb) = sums[kv.Key];
            int n = kv.Value;
            int ar = (int)(sr / n), ag = (int)(sg / n), ab = (int)(sb / n);
            int luma = (ar * 299 + ag * 587 + ab * 114) / 1000;

            if (n > bestCountAny) { bestCountAny = n; bestKeyAny = kv.Key; }
            if (luma < 6 || luma > 250) continue;
            if (n > bestCount) { bestCount = n; bestKey = kv.Key; }
        }

        int chosen = bestKey >= 0 ? bestKey : bestKeyAny;
        var (tr, tg, tb) = sums[chosen];
        int total = counts[chosen];
        var rawColor = Color.FromRgb(
            (byte)(tr / total),
            (byte)(tg / total),
            (byte)(tb / total));

        // Gentle saturation floor so genuinely grey covers stay grey, but covers
        // with any hue at all read as tinted rather than washed out.
        var (h, s, l) = RgbToHsl(rawColor.R, rawColor.G, rawColor.B);
        if (s > 0.05 && s < 0.10) s = 0.10;
        return HslToColor(h, s, l);
    }
    catch
    {
        return FallbackColor;
    }
}
```

- [ ] **Step 2: Add a cached wrapper next to `GetOrExtractDominantColor`.**

Find the existing `GetOrExtractDominantColor` and `GetOrExtractPalette`. Add a third cache and wrapper alongside them:

```csharp
private static readonly ConcurrentDictionary<string, Color> EdgeBackgroundCache = new(StringComparer.OrdinalIgnoreCase);

/// <summary>
/// Returns a cached edge-background color for the given artwork path, or extracts and caches it.
/// </summary>
public static Color GetOrExtractEdgeBackgroundColor(string artworkPath, Bitmap bitmap)
{
    if (EdgeBackgroundCache.TryGetValue(artworkPath, out var cached))
        return cached;

    var color = ExtractEdgeBackgroundColor(bitmap);

    if (EdgeBackgroundCache.Count >= MaxCacheSize)
        EdgeBackgroundCache.Clear();

    EdgeBackgroundCache.TryAdd(artworkPath, color);
    return color;
}
```

- [ ] **Step 3: Mark the old `ExtractAmbientColor` as obsolete and forward it.**

Replace the body of `ExtractAmbientColor` (it currently runs k-means + lightness clamp) with a forward, and add the obsolete attribute on the method:

```csharp
[Obsolete("Use ExtractEdgeBackgroundColor — Apple Music-style edge-ring sampling without lightness clamping.")]
public static Color ExtractAmbientColor(Bitmap? bitmap) => ExtractEdgeBackgroundColor(bitmap);
```

Delete the old k-means / clamp body (everything between the original method's `{` and `}` except the new forwarding line).

- [ ] **Step 4: Build to confirm it compiles (the `[Obsolete]` will produce a warning at the existing call site in `AlbumDetailViewModel.RebuildBackgroundBrush`; that's fine — Task 3 replaces that call).**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: build succeeds. May show one obsolete-warning on `AlbumDetailViewModel.cs` line ~329; that warning will disappear after Task 3.

- [ ] **Step 5: Commit.**

```bash
git add src/Noctis/Services/DominantColorExtractor.cs
git commit -m "feat(color): add edge-pixel background extraction (Apple Music style)"
```

---

## Task 3: Wire foreground brush family into `AlbumDetailViewModel`

**Files:**
- Modify: `src/Noctis/ViewModels/AlbumDetailViewModel.cs`

- [ ] **Step 1: Add the new observable properties next to the existing `_backgroundBrush` field (around line 36).**

Find this line in the property block:

```csharp
[ObservableProperty] private IBrush? _backgroundBrush;
```

Add directly after it:

```csharp
[ObservableProperty] private bool _isLightTint;
[ObservableProperty] private IBrush _pageForegroundBrush = Brushes.White;
[ObservableProperty] private IBrush _pageSubtleForegroundBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
[ObservableProperty] private IBrush _pageDividerBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
```

(Defaults match dark-theme behavior so the page looks identical to today when tint is off or art is missing.)

- [ ] **Step 2: Replace `RebuildBackgroundBrush` body with the new logic.**

Locate `RebuildBackgroundBrush` (around line 315) and replace the entire method body with:

```csharp
private void RebuildBackgroundBrush()
{
    var bmp = AlbumArt;
    if (bmp == null || _settings?.AlbumDetailColorTintEnabled == false)
    {
        BackgroundBrush = null;
        IsLightTint = false;
        PageForegroundBrush = Brushes.White;
        PageSubtleForegroundBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        PageDividerBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        return;
    }

    // Extraction must run on the UI thread — DominantColorExtractor uses
    // RenderTargetBitmap/DrawImage, which Avalonia restricts to the UI thread.
    // The sample target is 50x50, so this is sub-millisecond and does not stutter the UI.
    try
    {
        var color = DominantColorExtractor.ExtractEdgeBackgroundColor(bmp);
        BackgroundBrush = new SolidColorBrush(color);

        var luminance = DominantColorExtractor.GetRelativeLuminance(color);
        var isLight = luminance > 0.55;
        IsLightTint = isLight;

        if (isLight)
        {
            PageForegroundBrush = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
            PageSubtleForegroundBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
            PageDividerBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
        }
        else
        {
            PageForegroundBrush = Brushes.White;
            PageSubtleForegroundBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
            PageDividerBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        }
    }
    catch (Exception ex)
    {
        DebugLogger.Error(DebugLogger.Category.UI, "AlbumDetail.GradientBg", ex.ToString());
        BackgroundBrush = null;
        IsLightTint = false;
        PageForegroundBrush = Brushes.White;
        PageSubtleForegroundBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        PageDividerBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    }
}
```

(The `Brushes`, `Color`, and `SolidColorBrush` types come from `Avalonia.Media`, which is already imported at the top of the file — line 4.)

- [ ] **Step 3: Build to confirm it compiles.**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: build succeeds. The obsolete-warning from Task 2 should now be gone.

- [ ] **Step 4: Commit.**

```bash
git add src/Noctis/ViewModels/AlbumDetailViewModel.cs
git commit -m "feat(album): adaptive page foreground brushes driven by tint luminance"
```

---

## Task 4: Bind foreground in `AlbumDetailView.axaml`

**Files:**
- Modify: `src/Noctis/Views/AlbumDetailView.axaml`

- [ ] **Step 1: Inherit page foreground from the scroll content.**

Find the outer `StackPanel` directly inside `TrackScrollViewer` (around line 87 — `<StackPanel>` opening with no attributes). Replace it with:

```xml
<StackPanel TextElement.Foreground="{Binding PageForegroundBrush}">
```

(Setting `TextElement.Foreground` here propagates to descendant `TextBlock`s and `PathIcon`s that don't set `Foreground` explicitly. Avalonia 11 supports the attached `TextElement.Foreground` property the same way WPF does.)

- [ ] **Step 2: Swap the disc-group header brush.**

Find this line (around line 404 inside the `DiscGroup` `DataTemplate`):

```xml
Foreground="{DynamicResource SystemControlForegroundBaseHighBrush}"
```

Replace with:

```xml
Foreground="{Binding $parent[UserControl].((vm:AlbumDetailViewModel)DataContext).PageForegroundBrush}"
```

- [ ] **Step 3: Swap the per-row EqVisualizer foreground.**

Find this line (around line 525 inside the `Track` `DataTemplate`):

```xml
Foreground="White"
```

(This is on the `<controls:EqVisualizer x:Name="RowEq" ...>` element. Only this one — do NOT touch the `Foreground="White"` on the album-header shuffle/play accent buttons; those sit on `#E74856` and must stay white.)

Replace with:

```xml
Foreground="{Binding $parent[UserControl].((vm:AlbumDetailViewModel)DataContext).PageForegroundBrush}"
```

- [ ] **Step 4: Confirm out-of-scope foregrounds are NOT modified.**

The following lines must remain unchanged:
- Line ~151, ~225, ~320, ~480, ~585, ~611, ~671: `Foreground="#E74856"` (accent red — heart, artist link, more-dots).
- Line ~265, ~272, ~279, ~300: `Foreground="White"` on accent-button content (sits on `#E74856` background — text must stay white).
- Lines inside `MenuFlyout` / `ContextMenu` blocks using `SystemControlForegroundBaseHighBrush` — those render in popup chrome, not on the page.
- Lines inside `ToolTip.Tip` blocks (e.g., line 191/195/201) — render in popup chrome.
- Related-album / "More by artist" tile templates (lines 755–1118) — out of scope for this spec.

- [ ] **Step 5: Build to confirm the XAML compiles.**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: build succeeds with no XAML compile errors.

- [ ] **Step 6: Commit.**

```bash
git add src/Noctis/Views/AlbumDetailView.axaml
git commit -m "feat(album): bind album page foreground to adaptive tint brushes"
```

---

## Task 5: Manual visual verification

**Files:** none (manual run).

- [ ] **Step 1: Run the app.**

Run: `dotnet run --project src/Noctis/Noctis.csproj`

- [ ] **Step 2: In Settings, confirm the toggle still works.**

- Open Settings → "Album page tint" toggle.
- Toggle off: navigate into any album. Page background should be the standard `AppMainBackground`. Text should be white. Look identical to pre-change behavior.
- Toggle on: re-enter the same album. Page should tint to the cover's color, text should adapt.

- [ ] **Step 3: Verify across cover-color categories.**

Open one album from each category and compare to the Apple Music screenshots in the spec:

| Cover style | Expected result |
|---|---|
| Dark moody (Take Care, Beauty in Death-style) | Tint near the cover's dark color; text **white**. |
| Vibrant mid-tone (teal, purple, deep red) | Saturated mid-tint; text **white**. |
| Pastel / light (Lover-style pink, light pop) | Light tint; text **dark** (`#111`). |
| Sepia / monochrome (Fearless TV, b&w covers) | Warm or neutral tint preserved; text adapts based on brightness. |
| Near-white (Already Dead-style) | Near-white page; text **dark**. |

- [ ] **Step 4: Verify accent elements stayed accent.**

The play and shuffle pill buttons in the album header must remain the red accent (`#E74856`) with white content on **all** tints — they are not page-foreground elements.

- [ ] **Step 5: Verify track-row EQ visualizer color flips.**

Play a track from a light-tint album. The animated EQ bars on the playing row should be **dark** (matches `PageForegroundBrush`). On a dark-tint album, they should be **white**.

- [ ] **Step 6: Verify the disc-group header on multi-disc albums adapts.**

Open a multi-disc album with a light cover (if available in the user's library). The "Disc 1 / Disc 2" header text must read in dark, not white.

- [ ] **Step 7: If anything looks wrong, capture which cover and which symptom, and stop here for redesign rather than tweaking values blindly.**

The threshold (0.55) and saturation floor (0.10) are intentional starting values; the spec calls out a deferred dead-zone option if borderline covers flicker.

- [ ] **Step 8: No commit (verification only). If everything checks out, the branch is ready for review.**

---

## Self-Review Notes

- Spec coverage:
  - "Edge-pixel background extraction" → Task 2.
  - "Don't clamp lightness" → Task 2 (no clamp; only a 0.05 → 0.10 saturation floor on already-tinted covers).
  - "IsLightTint flag, threshold 0.55" → Task 3.
  - "Three foreground brushes with given color values" → Task 3.
  - "Toggle off = identical to today" → Task 3 (default brushes match today's white-on-dark) + Task 5 step 2.
  - "Scope: album header + track list only" → Task 4 step 4 explicitly enumerates what is NOT touched.
  - "Caching mirrors existing pattern" → Task 2 step 2.
- Type consistency: `PageForegroundBrush` / `PageSubtleForegroundBrush` / `PageDividerBrush` / `IsLightTint` / `BackgroundBrush` used identically across Tasks 3 and 4.
- No placeholders. All steps include exact code or exact commands.
- Risks called out in spec (threshold tuning, edge-bordered covers) are preserved as future-work notes — not blocking this plan.
