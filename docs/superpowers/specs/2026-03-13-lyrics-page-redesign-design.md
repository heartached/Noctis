# Lyrics Page Redesign — Immersive Layout

**Date:** 2026-03-13
**Status:** Approved
**Approach:** Rewrite LyricsView.axaml in-place (Approach A)

## Goal

Redesign the lyrics page to create an immersive, cinematic experience matching a reference image: large cover art on the middle-left, dramatic synced lyrics on the right, album-color background atmosphere, with the existing player bar staying visible at the top.

## Design Decisions

- **Approach A chosen:** Rewrite the AXAML in-place, keep ViewModel and code-behind logic intact. No new files, no new dependencies.
- **Remove all duplicate playback controls** from the lyrics page (seek bar, play/pause, prev/next, shuffle, repeat, volume, queue, options flyout, favorites indicator, back button) — the persistent top player bar covers all of these.

---

## Section 1: Navigation & Player Bar Visibility

**Files:** `MainWindowViewModel.cs`

- Remove the early `return` guard (`if (key == "lyrics") return;`) in `Navigate()`.
- Re-enable `ToggleLyrics()`: add a `string? _preLyricsViewKey` field. When toggling ON (CurrentView != _lyricsVm): capture `_preLyricsViewKey = GetCurrentViewKey()` **before** switching, then `Navigate("lyrics")`. When toggling OFF (CurrentView == _lyricsVm): `Navigate(_preLyricsViewKey ?? "home")`. This is a simple toggle — no navigation stack complexity needed.
- Re-enable `SearchLyricsForTrack(Track track)`: restore it to navigate to lyrics and trigger `_lyricsVm.SearchLyricsForTrack(track)`. This method is wired into context menus across home, songs, favorites, player, genre detail, artist detail, album detail, and playlist views. Without re-enabling it, the "Search Lyrics" context menu action across the entire app stays broken.
- Change `IsPlaybackBarVisible` to `Player.HasContent` (remove the `!IsLyricsViewActive` guard).
- Keep `IsLyricsViewActive` — still used to hide TopBarView. Note: TopBarView and PlaybackBarView share `Grid.Row="0"` in MainWindow.axaml. This is safe because when lyrics is active, TopBarView hides (`IsVisible=false`) so only PlaybackBarView occupies the row. When lyrics is not active, both are visible (existing behavior, unchanged).

## Section 2: Left Panel — Cover Art + Metadata Only

**Files:** `LyricsView.axaml`, `LyricsView.axaml.cs`

**Remove:**
- Seek bar, playback controls (shuffle/prev/play/next/repeat), options flyout, queue button, volume control, favorites indicator, back button.

**Keep:**
- Album art: **420x420** (intentional reduction from current 450x450 — without the playback controls below, a slightly smaller art gives better visual balance with the metadata underneath). High-quality, rounded corners (12px), box shadow `0 12 40 0 #50000000`.
- Song title (22px, Bold, White) + E explicit badge (inline, same line).
- Artist name (16px, accent color `#E74856`, clickable via `ViewArtistCommand`).
- Album name (14px, accent color `#E74856`, clickable via `ViewAlbumCommand`).

**Layout:**
- AXAML column definitions: `Auto, *`. Left panel width managed by code-behind (existing pattern): wide mode sets `LeftPanel.Width = 480`, narrow mode sets `LeftPanel.Width = double.NaN`. This keeps the responsive layout logic in one place rather than splitting between AXAML defaults and code-behind overrides.
- Content vertically centered in the left panel (`VerticalAlignment="Center"`).
- Text centered under the art block with clean spacing.

## Section 3: Right Panel — Immersive Lyrics with Fade Mask

**Files:** `LyricsView.axaml`

**Fade mask:**
- Apply `OpacityMask` on the lyrics container (Border wrapping the ScrollViewer) using a vertical LinearGradientBrush.
- Gradient: top ~30% fades transparent→opaque, middle ~40% fully opaque (focus zone), bottom ~30% fades opaque→transparent.
- Fallback if OpacityMask on ScrollViewer doesn't work cleanly in Avalonia: use a clipping Border wrapper with the mask applied to it instead.

**Synced lyrics text styling:**
- Active line: FontSize 36px, Bold, White, opacity 1.0.
- Inactive lines: FontSize 28px, Bold, White, opacity 0.25.
- `TextWrapping="Wrap"` — allows natural multi-line wraps.
- Glow layer on active lines: keep the existing approach — a duplicate TextBlock behind the main one with `BlurEffect` (`Radius="24"`, up from current 20). `BlurEffect` is already proven to work in this codebase (used in current LyricsView.axaml and CoverFlowView.axaml). Opacity bound to active state (0 when inactive, ~0.45 when active, matching current pattern).
- Keep existing transition durations: opacity 0.5s, font size 0.4s, both CubicEaseInOut (no change from current).

**Unsynced lyrics tab styling:**
- Match the new visual treatment: FontSize 28px, Bold, White, opacity 1.0 (all lines active, no highlighting).
- Same fade mask applies — the mask is on the container, not per-line.
- No font size transitions needed (no active/inactive state for unsynced).

**Empty/loading states:**
- "No lyrics available" + "Search Lyrics" button: centered in the right panel, same as current. Left panel (art + metadata) still shows.
- "Searching for lyrics..." indicator: centered in right panel, left panel still shows.
- "Search failed" / "No lyrics found online": centered in right panel with search retry button, left panel still shows.
- These states are not affected by the fade mask (they use `VerticalAlignment="Center"` and sit in the middle of the visible zone).

**Scroll/follow:**
- Keep existing `ScrollToActiveLine()` — positions active line at ~1/3 viewport height with ease-out-sine animation.
- Keep auto-follow pause on mouse wheel + 5s resume timer.
- "Follow" pill button and "Save Lyrics" pill button: positioned at bottom of the right panel. **Important:** these must be siblings of the opacity-masked container, not children of it — otherwise the fade mask would make them transparent. Structure: outer Grid contains (1) the masked Border with ScrollViewer and (2) the pill buttons as separate overlaid children with `ZIndex="2"`.

**Spacing:**
- Vertical margin between lyric lines: 10px (up from 6px).
- Padding: 32px left, 48px right.

## Section 4: Background — Immersive Album-Color Atmosphere

**Files:** No changes needed.

- Keep `FullBackgroundBrush` as the main Grid background (unified horizontal gradient from `DominantColorExtractor`).
- Both panels stay `Background="Transparent"` so the unified gradient shows through.
- No changes to `DominantColorExtractor.cs` or brush generation.
- `LeftPanelBrush` and `LyricsBackgroundBrush` properties in LyricsViewModel become unused by the view. The `UpdateAdaptiveBackground()` method still computes them on every track change — this is lightweight and acceptable. Accepted as dead code, no ViewModel changes in this task. Can be cleaned up in a follow-up if desired.

## Section 5: Code-Behind Changes

**Files:** `LyricsView.axaml.cs`

**Remove:**
- `OnLyricsVolumeContainerEntered/Exited`, `OnLyricsVolumeSliderPropertyChanged`, `UpdateLyricsVolumePercentagePosition`.
- `OnQueueButtonClick`.

**Update responsive layout:**
- Breakpoint: keep `NarrowBreakpoint = 900` (unchanged).
- Narrow mode: album art shrinks to 200x200, left panel fills width (`Width = double.NaN`), padding `Thickness(30, 20)` (symmetric). Panels stack vertically (row definitions: `Auto, *`). Left panel has `MaxHeight = 320` to prevent it from consuming too much vertical space on short windows — ensures lyrics always get meaningful space. Metadata text sizes unchanged. Fade mask still applies to lyrics in narrow mode.
- Wide mode: `LeftPanel.Width = 480`, album art 420x420, padding `Thickness(40, 30)` (symmetric: 40px horizontal, 30px vertical). Side-by-side column layout (`Auto, *`).
- Remove references to deleted elements: `LyricsVolumeSlider`, `LyricsVolumePercentage`, `LyricsVolumeContainer`, `SeekSlider`.

**Keep intact:**
- `ScrollToActiveLine`, `AnimateScroll`, `OnViewModelPropertyChanged`, `PauseAutoFollow`, `OnLyricsPointerWheelChanged`, `CancelScrollAnimation`, `CancelAutoFollowResumeTimer`.

---

## Files Changed

| File | Change | Size |
|---|---|---|
| `src/Noctis/Views/LyricsView.axaml` | Rewrite | ~870 lines → ~250-300 lines |
| `src/Noctis/Views/LyricsView.axaml.cs` | Edit | Remove volume/queue handlers, update responsive layout |
| `src/Noctis/ViewModels/MainWindowViewModel.cs` | Edit | ~5 small edits: re-enable navigation, ToggleLyrics, SearchLyricsForTrack, keep player bar |

## No Changes To

- `LyricsViewModel.cs` — all logic preserved. `LeftPanelBrush`/`LyricsBackgroundBrush` become dead code (accepted, follow-up cleanup).
- `DominantColorExtractor.cs` — brush generation preserved.
- `PlaybackBarView.axaml/.cs` — player bar untouched.
- `MainWindow.axaml` — layout structure untouched.

## Risks

- `OpacityMask` on ScrollViewer in Avalonia — needs testing; fallback is a wrapping Border with the mask.
- Larger font size transitions (28→36 vs old 21→32) with `TextWrapping="Wrap"` may cause visible layout reflow in the ItemsControl as lines re-wrap during animation. If this is visually jarring, mitigate by removing the FontSize transition and snapping instantly, keeping only the opacity transition for smoothness.
- Responsive narrow-mode breakpoint needs manual testing at various window sizes.
- Glow effect: `BlurEffect` with radius 24 (up from 20) may render slightly differently — visual quality check needed but low risk since it's already in use.
