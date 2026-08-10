# Verified claims

Every capability statement on this site must trace to this file. It was produced
by reading the app source at `heartached/Noctis`, not the README — the README
overstates several features, and those gaps are listed below.

Regenerate with the `noctis-fact-verify` workflow if the app changes.

---

## Safe to claim

**Engine**
- LibVLC via LibVLCSharp 3.10.0. Native libvlc ships inside the Windows and macOS
  builds; Linux uses a system-installed libvlc.
- C# / .NET 8 / Avalonia UI. Not Electron.

**Output — Windows only**
- WASAPI exclusive mode opens the device at the source track's own sample rate
  (8–384 kHz), bypassing the system mixer.
- 16-bit sources pass through bit-for-bit at their native rate.
- Settings shows a live output status line, e.g. `WASAPI Exclusive — 44.1 kHz / 24-bit`.

**Playback**
- Gapless playback, on by default, via a pre-decoded standby player.
- Crossfade, 1–12 s. Unavailable while Windows exclusive mode is on (it needs two
  overlapping streams).
- AutoMix times blends using BPM and Camelot key when present. Stands down for
  manual skips, repeat-one, tracks under 45 s, and same-album sequential tracks.
- ReplayGain with four modes (Off / Track / Album / Auto) plus a ±12 dB pre-amp.
- Built-in ReplayGain scanner measuring EBU R128 integrated loudness via ffmpeg,
  writing REPLAYGAIN tags back to the files.

**EQ**
- 5–10 user-defined bands, each with editable centre frequency (20 Hz–20 kHz),
  gain (±12 dB) and Q (0.1–10).
- 18 named presets plus Custom. Flat is a true bypass.

**Formats — 13 playable**
FLAC, ALAC, WAV, AIFF, APE, WavPack, MP3, AAC, M4A, MP4, OGG, Opus, WMA.

**Library**
- Seven surfaces: Home, Songs, Albums, Artists, Folders, Playlists, Favorites.
- Albums view has release-type filter chips: All / Albums / Singles / EPs / Other.
- Per-file bit depth and sample rate, shown as e.g. `24-bit/96kHz`.
- A `Hi-Res Lossless` badge for lossless files ≥24-bit and above 48 kHz.
- Batch converter with 11 targets, via ffmpeg.

**Lyrics**
- Word-level timing when the source provides it (enhanced LRC, `.lyricsfile`, `.ttml`).
- Fetched from LRCLIB and NetEase, cached offline.
- Ambient album-art-tinted background on the **lyrics** view and lyrics panel.

**Themes**
Dark, Gray, Midnight, Light, System — plus a custom theme editor.

**Privacy — approved wording**
> No telemetry. No analytics. No crash reporting. No ads.

Verified: no telemetry, analytics SDK, crash reporter, session beacon, or device
identifier anywhere in the source or package list.

---

## Do NOT claim

| Claim | Why it is false |
|---|---|
| "Bit-perfect" unqualified, or bit-perfect hi-res / 24-bit | LibVLC 3.x's callback delivers 16-bit PCM only; >16-bit is truncated upstream. Also false at any volume below 100. |
| A live signal-path badge | Computed in `PlayerViewModel` but bound by no XAML. Never rendered. |
| 3D / perspective Cover Flow, tilted covers, reflections | Uses only scale + translate + z-index + a dimming overlay. No rotation. |
| DSD / DSF / DFF playback | Indexed and browsable only. The bundled VLC 3.x has no DSD decoder. |
| Streaming from Navidrome, SMB or WebDAV | No shipped UI can create a source connection. WebDAV is a stub returning empty. |
| An offline cache for remote content | `OfflineCacheService` is dead code; nothing resolves it. |
| Artist biographies | No bio feature exists. Only album descriptions, from Last.fm. |
| MusicBrainz as the artist-image source | Portraits come from Deezer only. |
| Ambient backgrounds on **album** pages | `AlbumDetailViewModel` sets the brush to null; the gradient sits at opacity 0. Lyrics pages only. |
| A "Compilations" album category | The fourth chip is "Other" — it buckets Compilation, Live, Remix and Soundtrack. |
| Genre distribution on the statistics page | Genre appears only in Wrap. |
| `.elrc` file support | Word tags parse, but the sidecar scanner only probes `.lyricsfile`, `.ttml`, `.lrc`. |
| Global instant search from the top bar | That filters the current page. Cross-library search is the Ctrl+K command palette. |
| "SQLite-backed library" unqualified | JSON files are the system of record; SQLite is a secondary index. |
| A Linux **ARM64 AppImage** | arm64 ships a **tar.gz** only. AppImage is x86_64. |
| A Windows on ARM build | Not produced. |
| Exclusive / hog-mode output on macOS or Linux | Gated to Windows and forced off elsewhere. |
| "Nothing ever leaves your machine" | The app contacts GitHub (update check) and Deezer (artist portraits) automatically. Use the approved privacy wording above. |
| Word-by-word lyrics for every song | Only when the source supplies word timing. |

---

## Note for the app repo, not the site

A Last.fm API key and secret are committed in plaintext at
`src/Noctis/Services/LastFmService.cs:15-16`. Worth rotating and moving out of
source control. Not a website issue.
