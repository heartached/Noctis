# Collapsible Sidebar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Redesign the sidebar to collapse to 60px icon-only by default, expanding to 220px on hover with smooth animation.

**Architecture:** Clip-based approach — `SidebarWrapper` width controls visibility, `SidebarView` always renders at 220px internally. `IsPointerOver` on the wrapper drives expand/collapse. Existing `DoubleTransition` handles animation.

**Tech Stack:** Avalonia UI, C#, CommunityToolkit MVVM

**Spec:** `docs/superpowers/specs/2026-03-25-collapsible-sidebar-design.md`

---

### Task 1: Remove sidebar toggle infrastructure from MainWindowViewModel

**Files:**
- Modify: `src/Noctis/ViewModels/MainWindowViewModel.cs:65-83` (sidebar toggle section)
- Modify: `src/Noctis/ViewModels/MainWindowViewModel.cs:459-474` (OnCurrentViewChanged)

- [ ] **Step 1: Remove sidebar toggle fields and command**

Delete two blocks — the sidebar toggle fields (lines 65-71) and the ToggleSidebar command (lines 78-83). **Preserve lines 73-76** (`_isLyricsPanelOpen` and the lyrics side panel comment) — they are unrelated.

```csharp
// DELETE block 1 (lines 65-71):
// ── Sidebar toggle (lyrics immersive mode) ──
/// <summary>Session preference: user wants sidebar hidden in lyrics view.</summary>
private bool _lyricsSidebarPref;
/// <summary>Whether the sidebar is currently hidden (animated to width 0).</summary>
[ObservableProperty] private bool _isSidebarHidden;

// KEEP lines 73-76 (_isLyricsPanelOpen) — do NOT delete

// DELETE block 2 (lines 78-83):
[RelayCommand]
private void ToggleSidebar()
{
    _lyricsSidebarPref = !_lyricsSidebarPref;
    IsSidebarHidden = _lyricsSidebarPref;
}
```

- [ ] **Step 2: Clean up OnCurrentViewChanged**

In `OnCurrentViewChanged` (around line 459), replace the sidebar block:

```csharp
// BEFORE (lines 465-474):
// Sidebar: force-show when leaving lyrics, re-apply pref when entering
if (IsLyricsViewActive)
{
    IsSidebarHidden = _lyricsSidebarPref;
    IsLyricsPanelOpen = false;
}
else
{
    IsSidebarHidden = false;
}

// AFTER:
// Close lyrics panel when entering lyrics full-screen view
if (IsLyricsViewActive)
{
    IsLyricsPanelOpen = false;
}
```

- [ ] **Step 3: Build to verify no compile errors**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build may fail due to references in MainWindow.axaml.cs and LyricsView.axaml.cs — that's OK, they're fixed in Tasks 2 and 3.

---

### Task 2: Remove sidebar toggle from MainWindow AXAML and code-behind

**Files:**
- Modify: `src/Noctis/Views/MainWindow.axaml:25-27` (toggle button style)
- Modify: `src/Noctis/Views/MainWindow.axaml:36-37` (SidebarWrapper width)
- Modify: `src/Noctis/Views/MainWindow.axaml:506-526` (SidebarToggleBtn)
- Modify: `src/Noctis/Views/MainWindow.axaml.cs:26,60,62-68` (toggle wiring)

- [ ] **Step 1: Remove SidebarToggleBtn style from MainWindow.axaml**

Delete lines 25-27:
```xml
<!-- DELETE -->
<Style Selector="Button#SidebarToggleBtn:pointerover">
    <Setter Property="Opacity" Value="0.9" />
</Style>
```

- [ ] **Step 2: Change SidebarWrapper default width to 60**

At line 37, change:
```xml
<!-- BEFORE -->
Width="220"
<!-- AFTER -->
Width="60"
```

- [ ] **Step 3: Remove SidebarToggleBtn button**

Delete lines 506-526 — the entire `<Button x:Name="SidebarToggleBtn" ...>` block.

- [ ] **Step 4: Update MainWindow.axaml.cs — remove toggle field, add hover wiring**

In `MainWindow.axaml.cs`:

a) Remove the `_sidebarToggleBtn` field (line 26):
```csharp
// DELETE
private Button? _sidebarToggleBtn;
```

b) Remove the `FindControl<Button>("SidebarToggleBtn")` line (line 60):
```csharp
// DELETE
_sidebarToggleBtn = this.FindControl<Button>("SidebarToggleBtn");
```

c) Replace the `IsSidebarHidden` handler block (lines 62-68) with hover wiring. The full `_mainVmPropertyChangedHandler` should become:

```csharp
_mainVmPropertyChangedHandler = (s, e) =>
{
    var mainVm2 = (MainWindowViewModel)s!;
    if (e.PropertyName == nameof(MainWindowViewModel.IsLyricsPanelOpen))
    {
        if (_lyricsPanelWrapper != null) _lyricsPanelWrapper.Width = mainVm2.IsLyricsPanelOpen ? 340 : 0;
    }
};
vm.PropertyChanged += _mainVmPropertyChangedHandler;
```

d) After the `_mainVmPropertyChangedHandler` block, add the sidebar hover wiring:

```csharp
// Sidebar hover expand/collapse
if (_sidebarWrapper != null)
{
    _sidebarWrapper.PropertyChanged += (_, e) =>
    {
        if (e.Property == Border.IsPointerOverProperty)
            _sidebarWrapper.Width = _sidebarWrapper.IsPointerOver ? 220 : 60;
    };
}
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: May still fail due to LyricsView.axaml.cs references — fixed in Task 3.

---

### Task 3: Remove immersive mode from LyricsView

**Files:**
- Modify: `src/Noctis/Views/LyricsView.axaml.cs:27-28` (fields)
- Modify: `src/Noctis/Views/LyricsView.axaml.cs:132-142` (subscription in OnAttachedToVisualTree)
- Modify: `src/Noctis/Views/LyricsView.axaml.cs:155-161` (unsubscribe in OnDetachedFromVisualTree)
- Modify: `src/Noctis/Views/LyricsView.axaml.cs:216-222` (wide mode immersive references)
- Modify: `src/Noctis/Views/LyricsView.axaml.cs:235-259` (ApplyImmersiveMode method)

- [ ] **Step 1: Remove immersive mode fields**

Delete lines 27-28:
```csharp
// DELETE
private bool _isImmersiveMode;
private System.ComponentModel.PropertyChangedEventHandler? _mainVmImmersiveHandler;
```

- [ ] **Step 2: Remove immersive subscription in OnAttachedToVisualTree**

Delete lines 132-142:
```csharp
// DELETE
// Subscribe to sidebar hidden state for immersive scaling
if (this.FindAncestorOfType<Window>()?.DataContext is MainWindowViewModel mainVm)
{
    ApplyImmersiveMode(mainVm.IsSidebarHidden);
    _mainVmImmersiveHandler = (s, args) =>
    {
        if (args.PropertyName == nameof(MainWindowViewModel.IsSidebarHidden))
            Dispatcher.UIThread.Post(() => ApplyImmersiveMode(((MainWindowViewModel)s!).IsSidebarHidden));
    };
    mainVm.PropertyChanged += _mainVmImmersiveHandler;
}
```

- [ ] **Step 3: Remove unsubscribe in OnDetachedFromVisualTree**

Delete lines 155-161:
```csharp
// DELETE
// Unsubscribe immersive mode listener
if (_mainVmImmersiveHandler != null &&
    this.FindAncestorOfType<Window>()?.DataContext is MainWindowViewModel mainVm)
{
    mainVm.PropertyChanged -= _mainVmImmersiveHandler;
    _mainVmImmersiveHandler = null;
}
```

- [ ] **Step 4: Hardcode immersive sizes in wide mode layout**

Replace lines 216-222 (the conditional immersive/normal sizes) with the immersive values always:
```csharp
// BEFORE:
AlbumArtBorder.Width = _isImmersiveMode ? 620 : 520;
AlbumArtBorder.Height = _isImmersiveMode ? 620 : 520;
LeftContentStack.MaxWidth = _isImmersiveMode ? 680 : 560;
LyricsItemsControl.MaxWidth = _isImmersiveMode ? 620 : 500;
RightPanel.RenderTransform = _isImmersiveMode
    ? Avalonia.Media.Transformation.TransformOperations.Parse("scale(1.1, 1.1)")
    : Avalonia.Media.Transformation.TransformOperations.Parse("scale(1, 1)");

// AFTER:
AlbumArtBorder.Width = 620;
AlbumArtBorder.Height = 620;
LeftContentStack.MaxWidth = 680;
LyricsItemsControl.MaxWidth = 620;
RightPanel.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1.1, 1.1)");
```

- [ ] **Step 5: Delete ApplyImmersiveMode method**

Delete lines 235-259 — the entire `ApplyImmersiveMode` method.

- [ ] **Step 6: Build to verify all compile errors resolved**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds with 0 errors.

---

### Task 4: Adjust SidebarView layout for collapsed centering + tooltips

**Files:**
- Modify: `src/Noctis/Views/SidebarView.axaml:24` (logo margin)
- Modify: `src/Noctis/Views/SidebarView.axaml:57` (nav item tooltip)
- Modify: `src/Noctis/Views/SidebarView.axaml:77-79` (divider margin)
- Modify: `src/Noctis/Views/SidebarView.axaml:88` (favorites ListBox margin)
- Modify: `src/Noctis/Views/SidebarView.axaml:91` (favorites tooltip)
- Modify: `src/Noctis/Views/SidebarView.axaml:144` (playlist ListBox margin)
- Modify: `src/Noctis/Views/SidebarView.axaml:148` (playlist tooltip)

- [ ] **Step 1: Adjust logo margin for centering**

Line 24 — change left margin from 18 to 16:
```xml
<!-- BEFORE -->
Margin="18,16,18,14"
<!-- AFTER -->
Margin="16,16,18,14"
```

- [ ] **Step 2: Add tooltip to nav items**

In the nav item DataTemplate (line 57), add `ToolTip.Tip` to the root StackPanel:
```xml
<!-- BEFORE -->
<StackPanel Orientation="Horizontal" Spacing="14">
<!-- AFTER -->
<StackPanel Orientation="Horizontal" Spacing="14"
            ToolTip.Tip="{Binding Label}">
```

- [ ] **Step 3: Adjust divider margin**

Lines 77-79 — reduce horizontal margins:
```xml
<!-- BEFORE -->
Margin="16,8,16,8"
<!-- AFTER -->
Margin="8,8,8,8"
```

- [ ] **Step 4: Adjust Favorites ListBox margin and add tooltip**

Line 88 — change ListBox margin:
```xml
<!-- BEFORE -->
Margin="8,0,8,4"
<!-- AFTER -->
Margin="4,0,8,4"
```

Line 91 — add tooltip to the favorites item template root StackPanel:
```xml
<!-- BEFORE -->
<StackPanel Orientation="Horizontal" Spacing="10">
<!-- AFTER -->
<StackPanel Orientation="Horizontal" Spacing="10"
            ToolTip.Tip="{Binding Label}">
```

- [ ] **Step 5: Adjust Playlist ListBox margin and add tooltip**

Line 144 — change ListBox margin:
```xml
<!-- BEFORE -->
Margin="8,0,8,4"
<!-- AFTER -->
Margin="4,0,8,4"
```

Line 148 — add tooltip to the playlist item template root StackPanel:
```xml
<!-- BEFORE -->
<StackPanel Orientation="Horizontal" Spacing="10">
<!-- AFTER -->
<StackPanel Orientation="Horizontal" Spacing="10"
            ToolTip.Tip="{Binding Label}">
```

- [ ] **Step 6: Build and verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds with 0 errors.

---

### Task 5: Final verification

- [ ] **Step 1: Full build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds, 0 errors, 0 warnings (or baseline warnings only).

- [ ] **Step 2: Verify no remaining references to removed code**

Search for stale references:
```bash
grep -rn "IsSidebarHidden\|ToggleSidebarCommand\|_lyricsSidebarPref\|SidebarToggleBtn\|ApplyImmersiveMode\|_isImmersiveMode\|_mainVmImmersiveHandler" src/Noctis/
```
Expected: No matches.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Views/MainWindow.axaml src/Noctis/Views/MainWindow.axaml.cs src/Noctis/ViewModels/MainWindowViewModel.cs src/Noctis/Views/LyricsView.axaml.cs src/Noctis/Views/SidebarView.axaml
git commit -m "Add collapsible sidebar with hover expand/collapse"
```
