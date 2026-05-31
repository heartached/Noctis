# Update-Available Sidebar Badge

**Date:** 2026-05-31
**Status:** Approved

## Overview

Surface the *existing* "update available" state outside of Settings so users
are nudged to update without having to remember to open Settings → About and
press "Check for Updates".

The silent startup check, the GitHub query, the download, and the install flow
already exist. This feature adds **only a passive, persistent visual indicator**
— a small accent dot on the **Settings** item in the sidebar — driven by the
update state that is already computed today.

## Background (what already exists — do NOT rebuild)

- `Services/UpdateService.cs` — GitHub Releases check, download, install.
- `ViewModels/MainWindowViewModel.cs` (~L363-374) — fires
  `Settings.CheckForUpdateSilentAsync()` ~5s after startup, fire-and-forget,
  errors swallowed. **Timing is already "on startup"; no change to when we check.**
- `ViewModels/SettingsViewModel.cs`:
  - `CheckForUpdateSilentAsync()` sets `IsUpdateAvailable`, `LatestVersionTag`,
    `IsLatestPrerelease` on success.
  - `IsUpdateAvailable` / `IsDownloadingUpdate` / `IsReadyToInstall` already
    drive the About-section buttons in `Views/SettingsView.axaml`.

The gap: `IsUpdateAvailable` is only visible inside Settings → About.

## Architecture

Chosen approach: a `ShowBadge` flag on the sidebar `NavItem`, wired by
`MainWindowViewModel` (which already owns both `Settings` and `Sidebar`).
This keeps `SidebarViewModel` and `SettingsViewModel` decoupled and mirrors the
existing `FavoritesCount` pattern in the sidebar.

### Modified: `Models/NavItem.cs`

- `NavItem` is in the `Noctis.Models` namespace and already an `ObservableObject`
  (its `Label` already uses `[ObservableProperty]`).
- Add `[ObservableProperty] private bool _showBadge;`
- Default `false`. Purely presentational state for a nav entry.

### Modified: `ViewModels/MainWindowViewModel.cs`

- Resolve the Settings nav item once:
  `Sidebar.NavItems.First(n => n.Key == "settings")` (guard with
  `FirstOrDefault`).
- Subscribe to `Settings.PropertyChanged`; when
  `e.PropertyName == nameof(SettingsViewModel.IsUpdateAvailable)`, set the
  Settings nav item's `ShowBadge = Settings.IsUpdateAvailable` (marshalled to
  the UI thread via `Dispatcher.UIThread.Post`, since the silent check runs on a
  ThreadPool task).
- No change to the existing silent-check call or its timing. The badge appears
  when the check flips `IsUpdateAvailable` to true, and clears automatically
  when the update is downloaded/installed (the existing flow sets
  `IsUpdateAvailable = false` at that point).

### Modified: `Views/SidebarView.axaml`

- The `NavItems` `ItemsControl` uses `<DataTemplate x:DataType="m:NavItem">`
  containing the nav `RadioButton` (with an `ImageBrush`/`IconGlyph` icon).
  Note the adjacent `FavoritesItems` template already renders a count badge
  bound to `FavoritesCount` — mirror that badge styling for visual consistency.
- Wrap the nav `RadioButton` in a `Grid`/`Panel` overlay and add a small
  `Ellipse` "dot":
  - Accent color (reuse the existing accent brush used elsewhere in the app;
    the update buttons use `#E74856` — prefer an existing accent resource if one
    exists, else match that value).
  - ~8px diameter, positioned top-right of the item, `IsVisible="{Binding ShowBadge}"`.
  - Overlay placement avoids editing the `nav-item` ControlTheme or the
    `RadioButton` content, so existing nav styling/selection behavior is untouched.

## Data Flow

```
startup (existing) → CheckForUpdateSilentAsync() on ThreadPool
  → success: SettingsViewModel.IsUpdateAvailable = true
    → PropertyChanged → MainWindowViewModel handler (→ UI thread)
      → settingsNavItem.ShowBadge = true
        → SidebarView Ellipse becomes visible (dot on Settings)

user opens Settings → existing red "Update available" button → existing
download/install flow → IsUpdateAvailable = false
  → PropertyChanged → ShowBadge = false → dot disappears
```

## UX

- A small accent dot appears on the **Settings** sidebar entry when a newer
  release is detected. It persists across the session and re-appears on the next
  launch (because the silent check runs every startup) until the user updates.
- Clicking **Settings** behaves exactly as today; the existing
  "Update available" button in About performs the update.
- Sidebar collapsed/expanded states: the dot must remain visible (or visibly
  indicated) in both, since it's anchored to the Settings item itself.

## Error Handling

- Inherited from the existing silent check: any failure (offline, timeout, API
  error) is swallowed and simply results in **no badge**. No new error surfaces.

## Testing / Verification

- Build: `dotnet build src/Noctis/Noctis.csproj -v minimal` (project is `Noctis`,
  not the `Velour` path referenced in older rule docs — confirm csproj name).
- Manual: temporarily force `IsUpdateAvailable = true` (or point the check at a
  higher release) and confirm:
  1. Dot appears on the Settings nav item after startup.
  2. Dot is on the UI thread / no cross-thread exception.
  3. Dot clears after the update flow sets `IsUpdateAvailable = false`.
  4. No dot when already on the latest version / when offline.

## Scope Exclusions (YAGNI)

- No toast/banner notification.
- No periodic re-check while running (startup-only, unchanged).
- No auto-download or auto-install.
- No new user setting/toggle for the badge.
- No deep-link / auto-scroll to the update card in Settings.
- No change to update check timing or `UpdateService`.

## Files Changed

| File | Change |
|------|--------|
| `Models/NavItem.cs` | Add `ShowBadge` observable property |
| `ViewModels/MainWindowViewModel.cs` | Wire `Settings.IsUpdateAvailable` → Settings nav item `ShowBadge` (UI thread) |
| `Views/SidebarView.axaml` | Overlay accent dot on nav item, bound to `ShowBadge` |

## Dependencies

None. Uses existing MVVM toolkit (`[ObservableProperty]`), existing accent
color, existing update state.
