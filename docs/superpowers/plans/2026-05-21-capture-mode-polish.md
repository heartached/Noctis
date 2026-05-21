# Capture Mode Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the in-app Lyrics Capture Mode cleaner — match the lyrics page's scroll smoothness, tighten the top-left header toward Apple Music's capture view, nudge the centered header left, and give the capture lyrics a bolder Inter typeface.

**Architecture:** Four small, independent UI changes confined to Capture Mode. Three edit `src/Noctis/Views/LyricsCaptureView.axaml` (+ its code-behind); one also adds a `FontFamily` resource to `App.axaml`. No view-model, sync-engine, or global-app changes.

**Tech Stack:** C# / .NET, Avalonia 11 (AXAML), `Avalonia.Fonts.Inter` NuGet package (already referenced).

**Build command:** `dotnet build src/Noctis/Noctis.csproj -v minimal`
(The test project does not compile at baseline — see `.claude/rules/testing.md` — so verification is build + manual. No automated tests are added.)

---

## File Structure

### Modified files
- `src/Noctis/Views/LyricsCaptureView.axaml.cs` — scroll animation easing + duration.
- `src/Noctis/Views/LyricsCaptureView.axaml` — top-left header restyle, centered-header nudge, lyric font.
- `src/Noctis/App.axaml` — new `InterBold` `FontFamily` resource.

---

## Task 1: Match the scroll motion to the lyrics page

**Files:**
- Modify: `src/Noctis/Views/LyricsCaptureView.axaml.cs`

- [ ] **Step 1: Change the scroll duration formula**

In `src/Noctis/Views/LyricsCaptureView.axaml.cs`, inside `TryScrollToLine`, find:

```csharp
            AnimateScroll(currentOffset, targetOffset,
                (int)Math.Min(1250, Math.Max(760, diff * 0.72)));
```

Replace with:

```csharp
            AnimateScroll(currentOffset, targetOffset,
                (int)Math.Min(1050, Math.Max(650, diff * 0.85)));
```

- [ ] **Step 2: Change the easing to single SmootherStep**

In the same file, inside `AnimateScroll`, find:

```csharp
            var eased = Easing.SmootherStep(Easing.SmootherStep(t));
```

Replace with:

```csharp
            var eased = Easing.SmootherStep(t);
```

These two values now match `LyricsView.axaml.cs`'s `AnimateScroll` exactly.

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Views/LyricsCaptureView.axaml.cs
git commit -m "fix(capture): match lyrics-page scroll easing and duration"
```

---

## Task 2: Polish the top-left header layout

**Files:**
- Modify: `src/Noctis/Views/LyricsCaptureView.axaml`

- [ ] **Step 1: Restyle the top-left artwork + metadata block**

In `src/Noctis/Views/LyricsCaptureView.axaml`, find this block (the header with `IsVisible="{Binding !IsArtCentered}"`):

```xml
        <!-- Top-left: artwork + metadata (16px 9:16-crop safe zone) -->
        <StackPanel Orientation="Horizontal"
                    HorizontalAlignment="Left" VerticalAlignment="Top"
                    Margin="16" Spacing="12"
                    IsVisible="{Binding !IsArtCentered}">
            <Border Width="120" Height="120" CornerRadius="12" ClipToBounds="True"
                    Background="#222222">
                <Image Source="{Binding AlbumArt}" Stretch="UniformToFill"/>
            </Border>
            <StackPanel Spacing="4" VerticalAlignment="Center">
                <StackPanel Orientation="Horizontal" Spacing="6">
                    <TextBlock Text="{Binding TrackTitle}" FontSize="14" FontWeight="SemiBold"
                               Foreground="White" MaxWidth="240"
                               VerticalAlignment="Center"
                               TextTrimming="CharacterEllipsis"/>
```

Replace exactly that span with:

```xml
        <!-- Top-left: artwork + metadata (16px 9:16-crop safe zone) -->
        <StackPanel Orientation="Horizontal"
                    HorizontalAlignment="Left" VerticalAlignment="Top"
                    Margin="16" Spacing="14"
                    IsVisible="{Binding !IsArtCentered}">
            <Border Width="88" Height="88" CornerRadius="14" ClipToBounds="True"
                    Background="#222222">
                <Image Source="{Binding AlbumArt}" Stretch="UniformToFill"/>
            </Border>
            <StackPanel Spacing="3" VerticalAlignment="Center">
                <StackPanel Orientation="Horizontal" Spacing="6">
                    <TextBlock Text="{Binding TrackTitle}" FontSize="17" FontWeight="Bold"
                               Foreground="White" MaxWidth="240"
                               VerticalAlignment="Center"
                               TextTrimming="CharacterEllipsis"/>
```

(Only the outer `StackPanel`'s `Spacing`, the `Border` `Width`/`Height`/`CornerRadius`, the inner metadata `StackPanel`'s `Spacing`, and the title `TextBlock`'s `FontSize`/`FontWeight` change. The explicit-badge `Border` that follows the title is left untouched.)

- [ ] **Step 2: Brighten and enlarge the artist line**

In the same block, immediately below the explicit-badge `Border`, find the artist `TextBlock`:

```xml
                <TextBlock Text="{Binding TrackArtist}" FontSize="12" FontWeight="Regular"
                           Foreground="#999999" MaxWidth="240"
                           TextTrimming="CharacterEllipsis"/>
```

Replace with:

```xml
                <TextBlock Text="{Binding TrackArtist}" FontSize="13" FontWeight="Regular"
                           Foreground="#B5B5B5" MaxWidth="240"
                           TextTrimming="CharacterEllipsis"/>
```

Note: there are two artist `TextBlock`s with `FontSize="12"` in this file — this one has `Foreground="#999999"` and `MaxWidth="240"`. The other (in the centered layout) has `Foreground="#BBBBBB"` and `MaxWidth="320"`; do **not** change that one.

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Views/LyricsCaptureView.axaml
git commit -m "feat(capture): tighten top-left header to match Apple Music"
```

---

## Task 3: Nudge the centered header left

**Files:**
- Modify: `src/Noctis/Views/LyricsCaptureView.axaml`

- [ ] **Step 1: Add a left translate to the centered art block**

In `src/Noctis/Views/LyricsCaptureView.axaml`, find the centered header `StackPanel` (the one with `IsVisible="{Binding IsArtCentered}"`):

```xml
        <!-- Top-center: artwork + metadata (Apple-Music style; lyrics fade under it) -->
        <StackPanel Orientation="Horizontal"
                    HorizontalAlignment="Center" VerticalAlignment="Top"
                    Margin="0,30,0,0" Spacing="14" ZIndex="50"
                    IsVisible="{Binding IsArtCentered}">
```

Replace with:

```xml
        <!-- Top-center: artwork + metadata (Apple-Music style; lyrics fade under it) -->
        <StackPanel Orientation="Horizontal"
                    HorizontalAlignment="Center" VerticalAlignment="Top"
                    Margin="0,30,0,0" Spacing="14" ZIndex="50"
                    RenderTransform="translateX(-60px)"
                    IsVisible="{Binding IsArtCentered}">
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Views/LyricsCaptureView.axaml
git commit -m "feat(capture): shift centered header 60px left"
```

---

## Task 4: Bolder Inter typeface on the capture lyrics

**Files:**
- Modify: `src/Noctis/App.axaml`
- Modify: `src/Noctis/Views/LyricsCaptureView.axaml`

- [ ] **Step 1: Register the InterBold font resource**

In `src/Noctis/App.axaml`, find:

```xml
            <FontFamily x:Key="InterSemiBold">avares://Noctis/Assets/Fonts/Inter-SemiBold.ttf#Inter SemiBold</FontFamily>
```

Add this line directly below it:

```xml
            <FontFamily x:Key="InterBold">avares://Avalonia.Fonts.Inter/Assets#Inter</FontFamily>
```

`Avalonia.Fonts.Inter` is already referenced in `Noctis.csproj`; its `Inter` family carries all weights, so `FontWeight="Bold"` selects the bold face.

- [ ] **Step 2: Apply the bold font + tracking to the capture lyric line**

In `src/Noctis/Views/LyricsCaptureView.axaml`, find the capture lyric `TextBlock` inside the `DataTemplate`:

```xml
                            <TextBlock Text="{Binding Text}"
                                       IsVisible="{Binding !IsIntroPlaceholder}"
                                       Classes="capture-line"
                                       Classes.active="{Binding IsActive}"
                                       Foreground="White" FontWeight="SemiBold"
                                       Opacity="{Binding LineOpacity}"
                                       TextAlignment="Center" TextWrapping="Wrap">
```

Replace with:

```xml
                            <TextBlock Text="{Binding Text}"
                                       IsVisible="{Binding !IsIntroPlaceholder}"
                                       Classes="capture-line"
                                       Classes.active="{Binding IsActive}"
                                       FontFamily="{StaticResource InterBold}"
                                       Foreground="White" FontWeight="Bold"
                                       LetterSpacing="-0.5"
                                       Opacity="{Binding LineOpacity}"
                                       TextAlignment="Center" TextWrapping="Wrap">
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/App.axaml src/Noctis/Views/LyricsCaptureView.axaml
git commit -m "feat(capture): render lyrics in Inter Bold with tighter tracking"
```

---

## Task 5: Manual verification

**No automated tests** — the test project does not compile at baseline (`.claude/rules/testing.md`). Verify by running the app.

- [ ] **Step 1: Final build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run the app and verify**

Launch the app, play a track with synced lyrics, open Capture Mode, and check:

- [ ] The lyric-list scroll glide feels identical to the lyrics page (single smootherstep, slightly snappier than before).
- [ ] Top-left header layout: album art is smaller (88px) with rounder corners; the title is larger and bold; the artist line is brighter.
- [ ] Top-center header layout (toggle the cover-art-position button): the art + metadata block sits noticeably left of center.
- [ ] The lyrics render in a bolder Inter face with slightly tighter letter-spacing; intro dots and other text are unaffected.

- [ ] **Step 3: Report results**

Report which checklist items passed and any that failed, with the exact build command output. Do not claim success for any item not actually observed.

---

## Notes

- All changes are scoped to Capture Mode. The lyrics page, global app font, and `LyricsCaptureViewModel` are untouched.
- `AvaloniaResource Include="Assets\**"` already covers font assets, but Task 4 adds no new file — it uses the existing `Avalonia.Fonts.Inter` package — so no `.csproj` change is needed.
