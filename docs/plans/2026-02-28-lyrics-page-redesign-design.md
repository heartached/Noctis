# Lyrics Page Redesign — Design Document

**Date:** 2026-02-28
**Status:** Approved

## Goal

Redesign the Lyrics page to be a clean, immersive, polished experience with:
- Larger high-quality cover art
- Full track metadata (title, artist, album, explicit badge, lossless/quality badges)
- Real playback controls (same PlayerViewModel, not duplicates)
- Album-art adaptive background colors
- Responsive layout (stacks vertically on narrow windows)
- Island playback bar hidden when lyrics view is active

## Decisions

| Decision | Choice |
|----------|--------|
| Approach | Full XAML rewrite in-place (Approach A) |
| Island bar in lyrics | Hidden |
| Cover art size | Fill available width (~380px) |
| Badge placement | E inline with title; quality badges on separate line below album |
| Responsive behavior | Stack vertically below ~900px |
| Lyrics background | Album-art adaptive (replace preset system) |
| Left panel background | Unified adaptive (darker tint of dominant color) |

## Architecture

### Overall Layout

Two-panel side-by-side (wide) or stacked (narrow):

```
Wide (≥900px):
┌──────────────────┬──────────────────────────────────┐
│   LEFT (420px)   │   RIGHT (fills remaining)        │
│   Cover art      │   Lyrics scroll area             │
│   Track info     │   Adaptive gradient background    │
│   Controls       │   Top/bottom fade overlays        │
└──────────────────┴──────────────────────────────────┘

Narrow (<900px):
┌────────────────────────────────────────────────────┐
│   Cover art (~200px centered)                       │
│   Track info + badges                               │
│   Compact controls row                              │
│   Lyrics scroll area (fills remaining height)       │
└────────────────────────────────────────────────────┘
```

### Left Panel Detail

```
┌─────────────────────┐
│  [←] Back           │
│                     │
│  ┌───────────────┐  │  Cover art: ~380px wide (panel - 40px margins)
│  │               │  │  CornerRadius: 12px, box shadow
│  │  ALBUM ART    │  │  Stretch: UniformToFill
│  │               │  │  BitmapInterpolationMode: HighQuality
│  └───────────────┘  │
│                     │
│  Track Title  [E]   │  22px bold + explicit-badge compact (inline)
│  Artist Name        │  16px, clickable → ViewArtistCommand
│  Album Title        │  14px, clickable → ViewAlbumCommand
│  [Lossless] [FLAC]  │  12px pill badges, visible when IsLossless
│                     │
│  0:21 ════●═══ 2:43 │  Seek slider (Player.PositionFraction)
│                     │
│  ♻  ◄◄  ▶  ►►  🔁  │  Shuffle/Prev/PlayPause/Next/Repeat
│                     │
│  [Lyrics] [Queue]   │  Action pill buttons
│  🔊 ───●─── 🔊     │  Volume slider (Player.Volume)
└─────────────────────┘
```

### Right Panel Detail

- Lyrics ItemsControl with click-to-seek buttons
- Active line: 1.0 opacity, inactive: 0.35 opacity, 0.3s transition
- Font: 24px semibold, line height 36px, text wrapping
- Auto-scroll: 200-400ms cubic ease-in-out animation
- Manual scroll pause: 5s resume timer + "Follow" button
- Top fade (80px) and bottom fade (100px) overlays
- Synced/Unsynced tab switching preserved
- "No lyrics available" + "Search Lyrics" button preserved

### Album-Art Color Extraction

New service: `src/Velour/Services/AlbumArtColorService.cs`

```
AlbumArtColorService (static helper)
├── ExtractDominantColor(Bitmap) → Color
│   ├── Downscale to ~50x50
│   ├── Sample pixels, skip near-black/near-white
│   ├── Center-weighted average
│   └── Return dominant Color
└── GenerateAdaptiveBrushes(Color) → (IBrush left, IBrush right)
    ├── Left: LinearGradient, dark tint (70% black + 30% dominant)
    └── Right: RadialGradient, dominant at ~15-20% with dark edges
```

- Called from LyricsViewModel when Player.AlbumArt changes
- No new NuGet dependencies (Avalonia bitmap pixel access)
- Fallback: default dark gradient when no art available

### Island Bar Hiding

- `MainWindowViewModel`: Add computed `IsLyricsViewActive` bool
- `MainWindow.axaml`: Bind `PlaybackBarView.IsVisible` to `!IsLyricsViewActive`

### Responsive Behavior

- Triggered by `Bounds.Width` change in LyricsView.axaml.cs
- Wide (≥900px): Two-column Grid, left 420px / right *
- Narrow (<900px): Single-column stack, art shrinks to ~200px, controls compact

## Files Changed

| File | Type | Description |
|------|------|-------------|
| `src/Velour/Views/LyricsView.axaml` | Rewrite | Full XAML layout redesign |
| `src/Velour/Views/LyricsView.axaml.cs` | Modify | Update element refs, add responsive handler |
| `src/Velour/ViewModels/LyricsViewModel.cs` | Modify | Wire adaptive colors, repurpose brush properties |
| `src/Velour/Services/AlbumArtColorService.cs` | **New** | Dominant color extraction + gradient generation |
| `src/Velour/ViewModels/MainWindowViewModel.cs` | Modify | Add IsLyricsViewActive property |
| `src/Velour/Views/MainWindow.axaml` | Modify | Hide playback bar when lyrics active |

## Preserved Behaviors (No Regression)

- All playback commands bound to shared PlayerViewModel
- Lyrics sync timer (50ms), 200ms lookahead
- Race guards (_searchGeneration)
- Auto-scroll with manual pause + Follow button
- Click-to-seek on lyric lines
- Sidecar .lrc save priority
- Tab auto-selection (synced vs unsynced)
- Background presets removed, replaced by adaptive system

## Risks

1. **Color extraction performance** — mitigated by downscaling to 50x50 before sampling
2. **Scroll logic breakage** — codebehind refs to ScrollViewer/ItemsControl must match new XAML names
3. **Responsive layout complexity** — keep it simple, just swap Grid column definitions
4. **Dark album art** — fallback to default dark gradient when dominant color is too dark
