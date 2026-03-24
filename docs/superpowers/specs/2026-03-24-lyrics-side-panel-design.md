# Lyrics Side Panel Design

## Goal

A lightweight lyrics overlay panel (~340px) that slides in from the right edge of any view, showing time-synced scrolling lyrics without navigating away from the current page.

## Architecture

### New Files
- `src/Noctis/ViewModels/LyricsPanelViewModel.cs` — lightweight ViewModel that observes `PlayerViewModel` for current track/position, fetches lyrics via existing `LrcLibService`/cache pipeline, and drives sync timer for active line tracking.
- `src/Noctis/Views/LyricsPanelView.axaml` + `.axaml.cs` — compact lyrics display panel with header, scrolling synced lines, and close button.

### Modified Files
- `src/Noctis/ViewModels/MainWindowViewModel.cs` — adds `IsLyricsPanelOpen`, `ToggleLyricsPanelCommand`, auto-hide when entering full lyrics view, mutual exclusion with queue popup.
- `src/Noctis/Views/MainWindow.axaml` — hosts `LyricsPanelView` as right-aligned overlay in the content Grid (same pattern as `QueuePopupPanel`).
- `src/Noctis/Views/MainWindow.axaml.cs` — wire panel width animation from code-behind (same pattern as sidebar toggle).
- `src/Noctis/Views/PlaybackBarView.axaml` — add new side-panel toggle button next to existing lyrics button.
- `src/Noctis/Views/PlaybackBarView.axaml.cs` — click handler for new button.

## Panel Layout (top to bottom)

1. **Header:** Mini album art thumbnail (40x40), track title + artist, close (X) button top-right.
2. **Separator line.**
3. **Scrolling lyrics area:** Time-synced lines with active line highlighted (bold, full opacity). Inactive lines dimmed. Auto-scrolls to keep active line centered. Tapping a synced line seeks playback to that timestamp.

## Visual Style

- **Background:** Dark base with subtle tint from album's dominant color. Use `DominantColorExtractor.ExtractDominantColor()` to get the color, then create a subdued vertical gradient — dark tinted top, darker tinted bottom. Not as vivid as the full lyrics view — just a hint of color.
- **Corners:** Rounded (CornerRadius 16), same as Queue popup.
- **Shadow:** Left box-shadow for depth (`-2 0 12 0 #40000000`), mirroring Queue popup.
- **Margin:** 8px from edges (top, right, bottom), same as Queue popup.
- **Text:** White, ~15px for lyrics lines, ~13px for header info. Active line bold + full opacity, inactive ~0.4 opacity.

## Behavior

### Toggle
- New button in playback bar (next to existing lyrics button) toggles `IsLyricsPanelOpen` on `MainWindowViewModel`.
- Button hidden/disabled when `IsLyricsViewActive` (full lyrics view is showing).

### Mutual Exclusion
- Opening lyrics panel closes Queue popup (`Player.IsQueuePopupOpen = false`).
- Opening Queue popup closes lyrics panel (`IsLyricsPanelOpen = false`).

### Auto-hide on Full Lyrics
- When `CurrentView` changes to lyrics view, set `IsLyricsPanelOpen = false`.
- When leaving lyrics view, panel does NOT auto-reopen (user must toggle manually).

### Animation
- Slide-in: 200ms `DoubleTransition` on a wrapper Border's Width (0 -> 340), with `CubicEaseOut` easing and `ClipToBounds="True"`.
- Same proven pattern as sidebar slide animation.

### Persistence
- Panel state is session-scoped only (resets on app restart).
- Panel persists across track changes — lyrics update automatically when track changes.

## Data Flow

1. `LyricsPanelViewModel` is created once (owned by `MainWindowViewModel`), receives `PlayerViewModel` + `ILrcLibService` + `IMetadataService` via constructor.
2. Subscribes to `PlayerViewModel.CurrentTrack` PropertyChanged — on change, loads lyrics (sidecar .lrc -> embedded -> cache -> online search, same priority as `LyricsViewModel`).
3. A `DispatcherTimer` (same pattern as `LyricsViewModel._lyricsSyncTimer`) polls `PlayerViewModel.PositionMs` to update `ActiveLineIndex`.
4. Parsed lyrics stored as `ObservableCollection<LyricLine>` (reuse existing `LyricLine` model).
5. Active line index drives UI highlight + auto-scroll in the view's code-behind.
6. Tapping a synced line calls `PlayerViewModel.SeekCommand` with that line's timestamp.

### Background Tint
- On track change, extract dominant color from album art bitmap (off UI thread via `ThreadPool`).
- Create a subtle vertical gradient: `HslToColor(hue, sat * 0.4, 0.08)` at top, `HslToColor(hue, sat * 0.3, 0.05)` at bottom.
- Apply via `BackgroundBrush` property on `LyricsPanelViewModel`, bound in XAML.
- 400ms opacity fade-in transition on the background Border.

## Playback Bar Button

- New button placed immediately before the existing lyrics button.
- Icon: a panel/split-screen icon (or reuse lyrics icon with a small panel indicator).
- Style: `secondary-btn` class (matches Queue and Lyrics buttons).
- Size: 34x34, same as adjacent buttons.
- Click handler navigates up to `MainWindowViewModel.ToggleLyricsPanelCommand`.
- `IsVisible` bound to `!IsLyricsViewActive` (hidden on full lyrics view).
