# Lyrics Panel Header Redesign

**Date:** 2026-05-18
**Status:** Approved for implementation
**Surface:** `src/Noctis/Views/LyricsPanelView.axaml`

## Goal

Give the side lyrics panel an Apple-Music-mobile-style header (artwork + title/artist + 3-dots menu) so the panel reads as a self-contained "now playing lyrics" surface rather than a bare scrolling text column.

## Reference

The Apple Music iOS lyrics view: small square artwork at top-left, track title and artist stacked next to it, two pill buttons (star + 3-dots) at top-right, lyrics below with a subtle separator. We adopt the same anatomy minus the star button (favorites are already one tap deep inside the 3-dots menu and also live on the playback bar — adding a second control on a 340 px panel is noise).

## Layout

```
┌─────────────────────────────────────┐
│ ┌──┐  Track Title           ⋯       │
│ │ART│  Artist                       │
│ └──┘                                │
│ ─────────────────────────────────── │
│                                     │
│   Past lyric line                   │
│   Past lyric line                   │
│   Active lyric (bold, larger)       │
│   Upcoming lyric                    │
│   ...                               │
└─────────────────────────────────────┘
```

The header is a fixed row above the existing lyrics scroll area, inside the same 340 px panel border in `MainWindow.axaml`. Structurally:

- Outer `Grid` with `RowDefinitions="Auto,*"`
- Row 0: header
- Row 1: existing opacity-masked `ScrollViewer` + `ItemsControl` (unchanged internals)

### Header row

- **Padding:** `16,14,12,12`
- **Columns:** `Auto, *, Auto` — artwork, text stack, 3-dots button
- **Background:** transparent (the panel's tinted background shows through)

#### Artwork

- 44 × 44 `Border`, `CornerRadius=8`, `ClipToBounds=True`
- `Image` source bound to `Player.AlbumArt` with `Stretch=UniformToFill`
- Placeholder when null: ♪ glyph at `Opacity=0.3`, matching the PlaybackBar pattern at `PlaybackBarView.axaml:466-482`

#### Title + artist stack

- `StackPanel Orientation=Vertical`, `VerticalAlignment=Center`, `Margin=12,0,8,0`
- **Title:** `TextBlock`, text bound to `Player.CurrentTrack` via the existing `DisplayTitle` converter, `FontSize=14`, `FontWeight=SemiBold`, `MaxLines=1`, `TextTrimming=CharacterEllipsis`, foreground `LyricsPrimaryFg`
- **Artist:** `TextBlock`, text bound to `Player.CurrentTrack.Artist`, `FontSize=12`, `Opacity=0.6`, `MaxLines=1`, `TextTrimming=CharacterEllipsis`, foreground `LyricsPrimaryFg`

#### Navigate-to-album affordance

The artwork + text stack live inside a single transparent `Button`:

- `Background=Transparent`, `BorderThickness=0`, `Padding=0`, `Cursor=Hand`
- `Command="{Binding Player.ViewCurrentTrackAlbumCommand}"`
- No hover background — Apple-Music-clean. A `:pointerover` style can lift artwork opacity slightly (e.g., `Opacity=0.92`) if desired, but is optional and can be skipped for V1.

#### 3-dots button

- `Button` 32 × 32, `CornerRadius=16`, `Background=#1AFFFFFF`, `BorderThickness=0`, `Cursor=Hand`, `VerticalAlignment=Center`
- Hover: `Background=#26FFFFFF` via a style selector
- Content: existing `MoreDotsIcon` path inside a 16 × 16 Viewbox, fill `LyricsPrimaryFg`
- `Button.Flyout`: a `MenuFlyout` that is a verbatim copy of the one already attached to the full-screen lyrics view's options button (`LyricsView.axaml:636-727`). All commands resolve against `LyricsViewModel` (which exposes `Player`), so no rebinding is required.

### Divider

A 1 px `Rectangle` (or `Border`) directly below the header inside row 0, full panel width:

- `Height=1`, `Fill=White`, `Opacity=0.08`
- Sits at the bottom of the header `Grid` so it visually separates header from lyrics

### Visibility

The entire header (artwork, text, 3-dots, divider) is hidden when no track is loaded, mirroring the existing pattern:

- `IsVisible="{Binding Player.CurrentTrack, Converter={x:Static ObjectConverters.IsNotNull}}"`

When the header is hidden, the existing "No track playing" centered placeholder shows as today.

## Lyrics scroll area

No behavior changes. The existing `Border` with `OpacityMask`, `ScrollViewer`, and `ItemsControl` move into row 1 of the new outer `Grid`. The active-line centering math in `LyricsPanelView.axaml.cs` measures against `PanelScrollViewer.Viewport.Height`, which automatically accounts for the new header taking row 0 — no code-behind changes needed.

The top gradient stop on the opacity mask continues to fade lyrics as they approach the divider; this looks correct because the fade now happens against the divider line instead of the panel's top edge.

## Files touched

- `src/Noctis/Views/LyricsPanelView.axaml` — wrap existing `Panel` content in a `Grid RowDefinitions=Auto,*`; add header row in row 0; move existing visual tree into row 1
- No changes to `LyricsPanelView.axaml.cs`
- No changes to `LyricsViewModel.cs` (all needed bindings already exist: `Player`, `Player.AlbumArt`, `Player.CurrentTrack`, `Player.ViewCurrentTrackAlbumCommand`, `LyricsPrimaryFg`, and the same commands used by `LyricsView`'s menu)

## Out of scope

- Favorite/star button (intentionally omitted — see Reference rationale)
- Changing the panel's outer width (340 px), position, or open/close animation in `MainWindow.axaml`
- Any changes to the full-screen `LyricsView`
- Animations on the header itself

## Verification

After implementation:

- `dotnet build src/Noctis/Noctis.csproj -v minimal` succeeds
- Manual: open the lyrics panel with a track playing — header shows artwork, title, artist, 3-dots
- Manual: 3-dots menu opens and all items work (Play, Shuffle, Favorites, Metadata, Search Lyrics, etc.)
- Manual: clicking artwork or text navigates to the current album
- Manual: stop playback / clear current track — header collapses, "No track playing" placeholder shows
- Manual: long track/artist names ellipsize and do not push the 3-dots button off-screen
