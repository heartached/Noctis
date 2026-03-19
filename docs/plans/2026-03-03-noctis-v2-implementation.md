# Velour v2 (Tauri + React + Rust) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Rebuild the Velour music player as a Tauri 2 desktop app with React/TypeScript frontend and Rust backend, achieving full feature parity with the existing Avalonia app.

**Architecture:** React handles all UI rendering and user interaction via Zustand stores. Rust handles audio playback (rodio/CPAL/Symphonia), metadata extraction (lofty-rs), library scanning, SQLite indexing, persistence (serde_json), and external integrations (LRCLIB, Last.fm, Discord, Navidrome, SMB, WebDAV). Communication crosses the boundary via Tauri invoke (request/response) and events (push from Rust to React).

**Tech Stack:** Tauri 2, React 19, TypeScript, Vite, Zustand, Rust, rodio, lofty-rs, rusqlite, reqwest, serde, tauri-plugin-dialog, tauri-plugin-shell

---

## Task 1: Scaffold Tauri 2 + React + Vite project

**Files:**
- Create: `velour-v2/` (entire scaffold via CLI)
- Modify: `velour-v2/src-tauri/Cargo.toml` (add dependencies)
- Modify: `velour-v2/package.json` (add dependencies)
- Modify: `velour-v2/src-tauri/tauri.conf.json` (window config)

**Step 1: Create Tauri project with React + TypeScript template**

```bash
cd "c:/Users/okfer/Downloads/Velour - Backup"
npm create tauri-app@latest velour-v2 -- --template react-ts --manager npm
```

Accept defaults. This creates a working Tauri 2 + React + Vite project.

**Step 2: Install frontend dependencies**

```bash
cd "c:/Users/okfer/Downloads/Velour - Backup/velour-v2"
npm install zustand react-router-dom
npm install -D @types/react-router-dom
```

**Step 3: Add Rust dependencies to Cargo.toml**

In `velour-v2/src-tauri/Cargo.toml`, set the `[dependencies]` section:

```toml
[dependencies]
tauri = { version = "2", features = [] }
tauri-plugin-opener = "2"
serde = { version = "1", features = ["derive"] }
serde_json = "1"
tokio = { version = "1", features = ["full"] }
```

Do NOT add audio/metadata/DB crates yet. We add them when their tasks arrive.

**Step 4: Configure window in tauri.conf.json**

Set the window config to match the Velour app:
- title: "Velour"
- width: 1280, height: 800
- minWidth: 900, minHeight: 600
- decorations: true
- center: true

**Step 5: Verify it builds and opens**

```bash
cd "c:/Users/okfer/Downloads/Velour - Backup/velour-v2"
npm run tauri dev
```

Expected: A window opens with the default Tauri + React template content.

**Step 6: Commit**

```bash
git init
git add -A
git commit -m "scaffold: Tauri 2 + React + TypeScript + Vite + Zustand"
```

---

## Task 2: Set up project structure (frontend)

**Files:**
- Create: `velour-v2/src/stores/playerStore.ts`
- Create: `velour-v2/src/stores/libraryStore.ts`
- Create: `velour-v2/src/stores/settingsStore.ts`
- Create: `velour-v2/src/stores/sidebarStore.ts`
- Create: `velour-v2/src/stores/uiStore.ts`
- Create: `velour-v2/src/types/index.ts`
- Create: `velour-v2/src/lib/commands.ts`
- Create: `velour-v2/src/components/Layout/Shell.tsx`
- Create: `velour-v2/src/views/Home.tsx`
- Modify: `velour-v2/src/App.tsx`
- Modify: `velour-v2/src/main.tsx`
- Delete: Default template files (`src/App.css`, template content)

**Step 1: Create TypeScript type definitions**

`velour-v2/src/types/index.ts`:
```typescript
export interface Track {
  id: string;
  title: string;
  artist: string;
  album: string;
  album_id: string;
  genre: string;
  duration_ms: number;
  track_number: number;
  disc_number: number;
  year: number;
  file_path: string;
  artwork_path: string | null;
  is_favorite: boolean;
  play_count: number;
  is_explicit: boolean;
  bitrate: number;
  sample_rate: number;
  channels: number;
  codec: string;
  file_size: number;
  date_added: string;
  last_played: string | null;
  lyrics: string | null;
  synced_lyrics: string | null;
  source_type: 'local' | 'navidrome' | 'smb' | 'webdav';
}

export interface Album {
  id: string;
  name: string;
  artist: string;
  year: number;
  genre: string;
  artwork_path: string | null;
  track_count: number;
  is_explicit: boolean;
  tracks: Track[];
}

export interface Artist {
  name: string;
  album_count: number;
  track_count: number;
  image_url: string | null;
}

export interface Genre {
  name: string;
  track_count: number;
  color: string;
}

export interface Playlist {
  id: string;
  name: string;
  description: string;
  track_ids: string[];
  is_smart: boolean;
  rules: SmartPlaylistRule[];
  sort_field: string | null;
  sort_ascending: boolean;
  limit: number | null;
  created_at: string;
  updated_at: string;
}

export interface SmartPlaylistRule {
  field: string;
  operator: string;
  value: string;
}

export interface LyricLine {
  timestamp_ms: number;
  text: string;
}

export interface QueueState {
  current_track: Track | null;
  up_next: Track[];
  history: Track[];
}

export type PlaybackState = 'stopped' | 'playing' | 'paused';
export type RepeatMode = 'off' | 'all' | 'one';
export type ThemeMode = 'dark' | 'light' | 'system';
export type SortColumn = 'title' | 'time' | 'artist' | 'album' | 'genre' | 'plays';

export interface AppSettings {
  theme: ThemeMode;
  music_folders: string[];
  scan_on_startup: boolean;
  crossfade_enabled: boolean;
  crossfade_duration: number;
  sound_check_enabled: boolean;
  equalizer_enabled: boolean;
  eq_preset_index: number;
  eq_bands: number[];
  discord_rpc_enabled: boolean;
  lastfm_enabled: boolean;
  lastfm_session_key: string | null;
  lastfm_username: string | null;
  volume: number;
  is_muted: boolean;
  shuffle: boolean;
  repeat_mode: RepeatMode;
}

export type NavKey =
  | 'home' | 'songs' | 'albums' | 'artists'
  | 'genres' | 'playlists' | 'favorites'
  | 'settings' | 'statistics';
```

**Step 2: Create Zustand stores (skeleton)**

`velour-v2/src/stores/playerStore.ts`:
```typescript
import { create } from 'zustand';
import type { Track, PlaybackState, RepeatMode } from '../types';

interface PlayerState {
  currentTrack: Track | null;
  state: PlaybackState;
  position: number;
  duration: number;
  volume: number;
  isMuted: boolean;
  isShuffleEnabled: boolean;
  repeatMode: RepeatMode;
  upNext: Track[];
  history: Track[];
  isQueuePopupOpen: boolean;

  setCurrentTrack: (track: Track | null) => void;
  setState: (state: PlaybackState) => void;
  setPosition: (pos: number) => void;
  setDuration: (dur: number) => void;
  setVolume: (vol: number) => void;
  setMuted: (muted: boolean) => void;
  toggleShuffle: () => void;
  cycleRepeat: () => void;
  setUpNext: (tracks: Track[]) => void;
  setHistory: (tracks: Track[]) => void;
  toggleQueuePopup: () => void;
}

export const usePlayerStore = create<PlayerState>((set) => ({
  currentTrack: null,
  state: 'stopped',
  position: 0,
  duration: 0,
  volume: 80,
  isMuted: false,
  isShuffleEnabled: false,
  repeatMode: 'off',
  upNext: [],
  history: [],
  isQueuePopupOpen: false,

  setCurrentTrack: (track) => set({ currentTrack: track }),
  setState: (state) => set({ state }),
  setPosition: (position) => set({ position }),
  setDuration: (duration) => set({ duration }),
  setVolume: (volume) => set({ volume }),
  setMuted: (isMuted) => set({ isMuted }),
  toggleShuffle: () => set((s) => ({ isShuffleEnabled: !s.isShuffleEnabled })),
  cycleRepeat: () =>
    set((s) => ({
      repeatMode: s.repeatMode === 'off' ? 'all' : s.repeatMode === 'all' ? 'one' : 'off',
    })),
  setUpNext: (tracks) => set({ upNext: tracks }),
  setHistory: (tracks) => set({ history: tracks }),
  toggleQueuePopup: () => set((s) => ({ isQueuePopupOpen: !s.isQueuePopupOpen })),
}));
```

`velour-v2/src/stores/libraryStore.ts`:
```typescript
import { create } from 'zustand';
import type { Track, Album, Artist, Genre, SortColumn } from '../types';

interface LibraryState {
  tracks: Track[];
  albums: Album[];
  artists: Artist[];
  genres: Genre[];
  isScanning: boolean;
  scanProgress: number;
  searchText: string;
  sortColumn: SortColumn;
  sortAscending: boolean;

  setTracks: (tracks: Track[]) => void;
  setAlbums: (albums: Album[]) => void;
  setArtists: (artists: Artist[]) => void;
  setGenres: (genres: Genre[]) => void;
  setScanning: (scanning: boolean) => void;
  setScanProgress: (progress: number) => void;
  setSearchText: (text: string) => void;
  setSortColumn: (col: SortColumn) => void;
  toggleSortDirection: () => void;
}

export const useLibraryStore = create<LibraryState>((set) => ({
  tracks: [],
  albums: [],
  artists: [],
  genres: [],
  isScanning: false,
  scanProgress: 0,
  searchText: '',
  sortColumn: 'title',
  sortAscending: true,

  setTracks: (tracks) => set({ tracks }),
  setAlbums: (albums) => set({ albums }),
  setArtists: (artists) => set({ artists }),
  setGenres: (genres) => set({ genres }),
  setScanning: (isScanning) => set({ isScanning }),
  setScanProgress: (scanProgress) => set({ scanProgress }),
  setSearchText: (searchText) => set({ searchText }),
  setSortColumn: (sortColumn) => set({ sortColumn }),
  toggleSortDirection: () => set((s) => ({ sortAscending: !s.sortAscending })),
}));
```

`velour-v2/src/stores/settingsStore.ts`:
```typescript
import { create } from 'zustand';
import type { ThemeMode } from '../types';

interface SettingsState {
  theme: ThemeMode;
  musicFolders: string[];
  scanOnStartup: boolean;
  crossfadeEnabled: boolean;
  crossfadeDuration: number;
  eqEnabled: boolean;
  eqPresetIndex: number;
  eqBands: number[];
  discordRpcEnabled: boolean;
  lastfmEnabled: boolean;
  lastfmUsername: string | null;
  isLastfmConnected: boolean;

  setTheme: (theme: ThemeMode) => void;
  setMusicFolders: (folders: string[]) => void;
  setScanOnStartup: (v: boolean) => void;
  setCrossfadeEnabled: (v: boolean) => void;
  setCrossfadeDuration: (v: number) => void;
  setEqEnabled: (v: boolean) => void;
  setEqPresetIndex: (v: number) => void;
  setEqBand: (index: number, value: number) => void;
}

export const useSettingsStore = create<SettingsState>((set) => ({
  theme: 'dark',
  musicFolders: [],
  scanOnStartup: false,
  crossfadeEnabled: false,
  crossfadeDuration: 3,
  eqEnabled: false,
  eqPresetIndex: 1,
  eqBands: [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
  discordRpcEnabled: false,
  lastfmEnabled: false,
  lastfmUsername: null,
  isLastfmConnected: false,

  setTheme: (theme) => set({ theme }),
  setMusicFolders: (musicFolders) => set({ musicFolders }),
  setScanOnStartup: (scanOnStartup) => set({ scanOnStartup }),
  setCrossfadeEnabled: (crossfadeEnabled) => set({ crossfadeEnabled }),
  setCrossfadeDuration: (crossfadeDuration) => set({ crossfadeDuration }),
  setEqEnabled: (eqEnabled) => set({ eqEnabled }),
  setEqPresetIndex: (eqPresetIndex) => set({ eqPresetIndex }),
  setEqBand: (index, value) =>
    set((s) => {
      const bands = [...s.eqBands];
      bands[index] = value;
      return { eqBands: bands };
    }),
}));
```

`velour-v2/src/stores/sidebarStore.ts`:
```typescript
import { create } from 'zustand';
import type { NavKey, Playlist } from '../types';

interface SidebarState {
  activeNav: NavKey;
  playlists: Playlist[];
  navStack: NavKey[];

  setActiveNav: (key: NavKey) => void;
  pushNav: (key: NavKey) => void;
  popNav: () => NavKey | null;
  setPlaylists: (playlists: Playlist[]) => void;
}

export const useSidebarStore = create<SidebarState>((set, get) => ({
  activeNav: 'home',
  playlists: [],
  navStack: [],

  setActiveNav: (activeNav) => set({ activeNav }),
  pushNav: (key) =>
    set((s) => ({ navStack: [...s.navStack, s.activeNav], activeNav: key })),
  popNav: () => {
    const stack = get().navStack;
    if (stack.length === 0) return null;
    const prev = stack[stack.length - 1];
    set({ navStack: stack.slice(0, -1), activeNav: prev });
    return prev;
  },
  setPlaylists: (playlists) => set({ playlists }),
}));
```

`velour-v2/src/stores/uiStore.ts`:
```typescript
import { create } from 'zustand';

interface UiState {
  isSearchVisible: boolean;
  isDebugPanelVisible: boolean;
  isLyricsViewActive: boolean;
  detailView: { type: string; id: string } | null;

  toggleSearch: () => void;
  toggleDebugPanel: () => void;
  setLyricsViewActive: (v: boolean) => void;
  setDetailView: (view: { type: string; id: string } | null) => void;
}

export const useUiStore = create<UiState>((set) => ({
  isSearchVisible: false,
  isDebugPanelVisible: false,
  isLyricsViewActive: false,
  detailView: null,

  toggleSearch: () => set((s) => ({ isSearchVisible: !s.isSearchVisible })),
  toggleDebugPanel: () => set((s) => ({ isDebugPanelVisible: !s.isDebugPanelVisible })),
  setLyricsViewActive: (isLyricsViewActive) => set({ isLyricsViewActive }),
  setDetailView: (detailView) => set({ detailView }),
}));
```

**Step 3: Create Tauri command bridge**

`velour-v2/src/lib/commands.ts`:
```typescript
import { invoke } from '@tauri-apps/api/core';

// Placeholder - commands added as backend features are implemented
export async function greet(name: string): Promise<string> {
  return invoke('greet', { name });
}
```

**Step 4: Create Shell layout component**

`velour-v2/src/components/Layout/Shell.tsx`:
```typescript
import { ReactNode } from 'react';
import './Shell.css';

interface ShellProps {
  sidebar: ReactNode;
  topbar: ReactNode;
  children: ReactNode;
}

export function Shell({ sidebar, topbar, children }: ShellProps) {
  return (
    <div className="shell">
      <aside className="shell-sidebar">{sidebar}</aside>
      <div className="shell-main">
        <header className="shell-topbar">{topbar}</header>
        <main className="shell-content">{children}</main>
      </div>
    </div>
  );
}
```

`velour-v2/src/components/Layout/Shell.css`:
```css
.shell {
  display: flex;
  height: 100vh;
  overflow: hidden;
  background: var(--bg-main);
  color: var(--text-high);
}

.shell-sidebar {
  width: 220px;
  min-width: 220px;
  background: var(--bg-sidebar);
  display: flex;
  flex-direction: column;
  overflow-y: auto;
}

.shell-main {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  position: relative;
}

.shell-topbar {
  flex-shrink: 0;
  position: relative;
  z-index: 10;
}

.shell-content {
  flex: 1;
  overflow-y: auto;
  overflow-x: hidden;
}
```

**Step 5: Create placeholder Home view**

`velour-v2/src/views/Home.tsx`:
```typescript
export function Home() {
  return (
    <div style={{ padding: '24px' }}>
      <h2>Home</h2>
      <p style={{ opacity: 0.6 }}>Welcome to Velour</p>
    </div>
  );
}
```

**Step 6: Wire App.tsx with Shell**

`velour-v2/src/App.tsx`:
```typescript
import { Shell } from './components/Layout/Shell';
import { Home } from './views/Home';

function App() {
  return (
    <Shell
      sidebar={<div className="sidebar-placeholder">Sidebar</div>}
      topbar={<div className="topbar-placeholder">TopBar</div>}
    >
      <Home />
    </Shell>
  );
}

export default App;
```

**Step 7: Clean up default template files**

Remove: `src/App.css`, any default Vite/Tauri template demo content.

**Step 8: Verify it builds**

```bash
cd "c:/Users/okfer/Downloads/Velour - Backup/velour-v2"
npm run tauri dev
```

Expected: Window opens with sidebar placeholder (220px left), topbar, and "Home" content area.

**Step 9: Commit**

```bash
git add -A
git commit -m "feat: add project structure, types, Zustand stores, Shell layout"
```

---

## Task 3: Theme and global styles

**Files:**
- Create: `velour-v2/src/styles/theme.css`
- Create: `velour-v2/src/styles/globals.css`
- Modify: `velour-v2/src/main.tsx` (import styles)
- Copy: `Inter-SemiBold.ttf` to `velour-v2/public/fonts/`

**Step 1: Copy the Inter font**

```bash
mkdir -p "c:/Users/okfer/Downloads/Velour - Backup/velour-v2/public/fonts"
cp "c:/Users/okfer/Downloads/Velour - Backup/src/Velour/Assets/Fonts/Inter-SemiBold.ttf" \
   "c:/Users/okfer/Downloads/Velour - Backup/velour-v2/public/fonts/"
```

**Step 2: Create theme.css with CSS variables**

`velour-v2/src/styles/theme.css`:
```css
@font-face {
  font-family: 'Inter';
  font-weight: 600;
  font-style: normal;
  src: url('/fonts/Inter-SemiBold.ttf') format('truetype');
}

:root,
[data-theme='dark'] {
  --bg-sidebar: #141414;
  --bg-main: #252525;
  --bg-surface: #2a2a2a;
  --bg-hover: rgba(231, 72, 86, 0.16);
  --bg-selected: rgba(231, 72, 86, 0.4);
  --bg-island: rgba(24, 24, 24, 0.94);

  --text-high: #ffffff;
  --text-medium: #cccccc;
  --text-low: #999999;

  --accent: #E74856;
  --accent-dark1: #C73E4C;
  --accent-dark2: #A73441;
  --accent-light1: #EC5F6A;
  --accent-light2: #F07881;

  --island-fg: #F0F0F0;
  --island-fg-secondary: #A0A0A0;
  --island-fg-tertiary: #707070;
  --island-icon: #ffffff;
  --island-play-bg: rgba(255, 255, 255, 0.2);
  --island-slider-filled: #E8E8E8;
  --island-slider-unfilled: #4A4A4A;

  --explicit-badge-bg: rgba(136, 136, 136, 0.53);
  --scrollbar-thumb: rgba(255, 255, 255, 0.3);

  --border-subtle: rgba(255, 255, 255, 0.08);
  --shadow-popup: 0 8px 32px rgba(0, 0, 0, 0.5);

  --radius-sm: 4px;
  --radius-md: 8px;
  --radius-lg: 16px;
  --radius-pill: 999px;

  --transition-fast: 100ms cubic-bezier(0.4, 0, 0.2, 1);
  --transition-normal: 150ms cubic-bezier(0.4, 0, 0.2, 1);
  --transition-slow: 200ms cubic-bezier(0.4, 0, 0.2, 1);
}

[data-theme='light'] {
  --bg-sidebar: #F0F0F0;
  --bg-main: #FFFFFF;
  --bg-surface: #F5F5F5;
  --bg-hover: rgba(231, 72, 86, 0.1);
  --bg-selected: rgba(231, 72, 86, 0.2);
  --bg-island: rgba(24, 24, 24, 0.94);

  --text-high: #000000;
  --text-medium: #333333;
  --text-low: #666666;

  --border-subtle: rgba(0, 0, 0, 0.08);
  --shadow-popup: 0 8px 32px rgba(0, 0, 0, 0.15);
  --scrollbar-thumb: rgba(0, 0, 0, 0.3);
}
```

**Step 3: Create globals.css**

`velour-v2/src/styles/globals.css`:
```css
@import './theme.css';

* {
  margin: 0;
  padding: 0;
  box-sizing: border-box;
}

html, body, #root {
  height: 100%;
  overflow: hidden;
}

body {
  font-family: 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
  font-weight: 600;
  font-size: 14px;
  color: var(--text-high);
  background: var(--bg-main);
  -webkit-font-smoothing: antialiased;
  user-select: none;
}

::-webkit-scrollbar {
  width: 14px;
}
::-webkit-scrollbar-track {
  background: transparent;
}
::-webkit-scrollbar-thumb {
  background: var(--scrollbar-thumb);
  border-radius: 5px;
  border: 4px solid transparent;
  background-clip: padding-box;
}
::-webkit-scrollbar-thumb:hover {
  background: rgba(255, 255, 255, 0.5);
  background-clip: padding-box;
}

a {
  color: var(--accent);
  text-decoration: none;
}

button {
  font-family: inherit;
  font-weight: 600;
  cursor: pointer;
  border: none;
  background: none;
  color: inherit;
}

img {
  display: block;
}

input, textarea {
  font-family: inherit;
  font-weight: 600;
}
```

**Step 4: Import globals in main.tsx**

Ensure `velour-v2/src/main.tsx` imports `'./styles/globals.css'` before rendering.

**Step 5: Apply dark theme by default**

In `index.html`, add `data-theme="dark"` to `<html>` tag.

**Step 6: Verify**

```bash
npm run tauri dev
```

Expected: Dark background (#252525), sidebar (#141414), Inter font active.

**Step 7: Commit**

```bash
git add -A
git commit -m "feat: add theme system, CSS variables, Inter font, dark/light mode"
```

---

## Task 4: Sidebar component

**Files:**
- Create: `velour-v2/src/components/Layout/Sidebar.tsx`
- Create: `velour-v2/src/components/Layout/Sidebar.css`
- Modify: `velour-v2/src/App.tsx` (wire sidebar)

**Step 1: Create SVG icon set**

Create `velour-v2/src/components/icons.tsx` with SVG icon components matching the Fluent icons from the Avalonia app. Each icon is a React component accepting `size` and `className` props. Icons needed:
- Home, Songs, Albums, Artists, Genres, Playlists, Favorites, Settings, Statistics
- Shuffle, Previous, Play, Pause, Next, RepeatAll, RepeatOne
- Queue, Lyrics, MoreDots, Search
- SpeakerHigh, SpeakerLow, SpeakerZero, SpeakerMute
- SmartPlaylist, Metadata, Folder
- Heart, HeartFilled, Plus, X, ChevronLeft, ChevronRight

Use 24x24 viewBox. Source the paths from the StreamGeometry values in `src/Velour/Assets/Icons.axaml`.

**Step 2: Build Sidebar component**

`velour-v2/src/components/Layout/Sidebar.tsx`:
```typescript
import { useSidebarStore } from '../../stores/sidebarStore';
import type { NavKey } from '../../types';
import {
  HomeIcon, SongsIcon, AlbumsIcon, ArtistsIcon,
  GenresIcon, PlaylistsIcon, FavoritesIcon,
  SettingsIcon, StatisticsIcon, SmartPlaylistIcon, PlusIcon,
} from '../icons';
import './Sidebar.css';

interface NavItem {
  key: NavKey;
  label: string;
  icon: React.ReactNode;
}

const libraryItems: NavItem[] = [
  { key: 'albums', label: 'Albums', icon: <AlbumsIcon size={18} /> },
  { key: 'artists', label: 'Artists', icon: <ArtistsIcon size={18} /> },
  { key: 'genres', label: 'Genres', icon: <GenresIcon size={18} /> },
  { key: 'playlists', label: 'Playlists', icon: <PlaylistsIcon size={18} /> },
  { key: 'songs', label: 'Songs', icon: <SongsIcon size={18} /> },
];

export function Sidebar() {
  const { activeNav, setActiveNav, playlists } = useSidebarStore();

  const handleNav = (key: NavKey) => {
    setActiveNav(key);
  };

  return (
    <nav className="sidebar">
      <div className="sidebar-section">
        <button
          className={`sidebar-item ${activeNav === 'home' ? 'active' : ''}`}
          onClick={() => handleNav('home')}
        >
          <HomeIcon size={18} />
          <span>Home</span>
        </button>
      </div>

      <div className="sidebar-section">
        <span className="sidebar-header">LIBRARY</span>
        {libraryItems.map((item) => (
          <button
            key={item.key}
            className={`sidebar-item ${activeNav === item.key ? 'active' : ''}`}
            onClick={() => handleNav(item.key)}
          >
            {item.icon}
            <span>{item.label}</span>
          </button>
        ))}
      </div>

      <div className="sidebar-section">
        <span className="sidebar-header">FAVORITES</span>
        <button
          className={`sidebar-item ${activeNav === 'favorites' ? 'active' : ''}`}
          onClick={() => handleNav('favorites')}
        >
          <FavoritesIcon size={18} />
          <span>Favorites</span>
        </button>
      </div>

      <div className="sidebar-spacer" />

      <div className="sidebar-section sidebar-bottom">
        <button
          className={`sidebar-item ${activeNav === 'settings' ? 'active' : ''}`}
          onClick={() => handleNav('settings')}
        >
          <SettingsIcon size={18} />
          <span>Settings</span>
        </button>
        <button
          className={`sidebar-item ${activeNav === 'statistics' ? 'active' : ''}`}
          onClick={() => handleNav('statistics')}
        >
          <StatisticsIcon size={18} />
          <span>Statistics</span>
        </button>
      </div>
    </nav>
  );
}
```

`velour-v2/src/components/Layout/Sidebar.css`:
```css
.sidebar {
  display: flex;
  flex-direction: column;
  height: 100%;
  padding: 8px;
}

.sidebar-section {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.sidebar-header {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-low);
  padding: 14px 14px 6px;
  letter-spacing: 0.5px;
}

.sidebar-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 14px;
  margin: 2px 4px;
  border-radius: var(--radius-pill);
  font-size: 15px;
  color: var(--text-high);
  transition: background var(--transition-fast);
  text-align: left;
}

.sidebar-item:hover {
  background: var(--bg-hover);
}

.sidebar-item.active {
  background: var(--bg-selected);
}

.sidebar-spacer {
  flex: 1;
}

.sidebar-bottom {
  padding-bottom: 8px;
}
```

**Step 3: Wire into App.tsx**

Replace sidebar placeholder with `<Sidebar />` component.

**Step 4: Verify**

Expected: Sidebar renders with nav items, clicking highlights the active item, 220px width, #141414 background.

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: add Sidebar with navigation items, icons, and active state"
```

---

## Task 5: TopBar + PlaybackBar shell

**Files:**
- Create: `velour-v2/src/components/Layout/TopBar.tsx`
- Create: `velour-v2/src/components/Layout/TopBar.css`
- Create: `velour-v2/src/components/Player/PlaybackBar.tsx`
- Create: `velour-v2/src/components/Player/PlaybackBar.css`
- Modify: `velour-v2/src/App.tsx`

**Step 1: Build TopBar**

`velour-v2/src/components/Layout/TopBar.tsx`:
```typescript
import { useState } from 'react';
import { useSidebarStore } from '../../stores/sidebarStore';
import { ChevronLeftIcon, SearchIcon, XIcon } from '../icons';
import { PlaybackBar } from '../Player/PlaybackBar';
import './TopBar.css';

export function TopBar() {
  const { navStack, popNav } = useSidebarStore();
  const [searchText, setSearchText] = useState('');
  const [isSearchFocused, setSearchFocused] = useState(false);

  const canGoBack = navStack.length > 0;

  return (
    <div className="topbar">
      <div className="topbar-left">
        {canGoBack && (
          <button className="topbar-back-btn" onClick={() => popNav()}>
            <ChevronLeftIcon size={14} />
          </button>
        )}
      </div>

      <div className="topbar-center">
        <PlaybackBar />
      </div>

      <div className="topbar-right">
        <div className={`topbar-search ${isSearchFocused ? 'focused' : ''}`}>
          <SearchIcon size={14} />
          <input
            type="text"
            placeholder="Search"
            value={searchText}
            onChange={(e) => setSearchText(e.target.value)}
            onFocus={() => setSearchFocused(true)}
            onBlur={() => setSearchFocused(false)}
          />
          {searchText && (
            <button className="search-clear" onClick={() => setSearchText('')}>
              <XIcon size={12} />
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
```

`velour-v2/src/components/Layout/TopBar.css`:
```css
.topbar {
  display: flex;
  align-items: center;
  padding: 8px 16px;
  min-height: 56px;
  position: relative;
}

.topbar-left {
  flex: 1;
  display: flex;
  align-items: center;
}

.topbar-back-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border-radius: var(--radius-pill);
  background: var(--accent);
  color: white;
  padding: 7px 16px;
  transition: transform var(--transition-fast);
}
.topbar-back-btn:hover {
  transform: scale(1.05);
}

.topbar-center {
  position: absolute;
  left: 50%;
  transform: translateX(-50%);
  z-index: 5;
}

.topbar-right {
  flex: 1;
  display: flex;
  justify-content: flex-end;
}

.topbar-search {
  display: flex;
  align-items: center;
  gap: 8px;
  background: var(--bg-surface);
  border-radius: var(--radius-pill);
  padding: 6px 14px;
  max-width: 220px;
  height: 32px;
  transition: background var(--transition-fast);
}

.topbar-search input {
  background: none;
  border: none;
  outline: none;
  color: var(--text-high);
  font-size: 13px;
  width: 100%;
}

.topbar-search input::placeholder {
  color: var(--text-low);
}

.search-clear {
  display: flex;
  align-items: center;
  opacity: 0.6;
}
.search-clear:hover {
  opacity: 1;
}
```

**Step 2: Build PlaybackBar skeleton**

`velour-v2/src/components/Player/PlaybackBar.tsx`:
```typescript
import { usePlayerStore } from '../../stores/playerStore';
import {
  ShuffleIcon, PreviousIcon, PlayIcon, PauseIcon, NextIcon,
  RepeatAllIcon, RepeatOneIcon, QueueIcon, LyricsIcon,
  SpeakerHighIcon, SpeakerLowIcon, SpeakerMuteIcon,
} from '../icons';
import './PlaybackBar.css';

function formatTime(ms: number): string {
  const totalSeconds = Math.floor(ms / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}

export function PlaybackBar() {
  const {
    currentTrack, state, position, duration, volume, isMuted,
    isShuffleEnabled, repeatMode, toggleShuffle, cycleRepeat,
    toggleQueuePopup,
  } = usePlayerStore();

  const fraction = duration > 0 ? position / duration : 0;

  const RepeatIcon = repeatMode === 'one' ? RepeatOneIcon : RepeatAllIcon;
  const VolumeIcon = isMuted || volume === 0
    ? SpeakerMuteIcon
    : volume < 50
    ? SpeakerLowIcon
    : SpeakerHighIcon;

  return (
    <div className="playback-bar">
      {/* Album art */}
      <div className="pb-artwork">
        {currentTrack?.artwork_path ? (
          <img src={currentTrack.artwork_path} alt="" />
        ) : (
          <div className="pb-artwork-placeholder" />
        )}
      </div>

      {/* Track info + controls */}
      <div className="pb-center">
        <div className="pb-track-info">
          <span className="pb-title">
            {currentTrack?.title || 'Not Playing'}
          </span>
          <span className="pb-artist">
            {currentTrack?.artist || ''}
          </span>
        </div>

        <div className="pb-controls">
          <button
            className={`pb-btn ${isShuffleEnabled ? 'active' : ''}`}
            onClick={toggleShuffle}
          >
            <ShuffleIcon size={16} />
          </button>
          <button className="pb-btn">
            <PreviousIcon size={18} />
          </button>
          <button className="pb-btn pb-play-btn">
            {state === 'playing' ? <PauseIcon size={20} /> : <PlayIcon size={20} />}
          </button>
          <button className="pb-btn">
            <NextIcon size={18} />
          </button>
          <button
            className={`pb-btn ${repeatMode !== 'off' ? 'active' : ''}`}
            onClick={cycleRepeat}
          >
            <RepeatIcon size={16} />
          </button>
        </div>

        {/* Seek bar */}
        <div className="pb-seek-row">
          <span className="pb-time">{formatTime(position)}</span>
          <div className="pb-seek-track">
            <div className="pb-seek-fill" style={{ width: `${fraction * 100}%` }} />
            <input
              type="range"
              className="pb-seek-input"
              min={0}
              max={1000}
              value={Math.round(fraction * 1000)}
              onChange={() => {/* wired in playback task */}}
            />
          </div>
          <span className="pb-time">{formatTime(duration)}</span>
        </div>
      </div>

      {/* Right controls */}
      <div className="pb-right">
        <div className="pb-volume">
          <button className="pb-btn">
            <VolumeIcon size={16} />
          </button>
          <input
            type="range"
            className="pb-volume-slider"
            min={0}
            max={100}
            value={isMuted ? 0 : volume}
            onChange={() => {/* wired in playback task */}}
          />
        </div>
        <button className="pb-btn" onClick={toggleQueuePopup}>
          <QueueIcon size={16} />
        </button>
        <button className="pb-btn">
          <LyricsIcon size={16} />
        </button>
      </div>
    </div>
  );
}
```

`velour-v2/src/components/Player/PlaybackBar.css`:
```css
.playback-bar {
  display: flex;
  align-items: center;
  gap: 12px;
  background: var(--bg-island);
  border-radius: var(--radius-lg);
  padding: 8px 16px;
  min-width: 520px;
  max-width: 680px;
  backdrop-filter: blur(20px);
}

.pb-artwork {
  width: 40px;
  height: 40px;
  border-radius: var(--radius-sm);
  overflow: hidden;
  flex-shrink: 0;
}
.pb-artwork img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}
.pb-artwork-placeholder {
  width: 100%;
  height: 100%;
  background: var(--island-play-bg);
}

.pb-center {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
  min-width: 0;
}

.pb-track-info {
  display: flex;
  gap: 8px;
  align-items: baseline;
  max-width: 100%;
}
.pb-title {
  font-size: 13px;
  color: var(--island-fg);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.pb-artist {
  font-size: 11px;
  color: var(--island-fg-secondary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.pb-controls {
  display: flex;
  align-items: center;
  gap: 8px;
}

.pb-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 4px;
  border-radius: var(--radius-pill);
  color: var(--island-icon);
  opacity: 0.8;
  transition: opacity var(--transition-fast);
}
.pb-btn:hover { opacity: 1; }
.pb-btn.active { color: var(--accent); opacity: 1; }

.pb-play-btn {
  width: 36px;
  height: 36px;
  background: var(--island-play-bg);
  border-radius: 50%;
  opacity: 1;
}

.pb-seek-row {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
}
.pb-time {
  font-size: 10px;
  color: var(--island-fg-tertiary);
  min-width: 32px;
  text-align: center;
}
.pb-seek-track {
  flex: 1;
  height: 4px;
  background: var(--island-slider-unfilled);
  border-radius: 2px;
  position: relative;
}
.pb-seek-fill {
  height: 100%;
  background: var(--island-slider-filled);
  border-radius: 2px;
}
.pb-seek-input {
  position: absolute;
  top: -6px;
  left: 0;
  width: 100%;
  height: 16px;
  opacity: 0;
  cursor: pointer;
}

.pb-right {
  display: flex;
  align-items: center;
  gap: 4px;
}
.pb-volume {
  display: flex;
  align-items: center;
  gap: 4px;
}
.pb-volume-slider {
  width: 70px;
  height: 4px;
  accent-color: var(--island-slider-filled);
}
```

**Step 3: Wire into App.tsx**

Replace topbar placeholder with `<TopBar />`.

**Step 4: Verify**

Expected: TopBar with search pill (right), floating playback island (center), back button appears when nav stack has entries.

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: add TopBar with search and PlaybackBar island"
```

---

## Task 6: Content area routing + placeholder views

**Files:**
- Create: `velour-v2/src/views/Songs.tsx`
- Create: `velour-v2/src/views/Albums.tsx`
- Create: `velour-v2/src/views/Artists.tsx`
- Create: `velour-v2/src/views/Genres.tsx`
- Create: `velour-v2/src/views/Playlists.tsx`
- Create: `velour-v2/src/views/Favorites.tsx`
- Create: `velour-v2/src/views/Settings.tsx`
- Create: `velour-v2/src/views/Statistics.tsx`
- Modify: `velour-v2/src/App.tsx` (route based on sidebarStore.activeNav)

**Step 1: Create placeholder views**

Each view follows the same pattern:
```typescript
export function Songs() {
  return (
    <div style={{ padding: '24px' }}>
      <h2>Songs</h2>
    </div>
  );
}
```

Create one per view: Songs, Albums, Artists, Genres, Playlists, Favorites, Settings, Statistics.

**Step 2: Wire routing in App.tsx**

```typescript
import { useSidebarStore } from './stores/sidebarStore';
// ... import all views

function ContentRouter() {
  const activeNav = useSidebarStore((s) => s.activeNav);

  switch (activeNav) {
    case 'home': return <Home />;
    case 'songs': return <Songs />;
    case 'albums': return <Albums />;
    case 'artists': return <Artists />;
    case 'genres': return <Genres />;
    case 'playlists': return <Playlists />;
    case 'favorites': return <Favorites />;
    case 'settings': return <Settings />;
    case 'statistics': return <Statistics />;
    default: return <Home />;
  }
}

function App() {
  return (
    <Shell sidebar={<Sidebar />} topbar={<TopBar />}>
      <ContentRouter />
    </Shell>
  );
}
```

**Step 3: Verify**

Click sidebar items. Expected: content area switches between views.

**Step 4: Commit**

```bash
git add -A
git commit -m "feat: add content routing and placeholder views for all nav items"
```

---

## Task 7: Queue popup overlay

**Files:**
- Create: `velour-v2/src/components/Queue/QueuePopup.tsx`
- Create: `velour-v2/src/components/Queue/QueuePopup.css`
- Modify: `velour-v2/src/App.tsx` (render overlay)

**Step 1: Build QueuePopup component**

Renders when `isQueuePopupOpen` is true. Fixed right panel (330px), shows current track, up next list, clear button. Matches original: rounded border (16px radius), dark background.

**Step 2: Wire into App.tsx**

Position as overlay inside `shell-main`, right-aligned, full height of content area.

**Step 3: Verify**

Click queue icon in playback bar. Expected: panel slides in from right.

**Step 4: Commit**

```bash
git add -A
git commit -m "feat: add Queue popup overlay panel"
```

---

## Task 8: Rust backend — module structure + Tauri commands skeleton

**Files:**
- Create: `velour-v2/src-tauri/src/lib.rs`
- Create: `velour-v2/src-tauri/src/commands/mod.rs`
- Create: `velour-v2/src-tauri/src/commands/playback.rs`
- Create: `velour-v2/src-tauri/src/commands/library.rs`
- Create: `velour-v2/src-tauri/src/commands/metadata.rs`
- Create: `velour-v2/src-tauri/src/commands/playlists.rs`
- Create: `velour-v2/src-tauri/src/commands/lyrics.rs`
- Create: `velour-v2/src-tauri/src/commands/settings.rs`
- Create: `velour-v2/src-tauri/src/commands/integrations.rs`
- Create: `velour-v2/src-tauri/src/models/mod.rs`
- Create: `velour-v2/src-tauri/src/models/track.rs`
- Create: `velour-v2/src-tauri/src/models/album.rs`
- Create: `velour-v2/src-tauri/src/models/playlist.rs`
- Create: `velour-v2/src-tauri/src/models/settings.rs`
- Create: `velour-v2/src-tauri/src/models/lyrics.rs`
- Create: `velour-v2/src-tauri/src/audio/mod.rs`
- Create: `velour-v2/src-tauri/src/audio/player.rs`
- Create: `velour-v2/src-tauri/src/audio/queue.rs`
- Create: `velour-v2/src-tauri/src/library/mod.rs`
- Create: `velour-v2/src-tauri/src/library/scanner.rs`
- Create: `velour-v2/src-tauri/src/library/index.rs`
- Create: `velour-v2/src-tauri/src/library/artwork.rs`
- Create: `velour-v2/src-tauri/src/services/mod.rs`
- Create: `velour-v2/src-tauri/src/persistence/mod.rs`
- Modify: `velour-v2/src-tauri/src/main.rs`

**Step 1: Create Rust models**

Define `Track`, `Album`, `Artist`, `Playlist`, `AppSettings`, `LyricLine`, `QueueState` as Rust structs with `#[derive(Debug, Clone, Serialize, Deserialize)]`. These mirror the TypeScript types.

**Step 2: Create command module stubs**

Each command file exports `#[tauri::command]` functions that return placeholder data or `Ok(())`. This lets the frontend wire up invoke calls before real logic exists.

Key commands per module:
- `playback.rs`: `play`, `pause`, `resume`, `stop`, `seek`, `set_volume`, `set_muted`
- `library.rs`: `scan_library`, `get_tracks`, `get_albums`, `get_artists`, `get_genres`, `remove_track`, `import_files`
- `metadata.rs`: `read_metadata`, `write_metadata`, `extract_artwork`
- `playlists.rs`: `get_playlists`, `create_playlist`, `update_playlist`, `delete_playlist`, `create_smart_playlist`
- `lyrics.rs`: `search_lyrics`, `get_cached_lyrics`, `save_lyrics`
- `settings.rs`: `load_settings`, `save_settings`
- `integrations.rs`: `discord_connect`, `discord_disconnect`, `lastfm_auth`, `lastfm_scrobble`

**Step 3: Register commands in main.rs**

```rust
fn main() {
    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![
            commands::playback::play,
            commands::playback::pause,
            // ... all commands
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
```

**Step 4: Verify Rust compiles**

```bash
cd "c:/Users/okfer/Downloads/Velour - Backup/velour-v2/src-tauri"
cargo build
```

Expected: Compiles without errors.

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: add Rust module structure, models, and command stubs"
```

---

## Task 9: Library scanning (Rust)

**Files:**
- Modify: `velour-v2/src-tauri/Cargo.toml` (add lofty, walkdir, rusqlite, uuid)
- Modify: `velour-v2/src-tauri/src/library/scanner.rs`
- Modify: `velour-v2/src-tauri/src/library/artwork.rs`
- Modify: `velour-v2/src-tauri/src/library/index.rs`
- Modify: `velour-v2/src-tauri/src/commands/library.rs`
- Modify: `velour-v2/src-tauri/src/persistence/mod.rs`

**Step 1: Add Rust dependencies**

```toml
lofty = "0.22"
walkdir = "2"
rusqlite = { version = "0.33", features = ["bundled"] }
uuid = { version = "1", features = ["v4"] }
image = { version = "0.25", default-features = false, features = ["jpeg", "png"] }
```

**Step 2: Implement scanner**

`scanner.rs`:
- Walk directories recursively, filter by audio extensions (mp3, flac, m4a, ogg, wav, wma, aac, opus, wv, ape)
- Extract metadata per file via lofty: title, artist, album, genre, year, track/disc number, duration, bitrate, sample rate, channels, codec
- Generate album IDs from normalized `(album, artist)` pair
- Emit scan progress events to frontend via Tauri event system

**Step 3: Implement artwork extraction**

`artwork.rs`:
- Extract embedded artwork via lofty
- Cache to `{data_dir}/artwork/{album_id}.jpg`
- Resize to max 500x500 via `image` crate for cache efficiency

**Step 4: Implement SQLite index**

`index.rs`:
- Create `library.db` with tracks table
- Insert/update/delete tracks
- Query tracks, albums (aggregated), artists (aggregated), genres (aggregated)

**Step 5: Implement persistence**

`persistence/mod.rs`:
- `data_dir()` → `%APPDATA%\Velour` (create if missing)
- JSON load/save for settings, playlists, queue state
- Artwork path helpers

**Step 6: Wire library commands**

`commands/library.rs`:
- `scan_library(folders: Vec<String>)` → runs scanner, stores to index, returns track count
- `get_tracks()` → reads all tracks from index
- `get_albums()` → aggregated from tracks
- `get_artists()` → aggregated from tracks
- `get_genres()` → aggregated from tracks

**Step 7: Update frontend command bridge**

`velour-v2/src/lib/commands.ts`:
```typescript
import { invoke } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';
import type { Track, Album, Artist, Genre } from '../types';

export async function scanLibrary(folders: string[]): Promise<number> {
  return invoke('scan_library', { folders });
}
export async function getTracks(): Promise<Track[]> {
  return invoke('get_tracks');
}
export async function getAlbums(): Promise<Album[]> {
  return invoke('get_albums');
}
export async function getArtists(): Promise<Artist[]> {
  return invoke('get_artists');
}
export async function getGenres(): Promise<Genre[]> {
  return invoke('get_genres');
}

export function onScanProgress(callback: (count: number) => void) {
  return listen<number>('scan-progress', (event) => callback(event.payload));
}
```

**Step 8: Verify**

```bash
cargo build
```

Add a temporary test button in Settings view that calls `scanLibrary` with a hardcoded path. Verify tracks appear in console.

**Step 9: Commit**

```bash
git add -A
git commit -m "feat: implement library scanning with lofty metadata, artwork cache, SQLite index"
```

---

## Task 10: Songs view (frontend)

**Files:**
- Modify: `velour-v2/src/views/Songs.tsx`
- Create: `velour-v2/src/views/Songs.css`
- Create: `velour-v2/src/components/Library/TrackRow.tsx`
- Create: `velour-v2/src/components/Library/TrackRow.css`
- Create: `velour-v2/src/components/common/ExplicitBadge.tsx`
- Modify: `velour-v2/src/stores/libraryStore.ts` (add filtered/sorted tracks logic)

**Step 1: Build TrackRow component**

Matches the original: grid layout with Title+Explicit | Time | Artist | Album | Genre | Plays | Options button. Hover highlight, double-click to play.

**Step 2: Build Songs view**

- Header "Songs" + shuffle button + queue-all button
- Sortable column headers (click to sort, arrow indicator)
- Virtualized track list (use CSS `overflow-y: auto` with fixed-height rows)
- Search filtering from topbar search text
- Empty state when no tracks

**Step 3: Wire library data loading**

On mount / library update, call `getTracks()` and store in libraryStore. Filter and sort in a `useMemo` derived from store state.

**Step 4: Verify**

After scanning, navigate to Songs. Expected: tracks displayed in sortable table, search filters.

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: implement Songs view with sortable columns, search, and track rows"
```

---

## Task 11: Albums view (frontend)

**Files:**
- Modify: `velour-v2/src/views/Albums.tsx`
- Create: `velour-v2/src/views/Albums.css`
- Create: `velour-v2/src/components/Library/AlbumTile.tsx`
- Create: `velour-v2/src/components/Library/AlbumTile.css`

**Step 1: Build AlbumTile component**

Matches original: artwork (square, rounded corners), album name, artist name, explicit badge, hover effect.

**Step 2: Build Albums view**

- Header "Albums"
- CSS Grid (6 columns, responsive)
- Search filtering
- Click album → detail view (wired later)
- Empty state

**Step 3: Verify**

Expected: Album grid with artwork tiles, 6 per row, search works.

**Step 4: Commit**

```bash
git add -A
git commit -m "feat: implement Albums grid view with artwork tiles"
```

---

## Task 12: Artists, Genres, Playlists, Favorites views (frontend)

**Files:**
- Modify: `velour-v2/src/views/Artists.tsx` + CSS
- Modify: `velour-v2/src/views/Genres.tsx` + CSS
- Modify: `velour-v2/src/views/Playlists.tsx` + CSS
- Modify: `velour-v2/src/views/Favorites.tsx` + CSS
- Create: `velour-v2/src/components/Library/ArtistRow.tsx` + CSS
- Create: `velour-v2/src/components/Library/GenreTile.tsx` + CSS

**Step 1: Artists view**

Alphabetical list with letter headers (A, B, C...), circular avatar (40x40), name + album/track count. Matches original.

**Step 2: Genres view**

Colored tiles (200x120) with genre name + track count. Use the 20-color palette from the original app.

**Step 3: Playlists view**

Tiles (220x200) with colored square + icon, name + track count, "Smart Playlist" badge. Create playlist button.

**Step 4: Favorites view**

Track cards (200px wide) with 180x180 artwork, heart overlay, title + artist.

**Step 5: Verify each**

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: implement Artists, Genres, Playlists, and Favorites views"
```

---

## Task 13: Audio playback (Rust + frontend wiring)

**Files:**
- Modify: `velour-v2/src-tauri/Cargo.toml` (add rodio)
- Modify: `velour-v2/src-tauri/src/audio/player.rs`
- Modify: `velour-v2/src-tauri/src/audio/queue.rs`
- Modify: `velour-v2/src-tauri/src/commands/playback.rs`
- Modify: `velour-v2/src/lib/commands.ts`
- Modify: `velour-v2/src/stores/playerStore.ts`
- Modify: `velour-v2/src/components/Player/PlaybackBar.tsx`
- Create: `velour-v2/src/hooks/usePlayer.ts`
- Create: `velour-v2/src/hooks/useTauriEvents.ts`

**Step 1: Add rodio dependency**

```toml
rodio = "0.20"
```

**Step 2: Implement Rust player**

`audio/player.rs`:
- `AudioPlayer` struct holding rodio `OutputStream`, `OutputStreamHandle`, `Sink`
- Methods: `play(path)`, `pause()`, `resume()`, `stop()`, `seek(position_ms)`, `set_volume(0.0-1.0)`
- Position tracking via separate thread (emits `position-changed` event ~4/sec)
- Track end detection (emits `track-ended` event)
- Thread-safe: wrap in `Arc<Mutex<>>`, manage via Tauri state

**Step 3: Implement queue logic**

`audio/queue.rs`:
- `QueueManager`: `up_next: Vec<Track>`, `history: Vec<Track>`, `current: Option<Track>`
- Shuffle: Fisher-Yates on `up_next`, store `_original_order` for unshuffle
- Repeat modes: off (stop at end), all (loop queue), one (loop current)
- `advance()`: move current to history, pop next from up_next
- `previous()`: if position > 3s restart, else pop from history

**Step 4: Wire Tauri commands**

```rust
#[tauri::command]
async fn play(path: String, state: State<'_, AudioState>) -> Result<(), String> { ... }

#[tauri::command]
async fn replace_queue_and_play(tracks: Vec<Track>, start_index: usize, ...) -> Result<(), String> { ... }
```

**Step 5: Wire frontend**

`hooks/useTauriEvents.ts`:
- Listen for `position-changed`, `track-ended`, `track-started`
- Update playerStore on each event

`hooks/usePlayer.ts`:
- Wrapper functions: `playPause()`, `next()`, `previous()`, `seekTo(fraction)`, `setVolume(vol)`
- Each calls the appropriate Tauri command

Wire PlaybackBar controls to these hooks.

**Step 6: Wire Songs view double-click**

Double-click a track row → `replaceQueueAndPlay(filteredTracks, clickedIndex)`.

**Step 7: Verify**

Play a track from Songs view. Expected: audio plays, seek bar moves, pause/resume works, next/previous work.

**Step 8: Commit**

```bash
git add -A
git commit -m "feat: implement audio playback with rodio, queue management, and PlaybackBar wiring"
```

---

## Task 14: Context menus

**Files:**
- Create: `velour-v2/src/components/common/ContextMenu.tsx`
- Create: `velour-v2/src/components/common/ContextMenu.css`
- Create: `velour-v2/src/hooks/useContextMenu.ts`
- Modify: All views that need context menus (Songs, Albums, etc.)

**Step 1: Build ContextMenu component**

Custom right-click context menu (not browser default). Position at cursor, dismiss on click outside. Styled to match original: dark background, rounded corners, hover highlight.

**Step 2: Build useContextMenu hook**

Manages open/close state, position, and menu items.

**Step 3: Add context menus to track rows**

Menu items matching original:
- Play
- Play Next
- Add to Queue
- Add to Playlist → submenu
- Add to Favorites / Remove from Favorites
- View Album
- View Artist
- Search Lyrics
- Show in Explorer
- Edit Metadata
- Remove from Library

**Step 4: Add context menus to album tiles**

Play, Shuffle, Play Next, Add to Queue, Add to Playlist, View Album, Show in Explorer.

**Step 5: Verify**

Right-click a track → menu appears with all items. Click "Play Next" → track added to up_next.

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: implement context menus for tracks and albums"
```

---

## Task 15: Settings view + persistence (Rust + frontend)

**Files:**
- Modify: `velour-v2/src/views/Settings.tsx` + CSS
- Modify: `velour-v2/src-tauri/src/commands/settings.rs`
- Modify: `velour-v2/src-tauri/src/persistence/mod.rs`
- Modify: `velour-v2/src/lib/commands.ts`
- Modify: `velour-v2/src/stores/settingsStore.ts`

**Step 1: Implement settings persistence (Rust)**

Load/save `settings.json` from `%APPDATA%\Velour\`. Serialize `AppSettings` struct.

**Step 2: Build Settings view sections**

Match original layout with setting cards (rounded 24px):
- **Appearance**: Dark / Light / System theme toggle
- **Playback**: Crossfade toggle + duration slider
- **Library**: Music folders list, add/remove folder (Tauri dialog), scan on startup, rescan button, rebuild index
- **Storage**: Library data size, artwork cache size, clear artwork cache
- **Integrations**: Discord RPC toggle, Last.fm section (connect/disconnect)
- **Reset**: Reset library confirmation

**Step 3: Wire theme switching**

Apply `data-theme` attribute to `<html>` when settings change. Persist immediately.

**Step 4: Wire folder picker**

Use `@tauri-apps/plugin-dialog` for native folder picker.

**Step 5: Verify**

Change theme → persists across restart. Add music folder → appears in list.

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: implement Settings view with persistence, theme switching, folder management"
```

---

## Task 16: Home view

**Files:**
- Modify: `velour-v2/src/views/Home.tsx` + CSS
- Modify: `velour-v2/src-tauri/src/commands/library.rs` (add recent/new queries)

**Step 1: Implement Rust commands**

- `get_recently_played()` → from queue history persistence
- `get_newly_added_albums(days: u32)` → albums with tracks added in last N days
- `get_favorite_albums()` → albums with at least 1 favorited track

**Step 2: Build Home view sections**

Match original layout:
- "Recently Played Songs" section (horizontal scroll or wrap)
- "Recently Played Albums" album row (max 12)
- "Newly Added" album row (last 3 days, max 24)
- "Favorite Albums" album row (max 12)
- Empty state overlay when library is empty

**Step 3: Verify**

**Step 4: Commit**

```bash
git add -A
git commit -m "feat: implement Home view with recently played, newly added, and favorites sections"
```

---

## Task 17: Album detail view

**Files:**
- Create: `velour-v2/src/views/AlbumDetail.tsx` + CSS
- Modify: `velour-v2/src/stores/uiStore.ts` (detail view state)
- Modify: `velour-v2/src/App.tsx` (render detail views)

**Step 1: Build AlbumDetail view**

Match original:
- Large album art (280x280, rounded)
- Album name + explicit badge
- Artist link (clickable, accent color)
- Genre, year, track count, audio quality badge
- Play + Shuffle buttons (accent color)
- Track list (# | Title + Explicit | Heart + Duration)
- Album footer: release date + copyright

**Step 2: Wire navigation**

Click album tile → `uiStore.setDetailView({ type: 'album', id })`. Back button → clear detail view. Override content area when detail view is set.

**Step 3: Verify**

Click album in grid → detail view with tracks. Back button returns to albums.

**Step 4: Commit**

```bash
git add -A
git commit -m "feat: implement Album detail view with track list and navigation"
```

---

## Task 18: Lyrics panel (Rust + frontend)

**Files:**
- Modify: `velour-v2/src-tauri/Cargo.toml` (reqwest already added)
- Create: `velour-v2/src-tauri/src/services/lrclib.rs`
- Modify: `velour-v2/src-tauri/src/commands/lyrics.rs`
- Create: `velour-v2/src/views/Lyrics.tsx` + CSS
- Create: `velour-v2/src/stores/lyricsStore.ts`
- Modify: `velour-v2/src/lib/commands.ts`

**Step 1: Implement LRCLIB service (Rust)**

`services/lrclib.rs`:
- `search_lyrics(artist, title)` → GET `https://lrclib.net/api/search?artist_name=...&track_name=...`
- `get_lyrics(artist, title, duration)` → GET `https://lrclib.net/api/get?artist_name=...&track_name=...&duration=...`
- Parse response into `LrcLibResult { synced_lyrics, plain_lyrics }`
- Cache results as `.lrc` files in `%APPDATA%\Velour\lyrics_cache\`

**Step 2: Implement .lrc parser**

Parse `[mm:ss.xx] text` format into `Vec<LyricLine>`. Handle offset tags.

**Step 3: Implement lyrics commands**

- `load_lyrics(track_id, file_path, artist, title, duration)`:
  1. Check sidecar .lrc
  2. Check embedded metadata
  3. Check cache
  4. Search online
- `save_lyrics(file_path, content)` → write sidecar .lrc

**Step 4: Create lyricsStore**

```typescript
interface LyricsState {
  lines: LyricLine[];
  unsyncedLines: string[];
  activeLineIndex: number;
  isSynced: boolean;
  isSearching: boolean;
  canSave: boolean;
  // ... setters
}
```

**Step 5: Build Lyrics view**

Match original:
- Full-height panel
- Album art (top)
- Current track info
- Synced / Unsynced tabs
- Synced: auto-scrolling lyrics with active line highlight (accent color)
- Click line to seek
- Search lyrics button
- Save lyrics button

**Step 6: Wire sync timer**

Use `setInterval(100ms)` to update `activeLineIndex` based on current playback position with 350ms lookahead.

**Step 7: Verify**

Play a track → open lyrics → synced lyrics auto-scroll and highlight.

**Step 8: Commit**

```bash
git add -A
git commit -m "feat: implement lyrics panel with LRCLIB, synced auto-scroll, and save"
```

---

## Task 19: Playlists CRUD (Rust + frontend)

**Files:**
- Modify: `velour-v2/src-tauri/src/commands/playlists.rs`
- Modify: `velour-v2/src-tauri/src/persistence/mod.rs`
- Modify: `velour-v2/src/views/Playlists.tsx`
- Create: `velour-v2/src/views/PlaylistDetail.tsx` + CSS
- Create: `velour-v2/src/components/common/Dialog.tsx` + CSS
- Modify: `velour-v2/src/stores/sidebarStore.ts`

**Step 1: Implement playlist persistence (Rust)**

Load/save `playlists.json`. CRUD operations.

**Step 2: Implement smart playlist evaluation**

Evaluate rules (field/operator/value conditions) against track library. Match all / match any logic. Sort + limit.

**Step 3: Build create playlist dialog**

Match original: name field (required), description field (optional), create/cancel buttons.

**Step 4: Build create smart playlist dialog**

Match original: name, description, match mode toggle, condition list (add/remove rules), preview count.

**Step 5: Build PlaylistDetail view**

Track list with reorder (drag), play, remove track, smart badge.

**Step 6: Wire sidebar playlist items**

Show playlists in sidebar under library items. Click → detail view.

**Step 7: Verify**

Create playlist → add tracks via context menu → view playlist → play.

**Step 8: Commit**

```bash
git add -A
git commit -m "feat: implement playlists with CRUD, smart evaluation, and detail view"
```

---

## Task 20: Metadata editor

**Files:**
- Modify: `velour-v2/src-tauri/src/commands/metadata.rs`
- Create: `velour-v2/src/components/common/MetadataDialog.tsx` + CSS
- Modify: `velour-v2/src/lib/commands.ts`

**Step 1: Implement tag write (Rust)**

Use lofty to write: title, artist, album, album_artist, genre, year, track_number, disc_number, comment, lyrics.

**Step 2: Build MetadataDialog**

Match original: header (title + artist + album), scrollable fields, save/cancel. Open as modal overlay.

**Step 3: Wire from context menus**

"Edit Metadata" → opens MetadataDialog → save → refresh library entry.

**Step 4: Verify**

Edit a track's title → save → verify file metadata changed.

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: implement metadata editor with tag read/write"
```

---

## Task 21: EQ + audio effects (Rust)

**Files:**
- Modify: `velour-v2/src-tauri/src/audio/player.rs`
- Create: `velour-v2/src-tauri/src/audio/equalizer.rs`
- Modify: `velour-v2/src-tauri/src/commands/playback.rs`
- Modify: `velour-v2/src/views/Settings.tsx` (EQ section)

**Step 1: Implement 10-band EQ**

Process audio samples through biquad filters (10 bands: 60, 170, 310, 600, 1k, 3k, 6k, 12k, 14k, 16k Hz). Apply gains from settings.

**Step 2: Implement EQ presets**

Match VLC presets: Flat, Classical, Club, Dance, Full Bass, Full Treble, Headphones, Large Hall, Live, Party, Pop, Reggae, Rock, Ska, Soft, Soft Rock, Techno.

**Step 3: Build EQ UI in Settings**

10 vertical sliders (-12 to +12 dB), preset dropdown, enable/disable toggle.

**Step 4: Verify**

Enable EQ, select "Rock" preset, play a track. Expected: audible EQ change.

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: implement 10-band equalizer with presets"
```

---

## Task 22: Discord + Last.fm integrations (Rust)

**Files:**
- Modify: `velour-v2/src-tauri/Cargo.toml` (add discord-rich-presence, reqwest)
- Create: `velour-v2/src-tauri/src/services/discord.rs`
- Create: `velour-v2/src-tauri/src/services/lastfm.rs`
- Modify: `velour-v2/src-tauri/src/commands/integrations.rs`
- Modify: `velour-v2/src/views/Settings.tsx` (integration sections)

**Step 1: Discord RPC**

Connect/disconnect, update presence with track info.

**Step 2: Last.fm**

Auth flow (get auth URL, complete auth with token), scrobble (>50% or >4min), update now playing, get artist image URL, get album description.

**Step 3: Wire Settings UI**

Discord: enable/disable toggle.
Last.fm: connect button → auth flow, username display, disconnect button.

**Step 4: Wire automatic scrobbling**

On track start + position > 50% duration, call scrobble.

**Step 5: Verify**

Enable Discord → check Discord shows now playing. Connect Last.fm → play track past 50% → check scrobble on Last.fm profile.

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: implement Discord Rich Presence and Last.fm scrobbling"
```

---

## Task 23: Remote sources — Navidrome, SMB, WebDAV (Rust)

**Files:**
- Create: `velour-v2/src-tauri/src/services/navidrome.rs`
- Create: `velour-v2/src-tauri/src/services/smb.rs`
- Create: `velour-v2/src-tauri/src/services/webdav.rs`
- Modify: `velour-v2/src-tauri/src/commands/integrations.rs`
- Modify: `velour-v2/src/views/Settings.tsx`

**Step 1: Navidrome connector**

Subsonic API client: auth, get albums, get tracks, stream URL, sync state.

**Step 2: SMB connector**

Connect to SMB shares, list directories, stream files.

**Step 3: WebDAV connector**

HTTP-based file access: list, download, stream.

**Step 4: Unify with local library**

Merge remote tracks into the library view with source type indicator.

**Step 5: Verify**

Connect to a Navidrome server → tracks appear in library → playback works.

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: implement Navidrome, SMB, and WebDAV remote source connectors"
```

---

## Task 24: Genre detail, Now Playing, Statistics views

**Files:**
- Create: `velour-v2/src/views/GenreDetail.tsx` + CSS
- Create: `velour-v2/src/views/NowPlaying.tsx` + CSS
- Modify: `velour-v2/src/views/Statistics.tsx` + CSS
- Modify: `velour-v2/src/views/Queue.tsx` + CSS

**Step 1: Genre detail**

Colored banner, genre name + track count, play/shuffle, track list.

**Step 2: Now Playing**

Large album art (350x350), centered track info, seek bar, playback controls.

**Step 3: Statistics**

Overview cards (total tracks/albums/artists/genres/duration), most played tracks/albums with bar graphs, genre distribution.

**Step 4: Queue view**

Full queue page (separate from popup): now playing card, history section, up next section, drag reorder.

**Step 5: Verify each view**

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: implement GenreDetail, NowPlaying, Statistics, and Queue views"
```

---

## Task 25: Polish and final integration

**Files:**
- Various existing files

**Step 1: Keyboard shortcuts**

- Space: play/pause
- Left/Right: seek ±5s
- Ctrl+Left/Right: previous/next
- Ctrl+Shift+D: debug panel
- Ctrl+F: focus search

**Step 2: Drag-drop import**

Handle file/folder drops on window → import to library.

**Step 3: Scroll position restoration**

Save/restore scroll offset when navigating between views.

**Step 4: Windows taskbar integration**

Tauri plugin for taskbar progress indicator.

**Step 5: Animated equalizer indicator**

Show animated bars on the currently playing track row.

**Step 6: Final verification**

```bash
cd "c:/Users/okfer/Downloads/Velour - Backup/velour-v2"
npm run tauri build
```

Expected: Builds a single executable.

**Step 7: Commit**

```bash
git add -A
git commit -m "feat: add keyboard shortcuts, drag-drop, scroll restore, taskbar integration"
```

---

## Build First Priority

**Tasks 1-7** (scaffold + shell + theme + sidebar + topbar + playback bar + routing + queue popup) should be built first as a single batch. This gives a visually complete app shell that matches the original layout before any backend logic.

**Then Task 8-9** (Rust module structure + library scanning) to get real data flowing.

**Then Task 13** (audio playback) to make the app functional.

Everything else builds on this foundation.
