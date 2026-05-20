# Lyrics Capture Mode — Phase 1 Design

Date: 2026-05-20
Status: Approved

## Overview

A full-screen, distraction-free "Capture Mode" for recording lyric content with
OBS (targeting 1920×1080 / 9:16 crop for Reels/TikTok). Phase 1 delivers the
capture view, real-time lyric display, and a minimal floating control panel.

This is **Phase 1 of 2**. Capture presets, animated full-screen artwork, and the
background color picker are deferred to a separate Phase 2 spec.

## Scope

### In scope (Phase 1)

- New full-screen in-app capture view (a `CurrentView` page, not a window).
- Real-time lyric display: previous / current / next 2–3 lines.
- Static album-tinted background.
- Top-left 120×120 album artwork with song/artist metadata.
- Minimal floating control panel: play/pause, font-size slider, close.
- Auto-hide control panel (5s inactivity); `Esc` to exit.
- Navigation entry point: camera-icon button on the lyrics page.

### Out of scope (deferred to Phase 2)

- `CapturePreset` model + `CapturePresetService` + `capture_presets.json`.
- `SettingsViewModel` preset collection and load/save/delete commands.
- Animated full-screen artwork behind lyrics + animated-artwork toggle.
- Background color picker, tint-intensity slider, custom accent color.
- Preset manager dropdown.

## Design decisions

These were settled during brainstorming:

1. **Hosting:** In-app full-screen view (a `CurrentView` page), not a separate
   borderless window. OBS captures the maximized app window.
2. **Lyric sync source:** Reuse the existing `LyricsViewModel` sync engine. The
   capture VM does not run its own timer.
3. **Animated artwork (Phase 2):** Both a static top-left thumbnail AND a
   full-screen animated background when the toggle is on.
4. **Phasing:** Phase 1 = core capture view + lyric display + basic control
   panel. Phase 2 = presets, animated artwork, color picker.

## Architecture

### LyricsCaptureViewModel (new)

- A `ViewModelBase` page, constructed once in `MainWindowViewModel`.
- Holds references to `Player` (`PlayerViewModel`) and the existing
  `LyricsViewModel` instance.
- Does **not** run its own sync timer. Subscribes to
  `LyricsViewModel.PropertyChanged` for `ActiveLineIndex` and derives:
  - `PreviousLine` — line at `ActiveLineIndex - 1` (nullable).
  - `CurrentLine` — line at `ActiveLineIndex` (nullable).
  - `UpcomingLines` — next 2–3 lines after `ActiveLineIndex`.
  - Source collection: `LyricsViewModel.LyricLines`.
- Exposes `FontSize` (double, 48–96, snaps to 60/72/80 presets).
- Exposes a play/pause command relaying to `Player`, plus `IsPlaying` state.
- Exposes `CloseCommand` that returns to the previous view.
- Background brush derived from `DominantColorExtractor` on the current track's
  artwork, at a fixed default tint intensity (intensity slider is Phase 2).

### LyricsCaptureView (new)

- `Views/LyricsCaptureView.axaml` + `.axaml.cs`.
- Fills the whole window (chrome suppressed — see below).
- Layout:
  - Top-left: 120×120 album artwork, `CornerRadius=12`; below it the song
    title (12px, SemiBold, ~60% opacity) and artist (11px, ~50% opacity).
  - Center: vertical lyric stack — previous line above, current line centered,
    upcoming lines below.
  - Bottom-center: floating pill control panel.
- Code-behind owns: the auto-hide `DispatcherTimer`, pointer-move handler to
  reveal the panel, and the `Esc` key handler.

### Chrome suppression

`MainWindow.axaml` root is a `Panel` with three layers: the content `DockPanel`
(`RootPanel`, `Margin="76,0,0,0"`), the overlaid `PlaybackBarView` (gated by
`IsPlaybackBarMounted` / `PlaybackBarOpacity`), and the overlaid
`SidebarWrapper` border. TopBar already hides when `IsLyricsViewActive`.

Add `IsCaptureModeActive` (bool, `[ObservableProperty]`) to
`MainWindowViewModel`. When true:

- `SidebarWrapper.IsVisible` → false.
- `IsPlaybackBarMounted` → false (playback continues; only the bar is hidden).
- `RootPanel` left margin → 0 (from 76).
- TopBar hidden (extend the existing lyrics-active condition).

Playback state and audio are unaffected — only visual chrome is suppressed.

### Navigation

- `OpenLyricsCaptureCommand` on `MainWindowViewModel`: pushes the current view
  to navigation history, sets `CurrentView = _captureVm`, sets
  `IsCaptureModeActive = true`.
- Closing (control-panel X, `Esc`, or `CloseCommand`): sets
  `IsCaptureModeActive = false` and navigates back to the previous view
  (the lyrics page) with playback preserved.
- Entry point: a camera-icon button on the lyrics page (top-right area),
  disabled when `LyricsViewModel.HasSyncedLyricsAvailable` is false.
- VM→View mapping registered via the existing view-locator mechanism.

## Lyric display

- Current line: 72px default (driven by `FontSize`), SemiBold, white, centered
  horizontally, vertically centered (or slightly above center).
- Upcoming lines: 48px, ~30% opacity gray, 24px spacing, below current.
- Previous line: 36px, ~15% opacity gray, above current.
- All lines: centered; wrap or truncate within a safe area.
- Text shadow on all lyric text: offset 0,2 / 8px blur / `#000000` at 60%
  opacity, for readability over any background.
- Lines update immediately when `ActiveLineIndex` changes — no debounce.
- 200ms easing transitions on font-size changes.

## Background

- Solid tint derived from `DominantColorExtractor` applied to the current
  track's artwork, at a fixed default intensity.
- Falls back to a default dark background if no artwork / extraction fails.
- The tunable intensity slider and custom color picker are Phase 2.

## Control panel

- Pill-shaped container: `#1A1A1A` at 80% opacity, 16px padding, rounded.
- Position: bottom-center, 16px from the bottom edge.
- Phase 1 controls (left to right):
  1. Play/Pause — icon button reflecting `IsPlaying`.
  2. Font-size slider — range 48–96, snaps to 60/72/80 presets.
  3. Close (X) — exits capture mode.
- Auto-hide behavior:
  - Hidden by default.
  - Appears on pointer movement over the view.
  - Hides after 5s of pointer inactivity.
  - 200ms fade in/out.
- Controls disable if no synced lyrics are available.

## Exit paths

- Control-panel Close (X) button.
- `Esc` key.
- All exits set `IsCaptureModeActive = false`, restore chrome, return to the
  lyrics page, and preserve playback state.

## Files

### New

- `src/Noctis/Views/LyricsCaptureView.axaml`
- `src/Noctis/Views/LyricsCaptureView.axaml.cs`
- `src/Noctis/ViewModels/LyricsCaptureViewModel.cs`

### Modified

- `src/Noctis/ViewModels/MainWindowViewModel.cs` — `IsCaptureModeActive` flag,
  `OpenLyricsCaptureCommand`, capture VM construction and wiring.
- `src/Noctis/Views/MainWindow.axaml` — chrome gates for sidebar, playback bar,
  root margin, and TopBar.
- `src/Noctis/Views/LyricsView.axaml` — camera-icon entry button.
- View-locator registration for `LyricsCaptureViewModel` → `LyricsCaptureView`.

## Testing / verification

- Build: `dotnet build src/Noctis/Noctis.csproj -v minimal` (project was
  renamed from `Velour`; confirm csproj path during implementation).
- Manual verification on a 1920×1080 display:
  - Open a track with synced lyrics, click the camera button.
  - Full-screen view appears; sidebar / playback bar / TopBar hidden.
  - Lyrics update in real time as playback progresses.
  - Control panel reveals on pointer move, hides after 5s.
  - Font-size slider adjusts 48–96 and snaps to presets.
  - `Esc` and the X button both exit; playback continues uninterrupted.
  - Camera button is disabled for tracks without synced lyrics.

## Risks / unknowns

- The `.claude/rules` files still reference the old project name `Velour`;
  actual project directory is `src/Noctis`. Confirm the `.csproj` filename
  during implementation.
- Suppressing `IsPlaybackBarMounted` must not interrupt audio — verify the flag
  only affects the visual bar, not the player.
