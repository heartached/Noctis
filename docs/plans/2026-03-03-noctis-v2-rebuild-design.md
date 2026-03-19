# Velour v2 Rebuild Design

## Stack
- **Desktop shell:** Tauri 2
- **Frontend:** React + TypeScript + Vite
- **State:** Zustand
- **Backend:** Rust
- **Audio:** rodio (CPAL + Symphonia)
- **Metadata:** lofty-rs
- **Database:** rusqlite (SQLite)
- **HTTP:** reqwest
- **Serialization:** serde + serde_json

## Architecture
- React handles presentation only
- Rust handles: playback, metadata, scanning, persistence, network, integrations
- Communication: Tauri `invoke()` (request/response) + `emit/listen` (push events)

## Layout
- Sidebar (220px, left): nav items + playlists
- TopBar: back button + floating playback island (center) + search (right)
- Content area: routed views
- Queue popup: overlay panel (330px, right)

## Views
Home, Songs, Albums, Artists, Genres, Playlists, Favorites, AlbumDetail, GenreDetail, PlaylistDetail, NowPlaying, Lyrics, Settings, Statistics, Queue

## Features (full parity)
- Playback: play/pause/seek/volume/mute/shuffle/repeat/crossfade/EQ/normalization
- Library: folder scan, metadata extraction, artwork cache, SQLite index, search, sort, favorites, play counts
- Queue: up next + history, drag reorder, add next/add to queue
- Lyrics: synced/unsynced, .lrc sidecar, LRCLIB, cache, auto-scroll, adaptive colors
- Playlists: manual + smart (rule builder), M3U/PLS import/export
- Metadata editor: tag read/write, artwork
- Settings: theme (dark/light/system), EQ (10-band), crossfade, normalization
- Integrations: Discord RPC, Last.fm (auth, scrobble, images, descriptions)
- Remote sources: Navidrome, SMB, WebDAV
- UI: context menus, back nav stack, scroll restore, debug panel, taskbar integration

## Theme
- Accent: #E74856
- Dark: sidebar #141414, main #252525
- Light: sidebar #F0F0F0, main #FFFFFF
- Font: Inter SemiBold
- Playback island: #F0181818

## Phases
1. Scaffold (Tauri 2 + React + Vite + Zustand + Rust)
2. Shell & Layout
3. Theme & Styling
4. Library Scanning
5. Playback
6. Queue & Shuffle/Repeat
7. Search & Sort
8. Lyrics
9. Playlists
10. Metadata Editor
11. Settings & EQ
12. Integrations
13. Detail Views & Polish
