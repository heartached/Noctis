# Album Page Tint — Apple Music Parity

**Date:** 2026-05-04
**Branch context:** `perf/startup`
**Setting label:** "Album page tint" (BETA) — `AlbumDetailColorTintEnabled`

## Problem

The current "Tint album pages with cover art" feature force-clamps every cover's lightness to `0.45` and saturation into `[0.45, 0.85]`. The result is that all albums render as a similar medium-saturated mid-tone wash — a dark moody cover (Drake — *Take Care*) and a vivid pop cover (Taylor Swift — *Lover*) end up looking nearly the same on the album page. White text is also kept on every tint, which makes light covers (*Lover*, *Already Dead*, *Fearless TV*) look washed out and unreadable.

Apple Music does not behave this way:

- **Take Care** → near-black brown page, light text.
- **Beauty in Death** → rich teal page, light text.
- **Lover** → light pink page, **dark** text.
- **Fearless TV** → warm sepia page, light text.
- **Already Dead - Single** → near-white page, **dark** text.

The tint reads as the cover's actual color, and the foreground inverts on light covers to keep the page legible.

## Goal

Make the album page tint visually match Apple Music for both background color *and* foreground contrast across the full range of cover art (dark, vibrant, pastel, light, monochrome / sepia).

Out of scope:
- Other pages (artist, playlist, lyrics) — this spec is the album detail page only.
- The Lyrics view's gradient generation — that uses a separate code path and is unaffected.
- Animated transitions between tints.

## Approach

Replace the lightness-clamped k-means selection with **edge-pixel background extraction** (Panic ColorArt-style — the algorithm Apple's iTunes/Music descends from), then derive a foreground brush family from the tint's perceptual luminance.

### 1. Background color extraction (replaces `ExtractAmbientColor`)

In [DominantColorExtractor.cs](src/Noctis/Services/DominantColorExtractor.cs):

- Add `ExtractEdgeBackgroundColor(Bitmap)`:
  - Downscale to 50×50 (reuse the existing RTB pipeline).
  - Sample only the outer 1-pixel ring of the downscaled image (top, bottom, left, right rows/columns).
  - Bucket pixels into a coarse 6-bits-per-channel histogram (4096 buckets).
  - Pick the most frequent bucket whose representative color is **not near-black** (luminance > 6) and **not near-white** (luminance < 250). If only black/white remain, allow them — that is genuinely the cover's color (e.g., monochrome covers).
  - Return the weighted average of the pixels in that bucket (smoother than the bucket center).
- **Do not clamp lightness.** Preserve the cover's natural lightness.
- Apply only a gentle saturation floor (`s = max(s, 0.10)`) so a cover that is genuinely grey stays grey rather than being forced into a hue.
- Cache results per artwork path, mirroring the existing `ColorCache` pattern.

Rationale for edge-ring sampling: album covers' edges are overwhelmingly the canvas/background color (sky for *Lover*, sepia for *Fearless TV*, near-white for *Already Dead*, brown/black for *Take Care*). K-means on the whole image is biased by subject mass; edge sampling is what Apple's published algorithm uses and is far more accurate to "what color does this cover read as."

### 2. Foreground luminance flag

Add to `AlbumDetailViewModel`:

- `[ObservableProperty] bool _isLightTint;`
- Computed when the background color is computed:
  - `relativeLuminance = 0.2126·R + 0.7152·G + 0.0722·B` (sRGB-linearized).
  - `IsLightTint = relativeLuminance > 0.55` (threshold tuned so mid-grey-ish tints fall on the dark-text side; pastel/light covers go to dark text).
- When tint is disabled or no artwork is available, `IsLightTint = false` (dark theme defaults remain).

### 3. Foreground brush family

Expose three brushes on `AlbumDetailViewModel` (computed alongside `BackgroundBrush`):

| Property | Light tint | Dark tint |
|---|---|---|
| `PageForegroundBrush` | `#111111` | `White` |
| `PageSubtleForegroundBrush` | `#66000000` | `#B0FFFFFF` |
| `PageDividerBrush` | `#1F000000` | `#1FFFFFFF` |

Accent red (`#E74856`) is unchanged across both states — it has acceptable contrast on both backgrounds and is the app's identity color.

When tint is disabled, the brushes resolve to the existing dark-theme defaults (`White` / system base brushes / current divider) so the page is visually identical to today's behavior with the toggle off.

### 4. View binding swap

In [AlbumDetailView.axaml](src/Noctis/Views/AlbumDetailView.axaml), replace hard-coded `Foreground="White"` and `{DynamicResource SystemControlForegroundBaseHighBrush}` references **inside the album header and track-list area** with bindings to `PageForegroundBrush` / `PageSubtleForegroundBrush`. Dividers and separator lines bind to `PageDividerBrush`.

Scoped explicitly to:
- Album title, artist, metadata row.
- Track row text (title, duration, track number).
- Header action icons (play, shuffle, more) — `PageForegroundBrush`.
- Section dividers within the album page.

Out of scope for binding swap (intentionally untouched):
- Accent-red elements (heart fill, accent buttons).
- "Related albums" / "More by artist" tile area below the fold — these sit on the page background but use their own card chrome; revisit only if visually necessary.
- Any control templates outside this view.

### 5. Setting + persistence

No changes to `AppSettings.AlbumDetailColorTintEnabled` or `SettingsViewModel` wiring. Toggle behavior is preserved: enabling rebuilds the brush; disabling resets `BackgroundBrush = null` and `IsLightTint = false`, which collapses all foreground brushes back to dark-theme defaults.

## Files changed

- `src/Noctis/Services/DominantColorExtractor.cs` — add `ExtractEdgeBackgroundColor`; rewrite or replace `ExtractAmbientColor` callers.
- `src/Noctis/ViewModels/AlbumDetailViewModel.cs` — add `IsLightTint`, foreground brush properties, update `RebuildBackgroundBrush`.
- `src/Noctis/Views/AlbumDetailView.axaml` — replace hard-coded foregrounds in scope listed above.

## Risks & unknowns

- **Edge-ring bias on bordered covers.** A small minority of covers have a contrasting frame (e.g., white border around art). The histogram approach handles this correctly — the border *is* the visual canvas — but it may surprise users who expect the inner subject color. Acceptable; matches Apple Music behavior on the same covers.
- **Threshold tuning.** `0.55` luminance threshold is a starting value. Some borderline covers (mid-grey, faded sepia) may flicker between light/dark text classification across edits; if needed, add a small dead-zone (e.g., `>0.58 = light`, `<0.48 = dark`, in-between keeps previous state) — defer until observed.
- **Existing `ExtractAmbientColor` is public.** Search confirms no other callers in the repo. Safe to replace; if any external consumers exist they will be a compile error caught immediately.
- **Cache invalidation.** The existing `ColorCache` keys by artwork path. New extractor reuses the same key space; on first run after upgrade, all entries refresh naturally.

## Verification

- Build: `dotnet build src/Noctis/Noctis.csproj -v minimal`.
- Manual visual check: open *Take Care*, *Beauty in Death*, *Lover*, *Fearless TV*, *Already Dead* (or equivalents in user's library) and compare against the Apple Music screenshots. Light covers must show dark text; dark covers must keep light text; tint must read as the cover's color.
- Toggle behavior: setting off → page is identical to today's untinted rendering.
