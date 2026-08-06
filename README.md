<div align="center">

<h1>
  <img src="src/Noctis/Assets/Icons/Noctis.ico" width="48" align="absmiddle" />&nbsp;Noctis
</h1>

**A music player that respects what's yours. Zero tracking, total control.**

[![Discord](https://img.shields.io/badge/DISCORD-JOIN%20SERVER-5865F2?style=for-the-badge&logo=discord&logoColor=white&labelColor=4F4F4F)](https://discord.gg/BNCDZQUVx7) &nbsp; [![Downloads](https://img.shields.io/github/downloads/heartached/Noctis/total?color=E74856&style=for-the-badge&cacheSeconds=600)](https://github.com/heartached/Noctis/releases) &nbsp; [![Support](https://img.shields.io/badge/SUPPORT-BUY%20ME%20A%20COFFEE-FFDD00?style=for-the-badge&logo=buymeacoffee&logoColor=black&labelColor=4F4F4F)](https://buymeacoffee.com/heartached)

[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-blue.svg?style=for-the-badge)]()

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
<summary><b>More screenshots</b>: Hi-Res library, parametric EQ, lyrics panel, queue, artist pages</summary>
<br>

|  |  |
|:---:|:---:|
| ![Hi-Res library](docs/images/songs.png) | ![Parametric EQ](docs/images/eq.png) |
| ![Album page](docs/images/album.png) | ![Queue](docs/images/queue.png) |

![Artist pages](docs/images/artist-page.png)

</details>

---

## Install

**Windows**, via a package manager:

```powershell
# winget
winget install heartached.Noctis

# Scoop (add the bucket once, then install)
scoop bucket add noctis https://github.com/heartached/scoop-bucket
scoop install noctis
```

Or download the installer or portable zip from the
[latest release](https://github.com/heartached/Noctis/releases/latest).

**macOS and Linux**: download the `.dmg` or AppImage from the
[latest release](https://github.com/heartached/Noctis/releases/latest).
Both ship with everything they need, so there is nothing else to install.

---

## Features

### Sound

- [x] Plays FLAC, ALAC, WAV, AIFF, APE, WavPack, MP3, AAC, OGG, Opus, WMA and M4A
- [x] Bit-perfect exclusive output on Windows
- [x] Parametric EQ with presets, and a saved preset per track if you want one
- [x] Gapless playback, crossfade and AutoMix transitions
- [x] ReplayGain and Sound Check volume leveling
- [x] Automatic BPM and musical key detection
- [x] Track Radio and Autoplay keep the music going when the queue runs out
- [x] Batch converter between formats (ffmpeg)

### Library

- [x] Songs, Albums, Artists, Folders and Playlists views
- [x] Albums split into Albums, Singles and EPs
- [x] Smart playlists, favorites, star ratings and play counts
- [x] Full metadata editor for artwork, lyrics and per-track options
- [x] Auto-tagging and cover art search using Deezer, MusicBrainz and Apple Music
- [x] Drag and drop import, watched folders, bulk edits
- [x] Playlist import from Exportify CSV, TuneMyMusic JSON and m3u files
- [x] Duplicate finder and file organizer
- [x] Command palette
- [x] Listening stats with a monthly and yearly Wrap

### Lyrics

- [x] Word-by-word karaoke lyrics, Apple Music style
- [x] Reads plain text, LRC, word-level LRC, TTML and Lyricsfile sidecars
- [x] Auto-fetched from LRCLIB and NetEase, cached offline
- [x] Lyrics panel you can keep open next to any page
- [x] Written By credits for the songwriters and producers
- [x] Built-in lyrics editor with `.lrc` export
- [x] Share lyrics as image cards or short clips

### Look and feel

- [x] Cover Flow browsing
- [x] Animated cover art
- [x] Ambient blurred backdrops on the lyrics page
- [x] Themes and accent colors, plus a custom theme editor
- [x] Liquid Glass translucent window mode
- [x] Resizable mini player with search, queue, volume and karaoke lyrics

### Connect

- [x] Stream from Jellyfin, Navidrome, Airsonic, Gonic or any Subsonic server
- [x] Discord Rich Presence
- [x] Scrobble to Last.fm and ListenBrainz
- [x] Web remote so you can control playback from your phone
- [x] Media keys on every platform, plus Windows taskbar controls
- [x] Artist images from Deezer
- [x] Sleep timer, tray icon and launch at login
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

**Requirements:** .NET 10 SDK

Supported platforms: Windows 10/11 (x64), macOS 12+ (Intel and Apple Silicon), Linux (x64 and ARM64).

### Native dependency: libvlc

The released downloads already carry everything they need. This only matters if
you are building from source.

- **Windows:** bundled automatically via NuGet, nothing to install.
- **macOS:** install [VLC](https://www.videolan.org/vlc/), which Noctis loads from
  `/Applications/VLC.app`. Packaged release builds bundle their own copy of VLC
  inside the app, so a downloaded Noctis needs no VLC install.
  ```bash
  brew install --cask vlc
  ```
- **Linux:** install via your package manager. The `-dev` package provides the
  unversioned `libvlc.so` symlink that the .NET loader looks for. The released
  AppImage bundles libvlc and its plugins, so it runs without this.
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
are unsigned self-contained builds.

**macOS**, using the portable zip:
```bash
unzip Noctis-osx-arm64.zip
xattr -dr com.apple.quarantine Noctis.app   # remove Gatekeeper quarantine flag
open Noctis.app
```

**Linux**, using the AppImage:
```bash
chmod +x Noctis-x86_64.AppImage
./Noctis-x86_64.AppImage
```

Or the portable tarball, which extracts without a top-level folder:
```bash
mkdir noctis && tar -xzf Noctis-linux-x64.tar.gz -C noctis
chmod +x noctis/Noctis
./noctis/Noctis
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

MIT, see [LICENSE](LICENSE)

---

> [!WARNING]
> Windows may flag the installer as untrusted because it isn't code-signed. This is normal for indie software and the app is safe to use.
