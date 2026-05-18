# Lyrics Page Playback Bar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the lyrics page's custom player bar with the standard `PlaybackBarView` used everywhere else, and relocate the two lyrics-only controls (Synced/Plain toggle, Background Color picker) into the bar's ⋯ menu.

**Architecture:** Embed `PlaybackBarView` verbatim on the lyrics page bound to `Player`. Append three menu items to PlaybackBar's existing `MenuFlyout`, gated by a new `Player.IsLyricsPageActive` flag. The lyrics-mode commands are exposed on `PlayerViewModel` as pass-throughs that invoke registered `Action`s, set up by `MainWindowViewModel` when the lyrics view becomes the current view. The color picker `<Button.Flyout>` is kept as-is on a hidden host button in `LyricsView`, opened programmatically from code-behind in response to a VM event.

**Tech Stack:** Avalonia UI XAML, CommunityToolkit MVVM.

**Spec:** [docs/superpowers/specs/2026-05-18-lyrics-page-playback-bar-design.md](../specs/2026-05-18-lyrics-page-playback-bar-design.md)

**Testing note:** Per `.claude/rules/testing.md`, the test project has a baseline compile failure unrelated to this work. UI XAML changes are not unit-tested in this codebase. Verification per task is `dotnet build src/Noctis/Noctis.csproj -v minimal` + the manual checklist at the end of the plan.

**Pre-existing WIP:** `src/Noctis/Views/PlaybackBarView.axaml` was already modified before this work started (unrelated WIP from the user). When committing edits to that file in Task 4, stage **only** that file and the lyrics-related diff — do not run `git add -A` or `git add .`. Same caution applies to any other pre-existing modified files surfaced by `git status`.

---

## Task 1: LyricsViewModel — dedicated Synced/Plain commands + color picker open event

**Files:**
- Modify: `src/Noctis/ViewModels/LyricsViewModel.cs`

- [ ] **Step 1: Add `OpenBackgroundColorRequested` event near the top of the class (after the field declarations region)**

Locate the class body (search for `public partial class LyricsViewModel`). Inside the class, near other event declarations or just after the constructor, add:

```csharp
/// <summary>
/// Raised when the user requests the background color picker to open from outside the
/// lyrics view's own bar (e.g. from the standard PlaybackBar's ⋯ menu).
/// The lyrics view's code-behind subscribes and calls Flyout.ShowAt on the hidden host button.
/// </summary>
public event Action? OpenBackgroundColorRequested;
```

- [ ] **Step 2: Add two dedicated relay commands for Synced / Plain selection**

Find the existing `ToggleLyricsMode` command (around line 212-218). Add the two new commands directly below it:

```csharp
[RelayCommand]
private void SelectSyncedLyrics()
{
    if (!HasSyncedLyricsAvailable) return;
    IsSyncTabSelected = true;
    IsUnsyncTabSelected = false;
}

[RelayCommand]
private void SelectPlainLyrics()
{
    IsSyncTabSelected = false;
    IsUnsyncTabSelected = true;
}
```

- [ ] **Step 3: Add a relay command that raises the open-picker event**

Directly below the two commands from Step 2, add:

```csharp
[RelayCommand]
private void OpenBackgroundColorPicker()
{
    OpenBackgroundColorRequested?.Invoke();
}
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: build succeeds, 0 errors. (`Noctis (PID)` lock errors are fine — the source compiled.)

- [ ] **Step 5: Commit**

```bash
git add src/Noctis/ViewModels/LyricsViewModel.cs
git commit -m "feat(lyrics): add dedicated Synced/Plain commands and color picker open event"
```

---

## Task 2: PlayerViewModel — lyrics-mode flags, pass-through commands, registration methods

**Files:**
- Modify: `src/Noctis/ViewModels/PlayerViewModel.cs`

- [ ] **Step 1: Add the observable properties near the top of the class (with other `[ObservableProperty]` declarations)**

Locate the existing `[ObservableProperty]` declarations in `PlayerViewModel.cs`. Add this block adjacent to them:

```csharp
// ── Lyrics page integration (flags + pass-through commands set up by MainWindowViewModel) ──

[ObservableProperty] private bool _isLyricsPageActive;
[ObservableProperty] private bool _isLyricsSyncedActive;
[ObservableProperty] private bool _isLyricsPlainActive;
[ObservableProperty] private bool _isLyricsSyncedAvailable;
```

- [ ] **Step 2: Add the private action fields**

Below the properties from Step 1, add:

```csharp
private Action? _selectLyricsSynced;
private Action? _selectLyricsPlain;
private Action? _openLyricsBackgroundColor;
```

- [ ] **Step 3: Add the three pass-through relay commands**

Anywhere in the class with other commands, add:

```csharp
[RelayCommand]
private void SetLyricsSynced() => _selectLyricsSynced?.Invoke();

[RelayCommand]
private void SetLyricsPlain() => _selectLyricsPlain?.Invoke();

[RelayCommand]
private void OpenLyricsBackgroundColor() => _openLyricsBackgroundColor?.Invoke();
```

- [ ] **Step 4: Add the registration and clear methods**

After the commands from Step 3, add:

```csharp
/// <summary>
/// Called by MainWindowViewModel when the lyrics view becomes the current view.
/// Wires the three pass-through commands and seeds the active-state flags.
/// </summary>
public void SetLyricsPageActions(
    Action selectSynced,
    Action selectPlain,
    Action openBackgroundColor,
    bool isSyncedActive,
    bool isPlainActive,
    bool isSyncedAvailable)
{
    _selectLyricsSynced = selectSynced;
    _selectLyricsPlain = selectPlain;
    _openLyricsBackgroundColor = openBackgroundColor;
    IsLyricsSyncedActive = isSyncedActive;
    IsLyricsPlainActive = isPlainActive;
    IsLyricsSyncedAvailable = isSyncedAvailable;
    IsLyricsPageActive = true;
}

/// <summary>
/// Called by MainWindowViewModel when navigating away from the lyrics view.
/// </summary>
public void ClearLyricsPageActions()
{
    _selectLyricsSynced = null;
    _selectLyricsPlain = null;
    _openLyricsBackgroundColor = null;
    IsLyricsPageActive = false;
    IsLyricsSyncedActive = false;
    IsLyricsPlainActive = false;
    IsLyricsSyncedAvailable = false;
}

/// <summary>
/// Called by MainWindowViewModel whenever the lyrics view's Synced/Plain selection
/// or synced-availability changes, to keep the menu's checkmarks accurate.
/// </summary>
public void UpdateLyricsPageState(bool isSyncedActive, bool isPlainActive, bool isSyncedAvailable)
{
    IsLyricsSyncedActive = isSyncedActive;
    IsLyricsPlainActive = isPlainActive;
    IsLyricsSyncedAvailable = isSyncedAvailable;
}
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: build succeeds, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/Noctis/ViewModels/PlayerViewModel.cs
git commit -m "feat(player): add lyrics-page integration hooks for the standard PlaybackBar menu"
```

---

## Task 3: PlaybackBarView — append three menu items gated by `IsLyricsPageActive`

**Files:**
- Modify: `src/Noctis/Views/PlaybackBarView.axaml`

- [ ] **Step 1: Read the current PlaybackBar flyout block**

Open `src/Noctis/Views/PlaybackBarView.axaml` and locate the existing `<MenuFlyout>` attached to the ⋯ button. It ends with the standard "Remove from Library" item followed by `</MenuFlyout>`. The new entries will be inserted just before that closing tag.

- [ ] **Step 2: Insert a separator + three menu items immediately before `</MenuFlyout>`**

Place this block at the end of the existing flyout's items, before `</MenuFlyout>`:

```xml
<!-- Lyrics-mode entries (only visible when the bar is mounted inside the lyrics page) -->
<Separator IsVisible="{Binding IsLyricsPageActive}"/>
<MenuItem Header="Lyrics Display" IsVisible="{Binding IsLyricsPageActive}">
    <MenuItem Header="Synced"
              Command="{Binding SetLyricsSyncedCommand}"
              IsEnabled="{Binding IsLyricsSyncedAvailable}">
        <MenuItem.Icon>
            <PathIcon Data="{StaticResource CheckmarkIcon}"
                      Width="14" Height="14"
                      Foreground="{DynamicResource SystemControlForegroundBaseHighBrush}"
                      IsVisible="{Binding IsLyricsSyncedActive}"/>
        </MenuItem.Icon>
    </MenuItem>
    <MenuItem Header="Plain"
              Command="{Binding SetLyricsPlainCommand}">
        <MenuItem.Icon>
            <PathIcon Data="{StaticResource CheckmarkIcon}"
                      Width="14" Height="14"
                      Foreground="{DynamicResource SystemControlForegroundBaseHighBrush}"
                      IsVisible="{Binding IsLyricsPlainActive}"/>
        </MenuItem.Icon>
    </MenuItem>
</MenuItem>
<MenuItem Header="Background Color"
          Command="{Binding OpenLyricsBackgroundColorCommand}"
          IsVisible="{Binding IsLyricsPageActive}">
    <MenuItem.Icon>
        <PathIcon Data="{StaticResource PaletteIcon}"
                  Width="14" Height="14"
                  Foreground="{DynamicResource SystemControlForegroundBaseHighBrush}"/>
    </MenuItem.Icon>
</MenuItem>
```

- [ ] **Step 3: Verify the static resources `CheckmarkIcon` and `PaletteIcon` exist**

Run from the repo root:

```bash
grep -an "x:Key=\"CheckmarkIcon\"\|x:Key=\"PaletteIcon\"" src/Noctis/Assets/Icons.axaml
```

Expected: both keys present. If either is missing, abort and report — the spec assumed both exist. (`PaletteIcon` is referenced by the existing lyrics-page color picker so it definitely exists; `CheckmarkIcon` may need adding if it doesn't.)

If `CheckmarkIcon` is missing, add this entry to `src/Noctis/Assets/Icons.axaml` near other icon path resources:

```xml
<StreamGeometry x:Key="CheckmarkIcon">M9,16.17L4.83,12l-1.42,1.41L9,19 21,7l-1.41,-1.41z</StreamGeometry>
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: build succeeds, 0 errors.

- [ ] **Step 5: Commit**

Stage **only** the files you actually touched in this task:

```bash
git add src/Noctis/Views/PlaybackBarView.axaml
# only add Icons.axaml if Step 3 required adding CheckmarkIcon:
# git add src/Noctis/Assets/Icons.axaml
git commit -m "feat(playback-bar): append lyrics-mode menu entries gated by IsLyricsPageActive"
```

---

## Task 4: MainWindowViewModel — register/unregister lyrics-page actions on navigation

**Files:**
- Modify: `src/Noctis/ViewModels/MainWindowViewModel.cs`

- [ ] **Step 1: Find where the lyrics view becomes the current view**

Run:

```bash
grep -an "_lyricsVm\|LyricsViewModel\b" src/Noctis/ViewModels/MainWindowViewModel.cs
```

Identify the navigation path that sets `CurrentView = _lyricsVm` (or equivalent). Also identify the `_lyricsVm` field declaration and constructor where the LyricsViewModel is instantiated.

- [ ] **Step 2: Add a private helper that subscribes to LyricsViewModel state changes**

Place this method anywhere in the class (near other private helpers):

```csharp
private void WireLyricsPageToPlayer()
{
    Player.SetLyricsPageActions(
        selectSynced: () => _lyricsVm.SelectSyncedLyricsCommand.Execute(null),
        selectPlain: () => _lyricsVm.SelectPlainLyricsCommand.Execute(null),
        openBackgroundColor: () => _lyricsVm.OpenBackgroundColorPickerCommand.Execute(null),
        isSyncedActive: _lyricsVm.IsSyncTabSelected,
        isPlainActive: _lyricsVm.IsUnsyncTabSelected,
        isSyncedAvailable: _lyricsVm.HasSyncedLyricsAvailable);

    _lyricsVm.PropertyChanged -= OnLyricsVmPropertyChanged;
    _lyricsVm.PropertyChanged += OnLyricsVmPropertyChanged;
}

private void UnwireLyricsPageFromPlayer()
{
    _lyricsVm.PropertyChanged -= OnLyricsVmPropertyChanged;
    Player.ClearLyricsPageActions();
}

private void OnLyricsVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
{
    if (e.PropertyName == nameof(LyricsViewModel.IsSyncTabSelected)
        || e.PropertyName == nameof(LyricsViewModel.IsUnsyncTabSelected)
        || e.PropertyName == nameof(LyricsViewModel.HasSyncedLyricsAvailable))
    {
        Player.UpdateLyricsPageState(
            isSyncedActive: _lyricsVm.IsSyncTabSelected,
            isPlainActive: _lyricsVm.IsUnsyncTabSelected,
            isSyncedAvailable: _lyricsVm.HasSyncedLyricsAvailable);
    }
}
```

- [ ] **Step 3: Call `WireLyricsPageToPlayer` when navigating to the lyrics view**

In the navigation code path identified in Step 1 (where `CurrentView = _lyricsVm` is set — typically inside the `Navigate` switch case for `"lyrics"` or an `OpenLyrics` method), add **immediately after** the `CurrentView = _lyricsVm` line:

```csharp
WireLyricsPageToPlayer();
```

- [ ] **Step 4: Call `UnwireLyricsPageFromPlayer` whenever the lyrics view stops being current**

Find `ClearAllTopBarActions()` or the equivalent cleanup that runs on every navigation. Inside it, add an unconditional call:

```csharp
UnwireLyricsPageFromPlayer();
```

`UnwireLyricsPageFromPlayer` is idempotent — it removes a possibly-not-subscribed handler and clears already-null actions, so calling it on every navigation is safe and ensures correctness when leaving the lyrics view by any path. The subsequent `WireLyricsPageToPlayer()` (in Step 3) re-establishes the wiring when returning to lyrics.

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: build succeeds, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/Noctis/ViewModels/MainWindowViewModel.cs
git commit -m "feat(main): wire LyricsViewModel into Player on lyrics-view navigation"
```

---

## Task 5: LyricsView — remove the old bar, mount PlaybackBarView, host the color picker

**Files:**
- Modify: `src/Noctis/Views/LyricsView.axaml`
- Modify: `src/Noctis/Views/LyricsView.axaml.cs`

- [ ] **Step 1: Read the current LyricsView bar region**

Open `src/Noctis/Views/LyricsView.axaml`. The bar region is the entire seek block starting at the `<Grid Grid.Row="1" Height="20" ClipToBounds="False">` around line 494, through the volume `StackPanel` ending around line 990+. Also note the styles in lines 55-160 (`lyrics-ctrl-btn`, `lyrics-seek`, `lyrics-volume`) and the brush resources `LyricsSliderThumbRes`, `LyricsSliderFilledRes`, `LyricsSliderUnfilledRes` in lines 21-23.

- [ ] **Step 2: Replace the seek slider + pill island block with the standard PlaybackBar**

In the file, the bar consists of two **sibling** elements inside the same parent layout panel:

1. The seek-bar `<Grid Margin="6,8,6,0" RowDefinitions="Auto,Auto">` (starts around line 477) — this contains the time labels Grid (row 0) and the `LyricsSeekVisual` Canvas + Slider (row 1).
2. The `<Border Background="#F0181818" CornerRadius="999" ...>` pill island (starts around line 528) — this contains all the `lyrics-ctrl-btn` buttons, the Synced/Plain pill, the volume popup, and the color picker button.

Delete **both** of those sibling elements in their entirety. In their place, insert these two new siblings (no `Grid.Row` attribute — they inherit StackPanel-style layout from the parent, same as the elements being replaced):

```xml
<!-- Standard playback bar (visually identical to the rest of the app) -->
<views:PlaybackBarView DataContext="{Binding Player}"
                       HorizontalAlignment="Stretch"
                       Margin="0,12,0,0"/>

<!-- Hidden host button anchoring the background-color flyout. The ⋯ menu's
     "Background Color" item raises an event that calls Flyout.ShowAt on this. -->
<Button x:Name="LyricsColorPickerHost"
        Width="1" Height="1"
        Opacity="0"
        IsHitTestVisible="False"
        HorizontalAlignment="Center"
        VerticalAlignment="Bottom"
        Margin="0,0,0,60">
    <Button.Flyout>
        <!-- PASTE the existing <Flyout Placement="TopEdgeAlignedRight">…</Flyout> block
             from the old color-picker button verbatim here. Keep the entire FlyoutPresenterTheme,
             inner Border, Solid/Gradient toggle, swatch ScrollViewers, etc. -->
    </Button.Flyout>
</Button>
```

If the parent layout panel is a `Grid` rather than `StackPanel`/`DockPanel` (verify by looking at the parent element), copy whatever `Grid.Row` (and `Grid.RowSpan`) the deleted pill island had onto **both** the new `PlaybackBarView` and the `LyricsColorPickerHost` button.

**Important:** the color picker `<Flyout>` body (lines 825-929 in the original file) is moved **as a whole** — the implementer should cut-and-paste the entire `<Flyout Placement="TopEdgeAlignedRight">…</Flyout>` element from the original color-picker button into the new `LyricsColorPickerHost.Flyout` slot. All inner bindings (`SelectColorModeSolidCommand`, `IsColorModeSolid`, swatch templates, etc.) keep working because the host button sits inside `LyricsView` and so still resolves `LyricsViewModel` via the inherited DataContext.

- [ ] **Step 3: Remove the now-unused style blocks and brushes from LyricsView.axaml**

Delete these style selectors and resource entries:
- `<Style Selector="Button.lyrics-ctrl-btn">` and all its variants (`:pointerover`, `:pressed`, `:pointerover /template/ ContentPresenter`, `:pressed /template/ ContentPresenter`) — roughly lines 55-72
- `<Style Selector="Slider.lyrics-seek">` and any nested style selectors targeting it — roughly lines 100-108
- `<Style Selector="Slider.lyrics-volume">` and all its nested template selectors — roughly lines 109-165
- `<SolidColorBrush x:Key="LyricsSliderThumbRes">`, `LyricsSliderFilledRes`, `LyricsSliderUnfilledRes` — roughly lines 21-23

After removal, run:

```bash
grep -an "lyrics-ctrl-btn\|lyrics-seek\|lyrics-volume\|LyricsSliderThumbRes\|LyricsSliderFilledRes\|LyricsSliderUnfilledRes\|LyricsSeekVisual\|LyricsSeekTrackBackground\|LyricsSeekTrackFill\|LyricsSeekThumb\|LyricsVolumeContainer\|LyricsVolumeMuteButton" src/Noctis/Views/LyricsView.axaml
```

Expected: zero matches. If any remain, finish removing them.

- [ ] **Step 4: Strip the code-behind handlers tied to the removed bar**

Open `src/Noctis/Views/LyricsView.axaml.cs`. Remove every handler and field whose only call sites were the markup deleted in Step 2-3. Specifically search for and delete:

- `OnSeekPointerPressed`, `OnSeekPointerMoved`, `OnSeekPointerReleased` methods
- `_isSeekDragging` field (and any other seek-drag-related fields)
- `GetPercentageFromPointer` helper (only if no other call sites remain — verify with grep first)
- `OnTrackFlyoutOpened`, `OnTrackFlyoutButtonPointerPressed` (both should already be empty no-ops)
- `OnLyricsVolumeContainerEntered`, `OnLyricsVolumeContainerExited`, and any private fields used solely by them

Verify each removal with grep before deleting:

```bash
grep -an "OnSeekPointerPressed\|_isSeekDragging\|GetPercentageFromPointer\|OnTrackFlyoutOpened\|OnTrackFlyoutButtonPointerPressed\|OnLyricsVolumeContainerEntered\|OnLyricsVolumeContainerExited" src/Noctis/Views/LyricsView.axaml src/Noctis/Views/LyricsView.axaml.cs
```

The remaining matches after deletion should only be in the `.axaml.cs` (the method definitions you're about to remove); the `.axaml` should have zero matches because the markup that called them is gone.

- [ ] **Step 5: Subscribe to `OpenBackgroundColorRequested` in the code-behind**

In `src/Noctis/Views/LyricsView.axaml.cs`, ensure the view subscribes to the new event on the VM. Add (or update) the `OnDataContextChanged` override:

```csharp
private LyricsViewModel? _subscribedLyricsVm;

protected override void OnDataContextChanged(EventArgs e)
{
    base.OnDataContextChanged(e);

    if (_subscribedLyricsVm != null)
    {
        _subscribedLyricsVm.OpenBackgroundColorRequested -= OnOpenBackgroundColorRequested;
        _subscribedLyricsVm = null;
    }

    if (DataContext is LyricsViewModel vm)
    {
        vm.OpenBackgroundColorRequested += OnOpenBackgroundColorRequested;
        _subscribedLyricsVm = vm;
    }
}

private void OnOpenBackgroundColorRequested()
{
    Dispatcher.UIThread.Post(() =>
    {
        LyricsColorPickerHost?.Flyout?.ShowAt(LyricsColorPickerHost);
    });
}
```

Make sure the file has `using Avalonia.Threading;` and `using Noctis.ViewModels;` at the top (add if missing).

Also unsubscribe in `OnDetachedFromVisualTree` (or the existing detach handler) — find the existing one and add the unsubscribe before the base call:

```csharp
if (_subscribedLyricsVm != null)
{
    _subscribedLyricsVm.OpenBackgroundColorRequested -= OnOpenBackgroundColorRequested;
    _subscribedLyricsVm = null;
}
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: build succeeds, 0 errors. If any `lyrics-ctrl-btn`, seek, or volume reference still exists, the compiler will flag it — fix and rebuild.

- [ ] **Step 7: Commit**

```bash
git add src/Noctis/Views/LyricsView.axaml src/Noctis/Views/LyricsView.axaml.cs
git commit -m "feat(lyrics): adopt standard PlaybackBar, host color picker via hidden anchor"
```

---

## Final manual verification checklist

Run Noctis after the implementer commits all five tasks. On the lyrics page:

- [ ] Bar is visually identical to the main app's PlaybackBar (chrome, pill radius, transport icons, central seek block, ⋯/queue/lyrics/volume row).
- [ ] Album art thumbnail + title + artist + seek + time labels all render in the bar's center.
- [ ] The dedicated Synced/Plain pill, the color-picker dot, and the separate seek slider above the bar are all gone.
- [ ] Clicking ⋯ shows the standard menu items, then a separator, then **Lyrics Display** (with Synced/Plain submenu, ✓ on the active option, Synced disabled when no synced lyrics are available), then **Background Color**.
- [ ] Clicking Synced/Plain in the menu toggles the lyrics display mode; the checkmark moves to the active option.
- [ ] Clicking Background Color opens the existing color picker flyout (the same Solid/Gradient toggle + swatches popup, anchored sensibly near the bottom-center of the page).
- [ ] Picking a swatch in the popup updates the lyrics page background.
- [ ] Volume control on the bar works as on the main app.
- [ ] Queue button and lyrics-icon button on the bar still navigate/toggle correctly.

Elsewhere in the app (Home, Albums, Album Detail, Artist, Playlists, Favorites, etc.):

- [ ] The PlaybackBar's ⋯ menu does **not** show the separator, **Lyrics Display**, or **Background Color** entries.
- [ ] No visual or behavioral regressions in the standard PlaybackBar.

Side lyrics panel:

- [ ] Still functions as before; this work doesn't touch it.

If any check fails, report the specific failure with screenshot and which task likely caused it.
