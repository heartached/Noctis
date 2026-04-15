# Collapsible Sidebar — Design Spec

## Summary

Redesign the sidebar to have two states: collapsed (60px, icon-only) and expanded (220px, icons + text). Collapsed is the default. Hover expands, mouse-leave collapses. Smooth 200ms CubicEaseOut transition. Remove the lyrics-view sidebar toggle button entirely.

## Motivation

A collapsible sidebar gives the main content area more space by default while keeping navigation accessible. The reference design (Const Genius screenshot) shows the pattern: a narrow icon strip that expands on hover. This also eliminates the need for the separate lyrics-view sidebar toggle.

## Design

### Approach: Clip-based expand/collapse

The `SidebarWrapper` in MainWindow controls the visible width. The `SidebarView` itself always renders at 220px internally. When collapsed, the wrapper clips to 60px — only the left portion (centered icons) is visible. When expanded, the full 220px is shown.

This avoids dual templates, responsive layouts, or ViewModel state for expand/collapse. The existing `DoubleTransition` on `SidebarWrapper.Width` handles animation.

### Hit-test safety

`ClipToBounds="True"` clips rendering but not hit-testing. The SidebarView at 220px would intercept clicks meant for main content behind it. Fix: use `IsPointerOver` on the `SidebarWrapper` to drive expand/collapse instead of `PointerEntered`/`PointerExited` events (which can fire spuriously when entering child controls). The `IsPointerOver` property is reliable in Avalonia — it stays true while any descendant has the pointer, and the wrapper's hit-test area matches its rendered width since `ClipToBounds` is set.

### MainWindow.axaml changes

1. **SidebarWrapper**: Change default `Width` from `220` to `60`.
2. **Remove SidebarToggleBtn**: Delete the `<Button x:Name="SidebarToggleBtn" ...>` block entirely.
3. **Remove SidebarToggleBtn style**: Delete the `Button#SidebarToggleBtn:pointerover` style.

### MainWindow.axaml.cs changes

1. **Add hover wiring**: Watch `SidebarWrapper.IsPointerOver` via `PropertyChanged` to set width 220 (over) or 60 (out).
2. **Remove sidebar toggle wiring**: Delete the `IsSidebarHidden` property change handler that toggles width 0/220.
3. **Remove `_sidebarToggleBtn` field** and its `FindControl` call.

### MainWindowViewModel.cs changes

1. **Remove**: `_lyricsSidebarPref` field, `IsSidebarHidden` observable property, `ToggleSidebar()` relay command.
2. **Remove sidebar logic in `OnCurrentViewChanged`**: Delete only the `IsSidebarHidden` assignments (lines 466-468 and 471-474). Preserve the `IsLyricsPanelOpen = false` line (line 469) since it's unrelated — it closes the lyrics side panel when entering lyrics full-screen view.
3. Keep `IsLyricsViewActive` (still used elsewhere).

### LyricsView.axaml.cs changes

1. **Remove immersive mode subscription**: Delete the `IsSidebarHidden` property change handler and `ApplyImmersiveMode` calls that subscribed to sidebar state.
2. **Apply immersive mode always**: Since the sidebar is now always collapsed (60px) in lyrics view, apply the "immersive" sizes (620px art, 680px content, 1.1 scale) as the default lyrics layout. Remove the `ApplyImmersiveMode` method and `_isImmersiveMode` field — the lyrics view always has the extra space.

### SidebarView.axaml layout changes

All items must be visually centered within the first 60px when clipped. Current ListBoxItem padding is `14,10` (from `sidebar` class in Styles.axaml).

1. **Logo section**: Change `Margin="18,16,18,14"` to `Margin="16,16,18,14"` — logo (28px) at x=16, center at 30px = center of 60px. "Noctis" text clips naturally.

2. **Nav items (ListBox)**: Keep `Margin="8,0,8,4"`. With ListBoxItem padding 14, the 20px icon starts at 22px (center at 32px vs 30px ideal). Close enough — no change needed. Add `ToolTip.Tip="{Binding Label}"` on the item template root.

3. **Divider**: Change from `Margin="16,8,16,8"` to `Margin="8,8,8,8"` so it's visible in collapsed state.

4. **Favorites**: Reduce ListBox left margin to `Margin="4,0,8,4"` so the 36px thumbnail centers better in 60px (at x=18, center at 36px vs 30px — still offset but more balanced). Add `ToolTip.Tip="{Binding Label}"`.

5. **PLAYLISTS header**: Clips away in collapsed mode. Acceptable.

6. **Playlist items**: Same ListBox margin as Favorites (`Margin="4,0,8,4"`). Add `ToolTip.Tip="{Binding Label}"`. Text clips.

7. **Show All button**: Clips away in collapsed mode. Acceptable.

### Tooltip behavior

- `ToolTip.Tip="{Binding Label}"` on nav items, favorites, and playlist items.
- Standard Avalonia tooltip behavior — appears on hover.
- Useful in collapsed mode; harmless when expanded.

### What gets removed

| Item | File |
|------|------|
| `SidebarToggleBtn` button | MainWindow.axaml |
| `Button#SidebarToggleBtn:pointerover` style | MainWindow.axaml |
| `_sidebarToggleBtn` field + FindControl | MainWindow.axaml.cs |
| `IsSidebarHidden` property handler wiring | MainWindow.axaml.cs |
| `_lyricsSidebarPref` field | MainWindowViewModel.cs |
| `IsSidebarHidden` observable property | MainWindowViewModel.cs |
| `ToggleSidebar()` relay command | MainWindowViewModel.cs |
| Sidebar show/hide logic in `OnCurrentViewChanged` | MainWindowViewModel.cs |
| `ApplyImmersiveMode` method + subscription | LyricsView.axaml.cs |
| `_isImmersiveMode` + `_mainVmImmersiveHandler` fields | LyricsView.axaml.cs |

## Files Changed

- `src/Noctis/Views/MainWindow.axaml`
- `src/Noctis/Views/MainWindow.axaml.cs`
- `src/Noctis/ViewModels/MainWindowViewModel.cs`
- `src/Noctis/Views/SidebarView.axaml`
- `src/Noctis/Views/LyricsView.axaml.cs`

## Edge Cases

- **Rapid hover in/out**: The transition handles this gracefully — Avalonia interrupts in-progress transitions and animates from the current value.
- **Lyrics view**: The collapsed sidebar (60px) is always present, giving lyrics the extra space previously provided by hiding the sidebar. Immersive sizes are now the default.
- **Playlist selection in collapsed mode**: Clicking a 36px thumbnail in collapsed mode triggers navigation. The ListBox item is rendered within the 60px clip area.
- **Scrolling in collapsed mode**: The ScrollViewer still works for scrolling the icon strip.
- **PointerExited into child controls**: Using `IsPointerOver` instead of `PointerEntered`/`PointerExited` avoids false collapses when the pointer moves between child controls.
- **DebugPanelView margin**: Has hardcoded `Margin="228,8,0,8"`. Minor cosmetic issue at 60px — acceptable since debug panel is dev-only. Can be fixed separately if needed.
