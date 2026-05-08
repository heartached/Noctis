# Animated Cover Art Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-track / per-album animated cover art (looping MP4/WebM, video-only) to Noctis, animating only on the currently-playing track surfaces (Now Playing, Album Detail, Playback Bar mini-art), with a master Settings toggle and a new tab in the metadata dialog.

**Architecture:** A pure file-system `AnimatedCoverService` owns lookup (sidecar → managed cache) and import/remove. A reusable `AnimatedCoverView` Avalonia control wraps `LibVLCSharp.Avalonia.VideoView`, owning one `LibVLC`/`MediaPlayer` per instance, fully torn down when inactive. `PlayerViewModel` exposes `CurrentAnimatedCoverPath`; surfaces overlay the control over their existing static art `Image` and bind activation to `EnableAnimatedCovers && path != null && this surface is current`.

**Tech Stack:** Avalonia 11, C#/.NET 8, CommunityToolkit.Mvvm, LibVLCSharp 3.x (already a dependency), LibVLCSharp.Avalonia (new).

**Spec:** [docs/superpowers/specs/2026-05-08-animated-cover-art-design.md](../specs/2026-05-08-animated-cover-art-design.md)

---

## File Structure

**Create:**
- `src/Noctis/Services/IAnimatedCoverService.cs` — interface + `AnimatedCoverScope` enum
- `src/Noctis/Services/AnimatedCoverService.cs` — implementation
- `src/Noctis/Controls/AnimatedCoverView.axaml` + `.axaml.cs` — Avalonia control
- `tests/Noctis.Tests/AnimatedCoverServiceTests.cs` — service unit tests

**Modify:**
- `src/Noctis/Noctis.csproj` — add `LibVLCSharp.Avalonia` package
- `src/Noctis/Services/IPersistenceService.cs` — add `GetAnimatedCoverPath`, `EnsureAnimatedCoverDir`
- `src/Noctis/Services/PersistenceService.cs` — implement them
- `tests/Noctis.Tests/TestPersistenceService.cs` — implement them
- `src/Noctis/Models/AppSettings.cs` — add `EnableAnimatedCovers` (default true)
- `src/Noctis/ViewModels/SettingsViewModel.cs` — observable property + load/save
- `src/Noctis/Views/SettingsView.axaml` — toggle UI
- `src/Noctis/ViewModels/PlayerViewModel.cs` — `CurrentAnimatedCoverPath`
- `src/Noctis/ViewModels/MetadataViewModel.cs` — animated-cover state + commands
- `src/Noctis/Views/MetadataWindow.axaml` — new "Animated Cover" tab
- `src/Noctis/Views/NowPlayingView.axaml` — overlay `AnimatedCoverView`
- `src/Noctis/Views/AlbumDetailView.axaml` — overlay `AnimatedCoverView` (header)
- `src/Noctis/Views/PlaybackBarView.axaml` — overlay `AnimatedCoverView` (mini-art)

---

### Task 1: Add LibVLCSharp.Avalonia package

**Files:**
- Modify: `src/Noctis/Noctis.csproj` (add package next to existing `LibVLCSharp` reference)

- [ ] **Step 1: Add the package reference**

In `src/Noctis/Noctis.csproj`, add this line directly after the existing `LibVLCSharp` PackageReference:

```xml
<PackageReference Include="LibVLCSharp.Avalonia" Version="3.9.6" />
```

(Match the major/minor version of the existing `LibVLCSharp` 3.9.6 entry.)

- [ ] **Step 2: Restore and build to verify the package resolves**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: build succeeds with no new warnings about missing LibVLCSharp.Avalonia.

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Noctis.csproj
git commit -m "deps: add LibVLCSharp.Avalonia for animated cover art"
```

---

### Task 2: Persistence — animated-cover paths

**Files:**
- Modify: `src/Noctis/Services/IPersistenceService.cs`
- Modify: `src/Noctis/Services/PersistenceService.cs`
- Modify: `tests/Noctis.Tests/TestPersistenceService.cs`

- [ ] **Step 1: Extend the interface**

Add inside the `IPersistenceService` interface:

```csharp
    /// <summary>
    /// Returns the cache path for an animated cover.
    /// Album scope: <DataRoot>/animated_covers/<albumId>.<ext>
    /// Track scope: <DataRoot>/animated_covers/<albumId>__<trackId>.<ext>
    /// </summary>
    string GetAnimatedCoverPath(Guid albumId, Guid? trackId, string extension);

    /// <summary>Ensures the animated_covers directory exists. Idempotent.</summary>
    void EnsureAnimatedCoverDir();
```

- [ ] **Step 2: Implement in `PersistenceService`**

Add a private property near `ArtworkDirectory`:

```csharp
    private string AnimatedCoverDirectory => Path.Combine(DataDirectory, "animated_covers");
```

In the constructor, after `Directory.CreateDirectory(ArtworkDirectory);`, add:

```csharp
        Directory.CreateDirectory(AnimatedCoverDirectory);
```

Add these methods (place them under the `// ── Artwork ──` region or in a new `// ── Animated Cover ──` region):

```csharp
    public string GetAnimatedCoverPath(Guid albumId, Guid? trackId, string extension)
    {
        var ext = string.IsNullOrWhiteSpace(extension) ? ".mp4" : extension;
        if (!ext.StartsWith('.')) ext = "." + ext;
        var fileName = trackId.HasValue
            ? $"{albumId}__{trackId.Value}{ext}"
            : $"{albumId}{ext}";
        return Path.Combine(AnimatedCoverDirectory, fileName);
    }

    public void EnsureAnimatedCoverDir()
    {
        Directory.CreateDirectory(AnimatedCoverDirectory);
    }
```

- [ ] **Step 3: Implement the same on the test stub**

In `tests/Noctis.Tests/TestPersistenceService.cs`, add:

```csharp
    public string GetAnimatedCoverPath(Guid albumId, Guid? trackId, string extension)
    {
        var ext = string.IsNullOrWhiteSpace(extension) ? ".mp4" : (extension.StartsWith('.') ? extension : "." + extension);
        var name = trackId.HasValue ? $"{albumId}__{trackId.Value}{ext}" : $"{albumId}{ext}";
        return Path.Combine(_root, "animated_covers", name);
    }

    public void EnsureAnimatedCoverDir()
    {
        Directory.CreateDirectory(Path.Combine(_root, "animated_covers"));
    }
```

- [ ] **Step 4: Build and run tests to confirm baseline**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: success.

Run: `dotnet test tests/Noctis.Tests/Noctis.Tests.csproj -v minimal`
Expected: existing tests pass (no new tests added yet).

- [ ] **Step 5: Commit**

```bash
git add src/Noctis/Services/IPersistenceService.cs src/Noctis/Services/PersistenceService.cs tests/Noctis.Tests/TestPersistenceService.cs
git commit -m "feat(persistence): animated cover cache paths"
```

---

### Task 3: `AnimatedCoverService` (TDD)

**Files:**
- Create: `src/Noctis/Services/IAnimatedCoverService.cs`
- Create: `src/Noctis/Services/AnimatedCoverService.cs`
- Create: `tests/Noctis.Tests/AnimatedCoverServiceTests.cs`

- [ ] **Step 1: Write the interface skeleton**

Create `src/Noctis/Services/IAnimatedCoverService.cs`:

```csharp
using Noctis.Models;

namespace Noctis.Services;

public enum AnimatedCoverScope
{
    Track,
    Album
}

public interface IAnimatedCoverService
{
    /// <summary>
    /// Returns the absolute path of the best animated cover for the track, or null.
    /// Lookup priority:
    ///   1. Track sidecar: <track>.mp4 / <track>.webm next to the audio file
    ///   2. Album sidecar: cover.mp4 / cover.webm in the track's folder
    ///   3. Track-scoped managed cache
    ///   4. Album-scoped managed cache
    /// </summary>
    string? Resolve(Track track);

    /// <summary>
    /// Copies sourcePath into the managed cache for the given scope, overwriting any existing entry.
    /// Returns the cache path.
    /// </summary>
    Task<string> ImportAsync(Track track, string sourcePath, AnimatedCoverScope scope);

    /// <summary>
    /// Removes the managed-cache entry for the given scope. Sidecar files are NEVER deleted.
    /// </summary>
    void Remove(Track track, AnimatedCoverScope scope);
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Noctis.Tests/AnimatedCoverServiceTests.cs`:

```csharp
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class AnimatedCoverServiceTests : IDisposable
{
    private readonly TestPersistenceService _persistence = new();
    private readonly string _libraryRoot;
    private readonly AnimatedCoverService _svc;

    public AnimatedCoverServiceTests()
    {
        _libraryRoot = Path.Combine(Path.GetTempPath(), "NoctisAnim", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libraryRoot);
        _svc = new AnimatedCoverService(_persistence);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        try { if (Directory.Exists(_libraryRoot)) Directory.Delete(_libraryRoot, true); } catch { }
    }

    private Track NewTrack(out string folder)
    {
        folder = Path.Combine(_libraryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var audioPath = Path.Combine(folder, "song.flac");
        File.WriteAllBytes(audioPath, new byte[] { 0 });
        return new Track
        {
            FilePath = audioPath,
            AlbumId = Guid.NewGuid(),
            Id = Guid.NewGuid(),
            Album = "X",
            AlbumArtist = "Y"
        };
    }

    private static void Touch(string path) => File.WriteAllBytes(path, new byte[] { 1 });

    [Fact]
    public void Resolve_PrefersTrackSidecar_OverAlbumSidecar()
    {
        var t = NewTrack(out var folder);
        Touch(Path.Combine(folder, "song.mp4"));
        Touch(Path.Combine(folder, "cover.mp4"));

        var result = _svc.Resolve(t);

        Assert.Equal(Path.Combine(folder, "song.mp4"), result);
    }

    [Fact]
    public void Resolve_FallsBackToAlbumSidecar()
    {
        var t = NewTrack(out var folder);
        Touch(Path.Combine(folder, "cover.webm"));

        var result = _svc.Resolve(t);

        Assert.Equal(Path.Combine(folder, "cover.webm"), result);
    }

    [Fact]
    public void Resolve_FallsBackToTrackCache()
    {
        var t = NewTrack(out _);
        var cachePath = _persistence.GetAnimatedCoverPath(t.AlbumId, t.Id, ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        Touch(cachePath);

        var result = _svc.Resolve(t);

        Assert.Equal(cachePath, result);
    }

    [Fact]
    public void Resolve_FallsBackToAlbumCache()
    {
        var t = NewTrack(out _);
        var cachePath = _persistence.GetAnimatedCoverPath(t.AlbumId, null, ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        Touch(cachePath);

        var result = _svc.Resolve(t);

        Assert.Equal(cachePath, result);
    }

    [Fact]
    public void Resolve_PrefersTrackCacheOverAlbumCache()
    {
        var t = NewTrack(out _);
        var trackCache = _persistence.GetAnimatedCoverPath(t.AlbumId, t.Id, ".mp4");
        var albumCache = _persistence.GetAnimatedCoverPath(t.AlbumId, null, ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(trackCache)!);
        Touch(trackCache);
        Touch(albumCache);

        var result = _svc.Resolve(t);

        Assert.Equal(trackCache, result);
    }

    [Fact]
    public void Resolve_ReturnsNullWhenNothingPresent()
    {
        var t = NewTrack(out _);
        Assert.Null(_svc.Resolve(t));
    }

    [Fact]
    public async Task ImportAsync_Track_WritesTrackScopedCacheFile()
    {
        var t = NewTrack(out var folder);
        var src = Path.Combine(folder, "src.mp4");
        File.WriteAllBytes(src, new byte[] { 9, 9, 9 });

        var dst = await _svc.ImportAsync(t, src, AnimatedCoverScope.Track);

        Assert.Equal(_persistence.GetAnimatedCoverPath(t.AlbumId, t.Id, ".mp4"), dst);
        Assert.True(File.Exists(dst));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(dst));
    }

    [Fact]
    public async Task ImportAsync_Album_WritesAlbumScopedCacheFile()
    {
        var t = NewTrack(out var folder);
        var src = Path.Combine(folder, "src.webm");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3 });

        var dst = await _svc.ImportAsync(t, src, AnimatedCoverScope.Album);

        Assert.Equal(_persistence.GetAnimatedCoverPath(t.AlbumId, null, ".webm"), dst);
        Assert.True(File.Exists(dst));
    }

    [Fact]
    public async Task ImportAsync_Overwrites_ExistingEntry()
    {
        var t = NewTrack(out var folder);
        var src1 = Path.Combine(folder, "a.mp4"); File.WriteAllBytes(src1, new byte[] { 1 });
        var src2 = Path.Combine(folder, "b.mp4"); File.WriteAllBytes(src2, new byte[] { 2 });

        await _svc.ImportAsync(t, src1, AnimatedCoverScope.Album);
        var dst = await _svc.ImportAsync(t, src2, AnimatedCoverScope.Album);

        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(dst));
    }

    [Fact]
    public async Task Remove_Track_DeletesOnlyTrackEntry()
    {
        var t = NewTrack(out var folder);
        var src = Path.Combine(folder, "src.mp4"); File.WriteAllBytes(src, new byte[] { 1 });
        var trackDst = await _svc.ImportAsync(t, src, AnimatedCoverScope.Track);
        var albumDst = await _svc.ImportAsync(t, src, AnimatedCoverScope.Album);

        _svc.Remove(t, AnimatedCoverScope.Track);

        Assert.False(File.Exists(trackDst));
        Assert.True(File.Exists(albumDst));
    }

    [Fact]
    public async Task Remove_Album_DeletesOnlyAlbumEntry()
    {
        var t = NewTrack(out var folder);
        var src = Path.Combine(folder, "src.mp4"); File.WriteAllBytes(src, new byte[] { 1 });
        var trackDst = await _svc.ImportAsync(t, src, AnimatedCoverScope.Track);
        var albumDst = await _svc.ImportAsync(t, src, AnimatedCoverScope.Album);

        _svc.Remove(t, AnimatedCoverScope.Album);

        Assert.True(File.Exists(trackDst));
        Assert.False(File.Exists(albumDst));
    }

    [Fact]
    public void Remove_DoesNotDeleteSidecars()
    {
        var t = NewTrack(out var folder);
        var sidecar = Path.Combine(folder, "song.mp4");
        Touch(sidecar);

        _svc.Remove(t, AnimatedCoverScope.Track);
        _svc.Remove(t, AnimatedCoverScope.Album);

        Assert.True(File.Exists(sidecar));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Noctis.Tests/Noctis.Tests.csproj --filter FullyQualifiedName~AnimatedCoverServiceTests -v minimal`
Expected: compile error — `AnimatedCoverService` not found.

- [ ] **Step 4: Implement `AnimatedCoverService`**

Create `src/Noctis/Services/AnimatedCoverService.cs`:

```csharp
using Noctis.Models;

namespace Noctis.Services;

public class AnimatedCoverService : IAnimatedCoverService
{
    private static readonly string[] SupportedExtensions = { ".mp4", ".webm" };

    private readonly IPersistenceService _persistence;

    public AnimatedCoverService(IPersistenceService persistence)
    {
        _persistence = persistence;
    }

    public string? Resolve(Track track)
    {
        if (string.IsNullOrWhiteSpace(track.FilePath))
            return null;

        // 1. Track sidecar: <track>.mp4 / <track>.webm
        foreach (var ext in SupportedExtensions)
        {
            var p = Path.ChangeExtension(track.FilePath, ext);
            if (File.Exists(p)) return p;
        }

        // 2. Album sidecar: cover.mp4 / cover.webm in track's folder
        var folder = Path.GetDirectoryName(track.FilePath);
        if (!string.IsNullOrEmpty(folder))
        {
            foreach (var ext in SupportedExtensions)
            {
                var p = Path.Combine(folder, "cover" + ext);
                if (File.Exists(p)) return p;
            }
        }

        // 3. Track-scoped cache
        foreach (var ext in SupportedExtensions)
        {
            var p = _persistence.GetAnimatedCoverPath(track.AlbumId, track.Id, ext);
            if (File.Exists(p)) return p;
        }

        // 4. Album-scoped cache
        foreach (var ext in SupportedExtensions)
        {
            var p = _persistence.GetAnimatedCoverPath(track.AlbumId, null, ext);
            if (File.Exists(p)) return p;
        }

        return null;
    }

    public async Task<string> ImportAsync(Track track, string sourcePath, AnimatedCoverScope scope)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source not found", sourcePath);

        _persistence.EnsureAnimatedCoverDir();

        var ext = NormalizeExtension(Path.GetExtension(sourcePath));
        var trackId = scope == AnimatedCoverScope.Track ? (Guid?)track.Id : null;

        // Wipe sibling extensions for this scope so old .webm doesn't shadow a new .mp4
        foreach (var other in SupportedExtensions)
        {
            var p = _persistence.GetAnimatedCoverPath(track.AlbumId, trackId, other);
            if (File.Exists(p)) try { File.Delete(p); } catch { }
        }

        var dst = _persistence.GetAnimatedCoverPath(track.AlbumId, trackId, ext);
        await using var src = File.OpenRead(sourcePath);
        await using var dstStream = File.Create(dst);
        await src.CopyToAsync(dstStream);
        return dst;
    }

    public void Remove(Track track, AnimatedCoverScope scope)
    {
        var trackId = scope == AnimatedCoverScope.Track ? (Guid?)track.Id : null;
        foreach (var ext in SupportedExtensions)
        {
            var p = _persistence.GetAnimatedCoverPath(track.AlbumId, trackId, ext);
            if (File.Exists(p))
            {
                try { File.Delete(p); } catch { }
            }
        }
    }

    private static string NormalizeExtension(string ext)
    {
        ext = (ext ?? string.Empty).ToLowerInvariant();
        return SupportedExtensions.Contains(ext) ? ext : ".mp4";
    }
}
```

If `Track` does not already have an `Id` property, use whatever stable per-track identifier exists (e.g. compute a deterministic hash from `FilePath` via the existing helper). Verify before proceeding:

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: success. If `track.Id` is unresolved, replace `track.Id` everywhere in this task with the existing track identifier (search `Track.cs` for the public `Guid` or `string` id property and substitute).

- [ ] **Step 5: Run tests, expect pass**

Run: `dotnet test tests/Noctis.Tests/Noctis.Tests.csproj --filter FullyQualifiedName~AnimatedCoverServiceTests -v minimal`
Expected: 11 passed.

- [ ] **Step 6: Commit**

```bash
git add src/Noctis/Services/IAnimatedCoverService.cs src/Noctis/Services/AnimatedCoverService.cs tests/Noctis.Tests/AnimatedCoverServiceTests.cs
git commit -m "feat: AnimatedCoverService with sidecar + cache lookup"
```

---

### Task 4: `AnimatedCoverView` Avalonia control

**Files:**
- Create: `src/Noctis/Controls/AnimatedCoverView.axaml`
- Create: `src/Noctis/Controls/AnimatedCoverView.axaml.cs`

- [ ] **Step 1: Write the AXAML**

Create `src/Noctis/Controls/AnimatedCoverView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vlc="using:LibVLCSharp.Avalonia"
             x:Class="Noctis.Controls.AnimatedCoverView"
             ClipToBounds="True">
    <Panel>
        <Image x:Name="FallbackImageHost"
               Source="{Binding FallbackImage, RelativeSource={RelativeSource AncestorType=UserControl}}"
               Stretch="UniformToFill"
               IsVisible="True" />
        <vlc:VideoView x:Name="VideoHost"
                       IsVisible="False" />
    </Panel>
</UserControl>
```

- [ ] **Step 2: Write the code-behind**

Create `src/Noctis/Controls/AnimatedCoverView.axaml.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using LibVLCSharp.Shared;
using LibVLCSharp.Avalonia;

namespace Noctis.Controls;

public partial class AnimatedCoverView : UserControl
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<AnimatedCoverView, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<AnimatedCoverView, bool>(nameof(IsActive), defaultValue: false);

    public static readonly StyledProperty<IImage?> FallbackImageProperty =
        AvaloniaProperty.Register<AnimatedCoverView, IImage?>(nameof(FallbackImage));

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public IImage? FallbackImage
    {
        get => GetValue(FallbackImageProperty);
        set => SetValue(FallbackImageProperty, value);
    }

    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private VideoView? _videoHost;

    public AnimatedCoverView()
    {
        InitializeComponent();
        _videoHost = this.FindControl<VideoView>("VideoHost");
        DetachedFromVisualTree += (_, _) => Teardown();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == IsActiveProperty)
            Refresh();
    }

    private void Refresh()
    {
        var shouldPlay = IsActive && !string.IsNullOrEmpty(Source) && File.Exists(Source);
        if (!shouldPlay)
        {
            Teardown();
            ShowFallback(true);
            return;
        }

        try
        {
            EnsurePlayer();
            using var media = new Media(_libVlc!, Source!, FromType.FromPath,
                ":no-audio", ":input-repeat=65535", ":video-on-top=0");
            _player!.Play(media);
            ShowFallback(false);
        }
        catch
        {
            Teardown();
            ShowFallback(true);
        }
    }

    private void EnsurePlayer()
    {
        if (_libVlc == null)
        {
            Core.Initialize();
            _libVlc = new LibVLC("--quiet", "--no-video-title-show");
        }
        if (_player == null)
        {
            _player = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true, Mute = true };
            if (_videoHost != null)
                _videoHost.MediaPlayer = _player;
        }
    }

    private void Teardown()
    {
        try { _player?.Stop(); } catch { }
        try
        {
            if (_videoHost != null) _videoHost.MediaPlayer = null;
            _player?.Dispose();
        }
        catch { }
        _player = null;

        try { _libVlc?.Dispose(); } catch { }
        _libVlc = null;
    }

    private void ShowFallback(bool visible)
    {
        var fb = this.FindControl<Image>("FallbackImageHost");
        if (fb != null) fb.IsVisible = visible;
        if (_videoHost != null) _videoHost.IsVisible = !visible;
    }
}
```

- [ ] **Step 3: Build to confirm the control compiles**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: success.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Controls/AnimatedCoverView.axaml src/Noctis/Controls/AnimatedCoverView.axaml.cs
git commit -m "feat: AnimatedCoverView control wrapping LibVLC VideoView"
```

---

### Task 5: Settings — `EnableAnimatedCovers` model + VM

**Files:**
- Modify: `src/Noctis/Models/AppSettings.cs`
- Modify: `src/Noctis/ViewModels/SettingsViewModel.cs`

- [ ] **Step 1: Add the persisted field**

In `src/Noctis/Models/AppSettings.cs`, alongside other booleans (e.g. near `CoverFlowMarqueeEnabled` around line 92), add:

```csharp
    public bool EnableAnimatedCovers { get; set; } = true;
```

- [ ] **Step 2: Add the observable property**

In `src/Noctis/ViewModels/SettingsViewModel.cs`, near the other display-related `[ObservableProperty] private bool` declarations (around line 74, next to `_albumDetailColorTintEnabled`), add:

```csharp
    [ObservableProperty] private bool _enableAnimatedCovers = true;
```

- [ ] **Step 3: Wire up load/save**

In the `Load` block (around line 351 where `CoverFlowMarqueeEnabled = _settings.CoverFlowMarqueeEnabled;` lives), add:

```csharp
            EnableAnimatedCovers = _settings.EnableAnimatedCovers;
```

In the `Save` block (around line 486), add:

```csharp
        _settings.EnableAnimatedCovers = EnableAnimatedCovers;
```

- [ ] **Step 4: Build to confirm**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: success.

- [ ] **Step 5: Commit**

```bash
git add src/Noctis/Models/AppSettings.cs src/Noctis/ViewModels/SettingsViewModel.cs
git commit -m "feat(settings): EnableAnimatedCovers (default on)"
```

---

### Task 6: Settings UI toggle

**Files:**
- Modify: `src/Noctis/Views/SettingsView.axaml`

- [ ] **Step 1: Locate the Playback section**

Open `src/Noctis/Views/SettingsView.axaml`. Find the section heading **"Playback"** (or the closest existing section that holds toggles like `CrossfadeEnabled` / `AutoMixEnabled`). Note the exact CheckBox styling pattern used so the new toggle visually matches.

- [ ] **Step 2: Insert the toggle**

Within that section, immediately after the last existing CheckBox in that section, insert (matching the surrounding `Classes`, indentation, and any wrapping `StackPanel`):

```xml
<CheckBox Content="Enable animated cover art"
          IsChecked="{Binding EnableAnimatedCovers}"
          ToolTip.Tip="Plays looping MP4/WebM cover videos for the currently playing track. When off, no video decoders are created." />
```

If the existing checkboxes use a specific `Classes` value (e.g. `settings-toggle`), apply the same class.

- [ ] **Step 3: Build, run app, eyeball the toggle**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: success.

Manually launch the app, open Settings, confirm the toggle appears in the Playback section, defaults ON, persists across restarts.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Views/SettingsView.axaml
git commit -m "feat(settings): UI toggle for animated cover art"
```

---

### Task 7: `PlayerViewModel.CurrentAnimatedCoverPath`

**Files:**
- Modify: `src/Noctis/ViewModels/PlayerViewModel.cs`

- [ ] **Step 1: Inject `IAnimatedCoverService`**

Open `PlayerViewModel.cs`. In the constructor, add an `IAnimatedCoverService animatedCoverService` parameter, store it in a private readonly field `_animatedCovers`.

```csharp
    private readonly IAnimatedCoverService _animatedCovers;
    // ...constructor body:
    _animatedCovers = animatedCoverService;
```

Update the DI / composition root that constructs `PlayerViewModel` (search for `new PlayerViewModel(`) to pass `new AnimatedCoverService(persistence)`.

- [ ] **Step 2: Add the observable property**

Near other current-track-related properties in `PlayerViewModel`:

```csharp
    [ObservableProperty] private string? _currentAnimatedCoverPath;
```

- [ ] **Step 3: Refresh on track change**

Find where the current track is updated (search for the existing artwork reload, around line 920 referenced earlier with `_persistence.GetArtworkPath(track.AlbumId)`). Immediately after the static artwork path is set, add:

```csharp
        CurrentAnimatedCoverPath = track != null ? _animatedCovers.Resolve(track) : null;
```

- [ ] **Step 4: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: success.

- [ ] **Step 5: Commit**

```bash
git add src/Noctis/ViewModels/PlayerViewModel.cs
# also commit the composition-root file you touched in Step 1
git commit -m "feat(player): expose CurrentAnimatedCoverPath"
```

---

### Task 8: MetadataViewModel — animated cover state + commands

**Files:**
- Modify: `src/Noctis/ViewModels/MetadataViewModel.cs`

- [ ] **Step 1: Inject the service and add state**

In the constructor of `MetadataViewModel`, add an `IAnimatedCoverService animatedCovers` parameter, store as `_animatedCovers`. Update the call site that constructs `MetadataViewModel` (search `new MetadataViewModel(`) to pass it.

Add fields next to the artwork fields (around line 54-58):

```csharp
    // ── Animated cover tab ──
    [ObservableProperty] private string? _animatedCoverPath;
    [ObservableProperty] private bool _hasAnimatedCover;
    [ObservableProperty] private bool _animatedCoverScopeIsAlbum = true;  // default to Whole album
    private string? _newAnimatedCoverSource;
    private bool _animatedCoverRemoved;
```

- [ ] **Step 2: Load current state**

Add a `LoadAnimatedCover()` method, called from the constructor after `LoadArtwork()`:

```csharp
    private void LoadAnimatedCover()
    {
        AnimatedCoverPath = _animatedCovers.Resolve(_track);
        HasAnimatedCover = !string.IsNullOrEmpty(AnimatedCoverPath);
    }
```

In the constructor, add `LoadAnimatedCover();` immediately after `LoadArtwork();`.

- [ ] **Step 3: Add Add/Remove commands**

Add these `[RelayCommand]` methods alongside the artwork ones:

```csharp
    [RelayCommand]
    private async Task AddAnimatedCover(Avalonia.Visual visual)
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(visual);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Animated Cover",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video") { Patterns = new[] { "*.mp4", "*.webm" } }
            }
        });
        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        _newAnimatedCoverSource = path;
        _animatedCoverRemoved = false;
        AnimatedCoverPath = path;          // preview pane plays the user-selected file
        HasAnimatedCover = true;
    }

    [RelayCommand]
    private void RemoveAnimatedCover()
    {
        _newAnimatedCoverSource = null;
        _animatedCoverRemoved = true;
        AnimatedCoverPath = null;
        HasAnimatedCover = false;
    }
```

- [ ] **Step 4: Wire into `Save()`**

In `Save()`, after the existing artwork-handling block (around line 632, after `ArtworkCache.Invalidate(...)` block), add:

```csharp
        // Animated cover handling
        var scope = AnimatedCoverScopeIsAlbum ? AnimatedCoverScope.Album : AnimatedCoverScope.Track;
        if (_newAnimatedCoverSource != null)
        {
            try { await _animatedCovers.ImportAsync(_track, _newAnimatedCoverSource, scope); }
            catch { /* Non-fatal — preview still showed source */ }
        }
        else if (_animatedCoverRemoved)
        {
            _animatedCovers.Remove(_track, scope);
        }
        else if (oldAlbumId != _track.AlbumId)
        {
            // AlbumId changed — move album-scoped cache file to new id
            foreach (var ext in new[] { ".mp4", ".webm" })
            {
                var oldP = _persistence.GetAnimatedCoverPath(oldAlbumId, null, ext);
                if (File.Exists(oldP))
                {
                    try { File.Move(oldP, _persistence.GetAnimatedCoverPath(_track.AlbumId, null, ext), overwrite: true); }
                    catch { }
                }
            }
        }
```

- [ ] **Step 5: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: success.

- [ ] **Step 6: Commit**

```bash
git add src/Noctis/ViewModels/MetadataViewModel.cs
# plus the call-site file touched in Step 1
git commit -m "feat(metadata): animated cover state + add/remove commands"
```

---

### Task 9: MetadataWindow — Animated Cover tab

**Files:**
- Modify: `src/Noctis/Views/MetadataWindow.axaml`

- [ ] **Step 1: Add the namespace**

In the `<Window>` opening tag of `MetadataWindow.axaml`, add:

```xml
xmlns:controls="using:Noctis.Controls"
```

- [ ] **Step 2: Insert the new tab**

Immediately AFTER the existing `<TabItem Header="Artwork" ...>` block (which ends at line 656 with `</TabItem>`), insert:

```xml
            <!-- ═══════════ ANIMATED COVER TAB ═══════════ -->
            <TabItem Header="Animated Cover" FontSize="14" FontWeight="SemiBold">
                <DockPanel Margin="24,16">
                    <StackPanel DockPanel.Dock="Bottom"
                                Orientation="Horizontal"
                                HorizontalAlignment="Center"
                                Spacing="12"
                                Margin="0,16,0,0">
                        <Button Content="Add Animated Cover"
                                Command="{Binding AddAnimatedCoverCommand}"
                                CommandParameter="{Binding $parent[Window]}"
                                Padding="10,5"
                                CornerRadius="999"
                                Cursor="Hand"
                                FontSize="11" />
                        <Button Content="Remove"
                                Command="{Binding RemoveAnimatedCoverCommand}"
                                IsVisible="{Binding HasAnimatedCover}"
                                Padding="10,5"
                                CornerRadius="999"
                                Cursor="Hand"
                                FontSize="11" />
                    </StackPanel>

                    <StackPanel DockPanel.Dock="Bottom"
                                Orientation="Horizontal"
                                HorizontalAlignment="Center"
                                Spacing="20"
                                Margin="0,12,0,0">
                        <RadioButton Content="This track"
                                     GroupName="AnimCoverScope"
                                     IsChecked="{Binding !AnimatedCoverScopeIsAlbum}" />
                        <RadioButton Content="Whole album"
                                     GroupName="AnimCoverScope"
                                     IsChecked="{Binding AnimatedCoverScopeIsAlbum}" />
                    </StackPanel>

                    <Border HorizontalAlignment="Center"
                            VerticalAlignment="Center"
                            MaxWidth="450" MaxHeight="450"
                            CornerRadius="8"
                            ClipToBounds="True"
                            Background="{DynamicResource SystemControlBackgroundBaseLowBrush}">
                        <Panel>
                            <TextBlock Text="No Animated Cover"
                                       FontSize="18"
                                       Opacity="0.3"
                                       HorizontalAlignment="Center"
                                       VerticalAlignment="Center"
                                       IsVisible="{Binding !HasAnimatedCover}" />
                            <controls:AnimatedCoverView
                                Source="{Binding AnimatedCoverPath}"
                                IsActive="{Binding HasAnimatedCover}"
                                IsVisible="{Binding HasAnimatedCover}" />
                        </Panel>
                    </Border>
                </DockPanel>
            </TabItem>
```

- [ ] **Step 3: Build and visually verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: success.

Launch the app, right-click a track → Get Info → confirm the new "Animated Cover" tab appears between "Artwork" and "Plain Lyrics", shows the preview pane and buttons, and the scope radio works.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Views/MetadataWindow.axaml
git commit -m "feat(metadata): Animated Cover tab"
```

---

### Task 10: NowPlayingView — overlay AnimatedCoverView

**Files:**
- Modify: `src/Noctis/Views/NowPlayingView.axaml`

- [ ] **Step 1: Add namespace**

Ensure the root tag has:

```xml
xmlns:controls="using:Noctis.Controls"
```

- [ ] **Step 2: Wrap the existing artwork Image**

Find the main artwork `Image` element in `NowPlayingView.axaml` (the one bound to the current-track artwork). Wrap it in a `Panel` so the `AnimatedCoverView` overlays the static image, like:

```xml
<Panel>
    <Image Source="{Binding Player.CurrentArtwork}"
           Stretch="UniformToFill" />  <!-- existing element, unchanged -->
    <controls:AnimatedCoverView
        Source="{Binding Player.CurrentAnimatedCoverPath}"
        IsActive="{Binding $parent[Window].DataContext.Settings.EnableAnimatedCovers, FallbackValue=False}" />
</Panel>
```

If `Settings` is not directly reachable from this DataContext, bind through whatever path matches the existing `MainWindowViewModel` structure (search the file for an existing `Settings.` binding pattern; if none, expose `EnableAnimatedCovers` on `MainWindowViewModel` as a forwarder). Match that pattern.

- [ ] **Step 3: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: success.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Views/NowPlayingView.axaml
git commit -m "feat(now-playing): overlay animated cover"
```

---

### Task 11: AlbumDetailView header — overlay AnimatedCoverView

**Files:**
- Modify: `src/Noctis/Views/AlbumDetailView.axaml`

- [ ] **Step 1: Add namespace** (`xmlns:controls="using:Noctis.Controls"`).

- [ ] **Step 2: Wrap the header artwork**

The Album Detail header shows the album's static artwork. Animated cover should only animate when this album *is the currently-playing album*. Wrap that Image:

```xml
<Panel>
    <!-- existing static album-art Image stays here, unchanged -->
    <controls:AnimatedCoverView
        Source="{Binding Player.CurrentAnimatedCoverPath}"
        IsActive="{Binding IsCurrentAlbumPlaying}" />
</Panel>
```

- [ ] **Step 3: Add `IsCurrentAlbumPlaying` to `AlbumDetailViewModel`**

In `src/Noctis/ViewModels/AlbumDetailViewModel.cs`, add a computed observable property that returns:

```csharp
    public bool IsCurrentAlbumPlaying =>
        _settings.EnableAnimatedCovers
        && _player.CurrentTrack != null
        && _player.CurrentTrack.AlbumId == _albumId
        && !string.IsNullOrEmpty(_player.CurrentAnimatedCoverPath);
```

Subscribe to `_player.PropertyChanged` (filter to `CurrentTrack` / `CurrentAnimatedCoverPath`) and `_settings.PropertyChanged` (filter to `EnableAnimatedCovers`) to call `OnPropertyChanged(nameof(IsCurrentAlbumPlaying))`. Inject `_settings` (`SettingsViewModel`) if not already available — search the constructor for existing fields to follow the local DI pattern.

- [ ] **Step 4: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: success.

- [ ] **Step 5: Commit**

```bash
git add src/Noctis/Views/AlbumDetailView.axaml src/Noctis/ViewModels/AlbumDetailViewModel.cs
git commit -m "feat(album-detail): overlay animated cover when album is current"
```

---

### Task 12: PlaybackBarView mini-art — overlay AnimatedCoverView

**Files:**
- Modify: `src/Noctis/Views/PlaybackBarView.axaml`

- [ ] **Step 1: Add namespace** (`xmlns:controls="using:Noctis.Controls"`).

- [ ] **Step 2: Wrap the mini-art**

Find the playback-bar mini-art `Image`. Wrap with a `Panel`:

```xml
<Panel>
    <!-- existing mini-art Image stays here, unchanged -->
    <controls:AnimatedCoverView
        Source="{Binding CurrentAnimatedCoverPath}"
        IsActive="{Binding $parent[Window].DataContext.Settings.EnableAnimatedCovers, FallbackValue=False}" />
</Panel>
```

Match the binding path to whatever DataContext the existing mini-art uses (search the file for the current artwork binding and use the same root). If `Settings.EnableAnimatedCovers` is not reachable, follow the pattern you established in Task 10.

- [ ] **Step 3: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: success.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Views/PlaybackBarView.axaml
git commit -m "feat(playback-bar): overlay animated cover on mini-art"
```

---

### Task 13: Manual verification

**Files:** none

- [ ] **Step 1: Build clean**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: success, zero warnings about animated-cover code.

- [ ] **Step 2: Run all tests**

Run: `dotnet test tests/Noctis.Tests/Noctis.Tests.csproj -v minimal`
Expected: all `AnimatedCoverServiceTests` pass; no regressions in other tests.

- [ ] **Step 3: Manual UX checks**

Launch the app and verify, in order:

1. Drop a `cover.mp4` (any short H.264 looping video) into an album folder. Restart app or rescan. Play a track from that album. Confirm:
   - Now Playing view animates the cover.
   - Album Detail header animates while that album is playing.
   - Playback Bar mini-art animates.
   - Album grid / Home rows do NOT animate.
2. Settings → toggle "Enable animated cover art" OFF. Confirm:
   - All three surfaces immediately show the static artwork.
   - Open Task Manager / `ps`; confirm no LibVLC video activity.
3. Toggle ON again — animation resumes.
4. Right-click a different track → Get Info → Animated Cover tab. Click Add, pick an MP4. Preview plays. Save. Confirm Now Playing picks it up after switching to that track.
5. Open metadata for a track in album-scope mode. Pick "Whole album", add a cover. Save. Confirm every track in that album resolves to the same animation.
6. Switch tracks rapidly through 20 tracks. Confirm no decoder leak (memory stable, no orphaned LibVLC log spam).

- [ ] **Step 4: Final commit (if any docs/cleanup)**

If anything was tweaked during manual testing, commit it. Otherwise the feature is complete on the current branch.

```bash
git status
# if clean: feature is shipped on this branch
```

---

## Self-review notes

- Spec coverage: every spec section maps to a task (sidecar/cache lookup → T3; metadata tab → T8/T9; settings toggle → T5/T6; surfaces → T7/T10/T11/T12; control → T4; persistence paths → T2; tests → T3 + T13).
- No placeholders: all code blocks are concrete; the only "search and follow existing pattern" instructions are for Avalonia binding paths that genuinely require reading neighbouring code, and they specify exactly what to look for.
- Type consistency: `AnimatedCoverScope` enum, `IAnimatedCoverService.Resolve/ImportAsync/Remove`, `GetAnimatedCoverPath(Guid, Guid?, string)`, and `EnableAnimatedCovers` property name are used consistently across all tasks.
