# Animated Cover Art — Design

**Date:** 2026-05-08
**Status:** Approved (brainstorming)

## Summary

Add support for short, looping animated cover art (MP4 / WebM, video-only) to Noctis. Covers can be assigned per-track or per-album via a new tab in the metadata dialog, are auto-discovered from sidecar files next to audio, and animate only on the surfaces that show the *currently playing* track (Now Playing, Album Detail header when it is the current album, and the Playback Bar mini-art). A single master toggle in Settings disables the feature globally without instantiating any video decoders.

## Goals

- Apple Music–style animated covers for the *currently playing* track.
- Two assignment paths for users: drop a sidecar file in their library, or assign one through the metadata dialog (managed copy).
- No regression to album-grid / Home performance — those surfaces stay static.
- Off = truly off (no `LibVLC` / `MediaPlayer` instances created).

## Non-goals

- No GIF / APNG support in v1.
- No animation on album-grid tiles, Home rows, or any non-current-track surface.
- No file-watching for sidecars dropped while the app is running. Resolved at track change.
- No per-surface settings, no battery/focus auto-pause. (Single master toggle only.)
- No embedded-in-audio-container animated covers.

## User-visible behavior

### Metadata dialog

- New `<TabItem Header="Animated Cover">` placed **immediately after** the existing **Artwork** tab in `MetadataWindow.axaml`. Existing Artwork tab is unchanged.
- Tab contents (top → bottom):
  - Looping video preview pane (centered, max 450×450, same framing as the static Artwork preview). Plays muted, loops. Shows "No Animated Cover" placeholder when none.
  - Scope radio: `○ This track   ○ Whole album` (default: Whole album).
  - Buttons row: `Add Animated Cover…`, `Remove`. Add opens a file picker filtered to `*.mp4;*.webm`.
- Tab is hidden in the album-scoped editor only when track-scope wouldn't make sense; here both scopes are valid, so the tab is always visible.

### Settings

- New checkbox **"Enable animated cover art"** in `SettingsView.axaml`, placed in the Playback section (or the closest existing visual section).
- Default: **ON** for new and existing users.
- Persisted via the existing settings store the same way other booleans are.

### Playback surfaces

- `NowPlayingView`, `AlbumDetailView` header, and `PlaybackBarView` mini-art each show animated cover for the current track when:
  - Setting `EnableAnimatedCovers == true`, AND
  - `CurrentAnimatedCoverPath != null` for the current track, AND
  - The surface is currently visible.
- Otherwise the existing static artwork is shown unchanged.
- Album grids, Home rows, queue rows, and all other surfaces remain static at all times.

## Architecture

### New files

- `src/Noctis/Services/IAnimatedCoverService.cs`
- `src/Noctis/Services/AnimatedCoverService.cs`
- `src/Noctis/Controls/AnimatedCoverView.axaml` + `.axaml.cs`

### Changed files

- `src/Noctis/Services/IPersistenceService.cs` — add `GetAnimatedCoverPath(Guid albumId, Guid? trackId)` and `EnsureAnimatedCoverDir()`.
- `src/Noctis/Services/PersistenceService.cs` — implement those, mirroring the existing `GetArtworkPath` pattern. Cache directory: `<AppPaths.DataRoot>/animated_covers/`.
- `src/Noctis/ViewModels/MetadataViewModel.cs` — add animated-cover state + commands; extend `Save()` to import/copy/delete and invalidate.
- `src/Noctis/Views/MetadataWindow.axaml` — add the new tab.
- `src/Noctis/ViewModels/SettingsViewModel.cs` — add `EnableAnimatedCovers` observable property + persistence wiring.
- `src/Noctis/Views/SettingsView.axaml` — add the toggle.
- `src/Noctis/ViewModels/PlayerViewModel.cs` — add `CurrentAnimatedCoverPath`, refresh on track change.
- `src/Noctis/Views/NowPlayingView.axaml`, `AlbumDetailView.axaml`, `PlaybackBarView.axaml` — overlay `AnimatedCoverView` on the existing static `Image`/`CachedImage`.
- `src/Noctis/Noctis.csproj` — add `LibVLCSharp.Avalonia` package (matching the existing `LibVLCSharp` major version).
- `tests/Noctis.Tests/AnimatedCoverServiceTests.cs` — new test file.

### `IAnimatedCoverService`

```csharp
public interface IAnimatedCoverService
{
    /// <summary>
    /// Returns the absolute path of the best animated cover for the track, or null.
    /// Lookup priority:
    ///   1. <track>.mp4 / <track>.webm next to the audio file
    ///   2. cover.mp4 / cover.webm in the track's folder
    ///   3. Managed cache: track-scoped, then album-scoped
    /// </summary>
    string? Resolve(Track track);

    /// <summary>
    /// Copies the source file into the managed cache for the given scope.
    /// Returns the resulting cache path. Overwrites any existing entry at that scope.
    /// </summary>
    Task<string> ImportAsync(Track track, string sourcePath, AnimatedCoverScope scope);

    /// <summary>
    /// Removes the managed-cache entry for the given scope. Sidecar files are NEVER deleted.
    /// </summary>
    void Remove(Track track, AnimatedCoverScope scope);
}

public enum AnimatedCoverScope { Track, Album }
```

Pure file-system service. No UI, no LibVLC.

### `AnimatedCoverView` control

- Embeds `LibVLCSharp.Avalonia.VideoView`.
- Holds one `LibVLC` and one `MediaPlayer` instance, owned by the control.
- Bindable properties:
  - `Source` (string?) — absolute path to the cover file. Setting null tears the player down.
  - `IsActive` (bool) — when false, no `LibVLC`/`MediaPlayer` is constructed.
  - `FallbackImage` (IImage?) — shown when `Source` is null or playback fails.
- Media options on load: `:no-audio :input-repeat=65535` (loop forever, muted, video-only).
- Disposes `MediaPlayer` and `LibVLC` on detach-from-visual-tree and on `IsActive` going false.
- Fails closed: any exception → tear down player, show `FallbackImage`.

### Cache layout

- Album scope: `<DataRoot>/animated_covers/<albumId>.mp4` (extension preserved from import).
- Track scope: `<DataRoot>/animated_covers/<albumId>__<trackId>.mp4`.
- Resolution prefers track-scoped over album-scoped at the cache layer.

### Settings persistence

- `SettingsViewModel.EnableAnimatedCovers` follows the same `[ObservableProperty]` + load/save round-trip as adjacent booleans (e.g. cover-flow toggle). Default `true`.
- `PlayerViewModel` and views observe the property; flipping it at runtime causes `AnimatedCoverView.IsActive` to retract and disposes decoders.

### Save flow in `MetadataViewModel`

The `Save()` method already has a clear artwork-handling branch (added/removed/album-id-changed). Animated cover follows the same pattern:

- If `_newAnimatedCoverPath` set: `await _animatedCoverService.ImportAsync(_track, _newAnimatedCoverPath, scope)`.
- Else if `_animatedCoverRemoved`: `_animatedCoverService.Remove(_track, scope)`.
- Else if `oldAlbumId != _track.AlbumId` and an album-scoped cache file existed at the old id: copy/move it to the new id.

No file-tag write is involved — animated cover never touches audio file metadata.

## Performance

Worst case on a typical playback session: 3 simultaneous `MediaPlayer` instances (NowPlaying + AlbumDetail + PlaybackBar), all decoding the same short H.264 loop. This is acceptable for a desktop app and matches the precedent set by other animated-cover players.

When `EnableAnimatedCovers == false` no `LibVLC`/`MediaPlayer` is ever created — the views stay on the existing static `Image`.

## Testing

### Unit (`AnimatedCoverServiceTests`)

- `Resolve` returns track sidecar when present, even if cover.mp4 also present.
- `Resolve` returns `cover.mp4` when only album sidecar present.
- `Resolve` returns track-cache path when only track-cache present.
- `Resolve` returns album-cache path when only album-cache present.
- `Resolve` returns null when nothing present.
- `ImportAsync(Track)` writes to `<albumId>__<trackId>.<ext>`, overwrites existing.
- `ImportAsync(Album)` writes to `<albumId>.<ext>`, overwrites existing.
- `Remove(Track)` deletes only the track-cache file; album-cache and sidecars untouched.
- `Remove(Album)` deletes only the album-cache file; track-cache and sidecars untouched.

### Manual

- Drop `cover.mp4` next to an album → set Now Playing → confirm animation in all 3 surfaces.
- Toggle setting OFF → confirm `MediaPlayer` instances disposed (no decoder activity in Task Manager).
- Switch tracks rapidly through 20 songs → confirm no decoder leak.
- Open metadata dialog, assign a per-track cover, save → confirm preview, then confirm Now Playing picks it up after the dialog closes.
- Album-scoped metadata dialog: assign album cover → confirm all tracks in the album resolve to it.

## Risks / unknowns

- **`VideoView` over a transparent Avalonia window**: `MetadataWindow` uses `Background="Transparent"` + `TransparencyLevelHint="Transparent"`. The animated-cover preview lives inside the dialog card (which itself has an opaque background), so the `VideoView` is not parented to a transparent surface — should be fine, but worth a 5-minute spike before wiring up.
- **Linux**: `Noctis.csproj` references `VideoLAN.LibVLC.Windows` and `VideoLAN.LibVLC.Mac` but no Linux native package. Linux already depends on system VLC for audio, so this feature inherits that constraint without adding a new gap.
- **No file-watcher**: Sidecars dropped while the app is running won't be picked up until the next track change. Acceptable for v1.
