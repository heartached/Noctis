# Sidebar Playlist Limit — Design Spec

## Summary

Limit the sidebar playlist list to the 5 most recently modified playlists. Add a subtle "See all" text button below to navigate to the full Playlists page. Hide the button when the user has 5 or fewer playlists.

## Motivation

The sidebar playlist list grows unbounded as users create playlists, consuming vertical space and pushing content out of view. Capping at 5 keeps the sidebar compact while still surfacing the most relevant playlists.

## Design

### ViewModel Changes (`SidebarViewModel.cs`)

1. **Add `ModifiedAt` to `PlaylistNavItem`**: A plain `DateTime` property (not observable — used only as a sort key, not bound to UI). Populated from `Playlist.ModifiedAt` in `BuildPlaylistNavItem()`.

2. **Add `SidebarPlaylistItems`**: A new `ObservableCollection<PlaylistNavItem>` containing at most 5 items, derived from `PlaylistItems` sorted by `ModifiedAt` descending (most recent first). The full `PlaylistItems` collection is not re-sorted — it retains persistence load order.

3. **Add `ShowSeeAll`**: An `[ObservableProperty]` bool, set to `true` when `PlaylistItems.Count > 5`.

4. **Add `RefreshSidebarPlaylists()`**: A **public** method that:
   - Sorts `PlaylistItems` by `ModifiedAt` descending
   - Takes the first 5
   - Replaces `SidebarPlaylistItems` contents
   - Updates `ShowSeeAll`
   - Called after every playlist mutation in `SidebarViewModel`: `LoadPlaylistsAsync`, `CreatePlaylist`, `CreatePlaylistWithTrackAsync`, `CreatePlaylistWithTracksAsync`, `CreateSmartPlaylistAsync`, `DeletePlaylistAsync`, `RenamePlaylist`, `AddTracksToPlaylist`, `EditPlaylistAsync`
   - Public so that external ViewModels (`PlaylistViewModel`, `LibraryPlaylistsViewModel`) that mutate playlists directly can call it after their mutations.

5. **Add `SeeAllPlaylists` relay command**: Sets `SelectedNavItem` to the "playlists" `NavItem` (to keep sidebar highlight in sync) and fires `NavigationRequested` with key `"playlists"`.

### View Changes (`SidebarView.axaml`)

1. **Bind playlist ListBox** to `SidebarPlaylistItems` instead of `PlaylistItems`.

2. **Add "See all" button** below the playlist ListBox:
   - Styled as a subtle text link: FontSize 12, Opacity 0.4, transparent background, no border
   - Bound to `SeeAllPlaylistsCommand`
   - `IsVisible="{Binding ShowSeeAll}"`
   - Left-aligned with playlist content margin

### Model Changes (`PlaylistNavItem.cs`)

- Add `DateTime ModifiedAt` plain property.

## Files Changed

- `src/Noctis/ViewModels/SidebarViewModel.cs`
- `src/Noctis/Views/SidebarView.axaml`
- `src/Noctis/Models/PlaylistNavItem.cs`

## Edge Cases

- **0 playlists**: No items shown, "See all" hidden — same as current behavior.
- **1–5 playlists**: All shown, "See all" hidden.
- **6+ playlists**: Top 5 by `ModifiedAt` shown, "See all" visible.
- **Playlist modified while on sidebar**: `RefreshSidebarPlaylists()` re-sorts so the modified playlist bubbles to top.
- **Active playlist bumped from top 5**: If the currently selected sidebar playlist is removed from `SidebarPlaylistItems` during a refresh, the ListBox's `SelectedItem` becomes null. This is acceptable — the user remains on the playlist view they were already viewing, and the sidebar simply deselects. They can re-access it via "See all".
