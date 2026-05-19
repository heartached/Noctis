# Lyrics Page Playback Bar — Standard PlaybackBar Adoption

**Date:** 2026-05-18
**Status:** Approved for implementation
**Surface:** `src/Noctis/Views/LyricsView.axaml`, `src/Noctis/Views/PlaybackBarView.axaml`, `src/Noctis/ViewModels/PlayerViewModel.cs`, `src/Noctis/ViewModels/LyricsViewModel.cs`, `src/Noctis/ViewModels/MainWindowViewModel.cs`

## Goal

Replace the lyrics-page-specific player bar with the same `PlaybackBarView` used everywhere else in the app, and relocate the two lyrics-only controls (Synced/Plain toggle, Background Color picker) into the bar's ⋯ menu. End state: the player bar looks and behaves identically across the entire app; the lyrics page becomes less cluttered.

## Visual reference

Before: the lyrics-page bar has shuffle, prev, play, next, repeat, ⋯, queue, lyrics-icon, **Synced/Plain pill**, **color-picker dot**, volume — plus a separate custom seek slider (`LyricsSeekVisual`) above the bar.

After: the lyrics page mounts `PlaybackBarView` verbatim — transport on the left, central album-art + title + artist + seek slider + time labels, ⋯ / queue / lyrics / volume on the right. The custom seek above the bar is gone. The two lyrics-only controls live inside the bar's ⋯ menu.

Title and album art are shown twice on the page (large at the top, thumbnail-size in the bar). This matches Apple Music exactly and is intentional.

## Bar mount

In `LyricsView.axaml`, the entire `<Border>` containing the existing `lyrics-ctrl-btn` row plus its surrounding pill chrome is replaced with:

```xml
<views:PlaybackBarView DataContext="{Binding Player}" />
```

The PlaybackBar's existing positioning (anchored to the bottom of the lyrics page area) is preserved by keeping the same outer container `Grid.Row` / `DockPanel.Dock` placement that the old bar used.

## ⋯ menu additions

The PlaybackBar's `MenuFlyout` in `PlaybackBarView.axaml` gains three new entries appended after the existing items, before the final separator + "Remove from Library":

1. **Separator** (visible only on the lyrics page)
2. **Lyrics Display** parent `MenuItem` with a submenu:
   - **Synced** — bound to `Player.SetLyricsSyncedCommand`. Icon: `MenuItem.Icon` shows a checkmark `PathIcon` (Data `CheckmarkIcon`, already in `Assets/Icons.axaml`) when `Player.IsLyricsSyncedActive == true`, otherwise empty.
   - **Plain** — bound to `Player.SetLyricsPlainCommand`. Icon: checkmark when `Player.IsLyricsPlainActive == true`.
3. **Background Color** — bound to `Player.OpenLyricsBackgroundColorCommand`. Icon: a small palette `PathIcon` (`PaletteIcon` — confirm existence; if not present, add it to `Assets/Icons.axaml` from the existing color-picker button source).

All three new entries (and the leading separator) have `IsVisible="{Binding IsLyricsPageActive}"` so they never appear when the bar is mounted elsewhere.

The "Lyrics Display" submenu is a standard nested `MenuItem` — Avalonia renders it as a side-flyout on hover/click, same as native context menus elsewhere in the app.

## ViewModel wiring

### `PlayerViewModel` additions

New observable properties:

- `bool IsLyricsPageActive` — `false` by default; toggled by `MainWindowViewModel` when navigating to/from the lyrics view.
- `bool IsLyricsSyncedActive` — mirrors the lyrics page's current Synced/Plain selection; updated when the lyrics page registers its state callbacks.
- `bool IsLyricsPlainActive` — mirror of the other side of the toggle.

New relay commands (no-ops when no callbacks are registered):

- `SetLyricsSyncedCommand` → invokes a registered `Action`.
- `SetLyricsPlainCommand` → invokes a registered `Action`.
- `OpenLyricsBackgroundColorCommand` → invokes a registered `Action`.

New registration method:

```csharp
public void SetLyricsPageActions(
    Action setSynced,
    Action setPlain,
    Action openBackgroundColor,
    Func<bool> isSyncedActive,
    Func<bool> isPlainActive,
    out Action refreshState);
```

`refreshState` is returned by the method and is the delegate the lyrics page calls when its Synced/Plain selection changes, so `Player.IsLyricsSyncedActive` / `IsLyricsPlainActive` stay in sync with whatever the UI reflects. The pattern mirrors `SetSearchLyricsAction` / `SetViewArtistAction` used elsewhere in the codebase.

A companion method `ClearLyricsPageActions()` is called by `MainWindowViewModel` when navigating away.

### `LyricsViewModel` integration

When the lyrics view becomes the current view, `MainWindowViewModel` calls:

```csharp
Player.SetLyricsPageActions(
    setSynced: () => lyricsVm.SelectSyncedTabCommand.Execute(null),
    setPlain:  () => lyricsVm.SelectPlainTabCommand.Execute(null),
    openBackgroundColor: () => lyricsVm.OpenBackgroundColorFlyoutCommand.Execute(null),
    isSyncedActive: () => lyricsVm.IsSyncedTabActive,
    isPlainActive: () => lyricsVm.IsPlainTabActive,
    out var refresh);
lyricsVm.RegisterUiStateChanged(refresh);
Player.IsLyricsPageActive = true;
```

The exact command/property names on `LyricsViewModel` are whatever the existing Synced/Plain toggle pill already binds to — the plan-writing step resolves them by reading `LyricsViewModel.cs` and `LyricsView.axaml` lines 770-794.

The color picker today is opened by clicking a button whose `Button.Flyout` hosts the picker contents. Since `MenuItem` from a different control's flyout can't directly trigger another button's flyout to open, the implementation adds a public `OpenBackgroundColorPicker()` method on `LyricsView.axaml.cs` that calls `_colorPickerButton.Flyout?.ShowAt(_colorPickerButton)`, plus a public method on `LyricsViewModel` that raises an event the view subscribes to. `OpenLyricsBackgroundColorCommand` invokes that VM method. This keeps the existing flyout XAML intact and avoids reimplementing the picker.

### `MainWindowViewModel` changes

Wherever the lyrics view is set as `CurrentView`, append `Player.SetLyricsPageActions(...)` plus `Player.IsLyricsPageActive = true`. Wherever the lyrics view is left, call `Player.ClearLyricsPageActions()` and set `Player.IsLyricsPageActive = false`. The natural hook points are the lyrics navigation entry in `Navigate` (or wherever the LyricsViewModel becomes `CurrentView`) and the corresponding teardown in `ClearAllTopBarActions` or `PushCurrentViewToHistory`.

## Removals from `LyricsView.axaml`

- The entire bar `<Border>` and everything inside it: shuffle/prev/play/next/repeat buttons, the existing 3-dots button + its `MenuFlyout` (lines ~636-727), queue toggle, lyrics-icon toggle, Synced/Plain pill (lines ~770-794), color-picker dot + its flyout (lines ~799 onward), volume button + popup.
- The `LyricsSeekVisual` Canvas, its `LyricsSeekTrackBackground`, `LyricsSeekTrackFill`, time labels, and surrounding container (lines ~495 onward).
- Style blocks that become unused: `Button.lyrics-ctrl-btn`, `Slider.lyrics-seek`, `Slider.lyrics-volume`, and the `LyricsSliderThumbRes` / `LyricsSliderFilledRes` / `LyricsSliderUnfilledRes` brushes.
- Resource imports / namespaces that become orphaned.

A grep pass for `lyrics-ctrl-btn`, `lyrics-seek`, `lyrics-volume`, `LyricsSlider`, `LyricsSeekVisual` after edits should return zero hits inside `LyricsView.axaml`.

## Removals from `LyricsView.axaml.cs`

- Seek-drag handlers: `OnSeekPointerPressed`, `OnSeekPointerMoved`, `OnSeekPointerReleased`, plus the `_isSeekDragging` field and the `GetPercentageFromPointer` helper (unless it's used elsewhere — verify).
- Flyout handlers: `OnTrackFlyoutOpened`, `OnTrackFlyoutButtonPointerPressed` (both already empty no-ops, safe to remove).
- Any volume slider handlers tied to the removed volume popup.

## Files touched

- `src/Noctis/Views/LyricsView.axaml` — large net-negative diff (bar block + seek + style/brush blocks removed; one-line bar mount added)
- `src/Noctis/Views/LyricsView.axaml.cs` — handler/field removals
- `src/Noctis/Views/PlaybackBarView.axaml` — three new menu items + leading separator appended to the existing `MenuFlyout`, all gated by `IsLyricsPageActive`
- `src/Noctis/ViewModels/PlayerViewModel.cs` — three observable properties, three relay commands, `SetLyricsPageActions` and `ClearLyricsPageActions` methods
- `src/Noctis/ViewModels/LyricsViewModel.cs` — possibly one new `[RelayCommand]` if the color picker needs a command-driven open; possibly a small "register UI state change callback" hook
- `src/Noctis/ViewModels/MainWindowViewModel.cs` — wire/unwire `Player.SetLyricsPageActions` and `IsLyricsPageActive` around lyrics navigation

## Out of scope

- Changing the lyrics page header / background brushes / album art display at the top of the page.
- Restyling the standard `PlaybackBarView` itself.
- Animation or transition tweaks to the bar's appearance/disappearance on the lyrics page.
- The lyrics side panel (already shipped in a prior change — no overlap with this work).

## Verification

After implementation:

- `dotnet build src/Noctis/Noctis.csproj -v minimal` succeeds with zero errors.
- Manual on lyrics page:
  - Bar is visually identical to the main app's PlaybackBar (chrome, pill radius, transport icons, central seek block, ⋯/queue/lyrics/volume row).
  - Album art thumbnail + title + artist + seek + time labels all render in the bar's center.
  - ⋯ menu shows: all existing standard items, then a separator, then **Lyrics Display** submenu (Synced/Plain with ✓ on active), then **Background Color**.
  - Clicking Synced/Plain in the menu toggles the lyrics display mode; the checkmark moves to the active option.
  - Clicking Background Color opens the existing color picker flyout (popup anchored sensibly).
  - The dedicated Synced/Plain pill, color-picker dot, and separate seek slider above the bar are gone.
- Manual elsewhere in the app:
  - On Home / Albums / Album Detail / etc., the ⋯ menu of the PlaybackBar does **not** show the three lyrics-mode entries (verified by `IsLyricsPageActive` being false).
- No regressions in the side lyrics panel (out of scope but should still work, since it has its own bar binding).
