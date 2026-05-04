<div align="center">

<h1>
  <img src="src/Noctis/Assets/Icons/Noctis%20Logo.png" width="48" align="absmiddle" />&nbsp;Noctis
</h1>

A music player that respects what's yours. Zero tracking, total control.

[![License: MIT](https://img.shields.io/badge/License-MIT-red.svg?style=for-the-badge)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg?style=for-the-badge)]()
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg?style=for-the-badge)]()
[![Downloads](https://img.shields.io/github/downloads/heartached/Noctis/total?color=yellowgreen&style=for-the-badge&cacheSeconds=300)](https://github.com/heartached/Noctis/releases)
[![Build](https://img.shields.io/github/actions/workflow/status/heartached/Noctis/dotnet.yml?label=build&style=for-the-badge)](https://github.com/heartached/Noctis/actions)

[Download](https://github.com/heartached/Noctis/releases) • [Features](#features) • [Build](#build) • [Feedback](#feedback)

</div>

---

### Home

![Home](docs/images/Home.png)

### Synced Lyrics

![Lyrics](docs/images/lyrics.png)

### Cover Flow

![Cover Flow](docs/images/coverflow.png)

### Albums

![Albums](docs/images/albums.png)

### Discord Rich Presence

<img src="docs/images/discord.png" width="280">

---

## Features

It packs a horizon of features including,

- [x] Lossless audio support — FLAC, ALAC, WAV, AIFF, APE, WavPack (plus MP3, AAC, OGG, Opus, WMA, M4A)
- [x] Cover Flow view for browsing albums
- [x] Synced lyrics via LRCLIB with offline cache
- [x] Side lyrics panel alongside any view
- [x] Collapsible sidebar with smooth animation
- [x] 10-band equalizer with presets
- [x] Smart playlists & favorites
- [x] Drag and drop import from Windows Explorer
- [x] Multi-select with bulk actions across all library views
- [x] In-app self-update from GitHub releases
- [x] Dynamic ambient backgrounds on lyrics and album pages
- [x] Playlist management with artwork, drag reorder, and real-time counts
- [x] Replay Gain & volume normalization
- [x] Gapless playback & crossfade
- [x] Full metadata editor with artwork, lyrics, and per-track options
- [x] Library statistics with play counts, genre distribution, and listening trends
- [x] Navidrome, SMB, and WebDAV remote source support with offline cache
- [x] Last.fm scrobbling
- [x] Discord Rich Presence integration
- [x] Library indexing with SQLite

---

## Build

```bash
git clone https://github.com/heartached/noctis
dotnet run --project src/Noctis/Noctis.csproj
```

**Requirements:** .NET 8 SDK · Windows 10/11 x64

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
