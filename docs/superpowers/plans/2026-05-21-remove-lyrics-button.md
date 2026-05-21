# Remove Lyrics Button Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Remove lyrics" affordance to the Metadata window's Synced Lyrics tab and to the lyrics-page 3-dot menu, reusing existing removal logic.

**Architecture:** Part A adds a `RemoveSyncedLyricsCommand` to `MetadataViewModel` and a button in `MetadataWindow.axaml` that clears the synced-lyrics editor (persisted on Save). Part B adds a `MenuItem` in `PlaybackBarView.axaml` that invokes the existing `LyricsViewModel.RemoveLyricsCommand` through the window's `MainWindowViewModel.Lyrics` accessor.

**Tech Stack:** C# / .NET, Avalonia 11 (AXAML), CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`).

**Build command:** `dotnet build src/Noctis/Noctis.csproj -v minimal`
(The test project does not compile at baseline — see `.claude/rules/testing.md` — so verification is build + manual. No automated tests are added.)

---

## File Structure

### Modified files
- `src/Noctis/ViewModels/MetadataViewModel.cs` — new `RemoveSyncedLyricsCommand`.
- `src/Noctis/Views/MetadataWindow.axaml` — new "Remove lyrics" button in the Synced Lyrics tab footer.
- `src/Noctis/Views/PlaybackBarView.axaml` — new "Remove Lyrics" menu item.

---

## Task 1: RemoveSyncedLyricsCommand on MetadataViewModel

**Files:**
- Modify: `src/Noctis/ViewModels/MetadataViewModel.cs`

- [ ] **Step 1: Add the command**

In `src/Noctis/ViewModels/MetadataViewModel.cs`, find this existing code:

```csharp
    [RelayCommand]
    private async Task SearchSyncedLyrics()
    {
```

Insert the following method directly **above** the `[RelayCommand]` line for `SearchSyncedLyrics` (so the new method precedes it):

```csharp
    [RelayCommand]
    private void RemoveSyncedLyrics()
    {
        SyncedLyrics = string.Empty;
        HasCustomSyncedLyrics = false;
        SyncedLyricsSearchStatus = string.Empty;
    }

```

Notes for the engineer:
- `SyncedLyrics`, `HasCustomSyncedLyrics`, and `SyncedLyricsSearchStatus` are existing `[ObservableProperty]`-generated properties on this class (`_syncedLyrics`, `_hasCustomSyncedLyrics`, `_syncedLyricsSearchStatus`).
- The generated command name will be `RemoveSyncedLyricsCommand`.
- This only changes editor state; the cleared lyrics are written to the track/sidecar when the user clicks Save (existing `SaveCommand` behavior).

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/ViewModels/MetadataViewModel.cs
git commit -m "feat(metadata): add RemoveSyncedLyrics command"
```

---

## Task 2: "Remove lyrics" button in the Metadata Synced Lyrics tab

**Files:**
- Modify: `src/Noctis/Views/MetadataWindow.axaml`

- [ ] **Step 1: Add the button before "Search lyrics"**

In `src/Noctis/Views/MetadataWindow.axaml`, find this block (the Synced Lyrics tab footer's right-hand `StackPanel`):

```xml
                        <StackPanel Grid.Column="2"
                                    Orientation="Horizontal"
                                    VerticalAlignment="Center"
                                    Spacing="10">
                            <TextBlock Text="{Binding SyncedLyricsSearchStatus}"
                                       FontSize="12"
                                       Opacity="0.75"
                                       VerticalAlignment="Center"
                                       IsVisible="{Binding SyncedLyricsSearchStatus, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
                            <Button Content="Search lyrics"
```

Replace exactly that span with:

```xml
                        <StackPanel Grid.Column="2"
                                    Orientation="Horizontal"
                                    VerticalAlignment="Center"
                                    Spacing="10">
                            <Button Content="Remove lyrics"
                                    Command="{Binding RemoveSyncedLyricsCommand}"
                                    IsEnabled="{Binding HasCustomSyncedLyrics}"
                                    Background="#1AFFFFFF"
                                    Foreground="{DynamicResource SystemControlForegroundBaseHighBrush}"
                                    BorderThickness="0"
                                    Padding="14,6"
                                    CornerRadius="999"
                                    Cursor="Hand"
                                    FontWeight="SemiBold"
                                    FontSize="12" />
                            <TextBlock Text="{Binding SyncedLyricsSearchStatus}"
                                       FontSize="12"
                                       Opacity="0.75"
                                       VerticalAlignment="Center"
                                       IsVisible="{Binding SyncedLyricsSearchStatus, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
                            <Button Content="Search lyrics"
```

(Only the new "Remove lyrics" `Button` is added, as the first child of the `StackPanel`. The status `TextBlock` and "Search lyrics" `Button` are unchanged.)

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Views/MetadataWindow.axaml
git commit -m "feat(metadata): add Remove lyrics button to Synced Lyrics tab"
```

---

## Task 3: "Remove Lyrics" item in the lyrics-page 3-dot menu

**Files:**
- Modify: `src/Noctis/Views/PlaybackBarView.axaml`

- [ ] **Step 1: Add the menu item after "Search Lyrics"**

In `src/Noctis/Views/PlaybackBarView.axaml`, find this existing `MenuItem` and the one that follows it:

```xml
                                <MenuItem Header="Search Lyrics"
                                          Command="{Binding SearchCurrentTrackLyricsCommand}">
                                    <MenuItem.Icon>
                                        <Border Width="14" Height="14" Background="{DynamicResource SystemControlForegroundBaseHighBrush}" RenderOptions.BitmapInterpolationMode="HighQuality"><Border.OpacityMask><ImageBrush Source="avares://Noctis/Assets/Icons/Lyrics%20ICON.png" Stretch="Uniform" /></Border.OpacityMask></Border>
                                    </MenuItem.Icon>
                                </MenuItem>
                                <MenuItem Header="Show Folder"
```

Replace exactly that span with:

```xml
                                <MenuItem Header="Search Lyrics"
                                          Command="{Binding SearchCurrentTrackLyricsCommand}">
                                    <MenuItem.Icon>
                                        <Border Width="14" Height="14" Background="{DynamicResource SystemControlForegroundBaseHighBrush}" RenderOptions.BitmapInterpolationMode="HighQuality"><Border.OpacityMask><ImageBrush Source="avares://Noctis/Assets/Icons/Lyrics%20ICON.png" Stretch="Uniform" /></Border.OpacityMask></Border>
                                    </MenuItem.Icon>
                                </MenuItem>
                                <MenuItem Header="Remove Lyrics"
                                          Command="{Binding $parent[Window].((vm:MainWindowViewModel)DataContext).Lyrics.RemoveLyricsCommand}">
                                    <MenuItem.IsVisible>
                                        <MultiBinding Converter="{x:Static BoolConverters.And}">
                                            <Binding Path="IsLyricsPageActive" />
                                            <Binding Path="$parent[Window].((vm:MainWindowViewModel)DataContext).Lyrics.CanRemoveLyrics" />
                                        </MultiBinding>
                                    </MenuItem.IsVisible>
                                    <MenuItem.Icon>
                                        <PathIcon Data="{StaticResource TrashIcon}" Width="14" Height="14" />
                                    </MenuItem.Icon>
                                </MenuItem>
                                <MenuItem Header="Show Folder"
```

Notes for the engineer:
- `PlaybackBarView.axaml` already declares `xmlns:vm="using:Noctis.ViewModels"` — no namespace change needed.
- The menu's `DataContext` is `PlayerViewModel`; `IsLyricsPageActive` is a property on it (already used by the lyrics-mode menu entries below). `RemoveLyricsCommand` and `CanRemoveLyrics` live on `LyricsViewModel`, reached via `MainWindowViewModel.Lyrics`.
- `BoolConverters.And` is `Avalonia.Data.Converters.BoolConverters.And`, an `IMultiValueConverter` available without any namespace import (the `x:Static` resolves it from the default Avalonia namespace; `BoolConverters.Not` is already used elsewhere in this file).
- `TrashIcon` is a `StreamGeometry` resource in `Assets/Icons.axaml`, already merged app-wide.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Views/PlaybackBarView.axaml
git commit -m "feat(lyrics): add Remove Lyrics to the lyrics-page menu"
```

---

## Task 4: Manual verification

**No automated tests** — the test project does not compile at baseline (`.claude/rules/testing.md`). Verify by running the app.

- [ ] **Step 1: Final build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run the app and verify**

Launch the app and check:

- [ ] Play a track, open Metadata → Synced Lyrics tab. A "Remove lyrics" button sits to the left of "Search lyrics".
- [ ] When the track has synced lyrics, "Remove lyrics" is enabled; clicking it clears the text box and unchecks "Enable".
- [ ] When there are no synced lyrics, "Remove lyrics" is disabled.
- [ ] After clicking "Remove lyrics" then Save, the synced lyrics stay removed when the Metadata window is reopened.
- [ ] On the lyrics page, open the 3-dot menu: "Remove Lyrics" appears right after "Search Lyrics" when the track has removable (cached/online) lyrics, and is hidden when it does not.
- [ ] Clicking "Remove Lyrics" removes the lyrics and shows the search state.
- [ ] The bottom playback-bar 3-dot menu (not on the lyrics page) does NOT show "Remove Lyrics".

- [ ] **Step 3: Report results**

Report which checklist items passed and any that failed, with the exact build command output. Do not claim success for any item not actually observed.

---

## Notes

- All three changes reuse existing state/commands; no changes to `LyricsViewModel.RemoveLyricsCommand` or `PlayerViewModel`.
- The Metadata "Remove lyrics" is editor-only — persistence happens through the existing `SaveCommand`.
