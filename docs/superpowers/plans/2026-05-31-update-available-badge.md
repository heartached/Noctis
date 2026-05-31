# Update-Available Sidebar Badge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show a small accent dot on the sidebar **Settings** item whenever an app update is available, so users are nudged to update without opening Settings.

**Architecture:** The update state already exists (`SettingsViewModel.IsUpdateAvailable`, set by the existing silent startup check). Add a presentational `ShowBadge` flag to the sidebar `NavItem` model; `MainWindowViewModel` (which owns both `Settings` and `Sidebar`) mirrors `IsUpdateAvailable` onto the Settings nav item's `ShowBadge` on the UI thread; `SidebarView.axaml` renders a dot bound to `ShowBadge`, mirroring the existing Favorites count-badge pattern.

**Tech Stack:** C#, .NET 8, Avalonia UI, CommunityToolkit.Mvvm (`[ObservableProperty]`), MVVM.

---

## Background — already exists, do NOT rebuild

- `Services/UpdateService.cs` — GitHub Releases check/download/install.
- `ViewModels/MainWindowViewModel.cs:363-374` — fires `Settings.CheckForUpdateSilentAsync()` ~5s after launch (fire-and-forget, on a ThreadPool task, errors swallowed). **Update-check timing is unchanged by this plan.**
- `ViewModels/SettingsViewModel.cs:2125` — `CheckForUpdateSilentAsync()` sets `IsUpdateAvailable = true` on success; the existing download/install flow sets it back to `false`.

The only gap: `IsUpdateAvailable` is visible only inside Settings → About. This plan surfaces it on the sidebar.

## File Structure

- **Modify** `src/Noctis/Models/NavItem.cs` — add `ShowBadge` observable property. (One responsibility: a sidebar nav entry's presentational state.)
- **Modify** `src/Noctis/ViewModels/MainWindowViewModel.cs` — wire `Settings.IsUpdateAvailable` → Settings nav item `ShowBadge` (UI-thread marshalled).
- **Modify** `src/Noctis/Views/SidebarView.axaml` — add dot to the main-nav `DataTemplate`, bound to `ShowBadge`.

No test project changes: per `.claude/rules/testing.md` the test project (`tests/Velour.Tests`) currently fails to compile at baseline for unrelated reasons, and this feature is UI-binding + view-model wiring with no pure-logic unit seam worth a brittle test. Verification is via build + manual check (Task 4). If a future refactor restores the test project, a `NavItem.ShowBadge` round-trip test would be the natural addition.

---

### Task 1: Add `ShowBadge` to `NavItem`

**Files:**
- Modify: `src/Noctis/Models/NavItem.cs`

- [ ] **Step 1: Add the observable property**

In `src/Noctis/Models/NavItem.cs`, the class is already `public partial class NavItem : ObservableObject` and already uses `[ObservableProperty] private string _label`. Add a new property after the `Label` property (keep the existing `using CommunityToolkit.Mvvm.ComponentModel;`).

Current relevant section:

```csharp
    /// <summary>Display label shown in the sidebar.</summary>
    [ObservableProperty] private string _label = string.Empty;

    /// <summary>Icon glyph character or path identifier.</summary>
    public string IconGlyph { get; set; } = string.Empty;
```

Change to:

```csharp
    /// <summary>Display label shown in the sidebar.</summary>
    [ObservableProperty] private string _label = string.Empty;

    /// <summary>When true, the sidebar shows a small accent dot on this item
    /// (used to flag an available app update on the Settings entry).</summary>
    [ObservableProperty] private bool _showBadge;

    /// <summary>Icon glyph character or path identifier.</summary>
    public string IconGlyph { get; set; } = string.Empty;
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds. The CommunityToolkit source generator emits a public `ShowBadge` property (PascalCase) from `_showBadge`.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Models/NavItem.cs
git commit -m "feat(sidebar): add ShowBadge flag to NavItem"
```

---

### Task 2: Wire `IsUpdateAvailable` → Settings nav item `ShowBadge`

**Files:**
- Modify: `src/Noctis/ViewModels/MainWindowViewModel.cs`

Context: the constructor already creates `Sidebar` (line ~189) and `Settings` (line ~191), and `Sidebar.NavItems` contains a `NavItem` with `Key == "settings"`. `Settings` is a `SettingsViewModel` (an `ObservableObject`), so it raises `PropertyChanged` for `IsUpdateAvailable`. The silent check runs on a ThreadPool task, so the handler must marshal to the UI thread. `Avalonia.Threading.Dispatcher` is already used elsewhere in this file (e.g. line ~215, ~219), and `System.Linq` is needed for `FirstOrDefault`.

- [ ] **Step 1: Ensure `System.Linq` is available**

At the top of `src/Noctis/ViewModels/MainWindowViewModel.cs`, confirm `using System.Linq;` is present. If it is not in the using block, add it. (Do not duplicate it if already present.)

- [ ] **Step 2: Subscribe to `Settings.PropertyChanged` in the constructor**

In the constructor, immediately after the existing line:

```csharp
        Settings.SettingsReset += async (_, _) => await Sidebar.LoadPlaylistsAsync();
```

add:

```csharp
        // Mirror the "update available" state onto the Settings sidebar item so a
        // dot nudges the user without them opening Settings. The silent update
        // check runs on a background thread, so marshal to the UI thread.
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SettingsViewModel.IsUpdateAvailable)) return;
            Dispatcher.UIThread.Post(() =>
            {
                var settingsNav = Sidebar.NavItems.FirstOrDefault(n => n.Key == "settings");
                if (settingsNav is not null)
                    settingsNav.ShowBadge = Settings.IsUpdateAvailable;
            });
        };
```

Notes for the implementer:
- `Dispatcher` resolves to `Avalonia.Threading.Dispatcher` (already imported/used in this file).
- `IsUpdateAvailable` and `ShowBadge` are the generated public properties from Task 1 and the existing `[ObservableProperty] private bool _isUpdateAvailable;` in `SettingsViewModel`.
- No initial-sync call is needed: the silent check runs after startup and flips `IsUpdateAvailable`, which fires `PropertyChanged` and sets the badge then.

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds, no warnings about missing `System.Linq` or unresolved `Dispatcher`/`ShowBadge`/`IsUpdateAvailable`.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/ViewModels/MainWindowViewModel.cs
git commit -m "feat(sidebar): drive Settings nav badge from update-available state"
```

---

### Task 3: Render the dot in the sidebar

**Files:**
- Modify: `src/Noctis/Views/SidebarView.axaml` (main-nav `DataTemplate`, lines ~38-61)

Context (verified against the real file): the **main nav** `ListBox` (`ItemsSource="{Binding NavItems}"`, `x:Name="NavList"`) uses `<DataTemplate x:DataType="m:NavItem">` whose root is a **horizontal `StackPanel`**, NOT a Grid. It contains:
1. an icon `Grid` (28x28) holding a `Border` with an `OpacityMask`/`ImageBrush` for the glyph, and
2. a label `TextBlock` that is only visible when the sidebar is expanded
   (`IsVisible="{Binding ...IsExpanded}"`).

The sidebar has a real **icon-only collapsed rail**: `MainWindow.axaml.cs:104` sets the sidebar wrapper `Width = 60` when collapsed (labels hidden via `IsExpanded`), and `Width = 0` only when fully hidden. So the badge MUST be anchored to the **icon**, not placed in a trailing label column — otherwise it vanishes in the icon-only rail. We overlay a small `Ellipse` in the top-right corner of the 28x28 icon `Grid` (a `Grid` with no column/row defs stacks its children, so an aligned child becomes an overlay).

The accent brush actually defined/used in this file is **`AccentColorBrush`** (see the Favorites heart `Background="{DynamicResource AccentColorBrush}"`). Use that, not `AccentBrush`.

- [ ] **Step 1: Add the dot overlay to the icon Grid in the main-nav template**

In the **main nav** `DataTemplate` (the `NavList` one, root `StackPanel Orientation="Horizontal" Spacing="14"` — NOT the Favorites or Playlist templates), change the icon `Grid` from:

```xml
                                    <Grid Width="28"
                                          Height="28"
                                          VerticalAlignment="Center">
                                        <Border Width="20"
                                                Height="20"
                                                HorizontalAlignment="Center"
                                                VerticalAlignment="Center"
                                                RenderOptions.BitmapInterpolationMode="HighQuality"
                                                Background="{DynamicResource IslandForeground}">
                                            <Border.OpacityMask>
                                                <ImageBrush Source="{Binding IconGlyph, Converter={StaticResource IconKeyToGeometry}}"
                                                            Stretch="Uniform" />
                                            </Border.OpacityMask>
                                        </Border>
                                    </Grid>
```

to (add an `Ellipse` dot overlay anchored top-right, bound to `ShowBadge`):

```xml
                                    <Grid Width="28"
                                          Height="28"
                                          VerticalAlignment="Center">
                                        <Border Width="20"
                                                Height="20"
                                                HorizontalAlignment="Center"
                                                VerticalAlignment="Center"
                                                RenderOptions.BitmapInterpolationMode="HighQuality"
                                                Background="{DynamicResource IslandForeground}">
                                            <Border.OpacityMask>
                                                <ImageBrush Source="{Binding IconGlyph, Converter={StaticResource IconKeyToGeometry}}"
                                                            Stretch="Uniform" />
                                            </Border.OpacityMask>
                                        </Border>
                                        <!-- Update-available dot (top-right of icon; visible in
                                             both expanded and icon-only rail) -->
                                        <Ellipse Width="8" Height="8"
                                                 Fill="{DynamicResource AccentColorBrush}"
                                                 HorizontalAlignment="Right"
                                                 VerticalAlignment="Top"
                                                 Margin="0,-1,-1,0"
                                                 IsVisible="{Binding ShowBadge}" />
                                    </Grid>
```

Implementer notes:
- `ShowBadge` binds against the `m:NavItem` data context of the template (the generated PascalCase property from Task 1). It is only ever `true` for the Settings item (Task 2 only sets that one), so no per-item filtering is needed in XAML.
- `AccentColorBrush` is a `DynamicResource` already used by the Favorites heart in this same file — confirmed present.
- `Ellipse` is `Avalonia.Controls.Shapes.Ellipse`, in the default Avalonia XAML namespace already used by this view (no new `xmlns` needed).
- Because the dot lives inside the icon `Grid`, it stays visible in the icon-only collapsed rail (width 60) and in the expanded state. It disappears only when the whole sidebar is hidden (width 0), which is correct.
- Do NOT modify the Favorites or Playlist templates; only the `NavList` main-nav one.

- [ ] **Step 2: Build to verify XAML compiles**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds (Avalonia compiles XAML at build time; a bad binding/markup would fail here).

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Views/SidebarView.axaml
git commit -m "feat(sidebar): show accent dot on Settings item when update available"
```

---

### Task 4: Manual verification

**Files:** none (verification only).

- [ ] **Step 1: Temporarily force the badge on**

To verify without waiting on a real GitHub release, temporarily edit `src/Noctis/ViewModels/SettingsViewModel.cs` `CheckForUpdateSilentAsync()` to force the state. At the start of the `try` block (line ~2130), temporarily add:

```csharp
            // TEMP VERIFY — remove before commit
            LatestVersionTag = "v9.9.9";
            IsUpdateAvailable = true;
            return;
```

- [ ] **Step 2: Run the app and observe**

Run: `dotnet run --project src/Noctis/Noctis.csproj`
Expected, after ~5s (the silent-check delay):
1. A small accent dot appears on the **Settings** item in the sidebar.
2. No exception in the console (confirms the UI-thread marshalling in Task 2 is correct).
3. Collapse the sidebar to the icon-only rail (move the pointer off it so labels hide / `IsExpanded` is false): the dot is still visible on the Settings icon. Expand again: still visible. Fully hide the sidebar: it disappears with the sidebar (correct).

- [ ] **Step 3: Verify it clears**

With the app running, open Settings → About and click the existing **Update available** button (it starts the download flow, which sets `IsUpdateAvailable = false`). Expected: the sidebar dot disappears. (If the forced `return` in Step 1 blocks the real flow, instead confirm clearing by reasoning/code-reading: the existing `DownloadUpdateAsync` sets `IsUpdateAvailable = false`, which fires `PropertyChanged` → handler sets `ShowBadge = false`.)

- [ ] **Step 4: Revert the temporary edit**

Remove the `// TEMP VERIFY` block added in Step 1. Confirm `git diff src/Noctis/ViewModels/SettingsViewModel.cs` shows no changes.

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds, no leftover diff in `SettingsViewModel.cs`.

- [ ] **Step 5: Final sanity commit (only if anything legitimately changed)**

If Steps 1-4 left the tree clean (expected), there is nothing to commit. Do not create an empty commit.

---

## Self-Review

**Spec coverage:**
- Persistent dot on Settings nav item → Task 1 + Task 3. ✓
- Driven by existing `IsUpdateAvailable`, UI-thread safe → Task 2. ✓
- Startup-only check unchanged, no `UpdateService` changes → no task touches them (explicitly out of scope). ✓
- Visible in expanded/collapsed sidebar → an icon-only rail (width 60) DOES exist; dot is anchored to the icon so it survives both states (Task 3 context + Task 4 Step 2.3). ✓
- Mirror existing Favorites badge styling → Task 3 uses the same `AccentColorBrush`. ✓
- Auto-clears on update → Task 2 handler + Task 4 Step 3. ✓
- No toast / no auto-download / no new setting → none added. ✓

**Placeholder scan:** No TBD/TODO left in shipped code. The only `TEMP VERIFY` block is in Task 4 and is explicitly removed in Step 4. ✓

**Type consistency:** `ShowBadge` (NavItem), `IsUpdateAvailable` (SettingsViewModel), `Key == "settings"`, `Sidebar.NavItems`, `Dispatcher.UIThread.Post` used identically across Tasks 1-3. ✓

**Note on build command:** Plan uses `src/Noctis/Noctis.csproj`; the older `.claude/rules/testing.md` references a `Velour` path. The actual project is `Noctis` (per repo layout). If `Noctis.csproj` is not found, list `src/Noctis/*.csproj` to confirm the exact name before building.
