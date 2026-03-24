# Lyrics Sidebar Toggle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a toggle button in the lyrics view that slides the sidebar in/out for an immersive full-width lyrics experience.

**Architecture:** Add `IsSidebarHidden` state to `MainWindowViewModel`, wrap the sidebar in an animated-width `Border` in `MainWindow.axaml`, and place a chevron toggle button in the outer `<Panel>` overlay visible only when `IsLyricsViewActive`. The sidebar auto-shows on non-lyrics navigation and re-hides on lyrics re-entry based on session preference. Code-behind drives the width change (since `DoubleTransition` needs numeric property changes, not bool bindings).

**Tech Stack:** Avalonia UI, CommunityToolkit.Mvvm, XAML transitions (DoubleTransition + CubicEaseOut)

**Spec:** `docs/superpowers/specs/2026-03-24-lyrics-sidebar-toggle-design.md`

---

### Task 1: Add ViewModel state and command

**Files:**
- Modify: `src/Noctis/ViewModels/MainWindowViewModel.cs`

- [ ] **Step 1: Add the sidebar hidden property and command**

Near the existing `IsLyricsViewActive` property (line 59), add:

```csharp
// ── Sidebar toggle (lyrics immersive mode) ──

/// <summary>Session preference: user wants sidebar hidden in lyrics view.</summary>
private bool _lyricsSidebarPref;

/// <summary>Whether the sidebar is currently hidden (animated to width 0).</summary>
[ObservableProperty] private bool _isSidebarHidden;

[RelayCommand]
private void ToggleSidebar()
{
    _lyricsSidebarPref = !_lyricsSidebarPref;
    IsSidebarHidden = _lyricsSidebarPref;
}
```

- [ ] **Step 2: Add sidebar restore/re-apply logic in `OnCurrentViewChanged`**

In `OnCurrentViewChanged` (line 432), after the existing `OnPropertyChanged(nameof(IsPlaybackBarVisible))` call (line 436), add:

```csharp
// Sidebar: force-show when leaving lyrics, re-apply pref when entering
if (IsLyricsViewActive)
    IsSidebarHidden = _lyricsSidebarPref;
else
    IsSidebarHidden = false;
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/ViewModels/MainWindowViewModel.cs
git commit -m "Add IsSidebarHidden state and ToggleSidebar command"
```

---

### Task 2: Animate sidebar and add toggle button in MainWindow XAML

**Files:**
- Modify: `src/Noctis/Views/MainWindow.axaml`

- [ ] **Step 1: Wrap the SidebarView in an animated Border**

Replace the existing SidebarView element (lines 30-32):

```xml
<views:SidebarView DockPanel.Dock="Left"
                   Width="220"
                   DataContext="{Binding Sidebar}" />
```

With:

```xml
<Border x:Name="SidebarWrapper" DockPanel.Dock="Left"
        Width="220"
        ClipToBounds="True">
    <Border.Transitions>
        <Transitions>
            <DoubleTransition Property="Width" Duration="0:0:0.2">
                <DoubleTransition.Easing>
                    <CubicEaseOut/>
                </DoubleTransition.Easing>
            </DoubleTransition>
        </Transitions>
    </Border.Transitions>
    <views:SidebarView Width="220"
                       DataContext="{Binding Sidebar}" />
</Border>
```

The inner `SidebarView` keeps `Width="220"` so it doesn't shrink — the wrapper's `ClipToBounds` hides the overflow during animation.

- [ ] **Step 2: Add the toggle button in the outer Panel overlay**

The MainWindow already has an outer `<Panel>` (line 24) wrapping the `DockPanel`. Place the toggle button as a sibling of the `DockPanel`, after the `DragDropOverlay` closing tag (near end of file), so it floats above all content:

```xml
<!-- Sidebar toggle button (lyrics immersive mode) -->
<Button x:Name="SidebarToggleBtn"
        Command="{Binding ToggleSidebarCommand}"
        IsVisible="{Binding IsLyricsViewActive}"
        Background="Transparent"
        BorderThickness="0"
        Padding="6,8"
        Margin="8,8,0,0"
        VerticalAlignment="Top"
        HorizontalAlignment="Left"
        Cursor="Hand"
        Opacity="0.5"
        FontSize="14"
        Foreground="White"
        Content="«">
    <Button.Transitions>
        <Transitions>
            <DoubleTransition Property="Opacity" Duration="0:0:0.15" />
        </Transitions>
    </Button.Transitions>
</Button>
```

Placing it in the outer `<Panel>` gives true overlay positioning without layout hacks.

- [ ] **Step 3: Add hover style for the toggle button**

Add a `<Window.Styles>` section (before `<Panel>`) or append to existing styles:

```xml
<Style Selector="Button#SidebarToggleBtn:pointerover">
    <Setter Property="Opacity" Value="0.9" />
</Style>
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Noctis/Views/MainWindow.axaml
git commit -m "Add sidebar wrapper with slide animation and toggle button overlay"
```

---

### Task 3: Wire sidebar width from code-behind

**Files:**
- Modify: `src/Noctis/Views/MainWindow.axaml.cs`

- [ ] **Step 1: Add cached control fields and property change handler**

Add fields alongside existing handler fields (near line 22-23):

```csharp
private System.ComponentModel.PropertyChangedEventHandler? _mainVmPropertyChangedHandler;
private Border? _sidebarWrapper;
private Button? _sidebarToggleBtn;
```

- [ ] **Step 2: Subscribe in Loaded handler and cache controls**

In the existing `Loaded += async (_, _) =>` block (line 30), after the `vm.TopBar.PropertyChanged += _topBarPropertyChangedHandler;` line (line 51), add:

```csharp
// Wire sidebar toggle (lyrics immersive mode)
_sidebarWrapper = this.FindControl<Border>("SidebarWrapper");
_sidebarToggleBtn = this.FindControl<Button>("SidebarToggleBtn");
_mainVmPropertyChangedHandler = (s, e) =>
{
    if (e.PropertyName == nameof(MainWindowViewModel.IsSidebarHidden))
    {
        var hidden = ((MainWindowViewModel)s!).IsSidebarHidden;
        if (_sidebarWrapper != null) _sidebarWrapper.Width = hidden ? 0 : 220;
        if (_sidebarToggleBtn != null) _sidebarToggleBtn.Content = hidden ? "»" : "«";
    }
};
vm.PropertyChanged += _mainVmPropertyChangedHandler;
```

- [ ] **Step 3: Unsubscribe in cleanup**

In the existing cleanup block (near line 122-133, inside `if (DataContext is MainWindowViewModel vm)`), add after the `_topBarPropertyChangedHandler` unsubscribe:

```csharp
if (_mainVmPropertyChangedHandler != null)
    vm.PropertyChanged -= _mainVmPropertyChangedHandler;
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 5: Manual test**

1. Launch the app, play a track, enter lyrics view
2. Click the « button in the top-left — sidebar should slide out over ~200ms
3. Button should change to »
4. Click again — sidebar slides back in
5. Switch to another view (e.g., Songs) — sidebar should be visible
6. Return to lyrics — sidebar should auto-hide (if it was hidden before)
7. Close and reopen app — sidebar should default to visible (session-only)

- [ ] **Step 6: Commit**

```bash
git add src/Noctis/Views/MainWindow.axaml.cs
git commit -m "Wire sidebar toggle width and chevron from code-behind"
```
