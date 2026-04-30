# Now-Playing EQ Visualizer in Track Rows

**Date:** 2026-04-30
**Status:** Design

## Problem

Users cannot tell at a glance which track in a list is currently playing. Apple Music and Spotify show a small animated EQ-bars indicator in the leading column of the playing row, replacing the track number. Noctis should do the same.

## Goal

When a track is the currently playing track:

- Its leading "track number" cell shows an animated 4-bar EQ visualizer instead of the number.
- The bars animate while playback is playing.
- The bars freeze (in place, not reset) when playback is paused.
- The bars disappear and the track number returns when the track is no longer the current track (stopped, or another track became current).

The behavior must be consistent across every track list in the app.

## Non-Goals

- The EQ is not a real audio analyzer. Bars animate on fixed timing curves, not on FFT data.
- No per-album dominant-color theming. The visualizer uses the app accent brush.
- No setting to disable, customize bar count, or change colors. Single appearance.

## Design

### New control: `EqVisualizer`

Location: `src/Noctis/Controls/EqVisualizer.cs` plus `EqVisualizer.axaml` (templated control or `UserControl`; templated control preferred so it can be retemplated and styled cleanly).

Public API:

| Property | Type | Default | Notes |
|---|---|---|---|
| `IsAnimating` | `bool` | `false` | When true, animations run. When false, animations stop and bars freeze at their last `ScaleY`. |
| `Foreground` | `IBrush` | inherited | Bar color. Defaults to `{DynamicResource SystemControlHighlightAccentBrush}` at usage sites. |

Visual structure:

- 4 vertical bars laid out in a `StackPanel Orientation="Horizontal"` with even spacing.
- Each bar: `Rectangle` with `RadiusX="2" RadiusY="2"`, `Width=2`, `Height=` full control height, `VerticalAlignment="Bottom"`.
- Each bar has a `ScaleTransform` with `RenderTransformOrigin="50%,100%"` so scaling collapses toward the bottom.
- Default control size: 16w × 14h. Sized by parent.

Animation:

- 4 Avalonia `Animation` instances, one per bar, all with:
  - `IterationCount="Infinite"`
  - `PlaybackDirection="Alternate"`
  - Easing `CubicEaseInOut`
  - Keyframes animating `ScaleTransform.ScaleY` from ~0.25 (cue 0%) to 1.0 (cue 100%).
- Per-bar durations to break lockstep: 700 ms, 950 ms, 1100 ms, 850 ms.
- Per-bar initial delays (via differing starting `ScaleY` values rather than `Delay`, since Avalonia `Animation` doesn't expose a clean way to phase-shift looping animations) — set each bar's initial `ScaleY` to a different value (0.4, 0.9, 0.55, 0.75) so when animations start, bars are already out of phase.

Pause-as-freeze behavior:

- Stopping an Avalonia `Animation` reverts the animated property to its base value, which would snap bars back to their initial state — not what we want.
- To freeze: when `IsAnimating` flips to `false`, read each bar's current rendered `ScaleY` and set it as the base value, then stop the animation. When `IsAnimating` flips back to `true`, restart the animations from their declared start values (the small visual jump on resume is acceptable and matches Apple Music behavior closely).
- Implemented in code-behind via an `IsAnimating` change handler.

### Row integration

Each track-list row currently shows a track number `TextBlock` in column 0 (or an index for non-album lists). Replace that single element with a `Panel` (overlay) containing two children:

```xml
<Panel Grid.Column="0" Width="..." HorizontalAlignment="Right">
    <TextBlock Text="{Binding TrackNumber}"
               IsVisible="{Binding !IsCurrentRow}"
               ... existing styling ... />
    <ctrl:EqVisualizer Width="16" Height="14"
                       IsVisible="{Binding IsCurrentRow}"
                       IsAnimating="{Binding $parent[UserControl].DataContext.Player.IsPlaying}"
                       Foreground="{DynamicResource SystemControlHighlightAccentBrush}" />
</Panel>
```

`IsCurrentRow` is computed in XAML, not on the model, to avoid mutating shared `Track` instances. Two options considered:

- **A: MultiBinding + converter on each row.** Binds `Track.Id` and `Player.CurrentTrack.Id` and returns bool. Simple, but every row evaluates on every `CurrentTrack` change — fine for the visible viewport but redundant.
- **B: Single `IsCurrentRow` property on a row VM.** Requires per-row VMs or wrapping `Track` in an item VM, which several views don't currently do.

**Chosen: A.** Converter `TrackIsCurrentConverter : IMultiValueConverter`. Returns true when `values[0]` (track id) equals `values[1]` (current track id) and both are non-null. Cheap, no model surgery.

### Player state surface

`PlayerViewModel` already exposes `CurrentTrack` and a state. Confirm an `IsPlaying` boolean property exists; if not, add one derived from the playback state enum, with `INotifyPropertyChanged` raised whenever state changes. (`PlayerViewModel.IsPlaying` is referenced as the binding target above; verification that this exists is part of the implementation plan.)

### Integration points

Track lists to update (each shows a leading number/index column):

1. `src/Noctis/Views/AlbumDetailView.axaml` — `TextBlock Text="{Binding TrackNumber}"` at the row's column 0.
2. `src/Noctis/Views/PlaylistView.axaml` — leading index column.
3. `src/Noctis/Views/LibrarySongsView.axaml` — leading index column.
4. `src/Noctis/Views/FavoritesView.axaml` — leading index column.
5. `src/Noctis/Views/QueueView.axaml` — leading index column.

Exact line numbers and the precise binding shape for each (since some lists use a row index from `ItemsSource` rather than `Track.TrackNumber`) will be enumerated in the implementation plan.

### Theming

- Color: `{DynamicResource SystemControlHighlightAccentBrush}` (existing accent — `#E74856` in Dark theme, retheable per palette).
- Size: 16×14 in track rows. Right-aligned in the column to match where the number sat.

### Performance

- 4 active animations run only on the single currently-playing row at a time. Other rows render a static `TextBlock` and an invisible `EqVisualizer` (collapsed via `IsVisible=false`, so its template isn't realized when wrapped in a Panel — but to be safe, set the visualizer's child elements to skip animation start when `IsVisible=false`; restart on `IsVisible=true`).
- No measurable cost vs. current track-list rendering.

## Testing

Manual verification (no unit tests for visual control):

- Start playback on a track in AlbumDetailView. Confirm number is replaced by animated bars on the playing row only.
- Pause. Confirm bars freeze in place, not snap back.
- Resume. Confirm bars resume animating.
- Skip to next track. Confirm the previous row reverts to its number and the new row shows bars.
- Repeat in PlaylistView, LibrarySongsView, FavoritesView, QueueView.
- Switch theme. Confirm bar color follows the accent.

## Risks & Unknowns

- **Avalonia animation freeze semantics:** the "read current ScaleY, set as base, stop animation" approach may have subtle render-tick timing issues. If freezing snaps visibly, fall back to: leave animation running but animate `ScaleY` from `current` to `current` (zero-amplitude) — visually identical to frozen.
- **Per-row VM absence:** if any of the five track-list views uses raw `Track` items without an index, the leading column may not show a number today; in that case the EQ still shows when current, and nothing is shown when not — verify per view.
- **`PlayerViewModel.IsPlaying` may not exist as a bindable bool.** Adding it is in scope.
