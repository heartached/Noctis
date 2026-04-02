# Launch Hardening Pass — Design Spec

**Date:** 2026-04-01
**Goal:** Eliminate real-world performance bottlenecks, fix bugs, and harden stability for launch. No architectural changes, no cosmetic changes.

---

## 1. Cache All Long-Lived Views in CachedViewLocator

**Problem:** Only `LibrarySongsView` and `LibraryAlbumsView` are cached. All other views (Artists, CoverFlow, Home, Favorites, Playlists, Statistics, Queue, Settings) are destroyed and recreated from XAML on every navigation (~1s each).

**Files:**
- `src/Noctis/App.axaml.cs` (lines 27-31)

**Change:** Add all long-lived views to the `CachedViewLocator` factory dictionary:
- `LibraryArtistsViewModel` -> `LibraryArtistsView`
- `CoverFlowViewModel` -> `CoverFlowView`
- `HomeViewModel` -> `HomeView`
- `FavoritesViewModel` -> `FavoritesView`
- `LibraryPlaylistsViewModel` -> `LibraryPlaylistsView`
- `StatisticsViewModel` -> `StatisticsView`
- `QueueViewModel` -> `QueueView`
- `SettingsViewModel` -> `SettingsView`

**Not cached (correct):** `PlaylistViewModel`, `AlbumDetailViewModel`, `NowPlayingViewModel` — these are transient with multiple instances.

---

## 2. Add Dirty Checks to Refresh() Methods

**Problem:** Artists, Home, Favorites, Statistics, and Playlists rebuild their full data set on every navigation, even when nothing changed. Songs and Albums already have dirty checks.

**Files:**
- `src/Noctis/ViewModels/LibraryArtistsViewModel.cs`
- `src/Noctis/ViewModels/HomeViewModel.cs`
- `src/Noctis/ViewModels/FavoritesViewModel.cs`
- `src/Noctis/ViewModels/StatisticsViewModel.cs`
- `src/Noctis/ViewModels/LibraryPlaylistsViewModel.cs`

**Pattern (from LibrarySongsViewModel):**
```csharp
private bool _isDirty = true;

// In event handler:
_library.LibraryUpdated += (_, _) => { _isDirty = true; Dispatcher.UIThread.Post(Refresh); };

// In Refresh():
public void Refresh()
{
    if (!_isDirty && Collection.Count > 0) return;
    _isDirty = false;
    // ... actual rebuild
}
```

**Artists-specific:** Also skip re-triggering `FetchAndCacheAsync` when not dirty.

---

## 3. Increase ArtworkCache Capacity

**Problem:** Max 500 cached bitmaps causes frequent eviction and repeated disk I/O for libraries with thousands of albums.

**File:** `src/Noctis/Services/ArtworkCache.cs`

**Change:**
- `MaxCacheSize`: 500 -> 2000
- `EvictBatchSize`: 50 -> 200

**Memory impact:** At 512px decode width, ~80-100KB per bitmap. 2000 entries = ~160-200MB. Acceptable for desktop music player.

---

## 4. Batch Collection Mutations

**Problem:** `AlbumDetailViewModel.AddAlbumToQueue()` calls `AddToQueue()` in a loop, firing N `CollectionChanged` events.

**Files:**
- `src/Noctis/ViewModels/PlayerViewModel.cs` — add `AddRangeToQueue(List<Track>)` method
- `src/Noctis/ViewModels/AlbumDetailViewModel.cs` — call new batch method

**Pattern:** Use existing `_suppressHasContentNotify` guard, add all tracks, fire single Reset notification.

---

## 5. File I/O Hardening

**Problem:** Some file operations in `OfflineCacheService` lack try/catch.

**Files:**
- `src/Noctis/Services/OfflineCacheService.cs` — wrap `File.Move` and `File.WriteAllTextAsync` in try/catch with `DebugLogger.Error`

---

## 6. Dead Code Cleanup

**Problem:** Deleted files (converters, GenreDetailViewModel, TrackContextMenu) may have left orphaned references.

**Change:** Grep for references to deleted types. Remove any remaining `using` statements, field declarations, or registrations.

---

## Out of Scope

- SQLite-first migration (not needed under 50k tracks)
- Pagination / data windowing
- New virtualization strategies beyond Avalonia's built-in ListBox virtualization
- UI restyling
- New features
