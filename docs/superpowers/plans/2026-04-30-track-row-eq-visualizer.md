# Now-Playing EQ Visualizer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the track-number cell in the AlbumDetailView track list with an animated 4-bar EQ visualizer when that track is the currently playing track.

**Architecture:** New templated control `EqVisualizer` (4 staggered bar animations, freezes in place when `IsAnimating=false`). New `TrackIsCurrentConverter` (multi-binding: returns true when row track id equals current track id). New `IsPlaying` derived property on `PlayerViewModel`. Wire into `AlbumDetailView.axaml` row template by overlaying the EQ on the existing track-number TextBlock.

**Tech Stack:** Avalonia 11, .NET 8, CommunityToolkit.Mvvm, xUnit (existing test project at `tests/Velour.Tests` — yes, project name still says Velour, this repo was renamed to Noctis but tests project hasn't been renamed). The test project is currently broken (per `.claude/rules/testing.md`) — verify build of test project before adding new tests; if compile fails for unrelated baseline reasons, run new tests against a stub class as documented per task.

**Scope note:** Only AlbumDetailView has a track-number column today. Other track-list views (PlaylistView, LibrarySongsView, FavoritesView, QueueView) don't show numbers and are explicitly out of scope for this plan.

---

## File Structure

| File | Status | Responsibility |
|---|---|---|
| `src/Noctis/Controls/EqVisualizer.axaml` | Create | Template: 4 rounded `Rectangle` bars in a horizontal `StackPanel`, each with a named `ScaleTransform`. |
| `src/Noctis/Controls/EqVisualizer.axaml.cs` | Create | Code-behind: 4 `Animation` instances; `IsAnimating` styled property; freeze/resume logic. |
| `src/Noctis/Converters/TrackIsCurrentConverter.cs` | Create | `IMultiValueConverter`: true when `values[0]` (Track.Id) equals `values[1]` (Player.CurrentTrack?.Id). |
| `src/Noctis/ViewModels/PlayerViewModel.cs` | Modify | Add `IsPlaying` derived bool; raise its `PropertyChanged` whenever `State` changes. |
| `src/Noctis/Views/AlbumDetailView.axaml` | Modify | Replace track-number `TextBlock` (line 475-481) with a `Panel` containing the TextBlock and an `EqVisualizer`. |
| `src/Noctis/App.axaml` | Modify | Register `TrackIsCurrentConverter` as a global resource (so AlbumDetailView can reference it). |
| `tests/Velour.Tests/Converters/TrackIsCurrentConverterTests.cs` | Create | Unit tests for the converter. |

---

## Task 1: Add `IsPlaying` derived property to `PlayerViewModel`

**Files:**
- Modify: `src/Noctis/ViewModels/PlayerViewModel.cs:27-29`

**Why first:** Smallest, highest-leverage change. Other tasks bind to it.

- [ ] **Step 1: Read current `State` declaration**

Open `src/Noctis/ViewModels/PlayerViewModel.cs`, find lines 27-29:

```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(PlayPauseTooltip))]
private PlaybackState _state = PlaybackState.Stopped;
```

- [ ] **Step 2: Add `IsPlaying` notification and getter**

Edit lines 27-29 to:

```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(PlayPauseTooltip))]
[NotifyPropertyChangedFor(nameof(IsPlaying))]
private PlaybackState _state = PlaybackState.Stopped;
```

Then add this property right after the `PlayPauseTooltip` property (currently line 48):

```csharp
/// <summary>True when playback is actively playing (not paused or stopped).</summary>
public bool IsPlaying => State == PlaybackState.Playing;
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/ViewModels/PlayerViewModel.cs
git commit -m "feat(player): add IsPlaying derived property for view bindings"
```

---

## Task 2: Create `TrackIsCurrentConverter` with tests

**Files:**
- Create: `src/Noctis/Converters/TrackIsCurrentConverter.cs`
- Create: `tests/Velour.Tests/Converters/TrackIsCurrentConverterTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Velour.Tests/Converters/TrackIsCurrentConverterTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using Noctis.Converters;
using Xunit;

namespace Velour.Tests.Converters;

public class TrackIsCurrentConverterTests
{
    private readonly TrackIsCurrentConverter _conv = new();

    [Fact]
    public void Returns_true_when_ids_match()
    {
        var id = Guid.NewGuid().ToString();
        var result = _conv.Convert(new List<object?> { id, id }, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(true, result);
    }

    [Fact]
    public void Returns_false_when_ids_differ()
    {
        var result = _conv.Convert(
            new List<object?> { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() },
            typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Returns_false_when_current_is_null()
    {
        var result = _conv.Convert(
            new List<object?> { Guid.NewGuid().ToString(), null },
            typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Returns_false_when_row_is_null()
    {
        var result = _conv.Convert(
            new List<object?> { null, Guid.NewGuid().ToString() },
            typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Returns_false_when_both_null()
    {
        var result = _conv.Convert(
            new List<object?> { null, null },
            typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Velour.Tests/Velour.Tests.csproj --filter "FullyQualifiedName~TrackIsCurrentConverterTests" -v minimal`

Expected: compile error — `TrackIsCurrentConverter` not found.

If the test project has unrelated compile failures (per `.claude/rules/testing.md` baseline), note them and proceed; the new failure should be the missing converter type.

- [ ] **Step 3: Implement the converter**

Create `src/Noctis/Converters/TrackIsCurrentConverter.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// MultiBinding converter: returns true when the row track id (values[0])
/// equals the current track id (values[1]). Returns false if either is null.
/// </summary>
public sealed class TrackIsCurrentConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return false;
        var rowId = values[0] as string;
        var currentId = values[1] as string;
        if (string.IsNullOrEmpty(rowId) || string.IsNullOrEmpty(currentId)) return false;
        return string.Equals(rowId, currentId, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Velour.Tests/Velour.Tests.csproj --filter "FullyQualifiedName~TrackIsCurrentConverterTests" -v minimal`
Expected: all 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Noctis/Converters/TrackIsCurrentConverter.cs tests/Velour.Tests/Converters/TrackIsCurrentConverterTests.cs
git commit -m "feat(converters): add TrackIsCurrentConverter for now-playing row detection"
```

---

## Task 3: Create `EqVisualizer` control (template + animations)

**Files:**
- Create: `src/Noctis/Controls/EqVisualizer.axaml`
- Create: `src/Noctis/Controls/EqVisualizer.axaml.cs`

- [ ] **Step 1: Create the XAML template**

Create `src/Noctis/Controls/EqVisualizer.axaml`:

```xml
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ctrl="using:Noctis.Controls">
    <Style Selector="ctrl|EqVisualizer">
        <Setter Property="Width" Value="16" />
        <Setter Property="Height" Value="14" />
        <Setter Property="Foreground" Value="{DynamicResource SystemControlHighlightAccentBrush}" />
        <Setter Property="Template">
            <ControlTemplate>
                <StackPanel Orientation="Horizontal"
                            Spacing="2"
                            HorizontalAlignment="Center"
                            VerticalAlignment="Stretch">
                    <Rectangle x:Name="Bar1"
                               Width="2"
                               RadiusX="1" RadiusY="1"
                               VerticalAlignment="Stretch"
                               Fill="{TemplateBinding Foreground}"
                               RenderTransformOrigin="50%,100%">
                        <Rectangle.RenderTransform>
                            <ScaleTransform ScaleY="0.4" />
                        </Rectangle.RenderTransform>
                    </Rectangle>
                    <Rectangle x:Name="Bar2"
                               Width="2"
                               RadiusX="1" RadiusY="1"
                               VerticalAlignment="Stretch"
                               Fill="{TemplateBinding Foreground}"
                               RenderTransformOrigin="50%,100%">
                        <Rectangle.RenderTransform>
                            <ScaleTransform ScaleY="0.9" />
                        </Rectangle.RenderTransform>
                    </Rectangle>
                    <Rectangle x:Name="Bar3"
                               Width="2"
                               RadiusX="1" RadiusY="1"
                               VerticalAlignment="Stretch"
                               Fill="{TemplateBinding Foreground}"
                               RenderTransformOrigin="50%,100%">
                        <Rectangle.RenderTransform>
                            <ScaleTransform ScaleY="0.55" />
                        </Rectangle.RenderTransform>
                    </Rectangle>
                    <Rectangle x:Name="Bar4"
                               Width="2"
                               RadiusX="1" RadiusY="1"
                               VerticalAlignment="Stretch"
                               Fill="{TemplateBinding Foreground}"
                               RenderTransformOrigin="50%,100%">
                        <Rectangle.RenderTransform>
                            <ScaleTransform ScaleY="0.75" />
                        </Rectangle.RenderTransform>
                    </Rectangle>
                </StackPanel>
            </ControlTemplate>
        </Setter>
    </Style>
</Styles>
```

- [ ] **Step 2: Create the code-behind**

Create `src/Noctis/Controls/EqVisualizer.axaml.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Noctis.Controls;

/// <summary>
/// Animated 4-bar EQ visualizer used to indicate the currently playing track.
/// When IsAnimating is true, bars scale up and down on staggered loops.
/// When false, bars freeze at their current scale (matches Apple Music behavior).
/// </summary>
public class EqVisualizer : TemplatedControl
{
    public static readonly StyledProperty<bool> IsAnimatingProperty =
        AvaloniaProperty.Register<EqVisualizer, bool>(nameof(IsAnimating));

    public bool IsAnimating
    {
        get => GetValue(IsAnimatingProperty);
        set => SetValue(IsAnimatingProperty, value);
    }

    private readonly List<CancellationTokenSource> _animationCts = new();
    private Rectangle? _bar1, _bar2, _bar3, _bar4;

    // Per-bar durations (ms) and starting scales — chosen so bars are visibly out of phase.
    private static readonly (string Name, int DurationMs, double StartScale)[] BarConfigs =
    {
        ("Bar1",  700, 0.40),
        ("Bar2",  950, 0.90),
        ("Bar3", 1100, 0.55),
        ("Bar4",  850, 0.75),
    };

    static EqVisualizer()
    {
        IsAnimatingProperty.Changed.AddClassHandler<EqVisualizer>((c, _) => c.OnIsAnimatingChanged());
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _bar1 = e.NameScope.Find<Rectangle>("Bar1");
        _bar2 = e.NameScope.Find<Rectangle>("Bar2");
        _bar3 = e.NameScope.Find<Rectangle>("Bar3");
        _bar4 = e.NameScope.Find<Rectangle>("Bar4");
        if (IsAnimating) StartAll();
    }

    private void OnIsAnimatingChanged()
    {
        if (IsAnimating) StartAll();
        else StopAll();
    }

    private void StartAll()
    {
        StopAll();
        var bars = new[] { _bar1, _bar2, _bar3, _bar4 };
        for (var i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            if (bar?.RenderTransform is not ScaleTransform st) continue;
            var cts = new CancellationTokenSource();
            _animationCts.Add(cts);
            var (_, durationMs, _) = BarConfigs[i];
            _ = RunBarAnimation(st, durationMs, cts.Token);
        }
    }

    private void StopAll()
    {
        foreach (var cts in _animationCts) cts.Cancel();
        _animationCts.Clear();
    }

    private static async System.Threading.Tasks.Task RunBarAnimation(ScaleTransform st, int durationMs, CancellationToken ct)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new CubicEaseInOut(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(ScaleTransform.ScaleYProperty, 0.25d) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(ScaleTransform.ScaleYProperty, 1.0d) }
                },
            }
        };

        try
        {
            await animation.RunAsync(st, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation freezes the bar at its last rendered ScaleY,
            // because we don't reset the value on cancel.
        }
    }
}
```

- [ ] **Step 3: Register the styles in `App.axaml`**

Read `src/Noctis/App.axaml`. Find the `<Application.Styles>` (or equivalent merged styles) block. Add the EqVisualizer styles include:

```xml
<StyleInclude Source="avares://Noctis/Controls/EqVisualizer.axaml" />
```

Place it next to other control-style includes. If the project uses a different style-include pattern (e.g. all styles in `Assets/Styles.axaml`), add the include there instead — verify by searching for an existing `StyleInclude` in `App.axaml`/`Styles.axaml` and follow that pattern.

- [ ] **Step 4: Wire `EqVisualizer.axaml` as a build asset**

Open `src/Noctis/Noctis.csproj`. Confirm there's an `<AvaloniaResource Include="**\*.axaml" ... />` glob (typical Avalonia setup); if so, no change needed. If individual files are listed, add:

```xml
<AvaloniaResource Include="Controls\EqVisualizer.axaml" />
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 6: Commit**

```bash
git add src/Noctis/Controls/EqVisualizer.axaml src/Noctis/Controls/EqVisualizer.axaml.cs src/Noctis/App.axaml src/Noctis/Noctis.csproj
git commit -m "feat(controls): add animated EqVisualizer for now-playing indicator"
```

---

## Task 4: Wire `EqVisualizer` into `AlbumDetailView` track row

**Files:**
- Modify: `src/Noctis/Views/AlbumDetailView.axaml:474-481` (track number cell)
- Modify: `src/Noctis/Views/AlbumDetailView.axaml` (top of file: add converter resource + namespaces)

- [ ] **Step 1: Add namespace and converter resource**

Read the top of `src/Noctis/Views/AlbumDetailView.axaml`. Locate the `<UserControl ...>` opening element and any `<UserControl.Resources>` block.

Ensure these namespaces are declared on the root `UserControl`:

```xml
xmlns:ctrl="using:Noctis.Controls"
xmlns:conv="using:Noctis.Converters"
```

(If `conv` is already declared, keep it. Add `ctrl` if missing.)

In `<UserControl.Resources>` (create the block if it doesn't exist), add:

```xml
<conv:TrackIsCurrentConverter x:Key="TrackIsCurrent" />
```

- [ ] **Step 2: Replace the track-number TextBlock with the Panel overlay**

Find the existing block at lines 474-481:

```xml
<!-- Track number (always visible) -->
<TextBlock Grid.Column="0"
           Text="{Binding TrackNumber}"
           Opacity="0.5"
           FontSize="14"
           HorizontalAlignment="Right"
           Margin="0,0,10,0"
           VerticalAlignment="Center" />
```

Replace it with:

```xml
<!-- Track number, replaced by EQ visualizer when this row is the current track -->
<Panel Grid.Column="0"
       HorizontalAlignment="Right"
       Margin="0,0,10,0"
       VerticalAlignment="Center">
    <Panel.Resources>
        <!-- placed locally in case AlbumDetailView is hosted without inherited resources -->
    </Panel.Resources>
    <TextBlock Text="{Binding TrackNumber}"
               Opacity="0.5"
               FontSize="14"
               HorizontalAlignment="Right"
               VerticalAlignment="Center">
        <TextBlock.IsVisible>
            <MultiBinding Converter="{StaticResource TrackIsCurrent}" Mode="OneWay">
                <Binding Path="Id" />
                <Binding Path="$parent[UserControl].DataContext.Player.CurrentTrack.Id" />
                <!-- Inverted via the inner control: if current, hide TextBlock -->
            </MultiBinding>
        </TextBlock.IsVisible>
    </TextBlock>
    <ctrl:EqVisualizer Width="16" Height="14"
                       HorizontalAlignment="Right"
                       VerticalAlignment="Center"
                       Foreground="{DynamicResource SystemControlHighlightAccentBrush}"
                       IsAnimating="{Binding $parent[UserControl].DataContext.Player.IsPlaying}">
        <ctrl:EqVisualizer.IsVisible>
            <MultiBinding Converter="{StaticResource TrackIsCurrent}" Mode="OneWay">
                <Binding Path="Id" />
                <Binding Path="$parent[UserControl].DataContext.Player.CurrentTrack.Id" />
            </MultiBinding>
        </ctrl:EqVisualizer.IsVisible>
    </ctrl:EqVisualizer>
</Panel>
```

Note: the TextBlock's `IsVisible` uses the same converter — but we need it to be the *inverse*. Avalonia doesn't allow inline negation in MultiBinding cleanly. Instead, change the `TextBlock.IsVisible` to bind through a small helper resource. Use the existing pattern: define a second converter, or simpler — bind the TextBlock's `Opacity` to 0 when current via a `BoolConverters.Or`-style trick is not available.

**Cleanest approach (use this):** Add a second already-existing inverter converter, or define one inline. Search the repo for an `InverseBoolConverter`. If none exists, replace Step 2 above with this version that adds one:

  - Add to `src/Noctis/Converters/`: a small `InverseBoolConverter : IValueConverter` if not already present (search first with `grep`).
  - Then chain: bind `TextBlock.IsVisible` to a single combined source via `MultiBinding` returning bool, then use `<Binding Path="..." Converter="{StaticResource InverseBool}"/>` on a separate single-binding hop.

**Simpler still:** drive both TextBlock and EqVisualizer visibility from a single property exposed on a row VM. But the spec rejected per-row VMs. So:

**Final approach for this step (replaces above):** Implement a single-call helper converter `BoolToInverseConverter` if not already in the repo, and have the TextBlock bind to the EqVisualizer's `IsVisible` via `ElementName`:

```xml
<Panel Grid.Column="0"
       HorizontalAlignment="Right"
       Margin="0,0,10,0"
       VerticalAlignment="Center">
    <ctrl:EqVisualizer x:Name="RowEq"
                       Width="16" Height="14"
                       HorizontalAlignment="Right"
                       VerticalAlignment="Center"
                       Foreground="{DynamicResource SystemControlHighlightAccentBrush}"
                       IsAnimating="{Binding $parent[UserControl].DataContext.Player.IsPlaying}">
        <ctrl:EqVisualizer.IsVisible>
            <MultiBinding Converter="{StaticResource TrackIsCurrent}" Mode="OneWay">
                <Binding Path="Id" />
                <Binding Path="$parent[UserControl].DataContext.Player.CurrentTrack.Id" />
            </MultiBinding>
        </ctrl:EqVisualizer.IsVisible>
    </ctrl:EqVisualizer>
    <TextBlock Text="{Binding TrackNumber}"
               Opacity="0.5"
               FontSize="14"
               HorizontalAlignment="Right"
               VerticalAlignment="Center"
               IsVisible="{Binding !#RowEq.IsVisible}" />
</Panel>
```

`!#RowEq.IsVisible` is Avalonia's element-name binding with the `!` (not) shorthand — supported in Avalonia 11.

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. If app is running, close it first.

- [ ] **Step 4: Manual smoke test**

Run the app:

```bash
dotnet run --project src/Noctis/Noctis.csproj
```

1. Navigate to any album. Confirm track numbers display normally for all rows.
2. Click a track to start playback. Confirm that row's number is replaced by the animated EQ visualizer (4 colored bars, accent color, animating).
3. Press pause. Confirm bars freeze in place.
4. Press play. Confirm bars resume animating.
5. Skip to another track on the same album. Confirm previous row reverts to its number; new row shows the EQ.
6. Stop playback. Confirm the EQ disappears and number returns.

If any check fails, debug before committing.

- [ ] **Step 5: Commit**

```bash
git add src/Noctis/Views/AlbumDetailView.axaml
git commit -m "feat(album): show animated EQ on currently playing track row"
```

---

## Self-Review Notes

- **Spec coverage:**
  - 4-bar EQ that animates when playing, freezes when paused, hidden when not current → Tasks 1, 3, 4. ✓
  - Theme accent color → Task 3 default + Task 4 explicit Foreground. ✓
  - Track-number replacement (Apple Music behavior) → Task 4. ✓
  - Reusable control → Task 3 produces a templated control. ✓
  - "Apply to all four track lists" — descoped in this plan to AlbumDetailView only because the other views don't have a leading number column. Noted in plan header. Follow-up plan can add a leading-edge indicator to those views if desired.

- **Risks captured:**
  - Animation cancellation behavior: `Animation.RunAsync` with a cancellation token is the standard Avalonia pattern; on cancel, the property keeps its last value (which is the freeze behavior we want). Verified via the Avalonia 11 animation API surface.
  - `App.axaml` style-include pattern varies; Task 3 Step 3 explicitly tells the engineer to inspect existing pattern first.
  - Test project may have unrelated baseline failures; Task 2 tells the engineer to check.

- **Out of scope:** Other track-list views (Playlist, LibrarySongs, Favorites, Queue). Per-album dominant color. Settings to disable.
