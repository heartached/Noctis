# Remove Lyrics Button — Design

**Date:** 2026-05-21
**Goal:** Add a "Remove lyrics" affordance in two places — the Metadata window's Synced Lyrics tab, and the lyrics-page 3-dot menu — reusing the existing lyrics-removal logic where one exists.

## Background

- The **Metadata window's Synced Lyrics tab** (`MetadataWindow.axaml`, bound to `MetadataViewModel`) is an editor: a `SyncedLyrics` text box plus an `Enable` checkbox (`HasCustomSyncedLyrics`). Edits are written to the track / sidecar `.lrc` only when the user clicks **Save**.
- The **lyrics-page 3-dot menu** is the `MenuFlyout` in `PlaybackBarView.axaml` (bound to `PlayerViewModel`). It already contains a "Search Lyrics" item and lyrics-page-only entries gated by `IsLyricsPageActive`.
- `LyricsViewModel` already exposes `RemoveLyricsCommand` and `CanRemoveLyrics`. `LyricsPanelView` already uses them for its own "Remove Lyrics" menu item. `RemoveLyricsCommand` clears cached/online lyrics (deletes the `{trackId}.lrc` cache file) and resets lyrics state; it does not touch sidecar `.lrc` files or embedded tag lyrics.

## Scope

Two independent changes.

### Part A — "Remove lyrics" button in the Metadata Synced Lyrics tab

`MetadataViewModel.cs`:
- Add a `[RelayCommand]` method `RemoveSyncedLyrics()` that sets `SyncedLyrics = string.Empty`, `HasCustomSyncedLyrics = false`, and `SyncedLyricsSearchStatus = string.Empty`.
- This is a pure editor-state change. The removal is persisted to the track and sidecar only when the user clicks **Save** — consistent with the rest of the dialog.

`MetadataWindow.axaml`:
- Add a `Button` with `Content="Remove lyrics"` inside the bottom-right `StackPanel` (`Grid.Column="2"` of the Synced Lyrics tab footer grid), positioned **before** the existing "Search lyrics" button.
- Bind `Command` to `RemoveSyncedLyricsCommand`.
- Bind `IsEnabled` to `HasCustomSyncedLyrics` so the button is active only when there are lyrics to remove.
- Style: a subtle/secondary pill (not accent blue) so it does not compete visually with the accent "Search lyrics" and "Save" buttons. Match the dialog's non-accent button treatment (rounded `CornerRadius`, `Cursor="Hand"`, `FontSize="12"`, `FontWeight="SemiBold"`, restrained background).

### Part B — "Remove Lyrics" in the lyrics-page 3-dot menu

`PlaybackBarView.axaml`:
- Add a `MenuItem` with `Header="Remove Lyrics"` immediately after the existing "Search Lyrics" `MenuItem`, with the `TrashIcon` as its icon.
- Bind `Command` to `LyricsViewModel.RemoveLyricsCommand` via `$parent[Window].((vm:MainWindowViewModel)DataContext).Lyrics.RemoveLyricsCommand` — the menu's own DataContext is `PlayerViewModel`, so it reaches `LyricsViewModel` through the window's `MainWindowViewModel.Lyrics` accessor.
- Bind `IsVisible` to a `MultiBinding` combining `IsLyricsPageActive` AND `$parent[Window].((vm:MainWindowViewModel)DataContext).Lyrics.CanRemoveLyrics`, using `{x:Static BoolConverters.And}` as the converter. The item appears only on the lyrics page and only when removable (cached/online) lyrics exist — matching `LyricsPanelView`'s existing behavior.

## Files touched

- `src/Noctis/ViewModels/MetadataViewModel.cs` — new `RemoveSyncedLyricsCommand`.
- `src/Noctis/Views/MetadataWindow.axaml` — new "Remove lyrics" button.
- `src/Noctis/Views/PlaybackBarView.axaml` — new "Remove Lyrics" menu item.

## Non-goals

- No changes to `LyricsViewModel.RemoveLyricsCommand` itself or its removal scope.
- No new commands on `PlayerViewModel`.
- No deletion of sidecar `.lrc` files or embedded tag lyrics.
- No changes to the `LyricsPanelView` "Remove Lyrics" item (already exists).

## Verification

- Build: `dotnet build src/Noctis/Noctis.csproj -v minimal` — must succeed.
- The test project does not compile at baseline (`.claude/rules/testing.md`); verification is build + manual run.
- Manual checks:
  - Metadata → Synced Lyrics tab: "Remove lyrics" button sits left of "Search lyrics"; clicking it clears the text box and unchecks Enable; it is disabled when there are no synced lyrics; the cleared state persists after Save.
  - Lyrics page → 3-dot menu: "Remove Lyrics" appears after "Search Lyrics" only when removable lyrics exist; clicking it removes them; the item does not appear in the bottom playback-bar menu.
