# Global Cover Flow Toggle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the existing Albums-only "Library / Cover Flow" pill toggle a global view-mode toggle available from Home, Songs, Albums, Artists, Folders, Playlists, and Favorites, with Cover Flow behaving as a sticky global mode.

**Architecture:** `MainWindowViewModel` owns one `IsCoverFlowMode` flag plus the underlying-section key. The Library/CoverFlow swap moves out of the Albums-internal path and into the shell: when `IsCoverFlowMode` is true, `CurrentView` is the shared `_coverFlowVm`; sidebar navigation while in this mode updates the underlying section key without changing `CurrentView`. Navigating to a non-eligible section (e.g. opening album detail) auto-exits Cover Flow.

**Tech Stack:** .NET 8, Avalonia 11, CommunityToolkit MVVM, MVVM source generators (`[ObservableProperty]`, `[RelayCommand]`).

**Spec:** `docs/superpowers/specs/2026-04-29-global-cover-flow-toggle-design.md`

**Note on testing:** The repo has no unit tests covering MainWindow/TopBar/CoverFlow orchestration, and `tests/Velour.Tests/` has known baseline compile failures unrelated to this work (see `.claude/rules/testing.md`). Verification for this plan is `dotnet build src/Noctis/Noctis.csproj -v minimal` plus a manual pass through the seven sections. Adding new test scaffolding is out of scope.

---

## File Structure

**Modify:**
- `src/Noctis/ViewModels/TopBarViewModel.cs` — rename Albums-specific toggle members to general ones; drop the `AlbumsViewModeChanged` event.
- `src/Noctis/ViewModels/MainWindowViewModel.cs` — replace `_isAlbumsCoverFlowMode` with global `IsCoverFlowMode` + `_currentSectionKey`; add Enter/Exit helpers; rewire `Navigate`, `SetupAlbumsViewModeToggle`, `RestoreTopBarActionsForView`, `OpenAlbumDetail`, etc.
- `src/Noctis/Views/TopBarView.axaml` — update bindings to renamed properties/commands.
- `src/Noctis/Views/MainWindow.axaml` — no structural change needed (content is already a single `ContentControl` bound to `CurrentView`); double-check on inspection.

**No new files.** **No deletions.**

---

## Task 1: Generalize the toggle members on `TopBarViewModel`

**Files:**
- Modify: `src/Noctis/ViewModels/TopBarViewModel.cs:59-63`, `:156-177`

- [ ] **Step 1: Rename observable backing fields and methods**

In `src/Noctis/ViewModels/TopBarViewModel.cs`, replace the block at lines 59-63:

```csharp
    // Albums view mode toggle (Library / Up Next)
    [ObservableProperty] private bool _hasAlbumsViewModeToggle;
    [ObservableProperty] private bool _isAlbumsCoverFlowMode;
    [ObservableProperty] private ICommand? _albumsSetLibraryModeCommand;
    [ObservableProperty] private ICommand? _albumsSetCoverFlowModeCommand;
```

with:

```csharp
    // Global view mode toggle (Library / Cover Flow) — shown on Home, Songs, Albums, Artists, Folders, Playlists, Favorites
    [ObservableProperty] private bool _hasViewModeToggle;
    [ObservableProperty] private bool _isCoverFlowMode;
    [ObservableProperty] private ICommand? _setLibraryModeCommand;
    [ObservableProperty] private ICommand? _setCoverFlowModeCommand;
```

Then replace the block at lines 156-177:

```csharp
    /// <summary>Fires when the albums view mode toggle changes.</summary>
    public event EventHandler<bool>? AlbumsViewModeChanged;

    public void ShowAlbumsViewModeToggle(ICommand setLibraryMode, ICommand setCoverFlowMode, bool isCoverFlowMode)
    {
        AlbumsSetLibraryModeCommand = setLibraryMode;
        AlbumsSetCoverFlowModeCommand = setCoverFlowMode;
        IsAlbumsCoverFlowMode = isCoverFlowMode;
        HasAlbumsViewModeToggle = true;
    }

    public void HideAlbumsViewModeToggle()
    {
        HasAlbumsViewModeToggle = false;
        AlbumsSetLibraryModeCommand = null;
        AlbumsSetCoverFlowModeCommand = null;
    }

    partial void OnIsAlbumsCoverFlowModeChanged(bool value)
    {
        AlbumsViewModeChanged?.Invoke(this, value);
    }
```

with:

```csharp
    public void ShowViewModeToggle(ICommand setLibraryMode, ICommand setCoverFlowMode, bool isCoverFlowMode)
    {
        SetLibraryModeCommand = setLibraryMode;
        SetCoverFlowModeCommand = setCoverFlowMode;
        IsCoverFlowMode = isCoverFlowMode;
        HasViewModeToggle = true;
    }

    public void HideViewModeToggle()
    {
        HasViewModeToggle = false;
        SetLibraryModeCommand = null;
        SetCoverFlowModeCommand = null;
        IsCoverFlowMode = false;
    }
```

(The `AlbumsViewModeChanged` event and the `OnIsAlbumsCoverFlowModeChanged` partial method are removed entirely — `MainWindowViewModel` will drive the swap directly, so no event is needed.)

- [ ] **Step 2: Build to surface every consumer of the renamed members**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: FAIL — compile errors in `MainWindowViewModel.cs` and `TopBarView.axaml` (and possibly elsewhere) referencing `HasAlbumsViewModeToggle`, `IsAlbumsCoverFlowMode`, `AlbumsSetLibraryModeCommand`, `AlbumsSetCoverFlowModeCommand`, `ShowAlbumsViewModeToggle`, `HideAlbumsViewModeToggle`, `AlbumsViewModeChanged`. **Do not commit yet — leave the tree broken until Tasks 2-4 are done.**

---

## Task 2: Update XAML bindings in `TopBarView.axaml`

**Files:**
- Modify: `src/Noctis/Views/TopBarView.axaml` (lines around 207-223 — exact lines may differ; the snippet below is unique enough to locate)

- [ ] **Step 1: Locate and replace the toggle markup**

Find the block (starting near line 207 in the Albums view):

```xml
                    <!-- Albums view mode toggle (Library / Cover Flow) -->
                    <Border IsVisible="{Binding HasAlbumsViewModeToggle}"
```

Replace `HasAlbumsViewModeToggle` with `HasViewModeToggle`.

In the same `<Border>`, find and replace:
- `Command="{Binding AlbumsSetLibraryModeCommand}"` → `Command="{Binding SetLibraryModeCommand}"`
- `Command="{Binding AlbumsSetCoverFlowModeCommand}"` → `Command="{Binding SetCoverFlowModeCommand}"`

If the comment text reads "Albums view mode toggle (Library / Cover Flow)", change it to "View mode toggle (Library / Cover Flow)".

- [ ] **Step 2: Search for any other binding to the old names**

Run: `grep -rn "HasAlbumsViewModeToggle\|IsAlbumsCoverFlowMode\|AlbumsSetLibraryModeCommand\|AlbumsSetCoverFlowModeCommand\|AlbumsViewModeChanged" src/Noctis/Views/ src/Noctis/Controls/`
Expected: no matches. If matches appear, rename them the same way (`Has*` → drop "Albums", commands lose "Albums" prefix).

(Tree still broken — `MainWindowViewModel.cs` not yet updated.)

---

## Task 3: Replace Albums-specific cover-flow state in `MainWindowViewModel`

**Files:**
- Modify: `src/Noctis/ViewModels/MainWindowViewModel.cs:114` (field), `:732`, `:752-764` (helper updates), `:823` (Navigate hook), `:967` (`OpenAlbumDetail`), `:1059-1066` (`ClearAllTopBarActions`), `:1068-1096` (toggle setup + mode setters), `:1099-1108` (`RestoreTopBarActionsForView`)

The ordering inside this task matters. Apply Steps 1-7 in order, then build once at the end.

- [ ] **Step 1: Replace the state field**

At line 114, replace:

```csharp
    private bool _isAlbumsCoverFlowMode;
```

with:

```csharp
    /// <summary>True when the global Cover Flow overlay is active. Survives sidebar navigation between toggle-eligible sections; auto-exits when navigating to an ineligible section.</summary>
    private bool _isCoverFlowMode;

    /// <summary>The sidebar key for the section currently selected underneath Cover Flow (e.g. "home", "songs", "albums"). Tracked so clicking Library returns to the right section.</summary>
    private string _currentSectionKey = "home";

    /// <summary>Sidebar keys whose section is allowed to display the Library/Cover Flow toggle.</summary>
    private static readonly HashSet<string> ToggleEligibleSections = new(StringComparer.Ordinal)
    {
        "home", "songs", "albums", "artists", "folders", "playlists", "favorites"
    };
```

- [ ] **Step 2: Rewrite `SetupAlbumsViewModeToggle` and the mode setters (lines 1068-1096)**

Replace this block:

```csharp
    private void SetupAlbumsViewModeToggle()
    {
        TopBar.ShowAlbumsViewModeToggle(
            new RelayCommand(SetAlbumsLibraryMode),
            new RelayCommand(SetAlbumsCoverFlowMode),
            _isAlbumsCoverFlowMode);

        // Hide search in cover flow mode (must run after CurrentTabName sets IsSearchVisible=true)
        if (_isAlbumsCoverFlowMode)
            TopBar.IsSearchVisible = false;
    }

    private void SetAlbumsLibraryMode()
    {
        if (!_isAlbumsCoverFlowMode) return;
        _isAlbumsCoverFlowMode = false;
        TopBar.IsAlbumsCoverFlowMode = false;
        TopBar.IsSearchVisible = true;
        CurrentView = _albumsVm;
    }

    private void SetAlbumsCoverFlowMode()
    {
        if (_isAlbumsCoverFlowMode) return;
        _isAlbumsCoverFlowMode = true;
        TopBar.IsAlbumsCoverFlowMode = true;
        TopBar.IsSearchVisible = false;
        CurrentView = _coverFlowVm;
    }
```

with:

```csharp
    /// <summary>
    /// Shows the global Library/Cover Flow toggle in the top bar and reflects the current mode.
    /// Call after navigating to a toggle-eligible section.
    /// </summary>
    private void SetupGlobalViewModeToggle()
    {
        TopBar.ShowViewModeToggle(
            new RelayCommand(ExitCoverFlowMode),
            new RelayCommand(EnterCoverFlowMode),
            _isCoverFlowMode);

        // Hide search in cover flow mode (must run after CurrentTabName sets IsSearchVisible=true)
        if (_isCoverFlowMode)
            TopBar.IsSearchVisible = false;
    }

    private void EnterCoverFlowMode()
    {
        if (_isCoverFlowMode) return;
        _isCoverFlowMode = true;
        TopBar.IsCoverFlowMode = true;
        TopBar.IsSearchVisible = false;
        CurrentView = _coverFlowVm;
    }

    private void ExitCoverFlowMode()
    {
        if (!_isCoverFlowMode) return;
        _isCoverFlowMode = false;
        TopBar.IsCoverFlowMode = false;
        TopBar.IsSearchVisible = true;
        // Return to the section that was selected underneath
        CurrentView = ResolveSectionView(_currentSectionKey);
    }

    /// <summary>Resolves a sidebar section key to the cached long-lived ViewModel for that section. Refreshes content as needed (mirrors Navigate's switch).</summary>
    private ViewModelBase ResolveSectionView(string key) => key switch
    {
        "home"      => RefreshAndReturn(_homeVm),
        "songs"     => RefreshAndReturnSongs(_songsVm),
        "albums"    => ResetFilterAndReturnAlbums(),
        "artists"   => ResetAndReturnArtists(),
        "folders"   => RefreshAndReturnFolders(_foldersVm),
        "playlists" => RefreshAndReturnPlaylists(_playlistsVm),
        "favorites" => RefreshAndReturnFavorites(_favoritesVm),
        _           => RefreshAndReturn(_homeVm)
    };
```

Note that `ResetFilterAndReturnAlbums` currently contains a `_isAlbumsCoverFlowMode` reference (line 864). Step 3 fixes it.

- [ ] **Step 3: Strip the cover-flow branch from `ResetFilterAndReturnAlbums` (line 852-865)**

Replace:

```csharp
    private ViewModelBase ResetFilterAndReturnAlbums()
    {
        // Clear any stale artist filter from OnArtistOpened so the user
        // sees the full album grid when navigating via the sidebar.
        _albumsVm.ClearArtistFilter();

        // Use Refresh() so the dirty check prevents unnecessary rebuilds.
        // ApplyFilter("") was bypassing the dirty check, forcing a full
        // rebuild on every navigation even when data hadn't changed.
        _albumsVm.Refresh();

        // Return cover flow or library view based on remembered mode
        return _isAlbumsCoverFlowMode ? _coverFlowVm : _albumsVm;
    }
```

with:

```csharp
    private LibraryAlbumsViewModel ResetFilterAndReturnAlbums()
    {
        // Clear any stale artist filter from OnArtistOpened so the user
        // sees the full album grid when navigating via the sidebar.
        _albumsVm.ClearArtistFilter();
        _albumsVm.Refresh();
        return _albumsVm;
    }
```

(Cover-flow swap is now decided at the shell, not inside this helper.)

- [ ] **Step 4: Rewrite `Navigate` to handle sticky cover flow and auto-exit (lines ~774-830)**

Replace the body of `Navigate` (everything inside the method, keeping the `[RelayCommand]` and signature):

```csharp
    [RelayCommand]
    private void Navigate(string key)
    {
        DebugLogger.Info(DebugLogger.Category.UI, "Navigate", $"key={key}, from={GetCurrentViewKey()}, coverFlow={_isCoverFlowMode}");
        ClearNavigationHistory();

        var goingToEligibleSection = ToggleEligibleSections.Contains(key);

        // If we're in Cover Flow and the user clicks an ineligible destination
        // (album detail, settings, lyrics, etc.), auto-exit Cover Flow first
        // so the toggle hide and the destination view land in a consistent state.
        if (_isCoverFlowMode && !goingToEligibleSection)
        {
            _isCoverFlowMode = false;
            TopBar.IsCoverFlowMode = false;
            TopBar.IsSearchVisible = true;
        }

        // Track which top-level section the user is conceptually on. While in
        // Cover Flow this is the section "underneath" the overlay.
        if (goingToEligibleSection)
            _currentSectionKey = key;

        // Resolve the destination view. If we're staying in Cover Flow (eligible
        // destination + sticky mode), the visible content stays as the carousel;
        // only the underlying section key changes.
        if (_isCoverFlowMode && goingToEligibleSection)
        {
            // Touch the underlying section so its data is fresh when the user
            // exits Cover Flow (mirrors what Navigate would normally do).
            _ = ResolveSectionView(key);
            // CurrentView stays as _coverFlowVm.
        }
        else
        {
            CurrentView = key switch
            {
                "home" => RefreshAndReturn(_homeVm),
                "songs" => RefreshAndReturnSongs(_songsVm),
                "albums" => ResetFilterAndReturnAlbums(),
                "artists" => ResetAndReturnArtists(),
                "folders" => RefreshAndReturnFolders(_foldersVm),
                "playlists" => RefreshAndReturnPlaylists(_playlistsVm),
                "favorites" => RefreshAndReturnFavorites(_favoritesVm),
                "statistics" => RefreshAndReturnStatistics(_statisticsVm),
                "queue" => _queueVm,
                "lyrics" => EnsureLyricsAndReturn(_lyricsVm),
                "settings" => RefreshAndReturnSettings(),
                _ when key.StartsWith("playlist:") => CreatePlaylistView(key),
                _ => _homeVm
            };
        }

        // Close queue popup and clear search when switching views
        Player.IsQueuePopupOpen = false;
        TopBar.SearchText = string.Empty;

        TopBar.CurrentTabName = key switch
        {
            "home" => "Home",
            "songs" => "Songs",
            "albums" => "Albums",
            "artists" => "Artists",
            "folders" => "Folders",
            "playlists" => "Playlists",
            "favorites" => "Favorites",
            "statistics" => "Statistics",
            "queue" => "Queue",
            "lyrics" => "Lyrics",
            "settings" => "Settings",
            _ when key.StartsWith("playlist:") => "Playlist",
            _ => "Library"
        };

        // Clear all top bar actions, then set up the correct ones for the destination
        ClearAllTopBarActions();
        if (goingToEligibleSection)
            SetupGlobalViewModeToggle();
        if (key == "songs")
            SetupSongsTopBarActions();
        else if (key == "playlists")
            TopBar.ShowPlaylistActions(_playlistsVm.CreateSmartPlaylistCommand);
        else if (key == "favorites")
            TopBar.ShowFavoritesActions(_favoritesVm.ShuffleAllCommand, _favoritesVm.PlayAllCommand);

        RefreshBackButton();
    }
```

Key changes from the current implementation:
- Auto-exit cover flow before navigating to an ineligible destination.
- Track `_currentSectionKey` for eligible destinations.
- Skip the `CurrentView` swap when staying in Cover Flow on a sidebar tab change (sticky behavior).
- Replace the previous `if (key == "albums") SetupAlbumsViewModeToggle()` branch with a single "if eligible → `SetupGlobalViewModeToggle()`" call that runs for all 7 sections.

- [ ] **Step 5: Update `ClearAllTopBarActions` (lines 1059-1066)**

Replace:

```csharp
    /// <summary>Clears all page-specific, playlist, and artist top bar actions.</summary>
    private void ClearAllTopBarActions()
    {
        ClearTopBarPageActions();
        TopBar.HidePlaylistActions();
        TopBar.HideArtistActions();
        TopBar.HideFavoritesActions();
        TopBar.HideAlbumsViewModeToggle();
    }
```

with:

```csharp
    /// <summary>Clears all page-specific, playlist, artist, and view-mode top bar actions.</summary>
    private void ClearAllTopBarActions()
    {
        ClearTopBarPageActions();
        TopBar.HidePlaylistActions();
        TopBar.HideArtistActions();
        TopBar.HideFavoritesActions();
        TopBar.HideViewModeToggle();
    }
```

- [ ] **Step 6: Update `RestoreTopBarActionsForView` (lines 1099-1108)**

Replace:

```csharp
    /// <summary>Restores the correct top bar actions when navigating back to a view.</summary>
    private void RestoreTopBarActionsForView(ViewModelBase view)
    {
        if (ReferenceEquals(view, _songsVm))
            SetupSongsTopBarActions();
        else if (ReferenceEquals(view, _albumsVm) || ReferenceEquals(view, _coverFlowVm))
            SetupAlbumsViewModeToggle();
        else if (ReferenceEquals(view, _playlistsVm))
            TopBar.ShowPlaylistActions(_playlistsVm.CreateSmartPlaylistCommand);
        // Artist actions for _albumsVm are restored in CaptureRestoreState's lambda
    }
```

with:

```csharp
    /// <summary>Restores the correct top bar actions when navigating back to a view.</summary>
    private void RestoreTopBarActionsForView(ViewModelBase view)
    {
        // The toggle is shown for any of the 7 long-lived section views or while in Cover Flow.
        if (ReferenceEquals(view, _homeVm)
            || ReferenceEquals(view, _songsVm)
            || ReferenceEquals(view, _albumsVm)
            || ReferenceEquals(view, _artistsVm)
            || ReferenceEquals(view, _foldersVm)
            || ReferenceEquals(view, _playlistsVm)
            || ReferenceEquals(view, _favoritesVm)
            || ReferenceEquals(view, _coverFlowVm))
        {
            SetupGlobalViewModeToggle();
        }

        if (ReferenceEquals(view, _songsVm))
            SetupSongsTopBarActions();
        else if (ReferenceEquals(view, _playlistsVm))
            TopBar.ShowPlaylistActions(_playlistsVm.CreateSmartPlaylistCommand);
        else if (ReferenceEquals(view, _favoritesVm))
            TopBar.ShowFavoritesActions(_favoritesVm.ShuffleAllCommand, _favoritesVm.PlayAllCommand);
        // Artist actions for _albumsVm are restored in CaptureRestoreState's lambda
    }
```

- [ ] **Step 7: Remove the toggle setup from `OpenAlbumDetail` (line 967)**

In `OpenAlbumDetail` (around line 954-968), find and delete the trailing:

```csharp
        SetupAlbumsViewModeToggle();
```

(Album Detail is not in the toggle-eligible set per the spec; removing this line ensures the toggle is hidden on detail pages. `ClearAllTopBarActions()` earlier in the method already hides the toggle.)

- [ ] **Step 8: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: PASS, no errors. If any reference to the old member names lingers (e.g., XAML compilation), fix it the same way as Tasks 1-2.

- [ ] **Step 9: Commit**

```bash
git add src/Noctis/ViewModels/TopBarViewModel.cs \
        src/Noctis/ViewModels/MainWindowViewModel.cs \
        src/Noctis/Views/TopBarView.axaml
git commit -m "feat(ui): make Library/Cover Flow toggle global across main sections"
```

---

## Task 4: Manual verification pass

**Files:** none (runtime check only).

The seven scenarios below correspond directly to the spec's requirements.

- [ ] **Step 1: Launch the app**

Run: `dotnet run --project src/Noctis/Noctis.csproj`
Expected: app starts on Home with the Library/Cover Flow toggle visible in the top bar (Library pill active).

- [ ] **Step 2: Toggle visibility on each eligible section**

Click each of: Home, Songs, Albums, Artists, Folders, Playlists, Favorites.
Expected: the toggle is visible in the top bar on every one of them.

- [ ] **Step 3: Toggle hidden on ineligible sections**

Click an album to open Album Detail; click Settings; open Lyrics from the playback bar.
Expected: the toggle is hidden in each of these.

- [ ] **Step 4: Sticky behavior across sidebar tab switches**

From Songs, click Cover Flow → carousel appears.
Click Albums in the sidebar → carousel still visible (Cover Flow pill still active).
Click Artists in the sidebar → carousel still visible.
Click Library → returns to Artists' library content (the section last selected).
Expected: matches the description above. The Cover Flow pill is active until you click Library.

- [ ] **Step 5: Auto-exit on ineligible navigation**

From Albums (in Cover Flow mode), open an album by clicking it from the sidebar's recent or via search... actually the easiest path: while in Cover Flow on Albums, navigate to Settings via the sidebar.
Expected: Cover Flow exits, Settings opens, the toggle is hidden. Returning to Albums via the sidebar shows the Library grid (Library pill active), not Cover Flow.

- [ ] **Step 6: Search visibility**

Enter Cover Flow from any section.
Expected: the search box is hidden while in Cover Flow.
Click Library.
Expected: search box reappears (unless on Home / Settings / Lyrics, which already hide search per existing `OnCurrentTabNameChanged` logic in `TopBarViewModel`).

- [ ] **Step 7: Album-detail back-nav still works**

From Albums (Library mode), click an album → Album Detail. Use the Back button.
Expected: returns to Albums library grid, toggle re-appears (Library pill active). No regression.

- [ ] **Step 8: Commit any follow-up fixes**

If Steps 2-7 surface issues, fix them and commit each fix as its own commit referencing which scenario it addresses. Re-run the relevant scenarios after each fix.

---

## Self-review notes (for the implementing agent)

- The `CoverFlowViewModel` lifecycle (queue subscriptions etc.) is not explicitly start/stopped in this plan because the existing code doesn't do that today either — the VM is created at MainWindow construction and lives forever. The spec mentioned this as a "nice to have" risk but the existing behavior is acceptable; do not add lifecycle hooks unless manual verification (Step 4) reveals a leak or stale-data symptom.
- `IsLongLivedView` (line ~750) and `GetCurrentViewKey` (line ~1110) already include `_coverFlowVm` correctly — no changes needed there.
- Do not touch `LibraryAlbumsView.axaml` or `LibraryAlbumsViewModel.cs`; the Albums-internal swap was driven entirely from `MainWindowViewModel`, so removing the Albums-specific path in Task 3 is sufficient. (If grep reveals any Albums-side reference to the old toggle members, treat that as an oversight and rename in place.)
