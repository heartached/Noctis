# Smooth Lyric Flow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the lyrics panel the same smooth ease-in-out scroll as the lyrics page, and rebuild Capture Mode as a scrolling lyric list so lyrics glide instead of swapping instantly.

**Architecture:** Extract the `SmootherStep` easing into a shared helper. Swap the lyrics panel's easing to it. Rebuild `LyricsCaptureView` from three bound text properties into an `ItemsControl` + `ScrollViewer` driven by a `SmootherStep`-eased offset animation ported from `LyricsView`.

**Tech Stack:** C# / .NET 8, Avalonia 11 (AXAML), CommunityToolkit.Mvvm.

**Build command:** `dotnet build src/Noctis/Noctis.csproj -v minimal`
(The test project does not compile at baseline — see `.claude/rules/testing.md` — so verification is build + manual. No automated tests are added.)

---

## File Structure

### New files
- `src/Noctis/Helpers/Easing.cs` — shared static easing functions (`SmootherStep`).
- `src/Noctis/Converters/ActiveLineFontSizeConverter.cs` — multi-value converter: active lyric line uses the slider font size, inactive lines a fixed size.

### Modified files
- `src/Noctis/Views/LyricsView.axaml.cs` — use the shared `Easing.SmootherStep`.
- `src/Noctis/Views/LyricsPanelView.axaml.cs` — swap ease-out-sine for `Easing.SmootherStep`.
- `src/Noctis/ViewModels/LyricsCaptureViewModel.cs` — replace prev/current/upcoming projection with `LyricLines` + `ActiveLineIndex`.
- `src/Noctis/Views/LyricsCaptureView.axaml` — replace the centered 3-line stack with a `ScrollViewer` + `ItemsControl`.
- `src/Noctis/Views/LyricsCaptureView.axaml.cs` — add the scroll-follow animation.

---

## Task 1: Shared SmootherStep easing helper

**Files:**
- Create: `src/Noctis/Helpers/Easing.cs`
- Modify: `src/Noctis/Views/LyricsView.axaml.cs`

- [ ] **Step 1: Create the easing helper**

Create `src/Noctis/Helpers/Easing.cs` with this exact content:

```csharp
using System;

namespace Noctis.Helpers;

/// <summary>Shared easing functions for lyric scroll animations.</summary>
public static class Easing
{
    /// <summary>
    /// Cubic ease-in-out (Ken Perlin's smootherstep). Glides in and out instead
    /// of starting or stopping abruptly. Input is clamped to [0, 1].
    /// </summary>
    public static double SmootherStep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }
}
```

- [ ] **Step 2: Point LyricsView at the shared helper**

In `src/Noctis/Views/LyricsView.axaml.cs`, find this private method (near the end of the file):

```csharp
    private static double SmootherStep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }
```

Delete that entire method.

Then find the single call site inside `AnimateScroll`:

```csharp
            var eased = SmootherStep(t);
```

Change it to:

```csharp
            var eased = Easing.SmootherStep(t);
```

`LyricsView.axaml.cs` already has `using Noctis.Helpers;` at the top — no new using is needed. If for any reason it is missing, add `using Noctis.Helpers;`.

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Helpers/Easing.cs src/Noctis/Views/LyricsView.axaml.cs
git commit -m "refactor(lyrics): extract shared SmootherStep easing helper"
```

---

## Task 2: Lyrics panel easing swap

**Files:**
- Modify: `src/Noctis/Views/LyricsPanelView.axaml.cs`

- [ ] **Step 1: Swap the easing curve**

In `src/Noctis/Views/LyricsPanelView.axaml.cs`, find this line inside `AnimateScroll`:

```csharp
            var eased = Math.Sin(t * Math.PI / 2.0); // ease-out-sine
```

Change it to:

```csharp
            var eased = Easing.SmootherStep(t);
```

- [ ] **Step 2: Add the using directive**

At the top of `src/Noctis/Views/LyricsPanelView.axaml.cs`, the using block currently is:

```csharp
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.ViewModels;
```

Add `using Noctis.Helpers;` so the block becomes:

```csharp
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.ViewModels;
```

Note: `System` is no longer strictly required if `Math.Sin`/`Math.PI` was the only `Math` use — but `AnimateScroll`'s sibling code (`Math.Min`, `Math.Max`, `Math.Abs` in `ScrollToLine`) still uses `Math`. Leave any existing `using System;` / `Math` references as they are; only change the one easing line and add the `Noctis.Helpers` using.

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Views/LyricsPanelView.axaml.cs
git commit -m "feat(lyrics-panel): use SmootherStep easing for scroll animation"
```

---

## Task 3: Active-line font-size converter

**Files:**
- Create: `src/Noctis/Converters/ActiveLineFontSizeConverter.cs`

- [ ] **Step 1: Create the converter**

Create `src/Noctis/Converters/ActiveLineFontSizeConverter.cs` with this exact content:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Picks the font size for a Capture Mode lyric line. The active line uses the
/// user-controlled size (the font-size slider); inactive lines use a fixed
/// smaller size. Expects two bound values: [bool isActive, double activeSize].
/// </summary>
public sealed class ActiveLineFontSizeConverter : IMultiValueConverter
{
    /// <summary>Font size used for every non-active line.</summary>
    private const double InactiveFontSize = 48;

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isActive = values.Count > 0 && values[0] is bool b && b;
        if (isActive && values.Count > 1 && values[1] is double activeSize)
            return activeSize;
        return InactiveFontSize;
    }
}
```

Notes for the engineer:
- The `Noctis.Converters` namespace already exists (other converters live in `src/Noctis/Converters/`). Follow that folder.
- Avalonia 11's `IMultiValueConverter.Convert` signature is exactly `object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)`. If the installed Avalonia version differs, match the real interface signature — but it should be as written.

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Converters/ActiveLineFontSizeConverter.cs
git commit -m "feat(capture): add active-line font-size converter"
```

---

## Task 4: Rebuild the Capture Mode view model and view as a scrolling list

This task changes the view model and the AXAML together, because the AXAML's
compiled bindings reference the view model members — they must change in lockstep
to keep the build green.

**Files:**
- Modify: `src/Noctis/ViewModels/LyricsCaptureViewModel.cs`
- Modify: `src/Noctis/Views/LyricsCaptureView.axaml`

- [ ] **Step 1: Rewrite the view model**

Replace the ENTIRE contents of `src/Noctis/ViewModels/LyricsCaptureViewModel.cs` with:

```csharp
using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// View model for full-screen Capture Mode. Renders the shared LyricsViewModel
/// line list and mirrors its active-line index. Runs no timer of its own.
/// </summary>
public partial class LyricsCaptureViewModel : ViewModelBase
{
    private readonly PlayerViewModel _player;
    private readonly LyricsViewModel _lyrics;

    public LyricsCaptureViewModel(PlayerViewModel player, LyricsViewModel lyrics)
    {
        _player = player;
        _lyrics = lyrics;
        _activeLineIndex = _lyrics.ActiveLineIndex;
        _lyrics.PropertyChanged += OnLyricsPropertyChanged;
        _player.PropertyChanged += OnPlayerPropertyChanged;
    }

    /// <summary>Raised when the user requests to leave Capture Mode (X button or Esc).</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty]
    private double _fontSize = 72;

    public double MinFontSize => 48;
    public double MaxFontSize => 96;

    /// <summary>All lyric lines for the current track (the shared LyricsViewModel collection).</summary>
    public BulkObservableCollection<LyricLine> LyricLines => _lyrics.LyricLines;

    /// <summary>Index of the active line; mirrors LyricsViewModel.ActiveLineIndex.</summary>
    [ObservableProperty]
    private int _activeLineIndex = -1;

    public IBrush BackgroundBrush => _lyrics.FullBackgroundBrush;
    public Bitmap? AlbumArt => _player.AlbumArt;
    public string TrackTitle => _player.CurrentTrack?.Title ?? string.Empty;
    public string TrackArtist => _player.CurrentTrack?.Artist ?? string.Empty;
    public bool IsPlaying => _player.IsPlaying;

    [RelayCommand]
    private void TogglePlayPause() => _player.PlayPauseCommand.Execute(null);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Snap the font size to the nearest preset (60/72/80) when within 4px.</summary>
    partial void OnFontSizeChanged(double value)
    {
        foreach (var preset in new[] { 60d, 72d, 80d })
        {
            if (Math.Abs(value - preset) <= 4 && value != preset)
            {
                FontSize = preset;
                return;
            }
        }
    }

    private void OnLyricsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.ActiveLineIndex))
            ActiveLineIndex = _lyrics.ActiveLineIndex;
        else if (e.PropertyName == nameof(LyricsViewModel.FullBackgroundBrush))
            OnPropertyChanged(nameof(BackgroundBrush));
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsPlaying))
        {
            OnPropertyChanged(nameof(IsPlaying));
        }
        else if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack)
                 || e.PropertyName == nameof(PlayerViewModel.AlbumArt))
        {
            OnPropertyChanged(nameof(TrackTitle));
            OnPropertyChanged(nameof(TrackArtist));
            OnPropertyChanged(nameof(AlbumArt));
        }
    }
}
```

What changed and why:
- Removed `PreviousLine`, `CurrentLine`, `UpcomingLines`, `RefreshLines()`, and the `using System.Collections.ObjectModel;`.
- Added `LyricLines` (exposes `LyricsViewModel.LyricLines`, type `BulkObservableCollection<LyricLine>` from `Noctis.Helpers`) and an `[ObservableProperty] ActiveLineIndex` that mirrors the lyrics VM.
- `LyricLines` is a fixed collection instance that `LyricsViewModel` repopulates in place, so it never needs a change notification.

- [ ] **Step 2: Rewrite the view's center section**

In `src/Noctis/Views/LyricsCaptureView.axaml`:

First, add the converter namespace. The opening `UserControl` tag currently declares `xmlns:vm` and `xmlns:m`. Add `xmlns:conv="using:Noctis.Converters"` to it, so the tag becomes:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Noctis.ViewModels"
             xmlns:m="using:Noctis.Models"
             xmlns:conv="using:Noctis.Converters"
             x:Class="Noctis.Views.LyricsCaptureView"
             x:DataType="vm:LyricsCaptureViewModel"
             x:Name="CaptureRoot"
             Focusable="True"
             Background="{Binding BackgroundBrush}">
```

Next, add a `UserControl.Resources` block holding the converter. Insert it immediately before the existing `<UserControl.Styles>` line:

```xml
    <UserControl.Resources>
        <conv:ActiveLineFontSizeConverter x:Key="ActiveLineFontSize"/>
    </UserControl.Resources>
```

Then replace the entire center block — everything from `<!-- Center: lyric stack -->` through its closing `</StackPanel>` (the current block is the `<StackPanel HorizontalAlignment="Center" VerticalAlignment="Center" ...>` containing the `PreviousLine` TextBlock, the `CurrentLine` TextBlock, and the `UpcomingLines` ItemsControl) — with:

```xml
        <!-- Center: scrolling lyric list -->
        <ScrollViewer x:Name="CaptureScrollViewer"
                      VerticalScrollBarVisibility="Hidden"
                      HorizontalScrollBarVisibility="Disabled">
            <ItemsControl x:Name="CaptureLyricsItems"
                          ItemsSource="{Binding LyricLines}"
                          MaxWidth="1400" HorizontalAlignment="Center"
                          Margin="0,540">
                <ItemsControl.ItemTemplate>
                    <DataTemplate x:DataType="m:LyricLine">
                        <TextBlock Text="{Binding Text}"
                                   Foreground="White" FontWeight="SemiBold"
                                   Opacity="{Binding LineOpacity}"
                                   TextAlignment="Center" TextWrapping="Wrap"
                                   Margin="0,14">
                            <TextBlock.FontSize>
                                <MultiBinding Converter="{StaticResource ActiveLineFontSize}">
                                    <Binding Path="IsActive"/>
                                    <Binding Path="$parent[ItemsControl].((vm:LyricsCaptureViewModel)DataContext).FontSize"/>
                                </MultiBinding>
                            </TextBlock.FontSize>
                            <TextBlock.Effect>
                                <DropShadowEffect OffsetX="0" OffsetY="2" BlurRadius="8"
                                                  Color="Black" Opacity="0.6"/>
                            </TextBlock.Effect>
                        </TextBlock>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>
```

Leave the top-left metadata `StackPanel` and the bottom `ControlPanel` `Border` exactly as they are — only the center block changes.

Notes:
- `LyricLine` exposes `Text` (string), `IsActive` (bool, observable), and `LineOpacity` (double, observable, default 1.0) — all already used by the lyrics page.
- `Margin="0,540"` gives the list vertical headroom so the active line can be scrolled to the viewport center even near the start/end of the song.
- The `$parent[ItemsControl].((vm:LyricsCaptureViewModel)DataContext).FontSize` path reaches the Capture VM's slider value from inside the item template; this `$parent[...]` traversal pattern is already used in `LyricsView.axaml`.

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded, 0 errors. (The existing `LyricsCaptureView.axaml.cs` still compiles — it only references `ControlPanel`, `DataContext`, and `CloseCommand`, all unchanged.)

If the build fails on the `MultiBinding` path syntax, the fallback is to write the second `<Binding>` as
`<Binding Path="DataContext.FontSize" RelativeSource="{RelativeSource AncestorType=ItemsControl}"/>`
— functionally identical. Do not change anything else.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/ViewModels/LyricsCaptureViewModel.cs src/Noctis/Views/LyricsCaptureView.axaml
git commit -m "feat(capture): rebuild lyrics as a scrolling list"
```

---

## Task 5: Capture Mode scroll-follow animation

**Files:**
- Modify: `src/Noctis/Views/LyricsCaptureView.axaml.cs`

- [ ] **Step 1: Replace the code-behind**

Replace the ENTIRE contents of `src/Noctis/Views/LyricsCaptureView.axaml.cs` with:

```csharp
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsCaptureView : UserControl
{
    private readonly DispatcherTimer _autoHideTimer;
    private Border? _controlPanel;

    private LyricsCaptureViewModel? _subscribedVm;
    private int _lastScrolledIndex = -1;
    private DispatcherTimer? _scrollAnimTimer;

    public LyricsCaptureView()
    {
        InitializeComponent();
        _controlPanel = this.FindControl<Border>("ControlPanel");

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoHideTimer.Tick += (_, _) => HideControlPanel();

        PointerMoved += OnPointerMoved;
        KeyDown += OnKeyDown;
        AttachedToVisualTree += (_, _) => Focus();
        DataContextChanged += OnDataContextChanged;
        PropertyChanged += OnViewPropertyChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }
        if (DataContext is LyricsCaptureViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            _subscribedVm = vm;
        }
    }

    // When the overlay becomes visible, jump straight to the current line (no glide).
    private void OnViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.NewValue is true && _subscribedVm != null)
        {
            _lastScrolledIndex = -1;
            ScrollToActiveLine(_subscribedVm.ActiveLineIndex, animate: false);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsCaptureViewModel.ActiveLineIndex) && _subscribedVm != null)
            ScrollToActiveLine(_subscribedVm.ActiveLineIndex, animate: true);
    }

    private void ScrollToActiveLine(int index, bool animate)
    {
        if (index < 0)
        {
            _lastScrolledIndex = -1;
            return;
        }
        if (index == _lastScrolledIndex) return;
        _lastScrolledIndex = index;

        CancelScrollAnimation();

        // Brief delay so layout settles after the active line changes.
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                if (CaptureLyricsItems == null || index >= CaptureLyricsItems.ItemCount) return;

                var presenter = CaptureLyricsItems.GetVisualDescendants()
                    .OfType<ItemsPresenter>()
                    .FirstOrDefault();
                if (presenter == null) return;

                var panel = presenter.GetVisualChildren().FirstOrDefault() as Panel;
                if (panel == null || index >= panel.Children.Count) return;

                var targetChild = panel.Children[index];
                if (CaptureScrollViewer == null) return;

                var childBounds = targetChild.TransformToVisual(panel);
                if (childBounds == null) return;

                var childTop = childBounds.Value.Transform(new Point(0, 0)).Y;
                var childHeight = targetChild.Bounds.Height;
                var viewportHeight = CaptureScrollViewer.Viewport.Height;

                // Center the active line vertically.
                var targetOffset = childTop - (viewportHeight / 2.0) + (childHeight / 2.0);
                targetOffset = Math.Max(0, targetOffset);

                var currentOffset = CaptureScrollViewer.Offset.Y;
                var diff = Math.Abs(targetOffset - currentOffset);

                if (!animate || diff < 2)
                {
                    CaptureScrollViewer.Offset = new Vector(0, targetOffset);
                    return;
                }

                AnimateScroll(currentOffset, targetOffset,
                    (int)Math.Min(1050, Math.Max(650, diff * 0.85)));
            }
            catch { }
        }, TimeSpan.FromMilliseconds(10));
    }

    private void AnimateScroll(double from, double to, int durationMs)
    {
        CancelScrollAnimation();
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollAnimTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (CaptureScrollViewer == null)
            {
                CancelScrollAnimation();
                return;
            }

            var elapsed = sw.Elapsed.TotalMilliseconds;
            var t = Math.Min(1.0, elapsed / durationMs);
            var eased = Easing.SmootherStep(t);
            CaptureScrollViewer.Offset = new Vector(0, from + (to - from) * eased);

            if (t >= 1.0)
            {
                CaptureScrollViewer.Offset = new Vector(0, to);
                CancelScrollAnimation();
            }
        };
        timer.Start();
    }

    private void CancelScrollAnimation()
    {
        _scrollAnimTimer?.Stop();
        _scrollAnimTimer = null;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        ShowControlPanel();
        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is LyricsCaptureViewModel vm)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ShowControlPanel()
    {
        if (_controlPanel is null) return;
        _controlPanel.Opacity = 1;
        _controlPanel.IsHitTestVisible = true;
    }

    private void HideControlPanel()
    {
        _autoHideTimer.Stop();
        if (_controlPanel is null) return;
        _controlPanel.Opacity = 0;
        _controlPanel.IsHitTestVisible = false;
    }
}
```

What this adds:
- Subscribes to the Capture VM's `ActiveLineIndex` and, when it changes, animates the `ScrollViewer` so the active line glides to the vertical center using `Easing.SmootherStep` over a distance-scaled 650–1050ms duration — the same shape `LyricsView` uses.
- When the overlay becomes visible (`IsVisibleProperty` → true), it jumps to the current line without animation so opening Capture Mode is not a long scroll.
- `CaptureScrollViewer` and `CaptureLyricsItems` are the Avalonia-generated fields from the `x:Name`s added in Task 4.
- The auto-hide timer, pointer, and `Esc` handling are unchanged from the previous version.

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded, 0 errors.

If the build reports that `CaptureScrollViewer` or `CaptureLyricsItems` does not exist, confirm Task 4 added the `x:Name="CaptureScrollViewer"` and `x:Name="CaptureLyricsItems"` attributes to `LyricsCaptureView.axaml` — the generated fields come from those names.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Views/LyricsCaptureView.axaml.cs
git commit -m "feat(capture): glide lyrics with SmootherStep scroll animation"
```

---

## Task 6: Build and manual verification

**No automated tests** — the test project does not compile at baseline (`.claude/rules/testing.md`). Verify by running the app.

- [ ] **Step 1: Final build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run the app and verify**

Play a track with synced lyrics and check:

- [ ] **Lyrics panel:** as lines advance, the panel scroll glides smoothly — eases in at the start and out at the end, no abrupt jump. The active line still ends centered.
- [ ] **Capture Mode:** open it from the lyrics page. Lyric lines render as a centered list; the active line is large (font-size slider value), other lines smaller and faded by distance.
- [ ] **Capture Mode flow:** as playback advances, lines physically slide upward and the active line glides to the vertical center with the same smooth ease as the lyrics page — not an instant swap.
- [ ] **Capture Mode open:** opening Capture Mode snaps directly to the current line (no long initial scroll).
- [ ] The font-size slider still scales the active line; play/pause, close (X), `Esc`, and the auto-hiding control panel still work.
- [ ] **Lyrics page:** behaves exactly as before — no regression from the `SmootherStep` extraction.

- [ ] **Step 3: Report results**

Report which checklist items passed and any that failed, with the exact build command output. Do not claim success for any item not actually observed.

---

## Notes

- Capture Mode and the lyrics page render the same `LyricLine` instances; this is intentional and safe (observable data objects) and keeps opacity/active state consistent across views.
- `Helpers/SmoothScrollAnimator.cs` is a separate general-purpose momentum scroller and is intentionally left untouched.
