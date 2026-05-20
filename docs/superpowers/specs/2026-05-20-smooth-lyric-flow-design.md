# Smooth Lyric Flow — Design

Date: 2026-05-20
Status: Approved

## Overview

Bring smooth, gliding lyric motion to two surfaces that currently lack it (or
do it less smoothly than the main lyrics page):

1. **Lyrics panel** (`LyricsPanelView`) — already animates its scroll, but with
   an ease-out-sine curve. Upgrade it to the same cubic ease-in-out
   (`SmootherStep`) the full lyrics page uses, so it glides in *and* out.
2. **Capture Mode** (`LyricsCaptureView`) — currently swaps three bound text
   properties instantly. Rebuild it as a scrolling list so lyrics physically
   glide upward as playback advances, matching the lyrics page.

The reference behavior is the full lyrics page (`LyricsView`): when the active
line changes, it animates `ScrollViewer.Offset` to a target offset with a
`SmootherStep` cubic ease-in-out over a distance-scaled duration.

## Decisions (settled during brainstorming)

- **Lyrics panel:** match the full-page easing. No other panel changes.
- **Capture Mode:** rebuild as a scrolling list (ItemsControl + ScrollViewer +
  `SmootherStep` offset animation) — the only approach that reproduces the
  page's true positional glide. Cross-fade and transform-slide were rejected.
- **Shared easing:** `SmootherStep` will be extracted once into a shared static
  helper and reused by all three lyric code-behinds.

## Part 1 — Lyrics panel easing

`LyricsPanelView.axaml.cs` `AnimateScroll` currently eases with
`Math.Sin(t * Math.PI / 2.0)` (ease-out-sine). Replace that single easing
expression with a call to the shared `SmootherStep(t)` helper (see Part 3).

No other changes to the panel: duration calculation
(`Min(600, Max(300, diff * 0.7))`), the centered target offset
(`childTop - viewportHeight/2 + childHeight/2`), the 16ms timer, and
`CancelScrollAnimation` all stay exactly as they are.

## Part 2 — Capture Mode rebuilt as a scrolling list

### LyricsCaptureViewModel

Remove the per-line projection:
- Delete `PreviousLine`, `CurrentLine`, `UpcomingLines`, and `RefreshLines()`.
- The `OnLyricsPropertyChanged` handler no longer calls `RefreshLines()`.

Add list-oriented members:
- `LyricLines` — expose the existing `LyricsViewModel.LyricLines` collection
  directly (reused, not copied). Its per-line `LineOpacity` and `IsActive`
  observable properties remain the source of truth for fade state.
- `ActiveLineIndex` (int) — mirrors `LyricsViewModel.ActiveLineIndex`; updated
  in `OnLyricsPropertyChanged` when that property changes, and raises
  `PropertyChanged` so the view can drive the scroll animation.

Unchanged: `FontSize` (48–96, snaps to 60/72/80), `IsPlaying`, `AlbumArt`,
`TrackTitle`, `TrackArtist`, `BackgroundBrush`, `TogglePlayPauseCommand`,
`CloseCommand`, `CloseRequested`.

### LyricsCaptureView.axaml

Replace the centered three-line `StackPanel` (the `PreviousLine` /
`CurrentLine` / `UpcomingLines` block) with:
- A `ScrollViewer` filling the center, `VerticalScrollBarVisibility="Hidden"`
  and `HorizontalScrollBarVisibility="Disabled"`.
- Inside it, an `ItemsControl` bound to `LyricLines`.
- Each item: a centered, `TextWrapping="Wrap"` `TextBlock` bound to
  `LyricLine.Text`, with the existing drop shadow (offset 0,2 / blur 8 / black
  60%), `Foreground="White"`, and `Opacity` bound to `LyricLine.LineOpacity`.
- Font size: the **active** line renders at the capture VM's `FontSize`
  (slider, 48–96); inactive lines render at a fixed smaller size (48px). This
  is selected per item based on `LyricLine.IsActive` — the implementation plan
  chooses the exact binding mechanism (e.g. a `MultiBinding` of `IsActive` +
  the VM `FontSize`, or an `IsActive`-keyed style). The 0.2s `FontSize`
  `DoubleTransition` on the active line is preserved.

The top-left metadata block and the bottom floating control panel are
unchanged — they remain sibling overlay children of the root `Grid`, layered
above the `ScrollViewer`.

### LyricsCaptureView.axaml.cs

Add lyric-follow scroll animation, ported from `LyricsView`:
- Subscribe to the capture VM's `PropertyChanged`; when `ActiveLineIndex`
  changes, call a `ScrollToActiveLine(index)` method.
- `ScrollToActiveLine` computes a target offset that places the active line
  vertically centered in the `ScrollViewer` viewport
  (`childTop - viewportHeight/2 + childHeight/2`, clamped to ≥ 0), then runs a
  `SmootherStep`-eased `AnimateScroll` from the current offset over a
  distance-scaled duration (same shape as `LyricsView`/`LyricsPanelView`).
- Guard against re-scrolling to the same index (`_lastScrolledIndex`) and
  cancel any in-flight animation before starting a new one.
- The existing auto-hide timer, pointer-move handler, and `Esc` handling are
  untouched.

Capture Mode and the lyrics page render the *same* `LyricLine` instances; this
is safe (the lines are observable data objects) and keeps opacity/active state
consistent across views.

## Part 3 — Shared SmootherStep helper

`SmootherStep` (the cubic ease-in-out `t*t*t*(t*(t*6-15)+10)`, clamped to
[0,1]) is currently a private static method in `LyricsView.axaml.cs`. Extract
it once into a shared static helper in `Noctis.Helpers` (e.g. a static
`Easing` class with a `SmootherStep(double t)` method). Update all three
lyric code-behinds to call it:
- `LyricsView.axaml.cs` — replace its private `SmootherStep` with the shared
  call.
- `LyricsPanelView.axaml.cs` — use it in `AnimateScroll` (Part 1).
- `LyricsCaptureView.axaml.cs` — use it in the new `AnimateScroll` (Part 2).

The existing `Helpers/SmoothScrollAnimator.cs` is a separate, general-purpose
momentum/scroll animator (ease-out-cubic) used elsewhere; it is **not** changed
and is unrelated to this work.

## Files

### Modified
- `src/Noctis/ViewModels/LyricsCaptureViewModel.cs` — list-oriented members.
- `src/Noctis/Views/LyricsCaptureView.axaml` — ScrollViewer + ItemsControl.
- `src/Noctis/Views/LyricsCaptureView.axaml.cs` — scroll-follow animation.
- `src/Noctis/Views/LyricsPanelView.axaml.cs` — easing swap.
- `src/Noctis/Views/LyricsView.axaml.cs` — use the shared `SmootherStep`.

### New
- A shared easing helper in `src/Noctis/Helpers/` (e.g. `Easing.cs`).

## Testing / verification

- Build: `dotnet build src/Noctis/Noctis.csproj -v minimal`.
- Manual verification with a synced-lyrics track:
  - Lyrics panel: active line scroll glides smoothly in and out (no abrupt
    start), still centers the active line.
  - Capture Mode: lyric lines physically slide upward as playback advances;
    the active line glides to vertical center; opacity gradient fades older
    and upcoming lines; the font-size slider still scales the active line.
  - Control panel (play/pause, font slider, close), auto-hide, and `Esc` still
    work in Capture Mode.
  - Full lyrics page behaves exactly as before (no regression from the
    `SmootherStep` extraction).

## Risks / unknowns

- The exact per-item font-size binding mechanism in the Capture Mode
  `DataTemplate` (active line = slider value, inactive = fixed) is left to the
  implementation plan; both a `MultiBinding` converter and an `IsActive`-keyed
  style are viable.
- Capture Mode previously had no `ScrollViewer`; the new one must not intercept
  the pointer-move events the auto-hide control panel relies on — verify the
  pointer-move handler still fires (it is attached at the `UserControl` root,
  so it should bubble up regardless).
