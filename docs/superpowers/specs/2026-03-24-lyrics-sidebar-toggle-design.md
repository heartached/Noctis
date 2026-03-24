# Lyrics Sidebar Toggle

## Summary

Add a toggle button visible only in the lyrics view that slides the sidebar out/in, giving the user a full-width immersive lyrics experience. Preference is remembered for the session.

## Behavior

- **Toggle button** appears in the top-left corner of the content area only when `IsLyricsViewActive` is true.
- Clicking it hides the sidebar with a slide-out animation (width 220 → 0, ~250ms, `CubicEaseOut`).
- Clicking again slides it back in (0 → 220).
- The sidebar wrapper has `ClipToBounds="True"` so content doesn't overflow during animation.
- **Session persistence:** `IsSidebarHidden` stays across track changes and lyrics re-entry. Resets on app restart.
- **Navigation guard:** When the user navigates away from lyrics, the sidebar is forced visible. When they return, the saved preference is re-applied.

## Toggle Button Design

- Chevron text: `«` when sidebar is visible (click to hide), `»` when hidden (click to show).
- Positioned top-left of the main content area, overlaid (not inside the lyrics UserControl).
- Subtle: ~0.5 base opacity, 0.9 on hover. Small font size (~14px). No background.
- Opacity transition on hover (~150ms).

## Files Changed

| File | Change |
|------|--------|
| `MainWindowViewModel.cs` | Add `IsSidebarHidden` bool property, `ToggleSidebarCommand`, logic to force-show on non-lyrics nav and re-apply on lyrics entry |
| `MainWindow.axaml` | Wrap sidebar in a container with animated width + `ClipToBounds`. Add toggle button overlay bound to `IsLyricsViewActive` visibility and `IsSidebarHidden` for icon state. Add `DoubleTransition` on width. |

## Architecture Notes

- No new files, icons, or dependencies.
- Uses existing `DoubleTransition` + `CubicEaseOut` animation pattern from the codebase.
- MVVM boundary respected: ViewModel owns the state, View wires bindings and transitions.
- Toggle button uses `RelayCommand` pattern consistent with existing commands.
