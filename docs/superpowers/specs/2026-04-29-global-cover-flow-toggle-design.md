# Global Cover Flow Toggle

**Date:** 2026-04-29
**Status:** Approved

## Summary

Promote the existing Albums-only "Library / Cover Flow" pill toggle in the top bar to a global view-mode toggle that is available from Home, Songs, Albums, Artists, Folders, Playlists, and Favorites. Cover Flow itself is unchanged: it is the existing player-queue carousel (`CoverFlowView` / `CoverFlowViewModel`). The toggle becomes a global "now-playing mode" overlay that sits in front of whichever section is currently active.

## Motivation

Cover Flow is currently only reachable from the Albums section, even though its content (a carousel of the playback queue) is identical regardless of which section the user is browsing. Users want a single, consistent way to flip between "library content" and "now-playing carousel" from any of the main sections.

## Requirements

1. The Library / Cover Flow pill toggle is visible in the top bar whenever the active section is one of:
   - Home, Songs, Albums, Artists, Folders, Playlists, Favorites.
2. The toggle is hidden in all other sections (Settings, Album detail, Playlist detail, Lyrics, Metadata, etc.).
3. Cover Flow mode is **global and sticky**:
   - Clicking Cover Flow flips a single global flag.
   - Switching tabs in the sidebar while in Cover Flow keeps Cover Flow visible (the underlying section selection updates silently).
   - Clicking Library returns to whichever section is currently selected in the sidebar.
4. Navigating to a non-toggle-eligible section (e.g. opening an album detail) exits Cover Flow mode automatically — otherwise the toggle would disappear while leaving the user stuck in Cover Flow.
5. Returning to a toggle-eligible section starts in Library mode (mode does not persist across visits to ineligible sections).
6. Cover Flow content shown is always the existing single shared `CoverFlowView` instance — no per-section variants.

## Design

### State & ownership

`MainWindowViewModel` owns the single source of truth:

```csharp
[ObservableProperty] private bool _isCoverFlowMode;
```

The existing Albums-specific `IsCoverFlowMode` on `LibraryAlbumsViewModel` and any Albums-internal swap logic in that VM/view are removed. Albums stops doing its own swap; the swap is performed at the MainWindow shell.

`TopBarViewModel`:
- `HasAlbumsViewModeToggle` → renamed to `HasViewModeToggle`.
- `AlbumsSetLibraryModeCommand` / `AlbumsSetCoverFlowModeCommand` → renamed to `SetLibraryModeCommand` / `SetCoverFlowModeCommand`.
- `IsCoverFlowMode` bool stays (drives the active-pill styling) and is kept in sync with the MainWindow flag.
- The `AlbumsViewModeChanged` event is removed — MainWindow drives the swap directly.
- `ShowAlbumsViewModeToggle(...)` is renamed to `ShowViewModeToggle(...)`; `HideAlbumsViewModeToggle()` is renamed to `HideViewModeToggle()`.

### Toggle visibility

`HasViewModeToggle` is set true when the active section is a member of the 7-section set above; false otherwise. Wired in `MainWindowViewModel` at the same call sites that currently invoke `SetupAlbumsViewModeToggle()` / `HideAlbumsViewModeToggle()` — generalized to check membership in the 7-section set instead of "is Albums."

### Content swap

The MainWindow content host becomes a single `ContentControl` whose `Content` is bound to a computed property on `MainWindowViewModel`:

```
CurrentContent =
    IsCoverFlowMode ? CoverFlowVm
                    : <current section view-model>
```

`OnPropertyChanged(nameof(CurrentContent))` is raised whenever `IsCoverFlowMode` flips or the active section changes.

The existing single `CoverFlowViewModel` instance on `MainWindowViewModel` is reused — no new instance is created.

### Sidebar behavior while in Cover Flow

Sidebar selection still updates the active section underneath, so when the user clicks Library, the correct section is revealed. The sidebar's selected item visually reflects the underlying section — Cover Flow is a mode overlay, not a sidebar destination.

### Toggle commands

- `SetLibraryModeCommand` → `MainWindowViewModel.IsCoverFlowMode = false`.
- `SetCoverFlowModeCommand` → `MainWindowViewModel.IsCoverFlowMode = true`.

Both are owned by `MainWindowViewModel` and exposed to `TopBarViewModel` via the renamed `ShowViewModeToggle(...)` wiring.

When `IsCoverFlowMode` flips, in `MainWindowViewModel.OnIsCoverFlowModeChanged`:
- `TopBarViewModel.IsCoverFlowMode` is updated to drive pill styling.
- `CoverFlowViewModel` start/stop (queue subscriptions etc.) is invoked here so the VM only does work when actually visible. This logic is moved out of the Albums-specific path.

### Auto-exit on ineligible navigation

When navigation moves to a section not in the 7-set, `MainWindowViewModel` sets `IsCoverFlowMode = false` (and consequently hides the toggle). This is a single guard at the same place section-change is detected.

## Files affected

- `ViewModels/MainWindowViewModel.cs` — add `IsCoverFlowMode`, `CurrentContent`, mode commands, lifecycle hook for `CoverFlowViewModel`; replace Albums-specific setup with general 7-section check; auto-exit guard.
- `ViewModels/TopBarViewModel.cs` — rename `HasAlbumsViewModeToggle` → `HasViewModeToggle`, rename commands, drop `AlbumsViewModeChanged` event, rename `Show/HideAlbumsViewModeToggle`.
- `Views/MainWindow.axaml` — content host becomes a single `ContentControl` bound to `CurrentContent`.
- `Views/TopBarView.axaml` — update bindings to renamed properties/commands.
- `ViewModels/LibraryAlbumsViewModel.cs` + `Views/LibraryAlbumsView.axaml` / `.axaml.cs` — remove the Albums-internal Library / Cover Flow swap. Albums view becomes purely the library grid.

## Testing

- Manual: from each of the 7 sections, click Cover Flow → carousel shows; switch tabs in sidebar → carousel stays; click Library → returns to whichever tab is now selected.
- Manual: navigate to a non-eligible section (e.g. open an album detail) while in Cover Flow → mode exits, toggle disappears; returning to a 7-set section starts in Library mode.
- Manual: `CoverFlowViewModel` cleanly starts/stops queue subscriptions on toggle (no leaks when not visible).
- Build: `dotnet build src/Noctis/Noctis.csproj -v minimal`.
- No existing unit tests cover this UI flow.

## Risks

- Albums currently swaps internally; lifting that out means any saved scroll/state behavior tied to its swap path must keep working when entering/leaving Cover Flow from Albums. Should be unaffected because the Albums view itself is not unloaded — the shell just hides it behind `CoverFlowView`.
- `CoverFlowViewModel` lifecycle relocation: existing start/stop logic must be moved cleanly into `OnIsCoverFlowModeChanged`. Verify subscriptions/unsubscriptions from player events behave the same as today.

## Out of scope

- Per-tab alternate visualizations (a different "cover flow" per section). Cover Flow remains the single existing player-queue carousel.
- Per-tab memory of previous mode (each ineligible-section visit resets to Library on return).
- Any visual or behavioral changes to `CoverFlowView` itself.
