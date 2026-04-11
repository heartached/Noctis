<div align="center">

# Noctis

A sleek Windows music player with synced lyrics, equalizer, smart playlists, and a beautiful dark UI.

[![License: MIT](https://img.shields.io/badge/License-MIT-red.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)]()
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)]()
[![Downloads](https://img.shields.io/github/downloads/heartached/Noctis/total?color=yellowgreen)](https://github.com/heartached/Noctis/releases)
[![Build](https://img.shields.io/github/actions/workflow/status/heartached/Noctis/dotnet.yml?label=build)](https://github.com/heartached/Noctis/actions)

[Download](https://github.com/heartached/Noctis/releases) • [Features](#features) • [Build](#build)

</div>

---

> [!WARNING]
> This app is in early development and may contain bugs. If you find issues or want to request features, please open an [issue](https://github.com/heartached/Noctis/issues).
>
> Windows may flag the installer as untrusted because it isn't code-signed. This is normal for indie software — the app is safe to use.

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

- Synced lyrics via LRCLIB with offline cache
- 10-band equalizer with presets
- Smart playlists & favorites
- Lossless audio support — FLAC, WAV, AIFF, APE
- Album art & full metadata display
- Crossfade & volume normalization
- Last.fm scrobbling
- Discord Rich Presence integration
- Library indexing with SQLite

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

## License

MIT — see [LICENSE](LICENSE)
