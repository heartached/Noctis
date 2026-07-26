<div align="center">

<h1>
  <img src="src/Noctis/Assets/Icons/Noctis.ico" width="48" align="absmiddle" />&nbsp;Noctis
</h1>

**A music player that respects what's yours. Zero tracking, total control.**

[![Discord](https://img.shields.io/badge/DISCORD-JOIN%20SERVER-5865F2?style=for-the-badge&logo=discord&logoColor=white&labelColor=4F4F4F)](https://discord.gg/BNCDZQUVx7) &nbsp; [![Downloads](https://img.shields.io/github/downloads/heartached/Noctis/total?color=E74856&style=for-the-badge&cacheSeconds=600)](https://github.com/heartached/Noctis/releases) &nbsp; [![Latest](https://img.shields.io/github/v/release/heartached/Noctis?color=E74856&style=for-the-badge&label=LATEST)](https://github.com/heartached/Noctis/releases/latest)

[![License: MIT](https://img.shields.io/badge/License-MIT-E74856.svg?style=for-the-badge)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-blue.svg?style=for-the-badge)]()
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg?style=for-the-badge)]()
[![Build](https://img.shields.io/github/actions/workflow/status/heartached/Noctis/dotnet.yml?label=build&style=for-the-badge)](https://github.com/heartached/Noctis/actions)

</div>

---

## Screenshots

#### Word-by-word karaoke lyrics

![Fullscreen lyrics](docs/images/lyrics-page.png)

#### Home

![Home](docs/images/home-page.png)

#### Cover Flow

![Cover Flow](docs/images/cover-flow.png)

#### Album pages

![Album page with the lyrics panel](docs/images/lyrics-panel.png)

#### Themes & accent colors

![Themes](docs/images/appearance.png)

<details>
<summary><b>More screenshots</b> — Hi-Res library, parametric EQ, lyrics panel, queue, artist pages</summary>
<br>

|  |  |
|:---:|:---:|
| ![Hi-Res library](docs/images/songs.png) | ![Parametric EQ](docs/images/eq.png) |
| ![Album page](docs/images/album.png) | ![Queue](docs/images/queue.png) |

![Artist pages](docs/images/artist-page.png)

</details>

---

## Install

**Windows** — via a package manager:

```powershell
# winget
winget install heartached.Noctis

# Scoop (add the bucket once, then install)
scoop bucket add noctis https://github.com/heartached/scoop-bucket
scoop install noctis

# Chocolatey
choco install noctis
```

Or download the installer / portable zip from the
[latest release](https://github.com/heartached/Noctis/releases/latest).

**macOS / Linux** — download the `.dmg` / AppImage from the
[latest release](https://github.com/heartached/Noctis/releases/latest).

---

## Features

### Sound

- [x] Plays everything — FLAC, ALAC, WAV, AIFF, APE, WavPack, MP3, AAC, OGG, Opus, WMA, M4A
- [x] Bit-perfect exclusive output on Windows, with a live signal-path badge
- [x] Parametric EQ with presets
- [x] Gapless playback, crossfade & AutoMix
- [x] ReplayGain volume leveling
- [x] Batch converter between formats (ffmpeg)

### Library

- [x] Songs, Albums, Artists, Folders & Playlists views
- [x] Albums split into Albums / Singles / EPs / Compilations
- [x] Smart playlists & favorites
- [x] Full metadata editor — artwork, lyrics, per-track options
- [x] Drag & drop import, watched folders, bulk edits
- [x] Duplicate finder & file organizer
- [x] Command palette
- [x] Listening stats + a monthly / yearly Wrap

### Lyrics

- [x] Word-by-word karaoke lyrics, Apple Music style
- [x] Auto-fetched from LRCLIB & NetEase, cached offline
- [x] Lyrics panel you can keep open next to any page
- [x] Built-in lyrics editor with `.lrc` export
- [x] Share lyrics as image cards or short clips

### Look & feel

- [x] Cover Flow browsing
- [x] Animated cover art
- [x] Ambient backgrounds on lyrics & album pages
- [x] Themes & accent colors, plus a custom theme editor
- [x] Floating mini player

### Connect

- [x] Discord Rich Presence
- [x] Scrobble to Last.fm & ListenBrainz
- [x] Stream from Navidrome, SMB & WebDAV
- [x] Web remote — control playback from your phone
- [x] Artist images & bios via MusicBrainz and Deezer
- [x] Updates itself from GitHub releases

<p align="center">
  <img src="docs/images/discord.png" width="240" alt="Discord Rich Presence" />
</p>

---

## Build

```bash
git clone https://github.com/heartached/Noctis
cd Noctis
dotnet run --project src/Noctis/Noctis.csproj
```

**Requirements:** .NET 8 SDK

Supported platforms: Windows 10/11 (x64), macOS 12+ (Intel & Apple Silicon), Linux (x64 & ARM64).

### Native dependency — libvlc

- **Windows:** bundled automatically via NuGet — nothing to install.
- **macOS:** install [VLC](https://www.videolan.org/vlc/) (Noctis loads libvlc from `/Applications/VLC.app`):
  ```bash
  brew install --cask vlc
  ```
- **Linux:** install via your package manager. The `-dev` package provides the
  unversioned `libvlc.so` symlink that the .NET loader looks for:
  ```bash
  # Debian/Ubuntu
  sudo apt install libvlc-dev
  # Fedora
  sudo dnf install vlc-devel
  # Arch
  sudo pacman -S vlc
  ```

### Running a downloaded build (macOS / Linux)

The macOS and Linux artifacts on the [Releases page](https://github.com/heartached/Noctis/releases)
are unsigned self-contained builds. After unzipping:

**macOS:**
```bash
cd Noctis-macos-arm64
xattr -dr com.apple.quarantine .   # remove Gatekeeper quarantine flag
./Noctis
```

**Linux:**
```bash
cd Noctis-linux-x64
chmod +x Noctis
./Noctis
```

### Build for another OS

```bash
dotnet publish src/Noctis/Noctis.csproj -c Release -r linux-x64   --self-contained
dotnet publish src/Noctis/Noctis.csproj -c Release -r osx-arm64   --self-contained
dotnet publish src/Noctis/Noctis.csproj -c Release -r osx-x64     --self-contained
dotnet publish src/Noctis/Noctis.csproj -c Release -r linux-arm64 --self-contained
```

---

## Star History

[![Star History Chart](https://api.star-history.com/svg?repos=heartached/Noctis&type=Date)](https://star-history.com/#heartached/Noctis&Date)

---

## Feedback

If you have any feedback about bugs, feature requests, etc. about the app, please let me know through [issues](https://github.com/heartached/Noctis/issues).

Yours Truly, heartached.

---

## License

MIT — see [LICENSE](LICENSE)

---

> [!WARNING]
> Windows may flag the installer as untrusted because it isn't code-signed. This is normal for indie software — the app is safe to use.
