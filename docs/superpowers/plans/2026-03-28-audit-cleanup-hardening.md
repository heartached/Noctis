# Noctis Full Audit: Performance, Stability, Cleanup & Polish

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Noctis faster, more stable, cleaner, and smoother across every page — scaling reliably to 200K+ audio files.

**Architecture:** Phased approach — each phase is a self-contained commit. Phase 1 fixes perf/scale bottlenecks. Phase 2 hardens crash paths and security. Phase 3 removes dead code. Phase 4 adds polish animations.

**Tech Stack:** Avalonia 11.2.3, CommunityToolkit.Mvvm, LibVLCSharp, .NET 8.0

---

## File Map

### Phase 1: Performance & Scale
- Modify: `src/Noctis/Services/LibraryService.cs` (RebuildIndexesAsync, ScanAsync, FavoritesCount)
- Modify: `src/Noctis/Services/ArtworkCache.cs` (concurrent cache, decode size reduction)
- Modify: `src/Noctis/Controls/CachedImage.cs` (pool background loads)
- Modify: `src/Noctis/ViewModels/FavoritesViewModel.cs` (move Refresh off UI thread)
- Modify: `src/Noctis/ViewModels/HomeViewModel.cs` (move Refresh off UI thread)
- Modify: `src/Noctis/ViewModels/SidebarViewModel.cs` (cache favorites count)
- Modify: `src/Noctis/ViewModels/MainWindowViewModel.cs` (stagger startup refreshes)
- Modify: `src/Noctis/ViewModels/PlayerViewModel.cs` (batch queue ReplaceAll)
- Modify: `src/Noctis/Helpers/BulkObservableCollection.cs` (add AddRange)
- Modify: `src/Noctis/Services/PersistenceService.cs` (async file I/O with buffer)
- Modify: `src/Noctis/Services/DominantColorExtractor.cs` (cache extracted colors)
- Modify: `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs` (track dirty state)

### Phase 2: Stability & Security
- Modify: `src/Noctis/Services/VlcAudioPlayer.cs` (dispose safety, play null guard)
- Modify: `src/Noctis/Services/MetadataService.cs` (timeout TagLib, harden paths)
- Modify: `src/Noctis/Services/PersistenceService.cs` (deserialize error handling)
- Modify: `src/Noctis/ViewModels/PlayerViewModel.cs` (queue bounds guards)
- Modify: `src/Noctis/ViewModels/MainWindowViewModel.cs` (drop import hardening)
- Modify: `src/Noctis/Services/LibraryService.cs` (scan resilience)
- Modify: `src/Noctis/Services/DominantColorExtractor.cs` (null bitmap guard)
- Modify: `src/Noctis/ViewModels/LyricsViewModel.cs` (null guards in sync timer)
- Modify: `src/Noctis/Program.cs` (unhandled exception logging)

### Phase 3: Cleanup
- Delete: `src/Noctis/Helpers/PlaylistMenuHelper.cs`
- Delete: `src/Noctis/Controls/TrackContextMenu.axaml`
- Delete: `src/Noctis/Controls/TrackContextMenu.axaml.cs`
- Delete: `src/Noctis/Converters/BoolToFontWeightConverter.cs`
- Delete: `src/Noctis/Converters/BoolToMuteIconConverter.cs`
- Delete: `src/Noctis/Converters/BoolToScaleConverter.cs`
- Delete: `src/Noctis/Converters/BoolToShuffleIconConverter.cs`
- Delete: `src/Noctis/Converters/NullToVisibilityConverter.cs`
- Delete: `src/Noctis/Converters/RepeatModeToIconConverter.cs`
- Modify: `src/Noctis/Assets/Icons.axaml` (remove unused icon definitions)
- Modify: `src/Noctis/Assets/Styles.axaml` (remove unused custom styles)

### Phase 4: Navigation & Animations
- Modify: `src/Noctis/Assets/Styles.axaml` (add transition styles)
- Modify: `src/Noctis/Views/MainWindow.axaml` (content area transitions)
- Modify: `src/Noctis/Views/SidebarView.axaml` (hover/selection transitions)
- Modify: `src/Noctis/Views/PlaybackBarView.axaml` (button hover transitions)

---

## Phase 1: Performance & Scale

### Task 1: Replace lock-based ArtworkCache with ConcurrentDictionary for reduced contention

**Files:**
- Modify: `src/Noctis/Services/ArtworkCache.cs`

The current LRU cache uses a single `lock(CacheLock)` for every TryGet, which means every album tile in a scrolling grid serializes on this lock. With 200K tracks and fast scrolling, this is a bottleneck. Switch to `ConcurrentDictionary` for lock-free reads and reduce decode width from 512 to 300 (album tiles render at ~180px, so 300 is plenty with 2x retina coverage).

- [ ] **Step 1: Rewrite ArtworkCache to use ConcurrentDictionary + lightweight eviction**

Replace the entire `ArtworkCache` implementation:

```csharp
using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace Noctis.Services;

/// <summary>
/// Thread-safe LRU bitmap cache shared across the application.
/// Uses ConcurrentDictionary for lock-free reads on cache hits.
/// Decodes artwork at thumbnail size (300px) to minimize memory and decode time.
/// </summary>
public static class ArtworkCache
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object EvictionLock = new();
    private const int MaxCacheSize = 500;
    private const int EvictionBatch = 50;
    private const int DecodeWidth = 300;
    private static long _accessCounter;

    private sealed class CacheEntry
    {
        public Bitmap Bitmap { get; init; } = null!;
        public long LastAccess;
    }

    /// <summary>
    /// Returns a cached bitmap if available, or null on cache miss. No I/O performed.
    /// Lock-free on the hot path.
    /// </summary>
    public static Bitmap? TryGet(string path)
    {
        if (Cache.TryGetValue(path, out var entry))
        {
            entry.LastAccess = Interlocked.Increment(ref _accessCounter);
            return entry.Bitmap;
        }
        return null;
    }

    /// <summary>
    /// Removes a cached bitmap for the given path so the next load reads fresh data from disk.
    /// </summary>
    public static void Invalidate(string path)
    {
        if (Cache.TryRemove(path, out var removed))
            removed.Bitmap.Dispose();
        Invalidated?.Invoke(path);
    }

    /// <summary>
    /// Raised after a cached entry is removed, allowing live UI controls to reload.
    /// </summary>
    public static event Action<string>? Invalidated;

    /// <summary>
    /// Loads a bitmap from disk, caches it, and returns it.
    /// Safe to call from any thread. Returns null if the file doesn't exist or can't be decoded.
    /// </summary>
    public static Bitmap? LoadAndCache(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            // Check again after I/O (another thread may have cached it)
            if (Cache.TryGetValue(path, out var existing))
            {
                existing.LastAccess = Interlocked.Increment(ref _accessCounter);
                return existing.Bitmap;
            }

            Bitmap bitmap;
            using (var stream = File.OpenRead(path))
                bitmap = Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.MediumQuality);

            var entry = new CacheEntry
            {
                Bitmap = bitmap,
                LastAccess = Interlocked.Increment(ref _accessCounter)
            };

            if (!Cache.TryAdd(path, entry))
            {
                // Another thread won the race — dispose ours and use theirs
                bitmap.Dispose();
                if (Cache.TryGetValue(path, out var winner))
                    return winner.Bitmap;
                return null;
            }

            // Evict oldest entries if cache has grown too large
            if (Cache.Count > MaxCacheSize)
                EvictOldest();

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static void EvictOldest()
    {
        if (!Monitor.TryEnter(EvictionLock))
            return; // another thread is already evicting

        try
        {
            if (Cache.Count <= MaxCacheSize)
                return;

            var toRemove = Cache
                .OrderBy(kv => kv.Value.LastAccess)
                .Take(EvictionBatch)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                if (Cache.TryRemove(key, out var removed))
                    removed.Bitmap.Dispose();
            }
        }
        finally
        {
            Monitor.Exit(EvictionLock);
        }
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds, no errors.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Services/ArtworkCache.cs
git commit -m "perf: replace lock-based ArtworkCache with ConcurrentDictionary for lock-free reads"
```

---

### Task 2: Move FavoritesViewModel.Refresh() off the UI thread

**Files:**
- Modify: `src/Noctis/ViewModels/FavoritesViewModel.cs`

Currently `FavoritesViewModel.Refresh()` does all LINQ grouping, sorting, and row-building synchronously on the UI thread. With 200K tracks and many favorites, this blocks the UI for hundreds of milliseconds.

- [ ] **Step 1: Make Refresh async with background thread processing**

Replace the `Refresh()` method (around line 62):

```csharp
/// <summary>Refreshes the Favorites tab content with latest data.</summary>
public void Refresh()
{
    // Run heavy LINQ work on a background thread to avoid UI freeze
    ThreadPool.QueueUserWorkItem(_ =>
    {
        var favTracks = _library.Tracks
            .Where(t => t.IsFavorite)
            .ToList();

        var items = new List<FavoriteItem>();

        // Group favorite tracks by album
        var grouped = favTracks.GroupBy(t => t.AlbumId);
        foreach (var group in grouped)
        {
            var album = _library.GetAlbumById(group.Key);
            if (album != null && album.Tracks.Count > 1 && album.IsAllTracksFavorite)
            {
                items.Add(new FavoriteItem { Album = album });
            }
            else
            {
                foreach (var track in group.OrderBy(t => t.TrackNumber))
                    items.Add(new FavoriteItem { Track = track });
            }
        }

        // Sort: by artist then title
        items.Sort((a, b) =>
        {
            int cmp = string.Compare(a.Subtitle, b.Subtitle, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });

        // Build rows for the virtualized grid
        var rows = new List<FavoriteItemRow>();
        for (int i = 0; i < items.Count; i += ColumnsPerRow)
        {
            rows.Add(new FavoriteItemRow
            {
                Items = items.GetRange(i, Math.Min(ColumnsPerRow, items.Count - i))
            });
        }

        Dispatcher.UIThread.Post(() =>
        {
            FavoriteItems.ReplaceAll(items);
            FavoriteItemRows.ReplaceAll(rows);
        });
    });
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/ViewModels/FavoritesViewModel.cs
git commit -m "perf: move FavoritesViewModel.Refresh() off UI thread"
```

---

### Task 3: Optimize SidebarViewModel.RefreshFavoritesCount() to avoid scanning entire library

**Files:**
- Modify: `src/Noctis/ViewModels/SidebarViewModel.cs`

`RefreshFavoritesCount()` calls `_library.Tracks.Count(t => t.IsFavorite)` which iterates every track. This fires on every `LibraryUpdated` AND `FavoritesChanged` event. With 200K tracks, this is a hot path.

- [ ] **Step 1: Defer the count to a background thread**

Replace line 72-75:

```csharp
/// <summary>Recalculates the number of favorited tracks.</summary>
public void RefreshFavoritesCount()
{
    ThreadPool.QueueUserWorkItem(_ =>
    {
        var count = _library.Tracks.Count(t => t.IsFavorite);
        Dispatcher.UIThread.Post(() => FavoritesCount = count);
    });
}
```

Add missing using at top of file if not present:

```csharp
using Avalonia.Threading;
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/ViewModels/SidebarViewModel.cs
git commit -m "perf: move favorites count computation off UI thread"
```

---

### Task 4: Use BulkObservableCollection in PlayerViewModel queue operations

**Files:**
- Modify: `src/Noctis/Helpers/BulkObservableCollection.cs`
- Modify: `src/Noctis/ViewModels/PlayerViewModel.cs`

Currently `ReplaceQueueAndPlay` and `ToggleShuffle` use `UpNext.Clear()` + N×`UpNext.Add()` which fires N+1 collection change events. With 200K-track queues this causes massive UI churn. Change `UpNext` and `History` to `BulkObservableCollection<Track>` and use `ReplaceAll`.

- [ ] **Step 1: Add AddRange to BulkObservableCollection**

Add to `src/Noctis/Helpers/BulkObservableCollection.cs` after `ReplaceAll`:

```csharp
/// <summary>
/// Adds multiple items to the collection, firing a single Reset event.
/// </summary>
public void AddRange(IEnumerable<T> items)
{
    foreach (var item in items)
        Items.Add(item);

    OnPropertyChanged(new PropertyChangedEventArgs("Count"));
    OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
}
```

- [ ] **Step 2: Change UpNext and History to BulkObservableCollection in PlayerViewModel**

In `PlayerViewModel.cs`, change the property declarations (around line 52-55):

```csharp
/// <summary>Upcoming tracks to play.</summary>
public BulkObservableCollection<Track> UpNext { get; } = new();

/// <summary>Previously played tracks (most recent first).</summary>
public BulkObservableCollection<Track> History { get; } = new();
```

Update the `using` at top:

```csharp
using Noctis.Helpers;
```

- [ ] **Step 3: Replace N×Add with ReplaceAll/AddRange in ReplaceQueueAndPlay**

Replace the queue-building section of `ReplaceQueueAndPlay` (around line 416-422):

```csharp
// Clear and rebuild the queue
var upNextTracks = new List<Track>(tracks.Count - startIndex - 1);
for (int i = startIndex + 1; i < tracks.Count; i++)
    upNextTracks.Add(tracks[i]);
UpNext.ReplaceAll(upNextTracks);
```

- [ ] **Step 4: Replace N×Add with ReplaceAll in ToggleShuffle**

Replace the shuffle queue rebuild (around lines 207-227):

```csharp
if (IsShuffleEnabled)
{
    _originalQueue = UpNext.ToList();
    var shuffled = UpNext
        .Where(t => !t.SkipWhenShuffling)
        .OrderBy(_ => Random.Shared.Next())
        .ToList();
    UpNext.ReplaceAll(shuffled);
}
else if (_originalQueue.Count > 0)
{
    UpNext.ReplaceAll(_originalQueue);
    _originalQueue.Clear();
}
```

- [ ] **Step 5: Replace N×Add with AddRange in RestoreQueueStateAsync**

Replace the queue restoration loops (around lines 521-530):

```csharp
// Restore history
var restoredHistory = new List<Track>();
foreach (var id in state.HistoryIds)
{
    var track = _library.GetTrackById(id);
    if (track != null) restoredHistory.Add(track);
}
History.AddRange(restoredHistory);

// Restore up-next
var restoredUpNext = new List<Track>();
foreach (var id in state.UpNextIds)
{
    var track = _library.GetTrackById(id);
    if (track != null) restoredUpNext.Add(track);
}
UpNext.AddRange(restoredUpNext);
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/Noctis/Helpers/BulkObservableCollection.cs src/Noctis/ViewModels/PlayerViewModel.cs
git commit -m "perf: use BulkObservableCollection for queue operations to eliminate UI churn"
```

---

### Task 5: Stagger startup ViewModel refreshes to avoid blocking UI initialization

**Files:**
- Modify: `src/Noctis/ViewModels/MainWindowViewModel.cs`

Currently `InitializeAsync()` calls `_songsVm.Refresh()`, `_albumsVm.Refresh()`, `_artistsVm.Refresh()`, `_homeVm.Refresh()`, `_favoritesVm.Refresh()` synchronously. Each triggers background work but the Refresh() calls themselves do synchronous library access and dispatch. Stagger them so the UI can render between each.

- [ ] **Step 1: Stagger refreshes with Task.Yield()**

Replace lines 237-244 in `InitializeAsync()`:

```csharp
// Refresh content ViewModels with loaded data — stagger so UI can render
_songsVm.Refresh();
await Task.Yield();
_albumsVm.Refresh();
await Task.Yield();
_artistsVm.Refresh();
await Task.Yield();
_homeVm.Refresh();
_favoritesVm.Refresh();
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/ViewModels/MainWindowViewModel.cs
git commit -m "perf: stagger startup VM refreshes to allow UI rendering between loads"
```

---

### Task 6: Cache dominant color extraction results

**Files:**
- Modify: `src/Noctis/Services/DominantColorExtractor.cs`

`ExtractDominantColor` creates a `RenderTargetBitmap`, renders into it, saves to a MemoryStream, decodes back, and iterates pixels — all for every track change in lyrics view. Cache the result by artwork path.

- [ ] **Step 1: Add a static color cache with path key**

Add at the top of the class (after `FallbackColor` on line 41):

```csharp
private static readonly ConcurrentDictionary<string, Color> ColorCache = new(StringComparer.OrdinalIgnoreCase);

/// <summary>
/// Returns a cached dominant color for the given artwork path, or extracts and caches it.
/// </summary>
public static Color GetOrExtractDominantColor(string artworkPath, Bitmap bitmap)
{
    if (ColorCache.TryGetValue(artworkPath, out var cached))
        return cached;

    var color = ExtractDominantColor(bitmap);
    ColorCache.TryAdd(artworkPath, color);
    return color;
}
```

Add the missing using at top:

```csharp
using System.Collections.Concurrent;
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Services/DominantColorExtractor.cs
git commit -m "perf: cache dominant color extraction results by artwork path"
```

---

### Task 7: Add dirty-tracking to LibraryAlbumsViewModel to skip redundant rebuilds

**Files:**
- Modify: `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs`

Albums VM rebuilds rows on every `Refresh()` even if the library hasn't changed. Add a dirty flag like `LibrarySongsViewModel` already has.

- [ ] **Step 1: Add dirty tracking**

Add a field after `_rebuildGeneration` (line 26):

```csharp
private bool _isDirty = true;
```

Update the `LibraryUpdated` handler in the constructor (line 66-67):

```csharp
_library.LibraryUpdated += (_, _) =>
{
    _isDirty = true;
    Dispatcher.UIThread.Post(Refresh);
};
```

Add early return to `Refresh()` (line 76-78):

```csharp
public void Refresh()
{
    if (!_isDirty && FilteredAlbumRows.Count > 0)
        return;

    _isDirty = false;
    // ... rest of existing Refresh code
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/ViewModels/LibraryAlbumsViewModel.cs
git commit -m "perf: add dirty-tracking to albums VM to skip redundant rebuilds"
```

---

### Task 8: Use buffered FileStream for persistence reads

**Files:**
- Modify: `src/Noctis/Services/PersistenceService.cs`

With a 200K-track library, `library.json` can be 50-100MB. Using unbuffered `File.OpenRead` without specifying buffer size causes excessive syscalls.

- [ ] **Step 1: Add buffer to LoadJsonAsync**

Replace `LoadJsonAsync` (lines 133-147):

```csharp
private static async Task<T?> LoadJsonAsync<T>(string path) where T : class
{
    if (!File.Exists(path)) return null;

    try
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 65536, useAsync: true);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
    }
    catch
    {
        return null;
    }
}
```

- [ ] **Step 2: Add buffer to SaveJsonAsync**

Replace the `File.Create(tempPath)` line in `SaveJsonAsync` (line 155):

```csharp
await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
    FileShare.None, bufferSize: 65536, useAsync: true))
{
    await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Services/PersistenceService.cs
git commit -m "perf: use 64KB buffered async FileStream for persistence I/O"
```

---

### Task 9: Optimize RebuildIndexesAsync for large libraries

**Files:**
- Modify: `src/Noctis/Services/LibraryService.cs`

`RebuildIndexesAsync` does `File.Exists` for every unique album artwork path, which is N filesystem calls where N can be 30K+ albums. Batch this with a HashSet lookup from a single directory listing.

- [ ] **Step 1: Replace per-file File.Exists with directory-listing batch check**

Replace the artwork existence check section in `RebuildIndexesAsync` (lines 441-451):

```csharp
// Pre-collect unique album IDs and batch-check artwork existence
// using a directory listing instead of N individual File.Exists calls
var albumIds = tracks.Select(t => t.AlbumId).Distinct().ToList();
var artworkDir = Path.GetDirectoryName(persistence.GetArtworkPath(Guid.Empty));
var existingArtFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
if (artworkDir != null && Directory.Exists(artworkDir))
{
    foreach (var file in Directory.EnumerateFiles(artworkDir, "*.jpg"))
        existingArtFiles.Add(file);
}

var artworkExists = new HashSet<Guid>();
var artworkPaths = new Dictionary<Guid, string>(albumIds.Count);
foreach (var albumId in albumIds)
{
    var artPath = persistence.GetArtworkPath(albumId);
    artworkPaths[albumId] = artPath;
    if (existingArtFiles.Contains(artPath))
        artworkExists.Add(albumId);
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Services/LibraryService.cs
git commit -m "perf: batch artwork existence check with directory listing instead of N File.Exists calls"
```

---

## Phase 2: Stability & Security

### Task 10: Harden VlcAudioPlayer against disposed/null state

**Files:**
- Modify: `src/Noctis/Services/VlcAudioPlayer.cs`

Several methods check `_disposed` but the Play method could receive a null or empty path. Also, the position timer could fire after disposal.

- [ ] **Step 1: Add null/empty guard to Play and harden position timer**

Find the `Play` method and add at the top (look for `public void Play(string filePath)` or `public async Task PlayAsync`):

```csharp
if (_disposed || string.IsNullOrWhiteSpace(filePath))
    return;

if (!File.Exists(filePath))
{
    PlaybackError?.Invoke(this, $"File not found: {filePath}");
    return;
}
```

Find `OnPositionTimerElapsed` and wrap the body:

```csharp
private void OnPositionTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
{
    if (_disposed) return;
    try
    {
        // ... existing body
    }
    catch (ObjectDisposedException)
    {
        // Timer fired after disposal — ignore
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Services/VlcAudioPlayer.cs
git commit -m "fix: harden VlcAudioPlayer against null paths and post-disposal timer fires"
```

---

### Task 11: Harden MetadataService against corrupted files

**Files:**
- Modify: `src/Noctis/Services/MetadataService.cs`

TagLib# can throw `CorruptFileException` or hang on malformed files. The existing try-catch is broad, but there's no timeout protection for files that cause TagLib to loop.

- [ ] **Step 1: Add path validation and wrap with specific exception handling**

In `ReadTrackMetadata` (line 58), add before `TagLib.File.Create`:

```csharp
if (string.IsNullOrWhiteSpace(filePath))
    return null;

// Reject paths with characters that could cause issues
if (filePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
    return null;

// Skip very large files that are unlikely to be audio (>2GB)
try
{
    var fi = new FileInfo(filePath);
    if (fi.Length > 2L * 1024 * 1024 * 1024)
        return null;
}
catch
{
    return null;
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Services/MetadataService.cs
git commit -m "fix: harden MetadataService against invalid paths and oversized files"
```

---

### Task 12: Harden PlayerViewModel queue operations against out-of-bounds access

**Files:**
- Modify: `src/Noctis/ViewModels/PlayerViewModel.cs`

Several queue operations access `UpNext[0]` or `History[0]` after checking `Count > 0`, but without synchronization, another thread could modify the collection between the check and the access.

- [ ] **Step 1: Add defensive index checks to AdvanceQueueCore and GoBackInQueue**

In `AdvanceQueueCore` (line 716-720), wrap the access:

```csharp
if (UpNext.Count > 0)
{
    Track next;
    try
    {
        next = UpNext[0];
        UpNext.RemoveAt(0);
    }
    catch (ArgumentOutOfRangeException)
    {
        return; // collection was modified concurrently
    }
    PlayTrack(next);
}
```

In `GoBackInQueue` (line 767-769), same pattern:

```csharp
private void GoBackInQueue()
{
    if (History.Count == 0) return;

    if (CurrentTrack != null)
        UpNext.Insert(0, CurrentTrack);

    Track prev;
    try
    {
        prev = History[0];
        History.RemoveAt(0);
    }
    catch (ArgumentOutOfRangeException)
    {
        return;
    }
    PlayTrack(prev);
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/ViewModels/PlayerViewModel.cs
git commit -m "fix: defend queue operations against concurrent out-of-bounds access"
```

---

### Task 13: Harden drop import against path traversal and invalid input

**Files:**
- Modify: `src/Noctis/ViewModels/MainWindowViewModel.cs`

`ImportDroppedMediaAsync` normalizes paths but doesn't validate against path traversal attacks. A malicious drag-drop payload could use `..` segments to escape the managed root.

- [ ] **Step 1: Add path traversal guard to CopyFileIntoManagedRoot**

In `CopyFileIntoManagedRoot` (line 1067), add after computing `destinationPath`:

```csharp
// Prevent path traversal: ensure destination stays within managed root
var fullDestination = Path.GetFullPath(destinationPath);
var fullRoot = Path.GetFullPath(rootPath);
if (!fullDestination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
{
    System.Diagnostics.Debug.WriteLine($"[DropImport] Path traversal blocked: {destinationPath}");
    return null;
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/ViewModels/MainWindowViewModel.cs
git commit -m "security: add path traversal guard to drop import"
```

---

### Task 14: Harden PersistenceService deserialization against corrupted JSON

**Files:**
- Modify: `src/Noctis/Services/PersistenceService.cs`

If `library.json` is partially written (e.g., crash during save), `JsonSerializer.DeserializeAsync` throws. The current code catches all exceptions and returns null, which is correct, but we should also handle the case where the temp file exists (indicating a failed save).

- [ ] **Step 1: Add temp file recovery in LoadJsonAsync**

Replace `LoadJsonAsync`:

```csharp
private static async Task<T?> LoadJsonAsync<T>(string path) where T : class
{
    // If the main file doesn't exist but the temp does, a previous save crashed
    // mid-write. The temp file may be complete, so try it as a recovery path.
    var tempPath = path + ".tmp";
    if (!File.Exists(path) && File.Exists(tempPath))
    {
        try
        {
            File.Move(tempPath, path);
        }
        catch
        {
            // If recovery fails, we'll just return null below
        }
    }

    if (!File.Exists(path)) return null;

    try
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 65536, useAsync: true);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[PersistenceService] Failed to load {Path.GetFileName(path)}: {ex.Message}");
        return null;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Services/PersistenceService.cs
git commit -m "fix: recover from crashed saves by detecting orphaned temp files"
```

---

### Task 15: Harden DominantColorExtractor against null/disposed bitmaps

**Files:**
- Modify: `src/Noctis/Services/DominantColorExtractor.cs`

`ExtractDominantColor` can receive a disposed or null bitmap when tracks change rapidly.

- [ ] **Step 1: Add null guard at start of ExtractDominantColor**

At the top of `ExtractDominantColor` (line 47-48), add:

```csharp
public static Color ExtractDominantColor(Bitmap? bitmap)
{
    if (bitmap == null || bitmap.Size.Width <= 0 || bitmap.Size.Height <= 0)
        return FallbackColor;
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Services/DominantColorExtractor.cs
git commit -m "fix: guard DominantColorExtractor against null/disposed bitmaps"
```

---

### Task 16: Add global unhandled exception logging to Program.cs

**Files:**
- Modify: `src/Noctis/Program.cs`

Currently unhandled exceptions from background tasks silently disappear. Add a top-level handler that logs to a crash file.

- [ ] **Step 1: Add crash log handler**

In `Main`, after `App.Services = provider;` (line 34), add:

```csharp
// Log unhandled exceptions to a crash file for post-mortem debugging
AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    if (args.ExceptionObject is Exception ex)
        LogCrash("AppDomain.UnhandledException", ex);
};

TaskScheduler.UnobservedTaskException += (_, args) =>
{
    LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
    args.SetObserved(); // prevent process termination
};
```

Add the helper method at the bottom of the `Program` class:

```csharp
private static void LogCrash(string source, Exception ex)
{
    try
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var crashDir = Path.Combine(appData, "Noctis");
        Directory.CreateDirectory(crashDir);
        var crashPath = Path.Combine(crashDir, "crash.log");
        var entry = $"[{DateTime.UtcNow:O}] {source}: {ex}\n---\n";
        File.AppendAllText(crashPath, entry);
    }
    catch
    {
        // Last-resort: don't let crash logging itself crash
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Program.cs
git commit -m "fix: add global unhandled exception logging to crash.log"
```

---

### Task 17: Harden LibraryService scan against file access exceptions

**Files:**
- Modify: `src/Noctis/Services/LibraryService.cs`

In `ScanAsync`, `new FileInfo(filePath)` and `File.Exists` can throw on locked/inaccessible files. The Parallel.ForEach body has no try-catch, so one bad file can terminate the entire parallel loop.

- [ ] **Step 1: Wrap the Parallel.ForEach body in try-catch**

In `ScanAsync`, wrap the entire body of the `Parallel.ForEach` lambda (lines 107-160) — add after `if (ct.IsCancellationRequested) return;`:

The existing code at line 107 starts with:
```csharp
Parallel.ForEach(files, options, filePath =>
{
    if (ct.IsCancellationRequested) return;
```

Wrap the rest of the body:
```csharp
Parallel.ForEach(files, options, filePath =>
{
    if (ct.IsCancellationRequested) return;

    try
    {
        // ... existing body unchanged
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Skip files that can't be read (locked, permissions, I/O error)
        Interlocked.Increment(ref skippedCount);
        Interlocked.Increment(ref fileCount);
        ScanProgress?.Invoke(this, fileCount);
    }
});
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Services/LibraryService.cs
git commit -m "fix: wrap scan loop body in try-catch to skip inaccessible files gracefully"
```

---

## Phase 3: Cleanup

### Task 18: Delete unused helper, control, and converter files

**Files:**
- Delete: `src/Noctis/Helpers/PlaylistMenuHelper.cs`
- Delete: `src/Noctis/Controls/TrackContextMenu.axaml`
- Delete: `src/Noctis/Controls/TrackContextMenu.axaml.cs`
- Delete: `src/Noctis/Converters/BoolToFontWeightConverter.cs`
- Delete: `src/Noctis/Converters/BoolToMuteIconConverter.cs`
- Delete: `src/Noctis/Converters/BoolToScaleConverter.cs`
- Delete: `src/Noctis/Converters/BoolToShuffleIconConverter.cs`
- Delete: `src/Noctis/Converters/NullToVisibilityConverter.cs`
- Delete: `src/Noctis/Converters/RepeatModeToIconConverter.cs`

All of these files have zero references in the codebase.

- [ ] **Step 1: Verify zero references for each file**

Run grep for each class name to confirm zero references:
```bash
grep -r "PlaylistMenuHelper" src/Noctis/ --include="*.cs" --include="*.axaml" | grep -v "PlaylistMenuHelper.cs"
grep -r "TrackContextMenu" src/Noctis/ --include="*.cs" --include="*.axaml" | grep -v "TrackContextMenu"
grep -r "BoolToFontWeightConverter" src/Noctis/ --include="*.cs" --include="*.axaml" | grep -v "BoolToFontWeightConverter.cs"
grep -r "BoolToMuteIconConverter" src/Noctis/ --include="*.cs" --include="*.axaml" | grep -v "BoolToMuteIconConverter.cs"
grep -r "BoolToScaleConverter" src/Noctis/ --include="*.cs" --include="*.axaml" | grep -v "BoolToScaleConverter.cs"
grep -r "BoolToShuffleIconConverter" src/Noctis/ --include="*.cs" --include="*.axaml" | grep -v "BoolToShuffleIconConverter.cs"
grep -r "NullToVisibilityConverter" src/Noctis/ --include="*.cs" --include="*.axaml" | grep -v "NullToVisibilityConverter.cs"
grep -r "RepeatModeToIconConverter" src/Noctis/ --include="*.cs" --include="*.axaml" | grep -v "RepeatModeToIconConverter.cs"
```

Expected: No output (zero references) for each.

- [ ] **Step 2: Delete the files**

```bash
rm src/Noctis/Helpers/PlaylistMenuHelper.cs
rm src/Noctis/Controls/TrackContextMenu.axaml
rm src/Noctis/Controls/TrackContextMenu.axaml.cs
rm src/Noctis/Converters/BoolToFontWeightConverter.cs
rm src/Noctis/Converters/BoolToMuteIconConverter.cs
rm src/Noctis/Converters/BoolToScaleConverter.cs
rm src/Noctis/Converters/BoolToShuffleIconConverter.cs
rm src/Noctis/Converters/NullToVisibilityConverter.cs
rm src/Noctis/Converters/RepeatModeToIconConverter.cs
```

- [ ] **Step 3: Build to verify nothing breaks**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds (these files are unreferenced).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "cleanup: remove 9 unused files (helpers, controls, converters)"
```

---

### Task 19: Remove unused icon definitions from Icons.axaml

**Files:**
- Modify: `src/Noctis/Assets/Icons.axaml`

Remove these unused icon path data entries: `GenresIcon`, `BulletListIcon`, `MetadataIcon`, `QueueIcon`, `SearchIcon`, `SortIcon`, `SyncIcon`, `ViewSortIcon`.

- [ ] **Step 1: Verify each icon is unused**

```bash
for icon in GenresIcon BulletListIcon MetadataIcon QueueIcon SearchIcon SortIcon SyncIcon ViewSortIcon; do
  echo "--- $icon ---"
  grep -r "$icon" src/Noctis/ --include="*.axaml" --include="*.cs" | grep -v "Icons.axaml"
done
```

Expected: No output for each (the icon is only defined in Icons.axaml, never referenced).

- [ ] **Step 2: Remove the unused entries from Icons.axaml**

Remove each `<StreamGeometry x:Key="GenresIcon">...</StreamGeometry>` (and similar for each unused icon).

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Assets/Icons.axaml
git commit -m "cleanup: remove 8 unused icon definitions from Icons.axaml"
```

---

### Task 20: Remove unused custom styles from Styles.axaml

**Files:**
- Modify: `src/Noctis/Assets/Styles.axaml`

Remove unused custom app styles: `IslandPlayButtonBackground`, `SmartPlaylistBtnBackground`, `TrackListStripeHoverBrush`.

- [ ] **Step 1: Verify each style is unused**

```bash
for style in IslandPlayButtonBackground SmartPlaylistBtnBackground TrackListStripeHoverBrush; do
  echo "--- $style ---"
  grep -r "$style" src/Noctis/ --include="*.axaml" --include="*.cs" | grep -v "Styles.axaml"
done
```

- [ ] **Step 2: Remove the entries from Styles.axaml**

Remove the `<SolidColorBrush x:Key="...">` or `<Color x:Key="...">` entries for each unused style.

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Assets/Styles.axaml
git commit -m "cleanup: remove unused custom styles from Styles.axaml"
```

---

## Phase 4: Navigation & Animations

### Task 21: Add smooth opacity+translate transitions to content area page switches

**Files:**
- Modify: `src/Noctis/Assets/Styles.axaml`
- Modify: `src/Noctis/Views/MainWindow.axaml`

Add a subtle fade-in transition when the content area switches pages. Avalonia's `PageSlide` or custom `Transitions` on the ContentControl.

- [ ] **Step 1: Add a content-transition style to Styles.axaml**

Add at the end of Styles.axaml (before closing `</Styles>`):

```xml
<!-- Page transition: subtle fade-in for content area switches -->
<Style Selector="ContentControl.page-host">
  <Setter Property="Transitions">
    <Transitions>
      <DoubleTransition Property="Opacity" Duration="0:0:0.15" Easing="CubicEaseOut" />
    </Transitions>
  </Setter>
</Style>
```

- [ ] **Step 2: Apply the class to the content ContentControl in MainWindow.axaml**

Find the `ContentControl` that displays `CurrentView` and add `Classes="page-host"`:

```xml
<ContentControl Content="{Binding CurrentView}" Classes="page-host" />
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Assets/Styles.axaml src/Noctis/Views/MainWindow.axaml
git commit -m "polish: add subtle fade-in transition for page switches"
```

---

### Task 22: Add hover/press transitions to sidebar navigation items

**Files:**
- Modify: `src/Noctis/Views/SidebarView.axaml`

Add smooth background/opacity transitions to sidebar nav items on hover and selection.

- [ ] **Step 1: Add transitions to sidebar nav item style**

Find the sidebar nav item template (ListBox or ItemsControl) and add transitions to the item container:

```xml
<Setter Property="Transitions">
  <Transitions>
    <BrushTransition Property="Background" Duration="0:0:0.15" />
    <DoubleTransition Property="Opacity" Duration="0:0:0.1" />
  </Transitions>
</Setter>
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Views/SidebarView.axaml
git commit -m "polish: add smooth hover/selection transitions to sidebar items"
```

---

### Task 23: Add hover transitions to playback bar buttons

**Files:**
- Modify: `src/Noctis/Views/PlaybackBarView.axaml`

Add smooth opacity/scale transitions to playback bar buttons (play/pause, next, previous, shuffle, repeat).

- [ ] **Step 1: Add transitions to playback bar button style**

Find the control buttons in PlaybackBarView.axaml and add:

```xml
<Setter Property="Transitions">
  <Transitions>
    <DoubleTransition Property="Opacity" Duration="0:0:0.1" />
    <TransformOperationsTransition Property="RenderTransform" Duration="0:0:0.1" />
  </Transitions>
</Setter>
```

Add hover trigger:

```xml
<Style Selector="Button.player-btn:pointerover">
  <Setter Property="RenderTransform" Value="scale(1.08)" />
</Style>
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Views/PlaybackBarView.axaml
git commit -m "polish: add hover scale/opacity transitions to playback bar buttons"
```

---

### Task 24: Add smooth width transition to sidebar expand/collapse

**Files:**
- Modify: `src/Noctis/Views/MainWindow.axaml`

The sidebar currently snaps between 60px and 220px on hover. Add a smooth width transition.

- [ ] **Step 1: Add width transition to sidebar wrapper**

Find the `SidebarWrapper` Border in MainWindow.axaml and add:

```xml
<Border.Transitions>
  <Transitions>
    <DoubleTransition Property="Width" Duration="0:0:0.2" Easing="CubicEaseOut" />
  </Transitions>
</Border.Transitions>
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Views/MainWindow.axaml
git commit -m "polish: add smooth width transition to sidebar expand/collapse"
```

---

### Task 25: Add smooth width transition to lyrics panel open/close

**Files:**
- Modify: `src/Noctis/Views/MainWindow.axaml`

The lyrics side panel snaps between 0 and 356px. Add a smooth transition.

- [ ] **Step 1: Add width transition to lyrics panel wrapper**

Find the `LyricsPanelWrapper` Border in MainWindow.axaml and add:

```xml
<Border.Transitions>
  <Transitions>
    <DoubleTransition Property="Width" Duration="0:0:0.25" Easing="CubicEaseOut" />
  </Transitions>
</Border.Transitions>
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Views/MainWindow.axaml
git commit -m "polish: add smooth width transition to lyrics panel open/close"
```

---

## Final Verification

### Task 26: Full build and smoke test

- [ ] **Step 1: Clean build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds with zero errors.

- [ ] **Step 2: Verify no regressions in test project**

Run: `dotnet build tests/Noctis.Tests/Noctis.Tests.csproj -v minimal 2>&1 || echo "BASELINE_FAILURE"`
Expected: Baseline test project may fail on missing interface methods — document if this is pre-existing.

- [ ] **Step 3: Commit plan document**

```bash
git add docs/superpowers/plans/2026-03-28-audit-cleanup-hardening.md
git commit -m "docs: add audit/cleanup/hardening implementation plan"
```
