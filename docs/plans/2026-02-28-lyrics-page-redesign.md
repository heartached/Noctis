# Lyrics Page Redesign Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Redesign the Lyrics page with larger album art, adaptive backgrounds from album art colors, explicit/lossless badges, full playback controls, responsive layout, and hidden island bar.

**Architecture:** Rewrite `LyricsView.axaml` in-place. Extend existing `DominantColorExtractor` with bitmap pixel sampling. Wire adaptive brushes through `LyricsViewModel`. Hide playback island via `MainWindowViewModel.IsLyricsViewActive`. Responsive stacking via codebehind `Bounds.Width` check.

**Tech Stack:** C# 12 / .NET 8 / Avalonia UI 11 / CommunityToolkit.Mvvm

---

## Task 1: Extend DominantColorExtractor with Bitmap Color Extraction

**Files:**
- Modify: `src/Velour/Services/DominantColorExtractor.cs`

This file already has `CreateGradientFromColor(Color)` and HSL utilities. We add a method to extract the dominant color from a Bitmap and a method to generate left+right adaptive brushes.

**Step 1: Add ExtractDominantColor and GenerateAdaptiveBrushes methods**

Add these methods to the existing `DominantColorExtractor` static class (after `CreateGradientFromColor`, around line 37):

```csharp
/// <summary>
/// Extracts the dominant color from an Avalonia Bitmap by downscaling and
/// center-weighted pixel averaging.  Skips near-black and near-white pixels
/// so album borders and pure-white text don't skew the result.
/// </summary>
public static Color ExtractDominantColor(Bitmap bitmap)
{
    const int sampleSize = 50;

    using var scaled = bitmap.CreateScaledBitmap(
        new PixelSize(sampleSize, sampleSize),
        BitmapInterpolationMode.LowQuality);

    using var buf = scaled.Lock(PixelFormat.Bgra8888);

    double totalR = 0, totalG = 0, totalB = 0, totalWeight = 0;
    var centerX = sampleSize / 2.0;
    var centerY = sampleSize / 2.0;
    var maxDist = Math.Sqrt(centerX * centerX + centerY * centerY);

    unsafe
    {
        var ptr = (byte*)buf.Address;
        var stride = buf.RowBytes;

        for (int y = 0; y < sampleSize; y++)
        {
            var row = ptr + y * stride;
            for (int x = 0; x < sampleSize; x++)
            {
                byte b = row[x * 4 + 0];
                byte g = row[x * 4 + 1];
                byte r = row[x * 4 + 2];
                // byte a = row[x * 4 + 3]; // unused

                // Skip near-black and near-white
                var brightness = (r + g + b) / 3.0;
                if (brightness < 15 || brightness > 240) continue;

                // Center-weighted: pixels closer to center matter more
                var dist = Math.Sqrt((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY));
                var weight = 1.0 - (dist / maxDist) * 0.6; // center=1.0, edge=0.4

                totalR += r * weight;
                totalG += g * weight;
                totalB += b * weight;
                totalWeight += weight;
            }
        }
    }

    if (totalWeight < 1)
        return Color.FromRgb(0x1A, 0x1A, 0x2E); // fallback dark

    return Color.FromRgb(
        (byte)(totalR / totalWeight),
        (byte)(totalG / totalWeight),
        (byte)(totalB / totalWeight));
}

/// <summary>
/// Generates a pair of adaptive brushes from a dominant color.
/// Left brush: darker atmospheric gradient for the control panel.
/// Right brush: subtle gradient for the lyrics area.
/// </summary>
public static (LinearGradientBrush Left, LinearGradientBrush Right) GenerateAdaptiveBrushes(Color dominant)
{
    var left = CreateGradientFromColor(dominant);

    // Right panel: more subdued version — darker overall, color as accent
    var (hue, sat, _) = RgbToHsl(dominant.R, dominant.G, dominant.B);
    if (sat < 0.15) sat = 0.15;

    var rDarkest = HslToColor(hue, sat * 0.4, 0.04);
    var rDark    = HslToColor(hue, sat * 0.6, 0.08);
    var rMid     = HslToColor(hue, sat * 0.5, 0.12);

    var right = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint   = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(rDark,    0.0),
            new GradientStop(rDarkest, 0.4),
            new GradientStop(rMid,     1.0),
        }
    };

    return (left, right);
}
```

**Required using additions** (at top of file):

```csharp
using Avalonia.Media.Imaging;
using Avalonia.Platform;
```

Note: Avalonia's `Bitmap.Lock()` returns an `ILockedFramebuffer` with `Address` property. The `PixelFormat` is in `Avalonia.Platform` namespace. The `CreateScaledBitmap` method is an Avalonia extension. If it doesn't exist, use `bitmap.CreateScaledBitmap(new PixelSize(...), ...)` from `Avalonia.Media.Imaging` — check the actual Avalonia 11 API at build time and adjust. An alternative is `using var rt = new RenderTargetBitmap(new PixelSize(50,50)); rt.Render(new Image { Source = bitmap })` pattern.

**Step 2: Build to verify**

Run: `dotnet build src/Velour/Velour.csproj -v minimal`
Expected: Build succeeds. If `CreateScaledBitmap` API differs, adjust to use `RenderTargetBitmap` scaling approach.

**Step 3: Commit**

```bash
git add src/Velour/Services/DominantColorExtractor.cs
git commit -m "feat(lyrics): add bitmap color extraction to DominantColorExtractor"
```

---

## Task 2: Wire Adaptive Colors into LyricsViewModel

**Files:**
- Modify: `src/Velour/ViewModels/LyricsViewModel.cs`

The ViewModel already has `LyricsBackgroundBrush` and `BackgroundPresets`. We replace the preset system with album-art adaptive colors.

**Step 1: Add LeftPanelBrush property and remove preset system**

1. Add a new observable property near line 75 (after `_lyricsBackgroundBrush`):

```csharp
/// <summary>Background brush for the left (controls) panel.</summary>
[ObservableProperty]
private IBrush _leftPanelBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x38));
```

2. In the constructor (around line 108), **remove** `InitializeBackgroundPresets()` and the preset brush application (lines 108, 133-136). Replace with subscribing to `AlbumArt` changes:

```csharp
// Subscribe to album art changes for adaptive background
_player.PropertyChanged += OnPlayerAlbumArtChanged;

// Initialize adaptive background if album art is already loaded
if (_player.AlbumArt != null)
    UpdateAdaptiveBackground(_player.AlbumArt);
```

3. Add the handler method (after `OnPlayerPropertyChanged`):

```csharp
private void OnPlayerAlbumArtChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
{
    if (e.PropertyName == nameof(PlayerViewModel.AlbumArt))
        UpdateAdaptiveBackground(_player.AlbumArt);
}

private void UpdateAdaptiveBackground(Bitmap? albumArt)
{
    if (albumArt == null)
    {
        // Fallback to default dark gradient
        var defaultColor = Color.FromRgb(0x1A, 0x1A, 0x2E);
        LyricsBackgroundBrush = DominantColorExtractor.CreateGradientFromColor(defaultColor);
        LeftPanelBrush = DominantColorExtractor.CreateGradientFromColor(defaultColor);
        return;
    }

    try
    {
        var dominant = DominantColorExtractor.ExtractDominantColor(albumArt);
        var (left, right) = DominantColorExtractor.GenerateAdaptiveBrushes(dominant);
        LeftPanelBrush = left;
        LyricsBackgroundBrush = right;
    }
    catch
    {
        // Fallback on any error
        var defaultColor = Color.FromRgb(0x1A, 0x1A, 0x2E);
        LyricsBackgroundBrush = DominantColorExtractor.CreateGradientFromColor(defaultColor);
        LeftPanelBrush = DominantColorExtractor.CreateGradientFromColor(defaultColor);
    }
}
```

4. **Remove** these methods/fields that are no longer needed:
   - `BackgroundPresets` collection (line 100)
   - `InitializeBackgroundPresets()` (lines 183-211)
   - `AddPreset()` (lines 213-223)
   - `AddGradientPreset()` (lines 225-244)
   - `SelectBackgroundPresetCommand` (lines 172-181)

5. **Remove** the `using Velour.Models` import only if `BackgroundPreset` was the only model used from it — but `LyricLine`, `PlaybackState`, `RepeatMode` etc. are also used, so keep the import.

6. In `Dispose()` (line 962), add unsubscribe:

```csharp
_player.PropertyChanged -= OnPlayerAlbumArtChanged;
```

Note: `OnPlayerPropertyChanged` already subscribes to `_player.PropertyChanged`. The new `OnPlayerAlbumArtChanged` is a **separate** handler for clarity. Alternatively, add the `AlbumArt` check inside the existing `OnPlayerPropertyChanged` handler to avoid double-subscribing. **Preferred approach**: merge into `OnPlayerPropertyChanged`:

```csharp
// In existing OnPlayerPropertyChanged, add:
else if (e.PropertyName == nameof(PlayerViewModel.AlbumArt))
{
    UpdateAdaptiveBackground(_player.AlbumArt);
}
```

This avoids the extra subscription. Remove the separate `OnPlayerAlbumArtChanged` method and the extra `_player.PropertyChanged += OnPlayerAlbumArtChanged` line.

**Step 2: Add using for Bitmap**

At top of file, add if not present:

```csharp
using Avalonia.Media.Imaging;
```

**Step 3: Build to verify**

Run: `dotnet build src/Velour/Velour.csproj -v minimal`
Expected: Build succeeds.

**Step 4: Commit**

```bash
git add src/Velour/ViewModels/LyricsViewModel.cs
git commit -m "feat(lyrics): wire adaptive album-art background, remove preset system"
```

---

## Task 3: Add IsLyricsViewActive to MainWindowViewModel + Hide Island

**Files:**
- Modify: `src/Velour/ViewModels/MainWindowViewModel.cs`
- Modify: `src/Velour/Views/MainWindow.axaml`

**Step 1: Add IsLyricsViewActive property**

In `MainWindowViewModel.cs`, add a computed property. Since `CurrentView` is an `[ObservableProperty]`, CommunityToolkit generates `OnCurrentViewChanged`. We need to notify when `IsLyricsViewActive` changes.

Find the `_currentView` field (line 49). Add below:

```csharp
/// <summary>True when the lyrics view is the active content view.</summary>
public bool IsLyricsViewActive => CurrentView == _lyricsVm;
```

Then find the existing `OnCurrentViewChanged` partial method (search for it — CommunityToolkit generates it from `[ObservableProperty]`). If it already exists, add `OnPropertyChanged(nameof(IsLyricsViewActive));` inside it. If it doesn't exist as an explicit override, add:

```csharp
partial void OnCurrentViewChanged(ViewModelBase value)
{
    OnPropertyChanged(nameof(IsLyricsViewActive));
}
```

**Important:** Check if `OnCurrentViewChanged` already exists in the file. If it does, just add the `OnPropertyChanged` line to it. Search for `OnCurrentViewChanged` in the file first.

**Step 2: Bind PlaybackBarView visibility in MainWindow.axaml**

In `src/Velour/Views/MainWindow.axaml`, find the PlaybackBarView (around line 35-50). Currently:

```xml
<views:PlaybackBarView
    DataContext="{Binding Player}"
    IsVisible="{Binding HasContent}"
    ...
```

The `DataContext` is `Player` (PlayerViewModel), so `HasContent` resolves on PlayerVM. We need to also check `IsLyricsViewActive` from the parent MainWindowViewModel. We can't directly bind to both from different DataContexts in a single `IsVisible`.

**Solution:** Use a `MultiBinding` with a converter, OR change the binding approach. Simpler: use `IsVisible` with a binding to a new property on `PlayerViewModel` that the `MainWindowViewModel` sets. Even simpler: Move the visibility logic to a wrapping panel.

**Simplest approach:** Wrap the PlaybackBarView in a Panel and use two `IsVisible` bindings:

```xml
<Panel IsVisible="{Binding !IsLyricsViewActive}">
    <views:PlaybackBarView
        DataContext="{Binding Player}"
        IsVisible="{Binding HasContent}"
        HorizontalAlignment="Center"
        VerticalAlignment="Center"
        Margin="0,4,0,4">
        <views:PlaybackBarView.Transitions>
            <Transitions>
                <DoubleTransition Property="Opacity" Duration="0:0:0.2">
                    <DoubleTransition.Easing>
                        <CubicEaseOut/>
                    </DoubleTransition.Easing>
                </DoubleTransition>
            </Transitions>
        </views:PlaybackBarView.Transitions>
    </views:PlaybackBarView>
</Panel>
```

The outer Panel binds to `{Binding !IsLyricsViewActive}` (MainWindowViewModel context), while the inner PlaybackBarView still has its own DataContext=Player.

**Step 3: Build to verify**

Run: `dotnet build src/Velour/Velour.csproj -v minimal`
Expected: Build succeeds.

**Step 4: Commit**

```bash
git add src/Velour/ViewModels/MainWindowViewModel.cs src/Velour/Views/MainWindow.axaml
git commit -m "feat(lyrics): hide playback island when lyrics view is active"
```

---

## Task 4: Rewrite LyricsView.axaml

**Files:**
- Rewrite: `src/Velour/Views/LyricsView.axaml`

This is the largest task. The full XAML needs to be rewritten with:
- Adaptive background bindings (LeftPanelBrush, LyricsBackgroundBrush)
- Larger cover art (~380px, fills panel width minus margins)
- Explicit badge inline with title
- Lossless/quality badges below album
- CornerRadius 12, HighQuality interpolation on art
- Same playback controls (same bindings, same commands)
- Preserved lyrics display (same ItemsControl, ScrollViewer names, DataTemplate)
- Responsive support via x:Name on layout elements (codebehind swaps)

**Critical:** The XAML must keep these `x:Name` attributes that the codebehind references:
- `LyricsScrollViewer` (ScrollViewer)
- `LyricsItemsControl` (ItemsControl)
- `SeekSlider` (Slider, referenced if any codebehind uses it)

**Step 1: Write the new LyricsView.axaml**

Replace the entire file content. Key structural changes from the current version:

1. **Root background**: Bind to `{Binding LeftPanelBrush}` instead of hardcoded `#1A1A2E`
2. **Left panel Border background**: Bind to `{Binding LeftPanelBrush}` instead of `#252538`
3. **Album art**: Width/Height changed from 300 to `{Binding $parent[Border].Bounds.Width}` minus margins, or fixed larger size. Use `RenderOptions.BitmapInterpolationMode="HighQuality"` on the Image.
4. **Title row**: StackPanel with title TextBlock + explicit badge Border (inline)
5. **Quality row**: New StackPanel below album with `AudioQualityBadge` + `CodecShortName` pills
6. **Right panel background**: Bind to `{Binding LyricsBackgroundBrush}` instead of hardcoded gradient
7. **Fade overlays**: Use semi-transparent versions that work with dynamic backgrounds. Bind fade colors to the adaptive brush or use a generic dark fade that works on any bg.

Full AXAML structure (write the complete file):

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Velour.ViewModels"
             xmlns:m="using:Velour.Models"
             xmlns:conv="using:Velour.Converters"
             x:Class="Velour.Views.LyricsView"
             x:DataType="vm:LyricsViewModel">

    <UserControl.Resources>
        <conv:BoolToOpacityConverter x:Key="BoolToOpacity"/>
    </UserControl.Resources>

    <UserControl.Styles>
        <!-- Seek slider style -->
        <Style Selector="Slider.lyrics-seek">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Foreground" Value="#E8E8E8"/>
            <Setter Property="Height" Value="28"/>
        </Style>
        <Style Selector="Slider.lyrics-seek /template/ Track">
            <Setter Property="Height" Value="4"/>
        </Style>
        <Style Selector="Slider.lyrics-seek /template/ RepeatButton /template/ Border">
            <Setter Property="Background" Value="#E8E8E8"/>
            <Setter Property="Height" Value="4"/>
            <Setter Property="CornerRadius" Value="2"/>
        </Style>
        <Style Selector="Slider.lyrics-seek /template/ RepeatButton#PART_IncreaseButton /template/ Border">
            <Setter Property="Background" Value="#4A4A4A"/>
            <Setter Property="Height" Value="4"/>
            <Setter Property="CornerRadius" Value="2"/>
        </Style>
        <Style Selector="Slider.lyrics-seek /template/ Thumb">
            <Setter Property="Background" Value="#FFFFFF"/>
            <Setter Property="Width" Value="14"/>
            <Setter Property="Height" Value="14"/>
        </Style>
        <Style Selector="Slider.lyrics-seek /template/ Thumb /template/ Border">
            <Setter Property="Background" Value="#FFFFFF"/>
            <Setter Property="CornerRadius" Value="7"/>
        </Style>

        <!-- Volume slider style -->
        <Style Selector="Slider.lyrics-volume">
            <Setter Property="Orientation" Value="Horizontal"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Height" Value="28"/>
            <Setter Property="MinWidth" Value="100"/>
        </Style>
        <Style Selector="Slider.lyrics-volume /template/ Track">
            <Setter Property="Height" Value="4"/>
        </Style>
        <Style Selector="Slider.lyrics-volume /template/ RepeatButton /template/ Border">
            <Setter Property="Background" Value="White"/>
            <Setter Property="Height" Value="4"/>
            <Setter Property="CornerRadius" Value="2"/>
        </Style>
        <Style Selector="Slider.lyrics-volume /template/ RepeatButton#PART_IncreaseButton /template/ Border">
            <Setter Property="Background" Value="#555555"/>
            <Setter Property="Height" Value="4"/>
            <Setter Property="CornerRadius" Value="2"/>
        </Style>
        <Style Selector="Slider.lyrics-volume /template/ Thumb">
            <Setter Property="Background" Value="White"/>
            <Setter Property="Width" Value="12"/>
            <Setter Property="Height" Value="12"/>
        </Style>
        <Style Selector="Slider.lyrics-volume /template/ Thumb /template/ Border">
            <Setter Property="Background" Value="White"/>
            <Setter Property="CornerRadius" Value="6"/>
        </Style>

        <!-- Pill button style -->
        <Style Selector="Button.pill-btn">
            <Setter Property="Background" Value="#33FFFFFF"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Padding" Value="16,8"/>
            <Setter Property="CornerRadius" Value="18"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Cursor" Value="Hand"/>
        </Style>
        <Style Selector="Button.pill-btn:pointerover">
            <Setter Property="Background" Value="#55FFFFFF"/>
        </Style>

        <!-- Control button style -->
        <Style Selector="Button.control-btn">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Padding" Value="0"/>
            <Setter Property="Cursor" Value="Hand"/>
        </Style>
        <Style Selector="Button.control-btn:pointerover">
            <Setter Property="Opacity" Value="0.8"/>
        </Style>

        <!-- Quality badge pill -->
        <Style Selector="Border.quality-badge">
            <Setter Property="Background" Value="#33FFFFFF"/>
            <Setter Property="CornerRadius" Value="8"/>
            <Setter Property="Padding" Value="8,2"/>
            <Setter Property="VerticalAlignment" Value="Center"/>
        </Style>
    </UserControl.Styles>

    <Grid x:Name="RootLayout" ColumnDefinitions="420,*">

        <!-- LEFT PANEL: Album Art + Controls -->
        <Border Grid.Column="0"
                x:Name="LeftPanel"
                Background="{Binding LeftPanelBrush}"
                Padding="20,30">
            <Grid RowDefinitions="Auto,*,Auto">

                <!-- Back Button -->
                <Button Grid.Row="0"
                        Command="{Binding Player.ShowLyricsCommand}"
                        Background="Transparent"
                        BorderThickness="0"
                        Padding="8"
                        HorizontalAlignment="Left"
                        Margin="-10,-20,0,20"
                        Cursor="Hand">
                    <TextBlock Text="&#xE72B;"
                               FontFamily="Segoe Fluent Icons"
                               FontSize="18"
                               Foreground="White"
                               Opacity="0.7"/>
                </Button>

                <!-- Album Art + Track Info + Controls -->
                <ScrollViewer Grid.Row="1"
                              VerticalScrollBarVisibility="Auto"
                              HorizontalScrollBarVisibility="Disabled">
                    <StackPanel VerticalAlignment="Center" Spacing="24">

                        <!-- Album Art (large, fills width) -->
                        <Border CornerRadius="12"
                                ClipToBounds="True"
                                HorizontalAlignment="Center"
                                BoxShadow="0 8 32 0 #40000000"
                                MaxWidth="380"
                                MaxHeight="380">
                            <Panel>
                                <Border Background="#333344"
                                        MinWidth="280" MinHeight="280">
                                    <TextBlock Text="&#x266B;"
                                               FontSize="60"
                                               Foreground="#555566"
                                               HorizontalAlignment="Center"
                                               VerticalAlignment="Center"/>
                                </Border>
                                <Image Source="{Binding Player.AlbumArt}"
                                       Stretch="UniformToFill"
                                       RenderOptions.BitmapInterpolationMode="HighQuality"
                                       IsVisible="{Binding Player.AlbumArt, Converter={x:Static ObjectConverters.IsNotNull}}"/>
                            </Panel>
                        </Border>

                        <!-- Track Info -->
                        <StackPanel Spacing="6" HorizontalAlignment="Center" MaxWidth="380">
                            <!-- Title + Explicit badge inline -->
                            <StackPanel Orientation="Horizontal"
                                        HorizontalAlignment="Center"
                                        Spacing="6">
                                <TextBlock Text="{Binding Player.CurrentTrack.Title, FallbackValue='No track playing'}"
                                           FontSize="22"
                                           FontWeight="Bold"
                                           Foreground="White"
                                           TextAlignment="Center"
                                           MaxWidth="340"
                                           TextTrimming="CharacterEllipsis"
                                           VerticalAlignment="Center"/>
                                <Border Classes="explicit-badge compact"
                                        Margin="0"
                                        IsVisible="{Binding Player.CurrentTrack.IsExplicit}">
                                    <TextBlock Text="E"
                                               Classes="explicit-badge-text compact"
                                               FontSize="9"
                                               Opacity="0.9"/>
                                </Border>
                            </StackPanel>

                            <!-- Artist (clickable) -->
                            <Button Background="Transparent"
                                    BorderThickness="0"
                                    Padding="0"
                                    HorizontalAlignment="Center"
                                    Cursor="Hand"
                                    Command="{Binding ViewArtistCommand}">
                                <TextBlock Text="{Binding Player.CurrentTrack.Artist, FallbackValue=''}"
                                           FontSize="16"
                                           Foreground="#AAAAAA"
                                           TextAlignment="Center"
                                           MaxWidth="360"
                                           TextTrimming="CharacterEllipsis"/>
                            </Button>

                            <!-- Album (clickable) -->
                            <Button Background="Transparent"
                                    BorderThickness="0"
                                    Padding="0"
                                    HorizontalAlignment="Center"
                                    Cursor="Hand"
                                    Command="{Binding ViewAlbumCommand}"
                                    IsVisible="{Binding Player.CurrentTrack.Album, Converter={x:Static StringConverters.IsNotNullOrEmpty}}">
                                <TextBlock Text="{Binding Player.CurrentTrack.Album, FallbackValue=''}"
                                           FontSize="14"
                                           Foreground="#777777"
                                           TextAlignment="Center"
                                           MaxWidth="360"
                                           TextTrimming="CharacterEllipsis"/>
                            </Button>

                            <!-- Quality badges (Lossless + Codec) -->
                            <StackPanel Orientation="Horizontal"
                                        HorizontalAlignment="Center"
                                        Spacing="6"
                                        IsVisible="{Binding Player.CurrentTrack.IsLossless}">
                                <Border Classes="quality-badge">
                                    <TextBlock Text="{Binding Player.CurrentTrack.AudioQualityBadge}"
                                               FontSize="11"
                                               FontWeight="SemiBold"
                                               Foreground="White"
                                               Opacity="0.8"/>
                                </Border>
                                <Border Classes="quality-badge"
                                        IsVisible="{Binding Player.CurrentTrack.CodecShortName, Converter={x:Static StringConverters.IsNotNullOrEmpty}}">
                                    <TextBlock Text="{Binding Player.CurrentTrack.CodecShortName}"
                                               FontSize="11"
                                               FontWeight="SemiBold"
                                               Foreground="White"
                                               Opacity="0.8"/>
                                </Border>
                            </StackPanel>
                        </StackPanel>

                        <!-- Progress Bar -->
                        <StackPanel Spacing="4" MaxWidth="380" HorizontalAlignment="Stretch">
                            <Slider Value="{Binding Player.PositionFraction, Mode=TwoWay}"
                                    Classes="lyrics-seek"
                                    Minimum="0" Maximum="1"
                                    x:Name="SeekSlider"/>
                            <Grid>
                                <TextBlock Text="{Binding Player.PositionText}"
                                           FontSize="12"
                                           Foreground="#888888"
                                           HorizontalAlignment="Left"/>
                                <TextBlock Text="{Binding Player.DurationText}"
                                           FontSize="12"
                                           Foreground="#888888"
                                           HorizontalAlignment="Right"/>
                            </Grid>
                        </StackPanel>

                        <!-- Playback Controls -->
                        <StackPanel Orientation="Horizontal"
                                    HorizontalAlignment="Center"
                                    Spacing="16">
                            <!-- Shuffle -->
                            <Button Classes="control-btn"
                                    Command="{Binding Player.ToggleShuffleCommand}"
                                    Width="40" Height="40"
                                    ToolTip.Tip="Shuffle">
                                <Viewbox Width="20" Height="20"
                                         Opacity="{Binding Player.IsShuffleEnabled, Converter={StaticResource BoolToOpacity}, ConverterParameter=0.4;1.0}">
                                    <Canvas Width="24" Height="24">
                                        <Path Fill="White" Data="{StaticResource ShuffleIcon}"/>
                                    </Canvas>
                                </Viewbox>
                            </Button>
                            <!-- Previous -->
                            <Button Classes="control-btn"
                                    Command="{Binding Player.PreviousCommand}"
                                    Width="44" Height="44"
                                    ToolTip.Tip="Previous">
                                <Viewbox Width="24" Height="24">
                                    <Canvas Width="24" Height="24">
                                        <Path Fill="White" Data="{StaticResource PreviousIcon}"/>
                                    </Canvas>
                                </Viewbox>
                            </Button>
                            <!-- Play/Pause -->
                            <Button Classes="control-btn"
                                    Command="{Binding Player.PlayPauseCommand}"
                                    Width="56" Height="56"
                                    Background="#44FFFFFF"
                                    CornerRadius="28"
                                    ToolTip.Tip="Play/Pause">
                                <Panel>
                                    <Viewbox Width="26" Height="26"
                                             IsVisible="{Binding Player.State, Converter={x:Static ObjectConverters.NotEqual}, ConverterParameter={x:Static m:PlaybackState.Playing}}">
                                        <Canvas Width="24" Height="24">
                                            <Path Fill="White" Data="{StaticResource PlayIcon}"/>
                                        </Canvas>
                                    </Viewbox>
                                    <Viewbox Width="26" Height="26"
                                             IsVisible="{Binding Player.State, Converter={x:Static ObjectConverters.Equal}, ConverterParameter={x:Static m:PlaybackState.Playing}}">
                                        <Canvas Width="24" Height="24">
                                            <Path Fill="White" Data="{StaticResource PauseIcon}"/>
                                        </Canvas>
                                    </Viewbox>
                                </Panel>
                            </Button>
                            <!-- Next -->
                            <Button Classes="control-btn"
                                    Command="{Binding Player.NextCommand}"
                                    Width="44" Height="44"
                                    ToolTip.Tip="Next">
                                <Viewbox Width="24" Height="24">
                                    <Canvas Width="24" Height="24">
                                        <Path Fill="White" Data="{StaticResource NextIcon}"/>
                                    </Canvas>
                                </Viewbox>
                            </Button>
                            <!-- Repeat -->
                            <Button Classes="control-btn"
                                    Command="{Binding Player.CycleRepeatCommand}"
                                    Width="40" Height="40"
                                    ToolTip.Tip="Repeat">
                                <Panel>
                                    <Viewbox Width="20" Height="20"
                                             Opacity="0.4"
                                             IsVisible="{Binding Player.RepeatMode, Converter={x:Static ObjectConverters.Equal}, ConverterParameter={x:Static m:RepeatMode.Off}}">
                                        <Canvas Width="24" Height="24">
                                            <Path Fill="White" Data="{StaticResource RepeatAllIcon}"/>
                                        </Canvas>
                                    </Viewbox>
                                    <Viewbox Width="20" Height="20"
                                             IsVisible="{Binding Player.RepeatMode, Converter={x:Static ObjectConverters.Equal}, ConverterParameter={x:Static m:RepeatMode.All}}">
                                        <Canvas Width="24" Height="24">
                                            <Path Fill="White" Data="{StaticResource RepeatAllIcon}"/>
                                        </Canvas>
                                    </Viewbox>
                                    <Viewbox Width="20" Height="20"
                                             IsVisible="{Binding Player.RepeatMode, Converter={x:Static ObjectConverters.Equal}, ConverterParameter={x:Static m:RepeatMode.One}}">
                                        <Canvas Width="24" Height="24">
                                            <Path Fill="White" Data="{StaticResource RepeatOneIcon}"/>
                                        </Canvas>
                                    </Viewbox>
                                </Panel>
                            </Button>
                        </StackPanel>
                    </StackPanel>
                </ScrollViewer>

                <!-- Bottom Controls: Lyrics, Queue, Volume -->
                <StackPanel Grid.Row="2"
                            Orientation="Horizontal"
                            HorizontalAlignment="Center"
                            Spacing="12"
                            Margin="0,20,0,0">
                    <!-- Lyrics Button (active) -->
                    <Button Classes="pill-btn"
                            Background="#E74856"
                            Command="{Binding Player.ShowLyricsCommand}">
                        <StackPanel Orientation="Horizontal" Spacing="6">
                            <TextBlock Text="&#xE8F1;"
                                       FontFamily="Segoe Fluent Icons"
                                       FontSize="14"
                                       VerticalAlignment="Center"/>
                            <TextBlock Text="Lyrics" VerticalAlignment="Center"/>
                        </StackPanel>
                    </Button>
                    <!-- Queue Button -->
                    <Button Classes="pill-btn"
                            Click="OnQueueButtonClick">
                        <StackPanel Orientation="Horizontal" Spacing="6">
                            <TextBlock Text="&#xE8FD;"
                                       FontFamily="Segoe Fluent Icons"
                                       FontSize="14"
                                       VerticalAlignment="Center"/>
                            <TextBlock Text="Queue" VerticalAlignment="Center"/>
                        </StackPanel>
                    </Button>
                    <!-- Volume -->
                    <StackPanel Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
                        <Button Classes="control-btn"
                                Command="{Binding Player.ToggleMuteCommand}"
                                Width="24" Height="24">
                            <TextBlock Text="&#xE767;"
                                       FontFamily="Segoe Fluent Icons"
                                       FontSize="14"
                                       Foreground="White"
                                       Opacity="{Binding Player.IsMuted, Converter={StaticResource BoolToOpacity}, ConverterParameter=1.0;0.4}"/>
                        </Button>
                        <Slider Classes="lyrics-volume"
                                Value="{Binding Player.Volume, Mode=TwoWay}"
                                Minimum="0" Maximum="100"
                                Width="100"/>
                        <TextBlock Text="&#xE995;"
                                   FontFamily="Segoe Fluent Icons"
                                   FontSize="14"
                                   Foreground="White"
                                   Opacity="0.5"
                                   VerticalAlignment="Center"/>
                    </StackPanel>
                </StackPanel>
            </Grid>
        </Border>

        <!-- RIGHT PANEL: Lyrics -->
        <Border Grid.Column="1"
                x:Name="RightPanel"
                Background="{Binding LyricsBackgroundBrush}"
                ClipToBounds="True">
            <Grid>
                <!-- Top fade -->
                <Border VerticalAlignment="Top" Height="80" IsHitTestVisible="False" ZIndex="1">
                    <Border.Background>
                        <LinearGradientBrush StartPoint="50%,0%" EndPoint="50%,100%">
                            <GradientStop Color="#FF000000" Offset="0"/>
                            <GradientStop Color="#00000000" Offset="1"/>
                        </LinearGradientBrush>
                    </Border.Background>
                    <Border.OpacityMask>
                        <LinearGradientBrush StartPoint="50%,0%" EndPoint="50%,100%">
                            <GradientStop Color="#88000000" Offset="0"/>
                            <GradientStop Color="#00000000" Offset="1"/>
                        </LinearGradientBrush>
                    </Border.OpacityMask>
                </Border>

                <!-- Bottom fade -->
                <Border VerticalAlignment="Bottom" Height="100" IsHitTestVisible="False" ZIndex="1">
                    <Border.Background>
                        <LinearGradientBrush StartPoint="50%,100%" EndPoint="50%,0%">
                            <GradientStop Color="#FF000000" Offset="0"/>
                            <GradientStop Color="#00000000" Offset="1"/>
                        </LinearGradientBrush>
                    </Border.Background>
                    <Border.OpacityMask>
                        <LinearGradientBrush StartPoint="50%,100%" EndPoint="50%,0%">
                            <GradientStop Color="#88000000" Offset="0"/>
                            <GradientStop Color="#00000000" Offset="1"/>
                        </LinearGradientBrush>
                    </Border.OpacityMask>
                </Border>

                <!-- Search Lyrics Button (when no lyrics) -->
                <StackPanel HorizontalAlignment="Center"
                            VerticalAlignment="Center"
                            Spacing="16"
                            IsVisible="{Binding ShowSearchButton}">
                    <TextBlock Text="No lyrics available"
                               FontSize="18"
                               Foreground="#888888"
                               HorizontalAlignment="Center"/>
                    <Button Background="#E74856"
                            Foreground="White"
                            BorderThickness="0"
                            Padding="24,12"
                            FontSize="15"
                            FontWeight="SemiBold"
                            CornerRadius="25"
                            Cursor="Hand"
                            Command="{Binding SearchLyricsCommand}"
                            HorizontalAlignment="Center">
                        <StackPanel Orientation="Horizontal" Spacing="8">
                            <TextBlock Text="&#xE721;"
                                       FontFamily="Segoe Fluent Icons"
                                       FontSize="16"
                                       VerticalAlignment="Center"/>
                            <TextBlock Text="Search Lyrics" VerticalAlignment="Center"/>
                        </StackPanel>
                    </Button>
                </StackPanel>

                <!-- Searching indicator -->
                <TextBlock Text="Searching for lyrics..."
                           FontSize="16"
                           Foreground="#888888"
                           HorizontalAlignment="Center"
                           VerticalAlignment="Center"
                           IsVisible="{Binding IsSearching}"/>

                <!-- Lyrics ScrollViewer -->
                <ScrollViewer x:Name="LyricsScrollViewer"
                              VerticalScrollBarVisibility="Hidden"
                              HorizontalScrollBarVisibility="Disabled"
                              Padding="50,120,50,200">
                    <ItemsControl ItemsSource="{Binding LyricLines}"
                                  x:Name="LyricsItemsControl">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate x:DataType="m:LyricLine">
                                <Button Background="Transparent"
                                        BorderThickness="0"
                                        Padding="0"
                                        Margin="0,4"
                                        HorizontalAlignment="Stretch"
                                        HorizontalContentAlignment="Left"
                                        Cursor="Hand"
                                        Command="{Binding $parent[ItemsControl].((vm:LyricsViewModel)DataContext).SeekToLineCommand}"
                                        CommandParameter="{Binding}">
                                    <TextBlock Text="{Binding Text}"
                                               FontSize="24"
                                               FontWeight="SemiBold"
                                               Foreground="White"
                                               Opacity="{Binding IsActive, Converter={StaticResource BoolToOpacity}, ConverterParameter=0.35;1.0}"
                                               TextWrapping="Wrap"
                                               LineHeight="36">
                                        <TextBlock.Transitions>
                                            <Transitions>
                                                <DoubleTransition Property="Opacity" Duration="0:0:0.3"/>
                                            </Transitions>
                                        </TextBlock.Transitions>
                                    </TextBlock>
                                </Button>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </ScrollViewer>

                <!-- Follow Button -->
                <Button Classes="pill-btn"
                        HorizontalAlignment="Center"
                        VerticalAlignment="Bottom"
                        Margin="0,0,0,30"
                        ZIndex="2"
                        IsVisible="{Binding IsAutoFollowPaused}"
                        Command="{Binding ResumeAutoFollowCommand}">
                    <StackPanel Orientation="Horizontal" Spacing="6">
                        <TextBlock Text="&#xE74B;"
                                   FontFamily="Segoe Fluent Icons"
                                   FontSize="14"
                                   VerticalAlignment="Center"/>
                        <TextBlock Text="Follow" VerticalAlignment="Center"/>
                    </StackPanel>
                </Button>
            </Grid>
        </Border>
    </Grid>
</UserControl>
```

**Key differences from original:**
1. Root has no hardcoded `Background` — adaptive via `LeftPanelBrush`
2. Left panel: `Background="{Binding LeftPanelBrush}"`, padding reduced to 20px for wider art
3. Art: MaxWidth/MaxHeight 380, CornerRadius 12, `RenderOptions.BitmapInterpolationMode="HighQuality"`
4. Title: Horizontal StackPanel with explicit badge inline
5. Artist/Album: Wrapped in clickable Buttons
6. Quality badges: New StackPanel with `quality-badge` class
7. Right panel: `Background="{Binding LyricsBackgroundBrush}"` (dynamic)
8. Fades: Generic black opacity fades that work on any bg color
9. Named elements preserved: `LyricsScrollViewer`, `LyricsItemsControl`, `SeekSlider`
10. New named elements for responsive: `RootLayout`, `LeftPanel`, `RightPanel`

**Step 2: Build to verify**

Run: `dotnet build src/Velour/Velour.csproj -v minimal`
Expected: Build succeeds. Fix any binding/resource errors.

**Step 3: Commit**

```bash
git add src/Velour/Views/LyricsView.axaml
git commit -m "feat(lyrics): redesign XAML with large art, badges, adaptive backgrounds"
```

---

## Task 5: Update LyricsView.axaml.cs for Responsive Layout

**Files:**
- Modify: `src/Velour/Views/LyricsView.axaml.cs`

Add responsive layout switching based on the UserControl's width.

**Step 1: Add responsive handler**

Add to the constructor (after existing `InitializeComponent()` and pointer wheel setup):

```csharp
// Responsive layout: switch between side-by-side and stacked
this.GetObservable(BoundsProperty).Subscribe(bounds =>
{
    UpdateLayout(bounds.Width);
});
```

Add the `UpdateLayout` method:

```csharp
private bool _isNarrowLayout;

private void UpdateLayout(double width)
{
    var shouldBeNarrow = width < 900;
    if (shouldBeNarrow == _isNarrowLayout) return;
    _isNarrowLayout = shouldBeNarrow;

    if (RootLayout == null || LeftPanel == null) return;

    if (shouldBeNarrow)
    {
        // Stack vertically: single column
        RootLayout.ColumnDefinitions.Clear();
        RootLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        RootLayout.RowDefinitions.Clear();
        RootLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        RootLayout.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        Grid.SetColumn(LeftPanel, 0);
        Grid.SetRow(LeftPanel, 0);
        LeftPanel.Width = double.NaN; // auto width
        LeftPanel.Padding = new Thickness(20, 20);

        if (RightPanel != null)
        {
            Grid.SetColumn(RightPanel, 0);
            Grid.SetRow(RightPanel, 1);
        }
    }
    else
    {
        // Side by side: two columns
        RootLayout.RowDefinitions.Clear();
        RootLayout.ColumnDefinitions.Clear();
        RootLayout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(420)));
        RootLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        Grid.SetColumn(LeftPanel, 0);
        Grid.SetRow(LeftPanel, 0);
        LeftPanel.Width = double.NaN; // controlled by column definition
        LeftPanel.Padding = new Thickness(20, 30);

        if (RightPanel != null)
        {
            Grid.SetColumn(RightPanel, 1);
            Grid.SetRow(RightPanel, 0);
        }
    }
}
```

**Required using additions:**

```csharp
using Avalonia.Controls;   // ColumnDefinition, RowDefinition
using Avalonia.Layout;     // if needed
```

Most of these should already be imported. Check and add only what's missing.

**Step 2: Build to verify**

Run: `dotnet build src/Velour/Velour.csproj -v minimal`
Expected: Build succeeds.

**Step 3: Commit**

```bash
git add src/Velour/Views/LyricsView.axaml.cs
git commit -m "feat(lyrics): add responsive layout switching at 900px breakpoint"
```

---

## Task 6: Clean Up BackgroundPreset References

**Files:**
- Modify: `src/Velour/ViewModels/LyricsViewModel.cs` (if not fully cleaned in Task 2)
- Check: Any XAML that referenced `BackgroundPresets` or preset picker UI

**Step 1: Search for remaining BackgroundPreset references**

Search the codebase for any remaining references to `BackgroundPresets`, `SelectBackgroundPreset`, or the preset picker UI. These must be removed since the preset system is replaced by adaptive colors.

Run: `rg -n "BackgroundPreset" src/`

Remove any remaining references in XAML or code.

**Step 2: Verify BackgroundPreset model can be kept or removed**

If `BackgroundPreset.cs` is only used by the lyrics preset system, it can be deleted. If other code references it, keep it but remove lyrics-specific usage.

**Step 3: Build to verify**

Run: `dotnet build src/Velour/Velour.csproj -v minimal`
Expected: Build succeeds with no warnings about unused types.

**Step 4: Commit**

```bash
git add -A
git commit -m "chore(lyrics): remove unused background preset system"
```

---

## Task 7: Final Build Verification and Bug Sweep

**Files:**
- All modified files

**Step 1: Full build**

Run: `dotnet build src/Velour/Velour.csproj -v minimal`
Expected: 0 errors, 0 warnings (or only pre-existing warnings).

**Step 2: Run tests (expect baseline failure)**

Run: `dotnet test tests/Velour.Tests/Velour.Tests.csproj -v minimal`
Expected: Known baseline failure in `TestPersistenceService.cs` (missing `LoadIndexCacheAsync`/`SaveIndexCacheAsync`). No NEW failures.

**Step 3: Visual inspection checklist**

If you can run the app (`dotnet run --project src/Velour/Velour.csproj`), verify:

- [ ] Lyrics view shows adaptive background matching album art
- [ ] Cover art is large (~380px) and sharp (no pixelation)
- [ ] Explicit "E" badge shows inline with title for explicit tracks
- [ ] Lossless/quality badges show for lossless tracks
- [ ] Playback controls (play/pause, prev, next, shuffle, repeat) all work
- [ ] Seek slider updates correctly
- [ ] Volume slider works
- [ ] Lyrics auto-scroll and click-to-seek work
- [ ] "Follow" button appears after manual scroll
- [ ] Island playback bar is hidden in lyrics view
- [ ] Island playback bar reappears when leaving lyrics view
- [ ] Back button returns to previous view
- [ ] Queue button navigates to queue
- [ ] Responsive: narrow window stacks vertically
- [ ] No visual glitches with dark or light album art

**Step 4: Final commit**

```bash
git add -A
git commit -m "feat(lyrics): complete lyrics page redesign with adaptive backgrounds"
```

---

## Implementation Notes

### Bitmap API for Color Extraction

Avalonia 11's `Bitmap` class exposes `Lock()` which returns `ILockedFramebuffer`. The `CreateScaledBitmap` method may need to be implemented as:

```csharp
// If CreateScaledBitmap doesn't exist on Bitmap directly:
var rt = new RenderTargetBitmap(new PixelSize(50, 50));
using var ctx = rt.CreateDrawingContext();
ctx.DrawImage(bitmap, new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), new Rect(0, 0, 50, 50));
// Then lock `rt` instead
```

Check the actual Avalonia 11 API and adapt. The `unsafe` pixel access pattern is standard for `ILockedFramebuffer`.

### Thread Safety

`UpdateAdaptiveBackground` is called from `OnPlayerPropertyChanged` which fires on the UI thread (CommunityToolkit.Mvvm fires PropertyChanged on the thread that set the value, and `AlbumArt` is set from UI-thread posts). The color extraction loop on a 50x50 image takes <1ms, so it's safe to run synchronously on UI thread.

### Fade Overlay Strategy

The original fades used hardcoded colors matching the static background. With adaptive backgrounds, we use a generic semi-transparent black gradient with `OpacityMask` so the fade works regardless of the underlying color. Alternative: compute fade colors from the dominant color. The generic approach is simpler and looks fine.

### Existing Bug Awareness

The `BackgroundPreset` system in the original code references `DominantColorExtractor.CreateGradientFromColor` — this confirms the service already exists and can be extended. The existing HSL conversion utilities are reused for the right-panel brush generation.
