# Multi-Select Metadata Editor + Selection UX — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the checkbox-based multi-track metadata editor with the existing Apple-Music-style tabbed editor (generalized to any selection), make Select All discoverable, and reset selection when leaving a view.

**Architecture:** Generalize `MetadataViewModel`/`MetadataWindow` with a `multiSelect` mode that reuses the album-scoped Mixed + change-tracked fan-out logic, shows a blank music-note header with "N artists / M songs selected", hides the Animated Artwork tab, applies artwork to every selected track, and hosts the ported rename-by-pattern tool in the Options tab. Select All / Deselect All are added to existing right-click menus; each view clears its selection on detach.

**Tech Stack:** C#, .NET 8, Avalonia 11 (compiled XAML bindings, `x:DataType`), CommunityToolkit.Mvvm.

**Verification note (read first):** The test project (`tests/Velour.Tests`) has a documented pre-existing baseline compile failure and the app is UI/VM-heavy, so this plan uses **build + targeted manual verification** as the gate rather than unit tests. Build command for every task:
```
dotnet build src/Noctis/Noctis.csproj -v minimal
```
Compiled bindings (`x:DataType` on `MetadataWindow`/`TopBarView`) validate new bindings at build time, so a green build catches binding typos. Commit messages must not mention AI and must not add co-author trailers (project release hygiene).

---

## File Structure

- `src/Noctis/ViewModels/MetadataViewModel.cs` — add `_multiSelect` mode: header, `IsMultiSelect`/`ShowAnimatedArtworkTab`, skip album-art autoload, multi-select artwork save, ported rename logic.
- `src/Noctis/Views/MetadataWindow.axaml` — header variant (music note + counts), Animated Artwork tab `IsVisible`, rename section at bottom of Options tab.
- `src/Noctis/ViewModels/MetadataHelper.cs` — route `OpenBatchMetadataWindow` to the multi-select `MetadataWindow`.
- `src/Noctis/ViewModels/BatchMetadataViewModel.cs`, `src/Noctis/Views/BatchMetadataWindow.axaml(.cs)` — retired in the final task.
- `src/Noctis/Helpers/TrackContextMenuBuilder.cs` — add Select All / Deselect All items.
- `src/Noctis/Views/LibrarySongsView.axaml.cs`, `PlaylistView.axaml.cs`, `LibraryAlbumsView.axaml(.cs)` — Select All wiring + selection reset on detach.
- `src/Noctis/Views/FavoritesView.axaml.cs`, `LibraryFoldersView.axaml.cs`, `MoreByArtistView.axaml.cs`, `HomeView.axaml.cs` — selection reset on detach where a selection set exists.

---

## Task 1: Add multi-select mode to MetadataViewModel + header/tab UI

**Files:**
- Modify: `src/Noctis/ViewModels/MetadataViewModel.cs`
- Modify: `src/Noctis/Views/MetadataWindow.axaml`

- [ ] **Step 1: Add the `_multiSelect` field and constructor parameter**

In `MetadataViewModel.cs`, the field block currently reads:
```csharp
    private readonly bool _albumScoped;
    private readonly List<Track>? _albumTracks;
```
Add below them:
```csharp
    private readonly bool _multiSelect;
```

Change the constructor signature from:
```csharp
    public MetadataViewModel(Track track, IMetadataService metadata, ILibraryService library, IPersistenceService persistence, IAnimatedCoverService animatedCovers, bool albumScoped = false, List<Track>? albumTracks = null, ITunesArtworkService? itunes = null, ILrcLibService? lrcLib = null)
```
to add a trailing parameter:
```csharp
    public MetadataViewModel(Track track, IMetadataService metadata, ILibraryService library, IPersistenceService persistence, IAnimatedCoverService animatedCovers, bool albumScoped = false, List<Track>? albumTracks = null, ITunesArtworkService? itunes = null, ILrcLibService? lrcLib = null, bool multiSelect = false)
```
In the constructor body, next to `_albumScoped = albumScoped;` add:
```csharp
        _multiSelect = multiSelect;
```

- [ ] **Step 2: Add multi-select header + tab-visibility + count properties**

Find the existing block:
```csharp
    /// <summary>Track title and artist for the header.</summary>
    public string HeaderTitle => _albumScoped && !string.IsNullOrWhiteSpace(_track.Album) ? _track.Album : _track.Title;
    public string HeaderArtist => _track.Artist;
    public string HeaderAlbum => _track.Album;
```
Replace those three lines with:
```csharp
    /// <summary>Track title and artist for the header.</summary>
    public string HeaderTitle => _multiSelect
        ? $"{DistinctArtistCount} artists selected"
        : (_albumScoped && !string.IsNullOrWhiteSpace(_track.Album) ? _track.Album : _track.Title);
    public string HeaderArtist => _multiSelect ? $"{SongCount} songs selected" : _track.Artist;
    public string HeaderAlbum => _multiSelect ? string.Empty : _track.Album;

    /// <summary>True when editing an arbitrary multi-track selection (blank art, "N selected" header).</summary>
    public bool IsMultiSelect => _multiSelect;
    private int SongCount => _albumTracks?.Count ?? 1;
    private int DistinctArtistCount => _albumTracks == null
        ? 1
        : _albumTracks.Select(t => t.Artist ?? string.Empty).Distinct().Count();
```

Find:
```csharp
    public bool ShowFileTab => !_albumScoped;
```
Add directly below it:
```csharp
    /// <summary>Animated Artwork is per-album; hide it for arbitrary multi-track selections.</summary>
    public bool ShowAnimatedArtworkTab => !_multiSelect;
```

- [ ] **Step 3: Skip album-art autoload in multi-select mode**

Find the `LoadArtwork();` call in the constructor and the `LoadArtwork()` method. At the very top of `LoadArtwork()` add an early return so a multi-album selection doesn't show one album's art as if shared:
```csharp
    private void LoadArtwork()
    {
        if (_multiSelect) { HasArtwork = false; return; }
        // ... existing body unchanged ...
```
(If `LoadArtwork` is `async`, keep its signature; just add the guard as the first statements.)

- [ ] **Step 4: Header markup — music-note placeholder + counts**

In `MetadataWindow.axaml`, the header thumbnail `Panel` currently is:
```xml
                    <Panel>
                        <TextBlock Text="Album"
                                   FontSize="11"
                                   Opacity="0.35"
                                   HorizontalAlignment="Center"
                                   VerticalAlignment="Center"
                                   IsVisible="{Binding !HasArtwork}" />
                        <Image Source="{Binding ArtworkPreview}"
                               Stretch="UniformToFill"
                               IsVisible="{Binding HasArtwork}" />
                    </Panel>
```
Replace with (adds a music-note icon shown in multi-select; keeps existing behaviour otherwise):
```xml
                    <Panel>
                        <TextBlock Text="Album"
                                   FontSize="11"
                                   Opacity="0.35"
                                   HorizontalAlignment="Center"
                                   VerticalAlignment="Center"
                                   IsVisible="{Binding !HasArtwork}" />
                        <PathIcon Data="{StaticResource MusicNoteIcon}"
                                  Width="40" Height="40"
                                  Opacity="0.4"
                                  HorizontalAlignment="Center"
                                  VerticalAlignment="Center"
                                  IsVisible="{Binding IsMultiSelect}" />
                        <Image Source="{Binding ArtworkPreview}"
                               Stretch="UniformToFill"
                               IsVisible="{Binding HasArtwork}" />
                    </Panel>
```
If `MusicNoteIcon` is not an existing `StaticResource`, find the resource key used for the sidebar Songs icon (search `Assets/Styles.axaml` and existing views for a music-note `PathIcon`/`StreamGeometry`) and use that key; otherwise use the existing Songs PNG asset via the `CreatePngIcon` pattern is not available in XAML, so fall back to a `Border` with `OpacityMask` `ImageBrush Source="avares://Noctis/Assets/Icons/Songs ICON.png"` (mirror the icon pattern already used elsewhere in this file, e.g. the Metadata icon borders).

Note: the `IsVisible="{Binding !HasArtwork}"` "Album" text and the music-note both live in the Panel; in multi-select `HasArtwork` is false so both would show. Gate the "Album" text so it does not show in multi-select by changing its binding to a converter-free form: wrap it so it is hidden when `IsMultiSelect`. Simplest: bind its `IsVisible` to a new VM property `ShowAlbumArtPlaceholderText => !HasArtwork && !_multiSelect`. Add that property next to `IsMultiSelect`:
```csharp
    public bool ShowAlbumArtPlaceholderText => !HasArtwork && !_multiSelect;
```
and change the TextBlock to `IsVisible="{Binding ShowAlbumArtPlaceholderText}"`. Also raise it when `HasArtwork` changes — add to the generated partial:
```csharp
    partial void OnHasArtworkChanged(bool value) => OnPropertyChanged(nameof(ShowAlbumArtPlaceholderText));
```

- [ ] **Step 5: Hide the Animated Artwork tab in multi-select**

In `MetadataWindow.axaml` the Animated Artwork tab header is:
```xml
            <TabItem Header="Animated Artwork" FontSize="14" FontWeight="SemiBold">
```
Change to:
```xml
            <TabItem Header="Animated Artwork" FontSize="14" FontWeight="SemiBold" IsVisible="{Binding ShowAnimatedArtworkTab}">
```

- [ ] **Step 6: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded. 0 Error(s)`. (No behavior change yet — nothing opens in multi mode until Task 2.)

- [ ] **Step 7: Commit**

```bash
git add src/Noctis/ViewModels/MetadataViewModel.cs src/Noctis/Views/MetadataWindow.axaml
git commit -m "feat(metadata): add multi-select mode scaffolding to metadata editor"
```

---

## Task 2: Route multi-track editing to the new editor

**Files:**
- Modify: `src/Noctis/ViewModels/MetadataHelper.cs`

- [ ] **Step 1: Add a multi-select open path and repoint `OpenBatchMetadataWindow`**

In `MetadataHelper.cs`, replace the body of `OpenBatchMetadataWindow` so it constructs the `MetadataViewModel` in multi-select mode instead of `BatchMetadataViewModel`. Current:
```csharp
    public static async Task OpenBatchMetadataWindow(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;
        if (tracks.Count == 1) { await OpenMetadataWindow(tracks[0]); return; }

        var metadata = App.Services!.GetRequiredService<IMetadataService>();
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var vm = new BatchMetadataViewModel(tracks, metadata, library);
        var window = new BatchMetadataWindow(vm);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            DialogHelper.SizeToOwner(window, desktop.MainWindow);
            await window.ShowDialog(desktop.MainWindow);
        }
        else
        {
            window.Show();
        }
    }
```
Replace with:
```csharp
    public static async Task OpenBatchMetadataWindow(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;
        if (tracks.Count == 1) { await OpenMetadataWindow(tracks[0]); return; }
        await OpenMultiTrackMetadataWindow(tracks);
    }

    public static async Task OpenMultiTrackMetadataWindow(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;
        if (tracks.Count == 1) { await OpenMetadataWindow(tracks[0]); return; }

        var metadata = App.Services!.GetRequiredService<IMetadataService>();
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var persistence = App.Services!.GetRequiredService<IPersistenceService>();
        var animatedCovers = new AnimatedCoverService(persistence);
        var itunes = App.Services!.GetService<ITunesArtworkService>();
        var lrcLib = App.Services!.GetService<ILrcLibService>();

        var vm = new MetadataViewModel(tracks[0], metadata, library, persistence, animatedCovers,
            albumScoped: true, albumTracks: tracks.ToList(), itunes: itunes, lrcLib: lrcLib, multiSelect: true);

        var window = new MetadataWindow(vm);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            DialogHelper.SizeToOwner(window, desktop.MainWindow);
            await window.ShowDialog(desktop.MainWindow);
        }
        else
        {
            window.Show();
        }
    }
```
Add `using System.Linq;` if not present (it is at top of the file already).

- [ ] **Step 2: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Manual verification**

Run the app: `dotnet run --project src/Noctis/Noctis.csproj` (or the project's run skill). In Songs, Ctrl+Click 3 tracks from different artists → right-click → Metadata. Expect: the tabbed editor opens with a music-note thumbnail, header "3 artists selected / 3 songs selected", tabs Details / Artwork / Options (no Animated Artwork, no Title/Performer), and differing fields show `Mixed`. Don't save yet (artwork-to-all comes in Task 3).

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/ViewModels/MetadataHelper.cs
git commit -m "feat(metadata): open multi-track selections in the tabbed editor"
```

---

## Task 3: Apply artwork to every selected track in multi-select

**Files:**
- Modify: `src/Noctis/ViewModels/MetadataViewModel.cs`

- [ ] **Step 1: Branch the artwork-save block for multi-select**

In the `Save()` method, the artwork section currently begins:
```csharp
        // Handle artwork changes
        if (_newArtworkData != null)
        {
            _metadata.WriteAlbumArt(_track.FilePath, _newArtworkData);
            _persistence.SaveArtwork(_track.AlbumId, _newArtworkData);
            ArtworkCache.Invalidate(_persistence.GetArtworkPath(_track.AlbumId));
        }
        else if (_artworkRemoved)
        {
```
Replace the `if (_newArtworkData != null) { ... }` branch (only that branch) with a multi-select-aware version:
```csharp
        // Handle artwork changes
        if (_newArtworkData != null)
        {
            if (_multiSelect && _albumTracks != null)
            {
                // Apply the chosen image to every selected track and refresh each
                // affected album's cached cover.
                foreach (var albumId in _albumTracks.Select(t => t.AlbumId).Distinct())
                {
                    _persistence.SaveArtwork(albumId, _newArtworkData);
                    ArtworkCache.Invalidate(_persistence.GetArtworkPath(albumId));
                }
                foreach (var t in _albumTracks)
                {
                    try { _metadata.WriteAlbumArt(t.FilePath, _newArtworkData); } catch { }
                    t.AlbumArtworkPath = null;
                }
            }
            else
            {
                _metadata.WriteAlbumArt(_track.FilePath, _newArtworkData);
                _persistence.SaveArtwork(_track.AlbumId, _newArtworkData);
                ArtworkCache.Invalidate(_persistence.GetArtworkPath(_track.AlbumId));
            }
        }
        else if (_artworkRemoved)
        {
```
The existing `_artworkRemoved` branch already iterates `_albumTracks` (which in multi-select is the full selection) and strips art from each, so removal already fans out correctly — leave it unchanged.

- [ ] **Step 2: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Manual verification**

Multi-select tracks spanning two albums → Metadata → Artwork tab → set an image → Save. Expect both albums' covers update in the grid and the embedded art changes for all selected tracks.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/ViewModels/MetadataViewModel.cs
git commit -m "feat(metadata): apply multi-select artwork to all selected tracks"
```

---

## Task 4: Port rename-by-pattern into the editor's Options tab

**Files:**
- Modify: `src/Noctis/ViewModels/MetadataViewModel.cs`
- Modify: `src/Noctis/Views/MetadataWindow.axaml`

- [ ] **Step 1: Add rename state + preview model to MetadataViewModel**

Add near the other `[ObservableProperty]` fields:
```csharp
    // ── Rename-by-pattern (multi-select only) ──
    [ObservableProperty] private bool _applyRename;
    [ObservableProperty] private string _renamePattern = "%tracknumber2% - %title%";
    public ObservableCollection<RenamePreview> RenamePreviews { get; } = new();
    public bool ShowRenameSection => _multiSelect;

    public sealed class RenamePreview
    {
        public string OriginalName { get; set; } = string.Empty;
        public string NewName { get; set; } = string.Empty;
        public bool Conflict { get; set; }
    }
```
Add the rename preview builder + path computation (ported from `BatchMetadataViewModel`):
```csharp
    partial void OnRenamePatternChanged(string value) => RebuildRenamePreview();

    private void RebuildRenamePreview()
    {
        RenamePreviews.Clear();
        if (!_multiSelect || _albumTracks == null) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _albumTracks.Take(8))
        {
            var newPath = ComputeRenamedPath(t, out var conflict, seen);
            RenamePreviews.Add(new RenamePreview
            {
                OriginalName = Path.GetFileName(t.FilePath),
                NewName = newPath != null ? Path.GetFileName(newPath) : "(empty pattern)",
                Conflict = conflict,
            });
        }
    }

    private string? ComputeRenamedPath(Track t, out bool conflict, HashSet<string>? seenInBatch = null)
    {
        conflict = false;
        var expanded = TitleFormatter.Expand(RenamePattern, t, sanitizeForFilename: true);
        if (string.IsNullOrWhiteSpace(expanded)) return null;

        var dir = Path.GetDirectoryName(t.FilePath) ?? string.Empty;
        var ext = Path.GetExtension(t.FilePath);
        var newPath = Path.Combine(dir, expanded + ext);

        if (string.Equals(newPath, t.FilePath, StringComparison.OrdinalIgnoreCase)) return newPath;
        if (File.Exists(newPath)) conflict = true;
        if (seenInBatch != null && !seenInBatch.Add(newPath.ToLowerInvariant())) conflict = true;
        return newPath;
    }
```
Confirm `TitleFormatter` is the same type used by `BatchMetadataViewModel` (namespace `Noctis.Helpers`); add `using Noctis.Helpers;` if missing (it is already used).

Build the initial preview when entering multi-select. In the constructor block that already calls `LoadAlbumScopedOverrides()`, append:
```csharp
        if (_multiSelect)
            RebuildRenamePreview();
```

- [ ] **Step 2: Execute rename during Save (multi-select only)**

In `Save()`, after the track-tag write loop (the block that calls `_metadata.WriteTrackMetadata(t)` for album-scoped) and before `await _library.SaveAsync();`, add:
```csharp
        // Rename files by pattern (multi-select only). Done after tag writes so the
        // new name can reflect just-applied tags.
        if (_multiSelect && ApplyRename && _albumTracks != null)
        {
            var renameSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _albumTracks)
            {
                var newPath = ComputeRenamedPath(t, out var conflict, renameSeen);
                if (newPath != null && !conflict
                    && !string.Equals(newPath, t.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Move(t.FilePath, newPath); t.FilePath = newPath; }
                    catch { /* Non-fatal — skip this file */ }
                }
            }
        }
```
Verify the exact insertion point: it must be after the `if (_albumScoped && _albumTracks != null && _albumTracks.Count > 0) { foreach (var t in _albumTracks) _metadata.WriteTrackMetadata(t); }` block introduced earlier, and before `await _library.SaveAsync();`.

- [ ] **Step 3: Add the rename section to the Options tab**

In `MetadataWindow.axaml`, find the closing of the Options tab's content (the last field group before `</TabItem>` for `Header="Options"`). Immediately before that tab's closing `</StackPanel>`/`</ScrollViewer>`, add a rename section gated on `ShowRenameSection`:
```xml
                        <!-- Rename by pattern (multi-select only) -->
                        <StackPanel Spacing="8" IsVisible="{Binding ShowRenameSection}">
                            <Border Height="1" Background="{DynamicResource SystemControlForegroundBaseLowBrush}" Opacity="0.5" Margin="0,6" />
                            <CheckBox Classes="metadata-pill-checkbox"
                                      Content="Rename files by pattern"
                                      IsChecked="{Binding ApplyRename}" />
                            <TextBlock Text="Tokens: %artist% %albumartist% %album% %title% %tracknumber% %tracknumber2% %discnumber% %year% %genre% %composer%"
                                       FontSize="11" Opacity="0.55" TextWrapping="Wrap" />
                            <TextBox Classes="metadata-info-field"
                                     Text="{Binding RenamePattern}"
                                     IsEnabled="{Binding ApplyRename}" />
                            <Border Background="#10FFFFFF" CornerRadius="8" Padding="10"
                                    IsVisible="{Binding ApplyRename}">
                                <StackPanel Spacing="2">
                                    <TextBlock Text="Preview (first 8 tracks)" FontSize="11" Opacity="0.6" Margin="0,0,0,4" />
                                    <ItemsControl ItemsSource="{Binding RenamePreviews}">
                                        <ItemsControl.ItemTemplate>
                                            <DataTemplate x:DataType="vm:MetadataViewModel+RenamePreview">
                                                <Grid ColumnDefinitions="*,Auto,*,Auto" Margin="0,1">
                                                    <TextBlock Grid.Column="0" Text="{Binding OriginalName}" FontSize="11" Opacity="0.7" TextTrimming="CharacterEllipsis" Margin="0,0,8,0" />
                                                    <TextBlock Grid.Column="1" Text="→" FontSize="11" Opacity="0.5" Margin="0,0,8,0" />
                                                    <TextBlock Grid.Column="2" Text="{Binding NewName}" FontSize="11" TextTrimming="CharacterEllipsis" />
                                                    <TextBlock Grid.Column="3" Text="conflict" FontSize="10"
                                                               Foreground="#E74856" FontWeight="SemiBold"
                                                               Margin="8,0,0,0"
                                                               IsVisible="{Binding Conflict}" />
                                                </Grid>
                                            </DataTemplate>
                                        </ItemsControl.ItemTemplate>
                                    </ItemsControl>
                                </StackPanel>
                            </Border>
                        </StackPanel>
```
Confirm the `vm` namespace alias is declared on the root `Window` element (it is — `xmlns:vm="using:Noctis.ViewModels"`).

- [ ] **Step 4: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Manual verification**

Multi-select tracks → Metadata → Options tab → scroll to Rename → tick "Rename files by pattern", edit the pattern, confirm the 8-row preview updates and conflicts flag. Save and confirm files are renamed on disk.

- [ ] **Step 6: Commit**

```bash
git add src/Noctis/ViewModels/MetadataViewModel.cs src/Noctis/Views/MetadataWindow.axaml
git commit -m "feat(metadata): port rename-by-pattern into multi-select editor"
```

---

## Task 5: Retire the old BatchMetadataWindow

**Files:**
- Delete: `src/Noctis/Views/BatchMetadataWindow.axaml`
- Delete: `src/Noctis/Views/BatchMetadataWindow.axaml.cs`
- Delete: `src/Noctis/ViewModels/BatchMetadataViewModel.cs`

- [ ] **Step 1: Confirm there are no remaining references**

Run: `grep -rn "BatchMetadataWindow\|BatchMetadataViewModel" src/Noctis`
Expected: no matches outside the three files being deleted. (`OpenBatchMetadataWindow` is a `MetadataHelper` method name and must remain — it no longer references the deleted types.)

- [ ] **Step 2: Delete the three files**

```bash
git rm src/Noctis/Views/BatchMetadataWindow.axaml src/Noctis/Views/BatchMetadataWindow.axaml.cs src/Noctis/ViewModels/BatchMetadataViewModel.cs
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git commit -m "refactor(metadata): remove superseded checkbox batch editor"
```

---

## Task 6: Add visible Select All / Deselect All

**Files:**
- Modify: `src/Noctis/Helpers/TrackContextMenuBuilder.cs`
- Modify: `src/Noctis/Views/LibrarySongsView.axaml.cs`
- Modify: `src/Noctis/Views/PlaylistView.axaml.cs`
- Modify: `src/Noctis/Views/LibraryAlbumsView.axaml(.cs)`

- [ ] **Step 1: Add Select All / Deselect All items to the track context menu builder**

In `TrackContextMenuBuilder.cs`, add named items (declarations near the others):
```csharp
    public MenuItem SelectAll { get; private set; } = null!;
    public MenuItem DeselectAll { get; private set; } = null!;
```
In `Build(...)`, after `AddToQueue` is added and before the first `items.Add(new Separator());`, insert:
```csharp
        SelectAll = new MenuItem { Header = "Select All" };
        items.Add(SelectAll);
        DeselectAll = new MenuItem { Header = "Deselect All" };
        items.Add(DeselectAll);
        items.Add(new Separator());
```
Add a wiring method (commands supplied by the view as `Action`s wrapped in a lightweight `RelayCommand`, or expose direct click handlers). Add an overload to wire them:
```csharp
    public void BindSelection(ICommand selectAllCommand, ICommand deselectAllCommand)
    {
        SelectAll.Command = selectAllCommand;
        DeselectAll.Command = deselectAllCommand;
    }
```

- [ ] **Step 2: Wire Select All in LibrarySongsView**

In `LibrarySongsView.axaml.cs`, add helper methods that reuse `MultiSelectHelper`:
```csharp
    private void SelectAllTracks()
    {
        _selectedTracks.Clear();
        if (TrackList.ItemsSource is System.Collections.IEnumerable items)
            foreach (var t in items.OfType<Track>()) _selectedTracks.Add(t);
        foreach (var child in TrackList.GetVisualDescendants())
            if (child is ListBoxItem li) li.Classes.Add("ctrl-selected");
        if (DataContext is LibrarySongsViewModel vm) vm.CtrlSelectedTracks = _selectedTracks.ToList();
    }

    private void DeselectAllTracks()
    {
        MultiSelectHelper.ClearTrackSelectionsByData(_selectedTracks);
        foreach (var child in TrackList.GetVisualDescendants())
            if (child is ListBoxItem li) li.Classes.Remove("ctrl-selected");
        if (DataContext is LibrarySongsViewModel vm) vm.CtrlSelectedTracks = new List<Track>();
    }
```
In `BindContextMenuToTrack`, after the existing `_menuBuilder.Bind(...)` call, add:
```csharp
        _menuBuilder.BindSelection(
            new CommunityToolkit.Mvvm.Input.RelayCommand(SelectAllTracks),
            new CommunityToolkit.Mvvm.Input.RelayCommand(DeselectAllTracks));
```
Add `using CommunityToolkit.Mvvm.Input;` and `using Avalonia.VisualTree;` if not present (`Avalonia.VisualTree` already is).

- [ ] **Step 3: Wire Select All in PlaylistView**

`PlaylistView.axaml.cs` uses the same `TrackContextMenuBuilder` + `_selectedTracks` pattern (confirmed: it has `_selectedTracks`, `BindContextMenuToTrack`/equivalent, and `TrackList`). Add identical `SelectAllTracks`/`DeselectAllTracks` methods (same code as Step 2 but pushing to the playlist VM's `CtrlSelectedTracks`), and call `_menuBuilder.BindSelection(...)` where it binds the menu. Match the field/VM names already in that file.

- [ ] **Step 4: Wire Select All in LibraryAlbumsView**

The album grid uses tile Buttons + `_selectedTiles` and a `ContextMenu` defined in `LibraryAlbumsView.axaml`. Add "Select All" / "Deselect All" `MenuItem`s to that album-tile `ContextMenu`, with `Click` handlers in `LibraryAlbumsView.axaml.cs`:
```csharp
    private void OnSelectAllAlbums(object? sender, RoutedEventArgs e)
    {
        _selectedTiles.Clear();
        foreach (var tile in CollectAlbumTiles())   // reuse the same tile-collection logic used by Ctrl+A
        {
            tile.Classes.Add("ctrl-selected");
            _selectedTiles.Add(tile);
        }
        if (DataContext is LibraryAlbumsViewModel vm)
            vm.CtrlSelectedAlbums = MultiSelectHelper.GetSelectedData<Album>(_selectedTiles);
    }

    private void OnDeselectAllAlbums(object? sender, RoutedEventArgs e)
    {
        MultiSelectHelper.ClearAlbumSelections(_selectedTiles);
        if (DataContext is LibraryAlbumsViewModel vm)
            vm.CtrlSelectedAlbums = new List<Album>();
    }
```
Use the existing tile-collection helper the view already uses to build the `allTiles` list for `HandleAlbumSelectAll` (extract it into a `CollectAlbumTiles()` method if inline). Add the two `MenuItem`s referencing these handlers (use `Click="OnSelectAllAlbums"` etc.) in the album `ContextMenu` in the axaml, placed near the top of the menu.

- [ ] **Step 5: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 6: Manual verification**

Right-click a track in Songs and in a Playlist → "Select All" highlights all rows; "Deselect All" clears. Right-click an album tile → "Select All" highlights all tiles. Open Metadata after Select All → multi-select editor reflects the full set.

- [ ] **Step 7: Commit**

```bash
git add src/Noctis/Helpers/TrackContextMenuBuilder.cs src/Noctis/Views/LibrarySongsView.axaml.cs src/Noctis/Views/PlaylistView.axaml.cs src/Noctis/Views/LibraryAlbumsView.axaml src/Noctis/Views/LibraryAlbumsView.axaml.cs
git commit -m "feat(selection): add Select All / Deselect All to track and album menus"
```

---

## Task 7: Reset selection when leaving a view

**Files:**
- Modify: `src/Noctis/Views/LibrarySongsView.axaml.cs`
- Modify: `src/Noctis/Views/PlaylistView.axaml.cs`
- Modify: `src/Noctis/Views/LibraryAlbumsView.axaml.cs`
- Modify (if they hold a selection set): `src/Noctis/Views/FavoritesView.axaml.cs`, `LibraryFoldersView.axaml.cs`, `MoreByArtistView.axaml.cs`, `HomeView.axaml.cs`

- [ ] **Step 1: Clear track selection on detach (Songs)**

In `LibrarySongsView.axaml.cs` `OnDetachedFromVisualTree`, after the existing scroll-offset save and before `base.OnDetachedFromVisualTree(e);`, add:
```csharp
        _selectedTracks.Clear();
        foreach (var child in TrackList.GetVisualDescendants())
            if (child is ListBoxItem li) li.Classes.Remove("ctrl-selected");
        if (DataContext is LibrarySongsViewModel selVm) selVm.CtrlSelectedTracks = new List<Track>();
```

- [ ] **Step 2: Clear track selection on detach (Playlist)**

In `PlaylistView.axaml.cs` `OnDetachedFromVisualTree`, add the equivalent block using that view's `_selectedTracks`, `TrackList`, and playlist VM `CtrlSelectedTracks`.

- [ ] **Step 3: Clear album selection on detach (Albums)**

In `LibraryAlbumsView.axaml.cs` `OnDetachedFromVisualTree`, add:
```csharp
        MultiSelectHelper.ClearAlbumSelections(_selectedTiles);
        if (DataContext is LibraryAlbumsViewModel selVm) selVm.CtrlSelectedAlbums = new List<Album>();
```

- [ ] **Step 4: Audit and clear the remaining selection-tracking views**

Run: `grep -rn "_selected\(Tracks\|Tiles\|Items\)" src/Noctis/Views/FavoritesView.axaml.cs src/Noctis/Views/LibraryFoldersView.axaml.cs src/Noctis/Views/MoreByArtistView.axaml.cs src/Noctis/Views/HomeView.axaml.cs`
For each that maintains a selection set, add the matching clear block in its `OnDetachedFromVisualTree` (create the override if absent, calling `base.OnDetachedFromVisualTree(e)`). Skip files with no selection set.

- [ ] **Step 5: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 6: Manual verification**

Songs → Select All (or Ctrl+A) → navigate to Albums → back to Songs: selection is empty. Repeat for Albums and Playlists.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "fix(selection): reset multi-select when navigating away from a view"
```

---

## Self-Review

**Spec coverage:**
- Apple-Music multi-select editor (blank art, N selected header, Mixed, Details/Artwork/Options, hide Animated) → Tasks 1–3.
- Rename kept as a section → Task 4.
- Retire old editor → Task 5.
- Visible Select All in Songs/Albums/Playlists → Task 6.
- Reset selection on navigation → Task 7.
- Sorting tab explicitly deferred (non-goal) → not implemented. ✓

**Type consistency:** `multiSelect` ctor param, `IsMultiSelect`, `ShowAnimatedArtworkTab`, `ShowAlbumArtPlaceholderText`, `ShowRenameSection`, `RenamePreview`, `ApplyRename`, `RenamePattern`, `OpenMultiTrackMetadataWindow`, `BindSelection`, `SelectAllTracks`/`DeselectAllTracks` are defined before use and referenced consistently. `CtrlSelectedTracks`/`CtrlSelectedAlbums` match existing VM members.

**Open implementation checks (call out, don't block):**
- `MusicNoteIcon` resource key may not exist — Task 1 Step 4 gives the PNG-`OpacityMask` fallback used elsewhere in the file.
- Exact insertion points in `Save()` and the Options tab closing tag must be confirmed against current line numbers at execution time (the file shifted during the earlier album-scoped work).
- `PlaylistView`/`LibraryAlbumsView` field and VM property names must be matched to those files (Task 6 Steps 3–4, Task 7 Steps 2–4).
