# Multi-Select Metadata Editor + Selection UX — Design

Date: 2026-05-29
Status: Approved (pending spec review)

## Summary

Three related improvements to bulk editing / selection in Noctis:

1. **Apple-Music-style multi-select metadata editor** — when editing more than one
   track (Songs multi-select, multi-album tile selection, "Edit All Tracks…"), open
   the same polished tabbed editor used for single albums, generalized to an
   arbitrary selection: blank music-note artwork, an "N artists / M songs selected"
   header, `Mixed` placeholders for differing fields, and edits that fan out to every
   selected track.
2. **Discoverable Select All / Deselect All** in Songs, Albums, and Playlists.
3. **Reset selection when navigating away** from a view.

## Current state (evidence)

- `MetadataViewModel` / `MetadataWindow` already support an album-scoped mode
  (`_albumScoped` + `_albumTracks`) that shows `Mixed` placeholders, hides
  per-track fields (Title, Performer) and the Advanced/Lyrics/Synced/File tabs, and
  fans out only user-edited fields to every album track. Tabs in album mode:
  Details, Artwork, Animated Artwork, Options.
- Multi-track editing currently routes to a **separate** editor:
  `MetadataHelper.OpenBatchMetadataWindow(tracks)` → `BatchMetadataWindow` /
  `BatchMetadataViewModel`. That editor is a checkbox-per-field "apply" UI marked
  `BETA · EXPERIMENTAL`, and it owns a useful **rename-by-pattern** feature
  (pattern + 8-row preview + conflict detection) not present elsewhere.
- Callers of `OpenBatchMetadataWindow`: `LibrarySongsViewModel.OpenMetadata`
  (when `CtrlSelectedTracks.Count > 1`), and the "Edit All Tracks…" commands in
  `AlbumDetailViewModel`, `LibraryAlbumsViewModel`, `HomeViewModel`,
  `FavoritesViewModel`.
- Selection is tracked in each view's **code-behind** (`HashSet<Track>` /
  `HashSet<Button>`) via `MultiSelectHelper` (Ctrl+Click, Ctrl+A). It is pushed to
  the VM (`CtrlSelectedTracks` / `CtrlSelectedAlbums`) when a context menu opens.
  Ctrl+A is already wired in Songs, Albums, and Playlists. There is **no visible**
  Select All control anywhere.
- Selection persists across navigation: the code-behind set is **not** cleared in
  `OnDetachedFromVisualTree`, and `MultiSelectHelper.SyncContainerVisual` re-applies
  the visual class when the view is revisited.

## Goals

- One consistent metadata editor for single-track, single-album, and multi-select.
- Multi-select editor visually/behaviorally matches Apple Music's multi-item Get Info
  (blank art, "N selected" header, `Mixed`, Details/Artwork/Options).
- Preserve the rename-by-pattern feature.
- Make Select All discoverable.
- Selection does not leak across page navigation.

## Non-goals

- No "Sorting" tab (sort-as fields) in this iteration — deferred.
- No change to single-track or single-album editor behavior/appearance.
- No visual restyle beyond the multi-select editor surface.
- No change to existing Ctrl+Click / Ctrl+A mechanics.

## Design

### Feature 1 — Generalized multi-select editor

Add a third editing mode to the existing `MetadataViewModel`. Keep `_albumScoped`
meaning "editing a set of tracks" (true for both album and multi-select, so the
existing Mixed / fan-out / hide-per-track-fields logic is reused unchanged) and add a
new `_multiSelect` flag to distinguish an arbitrary selection from a single album.

`MetadataHelper`:
- `OpenMetadataWindow` gains a `multiSelect` path (e.g. an
  `OpenMultiTrackMetadataWindow(IReadOnlyList<Track> tracks)` helper) that constructs
  `MetadataViewModel` with the explicit selection as the target set, `albumScoped =
  true`, `multiSelect = true`.
- `OpenBatchMetadataWindow(tracks)` is repointed to this path (single-track selection
  still falls through to the normal single editor, as it does today). All existing
  callers keep working without change.
- `BatchMetadataWindow` + `BatchMetadataViewModel` are retired once nothing
  references them.

Behavior differences driven by `_multiSelect`:

| Aspect | Album-scoped (existing) | Multi-select (new) |
|---|---|---|
| Header title | Album name | "N artists selected" / "M songs selected" |
| Header artwork | Album cover | Blank music-note placeholder |
| Animated Artwork tab | Shown | Hidden |
| Details/Artwork/Options tabs | Shown | Shown |
| Title / Performer fields | Hidden | Hidden |
| Advanced/Lyrics/Synced/File tabs | Hidden | Hidden |
| Mixed + change-tracked fan-out | Yes | Yes (same logic) |
| Artwork scope on save | Per album (AlbumId) | Every selected track + each affected album's cache |
| Rename-by-pattern section | No | Yes (in Options tab) |

New/changed VM members (illustrative):
- `bool ShowAnimatedArtworkTab => !_multiSelect;` — bind the Animated Artwork
  `TabItem.IsVisible` to it (single-track and album keep it; multi hides it).
- Header: when `_multiSelect`, `HeaderTitle` = "N artists selected" and a second line
  "M songs selected"; `HasArtwork` forced false so the existing music-note placeholder
  shows. `N` = count of distinct `Artist` values across the selection; `M` = track
  count.
- Artwork save: when `_multiSelect`, write the new artwork to every selected track's
  file and invalidate/refresh each affected album's cached art; removal clears across
  all selected tracks. (Album mode path unchanged.)

Rename section (kept):
- Port the rename-by-pattern logic (pattern, `TitleFormatter.Expand`, 8-row preview,
  conflict detection, file move on save) from `BatchMetadataViewModel` into the
  multi-select path. Rendered as a collapsible section at the bottom of the **Options**
  tab, visible only when `_multiSelect`. Rename runs as part of Save, after tag writes,
  matching today's behavior.

### Feature 2 — Visible Select All / Deselect All

Add "Select All" and "Deselect All" items to the right-click context menus in Songs,
Albums, and Playlists, wired to each view's existing `MultiSelectHelper` select-all /
clear logic (the same code Ctrl+A uses). No top-bar / cross-VM plumbing. Ctrl+A is
retained.

- Track lists (Songs, Playlists): reuse `HandleTrackSelectAllByData` /
  `ClearTrackSelectionsByData` plus `SyncContainerVisual` over realized rows.
- Album grid (Albums): reuse `HandleAlbumSelectAll` / `ClearAlbumSelections`.
- After selecting, push to the VM's `CtrlSelected…` list so a subsequent metadata /
  convert / queue action sees the full selection (mirrors current context-menu flow).

### Feature 3 — Reset selection on navigation

In each multi-select view's `OnDetachedFromVisualTree`, clear the code-behind
selection set, strip the `ctrl-selected` visual class from realized containers, and
clear the VM's `CtrlSelected…` list. Views: `LibrarySongsView`, `LibraryAlbumsView`,
`PlaylistView`, and any of `FavoritesView` / `LibraryFoldersView` / `MoreByArtistView`
/ `HomeView` that maintain a selection set (confirmed during implementation).

Result: select-all in Songs → switch to Albums → return to Songs starts with an empty
selection.

## Affected files

- `src/Noctis/ViewModels/MetadataViewModel.cs` — multi-select mode, header,
  ShowAnimatedArtworkTab, multi-select artwork save, rename section logic.
- `src/Noctis/Views/MetadataWindow.axaml` — header variant (count + music-note),
  Animated Artwork tab `IsVisible`, rename section in Options tab.
- `src/Noctis/ViewModels/MetadataHelper.cs` — route `OpenBatchMetadataWindow` to the
  multi-select editor.
- `src/Noctis/Views/BatchMetadataWindow.axaml(.cs)`,
  `src/Noctis/ViewModels/BatchMetadataViewModel.cs` — retire after migration (rename
  logic ported first).
- Context menus / code-behind for Select All + selection reset:
  `LibrarySongsView.axaml(.cs)`, `LibraryAlbumsView.axaml(.cs)`,
  `PlaylistView.axaml(.cs)`, `Helpers/TrackContextMenuBuilder.cs`, and the album/
  playlist context-menu definitions; plus `OnDetachedFromVisualTree` in the views
  that track selection.

## Edge cases

- **Single-track selection** → normal single-track editor (unchanged).
- **Multi-select spanning albums** → artwork/album/album-artist edits change each
  track's `AlbumId`; recompute per track on save (existing logic).
- **`Mixed` untouched** → not written, so each track keeps its own value (titles,
  track numbers preserved).
- **Rename conflicts** → existing conflict detection/skip preserved.
- **Empty selection** when a context menu opens on an unselected row → falls back to
  the single right-clicked item (current behavior).
- **Deselect All / nav reset** must also clear the VM `CtrlSelected…` lists, not just
  visuals, to avoid stale bulk actions.

## Verification

- `dotnet build src/Noctis/Noctis.csproj -v minimal` (compiled bindings validate the
  new editor bindings).
- Manual: select multiple Songs → metadata shows blank art + "N artists / M songs",
  `Mixed` fields, edits apply to all, rename section works; multi-album selection edits
  artwork across albums; single album editor still shows album art + Animated Artwork;
  right-click Select All / Deselect All in all three views; select-all then switch
  pages resets selection.
- Test project has a documented pre-existing baseline compile failure unrelated to
  this work.
