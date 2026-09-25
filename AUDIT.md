# Noctis Audit — 2026-09-25

**Branch:** `audit/full-pass` (worktree `wt-audit`), based on `3a2e13d` = released **v1.5.4**.
**Previous audits:** `AUDIT_2026-08-04.md` (was `AUDIT.md`), `AUDIT_2026-07-24.md`.

## How to read this report

- Every finding has a `file:line` in the v1.5.4 code, the quoted code, why it is a bug, the user-visible impact, and a proposed fix.
- **Status** is what adversarial verification concluded. Independent verifier agents re-opened the cited code and tried to *refute* each finding.
  - **confirmed**: the verifiers re-read the code and agreed.
  - **refuted**: the code does not match, or the problem is mitigated elsewhere. Refuted findings are listed in Appendix C and were not fixed.
  - Every **high** finding got two independent votes: a code-accuracy lens and a reachability/impact lens. Disagreements went to a tie-breaker.
- **Severity** is the verifiers' corrected severity, taking the lowest (most conservative) vote. The finder's original severity is shown when it differs.
- "Needs runtime check" means the claim depends on behaviour that reading the code cannot settle. Phase 2 below records what the silent runtime test showed.

## Corrections to the brief (evidence, not assumptions)

| Brief said | The code says (v1.5.4) |
|---|---|
| C# / **.NET 8** | Every project targets **net10.0** (`src/Noctis/Noctis.csproj`: `net10.0-windows10.0.19041.0` on Windows, `net10.0` elsewhere). Avalonia **12.1.2**, LibVLCSharp 3.10.0, NAudio 2.3.0. |
| WASAPI **via waveout** | The Windows default is the **gapless engine**: VLC decodes into amem callbacks, then `GaplessSink`/`GaplessSpliceCore` ring, then NAudio **`WasapiOut`** (shared, event-driven, 100 ms), at `GaplessSink.cs:189` and `VlcAudioPlayer.cs:666-694`. The fallback is VLC `--aout=mmdevice` (`VlcAudioPlayer.cs:538-540`). No waveOut is used. |
| LibVLC **file-caching=0** | `--file-caching={DefaultCachingMs}` with `DefaultCachingMs = 1000` (`VlcAudioPlayer.cs:185, 497-516`), plus an adaptive per-media read-ahead (`VlcAudioPlayer.cs:5305-5336`). The seek-stutter fix is `--demux=avformat`. Not changed by this audit. |
| **80 ms** volume debounce | The volume-static fix is a float **session-volume ramp** (`VlcAudioPlayer.cs:74-80` tick 16 ms, step ≤ 10‰; `ScheduleVolumeWrite` 1026-1049; worker 1145-1206). There is no 80 ms debounce. The legacy `NOCTIS_VOL_SETTLE` debounce (64-66, 1054-1095) defaults to 0 and only runs when the OS session is unavailable. Every volume writer goes through `PlayerViewModel.Volume` → `ScheduleVolumeWrite`: bar slider and wheel, mini player, Ctrl+Up/Down, MPRIS, Web remote/Local API. No bypass was found. **Intact.** |
| RequestBringIntoView fix | **No `RequestBringIntoView` handler exists in `src/`**, and `git log -S RequestBringIntoView` finds none in history. The scroll-jump protection that does exist is `ListBoxItem Focusable=False` + `AutoScrollToSelectedItem=False` (Songs, Albums, Artists, Favorites, Playlist, Server, Queue popup), and it is intact. If the fix you meant lives on another branch, it is not in v1.5.4. **Owner: please confirm which fix you meant.** |
| Lyrics Grid + `TextWrapping=Wrap` | `TextWrapping="Wrap"` is intact on the lyric line and on the romanization/translation rows (`LyricsView.axaml:1180, 1346, 1369`). No finding touches it. |

## Method

1. **Five read-only lanes** ran in parallel: Audio pipeline, UI performance, Settings/dialogs/popups/commands, Security, Cross-platform. Each ran three sequential sweeps (15 finder agents, about 160 files read per lane). Every finder had to cite `file:line`, quote the code, and cite docs/source URLs for library-behaviour claims (NAudio, LibVLC, Avalonia, WASAPI).
2. **Adversarial verification:** every finding was re-checked by a separate verifier told to refute it (default: refuted if the cited code does not exist). High findings got a second, independent impact-lens vote.
3. **Incident:** the first run hit the account's **session usage limit**, and 29 of 52 agents died instantly with 0 tokens. The findings already produced were kept on disk. A second run redid only the missing work: the failed `audio-vm` sweep plus a vote on every finding that had none. Every finding in this report has at least one verifier vote (129 have one; 14 have two).
4. **Dependencies:** `dotnet list package --vulnerable --include-transitive` and `--outdated` were run for every desktop project and the plugin. See Phase 3.
5. Baseline build of `tests/Noctis.Tests` (which builds the app) at `3a2e13d`: **0 errors, 346 warnings** (287 unique after dedup). "Zero new warnings" in Phase 2 means compared against this set, by file, code and message.

## Summary

| Area | Critical | High | Medium | Low | Total |
|---|---|---|---|---|---|
| Audio pipeline | 0 | 2 | 24 | 12 | 38 |
| Cross-platform | 0 | 2 | 13 | 11 | 26 |
| Security | 0 | 2 | 8 | 6 | 16 |
| Settings, dialogs, popups, commands | 0 | 3 | 8 | 16 | 27 |
| UI performance | 0 | 0 | 15 | 13 | 28 |
| **Total (unique)** | **0** | **9** | **68** | **58** | **135** |

Six findings were reported independently by two lanes and are counted once: P22=A34, S10=U09, P27=S14, S23=U16, P31=S28, P03=X04. Two findings were refuted (Appendix C).

## The three known audio bugs

| Known bug | What Phase 1 found (static) |
|---|---|
| **Buzz on track start** | Not root-caused from code alone. The Aug-13 root cause (`Array.Clear` on NAudio's `byte[]`-punned float buffer) is fixed; the only such call site was replaced. **A08** is a live candidate for a click or buzz at track start and at every junction: on **44.1 kHz stereo devices** the junction declick writes an **odd** number of samples (`GaplessSpliceCore.cs:398`, 441 = 44100×2×5/1000), which swaps L/R for the rest of that read and adds a one-sample discontinuity. **A13/A11** are the same `byte[]`-pun class of bug: `Array.Copy` into the punned render buffer throws `ArrayTypeMismatchException` whenever speed ≠ 1× or pitch ≠ 0, which kills the engine's render thread. Phase 2 records the runtime evidence. |
| **Timeline slider desync on restore** | **A26**: a seek on a restored, not-yet-played track is dropped, and Play then jumps back to the stale restored position (`PlayerViewModel.cs:628`). **A30**: at speeds above 1× the timeline freezes for seconds after each track start or seek. |
| **Volume slider static** | The fix is intact (see "Corrections to the brief"). Related findings: **A14**, where the ramp worker keeps writing the session during pause/crossfade fades that assume it is parked, so the two fight; **A05**, where after a device switch the new stream renders before the user volume is re-applied, giving a brief full-volume blip; **A27**, where a restored mute state reaches only the UI and not the audio player. |

Other audible issues: **A07** (engine pause/resume cuts with no ramp, a click at both edges when "fade on pause" is off, which is the default), **A09** (skip or stop during a crossfade clicks), **A10** (stale pre-pause frame in the declick after a pause), **A23** (exclusive mode hard-cuts on every seek/stop/underrun).

## Phase 1 — Findings

Table first (most severe first), then full detail per finding.

| ID | Severity | Status | Area | Location | Title |
|---|---|---|---|---|---|
| A13 | high | confirmed | Audio pipeline | `src/Noctis/Services/TempoStretchProvider.cs:77` | Any playback speed ≠ 1× or any pitch shift throws on the render thread (Array.Copy into NAudio's byte[]-punned float[]), which kills the gapless engine's WASAPI output |
| A24 | high | confirmed | Audio pipeline | `src/Noctis/ViewModels/PlayerViewModel.cs:2914` | 'Stop after current track' / sleep-timer end-of-track does not stop when gapless or AutoMix is on: the next track plays anyway |
| P01 | high | confirmed | Cross-platform | `.github/workflows/dotnet.yml:136` | The Apple Silicon build ships an Intel-only ffmpeg, so every ffmpeg feature fails on an M-series Mac without Rosetta 2 |
| P03 | high | confirmed (duplicate of X04) | Cross-platform | `src/Noctis.Core.Server/Services/Server/ServerCertificate.cs:20` | Windows: the built-in server's TLS certificate is loaded with EphemeralKeySet, which Schannel does not support for server authentication |
| P25 | high | confirmed | Cross-platform | `src/Noctis/Services/VlcAudioPlayer.cs:431` | Linux tarball builds (x64 and the arm64-only artifact) can't load a distro libvlc, and the startup error tells Debian/Ubuntu/Fedora users to install `vlc`, which doesn't fix it |
| P27 | high | confirmed (duplicate of S14) | Cross-platform | `src/Noctis/ViewModels/MetadataViewModel.cs:2613` | Multi-select 'Rename files by pattern' changes Track.FilePath but keeps the old Id, so the next scan drops the renamed tracks' favorites, play counts, DateAdded and playlist membership |
| S04 | high | confirmed | Settings, dialogs, popups, commands | `src/Noctis/Services/FileOrganizerService.cs:159` | Organize Files rewrites playlist IDs only on disk; the sidebar's in-memory playlists keep the old track IDs and overwrite the remap on the next playlist save |
| S07 | high | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/LibrarySongsViewModel.cs:343` | Right-click actions on a row or tile outside the Ctrl-selection act on the selection instead: Remove from Library, which can send files to the Recycle Bin, and Remove from Playlist hit the wrong items |
| S14 | high | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/MetadataViewModel.cs:2610` | Metadata editor 'Rename files by pattern' moves the files without re-keying the track (skips RelocateTracksAsync): duplicate rows right away, then lost play counts, favorites and playlist membership |
| X04 | high | confirmed | Security | `src/Noctis.Core.Server/Services/Server/ServerCertificate.cs:20` | Windows: the built-in Noctis Server's HTTPS cannot complete a TLS handshake because the certificate key is loaded with EphemeralKeySet |
| X07 | high | confirmed | Security | `src/Noctis/Helpers/RecycleBin.cs:71` | 'Move to Recycle Bin' permanently deletes files on drives without a Recycle Bin: FOF_NOCONFIRMATION set without FOF_WANTNUKEWARNING |
| A01 | medium | confirmed | Audio pipeline | `src/Noctis/Services/GaplessSink.cs:81` | The engine's sample rate and channel count are fixed from the startup device for the whole session, so a mono or low-rate startup endpoint degrades all playback even after moving to a stereo device |
| A03 | medium | confirmed | Audio pipeline | `src/Noctis/Services/GaplessSink.cs:132` | The gapless engine keeps a WASAPI render stream running forever, including when paused or stopped, so the OS 'audio stream in use' power request never clears and idle sleep is blocked |
| A05 | medium | confirmed | Audio pipeline | `src/Noctis/Services/GaplessSink.cs:287` | After a device switch the new output starts playing before the user volume is set, so the new session can briefly render at full or stale volume |
| A07 | medium | confirmed | Audio pipeline | `src/Noctis/Services/GaplessSink.cs:326` | Engine pause and resume cut the waveform with no ramp, so every pause and resume clicks by default |
| A08 | medium | confirmed | Audio pipeline | `src/Noctis/Services/GaplessSpliceCore.cs:398` | On 44.1 kHz stereo devices the junction declick writes an odd number of samples, which swaps L and R for the rest of the read and adds a 1-sample underrun and a discontinuity |
| A09 | medium | confirmed | Audio pipeline | `src/Noctis/Services/GaplessSpliceCore.cs:603` | During an engine crossfade the declick ramp starts from the unmixed sample, so skipping or stopping during a blend clicks |
| A11 | medium | confirmed | Audio pipeline | `src/Noctis/Services/PitchShiftProvider.cs:101` | PitchShiftProvider copies into NAudio's byte[]-punned render buffer with Array.Copy, a second site of the reported TempoStretch crash, hit when pitch is reset to 0 |
| A12 | medium | confirmed | Audio pipeline | `src/Noctis/Services/PitchShiftProvider.cs:130` | PitchShiftProvider._ended is set on any 0-read and never cleared, so one ring underrun while pitch-shifted silences the rest of the track |
| A16 | medium | confirmed | Audio pipeline | `src/Noctis/Services/VlcAudioPlayer.cs:2075` | The ReplayGain reader ignores APE and ASF tags, but the RG scanner writes into them, so RG never applies to .ape/.wv/.wma files (or MP3s with APEv2-only RG) |
| A17 | medium | confirmed | Audio pipeline | `src/Noctis/Services/VlcAudioPlayer.cs:2298` | Remote (media-server) streams still never get gapless/crossfade; every boundary pays the EndReached grace plus a network open and parse |
| A18 | medium | confirmed | Audio pipeline | `src/Noctis/Services/VlcAudioPlayer.cs:2644` | On the engine, the next track's ReplayGain level is applied at the splice, ≥0.5 s before the audible boundary, so the outgoing tail (or the whole engine crossfade) plays at the wrong gain |
| A19 | medium | confirmed | Audio pipeline | `src/Noctis/Services/VlcAudioPlayer.cs:4243` | Pause is ignored during the engine tail (VLC input already Ended while the ring still plays), and TrackEnded then starts the next track |
| A21 | medium | confirmed | Audio pipeline | `src/Noctis/Services/VlcAudioPlayer.cs:4704` | The engine EndWatchdog treats the still-rendering outgoing tail as 'drained' and fires TrackEnded right after a gapless splice into a short, already-decoded track, so that track is skipped |
| A22 | medium | confirmed | Audio pipeline | `src/Noctis/Services/VlcAudioPlayer.cs:4978` | ReleasePreparedNext abandons whatever segment sits in the standby slot, including the audible outgoing/crossfade tail, so a queue edit, pause, seek or settings change during an engine crossfade pops and snaps the incoming track to full level |
| A23 | medium | confirmed | Audio pipeline | `src/Noctis/Services/WasapiGainOutput.cs:305` | Exclusive mode (WasapiGainOutput) hard-cuts on every seek, stop and underrun with no declick, and the seek worker deliberately skips masking on this path |
| A25 | medium | confirmed | Audio pipeline | `src/Noctis/Services/VlcAudioPlayer.cs:2513` | A seek, pause or any queue edit during a track's open/parse aborts the new track: the old song keeps playing under the new title, or there is silence |
| A26 | medium | confirmed | Audio pipeline | `src/Noctis/ViewModels/PlayerViewModel.cs:628` | Seeking a restored (not yet played) track is dropped and Play jumps back to the stale restored position; after stop-after-current, a seek plays audio while the UI stays Stopped |
| A27 | medium | confirmed | Audio pipeline | `src/Noctis/ViewModels/PlayerViewModel.cs:1537` | A restored mute state reaches only the UI, not the audio player: the app shows Muted but plays audibly |
| A28 | medium | confirmed | Audio pipeline | `src/Noctis/ViewModels/PlayerViewModel.cs:3161` | Removing or deleting the paused/stopped current track auto-starts the next track (surprise playback, including right after launch) |
| A29 | medium | confirmed | Audio pipeline | `src/Noctis/ViewModels/PlayerViewModel.cs:2292` | Repeat All wrap replays a stale start-time snapshot: it ignores Shuffle (second pass plays in album order with Shuffle lit) and later queue edits |
| A30 | medium | confirmed | Audio pipeline | `src/Noctis/ViewModels/PlayerViewModel.cs:2611` | At playback speed above 1x the timeline (and every tick-driven transition) freezes for seconds after each track start or seek |
| A31 | medium | confirmed | Audio pipeline | `src/Noctis/ViewModels/PlayerViewModel.cs:3084` | A playback-error cascade (drive offline) or one error on the last track wipes the whole queue and history, and the empty queue is then persisted |
| A32 | medium | confirmed | Audio pipeline | `src/Noctis/ViewModels/MainWindowViewModel.cs:976` | Shutdown saves the queue position and volume last, behind steps that can use up the 4 s deadline; the volume is persisted only on graceful exit |
| A34 | medium | confirmed | Audio pipeline | `src/Noctis/Services/MprisService.cs:618` | MPRIS Shuffle writes flip the flag without shuffling; Shuffle/LoopStatus changes are never signalled |
| P04 | medium | confirmed | Cross-platform | `src/Noctis.Core/Services/LibraryService.cs:2869` | Linux: case-folded track ids strand a track after a case-only rename (it points at a path that no longer exists, through every rescan) and merge files whose names differ only by case |
| P05 | medium | confirmed | Cross-platform | `src/Noctis.Core/Services/LibraryWatcherService.cs:81` | Linux: folder watching walks the whole music tree synchronously on the UI thread at every launch (inotify needs one watch per directory, added inside EnableRaisingEvents) |
| P06 | medium | confirmed | Cross-platform | `src/Noctis.Core/Services/LibraryWatcherService.cs:261` | Linux: hitting the inotify watch limit starts a re-entrant Refresh() storm from inside FileSystemWatcher construction |
| P08 | medium | confirmed | Cross-platform | `src/Noctis.Core/Services/MetadataService.cs:435` | Linux: folder cover art is found only if its name is entirely lower-case — Folder.jpg, Cover.jpg and cover.JPG are never used |
| P09 | medium | confirmed | Cross-platform | `src/Noctis.Server/Program.cs:160` | noctis-server skips its graceful shutdown on SIGTERM (docker stop / systemctl stop): the ProcessExit handler only cancels a token and returns, so the process can exit before the scan checkpoint runs |
| P12 | medium | confirmed | Cross-platform | `src/Noctis/Helpers/PlatformHelper.cs:169` | Linux AppImage: the AppImage environment scrub is not applied to OpenUrl (xdg-open), 'Open in <app>', gio trash or the theme probes, so host tools inherit the bundle's LD_LIBRARY_PATH |
| P15 | medium | confirmed | Cross-platform | `src/Noctis/Program.cs:36` | macOS: 'Open With Noctis', double-clicking an audio file and dropping files on the Dock icon never play anything |
| P16 | medium | confirmed | Cross-platform | `src/Noctis/Program.cs:143` | macOS/Linux: a fatal startup error (such as libvlc failing to load) is written only to stderr, so a Finder or Dock launch just bounces and quits with no message |
| P17 | medium | confirmed | Cross-platform | `src/Noctis/Services/Lyrics/LyricsWriter.cs:150` | Lyrics Studio Save runs the Finder trash on the UI thread (up to 15 s), and when the trash fails it overwrites the user's own .lrc while reporting it was moved to the recycle bin |
| P22 | medium | confirmed (duplicate of A34) | Cross-platform | `src/Noctis/Services/MprisService.cs:618` | MPRIS `Set Shuffle` only flips IsShuffleEnabled and skips ToggleShuffle, so shuffle from playerctl or the KDE widget lights the icon but never shuffles the queue |
| P24 | medium | confirmed | Cross-platform | `src/Noctis/Services/VlcAudioPlayer.cs:5270` | macOS: a user-installed VLC.app is always tried first and a load failure aborts startup; the bundled libvlc is never tried as a fallback |
| P28 | medium | confirmed | Cross-platform | `src/Noctis/Views/MainWindow.axaml.cs:43` | Linux resume remap hides MainWindow, which makes Avalonia hide every open dialog, detach it from its owner and end its ShowDialog as if cancelled; the dialog is never shown again |
| P29 | medium | confirmed | Cross-platform | `src/Noctis/Views/MainWindow.axaml.cs:490` | On desktops with no tray host (stock GNOME), start-minimized-at-login and hide-to-tray leave Noctis invisible: the `_trayIcon != null` guard can't detect a missing tray |
| P30 | medium | confirmed | Cross-platform | `src/Noctis/Views/MainWindow.axaml.cs:1057` | macOS: clicking the Dock icon does not bring back a window hidden to the tray (start minimized at login, close or minimize to tray) |
| P31 | medium | confirmed (duplicate of S28) | Cross-platform | `src/Noctis/Views/MainWindow.axaml.cs:1563` | Default ⌘/Ctrl+Arrow shortcuts are taken away from text boxes: ⌘←/⌘→ in the search box skip tracks instead of moving to the start or end of the line |
| S13 | medium | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/MetadataViewModel.cs:2515` | Metadata editor ignores WriteAlbumArt's return value: cover changes that fail on the playing file are reported as saved, and the old cover later comes back |
| S15 | medium | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/PlaylistViewModel.cs:666` | 'Remove from Playlist' appears on smart playlists and does not really remove the song: it disappears, then reappears on the next reload |
| S19 | medium | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/SettingsViewModel.cs:6233` | 'Reset settings' leaves many settings unreset (Upmix, shortcuts, launch-at-login toggle, accent-follows-art, ...) and the next save writes them back |
| S22 | medium | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/SidebarViewModel.cs:746` | The Add to Playlist dialog lists smart playlists; picking one quietly writes track IDs that never show and breaks the track count |
| S24 | medium | confirmed | Settings, dialogs, popups, commands | `src/Noctis/Views/LrcEditorDialog.axaml.cs:20` | In the LRC editor, a focused button takes the Space key, so Space pauses the song or edits a line instead of stamping it |
| S27 | medium | confirmed | Settings, dialogs, popups, commands | `src/Noctis/Views/MainWindow.axaml.cs:743` | Ctrl+A and Escape in the queue panel are handled by the page underneath first (Avalonia runs same-element tunnel handlers newest-first), so the queue never gets them |
| S28 | medium | confirmed | Settings, dialogs, popups, commands | `src/Noctis/Views/MainWindow.axaml.cs:1563` | Global shortcuts take Ctrl+Left/Right/Up/Down inside text boxes: word-jumping in the search box or the Lyrics Studio editor skips tracks and changes the volume |
| S30 | medium | confirmed | Settings, dialogs, popups, commands | `src/Noctis/Views/PlaylistView.axaml.cs:77` | PlaylistView keeps the previous playlist's Ctrl-selection when switching directly to another playlist, so menu actions in the new playlist act on songs from the old one |
| U03 | medium | confirmed | UI performance | `src/Noctis/Helpers/QueueRowSelection.cs:150` | Queue Ctrl+A then Delete (or removing a large selection) is O(N^2): every RemoveAt rebuilds the whole selection dictionary |
| U05 | medium | confirmed | UI performance | `src/Noctis/ViewModels/AlbumDetailViewModel.cs:330` | Album page closes itself (navigates Back) during any library rescan: RefreshFromLibrary treats a partial progressive publish as 'album removed' |
| U06 | medium | confirmed | UI performance | `src/Noctis/ViewModels/AlbumDetailViewModel.cs:355` | Album page tears down and rebuilds every track row and both carousels on every LibraryUpdated, even when nothing about the album changed |
| U07 | medium | confirmed | UI performance | `src/Noctis/ViewModels/ArtistDetailViewModel.cs:435` | Opening an artist page runs a whole-library regex token classification on the UI thread, and every artist page kept in history re-runs it on each LibraryUpdated |
| U08 | medium | confirmed | UI performance | `src/Noctis/ViewModels/BulkLyricsViewModel.cs:113` | Bulk Lyrics dialog reads the lyrics store of every selected track synchronously on the UI thread before it opens |
| U09 | medium | confirmed | UI performance | `src/Noctis/ViewModels/LyricsBackgroundPickerViewModel.cs:127` | Lyrics Background picker and Lyrics Studio 'Choose songs' picker run an un-debounced, allocation-heavy full-library search on the UI thread per keystroke |
| U11 | medium | confirmed | UI performance | `src/Noctis/ViewModels/MetadataViewModel.cs:2600` | Multi-track metadata 'Rename files by pattern' moves every file (plus sidecar probes/moves) synchronously on the UI thread |
| U13 | medium | confirmed | UI performance | `src/Noctis/ViewModels/PlayerViewModel.cs:3150` | Player removes deleted queue entries one at a time on LibraryUpdated: O(queue × removed) plus one CollectionChanged (and posted renumber) per track |
| U14 | medium | confirmed | UI performance | `src/Noctis/ViewModels/SendToFolderViewModel.cs:54` | Send to Folder rebuilds the whole plan (file stats per track) and the unvirtualized row list on the UI thread for every keystroke in the Destination box |
| U17 | medium | confirmed | UI performance | `src/Noctis/ViewModels/StatisticsViewModel.cs:109` | Statistics page still recomputes all library/history aggregates synchronously on the UI thread on every visit |
| U18 | medium | confirmed | UI performance | `src/Noctis/Views/AudioConverterDialog.axaml:165` | Selection-sized tool dialogs (Convert, Bulk Lyrics, Metadata Finder, Duplicate Finder, Send to Folder) realize every row: the ReplayGain virtualization fix was not applied to its siblings |
| U21 | medium | confirmed | UI performance | `src/Noctis/Views/LibrarySongsView.axaml.cs:225` | Songs/Albums/Artists lists jump to the top on any library reload while a search or artist filter is active |
| U22 | medium | confirmed | UI performance | `src/Noctis/Views/LyricsPanelView.axaml.cs:65` | Closing the lyrics side panel never un-registers it as a visible lyrics surface, so the per-frame word clock, the 100 ms sync timer and a 250 ms flow poll keep running for the rest of the session |
| U24 | medium | confirmed | UI performance | `src/Noctis/Views/MiniPlayerWindow.axaml.cs:1137` | Closing the mini player in the Pill design while music plays leaves the cover-spin frame loop running forever and keeps the closed MiniPlayerWindow in memory (one more for every close) |
| U26 | medium | confirmed | UI performance | `src/Noctis/Views/OrganizeFilesDialog.axaml:150` | Organize Files dialog renders one non-virtualized row per library track, added one at a time on the UI thread |
| X02 | medium | confirmed | Security | `src/Noctis.Core.Server/Services/Server/NoctisServer.cs:122` | Server login throttle is keyed on the TCP peer address and checked before authentication: behind the documented reverse proxy, 8 bad logins lock every user and API key out for 15 minutes |
| X03 | medium | confirmed | Security | `src/Noctis.Core.Server/Services/Server/NoctisServer.cs:222` | Subsonic 'download' fails for any track whose file name is not ASCII: the raw name goes into the Content-Disposition header |
| X05 | medium | confirmed | Security | `src/Noctis.Core/Services/EnhancedLrcParser.cs:187` | Words per line and [bg:] lines are uncapped, and MergeSyllables concatenates in O(n²); one crafted LRC line freezes the UI |
| X06 | medium | confirmed | Security | `src/Noctis.Core/Services/LogRedaction.cs:30` | Log redaction misses Subsonic stream credentials (t/s, or p=enc:<hex password>) in VLC lines that have no scheme, so Developer Mode's VLC bridge writes them to the session log and crash.log |
| X09 | medium | confirmed | Security | `src/Noctis/Services/LyricsfileParser.cs:54` | Lyricsfile (YAML) and TTML parsers skip the 3000-line MaxLyricLines cap, so a crafted sidecar or LRCLIB record freezes the UI |
| X11 | medium | confirmed | Security | `src/Noctis/Services/Plugins/ContentPack.cs:408` | A content pack's language string can carry a huge numeric format specifier ('{0:D999999999}') that passes validation and triggers a multi-GB allocation at startup |
| X12 | medium | confirmed | Security | `src/Noctis/Services/Plugins/PluginHost.cs:695` | After 'Reset all settings' (or a settings.json that cannot be recovered), the next launch turns on community plugins and approves and starts every installed code plugin, including ones the user had switched off or never approved |
| X16 | medium | confirmed | Security | `src/Noctis/ViewModels/SettingsViewModel.cs:6536` | In-app update download has a hard 5-minute deadline: on connections below about 4–6 Mbit/s it always fails as "Download cancelled." |
| A02 | low | confirmed | Audio pipeline | `src/Noctis/Services/GaplessSink.cs:113` | NOCTIS_ENGINE_TAP rebuilds the render chain as Tap(Provider), silently removing the mute gate and the beat and spectrum tap |
| A04 | low | confirmed | Audio pipeline | `src/Noctis/Services/GaplessSink.cs:244` | GaplessSink reads the device format and watches the Multimedia role, but NAudio's WasapiOut opens the Console role |
| A06 | low | confirmed | Audio pipeline | `src/Noctis/Services/GaplessSink.cs:287` | The sink rebuild starts the new output before publishing it and keeps _rebuilding set during the callback, so an immediate failure of the new stream is dropped and the engine stays silent (or rebuilds in a hot loop) |
| A10 | low | confirmed | Audio pipeline | `src/Noctis/Services/GaplessSpliceCore.cs:637` | After a paused sink, the provider's declick ramp replays the stale pre-pause frame, giving a click when a new track is chosen, or a seek or restart is made, while paused |
| A14 | low | confirmed | Audio pipeline | `src/Noctis/Services/VlcAudioPlayer.cs:1160` | The volume ramp worker keeps writing the session during pause and crossfade fades, which the setters assume are parked, so the two fight |
| A15 | low | confirmed | Audio pipeline | `src/Noctis/Services/VlcAudioPlayer.cs:1684` | Output-mode rebuild resumes at _player.Time, which on the engine is ahead of the audible position by the ring depth, so enabling Exclusive Mode skips ahead |
| A20 | low | confirmed | Audio pipeline | `src/Noctis/Services/VlcAudioPlayer.cs:4370` | The engine seek re-bases the segment from the unclamped target while the worker seeks to the end-guarded target, so the timeline and lyrics run up to 1 s ahead |
| A33 | low | confirmed | Audio pipeline | `src/Noctis/Services/VlcAudioPlayer.cs:4400` | The UI thread calls native libvlc get_length on every position tick and in Seek(), which can block while a worker's _player.Stop() joins a stalled input thread |
| A35 | low | confirmed | Audio pipeline | `src/Noctis/ViewModels/PlayerViewModel.cs:2001` | PlayTrack counts a play, records history and fires TrackStarted before the file is opened, so missing or unparsable files get phantom plays |
| A36 | low | confirmed | Audio pipeline | `src/Noctis/ViewModels/PlayerViewModel.cs:1536` | The desktop never persists the pre-shuffle order, so turning Shuffle off after a restart leaves the queue scrambled |
| A37 | low | confirmed | Audio pipeline | `src/Noctis/Services/VlcAudioPlayer.cs:4243` | A pause arriving while VLC is still Opening/Buffering a new track is ignored: audio starts under a Paused UI |
| A38 | low | confirmed | Audio pipeline | `src/Noctis/Services/MprisService.cs:522` | MPRIS SetPosition ignores the TrackId, so a stale scrub from a widget seeks the next track |
| P02 | low | confirmed | Cross-platform | `.github/workflows/dotnet.yml:422` | AppImage desktop entry has no MimeType= and no %F, so "Open with Noctis" (which the app supports) never appears in Linux file managers |
| P07 | low | confirmed | Cross-platform | `src/Noctis.Core/Services/LibraryWatcherService.cs:392` | Linux: the watcher's "file still being written" check can't see writers and passes on the first sample, so half-written files are imported |
| P10 | low | confirmed | Cross-platform | `src/Noctis/Helpers/LibraryRemovalHelper.cs:290` | Linux: whole-folder trash compares the removed paths case-insensitively and can trash a different file whose name differs only by case |
| P11 | low | confirmed | Cross-platform | `src/Noctis/Helpers/PlatformHelper.cs:124` | Linux 'Show in folder': dbus-send is run without --print-reply, so it exits 0 even when no file manager answers and the xdg-open fallback never runs |
| P13 | low | confirmed | Cross-platform | `src/Noctis/Helpers/PlatformHelper.cs:386` | Linux "System" theme: gsettings color-scheme 'default' is treated as a definite Light answer, so the portal/KDE/gtk-theme probes added for L24 never run |
| P14 | low | confirmed | Cross-platform | `src/Noctis/Helpers/StartupHelper.cs:129` | macOS launch at login saves whatever bundle path the app is running from (App Translocation, a mounted DMG, Downloads) and keeps showing ON after that path is gone |
| P18 | low | confirmed | Cross-platform | `src/Noctis/Services/MacNowPlayingService.cs:139` | macOS Now Playing always publishes playback rate 1.0 and ignores speed changes, so the Control Center position drifts at 0.75×–2× |
| P19 | low | confirmed | Cross-platform | `src/Noctis/Services/MacNowPlayingService.cs:174` | macOS Now Playing never shows album art: MPMediaItemArtwork initWithImage: does not exist on macOS, so the probe always skips the art |
| P20 | low | confirmed | Cross-platform | `src/Noctis/Services/MprisService.cs:192` | MPRIS never sends PropertiesChanged for Shuffle or LoopStatus, so desktop widgets and playerctl show stale repeat/shuffle state |
| P21 | low | confirmed | Cross-platform | `src/Noctis/Services/MprisService.cs:375` | MPRIS Raise skips ShowFromTray: it can show the main window next to the Topmost mini player and doesn't restore ShowInTaskbar |
| P23 | low | confirmed | Cross-platform | `src/Noctis/Services/SendToFolderService.cs:72` | Send to Folder (flat mode) keeps the source filename as-is, so Linux/macOS names containing : ? * " < > / fail on FAT32/exFAT/NTFS targets |
| S01 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis.Core/Services/LibraryService.cs:2133` | Metadata-schema backfill saves a startup snapshot of settings.json after a multi-minute pass, reverting changes made meanwhile |
| S02 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis.Core/Services/PersistenceService.cs:119` | A corrupt or unreadable settings.json is never surfaced to the user (SettingsLoadFailed has no reader); any read exception counts as corruption |
| S03 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis.Core/Services/PersistenceService.cs:602` | settings.json replace (File.Move overwrite) fails whenever another handle has the file open, and the failure is dropped with no retry or log |
| S05 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/CommandPaletteViewModel.cs:104` | Command palette 'Go to Settings' shows Settings as an inline page whose close (X) button does nothing, bypassing the modal |
| S06 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/CommandPaletteViewModel.cs:282` | Command palette: pressing Enter right after typing runs the previous result list's top row |
| S08 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/LibrarySongsViewModel.cs:383` | The track menu's 'Favorites' / 'Remove from Favorites' label follows the clicked row, but the command flips each selected track: on a mixed selection, 'Favorites' unfavorites the ones already favorited |
| S09 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/LrcEditorViewModel.cs:194` | The LRC editor stamps and seeks against whatever song is playing, even after playback has moved on to the next song |
| S10 | low | confirmed (duplicate of U09) | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/LyricsBackgroundPickerViewModel.cs:127` | The Lyrics Background picker and the Lyrics Studio 'Choose songs' picker scan the whole library on the UI thread on every keystroke, with no debounce |
| S11 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/LyricShareViewModel.cs:852` | Lyric share clip export cannot be cancelled; the still-card export ignores cancellation and a cancelled export leaves a truncated .mp4 |
| S12 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/MetadataHelper.cs:231` | Volume Adjust and EQ changes made in the multi-select or album metadata editor are not applied to the song that is playing |
| S16 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/SettingsViewModel.cs:384` | Release-list refresh is wired to the About tab, but the version manager now lives on the Advanced tab |
| S17 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/SettingsViewModel.cs:3043` | Changing any player/lyrics setting snaps every scrolling marquee title back to its start |
| S18 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/SettingsViewModel.cs:3973` | Four newer sliders save settings.json on every value tick instead of using the debounced save |
| S20 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/SettingsViewModel.Features.cs:473` | SyncDeviceId is generated on load but wiped by the first save's merge and never persisted |
| S21 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/SidebarViewModel.cs:611` | Deleting the playlist that is currently open (sidebar right-click, Delete) leaves its page showing; later edits there are silently lost |
| S23 | low | confirmed (duplicate of U16) | Settings, dialogs, popups, commands | `src/Noctis/ViewModels/SidebarViewModel.cs:879` | Edit Playlist changes the playlist fields before an unguarded cover File.Copy; if the copy fails, the edit is half-applied and nothing is shown |
| S26 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis/Views/LyricsBackgroundPickerDialog.axaml.cs:70` | Closing the Lyrics Background picker with Esc leaves a YouTube backdrop download running, which then silently applies the video |
| S29 | low | confirmed | Settings, dialogs, popups, commands | `src/Noctis/Views/MetadataWindow.axaml:490` | Metadata editor Cancel stays enabled while Save runs; it closes the window, the save still completes, and any write-failure message is lost |
| U01 | low | confirmed | UI performance | `src/Noctis.UI/Controls/GlassPanel.cs:618` | GlassBackdropOp.Render allocates a native SKRoundRect on every frosted repaint and never disposes it |
| U02 | low | confirmed | UI performance | `src/Noctis/Controls/SpectrumVisualizer.cs:145` | Lyrics-page SpectrumVisualizer starts a 250 ms polling DispatcherTimer while the LyricsView is pre-built but never shown (or detached), and it never stops |
| U04 | low | confirmed | UI performance | `src/Noctis/ViewModels/AddSongsDialogViewModel.cs:195` | Add Songs dialog 'Deselect all' is O(n²): List<Guid>.Remove per matched track |
| U10 | low | confirmed | UI performance | `src/Noctis/ViewModels/MetadataViewModel.cs:686` | Metadata editor constructor and Save read every selected track's lyrics from the disk-backed lyrics store (and sidecars) on the UI thread |
| U12 | low | confirmed | UI performance | `src/Noctis/ViewModels/PlayerViewModel.cs:1698` | Every track change probes the track's music folder 7+ times (music video + animated cover) on the UI thread, even with those features off |
| U15 | low | confirmed | UI performance | `src/Noctis/ViewModels/SettingsViewModel.cs:2049` | In Developer Mode, the Settings log pane is rebuilt (a full re-join of the 500-line session log) on every log burst, even while Settings is closed |
| U16 | low | confirmed | UI performance | `src/Noctis/ViewModels/SidebarViewModel.cs:879` | Edit Playlist copies the chosen cover synchronously on the UI thread with no error handling; a failed copy silently discards the whole edit |
| U19 | low | confirmed | UI performance | `src/Noctis/Views/LibraryFoldersView.axaml:93` | Folders view TreeView is not virtualized; expanding a root with many subfolders realizes every node |
| U20 | low | confirmed | UI performance | `src/Noctis/Views/LibraryPlaylistsView.axaml:28` | Playlists grid still realizes every tile (UniformGrid in a ScrollViewer), with a per-tile Height binding to its own Bounds |
| U23 | low | confirmed | UI performance | `src/Noctis/Views/MainWindow.axaml:1009` | Queue popup rows each build an inline ContextMenu whose icon eagerly decodes a 512x512 PNG per realized row |
| U25 | low | confirmed | UI performance | `src/Noctis/Views/MiniPlayerWindow.axaml.cs:1538` | Closing the mini player during a drawer slide leaves MiniPlayerViewModel.IsDrawerAnimating stuck true, so an in-flight search fill re-polls every 50 ms indefinitely |
| U27 | low | confirmed | UI performance | `src/Noctis/Views/PlaylistView.axaml.cs:115` | PlaylistView re-stamps every realized row once per CollectionChanged event; removing a multi-selection fires one event per track |
| U28 | low | confirmed | UI performance | `src/Noctis/Views/SettingsView.axaml:3892` | Settings 'Removed tracks' list is unbounded, non-virtualized and rebuilt item by item on every Settings open |
| X01 | low | confirmed | Security | `src/Noctis.Core.Server/Services/Server/LibraryServerAdapter.cs:117` | Server playlist endpoints load, modify and save playlists.json without a lock: concurrent create/update/delete/sync requests lose each other's changes |
| X08 | low | confirmed | Security | `src/Noctis/Services/Loon/LoonClient.cs:688` | Relay-supplied Loon chunk size is cast to int unchecked: a chunk_size of 2^32 gives a 0-byte chunk and an endless send loop that takes one of the 3 request slots and never releases it |
| X10 | low | confirmed | Security | `src/Noctis/Services/Plugins/ContentPack.cs:386` | A content pack, which installs with no approval and works in restricted mode, can replace the English (or current-language) text of the plugin consent/safety dialogs and destructive-action labels |
| X13 | low | confirmed | Security | `src/Noctis/Services/Plugins/PluginInstaller.cs:57` | A corrupt, encrypted or LZMA-compressed plugin .zip throws InvalidDataException that the install flow does not catch: install silently does nothing |
| X14 | low | confirmed | Security | `src/Noctis/Services/WrapArchiveService.cs:167` | A failed load of wrap_archive.json is followed by a Save that overwrites the permanent Wrap archive with only the years still in the live log |
| X15 | low | confirmed | Security | `src/Noctis/Services/YouTube/YtDlpTool.cs:126` | The yt-dlp executable is downloaded and silently auto-replaced with no checksum or signature check, then executed |

## Critical (0)

## High (11)

### A13 — Any playback speed ≠ 1× or any pitch shift throws on the render thread (Array.Copy into NAudio's byte[]-punned float[]), which kills the gapless engine's WASAPI output

- **Location:** `src/Noctis/Services/TempoStretchProvider.cs:77` (also: `src/Noctis/Services/PitchShiftProvider.cs:101`, `src/Noctis/Services/GaplessSpliceCore.cs:581`, `src/Noctis/Services/GaplessSpliceCore.cs:744`, `src/Noctis/Services/GaplessSink.cs:205`, `src/Noctis/Services/GaplessSink.cs:226`, `src/Noctis/Services/GaplessSink.cs:287`, `src/Noctis/Services/VlcAudioPlayer.cs:2280`, `src/Noctis/ViewModels/PlayerViewModel.cs:179`)
- **Area / sweep:** Audio pipeline / audio-core · **Category:** crash-render-thread · **Platforms:** windows
- **Verification:** confirmed (finder confidence: confirmed; finder severity: high)
- **Needs runtime check:** yes

**Evidence**

```
TempoStretchProvider.cs:73-77
    var avail = _outSamples - _outRead;
    if (avail > 0)
    {
        var n = Math.Min(avail, count - written);
        Array.Copy(_out, _outRead, buffer, offset + written, n);
PitchShiftProvider.cs:100-101
    if (take > 0)
        Array.Copy(_in, i * _channels, buffer, offset, take * _channels);
GaplessSpliceCore.cs:581  var n = adapted.Read(buffer, offset + written, count - written);   // same buffer
GaplessSpliceCore.cs:744-745  source = new PitchShiftProvider(...); source = new TempoStretchProvider(source, EffectiveStretchRate);
GaplessSink.cs:205  wasapiOut.Init(new SampleToWaveProvider(render));
Repro (PowerShell Add-Type, scratch only, outside the repo): a byte[64] viewed as float[] through an explicit-layout union gives view.GetType()=Byte[], and Array.Copy(new float[]{..}, 0, view, 0, 2) throws ArrayTypeMismatchException.
```

**Why it is a bug:** NAudio's SampleToWaveProvider.Read passes `new WaveBuffer(buffer).FloatBuffer` down the chain (https://github.com/naudio/NAudio/blob/master/NAudio.Core/Wave/SampleProviders/SampleToWaveProvider.cs). That float[] is really the byte[] render buffer, the same pun BUZZ_INVESTIGATION.md describes and the reason for the Array.Clear fix. MuteGateProvider:57 avoids the problem by using a Span. Array.Copy checks the runtime array types (float[] to byte[]) and throws ArrayTypeMismatchException, which the scratch repro confirmed. The previous audit only swept Array.Clear call sites, so these Array.Copy calls were missed. With upmix off (the default), the Provider passes this buffer straight to TempoStretchProvider.Read. Once the rate is ≠ 1 (VlcAudioPlayer.cs:2280, island speed menu, PlayerViewModel.cs:179), the first read that has WSOLA output reaches line 77 and throws. A pitch shift has the same effect: EffectiveStretchRate = rate/pitch ≠ 1 turns on the WSOLA stage. Setting the pitch back to 0 throws once more through PitchShiftProvider.DrainBufferedAtUnity:101. NAudio's WasapiOut.PlayThread catches the exception and raises PlaybackStopped(exception) (https://github.com/naudio/NAudio/blob/master/NAudio.Wasapi/WasapiOut.cs). GaplessSink then rebuilds (GaplessSink.cs:219-231), and the new render thread throws on its first FillBuffer. That stop event is most likely ignored: `_out` is only reassigned after newOut.Play() (GaplessSink.cs:287-299), and `_rebuilding` is still 1. The result is a dead output that nothing restarts, or else a tight rebuild loop.

**Impact:** On the default Windows engine, choosing any speed other than 1× or any pitch other than ±0 from the island menu makes the audio go silent. Most likely it stays silent even after going back to 1×, because the rebuilt output is dead and CheckDefaultDevice only rebuilds on a device-id change. The timeline also freezes because the segment is no longer consumed. It may instead spin in a WASAPI create/destroy loop with DeviceLost log spam.

**Proposed fix (small):** Never Array.Copy into the render buffer. Use `_out.AsSpan(_outRead, n).CopyTo(buffer.AsSpan(offset + written, n))` (a float Span over the pun is element-typed, as MuteGateProvider relies on) or an element loop, in TempoStretchProvider.Read:77 and PitchShiftProvider.DrainBufferedAtUnity:101. Add a regression test that drives GaplessSpliceProvider at rate 1.5 and pitch +2 through `new WaveBuffer(bytes).FloatBuffer`, like GaplessGapCanaryTests.

**Verifier votes**

- **confirmed** (refute lens): The code matches the finding. TempoStretchProvider.cs:77 does Array.Copy(_out, _outRead, buffer, offset + written, n) whenever rate != 1 (lines 58-64). GaplessSpliceCore.cs:581 passes the render buffer straight to adapted.Read, and Adapt (744-745) puts TempoStretchProvider last. The render chain is WasapiOut -> SampleToWaveProvider -> StallProbe -> BeatTapProvider -> MuteGateProvider -> GaplessSpliceProvider (GaplessSink.cs:95-127, 205). Every one of those hands the same buffer down, and none of them catches exceptions (GaplessSink.cs:503, BeatMeter.cs:223, MuteGateProvider.cs:50). I checked this at runtime in a scratch folder, outside the repo, against the project's own NAudio.Core 2.3.0: SampleToWaveProvider.Read hands the ISampleProvider a float[] whose runtime type is Byte[], Array.Copy into it throws ArrayTypeMismatchException, and a Span CopyTo works. Pitch reaches the same path: GaplessSpliceCore.cs:386-391 gives a stretch rate of rate/pitch != 1. The speed menu reaches it too: PlaybackBarView.axaml:500-535 -> PlayerViewModel.cs:179 -> VlcAudioPlayer.cs:2280. The engine is on by default on Windows (VlcAudioPlayer.cs:666). I did not check the exact rebuild-loop behaviour after WASAPI raises PlaybackStopped, but the render thread throwing on every read with rate != 1 is certain.
- **confirmed** (impact lens): The code matches. At TempoStretchProvider.cs:77, Array.Copy(_out, _outRead, buffer, ...) copies into the render buffer. With upmix off (the default), the chain from WasapiOut is SampleToWaveProvider -> StallProbe -> BeatTapProvider -> MuteGateProvider -> GaplessSpliceProvider -> adapted.Read (GaplessSpliceCore.cs:581). Every stage passes the same buffer through (GaplessSink.cs:503, BeatMeter.cs:223, MuteGateProvider Read). NAudio's SampleToWaveProvider.Read passes new WaveBuffer(buffer).FloatBuffer, which is the byte[] render buffer reinterpreted as float[]. I reproduced the throw independently on the .NET 10.0.12 runtime: Array.Copy from float[] into the explicit-layout pun throws ArrayTypeMismatchException (rt=System.Byte[]). No try/catch sits anywhere in the chain (GaplessSpliceCore.cs:492-581; StallProbe only guards the boost). NAudio's PlayThread catches the exception and raises PlaybackStopped(ex). The failing read never advances _outRead, so every later read throws again while rate != 1. That makes the rebuild in RebuildLoop (GaplessSink.cs:260-321) fail the same way: either the output dies or it keeps rebuilding. Which of those happens depends on the order of the PlaybackStopped delivery and the _out assignment, and the finding already hedges this. The pitch path is also correct. With pitch != 1, EffectiveStretchRate = rate/pitch != 1 (GaplessSpliceCore.cs:386-391), and PitchShiftProvider.DrainBufferedAtUnity:101 copies into the pun when TempoStretch is a pass-through. The code has shipped since v1.5.0 (aa940b60). The only WaveBuffer-pun test is GaplessGapCanaryTests, and TempoStretchTests do not use the pun. Reachability caveat: the island speed button, which also holds the pitch submenu, is opt-in because PlaybackBarShowPlaybackSpeed defaults to false (AppSettings.cs:443). Severity stays high because every use of the feature on the default Windows engine produces silence or a rebuild loop.

### A24 — 'Stop after current track' / sleep-timer end-of-track does not stop when gapless or AutoMix is on: the next track plays anyway

- **Location:** `src/Noctis/ViewModels/PlayerViewModel.cs:2914` (also: `src/Noctis/ViewModels/PlayerViewModel.cs:2755`, `src/Noctis/ViewModels/PlayerViewModel.cs:2878`, `src/Noctis/ViewModels/PlayerViewModel.cs:3046`, `src/Noctis.Core/Models/AppSettings.cs:173`, `tests/Noctis.Tests/AutoplayQueueTests.cs:113`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: high)

**Evidence**

```
PlayerViewModel.cs:2914-2921 (TryAdvanceForGapless guard, no StopAfterCurrentTrack check):
  if (!GaplessEnabled || AutoMixTransitionMode != ...Off || _autoMixAdvanceQueued ||
      CurrentTrack == null || UpNext.Count == 0 || RepeatMode == RepeatMode.One ||
      CurrentTrack.StopTimeMs > 0) return false;
2984:  AdvanceQueue(QueueAdvanceReason.Natural);
2231-2243: if (StopAfterCurrentTrack && reason is QueueAdvanceReason.Natural or QueueAdvanceReason.AutoMix)
  { CancelAutoMixTransition("stop after current track"); StopAfterCurrentTrack = false; State = PlaybackState.Stopped; ... return; }
3049-3053: Dispatcher.UIThread.Post(() => { CancelNaturalEndFallback(); AdvanceQueue(); });
```

**Why it is a bug:** Gapless is on by default (AppSettings.cs:173, PlayerViewModel.cs:113). With anything in Up Next, TryAdvanceForGapless calls AdvanceQueue(Natural) about 0.5 s before the track ends. The stop branch then clears the one-shot flag, sets State=Stopped and cancels the prepared standby. It never stops or pauses the audio player, so the outgoing track plays to its real end. VLC's EndReached plus the grace period then raises TrackEnded. OnTrackEnded posts AdvanceQueue() with no state check. StopAfterCurrentTrack is already false, so AdvanceQueueCore moves the track to History and calls PlayTrack(UpNext[0]), which sets State=Playing. AutoMix/Crossfade modes hit the same path through TryAdvanceForAutoMix -> AdvanceQueue(AutoMix) (line 2878). AutoplayQueueTests.StopAfterCurrentTrack_HaltsBeforeAutoplay only covers TrackEnded with an empty Up Next, so this path has no test.

**Impact:** With default settings, 'Stop after current track' and the sleep timer's 'end of track' mode never stop playback while songs are queued. The play button shows 'Play' for about 1-2 s, then the next song starts playing. Someone using it to fall asleep gets continued playback.

**Proposed fix (trivial):** Add `StopAfterCurrentTrack ||` to the early-return guards of TryAdvanceForGapless (line 2914) and TryAdvanceForAutoMix (line 2757) so the track ends through the normal TrackEnded path, which the stop branch already handles. As a defensive check, in OnTrackEnded's posted lambda return early when `State == PlaybackState.Stopped && CurrentTrack != null` (a stop-after has already halted).

**Verifier votes**

- **confirmed** (refute lens): Checked PlayerViewModel.cs:2912-2986. TryAdvanceForGapless has no StopAfterCurrentTrack guard and calls AdvanceQueue(Natural) at line 2984 once 0.5 s or less remains. TryAdvanceForAutoMix (2755-2879) is the same and calls AdvanceQueue(AutoMix) at 2878. The stop branch at 2231-2243 only runs CancelAutoMixTransition, clears the flag and sets State=Stopped. OnStateChanged (1731) only calls SyncNowPlayingFlags, and nothing calls _audioPlayer.Stop or Pause, so the outgoing track plays on. The next tick bails at 2923 because State is not Playing. VLC OnEndReachedCore (VlcAudioPlayer.cs:4467-4569) arms the grace timer, and TrackEnded fires at 4678. OnTrackEnded (3046-3053) posts AdvanceQueue() with the default Natural reason and no state check. The flag is already false, so AdvanceQueueCore reaches PlayTrack(UpNext[0]) at 2284. GaplessEnabled defaults to true (PlayerViewModel.cs:113, AppSettings.cs:173).
- **confirmed** (impact lens): The finding holds as written. TryAdvanceForGapless (PlayerViewModel.cs:2914-2921) has no StopAfterCurrentTrack guard and calls AdvanceQueue(Natural) at line 2984 when 0.5 s remain. TryAdvanceForAutoMix (2757-2761) does the same through AdvanceQueue(AutoMix) at 2878. The stop branch at 2231-2243 clears the one-shot flag, sets State=Stopped and calls CancelAutoMixTransition, which runs CancelPreparedNext at VlcAudioPlayer.cs:2510. It never stops or pauses the audio player, so the outgoing track plays on. OnEndReachedCore (VlcAudioPlayer.cs:4467-4570) then arms the grace deadline and restarts the position timer. The timer raises TrackEnded for the same session (4673-4678). OnTrackEnded (PlayerViewModel.cs:3046-3053) posts AdvanceQueue() with no state check. The flag is already false, so AdvanceQueueCore goes to PlayTrack(UpNext[0]) at line 2284, which sets State=Playing. OnStateChanged (1731) only syncs flags, and no path stops the audio. The only test (AutoplayQueueTests.cs:113) covers an empty Up Next through TrackEnded. On default settings (gapless on) with songs queued, Stop-after-current and the sleep timer's end-of-track mode fail every time they matter, so high is justified.

### P01 — The Apple Silicon build ships an Intel-only ffmpeg, so every ffmpeg feature fails on an M-series Mac without Rosetta 2

- **Location:** `.github/workflows/dotnet.yml:136` (also: `src/Noctis/Services/AudioConverterService.cs:147`, `src/Noctis/Services/AudioAnalysis/SideDecodeMeterFeed.cs:86`, `src/Noctis/App.axaml.cs:188`, `src/Noctis/Program.cs:374`)
- **Area / sweep:** Cross-platform / xplat-mac · **Category:** packaging · **Platforms:** macos
- **Verification:** confirmed (finder confidence: confirmed; finder severity: high)
- **Needs runtime check:** yes

**Evidence**

```
.github/workflows/dotnet.yml:132-141 (step runs for every rid starting with 'osx-', including osx-arm64, matrix lines 31-34):
          # evermeet.cx ships universal2 builds that work on both arm64 and x64.
          curl -sSL https://evermeet.cx/ffmpeg/ffmpeg-8.1.2.zip -o ffmpeg.zip
          echo "e91df72a1ee7c26606f90dd2dd4dcccc6a75140ff9ea6fdd50faae828b82ba69  ffmpeg.zip" | shasum -a 256 -c -
          unzip -q ffmpeg.zip -d ffmpeg-extract
          cp ffmpeg-extract/ffmpeg "publish/${{ matrix.rid }}/ffmpeg"
src/Noctis/Services/AudioConverterService.cs:147-150:
        // 1) Alongside the app — where CI-bundled binaries will live.
        var appDir = AppContext.BaseDirectory;
        var bundled = Path.Combine(appDir, exeName);
        if (File.Exists(bundled)) return bundled;
Checked by hand (scratch dir only): I downloaded the pinned zip. Its SHA-256 is e91df72a…ba69, which matches the pin. The first 16 bytes of the extracted ffmpeg are `cf fa ed fe 07 00 00 01 03 00 00 00 02 00 00 00`: a single-architecture 64-bit Mach-O with cputype 0x01000007 = CPU_TYPE_X86_64. A universal binary would start with `ca fe ba be`.
```

**Why it is a bug:** The comment in the CI step is wrong. evermeet.cx says its downloads are "static FFmpeg binaries for macOS 64-bit Intel" and "I do not plan to provide native ffmpeg binaries for Apple Silicon ARM" (https://evermeet.cx/ffmpeg/). The same step bundles this binary into Noctis-osx-arm64.dmg/.zip. On Apple Silicon an x86_64 executable can only run through Rosetta 2. Rosetta is not installed by default, and a plain exec from a native process does not trigger the install prompt (https://github.com/stemdeckapp/stemdeck/issues/642 hit the same problem with evermeet builds). The exec fails with 'Bad CPU type in executable'. GetFfmpegPath returns the bundled copy before it searches PATH, so a native Homebrew ffmpeg is never used unless the user sets a path by hand. These features all get ffmpeg from IAudioConverterService.GetFfmpegPath (Program.cs:296-388): Audio Converter, YouTube import, the waveform seek bar (FfmpegWaveformDecoder), BPM/key analysis, Lyrics Studio, and SideDecodeMeterFeed. SideDecodeMeterFeed is the only source for the visualizer and beat-reactive backdrops outside Windows (App.axaml.cs:188-191).

**Impact:** On an Apple Silicon Mac without Rosetta 2: conversions fail, the waveform seek bar stays empty, BPM/key are never filled in, the visualizer and beat-reactive backdrops never move, and Lyrics Studio cannot decode audio. The only workaround is to point Settings at another ffmpeg. With Rosetta installed, ffmpeg runs under x86 translation, which costs extra CPU during playback because the side decode runs continuously. Needs testing on macOS (Apple Silicon, with and without Rosetta).

**Proposed fix (small):** Pin ffmpeg per RID. For osx-arm64, bundle a native arm64 static build (for example from martin-riedl.de or osxexperts.net) pinned by its own SHA-256. Keep the evermeet build for osx-x64, or `lipo -create` both into one universal2 binary. Add a CI gate after the copy, for example `lipo -archs publish/osx-arm64/ffmpeg | grep -qw arm64`, and correct the comment. Optionally, GetFfmpegPath could skip a bundled binary that fails ValidateFfmpegAsync and fall through to PATH and /opt/homebrew/bin.

**Verifier votes**

- **confirmed** (refute lens): The CI step at .github/workflows/dotnet.yml:132-142 runs for every osx- RID, including osx-arm64 (matrix line 33). Its comment at line 136 claims universal2. I downloaded the pinned evermeet zip to scratch: its SHA-256 matches e91df72a...ba69, and the extracted ffmpeg starts with `cf fa ed fe 07 00 00 01`, which is a thin x86_64 Mach-O, not a fat `ca fe ba be` binary. The evermeet.cx page describes itself as 'static FFmpeg binaries for macOS 64-bit Intel'. Line 182 copies publish/<rid> into Contents/MacOS, and AudioConverterService.cs:147-150 returns that bundled copy before searching PATH. SideDecodeMeterFeed.cs:86 and App.axaml.cs:188-190 confirm the non-Windows visualizer depends on this binary. On Apple Silicon without Rosetta, every feature that needs ffmpeg fails.
- **confirmed** (impact lens): dotnet.yml:132-141 runs for every osx-* rid, including osx-arm64 (matrix lines 31-34). I re-hashed scratchpad/ffmpeg-8.1.2.zip and got e91df72a...ba69, which matches the pin. The extracted ffmpeg starts with cf fa ed fe 07 00 00 01, a thin x86_64 Mach-O (a universal binary would start with ca fe ba be), so the CI comment calling it universal2 is wrong. The .app step (dotnet.yml:182) copies publish/ into Contents/MacOS, so AppContext.BaseDirectory holds it. AudioConverterService.cs:147-150 returns this bundled copy before it checks PATH. Only a Settings override takes priority. Without Rosetta, exec fails with 'Bad CPU type in executable'. Rosetta is not installed by default, and only GUI launches prompt to install it, not command-line/exec launches. SideDecodeMeterFeed.cs:86 resolves this path on every track change, and App.axaml.cs:188-191 says the side decode is the only visualizer feed outside Windows. Playback itself is unaffected: the bundled VLC is the universal 3.0.23 build (dotnet.yml:195). High stands: multiple features silently fail on the main Mac build for every Apple Silicon user without Rosetta. It is platform-specific, but the conditions are common.

### P03 — Windows: the built-in server's TLS certificate is loaded with EphemeralKeySet, which Schannel does not support for server authentication

- **Location:** `src/Noctis.Core.Server/Services/Server/ServerCertificate.cs:20` (also: `src/Noctis.Core.Server/Services/Server/NoctisServer.cs:72`, `src/Noctis/ViewModels/SettingsViewModel.cs:1055`)
- **Area / sweep:** Cross-platform / xplat-general · **Category:** cross-platform · **Platforms:** windows
- **Verification:** confirmed (finder confidence: unverified; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
private static X509KeyStorageFlags KeyFlags => OperatingSystem.IsMacOS()
    ? X509KeyStorageFlags.Exportable
    : X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet;
```

**Why it is a bug:** The platform branch drops EphemeralKeySet only on macOS. On Windows the cert, whether loaded or created, keeps an ephemeral private key and goes straight to Kestrel through listen.UseHttps(certificate) (NoctisServer.cs:72). Microsoft documents that on Windows, SslStream with ephemeral-key certificates fails the handshake with 0x8009030E 'No credentials are available in the security package' because of a Schannel bug (https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-troubleshooting; https://github.com/dotnet/runtime/issues/23749). The only test (tests/Noctis.Tests/NoctisServerTests.cs:299-308) checks fingerprint round-trips and never performs a TLS handshake. Other server tests start with certificate:null.

**Impact:** If this holds on current Windows builds, clients cannot connect over HTTPS to the Noctis server hosted by the Windows desktop app (phone pairing / OpenSubsonic), while macOS and Linux hosts work.

**Proposed fix (trivial):** Drop EphemeralKeySet on Windows as well, i.e. use it only on Linux. Or, on Windows, re-import the PKCS#12 with X509KeyStorageFlags.Exportable | PersistKeySet or UserKeySet before passing it to Kestrel. Add a test that does a real HTTPS request against StartAsync(port, cert).

**Verifier votes**

- **confirmed** (refute lens): ServerCertificate.cs:20-22 keeps Exportable|EphemeralKeySet on every OS except macOS. Both LoadOrCreate (line 35) and Create (line 75) use these flags, and NoctisServer.cs:72 passes the certificate to listen.UseHttps. SettingsViewModel.cs:1055-1058 always starts the server with a certificate, so it is always HTTPS. I verified the failure on this machine (Windows 11 26200, .NET 10.0.12) with a scratch PowerShell loopback SslStream test that copies Create() exactly. The ephemeral-key certificate fails the handshake with 'AuthenticationException: Authentication failed because the platform does not support ephemeral keys' / 'Win32Exception: No credentials are available in the security package'. It fails with both ServerCertificate and SslStreamCertificateContext. The same PFX loaded with Exportable only completes TLS 1.3. Every HTTPS client connection to the server on Windows therefore fails. That makes the whole feature unusable on the primary platform, so I raised severity from medium to high.

### P25 — Linux tarball builds (x64 and the arm64-only artifact) can't load a distro libvlc, and the startup error tells Debian/Ubuntu/Fedora users to install `vlc`, which doesn't fix it

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:431` (also: `.github/workflows/dotnet.yml:318-322`, `.github/workflows/dotnet.yml:341-343`, `.github/workflows/dotnet.yml:355-359`, `.github/workflows/dotnet.yml:444-447`, `src/Noctis/Program.cs:141-144`, `README.md:183-203`)
- **Area / sweep:** Cross-platform / xplat-linux · **Category:** packaging-startup · **Platforms:** linux
- **Verification:** confirmed (finder confidence: likely; finder severity: high)
- **Needs runtime check:** yes

**Evidence**

```
VlcAudioPlayer.cs:5340-5345
    if (OperatingSystem.IsLinux())
    {
        return "libvlc is required but was not found. Install it with your package manager:\n" +
               "  Debian/Ubuntu:  sudo apt install vlc\n" +
               "  Fedora:         sudo dnf install vlc\n" +

VlcAudioPlayer.cs:429-432 (Linux branch): `else { Core.Initialize(); }` (no path, no resolver)

.github/workflows/dotnet.yml:355-359 (AppImage step only):
    # .NET's DllImport("libvlc") probes the UNVERSIONED soname
    # (libvlc.so), which distro packages only ship in -dev packages —
    # provide the symlinks ourselves.
    ln -sf libvlc.so.5 "$APPDIR/usr/lib/libvlc.so"
dotnet.yml:447: tar -czf "packaged/Noctis-linux-x64.tar.gz" -C publish/${{ matrix.rid }} .   (no libvlc, no symlink)
dotnet.yml:318-322: linux-arm64 ships ONLY this tarball.
Program.cs:141-144: non-Windows startup failure -> Console.Error.WriteLine only.
README.md:183: "The released downloads already carry everything they need."
```

**Why it is a bug:** LibVLCSharp binds with DllImport("libvlc"). Checking the cached LibVLCSharp 3.10.0 net10.0 DLL's strings finds no ".so" or "so.5" name, only the bare "libvlc" import. So on Linux the only probing is .NET's default: it adds ".so" and a "lib" prefix (https://learn.microsoft.com/en-us/dotnet/standard/native-interop/native-library-loading) and never tries libvlc.so.5. Debian/Ubuntu `vlc`/`libvlc5` install only libvlc.so.5, and Fedora `vlc` is the same. The unversioned libvlc.so comes from libvlc-dev / vlc-devel, which is exactly what LibVLCSharp's own Linux guide says to install: `sudo apt install libvlc-dev` (https://github.com/videolan/libvlcsharp/blob/3.x/docs/linux-setup.md). The repo's own CI comment (dotnet.yml:355-357) and README.md:193-195 say the same, but the symlink is added only inside the AppImage. The CI comment at dotnet.yml:341-343 also sends distros below the AppImage's glibc floor to "the tarball + system VLC path", which is the path that fails.

**Impact:** Every linux-arm64 user (tarball is the only artifact), plus x64 tarball users, sees Noctis refuse to start on Debian/Ubuntu/Mint/Fedora unless libvlc-dev / vlc-devel happens to be installed. The same goes for users of older distros that can't run the AppImage because of its glibc floor. Launched from a menu, nothing appears at all: the message goes only to stderr and crash.log. A user who does read it installs `vlc`, which they usually have already, and it still fails. Arch is unaffected because its vlc package ships libvlc.so. Needs testing on Linux (Ubuntu 22.04/24.04 and Raspberry Pi OS with only `vlc` installed).

**Proposed fix (small):** Before Core.Initialize() on Linux, register NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, ...). The resolver maps "libvlc" to the first of "libvlc.so" / "libvlc.so.5" that NativeLibrary.TryLoad accepts, maps "libvlccore" to "libvlccore.so.9" the same way, and returns IntPtr.Zero otherwise to keep default probing. Fix the Linux message to name libvlc-dev (Debian/Ubuntu) and vlc-devel (Fedora). Optionally also add the same two symlinks to the tarball, or document the tarball's requirement in README.

**Verifier votes**

- **confirmed** (refute lens): VlcAudioPlayer.cs:429-432: Linux calls Core.Initialize() with no path, and nothing in src registers a DllImport resolver or NativeLibrary (grep found none). The strings in LibVLCSharp 3.10.0 net10.0 contain only the bare names "libvlc"/"libvlccore", with no ".so.5". They also include the message "Using libvlcDirectoryPath is not supported on the Linux platform... Use LD_LIBRARY_PATH". So .NET's default probe for libvlc.so is the only lookup, and it fails when only the libvlc5/vlc runtime packages are installed. In dotnet.yml the libvlc.so/libvlccore.so symlinks (358-359) are added only to the AppImage AppDir. The arm64 tarball (318-322) and the x64 tarball (447) are plain publish folders. VlcAudioPlayer.cs:5342-5344 tells Debian/Fedora users to install `vlc`, which does not supply the unversioned soname. README.md:183 says the released downloads carry everything, and README:193-195 names the -dev packages only for source builds. Program.cs:141-144 writes the error only to stderr on non-Windows. The finding is accurate. It needs testing on Linux, but the load path is unambiguous from the code and the DLL strings.
- **confirmed** (impact lens): VlcAudioPlayer.cs:429-432 calls a bare Core.Initialize() on Linux. LibVLCSharp's Core.Desktop.cs InitializeDesktop returns immediately on Linux without loading anything. EnsureVersionsMatch then P/Invokes libvlc_get_version through DllImport("libvlc"). .NET probes libvlc.so, never libvlc.so.5. No SetDllImportResolver or NativeLibrary call exists anywhere in src (grep). Noctis.csproj:93-94 has no Linux libvlc NuGet. The libvlc.so/libvlccore.so symlinks are created only inside the AppImage (dotnet.yml:355-359). The linux-arm64 tarball (dotnet.yml:318-322) and the x64 tarball (dotnet.yml:447) are plain publish folders. The DllNotFoundException becomes an InvalidOperationException carrying the 'sudo apt install vlc' message (VlcAudioPlayer.cs:5340-5345). That installs only libvlc.so.5, so it does not fix the load. The error goes uncaught through the MainWindowViewModel resolve (App.axaml.cs:153), and Program.cs:141-144 writes it only to stderr on non-Windows. README.md:183 also says release downloads need nothing extra. Anchor: the root cause is the resolver-less init at line 431; the misleading message is at 5342. High is right: for every linux-arm64 user without libvlc-dev, the app does not start at all.

### P27 — Multi-select 'Rename files by pattern' changes Track.FilePath but keeps the old Id, so the next scan drops the renamed tracks' favorites, play counts, DateAdded and playlist membership

- **Location:** `src/Noctis/ViewModels/MetadataViewModel.cs:2613` (also: `src/Noctis.Core/Services/LibraryService.cs:298`, `src/Noctis.Core/Services/LibraryService.cs:1351`, `src/Noctis/Services/FileOrganizerService.cs:139`, `src/Noctis.Core/Models/Playlist.cs:24`)
- **Area / sweep:** Cross-platform / xplat-general · **Category:** data-loss · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: high)
- **Needs runtime check:** yes

**Evidence**

```
if (newPath != null && !conflict
    && !string.Equals(newPath, t.FilePath, StringComparison.OrdinalIgnoreCase))
{
    try
    {
        File.Move(t.FilePath, newPath);
        MoveLyricSidecars(t.FilePath, newPath);
        t.FilePath = newPath;
    }
    catch { /* Non-fatal — skip this file */ }
```

**Why it is a bug:** Track ids are path hashes (LibraryService.ComputeFileId, LibraryService.cs:2867). This rename sets t.FilePath and saves (MetadataViewModel.cs:2634), but t.Id is still the hash of the OLD path. On the next scan (LibraryService.cs:298) the file is looked up by ComputeFileId(newPath). That lookup misses, so the file is read as a brand-new track: lines 310-321 set SourceType=Local with no CopyMutableTrackState. The old-id track is never re-added, and the dedupe/publish at 555-558 plus SaveAsync at 593 make the loss permanent. Playlists hold track Guids (Models/Playlist.cs:24), so the renamed songs drop out of every playlist. With Watch Folders on (the default), the rename also raises a watcher event. ImportFilesAsync (LibraryService.cs:1136) then imports newPath under the new id while the old-id entry still points at the same file, so the song shows twice until the next scan. The file organizer does this correctly through LibraryService.RelocateTracksAsync (LibraryService.cs:1351-1394, called from FileOrganizerService.cs:139), which re-keys the id and returns a remap. This path skips all of that.

**Impact:** Renaming an album's files from the metadata editor silently loses user state for every renamed track (favorite, play count, last played, DateAdded, snooze, per-track EQ/volume) and removes them from playlists after the next scan or restart. In the same session the tracks can show as duplicates.

**Proposed fix (small):** Collect the (oldPath,newPath) pairs of successful moves and call _library.RelocateTracksAsync(moves) instead of assigning t.FilePath. Apply the returned id remap to playlists and the queue the same way FileOrganizerService does. Suppress the watcher for both paths first (ILibraryWatcherService.SuppressPaths), as the organizer does.

**Verifier votes**

- **confirmed** (refute lens): MetadataViewModel.cs:2600-2617 (the rename loop is reachable through the MetadataWindow.axaml:2085 ApplyRename checkbox) does File.Move and then `t.FilePath = newPath` without changing t.Id. It then saves at line 2634 and journals at 2643, both keyed by the old id. LibraryService.cs:298 looks up the existing track only by trackIndexSnapshot[ComputeFileId(filePath)]; grep finds no path-based or relocate fallback in the scan. The renamed file misses that lookup and is created as a new track: lines 314 and 320-321 set a new Id and SourceType=Local, and CopyMutableTrackState is not called. The old-id object is never added to newTracks, so the publish at 555-558 and SaveAsync at 593 drop it. Playlist.TrackIds is List<Guid> (Playlist.cs:24), so the renamed tracks drop out of playlists. The watcher import path at LibraryService.cs:1136-1166 keys by the new-path hash in the same way. The correct re-keying routine, RelocateTracksAsync (1351-1394: PrepareLyricsForIdChange, then track.Id = newId and a remap), exists but this path does not use it. User-state loss on an opt-in multi-select path warrants high.
- **confirmed** (impact lens): MetadataViewModel.cs:2600-2616 is reachable through the ApplyRename checkbox (MetadataWindow.axaml:2085). It moves the file and assigns t.FilePath = newPath but never re-keys t.Id. The id stays ComputeFileId(oldPath) (LibraryService.cs:314, 2867). On the next scan, LibraryService.cs:298 looks up ComputeFileId(newPath), misses, and creates a new track with SourceType=Local and no CopyMutableTrackState (lines 316-319). changedCount>0 skips the no-op fast path, and lines 555-558 plus SaveAsync at 593 publish a set without the old-id track. The failed-dir carry-forward at 453 does not apply. Playlists store Guid TrackIds (Playlist.cs:24). With WatchFoldersEnabled=true, the default (AppSettings.cs:95), OnRenamed feeds RecordRename (WatchDebouncer.cs:56-60). By flush time no track still has FilePath==oldPath, so the removal in LibraryWatcherService.cs:447-454 matches nothing. ImportFilesAsync (LibraryService.cs:1136-1165) then adds newPath under the new id: a duplicate this session. The organizer does this correctly through RelocateTracksAsync plus a playlist remap (FileOrganizerService.cs:139-141). High is right: silent user-data loss, but on an opt-in multi-select path.

### S04 — Organize Files rewrites playlist IDs only on disk; the sidebar's in-memory playlists keep the old track IDs and overwrite the remap on the next playlist save

- **Location:** `src/Noctis/Services/FileOrganizerService.cs:159` (also: `src/Noctis/ViewModels/SidebarViewModel.cs:91`, `src/Noctis/ViewModels/PlaylistViewModel.cs:359`, `src/Noctis/ViewModels/PlaylistViewModel.cs:686`, `src/Noctis.Core/Services/LibraryService.cs:1377`, `src/Noctis/ViewModels/OrganizeFilesViewModel.cs:97`, `src/Noctis/ViewModels/PlaylistImportViewModel.cs:199`)
- **Area / sweep:** Settings, dialogs, popups, commands / dialogs · **Category:** data-loss · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: high)

**Evidence**

```
// FileOrganizerService.cs:155-177
private async Task RemapPlaylistsAsync(IReadOnlyDictionary<Guid, Guid> remap)
{   var playlists = await _persistence.LoadPlaylistsAsync();
    ... pl.TrackIds[i] = newId; ...
    if (anyChanged)
        await _persistence.SavePlaylistsAsync(playlists);
}
// SidebarViewModel.cs:91   _library.LibraryUpdated += (_, _) => RefreshFavoritesCount();   (never reloads playlists)
// PlaylistViewModel.cs:686 await _persistence.SavePlaylistsAsync(_sidebar.Playlists.ToList());
```

**Why it is a bug:** RelocateTracksAsync gives every moved track a new Id, because the Id is the MD5 of the file path (LibraryService.cs:1377-1383, 2867-2872). The organizer then loads playlists.json from disk, remaps it and saves it back. SidebarViewModel.Playlists still holds the old IDs, and nothing reloads it after an organize. Sidebar.LoadPlaylistsAsync is called only at startup (MainWindowViewModel.cs:684), after a settings reset (:314) and after Playlist Import (PlaylistImportViewModel.cs:199). Every later playlist change writes that in-memory list back to disk: SidebarViewModel.cs:434/507/569/622/637/668/901, PlaylistViewModel.cs:87/686/711/744/961 and LibraryPlaylistsViewModel.cs:348/373 all call SavePlaylistsAsync(Playlists.ToList()). Scenario: the user runs Settings > Library > Organize and 500 files move. (1) Straight away, every playlist that contains a moved song shows it as missing, because PlaylistViewModel.LoadTracks looks songs up with library.GetTrackById(oldId), which now returns null (PlaylistViewModel.cs:359). (2) Before restarting, the user pins, renames, reorders or adds to any playlist. That save writes the stale IDs over the remapped playlists.json, so the moved songs are permanently gone from all playlists. Undo Last goes through the same RunAsync/RemapPlaylistsAsync path and has the same problem.

**Impact:** Moved songs disappear from every playlist in the current session. After any playlist edit made before a restart, they are lost for good.

**Proposed fix (small):** Update the live playlist objects instead of loading and saving a separate copy. Have ApplyAsync/UndoLastAsync return (or raise) the remap. On the UI thread, call a new SidebarViewModel.ApplyTrackIdRemap(remap) that rewrites TrackIds in place on the existing Playlist objects, which keeps object identity for any open PlaylistViewModel. It should then rebuild the nav items and save once with SavePlaylistsAsync(Playlists.ToList()). Remove the separate load/save in RemapPlaylistsAsync.

**Verifier votes**

- **confirmed** (refute lens): FileOrganizerService.cs:155-178 loads playlists.json into a separate list, remaps TrackIds and saves it. RunAsync (:137-141) calls it after RelocateTracksAsync, which changes track.Id to ComputeFileId(newPath) (LibraryService.cs:1377-1383). Nothing updates SidebarViewModel.Playlists in memory. The sidebar subscribes to LibraryUpdated only for RefreshFavoritesCount (SidebarViewModel.cs:91). Sidebar.LoadPlaylistsAsync is called only at MainWindowViewModel.cs:314/:684, PlaylistImportViewModel.cs:199 and the server adapter callback (SettingsViewModel.cs:1048). OrganizeFilesViewModel.Apply/UndoLast (:97, :114) and MetadataHelper.OpenOrganizeFilesDialog (:47-55) never reload. GetTrackById (LibraryService.cs:1239-1243) is a plain index lookup with no alias for old IDs, so PlaylistViewModel.LoadTracks (:359) drops moved songs. Every later playlist edit writes the stale in-memory list back: SidebarViewModel.cs has 14 SavePlaylistsAsync(Playlists.ToList()) sites, PlaylistViewModel.cs :87 (even a sort-mode change), :686, :711, :744 and :961, and LibraryPlaylistsViewModel.cs :348 and :373. That permanently overwrites the remap.
- **confirmed** (impact lens): FileOrganizerService.cs:155-177 remaps a fresh LoadPlaylistsAsync() copy and saves it to disk. Nothing reloads SidebarViewModel.Playlists afterwards. The only Sidebar.LoadPlaylistsAsync callers are MainWindowViewModel.cs:314/684, PlaylistImportViewModel.cs:199 and the server adapter (SettingsViewModel.cs:1048). The organize dialog opener (MetadataHelper.cs:47-55) and OrganizeFilesViewModel.Apply/UndoLast (:85-117) do not call it. RelocateTracksAsync changes track.Id to MD5(newPath) and rebuilds the index (LibraryService.cs:1377-1390), so PlaylistViewModel's GetTrackById(oldId) (PlaylistViewModel.cs:359) returns null. Any later sidebar or playlist save, such as TogglePinAsync at SidebarViewModel.cs:318 among 23 SavePlaylistsAsync sites, writes Playlists.ToList() with the stale IDs over the remapped file. UndoLast runs through the same RunAsync path.

### S07 — Right-click actions on a row or tile outside the Ctrl-selection act on the selection instead: Remove from Library, which can send files to the Recycle Bin, and Remove from Playlist hit the wrong items

- **Location:** `src/Noctis/ViewModels/LibrarySongsViewModel.cs:343` (also: `src/Noctis/Views/LibrarySongsView.axaml.cs:173`, `src/Noctis/ViewModels/LibrarySongsViewModel.cs:335`, `src/Noctis/ViewModels/LibrarySongsViewModel.cs:367`, `src/Noctis/ViewModels/LibrarySongsViewModel.cs:375`, `src/Noctis/ViewModels/LibrarySongsViewModel.cs:383`, `src/Noctis/Views/PlaylistView.axaml.cs:550`, `src/Noctis/ViewModels/PlaylistViewModel.cs:668`, `src/Noctis/ViewModels/PlaylistViewModel.cs:800`, `src/Noctis/ViewModels/PlaylistViewModel.cs:823`, `src/Noctis/ViewModels/PlaylistViewModel.cs:831`, `src/Noctis/ViewModels/PlaylistViewModel.cs:839`, `src/Noctis/Views/LibraryAlbumsView.axaml.cs:162`, `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs:1100`, `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs:1111`, `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs:1191`, `src/Noctis/Views/FavoritesView.axaml.cs:95`, `src/Noctis/ViewModels/FavoritesViewModel.cs:469`, `src/Noctis/ViewModels/FavoritesViewModel.cs:486`, `src/Noctis/ViewModels/FavoritesViewModel.cs:576`, `src/Noctis/Views/HomeView.axaml.cs:200`, `src/Noctis/ViewModels/HomeViewModel.cs:957`, `src/Noctis/ViewModels/HomeViewModel.cs:979`, `src/Noctis/ViewModels/HomeViewModel.cs:1038`, `src/Noctis/Views/AlbumDetailView.axaml.cs:297`, `src/Noctis/ViewModels/AlbumDetailViewModel.cs:859`, `src/Noctis/ViewModels/AlbumDetailViewModel.cs:867`, `src/Noctis/Helpers/AlbumTile.cs:30`)
- **Area / sweep:** Settings, dialogs, popups, commands / commands · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: high)

**Evidence**

```
LibrarySongsView.axaml.cs:172-173 (and :192-193 for the row '...' button):
    if (DataContext is LibrarySongsViewModel vm)
        vm.CtrlSelectedTracks = _selectedTracks.ToList();
LibrarySongsViewModel.cs:341-344:
    private async Task RemoveFromLibrary(Track track)
    {
        var tracks = CtrlSelectedTracks.Count > 0 ? CtrlSelectedTracks.ToList() : new List<Track> { track };
        if (!await Helpers.LibraryRemovalHelper.RemoveWithPromptAsync(_library, tracks))
The same file already has the correct rule at :405-406, used only by Rate, Lyrics and Send to Folder:
    private List<Track> SelectionOr(Track track) =>
        CtrlSelectedTracks.Count > 0 && CtrlSelectedTracks.Contains(track) ? CtrlSelectedTracks.ToList() : new List<Track> { track };
```

**Why it is a bug:** MultiSelectHelper.HandleTrackRowClickByData (Helpers/MultiSelectHelper.cs:209-210) and HandleAlbumTileClickByData (:300-301) return early on a right-click, so the Ctrl-selection stays in place. Every ContextRequested or Opening handler then copies the whole selection into the VM, whether or not the clicked item is part of it. The older commands use 'selection if non-empty, else the clicked item'. As a result, one menu does two different things: Rate, Badge, Lyrics and Send to Folder act on the clicked row, while Remove, Favorites, Add to Playlist, Convert and Scan ReplayGain act on the other rows. The menu header still says 'Remove from Library' for the clicked row (TrackContextMenuBuilder.cs:223). The dialog shows only a count ('Remove N tracks from your library?', RemoveFromLibraryDialog.axaml.cs:59). The album tile hover '...' button (Helpers/AlbumTile.cs:28-31) calls menu.Open(tile) directly. In Avalonia 12.1.2, ContextMenu.Opening is raised only from ControlContextRequested and not from a programmatic Open() (https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/ContextMenu.cs, CancelOpening). So on the Albums grid and on Favorites, the '...' path keeps whatever CtrlSelectedAlbums/CtrlSelectedItems the last right-click left behind. Those lists are not cleared by a plain click or by Ctrl-deselecting, and they survive a cancelled prompt. Scenario: on Songs, Ctrl-click A and B, then right-click C, choose 'Remove from Library' and then 'Move to Recycle Bin'. A and B are removed and their files trashed; C stays. Same on the Albums grid (LibraryAlbumsView.axaml.cs:162 into LibraryAlbumsViewModel.cs:1191) with whole albums.

**Impact:** The wrong tracks or albums are removed from the library (play counts, favorites, ratings and playlist membership are lost) and their files are moved to the Recycle Bin. The wrong songs are removed from a playlist, or favorited/unfavorited. The item the user clicked is left unchanged.

**Proposed fix (small):** Apply the SelectionOr rule everywhere. In each view's menu-open sync, push the selection only when it contains the clicked item, else push an empty list: LibrarySongsView.axaml.cs:173/193, PlaylistView.axaml.cs:437/550, AlbumDetailView.axaml.cs:297/317, LibraryAlbumsView.axaml.cs:162 (resolve the album from the menu's parent tile), FavoritesView.axaml.cs:95 and HomeView.axaml.cs:200. Alternatively, replace the `Count > 0 ? selection : single` pattern with SelectionOr(x) in the listed commands. In AlbumTile.OpenMenu, raise ContextRequested (as the Home path already does) instead of calling menu.Open, so Opening runs and syncs the selection.

**Verifier votes**

- **confirmed** (refute lens): Verified on the main right-click path. HandleTrackRowClickByData returns early unless the left button is pressed (MultiSelectHelper.cs:209-210), and HandleAlbumTileClickByData does the same (:300-301), so a right-click leaves the Ctrl-selection in place. The Songs view pushes the whole selection before opening the menu at LibrarySongsView.axaml.cs:173 (right-click) and :193 (options button). The Playlist view does the same at PlaylistView.axaml.cs:437/550, the Albums grid on Opening at LibraryAlbumsView.axaml.cs:162, Favorites at FavoritesView.axaml.cs:95 and Home at HomeView.axaml.cs:200. RemoveFromLibrary then uses `CtrlSelectedTracks.Count > 0 ? selection : {track}` without checking that the clicked item is in the selection (LibrarySongsViewModel.cs:343). The same pattern is at :335/:367/:375/:383, PlaylistViewModel.cs:668/800/823/831/839 and LibraryAlbumsViewModel.cs:1100/1111/1191. SelectionOr (LibrarySongsViewModel.cs:405-406, PlaylistViewModel.cs:599-600) is used only by Rate, Badge, Lyrics and Send to Folder. LibraryRemovalHelper.cs:24-42 removes the tracks and can trash their files. The dialog shows only a count (RemoveFromLibraryDialog.axaml.cs:59); that is a weak guard and does not prevent the error. The sub-claim that AlbumTile.OpenMenu (AlbumTile.cs:27-30) skips the Opening event, leaving CtrlSelectedAlbums stale, depends on Avalonia internals I did not check here, but the main right-click scenario does not depend on it. I keep high because the wrong items are destroyed (library state, and optionally files) even though the path is not the most common one.
- **confirmed** (impact lens): HandleTrackRowClickByData returns early for a non-left click (MultiSelectHelper.cs:208-209), so a right-click keeps the Ctrl-selection. LibrarySongsView.axaml.cs:173/193 then pushes _selectedTracks unconditionally, and RemoveFromLibrary at LibrarySongsViewModel.cs:341-346 uses 'Count > 0 ? selection : track'. The menu binds Remove.CommandParameter = the clicked track (TrackContextMenuBuilder.cs:425-426), but the command ignores it when a selection exists. The prompt shows only a count (LibraryRemovalHelper.cs:24), then calls RemoveTracksAsync and, if chosen, trashes the files. SelectionOr at :405-406 shows the intended rule. The Avalonia claim checks out: in the 12.1.2 ContextMenu.cs, Opening is raised only through CancelOpening() from ControlContextRequested, and programmatic Open() does not raise it. So AlbumTile.OpenMenu (AlbumTile.cs:28-31) skips OnAlbumContextMenuOpening, and vm.CtrlSelectedAlbums keeps its old value, because a plain click clears only _selectedAlbums (LibraryAlbumsView.axaml.cs:43-49), and only Escape or :234 clears the VM copy. The count in the dialog is a partial safeguard, so this stays high, not critical.

### S14 — Metadata editor 'Rename files by pattern' moves the files without re-keying the track (skips RelocateTracksAsync): duplicate rows right away, then lost play counts, favorites and playlist membership

- **Location:** `src/Noctis/ViewModels/MetadataViewModel.cs:2610` (also: `src/Noctis/Services/FileOrganizerService.cs:125`, `src/Noctis.Core/Services/LibraryService.cs:1351`, `src/Noctis.Core/Services/LibraryService.cs:2867`, `src/Noctis.Core/Services/LibraryWatcherService.cs:201`, `src/Noctis.Core/Services/WatchDebouncer.cs:56`, `src/Noctis.Core/Services/LibraryService.cs:1136`)
- **Area / sweep:** Settings, dialogs, popups, commands / dialogs · **Category:** data-loss · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: high)
- **Needs runtime check:** yes

**Evidence**

```
// MetadataViewModel.cs:2600-2616
if (_multiSelect && ApplyRename && _albumTracks != null)
{ ...
        File.Move(t.FilePath, newPath);
        MoveLyricSidecars(t.FilePath, newPath);
        t.FilePath = newPath;
    }
    catch { /* Non-fatal — skip this file */ }
// LibraryService.cs:298  if (trackIndexSnapshot.TryGetValue(ComputeFileId(filePath), out existing))
// LibraryService.cs:1136 var trackId = ComputeFileId(filePath);
```

**Why it is a bug:** A track's identity is the MD5 of its path (LibraryService.cs:2867). The file organizer does the same kind of move and handles it properly: it suppresses the watcher (FileOrganizerService.cs:125), calls ILibraryService.RelocateTracksAsync to re-key the Id and move store-backed lyrics (LibraryService.cs:1351-1394), and remaps playlists. The multi-select rename does none of this. The Track keeps Id = MD5(oldPath) while FilePath becomes newPath. Scenario: the user selects 12 songs, ticks Rename and saves. (a) Folder watching is on by default (AppSettings.cs:95). The rename is recorded as a delete of the old path plus a create of the new one (WatchDebouncer.cs:56-60). The delete matches no track, because removal is by FilePath and FilePath has already changed (LibraryWatcherService.cs:453-455). ImportFilesAsync then looks up MD5(newPath) (LibraryService.cs:1136-1137), finds nothing and adds a fresh Track, so every renamed song appears twice. (b) The next full scan keys tracks by MD5(path) (LibraryService.cs:298-324). The old entry, which carries PlayCount, Rating, IsFavorite, DateAdded and the Id that playlists reference, is not carried over and is dropped. Store-backed lyrics stay filed under the old Id. On Windows the rename of the playing file fails because the player holds it open, and that failure is swallowed.

**Impact:** Every renamed song shows up twice right after the save. After the next scan, play counts, ratings, favorites, date added, playlist membership and store-backed lyrics are lost for those songs.

**Proposed fix (medium):** Collect (oldPath, newPath) for each move that succeeds. Suppress the watcher for both paths before File.Move, as FileOrganizerService does. Then call `var remap = await _library.RelocateTracksAsync(moves)` and apply the remap to the in-memory playlists (same helper as the Organize fix). Report failed renames through SaveErrorMessage instead of swallowing them.

**Verifier votes**

- **confirmed** (refute lens): At MetadataViewModel.cs:2600-2616 the rename does File.Move, moves the sidecars and sets `t.FilePath = newPath`. It keeps the old Id and never calls RelocateTracksAsync. It also does no watcher suppression; nothing in the file matches Suppress or RelocateTracks. By contrast, FileOrganizerService.cs:125-141 calls SuppressForMove, then RelocateTracksAsync, then RemapPlaylistsAsync. Track identity is MD5 of the path (LibraryService.cs:2867). Folder watching is on by default (AppSettings.cs:95). The watcher's OnRenamed (LibraryWatcherService.cs:201-220) goes to RecordRename, which records a delete plus a create (WatchDebouncer.cs:56-60). The removal matches tracks by FilePath (LibraryWatcherService.cs:453-455), and FilePath is already the new path, so no track is removed. ImportFilesAsync looks up ComputeFileId(newPath) (LibraryService.cs:1136-1137), finds nothing, and adds a fresh track (:1162-1185), so each renamed song shows up twice. A full scan keys tracks by MD5(path) (:298) and rebuilds from what it enumerates, so the old-Id row and its user state are dropped. Failures are swallowed at :2615. The feature is opt-in (multi-select plus the Rename checkbox), so high rather than critical.
- **confirmed** (impact lens): The rename path is real. MetadataViewModel.cs:2600-2614 does File.Move, then sets t.FilePath = newPath, and never calls RelocateTracksAsync, so the Id stays MD5(oldPath) (LibraryService.cs:2867). The only callers of watcher suppression and RelocateTracksAsync are in FileOrganizerService.cs:125/139. With WatchFoldersEnabled=true by default (AppSettings.cs:94), OnRenamed goes to RecordRename, which records a delete of the old path and an import of the new one (WatchDebouncer.cs:56-60). ApplyBatchAsync removes by t.FilePath (LibraryWatcherService.cs:453-455), and FilePath is already newPath, so nothing is removed. ImportFilesAsync looks up MD5(newPath) (LibraryService.cs:1136-1137), misses, and adds a second Track. The next scan keys on ComputeFileId(path) (LibraryService.cs:298), so the old-Id row with the user state is dropped. The user-state journal is keyed by Id (LibraryService.cs:1620-1626), so nothing recovers it. The path is opt-in (multi-select plus the Rename checkbox), which keeps this at high rather than critical.

### X04 — Windows: the built-in Noctis Server's HTTPS cannot complete a TLS handshake because the certificate key is loaded with EphemeralKeySet

- **Location:** `src/Noctis.Core.Server/Services/Server/ServerCertificate.cs:20` (also: `src/Noctis.Core.Server/Services/Server/ServerCertificate.cs:35`, `src/Noctis.Core.Server/Services/Server/ServerCertificate.cs:75`, `src/Noctis.Core.Server/Services/Server/NoctisServer.cs:72`, `src/Noctis.Core.Server/Services/Server/NoctisServer.cs:65`, `src/Noctis/ViewModels/SettingsViewModel.cs:1055`)
- **Area / sweep:** Security / sec-net · **Category:** security-tls · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: high)
- **Needs runtime check:** yes

**Evidence**

```
ServerCertificate.cs:20-22
    private static X509KeyStorageFlags KeyFlags => OperatingSystem.IsMacOS()
        ? X509KeyStorageFlags.Exportable
        : X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet;
:35-36  var cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, File.ReadAllText(keyFile).Trim(), KeyFlags);
:75     return X509CertificateLoader.LoadPkcs12(pfx, null, KeyFlags);
NoctisServer.cs:72  if (certificate is not null) listen.UseHttps(certificate);
SettingsViewModel.cs:1055-1058 always loads the certificate this way and passes it to StartAsync.
```

**Why it is a bug:** Kestrel's HTTPS runs on SslStream, and on Windows SslStream uses Schannel. Schannel cannot use an ephemeral (in-process) private key for a server credential. The .NET 10 source handles this case directly. SslStreamPal.Windows.cs AcquireCredentialsHandle (dotnet/runtime release/10.0) catches SEC_E_NO_CREDENTIALS, has the comment "on Windows we do not support ephemeral keys", and throws AuthenticationException(SR.net_auth_ephemeral = "Authentication failed because the platform does not support ephemeral keys."). Microsoft documents this at https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-troubleshooting#handshake-failed-with-ephemeral-keys. The runtime issues are https://github.com/dotnet/runtime/issues/23749, #103101 (closed as not actionable) and #114640 (still open). The macOS branch already drops the flag; Windows does not. The server tests start only with certificate: null (tests/Noctis.Tests/NoctisServerTests.cs:60), so HTTPS is never exercised. Scenario: on Windows, a user turns on Settings → Noctis Server and points the phone at https://192.168.1.20:4747 from the QR code. Kestrel accepts the TCP connection, but the TLS handshake fails every time. Logging is cleared (NoctisServer.cs:65: builder.Logging.ClearProviders()), so nothing is recorded, and the Settings card still shows the URL, fingerprint and QR as if the server were running.

**Impact:** The built-in OpenSubsonic/sync server does nothing on Windows: no client (Android app or Subsonic app) can connect, and the user gets no error. Linux (OpenSSL) and macOS are not affected.

**Proposed fix (small):** Load the PFX without EphemeralKeySet on Windows, for example `OperatingSystem.IsWindows() ? X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet : ...`. This persists the per-user key, which is the documented workaround. Add a Windows test that runs a real HTTPS handshake against StartAsync(0, ServerCertificate.Create()).

**Verifier votes**

- **confirmed** (refute lens): ServerCertificate.cs:20-22 adds EphemeralKeySet on every OS except macOS. Both load paths use it: :35-36 (stored PFX) and :75 (fresh cert). SettingsViewModel.cs:1055-1058 and Program.cs:148-149 pass that cert to NoctisServer.StartAsync, and NoctisServer.cs:72 calls listen.UseHttps(certificate) with it unchanged. Kestrel's UseHttps(X509Certificate2) does not persist the key; ASP.NET Core's own PersistKey workaround applies only to PEM config loading. Evidence that .NET 10 on Windows still rejects ephemeral keys: the installed 10.0.12 System.Net.Security.dll contains the 'platform does not support ephemeral keys' resource, the UTF-16 usage string net_auth_ephemeral, and a HasEphemeral metadata name, which matches the AcquireCredentialsHandle NoCredentials to net_auth_ephemeral path. Schannel runs in LSASS and cannot open an in-process ephemeral key. Commit 07c62ec8 dropped the flag only for macOS. The only test (NoctisServerTests.cs:301-307) checks fingerprint stability and never does a TLS handshake. Logging is cleared (NoctisServer.cs:65), so the failure is silent. Not observed at runtime here, but the code and runtime evidence are strong. High severity: the feature does not work on the app's main platform.
- **confirmed** (impact lens): The code matches the finding. ServerCertificate.cs:20-22 adds EphemeralKeySet on every platform except macOS, and both load paths use it: :35 (LoadPkcs12FromFile) and :75 (the Create() round-trip, whose comment at :72 names the right workaround but the flag defeats it). The Windows path is reachable. SettingsViewModel.cs:1055-1058 always loads the certificate and passes it to StartAsync, with no OS gate. The switch is TurnOnNoctisServer (SettingsViewModel.Features.cs:142) and the pairing card is in SettingsView.axaml:4469. NoctisServer.cs:72 calls listen.UseHttps(certificate) directly. That skips Kestrel's config-loader persist workaround, which only applies to endpoints defined in configuration. The library claim is true. In dotnet/runtime release/10.0, SslStreamPal.Windows.cs AcquireCredentialsHandle catches NoCredentials, has the comment '// on Windows we do not support ephemeral keys.' and throws net_auth_ephemeral; there is no fallback. The Microsoft Learn SslStream troubleshooting page documents 0x8009030E with ephemeral keys on Windows. Issue #23749 is closed as tracking-external-issue. Credentials are acquired at the first handshake, not at app.StartAsync (NoctisServer.cs:78), so the server starts and the UI shows the URL and QR (SettingsViewModel.cs:1060-1064). Every TLS handshake then fails, and logging is cleared at NoctisServer.cs:65. The failure is user-visible and total for this opt-in feature on the main desktop platform, so high is right.

### X07 — 'Move to Recycle Bin' permanently deletes files on drives without a Recycle Bin: FOF_NOCONFIRMATION set without FOF_WANTNUKEWARNING

- **Location:** `src/Noctis/Helpers/RecycleBin.cs:71` (also: `src/Noctis/Helpers/LibraryRemovalHelper.cs:73`, `src/Noctis/Services/DuplicateFinderService.cs:46`, `src/Noctis/Services/Lyrics/LyricsWriter.cs:150`, `src/Noctis/ViewModels/MetadataViewModel.cs:2475`, `src/Noctis/Services/FileOrganizerService.cs:267`)
- **Area / sweep:** Security / sec-files · **Category:** data-loss · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: high)
- **Needs runtime check:** yes

**Evidence**

```
RecycleBin.cs:63-73
    private static bool WindowsRecycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
(class doc, lines 10-12: 'this never permanently deletes as a fallback')
```

**Why it is a bug:** FOF_ALLOWUNDO only 'preserve[s] undo information, if possible'. FOF_NOCONFIRMATION means 'Respond with Yes to All for any dialog box that is displayed', and that includes the shell's 'this file can't be recycled / is too big, permanently delete?' prompt. Only FOF_WANTNUKEWARNING, which 'partially overrides FOF_NOCONFIRMATION', stops the silent permanent delete (https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-shfileopstructw; same bug reported at https://github.com/FreakySneaky787/Simple-Organizer/issues/7). Because SHFileOperation returns 0 in that case, TryMoveToTrash and TryMoveDirectoryToTrash report success.

Scenario: a user whose library folder is a mapped NAS share (Z:\Music) or a UNC path, which is SourceType.Local, picks Remove from Library -> 'Move to Recycle Bin' (Strings.resx RemoveFromLibrary.MoveRecycleBin). LibraryRemovalHelper.TrashLocalFilesCoreAsync first tries TryMoveDirectoryToTrash on the whole album folder. The folder, its audio and its cover images are destroyed and nothing appears in the Recycle Bin. The same thing happens on removable USB sticks and with files larger than the bin quota.

**Impact:** Irreversible loss of music files, album folders and lyric sidecars when the UI promised a recoverable delete. The same helper is used by the duplicate finder, the lyrics sidecar replacement (LyricsWriter), the metadata editor's sidecar removal and the organizer's empty-folder cleanup.

**Proposed fix (trivial):** Add FOF_WANTNUKEWARNING (0x4000) to fFlags so the shell asks before a permanent delete. A 'No' answer sets fAnyOperationsAborted, so TryMoveToTrash returns false and the existing caller paths report the failure. Better still, refuse up front for UNC, Network and Removable drives (DriveInfo.DriveType) and return false, or switch to IFileOperation with FOFX_RECYCLEONDELETE plus FOF_WANTNUKEWARNING.

**Verifier votes**

- **confirmed** (refute lens): RecycleBin.cs:71 sets FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI and has no FOF_WANTNUKEWARNING (only the four constants at :76-80 are defined). Under the documented SHFileOperation semantics, a volume without a Recycle Bin (UNC or mapped network share, removable media) or an item over the bin quota makes the shell ask 'permanently delete?'. FOF_NOCONFIRMATION answers Yes, the call returns 0 with fAnyOperationsAborted=false, and :73 reports success. This contradicts the class doc at :10-12, which says the helper never permanently deletes. LibraryRemovalHelper.SelectTrashablePaths (:377-382) filters only on SourceType.Local, with no DriveType or UNC check, and :73 passes TryMoveDirectoryToTrash for whole-album folder trashing. A library on a mapped NAS drive therefore gets its folders permanently deleted even though the user chose 'Move to Recycle Bin'.
- **confirmed** (impact lens): The code matches the finding. RecycleBin.cs:71 sets FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI with no FOF_WANTNUKEWARNING (only the constants at :76-80 exist). :73 treats a return of 0 with no abort as success. The SHFILEOPSTRUCTW docs back the finding. FOF_ALLOWUNDO only preserves undo 'if possible'. FOF_NOCONFIRMATION answers 'Yes to All' to every dialog, including the permanently-delete prompt. FOF_WANTNUKEWARNING 'partially overrides FOF_NOCONFIRMATION' so a permanent delete raises a warning. A volume with no Recycle Bin (mapped share, UNC, removable drive) or a file over the bin quota is therefore deleted permanently and reported as trashed. The path is reachable. LibraryRemovalHelper.cs:379 selects only by SourceType.Local, with no drive-type or UNC check anywhere on the trash path. The app already expects Local tracks on network drives: WaveformService.cs:37 checks DriveType.Network for local files. So a NAS library at Z:\ goes into TrashLocalFilesCoreAsync (:73, :90), where the whole album folder is tried first. The file's own doc comment (:10-12) promises the opposite. This contradicts the class's own contract and the UI's 'Move to Recycle Bin' label, and the files cannot be recovered. It needs a non-default storage setup, but a NAS or USB library is common for music collections, so high is right (not critical, because the user did choose to delete).

## Medium (70)

### A01 — The engine's sample rate and channel count are fixed from the startup device for the whole session, so a mono or low-rate startup endpoint degrades all playback even after moving to a stereo device

- **Location:** `src/Noctis/Services/GaplessSink.cs:81` (also: `src/Noctis/Services/VlcAudioPlayer.cs:693`, `src/Noctis/Services/GaplessSink.cs:91`)
- **Area / sweep:** Audio pipeline / audio-output · **Category:** device-change · **Platforms:** windows
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
GaplessSink.cs:81-83
    var mix = device.AudioClient.MixFormat;
    SampleRate = Math.Clamp(mix.SampleRate, 8000, 384000);
    Channels = mix.Channels >= 2 ? 2 : 1;
GaplessSink.cs:26-28 (header): 'rebuild ... reusing the same provider chain (... so the VLC-facing format stays fixed)'
VlcAudioPlayer.cs:693 (only call, at construction):
    _enginePlayers[s].SetAudioFormat("S16N", (uint)_gaplessSink.SampleRate, (uint)_gaplessSink.Channels);
```

**Why it is a bug:** The engine format (the provider format and the amem format VLC is told to deliver) is read once from the default device when the app starts. RebuildLoop deliberately keeps it after a device change, and nothing ever calls SetAudioFormat again. If the startup default is a Bluetooth hands-free 'Headset' endpoint (mono, 8 or 16 kHz), a mono USB speakerphone or any mono endpoint, the engine runs at that rate and mono for the entire session. VLC downmixes and resamples every track to it, and moving to stereo headphones or speakers only upsamples an already degraded stream (WASAPI AUTOCONVERTPCM just matrixes and resamples: https://learn.microsoft.com/en-us/windows/win32/coreaudio/audclnt-streamflags-xxx-constants). A stereo, full-rate engine format would lose nothing on a mono device, because the shared engine's channel matrixer downmixes it.

**Impact:** After launching while such an endpoint is default, all music plays in mono, and at telephone bandwidth for 8 or 16 kHz endpoints, until the app restarts, even after switching to a stereo device.

**Proposed fix (small):** Pick a device-independent engine format: always 2 channels, and a sample rate of max(mix rate, 44100), or 48000 when the mix rate is below 44.1 kHz. Shared-mode AUTOCONVERTPCM, which NAudio already passes, handles mono and low-rate endpoints. The upmix check `deviceChannels > Channels` stays correct.

**Verifier votes**

- **confirmed** (refute lens): GaplessSink.cs:80-83 reads the Multimedia default's MixFormat once in the private ctor, and SampleRate/Channels (54-55) are get-only, so nothing can change them later. RebuildLoop (259-324) reuses the same Provider and never re-reads the format; the header comment at 26-28 says this is on purpose. The only engine SetAudioFormat call is VlcAudioPlayer.cs:693, at construction. The other SetAudioFormat calls (617, 1739, 1897) belong to the classic and exclusive sinks. OnGaplessSinkRebuilt (VlcAudioPlayer.cs:4067) only re-applies session volume. So if the app starts while the default endpoint is mono or low-rate (for example a Bluetooth hands-free endpoint), the whole session stays downmixed and resampled to that format. Medium fits because the path is uncommon.

### A03 — The gapless engine keeps a WASAPI render stream running forever, including when paused or stopped, so the OS 'audio stream in use' power request never clears and idle sleep is blocked

- **Location:** `src/Noctis/Services/GaplessSink.cs:132` (also: `src/Noctis/Services/GaplessSink.cs:326`, `src/Noctis/Services/VlcAudioPlayer.cs:4321`, `src/Noctis/Services/WasapiSilenceKeepAlive.cs:28`)
- **Area / sweep:** Audio pipeline / audio-core · **Category:** power-resource · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
GaplessSink.cs:128-132
    _out = CreateOutput();
    // Render immediately and forever: the provider always returns full
    // buffers (silence when idle), so the stream never stops between
    // tracks — the property true gapless depends on.
    _out.Play();
GaplessSink.cs:326-332  public void Pause() { _desiredPlaying = false; ... current.Pause(); }
VlcAudioPlayer.cs:4340-4351 (Stop(): EngineClearAll, ReleasePreparedNext, _player.Stop() — sink untouched)
WasapiSilenceKeepAlive.cs:28-30: "A running stream holds the OS 'audio stream is in use' power request, which would otherwise block laptop auto-sleep while Noctis sits idle — parking releases it."
```

**Why it is a bug:** NAudio's WasapiOut.Pause() only sets playbackState = Paused and never stops the IAudioClient (https://github.com/naudio/NAudio/blob/master/NAudio.Wasapi/WasapiOut.cs; the BUZZ notes IL-verified the same). Stop() never touches the sink. The engine stream is therefore active for the whole process lifetime. Windows keeps a SYSTEM power request while any render stream is active, even a silent one (https://github.com/niklam/iracedeck/issues/849, https://veg.by/en/blog/2022/07/28/pc-auto-sleep-with-audio/). The app already built a 10-minute idle park into WasapiSilenceKeepAlive for exactly this reason, but the engine sink, on by default on Windows, bypasses it.

**Impact:** While Noctis is open, whether idle, paused or stopped, the PC or laptop never auto-sleeps; `powercfg /requests` shows an audio stream in use. This drains battery on laptops left with the app open.

**Proposed fix (medium):** Mirror the keep-alive park: after N minutes with the sink paused, or with no active or pending segment, fully Stop() the WasapiOut (which stops the AudioClient). On Resume/Play/Enqueue, restart it through the existing Play path; RebuildLoop already shows how to recreate. At minimum, stop the stream when VlcAudioPlayer.Stop() runs or the pause lasts long.

**Verifier votes**

- **confirmed** (refute lens): GaplessSink.cs:128-132 calls _out.Play() once, 'render immediately and forever'. The engine is on by default on Windows (VlcAudioPlayer.cs:666-667). Pause (GaplessSink.cs:326-332) calls WasapiOut.Pause(). I dumped the IL of NAudio.Wasapi 2.3.0 WasapiOut.Pause from the nuget cache: it only moves playbackState from 1 to 2 (Playing to Paused). It never calls Stop or joins the thread, so the IAudioClient keeps running. VlcAudioPlayer.Stop (4321-4357) never touches the sink, and no idle park exists for it: the sink's only Pause call is at 4256. WasapiSilenceKeepAlive.cs:26-30 itself says a running stream holds the 'audio stream in use' power request, and that class parks after 10 min for exactly that reason. The engine sink skips that park. Windows' power-request behaviour is external, but the repo itself documents it.

### A05 — After a device switch the new output starts playing before the user volume is set, so the new session can briefly render at full or stale volume

- **Location:** `src/Noctis/Services/GaplessSink.cs:287` (also: `src/Noctis/Services/VlcAudioPlayer.cs:4067`, `src/Noctis/Services/GaplessSink.cs:58`)
- **Area / sweep:** Audio pipeline / audio-output · **Category:** volume · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
GaplessSink.cs:286-302
    _probe.RearmBoost();
    if (_desiredPlaying) newOut.Play();
    lock (_gate)
    { ... _out = newOut; _deviceId = id; }
    DebugLogger.Info(... "GaplessEngine.SinkRebuilt" ...);
    try { Rebuilt?.Invoke(); } catch { }
VlcAudioPlayer.cs:4067-4076
    private void OnGaplessSinkRebuilt()
    { ... ThreadPool.QueueUserWorkItem(_ => { _sessionVolume.Invalidate();
        for (...) { if (ReapplySessionVolume() && _sessionVolume.HoldsActiveSession) return; ...
```

**Why it is a bug:** The engine applies the user's volume only through the process's audio session (ISimpleAudioVolume). A rebuilt stream on another endpoint joins a new session on that device, and its level is Windows' persisted per-app level for that device, or 1.0 on first use ('By default, the volume level and muting state for a rendering session are persistent across application restarts', https://learn.microsoft.com/en-us/windows/win32/coreaudio/audclnt-streamflags-xxx-constants). The code's own comment at GaplessSink.cs:58-61 says the new session opens at 'Windows' default level ... or playback jumps to 100%'. newOut.Play() runs the first FillBuffer, a full 100 ms of ring audio, and Start() before Rebuilt is raised. The reassert then waits for a ThreadPool hop plus a full COM session enumeration (Resolve) before SetMasterVolume lands, so the first mixed periods play at the new session's initial level.

**Impact:** When the output switches to a device (plugging in headphones, Bluetooth connecting, changing the default), a short burst at up to 100% session volume can play. At a slider of 18% the level is about 0.027, so the burst is roughly +31 dB louder, directly into headphones.

**Proposed fix (small):** Hold the new output silent until the level is confirmed. Before newOut.Play() in RebuildLoop, set a hold flag on MuteGateProvider (gain 0 without consuming differently), then raise Rebuilt. Have OnGaplessSinkRebuilt clear the hold after ReapplySessionVolume() succeeds on the active session, and clear it on a ~1.5 s timeout. Alternatively, set the level on the new AudioClient's session (GetService ISimpleAudioVolume after Init) before Play().

**Verifier votes**

- **confirmed** (refute lens): GaplessSink.cs:286-302: `if (_desiredPlaying) newOut.Play();` (287) runs before the swap (297) and before `Rebuilt?.Invoke()` (302). Nothing in the render chain applies gain: MuteGateProvider only handles mute (GaplessSink.cs:96-101), and the user volume goes only through the process session. VlcAudioPlayer.cs:4067-4080 queues a ThreadPool worker that runs Invalidate, then SetLevel, then a full Resolve (WindowsSessionVolume.cs:122-226: COM session enumeration). So the first render periods after audioClient.Start mix at the new session's initial level, which is the persisted per-device level or 1.0 on first use. The code's own comments at GaplessSink.cs:57-61 and VlcAudioPlayer.cs:4061-4065 describe that initial level as a '100% blip'. The blip is short (roughly one to a few 10 ms periods) and only happens when the device switches, so medium is right, not critical.

### A07 — Engine pause and resume cut the waveform with no ramp, so every pause and resume clicks by default

- **Location:** `src/Noctis/Services/GaplessSink.cs:326` (also: `src/Noctis/Services/VlcAudioPlayer.cs:4252`, `src/Noctis/Services/VlcAudioPlayer.cs:4305`, `src/Noctis/Services/GaplessSpliceCore.cs:590`, `src/Noctis/Services/GaplessSpliceCore.cs:637`, `src/Noctis.Core/Models/AppSettings.cs:152`)
- **Area / sweep:** Audio pipeline / audio-output · **Category:** audio-glitch · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: high)
- **Needs runtime check:** yes

**Evidence**

```
GaplessSink.cs:326-331
    public void Pause()
    {
        _desiredPlaying = false;
        WasapiOut current;
        lock (_gate) current = _out;
        try { current.Pause(); } catch { /* device transitional */ }

VlcAudioPlayer.cs:4252-4256 (pauseOutput): `_player.Pause(); if (_gaplessEngine) _gaplessSink?.Pause();` and 4304-4305: `if (_gaplessEngine) _gaplessSink?.Resume();`. The fade path is skipped by default because AppSettings.cs:152 `public bool PlayPauseFadeEnabled { get; set; }` defaults to false (RunFadedPause at 4215-4217: `if (fade) fadeOut(); pauseOutput(); if (!fade) return;`).
```

**Why it is a bug:** NAudio's WasapiOut.Pause() only sets playbackState = Paused. It never stops or ramps the IAudioClient (https://github.com/naudio/NAudio/blob/master/NAudio.Wasapi/WasapiOut.cs; the IL-verified facts in BUZZ_INVESTIGATION.md say: 'Pause() never touches the AudioClient ... the device keeps draining then starves to silence; resume triggers ONE full-buffer read'). While paused, the fill loop stops calling Read, so GaplessSpliceProvider never sees the pause. Its declick ramp (GaplessSpliceCore.cs:637-648) and its fade-in (590-594) only engage on reads and silence streaks, so neither edge gets a ramp. Pause: the shared engine plays the ~100 ms queued tail, then inserts silence, which is a step from a mid-waveform sample to 0. Resume: Play() makes the loop read a whole buffer that continues the ring at full amplitude. _silentSamples is still 0 because no reads happened, so no fade is armed and the output steps from 0 back to mid-waveform. The codebase already treats this as a defect elsewhere: WasapiGainOutput.Pause (284-303) parks with a 15 ms slew for exactly this reason, and GaplessSpliceCore.cs:632-635 notes that 'an instant step to zero is an audible click'.

**Impact:** On the default Windows path with default settings, every pause (button, space, media key or SMTC) ends in a click about 100 ms later, and every resume starts with a click. The size depends on the waveform amplitude at the cut, and it is clearly audible on headphones.

**Proposed fix (medium):** Park inside the provider instead of pausing WasapiOut. Add GaplessSpliceProvider.SetParked(bool). While parked, Read runs the existing declick ramp from _lastFrame over _startFadeSamples and then writes zeros without reading any segment. Unparking sets _cutFadePending = true so the first audio gets the 5 ms fade-in. GaplessSink.Pause/Resume call SetParked instead of WasapiOut.Pause/Play. If the separately reported power-request fix stops the stream, stop it only after the ramp has drained (about 110 ms).

**Verifier votes**

- **confirmed** (refute lens): GaplessSink.cs:326-340: Pause and Resume only call WasapiOut.Pause/Play. BUZZ_INVESTIGATION.md:149-151 (IL-verified for NAudio 2.3.0) says Pause only sets playbackState: the device drains the queued audio and then starves, and resume reads one full buffer. While paused the fill loop never calls GaplessSpliceProvider.Read, so the pad declick (GaplessSpliceCore.cs:637-648) and the silence-streak fade (590-594) never engage; _silentSamples stays 0 and nothing is armed on resume. The engine's VLC pause/resume callbacks are no-ops (VlcAudioPlayer.cs:686-687). With fade off, which is the default (AppSettings.cs:152; RunFadedPause 4211-4220 skips the fade), both edges are unramped steps. Lowered to medium: AppSettings.cs:150-151 documents fade-off as 'the hard cut', the classic VLC path cuts the same way, and loudness depends on the waveform amplitude. It is a missing declick, not broken playback.
- **confirmed** (impact lens): The code matches and the path is reachable by default. The gapless engine is on for Windows unless NOCTIS_GAPLESS_ENGINE=0 or exclusive mode is used (VlcAudioPlayer.cs:666). PlayPauseFadeEnabled defaults to false (AppSettings.cs:152), so RunFadedPause (VlcAudioPlayer.cs:4211-4218) goes straight to pauseOutput, which calls _gaplessSink?.Pause() (:4256). GaplessSink.Pause/Resume (GaplessSink.cs:326-339) only call WasapiOut.Pause/Play. In NAudio master, Pause() only sets playbackState=Paused; PlayThread skips FillBuffer while paused and never calls audioClient.Stop. The memory note on the IL-disassembled NAudio 2.3.0 agrees: shared mode drains, then starves to silence. The VLC pause/resume callbacks are no-ops (VlcAudioPlayer.cs:684-685). MuteGateProvider is not involved in pause. GaplessSpliceProvider only ramps on reads, through the pad declick at GaplessSpliceCore.cs:632-647 and the silence-streak fade at :590. No reads happen while paused, so _silentSamples stays 0 and neither edge is ramped. On resume, the sink restarts (:4305) before VLC and reads the ring at full amplitude. By contrast, the exclusive WasapiGainOutput.Pause (WasapiGainOutput.cs:284-303) parks the stream with a ramp. Severity lowered to medium: this is an audible click at pause and resume, not broken playback. The setting's own doc calls the fade-off state 'the hard cut', a fade option exists as a mitigation, and memory shows no user reports of it.

### A08 — On 44.1 kHz stereo devices the junction declick writes an odd number of samples, which swaps L and R for the rest of the read and adds a 1-sample underrun and a discontinuity

- **Location:** `src/Noctis/Services/GaplessSpliceCore.cs:398` (also: `src/Noctis/Services/GaplessSpliceCore.cs:560`, `src/Noctis/Services/GaplessSpliceCore.cs:613`, `src/Noctis/Services/PitchShiftProvider.cs:50`, `src/Noctis/Services/VlcAudioPlayer.cs:2726`)
- **Area / sweep:** Audio pipeline / audio-output · **Category:** audio-glitch · **Platforms:** windows
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
GaplessSpliceCore.cs:398
    _startFadeSamples = WaveFormat.SampleRate * WaveFormat.Channels * Math.Clamp(startFadeMs, 0, 100) / 1000;
(44100*2*5/1000 = 441, an odd number)
GaplessSpliceCore.cs:563-570 (fast-refill cut path)
    var run = Math.Min(_declickRemaining, count - written);
    for (var i = 0; i < run; i++) { buffer[offset + written + i] = _lastFrame[i % rampCh] * ...; _declickRemaining--; }
    written += run;
    ...
    continue;   // next: adapted.Read(buffer, offset + written, count - written)
PitchShiftProvider.cs:50-51: `var frames = count / _channels; if (frames <= 0) return 0;`
```

**Why it is a bug:** On a 44.1 kHz stereo mix format _startFadeSamples is 441. When a cut is followed by a refill that is already available, the fast path writes 441 samples (written becomes odd) and then asks the adapter chain for an odd count at an odd buffer offset. The ring's L sample therefore lands in an R slot, so channels are swapped and skewed by one sample for the rest of that read. PitchShiftProvider floors to whole frames, so the chain returns count-1 samples. The provider then asks for the one remaining sample, gets 0, and treats it as a mid-track underrun: it arms _refillSamplesNeeded, pads with a declick sample and sets _silentSamples = 1. The next read starts frame-aligned again, so both channels jump from the swapped content to the correct content with no ramp. If fewer than 50 ms are buffered, a silence gap and a fade follow. This path is always taken when a track change abandons the active segment while the next one is staged (Next inside the 8 s staging window, abandon-swap at VlcAudioPlayer.cs:2725-2726), and taken for seeks whenever the VLC refill beats the next render read (BUZZ_INVESTIGATION notes this race is common). At 48 kHz the ramp is 480 samples, which is even, so the bug is invisible there, and every unit test uses 8 kHz mono or 48 kHz stereo (tests/Noctis.Tests/GaplessSpliceCoreTests.cs).

**Impact:** Users whose Windows default format is 44.1 kHz (common for USB DACs, 'CD quality' settings and some Bluetooth endpoints) hear a glitch at seeks and track changes: about 10 ms of L/R-swapped audio (the whole ~100 ms buffer when the cut is consumed on the post-resume read), followed by a hard channel jump and sometimes a short gap. The declick added in session 4 is defeated on these devices.

**Proposed fix (trivial):** Compute the fade and declick lengths in whole frames: `var fadeFrames = WaveFormat.SampleRate * Math.Clamp(startFadeMs,0,100) / 1000; _startFadeSamples = fadeFrames * WaveFormat.Channels;`. Do the same for _fadeArmSamples. As a defence, round `run` at lines 563 and 642 down to a multiple of WaveFormat.Channels. Add a 44100-Hz stereo SeekCut/TrackCut test that checks channel parity after the junction.

**Verifier votes**

- **confirmed** (refute lens): GaplessSpliceCore.cs:398 computes _startFadeSamples = rate*channels*5/1000, which is 441 (odd) at 44.1 kHz stereo. The fast-refill cut path (560-579) writes `run = min(441, count-written)` samples and continues. Line 581 then reads the adapter chain at an odd offset. The outermost stage, TempoStretchProvider, passes straight through at unity (TempoStretchProvider.cs:59-63). PitchShiftProvider floors to whole frames (PitchShiftProvider.cs:50-62), so frames land in swapped slots and the call returns count-442. The follow-up 1-sample read returns 0, and because the segment is not finished (613-616) that sets _refillSamplesNeeded (50 ms) and pads one declick sample, so _silentSamples becomes 1. The next read is frame-aligned again and has no fade, because _cutFadePending was cleared at 593 and _silentSamples is below _fadeArmSamples. The path is reachable through the abandon-swap at VlcAudioPlayer.cs:2725-2726 followed by the Read dequeue at GaplessSpliceCore.cs:503-514, and through seek flushes. At 48 kHz the ramp is 480 samples (even), so the bug is invisible there. The audible artifact is small, a few ms of swapped channels and then a step, plus a possible short gap after seeks, and only on 44.1 kHz devices: medium.

### A09 — During an engine crossfade the declick ramp starts from the unmixed sample, so skipping or stopping during a blend clicks

- **Location:** `src/Noctis/Services/GaplessSpliceCore.cs:603` (also: `src/Noctis/Services/GaplessSpliceCore.cs:447`, `src/Noctis/Services/GaplessSpliceCore.cs:637`, `src/Noctis/Services/VlcAudioPlayer.cs:2761`, `src/Noctis/Services/VlcAudioPlayer.cs:4978`)
- **Area / sweep:** Audio pipeline / audio-output · **Category:** audio-glitch · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
GaplessSpliceCore.cs:601-605 (inside the read loop, BEFORE the mix)
    _declickRemaining = 0;
    var tailCh = WaveFormat.Channels;
    if (n >= tailCh)
        for (var c = 0; c < tailCh; c++)
            _lastFrame[c] = buffer[offset + written - tailCh + c];
GaplessSpliceCore.cs:651: `MixFadingTail(buffer, offset, count);` (rescales by inGain and adds the outgoing tail AFTER _lastFrame was captured)
GaplessSpliceCore.cs:456-457 (Clear): `_fading?.Abandon(); _fading = null;`
```

**Why it is a bug:** _lastFrame is meant to be the last emitted frame, the start point for the next cut's declick ramp. During a crossfade, what is actually emitted is x*inGain + tail*outGain (MixFadingTail), but _lastFrame holds the raw incoming x. When Clear() runs during a blend (manual play or skip via EngineClearAll at VlcAudioPlayer.cs:2761, Stop at 4343), both the active segment and the fading tail are dropped. The next read pads and ramps down from the raw x (pad path 637-648), with no tail mixed in because _fading is null. The output therefore steps from the emitted mix to x before ramping to 0, a step of x(1-inGain) - tail*outGain, which can reach about half of full scale mid-fade. This is separate from the reported ReleasePreparedNext tail abandon at VlcAudioPlayer.cs:4978: fixing that one still leaves this step.

**Impact:** With Song Transitions (crossfade) enabled, choosing another song or stopping during the 6-12 s blend produces an audible click at the cut, which the engine's declick is supposed to prevent.

**Proposed fix (small):** Capture _lastFrame from the final emitted buffer after MixFadingTail, i.e. buffer[offset + count - ch .. offset + count - 1] at the end of Read. Then every later cut ramp, including Clear during a blend, starts from what the speaker actually got.

**Verifier votes**

- **confirmed** (refute lens): The cited code matches. GaplessSpliceCore.cs:601-605 sets _lastFrame from the raw adapted.Read output. MixFadingTail runs only afterwards, at :651, and rewrites the buffer as x*inGain + tail*outGain at :702-703. Clear() at :447-462 drops _active and _fading and sets _pendingCutSignal, but leaves _lastFrame alone. On the next Read the cut and pad path ramps from _lastFrame at :637-645 (or the junction ramp at :567), and MixFadingTail then returns early because _fading is null. The ramp therefore starts at the raw incoming sample, not at the mix that was actually played. It is reachable. The engine splice starts BeginCrossfade at VlcAudioPlayer.cs:2721. A manual play during the blend then reaches EngineClearAll at :2761 (canTransitionFade is false when _gaplessEngine, :2659-2661, so the volume is not faded first). Stop does the same at :4343.

### A11 — PitchShiftProvider copies into NAudio's byte[]-punned render buffer with Array.Copy, a second site of the reported TempoStretch crash, hit when pitch is reset to 0

- **Location:** `src/Noctis/Services/PitchShiftProvider.cs:101` (also: `src/Noctis/Services/TempoStretchProvider.cs:77`, `src/Noctis/Services/GaplessSpliceCore.cs:744`, `src/Noctis/Services/MuteGateProvider.cs:57`)
- **Area / sweep:** Audio pipeline / audio-output · **Category:** crash-render-thread · **Platforms:** windows
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
PitchShiftProvider.cs:95-101
    private int DrainBufferedAtUnity(float[] buffer, int offset, int frames)
    {
        var i = (int)Math.Round(_pos);
        var available = Math.Max(0, _inFrames - i);
        var take = Math.Min(frames, available);
        if (take > 0)
            Array.Copy(_in, i * _channels, buffer, offset, take * _channels);
Chain: SampleToWaveProvider.Read -> `source.Read(wb.FloatBuffer, offset / 4, samplesNeeded)` (byte[] punned as float[]) -> ... GaplessSpliceProvider.Read(buffer) -> adapted.Read(buffer,...) -> TempoStretchProvider (rate 1: `return _source.Read(buffer, offset, count);`) -> PitchShiftProvider.Read(buffer).
```

**Why it is a bug:** The render buffer is NAudio's WaveBuffer pun, a byte[] reinterpreted as float[] (https://github.com/naudio/NAudio/blob/master/NAudio.Core/Wave/SampleProviders/SampleToWaveProvider.cs). Array.Copy checks the runtime element type, so copying float[] into byte[] throws ArrayTypeMismatchException (the same mechanism as the reported TempoStretchProvider.cs:77 and the old Array.Clear buzz in BUZZ_INVESTIGATION.md). After any non-unity pitch, _in holds lookahead frames (Fill reads i+3-_inFrames+512 frames). When the user returns pitch to 0 semitones and the speed is 1x, TempoStretch passes the render buffer straight through and PitchShift's unity path copies those frames with Array.Copy, which throws on the render thread. _in is unchanged, so every following read throws again. Fixing only TempoStretch:77 leaves this. With upmix on the chain receives UpmixSampleProvider's real float[] _scratch, which is why it may not show in some tests.

**Impact:** Once the reported TempoStretch fix lands, resetting pitch to 0 after any shift still kills the engine output: silence and rebuild churn, see the RebuildLoop finding.

**Proposed fix (trivial):** Replace the copy with element stores, `for (var k = 0; k < take * _channels; k++) buffer[offset + k] = _in[i * _channels + k];`. Alternatively use `_in.AsSpan(i*_channels, take*_channels).CopyTo(buffer.AsSpan(offset))`: Span<float> goes through the static type and is pun-safe, as MuteGateProvider.cs:57 already relies on. Add a WaveBuffer-pun test like GaplessGapCanaryTests for pitch shift, then reset.

**Verifier votes**

- **confirmed** (refute lens): PitchShiftProvider.cs:101 is exactly `Array.Copy(_in, i * _channels, buffer, offset, take * _channels)`. The render buffer reaches it as NAudio's byte[] pun: wasapiOut.Init(new SampleToWaveProvider(render)) at GaplessSink.cs:205. Every stage passes the same buffer through: StallProbe (:503), BeatTapProvider (BeatMeter.cs:223), MuteGate, GaplessSpliceProvider.Read at :587, and TempoStretch at rate 1 (TempoStretchProvider.cs:59-62). Array.Copy from float[] to byte[] throws ArrayTypeMismatchException. Upmix replaces the buffer with a real float[] (UpmixSampleProvider.cs:91), as the finding says. The PitchRatio setter (GaplessSpliceCore.cs:379-383) only writes the value and does not rebuild the chain. The non-unity path leaves lookahead frames in _in (Fill asks for +512 frames, :72, and Compact keeps what is unread), so available > 0 when pitch returns to unity. The throw happens before the bookkeeping at :103-113, so every later read throws again.

### A12 — PitchShiftProvider._ended is set on any 0-read and never cleared, so one ring underrun while pitch-shifted silences the rest of the track

- **Location:** `src/Noctis/Services/PitchShiftProvider.cs:130` (also: `src/Noctis/Services/PitchShiftProvider.cs:57`, `src/Noctis/Services/PitchShiftProvider.cs:72`, `src/Noctis/Services/GaplessSpliceCore.cs:613`)
- **Area / sweep:** Audio pipeline / audio-output · **Category:** audio-dropout · **Platforms:** windows
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
PitchShiftProvider.cs:129-134
    var got = _source.Read(_in, _inFrames * _channels, wantFrames * _channels);
    if (got <= 0)
    {
        _ended = true;
        return false;
    }
PitchShiftProvider.cs:57: `if (produced < frames && !_ended)` and 72: `if (_ended || !Fill(...)) break;` (no code path resets _ended)
```

**Why it is a bug:** The source is the segment ring (SegmentSampleProvider), which returns 0 for a transient mid-track underrun as well as at the real end of stream (GaplessSpliceCore.cs:609-616 treats that 0 as 'pad and retry'). PitchShift treats the first 0 as permanent EOF: afterwards it returns only already-buffered frames and then 0 forever, at non-unity and at unity pitch alone. The provider keeps padding silence and re-arming the underrun hold while the ring backs up behind it. Today this is reachable when upmix is on, because the chain then gets a real float[] and does not hit the reported Array.Copy crash, and it becomes reachable for everyone once that crash is fixed.

**Impact:** With pitch shift active, a disk or network stall long enough to empty the ring (the HDD stalls the adaptive read-ahead exists for) turns into silence for the rest of the track instead of a short dropout.

**Proposed fix (trivial):** Do not latch _ended on a 0-read. Return false from Fill and let the next Read retry, and set _ended only when the owning segment reports EndOfStream, or drop the flag and rely on the provider's IsFinished check.

**Verifier votes**

- **confirmed** (refute lens): _ended is set only at PitchShiftProvider.cs:132 and never cleared (field init :27). After that, the non-unity path breaks at :72 and the unity path skips the source at :57, so every read returns 0 from then on. GaplessTrackSegment.Read (GaplessSpliceCore.cs:128-146) returns 0 whenever the ring is empty, and the provider treats that 0 as a hold, not the end (:613-616). The trigger is easier than a full stall: Fill runs repeatedly inside one Read, so the latch fires whenever a single pitched request is larger than what the ring holds. That is plausible right after a seek, when the refill gate is only 50 ms (FlushRearmThresholdMs, :328). The damage is also worse than stated. The segment keeps the same adapter across seeks. The ring fills and is never drained, so IsFinished ((endOfStream||abandoned) && _count==0, :71) never becomes true. Playback stays silent, with no auto-advance, until the user picks another track. Hence medium, not low.

### A16 — The ReplayGain reader ignores APE and ASF tags, but the RG scanner writes into them, so RG never applies to .ape/.wv/.wma files (or MP3s with APEv2-only RG)

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:2075` (also: `src/Noctis.Core/Services/AdvancedTagIO.cs:585`, `src/Noctis/Services/ReplayGainScannerService.cs:295`, `src/Noctis.Core/Services/MetadataService.cs:54`, `src/Noctis/Services/VlcAudioPlayer.cs:2045`)
- **Area / sweep:** Audio pipeline / audio-core · **Category:** replaygain · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
VlcAudioPlayer.cs:2075-2090
    if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3) { ... }
    if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph) { ... }
    if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple) { ... }
    return (track, album);
AdvancedTagIO.cs:585-600
    if (file.GetTag(TagTypes.Ape, clean != null) is TagLib.Ape.Tag ape) { ... ape.SetValue(key, clean); }
    if (file.GetTag(TagTypes.Asf, clean != null) is TagLib.Asf.Tag asf) { ... asf.SetDescriptorString(clean, key); }
ReplayGainScannerService.cs:295  AdvancedTagIO.WriteCustomField(file, "REPLAYGAIN_TRACK_GAIN", Gain(trackGainDb));
```

**Why it is a bug:** The library indexes .ape, .wv and .wma (MetadataService.cs:54-55), and those formats carry only APEv2 (Monkey's Audio, WavPack) or ASF (WMA) tags. The scanner writes RG into exactly those tags through WriteCustomField, but ReadReplayGainTags reads only ID3v2, Xiph and MP4 atoms, so it returns (null, null) and `_replayGainScalar` stays 1.0 (2001). mp3gain-style tools also write RG to APEv2 on MP3s.

**Impact:** ReplayGain (Track/Album/Auto) silently does nothing for WavPack, Monkey's Audio and WMA tracks, even after the in-app scanner reports them as scanned. Their loudness jumps against normalized tracks.

**Proposed fix (small):** Add readers for `file.GetTag(TagTypes.Ape,false) is TagLib.Ape.Tag ape` → `ParseDb(ape.GetItem("REPLAYGAIN_TRACK_GAIN")?.ToString())` (and ALBUM), and for `TagTypes.Asf` → `asf.GetDescriptorString("REPLAYGAIN_TRACK_GAIN")`. Also invalidate `_rgCachePath` after the scanner rewrites the file that is currently loaded.

**Verifier votes**

- **confirmed** (refute lens): The core defect is real, but the finding overstates it. ReadReplayGainTags (VlcAudioPlayer.cs:2065-2097) reads only Id3v2, Xiph and Apple tags. It never reads APE or ASF, so RG applies 1.0 (2001) to: WMA/ASF files, whose TagLib File only holds an Asf tag, so the scanner's value lands only in the ASF descriptor (AdvancedTagIO.cs:597-601); and any .wv/.ape/.mp3 whose RG was written only to APEv2 by an external tool (foobar2000, wavpack, mp3gain). However, the claim that .ape/.wv stay unnormalized 'even after the in-app scanner' is wrong. WriteCustomField calls GetTag(Id3v2, create:true) first (AdvancedTagIO.cs:565). A scratch probe against the project's TagLibSharp 2.3.0 showed that TagLib.WavPack.File and TagLib.Ape.File both create an Id3v2 tag on that call. After Save the file carries Id3v2+Ape, and the reader's Id3v2 TXXX path (2075-2078) reads the gain back. So in-app scanning does fix .wv/.ape files. Only WMA and externally tagged APEv2-only files are affected.

### A17 — Remote (media-server) streams still never get gapless/crossfade; every boundary pays the EndReached grace plus a network open and parse

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:2298` (also: `src/Noctis/Services/VlcAudioPlayer.cs:2659`, `src/Noctis/Services/VlcAudioPlayer.cs:2698`, `src/Noctis/ViewModels/PlayerViewModel.cs:2959`)
- **Area / sweep:** Audio pipeline / audio-core · **Category:** gapless-transition · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Prior audit:** AUDIT.md M4

**Evidence**

```
VlcAudioPlayer.cs:2296-2299
    public void PrepareNext(string filePath, long startPositionMs = -1)
    {
        if (_disposed || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;
VlcAudioPlayer.cs:2659-2661  canTransitionFade = ... && !isPathless;
VlcAudioPlayer.cs:2698  if (_gaplessEngine && !startPaused && !isPathless && ...
PlayerViewModel.cs:2959  if (string.IsNullOrWhiteSpace(nextTrack.FilePath) || !File.Exists(nextTrack.FilePath)) return false;
```

**Why it is a bug:** The code is unchanged from AUDIT M4, which FIXLOG lists as deferred. An http(s) track never passes File.Exists, so no staging happens and the engine splice and crossfade gates exclude pathless media. Every boundary goes through EndReached, whose grace on the engine is max(1200 ms, ring + 250 ms) (4559-4565), followed by a cold FromLocation + ParseNetwork open (2789-2805).

**Impact:** Streaming from Jellyfin/Subsonic always leaves a ~1–2 s gap between tracks, even with gapless on.

**Proposed fix (large):** As planned in FIXLOG: a remote-capable PrepareNext (FromLocation/ParseNetwork, URL scrubbing, skip File.Exists for IsRemoteStreamPath), and lift the `!isPathless` gate on the engine splice path.

**Verifier votes**

- **confirmed** (refute lens): The code matches the finding. VlcAudioPlayer.cs:2298 returns early on !File.Exists, so a URL is never staged. canTransitionFade (2659-2661) and the engine splice gate (2698) both require !isPathless. PlayerViewModel.cs:2959 also bails on !File.Exists. That leaves every remote boundary on the EndReached grace: EndReachedGraceMs=1200 at line 82, or max(1200, buffered+250) at 4559-4565. After that comes a cold FromLocation + ParseNetwork open (2789-2805). The in-code comment at 2648-2650 and FIXLOG.md:7/71 both list this as still-open M4. It is a known, deferred defect, not intended behaviour, and the description is accurate.

### A18 — On the engine, the next track's ReplayGain level is applied at the splice, ≥0.5 s before the audible boundary, so the outgoing tail (or the whole engine crossfade) plays at the wrong gain

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:2644` (also: `src/Noctis/Services/VlcAudioPlayer.cs:2014`, `src/Noctis/Services/VlcAudioPlayer.cs:2722`, `src/Noctis/Services/GaplessSpliceCore.cs:316`, `src/Noctis/ViewModels/PlayerViewModel.cs:2984`)
- **Area / sweep:** Audio pipeline / audio-core · **Category:** replaygain · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
VlcAudioPlayer.cs:2642-2644
    _currentMediaPath = filePath;
    if (!string.Equals(_rgMode, "Off", StringComparison.OrdinalIgnoreCase))
        ApplyReplayGain(_rgMode, _rgPreampDb);
VlcAudioPlayer.cs:2014-2019 ReapplyVolume -> ScheduleVolumeWrite (session ramp starts now)
VlcAudioPlayer.cs:2698-2755 (engine splice = bookkeeping only; outgoing keeps rendering from the ring)
VlcAudioPlayer.cs:2721-2723 engine crossfade wantMs up to 12000
```

**Why it is a bug:** On the engine the session level is a single post-mix gain over whatever the sink renders. PlayInternal re-reads RG for the incoming track and starts ramping the session (steps of 10‰ every 16 ms, 1178-1188) before the splice. The splice happens when the VM position (the fed position) has 0.5 s left (PlayerViewModel.cs:2903/2984), plus GaplessSink's 100 ms output latency, and the outgoing then keeps rendering from the ring. With BeginCrossfade the outgoing plays for the whole 1–12 s fade under the incoming track's gain. The 5 dB example below assumes typical Track-mode RG differences between tracks.

**Impact:** With RG in Track mode (or Album mode across albums) plus gapless or crossfade on the Windows engine, every transition audibly shifts the level of the previous track's last half second, or of its whole crossfade tail. A −9 dB track after a −4 dB track dips the ending by 5 dB early.

**Proposed fix (medium):** Make RG a per-segment gain: store the scalar on the GaplessTrackSegment at PrepareNext/EngineBeginSegment and apply it in the provider's read (per-sample, so crossfade tails keep their own gain). Alternatively, defer the session re-apply until ActiveSegment.Source == slot(_player), using the provider's existing SegmentStarted hook or the position timer.

**Verifier votes**

- **confirmed** (refute lens): PlayInternal sets _currentMediaPath and calls ApplyReplayGain at 2642-2644, before the engine splice branch at 2698. _transitionInFlight was cleared at 2636, so ReapplyVolume (2014-2020) runs and ScheduleVolumeWrite (1026-1049) ramps the single OS session level (ApplyRampLevel 1123-1135; the engine keeps volume on the session per 657-660) by at most 10 per-mille every 16 ms (74-80). The VM advances at remaining <= 0.5 s (PlayerViewModel.cs:2903, 2984), and the outgoing segment keeps rendering from the ring after the splice, or for the whole BeginCrossfade wantMs (2721-2723). The RG scalar has no per-segment consumer: _replayGainScalar is used only at 2024-2032, and nothing subscribes to SegmentStarted. The outgoing tail therefore plays at the incoming track's gain.

### A19 — Pause is ignored during the engine tail (VLC input already Ended while the ring still plays), and TrackEnded then starts the next track

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:4243` (also: `src/Noctis/Services/VlcAudioPlayer.cs:4299`, `src/Noctis/Services/VlcAudioPlayer.cs:4307`, `src/Noctis/Services/VlcAudioPlayer.cs:4526`, `src/Noctis/Services/VlcAudioPlayer.cs:4568`, `src/Noctis/ViewModels/PlayerViewModel.cs:449`, `src/Noctis/ViewModels/PlayerViewModel.cs:3046`)
- **Area / sweep:** Audio pipeline / audio-core · **Category:** playback-state · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Prior audit:** AUDIT.md M2 (residual)
- **Needs runtime check:** yes

**Evidence**

```
VlcAudioPlayer.cs:4240-4257
    if (_disposed) return;
    if (_player.IsPlaying)
    {
        ResetEndReachedPending();
        RunFadedPause(PlayPauseFadeArmed, PausedOutputDrainMs(OutputLatency), FadeOutBeforePause,
            () => { _player.Pause(); if (_gaplessEngine) _gaplessSink?.Pause(); _isPaused = true; ...
VlcAudioPlayer.cs:4559-4569 (EndReached arms the grace to the AUDIBLE end, bufferedMs + 250)
VlcAudioPlayer.cs:4684-4686 comment: "VLC sets IsPlaying=false before the audio output buffer fully drains"
VlcAudioPlayer.cs:4307  _player.Pause();   // Resume uses the toggle
PlayerViewModel.cs:3049-3052  Dispatcher.UIThread.Post(() => { CancelNaturalEndFallback(); AdvanceQueue(); });
```

**Why it is a bug:** libvlc_media_player_is_playing returns `libvlc_Playing == state`, and the input goes to libvlc_Ended at EOF (https://github.com/videolan/vlc/blob/3.0.x/lib/media_player.c). On the engine, amem has time_get=NULL (https://github.com/videolan/vlc/blob/3.0.x/modules/audio_output/amem.c), so the decoder is paced only by DecoderTimedWait(pts − AOUT_MAX_PREPARE_TIME), which is 2 s (https://github.com/videolan/vlc/blob/3.0.x/include/vlc_aout.h, src/input/decoder.c). The input therefore hits EOF about 1–2 s before the ring finishes rendering, as the code's own comments at 4541-4543 and 4552-4557 say. In that window Pause() sees IsPlaying == false: nothing is paused, the sink keeps rendering, and the grace deadline armed at 4568 is not reset. The VM still sets State = Paused (PlayerViewModel.cs:450). When the grace expires, TrackEnded runs AdvanceQueue regardless of state, so PlayTrack plays the next track. The same gate drops a pause that lands right after PlayInternal returns while the new input is still Opening; this is what remains of AUDIT M2. Resume's `_player.Pause()` toggle (4307) can also pause instead of resume if the state is transiently Playing.

**Impact:** If the user presses Pause in the last ~1–2 s of a track (the whole drain for the last queued track, or with gapless off), the audio keeps playing, the UI shows Paused, and the next track then starts on its own while the UI flips back to Playing.

**Proposed fix (small):** Gate the pause on intent, not on VLC's state: `if (_currentMedia != null && !_isPaused)`. On the engine, always call `_gaplessSink.Pause()` and use `_player.SetPause(true)`, which is idempotent and harmless on an Ended input. Keep ResetEndReachedPending; the EndWatchdog re-arms after resume once the tail drains. In Resume use `_player.SetPause(false)` instead of the `Pause()` toggle.

**Verifier votes**

- **confirmed** (refute lens): The Pause worker (4243) acts only when _player.IsPlaying. On the engine the input reaches Ended while the ring still renders, as the code's own comments say (4541-4543, 4552-4557, 4684-4686), and EndReached arms the grace to buffered+250 ms (4559-4569). A pause in that window changes nothing: the sink keeps playing, the deadline is not reset, and _isPaused stays false. The VM still sets State=Paused (PlayerViewModel.cs:449-450). When the deadline expires, the timer (4675-4681) fires TrackEnded. OnTrackEnded (3046-3053) calls AdvanceQueue, and AdvanceQueueCore (2223ff) has no State guard, so the next track starts. Pause() also calls CancelPreparedNext (4227), so the staged gapless successor is gone and the grace path is what advances. Resume uses the Pause() toggle at 4307, as the finding says.

### A21 — The engine EndWatchdog treats the still-rendering outgoing tail as 'drained' and fires TrackEnded right after a gapless splice into a short, already-decoded track, so that track is skipped

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:4704` (also: `src/Noctis/Services/VlcAudioPlayer.cs:4482`, `src/Noctis/Services/VlcAudioPlayer.cs:2698`, `src/Noctis/Services/VlcAudioPlayer.cs:2761`, `src/Noctis/ViewModels/PlayerViewModel.cs:1974`, `src/Noctis/ViewModels/PlayerViewModel.cs:2939`, `src/Noctis/ViewModels/PlayerViewModel.cs:3046`)
- **Area / sweep:** Audio pipeline / audio-core · **Category:** gapless-transition · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
VlcAudioPlayer.cs:4699-4712
    if (_player.State == VLCState.Ended)
    {
        var wdSeg = _gaplessSink?.Provider.ActiveSegment;
        if (wdSeg != null && wdSeg.Source is int wdSlot && wdSlot == EngineSlotOf(_player))
            wdSeg.MarkEndOfStream();
        var drained = wdSeg == null || wdSeg.IsFinished || wdSeg.BufferedSamples == 0 ||
                      (wdSeg.Source is int s && s != EngineSlotOf(_player));
        if (drained)
        { ... Interlocked.Exchange(ref _endReachedDeadlineTicksUtc, DateTime.UtcNow.AddMilliseconds(250).Ticks);
VlcAudioPlayer.cs:4482-4490 (staged player's EndReached -> IgnoredInactive)
```

**Why it is a bug:** The VM stages the next track about 8 s before the end (PlayerViewModel.cs:2939-2954) and splices when 0.5 s remains (2903, 2984). The staged player is Playing in VLC's view, and amem decode is paced about 2 s ahead of VLC's clock (AOUT_MAX_PREPARE_TIME, https://github.com/videolan/vlc/blob/3.0.x/include/vlc_aout.h), so a next track of roughly ≤ 6–8 s is fully decoded before the splice. Its EndReached fires while it is still `_standbyPlayer` and is ignored (4482-4490); only MarkEndOfStream runs. After the bookkeeping-only splice (2727-2746), `_player` is that Ended player, and ActiveSegment is still the outgoing track's tail from the other slot for about 0.5 s. The last clause of `drained` is therefore true, and the watchdog arms a 250 ms end grace at the first timer tick. TrackEnded then calls AdvanceQueue. The VM's 2 s commit guard (PlayerViewModel.cs:1974, 2923) means the next-next track cannot be staged yet, so PlayInternal takes the fresh path, and EngineClearAll (2761) abandons both the outgoing tail and the short track's fully decoded segment.

**Impact:** With gapless on (the default) on the Windows engine, short album tracks such as intros, skits and interludes are skipped entirely or cut to a fraction of a second. The last ~0.2 s of the previous track is also cut, the next track opens cold with a gap, and the play count and history still record the skipped track.

**Proposed fix (trivial):** Judge 'drained' by the current player's own segment rather than whatever is active: `var mySeg = Volatile.Read(ref _engineSegments[EngineSlotOf(_player)]); var drained = mySeg == null || mySeg.IsFinished;`. Only MarkEndOfStream mySeg, then arm the grace once it has actually rendered out.

**Verifier votes**

- **confirmed** (refute lens): The code matches the finding. VlcAudioPlayer.cs:4699-4712: the watchdog arms a 250 ms end grace when `_player.State == Ended`, and `drained` is true whenever the active segment's Source slot differs from `_player`'s slot. The splice path (2698-2755) swaps `_player` to the staged player, calls ResetEndReachedPending (2738) and starts the timer (2746), but leaves the outgoing tail as ActiveSegment. The staged player decodes freely from PrepareNext (2457-2468): VM prepares at <=8 s remaining and hands off at <=0.5 s (PlayerViewModel.cs:2902-2903, 2937-2984), the ring is 20 s (3989) and Write only blocks when that ring is full (GaplessSpliceCore.cs:98). So a short next track can reach input EOF before the splice. Its EndReached is ignored as inactive (4482-4490) apart from MarkEndOfStream, and that player is then in the Ended state. On the next tick the grace is armed and TrackEnded fires about 250 ms later. OnTrackEnded (PlayerViewModel.cs:3046-3053) calls AdvanceQueue with no recent-commit guard; the only guard is re-entrancy (2087). The commit guard (1974) blocks staging, so PlayInternal falls into EngineClearAll (2761). Only tracks shorter than about the decode-ahead window are affected, which is uncommon, so medium.

### A22 — ReleasePreparedNext abandons whatever segment sits in the standby slot, including the audible outgoing/crossfade tail, so a queue edit, pause, seek or settings change during an engine crossfade pops and snaps the incoming track to full level

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:4978` (also: `src/Noctis/Services/VlcAudioPlayer.cs:2510`, `src/Noctis/Services/VlcAudioPlayer.cs:2409`, `src/Noctis/Services/VlcAudioPlayer.cs:2722`, `src/Noctis/Services/GaplessSpliceCore.cs:682`, `src/Noctis/Services/GaplessSpliceCore.cs:707`, `src/Noctis/ViewModels/PlayerViewModel.cs:3023`)
- **Area / sweep:** Audio pipeline / audio-core · **Category:** pops-clicks · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
VlcAudioPlayer.cs:4970-4980
    private void ReleasePreparedNext()
    {
        if (_gaplessEngine)
        {
            _engineStagedPath = null;
            try { Volatile.Read(ref _engineSegments[EngineSlotOf(_standbyPlayer)])?.Abandon(); } catch { }
        }
        try { _standbyPlayer.Stop(); } catch { }
VlcAudioPlayer.cs:2727-2731 (splice: outgoing player becomes _standbyPlayer; its segment keeps rendering / fading)
VlcAudioPlayer.cs:2753-2754  QueueInactivePlayerCleanup(..., extraDelayMs: engineFadeMs > 0 ? engineFadeMs + 500 : 0);
GaplessSpliceCore.cs:682-718 (MixFadingTail: abandoned tail -> zeros, then _fading = null)
```

**Why it is a bug:** After an engine splice, `_standbyPlayer` is the outgoing player, and `_engineSegments[its slot]` is the tail that is still being rendered. During an engine crossfade (Song Transitions/AutoMix; BeginCrossfade at 2722) that tail is the provider's `_fading` segment for up to 12 s. ReleasePreparedNext runs even when `_standbyPrepared` is false. CancelPreparedNext (2510-2531) calls it, and the VM calls CancelPreparedNext through CancelAutoMixTransition on queue add/remove/reorder, shuffle and repeat toggles, settings changes, pause and seek (PlayerViewModel.cs:3013-3023; callers at 448, 627, 720, 790, 1151-1348). Abandon() empties the tail. MixFadingTail then mixes zeros and drops `_fading`, so within one ~10 ms period the outgoing (e.g. outGain 0.78) falls to 0 and the incoming jumps from inGain (e.g. 0.22) to 1.0, a step of about +13 dB. Stop() also kills the outgoing decoder mid-fade.

**Impact:** Editing the queue or toggling shuffle/repeat while an engine crossfade is running gives an audible pop, the previous track vanishes and the new track jumps in volume. After a plain gapless boundary, pausing or seeking within the ~0.5 s tail cuts the previous track's last moment.

**Proposed fix (small):** Track the staged segment explicitly (e.g. an `_engineStagedSegment` field set in PrepareNext) and have ReleasePreparedNext abandon only that segment, and stop the standby player only when `_standbyPrepared` is true. Leave outgoing and crossfade tails to QueueInactivePlayerCleanup. In PrepareNext, abandon a leftover tail only if it is not EndOfStream.

**Verifier votes**

- **confirmed** (refute lens): The code matches the finding. ReleasePreparedNext (VlcAudioPlayer.cs:4970-4989) abandons `_engineSegments[EngineSlotOf(_standbyPlayer)]` and Stops `_standbyPlayer` without checking `_standbyPrepared`. CancelPreparedNext (2510-2531) calls it unconditionally. After an engine splice, `_standbyPlayer` is the outgoing player (2727-2731), and its segment slot is never reset. With a transition-mode crossfade, that segment is the provider's `_fading` tail (BeginCrossfade, GaplessSpliceCore.cs:424-441). MixFadingTail (682-718) mixes zeros for an abandoned tail and drops `_fading` in the same read. The outgoing therefore disappears in one step, and the incoming jumps from inGain to 1.0 on the next read, with no ramp. The trigger is reachable: CancelAutoMixTransition calls CancelPreparedNext (PlayerViewModel.cs:3023) on queue, shuffle, repeat and settings changes (720, 790, 1151-1348), and VlcAudioPlayer.Pause (4227) and Seek (4365) call it directly. It needs a user action during a transition-mode crossfade, which is occasional, so medium. The plain-gapless tail cut it also describes is declicked (GaplessSpliceCore.cs:503-504) and only a small edge case.

### A23 — Exclusive mode (WasapiGainOutput) hard-cuts on every seek, stop and underrun with no declick, and the seek worker deliberately skips masking on this path

- **Location:** `src/Noctis/Services/WasapiGainOutput.cs:305` (also: `src/Noctis/Services/VlcAudioPlayer.cs:4863`, `src/Noctis/Services/VlcAudioPlayer.cs:3861`, `src/Noctis/Services/WasapiGainOutput.cs:410`)
- **Area / sweep:** Audio pipeline / audio-output · **Category:** audio-glitch · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
WasapiGainOutput.cs:305-309
    public void Flush()
    {
        if (_disposed) return;
        try { _buffer.ClearBuffer(); } catch { }
    }
WasapiGainOutput.cs:230: `ReadFully = true, // return silence (not 0) when idle`
VlcAudioPlayer.cs:4863-4875: `else if (ActiveCallbackSink != null || _gaplessEngine) { // Exclusive Mode / WASAPI gain path: the sink owns gain ... Just seek. _player.Time = targetMs; }`
VlcAudioPlayer.cs:3861: `private void AudioFlush(IntPtr data, long pts) => ActiveCallbackSink?.Flush();`
```

**Why it is a bug:** A seek or stop makes VLC call the amem flush callback, which empties the BufferedWaveProvider. The render thread plays the ~100 ms already in the device buffer, and then ReadFully returns zeros, a step from mid-waveform to 0. Post-seek PCM then starts at arbitrary amplitude. GainSampleProvider (410-474) only slews the volume and park gains. It has no cut or underrun declick like GaplessSpliceProvider's (GaplessSpliceCore.cs:632-648). On the classic path the seek click was masked by the session duck, and on the engine path by the provider's declick, but for the callback sink the seek worker explicitly does nothing. Mid-track underruns (ReadFully zero-fill) have the same step. In exclusive mode no OS mixer stage can soften it.

**Impact:** With Settings > Audio > Exclusive Mode on, every timeline or lyrics click and every manual skip mid-track produces audible clicks at the cut and at the restart.

**Proposed fix (medium):** Give GainSampleProvider a cut declick. Flush() sets a volatile _cutPending. On the render thread, the next Read ramps the last emitted frame to 0 over about 5 ms and then fades in the first post-cut audio. When the BufferedWaveProvider under-delivers (track BufferedBytes before Read), ramp the tail down instead of butt-joining zeros. Reuse GaplessSpliceProvider's pattern.

**Verifier votes**

- **confirmed** (refute lens): The code matches the finding. WasapiGainOutput.Flush (WasapiGainOutput.cs:305-309) only clears the BufferedWaveProvider, and ReadFully=true (230) then zero-fills. GainSampleProvider.Read (410-474) only slews volume and park gain; unlike GaplessSpliceProvider (GaplessSpliceCore.cs:529-648), it has no declick for a cut or an underrun. AudioFlush sends VLC's flush straight to the sink (VlcAudioPlayer.cs:3861). The seek worker deliberately does nothing extra for a callback sink (4863-4876: 'Just seek'), even though its own comment at 4847-4849 says the seek flush click needs masking. Manual skips in this mode have no pre-stop fade either: fadeOutMs is 0 because canTransitionFade needs `!_exclusiveModeEnabled` (2659-2664), then `_player.Stop()` runs at 2838. Exclusive mode is reachable because RebuildOutputModeLocked turns the engine off (1655-1664). This only affects users who turn on Exclusive Mode on Windows, so medium.

### A25 — A seek, pause or any queue edit during a track's open/parse aborts the new track: the old song keeps playing under the new title, or there is silence

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:2513` (also: `src/Noctis/Services/VlcAudioPlayer.cs:2624`, `src/Noctis/Services/VlcAudioPlayer.cs:2810`, `src/Noctis/Services/VlcAudioPlayer.cs:4226`, `src/Noctis/Services/VlcAudioPlayer.cs:4364`, `src/Noctis/ViewModels/PlayerViewModel.cs:3013`, `src/Noctis/ViewModels/PlayerViewModel.cs:1190`, `src/Noctis/ViewModels/PlayerViewModel.cs:449`, `src/Noctis/ViewModels/PlayerViewModel.cs:628`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** race-condition · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: high)
- **Needs runtime check:** yes

**Evidence**

```
VlcAudioPlayer.cs:2510-2513  public void CancelPreparedNext() { if (_disposed) return; CancelSkipCts(); ...
2623-2628 (PlayInternal)  _positionTimer.Stop(); var oldCts = _skipCts; _skipCts = new CancellationTokenSource(); ... var cancel = _skipCts.Token;
2803-2814  using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel); ...
           try { parseTask.Wait(cts.Token); }
           catch (OperationCanceledException) when (cancel.IsCancellationRequested) { media.Dispose(); return; }
4226 (Pause) CancelSkipCts();   4364 (Seek) CancelSkipCts();
PlayerViewModel.cs:3023  _audioPlayer.CancelPreparedNext();   (inside CancelAutoMixTransition)
```

**Why it is a bug:** PlayInternal's header parse waits on _skipCts.Token. CancelPreparedNext, Pause and Seek all cancel that same CTS. The VM calls CancelAutoMixTransition -> CancelPreparedNext on every queue edit: AddNext/AddToQueue (1193, 1202), AddRange, RemoveFromQueue, MoveInQueue, ClearQueue, ToggleShuffle, CycleRepeat, and OnLibraryUpdated pruning. It also calls Pause (PlayPause, 449) and Seek (SeekToPosition 628, EndSeek 1927) straight from user commands. If one of these lands while the new track's PlayInternal is inside parseTask.Wait, PlayInternal returns at 2814. By then it has already bumped the session id, stopped the position timer (2623) and set _currentMediaPath to the new file, but it never stopped or replaced the old media. The parse window is tens of ms on an SSD and up to 8 s on a spun-down HDD or NAS. Example: click song B, then 'Add to queue' on another song while the disk spins up. Song A keeps playing, the timeline freezes at 0:00 under B's title, and when A ends its EndReached arms the grace timer for B's session. TrackEnded then makes the VM advance past B, which never played. With Pause as the trigger, the Pause worker then pauses A, and Resume resumes A. On the first Play after a restore, nothing loads and Resume is a no-op (_currentMedia == null), so the UI shows 'Playing' with silence.

**Impact:** Clicking a song and then quickly pausing, seeking or editing the queue (common on slow disks, where nothing seems to happen for a moment) leaves the previous song playing under the new title with a frozen timeline, or leaves silence. The next natural end then skips the song the user picked.

**Proposed fix (small):** Give the open/parse its own CancellationTokenSource (e.g. _openCts), created at the start of PlayInternal and cancelled only by a newer Play()/Stop()/Dispose. Keep _skipCts for fades and the PrepareNext parse. Alternatively, have CancelPreparedNext cancel only the prepare's token, not _skipCts. Log when the parse is aborted.

**Verifier votes**

- **confirmed** (refute lens): The mechanism is as described. CancelPreparedNext (VlcAudioPlayer.cs:2510-2513) calls CancelSkipCts on the caller's thread, not under _playbackLock, so do Pause (4226) and Seek (4364). CancelSkipCts (4964) cancels the same _skipCts that PlayInternal created at 2625 and uses at 2803-2815. The parse wait catches OperationCanceledException and returns at 2814 after the session bump (2622), the position-timer stop (2623) and setting _currentMediaPath (2642), but before the old player is stopped (2833-2838). The VM calls CancelAutoMixTransition, and so CancelPreparedNext at 3023, on every queue edit (AddNext 1193, AddToQueue 1202, RemoveFromQueue 1229, etc.), and calls Pause at 449 and Seek at 628/1927. Downgraded to medium because it needs a user action inside the header-parse window, which is tens of ms on typical disks and only long on spun-down or network storage. Minor nuance: a Seek into the start region (4421-4433) calls Play(_currentMediaPath), which by then is the new path, so that one case recovers.
- **confirmed** (impact lens): The race is real. PlayInternal replaces _skipCts at 2624-2628, bumps the session and stops the position timer at 2622-2623, sets _currentMediaPath at 2642, then waits on a token linked to _skipCts at 2803-2808. If that token is cancelled, it returns at 2810-2814 without stopping or replacing the old media. CancelSkipCts (4964) cancels the live _skipCts from the UI thread, and it is called by CancelPreparedNext (2513), Pause (4226) and Seek (4364). CancelAutoMixTransition calls CancelPreparedNext at 3023 and runs on AddNext/AddToQueue (1193, 1202) and every other queue edit. PlayPause calls Pause at 449, and SeekToPosition calls Seek at 628. None of these callers checks whether a load is in flight. I found no automatic trigger right after PlayTrack: RefillRadioIfNeeded appends without cancelling (2438), and ReplaceQueueAndPlay cancels before Play (1151). So a user action has to land inside the parse window, which is short on an SSD and long only on a spun-down HDD or network drive; medium, not high. The outcome also differs by platform. On Windows the gapless engine is on by default (666-667), and EngineClearAll (2761, 3999-4003) abandons the active segment before the parse. There the result is silence with a frozen timeline under B's title, not A playing on. 'Old song keeps playing' applies to the non-engine paths (macOS/Linux, the WASAPI callback sink, or NOCTIS_GAPLESS_ENGINE=0). The later TrackEnded, which skips past B, and Pause/Resume acting on A's media (4243, 4299-4307) happen as described.

### A26 — Seeking a restored (not yet played) track is dropped and Play jumps back to the stale restored position; after stop-after-current, a seek plays audio while the UI stays Stopped

- **Location:** `src/Noctis/ViewModels/PlayerViewModel.cs:628` (also: `src/Noctis/ViewModels/PlayerViewModel.cs:1924`, `src/Noctis/ViewModels/PlayerViewModel.cs:534`, `src/Noctis/ViewModels/PlayerViewModel.cs:595`, `src/Noctis/ViewModels/PlayerViewModel.cs:1560`, `src/Noctis/ViewModels/PlayerViewModel.cs:2015`, `src/Noctis/Services/VlcAudioPlayer.cs:4361`, `src/Noctis/Services/VlcAudioPlayer.cs:4381`, `src/Noctis/Services/SmtcService.cs:182`, `src/Noctis/Services/MprisService.cs:364`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: high)

**Evidence**

```
PlayerViewModel.cs:623-628  Position = target; PositionText = FormatTime(target); PositionFraction = fraction; ... _audioPlayer.Seek(target);
1572  State = PlaybackState.Stopped; // user must press play
1576-1577  _resumePositionMs = (long)(positionSeconds * 1000); _resumeTrackId = track.Id;
2015-2023  var resumeMs = Interlocked.Exchange(ref _resumePositionMs, -1); ... else if (resumeMs > 0 && track.Id == _resumeTrackId ...) seekMs = resumeMs;
VlcAudioPlayer.cs:4361  if (_disposed || _currentMedia == null) return;
VlcAudioPlayer.cs:4381-4396  if ((state == VLCState.Ended || state == VLCState.Stopped) && ...) { ... Play(_currentMediaPath); return; }
```

**Why it is a bug:** (a) Restored session: RestoreQueueStateAsync leaves the VM Stopped with no media loaded. Dragging or clicking the timeline, SkipBack/SkipForward (SeekBy), Previous-restart (line 534) and SMTC/MPRIS/macOS scrubs all move Position and the slider to the target, then call _audioPlayer.Seek. Seek returns silently at 4361 because _currentMedia is null. _resumePositionMs still holds the saved offset, so pressing Play makes PlayTrack apply the OLD restored position: the slider and the audio jump back to where the last session ended. (b) After stop-after-current (VM Stopped, media Ended), Seek takes the Ended branch and calls Play(). Audio starts while the VM stays Stopped: the play icon shows, SMTC reports Stopped, and the gapless, AutoMix and natural-end logic all require State==Playing. Pressing Play then runs PlayTrack(CurrentTrack) and restarts from 0:00.

**Impact:** This is the reported restore desync: after launch, the user drags the timeline (for example back to 0:00) and presses Play, but the song resumes at the old saved point and the slider snaps there. After 'stop after current', touching the timeline starts audio behind a Stopped UI.

**Proposed fix (small):** In SeekToPosition, EndSeek (and its timer callback) and Previous's restart branch, add a Stopped path. When `State == PlaybackState.Stopped && CurrentTrack != null`, update the UI fields and set `_resumePositionMs = target > TimeSpan.Zero ? (long)target.TotalMilliseconds : -1; _resumeTrackId = CurrentTrack.Id;` instead of calling `_audioPlayer.Seek`. PlayTrack already consumes the resume target.

**Verifier votes**

- **confirmed** (refute lens): (a) RestoreQueueStateAsync (PlayerViewModel.cs:1560-1577) sets CurrentTrack, Duration, State=Stopped and _resumePositionMs without loading any media. _currentMedia is assigned only in PlayInternal or a handoff (VlcAudioPlayer.cs:2843 etc.). SeekToPosition (614-632) passes its guard (CurrentTrack and Duration are set), updates the UI and calls _audioPlayer.Seek. Seek returns at 4361 because _currentMedia is null. PlayPause in the Stopped state (461-466) calls PlayTrack(CurrentTrack), which consumes the stale _resumePositionMs at 2015-2023 and sets Position back to it. The slider is enabled whenever CurrentTrack is non-null (PlaybackBarView.axaml:1237), so this is reachable. (b) After a stop-after-current via TrackEnded, the media is still loaded and VLC is Ended. Seek takes the Ended/Stopped branch (4380-4397) and calls Play(), while SeekToPosition never changes State, so audio plays behind a Stopped UI.
- **confirmed** (impact lens): (a) RestoreQueueStateAsync (1560-1577) leaves State=Stopped and sets _resumePositionMs, and nothing loads media into the player, so _currentMedia stays null. The only caller is MainWindowViewModel.cs:694, and there is no preload. The seek slider has no IsEnabled gate (PlaybackBarView.axaml:1211-1217). OnPositionFractionChanged, EndSeek (1886-1929), SeekToPosition (614-632) and Previous's restart branch (530-541) move Position in the UI and then call _audioPlayer.Seek. Seek returns at VlcAudioPlayer.cs:4361 because _currentMedia is null. _resumePositionMs is written only at 1576 and read only at 2015, and no seek path updates it. Pressing Play therefore runs PlayTrack, which applies the old resume offset (2020-2045). (b) After a stop-after-current halt through TrackEnded, the VM is Stopped and VLC reports Ended. Seek takes the Ended/Stopped branch (4380-4397) and calls Play(_currentMediaPath), so audio starts while State stays Stopped. Both paths are deterministic but uncommon: a seek before the first Play after launch, or a seek after a stop-after halt. Medium fits better than high.

### A27 — A restored mute state reaches only the UI, not the audio player: the app shows Muted but plays audibly

- **Location:** `src/Noctis/ViewModels/PlayerViewModel.cs:1537` (also: `src/Noctis/ViewModels/PlayerViewModel.cs:1686`, `src/Noctis/Services/VlcAudioPlayer.cs:906`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: high)

**Evidence**

```
PlayerViewModel.cs:1535-1537  RepeatMode = state.RepeatMode; IsShuffleEnabled = state.IsShuffleEnabled; IsMuted = state.IsMuted;
1674  partial void OnIsMutedChanged(bool value) => RefreshSignalPath();
635-638  private void ToggleMute() { IsMuted = !IsMuted; _audioPlayer.IsMuted = IsMuted; }
(grep: _audioPlayer.IsMuted is written only at 638 and 1690)
```

**Why it is a bug:** The player mute is pushed only by ToggleMute and UnmuteForAdjust. The restore sets the generated property, whose change handler only refreshes the signal-path badge. VlcAudioPlayer._userMuted therefore stays false, and PlayInternal re-asserts that unmuted intent on every play. No other code syncs it (grep across src/Noctis).

**Impact:** If the user quit while muted, then on the next launch the island, mini player and signal-path badge all show 'Muted', but pressing Play plays at the full slider volume. The first press of the mute button then 'unmutes' (the UI flips while the audio is unchanged), so muting takes two presses.

**Proposed fix (trivial):** Push the mute from the property change: `partial void OnIsMutedChanged(bool value) { _audioPlayer.IsMuted = value; RefreshSignalPath(); }`. The existing explicit writes in ToggleMute and UnmuteForAdjust become redundant but harmless. Alternatively, add `_audioPlayer.IsMuted = IsMuted;` after line 1537.

**Verifier votes**

- **confirmed** (refute lens): PlayerViewModel.cs:1537 sets IsMuted = state.IsMuted. OnIsMutedChanged (1674) only calls RefreshSignalPath. A grep for IsMuted across src/*.cs shows _audioPlayer.IsMuted is written only at 638 (ToggleMute) and 1690 (UnmuteForAdjust), and no other startup code syncs it. VlcAudioPlayer._userMuted (803) starts false, and ScheduleMuteIntentReassert (4040-4058) pushes that unmuted intent on every play. So after a restore the UI shows Muted while audio plays, and the first ToggleMute sets the VM to false and the player to false, changing nothing audible. Downgraded to medium because it only happens when the user quit while muted, and audio plays at the slider volume, not full volume.
- **confirmed** (impact lens): I re-traced the path and found no mitigation. PlayerViewModel.cs:63 declares IsMuted as a plain [ObservableProperty]. Its handler at :1674 only calls RefreshSignalPath(). The restore at :1537 sets that generated property. Across src/, _audioPlayer.IsMuted is written only at :638 (ToggleMute) and :1690 (UnmuteForAdjust). MainWindowViewModel.cs:688-696 applies the volume and then calls RestoreQueueStateAsync. That call is gated by RestoreLastTrackOnStartup, which defaults to true (AppSettings.cs:227). Nothing after it syncs the mute. The value is persisted at :1420 and :1478, so it round-trips. In VlcAudioPlayer, _userMuted (:803) stays false. On Windows the gapless engine is on by default (:666-667), so SinkOwnsMute holds and ApplyMuteToOwner routes the false value to the engine gate (:941-948): the audio is audible. On the native path (macOS/Linux), ScheduleMuteIntentReassert (:4044-4053) forces _player.Mute back to _userMuted=false, so a mute restored by the OS would be undone as well. ToggleMute then sets IsMuted=false and _audioPlayer.IsMuted=false, which changes nothing audible, so muting takes two presses as described. The comment at :1533-1534 shows mute persistence was added recently, so this is a regression from that fix. The high severity stands: it happens on every launch after quitting while muted, and the audio plays at the slider volume while the UI says Muted. The proposed fix of pushing the mute from OnIsMutedChanged is sound.

### A28 — Removing or deleting the paused/stopped current track auto-starts the next track (surprise playback, including right after launch)

- **Location:** `src/Noctis/ViewModels/PlayerViewModel.cs:3161` (also: `src/Noctis/ViewModels/PlayerViewModel.cs:2084`, `src/Noctis/ViewModels/PlayerViewModel.cs:2252`, `src/Noctis/ViewModels/PlayerViewModel.cs:1119`, `src/Noctis.Core/Services/LibraryService.cs:1318`, `src/Noctis/ViewModels/MainWindowViewModel.cs:699`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
PlayerViewModel.cs:3157-3169
  if (CurrentTrack is { IsExternal: false } && _library.GetTrackById(CurrentTrack.Id) == null)
  {
      if (UpNext.Count > 0)
      {
          AdvanceQueue();
      }
      else { StopAndClear(); }
  }
1998 (PlayTrack)  State = PlaybackState.Playing;
```

**Why it is a bug:** AdvanceQueue() defaults to QueueAdvanceReason.Natural, and PlayTrack always sets Playing and calls Play, whatever the previous state was. At startup the restored track is Stopped ('user must press play'), and ScanOnStartup is on by default (AppSettings.cs:89). If the restored file was moved or deleted while the app was closed, the scan's final publish removes it, OnLibraryUpdated fires, and the next queued song starts playing by itself. The same happens when the user pauses and then removes the paused track's album from its album page (RemoveTracksAsync raises LibraryUpdated). Because the reason is Natural, Repeat One also replays the removed track from 0 (2252-2256), a pending StopAfterCurrentTrack is consumed with State=Stopped while the removed track keeps playing, and Autoplay may fire.

**Impact:** Audio starts without any user action, either a few seconds after launch or while paused. Repeat One restarts a track that was just removed.

**Proposed fix (small):** If the current track is deleted and `State != PlaybackState.Playing`, stop the player and load UpNext[0] without playing, mirroring the restore path: set CurrentTrack/Duration/Position, keep State Paused/Stopped and call `_audioPlayer.Stop()`. If State is Playing, use AdvanceQueue(QueueAdvanceReason.UserSkip) so Repeat One, stop-after and autoplay don't apply.

**Verifier votes**

- **confirmed** (refute lens): Confirmed. At PlayerViewModel.cs:3158-3164, when the current track is gone from the library and UpNext is not empty, the code calls AdvanceQueue() with the default reason Natural (2084). AdvanceQueueCore then calls PlayTrack(next) (2282-2284), which always sets State=Playing (1998) and calls _audioPlayer.Play (2047). It never checks whether the player was paused or stopped. Startup path: RestoreQueueStateAsync resolves the saved track through _library.GetTrackById. LoadAsync (LibraryService.cs:1476-1502) does not check that files still exist, so the moved track is restored with State=Stopped (1572). Then ScanOnStartup (MainWindowViewModel.cs:699, default true at AppSettings.cs:89) reaches the authoritative publish (LibraryService.cs:555-560, _publishingPartial already false at 383). That drops the missing file and fires LibraryUpdated, and playback starts by itself. The paused-then-remove path goes through RemoveTracksAsync, which raises LibraryUpdated at LibraryService.cs:1318. With Repeat One and reason Natural, the removed track is replayed (2252-2256). StopAfterCurrentTrack sets State=Stopped without calling _audioPlayer.Stop() (2231-2243). One small inaccuracy: this path runs only when UpNext.Count > 0, so autoplay can fire only in the rare case where PruneBlockedExplicit empties the queue.

### A29 — Repeat All wrap replays a stale start-time snapshot: it ignores Shuffle (second pass plays in album order with Shuffle lit) and later queue edits

- **Location:** `src/Noctis/ViewModels/PlayerViewModel.cs:2292` (also: `src/Noctis/ViewModels/PlayerViewModel.cs:1169`, `src/Noctis/ViewModels/PlayerViewModel.cs:1190`, `src/Noctis/ViewModels/PlayerViewModel.cs:1199`, `src/Noctis/ViewModels/PlayerViewModel.cs:1236`, `src/Noctis/ViewModels/PlayerViewModel.cs:1246`, `src/Noctis/ViewModels/PlayerViewModel.cs:717`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
PlayerViewModel.cs:2287-2305
  else if (RepeatMode == RepeatMode.All && (_repeatCycleTracks.Count > 0 || History.Count > 0))
  {   var allTracks = _repeatCycleTracks.Count > 0 ? new List<Track>(_repeatCycleTracks) : History.Reverse().ToList();
      ...
      History.Clear(); _queueHistoryDepth = 0;
      _originalQueue.Clear(); // clear stale shuffle state to prevent wrong restore
      ...
      UpNext.ReplaceAll(allTracks.Skip(1).ToList());
      PlayTrack(allTracks[0]);
1169  _repeatCycleTracks = tracks.Skip(startIndex).Concat(tracks.Take(startIndex)).ToList();
```

**Why it is a bug:** _repeatCycleTracks is written only by ReplaceQueueAndPlay (1169) and the restore (1545). AddNext, AddToQueue, AddRangeToQueue, RemoveFromQueue, ClearQueue, ToggleShuffle, autoplay and radio refills never update it, and StopAndClear doesn't clear it. At the wrap it is replayed in its original order, _originalQueue is cleared and IsShuffleEnabled stays true. Example: play an album, turn Shuffle on, set Repeat All. After the shuffled pass the second pass plays in album order while the Shuffle button is lit, and toggling Shuffle off then does nothing because _originalQueue is empty. Songs added with 'Add to queue' disappear from the next pass, and removed songs come back.

**Impact:** Shuffle + Repeat All becomes sequential after the first cycle. Queue edits are silently undone at every wrap.

**Proposed fix (small):** At the wrap, if IsShuffleEnabled, set `_originalQueue = allTracks` and reshuffle with ShuffleHelper.WeightedShuffle before ReplaceAll. Keep _repeatCycleTracks in sync with queue edits: append in AddToQueue/AddRangeToQueue/AddNext, remove in RemoveFromQueue/RemoveManyFromQueue, clear in ClearQueue/StopAndClear.

**Verifier votes**

- **confirmed** (refute lens): Confirmed. _repeatCycleTracks is written only at PlayerViewModel.cs:1169 (ReplaceQueueAndPlay, which also sets IsShuffleEnabled=false at 1165) and at 1545 (restore). None of these update it: AddNext (1190), AddToQueue (1199), AddRangeToQueue (1208), RemoveFromQueue (1224), ClearQueue (1236), StopAndClear (1246) and ToggleShuffle (718-784). ToggleShuffle only shuffles UpNext and saves _originalQueue. At the wrap (2287-2305), the saved cycle is replayed in its original album order, _originalQueue is cleared, and IsShuffleEnabled is never checked or reset. So Shuffle on + Repeat All plays the second pass in order while the Shuffle button stays lit. Queue additions and removals made during the pass are undone at the wrap.

### A30 — At playback speed above 1x the timeline (and every tick-driven transition) freezes for seconds after each track start or seek

- **Location:** `src/Noctis/ViewModels/PlayerViewModel.cs:2611` (also: `src/Noctis/ViewModels/PlayerViewModel.cs:177`, `src/Noctis/ViewModels/PlayerViewModel.cs:581`, `src/Noctis/Services/VlcAudioPlayer.cs:2241`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
PlayerViewModel.cs:2608-2614
  if (msSinceSeek < TrackStartStalePositionGuardMs)   // 9000
  {
      var expectedSeconds = _lastCommittedSeekTarget.TotalSeconds;
      var maxPlausibleSeconds = expectedSeconds + (msSinceSeek / 1000d) + 4;
      if (latest.TotalSeconds > maxPlausibleSeconds)
          return;
  }
1995  _lastSeekTime = DateTime.UtcNow;   (PlayTrack: every track start)
```

**Why it is a bug:** The stale-position guard assumes media time advances at 1x. At rate r, a real tick is target + r*t and is rejected once (r-1)*t > 4 s. At 2x (the island speed menu goes up to 200) every tick between about 4 s and 9 s after any seek or track start is dropped. The 9 s window is re-armed by PlayTrack and by every seek (so each Skip +15 s press re-arms it). While ticks are dropped, OnPositionChanged also skips the AutoMix/gapless/natural-end/StopTime checks. VLC's rate (legacy path) and the engine's stretch provider both advance media time at the chosen rate.

**Impact:** Podcast and audiobook users at 1.75-2x see the timeline and synced lyrics freeze for 3-5 s after every track start and skip, then jump about 10 s.

**Proposed fix (trivial):** Scale the elapsed term by the rate: `var maxPlausibleSeconds = expectedSeconds + (msSinceSeek / 1000d) * Math.Max(1.0, PlaybackRate) + 4;`.

**Verifier votes**

- **confirmed** (refute lens): Confirmed. The guard at PlayerViewModel.cs:2608-2614 (TrackStartStalePositionGuardMs=9000 at 353) allows target + elapsed wall-clock seconds + 4. Its window is re-armed by PlayTrack (1995-1996) and by every seek (629-630, reached through SkipForward/SeekBy at 591-601). Media position advances at the playback rate on both paths. Legacy path: _player.SetRate(rate) at VlcAudioPlayer.cs:2286 and _player.Time at 4730. Engine path: the segment's PositionMs is base + consumed source frames (GaplessSpliceCore.cs:75-80), and the stretch provider consumes source frames at the chosen rate (VlcAudioPlayer.cs:2280). The rate goes up to 200% (SetPlaybackRate, 583). At 2x, a tick is rejected once 2t > t+4, so ticks from about 4-5 s to 9 s after each track start or seek are dropped. That return also skips everything later in the callback. At 1.75x the freeze is about 3.5 s, and at 1.5x only about 1 s. The fix rests on one inference I could not read in the code: that the stretch provider's source consumption rate equals the chosen rate.

### A31 — A playback-error cascade (drive offline) or one error on the last track wipes the whole queue and history, and the empty queue is then persisted

- **Location:** `src/Noctis/ViewModels/PlayerViewModel.cs:3084` (also: `src/Noctis/ViewModels/PlayerViewModel.cs:1246`, `src/Noctis/ViewModels/PlayerViewModel.cs:3081`, `src/Noctis/ViewModels/MainWindowViewModel.cs:990`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** data-loss · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
PlayerViewModel.cs:3072-3092
  if (++_consecutivePlaybackErrors >= MaxConsecutivePlaybackErrors)
  {   ...
      if (UnreachableRootHint(CurrentTrack?.FilePath, Directory.Exists) is { } hint) DebugLog.Write("Audio", hint);
      _consecutivePlaybackErrors = 0;
      StopAndClear();
      return;
  }
  if (UpNext.Count > 0) AdvanceQueue(QueueAdvanceReason.Error);
  else StopAndClear();
1259-1265 (StopAndClear)  CurrentTrack = null; UpNext.Clear(); History.Clear(); ... _originalQueue.Clear();
```

**Why it is a bug:** The circuit breaker is meant to stop, but it reuses StopAndClear, which throws away the current track, Up Next, History and the preceding/shuffle state. Its own hint (3081) names the typical cause: the music drive is asleep, unplugged or remounted. The next SaveQueueStateInBackground or the shutdown SaveQueueStateAsync then writes that empty queue, so the next launch restores nothing. A single unreadable last track (UpNext empty) also wipes History.

**Impact:** If the user presses Play while the USB/NAS drive holding the music is unavailable, a long queue is permanently lost after five quick failures.

**Proposed fix (small):** On the cascade and on the error-with-empty-queue path, stop without clearing: `_audioPlayer.Stop(); State = PlaybackState.Stopped;` Keep CurrentTrack, UpNext and History, putting the failed tracks back at the front of UpNext (or leaving the first failed track as CurrentTrack) so Play works once the drive is back.

**Verifier votes**

- **confirmed** (refute lens): Confirmed. OnPlaybackError (PlayerViewModel.cs:3072-3092) calls StopAndClear on the 5th consecutive error, and also on any error when UpNext is empty. StopAndClear (1246-1265) sets CurrentTrack=null and clears UpNext, History, _precedingInQueue and _originalQueue. With the drive offline, VlcAudioPlayer.Play fails File.Exists and raises PlaybackError immediately (VlcAudioPlayer.cs:2570-2572). Each error then advances the queue (3089-3090), so pressing Play (465) cascades through five tracks and wipes the queue. The error counter resets only on a real position callback (2594). The shutdown save (MainWindowViewModel.cs:990 -> SaveQueueStateAsync at PlayerViewModel.cs:1410-1424) writes the empty CurrentTrack, UpNext and History unconditionally, so the next launch restores nothing. The scan-abort guard (LibraryService.cs:414-441) protects the library, not the queue. This is queue/session loss, not library data loss, so medium fits.

### A32 — Shutdown saves the queue position and volume last, behind steps that can use up the 4 s deadline; the volume is persisted only on graceful exit

- **Location:** `src/Noctis/ViewModels/MainWindowViewModel.cs:976` (also: `src/Noctis/App.axaml.cs:225`, `src/Noctis/App.axaml.cs:245`, `src/Noctis/ViewModels/PlayerViewModel.cs:497`, `src/Noctis/ViewModels/SettingsViewModel.cs:2876`, `src/Noctis/ViewModels/MainWindowViewModel.cs:688`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** data-loss · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
MainWindowViewModel.cs:951   try { Player.PauseForShutdown(); }   (no queue snapshot)
957   try { await Settings.StopNoctisServerAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
964   try { await _library.PauseActiveScanForShutdownAsync(TimeSpan.FromSeconds(5)); }
971   await FlushPendingScrobblesAsync();   (waits up to 3 s, line 2988)
976   Settings.SetVolume(Player.Volume);
990   try { await Player.SaveQueueStateAsync(); }
App.axaml.cs:225  try { await mainVm.ShutdownAsync().WaitAsync(ShutdownSaveDeadline); }   (245: 4 s)
```

**Why it is a bug:** The pre-save steps can take up to 2 + 5 + 3 = 10 s, but App.axaml.cs abandons ShutdownAsync after 4 s and calls desktop.Shutdown(). If the user quits during a library scan (ScanOnStartup is on by default) or while scrobbles are pending on a slow network, SetVolume/Settings.SaveAsync, the play-history flush, the queue snapshot and the per-play library flush never run. PauseForShutdown doesn't snapshot, so the last saved position is from the last pause or track start. SettingsViewModel._volume is written only here (SettingsViewModel.cs:2876), so any crash or kill also reverts the volume to the value from the previous graceful exit, which may be louder than what the user last set.

**Impact:** The next launch restores the track at an older position (often 0:00, the last track start), which is another source of the restore desync. Recent play counts can be lost, and the volume can come back louder than the user left it.

**Proposed fix (small):** Right after PauseForShutdown, first run `Settings.SetVolume(Player.Volume)`, `await Settings.SaveAsync()`, `await Player.SaveQueueStateAsync()` and `await Player.FlushPendingLibrarySaveAsync()`, and only then the plugin/server/scan/scrobble steps. Also persist the volume on slider release (CommitVolume) with a short debounce so a crash doesn't revert it.

**Verifier votes**

- **confirmed** (refute lens): The code matches. MainWindowViewModel.cs:951-994 runs PauseForShutdown (PlayerViewModel.cs:497-503, pause only, no snapshot), then the server stop (2 s cap), then PauseActiveScanForShutdownAsync (5 s cap). That call cancels the scan and waits for its checkpoint: RebuildIndexes plus a library SaveAsync (LibraryService.cs:638-668). Next comes the scrobble flush (3 s cap, :2988) and the tag-writer flush (3 s cap, :985). Only after all of that do SetVolume/SaveAsync (:976), SaveQueueStateAsync (:990) and FlushPendingLibrarySaveAsync (:994) run. App.axaml.cs:225 abandons ShutdownAsync after ShutdownSaveDeadline = 4 s (:245), so the scan step alone can use up the whole deadline. The volume is persisted only through SettingsViewModel.SetVolume (:2876, re-applied at :2860), and its only caller is MainWindowViewModel.cs:976. Correction to the impact: queue snapshots also happen on pause (PlayerViewModel.cs:453), on track start (:2072) and on close-to-tray (MainWindow.axaml.cs:1056). So the loss is limited to the position and state since the last track start or pause, not the whole queue.

### A34 — MPRIS Shuffle writes flip the flag without shuffling; Shuffle/LoopStatus changes are never signalled

- **Location:** `src/Noctis/Services/MprisService.cs:618` (also: `src/Noctis/ViewModels/PlayerViewModel.cs:717`, `src/Noctis/Services/MprisService.cs:621`, `src/Noctis/Services/MprisService.cs:190`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** correctness · **Platforms:** linux
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
MprisService.cs:615-619
  case "Shuffle":
  {
      var shuffle = value.GetBool();
      _s.OnUiThread(() => _s._player.IsShuffleEnabled = shuffle);
      break;
  }
MprisService.cs:192-209  OnPlayerPropertyChanged handles only State, CurrentTrack, CurrentArtPath, Volume
```

**Why it is a bug:** Only ToggleShuffle (PlayerViewModel.cs:717) reorders Up Next and saves _originalQueue, and there is no OnIsShuffleEnabledChanged hook. A shuffle toggle from a GNOME/KDE widget or `playerctl shuffle On` lights the indicator while the queue keeps album order. A later in-app toggle-off then finds _originalQueue empty and does nothing. Separately, in-app Shuffle/Repeat changes emit no PropertiesChanged, so desktop widgets show stale state.

**Impact:** On Linux, Shuffle from the desktop's media controls does nothing except change the icon.

**Proposed fix (small):** Use `if (_s._player.IsShuffleEnabled != shuffle) _s._player.ToggleShuffleCommand.Execute(null);` and route LoopStatus the same way (set the mode and call CancelAutoMixTransition, as CycleRepeat does). Add IsShuffleEnabled and RepeatMode cases to OnPlayerPropertyChanged that emit Shuffle/LoopStatus PropertiesChanged.

**Verifier votes**

- **confirmed** (refute lens): MprisService.cs:615-619 only assigns _player.IsShuffleEnabled. That is a plain [ObservableProperty] (PlayerViewModel.cs:106) with no OnIsShuffleEnabledChanged partial; only OnRepeatModeChanged exists (:3007). The only code that reorders UpNext and saves _originalQueue is ToggleShuffle (:717-784). So an MPRIS Shuffle=true lights the indicator and leaves the queue order unchanged, and a later in-app toggle-off hits _originalQueue.Count == 0 (:744) and does nothing. OnPlayerPropertyChanged (MprisService.cs:190-210) handles only State, CurrentTrack, CurrentArtPath and Volume, and EmitPropertiesChanged (:272-307) can only write PlaybackStatus, Metadata and Volume. Shuffle and LoopStatus changes are therefore never signalled. This is Linux-only.

### P04 — Linux: case-folded track ids strand a track after a case-only rename (it points at a path that no longer exists, through every rescan) and merge files whose names differ only by case

- **Location:** `src/Noctis.Core/Services/LibraryService.cs:2869` (also: `src/Noctis.Core/Services/WatchDebouncer.cs:33`, `src/Noctis.Core/Services/LibraryService.cs:1045`, `src/Noctis.Core/Services/LibraryService.cs:1149`, `src/Noctis.Core/Services/LibraryService.cs:556`)
- **Area / sweep:** Cross-platform / xplat-general · **Category:** cross-platform · **Platforms:** linux, macos
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Prior audit:** AUDIT_2026-07-24.md (ComputeFileId lowercases the path)

**Evidence**

```
private static Guid ComputeFileId(string filePath)
{
    var normalized = filePath.Replace('\\', '/').ToLowerInvariant();
...
if (trackIndexSnapshot.TryGetValue(ComputeFileId(filePath), out existing))
{
    if (entry.LastWriteTimeUtc == existing.LastModified && entry.Length == existing.FileSize)
    {
        newTracks.Add(existing);
```

**Why it is a bug:** Ids are lower-cased on every OS. On case-sensitive ext4/btrfs, a case-only rename such as '01 song.flac' → '01 Song.flac' (tag-based renamers like Picard or beets do this) does not change the mtime or size. The scan fast path (LibraryService.cs:298-305) finds the same id and re-adds `existing` with its OLD FilePath, and the no-change exit at 504 keeps it that way permanently. The watcher does not recover it either: WatchDebouncer's _pending map is OrdinalIgnoreCase (WatchDebouncer.cs:33), so RecordRename's Deleted(old) and CreatedOrChanged(new) collapse onto the old key. Drain then imports the old path, and ImportFilesAsync filters it out with .Where(File.Exists) (LibraryService.cs:1045). Separately, two different files whose names differ only by case get the same id and are deduped at 236/556 (GroupBy Id → First). One of them silently disappears, and which one depends on parallel scan order.

**Impact:** On Linux (and case-sensitive macOS volumes), a track renamed only in case becomes unplayable: VLC is handed a non-existent path, and rescans never fix it until the file content changes. Folders or files that differ only by case show only one of them in the library.

**Proposed fix (small):** Minimal and safe on every OS: in the scan fast path (298-305) and the import fast path (1149-1156), when existing.FilePath differs from the enumerated path under StringComparison.Ordinal, set existing.FilePath = filePath and count it as changed so it is persisted. Make WatchDebouncer use PathComparison.Comparer. For the collision case, on Linux use a case-preserving hash when the lowered id is already claimed by a different ordinal path. Changing ComputeFileId outright needs an id migration, because playlists and the journal are keyed by id.

**Verifier votes**

- **confirmed** (refute lens): LibraryService.cs:2869-2875: ComputeFileId lowercases the path on every OS. In the scan fast path (lines 298-305), a case-only rename matches the old id with the same mtime and size, so `existing` is re-added with its old FilePath. The no-change exit at 503-543 then keeps that entry. Nothing ever rewrites FilePath. WatchDebouncer.cs:33 uses OrdinalIgnoreCase, and in RecordRename (lines 56-60) the indexer set keeps the original key. So Drain yields ToImport=[oldPath], which ImportFilesAsync drops at 1045 (.Where(File.Exists)). PathComparison.cs confirms Linux paths are case-sensitive, which the id and debouncer ignore. Two files whose names differ only by case collide on one id, and the scan keeps only one: GroupBy(Id).First() at 556, plus the progressive-publish path at 236. The collision only happens on Linux or case-sensitive volumes after a case-only rename, so medium.

### P05 — Linux: folder watching walks the whole music tree synchronously on the UI thread at every launch (inotify needs one watch per directory, added inside EnableRaisingEvents)

- **Location:** `src/Noctis.Core/Services/LibraryWatcherService.cs:81` (also: `src/Noctis/ViewModels/MainWindowViewModel.cs:716`, `src/Noctis/ViewModels/MainWindowViewModel.cs:493`, `src/Noctis/ViewModels/SettingsViewModel.cs:3570`, `src/Noctis/Views/MainWindow.axaml.cs:495`)
- **Area / sweep:** Cross-platform / xplat-general · **Category:** ui-freeze · **Platforms:** linux
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
var w = new FileSystemWatcher(folder)
{
    IncludeSubdirectories = true,
    ...
};
...
w.EnableRaisingEvents = true;
_watchers.Add(w);
```

**Why it is a bug:** Refresh() runs on the UI thread. MainWindow.axaml.cs:495 awaits vm.InitializeAsync() in the window-loaded handler, and InitializeAsync (MainWindowViewModel.cs:716) calls Refresh() after awaits that have no ConfigureAwait(false). It is also called from the MusicFoldersChanged handler (MainWindowViewModel.cs:493) and the Watch Folders toggle (SettingsViewModel.cs:3570). On Linux, EnableRaisingEvents=true builds RunningInstance, whose constructor synchronously calls AddDirectoryWatchUnlocked. That call recurses through Directory.EnumerateDirectories and makes one inotify_add_watch per subdirectory on the caller's thread (https://raw.githubusercontent.com/dotnet/runtime/release/10.0/src/libraries/System.IO.FileSystem.Watcher/src/System/IO/FileSystemWatcher.Linux.cs). Windows (ReadDirectoryChangesW with a subtree flag) and macOS (FSEvents) do not enumerate the tree, so this cost exists only on Linux. WatchFoldersEnabled defaults to true (AppSettings.cs:95).

**Impact:** On Linux, launch blocks the UI for as long as it takes to list every directory under the music roots: one readdir plus one syscall per folder. That is negligible on a warm SSD cache. On a cold HDD or an NFS/SMB mount with thousands of album folders it can be several seconds, and it repeats whenever a music folder is added.

**Proposed fix (trivial):** Run the watcher rebuild off the UI thread: `_ = Task.Run(() => App.Services?.GetService<ILibraryWatcherService>()?.Refresh());` at the three call sites. Alternatively, have Refresh() move its body to the thread pool; it already serializes on _gate.

**Verifier votes**

- **confirmed** (refute lens): LibraryWatcherService.cs:67-82: Refresh() creates the FSW with IncludeSubdirectories=true and sets EnableRaisingEvents=true inside lock(_gate). In the .NET release/10.0 FileSystemWatcher.Linux.cs, StartRaisingEvents (lines 20-76) constructs RunningInstance. Its constructor (line 309) calls AddDirectoryWatchUnlocked, which runs Directory.EnumerateDirectories and recurses one inotify_add_watch plus lstat per subdirectory (lines 353-485) on the caller's thread. All call sites run on the UI thread. MainWindow.axaml.cs:495 awaits vm.InitializeAsync() from the window-loaded handler. MainWindowViewModel.cs:663-716 has only plain awaits before Refresh(), so it resumes on the UI context. MusicFoldersChanged (MainWindowViewModel.cs:493) is raised after a plain await SaveAsync() at SettingsViewModel.cs:5737/5770. OnWatchFoldersEnabledChanged (SettingsViewModel.cs:3570) is a property-change handler. WatchFoldersEnabled defaults to true (AppSettings.cs:95). The freeze only matters on Linux with cold or remote storage, so medium is right.

### P06 — Linux: hitting the inotify watch limit starts a re-entrant Refresh() storm from inside FileSystemWatcher construction

- **Location:** `src/Noctis.Core/Services/LibraryWatcherService.cs:261` (also: `src/Noctis.Core/Services/LibraryWatcherService.cs:84`, `src/Noctis.Core/Services/LibraryWatcherService.cs:55`)
- **Area / sweep:** Cross-platform / xplat-general · **Category:** correctness · **Platforms:** linux
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
private void OnError(object sender, ErrorEventArgs e)
{
    var ex = e.GetException();
    DebugLogger.Error(DebugLogger.Category.Error, "LibraryWatcher",
        $"watcher error: {ex?.Message}");

    // Rebuild the watcher set.
    Refresh();
```

**Why it is a bug:** On Linux, when inotify_add_watch fails with ENOSPC (fs.inotify.max_user_watches reached), .NET calls watcher.OnError synchronously from AddDirectoryWatchUnlocked, which runs inside EnableRaisingEvents. It then carries on with the next subdirectory, so it fires once per failing subdirectory (FileSystemWatcher.Linux.cs, release/10.0). OnError calls the handler directly when SynchronizingObject is null (FileSystemWatcher.cs, release/10.0). So OnError→Refresh() runs on the same thread, inside the outer Refresh's lock(_gate), which is re-entrant. The nested Refresh builds a new watcher whose root add fails immediately (the outer, half-built watcher still holds the watches). That raises OnError→Refresh again, nesting until inotify_init fails with EMFILE (max_user_instances, default 128), which the catch at line 84 swallows. The whole chain then repeats for every remaining failing subdirectory. Each unwinding level adds its zero-watch watcher to _watchers (line 82).

**Impact:** Mostly affects Linux users on older kernels or distros with the 8192 watch default, with large libraries or other watch-heavy apps (IDEs, sync clients). On the startup path it freezes the UI (see the previous finding) while thousands of watchers are created and disposed. It can leave about 127 idle watchers each holding an inotify instance, which exhausts the user's inotify instance quota so other apps (file managers, editors) fail to watch files. Watching ends up partial anyway.

**Proposed fix (small):** Never rebuild from inside the Error callback. For non-overflow errors, log once with DebugLog.Write (the message names the inotify limit) and schedule a single debounced rebuild with backoff, reusing the _reconcileTimer pattern. Also add a re-entrancy guard flag in Refresh(). For an ENOSPC IOException, keep the partially working watcher and do not rebuild.

**Verifier votes**

- **confirmed** (refute lens): OnError (LibraryWatcherService.cs:254-261) calls Refresh() unconditionally. On ENOSPC, .NET 10's AddDirectoryWatchUnlocked calls watcher.OnError synchronously and returns (fsw_linux lines 387-406). The subdirectory loop then continues, so the error fires once per failing subdirectory. FileSystemWatcher.OnError calls handler(this,e) directly when SynchronizingObject is null (FileSystemWatcher.cs:499-509). The call therefore re-enters Refresh inside the Monitor-reentrant lock(_gate). The half-built outer watcher is not yet in _watchers (the Add is at line 82, after EnableRaisingEvents), so it keeps its watches and the nested root add fails with ENOSPC again. Each level opens a new inotify instance (INotifyInit, fsw_linux:36) until EMFILE throws, which the catch at line 84 swallows. Each level then adds its zero-watch watcher at line 82. The damage is worse than stated: a zero-watch RunningInstance never releases its instance. CancellationCallback (fsw_linux:538-552) has no watches to remove, so the ProcessEvents read (fsw_linux:762-795) never wakes and _inotifyHandle is never disposed. Dispose does not free those instances or their threads. Linux only and needs the watch limit reached, so medium.

### P08 — Linux: folder cover art is found only if its name is entirely lower-case — Folder.jpg, Cover.jpg and cover.JPG are never used

- **Location:** `src/Noctis.Core/Services/MetadataService.cs:435` (also: `src/Noctis.Core/Services/MetadataService.cs:414`, `src/Noctis.Core/Services/LibraryService.cs:826`, `src/Noctis/Helpers/MusicVideoLocator.cs:24`)
- **Area / sweep:** Cross-platform / xplat-general · **Category:** cross-platform · **Platforms:** linux
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
foreach (var name in FolderArtNames)
{
    foreach (var ext in FolderArtExtensions)
    {
        var artPath = Path.Combine(directory, name + ext);
        if (File.Exists(artPath))
            candidates.Add(new FileInfo(artPath));
    }
}
```

**Why it is a bug:** FolderArtNames and FolderArtExtensions are all lower-case (lines 402-403), and each candidate is checked with File.Exists on an exact name. That lookup is case-sensitive on ext4/btrfs/xfs. The common real-world names are 'Folder.jpg' (Windows Media Player), 'Cover.jpg' (many rippers) and upper-case '.JPG'/'.PNG' from cameras and phones, and none of them match on Linux. The watcher's own check IsFolderArtCandidate (lines 414-416) is case-insensitive, so a changed Folder.jpg triggers a refresh that then finds nothing. TryGetFolderArtFile feeds both TryReadFolderArt (initial extraction, drop import at MainWindowViewModel.cs:1298) and the staleness refresh (LibraryService.cs:826).

**Impact:** On Linux, albums without embedded art that rely on Folder.jpg or Cover.jpg show the placeholder cover, while the same library shows covers on Windows and macOS. Music-video lookup has the same flaw for upper-case extensions such as .MP4/.MOV.

**Proposed fix (small):** List the directory once and match names case-insensitively, e.g. `foreach (var f in Directory.EnumerateFiles(directory)) if (IsFolderArtCandidate(f)) candidates.Add(new FileInfo(f));`, keeping the existing largest-file selection. That is also one listing instead of 30 File.Exists probes. Apply the same approach to MusicVideoLocator's extension probe.

**Verifier votes**

- **confirmed** (refute lens): MetadataService.cs:402-403 declares all-lowercase FolderArtNames and FolderArtExtensions. TryGetFolderArtFile (MetadataService.cs:425-449) probes File.Exists(Path.Combine(directory, name + ext)) at lines 435-436, which is case-sensitive on ext4, btrfs and xfs. So Folder.jpg, Cover.jpg and cover.JPG are never found. IsFolderArtCandidate (lines 410-417) uses OrdinalIgnoreCase, which is the inconsistency the finding describes. The lookup feeds the extraction fallback (MetadataService.cs:371, used when there is no embedded art or UseEmbeddedArtwork is off), the drop import (MainWindowViewModel.cs:1298) and the staleness check (LibraryService.cs:826). MusicVideoLocator.cs:9 and :24-37 has the same flaw with lowercase-only extension probes. Platform-specific, so medium.

### P09 — noctis-server skips its graceful shutdown on SIGTERM (docker stop / systemctl stop): the ProcessExit handler only cancels a token and returns, so the process can exit before the scan checkpoint runs

- **Location:** `src/Noctis.Server/Program.cs:160` (also: `Dockerfile:35`, `src/Noctis.Core/Services/LibraryService.cs:638-668`)
- **Area / sweep:** Cross-platform / xplat-linux · **Category:** lifecycle · **Platforms:** linux
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
Program.cs:158-173
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();
    ...
    Console.WriteLine("shutting down…");
    try { await server.StopAsync(); } catch { }
    try { await scanLoop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    try { await core.Library.PauseActiveScanForShutdownAsync(TimeSpan.FromSeconds(5)); } catch { }
```

**Why it is a bug:** SIGTERM, the stop signal for Docker (ENTRYPOINT exec form, Dockerfile:35) and systemd, is handled by the runtime by raising ProcessExit and then terminating once the handlers return. Microsoft's own ConsoleLifetime blocks inside OnProcessExit (`_shutdownBlock.WaitOne()`) for this reason (https://github.com/dotnet/runtime/blob/release/5.0/src/libraries/Microsoft.Extensions.Hosting/src/Internal/ConsoleLifetime.cs). Current versions register PosixSignalRegistration SIGTERM with context.Cancel = true (ConsoleLifetime.netcoreapp.cs). Here the handler only cancels a token, so the await chain in ServeAsync races process exit. SIGINT (CancelKeyPress with e.Cancel) is handled correctly.

**Impact:** `docker stop`, a container update or a systemd restart during a scan kills the scan without PauseActiveScanForShutdownAsync. That checkpoint (LibraryService.cs:638-668) exists so a re-scan "resumes incrementally instead of starting over". Without it, a large NAS library's first scan starts from zero after every restart, and the HTTP listener isn't stopped cleanly (exit code 143). Needs testing on Linux (docker stop mid-scan, then compare the next scan's duration).

**Proposed fix (small):** Register `using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdown.Cancel(); });`. Keep ServeAsync's ordered shutdown under Docker's 10 s grace (the two 5 s waits already total 10 s, so trim them). Alternatively, block in the ProcessExit handler on a ManualResetEventSlim that ServeAsync sets after its last step.

**Verifier votes**

- **confirmed** (refute lens): The code matches the finding. Program.cs:158-173: the ProcessExit handler (line 160) only calls shutdown.Cancel() and returns. On SIGTERM the runtime runs ProcessExit and then exits, which is why ConsoleLifetime blocks in that handler. So the ordered shutdown at 170-173 races process exit. Dockerfile:35 uses the exec-form ENTRYPOINT, so docker stop sends SIGTERM straight to the .NET process. One part of the finding is wrong: SIGINT is not 'handled correctly' for the checkpoint. Line 165 passes shutdown.Token into ScanLoopAsync, and ScanAsync links that token (LibraryService.cs:111-119). Any shutdown.Cancel() therefore cancels the scan before PauseActiveScanForShutdownAsync sets _checkpointRequested (LibraryService.cs:647). The scan then unwinds, usually within the 5s scanLoop.WaitAsync at line 172, and takes the RestoreOriginalLibrary rollback (LibraryService.cs:388-403) instead of the checkpoint. The checkpoint is lost on both SIGTERM and Ctrl+C. The proposed PosixSignalRegistration fix is needed but not enough: ScanLoopAsync must not get shutdown.Token directly (give it a separate token, or call PauseActiveScanForShutdownAsync before cancelling the loop).

### P12 — Linux AppImage: the AppImage environment scrub is not applied to OpenUrl (xdg-open), 'Open in <app>', gio trash or the theme probes, so host tools inherit the bundle's LD_LIBRARY_PATH

- **Location:** `src/Noctis/Helpers/PlatformHelper.cs:169` (also: `src/Noctis/Helpers/PlatformHelper.cs:78`, `src/Noctis/Helpers/RecycleBin.cs:180`, `src/Noctis/Helpers/PlatformHelper.cs:485`, `src/Noctis/Helpers/PlatformHelper.cs:425`, `.github/workflows/dotnet.yml:415`, `src/Noctis/ViewModels/SettingsViewModel.cs:4709`, `src/Noctis/Services/TidalAuth.cs:234`)
- **Area / sweep:** Cross-platform / xplat-general · **Category:** cross-platform · **Platforms:** linux
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
try
{
    Process.Start(new ProcessStartInfo
    {
        FileName = url,
        UseShellExecute = true
    });
}
catch
```

**Why it is a bug:** AppRun exports LD_LIBRARY_PATH=${HERE}/usr/lib:${HERE}/usr/lib/vlc (.github/workflows/dotnet.yml:415), and that directory holds linuxdeploy-bundled Ubuntu 24.04 libraries. The code documents the failure this causes (PlatformHelper.cs:292-299): a host tool like xdg-open, dbus-send or the file manager it spawns loads the AppImage's libraries ahead of the distro's and dies (SteamOS/Arch). It fixes this only for OpenLinuxFolder and TryShowInLinuxFileManager via ScrubAppImageEnvironment. OpenUrl uses UseShellExecute. On Unix, .NET builds envp from psi.Environment (the full process environment) and falls back to the system opener (xdg-open) with that same envp (https://raw.githubusercontent.com/dotnet/runtime/release/10.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Unix.cs). Process.Start returns once xdg-open is spawned, so the catch never fires when the browser later fails. Other host-tool launches are not scrubbed either: OpenFileWith (lines 78-83), RecycleBin.RunProcess ('gio trash', RecycleBin.cs:180-190), ReadGSettings and ReadPortalColorScheme.

**Impact:** On AppImage installs on non-Ubuntu distros (SteamOS/Arch, the setup the comment was written for), Last.fm and Tidal 'Connect' can open no browser. The status stays at 'Waiting for authorization in browser...' (SettingsViewModel.cs:4709-4717, TidalAuth.cs:234), and Settings links do nothing. 'Open in <app>' can crash the external program. If gio fails, 'Move to Trash' for files on another filesystem (SD card, second drive) is refused by the fallback (RecycleBin.cs:160-166), so the file stays on disk after the track is removed.

**Proposed fix (small):** On Linux, route OpenUrl through the existing scrubbed TryRunHostTool("xdg-open", new[]{url}, 4000) before, or instead of, the shell-execute path. Call PlatformHelper.ScrubAppImageEnvironment(psi) in OpenFileWith, RecycleBin.RunProcess, ReadGSettings and ReadPortalColorScheme.

**Verifier votes**

- **confirmed** (refute lens): OpenUrl (PlatformHelper.cs:169-173) uses UseShellExecute=true with no scrub. On Unix, .NET builds envp from psi.Environment (the inherited env, including AppRun's LD_LIBRARY_PATH from dotnet.yml:415) and passes it to xdg-open. The catch at 175 only fires if spawning fails, so the scrubbed xdg-open fallback (183) is never used when xdg-open or the browser dies later. OpenFileWith (78-83), RecycleBin.RunProcess (RecycleBin.cs:180-190, used by LinuxTrash at 115), ReadGSettings (485-493) and ReadPortalColorScheme (425-436) also skip ScrubAppImageEnvironment, whose only callers are lines 142 and 269. The code's own comment (292-299) says host tools like xdg-open die under this environment on SteamOS/Arch, which makes the Last.fm/Tidal browser failure credible. SettingsViewModel.cs:4709-4717 then shows 'Waiting for authorization in browser...'. One impact claim is wrong: .NET File.Move on Unix falls back to copy+delete on EXDEV. So if gio fails, FreedesktopTrash (RecycleBin.cs:158) usually still moves a file on another filesystem into the home Trash rather than leaving it on disk. It only fails when File.Move throws for another reason (for example, the home Trash filesystem is full or read-only).

### P15 — macOS: 'Open With Noctis', double-clicking an audio file and dropping files on the Dock icon never play anything

- **Location:** `src/Noctis/Program.cs:36` (also: `.github/workflows/dotnet.yml:232`, `.github/workflows/dotnet.yml:247`, `src/Noctis/App.axaml.cs:145`, `src/Noctis/Views/MainWindow.axaml.cs:756`)
- **Area / sweep:** Cross-platform / xplat-mac · **Category:** platform-integration · **Platforms:** macos
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
src/Noctis/Program.cs:33-38 (the only way files get in, apart from the Windows/Linux single-instance pipe):
            // Audio files passed on the command line ("Open with Noctis" /
            // double-clicked track): forwarded to the running instance, or
            // played once this instance finishes starting.
            var filesToOpen = args
                .Where(a => !a.StartsWith('-') && File.Exists(a))
                .ToArray();
.github/workflows/dotnet.yml:236-248: the generated Info.plist has CFBundleName … NSHighResolutionCapable, NSAppleEventsUsageDescription and no CFBundleDocumentTypes.
Searching src/ (excluding Mobile) for IActivatableLifetime|FileActivatedEventArgs|ActivationKind finds nothing.
Avalonia 12.1.2 native/Avalonia.Native/src/OSX/app.mm:72-77:
- (void)application:(NSApplication *)sender openFiles:(NSArray<NSString *> *)filenames
{   auto array = CreateAvnStringArray(filenames);
    _events->FilesOpened(array); }
src/Avalonia.Native/AvaloniaNativeApplicationPlatform.cs: FilesOpened → lifetime.OnActivated(new FileActivatedEventArgs(files))
```

**Why it is a bug:** On macOS, Finder and LaunchServices deliver documents through the open-documents Apple Event, not argv. Avalonia turns that event into IActivatableLifetime.Activated with FileActivatedEventArgs. Avalonia's docs say to handle it through Application.Current.TryGetFeature<IActivatableLifetime>() and to declare CFBundleDocumentTypes in Info.plist (https://docs.avaloniaui.net/docs/services/activatable-lifetime). Noctis does neither. Scenario: the user right-clicks song.flac and picks Open With → Other → Noctis. Noctis launches, or activates if already running; LaunchServices does not start a second process, so SingleInstanceGuard never runs. The file path arrives in FilesOpened, nobody is subscribed, and nothing plays. Because CFBundleDocumentTypes is missing, Finder also leaves Noctis out of the Open With list and the Dock refuses dropped audio files.

**Impact:** The 'Open with Noctis' feature, which works on Windows and Linux (Program.cs:33-38, MainWindow.axaml.cs:756-761), does nothing on macOS. Needs testing on macOS.

**Proposed fix (small):** 1) In App.OnFrameworkInitializationCompleted, before the main window is created: `if (TryGetFeature<IActivatableLifetime>() is { } al) al.Activated += (_, e) => { if (e is FileActivatedEventArgs f) Dispatcher.UIThread.Post(() => { mainWindow.ShowFromTray(); vm.OpenExternalFiles(f.Files.Select(i => i.TryGetLocalPath()).OfType<string>().ToList()); }); };` Buffer the paths into App.PendingOpenFiles if the view model is not ready yet. 2) Add CFBundleDocumentTypes to the CI Info.plist (LSItemContentTypes public.audio, public.mp3, org.xiph.flac, com.apple.m4a-audio, public.aiff-audio, com.microsoft.waveform-audio; CFBundleTypeRole Viewer; LSHandlerRank Alternate).

**Verifier votes**

- **confirmed** (refute lens): Program.cs:36-38 builds filesToOpen from argv only, and App.PendingOpenFiles is set only from that (Program.cs:73). The only runtime delivery path is SingleInstanceGuard.ActivationRequested (MainWindow.axaml.cs:756-762). A grep of src (excluding Mobile) finds no IActivatableLifetime, FileActivatedEventArgs or UrlsOpened handling. The only TryGetFeature uses are Skia leases. The Info.plist written by CI (dotnet.yml:236-250) has no CFBundleDocumentTypes. The local Avalonia.Native 12.1.2 DLL contains FilesOpened, FileActivatedEventArgs, OnActivated and IActivatableLifetime, so macOS open-document events are routed to an activation event that nothing in Noctis subscribes to. Open With, double-click and Dock drop therefore never reach OpenExternalFiles on macOS. Platform-specific: medium.

### P16 — macOS/Linux: a fatal startup error (such as libvlc failing to load) is written only to stderr, so a Finder or Dock launch just bounces and quits with no message

- **Location:** `src/Noctis/Program.cs:143` (also: `src/Noctis/Services/VlcAudioPlayer.cs:440`, `src/Noctis/Services/VlcAudioPlayer.cs:5349`, `src/Noctis/App.axaml.cs:153`)
- **Area / sweep:** Cross-platform / xplat-mac · **Category:** error-reporting · **Platforms:** macos, linux
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
src/Noctis/Program.cs:133-145:
            // On Windows, surface a native message box (libvlc DLLs missing etc.).
            // On macOS/Linux, the crash log + stderr is the post-mortem path.
            if (OperatingSystem.IsWindows())
            {
                MessageBox(IntPtr.Zero, $"Noctis failed to start:\n\n{ex.Message}", …);
            }
            else
            {
                Console.Error.WriteLine($"Noctis failed to start: {ex}");
            }
src/Noctis/Services/VlcAudioPlayer.cs:5349-5353 builds a helpful macOS message ("Install VLC from https://www.videolan.org/vlc/ or via Homebrew…") and throws it from the constructor (440). The UI-thread resolve in App.OnFrameworkInitializationCompleted (App.axaml.cs:153) rethrows it out of StartWithClassicDesktopLifetime into this catch.
```

**Why it is a bug:** A .app started from Finder, the Dock or the LaunchAgent has no terminal, so nothing the user can see receives stderr. The only record is crash.log in ~/.config/Noctis. CrashJournal.MarkFatal stamps the journal so the next launch can show a banner, but the next launch fails the same way, so the banner never appears. Scenario: libvlc fails to load (for example the already-reported case where a broken or Intel-only /Applications/VLC.app is tried first). The Dock icon bounces once, the app exits, and the 'install VLC' guidance written for exactly this case is never shown.

**Impact:** Users see an unexplained crash at launch and cannot recover on their own. Needs testing on macOS.

**Proposed fix (small):** In the non-Windows branch, also show a native alert. On macOS: `Process.Start("/usr/bin/osascript", ["-e", "on run argv", "-e", "display alert \"Noctis failed to start\" message (item 1 of argv) as critical", "-e", "end run", ex.Message])` using ArgumentList, then wait for it. On Linux, try zenity or kdialog. Include the crash.log path in the message.

**Verifier votes**

- **confirmed** (refute lens): Program.cs:128-145: on non-Windows the catch only calls LogCrash and `Console.Error.WriteLine` (line 143, not 141). No alert of any kind is shown; the only osascript use in the repo is RecycleBin.cs:104. A Finder/Dock launch has no visible stderr. The failure path is reachable. TryFindMacLibVlcPath (VlcAudioPlayer.cs:5266-5278) returns /Applications/VLC.app/Contents/MacOS/lib first whenever libvlc.dylib merely exists there. If Core.Initialize(macLibPath) (427) then fails, the error is rethrown at 433-440 as InvalidOperationException(BuildLibVlcMissingMessage()) without trying the bundled copy. The UI-thread resolve at App.axaml.cs:153 propagates it out of StartWithClassicDesktopLifetime, and the macOS/Linux install guidance at 5349-5361 is never shown. The comment at Program.cs:134-135 marks stderr as the post-mortem path on purpose, but that covers post-mortem only, not telling the user. CI bundling libvlc (dotnet.yml:184-202) makes the trigger uncommon: medium.

### P17 — Lyrics Studio Save runs the Finder trash on the UI thread (up to 15 s), and when the trash fails it overwrites the user's own .lrc while reporting it was moved to the recycle bin

- **Location:** `src/Noctis/Services/Lyrics/LyricsWriter.cs:150` (also: `src/Noctis/ViewModels/LyricsStudioViewModel.cs:672`, `src/Noctis/ViewModels/LyricsStudioViewModel.cs:682`, `src/Noctis/Helpers/RecycleBin.cs:103`, `src/Noctis/Helpers/RecycleBin.cs:192`, `src/Noctis/Services/Lyrics/LyricsWriter.cs:165`)
- **Area / sweep:** Cross-platform / xplat-mac · **Category:** data-loss · **Platforms:** macos, windows, linux
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
src/Noctis/Services/Lyrics/LyricsWriter.cs:145-156:
        var foreign = File.Exists(sidecarPath) && !_registry.Contains(sidecarPath);
        if (foreign)
        {
            if (!replaceForeign) return;
            // Best effort: if the trash refuses, the explicit save still wins.
            if (!TrashFile(sidecarPath))
                DebugLogger.Warn(DebugLogger.Category.Lyrics, "Lyrics.SidecarTrashFailed", sidecarPath);
            replaced = true;
        }
        File.WriteAllText(sidecarPath, Normalize(content), new UTF8Encoding(false));
src/Noctis/ViewModels/LyricsStudioViewModel.cs:662-663 (confirm text) and 672 (runs on the UI thread after `await ConfirmAsync` in a RelayCommand):
            "… A lyrics file Noctis didn't write goes to the Recycle Bin first."
            var outcome = _writer.SaveDetailed(item.Track, plain, synced, embed, replaceForeignSidecar: true);
LyricsStudioViewModel.cs:682: `: outcome.ReplacedForeignSidecar ? $"Saved · {format} · old .lrc moved to the recycle bin"`
src/Noctis/Helpers/RecycleBin.cs:103-108, 192: osascript → `tell application "Finder" to delete …`; `if (!p.WaitForExit(15000)) { p.Kill(true); return false; }`
```

**Why it is a bug:** Save is an async RelayCommand. After `await ConfirmAsync` it continues on the UI thread and calls SaveDetailed synchronously. For a sidecar the user wrote themselves, TrashFile runs RecycleBin.TryMoveToTrash, which on macOS starts osascript, sends an Apple Event to Finder, and blocks for up to 15 s. The first time, macOS shows the Automation consent prompt ('Noctis wants access to control Finder'). The Apple Event waits for the answer while the Noctis UI thread sits in WaitForExit (spinning cursor). If the user takes longer than 15 s, osascript is killed and the trash fails. If the user clicks Don't Allow, TCC remembers the denial and every later trash fails with -1743. In both cases WriteSidecar still sets `replaced = true` and overwrites the file, so the user's own .lrc (for ELRC, both .elrc and .lrc) is lost for good. The status line then says 'old .lrc moved to the recycle bin', and the confirm dialog had promised exactly that. The flag and overwrite logic is the same on every OS; macOS is where the trash fails in practice. The same wait-then-kill on the first TCC prompt also makes the first library 'Move to Trash' fail (off the UI thread there).

**Impact:** On macOS the UI freezes (hundreds of ms normally, up to 15 s on the first-time consent prompt) when saving over an existing user-written lyrics file. If the trash is denied or times out, a lyrics file the user timed themselves is destroyed with no copy in the Trash, and the status text says the opposite. Needs testing on macOS.

**Proposed fix (small):** 1) Run SaveDetailed off the UI thread (`await Task.Run(() => _writer.SaveDetailed(...))`) and marshal the status updates back. 2) In WriteSidecar, set `replaced = true` only when TrashFile succeeded. When it fails, do not overwrite: either return a 'couldn't move the old file to the Trash' outcome and ask the user, or first rename the old file to `<name>.lrc.bak`. 3) On macOS, replace the Finder/osascript trash with NSFileManager `trashItemAtURL:resultingItemURL:error:` through the objc interop already in MacNowPlayingService. That needs no Apple Events or TCC prompt and returns in milliseconds.

**Verifier votes**

- **confirmed** (refute lens): LyricsWriter.cs:145-156 matches the quote exactly. When TrashFile fails, the code only logs a Warn, still sets replaced=true, then runs File.WriteAllText over the foreign sidecar. SaveDetailed:82-83 calls WriteSidecar twice for ELRC, so both .elrc and .lrc can be overwritten. LyricsStudioViewModel.cs:662-663 (the confirm text promising the Recycle Bin) and :672 are correct: SaveDetailed runs synchronously on the UI thread, right after `await ConfirmAsync` inside the async RelayCommand. Line 682 then shows 'old .lrc moved to the recycle bin' based only on that flag. RecycleBin.cs:103-108 runs osascript through Finder, and :192-195 waits up to 15 s, kills the process and returns false. The comment at :149 shows that overwriting after a failed trash is intended ('explicit save still wins'). What contradicts the user-facing promise is the false status text, plus the class doc 'goes to the recycle bin, not into the void' (LyricsWriter.cs:57-58), so this is a real defect. The CI Info.plist does declare NSAppleEventsUsageDescription (.github/workflows/dotnet.yml:247), so the first-run consent prompt, and with it the 15 s timeout path, is reachable. How often the trash fails in practice is runtime-dependent and was not tested. The code defect itself is verified.

### P22 — MPRIS `Set Shuffle` only flips IsShuffleEnabled and skips ToggleShuffle, so shuffle from playerctl or the KDE widget lights the icon but never shuffles the queue

- **Location:** `src/Noctis/Services/MprisService.cs:618` (also: `src/Noctis/ViewModels/PlayerViewModel.cs:717-784`, `src/Noctis/ViewModels/PlayerViewModel.cs:786-798`, `src/Noctis/ViewModels/PlayerViewModel.cs:3007-3011`)
- **Area / sweep:** Cross-platform / xplat-linux · **Category:** correctness · **Platforms:** linux
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
MprisService.cs:615-620
    case "Shuffle":
    {
        var shuffle = value.GetBool();
        _s.OnUiThread(() => _s._player.IsShuffleEnabled = shuffle);
        break;
    }
PlayerViewModel.cs:106  [ObservableProperty] private bool _isShuffleEnabled;   (no OnIsShuffleEnabledChanged partial exists)
PlayerViewModel.cs:717-741 ToggleShuffle():
    IsShuffleEnabled = !IsShuffleEnabled;
    MarkQueueChanged();
    ...
    _originalQueue = UpNext.ToList();
    var shuffled = Helpers.ShuffleHelper.WeightedShuffle(...);
    UpNext.ReplaceAll(shuffled);
```

**Why it is a bug:** The only code that actually reorders the queue lives in the ToggleShuffle command: it saves _originalQueue, runs a weighted shuffle, restores on off, and marks the queue changed. The MPRIS setter writes the bare observable property, which has no change hook, so none of that runs. LoopStatus has the same shape (MprisService.cs:621-630 writes RepeatMode directly). That skips CycleRepeat's CancelAutoMixTransition("repeat changed"), and OnRepeatModeChanged cancels only for One.

**Impact:** `playerctl shuffle On` or the Plasma media widget's shuffle button turns Noctis's shuffle icon on, but the queue keeps playing in order. The next click on Noctis's own shuffle button then turns it OFF instead of on, so the user must click twice. Turning shuffle off remotely leaves the queue shuffled while the UI says Off. A later in-app shuffle-on then saves that shuffled order as the "original", and the real order is lost. The queue change isn't marked for persistence. Needs testing on Linux (playerctl shuffle On/Off).

**Proposed fix (trivial):** Change the handler to `_s.OnUiThread(() => { if (_s._player.IsShuffleEnabled != shuffle) _s._player.ToggleShuffleCommand.Execute(null); });`. For LoopStatus, also run the cancel that CycleRepeat does: expose a SetRepeatMode(mode) on PlayerViewModel that calls CancelAutoMixTransition("repeat changed") and then assigns.

**Verifier votes**

- **confirmed** (refute lens): MprisService.cs:615-620 assigns _player.IsShuffleEnabled directly. PlayerViewModel.cs:106 is a bare [ObservableProperty] with no OnIsShuffleEnabledChanged partial. The only other listener is LocalApiEventHub.cs:244, which just reports the change. All of the reorder logic lives in the ToggleShuffle command (PlayerViewModel.cs:717-784): saving _originalQueue, WeightedShuffle, restoring the order, and MarkQueueChanged. A remote shuffle therefore flips the flag and leaves the queue order unchanged, and the next in-app toggle goes the opposite way from what the user expects. The LoopStatus part is also accurate: MprisService.cs:629 assigns RepeatMode directly, which skips CycleRepeat's CancelAutoMixTransition (:790), and OnRepeatModeChanged (:3007-3011) cancels only for One.

### P24 — macOS: a user-installed VLC.app is always tried first and a load failure aborts startup; the bundled libvlc is never tried as a fallback

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:5270` (also: `src/Noctis/Services/VlcAudioPlayer.cs:410`, `src/Noctis/Services/VlcAudioPlayer.cs:434`, `src/Noctis/Noctis.csproj:95`)
- **Area / sweep:** Cross-platform / xplat-general · **Category:** cross-platform · **Platforms:** macos
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Prior audit:** AUDIT.md H7
- **Needs runtime check:** yes

**Evidence**

```
string[] candidates =
{
    "/Applications/VLC.app/Contents/MacOS/lib",
    Path.Combine(AppContext.BaseDirectory, "libvlc", "lib"),
    ...
};
foreach (var dir in candidates)
{
    if (File.Exists(Path.Combine(dir, "libvlc.dylib")))
        return dir;
```

**Why it is a bug:** The first directory that merely contains libvlc.dylib wins, and Core.Initialize(macLibPath) is called once (line 427). If that VLC.app cannot be loaded into this process, LibVLCSharp throws VLCException: 'Failed to load required native libraries' when the load fails, or 'Version mismatch between LibVLC N and LibVLCSharp 3' (https://raw.githubusercontent.com/videolan/libvlcsharp/3.x/src/LibVLCSharp/Shared/Core/Core.cs). Two ways to get there: an Intel-only VLC.app on an Apple Silicon Mac running the osx-arm64 build (dlopen fails with an architecture mismatch), or a VLC 4.x install. The catch at lines 434-440 turns this into InvalidOperationException('libvlc is required but was not found…'). The universal VLC 3.0.23 payload that CI bundles at Contents/MacOS/libvlc (csproj lines 95-100) is never attempted.

**Impact:** On a Mac with an incompatible VLC.app in /Applications, Noctis cannot create its audio player and fails at launch. On macOS there is no dialog (Program.cs:135-144 only writes to stderr and crash.log), and the error message tells the user to install VLC, which they already have.

**Proposed fix (small):** Prefer the pinned bundled payload when it exists, i.e. swap the first two candidates and keep VLC.app for unbundled or dev runs. At minimum, wrap Core.Initialize per candidate and fall through to the next directory on VLCException, logging which directory was used.

**Verifier votes**

- **confirmed** (refute lens): VlcAudioPlayer.cs:5268-5280 returns the first candidate directory that has libvlc.dylib, and /Applications/VLC.app is first. :427 calls Core.Initialize(macLibPath) once, with no retry. :434-440 turns VLCException, DllNotFoundException and FileNotFoundException into InvalidOperationException(BuildLibVlcMissingMessage()), so the bundled Contents/MacOS/libvlc (csproj:95-100) is never tried. Preferring VLC.app is documented (:5260-5261), but not falling back after a load failure is not addressed there. CI ships both osx-arm64 and osx-x64 (.github/workflows/dotnet.yml:33,40), so an architecture mismatch with a single-arch VLC.app is reachable. Program.cs:135-144 shows a dialog only on Windows and just writes to stderr on macOS. I am relying on LibVLCSharp 3.x Core.Initialize throwing VLCException when the native load fails or the version does not match, not on code I read in this repo.

### P28 — Linux resume remap hides MainWindow, which makes Avalonia hide every open dialog, detach it from its owner and end its ShowDialog as if cancelled; the dialog is never shown again

- **Location:** `src/Noctis/Views/MainWindow.axaml.cs:43` (also: `src/Noctis/Services/LinuxResumeWatcher.cs:71-76`, `src/Noctis/Views/MainWindow.axaml.cs:479`, `src/Noctis/ViewModels/MetadataHelper.cs:22-34`)
- **Area / sweep:** Cross-platform / xplat-linux · **Category:** correctness · **Platforms:** linux
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
MainWindow.axaml.cs:36-46
    foreach (var window in new List<Window>(desktop.Windows))
    {
        if (!window.IsVisible || window.WindowState == WindowState.Minimized)
            continue;
        var wasActive = window.IsActive;
        try
        {
            window.Hide();
            window.Show();
Avalonia 12.0.0 Window.Hide():
    if (_children.Count > 0)
        foreach (var child in _children.ToArray()) child.child.Hide();
    Owner = null;
    PlatformImpl?.Hide();
    IsVisible = false;
    _modalSubscription?.Dispose();
MetadataHelper.cs:28  await window.ShowDialog(desktop.MainWindow);   (metadata editor, Lyrics Studio, converter, YouTube, etc.)
```

**Why it is a bug:** In Avalonia 12, Window.Hide() hides every owned child and sets Owner = null (https://github.com/AvaloniaUI/Avalonia/blob/12.0.0/src/Avalonia.Controls/Window.cs). When a child's own Hide runs, it disposes that child's _modalSubscription. Disposing it runs owner.Activate() and completes the ShowDialog TaskCompletionSource with the default result. So when the loop hides MainWindow (first in desktop.Windows), every open modal dialog is hidden, detached and treated as cancelled. MainWindow.Show() does not re-show children. When the loop then reaches the dialog's entry, `!window.IsVisible` is true, so it is skipped. The dialog is never shown or closed again. Its Closed-driven cleanup never runs, and awaiting code continues as if the user cancelled.

**Impact:** The remap runs by default on Wayland+NVIDIA (ShouldRemapAfterResume) and for anyone with NOCTIS_RESUME_REMAP=1. On those setups, waking the machine with a dialog open makes the dialog disappear: metadata editor (unsaved edits lost), Lyrics Studio (a running transcription keeps going invisibly), audio converter, YouTube download, or a confirmation, which silently resolves as No. Each hidden window and its view model stays alive for the rest of the session. Needs testing on Linux (Wayland + NVIDIA, suspend with the metadata editor open).

**Proposed fix (small):** In RemapWindowsAfterResume, skip the remap while any visible window has an Owner (a dialog or owned window is open). Log it and defer the remap: subscribe once to that dialog's Closed event and remap then. Alternatively, remap only windows that have no owned children. Never Hide() an owner while a modal child is open.

**Verifier votes**

- **confirmed** (refute lens): MainWindow.axaml.cs:36-46 calls Hide() and then Show() on every visible window in desktop.Windows order, and MainWindow comes first. The project uses Avalonia 12.1.2 (Noctis.csproj:67), not the 12.0.0 the finding cites, but 12.1.2 behaves the same. In 12.1.2 Window.cs:867-893, Hide() first calls child.Hide() on every owned child, then sets Owner = null and disposes _modalSubscription. The modal disposable at Window.cs:1107-1112 then calls owner.Activate() and tcs.SetResult(_dialogResult ?? default), so ShowDialog completes as if cancelled. ShowCore (975-1123) does not re-show owned children. When the loop reaches the dialog, `!window.IsVisible` is true (line 38), so it is skipped and stays hidden but not closed. MetadataHelper.cs:28 awaits ShowDialog(desktop.MainWindow), so the await returns early. The remap only runs when LinuxResumeWatcher.cs:71-76 applies (Wayland+NVIDIA, or NOCTIS_RESUME_REMAP=1) and a dialog is open across a suspend, so medium is right.

### P29 — On desktops with no tray host (stock GNOME), start-minimized-at-login and hide-to-tray leave Noctis invisible: the `_trayIcon != null` guard can't detect a missing tray

- **Location:** `src/Noctis/Views/MainWindow.axaml.cs:490` (also: `src/Noctis/Views/MainWindow.axaml.cs:775-781`, `src/Noctis/Views/MainWindow.axaml.cs:1045-1058`, `src/Noctis/Views/MainWindow.axaml.cs:970-978`)
- **Area / sweep:** Cross-platform / xplat-linux · **Category:** platform-integration · **Platforms:** linux
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
MainWindow.axaml.cs:487-493
    // _trayIcon != null so a platform where the tray failed to initialize
    // never leaves the app running with no window AND no tray icon.
    if (App.StartMinimizedAtLogin && _trayIcon != null)
    {
        Hide();
    }
App.axaml.cs:171-175
    if (StartMinimizedAtLogin)
    {
        mainWindow.WindowState = Avalonia.Controls.WindowState.Minimized;
        mainWindow.ShowInTaskbar = false;
    }
```

**Why it is a bug:** On Linux, Avalonia's tray is DBusTrayIconImpl. Its constructor sets IsActive = true and then waits asynchronously for org.kde.StatusNotifierWatcher. If there is none, it logs "Interface 'org.kde.StatusNotifierWatcher' is unavailable" and never throws (https://github.com/AvaloniaUI/Avalonia/blob/12.0.0/src/Avalonia.FreeDesktop/DBusTrayIconImpl.cs). So `new TrayIcon{...}` at MainWindow.axaml.cs:970 succeeds and _trayIcon is non-null even though nothing is visible. The same check gates minimize-to-tray (line 775) and close-to-tray (line 1047).

**Impact:** On Fedora Workstation, Debian GNOME or Arch GNOME without the AppIndicator extension, "Launch at login" + "start minimized" makes Noctis start at every login with no window, no dash/taskbar entry (ShowInTaskbar=false) and no tray icon. Enabling Minimize-to-tray or Close-to-tray likewise hides the app into a tray that doesn't exist, and music keeps playing. The only way back is launching Noctis again (single-instance activation). Needs testing on Linux (stock GNOME).

**Proposed fix (small):** On Linux, check for a live tray host before treating the tray as usable: query whether org.kde.StatusNotifierWatcher has an owner on the session bus (Tmds.DBus.Protocol is already referenced). Keep the result in a `TrayAvailable` flag and use it in place of `_trayIcon != null` in the three guards. When there is no host, leave the window minimized with ShowInTaskbar=true.

**Verifier votes**

- **confirmed** (refute lens): The code is as quoted. MainWindow.axaml.cs:490 gates Hide() on `App.StartMinimizedAtLogin && _trayIcon != null`, and the same null check is at :775 (minimize-to-tray) and :1047 (close-to-tray). _trayIcon is set by `new TrayIcon{...}` at :970, and the only failure path is the catch at :1007-1011. Avalonia's TrayIcon constructor does not throw when there is no tray host. The Avalonia.FreeDesktop 12.1.2 DLL contains the 'StatusNotifierWatcher' and 'Unable to get a dbus connection' strings, which fits the log-don't-throw path the finding describes. So on any desktop with no tray host, _trayIcon is non-null and the guard cannot detect the missing tray. All three settings are opt-in (AppSettings.cs:215-223, default false) and are shown on every platform with no gating (SettingsView.axaml:1369-1381). LinuxSet writes the --minimized autostart argument (StartupHelper.cs:177, 76-77). App.axaml.cs:171-175 also sets ShowInTaskbar=false, so a login launch leaves nothing visible. This is platform-specific and always reproduces once enabled, so medium rather than low. Recovery is to relaunch the app, which goes through the single-instance pipe (MainWindow.axaml.cs:756-762).

### P30 — macOS: clicking the Dock icon does not bring back a window hidden to the tray (start minimized at login, close or minimize to tray)

- **Location:** `src/Noctis/Views/MainWindow.axaml.cs:1057` (also: `src/Noctis/Views/MainWindow.axaml.cs:490`, `src/Noctis/Views/MainWindow.axaml.cs:780`, `src/Noctis/Views/MainWindow.axaml.cs:1019`, `src/Noctis/App.axaml.cs:171`)
- **Area / sweep:** Cross-platform / xplat-mac · **Category:** platform-integration · **Platforms:** macos
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
src/Noctis/Views/MainWindow.axaml.cs:1052-1058 (close-to-tray):
            e.Cancel = true;
            ...
            vm.Player.SaveQueueStateInBackground();
            Hide();
            return;
MainWindow.axaml.cs:490-493 (start minimized at login): `if (App.StartMinimizedAtLogin && _trayIcon != null) { Hide(); }`
MainWindow.axaml.cs:775-781 (minimize to tray): `... && trayVm.Settings.MinimizeToTray && _miniPlayer == null) { Hide(); }`
ShowFromTray (1019-1039) is reachable only from the tray menu or click (942, 977) and the single-instance pipe (758).
Avalonia 12.1.2 app.mm:46-50:
-(BOOL)applicationShouldHandleReopen:(NSApplication *)sender hasVisibleWindows:(BOOL)flag
{   _events->OnReopen();
    return YES; }
AvaloniaNativeApplicationPlatform.OnReopen → lifetime.OnActivated(ActivationKind.Reopen). ClassicDesktopStyleApplicationLifetime.cs (12.1.2) contains no Reopen or Activated handling, and Noctis never subscribes to IActivatableLifetime.
```

**Why it is a bug:** On macOS the Dock icon is the normal way to bring an app's window back. Avalonia forwards the click as ActivationKind.Reopen and leaves the rest to the app. Noctis ignores it. The windows above were hidden with Hide() (orderOut), not miniaturized, so AppKit's default reopen handling has nothing to restore. On macOS, launching the app again (LaunchAgent `open`, Finder, Spotlight) also only sends reopen/activate to the running process, so the SingleInstanceGuard pipe that surfaces the window on Windows/Linux never fires either.

**Impact:** With 'Start minimized at login', close-to-tray or minimize-to-tray on, the app shows as running in the Dock but clicking its icon (or relaunching it) does nothing. The only way back is the menu-bar status item, and it is not obvious users will find it. Needs testing on macOS.

**Proposed fix (small):** Subscribe once in App (same handler as the file-activation fix): `al.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) Dispatcher.UIThread.Post(mainWindow.ShowFromTray); };`. Make ShowFromTray internal so App can call it.

**Verifier votes**

- **confirmed** (refute lens): The cited code exists: close-to-tray Hide() at MainWindow.axaml.cs:1057, start-minimized Hide() at :490-493, minimize-to-tray Hide() at :775-781. ShowFromTray (:1019-1039) is only reached from the tray menu (:942), a tray click (:977) and the single-instance pipe (:758). A grep of src/**/*.cs finds no IActivatableLifetime, ActivationKind or Reopen handling (the only 'Reopen' hits are MenuOpenAnimation and SideDecodeMeterFeed, both unrelated). So a Dock-icon reopen is never handled. The macOS LaunchAgent uses `/usr/bin/open <bundle>` (StartupHelper.cs:144-145), which only sends reopen/activate to a running instance, so the SingleInstanceGuard path does not run for Dock or Finder relaunches. Two points rest on native AppKit and Avalonia.Native behaviour rather than code I could read here: that orderOut windows are not restored by default, and that app.mm returns YES without showing windows. The finding matches documented AppKit behaviour. The menu-bar status item still works as a way back, and the feature is opt-in, so medium stands.

### P31 — Default ⌘/Ctrl+Arrow shortcuts are taken away from text boxes: ⌘←/⌘→ in the search box skip tracks instead of moving to the start or end of the line

- **Location:** `src/Noctis/Views/MainWindow.axaml.cs:1563` (also: `src/Noctis/Models/Shortcuts.cs:63`, `src/Noctis/Views/MainWindow.axaml.cs:739`, `src/Noctis/Views/MainWindow.axaml.cs:898`, `src/Noctis/Views/MainWindow.axaml.cs:905`)
- **Area / sweep:** Cross-platform / xplat-mac · **Category:** input · **Platforms:** windows, macos, linux
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
src/Noctis/Models/Shortcuts.cs:59-66:
        var primary = isMac ? KeyModifiers.Meta : KeyModifiers.Control;
            ShortcutAction.NextTrack => new KeyGesture(Key.Right, primary),
            ShortcutAction.PreviousTrack => new KeyGesture(Key.Left, primary),
            ShortcutAction.VolumeUp => new KeyGesture(Key.Up, primary),
            ShortcutAction.VolumeDown => new KeyGesture(Key.Down, primary),
src/Noctis/Views/MainWindow.axaml.cs:1561-1567 (tunnel handler, registered at 739):
        // An unmodified key (Space, or whatever the user bound) must still type in an
        // edit box: typing a space in the search box stays typing a space.
        if (e.KeyModifiers == KeyModifiers.None && e.Source is TextBox) return;
        if (!ExecuteShortcut(vm, action)) return;
        _consumedShortcut = action;
        e.Handled = true;
MainWindow.axaml.cs:898-901 (macOS menu bar):
            var next = new NativeMenuItem("Next Track")
            {
                Gesture = shortcuts.Get(ShortcutAction.NextTrack),
            };
```

**Why it is a bug:** Avalonia.Native (12.1.2, AvaloniaNativePlatform.cs:133-137) maps ⌘← and ⌘→ to TextBox MoveCursorToTheStartOfLine and EndOfLine. Mac laptops have no Home/End keys, so these are the standard caret keys. On Windows and Linux, PlatformHotkeyConfiguration makes Ctrl the whole-word modifier (PlatformHotkeyConfiguration.cs:18-24), so Ctrl+←/→ is word jump. The window-level tunnel handler runs before the focused TextBox and only lets unmodified keys through, so these chords run Next/Previous or change the volume and are marked Handled. On macOS there is a second layer: the NativeMenuItem gesture becomes a real NSMenuItem key equivalent (menu.mm SetGesture → setKeyEquivalent: with NSRightArrowFunctionKey, KeyTransform.mm:107-108). Avalonia's AvnView implements no performKeyEquivalent (none in native/Avalonia.Native/src/OSX at 12.1.2), so AppKit's main menu takes ⌘←/⌘→ before keyDown ever reaches Avalonia. The same interception of NativeMenu gestures is reported in https://github.com/coffeemuse/LizTerm/issues/23. Scenario: the user types 'beatles' in the main-window search box and presses ⌘← to go back to the start. The track skips back and the caret does not move. ⌘↑/⌘↓ change the volume instead of moving the caret. On Windows, Ctrl+→ in the search box skips a track.

**Impact:** Caret navigation is broken in every text field hosted in the main window (library search, Settings search and fields, inline renames), and pressing the keys changes the track or volume by accident. Needs testing on macOS; the Windows/Linux Ctrl+Arrow case follows directly from the code.

**Proposed fix (small):** In OnGlobalShortcutKeyDown, when the focused element or e.Source is a TextBox (or any text-input control), skip every gesture whose key is Left, Right, Up, Down, Home or End, whatever the modifiers. The simplest version is to exempt all arrow-key gestures. On macOS, do not set Gesture on the Next/Previous NativeMenuItems (keep the items clickable and let the tunnel handler own the keys), or have the menu Click handler forward the key to a focused TextBox.

**Verifier votes**

- **confirmed** (refute lens): Shortcuts.cs:59-66 makes the defaults Ctrl (Meta on Mac) + Left/Right/Up/Down. ShortcutService.TryMatch (ShortcutService.cs:98-106) matches key and modifiers exactly. OnGlobalShortcutKeyDown (MainWindow.axaml.cs:1553-1568) is registered as a window Tunnel handler at :739. Its only text-box exemption is `e.KeyModifiers == KeyModifiers.None && e.Source is TextBox` (:1563). A Ctrl+Right in the library or Settings search box (Settings is hosted in the main window, :1607-1608) therefore reaches ExecuteShortcut, runs NextCommand (:1586-1588) and sets e.Handled=true before the TextBox sees the key. On Windows and Linux this breaks word-jump and skips a track. On macOS, Cmd+Left/Right are the line-start/end keys and hit the same handler. Shortcuts.cs:72-73 already reasons that Ctrl+U 'is no TextBox editing key, so it works from the search box too', which shows the conflict with editing keys is known and the arrow defaults break it. The extra macOS layer (NSMenu key equivalents from the gestures at :898-908) is not needed to confirm the bug; the tunnel handler alone causes it. Shift+Ctrl+Arrow selection is not affected because it does not match exactly.

### S13 — Metadata editor ignores WriteAlbumArt's return value: cover changes that fail on the playing file are reported as saved, and the old cover later comes back

- **Location:** `src/Noctis/ViewModels/MetadataViewModel.cs:2515` (also: `src/Noctis/ViewModels/MetadataViewModel.cs:2538`, `src/Noctis.Core/Services/MetadataService.cs:586`, `src/Noctis/ViewModels/MetadataViewModel.cs:2236`, `src/Noctis/ViewModels/MetadataViewModel.cs:2652`)
- **Area / sweep:** Settings, dialogs, popups, commands / dialogs · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
// MetadataViewModel.cs:2511-2518
await Task.Run(() =>
{   foreach (var t in artTargets)
    {   try { _metadata.WriteAlbumArt(t.FilePath, _newArtworkData); } catch { }
        t.AlbumArtworkPath = null;
    }
    ClearOwnTrackArtwork(artTargets, _newArtworkData);
});
// :2538  try { _metadata.WriteAlbumArt(t.FilePath, null); } catch { }
```

**Why it is a bug:** WriteAlbumArt returns false rather than throwing when SaveTagsAtomically fails (MetadataService.cs:586-594); the File.Move over a file held open without delete sharing fails. The dialog's own comment (MetadataViewModel.cs:2387-2391) names the most common case: the currently playing track, which LibVLC holds open. Failed tag writes go into failedWrites and keep the dialog open (2652-2660), but failed cover writes are thrown away. The dialog closes as a success, and ClearOwnTrackArtwork stamps the new cover's fingerprint on the failed track too (2236-2240). Scenario: the user fixes the cover of the album that is playing. Every other file gets the new cover, but the playing file keeps the old one while the library records the new one. The next time that file is re-read (a later tag edit, a rescan of changed files, or, after Remove, the missing-art extraction at LibraryService.cs:686-691), the old cover returns, either as a per-track cover or, after Remove, as the album cover.

**Impact:** Cover edits to the album that is playing silently skip one file. Later the old or removed cover reappears.

**Proposed fix (small):** Use the return value: `if (!_metadata.WriteAlbumArt(t.FilePath, data)) lock (failedWrites) failedWrites.Add(Path.GetFileName(t.FilePath));` in both loops. Call ClearOwnTrackArtwork only for tracks whose write succeeded, so the existing inline error strip keeps the dialog open.

**Verifier votes**

- **confirmed** (refute lens): MetadataViewModel.cs:2515 and :2538 call `_metadata.WriteAlbumArt(...)` inside `try { } catch { }` and ignore the bool it returns. MetadataService.cs:634-636 routes WriteAlbumArt through SaveTagsAtomically, which catches every exception and returns false (:586-594). So a failed File.Move over the file is never reported. The tag and advisory writes do add to failedWrites (:2404-2405, :2420-2423), and failedWrites keeps the dialog open (:2652-2660). Cover failures never reach that list, so the dialog closes as if the save worked. ClearOwnTrackArtwork (:2234-2241) then stamps the new fingerprint on every target, the failed file included. When that file is re-read later (LibraryService.cs:310-338 on a scan, or :1159-1180 on import), its old embedded cover and hash come back. The main trigger (the playing file on Windows) depends on LibVLC's share mode, which I can't check from code. The dialog's own comment at :2387-2391 says this case happens. Read-only files and permission errors cause the same failure on any platform.

### S15 — 'Remove from Playlist' appears on smart playlists and does not really remove the song: it disappears, then reappears on the next reload

- **Location:** `src/Noctis/ViewModels/PlaylistViewModel.cs:666` (also: `src/Noctis/Views/PlaylistView.axaml.cs:404`, `src/Noctis/Views/PlaylistView.axaml:1118`)
- **Area / sweep:** Settings, dialogs, popups, commands / commands · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
PlaylistView.axaml.cs:383-384 / :404 (all playlists, smart included):
    return _menuBuilder.Build("Remove from Playlist", ...);
    removeCommand: vm.RemoveTrackCommand,
PlaylistViewModel.cs:666-674 (no IsSmartPlaylist guard, unlike MoveTrack :692 and AddSongs :992):
    private async Task RemoveTrack(Track track)
    {
        ...
            if (displayIdx >= 0)
                Tracks.RemoveAt(displayIdx);
            _playlist.TrackIds.Remove(t.Id);
```

**Why it is a bug:** Smart playlist contents come from rules (LoadTracks, PlaylistViewModel.cs:357-358: `SmartPlaylistEvaluator.Evaluate(playlist, library.Tracks)`), not TrackIds. So RemoveTrack only drops the row from the displayed collection and saves an unchanged playlist. The page reloads on every LibraryUpdated (:233-234, :241) and on every revisit, and the song comes back. The selection-bar Remove is hidden for smart playlists (PlaylistView.axaml:1118 `IsVisible="{Binding IsManualPlaylist}"`), which shows the intent. The context-menu item was not gated the same way.

**Impact:** On a smart playlist, the user removes a song, it disappears, then reappears after the next library update or page visit. Nothing tells them that smart playlists can't be edited this way.

**Proposed fix (trivial):** In PlaylistView.BindContextMenuToTrack set `_menuBuilder.Remove.IsVisible = vm.IsManualPlaylist;`, and add `if (IsSmartPlaylist) return;` at the top of PlaylistViewModel.RemoveTrack.

**Verifier votes**

- **confirmed** (refute lens): PlaylistView.axaml.cs:383-384 builds the menu with 'Remove from Playlist' and :404 binds removeCommand to vm.RemoveTrackCommand for every playlist. TrackContextMenuBuilder.cs:223-234 and :425-426 never hide Remove. PlaylistViewModel.RemoveTrack (:665-688) has no IsSmartPlaylist guard, unlike MoveTrack at :692 and the other guards at :423, :481, :721 and :992. It removes the displayed row and the id from TrackIds, but LoadTracks builds smart playlists from `SmartPlaylistEvaluator.Evaluate(playlist, library.Tracks)` (:357-358). SmartPlaylistEvaluator.cs never reads TrackIds and has no exclusion list. Smart playlists reload on LibraryUpdated (:233-234, :239-242), so the song comes back. The selection-bar Remove is gated with `IsVisible="{Binding IsManualPlaylist}"` (PlaylistView.axaml:1118), which shows the item was meant to be manual-only.

### S19 — 'Reset settings' leaves many settings unreset (Upmix, shortcuts, launch-at-login toggle, accent-follows-art, ...) and the next save writes them back

- **Location:** `src/Noctis/ViewModels/SettingsViewModel.cs:6233` (also: `src/Noctis/ViewModels/SettingsViewModel.cs:6125`, `src/Noctis/ViewModels/SettingsViewModel.cs:6329`, `src/Noctis/ViewModels/SettingsViewModel.cs:2642`, `src/Noctis/ViewModels/SettingsViewModel.Features.cs:491`, `src/Noctis/ViewModels/SettingsViewModel.cs:839`)
- **Area / sweep:** Settings, dialogs, popups, commands / settings · **Category:** settings-wiring · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Prior audit:** AUDIT_2026-07-24.md §2 HIGH "Reset Settings half-resets" (partially fixed; remaining fields listed here)

**Evidence**

```
ConfirmResetLibrary writes defaults to disk, then resets only a hand-picked list of VM properties:
  6125:            _settings = defaultSettings;
  6233:            try { Helpers.StartupHelper.SetEnabled(false); } catch { }
  6329:            ApplyAudioSettings();
The list never touches AccentFollowsArtwork, UpmixMode, LaunchAtStartup, DiscordShowAlbum, MusicVideosEnabled, MusicVideoRoundedCorners, LyricsBackgroundPausesWithPlayback, _lyricsBackgroundOverrides, the ShortcutService overrides, the Show*Column flags, LyricsStudio*, YouTubeDownloadFolder or YtDlpPath. SyncToSettings re-persists all of them on the next save:
  2642:        ShortcutService.SaveTo(_settings);
  2669:        _settings.AccentFollowsArtwork = AccentFollowsArtwork;
  2746:        _settings.LyricsBackgroundMediaOverrides = new Dictionary<string, string>(_lyricsBackgroundOverrides);
SettingsViewModel.Features.cs:491:
  _settings.UpmixMode = UpmixMode ?? "Off";
```

**Why it is a bug:** The reset saves new AppSettings() at :6114 and swaps _settings, but the VM properties listed above keep their pre-reset values. SaveAsync then runs MergeExternalSettingChangesAsync, which reads the defaults from disk, followed by SyncToSettings, which writes the stale VM values back. So the first save after the reset (any toggle, or the unconditional save when the window closes) restores them. ApplyAudioSettings at :6329 also pushes the stale UpmixMode (:2970) to the engine, so upmix stays active. StartupHelper.SetEnabled(false) removes the OS autostart entry, but the LaunchAtStartup property (:839) is not re-read, so the 'Open Noctis when computer starts' toggle keeps showing ON.

**Impact:** After the user confirms 'Reset everything', which says every setting returns to its default: multi-channel upmix keeps playing, custom keyboard shortcuts stay bound, and accent-follows-artwork stays on. The Discord album line, music-video options and per-song lyrics clips also survive. All of these are written back to settings.json on the next save. The launch-at-login toggle shows ON although login launch was unregistered.

**Proposed fix (medium):** Factor LoadAsync's 'apply AppSettings to VM' block into ApplyFromSettings(AppSettings). Call it from the reset with defaultSettings (under _suspendSettingPersistence), then call ShortcutService.Load(defaultSettings), clear _lyricsBackgroundOverrides, and set LaunchAtStartup = StartupHelper.IsEnabled() with _suppressLaunchAtStartupHandler set. This removes the hand-maintained list that keeps drifting.

**Verifier votes**

- **confirmed** (refute lens): ConfirmResetLibrary (SettingsViewModel.cs:6110-6343) saves new AppSettings(), sets _settings = defaultSettings at :6125, then resets a hand-written list of properties. That list never touches AccentFollowsArtwork, UpmixMode, LaunchAtStartup, DiscordShowAlbum, MusicVideosEnabled, MusicVideoRoundedCorners, LyricsBackgroundPausesWithPlayback, _lyricsBackgroundOverrides or ShortcutService. SyncToSettings writes all of these back on the next save: :2642 ShortcutService.SaveTo, :2669 AccentFollowsArtwork, :2746-2749 overrides and music video, :2833 DiscordShowAlbum, and Features.cs:491 UpmixMode via SaveFeatureSettings, called at :2816. ApplyAudioSettings at :6329 pushes the stale UpmixMode (:2970). :6233 calls StartupHelper.SetEnabled(false) but never re-reads LaunchAtStartup, which is only read from the OS at load (:2319), so the toggle stays ON. The only SettingsReset subscriber (MainWindowViewModel.cs:311) just reloads playlists, so nothing re-runs LoadAsync. Reset is an uncommon path, so medium fits.

### S22 — The Add to Playlist dialog lists smart playlists; picking one quietly writes track IDs that never show and breaks the track count

- **Location:** `src/Noctis/ViewModels/SidebarViewModel.cs:746` (also: `src/Noctis/Views/AddToPlaylistDialog.axaml:180`, `src/Noctis/ViewModels/SidebarViewModel.cs:641`, `src/Noctis/Views/SidebarView.axaml.cs:154`, `src/Noctis/Services/SmartPlaylistEvaluator.cs:16`)
- **Area / sweep:** Settings, dialogs, popups, commands / dialogs · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
// SidebarViewModel.cs:746
var dialogVm = new AddToPlaylistDialogViewModel(PlaylistItems, tracks.Count);
// SidebarViewModel.cs:775-777
if (selectedExistingId is Guid id)
{   await AddTracksToPlaylist(id, tracks); return; }
// SmartPlaylistEvaluator.cs:16 if (!playlist.IsSmartPlaylist || playlist.Rules.Count == 0) return new List<Track>();
// SidebarView.axaml.cs:153-154 CanAcceptTracks => item is { IsFolder: false, IsSmartPlaylist: false, PlaylistId: not null };
```

**Why it is a bug:** The dialog's list (AddToPlaylistDialog.axaml:180) is bound to every nav item, smart playlists included, with no filter. AddTracksToPlaylist (SidebarViewModel.cs:641) has no IsSmartPlaylist guard, unlike OpenAddSongsAsync (:676) and sidebar drag-and-drop (SidebarView.axaml.cs:154). So the IDs are appended to the smart playlist's TrackIds. A smart playlist's contents come only from its rules (PlaylistViewModel.cs:357-359), so the added songs never appear. The sidebar and grid build their '16 tracks · 54 min' line from TrackIds (SidebarViewModel.cs:331-349), so the smart playlist now shows the wrong count and duration.

**Impact:** Right-click a song > Add to Playlist > choose a smart playlist appears to succeed but does nothing, and the playlist tile then shows the wrong track count.

**Proposed fix (trivial):** Pass the dialog only manual playlists, e.g. new ObservableCollection<PlaylistNavItem>(PlaylistItems.Where(p => !p.IsSmartPlaylist)). Also add `if (playlist.IsSmartPlaylist) return;` to AddTracksToPlaylist.

**Verifier votes**

- **confirmed** (refute lens): SidebarViewModel.cs:746 passes PlaylistItems unfiltered. That list includes smart playlists (BuildPlaylistNavItem :322-330, added at :937). AddToPlaylistDialog.axaml:180 binds to all of them, and AddToPlaylistDialogViewModel.cs:33-53 does no filtering. The selected id goes to AddTracksToPlaylist (:776-777), which has no IsSmartPlaylist guard (:641-669). OpenAddSongsAsync (:676) and SidebarView.axaml.cs:154 do have that guard. PlaylistViewModel.cs:348/358 resolves smart playlists only via SmartPlaylistEvaluator, so the added ids never appear. One nuance: the smart playlist's TrackCount/MetaText (:331-349) was already wrong before this, because it is built from an empty TrackIds; the add changes a wrong '0 tracks' into a different wrong 'N tracks'. The core silent no-op is real.

### S24 — In the LRC editor, a focused button takes the Space key, so Space pauses the song or edits a line instead of stamping it

- **Location:** `src/Noctis/Views/LrcEditorDialog.axaml.cs:20` (also: `src/Noctis/Views/LyricsStudioPanel.axaml.cs:152`, `src/Noctis/Views/LrcEditorDialog.axaml:94`, `src/Noctis/Views/LrcEditorDialog.axaml:153`)
- **Area / sweep:** Settings, dialogs, popups, commands / dialogs · **Category:** input-handling · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
// LrcEditorDialog.axaml.cs:20
KeyDown += OnDialogKeyDown;
// :32-39
private void OnDialogKeyDown(object? sender, KeyEventArgs e)
{   // Space = tap-to-sync stamp (the core editing gesture).
    if (e.Key == Key.Space)
    {   Vm?.StampCurrentCommand.Execute(null);
        e.Handled = true;
```

**Why it is a bug:** The handler is registered for the bubbling phase and does not see already-handled events. A left click moves keyboard focus to the clicked control; Avalonia's FocusManager does this with a class handler on pointer press/release (https://github.com/AvaloniaUI/Avalonia/blob/master/src/Avalonia.Base/Input/FocusManager.cs). Button.OnKeyDown handles Space itself (IsPressed = true; e.Handled = true) and clicks on key-up (https://github.com/AvaloniaUI/Avalonia/blob/master/src/Avalonia.Controls/Button.cs). So while any button in the editor has focus, the window never receives Space. The normal flow is: open the editor, click Play (LrcEditorDialog.axaml:94-96), press Space to stamp. Space then clicks Play/Pause again and pauses the song. After clicking a row's +, − or ✕ button (lines 153-177), Space nudges or clears that line instead of stamping. The Lyrics Studio fixed exactly this: LyricsStudioPanel.axaml.cs:152 says 'Tunnelled so a focused Button cannot swallow Space first'.

**Impact:** Tap-to-sync, the editor's main gesture, pauses playback or changes the wrong line.

**Proposed fix (small):** Register the handler for the tunnel phase with AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel), add a tunnel KeyUp handler that marks Space handled, and skip both when the source is a TextBox. This mirrors LyricsStudioPanel.axaml.cs:137-172.

**Verifier votes**

- **confirmed** (refute lens): LrcEditorDialog.axaml.cs:20 uses a plain `KeyDown +=` (bubbling, skips handled events), and OnDialogKeyDown (:32-39) stamps on Space. The editor's buttons (Play/Pause :94-97, and the row timestamp, -, +, stamp and clear buttons :127-177) have no Focusable=False: the lrc-pill and lrc-nudge styles (:20-46) do not set it, and no global style does. The app's click-away unfocus (App.axaml.cs:86-97) only clears focus from TextBoxes, so a clicked Button keeps focus. Avalonia's Button.OnKeyDown marks Space handled and clicks on key-up, so the window handler never runs and Space clicks the focused button instead (Play/Pause toggles). LyricsStudioPanel.axaml.cs:137-172 uses a tunnel handler with a comment describing exactly this. The dialog is reachable from LyricsViewModel.cs:3016.

### S27 — Ctrl+A and Escape in the queue panel are handled by the page underneath first (Avalonia runs same-element tunnel handlers newest-first), so the queue never gets them

- **Location:** `src/Noctis/Views/MainWindow.axaml.cs:743` (also: `src/Noctis/Helpers/WindowKeyForwarder.cs:39`, `src/Noctis/Views/MainWindow.axaml.cs:1815`, `src/Noctis/Views/LibrarySongsView.axaml.cs:72`, `src/Noctis/Views/HomeView.axaml.cs:101`, `src/Noctis/Views/PlaylistView.axaml.cs:361`, `src/Noctis/Views/AlbumDetailView.axaml.cs:93`)
- **Area / sweep:** Settings, dialogs, popups, commands / commands · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
MainWindow.axaml.cs:741-743 (registered in the constructor):
    // Queue-row keys (GitHub #85). Tunnel at the window and registered before any page's
    // WindowKeyForwarder, so Ctrl+A / Escape inside the queue don't also hit the page.
    AddHandler(KeyDownEvent, OnQueueKeyDown, RoutingStrategies.Tunnel);
Helpers/WindowKeyForwarder.cs:39-40 (registered later, on page attach):
    _topLevel?.AddHandler(InputElement.KeyDownEvent, OnKeyDown,
        RoutingStrategies.Tunnel, handledEventsToo: true);
LibrarySongsView.axaml.cs:82: MultiSelectHelper.HandleTrackSelectAllByData(e, TrackList, _selectedTracks); // sets e.Handled = true
```

**Why it is a bug:** The code comment assumes registration order equals call order. In Avalonia 12.1.2, Interactive.AddToEventRoute appends one element's handlers in registration order (https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Base/Interactivity/Interactive.cs). EventRoute.RaiseEventImpl then walks the whole route backwards for Tunnel (`start = end - 1; step = end = -1`, https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Base/Interactivity/EventRoute.cs). So a page's forwarder, added later on attach, runs before OnQueueKeyDown. The page handlers take Ctrl+A unconditionally and Escape whenever the page has a selection, and set Handled. OnQueueKeyDown is not handledEventsToo, so it is skipped. This affects every page that uses WindowKeyForwarder: Songs, Playlist, Album detail, Albums, Favorites and Home. (The same ordering is why LyricsStudioPanel's later-registered Space handler correctly beats the global Play/Pause.)

**Impact:** With a queue row focused on those pages, Ctrl+A selects every song or album on the page behind the panel instead of the queue rows. The next right-click on the page then acts on that hidden 'select all' (see the context-menu finding). Escape clears the page's selection instead of the queue selection or closing the panel. With the Settings sheet open, Escape likewise clears the hidden page selection instead of closing Settings.

**Proposed fix (small):** Have WindowKeyForwarder.OnKeyDown ignore keys whose focused element is outside its owner and inside an overlay panel, e.g. `if (TopLevel.GetTopLevel(_owner)?.FocusManager?.GetFocusedElement() is Visual f && !_owner.IsVisualAncestorOf(f) && f.FindAncestorOfType<Border>(...QueuePopupPanel...) != null) return;`. A simpler option is to skip when focus is not inside _owner at all (Ctrl+A would then need a page click first). Also correct the misleading comment at MainWindow.axaml.cs:741-742.

**Verifier votes**

- **confirmed** (refute lens): The ordering claim is verified at runtime, not only from source: loading Avalonia.Base 12.1.2 (net10.0) in memory and adding two Tunnel handlers to one Interactive printed 'second-registered;first-registered;'. OnQueueKeyDown is added in the constructor path (MainWindow.axaml.cs:241 -> 743). WindowKeyForwarder adds its TopLevel tunnel handler later, on page attach (WindowKeyForwarder.cs:39-40), so the page handler runs first. LibrarySongsView.OnViewKeyDown (the forwarder target at :38) sets Handled for Escape when the page has a selection, and HandleTrackSelectAllByData (MultiSelectHelper.cs:235-238) sets Handled for any Ctrl+A. OnQueueKeyDown (MainWindow.axaml.cs:1815-1842) is not registered handledEventsToo, so it is skipped. The comments at MainWindow.axaml.cs:741-742 and WindowKeyForwarder.cs:53-54 assume the opposite order.

### S28 — Global shortcuts take Ctrl+Left/Right/Up/Down inside text boxes: word-jumping in the search box or the Lyrics Studio editor skips tracks and changes the volume

- **Location:** `src/Noctis/Views/MainWindow.axaml.cs:1563` (also: `src/Noctis/Models/Shortcuts.cs:63`, `src/Noctis/Views/LyricsStudioPanel.axaml:531`, `src/Noctis/Views/SidebarView.axaml:300`, `src/Noctis/Views/PlaylistView.axaml:421`)
- **Area / sweep:** Settings, dialogs, popups, commands / commands · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
MainWindow.axaml.cs:1559-1567:
    if (shortcuts.TryMatch(e) is not { } action) return;
    // An unmodified key (Space, or whatever the user bound) must still type in an
    // edit box: typing a space in the search box stays typing a space.
    if (e.KeyModifiers == KeyModifiers.None && e.Source is TextBox) return;
    if (!ExecuteShortcut(vm, action)) return;
    _consumedShortcut = action;
    e.Handled = true;
Models/Shortcuts.cs:63-66 (defaults):
    ShortcutAction.NextTrack => new KeyGesture(Key.Right, primary),
    ShortcutAction.PreviousTrack => new KeyGesture(Key.Left, primary),
    ShortcutAction.VolumeUp => new KeyGesture(Key.Up, primary),
```

**Why it is a bug:** The handler is a window-level Tunnel handler (MainWindow.axaml.cs:739), so it runs before the focused TextBox. It exempts only unmodified keys. Avalonia's TextBox does word-wise caret movement for Ctrl+Left/Right (Cmd+Left/Right moves to line start/end on macOS): `hasWholeWordModifiers = modifiers.HasAllFlags(keymap.WholeWordTextActionModifiers)` in OnKeyDown (https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/TextBox.cs). TextBox.OnKeyDown is reached through a class handler registered without handledEventsToo (InputElement static ctor, https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Base/Input/InputElement.cs), so once the tunnel handler sets Handled the TextBox never sees the key. The source comments at Shortcuts.cs:73-74 show the authors knew modified shortcuts fire from text boxes, but treated them as harmless. Affected text boxes in the main window: sidebar search (SidebarView.axaml:300), playlist rename (PlaylistView.axaml:421), the Lyrics Studio paste/edit box and review rows (LyricsStudioPanel.axaml:531, :634), and every Settings text field.

**Impact:** While typing in search, renaming a playlist or editing lyrics in Lyrics Studio, Ctrl+Left/Right jumps to the previous or next song instead of moving the caret, and Ctrl+Up/Down changes the volume. Word navigation does not work in any main-window text box.

**Proposed fix (trivial):** When e.Source is a TextBox, also skip gestures whose key is a text-navigation or editing key, whatever the modifiers: `if (e.Source is TextBox && (e.KeyModifiers == KeyModifiers.None || e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Back or Key.Delete)) return;`

**Verifier votes**

- **confirmed** (refute lens): MainWindow.axaml.cs:1559-1567 exempts only unmodified keys when the source is a TextBox, and ShortcutService.TryMatch (ShortcutService.cs:98-106) matches Key and exact modifiers. The defaults at Shortcuts.cs:59-66 map Ctrl+Left/Right/Up/Down (Cmd on macOS) to Previous/Next/Volume, so Ctrl+Left in SearchBox (SidebarView.axaml:300) or the Lyrics Studio draft box (LyricsStudioPanel.axaml:531) runs the shortcut and sets Handled in the window tunnel phase (registered at :739). TextBox.OnKeyDown is a bubble class handler that skips handled events, so the TextBox never sees the key. The later-registered tunnel handlers (page forwarders, Lyrics Studio OnHostKeyDown:156) all return early for TextBox sources, so nothing else intercepts it first. I found no comment or doc that makes this intended.

### S30 — PlaylistView keeps the previous playlist's Ctrl-selection when switching directly to another playlist, so menu actions in the new playlist act on songs from the old one

- **Location:** `src/Noctis/Views/PlaylistView.axaml.cs:77` (also: `src/Noctis/Views/PlaylistView.axaml.cs:550`, `src/Noctis/App.axaml:95`, `src/Noctis/Views/AlbumDetailView.axaml.cs:413`)
- **Area / sweep:** Settings, dialogs, popups, commands / commands · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
PlaylistView.axaml.cs:77-93:
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Reset shared menu so it picks up new VM commands
        _menuBuilder?.Reset();
        _menuBuilder = null;
        if (_observedVm != null) { ...unsubscribe... }
        _observedVm = DataContext as PlaylistViewModel;
        ...
(no _selectedTracks.Clear()). Compare AlbumDetailView.axaml.cs:412-413:
    // Drop any selection from the previous album so it doesn't carry over.
    _selectedTracks.Clear();
```

**Why it is a bug:** PlaylistViewModel is mapped by a plain DataTemplate (App.axaml:95), and the content host is a ContentControl (MainWindow.axaml:689). Avalonia's ContentPresenter recycles the old child when the same IRecyclingDataTemplate matches (`var toRecycle = rdt == _recyclingDataTemplate ? oldChild : null;`, https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/Presenters/ContentPresenter.cs), and DataTemplate.Build returns `existing ?? ...` (https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Markup/Avalonia.Markup.Xaml/Templates/DataTemplate.cs). Going from playlist A to playlist B (sidebar click, Back or Forward) therefore reuses the same PlaylistView with no detach, and _selectedTracks (the HashSet of Tracks) survives. The new VM has SelectedCount 0, so the selection bar is hidden. The next right-click still pushes the old set: `vm.CtrlSelectedTracks = _selectedTracks.ToList()` (:550).

**Impact:** In playlist B, 'Remove from Playlist' on a song leaves that song and instead silently removes any of A's selected songs that B also contains (then saves). Favorites, Add to Playlist, Convert and ReplayGain act on songs from A that the user cannot see. Songs shared by both playlists show up as ctrl-selected in B although the user never selected them there.

**Proposed fix (trivial):** In OnDataContextChanged, call ClearSelectionState() before switching _observedVm, mirroring AlbumDetailView.axaml.cs:413: clear _selectedTracks, strip the 'ctrl-selected' class from realized rows, and reset the outgoing VM's CtrlSelectedTracks and SelectedCount.

**Verifier votes**

- **confirmed** (refute lens): PlaylistView.axaml.cs:77-94: OnDataContextChanged resets the menu and VM subscriptions but never clears _selectedTracks (declared at :24). The only clear on navigation is in OnDetachedFromVisualTree (:772-780), and that does not run when the view is reused. The main host is a plain ContentControl bound to CurrentView (MainWindow.axaml:689) using the App.axaml:95 DataTemplate. OnPlaylistOpened (MainWindowViewModel.cs:2155-2161) swaps in a new PlaylistViewModel, so the same view is reused. The repo already documents this reuse for AlbumDetailView (AlbumDetailView.axaml.cs:398-413), which clears its selection for exactly this reason. The stale set is pushed into the new VM at :550 (context menu) and :437 (options button), and ContainerPrepared re-applies the ctrl-selected class to shared songs (:460). PlaylistViewModel.RemoveTrack (PlaylistViewModel.cs:666-686) uses CtrlSelectedTracks whenever it is non-empty, even if the clicked song is not in it. It then removes those IDs from B and saves. ToggleFavorite (:837-845), Convert (:821-826) and ReplayGain (:829-834) act on the stale tracks the same way. Medium is right: it needs a Ctrl-selection followed by switching directly to another playlist, but then it silently changes a playlist.

### U03 — Queue Ctrl+A then Delete (or removing a large selection) is O(N^2): every RemoveAt rebuilds the whole selection dictionary

- **Location:** `src/Noctis/Helpers/QueueRowSelection.cs:150` (also: `src/Noctis/ViewModels/PlayerViewModel.cs:1300-1311`, `src/Noctis/ViewModels/PlayerViewModel.cs:1320-1341`, `src/Noctis/Views/MainWindow.axaml.cs:517-525`, `src/Noctis/Views/MainWindow.axaml.cs:1764-1768`, `src/Noctis/Views/MainWindow.axaml.cs:1830-1835`, `src/Noctis/ViewModels/PlayerViewModel.cs:432`)
- **Area / sweep:** UI performance / ui-lists · **Category:** ui-freeze · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
QueueRowSelection.cs:117-121 (Apply, Remove case): Remap(i => i < at ? i : i >= at + n ? i - n : -1);
QueueRowSelection.cs:150-156
    private void Remap(Func<int, int> map)
    {
        var moved = _selected.Select(kv => (Row: map(kv.Key), Item: kv.Value)).ToList();
        _selected.Clear();
        foreach (var (row, item) in moved)
            if (row >= 0) _selected[row] = item;   // SortedDictionary insert
PlayerViewModel.cs:1309-1310: foreach (var i in rows) UpNext.RemoveAt(i);
MainWindow.axaml.cs:517-524: vm.Player.UpNext.CollectionChanged += (_, e) => { queueSelection.Apply(e); Dispatcher.UIThread.Post(() => { RenumberQueueRows(queueList); SyncQueueSelectionVisuals(queueList); }, DispatcherPriority.Loaded); };
MainWindow.axaml.cs:1833-1834: vm.Player.RemoveManyFromQueue(selection.Snapshot()); selection.Clear();
```

**Why it is a bug:** RemoveManyFromQueue removes rows one at a time, so N CollectionChanged Remove events fire synchronously on the UI thread. The selection is cleared only after the call (MainWindow.axaml.cs:1834; the context-menu path at 1766 never clears it). Each event therefore runs Apply -> Remap over the whole remaining selection: it allocates a List of S tuples and re-inserts S entries into a SortedDictionary. With S going from N down to 1, the total is about N^2/2 inserts, each O(log N) with a node allocation. Each event also posts a RenumberQueueRows/SyncQueueSelectionVisuals operation, raises PropertyChanged(HasContent) (PlayerViewModel.cs:432) and calls UpdateWaveformPlan. Example: a queue of 5,000 ("Play all" on the library), Ctrl+A and Delete in the queue panel. That is about 12.5M SortedDictionary inserts plus about 12.5M tuple list entries on the UI thread, several hundred MB of short-lived allocations (estimated, not measured). MoveBlockInQueue (drag of a large Shift-selected block) takes the same path, with 2x block-size events.

**Impact:** Multi-second UI freeze (audio keeps playing but the window hangs) when deleting a large queue selection with Delete or the row menu. It gets quadratically worse with queue length.

**Proposed fix (small):** In OnQueueKeyDown and OnQueueRemoveClick, take the snapshot, call selection.Clear() before RemoveManyFromQueue, then call RefreshQueueSelectionVisuals. Remap then iterates an empty dictionary. Also, in RemoveManyFromQueue, build the kept list once and call UpNext.ReplaceAll(kept) when rows.Count exceeds a small threshold (one Reset, which QueueRowSelection already handles). Coalesce the posted renumber with a pending flag.

**Verifier votes**

- **confirmed** (refute lens): PlayerViewModel.cs:1309-1310 removes rows one at a time (descending) with UpNext.RemoveAt. UpNext is a BulkObservableCollection (PlayerViewModel.cs:329) that does not override RemoveAt (BulkObservableCollection.cs:12-61), so every removal raises its own Remove event. MainWindow.axaml.cs:517-519 calls queueSelection.Apply(e) on each event. For Remove, Apply calls Remap (QueueRowSelection.cs:117-121), which copies the whole remaining selection into a list and re-inserts it into the SortedDictionary (150-157). The Delete-key path clears the selection only after RemoveManyFromQueue returns (MainWindow.axaml.cs:1833-1834). The context-menu path (1766) never clears it. After Ctrl+A on an N-row queue that comes to about N^2/2 inserts plus allocations. The queue has no size cap: ReplaceQueueAndPlay copies all tracks (PlayerViewModel.cs:1180-1182). Each event also posts a renumber/sync callback (MainWindow.axaml.cs:520-524) and a HasContent notification (PlayerViewModel.cs:432). Result: a multi-second UI stall on a large queue, on an uncommon path.

### U05 — Album page closes itself (navigates Back) during any library rescan: RefreshFromLibrary treats a partial progressive publish as 'album removed'

- **Location:** `src/Noctis/ViewModels/AlbumDetailViewModel.cs:330` (also: `src/Noctis.Core/Services/ILibraryService.cs:25`, `src/Noctis.Core/Services/LibraryService.cs:231`, `src/Noctis/ViewModels/MainWindowViewModel.cs:2198`, `src/Noctis/ViewModels/LibraryFoldersViewModel.cs:166`, `src/Noctis/ViewModels/PlayerViewModel.cs:3128`)
- **Area / sweep:** UI performance / ui-thread · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
AlbumDetailViewModel.cs:213  _libraryUpdatedHandler = (_, _) => Dispatcher.UIThread.Post(RefreshFromLibrary);
AlbumDetailViewModel.cs:330  var updatedAlbum = _library.Albums.FirstOrDefault(a => a.Id == Album.Id);
  :334  if (updatedAlbum == null) { ... updatedAlbum = _library.Albums.FirstOrDefault(a => a.Tracks.Any(t => trackIds.Contains(t.Id))); }
  :341  if (updatedAlbum == null)
  :343  {   // Album truly removed — navigate back
  :344      BackRequested?.Invoke(this, EventArgs.Empty);
LibraryService.cs:231-241 (progressive publish)  var snapshot = newTracks.ToArray(); _tracks = snapshot...; await RebuildIndexesAsync(persistCache: false); LibraryUpdated?.Invoke(...)
```

**Why it is a bug:** During every ScanAsync (Scan button, Scan-on-startup in MainWindowViewModel.InitializeAsync:699-713, auto-scan after adding/removing a media folder) LibraryService replaces _tracks/_albums every 1.5 s with ONLY the tracks found so far (LibraryService.cs:222-241). ILibraryService.cs:25-31 documents this: 'a missing id is "not scanned yet", not "deleted". Consumers that treat a missing track as removed ... must wait for the authoritative publish' (IsPublishingPartial). Only PlayerViewModel.OnLibraryUpdated (PlayerViewModel.cs:3128) honours it. AlbumDetailViewModel.RefreshFromLibrary does not: when the open album's folder has not been re-walked yet, both lookups return null and it raises BackRequested; MainWindowViewModel.OpenAlbumDetail (MainWindowViewModel.cs:2198-2202) then calls GoBackInHistory() because the page is current. Same root cause in LibraryFoldersViewModel.RefreshAsync (LibraryFoldersViewModel.cs:146,166-176): a partial forest without the selected folder sets SelectedNode = null, and because the next refresh captures selectedPath from the now-null SelectedNode, the user stays kicked out of their folder after the scan completes.

**Impact:** With 'Scan on startup' on, or after pressing Scan / adding a folder, a user browsing an album page is bounced back to the previous page a few seconds later (whenever that album's folder has not been reached by the walk yet). On the Folders tab the selected folder is deselected and the track pane empties mid-scan. Reproducible on every scan of a library large enough to take more than 1.5 s.

**Proposed fix (trivial):** At the top of AlbumDetailViewModel.RefreshFromLibrary add `if (_library.IsPublishingPartial) return;` (the authoritative publish arrives with it false, exactly like PlayerViewModel.cs:3128). In LibraryFoldersViewModel.RefreshAsync skip the rebuild (keep _isDirty = true) while `_library.IsPublishingPartial`, or at least keep the previous selectedPath when FindNode misses instead of nulling SelectedNode.

**Verifier votes**

- **confirmed** (refute lens): AlbumDetailViewModel.cs:213 posts RefreshFromLibrary on every LibraryUpdated with no IsPublishingPartial check. :330-345 looks the album up by Id, then by track id, and raises BackRequested when neither is found. LibraryService.cs:222-241 replaces _tracks every 1.5 s (ProgressivePublishMs=1500 at :23) with only newTracks found so far, rebuilds the indexes and fires LibraryUpdated. Unchanged files are also added to newTracks (:302), so this happens on every scan, including no-change ones. ILibraryService.cs:25-31 documents that a missing id during a partial publish means 'not scanned yet'. Only PlayerViewModel.cs:3128 checks for this. MainWindowViewModel.cs:2198-2202 calls GoBackInHistory when the page is current. Folders tab: LibraryFoldersViewModel.cs:86-91 refreshes while active. FolderTreeBuilder.cs:39-51 creates subfolder nodes only from tracks, so a partial forest lacks unwalked folders. RefreshAsync (:166-176) then nulls SelectedNode, and the next pass reads selectedPath from the null node (:146), so the user stays deselected. Rollback via RestoreOriginalLibrary (:215) comes too late.

### U06 — Album page tears down and rebuilds every track row and both carousels on every LibraryUpdated, even when nothing about the album changed

- **Location:** `src/Noctis/ViewModels/AlbumDetailViewModel.cs:355` (also: `src/Noctis/ViewModels/AlbumDetailViewModel.cs:213`, `src/Noctis/ViewModels/AlbumDetailViewModel.cs:262`, `src/Noctis/ViewModels/AlbumDetailViewModel.cs:581`, `src/Noctis/ViewModels/ArtistDetailViewModel.cs:491`)
- **Area / sweep:** UI performance / ui-thread · **Category:** ui-thread-blocking · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Prior audit:** AUDIT.md 'MINOR ITEMS NOT PROMOTED TO FINDINGS' (AlbumDetailViewModel.RefreshFromLibrary unconditional Tracks.ReplaceAll) — still present; the row/menu teardown impact was not assessed there

**Evidence**

```
AlbumDetailViewModel.cs:355  Album = updatedAlbum;              // new Album instance per index rebuild → OnAlbumChanged → BuildRelatedSections()
  :357  Tracks.ReplaceAll(updatedAlbum.Tracks);
  :358  BuildDiscGroups();                // DiscGroups.Clear() + new ObservableCollections
  :359  AnimatedCoverPath = ResolveAlbumAnimatedCover();
  :362  BuildRelatedSections();           // second full rebuild of OtherVersions/MoreByArtist
  :584  DiscGroups.Clear();
  :526  OtherVersions.Clear(); ... :557 MoreByArtist.Clear();
```

**Why it is a bug:** RefreshFromLibrary runs (UI thread, Dispatcher.Post at :213) for every LibraryUpdated. LibraryService.RebuildIndexesAsync creates new Album objects each time (LibraryService.cs:1900), so `Album = updatedAlbum` always fires OnAlbumChanged (:262-271, which already calls BuildRelatedSections), then the method rebuilds the disc groups (Clear + re-add; the per-disc track lists are deliberately non-virtualized, so every row is re-realized) and calls BuildRelatedSections a second time. No change detection. The app already fixed this exact pattern elsewhere: ArtistDetailViewModel.cs:491-495 ('the unconditional Clear+Add tore down every row and tile each time: the row under the pointer flickered, and a track menu whose owner row left the tree was closed by Avalonia') and HomeViewModel.cs:449-451. AlbumDetailView attaches the track ContextMenu to the row container (AlbumDetailView.axaml.cs:306 item.ContextMenu = menu), so tearing the row down closes an open menu. It also re-probes the music folder for cover.mp4 each time (AnimatedCoverService.cs:30-34).

**Impact:** While an album page is open during a scan (every 1.5 s), a watched-folder import, a BPM/key analysis pass (AudioAnalysisCoordinator.cs:208 NotifyMetadataChanged) or any metadata save elsewhere, the whole track list and both carousels are re-realized: hover flicker, dropped frames, and an open right-click menu on a track snaps shut.

**Proposed fix (small):** In RefreshFromLibrary: return early when IsPublishingPartial; resolve with _library.GetAlbumById; compare updatedAlbum.Tracks with Tracks by reference/sequence (like HomeViewModel.ReplaceRowIfChanged) and only ReplaceAll/BuildDiscGroups when they differ; assign the Album backing field without triggering BuildRelatedSections when only the instance changed, and drop the duplicate BuildRelatedSections call at :362.

**Verifier votes**

- **confirmed** (refute lens): RefreshFromLibrary (AlbumDetailViewModel.cs:327-368) has no change detection. It always calls Tracks.ReplaceAll (BulkObservableCollection.cs:18-27 clears the list and raises Reset), BuildDiscGroups (:583 DiscGroups.Clear() and new per-disc ObservableCollections) and ResolveAlbumAnimatedCover (AnimatedCoverService.cs:27-46 runs File.Exists probes). It also calls BuildRelatedSections explicitly at :362. Album (Models/Album.cs:11) does not override Equals, and RebuildIndexesCoreAsync (LibraryService.cs:1900) creates new Album instances on each rebuild. So `Album = updatedAlbum` (:355) fires OnAlbumChanged, and OnAlbumChanged calls BuildRelatedSections again (:270), making two full Clear+Add passes over OtherVersions and MoreByArtist (:526, :556). The track list is a non-virtualized StackPanel (AlbumDetailView.axaml:736-741). The row owns the context menu (AlbumDetailView.axaml.cs:305-306). ArtistDetailViewModel.cs:491-495 documents the same teardown problem, with menus closing, and fixes it there. Triggers include scan publishes, artwork publishes (LibraryService.cs:706) and NotifyMetadataChanged (:1779-1785).

### U07 — Opening an artist page runs a whole-library regex token classification on the UI thread, and every artist page kept in history re-runs it on each LibraryUpdated

- **Location:** `src/Noctis/ViewModels/ArtistDetailViewModel.cs:435` (also: `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs:1289`, `src/Noctis.Core/Models/ArtistGrouping.cs:147`, `src/Noctis/ViewModels/MainWindowViewModel.cs:2247`, `src/Noctis/ViewModels/MainWindowViewModel.cs:1638`)
- **Area / sweep:** UI performance / ui-thread · **Category:** ui-thread-blocking · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
ArtistDetailViewModel.cs:333   Rebuild();   // ctor, UI thread
  :339   _libraryUpdatedHandler = (_, _) => Dispatcher.UIThread.Post(Rebuild);
  :435   var (releases, appearsOn, songs) = Classify(_library.Albums, ArtistName);
  :361-363  var appearsOn = allAlbums.Where(a => !releaseIds.Contains(a.Id)
                && a.Tracks.Any(t => LibraryAlbumsViewModel.ContainsArtistToken(t.Artist, artistName)))
  :368-370  var songs = allAlbums.SelectMany(a => a.Tracks)
                .Where(t => LibraryAlbumsViewModel.ContainsArtistToken(t.Artist, artistName))
LibraryAlbumsViewModel.cs:1298-1299  var fieldTokens = Track.ParseArtistTokens(artistField);
                                     var filterTokens = Track.ParseArtistTokens(artistName);
```

**Why it is a bug:** Caller chain: sidebar/Artists grid/any 'View Artist' link → MainWindowViewModel.OpenArtistDiscography (MainWindowViewModel.cs:2247, UI thread) → new ArtistDetailViewModel → Rebuild() synchronously before CurrentView is assigned. Classify walks every track of every album twice (appears-on + songs). For each track whose Artist is not an exact match, ContainsArtistToken calls Track.ParseArtistTokens twice (the filter name is re-parsed every call), each a compiled-Regex Split + Select/Where/Distinct(HashSet)/ToArray (ArtistGrouping.cs:147-157). On a 50k-track library that is ~200k regex splits and >1M allocations per open (estimate; not measured). In addition the LibraryUpdated handler is not gated on visibility: ArtistDetailViewModel instances stay alive and subscribed while they sit in the 30-entry navigation history (DisposeViewIfTransient skips views in history, MainWindowViewModel.cs:1517-1528, MaxNavigationHistory=30 at :1638), so every LibraryUpdated (1.5 s progressive publishes during a scan, watcher imports, metadata saves, BPM-analysis write-back) posts one full Classify per artist page in history — the section VMs got IsActive gating for exactly this reason (MainWindowViewModel.cs:69-88) but detail pages did not.

**Impact:** Visible hitch when opening an artist page on large libraries (estimated a few hundred ms at 50k tracks, scaling linearly), and repeated UI stalls every ~1.5 s during scans for each artist page the user has visited this session (stacking with history depth). Rebuild on a partial publish also makes the visible artist page's lists shrink and regrow mid-scan.

**Proposed fix (medium):** Parse the filter tokens once per Classify (pass a pre-split HashSet into a ContainsArtistToken overload), run Classify via Task.Run with a generation guard (apply results on the UI thread), and gate the LibraryUpdated handler: mark dirty when the page is not CurrentView (or IsPublishingPartial) and rebuild on re-activation, mirroring the IsActive pattern of the section view models. Better still, use the library's album-by-artist index (_library.GetAlbumsByArtist, already used by AlbumDetailViewModel.cs:516) for releases.

**Verifier votes**

- **confirmed** (refute lens): The ArtistDetailViewModel ctor calls Rebuild() synchronously (ArtistDetailViewModel.cs:333). OpenArtistDiscography (MainWindowViewModel.cs:2247) constructs it on the UI thread. Rebuild (:435) calls Classify (:351-375), which evaluates ContainsArtistToken for every album (releases) and for every track (appears-on, which short-circuits per album, and songs). Unless the field is an exact match, ContainsArtistToken (LibraryAlbumsViewModel.cs:1298-1299) re-parses both the field and the constant artistName on each call. Each parse is a regex Split plus a LINQ Distinct/ToArray (ArtistGrouping.cs:147-157, via Track.cs:827). The LibraryUpdated handler (:339) posts Rebuild with no visibility or IsPublishingPartial gate, and the class has no IsActive. Pages in history are not disposed (DisposeViewIfTransient at MainWindowViewModel.cs:1517-1528; history cap of 30 at :1638), so every retained artist page reclassifies on each LibraryUpdated. The section VMs were gated for exactly this reason (:69-88). The timing figures are estimates, but the code path is as described.

### U08 — Bulk Lyrics dialog reads the lyrics store of every selected track synchronously on the UI thread before it opens

- **Location:** `src/Noctis/ViewModels/BulkLyricsViewModel.cs:113` (also: `src/Noctis/ViewModels/MetadataHelper.cs:105`, `src/Noctis/ViewModels/LyricsStudioViewModel.cs:232`, `src/Noctis/Services/LyricsStudio/LyricsStudioDraftStore.cs:53`)
- **Area / sweep:** UI performance / ui-thread · **Category:** ui-thread-blocking · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
BulkLyricsViewModel.cs:44-45  foreach (var t in tracks)
                                 Rows.Add(new Row(t));
  :110  public Row(Track track)
  :113      Status = string.IsNullOrWhiteSpace(track.SyncedLyrics) ? (string.IsNullOrWhiteSpace(track.Lyrics) ? "no lyrics" : "plain lyrics") : "synced";
MetadataHelper.cs:109  var vm = new BulkLyricsViewModel(tracks, service, remove);
```

**Why it is a bug:** LibrarySongsViewModel.FetchLyrics / RemoveLyrics and PlaylistViewModel equivalents are [RelayCommand]s (UI thread) → MetadataHelper.OpenBulkLyricsDialog → BulkLyricsViewModel ctor. Each Row evaluates track.SyncedLyrics, whose getter performs a synchronous LyricsStore.Read (File.Exists + ReadAllText + JSON deserialize when a file exists; Track.cs:252, LyricsStore.cs:126-143); the 16-entry LRU gives no benefit across a large selection. This is independent of the already-reported row-virtualization issue: the cost is paid in the constructor before any row is rendered. Same pattern: LyricsStudioViewModel ctor calls _drafts.TryLoad (File.Exists + File.ReadAllText, LyricsStudioDraftStore.cs:53-54) per selected track on the UI thread (LyricsStudioViewModel.cs:232-243) when opened from a Songs selection.

**Impact:** 'Fetch lyrics' / 'Remove lyrics' on a big selection (e.g. Ctrl+A on Songs) freezes the window for one file probe/read per track before the dialog even appears — seconds for thousands of tracks.

**Proposed fix (small):** Create the rows with a placeholder status and compute Status in Task.Run (reading the store off-thread), posting results back; or have ILyricsBulkService expose a batched off-thread status query. Apply the same to LyricsStudioViewModel's draft probing (enumerate the drafts directory once into a HashSet off-thread).

**Verifier votes**

- **confirmed** (refute lens): BulkLyricsViewModel.cs:44-45 builds one Row per selected track in the ctor. Row (:113) reads track.SyncedLyrics, and track.Lyrics when that is empty. The Track.cs:250-257 getter calls LyricsStore.Read, which on a cache miss does File.Exists, then ReadAllText and JSON deserialize when a file exists (LyricsStore.cs:126-135). The LRU holds 16 entries (:52), so a large selection pays one disk probe per track. The ctor runs synchronously on the UI thread from MetadataHelper.cs:109. Callers are the [RelayCommand] FetchLyrics/RemoveLyrics handlers at LibrarySongsViewModel.cs:415-427 and PlaylistViewModel.cs:625-637, and the dialog is shown only after the ctor returns. The LyricsStudioViewModel ctor (:232-243) does the same with _drafts.TryLoad per track (LyricsStudioDraftStore.cs:53-54). This is an uncommon path (a bulk action on a large selection), and 'seconds' is plausible only for thousands of tracks or cold/HDD storage, so medium is right.

### U09 — Lyrics Background picker and Lyrics Studio 'Choose songs' picker run an un-debounced, allocation-heavy full-library search on the UI thread per keystroke

- **Location:** `src/Noctis/ViewModels/LyricsBackgroundPickerViewModel.cs:127` (also: `src/Noctis/ViewModels/LyricsStudioPickerViewModel.cs:137`, `src/Noctis.Core/Helpers/SearchText.cs:43`, `src/Noctis/ViewModels/AddSongsDialogViewModel.cs:81`, `src/Noctis/ViewModels/LibraryFoldersViewModel.cs:219`)
- **Area / sweep:** UI performance / ui-thread · **Category:** ui-thread-blocking · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
LyricsBackgroundPickerViewModel.cs:127  partial void OnSearchTextChanged(string value) => RefreshResults();
  :139-146  foreach (var album in _library.Albums.Where(a => SearchText.Matches(a.Name, query) || SearchText.Matches(a.Artist, query)).Take(MaxAlbumRows)) ...
            foreach (var track in _library.Tracks.Where(t => PlaylistViewModel.MatchesSearch(t, query)).Take(MaxTrackRows)) ...
LyricsStudioPickerViewModel.cs:104  partial void OnSearchTextChanged(string value) => RefreshResults();
  :166-167  foreach (var track in _library.Tracks.Where(t => t.SourceType == SourceType.Local && PlaylistViewModel.MatchesSearch(t, query)).Take(MaxTrackRows))
SearchText.cs:54-56  var nq = Normalize(query); ... return Normalize(source).Contains(nq, StringComparison.Ordinal);
```

**Why it is a bug:** Both setters are TwoWay-bound to dialog TextBoxes, so RefreshResults runs synchronously on the UI thread for every character typed. Take(80) only short-circuits when 80 matches exist; for any specific query (the normal case) it scans the whole library. For every non-matching field SearchText.Matches re-normalizes BOTH the query and the source (string.Normalize(FormD) + StringBuilder + ToString), i.e. up to 6 normalizations and ~6 string allocations per track per keystroke. AddSongsDialogViewModel.cs:81-88 documents the identical problem and fix ('a query scanned all 50,000 tracks per keystroke ... Every other search surface in the app already debounces'); these two newer pickers regressed it. LibraryFoldersViewModel.RebuildTrackPane (:219-232) runs the same normalized matcher over every track on the UI thread for a Folders search with no folder selected (debounced by the top bar, but still synchronous).

**Impact:** Typing in Settings › Lyrics Background Video › Modify, or in Lyrics Studio › Choose songs, stutters on each keystroke on large libraries (estimated 100-300 ms per key at 50k tracks, plus GC pressure); fast typing queues several full scans back to back.

**Proposed fix (small):** Add the same 250 ms debounce used by AddSongsDialogViewModel, normalize the query once per refresh (pass the precomputed key and compare against the cached Track.SearchTitleKey/SearchArtistKey/SearchAlbumKey like LibrarySongsViewModel.MatchesSearch does), and run the scan in Task.Run with a generation guard, applying Results on the UI thread. Do the same for the Folders all-roots filter.

**Verifier votes**

- **confirmed** (refute lens): LyricsBackgroundPickerViewModel.cs:127 `partial void OnSearchTextChanged(string value) => RefreshResults();` and LyricsStudioPickerViewModel.cs:104 are the same, with no debounce. The TextBoxes in LyricsBackgroundPickerDialog.axaml:218 and LyricsStudioPickerDialog.axaml:155 use a plain `{Binding SearchText}` with no Delay, and the code-behind only calls SearchBox.Focus(). RefreshResults (:129-149 and :137-173) runs Albums.Where(SearchText.Matches x2) and Tracks.Where(PlaylistViewModel.MatchesSearch) synchronously. MatchesSearch (PlaylistViewModel.cs:254-260) calls SearchText.Matches for Title, Artist and Album, and each call re-normalizes both query and source (SearchText.cs:54-56). Take(20/80) only stops early once that many matches exist. AddSongsDialogViewModel.cs:81-109 documents exactly this problem and adds a 250 ms debounce. Only LyricsStudioPicker's ScanFormats moves the sidecar I/O off-thread; the matching itself stays on the UI thread. The 100-300 ms figure is an estimate, but the defect is real.

### U11 — Multi-track metadata 'Rename files by pattern' moves every file (plus sidecar probes/moves) synchronously on the UI thread

- **Location:** `src/Noctis/ViewModels/MetadataViewModel.cs:2600` (also: `src/Noctis/ViewModels/MetadataViewModel.cs:2680`, `src/Noctis/ViewModels/MetadataViewModel.cs:1063`, `src/Noctis/ViewModels/MetadataHelper.cs:214`)
- **Area / sweep:** UI performance / ui-thread · **Category:** ui-thread-blocking · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
MetadataViewModel.cs:2600  if (_multiSelect && ApplyRename && _albumTracks != null)
  :2603      foreach (var t in _albumTracks)
  :2605          var newPath = ComputeRenamedPath(t, out var conflict, renameSeen);   // File.Exists(newPath) at :1074
  :2611              File.Move(t.FilePath, newPath);
  :2612              MoveLyricSidecars(t.FilePath, newPath);   // 3x (File.Exists old + File.Exists new [+ File.Move]) at :2682-2689
  :2615          catch { /* Non-fatal — skip this file */ }
```

**Why it is a bug:** Save is a [RelayCommand] (MetadataViewModel.cs:2256-2265) invoked from the Save button, so SaveInternalAsync starts on the UI thread and every `await Task.Run(...)` before this block resumes on the Avalonia synchronization context. The tag writes above were moved to Task.Run for exactly this reason (comment at :2383-2386: 'doing it on the UI thread froze the app for seconds on large albums'), but the rename loop that follows them is still inline: per selected track one File.Exists, one File.Move in the user's music folder and up to six more File.Exists/File.Move for .lrc/.ttml/.txt sidecars. Multi-select is reachable with arbitrary selection size (LibrarySongsViewModel.OpenMetadata → MetadataHelper.OpenMultiTrackMetadataWindow with albumTracks = selection).

**Impact:** Renaming a large selection (hundreds/thousands of files, or any count on a NAS/SMB share or a busy HDD) freezes the whole window — the 'Saving…' spinner stops animating and input is ignored until every rename finishes; a failed rename is silently skipped with no log.

**Proposed fix (small):** Compute the rename plan on the UI thread (paths only), run the File.Move + MoveLyricSidecars loop inside `await Task.Run(...)`, collect (track, newPath) successes and assign t.FilePath back on the UI thread; log failures via DebugLog.

**Verifier votes**

- **confirmed** (refute lens): MetadataViewModel.cs:2600-2618 matches the finding: a foreach over _albumTracks calls ComputeRenamedPath (File.Exists at :1074), File.Move (:2611) and MoveLyricSidecars (:2680-2693: 3 extensions x 2 File.Exists + File.Move), with an empty catch at :2615. Save is a [RelayCommand] bound in MetadataWindow.axaml:499. SaveInternalAsync has no ConfigureAwait(false), so after the earlier `await Task.Run` calls (:2401, :2435) the method resumes on the UI thread and this loop runs there. The tag writes were moved off-thread for exactly this reason (comment :2382-2386). Multi-select accepts any selection size (MetadataHelper.cs:214-228, albumTracks: tracks.ToList(), multiSelect: true). Failed renames are swallowed without logging. Minor miscount: the sidecar work is up to 6 File.Exists plus 3 File.Move per file.

### U13 — Player removes deleted queue entries one at a time on LibraryUpdated: O(queue × removed) plus one CollectionChanged (and posted renumber) per track

- **Location:** `src/Noctis/ViewModels/PlayerViewModel.cs:3150` (also: `src/Noctis/Views/MainWindow.axaml.cs:517`, `src/Noctis/Helpers/QueueRowSelection.cs:150`)
- **Area / sweep:** UI performance / ui-thread · **Category:** ui-thread-blocking · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
PlayerViewModel.cs:3144  var deletedTracks = UpNext.Where(t => !t.IsExternal && _library.GetTrackById(t.Id) == null).ToList();
  :3150  foreach (var track in deletedTracks)
  :3151      UpNext.Remove(track);
  :3153  var deletedHistory = History.Where(...).ToList();
  :3154  foreach (var track in deletedHistory)
  :3155      History.Remove(track);
MainWindow.axaml.cs:517-524  vm.Player.UpNext.CollectionChanged += (_, e) => { queueSelection.Apply(e); Dispatcher.UIThread.Post(() => { RenumberQueueRows(queueList); SyncQueueSelectionVisuals(queueList); }, DispatcherPriority.Loaded); };
```

**Why it is a bug:** OnLibraryUpdated is posted to the UI thread (PlayerViewModel.cs:3121). Collection<T>.Remove is a linear search plus an array shift ('This method performs a linear search; therefore, this method is an O(n) operation' — https://learn.microsoft.com/en-us/dotnet/api/system.collections.objectmodel.collection-1.remove), and each call raises its own CollectionChanged, which MainWindow handles with QueueRowSelection.Apply and a separately posted renumber/visual-sync job. UpNext is a BulkObservableCollection that already supports ReplaceAll (used at PlayerViewModel.cs:2169). Scenario: 'Shuffle all' queues the whole library (PlayerViewModel.cs:477-479), then the user removes a media folder or deletes a large set of tracks → the authoritative LibraryUpdated removes k tracks from an n-track UpNext one by one.

**Impact:** Removing a folder of 10k tracks while a whole-library shuffle (50k) is queued costs ~10k linear searches/shifts plus 10k collection events and 10k queued dispatcher jobs on the UI thread — a multi-second freeze of the app right after the rescan finishes; smaller cases scale proportionally.

**Proposed fix (trivial):** Build the kept list once (`var kept = UpNext.Where(t => t.IsExternal || _library.GetTrackById(t.Id) != null).ToList();`) and call UpNext.ReplaceAll(kept) when kept.Count != UpNext.Count (single Reset); same for History.

**Verifier votes**

- **confirmed** (refute lens): PlayerViewModel.cs:3144-3155 matches the quote: deleted UpNext/History entries are removed one by one with Collection.Remove inside the UI-thread Post from OnLibraryUpdated (3121). UpNext and History are BulkObservableCollection<Track> (329, 332), and ReplaceAll exists (Noctis.UI/Helpers/BulkObservableCollection.cs:18). Each Remove fires CollectionChanged to several subscribers: HasContent (432), UpdateWaveformPlan (PlayerViewModel.Waveform.cs:33), CoverFlowViewModel:197, MiniPlayerViewModel:58, LocalApiEventHub:198, and MainWindow.axaml.cs:517-524. The MainWindow handler runs QueueRowSelection.Apply and posts a renumber/sync job for each event. The scenario can happen: Play with nothing loaded queues a weighted shuffle of the whole library via ReplaceQueueAndPlay (476-479), and removing a folder then publishes the non-partial LibraryUpdated. History is trimmed (TrimHistory), so the cost is mostly UpNext. It only hits on the uncommon path of a large removal against a large queue, so medium fits.

### U14 — Send to Folder rebuilds the whole plan (file stats per track) and the unvirtualized row list on the UI thread for every keystroke in the Destination box

- **Location:** `src/Noctis/ViewModels/SendToFolderViewModel.cs:54` (also: `src/Noctis/Views/SendToFolderDialog.axaml:80-81`, `src/Noctis/ViewModels/SendToFolderViewModel.cs:61-77`, `src/Noctis/Services/SendToFolderService.cs:65-110`, `src/Noctis/ViewModels/PlaylistViewModel.cs:656-663`)
- **Area / sweep:** UI performance / ui-lists · **Category:** ui-thread-io · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
SendToFolderDialog.axaml:80: <TextBox Grid.Column="0" Classes="pill" Text="{Binding Destination}" .../>
SendToFolderViewModel.cs:54: partial void OnDestinationChanged(string value) => RebuildPlan();
SendToFolderViewModel.cs:65-76
        Rows.Clear();
        var root = Destination?.Trim() ?? string.Empty;
        if (root.Length == 0 || !Directory.Exists(root)) { ... return; }
        _plan = _service.Plan(_tracks, root, OrganizeIntoFolders ? _organizePattern : null, IncludeLyrics);
        foreach (var item in _plan)
            Rows.Add(new PlanRow(item, root));
SendToFolderService.cs:76-77: var existing = probe(target); var sourceProbe = probe(source);
SendToFolderService.cs:113-118: var info = new FileInfo(path); return info.Exists ? new FileProbe(info.Length) : null;
```

**Why it is a bug:** An Avalonia TextBox.Text binding pushes to the source on every keystroke (UpdateSourceTrigger Default = PropertyChanged, https://docs.avaloniaui.net/api/avalonia/data/updatesourcetrigger). Each keystroke runs RebuildPlan synchronously on the UI thread. That is one Directory.Exists, and whenever the typed prefix is an existing folder (typing 'E:\Music\Phone' passes through 'E:\' and 'E:\Music'), SendToFolderPlanner.Plan. Plan does 2-3 FileInfo stats per track (target, source, .lrc sidecar, plus the rename loop), then clears and re-adds every row into a non-virtualized StackPanel (see the dialog-virtualization finding). The dialog is opened for a whole playlist (PlaylistViewModel.SendPlaylistToFolder) or a Ctrl+A selection. The target is typically a USB stick or card reader, where each stat is slow.

**Impact:** Typing or editing the destination path freezes the dialog for every character that forms an existing folder. With a few hundred tracks on a slow USB target this is a visible stall per keystroke.

**Proposed fix (small):** Debounce RebuildPlan (about 300 ms DispatcherTimer) and run _service.Plan on Task.Run with a generation guard. Apply the result with one ReplaceAll into a BulkObservableCollection shown in a VirtualizingStackPanel. Alternatively bind with UpdateSourceTrigger=LostFocus and rebuild on Browse or Enter.

**Verifier votes**

- **confirmed** (refute lens): SendToFolderViewModel.cs:54 is `partial void OnDestinationChanged(string value) => RebuildPlan();`. RebuildPlan (61-88) runs synchronously: Rows.Clear, Directory.Exists, _service.Plan, and one Rows.Add per item. SendToFolderDialog.axaml:80 binds TextBox Text={Binding Destination} with no UpdateSourceTrigger, and Avalonia's default for TextBox.Text is PropertyChanged, so this runs on every keystroke. SendToFolderPlanner.Plan (SendToFolderService.cs:75-100) probes the target, the source, the rename loop and the .lrc sidecar for each track. DiskProbe (113-118) is a FileInfo stat. Rows go into an ItemsControl with a plain StackPanel ItemsPanel inside a ScrollViewer (axaml:109-111), which is not virtualized. Drive-root and parent-folder prefixes such as 'E:' and 'E:\' pass Directory.Exists, so the full plan runs partway through typing. This only matters when the user types the path instead of using Browse, so medium is reasonable.

### U17 — Statistics page still recomputes all library/history aggregates synchronously on the UI thread on every visit

- **Location:** `src/Noctis/ViewModels/StatisticsViewModel.cs:109` (also: `src/Noctis/ViewModels/MainWindowViewModel.cs:1951`, `src/Noctis/ViewModels/MainWindowViewModel.cs:522`, `src/Noctis/ViewModels/SettingsViewModel.cs:5392`)
- **Area / sweep:** UI performance / ui-thread · **Category:** ui-thread-blocking · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Prior audit:** AUDIT_2026-07-24.md 'Statistics recomputes everything synchronously on the UI thread on every visit'
- **Needs runtime check:** yes

**Evidence**

```
StatisticsViewModel.cs:109  public void Refresh()
  :111      var tracks = _library.Tracks;
  :112      var events = _playHistory.Events;
  :114      ComputeOverview(tracks, events);   // Sum x2, Dictionary of all tracks, ListeningStatsCalculator, GroupBy artist, ToDictionary albums + GroupBy AlbumId
  :115      ComputeQuality(tracks);            // 4 Count/Where passes + GroupBy(FormatLabel) with ToLowerInvariant per track
  :116      ComputeHistory(tracks, events);    // GroupBy events, OrderBy per group, filter+sort favorites
MainWindowViewModel.cs:2098-2101  private StatisticsViewModel RefreshAndReturnStatistics(...) { vm.Refresh(); return vm; }
```

**Why it is a bug:** Navigate("statistics") (MainWindowViewModel.cs:1951, reached from the sidebar, the command palette and Settings › 'View All Stats' at :522-531) calls vm.Refresh() synchronously before CurrentView is set. Refresh performs roughly a dozen full passes over the library plus several string-keyed GroupBy/OrderBy passes (ComputeTopArtists groups by GroupingArtist.Trim() with a per-track Trim allocation, ComputeQuality's FormatLabel lower-cases every codec string). SettingsViewModel moved the smaller subset of this exact work off the UI thread with the comment 'On a large library that is a visible stall on the click it is reacting to' (SettingsViewModel.cs:5383-5405); the Statistics page itself was never converted. Prior audit item is still present in its main part (only the duplicate Sum and Reverse() were fixed).

**Impact:** Clicking Statistics (or 'View All Stats' from Settings, which swaps views while the modal animates) freezes the UI for the whole computation on large libraries — estimated 100-300 ms at 50k-100k tracks, every visit (no caching by design).

**Proposed fix (medium):** Snapshot tracks/events on the UI thread, compute an immutable result object in Task.Run (same shape as SettingsViewModel.RefreshLibraryStatsAsync/ComputeLibraryStats), then apply the ReplaceAll calls on the UI thread behind a generation guard; keep showing the previous numbers until the new ones land.

**Verifier votes**

- **confirmed** (refute lens): StatisticsViewModel.cs:109-117 Refresh() runs ComputeOverview/ComputeQuality/ComputeHistory inline, and MainWindowViewModel.cs:1951 plus :2098-2102 call vm.Refresh() synchronously inside the CurrentView switch. Settings > View All Stats (:522-531) reaches the same path. The cost is heavier than the finding says: GroupingArtist is a computed property (Track.cs:595, which calls GetGroupingArtist/GetPrimaryArtist), and ComputeTopArtists reads it 3 times per track (:160-162). CodecShortName also calls ToLowerInvariant per track (Track.cs:714). Also run per visit: ToDictionary albums + GroupBy AlbumId (:188-191), GroupBy FormatLabel (:238), and GroupBy events plus OrderByDescending per group (:330-335). SettingsViewModel.cs:5383-5405 moved the equivalent subset off the UI thread for exactly this reason. AUDIT_2026-07-24.md:795-798 raised the item before; only the duplicate Sum (:165) and Reverse (:286-291) were fixed. The 100-300 ms figure is an unmeasured estimate. The stall only matters on large libraries, so medium is right.

### U18 — Selection-sized tool dialogs (Convert, Bulk Lyrics, Metadata Finder, Duplicate Finder, Send to Folder) realize every row: the ReplayGain virtualization fix was not applied to its siblings

- **Location:** `src/Noctis/Views/AudioConverterDialog.axaml:165` (also: `src/Noctis/Views/BulkLyricsDialog.axaml:64-67`, `src/Noctis/Views/MetadataFinderDialog.axaml:108`, `src/Noctis/Views/DuplicateFinderDialog.axaml:111`, `src/Noctis/Views/DuplicateFinderDialog.axaml:120`, `src/Noctis/Views/SendToFolderDialog.axaml:109-112`, `src/Noctis/ViewModels/AudioConverterViewModel.cs:73-74`, `src/Noctis/ViewModels/BulkLyricsViewModel.cs:44-45`, `src/Noctis/ViewModels/MetadataFinderViewModel.cs:39-40`, `src/Noctis/ViewModels/DuplicateFinderViewModel.cs:46-47`, `src/Noctis/ViewModels/MetadataHelper.cs:72-75`, `src/Noctis/ViewModels/AudioConverterViewModel.cs:115`, `src/Noctis/ViewModels/BulkLyricsViewModel.cs:63`)
- **Area / sweep:** UI performance / ui-lists · **Category:** ui-virtualization · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
AudioConverterDialog.axaml:163-166
                        <Border Background="#10FFFFFF" CornerRadius="8" Padding="10" MaxHeight="220">
                            <ScrollViewer ...>
                                <ItemsControl ItemsSource="{Binding Jobs}">
                                    <ItemsControl.ItemTemplate>   (no ItemsPanel)
AudioConverterViewModel.cs:73-74: foreach (var t in tracks) Jobs.Add(new JobRow { Track = t, Status = "Pending" });
BulkLyricsDialog.axaml:64-66: <ItemsControl ItemsSource="{Binding Rows}"> ... <ItemsPanelTemplate><StackPanel Spacing="4" /></ItemsPanelTemplate>
BulkLyricsViewModel.cs:44-45: foreach (var t in tracks) Rows.Add(new Row(t));
SendToFolderDialog.axaml:109-111: <ItemsControl ItemsSource="{Binding Rows}"> ... <StackPanel Spacing="4" />
ReplayGainScannerDialog.axaml:65-72 (the fix): <!-- Virtualized: Jobs holds one entry per selected track ... --> <VirtualizingStackPanel />
```

**Why it is a bug:** Each dialog shows one row per selected track (Ctrl+A in Songs feeds the whole library through TrackContextMenuBuilder's convert, fetch-lyrics and send-to-folder commands). Each also lays the rows out in a plain StackPanel: explicitly, or through the ItemsControl default (https://github.com/AvaloniaUI/Avalonia/blob/master/src/Avalonia.Controls/ItemsControl.cs, DefaultPanel = StackPanel). The inner ScrollViewer's MaxHeight does not limit realization, so every row is built when the dialog opens. The rows are heavier than the ReplayGain rows that were fixed. Bulk Lyrics and Send to Folder rows carry a templated PathIcon. MetadataFinder rows (MetadataFinderDialog.axaml:108, library-wide 'poorly tagged' candidates from MetadataHelper.cs:72-75) have a templated CheckBox and 11 TextBlocks. DuplicateFinder (DuplicateFinderDialog.axaml:111/120) nests an ItemsControl with a CheckBox per duplicate row for every group. The team measured a comparable row at 1.6-2.5 ms each (MiniPlayerWindow.axaml:2370-2378). At that rate, 5,000 selected tracks are several seconds of UI-thread work on open (estimate). AudioConverterViewModel.cs:115 and BulkLyricsViewModel.cs:63 also still do an O(n) Rows.FirstOrDefault per progress report, the lookup ReplayGain replaced with a dictionary.

**Impact:** Choosing Convert, Fetch Lyrics or Send to Folder on a large selection, or opening Find Metadata or Find Duplicates on a messy library, freezes the UI for seconds before the dialog becomes usable, and holds thousands of controls in memory.

**Proposed fix (small):** Give each list a <VirtualizingStackPanel/> ItemsPanel, as in ReplayGainScannerDialog. VirtualizingStackPanel has no Spacing, so move the 4px Spacing into the row Margin. For DuplicateFinder, virtualize the outer Groups list. Build the collections before binding, or with one ReplaceAll. Optionally key the progress lookups by Track in a Dictionary, as ReplayGain does.

**Verifier votes**

- **confirmed** (refute lens): AudioConverterDialog.axaml:165 is an ItemsControl with no ItemsPanel (default StackPanel), inside a Border MaxHeight=220 ScrollViewer. AudioConverterViewModel.cs:73-74 adds one JobRow per track, and :115 does Jobs.FirstOrDefault per progress report. The grep matches the siblings: BulkLyricsDialog.axaml:64-66 and SendToFolderDialog.axaml:109-111 use explicit StackPanel Spacing=4, while MetadataFinderDialog.axaml:108 and DuplicateFinderDialog.axaml:111/120 use the default panel. The rows are added at BulkLyricsViewModel.cs:45, MetadataFinderViewModel.cs:40, DuplicateFinderViewModel.cs:47/99 and SendToFolderViewModel.cs:76. MetadataHelper.cs:184-189, :95-100 and :105-109 open these dialogs with the full selection, and :72-74 feeds MetadataFinder every poorly tagged local track. ReplayGainScannerDialog.axaml:65-72 has the documented VirtualizingStackPanel fix for this exact Ctrl+A case. The MetadataFinder row (:110-137) matches the finding: a CheckBox and 11 TextBlocks. The multi-second figure is an estimate. Large selections are an uncommon path, so medium.

### U21 — Songs/Albums/Artists lists jump to the top on any library reload while a search or artist filter is active

- **Location:** `src/Noctis/Views/LibrarySongsView.axaml.cs:225` (also: `src/Noctis/Views/LibraryAlbumsView.axaml.cs:175-190`, `src/Noctis/Views/LibraryArtistsView.axaml.cs:112-127`, `src/Noctis/ViewModels/LibrarySongsViewModel.cs:89-95`, `src/Noctis/ViewModels/LibrarySongsViewModel.cs:517`, `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs:366-374`, `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs:80-85`, `src/Noctis/Views/LibrarySongsView.axaml.cs:315`, `src/Noctis.Core/Services/LibraryService.cs:1779-1785`, `src/Noctis/ViewModels/MetadataViewModel.cs:2645`)
- **Area / sweep:** UI performance / ui-lists · **Category:** ui-scroll-jump · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
LibrarySongsView.axaml.cs:225-237
    private void OnFilteredTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm?.HasActiveFilter != true) return;
        if (_pendingScrollRestore != null || (_vm != null && _vm.SavedScrollOffset > 0)) return;
        Dispatcher.UIThread.Post(() => { var sv = TrackList.FindDescendantOfType<ScrollViewer>(); if (sv != null) sv.Offset = new Vector(0, 0); }, DispatcherPriority.Background);
    }
LibrarySongsViewModel.cs:89-94: _libraryUpdatedHandler = (_, _) => { _isDirty = true; ... if (_isActive) Dispatcher.UIThread.Post(Refresh); };
LibrarySongsViewModel.cs:517: FilteredTracks.ReplaceAll(result);   // raises Reset on every reload
LibraryService.cs:1779-1785: NotifyMetadataChanged() => Task.Run(async () => { await RebuildIndexesAsync(); LibraryUpdated?.Invoke(...); });
MetadataViewModel.cs:2645: _library.NotifyMetadataChanged();   // every metadata Save
```

**Why it is a bug:** The handler is meant to scroll to the top when the user changes the search text, but it runs on every CollectionChanged of FilteredTracks. The VM refills FilteredTracks with ReplaceAll (one Reset) for every LibraryUpdated while the view is active: a metadata Save, the end of an audio-analysis pass (AudioAnalysisCoordinator.cs:208), watcher imports, artwork backfill, and during a scan, which the VM comment says fires every ~1.5 s. So a reload also jumps the list to the top. Example: the user searches "love", scrolls to row 40, right-clicks, opens Metadata and saves. Save calls NotifyMetadataChanged, then LibraryUpdated, Refresh, ReplaceAll, the handler, and Offset=0. The opposite also happens: SavedScrollOffset on the Songs VM is only written on detach (LibrarySongsView.axaml.cs:315) and never cleared. After the user leaves Songs once while scrolled, the guard is permanently true and a new search no longer scrolls to the top. LibraryAlbumsView.OnFilteredRowsChanged and LibraryArtistsView.OnArtistRowsChanged have the same code. In Albums, a column-count change from a window resize (UpdateGridMetrics -> RebuildFilteredRows -> ReplaceAll) also triggers it while filtered, which overrides the savedY restore that OnSizeChanged posts.

**Impact:** While filtered, the user's position in the result list jumps to the top after editing a result's metadata, after an analysis pass or import, or repeatedly (~every 1.5 s) during a library scan. After the user has navigated away once, the intended scroll-to-top on a new search stops working for the rest of the session.

**Proposed fix (small):** Scroll to the top only when the filter itself changed. For example, have the view remember the SearchText (and artist filter) it last reset for, and reset only when _vm.SearchText differs. Or let the VM raise a dedicated FilterChanged event from ApplyFilter/SetArtistFilter and not from Refresh(). Also clear SavedScrollOffset after the pending restore consumes it. Apply the same change to LibraryAlbumsView and LibraryArtistsView.

**Verifier votes**

- **confirmed** (refute lens): LibrarySongsView.axaml.cs:225-238: OnFilteredTracksChanged scrolls to 0 on any CollectionChanged when HasActiveFilter is set. The only guards are _pendingScrollRestore and SavedScrollOffset>0. LibrarySongsViewModel.cs:89-95 posts Refresh on every LibraryUpdated while the view is active. Refresh (123-134) calls ApplyFilterAndSort(refreshFromLibrary:true), which does FilteredTracks.ReplaceAll at line 517, and BulkObservableCollection.ReplaceAll raises Reset. MetadataViewModel.cs:2645 calls NotifyMetadataChanged, which raises LibraryUpdated (LibraryService.cs:1779-1785). SavedScrollOffset is written only on detach (View:315) and is never cleared in the Songs VM or view, so after one scrolled leave the guard stays true for the rest of the session. The Artists view (LibraryArtistsView.axaml.cs:112-127) has the same code. In Albums, RebuildFilteredRows runs on the ThreadPool (LibraryAlbumsViewModel.cs:513+), so its reset lands after the savedY restore that OnSizeChanged posts (View:206-216) and overrides it. One difference: Albums SetArtistFilter clears SavedScrollOffset (VM:452).

### U22 — Closing the lyrics side panel never un-registers it as a visible lyrics surface, so the per-frame word clock, the 100 ms sync timer and a 250 ms flow poll keep running for the rest of the session

- **Location:** `src/Noctis/Views/LyricsPanelView.axaml.cs:65` (also: `src/Noctis/Views/MainWindow.axaml.cs:540-551`, `src/Noctis/Views/MainWindow.axaml.cs:710-716`, `src/Noctis/ViewModels/LyricsViewModel.cs:600-604`, `src/Noctis/ViewModels/LyricsViewModel.cs:3169-3208`, `src/Noctis/Views/LyricsPanelView.axaml.cs:124-130`, `src/Noctis/Helpers/FlowingArtworkAnimator.cs:136-141`, `src/Noctis/Helpers/FlowingArtworkAnimator.cs:186-197`, `src/Noctis.Core/Services/LyricsTimeline.cs:263`)
- **Area / sweep:** UI performance / ui-anim-leaks · **Category:** timer-leak / hidden-view work · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Prior audit:** AUDIT.md L5 (related: the tick-backstop fix for L5 is bypassed because the surface count never drops)

**Evidence**

```
LyricsPanelView.axaml.cs:71-74 (attach):
    if (!_countedAsVisible && _vm != null)
    {   _vm.SetLyricsSurfaceVisible(true);
        _countedAsVisible = true; }
LyricsPanelView.axaml.cs:81/87-91 (only uncounted on DetachedFromVisualTree):
    if (_countedAsVisible) { _countedAsVisible = false; _vm?.SetLyricsSurfaceVisible(false); }
MainWindow.axaml.cs:545-550 (panel close only hides the wrapper):
    _lyricsPanelWrapper.Width = 0;
    DispatcherTimer.RunOnce(() => { ... _lyricsPanelWrapper.IsVisible = false; }, TimeSpan.FromMilliseconds(240));
MainWindow.axaml.cs:713-715: if (host is null || host.Content is not null) return; host.Content = new LyricsPanelView { DataContext = vm.Lyrics };
LyricsViewModel.cs:3180-3183: private bool WantsWordClock => _hasSyncedLyrics && IsAnyLyricsSurfaceVisible && _player.State == Models.PlaybackState.Playing && _lyricsSyncTimer.IsEnabled && ...
```

**Why it is a bug:** The panel view is created once (EnsureLyricsPanelLoaded) and stays in LyricsPanelHost for the app lifetime; closing it only sets the wrapper's IsVisible=false. IsVisible=false does not detach a control from the visual tree (the repo states this itself at AnimatedCoverImage.axaml.cs:48-50: "IsVisible=False anywhere up the tree does not detach a control"), so DetachedFromVisualTree never fires and SetLyricsSurfaceVisible(false) is never called. After the first open, _visibleLyricsSurfaces is stuck at >= 1. Consequences: (1) the Tick backstop at LyricsViewModel.cs:600-604 (added for AUDIT L5) never parks the 100 ms _lyricsSyncTimer; (2) WantsWordClock stays true during every word-synced line, so OnWordClockFrame (LyricsViewModel.cs:3188-3203) re-registers RequestAnimationFrame every frame and runs UpdateActiveLine -> LyricsTimeline.Update, which writes w.Progress per word per frame (LyricsTimeline.cs:263) into the hidden panel's bindings; (3) UpdateFlowAnimationState (LyricsPanelView.axaml.cs:128-129) keys _flow.Enabled on VisualRoot != null, so FlowingArtworkAnimator.OnFrame sees !IsEffectivelyVisible and starts its 250 ms DispatcherTimer poll (FlowingArtworkAnimator.cs:136-140, 186-197), which runs until the panel is reopened. The VM's own comment (LyricsViewModel.cs:3161-3165) describes exactly this situation (word-timed track playing on Home with the panel closed) as the CPU/battery burn the counter was introduced to stop.

**Impact:** After a user opens and closes the lyrics side panel once, every word-synced line played for the rest of the session drives a 60 Hz UI-thread frame loop plus a 10 Hz timer (and a 4 Hz poll when the flowing-artwork background is on) with no lyrics on screen: on Home, in the tray, or behind a non-lyrics mini-player form. The result is wasted CPU and battery, and per-frame UI-thread work that competes with scrolling and input.

**Proposed fix (small):** Tie the panel's surface registration to its shown state, not to attachment. Add LyricsPanelView.SetShown(bool shown), which does the SetLyricsSurfaceVisible(+/-) bookkeeping (guarded by _countedAsVisible) and re-evaluates _flow.Enabled. In MainWindow's IsLyricsPanelOpen handler, call SetShown(true) when the panel opens and SetShown(false) in the RunOnce that sets _lyricsPanelWrapper.IsVisible = false. Keep the attach/detach hooks for window teardown. Also require _lyricsPanelWrapper.IsVisible, not just VisualRoot != null, in UpdateFlowAnimationState.

**Verifier votes**

- **confirmed** (refute lens): LyricsPanelView.axaml.cs:65-92 calls SetLyricsSurfaceVisible(true) on AttachedToVisualTree and calls (false) only on DetachedFromVisualTree. MainWindow.axaml.cs:540-551 closes the panel by setting Width=0 and then IsVisible=false. The panel is created once and never removed from LyricsPanelHost (EnsureLyricsPanelLoaded, 710-716). The repo itself notes that IsVisible=false does not detach a control (AnimatedCoverImage.axaml.cs:48, AnimatedCoverLeases.cs:66). So the count stays at 1 or more, and neither the Tick backstop (LyricsViewModel.cs:600-604) nor WantsWordClock (3180-3186) ever parks, and OnWordClockFrame re-queues every frame. UpdateFlowAnimationState (LyricsPanelView:124-130) keys _flow.Enabled on VisualRoot!=null, so FlowingArtworkAnimator.OnFrame falls into the 250 ms StartVisibilityPoll (FlowingArtworkAnimator.cs:136-141, 186-197). No other caller decrements the count for the panel.

### U24 — Closing the mini player in the Pill design while music plays leaves the cover-spin frame loop running forever and keeps the closed MiniPlayerWindow in memory (one more for every close)

- **Location:** `src/Noctis/Views/MiniPlayerWindow.axaml.cs:1137` (also: `src/Noctis/Helpers/CoverSpinner.cs:48-74`, `src/Noctis/Views/MiniPlayerWindow.axaml.cs:470-478`, `src/Noctis/Views/MiniPlayerWindow.axaml.cs:1111-1115`, `src/Noctis/Views/MainWindow.axaml.cs:119-137`, `src/Noctis.UI/Controls/SpinClock.cs:50`)
- **Area / sweep:** UI performance / ui-anim-leaks · **Category:** frame-loop leak / window leak · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
MiniPlayerWindow.axaml.cs:201   _pillSpinner = new CoverSpinner(this, PillCoverSpin);
MiniPlayerWindow.axaml.cs:1114  _pillSpinner.IsSpinning = Vm is { IsPillForm: true, Player.IsPlaying: true };
MiniPlayerWindow.axaml.cs:1139-1143 (OnClosed: every other loop is stopped, the spinner is not)
    _drawerHideTimer?.Stop(); _lyricsScrollTimer?.Stop(); _lyricsFontTimer?.Stop();
    _flow?.Dispose(); _topmostKeeper?.Dispose();
CoverSpinner.cs:68-73
    if (_clock.IsSettled) return;
    if (TopLevel.GetTopLevel(_host) is { } topLevel)
    {   _frameQueued = true;
        topLevel.RequestAnimationFrame(OnFrame); }
```

**Why it is a bug:** _host is the MiniPlayerWindow itself. TopLevel.GetTopLevel walks up to the first TopLevel, so it keeps returning the window after Close (Avalonia source: https://github.com/AvaloniaUI/Avalonia/blob/master/src/Avalonia.Controls/TopLevel.cs, GetTopLevel = `if (visual is TopLevel tl) return tl;`). SpinClock.IsSettled is `!IsRunning && Velocity == 0` (SpinClock.cs), which never becomes true while IsSpinning stays true. IsSpinning is only changed by UpdatePillSpin, which OnPlayerPropertyChanged (line 472-473) calls, and OnClosed unhooks that handler (line 1155) without stopping the spinner. TopLevel.RequestAnimationFrame is not per-window: it calls MediaContext.Instance.RequestAnimationFrame, which queues on the global MediaContext clock and schedules a render each time (https://github.com/AvaloniaUI/Avalonia/blob/master/src/Avalonia.Base/Media/MediaContext.Clock.cs: `_parent.ScheduleRender(false); _queuedAnimationFrames.Enqueue(action);`). Once the main window is shown again (OnMiniPlayerClosed -> Show()), the callback therefore fires every frame for the rest of the session. The queued delegate roots CoverSpinner -> _host, which is the closed window and its whole visual tree. MainWindow.ToggleMiniPlayer creates a new MiniPlayerWindow on every open (MainWindow.axaml.cs:130), so each Pill-mode close during playback adds another permanent loop and another retained window. EqVisualizer.axaml.cs:166-168 records that the same bug class (a closed mini player rooted by a timer while music plays) was already fixed there.

**Impact:** For Pill-design users, every time the mini player is closed while music is playing, the app keeps running a per-frame UI-thread callback that forces a render pass each frame, even when the app is otherwise idle. The closed window stays in memory with its artwork and bindings. CPU/battery use and memory grow with every open/close cycle until the app restarts.

**Proposed fix (small):** Give CoverSpinner a Stop() that sets a _stopped flag and _clock.IsRunning = false, and make OnFrame/QueueFrame return early when stopped. Call _pillSpinner?.Stop() in MiniPlayerWindow.OnClosed. As a second guard, have OnFrame stop when the host is no longer shown (`if (!_host.IsVisible) return;`; a closed window reports IsVisible=false).

**Verifier votes**

- **confirmed** (refute lens): MiniPlayerWindow.axaml.cs:201 creates CoverSpinner(this, PillCoverSpin). OnClosed (1137-1160) stops the other timers and unhooks Player.PropertyChanged, which is the only path to UpdatePillSpin, but never stops _pillSpinner. CoverSpinner.cs:57-74: OnFrame re-queues whenever !_clock.IsSettled, and SpinClock.cs:51 defines IsSettled as !IsRunning && Velocity==0, which is never true while IsRunning stays true. I checked Avalonia 12.1.2 IL: TopLevel.GetTopLevel only walks VisualParent (so it returns the closed window itself), and TopLevel.RequestAnimationFrame calls MediaContext.Instance.RequestAnimationFrame, which leads to MediaContextClock.RequestAnimationFrame (ScheduleRender plus enqueue on the global queue). The loop therefore keeps firing and roots the closed window. MainWindow.axaml.cs:130 creates a new MiniPlayerWindow on every open, so each Pill-mode close during playback adds another loop and another retained window.

### U26 — Organize Files dialog renders one non-virtualized row per library track, added one at a time on the UI thread

- **Location:** `src/Noctis/Views/OrganizeFilesDialog.axaml:150` (also: `src/Noctis/ViewModels/OrganizeFilesViewModel.cs:54-83`, `src/Noctis/ViewModels/MetadataHelper.cs:47-52`, `src/Noctis/Views/ReplayGainScannerDialog.axaml:64-73`)
- **Area / sweep:** UI performance / ui-lists · **Category:** ui-virtualization · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
OrganizeFilesDialog.axaml:148-151
                <ScrollViewer HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto" Padding="24,2,24,8">
                    <ItemsControl ItemsSource="{Binding Rows}">
                        <ItemsControl.ItemTemplate>   (no ItemsPanel -> default StackPanel)
MetadataHelper.cs:51-52: var tracks = library.Tracks.Where(t => t.SourceType == SourceType.Local).ToList();
        var vm = new OrganizeFilesViewModel(tracks, service, settings);
OrganizeFilesViewModel.cs:75-77
        Rows.Clear();
        foreach (var m in plan)
            Rows.Add(new OrganizeRow(m, root));
```

**Why it is a bug:** The tool always opens over the whole local library, and the plan has one entry per track, including 'already organized' skips. PreviewAsync then adds every row with an individual Rows.Add on the UI thread after the dialog is already bound. The ItemsControl has no ItemsPanel, so Avalonia uses its default StackPanel (ItemsControl.DefaultPanel = new StackPanel(), https://github.com/AvaloniaUI/Avalonia/blob/master/src/Avalonia.Controls/ItemsControl.cs). A StackPanel creates a container for every item as it is added. Each row is 2 Borders, a DockPanel, a StackPanel and 3 TextBlocks. With a 20,000-track library that is 20,000 CollectionChanged events and about 140k controls built in one continuation. 'Update Preview' clears and rebuilds all of them. The team already fixed exactly this pattern in ReplayGainScannerDialog.axaml:65-73 ('built thousands of Grid + 3 TextBlock trees on the UI thread').

**Impact:** Opening Organize Files, or pressing Update Preview, on a large library freezes the app for seconds and allocates a large visual tree that stays alive while the dialog is open.

**Proposed fix (small):** Add <ItemsControl.ItemsPanel><ItemsPanelTemplate><VirtualizingStackPanel/></ItemsPanelTemplate></ItemsControl.ItemsPanel>, as in the ReplayGain dialog. Make Rows a BulkObservableCollection and fill it with one ReplaceAll(plan.Select(...)) so there is a single Reset. Optionally list only rows whose Action != Skip.

**Verifier votes**

- **confirmed** (refute lens): OrganizeFilesDialog.axaml:148-170 has an ItemsControl with no ItemsPanel (so the default StackPanel) inside a ScrollViewer. Each row is 2 Borders, a DockPanel, a StackPanel and 3 TextBlocks. MetadataHelper.cs:51 passes every local track. FileOrganizePlanner.cs:54-68 adds one OrganizeMove per track, including Skip rows. OrganizeFilesViewModel.cs:29 declares a plain ObservableCollection, and lines 75-77 do Clear() and then one Add per plan entry on the UI thread. The same thing happens from Apply and Undo (102, 117) and from Update Preview. ReplayGainScannerDialog.axaml:64-73 has the same fix already documented (a VirtualizingStackPanel). The dialog is an uncommon path, so medium is right.

### X02 — Server login throttle is keyed on the TCP peer address and checked before authentication: behind the documented reverse proxy, 8 bad logins lock every user and API key out for 15 minutes

- **Location:** `src/Noctis.Core.Server/Services/Server/NoctisServer.cs:122` (also: `src/Noctis.Core.Server/Services/Server/LoginThrottle.cs:32`, `src/Noctis.Core.Server/Services/Server/LoginThrottle.cs:45`, `src/Noctis.Server/Program.cs:156`, `Dockerfile:7`)
- **Area / sweep:** Security / sec-net · **Category:** security-dos · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
NoctisServer.cs:122-129
                var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (_throttle.IsLocked(client, out var retryAfter))
                {
                    ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                    await WriteAsync(ctx, SubsonicResponse.Error(SubsonicResponse.ErrWrongCredentials,
                        "Too many failed login attempts. Try again later.", format, _serverVersion), 429).ConfigureAwait(false);
                    return;
                }
LoginThrottle.cs:13-15  MaxFailures = 8; Window = 10 min; Lockout = 15 min
```

**Why it is a bug:** The lockout key is the connection's RemoteIpAddress. No ForwardedHeaders middleware exists anywhere in src (grep for UseForwardedHeaders/KnownProxies finds nothing). The headless server's documented deployment is plain HTTP behind a TLS reverse proxy: Dockerfile:7-9 ("terminate TLS in your reverse proxy", NOCTIS_TLS=0 by default), docs/SELF-HOSTING.md:25-26 and Program.cs:156. In that setup every request's RemoteIpAddress is the proxy's address (https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer). IsLocked is also checked before Authenticate, so a locked key blocks even a valid apiKey. Scenario: anyone who can reach the public URL sends 8 requests with a wrong password to /rest/getLicense. The proxy's address locks for 15 minutes and every household device gets 429s. Repeating 8 requests every 15 minutes keeps the server unusable indefinitely. One phone with a stale password does the same by accident.

**Impact:** Anyone who can reach a self-hosted server can lock out every client and account cheaply and indefinitely; a single misconfigured device can do it by accident.

**Proposed fix (small):** Key failures on (client, username) instead of the address alone. Never refuse a request that authenticates successfully, meaning check the lockout only for the credential being tried. Add opt-in ForwardedHeaders with configured KnownProxies (for example, a NOCTIS_TRUSTED_PROXIES env var) so RemoteIpAddress is the real client when behind a proxy.

**Verifier votes**

- **confirmed** (refute lens): NoctisServer.cs:122-129 keys the throttle on ctx.Connection.RemoteIpAddress. It checks IsLocked before Authenticate (line 131), so while the address is locked, valid credentials and apiKeys get a 429 too. A grep for ForwardedHeaders/KnownProxies/X-Forwarded finds nothing anywhere in the repo. The documented deployment is plain HTTP behind a TLS reverse proxy: Dockerfile:8-9 (NOCTIS_TLS=0 at :31), docs/SELF-HOSTING.md:25-26, Program.cs:156. In that setup every client shares the proxy's address. LoginThrottle.cs:13-15/45-57 locks for 15 minutes after 8 failures in 10 minutes. A success clears the counter (:61), but once the lock is set nobody can authenticate. It gets worse: NoctisServer.cs:134-137 also counts ErrTokenAuthNotSupported, which is the default auth mode of many Subsonic clients, as a failure. So one household device using token auth behind the proxy locks everyone out within seconds. This only applies to proxied deployments, so medium.

### X03 — Subsonic 'download' fails for any track whose file name is not ASCII: the raw name goes into the Content-Disposition header

- **Location:** `src/Noctis.Core.Server/Services/Server/NoctisServer.cs:222`
- **Area / sweep:** Security / sec-files · **Category:** server-correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
NoctisServer.cs:221-223
    if (method.Equals("download", StringComparison.OrdinalIgnoreCase))
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{Path.GetFileName(track.FilePath).Replace("\"", "")}\"";
    await Results.File(track.FilePath, contentType, enableRangeProcessing: true).ExecuteAsync(ctx).ConfigureAwait(false);
NoctisServer.cs:66-74 ConfigureKestrel sets no ResponseHeaderEncodingSelector
```

**Why it is a bug:** By default Kestrel only accepts ASCII in response header values. Setting a value containing é or CJK characters throws InvalidOperationException 'Invalid non-ASCII or control character in header' (https://github.com/dotnet/aspnetcore/issues/26334).

NoctisServer uses Kestrel (WebApplication.CreateSlimBuilder, line 64) and sets no ResponseHeaderEncodingSelector. The exception is caught at line 161 and answered with a Subsonic 'Server error'.

Scenario: a Subsonic client (DSub, Symfonium offline cache) calls /rest/download?id=... for 'Beyoncé - Halo.flac' or a Japanese-named file. Nothing is streamed and the client shows an error. On Linux, file names containing control characters fail the same way.

**Impact:** Downloading or offline caching through the self-hosted server fails for every non-ASCII file name, which covers a large share of non-English libraries.

**Proposed fix (trivial):** Pass the name through Results.File(track.FilePath, contentType, fileDownloadName: Path.GetFileName(track.FilePath), enableRangeProcessing: true). ASP.NET Core then writes an RFC 5987 `filename*=UTF-8''...` header with an ASCII fallback. Alternatively build the header with ContentDispositionHeaderValue { FileNameStar = name }.

**Verifier votes**

- **confirmed** (refute lens): NoctisServer.cs:221-223 puts the raw Path.GetFileName into ctx.Response.Headers.ContentDisposition. Kestrel is configured at :64-74 with no ResponseHeaderEncodingSelector, and a grep of the repo finds none. With the default selector, Kestrel validates response header values as ASCII when they are set and throws InvalidOperationException ("Invalid non-ASCII or control character in header"). The catch at :161-165 then turns that into a Subsonic 'Server error', and no file is sent. This affects only the 'download' method; 'stream' sets no such header. NoctisServerTests.cs has no download test. Medium is right.

### X05 — Words per line and [bg:] lines are uncapped, and MergeSyllables concatenates in O(n²); one crafted LRC line freezes the UI

- **Location:** `src/Noctis.Core/Services/EnhancedLrcParser.cs:187` (also: `src/Noctis/ViewModels/LyricsViewModel.cs:2763`, `src/Noctis/ViewModels/LyricsViewModel.cs:2864`, `src/Noctis.Core/Services/EnhancedLrcParser.cs:276`, `src/Noctis/ViewModels/LyricsViewModel.cs:1816`, `src/Noctis/Views/LyricsView.axaml:1201`)
- **Area / sweep:** Security / sec-files · **Category:** security-dos · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Prior audit:** AUDIT_2026-07-24.md 'The lyric list is not virtualized and the parsers put no cap on line count' (the line cap does not bound words)
- **Needs runtime check:** yes

**Evidence**

```
EnhancedLrcParser.cs:182-192
    if (prev.Text.Length > 0 && cur.Text.Length > 0
        && IsJoinable(prev.Text[^1]) && IsJoinable(cur.Text[0]))
    {
        var segments = parts[^1] ??= [new WordSyllable(prev.Start, prev.End, prev.Text.Length)];
        segments.Add(new WordSyllable(cur.Start, cur.End, cur.Text.Length));
        merged[^1] = new WordTiming
        {
            Text = prev.Text + cur.Text,
LyricsViewModel.cs:2763-2768  if (trimmed.StartsWith(BgLinePrefix, ...)) { ... AttachBackgroundLine(lastMain, trimmed, offsetMs); continue; }  // never counted toward MaxLyricLines
EnhancedLrcParser.cs:282-286  var merged = new List<WordTiming>(target.BackgroundWords!); ... merged.AddRange(words);
```

**Why it is a bug:** MaxLyricLines limits the number of lines, not the number of words in a line. Each word is roughly 7 realized controls inside a WrapPanel (LyricsView.axaml:1201).

Take a synced-lyrics body of one line `[00:01.00]` followed by 300,000 `<00:01.00>a` tags (about 3.3 MB, under the 4 MB HTTP cap). EnhancedLrcParser.ParseLine gives 300k one-letter words. All of them are IsJoinable letters, so MergeSyllables rebuilds the growing string on every step: about 4.5e10 character copies and roughly 300k large-object-heap strings.

For online results, ParseLrcContent runs on the UI thread inside DisplayOnlineLyrics (LyricsViewModel.cs:1816). It is reached for NetEase, plugin providers, or LRCLIB records whose Lyricsfile is empty. A local .lrc/.elrc sidecar hits the same code in the background probe, and the resulting word cells are then realized on the UI thread.

Separately, `[bg: ...]` lines `continue` before the line counter increases, so 200k of them all attach to one main line. Each AppendBackground call copies the whole accumulated list (O(n²)) and builds a background row of 200k word cells.

**Impact:** Multi-minute UI freeze or out-of-memory when lyrics are fetched or a crafted sidecar is loaded. This is a DoS through community-sourced lyrics or files shipped next to downloaded music.

**Proposed fix (small):** Cap words per line (for example 512) in EnhancedLrcParser.ParseLine and in AttachBackgroundLine/AppendBackground, and count [bg:] lines toward MaxLyricLines. In MergeSyllables, gather the syllables of a merged word into a StringBuilder and create the WordTiming once, instead of repeating `prev.Text + cur.Text`.

**Verifier votes**

- **confirmed** (refute lens): The code matches the finding. In EnhancedLrcParser.cs:178-193, MergeSyllables rebuilds `prev.Text + cur.Text` on every joinable step, so a run of n joinable one-letter words costs O(n²) character copies. ParseLine (:77-121) puts no cap on the tag or word count. In LyricsViewModel.cs, ParseLrcContent checks MaxLyricLines only at :2745 and :2804, while the [bg:] branch (:2763-2769) hits `continue` before any lines.Add. AttachBackgroundLine (:2864-2877) then calls AppendBackground, which copies the whole existing list on each call (EnhancedLrcParser.cs:282-286), so that path is also O(n²). The online path runs on the UI thread: SearchLyrics resumes after awaits and calls DisplayOnlineLyrics (:1257), which calls ParseLrcContent at :1816. LrcLibService/NetEaseService read the body through HttpSafety.ReadStringBoundedAsync with a 4 MB cap, so a payload of about 3.3 MB gets through. One correction: 300k joinable one-letter words merge into ONE WordTiming, so that case does not realize 300k word cells. The freeze there comes from the quadratic merge. The per-word WrapPanel cost (LyricsView.axaml:1202) applies to non-joinable words and to the [bg:] seams, which are space-separated. The attack needs crafted content, so medium stands.

### X06 — Log redaction misses Subsonic stream credentials (t/s, or p=enc:<hex password>) in VLC lines that have no scheme, so Developer Mode's VLC bridge writes them to the session log and crash.log

- **Location:** `src/Noctis.Core/Services/LogRedaction.cs:30` (also: `src/Noctis/Services/VlcAudioPlayer.cs:5164`, `src/Noctis/Services/VlcAudioPlayer.cs:5201`, `src/Noctis/Services/MediaServer/SubsonicClient.cs:258`, `src/Noctis.Core/Services/DebugLog.cs:55`, `tests/Noctis.Tests/LogRedactionTests.cs:16`)
- **Area / sweep:** Security / sec-net · **Category:** security-secret-leak · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
LogRedaction.cs:17  [GeneratedRegex("""(https?://[^\s"'<>]+?)\?[^\s"'<>]*""", RegexOptions.IgnoreCase)]
:20  [GeneratedRegex("""\b(api_key|apikey|access_token|token)=("[^"]*"|[^&\s"'<>]+)""", ...)]
:30-31  if (message.Contains("://", StringComparison.Ordinal))
            message = UrlQueryRegex().Replace(message, "$1?[redacted]");
:33-36  if (message.Contains("token", ...) || message.Contains("api_key", ...) || message.Contains("apikey", ...))
            message = BareSecretRegex().Replace(message, "$1=[redacted]");
VlcAudioPlayer.cs:5175  _devBridgeRing[_devBridgeRingNext] = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
VlcAudioPlayer.cs:5205  DebugLog.Write("VLC", "  " + line);
```

**Why it is a bug:** Subsonic stream URLs put the credential in the query: u, t = md5(password+salt) and s = salt, or p=enc:<hex of the password> in Password mode (SubsonicClient.cs:256-268). Scrub removes only a query that follows "scheme://" and only keys named api_key, apikey, access_token or token. VLC logs these URLs without the scheme in two places. First, the core line in src/input/demux.c (3.0.x:232), `creating demux: access='%s' demux='%s' location='%s' file='%s'`, where location is the part after "://". Second, the https access, through vlc_http_dbg (modules/access/http/h1conn.c:157), logs `outgoing request:\nGET /rest/stream.view?u=..&t=..&s=.. HTTP/1.1` on every open and every Range (seek) request. vlc_http_dbg sends it at VLC_MSG_DBG through vlc_vaLog (connmgr.c:46-53), so the libvlc log callback receives it. In Developer Mode (SettingsViewModel.cs:6684), OnVlcLogForBridge keeps the last 40 lines of every level. On any VLC Error, for example a stream open that fails with 404/500 or `ES_OUT_SET_(GROUP_)PCR is called too late` after a seek on a network stream, it dumps them through DebugLog.Write. Neither line contains "://" or "token"/"api_key", so Scrub returns them unchanged. The session log is mirrored to crash.log on disk and is exactly the text "Copy Logs" puts on the clipboard. LogRedactionTests.cs:16-26 test only URLs that include a scheme. Jellyfin api_key values are caught; Subsonic credentials are not.

**Impact:** A Navidrome/Subsonic credential ends up in bug-report logs that users paste into Discord or GitHub. A t+s pair can be replayed, because the server just recomputes md5(password+s). A p=enc:<hex> value is the plaintext password.

**Proposed fix (trivial):** Redact credential parameters wherever they appear, not only after "://". For example, add a regex `([?&](?:u|t|s|p|apiKey|api_key|token|access_token)=)[^&\s"'<>]+` → `$1[redacted]` that runs unconditionally, or redact the whole query after `/rest/` or `/Audio/` paths. Add tests with the two VLC line shapes quoted above. The NOCTIS_VLC_LOG diag file uses the same Scrub and benefits from the same fix.

**Verifier votes**

- **confirmed** (refute lens): LogRedaction.cs:30-36 runs UrlQueryRegex only when the text contains "://", and that regex requires an `https?://` prefix. BareSecretRegex (:20) matches only api_key/apikey/access_token/token. Subsonic's credential keys are u, t, s and p (SubsonicClient.cs:256-268, where p=enc:<hex of the plaintext password>), so neither regex catches them. VlcAudioPlayer.cs:2791 passes the stream URL straight to LibVLC with FromType.FromLocation; there is no proxy. OnVlcLogForBridge (:5164-5207) writes every level into a 40-line ring (:5175, DevBridgeRingSize=40 at :262) before the Warning/Error filter, and on an Error it dumps the ring through DebugLog.Write (:5205). DebugLog.Write (DebugLog.cs:55) applies only LogRedaction.Scrub, then mirrors to the sink. LibVLC 3.x debug lines such as `http debug: outgoing request:\nGET /rest/stream.view?u=..&t=..&s=..` and `creating demux: ... location='host/rest/...'` carry the query without a scheme, so they pass through unredacted. Of the two lines, only the demux one also holds the host; the outgoing-request line shows just the path and query. LogRedactionTests.cs:16-26 cover only URLs that include a scheme. The leak needs Developer Mode (SettingsViewModel.cs:6684), so medium.

### X09 — Lyricsfile (YAML) and TTML parsers skip the 3000-line MaxLyricLines cap, so a crafted sidecar or LRCLIB record freezes the UI

- **Location:** `src/Noctis/Services/LyricsfileParser.cs:54` (also: `src/Noctis.Core/Services/TtmlParser.cs:78`, `src/Noctis/ViewModels/LyricsViewModel.cs:2296`, `src/Noctis/ViewModels/LyricsViewModel.cs:2309`, `src/Noctis/ViewModels/LyricsViewModel.cs:2354`, `src/Noctis/ViewModels/LyricsViewModel.cs:1806`, `src/Noctis/ViewModels/LyricsViewModel.cs:2727`, `src/Noctis/Views/LyricsView.axaml:1131`, `HANDOFF_AUDIT_FIXES.md:63`)
- **Area / sweep:** Security / sec-files · **Category:** security-dos · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: high)
- **Prior audit:** AUDIT_2026-07-24.md 'The lyric list is not virtualized and the parsers put no cap on line count' (fixed only for LRC/plain)
- **Needs runtime check:** yes

**Evidence**

```
LyricsfileParser.cs:52-56,117
    if (dto.Lines != null)
    {
        foreach (var raw in dto.Lines)
        {
            if (raw == null) continue;
            ...            lines.Add(line);
TtmlParser.cs:78-85  foreach (var p in root.Descendants().Where(e => LocalNameIs(e, "p") && !IsInHead(e))) { ... lines.Add(line); }
LyricsViewModel.cs:2745 (LRC path only)  if (lines.Count >= MaxLyricLines) break;
LyricsViewModel.cs:1804-1806 (UI thread)  if (result.HasLyricsfile) { var (lines, plain) = LyricsfileParser.Parse(result.Lyricsfile);
```

**Why it is a bug:** MaxLyricLines (LyricsViewModel.cs:2719-2727) exists because the lyrics list is not virtualized. The code comment says every line is realized, a word-timed line costs about 7 controls per word, and a hostile sidecar 'would build tens of thousands of controls in a single UI-thread pass'. LyricsItemsControl (LyricsView.axaml:1131) is a plain ItemsControl, which uses the non-virtualizing StackPanel by default. The cap is applied only in ParseLrcContent and SplitPlainLyrics. LyricsfileParser.Parse and TtmlParser.Parse return every line and word they find.

YamlDotNet makes this worse: its AliasValueDeserializer returns the same object for each `*alias` and has no alias limit (https://raw.githubusercontent.com/aaubry/YamlDotNet/master/YamlDotNet/Serialization/ValueDeserializers/AliasValueDeserializer.cs). A 5-byte `- *l` therefore repeats a whole line, and the Parse loop expands each repeat into a new LyricLine. That gives about 800k lines inside the 4 MB HttpSafety cap.

Online results reach this code: DisplayOnlineLyrics runs on the UI thread (it fills ObservableCollections) and parses `result.Lyricsfile` first. According to https://lrclib.net/docs (as summarized by search, because the page is rendered with JS), LRCLIB records are community-published, carry a lyricsfile field and store it as-is. The YAML is then cached as {id}.lyricsfile (LyricsViewModel.cs:1867) and parsed again on every later play (LyricsViewModel.cs:2351-2356).

Concrete scenario: an album download ships `Song.lyricsfile` with 200,000 lines of `- {text: la, start_ms: 1000}`, or `Song.ttml` with 200,000 `<p begin="1s">a</p>`. ProbeLocalLyricSources returns 200k lines, and ApplyLocalLyricsResult -> FillLyricCollections -> LyricLines.ReplaceAll realizes all of them on the UI thread.

**Impact:** Noctis hangs for a long time or runs out of memory whenever that track becomes current (sidecar or cache), or when the user searches lyrics online for it. The hang repeats on every play because the result is cached. Prior fix notes (HANDOFF_AUDIT_FIXES.md:63) claim the cap 'closes the unbounded case', but it does not for these two formats.

**Proposed fix (small):** Move MaxLyricLines to a shared constant and enforce it inside LyricsfileParser.Parse and TtmlParser.Parse (stop adding lines at the cap). Also cap words per line (for example 512) before MergeSyllables. Optionally reject YAML that contains alias events (pre-scan with YamlDotNet.Core.Parser for AnchorAlias), and run LyricsfileParser.Parse inside Task.Run in DisplayOnlineLyrics.

**Verifier votes**

- **confirmed** (refute lens): The code matches the finding. LyricsfileParser.cs:52-118 adds every dto.Lines entry with no count check. TtmlParser.cs:76-86 adds every body <p> the same way. The only cap is MaxLyricLines=3000 (LyricsViewModel.cs:2727), and it is enforced only in SplitPlainLyrics (:2733) and ParseLrcContent (:2745, :2804). The results reach the list uncapped: ProbeLocalLyricSources (:2296, :2309, :2354) -> ApplyLocalLyricsResult -> FillLyricCollections/ReplaceAll, and DisplayOnlineLyrics (:1806 -> :1830), which runs synchronously from the search continuation (:1257). The outer LyricsItemsControl (LyricsView.axaml:1131) sets no ItemsPanel and sits inside a StackPanel in a ScrollViewer, so it does not virtualize. HANDOFF_AUDIT_FIXES.md:63 does claim the cap 'closes the unbounded case'. The LRCLIB response is bounded only by HttpSafety (LrcLibService.cs:51/95). The .lyricsfile is cached (:1867) and re-parsed on every play (:2351). I lowered severity to medium because it needs a hostile sidecar or a hostile LRCLIB record, an uncommon path. It is not a frequent malfunction.

### X11 — A content pack's language string can carry a huge numeric format specifier ('{0:D999999999}') that passes validation and triggers a multi-GB allocation at startup

- **Location:** `src/Noctis/Services/Plugins/ContentPack.cs:408` (also: `src/Noctis.UI/Localization/Loc.cs:109`, `src/Noctis/Services/Plugins/ContentPack.cs:162`, `src/Noctis/Services/Plugins/PluginHost.cs:718`, `src/Noctis/Services/Plugins/PluginHost.cs:494`)
- **Area / sweep:** Security / sec-files · **Category:** security-dos · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
ContentPack.cs:395-411
    internal static bool PlaceholdersFit(string english, string value)
    {
        ...
        if (!Formats(english, args)) return true;
        return Formats(value, args);
    }
    private static bool Formats(string text, int args)
    {
        try { _ = string.Format(CultureInfo.InvariantCulture, text, Enumerable.Repeat((object)"x", args).ToArray()); return true; }
        catch (FormatException) { return false; }
    }
Loc.cs:109-110  public static string T(string key, params object[] args) => string.Format(Instance._culture, Instance.Get(key), args);
```

**Why it is a bug:** Validation formats the pack string with string arguments. String does not implement IFormattable, so any format component such as ':D999999999' is ignored and the check passes. At runtime Loc.T formats the same text with ints.

Since .NET 7 the maximum numeric precision is 999,999,999 (https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/7.0/max-precision-numeric-format-strings), so `2.ToString("D999999999")` builds a string of about 1e9 characters (about 2 GB).

Scenario: a content pack declares culture "en" (allowed by the CultureName regex) with `"Plugins.Content.Themes": "{0:D999999999} themes"` (the English text is "{0} themes", Strings.resx:1556) and lists 2 themes. Content packs need no approval, ignore restricted mode and are on by default (PluginHost.cs:715). At startup, PreloadContent (SettingsViewModel.cs:2166) installs the overlay. Every en-* UI culture resolves through it (Loc.cs:119-121). LoadAll -> Activate -> `plugin.Extensions = content.Summary` (PluginHost.cs:718) -> Loc.T("Plugins.Content.Themes", 2) runs on the UI thread.

**Impact:** On every launch after the pack is installed, the UI thread attempts a 2 GB or larger allocation. The result is an OutOfMemoryException (swallowed by the dispatcher handler, so plugin loading is aborted) or a hang or memory exhaustion. It persists until the user deletes the pack folder by hand. Content packs are advertised as safe data ('nothing in a content pack is ever run').

**Proposed fix (small):** In PlaceholdersFit, parse the placeholders of both strings (`\{(\d+)(,[^:}]*)?(:[^}]*)?\}`) and reject a pack value if any placeholder has an alignment or format component that the English string does not contain verbatim. Alternatively validate with typed sample arguments and reject precision or alignment above a small bound (for example 64).

**Verifier votes**

- **confirmed** (refute lens): PlaceholdersFit/Formats (ContentPack.cs:395-412) validate the pack text with string arguments ("x"). String is neither IFormattable nor ISpanFormattable, so the ':D999999999' format component is ignored and the check passes. The Placeholder regex (:182) counts arguments only from the English text. At runtime Loc.T (Loc.cs:109-110) formats with a boxed int. Int32 TryFormat into the builder's remaining span fails, so string.Format falls back to ToString("D999999999"), which allocates a string of about 1e9 characters, and then grows the builder to hold it. Culture 'en' is accepted (:365-369). The overlay is installed before LoadAll by PreloadContent (PluginHost.cs:494-520, SettingsViewModel.cs:2166). Activate then evaluates content.Summary (PluginHost.cs:718), and Summary calls Loc.T("Plugins.Content.Themes", Themes.Count) (ContentPack.cs:162; English '{0} themes', Strings.resx:1556). A malicious pack must be installed first, so medium is fair.

### X12 — After 'Reset all settings' (or a settings.json that cannot be recovered), the next launch turns on community plugins and approves and starts every installed code plugin, including ones the user had switched off or never approved

- **Location:** `src/Noctis/Services/Plugins/PluginHost.cs:695` (also: `src/Noctis/ViewModels/SettingsViewModel.cs:6111`, `src/Noctis/ViewModels/SettingsViewModel.cs:6125`, `src/Noctis/ViewModels/SettingsViewModel.cs:163`, `src/Noctis.Core/Models/AppSettings.cs:50`, `src/Noctis.Core/Services/PersistenceService.cs:131`, `src/Noctis/Services/Plugins/PluginHost.cs:715`, `src/Noctis/Services/Plugins/PluginHost.cs:747`)
- **Area / sweep:** Security / sec-data · **Category:** plugin-trust · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
PluginHost.cs:695-704 (MigrateSettings):
'''
if (settings.CommunityPluginsEnabled is null)
{
    // Plugins already installed ran before this switch existed: keep them running and
    // treat what they declare today as approved. Everyone else starts restricted.
    var hadPlugins = found.Any(p => p.AssemblyPath is not null || p.InProcessFactory is not null);
    settings.CommunityPluginsEnabled = hadPlugins;
    if (hadPlugins)
        foreach (var p in found.Where(p => !IsDisabled(p)))
            Grant(settings, p);
'''
SettingsViewModel.cs:6110-6114 and 6125 (ConfirmResetLibrary):
'''
// Reset settings to defaults and save
var defaultSettings = new AppSettings();
...
    await _persistence.SaveSettingsAsync(defaultSettings);
...
_settings = defaultSettings;
'''
AppSettings.cs:50: `public bool? CommunityPluginsEnabled { get; set; }` (defaults to null; DisabledPlugins and PluginPermissionGrants default to empty). The reset deletes library, artwork, cache, audit and crash files but never touches <data>/plugins (no reference to the plugins folder in ConfirmResetLibrary, SettingsViewModel.cs:5957-6351).
```

**Why it is a bug:** The 'is null' branch was meant as a one-time upgrade migration for installs older than the switch. But 'Reset all settings' writes a fresh AppSettings, so CommunityPluginsEnabled goes back to null (JsonIgnoreCondition.WhenWritingNull omits it), DisabledPlugins becomes empty and every approval is gone. The plugin folders are still on disk. On the next LoadAll (next launch via MainWindowViewModel.cs:371, or right away with 'Reload plugins', SettingsViewModel.cs:163), MigrateSettings sees null and at least one DLL plugin. It then sets CommunityPluginsEnabled = true and grants every plugin all of its declared permissions, because IsDisabled is now false for all of them. Activate (PluginHost.cs:715 `wanted = !IsDisabled(plugin) && (... IsApproved(plugin))`, :747 `else if (plugin.IsEnabled) Start(plugin);`) then loads each one into the process. No approval dialog is shown (ConfirmEnablePluginAsync only runs from the UI toggle path). The same thing happens when settings.json is corrupt and settings.json.bak is unusable: PersistenceService.cs:131 falls back to `new AppSettings()`. Example: the user installs plugin X, later switches it off because it misbehaves (or turns off Community plugins, i.e. restricted mode), then uses Settings → Reset. After the restart, X is running with every permission it declares, and restricted mode is off. A fresh install defaults to restricted mode (OFF), so the reset produces the opposite of the default.

**Impact:** Third-party .NET code that the user disabled, or never approved, runs in-process with full user rights and no prompt. Restricted mode is silently turned off. The Settings toggle keeps showing its old state until the next launch.

**Proposed fix (small):** Treat null as 'upgrade' only when the settings file really predates the switch. Simplest: in ConfirmResetLibrary set `defaultSettings.CommunityPluginsEnabled = false` before saving, and in LoadSettingsAsync set it to false when SettingsLoadFailed is true and no backup was recovered. More robust: key the migration on a schema/version marker (e.g. MetadataSchemaVersion or a new PluginsMigrated flag) instead of `is null`, so defaults never auto-grant.

**Verifier votes**

- **confirmed** (refute lens): MigrateSettings (PluginHost.cs:695-704) treats CommunityPluginsEnabled==null as the pre-switch upgrade case. It turns community plugins on and calls Grant for every non-disabled plugin whenever any code plugin is found. ConfirmResetLibrary saves a fresh AppSettings (SettingsViewModel.cs:6111-6114) and assigns it to _settings (:6125). That leaves CommunityPluginsEnabled null (AppSettings.cs:50) and DisabledPlugins and grants empty. The reset never touches the plugins folder: there is no PluginsDirectory reference in the reset path, and the SettingsReset handler (MainWindowViewModel.cs:311-316) only reloads playlists. The merge skips the plugin keys (SettingsViewModel.cs:2611-2614), so a later save does not restore the old values. PluginHost reads Settings.GetSettings() (MainWindowViewModel.cs:357). The next LoadAll (next launch, or ReloadPlugins at SettingsViewModel.cs:163) therefore auto-grants all plugins and Activate starts them (PluginHost.cs:715, :747) with no ConfirmEnable prompt. The same happens on the corrupt-settings path with no usable backup: PersistenceService.cs:131 falls back to new AppSettings().

### X16 — In-app update download has a hard 5-minute deadline: on connections below about 4–6 Mbit/s it always fails as "Download cancelled."

- **Location:** `src/Noctis/ViewModels/SettingsViewModel.cs:6536` (also: `src/Noctis/ViewModels/SettingsViewModel.cs:6814`, `src/Noctis/ViewModels/SettingsViewModel.cs:6877`, `src/Noctis/Services/UpdateService.cs:508`, `src/Noctis/Services/UpdateService.cs:558`)
- **Area / sweep:** Security / sec-net · **Category:** network-reliability · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
SettingsViewModel.cs:6536   _updateCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
:6555-6556  _downloadedInstallerPath = await _updateService.DownloadInstallerAsync(
                update, progress, _updateCts.Token, requireChecksums: true);
:6566-6568  catch (OperationCanceledException)
            {
                UpdateStatusText = "Download cancelled.";
UpdateService.cs:508  while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
UpdateService.cs:558  try { File.Delete(tempPath); } catch { /* best effort */ }
```

**Why it is a bug:** The token is one absolute deadline covering the release re-check, the whole installer download and the SHA-256 pass. The ReadAsync loop observes it. Asset sizes for v1.5.4 from the GitHub releases API: Noctis-v1.5.4-Setup.exe is 151,735,239 B, Noctis-osx-arm64.dmg is 206,441,097 B and Noctis-x86_64.AppImage is 213,055,992 B. Finishing within 300 s needs about 4.0, 5.5 and 5.7 Mbit/s sustained. On a slower link (mobile hotspot, rural DSL, a throttled GitHub CDN region) the download is cancelled at 5:00 and the partial file is deleted. The pill says "Download cancelled." although the user cancelled nothing, the OCE branch logs nothing, and each retry starts from zero, so the in-app update never succeeds. The same deadline is used at :6814 (version manager install) and :6877 (download to Downloads).

**Impact:** Users on slow connections can never update in-app, and the status text blames them ("cancelled").

**Proposed fix (small):** Remove the absolute deadline. In DownloadInstallerAsync, link the caller token with an inactivity CTS that is reset with CancelAfter(TimeSpan.FromSeconds(30)) after every chunk read, so only a stalled transfer is aborted. CancelUpdate remains the user's cancel. Report a timeout separately from a user cancel, and log it.

**Verifier votes**

- **confirmed** (refute lens): SettingsViewModel.cs:6536 creates `new CancellationTokenSource(TimeSpan.FromMinutes(5))`, and the same token covers the re-check at :6539 and DownloadInstallerAsync at :6555-6556. The ReadAsync loop at UpdateService.cs:508 observes it. On expiry, the catch at :555-559 deletes the partial file and SettingsViewModel.cs:6566-6570 shows "Download cancelled." without logging anything. The same pattern is at :6814 and :6877, which use the dev-mode status text. The shared HttpClient's 15 s Timeout (Program.cs:253) does not rescue this: with ResponseHeadersRead it covers only the wait for headers. The size claim checks out: the local installer-output/Noctis-v1.5.4-Setup.exe is 151,735,239 B. That needs about 4.05 Mbit/s sustained to finish in 300 s, so slow links always fail and each retry restarts from zero. Medium is right.

## Low (60)

### A02 — NOCTIS_ENGINE_TAP rebuilds the render chain as Tap(Provider), silently removing the mute gate and the beat and spectrum tap

- **Location:** `src/Noctis/Services/GaplessSink.cs:113`
- **Area / sweep:** Audio pipeline / audio-output · **Category:** diagnostics · **Platforms:** windows
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
GaplessSink.cs:100-105 build `renderSource = _muteGate` then `renderSource = new BeatTapProvider(renderSource, OutputLatencyMs);`
GaplessSink.cs:112-113
    _tap = new WaveFileWriter(tapPath, Provider.WaveFormat);
    renderSource = new TapProvider(Provider, _tap);
```

**Why it is a bug:** The tap wraps Provider directly and replaces renderSource, so MuteGateProvider and BeatTapProvider drop out of the chain whenever the diagnostic env var is set. IsMuted still writes _muteGate, but nothing reads it.

**Impact:** With NOCTIS_ENGINE_TAP on, the mute button does nothing and the visualizer and beat pulse freeze. Any capture meant to diagnose mute or post-gate behaviour records a different chain from the one users run.

**Proposed fix (trivial):** Wrap the existing chain instead: `renderSource = new TapProvider(renderSource, _tap);`. If the pre-mute signal is wanted, insert the tap before the mute gate and keep the gate and BeatTap after it.

**Verifier votes**

- **confirmed** (refute lens): GaplessSink.cs:100-105 builds MuteGate(Provider) and then BeatTap. Line 113 then sets `renderSource = new TapProvider(Provider, _tap);`, which wraps the raw Provider and throws away the gate and the BeatTap. StallProbe (126) and CreateOutput (192) render that value, so with NOCTIS_ENGINE_TAP set, IsMuted (40-44) writes to a gate nothing reads, and the beat tap stops. It only happens behind the diagnostic env var, so low.

### A04 — GaplessSink reads the device format and watches the Multimedia role, but NAudio's WasapiOut opens the Console role

- **Location:** `src/Noctis/Services/GaplessSink.cs:244` (also: `src/Noctis/Services/GaplessSink.cs:80`, `src/Noctis/Services/GaplessSink.cs:181`, `src/Noctis/Services/GaplessSink.cs:281`, `src/Noctis/Services/WindowsSessionVolume.cs:138`)
- **Area / sweep:** Audio pipeline / audio-output · **Category:** device-change · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
GaplessSink.cs:80/181/244/281: `enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)`
GaplessSink.cs:189: `new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: OutputLatencyMs)` -> NAudio helper: `return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);`
WindowsSessionVolume.cs:138 resolves sessions on `ERole.eConsole`.
```

**Why it is a bug:** NAudio's parameterless-device WasapiOut constructor binds the Console default endpoint (https://github.com/naudio/NAudio/blob/master/NAudio.Wasapi/WasapiOut.cs). The sink takes its rate and channel count (81-83), the upmix channel count (DeviceMixChannels) and its change detection (CheckDefaultDevice) from the Multimedia default instead. When the two role defaults differ, which per-role tools such as SoundSwitch/EarTrumpet or the classic panel can set, three things happen. The sink never follows a change of the Console default. A change of the Multimedia default triggers useless rebuilds onto the unchanged Console device. Upmix sizes its output for the wrong device, for example 8 channels folded into a stereo device.

**Impact:** For users with split role defaults, output does not follow device switches, and upmix can fold surround channels back into stereo.

**Proposed fix (small):** Open the stream on an explicit device: fetch `GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)` (or Multimedia, as long as it is used consistently everywhere, including WindowsSessionVolume) and pass that MMDevice to `new WasapiOut(device, ...)`. Store its ID and read the mix format from the same device.

**Verifier votes**

- **confirmed** (refute lens): GaplessSink.cs:80, 181, 244 and 281 all use Role.Multimedia. Line 189 uses `new WasapiOut(AudioClientShareMode.Shared, true, 100)`. In NAudio.Wasapi 2.3.0 that ctor chains to the static GetDefaultAudioEndpoint, whose IL is `newobj MMDeviceEnumerator; ldc.i4.0; ldc.i4.0; callvirt GetDefaultAudioEndpoint`, i.e. Render with Role.Console (0). WindowsSessionVolume.cs:138 uses eConsole. The inconsistency is real. One correction: the Windows Sound control panel and Settings set Console and Multimedia together. Split defaults only happen through IPolicyConfig tools that set a single role, so this is an edge case and low is right.

### A06 — The sink rebuild starts the new output before publishing it and keeps _rebuilding set during the callback, so an immediate failure of the new stream is dropped and the engine stays silent (or rebuilds in a hot loop)

- **Location:** `src/Noctis/Services/GaplessSink.cs:287` (also: `src/Noctis/Services/GaplessSink.cs:219`, `src/Noctis/Services/GaplessSink.cs:326`, `src/Noctis/Services/GaplessSink.cs:334`, `src/Noctis/Services/TempoStretchProvider.cs:77`)
- **Area / sweep:** Audio pipeline / audio-output · **Category:** device-change · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
GaplessSink.cs:221-230 (failure handler)
    if (_disposed || e.Exception == null) return;
    WasapiOut current;
    lock (_gate) current = _out;
    if (!ReferenceEquals(sender, current)) return;
    ...
    if (Interlocked.Exchange(ref _rebuilding, 1) == 0)
        Task.Run(RebuildLoop);
GaplessSink.cs:287-299: `if (_desiredPlaying) newOut.Play();` happens BEFORE `lock (_gate) { ... _out = newOut; }`
GaplessSink.cs:320-323: `finally { Volatile.Write(ref _rebuilding, 0); }` only runs after Rebuilt?.Invoke()
```

**Why it is a bug:** NAudio's PlayThread catches any exception from FillBuffer, GetBuffer or Start and raises PlaybackStopped(exception). The new WasapiOut is created on a ThreadPool thread, so there is no SynchronizationContext and the event is raised inline on the render thread (RaisePlaybackStopped, https://github.com/naudio/NAudio/blob/master/NAudio.Wasapi/WasapiOut.cs). If the new stream fails before `_out = newOut`, the sender check discards it as stale. If it fails after the swap but before the finally resets _rebuilding, the Interlocked check discards it. Either way nothing rebuilds again: CheckDefaultDevice only acts when the default device ID changes. Realistic triggers are an endpoint invalidated right after it becomes default (Bluetooth profile flips, USB re-enumeration) and any deterministic render-chain exception, such as the already-reported TempoStretch/Pitch Array.Copy. In the second case, if the swap wins the race, every attempt 'succeeds' and then dies at once, giving a rebuild loop with no backoff. Each Rebuilt also queues a session-reassert worker that can sleep on a pool thread for up to 1.5 s. The same ordering lets Pause()/Resume() race the swap: they read `_desiredPlaying`/`_out` of the old output, so the new stream can run while paused or never start while playing.

**Impact:** The gapless engine can go permanently silent while the timeline keeps moving (the 'silence until app restart' symptom the header says was fixed), or loop through rebuilds and starve the thread pool.

**Proposed fix (small):** In RebuildLoop, publish `_out = newOut` under _gate before calling Play(), and read _desiredPlaying under the same lock. In OnPlaybackStopped, when _rebuilding is already 1, set a `_rebuildAgain` flag that RebuildLoop checks (and loops on) before it clears _rebuilding. Count failures that happen within a short window after a successful rebuild and apply the existing 250 ms to 4 s backoff to them.

**Verifier votes**

- **confirmed** (refute lens): The code matches the finding. GaplessSink.cs:287 calls Play() before the `_out = newOut` swap under _gate (288-299). OnPlaybackStopped (219-231) drops any sender that is not `_out`, and it also drops a failure while `_rebuilding==1`, which is only cleared in the finally at 320-323 after the log line and Rebuilt. CreateOutput runs on a Task.Run thread, so NAudio has no sync context and raises PlaybackStopped inline on the render thread. CheckDefaultDevice (237-257) only rebuilds when the ID changes. Nothing in VlcAudioPlayer watches for a dead sink: the only other RequestRebuild caller is the upmix setting at :2264. The engine pause/resume callbacks are no-ops. Reaching this needs the new stream to fail within the narrow window between Init and the first GetBuffer/Start, roughly sub-ms to a few ms (after the swap, before the finally). The Pause/Resume race window between lines 287 and 297 is microseconds. The rebuild hot loop needs a separate deterministic render-chain exception. So this is an edge-case race with a severe consequence: low.

### A10 — After a paused sink, the provider's declick ramp replays the stale pre-pause frame, giving a click when a new track is chosen, or a seek or restart is made, while paused

- **Location:** `src/Noctis/Services/GaplessSpliceCore.cs:637` (also: `src/Noctis/Services/GaplessSpliceCore.cs:531`, `src/Noctis/Services/GaplessSpliceCore.cs:452`, `src/Noctis/Services/GaplessSink.cs:326`, `src/Noctis/Services/VlcAudioPlayer.cs:2903`)
- **Area / sweep:** Audio pipeline / audio-core · **Category:** pops-clicks · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
GaplessSpliceCore.cs:637-647
    if (_startFadeSamples > 0 && _silentSamples == 0)
        _declickRemaining = _startFadeSamples;
    if (_declickRemaining > 0)
    { ...
        buffer[pos + i] = _lastFrame[i % ch] * ((float)_declickRemaining / _startFadeSamples);
GaplessSpliceCore.cs:531-535 (cut: if (_silentSamples == 0) _declickRemaining = _startFadeSamples;)
GaplessSink.cs:326-332  public void Pause() { _desiredPlaying = false; ... current.Pause(); }
VlcAudioPlayer.cs:4255-4256  if (_gaplessEngine) _gaplessSink?.Pause();
```

**Why it is a bug:** WasapiOut.Pause just stops calling the provider. The device drains to silence, but the provider still believes live audio is playing: `_silentSamples` is 0 and `_lastFrame` holds the last pre-pause sample. The next cut is either PlayInternal's EngineClearAll (`_pendingCutSignal`, GaplessSpliceCore.cs:452) or a seek flush while paused (`_cutPending`). On the next read it emits a 5 ms ramp from that stale value down to 0, preceded by true silence, which is an isolated half-triangle pulse (a tick). The faded-pause option does not mask it because RestoreLevelWhilePaused puts the session back at the user level before the resume.

**Impact:** A tick or pop when starting a different track while paused, or on resume after moving the timeline or pressing Previous while paused. It is a candidate contributor to the 'noise on track start' reports.

**Proposed fix (small):** When the sink is paused, tell the provider (a volatile flag consumed on the render thread) to zero `_lastFrame` and set `_silentSamples = _fadeArmSamples`. The next audio then gets the normal 5 ms fade-in and no stale declick, which also removes the plain resume click.

**Verifier votes**

- **confirmed** (refute lens): I checked the NAudio.Wasapi 2.3.0 IL: WasapiOut.Pause only writes playbackState. The audio client keeps running and the provider is not called, so _silentSamples stays 0 and _lastFrame keeps the pre-pause sample. GaplessSink.Pause (GaplessSink.cs:326-332) calls only that. Clear() (GaplessSpliceCore.cs:452) sets _pendingCutSignal, and Flush (:178) sets _cutPending. After Resume (VlcAudioPlayer.cs:2904 for a new track), the first Read arms the declick because _silentSamples==0 (:531-535, :637-638) and ramps from the stale _lastFrame (:645), right after the device has drained to silence. No dip happens on the new-track path, and RestoreLevelWhilePaused (:2174) has already restored the level. One correction: for seek or Previous while paused followed by a faded resume, DipLevelBeforeResume (:4303) does mostly mask it. The finding's claim that the faded pause never masks it is true only for the new-track case.

### A14 — The volume ramp worker keeps writing the session during pause and crossfade fades, which the setters assume are parked, so the two fight

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:1160` (also: `src/Noctis/Services/VlcAudioPlayer.cs:2153`, `src/Noctis/Services/VlcAudioPlayer.cs:1352`, `src/Noctis/Services/VlcAudioPlayer.cs:4261`)
- **Area / sweep:** Audio pipeline / audio-output · **Category:** volume · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
VlcAudioPlayer.cs:1160-1184 (ramp loop, no _transitionInFlight check)
    while (!_disposed)
    {
        var target = Volatile.Read(ref _rampTargetMilli);
        ...
        var current = Volatile.Read(ref _rampCurrentMilli);
        ...
        ApplyRampLevel(next);
        Volatile.Write(ref _rampCurrentMilli, next);
VlcAudioPlayer.cs:1379-1380 (FadeSessionLevelBlocking also writes): `sv.SetLevel(milli / 1000.0); Volatile.Write(ref _rampCurrentMilli, milli);`
VlcAudioPlayer.cs:4263-4268: '// Slider parked for the drain too: a level written now would lift the same buffered tail.'
```

**Why it is a bug:** Only the setters (Volume/VolumeAdjust/CommitVolume) check _transitionInFlight. A ramp worker already running when a fade starts, i.e. a slider move converging at ≤10‰ per 16 ms (up to 1.6 s for a full-range move), keeps reading _rampCurrentMilli, which the fade overwrites every 35 ms, and pushes it back toward the user level. The session then zig-zags down 1/8 per fade step and up 10‰ per tick: exactly the reversing write pattern the ramp was tuned to avoid (header comment 67-73). After the pause fade lands at 0, the worker ramps back up during the drain sleep, reproducing the GitHub #77 tail burst the drain wait was added for. The same conflict applies to the classic-path crossfades that use FadeSessionLevelBlocking.

**Impact:** Moving the volume slider and then pausing within about 1-2 s, with the opt-in play/pause fade on, or during a classic-path crossfade, gives crackle during the fade and a short burst of the buffered tail.

**Proposed fix (trivial):** In the ramp loop, `if (_transitionInFlight) { await Task.Delay(_volumeRampTickMs); continue; }` without writing, so the target is still served once the transition clears. Or have FadeSessionLevelBlocking claim _rampWorkerActive for its duration.

**Verifier votes**

- **confirmed** (refute lens): The ramp loop at VlcAudioPlayer.cs:1160-1190 never checks _transitionInFlight. It reads _rampCurrentMilli each tick and writes ApplyRampLevel(next) plus _rampCurrentMilli. On the default Windows engine, ActiveCallbackSink is null (3810) and _sessionVolume is set (648-650), so FadeOutBeforePause (2153-2171) calls FadeSessionLevelBlocking. That fade writes sv.SetLevel and _rampCurrentMilli every ~35 ms (1369-1383) and never stops the worker. A worker that started before the fade therefore keeps pulling the level back toward _rampTargetMilli. It cannot converge while the fade drags current away, so it is still running into the drain sleep at 4261-4268. Only the setters (823, 838, 871) and ReapplyVolume (2016) are gated. This needs a volume change within the ramp's convergence window (up to ~1.6 s) right before a pause with the opt-in fade enabled, so it is an edge case.

### A15 — Output-mode rebuild resumes at _player.Time, which on the engine is ahead of the audible position by the ring depth, so enabling Exclusive Mode skips ahead

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:1684` (also: `src/Noctis/Services/VlcAudioPlayer.cs:1655`, `src/Noctis/Services/VlcAudioPlayer.cs:4725`)
- **Area / sweep:** Audio pipeline / audio-output · **Category:** position · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
VlcAudioPlayer.cs:1682-1684
    if (wasActive)
    {
        try { resumeMs = Math.Max(0, _player.Time); } catch { }
VlcAudioPlayer.cs:4719-4721 (same class): '// Engine: _player.Time leads the AUDIBLE position by the ring depth (amem reports no latency to VLC), so position comes from the sink's active segment.'
```

**Why it is a bug:** RebuildOutputModeLocked first tears down the engine (1655-1664) and then takes the resume point from VLC's clock. The class's own position logic says that clock is ahead of what was heard by the ring depth: about 2 s after a cold start or seek, and more (the staging lead, up to ~8-10 s) after a gapless advance, because the standby player's clock runs from the moment it is staged. The audible segment position (_gaplessSink.Provider.ActiveSegment.PositionMs) is the value that should be used, and it is discarded.

**Impact:** Turning on Exclusive Mode mid-track jumps the song forward by a few seconds up to about 10 s compared with what was playing.

**Proposed fix (small):** Before disabling the engine in RebuildOutputModeLocked, capture the audible position the same way OnPositionTimerElapsed does (active segment PositionMs when it belongs to _player, else 0 / _player.Time), and use that as resumeMs.

**Verifier votes**

- **confirmed** (refute lens): The code matches. RebuildOutputModeLocked (VlcAudioPlayer.cs:1655-1664) disables the engine, calls EngineClearAll (only Provider.Clear, 3999-4003) and disposes the sink. It then takes resumeMs = _player.Time (1684). The same class's position logic (4719-4730) says _player.Time leads the audible position by the ring depth on the engine. That logic reports ActiveSegment.PositionMs instead, and reports 0 while the old player's tail is still rendering. GaplessTrackSegment back-pressures a ring of up to 20 s (GaplessSpliceCore.cs:66 and 90-108, capacitySeconds: 20 at VlcAudioPlayer.cs:3989). That makes the jump forward real when Exclusive Mode is toggled while the engine is active. I could not settle from code the exact size of the jump, i.e. how far VLC decodes ahead.

### A20 — The engine seek re-bases the segment from the unclamped target while the worker seeks to the end-guarded target, so the timeline and lyrics run up to 1 s ahead

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:4370` (also: `src/Noctis/Services/VlcAudioPlayer.cs:3949`, `src/Noctis/ViewModels/PlayerViewModel.cs:2638`)
- **Area / sweep:** Audio pipeline / audio-core · **Category:** position · **Platforms:** windows
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
VlcAudioPlayer.cs:4369-4372
    if (_gaplessEngine)
        Interlocked.Exchange(
            ref _enginePendingBaseMs[EngineSlotOf(_player)],
            (long)position.TotalMilliseconds);
VlcAudioPlayer.cs:4407-4408
    var maxSeekMs = len - Math.Min(EndSeekGuardMs, len / 20);
    var clampedMs = (long)Math.Clamp(position.TotalMilliseconds, 0, maxSeekMs);
```

**Why it is a bug:** EngineFlush re-bases the segment with `_enginePendingBaseMs` (3949), and the engine position is base + consumed. A click in the last ~1 s of the timeline sets the base to the raw position, but VLC seeks to len − 1000 ms, so the reported position leads the audio by up to 1 s. The VM then extends Duration because latest > duration (PlayerViewModel.cs:2638-2642).

**Impact:** After clicking near the very end of the seek bar, lyrics run up to 1 s early and the duration label grows.

**Proposed fix (trivial):** Store the base only after clamping (move the Exchange below line 4408 and use clampedMs), or set it in the seek worker from the final targetMs.

**Verifier votes**

- **confirmed** (refute lens): Seek writes the raw position.TotalMilliseconds into _enginePendingBaseMs at 4369-4372, before the end guard is computed at 4407-4408 (EndSeekGuardMs=1000, line 32). The worker then seeks VLC to the clamped value (_latestSeekMs=clampedMs; _player.Time=targetMs at ~4858/4871). EngineFlush (3949) re-bases the segment from the unclamped base, and EnginePlay applies no pts-based correction. Reported position = raw + consumed, so it leads the audio by up to 1 s. When latest > duration, PlayerViewModel.cs:2638-2642 extends Duration. This only happens on seeks into the last ~1 s, so it is an edge case.

### A33 — The UI thread calls native libvlc get_length on every position tick and in Seek(), which can block while a worker's _player.Stop() joins a stalled input thread

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:4400` (also: `src/Noctis/Services/VlcAudioPlayer.cs:759`, `src/Noctis/Services/VlcAudioPlayer.cs:4400`, `src/Noctis/Services/VlcAudioPlayer.cs:4737`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** ui-thread-blocking · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
PlayerViewModel.cs:2627 (inside the UI-thread Post of OnPositionChanged)  var decoderDuration = _audioPlayer.Duration;
VlcAudioPlayer.cs:759-765  public TimeSpan Duration { get { ... var len = _player.Length; ... } }
VlcAudioPlayer.cs:4400 (Seek, called inline from SeekToPosition on the UI thread)  var len = _player.Length;
VlcAudioPlayer.cs:2838 (PlayInternal on a ThreadPool worker)  _player.Stop();
```

**Why it is a bug:** In VLC 3.0.x, libvlc_media_player_get_length takes the player's input lock (libvlc_get_input_thread -> lock_input). libvlc_media_player_stop holds that same lock across release_input_thread -> input_Stop/input_Close, which joins the input thread (https://code.videolan.org/videolan/vlc/-/blob/3.0.x/lib/media_player.c). A position tick posted just before PlayInternal's Stop, or a timeline click or SMTC scrub during a track change, runs get_length on the UI thread. If the input thread is stuck in a blocking read (spun-down HDD, SMB share), the UI freezes until the join completes. The timer thread already caches the length in _lastKnownLengthMs (4737-4739).

**Impact:** Occasional UI hangs when changing tracks from slow or network storage, the same symptom class as the Resume freeze fixed earlier (VlcAudioPlayer.cs:4285-4290).

**Proposed fix (trivial):** Have VlcAudioPlayer.Duration return the cached `Interlocked.Read(ref _lastKnownLengthMs)`, which the timer thread already maintains, instead of calling `_player.Length` on the caller's thread. In Seek(), use the cached length for the end guard.

**Verifier votes**

- **confirmed** (refute lens): The mechanism is real. Noctis.csproj:94 pins VideoLAN.LibVLC.Windows 3.0.23.1. In VLC 3.0.x lib/media_player.c, libvlc_media_player_stop holds lock_input across release_input_thread (input_Stop plus input_Close, which joins the thread) and input_resource_Terminate. get_length calls libvlc_get_input_thread, which takes lock_input. The code matches at VlcAudioPlayer.cs:759-766 (Duration -> _player.Length), :4384/:4400 (Seek, synchronous on the caller's thread) and :2838 (Stop on a ThreadPool worker). The position-tick path is mostly guarded, though. PlayInternal increments the session and stops _positionTimer before parse/Stop (:2622-2623), and ticks from an old session are dropped (:4742). PlayTrack also arms the 300 ms settle window (PlayerViewModel.cs:1995, :2602), and the stale-position guard (:2608-2613) returns before :2627 unless the old track was under about 4 s in. The reachable path is a user or SMTC seek during a track change, or during Stop() (:4345). The hang is long only when the input thread is stuck in a blocking read, so this is an edge case whose length depends on runtime.

### A35 — PlayTrack counts a play, records history and fires TrackStarted before the file is opened, so missing or unparsable files get phantom plays

- **Location:** `src/Noctis/ViewModels/PlayerViewModel.cs:2001` (also: `src/Noctis/ViewModels/PlayerViewModel.cs:3063`, `src/Noctis/ViewModels/MainWindowViewModel.cs:2830`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** data-integrity · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
PlayerViewModel.cs:2001-2005
  track.PlayCount++;
  track.LastPlayed = DateTime.UtcNow;
  MarkPlayStateDirty(track);
  _playHistory?.RecordPlay(track);
  MarkRecentlyPlayed(track);
2047  _audioPlayer.Play(track.FilePath);
2061  TrackStarted?.Invoke(this, track);
VlcAudioPlayer.cs:2570-2573  if (!IsPathlessMedia(filePath) && !File.Exists(filePath)) { PlaybackError?.Invoke(...); return; }
```

**Why it is a bug:** A missing file fails synchronously inside Play(). By then the play has already been counted, journaled and logged to play history, and TrackStarted then fires anyway: Discord presence, scrobble tracking, Home recents and lyrics load. In the error cascade, five dead tracks each get a phantom play.

**Impact:** Play counts, 'Last played', Wrap stats and smart playlists are inflated by tracks that never played, and Discord briefly shows them.

**Proposed fix (small):** Skip the counting block and TrackStarted when `!VlcAudioPlayer.IsPathlessMedia(track.FilePath) && !File.Exists(track.FilePath)`, or move the counting to the first accepted position tick of the new session.

**Verifier votes**

- **confirmed** (refute lens): PlayerViewModel.cs:2001-2005 increments PlayCount, sets LastPlayed, marks the play state dirty, calls RecordPlay and MarkRecentlyPlayed. Play() is called later, at :2047. VlcAudioPlayer.Play (:2570-2574) raises PlaybackError synchronously for a missing file and returns. OnPlaybackError (:3063-3094) only posts a skip; it never rolls back the counts. TrackStarted still fires at :2061, which drives OnTrackStartedForIntegrations (MainWindowViewModel.cs:2830-2854): the scrobble start, Discord presence, Now Playing and the sync push. There is no File.Exists pre-check on the PlayTrack path (the only checks are at :2841/:2959, for AutoMix prep). Parse failures (VlcAudioPlayer.cs:2818-2826) are counted the same way. Only data integrity is affected.

### A36 — The desktop never persists the pre-shuffle order, so turning Shuffle off after a restart leaves the queue scrambled

- **Location:** `src/Noctis/ViewModels/PlayerViewModel.cs:1536` (also: `src/Noctis.Core/Models/QueueState.cs:43`, `src/Noctis/ViewModels/PlayerViewModel.cs:1410`, `src/Noctis/ViewModels/PlayerViewModel.cs:1465`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
PlayerViewModel.cs:1536  IsShuffleEnabled = state.IsShuffleEnabled;   (_originalQueue not restored)
744  else if (_originalQueue.Count > 0) { ...restore order... }
QueueState.cs:38-43  /// Pre-shuffle order ... the desktop player writes its own QueueState and simply leaves this empty.
  public List<Guid> OriginalOrderIds { get; set; } = new();
```

**Why it is a bug:** Shuffle is restored as ON but _originalQueue is empty after launch, so ToggleShuffle's restore branch is skipped. The indicator turns off and the queue stays shuffled. The model already has OriginalOrderIds; the desktop save paths (1412-1423, 1470-1481) just don't fill it.

**Impact:** After restarting with Shuffle on, turning Shuffle off doesn't restore album/playlist order.

**Proposed fix (trivial):** Write `OriginalOrderIds = _originalQueue.Select(t => t.Id).ToList()` in SaveQueueStateAsync and SaveQueueStateInBackground, and rebuild `_originalQueue` from it (via Resolve) in RestoreQueueStateAsync.

**Verifier votes**

- **confirmed** (refute lens): Line 1536 of PlayerViewModel.cs restores IsShuffleEnabled, but nothing in RestoreQueueStateAsync (1505-1580) fills _originalQueue. The only writer of _originalQueue is ToggleShuffle at 733 (a grep finds no other). Both save paths leave OriginalOrderIds unset: SaveQueueStateAsync (1412-1423) and SaveQueueStateInBackground (1470-1481). So after a restart, the first ToggleShuffle turns shuffle off (721), skips the restore branch because `_originalQueue.Count > 0` is false (744), and the queue keeps its shuffled order. QueueState.cs:38-43 names this exact cold-start purpose for OriginalOrderIds and says the desktop 'simply leaves this empty'. That comment describes the gap; it does not justify it. The mobile PlaybackQueue already round-trips the field (PlaybackQueue.cs:208). The proposed fix is sound.

### A37 — A pause arriving while VLC is still Opening/Buffering a new track is ignored: audio starts under a Paused UI

- **Location:** `src/Noctis/Services/VlcAudioPlayer.cs:4243` (also: `src/Noctis/ViewModels/PlayerViewModel.cs:442`, `src/Noctis/Services/SmtcService.cs:128`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** race-condition · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
VlcAudioPlayer.cs:4240-4243 (Pause worker)
  try
  {
      if (_disposed) return;
      if (_player.IsPlaying)
PlayerViewModel.cs:448-450  _audioPlayer.Pause(); State = PlaybackState.Paused; SaveQueueStateInBackground();
```

**Why it is a bug:** This is a different trigger from the reported engine-tail case. libvlc_media_player_is_playing returns true only for libvlc_Playing (https://code.videolan.org/videolan/vlc/-/blob/3.0.x/lib/media_player.c). After PlayInternal releases the lock, VLC is still Opening/Buffering for tens of ms (longer on slow storage). A pause from Space, SMTC/MPRIS media keys or the taskbar in that window is dropped: the VM sets Paused, the audio starts, and the next Play press only flips the UI because Resume sees _isPaused false.

**Impact:** Pausing right as a track starts (for example a double media-key press) leaves music playing while the UI says paused.

**Proposed fix (trivial):** In the Pause worker, also pause when `_player.State is VLCState.Opening or VLCState.Buffering`: call `_player.SetPause(true)`, set `_isPaused = true` and stop the position timer.

**Verifier votes**

- **confirmed** (refute lens): The Pause worker in VlcAudioPlayer.cs acts only `if (_player.IsPlaying)` (4243) and has no else branch or saved pause intent (a grep finds no pending-pause field or state handler). Play() queues PlayInternal onto the ThreadPool under the same _playbackLock (2587-2600). PlayInternal calls _player.Play (2907) and then returns. Meanwhile PlayerViewModel.PlayTrack sets State = Playing right away (1998). The repo itself confirms the window at VlcAudioPlayer.cs:1666-1672: VLC opens input asynchronously, so a fresh Play reports IsPlaying false for the first few hundred ms (field repro 08-16). A pause in that window is dropped. The VM sets Paused (PlayerViewModel.cs:449-450) and has no audio-state event to reconcile it (subscriptions at 413-419 cover position, end, error and duration only). The next Resume is a no-op because _isPaused is false (4299), so the defect is real as described. Caveat on the fix: in VLC 3.0, libvlc set_pause/pause calls input_Stop when can-pause is not yet true, which can happen during Opening. SetPause(true) at that point could stop the track. Recording a pause intent and applying it once Playing starts, or using the existing startPaused/:start-paused path, is safer.

### A38 — MPRIS SetPosition ignores the TrackId, so a stale scrub from a widget seeks the next track

- **Location:** `src/Noctis/Services/MprisService.cs:522` (also: `src/Noctis/Services/MprisService.cs:357`)
- **Area / sweep:** Audio pipeline / audio-vm · **Category:** correctness · **Platforms:** linux
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
MprisService.cs:519-523
  case "SetPosition":
  {
      var reader = context.Request.GetBodyReader();
      reader.ReadObjectPath(); // trackid — single-track player, ignored
      _s.SeekTo(reader.ReadInt64());
```

**Why it is a bug:** The MPRIS spec says that if the TrackId differs from the current track, the SetPosition call is ignored as stale (https://specifications.freedesktop.org/mpris-spec/latest/Player_Interface.html#Method:SetPosition). A scrub sent just after a track change, when the widget still holds the old trackid, seeks the new track.

**Impact:** On Linux, occasionally the new track jumps to a position meant for the previous one.

**Proposed fix (trivial):** Compare the object path with `_trackId` under `_stateLock` and ignore the call when they differ.

**Verifier votes**

- **confirmed** (refute lens): MprisService.cs:519-525 reads the trackid object path and discards it ('single-track player, ignored'), then calls SeekTo. SeekTo (357-365) turns positionUs into a fraction of the current _player.Duration and seeks. _trackId is set per track as /com/heartached/noctis/track/<id> (231-233) and published as mpris:trackid (261), so the 'single-track' reasoning is wrong: the ID changes on every track. The MPRIS spec says a SetPosition whose TrackId does not match the current track is ignored as stale, so a scrub sent during a track change seeks the new track. The window is small and the bug is Linux-only, so low severity. A minor extra spec gap: out-of-range positions are clamped (363) when the spec says to ignore them.

### P02 — AppImage desktop entry has no MimeType= and no %F, so "Open with Noctis" (which the app supports) never appears in Linux file managers

- **Location:** `.github/workflows/dotnet.yml:422` (also: `src/Noctis/Program.cs:36-38`)
- **Area / sweep:** Cross-platform / xplat-linux · **Category:** packaging · **Platforms:** linux
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
dotnet.yml:422-431
    cat > "$APPDIR/noctis.desktop" <<DESKTOP
    [Desktop Entry]
    Type=Application
    Name=Noctis
    Comment=A music player that respects what's yours
    Exec=Noctis
    Icon=noctis
    Categories=AudioVideo;Audio;Player;
    Terminal=false
    DESKTOP
Program.cs:36-38 collects file args ("Open with Noctis" / double-clicked track); SingleInstanceGuard forwards them.
```

**Why it is a bug:** Under the freedesktop Desktop Entry / MIME association model, an application is listed as a handler for audio files only through MimeType=. Files are passed through the %f/%F/%u/%U field codes (https://specifications.freedesktop.org/desktop-entry-spec/latest/exec-variables.html). Nothing else in src registers MIME associations; a grep for MimeType/xdg-mime finds nothing.

**Impact:** After integrating the AppImage (AppImageLauncher, Gear Lever, appimaged), Noctis isn't offered under "Open With" for .mp3/.flac and can't be set as the default player. So the AUDIT.md manual check "open an .mp3 from the file manager while running" can't pass on Linux. Needs testing on Linux.

**Proposed fix (trivial):** Use `Exec=Noctis %F`. Add a `MimeType=` line covering the supported formats, e.g. audio/mpeg;audio/flac;audio/x-flac;audio/ogg;audio/x-vorbis+ogg;audio/opus;audio/mp4;audio/x-m4a;audio/aac;audio/wav;audio/x-wav;audio/x-aiff;audio/x-ape;audio/x-wavpack; and optionally StartupWMClass.

**Verifier votes**

- **confirmed** (refute lens): dotnet.yml:422-431 writes the only .desktop file in the repo. It has `Exec=Noctis` with no %F and no MimeType= line, and a grep of the repo finds no other desktop entry or xdg-mime registration. Program.cs:36-38 does accept file arguments, so the app supports 'Open with'. Noctis will not be offered as an audio handler. The claim that it 'never appears' is slightly overstated: GIO/KDE 'Other application' pickers can still choose it, and GIO appends %f when no field code is present. This is a missing integration, so low severity.

### P07 — Linux: the watcher's "file still being written" check can't see writers and passes on the first sample, so half-written files are imported

- **Location:** `src/Noctis.Core/Services/LibraryWatcherService.cs:392` (also: `src/Noctis.Core/Services/LibraryWatcherService.cs:329-360`)
- **Area / sweep:** Cross-platform / xplat-linux · **Category:** correctness · **Platforms:** linux
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Prior audit:** AUDIT_2026-07-24.md "Watcher readiness probe defers any file another process has open for reading" (fix introduced this gap)
- **Needs runtime check:** yes

**Evidence**

```
LibraryWatcherService.cs:386-399
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    var length = fs.Length;
    if (length <= 0) return false;
    if (_lastSeenSize.TryGetValue(path, out var previous) && previous != length)
    {
        _lastSeenSize[path] = length;
        return false;
    }
    _lastSeenSize[path] = length;
    return true;
```

**Why it is a bug:** On Unix, FileShare is advisory flock only. The runtime source says: "This is only advisory locking ... not mandatory" (https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs). cp, curl -o, torrent clients and file managers don't take flock, so the open never fails on Linux. That leaves the size-growth check as the only guard. With no previous sample it returns true, so the first check after the 1.5 s debounce reports ready whenever size > 0. The earlier fix (AUDIT_2026-07-24 "Watcher readiness probe…") asked for two samples.

**Impact:** A file written with pauses longer than 1.5 s gets imported half-written. That covers a slow network or USB copy, a torrent client writing straight to the final name, and curl/wget straight into a watched folder. The track then shows a wrong duration and tags, or "won't play" (the class's own comment at line 21-23), until a later change event re-imports it. Needs testing on Linux.

**Proposed fix (trivial):** When no previous sample exists, store it and return false, so readiness needs two equal size readings FileReadyRetryMs apart (the existing retry loop already reschedules). Also compare File.GetLastWriteTimeUtc between samples.

**Verifier votes**

- **confirmed** (refute lens): The quoted code is at LibraryWatcherService.cs:386-399. With no prior _lastSeenSize entry, TryGetValue fails, so the method stores the size and returns true on the first sample whenever length > 0. The FileShare.Read open only takes an advisory flock on Unix, and cp/curl do not flock, so on Linux the IOException retry path (line 401) never triggers for a plain writer. The finding correctly limits the problem to writers that pause for more than DebounceMs=1500 (line 18): continuous writes raise Changed events that keep resetting the debounce timer through Record (line 315). AUDIT_2026-07-24.md:353-356 did ask for two samples. The impact is transient: a later write raises Changed and the file is re-imported. Low is right.

### P10 — Linux: whole-folder trash compares the removed paths case-insensitively and can trash a different file whose name differs only by case

- **Location:** `src/Noctis/Helpers/LibraryRemovalHelper.cs:290` (also: `src/Noctis/Helpers/LibraryRemovalHelper.cs:115`, `src/Noctis/Helpers/LibraryRemovalHelper.cs:209`, `src/Noctis.Core/Helpers/PathComparison.cs:14`)
- **Area / sweep:** Cross-platform / xplat-general · **Category:** cross-platform · **Platforms:** linux
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
var goes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var path in removingPaths)
{
...
    goes.Add(full);
...
foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
{
    if (Directory.Exists(entry)) return false;
    if (goes.Contains(Path.GetFullPath(entry))) continue;             // being removed anyway
```

**Why it is a bug:** On a case-sensitive filesystem, a folder holding 'Song.flac' (being removed) and a separate 'song.flac' counts the second file as 'being removed anyway'. The folder then qualifies and is trashed whole, including a file the user never selected. Because of the id collision in the ComputeFileId finding, that second file is usually not even visible in the library. GroupByDirectory (line 115) and CleanupEmptiedFoldersAsync (line 209) use the same case-insensitive comparer, although PathComparison.Comparer exists for this purpose (Noctis.Core/Helpers/PathComparison.cs).

**Impact:** Rare, but it sends an unselected file to the Trash (recoverable).

**Proposed fix (trivial):** Use Helpers.PathComparison.Comparer for `goes`, GroupByDirectory and the `seen` set. OrdinalIgnoreCase can stay only in GetProtectedRootsAsync, where over-protecting is the safe direction.

**Verifier votes**

- **confirmed** (refute lens): LibraryRemovalHelper.cs:290 builds `goes` with StringComparer.OrdinalIgnoreCase, and line 307 skips any entry whose full path matches it case-insensitively. Example on Linux: Song.flac is being removed and an unselected song.flac sits in the same folder. The folder then qualifies, and TrashLocalFilesCoreAsync (lines 88-92) moves the whole folder, including song.flac, to the Trash. The same case-folding applies to .txt sidecars (line 299): a user's song.txt next to Song.flac is treated as a sidecar. GroupByDirectory (115) and the `seen` set (209) are also OrdinalIgnoreCase, while PathComparison.Comparer (Noctis.Core/Helpers/PathComparison.cs:14-15) is Ordinal on Linux. This needs two files whose names differ only by case, and the file can be restored from the Trash, so severity is low.

### P11 — Linux 'Show in folder': dbus-send is run without --print-reply, so it exits 0 even when no file manager answers and the xdg-open fallback never runs

- **Location:** `src/Noctis/Helpers/PlatformHelper.cs:124` (also: `src/Noctis/Helpers/PlatformHelper.cs:236`, `src/Noctis/Helpers/PlatformHelper.cs:43`)
- **Area / sweep:** Cross-platform / xplat-general · **Category:** cross-platform · **Platforms:** linux
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
var psi = new ProcessStartInfo
{
    FileName = "dbus-send",
    ArgumentList =
    {
        "--session",
        "--dest=org.freedesktop.FileManager1",
        "--type=method_call",
...
if (proc.ExitCode == 0) return true;
```

**Why it is a bug:** Without --print-reply, dbus-send does not block for the method reply (the dbus-send(1) man page, https://dbus.freedesktop.org/doc/dbus-send.1.html, describes --print-reply as 'Block for a reply to the message sent'). Its exit status then does not reflect an error reply; see freedesktop bug 105380 'Fix dbus-send not returning an error exit code in case an error occurred and --print-reply is not set' (https://bugs.freedesktop.org/show_bug.cgi?id=105380). OpenLinuxFolder (line 236) already passes --print-reply for exactly this reason, but TryShowInLinuxFileManager does not. So when no FileManager1 service exists, the method returns true and ShowInFileManager never reaches its OpenLinuxFolder fallback (lines 43-48).

**Impact:** On Linux desktops without an org.freedesktop.FileManager1 implementation (minimal window managers, some tiling setups), every 'Show in folder' action does nothing and leaves no log.

**Proposed fix (trivial):** Add "--print-reply" to the TryShowInLinuxFileManager argument list, matching OpenLinuxFolder, so a failed call returns non-zero and the fallback runs.

**Verifier votes**

- **confirmed** (refute lens): PlatformHelper.cs:124-147: TryShowInLinuxFileManager runs dbus-send without --print-reply and returns true when the exit code is 0. I checked upstream dbus tools/dbus-send.c (gitlab main): without print_reply it only calls dbus_connection_send, then dbus_connection_flush, then exit(0). It never waits for the error reply, so a missing org.freedesktop.FileManager1 still exits 0. The OpenLinuxFolder fallback at lines 43-48 is then never reached, and nothing is logged. OpenLinuxFolder itself (line 236) does pass --print-reply. This only affects desktops with no FileManager1 service.

### P13 — Linux "System" theme: gsettings color-scheme 'default' is treated as a definite Light answer, so the portal/KDE/gtk-theme probes added for L24 never run

- **Location:** `src/Noctis/Helpers/PlatformHelper.cs:386` (also: `src/Noctis/ViewModels/SettingsViewModel.cs:3537`)
- **Area / sweep:** Cross-platform / xplat-linux · **Category:** correctness · **Platforms:** linux
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Prior audit:** AUDIT.md L24 (residual)
- **Needs runtime check:** yes

**Evidence**

```
PlatformHelper.cs:385-392
    // GNOME 42+ exposes color-scheme; older GNOME exposes gtk-theme.
    var colorScheme = ReadGSettings("org.gnome.desktop.interface", "color-scheme");
    if (!string.IsNullOrEmpty(colorScheme))
        return colorScheme.Contains("dark", StringComparison.OrdinalIgnoreCase);

    var gtkTheme = ReadGSettings("org.gnome.desktop.interface", "gtk-theme");
PlatformHelper.cs:396-399 (portal: 0 = no preference falls through, unlike 'default' above)
```

**Why it is a bug:** The color-scheme key exists wherever gsettings-desktop-schemas 42 or later is installed, whatever the desktop, and its default value is 'default' (no preference). Any non-empty value returns, so the gtk-theme, xdg-desktop-portal and kdeglobals probes are unreachable on those systems. The portal probe itself treats "no preference" as a fall-through, so the two probes disagree. On Plasma X11, kde-gtk-config syncs through xsettingsd rather than GSettings (https://github.com/KDE/kde-gtk-config). The AUDIT.md L24 verifier noted that 'default' yields LIGHT, and that residual case is still present.

**Impact:** With the "System" theme, dark KDE X11, Xfce, Cinnamon or MATE desktops, and GNOME users on a legacy dark gtk-theme, get Noctis's Light theme. Cosmetic. Needs testing on Linux.

**Proposed fix (trivial):** Return true only for 'prefer-dark' and false only for 'prefer-light'. On 'default' (or any other value), fall through to the gtk-theme, portal and kdeglobals probes.

**Verifier votes**

- **confirmed** (refute lens): The code at PlatformHelper.cs:386-388 is as quoted. `ReadGSettings(... "color-scheme")` returns any non-empty value, and anything without "dark" in it (including GSettings' stock 'default') returns false (Light) right away. So the gtk-theme (390-392), portal (397-399) and kdeglobals (403-405) probes are never reached. That contradicts the portal probe, which treats 0 ('no preference') as a fall-through. It is reachable from SettingsViewModel.cs:3537 (`IsSystemTheme ? IsSystemDarkMode() ...`). The effect is cosmetic and limited to Linux: a GNOME user on a legacy dark gtk-theme, or a non-GNOME desktop whose gsettings key stays 'default', gets the Light theme.

### P14 — macOS launch at login saves whatever bundle path the app is running from (App Translocation, a mounted DMG, Downloads) and keeps showing ON after that path is gone

- **Location:** `src/Noctis/Helpers/StartupHelper.cs:129` (also: `src/Noctis/Helpers/StartupHelper.cs:40`, `src/Noctis/Helpers/StartupHelper.cs:160`)
- **Area / sweep:** Cross-platform / xplat-mac · **Category:** platform-integration · **Platforms:** macos
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
src/Noctis/Helpers/StartupHelper.cs:40: `if (OperatingSystem.IsMacOS()) return File.Exists(MacAgentPath);`
StartupHelper.cs:129-130, 144-147:
        var appPath = MacAppBundlePath();
        if (string.IsNullOrEmpty(appPath)) return false;
            "    <string>/usr/bin/open</string>\n" +
            $"    <string>{System.Security.SecurityElement.Escape(appPath)}</string>\n" +
StartupHelper.cs:160-169: MacAppBundlePath walks up from Environment.ProcessPath to the first '*.app' directory. Nothing checks for /private/var/folders/…/AppTranslocation/ or /Volumes/.
```

**Why it is a bug:** The CI ships an ad-hoc-signed .app in a .zip and a .dmg. If a user runs the quarantined app from the folder it was unzipped into, Gatekeeper App Translocation runs it from a random, read-only /private/var/folders/…/AppTranslocation/<UUID>/d/Noctis.app path that disappears after quit or reboot (https://lapcatsoftware.com/articles/app-translocation.html, https://eclecticlight.co/2023/05/09/what-causes-app-translocation/). If the user runs it straight from the mounted DMG, the path is under /Volumes. Either path gets written into the LaunchAgent, so at the next login `open <path>` fails and Noctis does not start. IsEnabled only checks that the plist exists, so the Settings toggle still shows ON.

**Impact:** 'Launch at login' silently does nothing for users who have not moved the app with Finder (for example into /Applications), while the toggle says it is on. Needs testing on macOS.

**Proposed fix (small):** In MacSet, refuse to register (return false with a hint to move Noctis to Applications) when appPath contains '/AppTranslocation/' or starts with '/Volumes/'. In IsEnabled, parse the plist's bundle path and return false, or re-register, when that path no longer exists.

**Verifier votes**

- **confirmed** (refute lens): StartupHelper.cs:40: IsEnabled on macOS only checks File.Exists(MacAgentPath). MacSet (129-151) writes whatever MacAppBundlePath() (160-169) returns, which is the first *.app found walking up from ProcessPath. Nothing checks for /Volumes/ or /AppTranslocation/, and nothing checks later that the saved path still exists. The only callers are SettingsViewModel.cs:870-871 (toggle) and 2319 (read), so the entry is never re-registered or checked again at startup. Two things narrow it: README.md:211-215 tells zip users to run `xattr -dr com.apple.quarantine`, which prevents translocation, and toggling the setting off and on again re-registers the current path. It still happens for users who run from the mounted DMG, who approve with 'Open Anyway' instead of running xattr, or who move the bundle after enabling. Launch at login then fails silently and the toggle still shows ON. Edge case, so low.

### P18 — macOS Now Playing always publishes playback rate 1.0 and ignores speed changes, so the Control Center position drifts at 0.75×–2×

- **Location:** `src/Noctis/Services/MacNowPlayingService.cs:139` (also: `src/Noctis/ViewModels/PlayerViewModel.cs:177`)
- **Area / sweep:** Cross-platform / xplat-mac · **Category:** correctness · **Platforms:** macos
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
src/Noctis/Services/MacNowPlayingService.cs:107-111:
        if (e.PropertyName is nameof(PlayerViewModel.State)
            or nameof(PlayerViewModel.CurrentTrack)
            or nameof(PlayerViewModel.CurrentArtPath))
        {
            OnUiThread(UpdateNowPlaying);
MacNowPlayingService.cs:138-139:
            SetDouble(dict, _keyElapsed, _player.Position.TotalSeconds);
            SetDouble(dict, _keyRate, _player.IsPlaying ? 1.0 : 0.0);
src/Noctis/ViewModels/PlayerViewModel.cs:177-179: `partial void OnPlaybackRatePercentChanged(int value) { _audioPlayer.SetPlaybackRate(value / 100.0); …}`
```

**Why it is a bug:** As the class comment says (117-119), macOS extrapolates the live position from elapsed + rate. The island speed menu really changes the playback speed (PlaybackRatePercent 75–200 → SetPlaybackRate), but the rate published to MPNowPlayingInfoCenter is always 1.0, and a speed change does not trigger an update. Scenario: at 1.5× for one minute, audio has advanced 90 s but Control Center shows 60 s, and the gap keeps growing until the next pause or seek.

**Impact:** The Control Center / Now Playing scrubber and elapsed time are wrong whenever speed ≠ 1×. Scrubbing there still works, because it seeks by absolute time. Needs testing on macOS.

**Proposed fix (trivial):** Publish `_player.IsPlaying ? _player.PlaybackRate : 0.0` and add nameof(PlayerViewModel.PlaybackRatePercent) to the property filter, so the elapsed time is re-anchored when the speed changes.

**Verifier votes**

- **confirmed** (refute lens): MacNowPlayingService.cs:139 publishes `_player.IsPlaying ? 1.0 : 0.0` as the rate. The property filter at :107-109 covers only State, CurrentTrack and CurrentArtPath, plus Seeked at :115, so a change to PlaybackRatePercent never triggers an update. The doc comment at :117-119 says macOS extrapolates the position from elapsed + rate. PlayerViewModel.cs:166-181 has PlaybackRatePercent (75-200) with a PlaybackRate getter and calls _audioPlayer.SetPlaybackRate. VlcAudioPlayer.cs:2275-2288 applies the rate on every output path. The speed menu is user-reachable (PlaybackBarView.axaml:500-539). Control Center drifts until the next state change, seek or track change. The proposed fix is accurate, since PlaybackRate exists at PlayerViewModel.cs:173.

### P19 — macOS Now Playing never shows album art: MPMediaItemArtwork initWithImage: does not exist on macOS, so the probe always skips the art

- **Location:** `src/Noctis/Services/MacNowPlayingService.cs:174` (also: `src/Noctis/Services/MacNowPlayingService.cs:141`)
- **Area / sweep:** Cross-platform / xplat-mac · **Category:** platform-integration · **Platforms:** macos
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
src/Noctis/Services/MacNowPlayingService.cs:170-175:
        // initWithImage: is the block-free initializer; it is deprecated in favor of
        // initWithBoundsSize:requestHandler:, so probe for it rather than risking an
        // unrecognized-selector NSException (which would abort the process).
        var artworkClass = GetClass("MPMediaItemArtwork");
        if (!MsgSendBool(artworkClass, Sel("instancesRespondToSelector:"), Sel("initWithImage:")))
            return IntPtr.Zero;
Apple's documentation data (developer.apple.com/tutorials/data/documentation/mediaplayer/mpmediaitemartwork/init(image:).json) lists init(image:) for iOS, iPadOS, Mac Catalyst, tvOS, visionOS and watchOS, and not macOS. init(boundsSize:requestHandler:) is listed for macOS 10.12.2+.
```

**Why it is a bug:** On macOS, MPMediaItemArtwork only offers the block-based initializer. The Campfire project hit the same wall and built a global block literal for initWithBoundsSize:requestHandler: (https://github.com/r0adkll/Campfire/pull/1066). Because the probe returns NO, GetArtwork returns IntPtr.Zero for every track and MPMediaItemPropertyArtwork is never set.

**Impact:** Control Center and the lock-screen Now Playing widget show title and artist but no cover. Needs testing on macOS.

**Proposed fix (medium):** Create the artwork with initWithBoundsSize:requestHandler:. Build a global block (isa=_NSConcreteGlobalBlock, flags BLOCK_IS_GLOBAL, invoke = an [UnmanagedCallersOnly] function taking (block, CGSize) and returning the cached NSImage*) and keep it rooted for the process lifetime. Keep the current probe as a fallback.

**Verifier votes**

- **confirmed** (refute lens): MacNowPlayingService.cs:170-175 matches: GetArtwork probes `instancesRespondToSelector: initWithImage:` and returns IntPtr.Zero when it fails. Only then does :181 call initWithImage:, and :141-143 sets the artwork only when it is non-zero. Apple's documentation data for MPMediaItemArtwork init(image:) (fetched) lists iOS 5-10, iPadOS, Mac Catalyst 13.1, tvOS, visionOS and watchOS, with no macOS. The Campfire PR #1066 (fetched) says 'MPMediaItemArtwork on macOS only offers the block-based initializer'. The probe therefore returns NO on macOS and no cover is ever published. The one residual unknown is whether a private runtime implementation of the selector exists, but both the docs and third-party experience say it does not. No crash, cosmetic only: low.

### P20 — MPRIS never sends PropertiesChanged for Shuffle or LoopStatus, so desktop widgets and playerctl show stale repeat/shuffle state

- **Location:** `src/Noctis/Services/MprisService.cs:192` (also: `src/Noctis/Services/MprisService.cs:120`, `src/Noctis/Services/MprisService.cs:272-315`)
- **Area / sweep:** Cross-platform / xplat-linux · **Category:** correctness · **Platforms:** linux
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
MprisService.cs:190-209
    switch (e.PropertyName)
    {
        case nameof(PlayerViewModel.State): ...
        case nameof(PlayerViewModel.CurrentTrack): ...
        case nameof(PlayerViewModel.CurrentArtPath): ...
        case nameof(PlayerViewModel.Volume):
            EmitPropertiesChanged(statusChanged: false, metadataChanged: false, volumeChanged: true);
            break;
    }
(no IsShuffleEnabled / RepeatMode case; EmitPropertiesChanged has no Shuffle/LoopStatus entry)
```

**Why it is a bug:** The MPRIS Player spec says for both LoopStatus and Shuffle: "When this property changes, the org.freedesktop.DBus.Properties.PropertiesChanged signal is emitted with the new value." (https://specifications.freedesktop.org/mpris/latest/Player_Interface.html). Clients cache the values from the first GetAll and update them only from this signal.

**Impact:** After you change repeat or shuffle in Noctis, the KDE Plasma media widget, GNOME extensions and `playerctl --follow loop/shuffle` keep showing the old value. Widgets that compute the next value from their cache (toggle shuffle, cycle loop) keep sending the same value, so their buttons appear stuck (likely). Needs testing on Linux.

**Proposed fix (small):** Add cases for nameof(PlayerViewModel.IsShuffleEnabled) and nameof(PlayerViewModel.RepeatMode). Extend EmitPropertiesChanged with shuffleChanged/loopChanged flags that write "Shuffle" (WriteVariantBool) and "LoopStatus" (WriteVariantString, using the mapping in WriteProperty). Also emit both in the post-connect burst at line 120.

**Verifier votes**

- **confirmed** (refute lens): The OnPlayerPropertyChanged switch at MprisService.cs:190-210 handles only State, CurrentTrack, CurrentArtPath and Volume, with no IsShuffleEnabled or RepeatMode case, although both are [ObservableProperty] (PlayerViewModel.cs:106-107). EmitPropertiesChanged at :272-315 can only write PlaybackStatus, Metadata and Volume. Shuffle and LoopStatus are served as readwrite properties (:615-629 setters, :683-697 getters, introspection :765/:767 with no emits-changed-signal annotation, so the default 'true' applies). The spec therefore requires PropertiesChanged when they change, and changes made inside Noctis are never signalled. A client's own Set also does not echo back through a signal. The post-connect burst at :120 omits them too. Location and fix are accurate.

### P21 — MPRIS Raise skips ShowFromTray: it can show the main window next to the Topmost mini player and doesn't restore ShowInTaskbar

- **Location:** `src/Noctis/Services/MprisService.cs:375` (also: `src/Noctis/Views/MainWindow.axaml.cs:1019-1039`)
- **Area / sweep:** Cross-platform / xplat-linux · **Category:** correctness · **Platforms:** linux
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
MprisService.cs:368-379
    private void RaiseWindow()
    {
        OnUiThread(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is { } window)
            {
                window.Show();
                window.Activate();
            }
MainWindow.axaml.cs:1021-1035 ShowFromTray(): closes _miniPlayer first ("surfacing the main window without closing it left the user with both on screen"), sets ShowInTaskbar = true, un-minimizes.
```

**Why it is a bug:** The desktop's MPRIS widget calls Raise when you click the player entry. That path skips ShowFromTray, which was written specifically to avoid showing both windows. While the mini player is open, _miniPlayer stays non-null, and the close-to-tray/minimize-to-tray conditions (`_miniPlayer == null`) then take the wrong branch. After a start-minimized login, ShowInTaskbar stays false.

**Impact:** Clicking Noctis in the GNOME/KDE media widget while the mini player is open shows both windows, with the Topmost mini player floating over the main one. Closing the main window then quits the app instead of going to tray. After a login start the raised window has no taskbar entry and stays minimized. Needs testing on Linux.

**Proposed fix (trivial):** Make MainWindow.ShowFromTray internal. In RaiseWindow, call `((Noctis.Views.MainWindow)window).ShowFromTray()` when the main window is a MainWindow, and fall back to Show/Activate otherwise.

**Verifier votes**

- **confirmed** (refute lens): MprisService.cs:368-379 RaiseWindow only calls window.Show()/Activate() and bypasses MainWindow.axaml.cs:1019-1039 ShowFromTray, which closes _miniPlayer first and sets ShowInTaskbar=true. ToggleMiniPlayer (MainWindow.axaml.cs:119-137) hides the main window while _miniPlayer stays non-null, so a Raise from the MPRIS widget shows both windows. Close-to-tray (:1045-1050) and minimize-to-tray (:775-781) both require _miniPlayer == null, so in that state a close falls through to a real close. App.axaml.cs:171-175 sets ShowInTaskbar=false and WindowState=Minimized at a login start, and Raise never restores either. CanRaise is true (MprisService.cs:664). This only affects Linux and only an uncommon path, so low is right.

### P23 — Send to Folder (flat mode) keeps the source filename as-is, so Linux/macOS names containing : ? * " < > | fail on FAT32/exFAT/NTFS targets

- **Location:** `src/Noctis/Services/SendToFolderService.cs:72` (also: `src/Noctis/Services/FileOrganizePlanner.cs:124`)
- **Area / sweep:** Cross-platform / xplat-general · **Category:** cross-platform · **Platforms:** linux, macos
- **Verification:** confirmed (finder confidence: likely; finder severity: low)

**Evidence**

```
var baseTarget = organized is not null && organized.TryGetValue(track.Id, out var organizedPath)
    ? organizedPath
    : Path.GetFullPath(Path.Combine(targetRoot, Path.GetFileName(source)));
```

**Why it is a bug:** Pattern mode goes through FileOrganizePlanner, which deliberately adds the Windows-invalid set because 'Linux only bans /' (FileOrganizePlanner.cs:126-128). Flat mode copies the ext4/APFS filename verbatim. Names such as 'What's Up?.flac' or 'Intro: Part 1.mp3' are legal on the source filesystem but rejected on the FAT32/exFAT/NTFS USB sticks and DAPs this feature is meant for (https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file). File.Copy then throws and the item is counted as failed (lines 163-167).

**Impact:** On Linux and macOS, 'Send to Folder' without a pattern fails for every track whose filename contains one of those characters.

**Proposed fix (trivial):** In flat mode, sanitize Path.GetFileName(source) with the same character set FileOrganizePlanner.Sanitize uses (make it internal and reuse it), keeping the extension.

**Verifier votes**

- **confirmed** (refute lens): SendToFolderService.cs:70-72 uses Path.GetFileName(source) verbatim in flat mode. Only the pattern path goes through FileOrganizePlanner.Sanitize, whose InvalidChars adds <>:"/\|?* on every OS (FileOrganizePlanner.cs:124-130). CopyAsync (:153-167) catches the File.Copy failure and counts the item as failed. On Linux, the vfat and exfat drivers reject those characters, so the failure is real for the FAT32/exFAT targets typical of this feature. The NTFS part is overstated: on Linux, ntfs-3g and ntfs3 accept them unless mounted with windows_names. Low severity stands.

### S01 — Metadata-schema backfill saves a startup snapshot of settings.json after a multi-minute pass, reverting changes made meanwhile

- **Location:** `src/Noctis.Core/Services/LibraryService.cs:2133` (also: `src/Noctis.Core/Services/LibraryService.cs:2058`, `src/Noctis.Core/Services/LibraryService.cs:1510`, `src/Noctis/ViewModels/LyricsViewModel.cs:879`, `src/Noctis/ViewModels/SettingsViewModel.cs:2621`)
- **Area / sweep:** Settings, dialogs, popups, commands / settings · **Category:** persistence-race · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)
- **Prior audit:** AUDIT_2026-07-24.md §3 MEDIUM "Read-modify-write on settings.json loses concurrent user setting changes" (still present for the backfill path)

**Evidence**

```
  2058:            settings = await _persistence.LoadSettingsAsync();
  ... backfills v2..v10, e.g. 2121: didBackfillMetadata |= await BackfillTrackArtworkAsync(_tracks);
  2129:        settings.MetadataSchemaVersion = CurrentMetadataSchemaVersion;
  2133:            await _persistence.SaveSettingsAsync(settings);
```

**Why it is a bug:** EnsureMetadataSchemaUpToDateAsync (called from LibraryService.cs:1510 at startup) loads the whole AppSettings. It then runs the backfills; v10 re-reads every file's embedded cover, which takes minutes on a large or HDD library. Finally it writes that stale document back in full. Anything another component wrote to settings.json during the pass is overwritten: ExcludedFilePaths from a track removal (LibraryService.cs:1402-1458), and LyricsBackgroundColorHex / LyricsShowArtworkBackground from the lyrics page (LyricsViewModel.cs:879-882, 941-943). SettingsViewModel's merge (SettingsViewModel.cs:2621-2630) then adopts the reverted on-disk values, so they are never restored. VM-owned fields are also reverted on disk until SettingsViewModel's next save.

**Impact:** This happens once after each upgrade that bumps the schema. A track the user removes during the backfill (with 'Keep files') loses its exclusion, and the next scan re-imports it. A lyrics background colour or mode chosen during that window also reverts.

**Proposed fix (small):** Do not hold the snapshot. After the backfill, reload the current settings immediately before saving and change only MetadataSchemaVersion. A better fix is to add PersistenceService.UpdateSettingsAsync(Action<AppSettings>), which does load, mutate and save under the per-file gate, and to use it for all partial writers (LibraryService, LyricsViewModel).

**Verifier votes**

- **confirmed** (refute lens): The code matches the finding. LibraryService.cs:2058 loads the whole AppSettings once. :2082-2121 then run the backfills, and v10 BackfillTrackArtworkAsync (:959-999) extracts the embedded art of every local file in a Parallel.ForEach, which takes minutes on a large library. :2129-2133 then stamps MetadataSchemaVersion and calls SaveSettingsAsync with that same stale object. The pass runs in the fire-and-forget background task (:1505-1510), so the UI stays live and other writers can save meanwhile: ExcludeFilePathsAndCleanFoldersAsync (:1402/:1458, called from RemoveTrack(s)Async :1297/:1314), ImportFilesAsync (:1053-1059), and LyricsViewModel (:879-882, :941-943). Nothing serializes these writes. SettingsViewModel.MergeExternalSettingChangesAsync (:2621-2629) then copies every non-placement property from disk, so the reverted ExcludedFilePaths and lyrics fields are adopted and never restored. VM-owned fields come back through SyncToSettings on the next VM save. I downgraded to low: the window opens only once per schema bump, a specific user action must land inside it, and the consequences are mild (one removed track re-imported, a lyrics colour reverted).

### S02 — A corrupt or unreadable settings.json is never surfaced to the user (SettingsLoadFailed has no reader); any read exception counts as corruption

- **Location:** `src/Noctis.Core/Services/PersistenceService.cs:119` (also: `src/Noctis.Core/Services/IPersistenceService.cs:25`, `src/Noctis.Core/Services/PersistenceService.cs:506`, `src/Noctis/ViewModels/SettingsViewModel.cs:2621`)
- **Area / sweep:** Settings, dialogs, popups, commands / settings · **Category:** error-handling · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)
- **Prior audit:** AUDIT_2026-07-24.md §2 MEDIUM "A corrupt or unreadable settings.json silently resets everything" (quarantine + .bak fixed; user notice still missing)

**Evidence**

```
  117:        if (outcome == LoadOutcome.Corrupt)
  118:        {
  119:            SettingsLoadFailed = true;
  120:            var quarantined = QuarantineCorruptFile(SettingsPath);
  121:            loaded = await TryParseAsync<AppSettings>(SettingsBackupPath);
LoadJsonWithOutcomeAsync, :506-511:
  catch (Exception ex)
  { ... return (null, LoadOutcome.Corrupt); }
`grep SettingsLoadFailed` over src/ matches only IPersistenceService.cs:25 and PersistenceService.cs:119/143, i.e. there is no consumer.
```

**Why it is a bug:** The recovery itself works: the damaged file is renamed to settings.json.corrupt-<stamp> and settings.json.bak is tried. But the flag documented as the signal (IPersistenceService.cs:19-24) is never read, so the only trace is a DebugLog line. Separately, every exception type, including a transient IOException, is classified as Corrupt. LoadSettingsAsync is also called on every save through MergeExternalSettingChangesAsync (SettingsViewModel.cs:2621), so a transient read failure mid-session quarantines a valid file. Settings then roll back to the one-save-old .bak for fields the VM does not own.

**Impact:** After a parse failure the user sees themes, music folders, EQ and scrobbler logins silently reset, or rolled back to the previous save, with no hint that the old file sits next to it as .corrupt-*.

**Proposed fix (small):** After Settings.LoadAsync in MainWindowViewModel.InitializeAsync, check _persistence.SettingsLoadFailed and show a notice naming the quarantined file and whether .bak was used. In LoadJsonWithOutcomeAsync, treat only JsonException / NotSupportedException as Corrupt, and retry or return a distinct outcome for IOException / UnauthorizedAccessException.

**Verifier votes**

- **confirmed** (refute lens): A grep of src/ finds SettingsLoadFailed only at PersistenceService.cs:119 (set) and :143, and IPersistenceService.cs:25 (declaration). All other hits are test stubs, so no UI consumer exists. The recovery itself works: :117-128 quarantine the file, parse .bak and write DebugLog. LoadJsonWithOutcomeAsync :500-512 does map every exception to LoadOutcome.Corrupt, IOException included. The mid-session quarantine scenario is weak, though. In-process writers write to .tmp and rename atomically, and File.Copy only reads, which is compatible with the reader's FileShare.Read. So a transient read failure needs an external process holding the file exclusively, which is rare. Recovery works and is logged, so this is a low-severity missing notice plus an over-broad catch.

### S03 — settings.json replace (File.Move overwrite) fails whenever another handle has the file open, and the failure is dropped with no retry or log

- **Location:** `src/Noctis.Core/Services/PersistenceService.cs:602` (also: `src/Noctis.Core/Services/PersistenceService.cs:502`, `src/Noctis.Core/Services/PersistenceService.cs:155`, `src/Noctis.Core/Services/PersistenceService.cs:566`, `src/Noctis/ViewModels/SettingsViewModel.cs:2557`, `src/Noctis/ViewModels/LyricsViewModel.cs:884`, `src/Noctis.Core/Services/LibraryService.cs:135`)
- **Area / sweep:** Settings, dialogs, popups, commands / settings · **Category:** persistence-race · **Platforms:** windows
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
Readers (not gated) keep the target open with share mode Read only, PersistenceService.cs:502-503:
  await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
      FileShare.Read, bufferSize: 65536, useAsync: true);
The per-file gate (:566-577) serializes writers only. The writer then replaces the file, :602:
  File.Move(tempPath, path, overwrite: true);
The .bak copy runs before the gate is entered (:155-157: File.Copy(SettingsPath, SettingsBackupPath, overwrite: true);).
SettingsViewModel.SaveAsync swallows the exception, SettingsViewModel.cs:2557-2560:
  catch (Exception ex)
  {
      Debug.WriteLine($"[SettingsViewModel] Failed to save settings: {ex.Message}");
  }
```

**Why it is a bug:** settings.json has many independent readers that do not take the write gate: LibraryService.ScanCoreAsync (LibraryService.cs:135, every scan), ImportFilesAsync (:1053), ExcludeFilePathsAndCleanFoldersAsync (:1402), LyricsViewModel.SearchLyrics (LyricsViewModel.cs:1119, each lyrics lookup), LibraryRemovalHelper (:358, :393), NavidromeSyncService (:26), and SettingsViewModel's own merge-read (SettingsViewModel.cs:2621). A concurrent SaveSettingsAsync from another component also runs its .bak File.Copy outside the gate. On Windows, File.Move(src, dst, overwrite:true) (MoveFileEx REPLACE_EXISTING) fails with 'Access denied' when any other handle holds the destination open. It fails even when that handle was opened with FileShare.Delete, and antivirus scanners that briefly open a freshly written file trigger it too: https://github.com/dotnet/runtime/issues/114230. Scenario: the user flips a Settings toggle while a scan starts, or while a lyrics search reads settings.json. The toggle's rename hits the open read handle and throws UnauthorizedAccessException. SaveAsync then logs only through Debug.WriteLine, which is compiled out in Release, and the change is not on disk. LyricsViewModel's own saves (LyricsViewModel.cs:882/943) swallow the same failure with catch { }. Nothing retries.

**Impact:** A setting change sometimes never reaches disk and reverts on the next launch. Nothing appears in the session log or crash.log. This is more likely during bursts of saves, e.g. the non-debounced sliders or a Windows Defender scan of the just-written file. If the final shutdown save hits it, the session's last changes (volume, window geometry, last toggle) are lost.

**Proposed fix (small):** In SaveJsonSerializedAsync, retry the File.Move 3-5 times with a short backoff (about 20-50 ms) on IOException or UnauthorizedAccessException before rethrowing. Take the settings .bak File.Copy inside the same per-file gate. Open readers with FileShare.ReadWrite | FileShare.Delete. Log the final failure with DebugLog.Write("Persistence", ...) at the single choke point (PersistenceService.cs:615) so every caller's failure is visible.

**Verifier votes**

- **confirmed** (refute lens): The code is as described. Readers open with FileShare.Read and no FileShare.Delete (PersistenceService.cs:502-503, :520-521). The writer does File.Move(tempPath, path, overwrite:true) at :602 with no retry, and :604-616 only calls Debug.WriteLine, which is [Conditional("DEBUG")], before rethrowing. The .bak File.Copy (:155-157) runs outside the per-file gate (:566-577). SettingsViewModel.SaveAsync :2557-2560 swallows the exception with Debug.WriteLine only, and LyricsViewModel :884/:945 use catch { }. The ungated readers exist at LibraryService.cs:135/:1053/:1402, LyricsViewModel.cs:1119, LibraryRemovalHelper.cs:358/:393 and NavidromeSyncService.cs:26. A replace-rename on Windows cannot supersede a target that is open without delete sharing, so the failure is real. I downgraded to low. The collision window is the few milliseconds a small JSON read takes. SettingsViewModel's own merge-read and write are sequential under _saveLock. A failed VM save heals on the next VM save, including the unconditional save at close, because SyncToSettings re-applies VM-owned fields. Permanent loss needs the final save to collide, or a one-shot Lyrics/Library write to collide.

### S05 — Command palette 'Go to Settings' shows Settings as an inline page whose close (X) button does nothing, bypassing the modal

- **Location:** `src/Noctis/ViewModels/CommandPaletteViewModel.cs:104` (also: `src/Noctis/ViewModels/MainWindowViewModel.cs:1958`, `src/Noctis/Views/SettingsView.axaml:1301`)
- **Area / sweep:** Settings, dialogs, popups, commands / commands · **Category:** wiring · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
CommandPaletteViewModel.cs:91 / :104:
    Execute = () => _main.NavigateCommand.Execute(key),
    Page("Go to Settings", "settings", "SettingsIcon");
MainWindowViewModel.cs:1958 (Navigate):
    "settings" => RefreshAndReturnSettings(),
The sidebar path intercepts it instead (MainWindowViewModel.cs:1895-1897):
    if (key == "settings") { OpenSettings(); ... return; }
SettingsView.axaml:1301 close button:
    Command="{Binding $parent[Window].((vm:MainWindowViewModel)DataContext).CloseSettingsCommand}"
```

**Why it is a bug:** Settings became a modal overlay (OpenSettings, MainWindowViewModel.cs:47-57). The palette still calls the page-navigation command, which sets CurrentView = Settings, so CachedViewLocator creates a second SettingsView in the content area. That view's X runs CloseSettings, which sets IsSettingsModalOpen=false (already false), so nothing happens. The page also skips the background RefreshPlaylistCountAsync that OpenSettings runs.

**Impact:** After Ctrl+K and 'Go to Settings', the user gets an embedded Settings page that the X button cannot close. It can only be left by clicking another section.

**Proposed fix (trivial):** Route the palette's 'settings' entry to `_main.OpenSettingsCommand.Execute(null)`, for example with a special case in Page(). Optionally make Navigate("settings") open the modal as well.

**Verifier votes**

- **confirmed** (refute lens): CommandPaletteViewModel.cs:91,104 calls _main.NavigateCommand.Execute("settings"). NavigateCommand is the [RelayCommand] Navigate at MainWindowViewModel.cs:1910-1911, so it skips the sidebar-only intercept in OnNavigationRequested (:1895-1906). Navigate sets CurrentView = RefreshAndReturnSettings() (:1958, :2104-2113), which does not run RefreshPlaylistCountAsync; OpenSettings does (:55). App.axaml.cs:54 maps SettingsViewModel to a cached SettingsView, and MainWindow.axaml:689 hosts CurrentView, so a second inline SettingsView appears; the modal host builds its own at MainWindow.axaml.cs:718-724. That view's X (SettingsView.axaml:1301) runs CloseSettings, which only sets IsSettingsModalOpen=false (:61). The value is already false, so nothing happens. Escape also checks IsSettingsModalOpen (MainWindow.axaml.cs:1649). Nothing converts CurrentView==Settings into the modal: OnCurrentViewChanged at :1373 has no such branch.

### S06 — Command palette: pressing Enter right after typing runs the previous result list's top row

- **Location:** `src/Noctis/ViewModels/CommandPaletteViewModel.cs:282` (also: `src/Noctis/Views/CommandPaletteDialog.axaml.cs:50`)
- **Area / sweep:** Settings, dialogs, popups, commands / dialogs · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
// CommandPaletteViewModel.cs:58-65
partial void OnQueryChanged(string value)
{ ... _ = DebouncedRefreshAsync(cts.Token); }   // 200 ms delay, then Task.Run library scan
// :279-284
private void ExecuteSelected()
{   if (SelectedIndex >= 0 && SelectedIndex < Results.Count)
        ExecuteItem(Results[SelectedIndex]);
}
```

**Why it is a bug:** Results update 200 ms after the last keystroke, plus the time of a background library scan (Refresh 141-251). Enter (CommandPaletteDialog.axaml.cs:50-53) runs whichever row is selected at that moment. Pasting or quickly typing a song name and pressing Enter runs the old list's first row. In a freshly opened palette that row is the first static item, 'Go to Home' (line 94).

**Impact:** Ctrl+K, type, Enter can navigate or play the wrong thing.

**Proposed fix (small):** On Enter, if a refresh is still pending (the debounce CTS is live or a generation is in flight), cancel the debounce, await Refresh() for the current Query, and then run the first result.

**Verifier votes**

- **confirmed** (refute lens): OnQueryChanged (CommandPaletteViewModel.cs:58-66) only starts a 200 ms debounce (:55, :72), and Refresh then awaits Task.Run (:173) before replacing Results. Enter (CommandPaletteDialog.axaml.cs:50-53) calls ExecuteSelected (:280-284) on whatever Results holds at that moment. QueryBox binds Query with no delay (CommandPaletteDialog.axaml:38). The constructor fills Results with the static items and SelectedIndex=0 (:46, :150-152), so that row is 'Go to Home' (:94). Pasting or typing and pressing Enter within about 200 ms therefore runs a stale row.

### S08 — The track menu's 'Favorites' / 'Remove from Favorites' label follows the clicked row, but the command flips each selected track: on a mixed selection, 'Favorites' unfavorites the ones already favorited

- **Location:** `src/Noctis/ViewModels/LibrarySongsViewModel.cs:383` (also: `src/Noctis/ViewModels/PlaylistViewModel.cs:839`, `src/Noctis/Helpers/TrackContextMenuBuilder.cs:376`)
- **Area / sweep:** Settings, dialogs, popups, commands / commands · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
TrackContextMenuBuilder.cs:374-380:
    Favorite.Command = toggleFavoriteCommand;
    Favorite.IsVisible = !track.IsFavorite;
    Unfavorite.IsVisible = track.IsFavorite;
LibrarySongsViewModel.cs:383-385:
    var tracks = CtrlSelectedTracks.Count > 0 ? CtrlSelectedTracks : new List<Track> { track };
    foreach (var t in tracks)
        t.IsFavorite = !t.IsFavorite;
```

**Why it is a bug:** The menu shows either 'Favorites' (add) or 'Remove from Favorites' based only on the clicked track. The bulk command then inverts each selected track on its own. Scenario: select A (a favorite) and B (not a favorite), right-click B and pick 'Favorites'. B becomes a favorite and A loses its favorite.

**Impact:** Favorites are silently removed from songs the user meant to add. The Favorites page and favorite counts change unexpectedly.

**Proposed fix (trivial):** Compute one target state from the clicked row (`var newState = !track.IsFavorite;`) and apply it to every track in the set. Same in PlaylistViewModel.ToggleFavorite (:839-841).

**Verifier votes**

- **confirmed** (refute lens): TrackContextMenuBuilder.cs:374-380 shows Favorite or Unfavorite based only on the clicked track's IsFavorite, and both items bind the same toggle command. LibrarySongsViewModel.cs:383-385 (and PlaylistViewModel.cs:839-841) run `t.IsFavorite = !t.IsFavorite` on each selected track, so a selection with mixed states inverts each track separately. The album version computes a single newState per album (LibraryAlbumsViewModel.cs:1117), which shows the intended add/remove behavior. Right-clicking non-favorite B inside a selection {A (favorite), B} and choosing 'Favorites' unfavorites A.

### S09 — The LRC editor stamps and seeks against whatever song is playing, even after playback has moved on to the next song

- **Location:** `src/Noctis/ViewModels/LrcEditorViewModel.cs:194` (also: `src/Noctis/ViewModels/LrcEditorViewModel.cs:170`, `src/Noctis/ViewModels/LrcEditorViewModel.cs:232`)
- **Area / sweep:** Settings, dialogs, popups, commands / dialogs · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
// LrcEditorViewModel.cs:191-197
private void StampCurrent()
{   if (SelectedIndex < 0 || SelectedIndex >= Lines.Count) return;
    Lines[SelectedIndex].Timestamp = _player.Position;
    if (SelectedIndex < Lines.Count - 1)
        SelectedIndex++;
}
// :238 _player.SeekToPositionCommand.Execute(target.TotalSeconds / _player.Duration.TotalSeconds);
```

**Why it is a bug:** The editor saves to _track but takes positions from the global player without checking that _player.CurrentTrack is still _track. If the song ends while the user is still stamping the outro, the queue auto-advances and the remaining stamps are positions in the next song (for example 0:02 and 0:04). BuildLrcPreservingUntimed sorts stamped lines by time (LrcEditorViewModel.cs:170), so those final lines are saved at the top of the .lrc. PlayFromLine (232-241) also seeks inside the wrong song.

**Impact:** The saved LRC can have its last lines timed at the start of the song, and 'play from line' seeks the wrong track.

**Proposed fix (trivial):** In StampCurrent, StampLine and PlayFromLine, add `if (!ReferenceEquals(_player.CurrentTrack, _track)) { StatusText = "A different song is playing"; return; }`.

**Verifier votes**

- **confirmed** (refute lens): LrcEditorViewModel.cs:191-197 StampCurrent and :200-207 StampLine write _player.Position, and :233-241 PlayFromLine seeks the global player. None of them compares _player.CurrentTrack with _track. LyricsViewModel.cs:2990-3016 captures track = _player.CurrentTrack only once, when the editor opens. The dialog (LrcEditorDialog.axaml.cs) is modal, but playback keeps running and Space always calls StampCurrent (:35-38). So after the queue auto-advances, new stamps are positions in the next song. Save then writes them to _track through BuildLrcPreservingUntimed, which sorts stamped lines by time (:170), so late lines move to the top. The finding describes this correctly.

### S10 — The Lyrics Background picker and the Lyrics Studio 'Choose songs' picker scan the whole library on the UI thread on every keystroke, with no debounce

- **Location:** `src/Noctis/ViewModels/LyricsBackgroundPickerViewModel.cs:127` (also: `src/Noctis/ViewModels/LyricsStudioPickerViewModel.cs:104`, `src/Noctis/ViewModels/AddSongsDialogViewModel.cs:88`, `src/Noctis.Core/Helpers/SearchText.cs:43`)
- **Area / sweep:** Settings, dialogs, popups, commands / dialogs · **Category:** ui-thread-perf · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
// LyricsBackgroundPickerViewModel.cs:127
partial void OnSearchTextChanged(string value) => RefreshResults();
// :143-146
foreach (var track in _library.Tracks
             .Where(t => PlaylistViewModel.MatchesSearch(t, query))
             .Take(MaxTrackRows))
    Results.Add(RowForTrack(track));
// LyricsStudioPickerViewModel.cs:104 same, scan at :166-169
```

**Why it is a bug:** Each keystroke synchronously filters every track. When a track's raw text does not contain the query, SearchText.Matches normalizes both the query and the field (Unicode FormD normalization plus a StringBuilder) for Title, Artist and Album (SearchText.cs:43-56), which is about six normalizations per non-matching track. AddSongsDialogViewModel.cs:81-88 documents the same cost ('scanned all 50,000 tracks per keystroke') and added a 250 ms debounce; these two pickers did not get that fix.

**Impact:** Typing stutters in both search boxes on large libraries.

**Proposed fix (small):** Reuse the AddSongsDialogViewModel pattern: a cancellable 250 ms debounce, with the filter run in Task.Run and a generation guard so stale results are dropped.

**Verifier votes**

- **confirmed** (refute lens): LyricsBackgroundPickerViewModel.cs:127 calls RefreshResults synchronously on every SearchText change, and :139-146 filters _library.Albums and _library.Tracks on the calling (UI) thread. LyricsStudioPickerViewModel.cs:104 and :149-169 do the same. SearchText.cs:43-56 normalizes both the query and the source (FormD plus StringBuilder) whenever the raw Contains check fails. PlaylistViewModel.cs:254-260 checks Title, Artist and Album. AddSongsDialogViewModel.cs:81-98 documents this exact cost and adds a 250 ms debounce. Take(Max) cuts the scan short only for broad queries. Narrow queries scan the whole library.

### S11 — Lyric share clip export cannot be cancelled; the still-card export ignores cancellation and a cancelled export leaves a truncated .mp4

- **Location:** `src/Noctis/ViewModels/LyricShareViewModel.cs:852` (also: `src/Noctis/Services/ShareClipRenderer.cs:130`, `src/Noctis/ViewModels/LyricShareViewModel.cs:920`)
- **Area / sweep:** Settings, dialogs, popups, commands / dialogs · **Category:** dialog-lifecycle · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
// LyricShareViewModel.cs:851-852 (no token passed)
var still = await Task.Run(() => ShareCardRenderer.RenderLyricCardStyled(spec));
var (ok, error) = await ShareClipRenderer.RenderAsync(ffmpeg, still, _track.FilePath, outputPath, timing);
// :905-906
/// <summary>Cancels an in-flight clip export. Bound to the dialog's Cancel affordance.</summary>
public void CancelExport() => _exportCts?.Cancel();
```

**Why it is a bug:** CancelExport and CanCancelExport are not bound in LyricShareDialog.axaml; the only binding is the IsRendering spinner at line 397, so there is no cancel button. For the non-karaoke path, _exportCts is never created, so closing the dialog (Detach, line 920) cannot stop ffmpeg either. When a karaoke export is cancelled, ffmpeg is killed (ShareClipRenderer.cs:130), but nothing deletes the partly written outputPath.

**Impact:** The user cannot stop a long export, and a cancelled export leaves an unplayable .mp4 at the chosen path.

**Proposed fix (small):** Create _exportCts before both paths and pass its token to RenderAsync. Bind a Cancel button to CancelExport while IsRendering. In the cancel/failure branches, delete outputPath if it exists.

**Verifier votes**

- **confirmed** (refute lens): Nothing in the source references CancelExport or CanCancelExport except their definitions (LyricShareViewModel.cs:906, :909). LyricShareDialog.axaml binds only IsRendering, at :397, so there is no Cancel button. The still path at :851-852 calls ShareClipRenderer.RenderAsync without a token, and _exportCts is created only in the karaoke branch (:867-868). Closing the dialog (LyricShareDialog.axaml.cs:23-27 -> Detach :920) therefore cannot stop the still export. In the karaoke branch, a cancel during encoding kills ffmpeg (ShareClipRenderer.cs:130). RunFfmpegAsync then returns (false, "cancelled") at :148, and nothing deletes outputPath, so a partial .mp4 is left behind (only frameDir is cleaned up, at :896).

### S12 — Volume Adjust and EQ changes made in the multi-select or album metadata editor are not applied to the song that is playing

- **Location:** `src/Noctis/ViewModels/MetadataHelper.cs:231` (also: `src/Noctis/ViewModels/MetadataHelper.cs:281`, `src/Noctis/ViewModels/MetadataViewModel.cs:2369`)
- **Area / sweep:** Settings, dialogs, popups, commands / dialogs · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
// MetadataHelper.cs:226-233 (multi-select: no ChangesSaved / AnimatedCoverChanging handler)
var vm = new MetadataViewModel(tracks[0], ..., multiSelect: true, ...);
AddUserEqPresets(vm);
var window = new MetadataWindow(vm);
await vm.InitializeAsync();
await ShowDialogOwned(window);
// :281 (single/album path)
if (main.Player.CurrentTrack != track) return;
```

**Why it is a bug:** The Options tab (Volume Adjust, EQ preset) is shown in multi-select mode (MetadataWindow.axaml:1942 has no IsVisible gate), and changes are applied to every selected track (MetadataViewModel.cs:2369-2379). However, the multi-select window never subscribes to ChangesSaved, and the album path compares only the representative track (tracks[0]). Scenario: select several songs including the one playing, or edit the album while track 5 plays, and change Volume Adjust. The value is saved but not heard until the next song starts, whereas a single-track edit applies it immediately (285-288).

**Impact:** The new volume or EQ is not heard on the current song.

**Proposed fix (small):** Move the ChangesSaved handler into a helper shared by both open paths, and apply the change when the edited track list contains main.Player.CurrentTrack.

**Verifier votes**

- **confirmed** (refute lens): MetadataHelper.cs:214-234 (the multi-select path) never subscribes to ChangesSaved or AnimatedCoverChanging. The album path at :236-294 live-applies only when main.Player.CurrentTrack == track (:281), where track is the representative album.Tracks[0] (for example LibraryAlbumsViewModel.cs:1145). MetadataViewModel.cs:2354-2379 fans VolumeAdjust and EqPreset out to every track in _albumTracks. The Options TabItem at MetadataWindow.axaml:1942-1944 has no IsVisible gate. The only other place VolumeAdjust reaches the audio player is PlayerViewModel.cs:2008, at track start. So an edit that includes the playing track, when that track is not tracks[0], is not heard until the next song starts.

### S16 — Release-list refresh is wired to the About tab, but the version manager now lives on the Advanced tab

- **Location:** `src/Noctis/ViewModels/SettingsViewModel.cs:384` (also: `src/Noctis/Views/SettingsView.axaml:4152`, `src/Noctis/Views/SettingsView.axaml:4189`)
- **Area / sweep:** Settings, dialogs, popups, commands / settings · **Category:** settings-wiring · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
  379:        // The GitHub release list is otherwise fetched only when Developer Mode
  ...
  383:        // visible; the old rows stay on screen until the new list arrives.
  384:        if (value == TabAbout && DeveloperMode)
  385:            _ = RefreshReleasesAsync();
The Developer Mode toggle and DevReleases list are inside AdvancedTabPanel (SettingsView.axaml:3934), at :4152-4166 and :4189. AboutTabPanel (:5493-5719) has no release list.
```

**Why it is a bug:** The comment says releases are re-fetched 'whenever the About tab opens with the version manager visible', but the Settings rail revamp (commit 09065c7) moved the version manager to Advanced. Opening Advanced therefore never refreshes the list, and opening About sends an unauthenticated GitHub API request whose result is not shown.

**Impact:** A release published while Noctis runs never appears in Developer Mode's version manager, and never takes the 'Latest' badge, until Developer Mode is toggled or the app restarts. Each About visit spends a GitHub API call (unauthenticated limit 60/h) for nothing.

**Proposed fix (trivial):** Change the condition to `value == TabAdvanced && DeveloperMode`.

**Verifier votes**

- **confirmed** (refute lens): SettingsViewModel.cs:384 reads `if (value == TabAbout && DeveloperMode) _ = RefreshReleasesAsync();`. The Developer Mode toggle (SettingsView.axaml:4157-4166) and the DevReleases ItemsControl (:4189) sit inside AdvancedTabPanel, which runs from :3934 to :4349. AboutTabPanel starts at :5493. DevReleases is bound nowhere else, and the TabAdvanced constant exists (SettingsViewModel.cs:72). One detail is overstated: DevReleases lives on the view model, so opening About first and then Advanced does show the refreshed list. The finding's 'never appears until toggle or restart' is not strictly true. The wiring bug itself is real.

### S17 — Changing any player/lyrics setting snaps every scrolling marquee title back to its start

- **Location:** `src/Noctis/ViewModels/SettingsViewModel.cs:3043` (also: `src/Noctis.UI/Controls/MarqueeTextBlock.cs:426`, `src/Noctis.UI/Controls/MarqueeTextBlock.cs:438`, `src/Noctis/ViewModels/SettingsViewModel.cs:3721`)
- **Area / sweep:** Settings, dialogs, popups, commands / settings · **Category:** ui-glitch · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
ApplyPlayerSettings always ends with:
  3036:        Controls.MarqueeTextBlock.GlobalCoverFlowScrollEnabled = CoverFlowMarqueeEnabled;
  ...
  3043:        Controls.MarqueeTextBlock.NotifyGlobalSettingsChanged();
MarqueeTextBlock.cs:426 and :438-445:
  private void OnGlobalSettingsChanged(object? sender, EventArgs e) => ResetAndRecalc();
  private void ResetAndRecalc()
  {
      StopScrolling();
      _offset = 0;
      _transform.X = 0;
```

**Why it is a bug:** ApplyPlayerSettings is called from about 50 handlers, most unrelated to marquees: the player-bar and track-box opacity sliders on every tick (:3721, :3734), the island button toggles, lyrics toggles, the Kawarp and min-opacity sliders, music video toggles, and lyrics background overrides. The notify is unconditional, so every attached MarqueeTextBlock stops and resets its offset to 0 each time, even though none of the seven marquee statics changed.

**Impact:** While dragging e.g. 'Player bar opacity', or when flipping any island or lyrics setting, the scrolling track and artist titles in the player island, mini player and lyrics page jump back to their start and restart, a visible jitter.

**Proposed fix (trivial):** Compare the seven Global*ScrollEnabled values before and after assignment, and call NotifyGlobalSettingsChanged() only when one of them actually changed.

**Verifier votes**

- **confirmed** (refute lens): SettingsViewModel.cs:3036-3043: ApplyPlayerSettings ends by setting all seven MarqueeTextBlock.Global* statics, then calls NotifyGlobalSettingsChanged() with no check for a change. MarqueeTextBlock.cs:34-37 raises a static event that every attached instance subscribes to (:357), and the handler at :426 calls ResetAndRecalc (:438-445). That method runs StopScrolling, sets _offset=0 and _transform.X=0, then reposts RecalcAndStart. ApplyPlayerSettings has 50 call sites in SettingsViewModel.cs, including the per-tick opacity slider handlers at :3721 and :3734. So every tick of those sliders snaps every scrolling marquee back to its start. The finding describes this correctly, and it is cosmetic.

### S18 — Four newer sliders save settings.json on every value tick instead of using the debounced save

- **Location:** `src/Noctis/ViewModels/SettingsViewModel.cs:3973` (also: `src/Noctis/ViewModels/SettingsViewModel.cs:4315`, `src/Noctis/ViewModels/SettingsViewModel.cs:4218`, `src/Noctis/ViewModels/SettingsViewModel.cs:4224`, `src/Noctis/Views/SettingsView.axaml:2051`, `src/Noctis/Views/SettingsView.axaml:2686`)
- **Area / sweep:** Settings, dialogs, popups, commands / settings · **Category:** performance · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)
- **Prior audit:** AUDIT_2026-07-24.md §2 HIGH "Sliders and the accent colour picker write settings.json on every pointer-move sample" (fixed for the older sliders, re-introduced for these four)
- **Needs runtime check:** yes

**Evidence**

```
  3970:    partial void OnAlbumPageTintStrengthChanged(int value)
  3973:        if (_settingsLoaded) _ = SaveAsync();
  4312:    partial void OnLyricsMinLineOpacityChanged(int value)
  4315:        if (_settingsLoaded) _ = SaveAsync();
  4215:    partial void OnLyricsKawarpWarpChanged(double value) { ApplyPlayerSettings(); if (_settingsLoaded) _ = SaveAsync(); }
  4221:    partial void OnLyricsKawarpBlurChanged(int value)  { ...same... }
The file's own rule, :3167-3169: "Everything driven by a continuous control (sliders, ...) must persist on a trailing edge, not per input sample."
```

**Why it is a bug:** The bound sliders snap per tick (SettingsView.axaml:2051 tint 0-100, :2686 min opacity 0-60, :2577 warp 0-3 in 0.1 steps, :2584 blur 1-16), so one drag fires up to 100 SaveAsync calls. They queue behind _saveLock. Each one re-reads and parses settings.json and DPAPI-unprotects it (MergeExternalSettingChangesAsync :2621), copies about 200 properties by reflection, and runs SyncToSettings. SaveSettingsAsync then does a synchronous File.Copy to .bak plus SerializeToNode and DPAPI protect. These steps run on the UI thread, because SaveAsync's awaits capture the context and PersistenceService's load path has no ConfigureAwait(false). An fsync'd temp write and rename follows. The older sliders were moved to QueueSettingsSave (:3180) for exactly this reason.

**Impact:** Dragging Album page 'Tint strength', 'Minimum line opacity' or the Kawarp sliders causes a backlog of up to 100 full settings writes, UI-thread work bursts during and after the drag, and disk churn, noticeably on an HDD. It also widens the window for the File.Move replace failure in the first finding.

**Proposed fix (trivial):** Replace `_ = SaveAsync()` with `QueueSettingsSave()` in these four handlers. Keep the live apply (ApplyPlayerSettings, and the AlbumDetailViewModel re-blend through PropertyChanged) immediate.

**Verifier votes**

- **confirmed** (refute lens): The four handlers call `_ = SaveAsync()` directly: tint strength at SettingsViewModel.cs:3970/3973, Kawarp warp at :4215, Kawarp blur at :4221 and min line opacity at :4312/:4315. Their sliders have IsSnapToTickEnabled set, with ranges 0-100 (SettingsView.axaml:2051), 0-3 in 0.1 steps (:2577), 1-16 (:2584) and 0-60 (:2686). QueueSettingsSave (:3180) exists, and the comment at :3167-3175 says continuous controls must use it. Each SaveAsync (:2545-2564) takes _saveLock, runs the merge, which calls LoadSettingsAsync and unprotects the secrets (PersistenceService.cs:108-139), runs SyncToSettings, then SaveSettingsAsync. SaveSettingsAsync does a synchronous File.Copy to .bak and SerializeToNode before the async write (:147-180). Its own comment assumes saves are debounced, but these four are not. The cost is performance only, so severity stays low.

### S20 — SyncDeviceId is generated on load but wiped by the first save's merge and never persisted

- **Location:** `src/Noctis/ViewModels/SettingsViewModel.Features.cs:473` (also: `src/Noctis/ViewModels/SettingsViewModel.cs:2595`, `src/Noctis.Core/Services/Sync/LibrarySyncService.cs:79`)
- **Area / sweep:** Settings, dialogs, popups, commands / settings · **Category:** settings-wiring · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
SettingsViewModel.Features.cs:472-473:
  if (string.IsNullOrWhiteSpace(_settings.SyncDeviceId))
      _settings.SyncDeviceId = Guid.NewGuid().ToString("N")[..12];
SaveFeatureSettings (:489-502) does not write SyncDeviceId. The merge copies every property except ProcessOwnedPlacementKeys, SettingsViewModel.cs:2624-2629:
  if (ProcessOwnedPlacementKeys.Contains(prop.Name)) continue;
  prop.SetValue(_settings, prop.GetValue(onDisk));
```

**Why it is a bug:** This is the same trap the code already documents for Language (:2682-2685). The ID is written straight onto _settings, but the first SaveAsync copies the on-disk empty string over it, and SyncToSettings never re-applies it. LibrarySyncService.DeviceId (LibrarySyncService.cs:75-81) therefore returns a random ID until the first save and 'desktop-<machine>' afterwards, and a new random ID every launch.

**Impact:** This is dormant today: the Sync card is disabled (SettingsView.axaml:4541, IsEnabled="False"), so SyncEnabled cannot be turned on from the UI. Once sync ships, ledger entries recorded before the first save of each session carry a throwaway device ID, so the device list shows duplicate or phantom devices.

**Proposed fix (trivial):** Add SyncDeviceId to ProcessOwnedPlacementKeys, or re-apply it in SaveFeatureSettings from a field captured in LoadFeatureSettings.

**Verifier votes**

- **confirmed** (refute lens): Features.cs:472-473 generates the ID only on _settings, and SaveFeatureSettings (:489-502) never writes SyncDeviceId. The merge (SettingsViewModel.cs:2615-2629) copies every property except ProcessOwnedPlacementKeys (:2594-2613), and that set does not include SyncDeviceId, so the first SaveAsync overwrites it with the empty value on disk. The other writers (LyricsViewModel, LibraryService) save their own copies loaded from disk, which carry the empty value too. So the ID is never persisted. LibrarySyncService.DeviceId (LibrarySyncService.cs:75-82) reads GetSettings() (SettingsViewModel.cs:2869, wired at Program.cs:322) and falls back to 'desktop-<machine>' when the value is empty. The bug is dormant: the Sync card at SettingsView.axaml:4541 has IsEnabled=False and contains the SyncEnabled toggle (:4554).

### S21 — Deleting the playlist that is currently open (sidebar right-click, Delete) leaves its page showing; later edits there are silently lost

- **Location:** `src/Noctis/ViewModels/SidebarViewModel.cs:611` (also: `src/Noctis/Views/SidebarView.axaml:123`, `src/Noctis/ViewModels/PlaylistViewModel.cs:1013`)
- **Area / sweep:** Settings, dialogs, popups, commands / commands · **Category:** correctness · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
SidebarViewModel.cs:611-622:
    public async Task DeletePlaylistAsync(Guid playlistId)
    {
        var playlist = Playlists.FirstOrDefault(p => p.Id == playlistId);
        if (playlist == null) return;
        Playlists.Remove(playlist);
        ...
        await _persistence.SavePlaylistsAsync(Playlists.ToList());
    }
The page's own Delete navigates away (PlaylistViewModel.cs:1013-1014): `await _sidebar.DeletePlaylistAsync(_playlist.Id); BackRequested?.Invoke(...)`
```

**Why it is a bug:** The sidebar context-menu Delete (SidebarView.axaml:123 → DeletePlaylistItemCommand → DeletePlaylist → DeletePlaylistAsync) raises nothing that MainWindowViewModel observes. A grep finds no Playlists.CollectionChanged handler outside LibraryPlaylistsViewModel. The open PlaylistViewModel keeps editing its orphaned `_playlist`: rename calls RenamePlaylist, which returns early because the playlist is gone; reorder and remove save `_sidebar.Playlists`, which no longer contains it. History entries for the playlist also survive.

**Impact:** The deleted playlist stays on screen and accepts edits (reorder, remove, rename, add songs) that are never saved. Back/Forward can bring the deleted playlist back.

**Proposed fix (small):** Raise a PlaylistDeleted(Guid) event from DeletePlaylistAsync. In MainWindowViewModel, when CurrentView is a PlaylistViewModel with that id, Navigate("playlists"), and drop history entries for that id.

**Verifier votes**

- **confirmed** (refute lens): SidebarViewModel.cs:611-623 DeletePlaylistAsync only removes from Playlists/PlaylistItems, rebuilds rows and saves, and raises no event. The sidebar menu (SidebarView.axaml:121-124) goes DeletePlaylistItem (:458) -> DeletePlaylist (:601) -> DeletePlaylistAsync, with no navigation afterwards. Only the page's own delete invokes BackRequested (PlaylistViewModel.cs:1009-1015). The only Playlists/PlaylistItems CollectionChanged subscribers are in LibraryPlaylistsViewModel.cs:59-60, none in MainWindowViewModel. Nav history keeps view objects (MainWindowViewModel.cs:219-222). RenamePlaylist returns early for a missing id (:628-629), and page saves write _sidebar.Playlists (PlaylistViewModel.cs:686/711/744/961), which no longer holds the playlist. The user deleted it on purpose, so impact stays low.

### S23 — Edit Playlist changes the playlist fields before an unguarded cover File.Copy; if the copy fails, the edit is half-applied and nothing is shown

- **Location:** `src/Noctis/ViewModels/SidebarViewModel.cs:879` (also: `src/Noctis/ViewModels/LibraryPlaylistsViewModel.cs:318`, `src/Noctis/App.axaml.cs:73`)
- **Area / sweep:** Settings, dialogs, popups, commands / dialogs · **Category:** error-handling · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
// SidebarViewModel.cs:847-851
playlist.Name = newName;
playlist.Description = newDescription;
playlist.IsPinned = dialogVm.IsPinned;
playlist.Folder = dialogVm.PlaylistFolder.Trim();
// :879
File.Copy(dialogVm.PendingCoverArtFile, destPath, overwrite: true);
```

**Why it is a bug:** The model is changed first and the copy runs afterwards without a try/catch. The copy throws if the picked image is on a network share or USB stick that has gone away, was deleted after it was picked, or is locked. AsyncRelayCommand rethrows the exception on the UI thread (https://github.com/CommunityToolkit/dotnet/blob/main/src/CommunityToolkit.Mvvm/Input/AsyncRelayCommand.cs), where App.axaml.cs:73-79 logs it and marks it handled. The nav-item refresh (884-898), RebuildSidebarRows and SavePlaylistsAsync (900-901) never run. The sidebar keeps showing the old name and folder, and the half-applied state reaches disk only through whatever unrelated save happens next. LibraryPlaylistsViewModel.cs:318-321 fixed this same pattern for 'Set cover art'.

**Impact:** Edit Playlist appears to do nothing and shows no error; a partial rename is saved at some later point.

**Proposed fix (trivial):** Do the cover copy first, inside a try/catch that logs and keeps the old cover, and only then apply the name, description, pin and folder and save. Alternatively, wrap lines 862-881 in a try/catch.

**Verifier votes**

- **confirmed** (refute lens): SidebarViewModel.cs:847-851 changes Name/Description/IsPinned/Folder before the unguarded File.Copy at :879. PendingCoverArtFile is any local path the user picked (EditPlaylistDialogViewModel.cs:97-100), so a source that disappeared or is locked makes the copy throw. All callers are [RelayCommand] async Tasks (SidebarViewModel.cs:443-449, LibraryPlaylistsViewModel.cs:216-222, PlaylistViewModel.cs:996-999), so the exception reaches App.axaml.cs:73-79, which logs it and marks it handled. The nav-item refresh (:884-898), RebuildSidebarRows and SavePlaylistsAsync (:900-901) are then skipped. LibraryPlaylistsViewModel.cs:318-321 wraps the same copy in a try/catch for the stated reason. The failure path is uncommon.

### S26 — Closing the Lyrics Background picker with Esc leaves a YouTube backdrop download running, which then silently applies the video

- **Location:** `src/Noctis/Views/LyricsBackgroundPickerDialog.axaml.cs:70` (also: `src/Noctis/ViewModels/LyricsBackgroundPickerViewModel.cs:252`, `src/Noctis/ViewModels/LyricsBackgroundPickerViewModel.cs:353`)
- **Area / sweep:** Settings, dialogs, popups, commands / dialogs · **Category:** dialog-lifecycle · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
// LyricsBackgroundPickerDialog.axaml.cs:65-72
protected override void OnKeyDown(KeyEventArgs e)
{   if (e.Key == Key.Escape)
    {   e.Handled = true;
        _ = CloseAnimatedAsync();
        return;
    }
// LyricsBackgroundPickerViewModel.cs:252-257
private void Close() { _downloadCts?.Cancel(); CloseRequested?.Invoke(this, EventArgs.Empty); }
```

**Why it is a bug:** The X button runs the view model's Close, which cancels the yt-dlp download. Esc skips it. yt-dlp keeps running, and when it finishes, DownloadFromYouTubeAsync (353-363) calls SetLyricsBackgroundMediaAsync or SetLyricsBackgroundOverrideAsync, changing the backdrop the user abandoned by pressing Esc.

**Impact:** A backdrop the user cancelled with Esc gets applied anyway a minute later.

**Proposed fix (trivial):** Route Esc through the view model: (DataContext as LyricsBackgroundPickerViewModel)?.CloseCommand.Execute(null).

**Verifier votes**

- **confirmed** (refute lens): LyricsBackgroundPickerDialog.axaml.cs:65-74: Esc calls CloseAnimatedAsync() directly and never runs the view model's Close (LyricsBackgroundPickerViewModel.cs:252-257), which is the only close path that calls _downloadCts.Cancel(). The dialog has no Closing/Closed override, and neither caller (SettingsView.axaml.cs:446-453, LyricsStudioView.axaml.cs:63-68) cancels after ShowDialog returns. The only binding to CloseCommand is the X button (axaml:133). So a running DownloadFromYouTubeAsync (VM:349) completes after Esc and applies the backdrop through SetLyricsBackgroundMediaAsync or SetLyricsBackgroundOverrideAsync (VM:353-363).

### S29 — Metadata editor Cancel stays enabled while Save runs; it closes the window, the save still completes, and any write-failure message is lost

- **Location:** `src/Noctis/Views/MetadataWindow.axaml:490` (also: `src/Noctis/ViewModels/MetadataViewModel.cs:2695`, `src/Noctis/ViewModels/MetadataViewModel.cs:2652`)
- **Area / sweep:** Settings, dialogs, popups, commands / dialogs · **Category:** dialog-lifecycle · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
<!-- MetadataWindow.axaml:488-490 -->
<Button Grid.Column="1"
        Content="{loc:T Metadata.Cancel}"
        Command="{Binding CancelCommand}"
// MetadataViewModel.cs:2695-2699
private void Cancel()
{   CloseRequested?.Invoke(this, EventArgs.Empty);
}
```

**Why it is a bug:** For albums, Save runs several seconds of background writes (SaveInternalAsync 2398-2437) behind a spinner. Cancel is not disabled while IsSaving and cancels nothing. Clicking it mid-save closes the dialog while every write still completes, so the edit the user tried to cancel is applied. If some writes fail, SaveErrorMessage (2652-2660) is set on a window that is already closed, so the failure is never seen.

**Impact:** Pressing 'Cancel' during a save still applies the edit and hides write failures.

**Proposed fix (trivial):** Add IsEnabled="{Binding !IsSaving}" to the Cancel button and ignore CancelCommand while IsSaving.

**Verifier votes**

- **confirmed** (refute lens): MetadataWindow.axaml:488-497: the Cancel button binds CancelCommand and has no IsEnabled/IsSaving gate. Only the Save button's content changes with IsSaving (:509-526). MetadataViewModel.cs:2696-2699: Cancel() only raises CloseRequested, which MetadataWindow.axaml.cs:108 turns into Close(). It cancels nothing. Save (2256-2271) awaits SaveInternalAsync, and that keeps running its Task.Run tag writes (2398-2437), library save (2634-2645) and ChangesSaved (2647) after the window closes. When writes fail, SaveErrorMessage is set at 2652-2660 on a window that is already closed, so the user never sees it. Severity stays low: the user has already pressed Save, the window's X button does the same thing, and nothing corrupts. The visible harm is a lost error message.

### U01 — GlassBackdropOp.Render allocates a native SKRoundRect on every frosted repaint and never disposes it

- **Location:** `src/Noctis.UI/Controls/GlassPanel.cs:618` (also: `src/Noctis.UI/Controls/GlassPanel.cs:600-644`)
- **Area / sweep:** UI performance / ui-anim-leaks · **Category:** IDisposable not disposed (per-frame) · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: low)

**Evidence**

```
GlassPanel.cs:617-628
    var m = canvas.TotalMatrix;
    var local = new SKRoundRect();
    local.SetRectRadii(new SKRect((float)Bounds.X, ...), new[] { ... });
    using var path = new SKPath();
    path.AddRoundRect(local);
    path.Transform(m);
```

**Why it is a bug:** SKRoundRect is an SKObject that wraps a native sk_rrect. Everything else in this method is `using`-disposed, but `local` is not, so each instance is freed only by SKNativeObject's finalizer (https://github.com/mono/SkiaSharp/blob/main/binding/SkiaSharp/SKObject.cs: `~SKNativeObject () { ... Dispose (false); }`). The op renders on the render thread every time a Liquid Glass panel (sidebar rail, playback island, Settings card) repaints, which is every frame while content scrolls under the rail or island.

**Impact:** With Liquid Glass on, native allocations and finalizer-queue entries pile up on every frame while scrolling. This adds GC and finalizer-thread pressure on a path that is already the most expensive per frame. It is minor and transient, not an unbounded leak.

**Proposed fix (trivial):** Change it to `using var local = new SKRoundRect();`.

**Verifier votes**

- **confirmed** (refute lens): GlassPanel.cs:618 has `var local = new SKRoundRect();` with no using or Dispose. Everything else in Render (600-644) is disposed: `using var api` at 604 and `using var path` at 626. SKRoundRect is an SKObject that owns its native sk_rrect, so it is freed only by the finalizer, and a new one is created on every GlassBackdropOp.Render. The cost is finalizer/GC churn on the render thread. It is not an unbounded leak, so low is the right severity.

### U02 — Lyrics-page SpectrumVisualizer starts a 250 ms polling DispatcherTimer while the LyricsView is pre-built but never shown (or detached), and it never stops

- **Location:** `src/Noctis/Controls/SpectrumVisualizer.cs:145` (also: `src/Noctis/Controls/SpectrumVisualizer.cs:99-100`, `src/Noctis/Controls/SpectrumVisualizer.cs:136-154`, `src/Noctis/Controls/SpectrumVisualizer.cs:291-309`, `src/Noctis/Views/LyricsView.axaml:523-537`, `src/Noctis/ViewModels/MainWindowViewModel.cs:780-789`)
- **Area / sweep:** UI performance / ui-anim-leaks · **Category:** timer running while view detached · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
MainWindowViewModel.cs:788  Dispatcher.UIThread.Post(() => App.CachedLocator?.Build(_lyricsVm), DispatcherPriority.Background);
LyricsView.axaml:536-537  IsActive="{Binding $self.IsVisible}"
                          IsVisible="{Binding Player.LyricsVisualizerEnabled}"/>
SpectrumVisualizer.cs:144-149
    if (_running || !IsActive) return;
    if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } topLevel)
    {   StartVisibilityPoll();
        return; }
SpectrumVisualizer.cs:297-299 (tick): if (!IsActive || !IsVisible) { StopVisibilityPoll(); return; }
    if (IsEffectivelyVisible && TopLevel.GetTopLevel(this) != null) TryStart();
```

**Why it is a bug:** About 1.5 s after launch the LyricsView is pre-built through CachedViewLocator.Build and is not attached, and its DataContext is null. The IsVisible binding (Player.LyricsVisualizerEnabled) cannot resolve and stays at the default true. The $self IsActive binding flips IsActive to true, the class handler (line 99) calls OnGateChanged -> TryStart, and TryStart finds no TopLevel and starts the poll. The poll stops only when a tick sees the gate closed, when TryStart succeeds (which needs attachment), or on Rest() in OnDetachedFromVisualTree. None of these happen for a view that is never attached, so a user who never opens the lyrics page carries a 4 Hz timer for the whole session. The same happens after leaving the lyrics page with the visualizer disabled: the DataContext inherited from the ContentPresenter is cleared after the visual detach, IsVisible reverts from false to its default true, and the gate reopens on the detached view.

**Impact:** Adds 4 no-op UI-thread timer wakeups per second for the app session (or while away from the lyrics page). Small but permanent CPU/battery cost.

**Proposed fix (trivial):** Only poll when the control is attached but hidden. In TryStart, return without polling when TopLevel.GetTopLevel(this) is null; OnAttachedToVisualTree already calls OnGateChanged. Apply the same rule to FlowingArtworkAnimator.TryStart/OnFrame.

**Verifier votes**

- **confirmed** (refute lens): The factory is `() => new LyricsView()` (App.axaml.cs:55). It sets no DataContext; DataContext only comes by inheritance once the view is hosted in the page-host ContentControl (MainWindow.axaml:689). MainWindowViewModel.cs:788 pre-builds the view. In LyricsView.axaml:536-537, `IsActive="{Binding $self.IsVisible}"` resolves without a DataContext. The IsVisible binding to Player.LyricsVisualizerEnabled cannot resolve and stays at the default true. IsActive then goes false to true, which runs the class handler (SpectrumVisualizer.cs:99), then OnGateChanged (136-140), then TryStart. TryStart has no TopLevel, so it calls StartVisibilityPoll (145-148). The poll tick (295-300) stops only when the gate closes. Otherwise it calls TryStart only once a TopLevel exists. Rest is reached only from OnDetachedFromVisualTree (130-134) or from a closed gate. So the 250 ms DispatcherTimer runs until the lyrics page is first opened. The second scenario (visualizer disabled, DataContext cleared after detach so IsVisible reverts to true) follows the same path. The cost is a 4 Hz no-op timer, so low.

### U04 — Add Songs dialog 'Deselect all' is O(n²): List<Guid>.Remove per matched track

- **Location:** `src/Noctis/ViewModels/AddSongsDialogViewModel.cs:195`
- **Area / sweep:** UI performance / ui-thread · **Category:** ui-thread-blocking · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
AddSongsDialogViewModel.cs:190  if (selectable.All(t => _selected.Contains(t.Id)))
  :192      foreach (var track in selectable)
  :194          _selected.Remove(track.Id);
  :195          _selectionOrder.Remove(track.Id);   // List<Guid>
  :26   private readonly List<Guid> _selectionOrder = new();
```

**Why it is a bug:** ToggleSelectAll is a [RelayCommand] (UI thread). 'Select all N' appends every match to the List<Guid> _selectionOrder in match order; 'Deselect all' then removes them in the same order, so each List.Remove finds the item at index 0 and shifts the remaining n-1 GUIDs ('This method is an O(n) operation, where n is (Count - index)' — https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1.removeat). Total work is ~n²/2 16-byte element moves.

**Impact:** In Playlist › Add songs, searching a broad query (e.g. "a" matching 40k tracks), pressing 'Select all' and then 'Deselect all' moves ~13 GB of memory on the UI thread — roughly a second or more of frozen UI; 100k matches take several seconds.

**Proposed fix (trivial):** When deselecting all, rebuild the order list in one pass: `var ids = selectable.Select(t => t.Id).ToHashSet(); _selected.ExceptWith(ids); _selectionOrder.RemoveAll(ids.Contains);`.

**Verifier votes**

- **confirmed** (refute lens): AddSongsDialogViewModel.cs:26 declares `_selectionOrder` as a List<Guid>. ToggleSelectAll (185-212) appends every selectable match in match order (201-205). Deselect removes them in the same order through `_selectionOrder.Remove(track.Id)` (192-196). Each Remove finds its item near the front and shifts the rest of the list, so the total work is O(n^2). The scan covers all matches without a cap (134), so a broad query can reach tens of thousands. The stall is short (well under a second to a few seconds) and needs a very broad Select all followed by Deselect all. That is an edge case, so low.

### U10 — Metadata editor constructor and Save read every selected track's lyrics from the disk-backed lyrics store (and sidecars) on the UI thread

- **Location:** `src/Noctis/ViewModels/MetadataViewModel.cs:686` (also: `src/Noctis.Core/Models/Track.cs:242`, `src/Noctis.Core/Services/LyricsStore.cs:69`, `src/Noctis/ViewModels/MetadataViewModel.cs:1224`, `src/Noctis/ViewModels/MetadataViewModel.cs:2400`)
- **Area / sweep:** UI performance / ui-thread · **Category:** ui-thread-blocking · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
MetadataViewModel.cs:434   CaptureLoadedTagSignatures();   // ctor, UI thread
  :688-690  _loadedTagSignatures[_track] = ComputeTagState(_track);
            foreach (var t in _albumTracks ?? Enumerable.Empty<Track>()) _loadedTagSignatures[t] = ComputeTagState(t);
  :722      t.Lyrics, t.Comment, t.Grouping, t.Copyright,     // inside ComputeTagState
  :2400     var tagWriteTargets = _albumTracks.Where(NeedsTagWrite).ToList();   // Save, UI thread, ComputeTagState again
Track.cs:244  get => _lyricsOverride ?? _legacyLyrics ?? ReadStoredLyrics().Plain;
LyricsStore.cs:52 CacheCapacity = 16;  :131-132 File.Exists(path) ... File.ReadAllText(path)
```

**Why it is a bug:** Track.Lyrics is disk-backed: the getter reads <data>/lyrics_store/{id}.json synchronously on first touch (Track.cs:238-257; LyricsStore.Read → ReadFromDisk does File.Exists + ReadAllText + JSON deserialize), and the LRU holds only 16 entries, so a loop over N tracks performs N disk probes. Track.cs:239-240 explicitly says 'hot paths read it off the UI thread'. MetadataViewModel's ctor runs on the UI thread (MetadataHelper.OpenMultiTrackMetadataWindow:226 / OpenMetadataWindow:258, both from RelayCommands) and computes a TagState — including t.Lyrics — for every track in the selection/album; Save does it again at :2400 before the Task.Run. InitializeAsync's comment ('the ctor only sets in-memory state', :438-445) is therefore not true: LoadFromTrack (:1224-1246) also does File.Exists + File.ReadAllText of the .lrc/.txt sidecar next to the audio file on the UI thread.

**Impact:** Ctrl+A in Songs → Edit metadata (or Save) on a few thousand tracks stalls the UI for one lyrics-store file probe/read per track, twice (open and save); with tracks that have stored lyrics each is a real file read, so the stall grows to seconds on large selections. Opening the editor for a single track on a slow/unreachable share blocks on the sidecar probes before the window appears.

**Proposed fix (small):** Build the TagState snapshots (and the sidecar reads) inside InitializeAsync's Task.Run (or a dedicated Task.Run) and store them before enabling Save; in SaveInternalAsync compute tagWriteTargets inside the existing Task.Run. Alternatively exclude Lyrics from TagState and compare against a lyrics snapshot captured off-thread.

**Verifier votes**

- **confirmed** (refute lens): The constructor calls CaptureLoadedTagSignatures() at MetadataViewModel.cs:434. At :686-691 it calls ComputeTagState for _track and every _albumTracks entry, and ComputeTagState reads t.Lyrics (:722). Track.Lyrics' getter (Track.cs:244) falls through to LyricsStore.Read. On an LRU miss (capacity 16, LyricsStore.cs:52) that goes to ReadFromDisk (:126-136: File.Exists, then ReadAllText + JSON). Save runs NeedsTagWrite -> ComputeTagState again at :2400, before the Task.Run at :2401. Both run on the UI thread: MetadataHelper.cs:226/258 builds the ViewModel, and there is no ConfigureAwait(false) anywhere in the file. LoadFromTrack :1224-1252 does File.Exists/ReadAllText on the .lrc/.txt sidecars, so the InitializeAsync comment at :442 ('ctor only sets in-memory state') is inaccurate. Lowered to low: the store is small files in local app data, so an album-sized selection costs milliseconds. Only very large multi-selections (thousands of tracks) produce a noticeable stall, and the sidecar probe covers a single track.

### U12 — Every track change probes the track's music folder 7+ times (music video + animated cover) on the UI thread, even with those features off

- **Location:** `src/Noctis/ViewModels/PlayerViewModel.cs:1698` (also: `src/Noctis/Helpers/MusicVideoLocator.cs:12`, `src/Noctis/Services/AnimatedCoverService.cs:16`, `src/Noctis/ViewModels/PlayerViewModel.cs:94`, `src/Noctis/ViewModels/PlayerViewModel.cs:1984`)
- **Area / sweep:** UI performance / ui-thread · **Category:** ui-thread-blocking · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
PlayerViewModel.cs:1693  partial void OnCurrentTrackChanged(Track? value)
  :1698      ResolveMusicVideo();   // runs regardless of MusicVideosEnabled (computes CurrentTrackHasMusicVideoFile)
MusicVideoLocator.cs:24-32  foreach (var ext in Extensions) { if (File.Exists(Path.Combine(folder, stem + ext))) return ...; }
                            foreach (var sub in SubFolders) { if (!Directory.Exists(Path.Combine(folder, sub))) continue; ... }
AnimatedCoverService.cs:30-33  foreach (var ext in SupportedExtensions) { var p = Path.Combine(folder, "cover" + ext); if (File.Exists(p)) return p; }
PlayerViewModel.cs:2542  CurrentAnimatedCoverPath = _animatedCovers.Resolve(track);   // LoadAlbumArt, same UI turn
```

**Why it is a bug:** PlayTrack (PlayerViewModel.cs:1984-1985) sets CurrentTrack and calls LoadAlbumArt synchronously on the UI thread for every click-to-play and natural advance. OnCurrentTrackChanged → MusicVideoLocator.Find does 5 File.Exists + 4 Directory.Exists (up to 25 probes if a videos folder exists) next to the audio file, and AnimatedCoverService.Resolve does 2 more in the same folder plus 4 in app data. None are cached per folder. On local SSDs this is sub-millisecond, but for libraries on SMB/NAS each probe is a network round trip, and for a share that has become unreachable the first probe blocks until the SMB client gives up (duration not measured here).

**Impact:** Track changes on NAS-hosted libraries add network round trips to the UI thread right when the lyrics fade and artwork swap animate; if the share dropped, pressing Play can freeze the window until the SMB timeout before the playback error path even runs.

**Proposed fix (small):** Resolve music-video and animated-cover paths in Task.Run keyed by the track (generation-guarded like LoadAlbumArt's decode), and cache per-folder directory listings; skip MusicVideoLocator entirely when MusicVideosEnabled is false unless the menu item is being opened.

**Verifier votes**

- **confirmed** (refute lens): PlayTrack (PlayerViewModel.cs:1984-1985) sets CurrentTrack and then calls LoadAlbumArt synchronously. OnCurrentTrackChanged (:1693-1698) calls ResolveMusicVideo (:94-102), which always runs MusicVideoLocator.Find. Find does 5 File.Exists plus 4 Directory.Exists, and more if a videos folder exists (MusicVideoLocator.cs:24-38). LoadAlbumArt :2501/:2542 calls _animatedCovers.Resolve, which does 2 File.Exists in the music folder plus 4 in app data (AnimatedCoverService.cs:30-47). None of this is cached, and all of it runs inline on the UI thread. Running the lookup with the feature off is intentional: CurrentTrackHasMusicVideoFile drives the menu item (comment :86-90). The perf cost itself is real but small: sub-millisecond on local disks, and only NAS/unreachable-share libraries are affected. That fits low.

### U15 — In Developer Mode, the Settings log pane is rebuilt (a full re-join of the 500-line session log) on every log burst, even while Settings is closed

- **Location:** `src/Noctis/ViewModels/SettingsViewModel.cs:2049` (also: `src/Noctis/ViewModels/SettingsViewModel.cs:6669-6672`, `src/Noctis/Views/SettingsView.axaml:4333`, `src/Noctis.Core/Services/DebugLog.cs:51-65`)
- **Area / sweep:** UI performance / ui-anim-leaks · **Category:** event handler doing work for a hidden view · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
SettingsViewModel.cs:2045-2056
    // Keep the Developer Mode log view live while it's visible. Coalesced: ...
    DebugLog.Changed += () =>
    {
        if (Interlocked.Exchange(ref _devLogRefreshQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _devLogRefreshQueued, 0);
            if (DeveloperMode)
                DevLogText = ComposeDevLogText();
```

**Why it is a bug:** The comment says the pane is kept live 'while it's visible', but the only gate is DeveloperMode. ComposeDevLogText (6669-6672) returns CrashJournal.PreservedBlock + DebugLog.Snapshot(), a fresh string of up to 500 lines plus any preserved crash block, and assigns it to the SelectableTextBlock at SettingsView.axaml:4333. In dev mode, DebugLog.Changed fires for every playback mirror entry and every LyricsView.Trace line (line changes, glide ends), so during playback this rebuilds several times a second while the Settings overlay is closed.

**Impact:** Only when Developer Mode is on: large repeated string allocations on the UI thread (a 500-line join is typically tens to hundreds of KB) while nobody is looking at the log. This adds GC pressure and UI-thread work during exactly the sessions users run to diagnose stutter.

**Proposed fix (trivial):** Also gate on the pane being shown: expose the modal/section state (e.g. MainWindowViewModel.IsSettingsModalOpen, or a view-set IsDevLogVisible flag) and skip the rebuild while hidden. Recompose once when the Settings modal or the developer section opens, which OnDeveloperModeChanged and the open path already do.

**Verifier votes**

- **confirmed** (refute lens): SettingsViewModel.cs:2049-2058: the DebugLog.Changed handler is coalesced but gated only on `if (DeveloperMode)`, even though its comment says 'while it's visible'. ComposeDevLogText (6669-6672) re-joins CrashJournal.PreservedBlock plus DebugLog.Snapshot() every time. In dev mode, OnDeveloperModeChanged sets DebugLog.VlcBridgeEnabled = value (6684), which turns on LyricsView.Trace (LyricsView.axaml.cs:33-37). Trace writes a log line on every glide/chase/scroll step (868, 907, 1063, 1156, 1181), so Changed fires repeatedly during playback while Settings is closed. The cost is bounded by the coalescing (one rebuild per Background dispatcher pass) and only applies in Developer Mode, so low.

### U16 — Edit Playlist copies the chosen cover synchronously on the UI thread with no error handling; a failed copy silently discards the whole edit

- **Location:** `src/Noctis/ViewModels/SidebarViewModel.cs:879` (also: `src/Noctis/ViewModels/LibraryPlaylistsViewModel.cs:318`)
- **Area / sweep:** UI performance / ui-thread · **Category:** ui-thread-blocking · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
SidebarViewModel.cs:847-851  playlist.Name = newName; playlist.Description = newDescription; ... playlist.ModifiedAt = DateTime.UtcNow;
  :864  var coversDir = Path.Combine(Helpers.AppPaths.DataRoot, "playlist_covers");
  :873  foreach (var stale in Directory.EnumerateFiles(coversDir, $"{playlist.Id}.*"))
  :879  File.Copy(dialogVm.PendingCoverArtFile, destPath, overwrite: true);
  :901  await _persistence.SavePlaylistsAsync(Playlists.ToList());
```

**Why it is a bug:** EditPlaylistAsync continues on the UI thread after `await dialog.ShowDialog(owner)` and copies the user-picked image (any size, possibly on a network share) synchronously. The sibling LibraryPlaylistsViewModel.SetCoverArt (:318-355) was already fixed to stream the copy and wrap it in try/catch ('a locked or read-only destination, a full disk, or a source on a disconnected network share terminated the app'), but this path was not. If File.Copy throws (source moved/locked, disk full), the exception escapes the async RelayCommand to the Dispatcher handler (App.axaml.cs:73-79, marked handled): the in-memory playlist already has the new name/description/pin/folder, but the nav item is not rebuilt and SavePlaylistsAsync at :901 never runs.

**Impact:** Large cover images from slow shares freeze the UI during the copy; a failed copy leaves the sidebar showing the old name and loses the rename/description edit on the next restart without any message.

**Proposed fix (small):** Wrap the cover handling in try/catch (log via DebugLog), perform the enumerate/delete/copy in `await Task.Run(...)`, and always rebuild the nav item and call SavePlaylistsAsync for the non-cover fields.

**Verifier votes**

- **confirmed** (refute lens): SidebarViewModel.cs:847-851 mutates the playlist's name, description, pin, folder and ModifiedAt, then File.Copy at :879 runs unguarded on the UI thread. On a throw, the nav-item rebuild (883-898), RebuildSidebarRows and SavePlaylistsAsync (:901) are skipped. PendingCoverArtFile is the raw picked path from TryGetLocalPath (EditPlaylistDialogViewModel.cs:97-100), which can be a UNC/network path. All callers are unguarded async RelayCommands: SidebarViewModel:448, LibraryPlaylistsViewModel:221, PlaylistViewModel:999. So the exception reaches the Dispatcher.UnhandledException handler, which marks it handled (App.axaml.cs:73-79). The sibling LibraryPlaylistsViewModel.SetCoverArt (318-355) is try/catch-wrapped and streams the copy asynchronously. One part of the impact is overstated: the in-memory Playlist is already mutated, so any later SavePlaylistsAsync from another playlist operation would persist the rename. The edit is lost only if nothing else saves before exit. The trigger (source locked or missing, disk full, slow share) is rare, so low.

### U19 — Folders view TreeView is not virtualized; expanding a root with many subfolders realizes every node

- **Location:** `src/Noctis/Views/LibraryFoldersView.axaml:93`
- **Area / sweep:** UI performance / ui-lists · **Category:** ui-virtualization · **Platforms:** all
- **Verification:** confirmed (finder confidence: unverified; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
LibraryFoldersView.axaml:93-99
            <TreeView x:Name="FolderTree"
                      ItemsSource="{Binding RootNodes}"
                      SelectedItem="{Binding SelectedNode, Mode=TwoWay}"
                      AutoScrollToSelectedItem="False">
                <TreeView.ItemTemplate>
                    <TreeDataTemplate x:DataType="m:FolderNode" ItemsSource="{Binding Children}">
LibraryFoldersViewModel.cs:159-161: RootNodes.Clear(); foreach (var root in forest) RootNodes.Add(root);   (on every dirty refresh; expansion is restored)
```

**Why it is a bug:** TreeView does not override the ItemsControl default panel (StackPanel, https://github.com/AvaloniaUI/Avalonia/blob/master/src/Avalonia.Controls/ItemsControl.cs). Every child of an expanded node gets a full TreeViewItem: the template with its expander ToggleButton, plus a StackPanel and 2 TextBlocks. A typical Music/<Artist>/<Album> layout puts 500-2,000 artist folders under one expanded root. Expansion is persisted and restored after every library-driven rebuild (RefreshAsync), so every rebuild re-realizes all of them.

**Impact:** Possible stall when expanding a large root folder, and after library updates while the Folders page is dirty. The size of the stall has not been measured.

**Proposed fix (medium):** Check the timing at runtime first. If it is significant, show the tree as a flattened, virtualized ListBox with indentation, or populate children lazily on expand.

**Verifier votes**

- **confirmed** (refute lens): LibraryFoldersView.axaml:93-105: the TreeView has no custom ItemsPanel, and its template is a StackPanel with 2 TextBlocks. Avalonia's TreeView and TreeViewItem do not virtualize. :23-24 binds TreeViewItem.IsExpanded TwoWay to FolderNode.IsExpanded. LibraryFoldersViewModel.cs:150-161 captures expansion, restores it onto the new forest, then does RootNodes.Clear() plus Add(root). So every dirty rebuild re-realizes every child of the expanded nodes. The finding says itself that the stall is unmeasured and depends on folder layout, so low severity fits.

### U20 — Playlists grid still realizes every tile (UniformGrid in a ScrollViewer), with a per-tile Height binding to its own Bounds

- **Location:** `src/Noctis/Views/LibraryPlaylistsView.axaml:28` (also: `src/Noctis/Views/LibraryPlaylistsView.axaml:136-140`)
- **Area / sweep:** UI performance / ui-lists · **Category:** ui-virtualization · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)
- **Prior audit:** AUDIT.md L2

**Evidence**

```
LibraryPlaylistsView.axaml:26-33
            <ScrollViewer HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Visible">
                <ItemsControl ItemsSource="{Binding FilteredPlaylists}" Margin="0,0,0,115">
                    <ItemsControl.ItemsPanel>
                        <ItemsPanelTemplate>
                            <UniformGrid Columns="5" VerticalAlignment="Top" />
LibraryPlaylistsView.axaml:136-140: <Style Selector="Border"><Setter Property="Height" Value="{Binding $self.Bounds.Width}" /></Style>
```

**Why it is a bug:** Prior AUDIT.md L2 is still present, and FIXLOG deferred it deliberately. Every playlist tile is realized: an inline ContextMenu with about 11 MenuItems, up to 6 CachedImages, and 2 Buttons. Each tile's art Border also sets Height from its own Bounds, which needs a second layout pass per tile whenever the width changes.

**Impact:** Only noticeable for users with hundreds of playlists: slower first navigation to Playlists and heavier re-layout on window resize.

**Proposed fix (medium):** Use the row-chunked, virtualized ListBox pattern from Albums/Favorites (deferred per FIXLOG L2), and replace the Bounds-bound Height with a size computed in OnSizeChanged, as LibraryAlbumsView does with TileArtworkSize.

**Verifier votes**

- **confirmed** (refute lens): LibraryPlaylistsView.axaml:26-34 matches the quote: a ScrollViewer around an ItemsControl with a UniformGrid Columns=5, so every tile is realized. Each tile has an inline ContextMenu (:45-128, 10 MenuItems and 2 Separators, close to the finding's count of 11). The file has 6 CachedImage occurrences. :136-140 sets Height from {Binding $self.Bounds.Width} through Border.Styles. This repeats prior AUDIT.md L2 (:48, :1676). FIXLOG.md:72 deliberately deferred it on cost/benefit because the view holds tens of items. That makes it a known, accepted low-priority item, not a new defect. The one new sub-claim, the Bounds-driven Height causing an extra layout pass, is accurate but minor. It only matters with hundreds of playlists, so low.

### U23 — Queue popup rows each build an inline ContextMenu whose icon eagerly decodes a 512x512 PNG per realized row

- **Location:** `src/Noctis/Views/MainWindow.axaml:1009` (also: `src/Noctis.UI/Assets/Icons.axaml:202-213`, `src/Noctis/Views/MainWindow.axaml:986-1016`)
- **Area / sweep:** UI performance / ui-lists · **Category:** ui-list-image-decode · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: medium)
- **Needs runtime check:** yes

**Evidence**

```
MainWindow.axaml:1000-1011 (inside QueuePopupListBox ItemTemplate)
                                            <Border.ContextMenu>
                                                <ContextMenu>
                                                    <MenuItem Header="{loc:T Main.RemoveFromQueue}" Click="OnQueueRemoveClick">
                                                        <MenuItem.Icon>
                                                            <Border Width="14" Height="14" ...>
                                                                <Border.OpacityMask>
                                                                    <ImageBrush Source="avares://Noctis.UI/Assets/Icons/Clear%20Queue%20ICON.png" Stretch="Uniform" />
'Clear Queue ICON.png' is 512x512 (IHDR read from the file).
```

**Why it is a bug:** The ContextMenu, its MenuItem and the icon Border are ordinary property values in the DataTemplate, so all of them are constructed every time a row template is built. The string Source goes through Avalonia's BitmapTypeConverter, which creates a new Bitmap on every call with no cache (https://github.com/AvaloniaUI/Avalonia/blob/main/src/Markup/Avalonia.Markup.Xaml/Converters/BitmapTypeConverter.cs). The Skia ImmutableBitmap(Stream) constructor decodes eagerly with SKBitmap.Decode (https://github.com/AvaloniaUI/Avalonia/blob/master/src/Skia/Avalonia.Skia/ImmutableBitmap.cs). So every realized queue row decodes a 1 MiB RGBA bitmap. The team's own probe (memory note scroll_hitch_row_realization) found that recycled rows rebuild their template child on every scroll realization. This is the same per-tile PNG menu icon cost that was removed from the album tiles on 09-24 with shared IconMask* brushes in Icons.axaml:202-213; this list template still has it.

**Impact:** Opening and scrolling the queue panel pays a PNG decode plus 1 MiB of native bitmap per row that comes into view. This shows up as scroll hitches, worst on software rendering, and as memory churn until the finalizer runs. The per-row decode cost has not been measured.

**Proposed fix (trivial):** Add <ImageBrush x:Key="IconMaskClearQueue" Source="avares://Noctis.UI/Assets/Icons/Clear%20Queue%20ICON.png" Stretch="Uniform"/> to Icons.axaml and reference it with <StaticResource ResourceKey="IconMaskClearQueue"/>. Better still, build the one-item menu once in code and attach it on ContextRequested, as TrackContextMenuBuilder does.

**Verifier votes**

- **confirmed** (refute lens): MainWindow.axaml:1000-1014: inside the QueuePopupListBox DataTemplate, each row builds an inline Border.ContextMenu. Its MenuItem.Icon has an OpacityMask ImageBrush with Source=avares://.../Clear%20Queue%20ICON.png, and the PNG IHDR reads 512x512. Icons.axaml:197-201 records that the team found this exact pattern (inline ImageBrush in an item template decodes per realized row) and fixed it for other menus with shared IconMask* brushes, but there is no IconMaskClearQueue. OnQueueRowContextRequested (MainWindow.axaml.cs:1803) only records the row index, so the inline menu is still built for every row. Downgraded to low: it is one PNG per row (the album tiles had nine), in a short queue popup list, and the cost has not been measured.

### U25 — Closing the mini player during a drawer slide leaves MiniPlayerViewModel.IsDrawerAnimating stuck true, so an in-flight search fill re-polls every 50 ms indefinitely

- **Location:** `src/Noctis/Views/MiniPlayerWindow.axaml.cs:1538` (also: `src/Noctis/ViewModels/MiniPlayerViewModel.cs:345-362`, `src/Noctis/Helpers/StreamingFill.cs:64-85`, `src/Noctis/Views/MiniPlayerWindow.axaml.cs:1137-1162`, `src/Noctis/ViewModels/MainWindowViewModel.cs:141-146`)
- **Area / sweep:** UI performance / ui-anim-leaks · **Category:** timer leak after window close · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
MiniPlayerWindow.axaml.cs:1451  if (Vm != null) Vm.IsDrawerAnimating = true;
MiniPlayerWindow.axaml.cs:1476-1478  AnimateDrawer(target, onLanded: () => { if (Vm != null) Vm.IsDrawerAnimating = false; ...
MiniPlayerWindow.axaml.cs:1538-1542
    if (generation != _resizeAnimationGeneration || _closeAnimationDone)
    {   // A newer animation owns _drawerAnim ...
        timer.Stop();
        return; }
StreamingFill.cs:68-71
    if (gate != null && !gate())
    {   DispatcherTimer.RunOnce(Step, TimeSpan.FromMilliseconds(GateRetryMs), DispatcherPriority.Background);
        return; }
```

**Why it is a bug:** Opening the Search drawer starts a StreamingFill of up to 30 rows gated on `() => !IsDrawerAnimating` (MiniPlayerViewModel.cs:350-351, 361-362). The window sets IsDrawerAnimating=true for the 240 ms slide and clears it only in onLanded. If the window closes mid-slide (close button, double-click on the glass, toggling from the main window), the tick sees _closeAnimationDone, stops without calling onLanded, and OnClosed (1137-1162) never resets the flag. MiniPlayerViewModel is cached for the app lifetime (MainWindowViewModel.cs:143), so the pending Step keeps re-arming DispatcherTimer.RunOnce every 50 ms until a newer search fill bumps the generation. That only happens the next time the Search drawer is opened in a later mini-player session.

**Impact:** A 20 Hz no-op timer can run for the rest of the session after an uncommon close timing. There is no visible glitch, only wasted wakeups.

**Proposed fix (trivial):** In MiniPlayerWindow.OnClosed, before unhooking, set `if (_hookedVm != null) _hookedVm.IsDrawerAnimating = false;`. Alternatively, run the reset part of onLanded in AnimateDrawer's abort branch when _closeAnimationDone is true.

**Verifier votes**

- **confirmed** (refute lens): The code matches the finding. OnDrawerChanged (MiniPlayerWindow.axaml.cs:1451) sets IsDrawerAnimating=true, and only onLanded (1478) or SnapDrawerClosed (1438) clears it. SnapDrawerClosed is called only from OnFormResizeRequested (533). The tick abort branch at 1538-1543 stops the timer when _closeAnimationDone is set (set in OnClosing, 1126) and never calls onLanded. OnClosed (1137-1162) does not reset the flag. The VM is cached for the app lifetime (MainWindowViewModel.cs:143). The Drawer=None at line 144 runs before the new window hooks PropertyChanged (MainWindow.axaml.cs:129-130), so the flag stays true. StreamingFill.cs:68-71 then re-arms RunOnce every 50 ms while the gate is closed. One small inaccuracy: the loop also ends when any later drawer slide lands (for example the Queue drawer) or a form jump runs SnapDrawerClosed. Either reopens the gate, and the stale Step then appends its remaining slices. So it does not only end on the next Search open. The 20 Hz wakeups last until the next mini-player drawer action, or for the rest of the session if the mini player is never reopened.

### U27 — PlaylistView re-stamps every realized row once per CollectionChanged event; removing a multi-selection fires one event per track

- **Location:** `src/Noctis/Views/PlaylistView.axaml.cs:115` (also: `src/Noctis/Views/PlaylistView.axaml.cs:146-188`, `src/Noctis/ViewModels/PlaylistViewModel.cs:665-688`, `src/Noctis/ViewModels/PlaylistViewModel.cs:719-745`)
- **Area / sweep:** UI performance / ui-lists · **Category:** ui-freeze · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
PlaylistView.axaml.cs:115-118
    private void OnTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshAllRowVisuals, DispatcherPriority.Loaded);
    }
PlaylistView.axaml.cs:163: var descendants = item.GetVisualDescendants().ToList();   // per realized row, 4 LINQ scans follow
PlaylistViewModel.cs:669-675
        foreach (var t in tracks)
        {
            var displayIdx = Tracks.IndexOf(t);
            if (displayIdx >= 0)
                Tracks.RemoveAt(displayIdx);
            _playlist.TrackIds.Remove(t.Id);
        }
```

**Why it is a bug:** Removing K selected tracks (selection bar Remove, or the row menu with a Ctrl-selection) does K IndexOf, K RemoveAt and K List.Remove calls (each O(N)), and K CollectionChanged events go to the virtualized ListBox. Each event also posts a full RefreshAllRowVisuals. The posts run after the loop, and each one walks every remaining realized row's whole visual subtree with ToList plus 4 LINQ searches. MoveTracks (block drag, PlaylistViewModel.cs:732-737) does the same with up to K Move events. Example: removing 1,000 of 3,000 tracks means 1,000 re-stamps over about 25 realized rows. That is about 0.5 s of redundant UI work (estimate) on top of about 9M list operations.

**Impact:** A noticeable hitch when removing or dragging hundreds of selected tracks in a large playlist. Smaller than the queue case, because the posts run after the list has shrunk.

**Proposed fix (small):** Coalesce the post with a flag: if (_restampPosted) return; _restampPosted = true; Post(() => { _restampPosted = false; RefreshAllRowVisuals(); }). In RemoveTrack, build a HashSet of the tracks to remove and call Tracks.ReplaceAll(Tracks.Where(t => !set.Contains(t))) and _playlist.TrackIds.RemoveAll(id => ids.Contains(id)) when more than a few are removed.

**Verifier votes**

- **confirmed** (refute lens): PlaylistView.axaml.cs:115-118 posts RefreshAllRowVisuals once per CollectionChanged event, with no coalescing. RefreshAllRowVisuals (146-151) runs UpdateRowIndexVisuals on each realized row, and that does GetVisualDescendants().ToList() plus several OfType/FirstOrDefault scans (163-187). PlaylistViewModel.RemoveTrack (665-675) loops over the Ctrl-selection with IndexOf, RemoveAt and TrackIds.Remove, so K removals raise K events and K posts. One correction on MoveTracks (732-737): it raises one Move per row pulled into place. For a block dragged downward, that is the number of rows it passes over, not at most K, so it can be more than K. The defect is real, and low is right for a hitch that needs a bulk operation on a large playlist.

### U28 — Settings 'Removed tracks' list is unbounded, non-virtualized and rebuilt item by item on every Settings open

- **Location:** `src/Noctis/Views/SettingsView.axaml:3892` (also: `src/Noctis/ViewModels/SettingsViewModel.cs:5346-5363`, `src/Noctis/ViewModels/SettingsViewModel.cs:5381-5405`, `src/Noctis/Helpers/LibraryRemovalHelper.cs:391-410`, `src/Noctis/Views/SettingsView.axaml:3838`)
- **Area / sweep:** UI performance / ui-lists · **Category:** ui-virtualization · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)
- **Needs runtime check:** yes

**Evidence**

```
SettingsView.axaml:3892: <ItemsControl ItemsSource="{Binding RemovedTracks}">   (no ItemsPanel; inside the page ScrollViewer)
SettingsViewModel.cs:5359-5361
        RemovedTracks.Clear();
        foreach (var e in entries)
            RemovedTracks.Add(e);
SettingsViewModel.cs:5497: _ = RefreshRemovedTracksAsync();   (end of ApplyLibraryStats, run on every Settings open per the comment at 5386-5388)
LibraryRemovalHelper.cs:400-409: SelectRemovedEntries(...) -> every ExcludedFilePaths entry that still exists, no cap
```

**Why it is a bug:** The list holds every file removed with 'Keep files'. Removing an artist or a folder that way can add hundreds or thousands of entries. Each entry is a Border, Grid, StackPanel, 2 TextBlocks and a templated Button, built in a StackPanel with no virtualization. The collection is cleared and re-added one item at a time (N CollectionChanged events) every time the statistics refresh runs, which is on every Settings open. SnoozedTracks (SettingsView.axaml:3838, SettingsViewModel.cs:5319-5321) has the same shape, but is normally small.

**Impact:** For users with a large removed-files list, opening Settings, or showing the tab that holds Hidden tracks, stalls the UI.

**Proposed fix (small):** Use a VirtualizingStackPanel ItemsPanel and a BulkObservableCollection with a single ReplaceAll. Or show the first N entries with an 'and X more' line.

**Verifier votes**

- **confirmed** (refute lens): SettingsView.axaml:3892-3927 has an ItemsControl with no ItemsPanel (a StackPanel). Each item is a Border, Grid, StackPanel, 2 TextBlocks and a Button. SettingsViewModel.cs:5336 declares a plain ObservableCollection, and lines 5359-5361 do Clear() and then one Add per entry. The refresh chain is RefreshAndReturnSettings (MainWindowViewModel.cs:2106), then RefreshLibraryStats, then ApplyLibraryStats (5476-5497), then RefreshRemovedTracksAsync (5497). That runs on every Settings navigation, on opening the Statistics tab (373), and after an import (MainWindowViewModel.cs:1222). The await in RefreshRemovedTracksAsync captures the UI context, so the adds run on the UI thread. LibraryRemovalHelper.cs:400-409 has no cap on the entries. It only bites users with a large removed-with-Keep-Files list, so low is right.

### X01 — Server playlist endpoints load, modify and save playlists.json without a lock: concurrent create/update/delete/sync requests lose each other's changes

- **Location:** `src/Noctis.Core.Server/Services/Server/LibraryServerAdapter.cs:117` (also: `src/Noctis.Core.Server/Services/Server/LibraryServerAdapter.cs:107`, `src/Noctis.Core.Server/Services/Server/LibraryServerAdapter.cs:132`, `src/Noctis.Core.Server/Services/Server/LibraryServerAdapter.cs:165`, `src/Noctis.Core/Services/PersistenceService.cs:294`)
- **Area / sweep:** Security / sec-net · **Category:** data-loss-race · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: low)

**Evidence**

```
LibraryServerAdapter.cs:117-127
    public async Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes)
    {
        var playlists = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
        var playlist = playlists.FirstOrDefault(p => p.Id == id);
        if (playlist is null) return false;
        ...
        playlist.TrackIds.AddRange(add);
        playlist.ModifiedAt = DateTime.UtcNow;
        await _persistence.SavePlaylistsAsync(playlists).ConfigureAwait(false);
```

**Why it is a bug:** Track mutations in this adapter go through _marshal, but CreatePlaylistAsync (:107), UpdatePlaylistAsync (:117), DeletePlaylistAsync (:132) and ApplyPlaylistStateAsync (:165) do an unlocked load → modify → save on Kestrel threads. PersistenceService serializes only the file write (SaveJsonAsync per-path gate, PersistenceService.cs:566-577), not the read-modify-write. Scenario: two devices (the phone app pushing sync state and a Subsonic client adding a song), or one client sending two updatePlaylist calls in parallel. Both load the same snapshot, and the second save writes a list without the first change. The desktop also writes its in-memory Sidebar list (SidebarViewModel/PlaylistViewModel SavePlaylistsAsync(_sidebar.Playlists)) between a server save and the NotifyPlaylists reload.

**Impact:** A playlist addition, rename or deletion made from a client occasionally disappears without any error.

**Proposed fix (small):** Serialize all playlist load-modify-save sequences in the adapter with one SemaphoreSlim held across load and save, and take the same gate in PlaylistImportService.CreateAsync. Ideally, route desktop saves through the same gated store.

**Verifier votes**

- **confirmed** (refute lens): LibraryServerAdapter.cs:107-140 and :165-189 each run LoadPlaylistsAsync, change the list, then SavePlaylistsAsync, with no gate. Only the track mutations go through Run/_marshal (lines 49, 74-105, 146-163). PersistenceService.cs:294-302 plus the SaveJsonAsync gate at :566-577 serialize only the file write, not the load-modify-save. NoctisServer.cs:418/423/435/441 call these methods straight from concurrent Kestrel request handlers, and LibrarySyncService.cs:205 calls ApplyPlaylistStateAsync. So two overlapping requests can load the same snapshot, and the later save drops the earlier change. It needs truly concurrent playlist writes, so low severity fits.

### X08 — Relay-supplied Loon chunk size is cast to int unchecked: a chunk_size of 2^32 gives a 0-byte chunk and an endless send loop that takes one of the 3 request slots and never releases it

- **Location:** `src/Noctis/Services/Loon/LoonClient.cs:688` (also: `src/Noctis/Services/Loon/LoonMessages.cs:147`, `src/Noctis/Services/Loon/LoonClient.cs:398`, `src/Noctis/Services/Loon/LoonClient.cs:759`)
- **Area / sweep:** Security / sec-net · **Category:** security-dos · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
LoonClient.cs:688-697
            var chunkSize = (int)(_chunkSize > 0 ? _chunkSize : 65536);
            ulong sequence = 0;
            var offset = 0;

            while (offset < fileBytes.Length)
            {
                var remaining = fileBytes.Length - offset;
                var size = Math.Min(chunkSize, remaining);
                var chunk = new byte[size];
LoonMessages.cs:147  case 1: chunkSize = r.ReadUInt64(); break;
```

**Why it is a bug:** _chunkSize comes straight from the relay's Hello (ulong, never validated). The project has no CheckForOverflowUnderflow, so the (int) cast is unchecked. With 4294967296 it becomes 0, Math.Min(0, remaining) is 0, and offset never advances. The loop then sends empty ContentChunk frames as fast as the socket drains, holding one of the three _requestGate slots (:759) forever. After three requests, every Discord cover request waits out RequestGateWait and is answered empty. Values between 2^31 and 2^32-1 turn negative, and new byte[negative] throws on every request, so no art is ever served. Elsewhere the code treats the relay as untrusted: ResolveArtworkPath guards against traversal and MaxInboundMessageBytes caps inbound messages.

**Impact:** A misconfigured or compromised relay can put a thread into an endless send loop and permanently stop Discord artwork.

**Proposed fix (trivial):** Clamp when reading the Hello or before use, for example `var chunkSize = (int)Math.Clamp(_chunkSize == 0 ? 65536UL : _chunkSize, 1024UL, 1UL << 20);`.

**Verifier votes**

- **confirmed** (refute lens): LoonClient.cs:398 stores hello.Constraints.ChunkSize unvalidated, and LoonMessages.cs:147 reads it as a raw ReadUInt64. No props, targets or csproj enables CheckForOverflowUnderflow, so the (int) cast at :688 is unchecked. A value of 2^32 becomes 0; Math.Min(0, remaining)=0 (:695), and offset never advances (:704), so the loop keeps sending empty chunks. A value in [2^31, 2^32) becomes negative, `new byte[size]` throws, and the catch at :710-713 sends CloseResponse, so no art is served. The claim that the slot is held 'forever' is overstated. The loop exits once SendAsync returns false (:701-702, :779/:791), which happens when the socket closes or aborts, and the finally at :493 then releases _requestGate. It stays pinned only while the relay keeps the connection open. The relay is also the component that serves the art to Discord, so it could deny artwork anyway. The code's own comments (:474-478, :719-721) treat the relay as potentially hostile, so this is a real input-validation gap, but low severity.

### X10 — A content pack, which installs with no approval and works in restricted mode, can replace the English (or current-language) text of the plugin consent/safety dialogs and destructive-action labels

- **Location:** `src/Noctis/Services/Plugins/ContentPack.cs:386` (also: `src/Noctis.UI/Localization/Loc.cs:117`, `src/Noctis/Services/Plugins/PluginHost.cs:724`, `src/Noctis/ViewModels/SettingsViewModel.cs:215`, `src/Noctis/ViewModels/SettingsViewModel.cs:267`, `src/Noctis/Views/RemoveFromLibraryDialog.axaml:58`, `docs/PLUGINS.md:527`)
- **Area / sweep:** Security / sec-data · **Category:** plugin-trust · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
ContentPack.cs:365-369 accept any culture, including shipped ones such as "en":
'''
var culture = RequiredString(o, "culture", rel).Trim();
if (!CultureName.IsMatch(culture)) throw ...;
try { culture = CultureInfo.GetCultureInfo(culture).Name; }
'''
ContentPack.cs:383-388 keep any key the app knows:
'''
var value = p.Value.GetString()!;
...
var en = english(p.Name);
if (en is null || !PlaceholdersFit(en, value)) { ignored++; continue; }
strings[p.Name] = value;
'''
Loc.cs:117-121, the overlay wins for the current culture chain (en-US → en):
'''
if (overlays.Count > 0)
    for (var c = _culture; !c.Equals(CultureInfo.InvariantCulture); c = c.Parent)
        if (overlays.TryGetValue(c.Name, out var strings) && strings.TryGetValue(key, out var value))
            return value;
'''
PluginHost.cs:724-729, a pack is Active with no approval, even in restricted mode:
'''
else if (plugin.IsContentPack)
{
    // Restricted mode is about code; a content pack toggles freely and is never started.
    plugin.CanToggle = true;
    plugin.Status = plugin.IsEnabled ? PluginStatus.Active : PluginStatus.Disabled;
'''
```

**Why it is a bug:** The approval model for code plugins depends on text that comes from Loc: Plugins.SafetyNote, Plugins.EnableTitle/EnableBody, Plugins.Perm.* (the permission list in the approval dialog, SettingsViewModel.cs:210-221 and PluginHost.cs:53), Plugins.TurnOnBody, and Plugins.Warning (SettingsView.axaml:5208). Content packs are presented as harmless ('Data only ... No code runs, so it needs no approval and works with community plugins off', Plugins.ContentNote). The install flow (SettingsViewModel.cs:267-270) activates a pack right away with no dialog. A pack declaring culture "en" therefore overrides these strings for every English-UI user without them selecting a language. Example: an 'Aurora theme pack' also ships languages/en.json that sets Plugins.SafetyNote to 'This plugin is verified by Noctis and runs in a sandbox', Plugins.Perm.PlaybackControl to 'Change the colour theme', and RemoveFromLibrary.MoveRecycleBin to 'Remove from library only'. The code plugin distributed next to it then gets approved under false pretences, and the relabelled delete option sends files to the Recycle Bin (or deletes them permanently on drives without one, per the separate RecycleBin finding). docs/PLUGINS.md:527-529 documents that packs replace shipped strings. The defect is that trust-critical strings are not excluded.

**Impact:** A no-code, no-approval pack can mislead the user into approving a malicious code plugin, turning off restricted mode, or deleting files they meant to keep.

**Proposed fix (small):** Do not let pack overlays replace trust-critical keys. In ParseLanguage, skip (count as ignored) any key under Plugins.*, RemoveFromLibrary.*, delete/confirm dialog keys and update-dialog keys. Alternatively, apply overlays for shipped cultures only after the user explicitly picks '<language> · by <pack>' in Settings → Language, instead of whenever the current UI culture matches.

**Verifier votes**

- **confirmed** (refute lens): The mechanism is as described. ParseLanguage (ContentPack.cs:365-369) accepts 'en'. It keeps any known key whose placeholders fit (:386-388). ContentCatalog.Update (:595-603) passes these to Loc.SetOverlays, and Loc.Get (Loc.cs:117-121) serves the overlay first across the en-US -> en chain. Content packs are activated with no approval and ignore restricted mode (PluginHost.cs:715, :724-729), and install shows no dialog (SettingsViewModel.cs:267-270). The trust dialogs read these keys: SafetyNote/TurnOnBody (SettingsViewModel.cs:190-194), EnableBody plus Perm.* via PluginPermissionText (:213-219, PluginHost.cs:53), and RemoveFromLibrary.MoveRecycleBin (RemoveFromLibraryDialog.axaml:58). docs/PLUGINS.md:527-529 documents that packs replace shipped strings, but it does not exempt trust-critical ones. Exploiting this still requires the user to install both a malicious pack and a malicious code plugin, so severity stays low.

### X13 — A corrupt, encrypted or LZMA-compressed plugin .zip throws InvalidDataException that the install flow does not catch: install silently does nothing

- **Location:** `src/Noctis/Services/Plugins/PluginInstaller.cs:57` (also: `src/Noctis/Services/Plugins/PluginInstaller.cs:39`, `src/Noctis/ViewModels/SettingsViewModel.cs:243`, `src/Noctis/Services/Plugins/PluginHost.cs:1062`)
- **Area / sweep:** Security / sec-files · **Category:** error-handling · **Platforms:** all
- **Verification:** confirmed (finder confidence: likely; finder severity: low)

**Evidence**

```
PluginInstaller.cs:37-39,54-60
    using (zip)
    {
        if (zip.Entries.Count > MaxEntries) ...
        PluginManifest manifest;
        try
        {
            using var reader = new StreamReader(manifestEntry.Open());
            manifest = PluginManifest.Parse(reader.ReadToEnd());
        }
        catch (PluginManifestException ex) { throw new PluginInstallException(ex.Message); }
SettingsViewModel.cs:243-244  try { (package, existing) = Plugins.InspectPackage(zipPath); }
    catch (PluginInstallException ex) { ... }
```

**Why it is a bug:** ZipArchive reads the central directory when Entries is first accessed, and Entries is documented to throw InvalidDataException when 'the zip archive is corrupt' (https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchive.entries). ZipArchiveEntry.Open throws the same exception for an unsupported compression method or corrupt data. Only the OpenRead call (lines 31-35) is guarded for it.

Scenario: the user installs a password-protected zip or one made by 7-Zip with LZMA. InvalidDataException leaves Inspect and InstallPluginPackageAsync, which catches only PluginInstallException, and is rethrown by the generated AsyncRelayCommand. App.axaml.cs:73-79 logs it and marks it handled.

**Impact:** The Install button appears to do nothing: no status message and no explanation that the zip is damaged or uses an unsupported format.

**Proposed fix (trivial):** In Inspect, wrap the body after OpenRead with `catch (InvalidDataException ex) { throw new PluginInstallException("The zip is damaged or uses an unsupported compression/encryption: " + ex.Message); }`. Also add InvalidDataException to the catch in PluginHost.InstallPackage at line 1063.

**Verifier votes**

- **confirmed** (refute lens): PluginInstaller.cs:31-35 guards InvalidDataException only around ZipFile.OpenRead. In Read mode, ZipArchive reads the central directory lazily, so zip.Entries (:39, :41, :178) and manifestEntry.Open()/ReadToEnd (:57-58) can still throw InvalidDataException: for a corrupt central directory, for an unsupported method such as LZMA/BZip2/AES (99) via ThrowIfNotOpenable, or for bad deflate data. The catch at :60 handles only PluginManifestException. SettingsViewModel.cs:243-248 catches only PluginInstallException, and so does PluginHost.cs:1062-1063. The exception therefore leaves the [RelayCommand] InstallPluginFromFileAsync (:228-235). The AsyncRelayCommand rethrows it on the dispatcher, and App.axaml.cs:73-79 logs it and sets Handled. ShowPluginStatus is never called, so the Install button appears to do nothing. The finding is accurate, and it is an edge case (low).

### X14 — A failed load of wrap_archive.json is followed by a Save that overwrites the permanent Wrap archive with only the years still in the live log

- **Location:** `src/Noctis/Services/WrapArchiveService.cs:167`
- **Area / sweep:** Security / sec-files · **Category:** data-loss · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: low)

**Evidence**

```
WrapArchiveService.cs:158-171
    try
    {
        if (File.Exists(_filePath))
        {
            var json = File.ReadAllText(_filePath);
            _entries = JsonSerializer.Deserialize<List<ArchivedWrap>>(json) ?? new List<ArchivedWrap>();
            return;
        }
    }
    catch (Exception ex) { DebugLogger.Error(..., "WrapArchive.Load", ex.Message); }
    _entries = new List<ArchivedWrap>();
(then EnsureArchived -> line 151: if (changed) Save();)
```

**Why it is a bug:** Any load failure leaves _entries as an empty list: a JsonException from a truncated file after a crash (the .tmp file is renamed without a flush), an IOException from a transient lock, or an older build reading a newer schema. The next EnsureArchived call, made when Wrap opens with any past-year events, adds those years and Save() atomically replaces wrap_archive.json.

The archive exists because the live log is capped at 10,000 events and older years are gone from it. Every year that was only in the archive is therefore lost for good. The load error is logged; the overwrite is not.

**Impact:** Permanent loss of past years' Wrap recaps after one unreadable read.

**Proposed fix (trivial):** On load failure, set a `_loadFailed` flag and skip Save() for the rest of the session. Alternatively move the unreadable file aside (wrap_archive.json.corrupt-<timestamp>) before writing a new one.

**Verifier votes**

- **confirmed** (refute lens): WrapArchiveService.cs:155-172: on any exception the catch at :167-170 logs it and falls through to `_entries = new List<ArchivedWrap>()` at :171. There is no flag, so the empty list is treated as authoritative. EnsureArchived (:95-152) then adds every past year still in the live log. With an empty list `existing` is null for each year, so changed=true and Save() at :151 runs. Save at :174-187 does a tmp write plus File.Move(overwrite: true), which replaces the unreadable file, and any years held only in the archive are lost. This is reachable: a past-year event is almost always in the log. The trigger, though, is rare: a transient IOException, or a truncated file after a power loss (a truncated file's data is mostly lost already). Low severity is right.

### X15 — The yt-dlp executable is downloaded and silently auto-replaced with no checksum or signature check, then executed

- **Location:** `src/Noctis/Services/YouTube/YtDlpTool.cs:126` (also: `src/Noctis/Services/YouTube/YtDlpTool.cs:247`, `src/Noctis/Services/YouTube/YtDlpTool.cs:312`, `src/Noctis/Services/YouTube/YtDlpTool.cs:486`, `src/Noctis/Services/UpdateService.cs:479`)
- **Area / sweep:** Security / sec-net · **Category:** security-supply-chain · **Platforms:** all
- **Verification:** confirmed (finder confidence: confirmed; finder severity: medium)

**Evidence**

```
YtDlpTool.cs:129-147
        var url = YtDlpParsing.ReleaseDownloadUrl();
        var temp = InstalledPath + ".part";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ...
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        File.Move(temp, InstalledPath, overwrite: true);
YtDlpParsing.cs:34  ReleaseBaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/"
```

**Why it is a bug:** InstallCoreAsync writes yt-dlp.exe / yt-dlp_macos / yt-dlp_linux into <data>/tools, and RunProcessAsync (YtDlpTool.cs:486-500) then executes it. Nothing checks integrity. yt-dlp publishes SHA2-256SUMS and SHA2-256SUMS.sig (a GPG signature against the key at https://github.com/yt-dlp/yt-dlp/blob/master/public.key) with every release: README "Release Files" at https://github.com/yt-dlp/yt-dlp#release-files, and the asset list of release 2026.08.19 confirmed through the GitHub API. The replacement also happens without user action. CheckForUpdateAsync (:247-251) calls Updater = InstallCoreAsync from the quiet once-per-session EnsureSessionUpdateCheckAsync (YouTubeDownloadViewModel.cs:73, LyricsBackgroundPickerViewModel.cs:288, SettingsViewModel.Features.cs:229), and again with force after any 403 (:431). The app's own updater holds itself to a stricter rule: it fails closed without a SHA256SUMS manifest (UpdateService.cs:479-481, 536-551). Scenario: a TLS-intercepting proxy or antivirus with an injected root, or a tampered CDN object on objects.githubusercontent.com, serves a different binary. It is installed silently and run with the user's rights on the next YouTube search.

**Impact:** Arbitrary code can run under the user's account through an unattended auto-update, and the app has nothing that would detect a corrupted or tampered binary.

**Proposed fix (small):** Resolve the release by tag, not latest, so the version and the binary match. Download SHA2-256SUMS from that release, compute the SHA-256 of the .part file and compare it before File.Move. Fail closed on a mismatch or a missing entry. Optionally verify SHA2-256SUMS.sig against the pinned yt-dlp public key. Cap the download size, and make FetchLatestVersionAsync (:312) use HttpSafety.ReadStringBoundedAsync.

**Verifier votes**

- **confirmed** (refute lens): YtDlpTool.cs:126-157 streams YtDlpParsing.ReleaseDownloadUrl() (https://github.com/yt-dlp/yt-dlp/releases/latest/download/, YtDlpParsing.cs:34,47) to .part and then runs File.Move(temp, InstalledPath, overwrite:true) at :147 with no hash or signature check. RunProcessAsync (:486-514) executes the result. The auto-replace path is real: EnsureSessionUpdateCheckAsync (:190-196) leads to CheckForUpdateAsync, which calls Updater at :247-251, and the forced check after a 403 is at :431. The contrast with UpdateService.cs:479-481 and :536-551, which fail closed on SHA256SUMS, is accurate. However, the transfer is HTTPS-only and the repo has no certificate-validation bypass (grep found none). Exploiting this needs a trusted-root TLS interceptor or a compromise of GitHub's infrastructure. In that stated MITM scenario the proposed same-origin SHA2-256SUMS check would not help, since the interceptor can rewrite both files; only pinned-key signature verification would. This is missing defense-in-depth with an edge-case threat model, so low, not medium.

## Phase 1 — Logging coverage map

State changes and failure paths with **no logging at all**, per lane. Each row gives where a log line belongs and what it should record. Phase 2 adds the audio-relevant ones (see Phase 2).
### Audio pipeline

| Area | Location | Missing | Suggested |
|---|---|---|---|
| Pause dropped | `src/Noctis/Services/VlcAudioPlayer.cs:4243` | When `_player.IsPlaying` is false the pause is silently dropped (see the Pause finding). | else-branch: DebugLogger.Info(Playback, "Pause.Ignored", $"vlcState={_player.State}, engineTail={EngineActiveTailSegment()!=null}, session={CurrentSessionId}") |
| Resume dropped / pause-resume transitions | `src/Noctis/Services/VlcAudioPlayer.cs:4299` | Resume with !_isPaused is a silent no-op, and successful pause/resume are never logged on the player side (only the VM's intent is). | Log "VLC.Paused"/"VLC.Resumed" (engine, fade, vlcState) and "Resume.Ignored" (isPaused, vlcState). |
| Stop | `src/Noctis/Services/VlcAudioPlayer.cs:4342` | The Stop worker (engine clear, standby release, native stop) has no log line. | DebugLogger.Info(Playback, "VLC.Stop", $"session={CurrentSessionId}, engine={_gaplessEngine}") |
| Seek failures / dropped seeks | `src/Noctis/Services/VlcAudioPlayer.cs:4936` | The seek worker's catch{} swallows exceptions; seeks skipped because Length<=0 (4835-4839) and Seek() returning at len<=0 (4400-4401) are not logged. | Warn "Seek.Failed" with the exception type/message; Info "Seek.Dropped" with the reason (lengthZero) and targetMs. |
| Engine ring write drops | `src/Noctis/Services/VlcAudioPlayer.cs:3929` | The result of `seg.Write(...)` is ignored. false means abandoned, or a 2 s timeout (dead render side, e.g. after the tempo-stretch render crash); decoded audio is lost with no trace. | Rate-limited Warn "GaplessEngine.WriteDropped" (slot, reason=timeout/abandoned/eos, buffered). |
| Engine play callback exceptions | `src/Noctis/Services/VlcAudioPlayer.cs:3936` | EnginePlay's catch{} swallows everything on the decoder thread (Marshal.Copy, checked overflow). | First-N / rate-limited Warn "GaplessEngine.PlayCallbackThrew" with the exception type. |
| Mid-track underruns | `src/Noctis/Services/GaplessSpliceCore.cs:615` | A 0-read mid-track (underrun pad plus 50 ms refill hysteresis) is never logged or counted; underruns are the main buffer event for dropouts and buzz. | Counter plus a rate-limited Info "GaplessEngine.Underrun" (buffered samples, segment slot, silentSamples) posted off the render thread, like StallProbe does. |
| Render-thread death ignored | `src/Noctis/Services/GaplessSink.cs:226` | PlaybackStopped with an exception is dropped silently when the sender is not `_out` (line 226) or a rebuild is in flight (line 229), which hides render-thread crashes such as the ArrayTypeMismatchException. | Warn "GaplessEngine.StopIgnored" with the exception type/message and the reason (staleSender/rebuilding). |
| ReplayGain application | `src/Noctis/Services/VlcAudioPlayer.cs:2009` | Neither the applied RG (mode, tag found, dB) nor a tag-read failure (catch at 2093-2096 returns null,null) is logged. | Info "ReplayGain.Applied" (mode, trackDb, albumDb, appliedDb, preamp); Warn "ReplayGain.TagReadFailed" (exception type, file name). |
| Session volume reassert give-up | `src/Noctis/Services/VlcAudioPlayer.cs:4120` | After the 1.5 s poll the loop exits silently even when the level never landed on an active session (the 'plays but no audio' / full-volume blip class). | After the loop: Warn "SessionVolume.ReassertGaveUp" (session, heldActive). |
| EQ apply branch | `src/Noctis/Services/VlcAudioPlayer.cs:1496` | Which branch ran (set, neutralize-to-unity, or unset, which can restart the aout filters) is not logged, although EQ-change dropouts are a known issue. | Info "EQ.Applied" (mode=set/neutral/unset, playing, standbyPrepared, version). |
| Transport/audio settings changes | `src/Noctis/Services/VlcAudioPlayer.cs:2121` | SetCrossfade, SetGapless, SetPlayPauseFade, SetNormalization and SetPlaybackRate/SetPitchSemitones (player side) change audio-path behavior with no log. | Info "Audio.Setting" with the name and new value (crossfade enabled/duration/overlap/fadeOut, gapless, rate, pitch). |
| Pause/resume state changes | `src/Noctis/Services/VlcAudioPlayer.cs:4252` | Pause() and Resume() (4305) log nothing: not whether the engine sink was paused or resumed, whether a fade ran, or the drain wait. | DebugLogger.Info(Playback, "Playback.Pause", $"engine={_gaplessEngine}, fade={PlayPauseFadeArmed}, drainMs={...}") and the same for "Playback.Resume". |
| Render thread stopped without an exception | `src/Noctis/Services/GaplessSink.cs:221` | `if (_disposed // e.Exception == null) return;` silently ignores an unexpected clean stop (for example NAudio's 0-read end-of-stream path), which leaves the engine dead with no trace. | Warn "GaplessEngine.StoppedUnexpectedly" when !_disposed and no Stop/rebuild of ours is in progress. |
| Dropped device-loss notifications | `src/Noctis/Services/GaplessSink.cs:226` | The stale-sender return (226) and the `_rebuilding == 1` path (229) drop PlaybackStopped errors without logging. | Warn "GaplessEngine.StopIgnored" with reason=staleSender/rebuildInProgress and the exception type. This would expose the RebuildLoop race. |
| Device change and format | `src/Noctis/Services/GaplessSink.cs:254` | DeviceChanged and SinkRebuilt (300) do not log the old and new endpoint (a hashed ID or friendly name), the new device's mix format, or the fixed engine format. | Include newRate/newChannels vs SampleRate/Channels plus an endpoint hash. This makes the locked mono/low-rate format visible. |
| Sink pause/resume exceptions | `src/Noctis/Services/GaplessSink.cs:331` | `try { current.Pause(); } catch { }` and Resume (339) swallow failures silently. | Rate-limited Warn "GaplessEngine.PauseFailed"/"ResumeFailed" with the exception type. |
| Dropped PCM on the engine input | `src/Noctis/Services/VlcAudioPlayer.cs:3929` | The return value of `seg.Write(...)` is ignored, so a 2 s timeout (full ring, dead render side) drops audio with no log. The catch-all at 3936 also swallows every exception on the decoder thread. | When Write returns false and the segment is neither Abandoned nor EndOfStream, rate-limited Warn "GaplessEngine.WriteDropped" with slot and buffered ms. Log the first exception in the catch. |
| Mid-track underrun in the splice provider | `src/Noctis/Services/GaplessSpliceCore.cs:615` | A mid-track 0-read (underrun that arms the refill hold) is logged only through the NOCTIS_ENGINE_TAP ReadTrace. | Queue off-thread (like StallProbe) a rate-limited Warn "GaplessEngine.Underrun" with BufferedSamples and whether the refill hold armed. |
| Session volume write failures | `src/Noctis/Services/VlcAudioPlayer.cs:1128` | ApplyRampLevel ignores SetLevel()==false, and WindowsSessionVolume.SetLevel swallows COM exceptions (WindowsSessionVolume.cs:90-96). A volume slider that does nothing leaves no trace. | Rate-limited Warn "Volume.SessionWriteFailed" with the HRESULT or exception type and milli. |
| Session reassert giving up | `src/Noctis/Services/VlcAudioPlayer.cs:4079` | OnGaplessSinkRebuilt (4073-4079) and ScheduleSessionVolumeReassert (4114-4120) exit after 1.5 s without ever holding the active session, silently. The user level may then be missing, possibly at 100%. | Warn "SessionVolume.ReassertGaveUp" with origin=rebuild/trackStart and the last Resolve counts. |
| Callback sink dropped blocks and faults | `src/Noctis/Services/WasapiGainOutput.cs:280` | AddSamples failures (dropped PCM) and PlaybackStopped (92-95) go only to the opt-in NOCTIS_WASAPI_LOG file, never to DebugLogger or the session log. | Rate-limited DebugLogger.Warn(Playback, "Exclusive.WriteDropped") and DebugLogger.Warn(Playback, "Exclusive.PlaybackStopped", exception). |
| Render-chain settings changes | `src/Noctis/Services/VlcAudioPlayer.cs:2244` | SetPlaybackRate/SetPitchSemitones (2244-2253) change the engine's render chain with no log, so the reported TempoStretch render-thread crash cannot be tied to a user action. | Info "Engine.RateChanged" with rate, pitch and effective stretch rate. |
| Sink startup timing and endpoint | `src/Noctis/Services/GaplessSink.cs:128` | The sink constructor (device enumeration, Init and first Play) has no timing or endpoint line. Only rate and channels are logged later as GaplessEngine.Wired. | Info "GaplessEngine.SinkOpened" with elapsedMs, endpoint hash and role, mix format and chosen engine format. |
| Queue restore | `src/Noctis/ViewModels/PlayerViewModel.cs:1505` | RestoreQueueStateAsync logs nothing: current track, saved position, resume target, restored vs saved counts for UpNext/History/cycle, the #91 fallback, mute/shuffle/repeat | DebugLogger.Info(Playback, "Queue.Restored", $"current={id} posSec={positionSeconds:F1} resumeMs={_resumePositionMs} upNext={restoredUpNext.Count}/{state.UpNextIds.Count} history=... muted={IsMuted} shuffle=... repeat=... fallback={usedUpNext0}"), mirrored to DebugLog |
| Resume/start seek selection | `src/Noctis/ViewModels/PlayerViewModel.cs:2034` | PlayTrack never logs which seek source won (AutoMix entry / restored resume / StartTimeMs / SavedPositionMs) or its value | Add seekMs and source to the existing PlayTrack DebugLogger entry |
| Position stale-guard rejections | `src/Noctis/ViewModels/PlayerViewModel.cs:2602` | Ticks dropped by the settle, stale and extended-settle guards (2602-2622) are silent, so slider snap-back and freeze reports can't be diagnosed | Rate-limited (1/s) DebugLogger.Info(Playback, "Position.Rejected", $"guard=... latestMs=... targetMs=... msSinceSeek=... rate={PlaybackRate}") |
| Seek entry points | `src/Noctis/ViewModels/PlayerViewModel.cs:614` | SeekToPosition (click, SMTC/MPRIS/macOS scrub, SeekBy/SeekTo, lyrics) has no log; only EndSeek logs | DebugLogger.Info(Playback, "SeekToPosition", $"targetMs=... state={State} track={CurrentTrack?.Id}") |
| StopAndClear reason | `src/Noctis/ViewModels/PlayerViewModel.cs:1246` | StopAndClear wipes the queue from six call sites (error cascade, empty library, drained queue, deleted current track, repeat-all with nothing playable) with no record of which one | Add a reason parameter and DebugLog.Write("Queue", $"StopAndClear: {reason}, upNext={UpNext.Count}, history={History.Count}") |
| Library update touching the current track | `src/Noctis/ViewModels/PlayerViewModel.cs:3158` | Silent advance or stop when the current track leaves the library | DebugLogger.Info(Queue, "CurrentTrackRemoved", $"state={State} upNext={UpNext.Count} action=advance/stop") |
| Background queue snapshot failure | `src/Noctis/ViewModels/PlayerViewModel.cs:1498` | The failure goes only to System.Diagnostics.Debug.WriteLine, which is invisible in release and the session log | DebugLog.Write("Queue", $"Queue snapshot failed: {msg}") |
| Queue restore failure | `src/Noctis/ViewModels/MainWindowViewModel.cs:695` | Restore exceptions are written only to Debug.WriteLine | DebugLog.Write("Queue", $"Queue restore failed: {ex}") |
| External track restore | `src/Noctis/ViewModels/PlayerViewModel.cs:1633` | Per-file restore failures are written only to Debug.WriteLine | DebugLog.Write("Queue", ...) with the file name, not the full path |
| AdvanceQueue re-entrancy drop | `src/Noctis/ViewModels/PlayerViewModel.cs:2087` | A second advance is silently discarded | DebugLogger.Warn(Queue, "AdvanceQueue.Reentrant", $"reason={reason}") |
| Remote transport commands | `src/Noctis/Services/SmtcService.cs:117` | SMTC button presses, MPRIS ExecutePlayerCommand/Set and macOS remote commands are never logged, so media-key races can't be reconstructed | DebugLogger.Info(Playback, "Smtc.Button" / "Mpris.Command" / "MacRemote.Command", $"{button} vmState={_player.State}") |
| Mute changes | `src/Noctis/ViewModels/PlayerViewModel.cs:635` | ToggleMute, UnmuteForAdjust and the restored mute leave no record | DebugLogger.Info(Playback, "Mute", $"muted={IsMuted} source=toggle/adjust/restore") |
| Aborted track open | `src/Noctis/Services/VlcAudioPlayer.cs:2810` | PlayInternal returns silently when its parse is cancelled (the root of the load-abort finding) | DebugLogger.Warn(Playback, "VLC.Play.Aborted", $"session={sessionId} path={Path.GetFileName(filePath)}") |
| Ignored pause | `src/Noctis/Services/VlcAudioPlayer.cs:4243` | A pause dropped because IsPlaying is false leaves no trace | else DebugLogger.Info(Playback, "Pause.IgnoredNotPlaying", $"vlcState={_player.State}") |
| Radio refill worker | `src/Noctis/ViewModels/PlayerViewModel.cs:2428` | Task.Run has no try/catch/finally; an exception in BuildSimilar goes unobserved and leaves _radioRefillInFlight=true for the rest of the session (radio never refills) | Wrap in try/catch that logs via DebugLog.Write, and reset _radioRefillInFlight in a finally posted to the UI thread |

### UI performance

| Area | Location | Missing | Suggested |
|---|---|---|---|
| Artwork decode failures in every list thumbnail | `src/Noctis/Controls/CachedImage.cs:373` | Decode or load failures go only to System.Diagnostics.Debug.WriteLine, so they are invisible in release, the session log and crash.log. Covers that stay blank cannot be diagnosed. | DebugLogger.Error(DebugLogger.Category.UI, "ArtworkLoadFailed", $"path={path}, width={decodeWidth}, {ex.GetType().Name}: {ex.Message}"), rate-limited per path. |
| Programmatic scroll resets in library lists | `src/Noctis/Views/LibrarySongsView.axaml.cs:232` | Nothing records when the view forces Offset=0 or why, so the reported scroll jumps cannot be traced to a filter change versus a library reload. | DebugLogger.Info(Category.UI, "SongsScrollReset", $"action={e.Action}, filter={_vm.SearchText}, saved={_vm.SavedScrollOffset}"). Do the same in LibraryAlbumsView.cs:184 and LibraryArtistsView.cs:121. |
| Removed-tracks list refresh | `src/Noctis/ViewModels/SettingsViewModel.cs:5353` | catch { return; } swallows a settings read failure silently. | DebugLog.Write("Settings", $"RemovedTracks refresh failed: {ex.Message}") before returning. |
| Playlist drag-reorder commit | `src/Noctis/Views/PlaylistView.axaml.cs:737` | A failed MoveTrack or MoveTracks (including the persistence save) goes only to Debug.WriteLine, so a reorder that did not persist leaves no trace. | DebugLogger.Error(Category.State, "PlaylistMoveFailed", $"from={from}, to={to}, block={block?.Count}, {ex.Message}"). |
| Folder tree refresh | `src/Noctis/ViewModels/LibraryFoldersViewModel.cs:128` | A failed RefreshAsync goes only to Debug.WriteLine; the Folders page just shows stale or empty data. | DebugLog.Write("Folders", ex) or DebugLogger.Error(Category.UI, "FoldersRefreshFailed", ex.Message). |
| Large tool-dialog population (freeze diagnosis) | `src/Noctis/ViewModels/OrganizeFilesViewModel.cs:75` | No record of row counts or build time when Organize Files, Convert, Bulk Lyrics, Metadata Finder, Duplicate Finder or Send to Folder fill their lists, so freeze reports cannot be tied to row count. | DebugLogger.Info(Category.UI, "OrganizePreview", $"rows={plan.Count}, planMs={sw.ElapsedMilliseconds}"), with the same one-liner in the other dialog VMs' constructors or RebuildPlan. |
| Queue bulk mutation cost | `src/Noctis/Views/MainWindow.axaml.cs:1833` | RemoveManyFromQueue logs only the count; the time spent in the per-event selection remap and renumbering is not recorded. | Wrap the call in a Stopwatch and log DebugLogger.Info(Category.Queue, "RemoveManyUi", $"count={n}, ms={elapsed}") when elapsed is over 100 ms. |
| Settings persistence failure | `src/Noctis/ViewModels/SettingsViewModel.cs:2559` | A failed settings save (disk full, locked settings.json, serialization error) is written only to Debug.WriteLine, which is compiled out of Release builds; the user's setting change silently does not persist and Copy Logs shows nothing. | DebugLog.Write("Settings", $"Save failed: {ex}") in the catch; same for the merge failure at SettingsViewModel.cs:2636. |
| Startup: queue restore / auto-scan / watcher start / update check | `src/Noctis/ViewModels/MainWindowViewModel.cs:695` | RestoreQueueStateAsync, the startup auto-scan (:710), watcher start (:717), Wrap archive (:734) and silent update check (:750) failures go only to Debug.WriteLine; the session log (crash.log mirror) has no trace of a failed startup step. | DebugLog.Write("Startup", ...) with the exception in each catch; also log the restored queue size and how many saved ids failed to resolve in PlayerViewModel.RestoreQueueStateAsync (PlayerViewModel.cs:1515-1545). |
| Shutdown save steps | `src/Noctis/ViewModels/MainWindowViewModel.cs:978` | Every ShutdownAsync step (settings save :978, play-history flush :980, tag-write flush :987, queue snapshot :991, library flush :995, Discord disconnect :1004, plugin unload :956) logs failures only via Debug.WriteLine, so data-loss-at-exit reports cannot be diagnosed from crash.log. | DebugLog.Write("Shutdown", $"<step> failed: {ex.Message}") per catch, plus one line with total shutdown duration. |
| Drag-and-drop import failures | `src/Noctis/ViewModels/MainWindowViewModel.cs:2733` | MoveFileIntoManagedRoot move/copy failures (:2724, :2733), folder enumeration failures (:1103) and the drop handler catch (Views/MainWindow.axaml.cs:1418) are Debug.WriteLine only; a drop that silently imports nothing leaves no session-log entry. | DebugLog.Write("DropImport", ...) for each failure and a summary line (files accepted / moved / imported / failed). |
| Metadata editor save side effects | `src/Noctis/ViewModels/MetadataViewModel.cs:2615` | File rename failures (catch { } at :2615), .lrc/.txt sidecar write failures (:2477, :2492) and embedded-artwork write failures (:2515, :2538) are swallowed without any log. | DebugLog.Write("Metadata", $"Rename failed {old} -> {new}: {ex.Message}") and similar for sidecar/artwork writes. |
| UI-thread navigation cost | `src/Noctis/ViewModels/MainWindowViewModel.cs:1942` | No timing is recorded for view resolution in Navigate / OpenArtistDiscography (:2247) / OpenAlbumDetail (:2190), so 'the app lags when I open X' reports cannot be tied to a page; DebugLogger.Info at :1913 is Developer-Mode only and has no duration. | Stopwatch around the CurrentView resolution and detail-VM construction; DebugLog.Write("UI", $"Navigate {key} took {ms} ms") when > 100 ms. |
| External open failures | `src/Noctis/ViewModels/MainWindowViewModel.cs:851` | OpenExternalFilesAsync / QueueExternalMediaAsync (:876) failures ("Open with Noctis") are Debug.WriteLine only. | DebugLog.Write("OpenWith", ex) with the file count. |
| Lyrics surface visibility tally | `src/Noctis/ViewModels/LyricsViewModel.cs:3171` | No log when the visible-surface count changes or when the sync timer is parked or started because of it; the stuck count in finding 1 is invisible in Copy Logs. | DebugLogger.Info(DebugLogger.Category.UI, "LyricsSurface", $"visible={visible} count={_visibleLyricsSurfaces} timer={_lyricsSyncTimer.IsEnabled}") |
| Word-clock frame loop start/stop | `src/Noctis/ViewModels/LyricsViewModel.cs:3157` | The per-frame RequestAnimationFrame loop starts (3157) and stops (3192/3206) silently. | Dev-mode log on start and stop with the host type (main window or mini player) and the reason it stopped (WantsWordClock false / no host). |
| Mini player teardown | `src/Noctis/Views/MiniPlayerWindow.axaml.cs:1137` | OnClosed logs nothing: no record of form, pill-spin state, drawer animation state or lyrics-surface registration at close. | DebugLogger.Info(Category.UI, "Mini.Closed", $"form={Vm?.Form} pillSpinning={_pillSpinner?.IsSpinning} drawerAnimating={Vm?.IsDrawerAnimating} lyricsRegistered={_lyricsSurfaceRegistered}") |
| Visibility-poll timers | `src/Noctis/Helpers/FlowingArtworkAnimator.cs:186` | StartVisibilityPoll/StopVisibilityPoll (also SpectrumVisualizer.cs:291) start background 250 ms timers without any trace. | Dev-mode log on poll start/stop with the host control name, so hidden-surface polling shows up in session logs. |
| Video backdrop open failure | `src/Noctis/Controls/VideoBackdrop.cs:229` | `catch { return; }` swallows LibVLC/Media.Parse failures for the lyrics video or music-video backdrop. | DebugLogger.Warn(Category.Playback, "Backdrop.Open", $"src={Path.GetFileName(source)} {ex.Message}") |
| Animated cover decoder open failure | `src/Noctis/Controls/AnimatedCoverFeed.cs:296` | `catch { }` in Session.Open swallows LibVLC player/media creation failures; only success is logged (Cover.Play). | DebugLogger.Warn(Category.Playback, "Cover.PlayFailed", $"src={Path.GetFileName(source)} {ex.Message}") |
| Artwork decode failure | `src/Noctis/Controls/CachedImage.cs:373` | Decode failures go only to System.Diagnostics.Debug.WriteLine, so they are absent from the session log and crash.log in release builds. | DebugLog.Write("Artwork", $"decode failed '{path}' @{decodeWidth}: {ex.Message}") (rate-limited, e.g. WriteOnce per path) |
| Playlist load failure | `src/Noctis/ViewModels/PlaylistViewModel.cs:409` | LoadTracks failure is Debug.WriteLine only; the playlist page silently keeps stale or empty rows. | DebugLogger.Error(Category.Error, "Playlist.LoadTracks", ex.Message) |
| Lyrics scroll anchoring | `src/Noctis/Views/LyricsView.axaml.cs:1248` | `catch { }` in ScrollToActiveLine (1248) and JumpToActiveLineWhenReady (1293), and in LyricsPanelView.axaml.cs:303/333, swallow anchor exceptions with no trace. | Route through the existing LyricsView.Trace(...) with the exception message. |

### Settings, dialogs, popups, commands

| Area | Location | Missing | Suggested |
|---|---|---|---|
| Settings save failure | `src/Noctis/ViewModels/SettingsViewModel.cs:2559` | A failed settings save logs only through Debug.WriteLine, which is compiled out in Release. A lost setting change leaves no trace in the session log or crash.log. | DebugLog.Write("Settings", $"Save failed: {ex.GetType().Name}: {ex.Message}"); DebugLogger.Error(DebugLogger.Category.Error, "Settings.SaveFailed", ex.Message); |
| Settings merge (on-disk re-base) failure | `src/Noctis/ViewModels/SettingsViewModel.cs:2636` | A failed merge logs only through Debug.WriteLine, so fields owned by other components can be reverted with nothing recorded. | DebugLog.Write("Settings", $"Merge of on-disk settings failed: {ex.Message}") |
| Persistence write failure (single choke point) | `src/Noctis.Core/Services/PersistenceService.cs:615` | The failed write of any JSON file (settings/library/playlists/queue) logs only through Debug.WriteLine before rethrowing; callers like LyricsViewModel swallow it with catch { }. | DebugLog.Write("Persistence", $"Failed to save {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}") before `throw;` |
| Audio-relevant setting changes | `src/Noctis/ViewModels/SettingsViewModel.cs:3684` | User toggles of Exclusive mode (3684), Gapless (4426), Song transitions/style (3635/3642), Sound Check (3678), ReplayGain mode/preamp (4404/4472), EQ on/off (4959) and Play/pause fade (3606) are not logged. An audio bug report can't show that the user changed the output path just before a dropout. Upmix is logged in VlcAudioPlayer.cs:2263; the others are not. | DebugLogger.Info(DebugLogger.Category.Playback, "Setting.Changed", $"{name}={value}") in each handler (mirrors to the session log in dev mode) |
| Audio settings snapshot at startup | `src/Noctis/ViewModels/SettingsViewModel.cs:2511` | No record of the effective audio configuration applied at load (exclusive, gapless, upmix, transitions, RG mode/preamp, EQ enabled/preset/preamp, play-pause fade). | DebugLog.Write("Audio", $"Settings applied: exclusive={ExclusiveAudioEnabled} gapless={GaplessPlaybackEnabled} upmix={UpmixMode} transitions={SongTransitionsEnabled}/{TransitionStyle} rg={ReplayGainMode}{ReplayGainPreampDb:+0.0} eq={EqualizerEnabled}/{SelectedEqPresetName}/{EqPreampDb:+0.0}") right after ApplyAudioSettings() |
| Reset all settings and data | `src/Noctis/ViewModels/SettingsViewModel.cs:5957` | No log that a full reset happened. All per-step failures (5968, 5977, 5986, 6008, 6026, 6044, 6062, 6080, 6092, 6107, 6118) use Debug.WriteLine only. The success status 'All settings and data have been reset.' is shown even if saving the defaults failed. | DebugLog.Write("Settings", "Reset all settings and data requested") at entry; DebugLog.Write("Settings", $"Reset step '<step>' failed: {ex.Message}") in each catch |
| Discord Rich Presence connect failure | `src/Noctis/ViewModels/SettingsViewModel.cs:4590` | On connect failure the toggle is silently flipped back with only Debug.WriteLine; no log and no user-visible reason. | DebugLog.Write("Discord", "Connect failed (Discord not running?) - toggle reverted") plus a transient status line |
| Loon relay connect/disconnect failure | `src/Noctis/ViewModels/SettingsViewModel.cs:4628` | Loon connect (4628) and disconnect (4641) failures use Debug.WriteLine only, so a missing Discord cover can't be diagnosed from logs. | DebugLogger.Warn(DebugLogger.Category.State, "Loon.ConnectFailed", ex.Message) |
| Launch-at-login registration failure | `src/Noctis/ViewModels/SettingsViewModel.cs:879` | When the OS refuses the autostart change, only the inline UI error is set; nothing is logged. | DebugLog.Write("Startup", $"Launch-at-login set {value} failed (ok={ok}, actual={actual})") |
| Lyrics background preference save failures | `src/Noctis/ViewModels/LyricsViewModel.cs:884` | PersistArtworkBackgroundPreferenceAsync (884) and SetBackgroundColor (945) swallow settings load/save failures with an empty catch. | catch (Exception ex) { DebugLog.Write("Lyrics", $"Could not persist lyrics background: {ex.Message}"); } |
| Add media folder failure | `src/Noctis/Views/SettingsView.axaml.cs:649` | A folder-picker or AddFolderPath failure is logged only through Debug.WriteLine. | DebugLog.Write("Settings", $"Add folder failed: {ex.Message}") |
| File organizer apply/undo | `src/Noctis/Services/FileOrganizerService.cs:131` | Per-file move failures are only returned to the dialog (errors list). There is no log of the batch result or the playlist remap count, so a failed or partial organize leaves no trace in the session log. | DebugLog.Write("Organize", $"{(writeUndoLog ? "apply" : "undo")} moved={done.Count} failed={errors.Count} remapped={remap.Count}") after RelocateTracksAsync, plus one DebugLog.Write per caught exception (from, to, ex.Message). |
| Metadata editor rename and animated cover | `src/Noctis/ViewModels/MetadataViewModel.cs:2615` | Rename failures (`catch { /* Non-fatal — skip this file */ }`) and animated-cover import failures (line 2578 `catch { }`) are swallowed with no log. | DebugLog.Write("Metadata", $"Rename failed '{t.FilePath}' -> '{newPath}': {ex.Message}") and DebugLog.Write("Metadata", $"Animated cover import failed: {ex.Message}"). |
| Remove from Library > Move to Recycle Bin | `src/Noctis/Helpers/LibraryRemovalHelper.cs:152` | A file the user asked to trash but that could not be moved is logged only through DebugLogger.Error (Library.TrashFailed, and Library.FolderTrashFailed at :247), which does nothing unless Developer Mode is on. The track has left the library but the file is still on disk, with nothing in the session log or crash.log. | Also write DebugLog.Write("Library", $"Trash failed after retries: {p}"), and show a transient status such as 'N files could not be moved to the Recycle Bin'. |
| Reset library/settings | `src/Noctis/ViewModels/SettingsViewModel.cs:5968` | ConfirmResetLibrary reports failures clearing the library, playlists (:5977), queue (:5986) and artwork cache (:6008) only through Debug.WriteLine, which is compiled out of Release builds. | DebugLog.Write("Settings", $"Reset: failed to clear <what>: {ex}") in each catch. |
| Dialog async void handlers | `src/Noctis/Views/LyricsStudioView.axaml.cs:46` | These catch blocks log only through System.Diagnostics.Debug.WriteLine (invisible in Release): LyricsStudioView.axaml.cs:46,72; SettingsView.axaml.cs:431,459,649; PlaylistImportDialog.axaml.cs:104,136; LyricShareDialog.axaml.cs:80,100,123; WrapDialog.axaml.cs:51,70; SendToFolderDialog.axaml.cs:37; AudioConverterDialog.axaml.cs:40; MainWindowViewModel.cs:2348; CommandPaletteViewModel.cs:249. | Replace each with DebugLog.Write("<Dialog>", ex) so failures reach the session log and crash.log. |
| Edit Playlist cover handling | `src/Noctis/ViewModels/SidebarViewModel.cs:858` | Failures deleting the old cover (`catch { /* non-fatal */ }` at 858, `catch { }` at 876) and the unguarded File.Copy at 879 leave no log that names the playlist. | DebugLog.Write("Playlists", $"Edit '{playlist.Name}': cover update failed: {ex.Message}"). |
| Lyrics Studio model download | `src/Noctis/ViewModels/LyricsStudioViewModel.cs:449` | A failed Whisper model download is shown only in ModelStatusText. Every other Studio step logs through DebugLogger, but this failure does not. | DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.ModelDownloadFailed", $"{model.Size}: {ex.GetType().Name}: {ex.Message}"). |
| LRC editor save | `src/Noctis/ViewModels/LrcEditorViewModel.cs:297` | Save failures appear only in StatusText. The embedded tag write's bool result is ignored (`try { _metadata.WriteTrackMetadata(_track); } catch { }` at :291), so a failed embed is never logged. | DebugLog.Write("LrcEditor", $"Save failed for '{_track.FilePath}': {ex.Message}"), and log when WriteTrackMetadata returns false. |
| Duplicate finder delete | `src/Noctis/ViewModels/DuplicateFinderViewModel.cs:81` | No log of how many user files were trashed, which ones, or which failed. This is a destructive action with no audit line. | DebugLog.Write("Duplicates", $"Trashed {n}/{ids.Count} duplicates"), plus the list of paths that failed. |
| Keyboard shortcuts | `src/Noctis/Views/MainWindow.axaml.cs:1579` | Nothing records which global shortcut fired, or on which focused element. 'My music skipped or paused while I typed' reports cannot be diagnosed. | DebugLogger.Info(Category.UI, "Shortcut", $"{action} key={e.Key} mods={e.KeyModifiers} source={e.Source?.GetType().Name}") before ExecuteShortcut. |
| Destructive library removal | `src/Noctis/Helpers/LibraryRemovalHelper.cs:39` | Remove from Library, with or without Recycle Bin, leaves no session-log entry: no count, no choice, no ids. Trash failures go only to DebugLogger (no-op unless Developer Mode is on). | DebugLog.Write("Library", $"Remove {tracks.Count} tracks choice={choice} first={tracks[0].FilePath}") and log the trash result count. |
| Remove current track from library (island menu) | `src/Noctis/ViewModels/PlayerViewModel.cs:1114` | There is no log of the removal or its choice. The trash runs after the advance, with no record of whether the file was released. | DebugLog.Write("Library", $"RemoveCurrent {trackToRemove.Id} choice={choice}"). |
| Playlist membership edits | `src/Noctis/ViewModels/PlaylistViewModel.cs:666` | RemoveTrack, MoveTrack and MoveTracks change playlist contents and save them with no log. | DebugLogger.Info(Category.UI, "Playlist.Remove", $"playlist={_playlist.Id} count={tracks.Count}"). |
| Drop import failure | `src/Noctis/Views/MainWindow.axaml.cs:1418` | The only record is Debug.WriteLine, which is [Conditional("DEBUG")] and absent from Release builds, so a failed drag-drop import leaves no trace. | DebugLog.Write("DropImport", ex). |
| Taskbar thumbnail buttons init | `src/Noctis/Views/MainWindow.axaml.cs:1280` | `catch { }` swallows any failure setting up the taskbar buttons, progress or favorite. | catch (Exception ex) { DebugLog.Write("Taskbar.Init", ex); } |
| Final settings flush on close | `src/Noctis/Views/MainWindow.axaml.cs:1101` | If FlushPendingSaveAsync fails at window close, the error is swallowed with `catch { }` (window geometry and the last settings are lost silently). | catch (Exception ex) { DebugLog.Write("Settings.FlushOnClose", ex); } |
| Command palette | `src/Noctis/ViewModels/CommandPaletteViewModel.cs:249` | Search failures go only to Debug.WriteLine (not in Release). The item run at :290 is never logged. | DebugLog.Write("Palette", ex) on failure; DebugLogger.Info(Category.UI, "Palette.Execute", item.Title). |
| Page action failures (playlist, sidebar) | `src/Noctis/Views/PlaylistView.axaml.cs:737` | Failures in drop-commit, rename and selection-bar actions (:252, :268, :303, :320, :337, :737) and in the SidebarView drop and playlist move (SidebarView.axaml.cs:195, :356) go only to Debug.WriteLine. | Replace them with DebugLog.Write("<Area>", ex) so they reach the session log / crash.log. |
| Managed import root and relocation | `src/Noctis/ViewModels/MainWindowViewModel.cs:2644` | EnsureManagedImportRootAsync and MoveFileIntoManagedRoot (:2724, :2733) report failures, including move-to-copy fallbacks and path-traversal blocks, only through Debug.WriteLine. | DebugLog.Write("DropImport", ...) for each failure branch. |
| Navigation history | `src/Noctis/ViewModels/MainWindowViewModel.cs:1659` | Navigate() logs, but GoBackInHistory/GoForwardInHistory (including those triggered by mouse X-buttons and BackRequested from album/artist pages) do not, so unexpected 'page jumped' reports cannot be traced. | DebugLogger.Info(Category.UI, "Nav.Back", $"to={target.View.GetType().Name} depth={_navigationHistory.Count}"). |

### Security

| Area | Location | Missing | Suggested |
|---|---|---|---|
| Lyricsfile parse failure | `src/Noctis/Services/LyricsfileParser.cs:42` | catch-all returns (null, null) with no log; a malformed sidecar, cache file or LRCLIB lyricsfile silently falls through to other sources | DebugLogger.Warn(Category.Lyrics, "Lyricsfile.ParseFailed", $"{ex.GetType().Name}: {ex.Message} (len={content.Length})") |
| TTML parse failure | `src/Noctis.Core/Services/TtmlParser.cs:60` | XDocument.Parse failure swallowed with no log | Log the exception type and message, with the content length, under the Lyrics category |
| Local lyrics probe | `src/Noctis/ViewModels/LyricsViewModel.cs:2301` | Five empty `catch { }` blocks (2301, 2314, 2327, 2340, 2371) swallow IO and decode errors for .lyricsfile/.ttml/.elrc/.lrc sidecars and the cache | DebugLogger.Warn(Lyrics, "Probe.SidecarFailed", $"{ext}: {ex.GetType().Name}: {ex.Message}") |
| Recycle Bin / trash backend | `src/Noctis/Helpers/RecycleBin.cs:73` | SHFileOperation return code and fAnyOperationsAborted are not logged; exceptions in TryMoveToTrash and TryMoveDirectoryToTrash (lines 27, 45) and RunProcess (199) are swallowed | Log the path, the SHFileOperation return code, the aborted flag and the drive type (Network/Removable) so a silent permanent delete or a trash failure can be diagnosed |
| Organizer undo log | `src/Noctis/Services/FileOrganizerService.cs:190` | Undo-log write failure is swallowed, so the user silently loses Undo; per-file move errors (line 133) and sidecar move failures (233) are not logged | DebugLogger.Error(State, "Organize.UndoLogWriteFailed", ex.Message), plus one log line per failed move |
| Plugin install failures | `src/Noctis/Services/Plugins/PluginHost.cs:1063` | Failed outcomes at 1063, 1085 and 1104 (inspect or extract errors, move-into-place failure) are returned to the UI but never logged; only success is logged at 1108 | DebugLogger.Warn(State, "Plugins", $"install failed {zipPath}: {message}") |
| Rename by pattern | `src/Noctis/ViewModels/MetadataViewModel.cs:2615` | File.Move failures during multi-select rename are swallowed; the file is silently not renamed and the user is not told | Log the source and target path with the exception, and add the file name to failedWrites so the dialog shows it |
| Metadata sidecar writes | `src/Noctis/ViewModels/MetadataViewModel.cs:2477` | .lrc and .txt sidecar write/trash failures (2477, 2492) are swallowed | DebugLogger.Warn(Lyrics, "Metadata.SidecarWriteFailed", path + ex.Message) |
| Send to Folder | `src/Noctis/Services/SendToFolderService.cs:163` | Copy failures are only returned in the result and never logged | DebugLogger.Warn(State, "SendToFolder.CopyFailed", $"{item.SourcePath} -> {item.TargetPath}: {ex.Message}") |
| Converter tag stamping | `src/Noctis/Services/AudioConverterService.cs:360` | StampSourceMetadata swallows every exception; converted files silently lack the custom tags | Log outPath and the exception under Category.State |
| Updater install launch | `src/Noctis/Services/UpdateService.cs:595` | LaunchInstaller failures (UAC declined, Process.Start null, /usr/bin/open failure, AppImage/tar script spawn failure at :587, :620, :634, :670, :705) go only to Debug.WriteLine, which is stripped from Release builds. The caller InstallUpdate (SettingsViewModel.cs:6606-6610) logs nothing either. | DebugLog.Write("Updater", $"LaunchInstaller failed: {ex.GetType().Name}: {ex.Message} (path={Path.GetFileName(installerPath)})") in each catch/null branch. |
| Updater download outcome | `src/Noctis/ViewModels/SettingsViewModel.cs:6566` | The OperationCanceledException branch (including the 5-minute deadline timeout) and the "corrupted" size-mismatch branch (:6561) write nothing to the session log. | DebugLog.Write("Updater", $"download aborted: {(user ? "user" : "timeout")} after {elapsed:F0}s, {bytes}/{total} bytes") and log the size-mismatch values. |
| Noctis Server transport/TLS errors | `src/Noctis.Core.Server/Services/Server/NoctisServer.cs:65` | builder.Logging.ClearProviders() drops all Kestrel diagnostics, so TLS handshake failures (for example the Windows ephemeral-key failure), connection resets and bad requests leave no trace anywhere. | Add a minimal ILoggerProvider that forwards Warning+ (and the HTTPS "AuthenticationFailed" connection event) from Microsoft.AspNetCore.Server.Kestrel* to DebugLog.WriteOnce("Server", ...). |
| Noctis Server authentication failures | `src/Noctis.Core.Server/Services/Server/NoctisServer.cs:134` | Individual failed logins (wrong password, unknown user, refused t/s token auth) are not logged; only the lockout itself is (:137). | DebugLogger.Warn(Category.State, "Server.AuthFailed", $"client={client} user={u} code={error}"), rate-limited per client. |
| LAN Web Remote rejected peers | `src/Noctis/Services/WebRemoteServer.cs:272` | Connections from non-private addresses are dropped silently (for example Tailscale/CGNAT 100.64.0.0/10), and 403 token failures in RouteAsync (:400-401) are not logged, so "phone can't connect" can't be diagnosed from Copy Logs. | DebugLog.WriteOnce("WebRemote", $"reject:{remote}", $"Dropped connection from non-private address {remote}") and a WriteOnce for token mismatches. |
| Discord RPC errors | `src/Noctis/Services/DiscordPresenceService.cs:250` | UpdateAsync/ConnectAsync catch blocks (:149-152, :250-252) and the client's OnError/OnConnectionFailed handlers (:117-121) use only Debug.WriteLine, so presence failures (for example DiscordRPC string-length exceptions) are invisible in Release builds. | DebugLog.WriteOnce("Discord", ex.GetType().Name, $"SetPresence failed: {ex.Message}") and log e.Code/e.Message from OnError. |
| Loon relay transport errors | `src/Noctis/Services/Loon/LoonClient.cs:512` | Receive-loop errors (:512), per-request handler failures (:712), send failures and socket aborts (:800) and thumbnail decode failures (:909) are Debug.WriteLine only, so a broken-image placeholder in Discord cannot be tied to a cause. | DebugLog.WriteOnce("Loon", $"send:{ex.GetType().Name}", $"Send failed, socket aborted: {ex.Message}") and similar for receive and handler errors. |
| yt-dlp binary install | `src/Noctis/Services/YouTube/YtDlpTool.cs:155` | The install/update records only the path; the downloaded size and hash are not logged, so a bad or tampered binary cannot be identified after the fact. | Log("YtDlp.Installed", $"{InstalledPath} bytes={len} sha256={hash} from={url}"). |
| Credential at-rest protection | `src/Noctis.Core/Services/PersistenceService.cs:437` | When DPAPI Protect throws, ProtectSecret silently returns the plaintext, so settings.json and tidal-auth.json get an unencrypted credential with no trace. | DebugLog.Write("Persistence", $"DPAPI protect failed ({ex.GetType().Name} 0x{ex.HResult:X8}); credential stored unencrypted") (never the value). |
| Credential decryption | `src/Noctis.Core/Services/PersistenceService.cs:456` | UnprotectSecret returns an empty string on DPAPI failure (and at :447 on non-Windows for enc:dpapi: values) with no log. The user is silently disconnected from Last.fm / ListenBrainz / the media server / TIDAL and nothing explains why. | At the call sites PersistenceService.cs:135-138 (and TidalAuth.cs:389), log which field could not be decrypted and the HResult, e.g. "lastFmSessionKey could not be decrypted (profile/machine changed?) - sign in again". |
| Plugin trust / restricted mode | `src/Noctis/Services/Plugins/PluginHost.cs:705` | The automatic 'community plugins ON + approve every installed plugin' migration is logged only through DebugLogger, which is a no-op unless Developer Mode is on. Which plugin ids were auto-approved is never recorded. | DebugLog.Write("Plugins", $"community plugins defaulted {(hadPlugins?"ON":"OFF")}; auto-approved: {string.Join(", ", ids)}"). |
| Third-party code load | `src/Noctis/Services/Plugins/PluginHost.cs:858` | Plugin load (and failure at :872, fault at :920) goes only to DebugLogger. A crash caused by an in-process plugin leaves no record in session.log / crash.log of which third-party assemblies were loaded. | DebugLog.Write("Plugins", $"loaded {plugin.Id} {plugin.Version} from {plugin.FolderName}") and DebugLog.Write for Failed/Fault with the message. |
| Restricted-mode toggle | `src/Noctis/Services/Plugins/PluginHost.cs:557` | SetCommunityPluginsEnabled changes the security posture (restricted mode on/off) with no log line. | DebugLog.Write("Plugins", $"community plugins {(enabled ? "ON" : "OFF (restricted mode)")} by user"). |
| Server TLS identity | `src/Noctis.Core.Server/Services/Server/ServerCertificate.cs:40` | An unreadable pfx or wrong key file silently mints a new certificate. The SHA-256 fingerprint changes, so every paired phone's pin fails, and nothing records why. | DebugLog.Write("Server", "server.pfx unreadable/expiring - generated a new certificate; clients must re-pair (new fingerprint ...)"). |
| Local API token | `src/Noctis/Services/LocalApi/LocalApiTokenStore.cs:78` | A corrupt local-api.json is treated as absent and LoadOrCreateToken silently mints a new token, which breaks Stream Deck/OBS integrations with no trace. | DebugLog.Write("LocalApi", $"local-api.json unreadable ({ex.Message}); a new token was issued"). |
| ListenBrainz scrobbling | `src/Noctis/Services/ListenBrainzService.cs:113` | Rejected submissions (e.g. 401 for a revoked token) and exceptions (:118) go only to Debug.WriteLine, so listens are lost with nothing in the session log. Last.fm got this fixed (LastFmService.cs:169-201) but ListenBrainz did not. | DebugLog.Write("ListenBrainz", $"submit-listens {kind} rejected ({status}): {Truncate(body,200)}") and raise a failure event as Last.fm does. |
| Last.fm sign-in | `src/Noctis/Services/LastFmService.cs:124` | auth.getSession failures (and auth.getToken at :84) go only to Debug.WriteLine; a failed Connect leaves nothing in Copy Logs. | DebugLog.Write("LastFm", $"auth failed: {ex.Message}"). |
| Reset all settings/data | `src/Noctis/ViewModels/SettingsViewModel.cs:6116` | The destructive reset writes no session-log line, and each failed step (library/playlists/queue/artwork/settings) goes only to Debug.WriteLine. | DebugLog.Write("Settings", "Reset all settings and data") at the start of ConfirmResetLibrary, plus DebugLog.Write for each failed step. |
| Settings merge | `src/Noctis/ViewModels/SettingsViewModel.cs:2636` | MergeExternalSettingChangesAsync failure (which then writes the in-memory view over disk) is logged only to Debug output. | DebugLog.Write("Settings", $"settings merge failed, saving in-memory view: {ex.Message}"). |
| TIDAL auth | `src/Noctis/Services/TidalAuth.cs:349` | Token refresh/exchange failures (:349, :356) and token store read/write errors (:394, :407) go only to DebugLogger (no-op unless Developer Mode), so 'TIDAL import stopped working' has no trace. | DebugLog.Write("Tidal", ...) for HTTP failures (status code only, never the body/token) and for store IO errors. |

### Cross-platform

| Area | Location | Missing | Suggested |
|---|---|---|---|
| OS trash backends | `src/Noctis/Helpers/RecycleBin.cs:197` | gio/osascript exit code and stderr are dropped; FreedesktopTrash's cross-device refusal (lines 160-166) and its outer catch (170-173) are silent, so 'Move to Trash did nothing' reports can't be diagnosed | DebugLog.Write("RecycleBin", $"{fileName} exited {p.ExitCode}: {stderr}") on non-zero exit, plus a line when the freedesktop fallback refuses a cross-device move (include the path's mount) |
| URL / external-app launch | `src/Noctis/Helpers/PlatformHelper.cs:175` | OpenUrl's catch and its fallback catch (185), OpenFileWith's catch (86) and ShowInFileManager's catch (51) swallow everything with no log | DebugLog.Write("PlatformHelper", $"OpenUrl failed: {ex.GetType().Name}: {ex.Message}") in each catch, matching OpenFolder at line 221 |
| Folder watcher setup | `src/Noctis.Core/Services/LibraryWatcherService.cs:84` | A folder that can't be watched (inotify limit, permissions, unmounted) is skipped silently, and OnError (line 257) only logs through DebugLogger, which is a no-op without Developer Mode, so the inotify-limit message never reaches the session log | DebugLog.Write("LibraryWatcher", $"cannot watch '{folder}': {ex.Message}") in the catch and in OnError (once per error kind) |
| libvlc selection on macOS | `src/Noctis/Services/VlcAudioPlayer.cs:427` | Nothing records which libvlc directory was loaded (VLC.app, bundled payload, Homebrew), which is key for macOS support reports | DebugLog.Write("Playback", $"libvlc loaded from {macLibPath ?? \"default probe\"} (VLC_PLUGIN_PATH={...})") after Core.Initialize |
| Data root resolution | `src/Noctis.Core/Helpers/AppPaths.cs:48` | The resolved DataRoot is never logged, and on Linux GetFolderPath(ApplicationData) returns "" when ~/.config is missing, which silently makes DataRoot the CWD-relative 'Noctis' | StartupTrace/DebugLog line with the resolved absolute DataRoot and a warning when ApplicationData came back empty |
| Launch at login | `src/Noctis/Helpers/StartupHelper.cs:70` | SetEnabled/IsEnabled failures go only to Debug.WriteLine (lines 45, 70), invisible in shipped builds | DebugLog.Write("Startup", $"autostart {(enabled?\"enable\":\"disable\")} failed on {platform}: {ex.Message}") |
| Linux/macOS self-update | `src/Noctis/Services/UpdateService.cs:670` | LaunchInstaller failures for the AppImage swap, tar.gz extract (684, 705) and macOS .dmg open (616, 620, 634) go only to Debug.WriteLine | DebugLog.Write("Update", ...) with the failing branch and exception message |
| Linux resume remap (unverified fix) | `src/Noctis/Services/LinuxResumeWatcher.cs:132` | Started/Resumed/Init-failed go only through DebugLogger (Developer Mode), so a user's bug report can't show whether the XWayland remap ever ran | Mirror ResumeWatch.Started / Resumed / Init failures to DebugLog.Write("ResumeWatch", ...) |
| Metadata editor rename | `src/Noctis/ViewModels/MetadataViewModel.cs:2615` | A failed File.Move in rename-by-pattern is swallowed with no log and no UI feedback | DebugLog.Write("Metadata", $"rename failed '{t.FilePath}' -> '{newPath}': {ex.Message}") and count the failures into SaveErrorMessage |
| Linux trash | `src/Noctis/Helpers/RecycleBin.cs:115` | If `gio trash` fails, its exit code and stderr are thrown away. The FreedesktopTrash fallback also swallows every exception (lines 160-172). A Linux "couldn't move to trash" report has no trace. | DebugLog.Write("Trash", $"gio trash exit={code} err={stderr}; fallback={ok} {ex?.GetType().Name}: {ex?.Message}") in RunProcess/FreedesktopTrash |
| Linux/AppImage self-update | `src/Noctis/Services/UpdateService.cs:655` | The detached /bin/sh script that runs `mv -f` (AppImage) or `tar -xzf` (tarball) writes its output nowhere. If the swap or extract fails, there is no relaunch and no trace. LaunchInstaller failures go only to Debug.WriteLine (lines 670, 705), not the session log. | Append `>>"$DATA/update.log" 2>&1` to the script, log the chosen path/target with DebugLog.Write("Update", ...), and replace Debug.WriteLine with DebugLog.Write |
| Autostart | `src/Noctis/Helpers/StartupHelper.cs:70` | Failures writing or removing ~/.config/autostart/noctis.desktop are logged only with Debug.WriteLine, so they are invisible in the session log and crash.log. | DebugLog.Write("Autostart", $"SetEnabled({enabled},{startMinimized}) failed: {ex.Message}; path={LinuxDesktopPath}") |
| libvlc init (Linux) | `src/Noctis/Services/VlcAudioPlayer.cs:561` | The loaded libvlc version, whether avformat is forced (NOCTIS_BUNDLED_VLC), the keep-alive state and the effective args are logged only when NOCTIS_VLC_LOG=1 (line 580). Normal Linux bug reports can't tell a system libvlc from the bundled one. | Always log Info "VLC.Init": libVlc.Version, OS, bundled={NOCTIS_BUNDLED_VLC}, avformatForced, keepAlive={_keepAlive!=null} |
| Resume remap | `src/Noctis/Views/MainWindow.axaml.cs:36` | There is no log of which windows were skipped, or that owned dialogs were hidden by the owner's Hide(). | DebugLogger.Info(UI, "ResumeWatch.Skip", $"{window.GetType().Name} owner={window.Owner?.GetType().Name} visible={window.IsVisible}") |
| Tray availability (Linux) | `src/Noctis/Views/MainWindow.axaml.cs:970` | Nothing records whether a StatusNotifier host exists, so an "app vanished after login/minimize" report can't be diagnosed. | On Linux, log Info "TrayIcon.Host" with whether org.kde.StatusNotifierWatcher has an owner |
| Mini player transparency | `src/Noctis/Views/MiniPlayerWindow.axaml.cs:103` | The compositor detection result and the chosen path (opaque vs per-pixel) aren't logged, so issue #26-style reports can't be triaged. | DebugLogger.Info(UI, "MiniPlayer.Transparency", $"compositor={..} wayland={WAYLAND_DISPLAY!=null} forceOpaque={forceOpaque}") |
| MPRIS remote commands | `src/Noctis/Services/MprisService.cs:499` | Incoming Player methods (PlayPause/Next/Seek) and property Sets (Volume/Shuffle/LoopStatus) aren't logged, and TrySendMessage returning false (line 309) is silent. "Media keys do nothing" reports have no trail. | DebugLogger.Info(Playback, "Mpris.Command", member) and one warning when TrySendMessage returns false |
| System theme probe | `src/Noctis/Helpers/PlatformHelper.cs:383` | Nothing records which probe (gsettings color-scheme / gtk-theme / portal / kdeglobals / fallback) decided the System theme. | DebugLog.Write("Theme", $"system dark={result} source={probe} value={raw}") |
| Server shutdown | `src/Noctis.Server/Program.cs:160` | Receipt of SIGTERM/ProcessExit isn't logged separately from Ctrl+C, and the shutdown steps don't report completion. | Console.WriteLine("[Shutdown] SIGTERM") in the signal handler, plus per-step done/timeout lines |
| macOS/Linux trash failures | `src/Noctis/Helpers/RecycleBin.cs:197` | RunProcess returns false on a non-zero exit, on the 15 s timeout kill (194) and on exceptions (199-202) without logging anything. The redirected stderr is never read, so osascript's 'Not authorized to send Apple events to Finder (-1743)' and gio errors are lost. | DebugLog.Write("RecycleBin", $"{fileName} exit={p.ExitCode} ms={elapsed} stderr={stderr}") on failure, plus a separate 'timed out after 15s (killed)' line. Read stderr asynchronously before WaitForExit. |
| libvlc selection on macOS | `src/Noctis/Services/VlcAudioPlayer.cs:427` | Nothing records which libvlc directory was chosen (VLC.app, the bundled Contents/MacOS/libvlc, or Homebrew), which plugin path was set, or whether VLC_PLUGIN_PATH was already set by the environment. | DebugLog.Write("Startup", $"libvlc: dir={macLibPath ?? "<default probe>"} plugins={pluginsPath} envPluginPath={existing}") before Core.Initialize, and the libvlc version (_libVlc.Version) after line 561. |
| ffmpeg validation | `src/Noctis/Services/AudioConverterService.cs:214` | ValidateFfmpegAsync swallows the exception with no log. The Win32Exception 'Bad CPU type in executable' from the Intel-only bundled ffmpeg on Apple Silicon is invisible. | DebugLogger.Warn(Category.Playback, "Ffmpeg.ValidateFailed", $"{exe}: {ex.GetType().Name}: {ex.Message}") |
| Launch-at-login failures | `src/Noctis/Helpers/StartupHelper.cs:70` | IsEnabled (45) and SetEnabled (70) failures go only to Debug.WriteLine, which is not in the session log. The macOS target path written into the LaunchAgent (154) is never logged. | DebugLog.Write("Startup", ...) for both catches, and log `LaunchAgent -> {appPath}` after File.WriteAllText in MacSet. |
| macOS update installer launch | `src/Noctis/Services/UpdateService.cs:620` | The /usr/bin/open and shell-execute failures for the downloaded .dmg (616, 620, 634) go only to Debug.WriteLine. | DebugLog.Write("Update", $"LaunchInstaller(dmg) failed: {ex.GetType().Name}: {ex.Message}") |
| macOS Now Playing wiring | `src/Noctis/Services/MacNowPlayingService.cs:191` | WireCommand silently skips a command whose property returned nil. The artwork path silently returns when initWithImage: is unavailable (174-175) or when NSImage fails to load the file (179). | DebugLogger.Warn(Category.Playback, "MacNowPlaying.CommandMissing", commandProperty), and a one-time "MacNowPlaying.ArtworkUnsupported" / "ArtworkLoadFailed" with the path. |
| Show in Finder / Open with app | `src/Noctis/Helpers/PlatformHelper.cs:51` | ShowInFileManager (51-54) and OpenFileWith (86-89) swallow exceptions without logging. OpenFolder in the same file does log. | DebugLog.Write("PlatformHelper", $"ShowInFileManager/OpenFileWith failed: {ex.GetType().Name}: {ex.Message}") |
| System theme detection | `src/Noctis/Helpers/PlatformHelper.cs:408` | Any failure of `defaults read -g AppleInterfaceStyle` (or the Linux probes) falls back to dark with no trace. | DebugLog.Write("Theme", $"system dark-mode probe failed: {ex.Message} — defaulting to dark") |

## Phase 1 — Wiring inventories (settings, dialogs, commands)

Every setting control, dialog control and command handler the settings lane checked. Only BROKEN/PARTIAL rows became findings.
### Wiring inventory — settings

| Section | Label | VM property/command (file:line) | Persisted | Applied at (file:line) | Status | Note |
|---|---|---|---|---|---|---|
| General | Language | LanguageChoice SettingsViewModel.cs:750, h:753 | Y :2685 | Loc.SetCulture :758 | OK | |
| General | Open Noctis when computer starts | LaunchAtStartup :839, h:854 | OS entry (not json) | StartupHelper.SetEnabled :870 | PARTIAL | reset unregisters (:6233) but the toggle stays ON |
| General | Start minimized to tray | StartMinimizedToTray :843, h:891 | Y :2758 | re-register :897; Program.cs:102 | OK | |
| General | Minimize to tray | MinimizeToTray :831, h:848 | Y :2756 | MainWindow.axaml.cs:777 | OK | |
| General | Close to tray | CloseToTray :834, h:849 | Y :2757 | MainWindow.axaml.cs:1050 | OK | |
| General | Restore last played track | RestoreLastTrackOnStartup :846, h:850 | Y :2759 | MainWindowViewModel.cs:692 | OK | |
| General | Keep sidebar expanded | SidebarAlwaysExpanded :1492, h:3696 | Y :2799 | event SidebarAlwaysExpandedChanged | OK | |
| General | Hover to expand sidebar | SidebarHoverExpand :1491, h:3691 | Y :2798 | MainWindow.axaml.cs:658,676 | OK | |
| General | Open audio files with Noctis (Register) | RegisterFileTypesCommand :1573 | registry | WindowsFileAssociations | OK | |
| General | Open file with (path + Browse) | ExternalOpenAppPath :1641, h:4536; BrowseExternalOpenAppCommand :4546 | Y :2810 (debounced) | ExternalOpenApp.cs:18 | OK | |
| Appearance | Theme tiles (Dark/Gray/Midnight/Light/Ink/Smoke) | Set*ThemeCommand :3271-3277 -> ApplyTheme :3482 | Y :2643-2652 | ThemeChanged :3492 | OK | |
| Appearance | Custom theme tiles / Edit / Delete / + Custom | ApplyCustomTheme :3280, OpenThemeEditor :3316, DeleteCustomTheme :3298 | Y :2654 | ThemeChanged | OK | Delete has no confirmation |
| Appearance | Pack themes | ApplyPackThemeCommand ContentPacks.cs:35 | Y :2652 | ThemeChanged | OK | |
| Appearance | Accent swatches | ApplyAccentPresetCommand :3404 -> ApplyAccent :3423 | Y :2667-2668 (debounced) | AccentChanged :3460 | OK | |
| Appearance | Custom colour picker | CustomAccentHex :523, h:548 | Y (debounced) | ApplyAccent | OK | |
| Appearance | Accent follows album art | AccentFollowsArtwork :527, h:529 | Y :2669 | MainWindow.axaml.cs:430 | PARTIAL | not reset by Reset settings |
| Appearance | Liquid Glass | LiquidGlassEnabled :1601, h:3704 | Y :2800 | LiquidGlassChanged event | OK | hidden on Linux |
| Appearance | Player bar opacity slider | PlaybackBarBackgroundOpacity :1463, h:3712 | Y :2791 (debounced) | ApplyPlayerSettings :3013 | OK | each tick resets all marquees (finding) |
| Appearance | Track box opacity slider | PlaybackBarTrackBoxOpacity :1466, h:3725 | Y :2792 (debounced) | :3014 | OK | same marquee side effect |
| Appearance | Text animation x9 marquee toggles | *MarqueeEnabled :622-630, h:3905-3957 | Y :2711-2719 | ApplyPlayerSettings :3000-3043 | OK | |
| Appearance | Home: Heavy rotation | HomeShowHeavyRotation :967, h:975 | Y :2790 | HomeViewModel.cs:232 | OK | |
| Appearance | Animated artwork | EnableAnimatedCovers :631, h:3959 | Y :2720 | CoverFlowView/LyricsView/MiniPlayerWindow bindings; AlbumDetailViewModel.cs:228 | OK | |
| Appearance | Album page colour | AlbumPageTintEnabled :634, h:3964 | Y :2721 | AlbumDetailViewModel.cs:234,388 | OK | |
| Appearance | Tint strength slider | AlbumPageTintStrength :637, h:3970 | Y :2722 | AlbumDetailViewModel.cs:236,420 | PARTIAL | full SaveAsync per tick (finding) |
| Appearance | Automatic album cover size | AlbumTileSizeAuto :1489, h:3772 | Y :2795 | LibraryAlbumsViewModel.cs:339; FavoritesViewModel.cs:76; HomeViewModel.cs:234 | OK | |
| Appearance | Cover size slider | AlbumTileTargetSize :1490, h:3778 | Y :2796 (debounced) | same consumers | OK | |
| Appearance | Song progress on taskbar | TaskbarProgressEnabled :1600, h:3759 | Y :2801 | MainWindow.axaml.cs:1275 | OK | Windows only |
| Player | Repeat/Favorite/Mini player/Shuffle/EQ/Speed/Sleep/Time/Skip buttons | PlaybackBarShow* :664-675, h:4068-4135 | Y :2726-2736 | ApplyPlayerSettings :3002-3011 | OK | |
| Player | Skip seconds 10/15/30 | IsSkipSeconds* :679-681 -> PlaybackBarSkipSeconds h:4074 | Y :2727 | :3003 | OK | |
| Player | Idle queue pill | PlaybackBarShowIdlePill :674, h:4137 | Y :2735 | MainWindowViewModel.cs:171,323 | OK | |
| Player | Waveform seek bar | WaveformSeekBarEnabled :677, h:4113 | Y :2737 | :3012 | OK | |
| Player | Player island width slider | PlaybackBarIslandWidth :1469, h:2896 | Y :2865 (debounced) | :2908, :3017 | OK | slider max 1400 while grip drag may store up to 4096 (ClampToValidRanges) |
| Player | Mini player design | IsMiniStyle* :658-660 -> MiniPlayerStyle h:4142 | Y :2725 | MiniPlayerViewModel.cs:67 | OK | |
| Player | Mini player opacity slider | MiniPlayerBackgroundOpacity :1478, h:3738 | Y :2793 (debounced) | direct binding | OK | |
| Player | Frosted mini player | MiniPlayerFrostedBackground :1482, h:3752 | Y :2794 | MiniPlayerWindow.axaml.cs:946 | OK | Windows only |
| Player | Now playing artwork | IsArtworkStyle* :647-650 -> NowPlayingArtworkStyle h:4040 | Y :2723 | LyricsView.axaml:582 | OK | |
| Player | Cover Flow layout | IsCoverFlow* :694-696 -> CoverFlowLayout h:4059 | Y :2724 | MainWindowViewModel.cs:624 | OK | |
| Lyrics | Lyrics presets Apply | ApplyLyricsPresetCommand ContentPacks.cs:52 | Y (one save :92) | ApplyPlayerSettings | OK | values range-checked in ContentPack.cs:342-352 |
| Lyrics | Flowing lyrics background combo | SelectedFlowingOption :4180 -> LyricsFlowingLightEnabled/Style h:4151/:4201 | Y :2738-2739 | :3018-3019 | OK | |
| Lyrics | Kawarp warp / blur sliders | LyricsKawarpWarp/Blur :4211-4212, h:4215/:4221 | Y :2740-2741 | :3020-3021 | PARTIAL | SaveAsync per tick (finding) |
| Lyrics | Audio visualizer / Artwork colour / style | LyricsVisualizerEnabled :703 h:4284; ArtworkColor :708 h:4290; Style :706 h:4296 | Y :2742-2744 | :3022-3024 | OK | |
| Lyrics | Fullscreen lyrics focus | LyricsFullScreenFocusEnabled :822, h:4306 | Y :2750 | :3030 | OK | |
| Lyrics | Minimum line opacity slider | LyricsMinLineOpacity :824, h:4312 | Y :2751 | :3031 | PARTIAL | SaveAsync per tick (finding) |
| Lyrics | Join split words / translations / romanization / background vocals | :825-828, h:4318-4340 | Y :2752-2755 | :3032-3035 | OK | |
| Lyrics | LRCLIB / NetEase | LrcLibEnabled/NetEaseEnabled :1631-1632, h:4342/:4348 | Y :2806/:2820 | LyricsViewModel.cs:1119-1121 (reads disk) | OK | |
| Lyrics Studio page (settings sub-view) | Pause bg video with playback / Music videos / Rounded corners | LyricsBackgroundPausesWithPlayback :802 h:3982; MusicVideosEnabled :805 h:808; MusicVideoRoundedCorners :806 h:814 | Y :2747-2749 | :3027-3029 | PARTIAL | not reset by Reset settings |
| Shortcuts | Key chips / per-row reset / Reset all | ShortcutService.Set/Reset/ResetAll (ShortcutService.cs:63/84/90) -> Changed -> QueueSettingsSave :2005 | Y :2642 | MainWindow key dispatch | PARTIAL | Reset settings does not reload defaults, so overrides are re-persisted |
| Audio | Song transitions | SongTransitionsEnabled :600, h:3635 | Y :2696 | ApplyAudioSettings :2966-2967 | OK | disabled while Exclusive is on |
| Audio | AutoMix / Crossfade style | IsAutoMixStyle/IsCrossfadeStyle :604-605 -> TransitionStyle h:3642 | Y :2704 | :2966, :2980 | OK | |
| Audio | Crossfade 3s/6s/10s + slider | SetCrossfadePresetCommand :592; CrossfadeDuration h:3665 | Y :2709 (debounced) | :2966 | OK | |
| Audio | Sound Check | SoundCheckEnabled :607, h:3678 | Y :2710 | :2964 | OK | |
| Audio | Gapless playback | GaplessPlaybackEnabled :1651, h:4426 | Y :2813 | SetGapless; PlayerViewModel.GaplessEnabled | OK | |
| Audio | Play/pause fade + duration | PlayPauseFadeEnabled :611 h:3606; PlayPauseFadeMs :612 h:3612 | Y :2697-2698 (debounced ms) | :2965 | OK | |
| Audio | Autoplay | AutoplayEnabled :1655, h:4433 | Y :2814 | PlayerViewModel.AutoplayEnabled | OK | |
| Audio | Explicit content | AllowExplicitContent :700, h:4051 | Y :2815 | PlayerViewModel.AllowExplicitContent | OK | |
| Audio | Exclusive mode | ExclusiveAudioEnabled :1668, h:3684 | Y :2819 | VlcAudioPlayer.SetExclusiveMode (idempotent, VlcAudioPlayer.cs:1604) | OK | Windows only |
| Audio | Multi-channel upmix Off/Duplicate/Surround | UpmixMode Features.cs:26, h:32 | Y Features.cs:491 | SetUpmixMode VlcAudioPlayer.cs:2257 | PARTIAL | not reset by Reset settings; re-applied stale at :6329 |
| Audio | ReplayGain toggle / mode / pre-amp | ReplayGainEnabled :1647 h:4420; ReplayGainMode :1644 h:4404; ReplayGainPreampDb :1645 h:4472 | Y :2811-2812 (preamp debounced) | ApplyReplayGain VlcAudioPlayer.cs:1964 | OK | |
| Audio | Analyze tempo & key / Save to tags | BpmKeyAnalysisEnabled :1659 h:4442; WriteAnalysisToTags :1660 h:4466 | Y :2817-2818 | AudioAnalysisCoordinator Start/Stop :4455-4463 | OK | |
| Audio | Equalizer master switch | EqualizerEnabled :1687, h:4959 | Y :2821 (debounced) | ApplyEqPresetByName :3123; PlayerViewModel.cs:806 | OK | |
| Audio | EQ preset / Save / Delete / Restore / Reset | SelectedEqPresetName :1692 h:4995; SaveUserEqPreset :5200; DeleteSelectedEqPreset :5245; RestoreBuiltInEqPresets :5260; ResetEqualizer :5295 | Y :2823-2831 | ApplyEqualizer :3046 | OK | |
| Audio | EQ pre-amp / band freq-gain-Q / Add/Remove band | EqPreampDb :1694 h:4972; EqBandViewModel -> OnEqBandEdited :5038; AddEqBand :5275; RemoveEqBand :5286 | Y :2822, :2827 | ApplyEqualizer | OK | |
| Library | Add folder / remove folder | AddFolderPath :5698 (SettingsView.axaml.cs:625); RemoveFolderCommand :5764 | Y :2686 | library scan :5743/:5773 | OK | |
| Library | Import dropped files | ImportDroppedMedia :615, h:3624 | Y :2699 | MainWindow.axaml.cs:1407,1441 | OK | |
| Library | Playlist album headers / NEW badge / Added / Favorite columns | :618-621, h:3630-3633 | Y :2700-2703 | PlaylistView.axaml:691,695,851,914; PlaylistView.axaml.cs:142 | OK | |
| Library | Scan library | RescanCommand :5818 | n/a | RunLibraryScanAsync :5826 | OK | |
| Library | Scan on startup | ScanOnStartup :1806, h:3559 | Y :2671 | MainWindowViewModel.cs:699 | OK | |
| Library | Watch folders | WatchFoldersEnabled :1808, h:3565 | Y :2672 | ILibraryWatcherService.Refresh :3570 | OK | |
| Library | Use embedded artwork | UseEmbeddedArtwork :1810, h:3573 | Y :2673 | MetadataService static :3577 | OK | |
| Library | Collapse album editions | CollapseAlbumEditions :1602, h:3766 | Y :2802 | LibraryAlbumsViewModel.cs:330 | OK | |
| Library | Merge featured artists | MergeFeaturedFromTitles :1603, h:3793 | Y :2803 | ApplyMergeFeaturedToLibraryAsync :3878 | OK | |
| Library | Group artists by | IsArtistGroupBy* :1610-1620 -> ArtistGroupMode h:3803 | Y :2804 | ArtistCredit.Configure :3859 | OK | |
| Library | Artist tag separators add/remove/reset | AddArtistSeparator :3825; RemoveArtistSeparator :3837; ResetArtistSeparators :3849 | Y :2805 | ApplyArtistGrouping :3856 | OK | |
| Library | Deezer / MusicBrainz | DeezerEnabled/MusicBrainzEnabled :1635-1636, h:4360/:4354 | Y :2807-2808 | MetadataFinderService.cs:34,58; AutoMatchCoordinator.cs:40 | OK | |
| Library | Organize / Duplicates / Find metadata / Import playlist | Open*Command :5802-5815 | n/a | dialogs | OK | OrganizePattern/TargetRoot persisted :1819-1820 |
| Library | YouTube open / folder / browse / yt-dlp path / install | OpenYouTubeDownloader Features.cs:267; YouTubeDownloadFolder :193 h:202; YtDlpPath :194 h:210; InstallYtDlp :236 | Y Features.cs:494-495 (debounced) | YtDlpTool.Resolve via GetSettings | OK | not reset by Reset settings |
| Library | Snoozed / Removed tracks | UnsnoozeCommand :5326; RestoreRemovedTrackCommand :5366 | library/settings | LibraryService | OK | |
| Advanced | Open data folder | OpenDataFolderCommand :6387 | n/a | PlatformHelper.OpenFolder | OK | |
| Advanced | Reset settings (confirm / cancel / reset everything) | ShowResetConfirm :5934; CancelReset :5954; ConfirmResetLibrary :5957 | defaults saved :6114 | :6121-6350 | PARTIAL | many fields left unreset and re-persisted (finding) |
| Advanced | Clear artwork cache | ClearArtworkCacheCommand :6354 | n/a | Task.Run delete | OK | |
| Advanced | FFmpeg path / Browse | FfmpegPath :1638, h:4366; BrowseFfmpegCommand :4518 | Y :2809 (debounced) | RefreshFfmpegStatus :4486 | OK | |
| Advanced | Include pre-release updates | IncludePrereleaseUpdates :1930, h:3586 | Y :2680 | CheckForUpdateSilentAsync :3595 | OK | |
| Advanced | Developer mode | DeveloperMode :6628, h:6677 | Y :2681 | DebugLog/DebugLogger flags :6684-6695 | OK | |
| Advanced | Version manager list / install / older / cancel | DevReleases :6632; InstallReleaseCommand :6789; ShowOlder/HideOlder :6765/:6775; CancelDevDownload :6926 | n/a | RefreshReleasesAsync :6707 | PARTIAL | refresh wired to the About tab, list lives in Advanced (finding) |
| Advanced | Copy logs / Clear / Open folder | CopyDevLogs :6929; ClearDevLogs :6944; OpenLogsFolder :6954 | n/a | | OK | |
| Account & Devices | Avatar pick / remove | SetProfileAvatarAsync :439 (view :397); ClearProfileAvatarCommand :470 | Y :2666 | CachedImage binding | OK | |
| Account & Devices | Name | ProfileName :392, h:395 | Y :2665 (debounced) | | OK | |
| Account & Devices | Account / pairing / change password (coming soon) | CreatePrimaryAccount Features.cs:169 etc. | users.db | | UNVERIFIED | whole card IsEnabled=False (SettingsView.axaml:4403) |
| Account & Devices | Library sync + device name (coming soon) | SyncEnabled Features.cs:55 h:85; SyncDeviceName :56 h:108 | Y Features.cs:492-493 | LibrarySyncService | PARTIAL | card disabled (:4541); SyncDeviceId never persisted (finding) |
| Account & Devices | Web remote + Copy | WebRemoteEnabled :1193, h:1270; CopyWebRemoteUrl :1248 | Y :2760 | UpdateWebRemoteState :1276 | OK | port has no UI; falls back to an ephemeral port :1300 |
| Account & Devices | Local API + show/copy/regenerate token | LocalApiEnabled :1339, h:1372; :1431/:1434/:1449 | Y :2761 | UpdateLocalApiState :1378 | OK | |
| Integrations | Discord Rich Presence | DiscordRichPresenceEnabled :1750, h:4566 | Y :2832 | HandleDiscordToggleAsync :4579 | OK | connect failure reverts silently |
| Integrations | Show album on Discord | DiscordShowAlbum :1752, h:4673 | Y :2833 | RepublishDiscordPresenceAsync :4649 | PARTIAL | not reset by Reset settings |
| Integrations | Last.fm Connect / Logout / Enable scrobbling | LoginLastFm :4687; LogoutLastFm :4773; LastFmScrobblingEnabled :1753 h:4681 | Y :2834-2837 | LastFmService | OK | |
| Integrations | ListenBrainz token / Connect / Logout / Enable | ListenBrainzToken :1760 h:4789; TestListenBrainz :4799; LogoutListenBrainz :4834; ListenBrainzScrobblingEnabled :1759 h:4784 | Y :2839-2844 (token only when connected) | ListenBrainzService | OK | token box hidden while connected |
| Integrations | TIDAL connect / disconnect | ConnectTidal :2133; DisconnectTidal :2142 | TIDAL token store | TidalAuth | OK | |
| Integrations | Music server type/url/user/password, Connect/Disconnect | MediaServer* :1776-1779; ConnectMediaServer :4897; DisconnectMediaServer :4945 | Y :2850-2855 | IMediaServerService.SetActiveConnection | OK | |
| Plugins | Community plugins | CommunityPluginsEnabled :170, h:181 | host-owned keys (process-owned :2611-2614) | PluginHost.SetCommunityPluginsEnabled | OK | |
| Plugins | Install / Open folder / Reload / Remove / Open plugin folder | :229, :150, :160, :279, :294 | host | PluginHost | OK | |
| Plugins | Per-plugin toggle + declared settings | LoadedPlugin.IsEnabled / PluginSettingItem (TwoWay) | host | PluginHost | UNVERIFIED | plugin lane |
| Statistics | View all stats | OpenStatisticsPageCommand :7010 | n/a | event | OK | stats computed off-thread :5392 |
| About | Check / Update / Install & restart / Cancel / GitHub / Discord / Website / copy version | :6446, :6523, :6590, :6584, :6957, :6998, :7004, :6968 | n/a | UpdateService | OK | |

### Wiring inventory — dialogs

| Dialog/Menu | Control | Handler (file:line) | Status | Note |
|---|---|---|---|---|
| ConfirmationDialog | Confirm / Cancel | Views/ConfirmationDialog.axaml.cs:89 / :95 | OK | No Esc handling; backdrop click swallowed (:106); owner is MainWindow, or the calling window via ShowAsync(Window,…) :112 |
| TextPromptDialog (Move to folder, Rename folder) | OK / Cancel / Enter / Esc | Views/TextPromptDialog.axaml.cs:66 / :68 / :74-87 | OK | An empty entry returns "" (Move to folder then removes the playlist from its folder) |
| BadgeNameDialog | Add / Cancel / Enter / Esc | Views/BadgeNameDialog.axaml.cs:32 / :34 / :36-40 | OK | Empty name is refused |
| RemoveFromLibraryDialog | Trash / Keep files / Cancel | Views/RemoveFromLibraryDialog.axaml.cs:63 / :69 / :75 → Helpers/LibraryRemovalHelper.cs:20 | OK | Trash failures logged only in Developer Mode (logging gap) |
| CreatePlaylistDialog | Create / Cancel | ViewModels/CreatePlaylistDialogViewModel.cs:21 / :36 → SidebarViewModel.cs:387-435 | OK | No Esc |
| EditPlaylistDialog | Save / Cancel / Set cover / Remove cover | EditPlaylistDialogViewModel.cs:59 / :73 / :79 / :105 → SidebarViewModel.cs:800-902 | BUG (low) | Unguarded File.Copy at :879 after the fields are changed |
| AddToPlaylistDialog | Pick playlist / New / Create / Cancel | AddToPlaylistDialogViewModel.cs:48 / :56 / :72 / :86 → SidebarViewModel.cs:742-797 | BUG (medium) | Smart playlists are listed and accept tracks (:746) |
| CreateSmartPlaylistDialog | Add rule / Remove rule / Create / Cancel | CreateSmartPlaylistDialogViewModel.cs:121 / :142 / :158 / :184 → SidebarViewModel.cs:908 | OK | Rules.Count==0 makes Create do nothing silently; a rule removed <240 ms before Create is still included (edge case) |
| AddSongsDialog | Tick / Select all / Add / Cancel / Reshuffle | AddSongsDialogViewModel.cs:157 / :184 / :223 / :243 / :121 → SidebarViewModel.cs:674 | OK | Search debounced 250 ms; smart playlists guarded |
| Sidebar playlist context menu | Edit / Pin / Delete / Move to folder / Remove from folder | SidebarViewModel.cs:443 / :451 / :457→:601 / :464 / :480 | OK | Delete has a confirm; deleting the open playlist leaves its page showing |
| Sidebar folder context menu | New playlist here / Rename / Dissolve | SidebarViewModel.cs:379 / :489 / :511 | OK | Dissolve has a confirm |
| Sidebar drop | Drop tracks on a playlist | Views/SidebarView.axaml.cs:179 | OK | Smart playlists excluded (:154) |
| Playlists grid context menu | Open/Play/Shuffle/PlayNext/Queue/Edit/Export/Pin/Delete | LibraryPlaylistsViewModel.cs:156/175/185/196/207/216/237/149/283 | OK | SetCoverArt :294 and RemoveCoverArt :358 are not bound anywhere (dead code) |
| Playlist import | Choose file / Drop / Link / Create / Close / Esc | PlaylistImportDialog.axaml.cs:108 / :90; PlaylistImportViewModel.cs:132 / :188 / :207; dialog :72 | OK | Create reloads Sidebar.LoadPlaylistsAsync (:199), which replaces the Playlist objects an open PlaylistViewModel may still hold |
| Metadata window | Save / Cancel | MetadataViewModel.cs:2256 / :2695 | BUG (low) | Cancel enabled during Save; album-art write result ignored (:2515/:2538, medium); rename skips relocation (:2611, high); no Esc |
| Metadata window | Search/Apply metadata, Artwork add/search/download/remove, Animated add/search/remove, Lyrics import/share/search, ShowInFolder, CopyPath, ResetPlayCount | MetadataViewModel.cs:761/858/1365/1473/2173/2160/1402/1584/1429/1891/883/1956/1031/1034/2728 | OK | Temp animated-cover preview files are never cleaned up on close (minor) |
| Metadata open paths | Single / album / multi-select | MetadataHelper.cs:236 / :236(albumScoped) / :214 | BUG (low) | Multi-select has no ChangesSaved hook; album path checks only tracks[0] |
| LRC editor | Space stamp / Esc / X / backdrop | LrcEditorDialog.axaml.cs:35 / :40 / :47 / :49 | BUG (medium) | Space taken by a focused Button; Esc/X/backdrop discard stamps without asking |
| LRC editor | Save / Play-from-line / Nudge± / Stamp / Clear | LrcEditorViewModel.cs:243 / :232 / :209,:217 / :199 / :224 | BUG (low) | Stamps use the global player even after the track changes |
| Lyric share card | Save PNG / Copy / Save video / X / backdrop | LyricShareDialog.axaml.cs:65 / :85 / :105 / :50 / :52 | BUG (low) | No cancel button bound; the still-card path is uncancellable; partial .mp4 left after cancel; Closed→Detach (:23) OK |
| Wrap (share) dialog | Save / Copy / X / backdrop | WrapDialog.axaml.cs:37 / :56 / :24 / :26 | OK | Closed→vm.Dispose (:80) |
| Command palette | Esc / arrows / Enter / click / backdrop | CommandPaletteDialog.axaml.cs:36 / :40-49 / :50 / :64 / :73 | BUG (low) | Enter can run stale results inside the debounce window |
| Songs View Options | X / backdrop / Esc | SongsViewOptionsDialog.axaml.cs:63 / :70 / :42 | OK | VM disposed via using (MainWindowViewModel.cs:2341) |
| Album / Playlist description | X / backdrop / Edit / Save / Cancel | AlbumDescriptionDialog.axaml.cs:47,52; PlaylistDescriptionDialog.axaml.cs:48,55; AlbumDetailViewModel.cs:739/748/769 | OK | Backdrop click ignored while there are unsaved changes |
| Theme editor | Save / Cancel / Esc | ThemeEditorViewModel.cs:107 / :110; ThemeEditorDialog.axaml.cs:41 → SettingsViewModel.cs:3316 | OK | Invalid hex falls back safely |
| Lyrics Studio dialog/page | Close / Esc / Start / Stop / Save / Skip / Align / Transcribe / Import / Download model | LyricsStudioViewModel.cs:1000 / LyricsStudioDialog.axaml.cs:28 / :459 / :645 / :652 / :699 / :817 / :774 / :791 / :437 | OK | Esc blocked while running; model download uses CancellationToken.None (continues after close); tap keys use a tunnel handler (LyricsStudioPanel.axaml.cs:153) |
| Lyrics Studio › Choose songs | Esc / backdrop / Add / Cancel / Select all | LyricsStudioPickerDialog.axaml.cs:49 / :61; LyricsStudioPickerViewModel.cs:267 / :275 / :234 | BUG (low) | Undebounced full-library search on the UI thread |
| Lyrics Background picker | Esc / Close / Choose default / Clear / Choose item / Clear item / YouTube download | LyricsBackgroundPickerDialog.axaml.cs:65; LyricsBackgroundPickerViewModel.cs:252 / :214 / :223 / :230 / :241 / :329 | BUG (low) | Esc does not cancel the yt-dlp download; undebounced search |
| Organize Files | Preview / Apply / Undo / Close / Esc | OrganizeFilesViewModel.cs:51 / :85 / :108 / :121; OrganizeFilesDialog.axaml.cs:20 | BUG (high) | Playlist remap goes to disk only, so the sidebar keeps stale IDs; Close/Esc during Apply leaves the moves running with no UI |
| Duplicate finder | Rescan / Delete / Close / Esc | DuplicateFinderViewModel.cs:35 / :62 / :88 | OK | Delete confirms first; the confirm is owned by MainWindow, not by this dialog |
| Metadata finder | Identify all / Apply / Cancel(X, Esc) | MetadataFinderViewModel.cs:49 / :95 / :159 | OK* | Apply does not recompute AlbumId or save the library (the default-on watcher re-import corrects it); Cancel during Apply does nothing |
| ReplayGain scanner | Start / Cancel / window close | ReplayGainScannerViewModel.cs:119 / :173; ReplayGainScannerDialog.axaml.cs:27 (OnClosing→CancelForClose) | OK | The only tool dialog that cancels its work on every close route |
| Send to Folder | Browse / Copy / Close-Cancel | SendToFolderDialog.axaml.cs:20; SendToFolderViewModel.cs:90 / :126 | OK | Close cancels while copying |
| Audio converter | Browse / Start / Cancel | AudioConverterDialog.axaml.cs:21; AudioConverterViewModel.cs:80 / :153 | OK | No OnClosing cancel (Alt+F4 route unverified) |
| Bulk lyrics | Start / Close-Cancel | BulkLyricsViewModel.cs:52 / :101 | OK | |
| YouTube download | Search / Download / Close / Esc | YouTubeDownloadViewModel.cs:103 / :162 / :197 | OK* | A download keeps running after close (CancellationToken.None :177) |
| Spectrogram | Esc / backdrop / X | SpectrogramWindow.axaml.cs:45 / :54; Closed→vm.Dispose :28 | OK | |
| EQ preset flyout (Settings) | Save (Enter/button) / delete / restore / close animation | SettingsView.axaml.cs:236,245 → SettingsViewModel.cs:5200; :5244; :5259; close hold :285 | OK | A deleted user preset still referenced by a track falls back correctly (SettingsViewModel.cs:3133-3150) |
| Color picker flyout | Spectrum / hue / hex commit / close animation | ColorPickerFlyout.axaml.cs:153 / :173 / :205 / :117 | OK | |
| Plugins | Install zip / Update confirm / Remove / Turn on community / Enable approval | SettingsViewModel.cs:228,238 / :258 / :278 / :188 / :208 | OK | InstallPackage extracts synchronously on the UI thread (plugins lane) |
| Version manager | Install release / Download to Downloads / Cancel | SettingsViewModel.cs:6788 / :6865 / :6925 | OK | 5-minute CTS timeout per download (a slow link can time out) |
| Crash banner | Clear logs | SettingsViewModel.cs:6943 (PreservedCrashBanner set at :6697) | OK | Shown only in Developer Mode |
| Track context menu (all views) | Play…Remove, Lyrics ▸, Badge ▸, Rate ▸, plugin items | Helpers/TrackContextMenuBuilder.cs:242 (Bind), :432 (plugins) | OK | Plugin failures are contained by PluginHost.RunTrackCommand; addToExistingPlaylistCommand is never wired (dead) |

### Wiring inventory — commands

Binding-name typos are ruled out as a class of defect: AvaloniaUseCompiledBindingsByDefault=true (Noctis.csproj), the repo has no ReflectionBinding, no x:CompileBindings="False" and no `new Binding(...)` in C#, and every `$parent[...]` command binding uses a typed cast. Nested `$parent[ItemsControl]` sites were checked (AlbumDetailView:359, HomeView:527/685/711/797, LyricsView:659/1163, LyricsPanelView:362, LibraryPlaylistsView:41, dialogs): the nearest ItemsControl carries the intended VM. View-to-VM mapping: CachedViewLocator (App.axaml.cs:43-60) for Songs, Albums, Artists, CoverFlow, Home, Favorites, Playlists grid, Statistics, Queue, Settings, Lyrics, Server, AudioCd, Visualizer and LyricsStudio; App.axaml DataTemplates for AlbumDetail, MoreByArtist, ArtistDetail, Playlist, PlaylistFeaturedArtists and Folders (recycled in place on a same-type swap).

| View | Control/label | Command/handler (file:line) | Status | Note |
|---|---|---|---|---|
| LibrarySongsView | Row menu: Remove from Library / Favorites / Add to Playlist / Convert / Scan ReplayGain | LibrarySongsViewModel.cs:343/383/335/367/375 via LibrarySongsView.axaml.cs:173,193 | BUG | acts on the Ctrl-selection even when the clicked row is outside it |
| PlaylistView | Row menu: Remove from Playlist / Favorites / Add / Convert / RG | PlaylistViewModel.cs:668/839/800/823/831 via PlaylistView.axaml.cs:437,550 | BUG | same as above |
| PlaylistView | Row menu 'Remove from Playlist' on a smart playlist | PlaylistView.axaml.cs:404, PlaylistViewModel.cs:666 | BUG | not persistent; the row comes back |
| PlaylistView | Selection across playlist→playlist swap | PlaylistView.axaml.cs:77 | BUG | stale _selectedTracks feeds menu commands |
| PlaylistView | Selection bar 'Favorite' | PlaylistView.axaml.cs:307 → PlaylistViewModel.cs:839 | MINOR | flips each track (the tooltip does say 'toggle') |
| LibraryAlbumsView | Tile menu Remove from Library / Favorites / Add to Playlist; hover '...' | LibraryAlbumsViewModel.cs:1191/1111/1100; LibraryAlbumsView.axaml.cs:162; Helpers/AlbumTile.cs:30 | BUG | selection used for an unselected tile; the '...' path skips Opening (stale list) |
| FavoritesView | Tile menu Remove from Library / Remove favorite / Add / Convert / RG; hover '...' | FavoritesViewModel.cs:576/486/469/531/541; FavoritesView.axaml.cs:95 | BUG | same |
| HomeView | Recent album menu Remove / Favorites / Add | HomeViewModel.cs:1038/979/957; HomeView.axaml.cs:200 | BUG | same |
| AlbumDetailView | Track menu Convert / Scan RG / Metadata (>1 selected) | AlbumDetailViewModel.cs:859/867/839 | MINOR | uses the selection for an unselected row (dialogs list the tracks) |
| MainWindow | Queue panel Ctrl+A / Escape | MainWindow.axaml.cs:743,1815 vs WindowKeyForwarder.cs:39 | BUG | the page's handler runs first (reverse tunnel order) |
| MainWindow | Global shortcuts Ctrl+←/→/↑/↓ in text boxes | MainWindow.axaml.cs:1553-1568 | BUG | take caret-navigation keys |
| SidebarView | Playlist row menu 'Delete' while that playlist's page is open | SidebarViewModel.cs:458→611 | BUG (low) | page stays open, edits lost |
| CommandPaletteDialog | 'Go to Settings' | CommandPaletteViewModel.cs:104 → MainWindowViewModel.cs:1958 | BUG (low) | inline Settings page, X does nothing |
| QueueView (palette 'Go to Queue') | Up Next / History rows | QueueViewModel.cs:29 RemoveFromQueueCommand, :35 PlayFromHistoryCommand | UNBOUND | rows are inert (no play/remove); Clear, Shuffle, Repeat, Save work |
| LibrarySongsView | OnQueueButtonClick | LibrarySongsView.axaml.cs:298 | DEAD | not referenced from XAML |
| MainWindowViewModel | ToggleSidebarCommand, CanGoBack, CanGoForward | MainWindowViewModel.cs:186,1655-1656 | DEAD | unbound; no change notification |
| PlayerViewModel | ViewCurrentTrackAlbum (CanExecute) | PlayerViewModel.cs:1069; re-evaluated at :1696, :3180 | OK | CanExecute refreshed on track change and library update |

OK counts (bindings or handlers present, target exists, behavior matches label on review): MainWindow 84/88; Sidebar 14/15; PlaybackBar 65/65; Home 19/22; Songs 11/16; Albums grid 14/18; Artists 6/6; Folders 2/2 (+code menus, single-item, OK); Playlists grid 13/13; Playlist 29/37; Favorites 11/17; AlbumDetail 53/56; ArtistDetail 44/44; MoreByArtist 14/14; Queue page 4/4 (+2 unbound VM commands); Lyrics 12/12; LyricsPanel 2/2; CoverFlow 3/3 (+wheel/arrow keys OK); Statistics 3/3; Server 14/14; AudioCd 6/6; CommandPalette 3/3 (+1 item wrong); LyricsStudioView 2/2; LyricsStudioPanel 23/23 (tap-mode Space verified correct); MiniPlayer 77/77 (hardcoded Space only; shortcuts service not used, by design per the Shortcuts.cs doc). Tray menu 5/5; taskbar buttons 4/4; macOS menu 4/4.

## Phase 2 — Logging, runtime verification and fixes

### 2.1 Logging added (commits on `audit/full-pass`)

| Commit | What |
|---|---|
| `316bc35` | **Silent test mode** behind the existing diagnostic env var `NOCTIS_AOUT=dummy`. The gapless engine renders into a new `NullWavePlayer`, which pulls the real provider chain at real-time pace with no device behind it. VLC gets `--aout=dummy` on every OS (the plugin `libadummy_plugin.dll` ships in the Windows payload). The keep-alive and both WASAPI sinks (the experimental one and exclusive mode) are off. One `Audio.SilentMode` warning goes to the session log. No behaviour changes when the variable is unset. 14 unit tests. |
| `185cd9b` | **Logging for the Phase 1 gaps** (table in the commit and in the logging-coverage map above). It covers: player-side `Playback.Pause/Resume` and `Pause/Resume.Ignored`, `VLC.Stop`, `VLC.Play.Aborted`, `Seek.Dropped/Failed`, `GaplessEngine.WriteDropped/PlayCallbackThrew/Underrun/StopIgnored/StoppedUnexpectedly/PauseFailed/ResumeFailed/SinkOpened`, device-change formats, `Exclusive.*`, `ReplayGain.Applied/TagReadFailed`, `Volume.SessionWriteFailed`, `SessionVolume.ReassertGaveUp`, `EQ.Applied`, `Audio.Setting`, `Queue.Restored`, the PlayTrack `seekMs`/`seekSource`, `SeekToPosition`, `Position.Rejected`, `Mute`, `StopAndClear(reason)`, `CurrentTrackRemoved`, `AdvanceQueue.Reentrant`, `Smtc.Button`, `Mpris.Command`, `MacRemote.Command`, a **UI-thread stall watchdog** (`UI.Stall` for blocks over 100 ms; it runs only while Developer Mode is on), and `Slow.<Op>` timings over 100 ms for PlayTrack, Navigate, SettingsSave and the library refreshes. Every line written from the audio or render thread goes through the new `DebugLogger.LogOffThread` and is rate-limited. Command errors already reached the session log through the existing global handlers (App.axaml.cs:73-79, Program.cs:105-115), so no change was needed there. |
| `bdcbfe3` | **Review fix-up.** Device-format reads on the device-change path are guarded so they only run with logging on. `UI.Stall` is limited to 1 line per second so the watchdog cannot feed itself through the Settings log view. |

Every commit: build exit 0, **zero new warnings** compared with the v1.5.4 baseline (287 instances), and a full suite of 3110 passed, 0 failed, 2 skipped.

### 2.2 Silent runtime test — method

The `wt-audit` Debug build was launched with `NOCTIS_AOUT=dummy`, an isolated scratch data folder (four generated test tracks), the Local API on port 9431 and Developer Mode on. Playback was driven over the Local API (`scratchpad/rt/drive.py`).

**Audio safety, proven twice before any play command:**
1. The `Audio.SilentMode` line must be in the session log.
2. `loopcap sessions` must show no audio session for the Noctis process. The engine renders from startup, so a broken null output would already own one.

After every run `loopcap sessions` showed **no Noctis session on the output device** ("noctis pid … absent"). Nothing reached the speakers.

**Test tones:** four slow sine sweeps with different content in L (0.30 amplitude) and R (0.25): FLAC 44.1 kHz, FLAC 48 kHz, MP3 44.1 kHz and FLAC 96 kHz/32-bit. A sweep never repeats and never jumps. So any sample-to-sample jump above the sweep's maximum slope (about 0.035) is a click, any exact 2 ms repeat within 50 ms is replayed audio (the old buzz signature), and R louder than L is a channel swap. The engine's own output was captured with the existing `NOCTIS_ENGINE_TAP` and analysed with `scratchpad/rt/analyze_tap.py`.

**Session 1 actions:** play, seek to 20 s, a volume sweep (40→90→20 in 16 ms steps), pause 2 s, resume, next (cold), seek to B's end minus 6 s so the B→C boundary is crossed naturally, seek, next, previous, seek, pause.

**Session 2 (restore):** relaunch on the saved queue, seek to 15 s **before** play, then play.

The engine ran at **48 kHz stereo** on this machine (the render reads are 960 floats = 10 ms).

### 2.3 Runtime results

| # | Result | Evidence (quoted from `run1/`, `run2/` logs and the tap) |
|---|---|---|
| **A26** | **CONFIRMED — this is the "timeline slider desync on restore" bug.** A seek before the first play of a restored track is dropped, while the UI and API already show the new position. Play then starts from the stale restored position. | Restored at 32.0 s: `Queue.Restored … savedPosSec=32.0, resumeMs=31991`. The user seeks to 15 s: API `positionMs: 15000`, `SeekToPosition | targetMs=15000, state=Stopped`, **`Seek.Dropped | reason=noMedia, targetMs=15000`**. Play: **`PlayTrack … seekMs=31991, seekSource=restore`**, and the position 3 s later is 34.9 s instead of about 18 s. |
| **R1 (new)** | **Any pause is misread as a disk stall and permanently raises VLC's read-ahead to 3.5 s.** After a 2 s pause and resume, the adaptive read-ahead fired. The same misfire is expected with the real `WasapiOut`: its `Pause()` also stops pulling audio, and the pts gap comes from VLC's clock during the pause. | `Playback.Pause | engine=True` … `Playback.Resume` → `Warn: GaplessEngine.RenderStall | gapMs=2030.7, gcPauseMs=0.0` → `Warn: GaplessEngine.PtsGap | slot=0, gapMs=2005` → **`Warn: GaplessEngine.ReadAheadRaised | gapMs=2005, fileCachingMs=1000->3500 (input stalled past the read-ahead window; media opened from now on read further ahead)`** → `position-timer stall: gapMs=2110`. Code: `VlcAudioPlayer.cs:3994` (PtsGap) and `:5511` (ReadAheadRaised). |
| **R2 (new)** | **The idle memory trim's forced full GC stalls the audio render thread during playback.** The 100 ms buffer absorbed this 39 ms stall, but a slower machine or a larger heap could turn it into a dropout. | `[Memory] trim after startup scan: managed 37 MB -> 34 MB` immediately followed by **`Warn: GaplessEngine.RenderStall | gapMs=39.1, gcPauseMs=34.1, gcs=1/1/1`**. Code: `MemoryTrim.cs:61` `GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true)`, requested after the startup scan while music plays. |
| **R3 (new)** | **Seeks click: 3 of 4 seek junctions were rendered with no declick ramp.** The pre-seek waveform steps straight to the post-seek audio, which is the butt-splice the Aug-13 fix was meant to prevent. This happened on 44.1 kHz, MP3 and 96 kHz sources. | Tap at 30.004 s (seek in the MP3): `L = -0.1515 → +0.0007` in one sample (the sweep moves at most about 0.035 per sample). Tap at 40.025 s (seek in the 96 kHz file): `L = -0.1386 → +0.0025 → +0.0709`, a direct splice with no ramp and no fade. Tap at 8.897 s (seek in the 44.1 kHz FLAC): `R = +0.0504 → -0.0004 → -0.1548`. The design (`GaplessSpliceCore.cs:525-651`) should ramp `_lastFrame` to zero and then fade in. |
| **R4 (new)** | **One seek junction attenuates L and R unequally.** This fits the declick ramp indexing channels relative to the run start (`_lastFrame[i % channels]`, `GaplessSpliceCore.cs:569, 648`): an odd run start would swap channels for the ramp. That is the same mechanism as A08, which needs a 44.1 kHz device and could not trigger on this 48 kHz machine. | Tap block at 20.990 s (seek to B's end minus 6 s): `rmsL=0.106 rmsR=0.132` against `0.211 / 0.176` on either side. |
| Buzz | **Not reproduced.** Zero replayed windows (exact 2 ms repeats within 50 ms) in 42 s of engine output across 4 seeks, 3 track changes and a pause. The Aug-13 `Array.Clear` fix holds. The clicks in R3 are the remaining audible junction defect. | `REPLAYED WINDOWS (exact 2 ms repeat within 50 ms): 0`. |
| Gapless | **Works.** The B→C boundary was a true splice. The 30 ms of silence after it is C's own MP3 encoder delay (its first 1254 frames have peak 0), not a gap Noctis inserted. | `Gapless.Advance | … remainingMs=403`, **`GaplessEngine.Spliced | path=03 Sweep C.mp3, crossfadeMs=0`**, `GaplessEngine.SegHead | slot=1 … frames=1254, peak=0`. |
| Track starts | **Clean.** No discontinuity at any cold start (A, B, D, the Previous restart). The 200 ms pre-buffer plus the 5 ms fade-in work. | No flagged jumps at the tap times for play, next or previous. |
| Pause/resume (A07) | **Resume edge clean in the tap** (20 ms pad, then fade-in). The **pause edge is not observable in silent mode**: on real hardware `WasapiOut.Pause()` lets the device drain its buffer and starve, and that happens below the tap. A07 remains a code-level finding that needs a real-device check. | Tap sound segments `4.91–17.00 s`, `17.02–26.93 s`, with no flagged jump at 17.00/17.02. |
| Volume | **The volume-static fix could not be verified in silent mode**, because there is no OS audio session to ramp. The volume path was exercised: 60 writes in 1.2 s, all through the ramp, and nothing bypassed it. | `Warn: Volume.SessionWriteFailed | origin=ramp …` and `SessionVolume.ReassertGaveUp` are expected here (no session). Also seen: `SessionVolume.Resolve | matched=0 …` is logged **432 times in about 45 s** when no session exists. It is harmless here, but it would flood the 500-line session log in a real "session lost" state. |
| UI stalls | Two UI-thread stalls: **437 ms** during startup, before the first play, and **113 ms** at the first play. | `[UI] UI.Stall | blockedMs=437`, `[UI] UI.Stall | blockedMs=113`. |
| Seek warnings | Every seek logs one VLC `buffer too late (-75…-94 ms): dropped` right after `DEMUX_SET_TIME`. This is VLC discarding one stale pre-seek block; nothing audible was linked to it. | `[VLC] Warning main: buffer too late (-94489 us): dropped`. |

### 2.4 Fixes

_In progress._

## Phase 3 — Dependencies

_In progress._

## Final report

_In progress._

## Appendix A — Lane notes and files read
### Audio pipeline

**audio-core:** Verification method: static reading only. Noctis was not launched and nothing was built or played. The one runtime check was a scratch in-memory PowerShell Add-Type snippet, outside the repo, confirming that Array.Copy from float[] into a byte[]-backed float[] view throws ArrayTypeMismatchException (the basis of the high finding).

BUZZ ON TRACK START: the root cause was not identified from the code.
- Candidates in this lane: (a) the stale-frame declick ramp after a paused sink (low finding); (b) an unverified hypothesis about `:start-time` opens (restore-resume, per-track start, Previous/drag-to-start with a residual offset, Ended-restart seeks). VLC 3 applies :start-time as an INPUT_CONTROL_SET_TIME, which can flush the decoder. If that flush reaches amem after the first blocks, GaplessTrackSegment.Flush sets `_flushRearmed`, and the cold segment's pre-buffer gate drops from the 200 ms start threshold to FlushRearmThresholdMs = 50 ms (GaplessSpliceCore.cs:543).
- Recommended next step: an NOCTIS_ENGINE_TAP capture of resume-position track starts vs. start-from-0, together with a check for SegHead + flush ordering.

Short-track finding: the ≤ ~6–8 s threshold is inferred from VLC's 2 s AOUT_MAX_PREPARE_TIME pacing, amem's NULL time_get, and the VM's 8 s staging lead. The exact threshold needs a runtime check. The length of the engine-tail pause window (EOF before the audible end) is inferred the same way and matches the code's own comments (~0.8–2 s).

Checked and not reported:
- A QueueInactivePlayerCleanup vs. re-staging race (the cleanup stopping a player that was re-staged within its delay). It is prevented in practice: the VM's 2 s commit guard is longer than the 1 s cleanup delay, and the 45 s AutoMix minimum track length covers the crossfade case. The cleanup still acts on a player reference without re-validating it, so it is fragile.
- The classic-path crossfade fallback fade-out reading `_player.Volume`. Not reported because VLC's mmdevice reports session volume changes back into the player volume.

Do-not-regress items (verified intact):
- LibVLC caching: DefaultCachingMs = 1000 and the --file/disc/live/network-caching args (lines 497-516), plus the adaptive read-ahead (5305-5336, only raises the per-media :file-caching). No change to caching is proposed.
- Session-volume ramp: 16 ms tick, 10‰ step (64-80, 1145-1206).
- Seek duck on the classic session path (4877-4901).
- Engine seek without duck (4863-4876).

Prior audit items, re-read in the current code:
- H1: fixed (3265-3273, 3454-3460, 3128-3136).
- H2: fixed (2920-2921).
- M1: fixed (4494-4505, 3153).
- M3: fixed (2320-2389).
- M2: fixed except the IsPlaying-gate residual, reported inside the Pause finding.
- M4: still open, reported with prior_audit_ref.

Other observations, not reported as findings:
- The high finding (TempoStretchProvider/PitchShiftProvider, GaplessSink) and several others sit partly in sink-lane files. They are reported because they are high impact and interact directly with VlcAudioPlayer.
- Minor, unverified: WindowsSessionVolume resolves the eConsole default endpoint (WindowsSessionVolume.cs:138) while GaplessSink binds the eMultimedia default (GaplessSink.cs:80). If a user sets different Console and Multimedia defaults, volume may drive the wrong device's sessions.
- ReplayGain tag cache (VlcAudioPlayer.cs:2045-2061) is keyed by path only. Scanning the currently playing file leaves its RG stale until another track plays.
- On the engine, EQ changes are heard one ring depth late (~1–2 s, longer after adaptive read-ahead raises caching). This is a design trade-off, not reported.

Not read: WasapiGainOutput.cs (exclusive/NOCTIS_WASAPI path), the full TempoStretchProvider/PitchShiftProvider bodies, VlcSilenceKeepAlive, SpectrumMeter.

Sources: https://github.com/videolan/vlc/blob/3.0.x/lib/media_player.c, https://github.com/videolan/vlc/blob/3.0.x/src/input/decoder.c, https://github.com/videolan/vlc/blob/3.0.x/include/vlc_aout.h, https://github.com/videolan/vlc/blob/3.0.x/modules/audio_output/amem.c, https://github.com/naudio/NAudio/blob/master/NAudio.Wasapi/WasapiOut.cs, https://github.com/naudio/NAudio/blob/master/NAudio.Core/Wave/SampleProviders/SampleToWaveProvider.cs, https://github.com/niklam/iracedeck/issues/849, https://veg.by/en/blog/2022/07/28/pc-auto-sleep-with-audio/

**audio-output:** VOLUME SLIDER STATIC fix: intact on the default engine path. The fix is the float session-volume ramp worker, not an 80 ms debounce: VlcAudioPlayer.cs:74-80 (tick 16 ms, step ≤10‰), 1026-1049 (ScheduleVolumeWrite), 1123-1135 (ApplyRampLevel → WindowsSessionVolume.SetLevel), 1145-1206 (worker with timeBeginPeriod(1)). No ~80 ms debounce exists in the current code. The legacy NOCTIS_VOL_SETTLE debounce (64-66, 1054-1095) defaults to 0 and only runs when _sessionVolume is null. The header comment at 52-54 still says 'default 4 / 5' while the code uses 16/10; that is documentation drift only.

Other session writers on the engine path are single-value writes: ReapplySessionVolume at track start and sink rebuild, and the opt-in play/pause fade (FadeSessionLevelBlocking, 35 ms steps). The engine seek no longer ducks (4863-4876), and mute is post-ring in MuteGateProvider. The only conflict found is the ramp worker racing a fade (low finding).

WaveBuffer-pun audit of every call site on the render buffer:
- TempoStretchProvider.cs:77 Array.Copy: already reported.
- PitchShiftProvider.cs:101 Array.Copy: NEW (finding).
- MuteGateProvider.cs:57 uses Span<float>.Clear, which goes through the static type and is correct.
- GaplessSpliceCore pad loop, WasapiGainOutput loops, TapProvider, ReplayDetector, BeatTapProvider/BeatMeter and SpectrumMeter use element access only, which is correct.
- UpmixSampleProvider hands a REAL float[] (_scratch) downstream, so with upmix on the pun crashes do not reproduce. This may explain inconsistent repros.
- NAudio WdlResampling/MonoToStereo only run if the segment format differs from the sink format, which never happens (segments are created at sink format).

NAudio render-thread exceptions: NAudio's PlayThread does catch them and raises PlaybackStopped(ex), so audio does not die silently by itself. GaplessSink rebuilds, but see the RebuildLoop race finding for when the recovery itself is lost.

Device change: no IMMNotificationClient is used. The engine polls the default device every 2 s (GaplessSink.cs:51) and relies on PlaybackStopped for invalidation. The keep-alive polls every 5 s. A device invalidated while the sink is paused is detected only by the default-ID poll or on resume.

Latent and unverified, not reported as findings:
(a) At speed ≠ 1 (currently masked by the reported crash), TempoStretch never flushes its last ~needFrames (~75-100 ms) at segment EOS, and Tempo/Pitch keep pre-seek frames across a Flush, so up to ~100 ms of stale pre-seek audio plays after a seek at non-unity speed or pitch.
(b) Each GaplessTrackSegment allocates capacitySeconds=20 of float on the LOH per track: 7.7 MB at 48 kHz, ~31 MB at 192 kHz, ~61 MB at 384 kHz. This is GC pressure (the StallProbe GC counters would show it), not a leak.
(c) The lock-convoy/priority-inversion concern from BUZZ_INVESTIGATION was not re-measured. The render Read takes GaplessTrackSegment._gate ~6-8 times per loop iteration and Write does a per-sample % under the lock. No current evidence of stalls was available (the app was not launched, per the rules).
(d) The COM apartment of WindowsSessionVolume._enumerator depends on which thread resolves VlcAudioPlayer from DI (Program.cs:246). Whether MMDevice objects are agile was not verified.
(e) BeginCrossfade drops an in-flight fading tail with no ramp (GaplessSpliceCore.cs:431). This is only reachable with back-to-back crossfades into a track shorter than the fade (edge case).
(f) Whether shared-mode Initialize accepts NAudio's non-extensible 6/8-channel IEEE float WAVEFORMATEX for upmix was not verified. On failure, RebuildLoop retries every 4 s with no audio until upmix is turned off.
(g) The NAudio 2.3.0 IL could not be inspected here. The Pause/Console-role/PlaybackStopped claims cite the NAudio master source plus the IL-verified notes in BUZZ_INVESTIGATION.md.

No regression of the listed do-not-regress items was found in scope: LibVLC caching untouched, session-volume ramp intact.

**audio-vm:** Summary: this sweep found 15 new defects in the audio/VM layer, none duplicating the listed earlier-sweep items.

Most urgent:
- Default gapless defeats 'stop after current' and the sleep timer's end-of-track mode.
- Any queue edit, pause or seek during a track's parse aborts the new track's load. Cause: VlcAudioPlayer uses one _skipCts for fades, prepares and the open.
- A restored mute is UI-only.
- The reported restore desync: seeks on a restored, not-yet-played track are dropped (no media loaded) and PlayTrack then applies the stale _resumePositionMs.

Focus answers:
1. Timeline desync on restore. Main cause: the seek-while-Stopped finding. A second cause is the shutdown-ordering finding: the final position is often never written, because PauseForShutdown takes no snapshot and the save runs after steps that can exceed the 4 s deadline. The resume itself is sound: PlayTrack passes PendingSeekMs -> ':start-time', and the engine seeds the segment base. One unverified risk remains: after 600 ms the VM accepts any tick up to target+elapsed+4, so a pre-open 0/low report from VLC on slow storage would snap the slider to 0:00. Not filed without runtime evidence; the suggested Position.Rejected logging would show it.
2. Slider drag vs timer. OnPositionChanged returns while _isSeeking. Both views (bar: tunnel handlers + capture; mini player: tunnel press/release + CaptureLost) route through BeginSeek/EndSeek, and every seek slider is Focusable=False, so keyboard changes can't bypass the protocol. Edge case, not filed: if a track changes mid-drag, PlayTrack's PositionFraction writes set a pending target, so the release seeks the NEW track to the pointer position.
3. SeekSettleWindowMs = 300 (< 1000 ms file-caching, unchanged). The 9 s stale guard assumes 1x speed (filed).
4. Volume. Every VM write goes through PlayerViewModel.Volume -> OnVolumeChanged -> IAudioPlayer.Volume -> ScheduleVolumeWrite (ramp / sink gain / settle debounce). Writers covered: bar slider and wheel, mini sliders and NudgeVolume, Ctrl+Up/Down (MainWindow.axaml.cs:1593), MPRIS Volume, WebRemote/LocalApi. No bypass found. The ~80 ms smoothing lives in VlcAudioPlayer, not the VM. The int<->double TwoWay binding cannot loop, and the VM does not observe MediaPlayer.VolumeChanged. Minor, unfiled: UnmuteForAdjust runs before the new Volume is set, so clicking the slider lower while muted briefly un-gates at the old level before the ramp converges. Magnitude unmeasured.
5. Position timer cost on the UI thread. One coalesced Post per 100 ms and 4 property sets. With AutoMix on, each tick builds a transition plan plus a log-key string (pure, cheap). The real risk is the native _player.Length call on the UI thread (filed, likely).
6. SMTC/MPRIS/macOS vs UI. All three marshal to the UI thread via Post, so commands serialize with UI commands. Play/Pause checks are idempotent; PlayPause toggles. Unfiled polish:
   - SMTC timeline isn't forced on pause (1 s throttle).
   - MPRIS Rate and macOS Now Playing report rate 1.0 regardless of speed, so external timelines drift at ≠1x.
   - Discord presence restarts from 0:00 on a resumed track (OnTrackStartedForIntegrations passes TimeSpan.Zero).
7. Next/Previous/repeat/shuffle.
   - Repeat One is correctly bypassed by user skips, and the planner refuses AutoMix under Repeat One.
   - Filed: Repeat All wrap and the MPRIS shuffle bypass.
   - Design question, not filed: after stop-after-current, Play replays the finished track from 0 rather than advancing (the test asserts this).
   - Every PlayTrack (including resuming a restored track) counts a play.
8. Fire-and-forget. AsyncRelayCommand faults are caught by the Dispatcher.UIThread.UnhandledException handler (App.axaml.cs:73), so they don't crash. Background tasks: the radio refill lacks try/finally (logging gap). The per-play save falls back to a full save on journal failure.

Not re-reported: engine-tail pause (VlcAudioPlayer.cs:4243). My Pause finding is the separate Opening/Buffering trigger on the same guard.

Nothing was built or run, and no files were changed. The findings that depend on timing (the aborted load, the Opening pause, the UI-thread get_length call, the shutdown deadline) are marked needs_runtime_check.

_Files read (75):_ `AUDIT.md (grep for in-scope files; engine not covered by prior audit)`, `AUDIT.md / AUDIT_2026-07-24.md (grep for restore/mute/stop-after/SMTC leads)`, `AUDIT.md H1, H2, M1-M4`, `C:/Users/okfer/Downloads/Noctis/Noctis/BUZZ_INVESTIGATION.md`, `C:/Users/okfer/Downloads/Noctis/Noctis/BUZZ_INVESTIGATION.md (full)`, `External: VLC 3.0.x lib/media_player.c, src/input/decoder.c, include/vlc_aout.h, modules/audio_output/amem.c; NAudio WasapiOut.cs and SampleToWaveProvider.cs`, `FIXLOG.md (H1/H2/M1-M4 status)`, `HANDOFF_AUDIO_BUGS.md`, `NAudio sources: WasapiOut.cs, SampleToWaveProvider.cs (GitHub master); MS docs AUDCLNT_STREAMFLAGS (NOPERSIST/AUTOCONVERTPCM)`, `src/Noctis.Core/Models/AppSettings.cs (defaults via grep: Gapless, RestoreLastTrack, ScanOnStartup)`, `src/Noctis.Core/Models/AppSettings.cs 146-155`, `src/Noctis.Core/Models/QueueState.cs (full)`, `src/Noctis.Core/Services/AdvancedTagIO.cs (WriteCustomField 561-602)`, `src/Noctis.Core/Services/IAudioPlayer.cs (full)`, `src/Noctis.Core/Services/LibraryService.cs (638-654, 1285-1319, 1577-1617)`, `src/Noctis.Core/Services/PersistenceService.cs (300-348 queue load/save, write gates via grep)`, `src/Noctis/App.axaml.cs (60-80 dispatcher handler, 195-245 shutdown)`, `src/Noctis/Controls/WaveformSeekBar.cs (render/invalidate paths via grep)`, `src/Noctis/Program.cs (unhandled-exception handlers via grep)`, `src/Noctis/Services/AudioAnalysis/AudioAnalysisCoordinator.cs (full)`, `src/Noctis/Services/AudioAnalysis/AudioAnalysisService.cs (full)`, `src/Noctis/Services/AudioAnalysis/AudioAnalysisStore.cs (full)`, `src/Noctis/Services/AudioAnalysis/BpmDetector.cs (full)`, `src/Noctis/Services/AudioAnalysis/Fft.cs (full)`, `src/Noctis/Services/AudioAnalysis/KeyDetector.cs (full)`, `src/Noctis/Services/AudioAnalysis/SideDecodeMeterFeed.cs (full)`, `src/Noctis/Services/AudioAnalysis/SpectrogramRenderer.cs (full)`, `src/Noctis/Services/AutoMixFadeMath.cs (full)`, `src/Noctis/Services/AutoMixKeyTempo.cs (full)`, `src/Noctis/Services/AutoMixPreparedTransitionValidator.cs (full)`, `src/Noctis/Services/AutoMixTransitionPlanner.cs (full)`, `src/Noctis/Services/AutoMixTransitionPlanner.cs (grep: per-tick cost, RepeatMode handling)`, `src/Noctis/Services/AutoplayService.cs (full)`, `src/Noctis/Services/BeatMeter.cs (BeatTapProvider only + grep for array ops)`, `src/Noctis/Services/BeatMeter.cs (full)`, `src/Noctis/Services/CoreAudioComInterop.cs (full)`, `src/Noctis/Services/EmphasisBell.cs (full)`, `src/Noctis/Services/GaplessSink.cs (full)`, `src/Noctis/Services/GaplessSpliceCore.cs (full)`, `src/Noctis/Services/GaplessSpliceCore.cs (grep: PositionMs, ActiveSegment)`, `src/Noctis/Services/MacNowPlayingService.cs (100-265)`, `src/Noctis/Services/MprisService.cs (190-430, 495-725)`, `src/Noctis/Services/MuteGateProvider.cs (Read)`, `src/Noctis/Services/MuteGateProvider.cs (full)`, `src/Noctis/Services/PitchShiftProvider.cs (1-130)`, `src/Noctis/Services/PitchShiftProvider.cs (full)`, `src/Noctis/Services/RadioService.cs (grep)`, `src/Noctis/Services/ReplayGainScannerService.cs (270-314)`, `src/Noctis/Services/ShortcutService.cs (full)`, `src/Noctis/Services/SilentWavFile.cs (full)`, `src/Noctis/Services/SmtcService.cs (full)`, `src/Noctis/Services/SpectrumMeter.cs (full)`, `src/Noctis/Services/TaskbarIntegrationService.cs (grep) + src/Noctis/Views/MainWindow.axaml.cs 1200-1300 (taskbar wiring)`, `src/Noctis/Services/TempoStretchProvider.cs (1-100)`, `src/Noctis/Services/TempoStretchProvider.cs (full)`, `src/Noctis/Services/UpmixSampleProvider.cs (Read)`, `src/Noctis/Services/UpmixSampleProvider.cs (full)`, `src/Noctis/Services/VlcAudioPlayer.cs (740-940 props/volume/mute, 1026-1175 volume ramp, 1463-1596, 2241-2281 rate, 2310-2340, 2510-2531 CancelPreparedNext, 2566-2999 Play/PlayInternal, 3981-4003, 4222-4448 Pause/Resume/Stop/Seek, 4457-4754 EndReached/position timer, 4955-4979)`, `src/Noctis/Services/VlcAudioPlayer.cs (all 5357 lines, read in chunks)`, `src/Noctis/Services/VlcAudioPlayer.cs lines 1-420, 560-1500, 1590-2300, 2296-3008, 3760-4130, 4130-4460, 4456-5093 (volume ramp, session volume, mute, pause/resume, seek worker, engine callbacks, sink wiring/rebuilt, output-mode rebuild, PrepareNext staging, end/position logic, Dispose)`, `src/Noctis/Services/VlcSilenceKeepAlive.cs (full)`, `src/Noctis/Services/WasapiGainOutput.cs (full)`, `src/Noctis/Services/WasapiSilenceKeepAlive.cs (1-110)`, `src/Noctis/Services/WasapiSilenceKeepAlive.cs (full)`, `src/Noctis/Services/WindowsSessionVolume.cs (full)`, `src/Noctis/ViewModels/MainWindowViewModel.cs (670-720 startup restore, 940-1006 ShutdownAsync, 2830-2891 integrations, 2986-2990)`, `src/Noctis/ViewModels/PlayerViewModel.cs (168-199, 425-544, 1690-1714, 1940-2059, 2205-2254, 2581-3069)`, `src/Noctis/ViewModels/PlayerViewModel.cs (all 3190 lines)`, `src/Noctis/ViewModels/PlayerViewModel.cs 1663-1690 (volume handlers), 2760-2970 (AutoMix/gapless prepare timing)`, `src/Noctis/Views/MiniPlayerWindow.axaml (all seek/volume sliders via grep: Focusable=False, TwoWay bindings; waveform style 225-250; 885-900)`, `src/Noctis/Views/MiniPlayerWindow.axaml.cs (seek/volume wiring 100-150, wheel 1097-1104, close/lifecycle 1117-1172)`, `src/Noctis/Views/PlaybackBarView.axaml (seek slider 1205-1217; volume flyout/slider references via grep)`, `src/Noctis/Views/PlaybackBarView.axaml.cs (ctor, seek handlers 764-844, volume handlers 893-983, attach/detach/DataContext 146-259)`, `tests/Noctis.Tests (grep of GaplessSpliceProvider constructors: rates/channels under test)`, `tests/Noctis.Tests/AutoplayQueueTests.cs (1-140)`

### UI performance

**ui-lists:** No RequestBringIntoView handler exists in src/, and git log -S "RequestBringIntoView" returns no commits in this worktree's history, so the "RequestBringIntoView scroll-jump fix" could not be checked as intact. The anti-jump measures that do exist are ListBoxItem Focusable=False and AutoScrollToSelectedItem=False in Songs, Albums, Artists, Favorites, Playlist, Server and the Queue popup. The Folders TrackList has neither; I did not treat that as a defect. If the fix lives in another branch, it is not in v1.5.4.

Do-not-regress checks. The lyrics view keeps TextWrapping="Wrap" on the line TextBlock (LyricsView.axaml:1180) and on the romanization/translation text (1346, 1369). The line layout uses Panel/StackPanel; I saw no Grid regression. Caching and volume code are out of this lane and I did not touch them.

Prior items verified fixed in current code:
- AUDIT.md H4: QueueView is now a Grid with a star row (QueueView.axaml:47-52).
- AUDIT.md M5: ArtworkPathConverter is still declared in 10 views but no binding uses it.
- AUDIT.md M9: the ServerView album grid is now a row-chunked ListBox.
- AUDIT.md L3: title-cell children are cached in Tag.
- AUDIT_2026-07-24: CachedImage now clears on source change for recycled rows. The ReplayGain dialog is virtualized and uses a dictionary lookup. The playlist drop-indicator loop now uses realized containers only. The mini-player queue is virtualized. The Stats play log is capped at 100.
- Spinners are gated on [IsVisible=True], and EqVisualizer parks hidden instances.

Deliberately not re-reported. The AlbumDetail track list is non-virtualized by design (HANDOFF) and is now filled in 200-row chunks (AlbumDetailViewModel.cs:574-598). One edge I did not report: its scroll restore gives up after 10 layout passes, which may land short for very large albums whose chunks are still streaming in.

Unverified risks not filed as findings:
- ArtistDetailView Albums/Singles/Appears On tabs and Top Favorites use non-virtualized UniformGrid/StackPanel inside the page ScrollViewer (ArtistDetailView.axaml:1092, 1108, 1076, 1129). An artist with hundreds of releases (e.g. a "Various Artists" entry, if one is created) would realize every tile, each with an inline ContextMenu. I found no exclusion of "Various Artists" in the ViewModels.
- The mini player search results (capped at 30 and streamed) are non-virtualized with the same heavy row that was measured at 1.6-2.5 ms per row. Mitigated.
- Server search tracks are a ListBox inside a StackPanel inside a ScrollViewer, so they are unvirtualized. Server result counts are presumably small; I did not check the cap.

All timing figures in the findings are estimates from code structure and the team's own measurements cited in code comments. I did not launch the app or run a profiler.

**ui-thread:** Nothing was built or run, so every timing figure is an estimate from reading the code. The findings marked needs_runtime_check still need profiling on a large library (50k+ tracks) and, for the per-track-change folder probes, on an SMB share.

Confirmed clean (re-read in the current code, no finding):
- The paths that move work off the UI thread: Songs/Albums/Artists rebuilds, Folders tree build, Home, Playlist LoadTracks, Command Palette, the lyrics local probe and colour warm-up, player artwork decode, Settings stats/storage, drop-import moves, and the metadata editor's TagLib reads and writes.
- The converters: ArtworkPathConverter is declared but never used in any .axaml. The IconKey and TrackContextMenu icon bitmaps are cached. PreBlurredArtworkConverter works on the 512-wide player bitmap.
- No `.Result`, `.Wait()` or synchronous `Dispatcher.UIThread.Invoke` reachable from the UI thread in ViewModels, Views, Helpers or Converters.
- Startup: InitializeAsync calls Settings.LoadAsync a second time, but it is guarded by `_settingsLoaded`. Plugin loading is posted at Background priority.
- Do-not-regress items in my scope (RequestBringIntoView handling, lyrics Grid + TextWrapping) were not touched by anything reported here. Volume debounce and caching are outside this lane.

Not reported because I could not confirm them:
- MainWindow.OnWindowDragOver re-enumerates the dropped paths and hits File/Directory.Exists on every DragOver. It is also registered as a Tunnel handler on the Window plus a Bubble handler on the root panel. Whether both fire depends on the RoutingStrategies of Avalonia 12's DragDrop.DragOverEvent, which I did not check.
- CreateSmartPlaylistDialogViewModel re-evaluates the whole library on every rule-VM PropertyChanged, several times per field change. The evaluator looks cheap per track, so this was not promoted.

Outside this lane, left for other auditors:
- The ArtistDetailViewModel comment says LibraryUpdated fires on every play-count save. It does not any more: SaveTrackUserStateAsync journals to SQLite without raising the event.
- HashSet-based Ctrl-selection is handed to commands via `.ToList()`, which does not preserve on-screen order. Playlists created from a selection may therefore come out in arbitrary order.

**ui-anim-leaks:** No finding is reported for infinite animations. Avalonia 12 automatically pauses keyframe/style animations whose target is not IsEffectivelyVisible and completes them on detach (PlaybackBehavior.Auto; PR https://github.com/AvaloniaUI/Avalonia/pull/20820, AnimationInstance`1.cs `_shouldPauseOnInvisible`). Every INFINITE spinner and dot animation is also gated with [IsVisible=True] or .active selectors.

No crash finding is reported for async void or fire-and-forget. Every async void in scope runs on the UI thread (none use ConfigureAwait(false)), and App.axaml.cs:73-79 marks Dispatcher.UIThread.UnhandledException handled, so a fault is logged rather than killing the process. Unobserved task faults are logged by Program.cs:111-113.

Prior audit items re-checked against current code:
- AUDIT.md M8 (EqVisualizer timer on hidden rows): mitigated. It now checks IsVisible and IsEffectivelyVisible, uses a 250 ms hidden poll and an IsAttached guard; it still animates Height, but the layout pass stays inside its fixed-size template.
- M10 (ungated spinners): fixed with [IsVisible=True] selectors.
- M11 (AlbumDetailView _bgHandler): fixed in OnAlbumDataContextChanged.
- L4 (LyricsView subscription sets): fixed with the canonical SubscribeVm/UnsubscribeVm.
- L5 (lyrics sync timer): fixed with a tick backstop, but that backstop is defeated by finding 1.

Checked and not reported:
- Duplicate-loop races in KawarpBackground, SpectrumVisualizer, WaveformSeekBar and the LyricsView chase require an off-then-on toggle within one frame and are bounded.
- VideoBackdrop and AnimatedCoverFeed LibVLC session teardown looks correct.
- Transient page VMs (Album, Artist, Playlist) unsubscribe in Dispose, and navigation history is capped at 30 with disposal.
- MarqueeTextBlock, GlassPanel, AnimatedCoverImage, LottieToggle and CachedImage pair their static-event subscriptions with attach and detach.

Could not determine without running the app (read-only audit, app not launched):
- Whether Avalonia's MediaContext keeps pumping RequestAnimationFrame callbacks when every window is hidden (tray). This is why a possible hidden-main-window cost for PlaybackBarView's own marquee (PlaybackBarView.axaml.cs:355-385, no IsEffectivelyVisible check) is not reported.
- The exact runtime ordering of the DataContext clear versus the visual detach used in finding 3 (inferred from ContentPresenter behaviour).
- The magnitude of the per-frame cost in findings 1 and 2. Only the mechanism is proven from code; a dev-mode trace (see logging_gaps) would confirm it.

_Files read (156):_ `AUDIT.md / AUDIT_2026-07-24.md (targeted greps for overlap)`, `Enumerated all 59 src/Noctis/Views/*.axaml, 6 src/Noctis/Controls/*.axaml and 7 src/Noctis.UI/**/*.axaml, and grep-scanned all of them for ItemsControl/ListBox/TreeView/ItemsRepeater/DataGrid, ItemsPanel, ScrollViewer and infinite animations`, `Prior audits: AUDIT.md, AUDIT_2026-07-24.md, FIXLOG.md, HANDOFF_AUDIT_FIXES.md (list/virtualization items)`, `Repo-wide greps over src/Noctis and src/Noctis.UI: DispatcherTimer/Threading.Timer/PeriodicTimer creation, RequestAnimationFrame, static events and subscribers, += on long-lived publishers, async void, ContinueWith, HttpClient/Process/FileSystemWatcher, Transitions target properties`, `Services: SendToFolderService.cs (40-140), LibraryService.cs (1776-1787 + LibraryUpdated raise map), AudioAnalysisCoordinator.cs (180-215), LibraryRemovalHelper.cs (391-410)`, `ViewModels: LibrarySongsViewModel (80-140, 440-540), LibraryAlbumsViewModel (64-87, 366-375, 425-554), PlaylistViewModel (655-745), PlayerViewModel (1205-1381), MiniPlayerViewModel (50-80, 318-457), AlbumDetailViewModel (560-610), ReplayGainScannerViewModel (full), AudioConverterViewModel (55-160), BulkLyricsViewModel (full), MetadataFinderViewModel (20-60), OrganizeFilesViewModel (25-95), SendToFolderViewModel (35-85), DuplicateFinderViewModel (25-65), SettingsViewModel (5305-5500), StatisticsViewModel (284-295), LibraryFoldersViewModel (120-180), MetadataViewModel (2610-2655)`, `XAML: every IterationCount=INFINITE style in LyricShareDialog, LyricsPanelView, LyricsStudioView, LyricsView, MainWindow, MetadataWindow, MiniPlayerWindow, SettingsView + all loading-spinner usages; LyricsView.axaml 495-540, 1030-1270; VisualizerView.axaml 55-80; MainWindow.axaml 715-735, 1255-1275, 1296-1315; MiniPlayerWindow.axaml 668-690; PlaybackBarView.axaml 860-1140; EqVisualizer.axaml; Noctis.UI Assets/Styles.axaml 1200-1235`, `src/Noctis.Core/Helpers/SearchText.cs (Normalize/Matches)`, `src/Noctis.Core/Models/ArtistGrouping.cs (141-187)`, `src/Noctis.Core/Models/Track.cs (lyrics getters 223-320, ParseArtistTokens)`, `src/Noctis.Core/Services/ILibraryService.cs (20-35)`, `src/Noctis.Core/Services/LibraryService.cs (150-260, LibraryUpdated raise sites, 1577-1607, 2530-2590)`, `src/Noctis.Core/Services/LyricsStore.cs (full)`, `src/Noctis.Core/Services/LyricsTimeline.cs (70-130)`, `src/Noctis.Core/Services/PersistenceService.cs (SaveArtwork)`, `src/Noctis.UI/Assets/Styles.axaml and Icons.axaml (grep)`, `src/Noctis.UI/Controls/ColorPickerFlyout.axaml.cs (60-150)`, `src/Noctis.UI/Controls/GlassPanel.cs (60-727)`, `src/Noctis.UI/Controls/HeartIcon.cs (190-226)`, `src/Noctis.UI/Controls/HeartIcon.cs (full)`, `src/Noctis.UI/Controls/MarqueeTextBlock.cs (200-628)`, `src/Noctis.UI/Controls/SpinClock.cs (full)`, `src/Noctis.UI/Converters/IconKeyToGeometryConverter.cs (full)`, `src/Noctis.UI/Converters/PreBlurredArtworkConverter.cs (full)`, `src/Noctis.UI/Helpers/AutoScrollBehavior.cs (full)`, `src/Noctis.UI/Helpers/BulkObservableCollection.cs (full)`, `src/Noctis.UI/Helpers/MenuOpenAnimation.cs (90-130, 220-260)`, `src/Noctis.UI/Helpers/SmoothScrollBehavior.cs (330-370)`, `src/Noctis.UI/Localization/TExtension.cs (full)`, `src/Noctis/App.axaml.cs (full)`, `src/Noctis/Controls/AnimatedCoverFeed.cs (full)`, `src/Noctis/Controls/AnimatedCoverImage.axaml.cs (full)`, `src/Noctis/Controls/CachedImage.cs (full)`, `src/Noctis/Controls/CachedViewLocator.cs (full)`, `src/Noctis/Controls/EqVisualizer.axaml + .axaml.cs (full)`, `src/Noctis/Controls/EqVisualizer.axaml.cs (full) + EqVisualizer.axaml`, `src/Noctis/Controls/KawarpBackground.cs (full)`, `src/Noctis/Controls/LottieToggle.axaml.cs (25-92)`, `src/Noctis/Controls/MediaArtwork.axaml.cs (full)`, `src/Noctis/Controls/SpectrumVisualizer.cs (full)`, `src/Noctis/Controls/VideoBackdrop.cs (full)`, `src/Noctis/Converters/ArtworkPathConverter.cs (full)`, `src/Noctis/Helpers/AlbumTile.cs (55-110)`, `src/Noctis/Helpers/AlbumTile.cs (full)`, `src/Noctis/Helpers/ComboBoxDropDownAnimator.cs (full)`, `src/Noctis/Helpers/CoverSpinner.cs (full)`, `src/Noctis/Helpers/DragFileBehavior.cs (100-135)`, `src/Noctis/Helpers/FlowingArtworkAnimator.cs (full)`, `src/Noctis/Helpers/LiquidReorder.cs (1-289)`, `src/Noctis/Helpers/LiquidReorder.cs (loop sites)`, `src/Noctis/Helpers/MiniPlayerPin.cs (full)`, `src/Noctis/Helpers/MultiSelectHelper.cs (full)`, `src/Noctis/Helpers/MusicVideoLocator.cs (full)`, `src/Noctis/Helpers/QueueRowSelection.cs (full)`, `src/Noctis/Helpers/StreamingFill.cs (full)`, `src/Noctis/Helpers/TrackContextMenuBuilder.cs (440-514)`, `src/Noctis/Helpers/TransientStatus.cs (full)`, `src/Noctis/Helpers/WindowKeyForwarder.cs (full)`, `src/Noctis/Program.cs (1-260)`, `src/Noctis/Program.cs (global handlers via grep, lines 105-113)`, `src/Noctis/Services/AnimatedCoverService.cs (1-52)`, `src/Noctis/Services/AudioAnalysis/AudioAnalysisCoordinator.cs (150-215)`, `src/Noctis/Services/LyricsStudio/ExistingLyricsLoader.cs (1-90)`, `src/Noctis/Services/SmartPlaylistEvaluator.cs (Evaluate/EvaluateRule via grep)`, `src/Noctis/ViewModels/AddSongsDialogViewModel.cs (full)`, `src/Noctis/ViewModels/AlbumDetailViewModel.cs (150-440, 502-612)`, `src/Noctis/ViewModels/AlbumDetailViewModel.cs (Dispose 1098-1111, subscription greps)`, `src/Noctis/ViewModels/ArtistDetailViewModel.cs (300-600, 600-670, 957-964)`, `src/Noctis/ViewModels/ArtistDetailViewModel.cs (570-730, 950-965)`, `src/Noctis/ViewModels/BulkLyricsViewModel.cs (full)`, `src/Noctis/ViewModels/CommandPaletteViewModel.cs (90-240)`, `src/Noctis/ViewModels/CreateSmartPlaylistDialogViewModel.cs (full)`, `src/Noctis/ViewModels/FavoritesViewModel.cs (100-300)`, `src/Noctis/ViewModels/HomeViewModel.cs (236-262, 630-660)`, `src/Noctis/ViewModels/HomeViewModel.cs (250-580)`, `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs (1-700, 1289-1314)`, `src/Noctis/ViewModels/LibraryArtistsViewModel.cs (100-300)`, `src/Noctis/ViewModels/LibraryArtistsViewModel.cs (160-190)`, `src/Noctis/ViewModels/LibraryFoldersViewModel.cs (full)`, `src/Noctis/ViewModels/LibraryPlaylistsViewModel.cs (290-375)`, `src/Noctis/ViewModels/LibrarySongsViewModel.cs (full)`, `src/Noctis/ViewModels/LrcEditorViewModel.cs (240-302)`, `src/Noctis/ViewModels/LyricShareViewModel.cs (230-320, 520-700)`, `src/Noctis/ViewModels/LyricShareViewModel.cs (280-700, 914-929)`, `src/Noctis/ViewModels/LyricsBackgroundPickerViewModel.cs (full)`, `src/Noctis/ViewModels/LyricsStudioPageViewModel.cs (full)`, `src/Noctis/ViewModels/LyricsStudioPickerViewModel.cs (full)`, `src/Noctis/ViewModels/LyricsStudioViewModel.cs (150-450)`, `src/Noctis/ViewModels/LyricsViewModel.cs (580-740, 1545-1690, 3074-3331)`, `src/Noctis/ViewModels/LyricsViewModel.cs (676-800, 1340-1740, 1990-2250)`, `src/Noctis/ViewModels/MainWindowViewModel.cs (141-160, 290-657, 770-795, 1395-1680, 1940-1975, 2104-2113)`, `src/Noctis/ViewModels/MainWindowViewModel.cs (48-90, 255-1006, 1008-1413, 1535-2760)`, `src/Noctis/ViewModels/MetadataHelper.cs (250-285)`, `src/Noctis/ViewModels/MetadataHelper.cs (full)`, `src/Noctis/ViewModels/MetadataViewModel.cs (330-560, 686-745, 905-935, 1046-1076, 1150-1330, 2256-2712)`, `src/Noctis/ViewModels/MiniPlayerViewModel.cs (full)`, `src/Noctis/ViewModels/PlayerViewModel.cs (60-120, 440-520, 1400-1640, 1690-2110, 2120-2600, 2800-3000, 3040-3190)`, `src/Noctis/ViewModels/PlayerViewModel.cs (timer lifecycle greps)`, `src/Noctis/ViewModels/PlaylistViewModel.cs (120-490)`, `src/Noctis/ViewModels/PlaylistViewModel.cs (200-460, 1015-1030)`, `src/Noctis/ViewModels/SettingsViewModel.Features.cs (468-490)`, `src/Noctis/ViewModels/SettingsViewModel.cs (2040-2060, 6660-6705)`, `src/Noctis/ViewModels/SettingsViewModel.cs (420-495, 1508-1526, 2140-2660, 3830-3910, 3990-4039, 4486-4512, 5290-5405, 5570-5700, 5689-5920, 5960-6120, 6330-6400)`, `src/Noctis/ViewModels/ShortcutsSettingsViewModel.cs (200-235)`, `src/Noctis/ViewModels/SidebarViewModel.cs (780-943 + grep)`, `src/Noctis/ViewModels/StatisticsViewModel.cs (95-438)`, `src/Noctis/ViewModels/TopBarViewModel.cs (debounce section via grep)`, `src/Noctis/ViewModels/WrapViewModel.cs (120-223)`, `src/Noctis/ViewModels/YouTubeDownloadViewModel.cs (120-180)`, `src/Noctis/Views/*.axaml.cs (grep sweeps for File/Directory/Bitmap/Decode/Wait/Invoke/LayoutUpdated/DispatcherTimer/async void/GetVisualDescendants)`, `src/Noctis/Views/AddSongsDialog.axaml (150-270)`, `src/Noctis/Views/AlbumDetailView.axaml (240-360, 700-940) + .axaml.cs (1-140, 380-517)`, `src/Noctis/Views/AlbumDetailView.axaml.cs (30-60, 150-232, 355-516)`, `src/Noctis/Views/ArtistDetailView.axaml (full) + .axaml.cs (1-200)`, `src/Noctis/Views/ArtistDetailView.axaml.cs (subscription greps)`, `src/Noctis/Views/AudioConverterDialog.axaml (88-187)`, `src/Noctis/Views/BulkLyricsDialog.axaml (55-91)`, `src/Noctis/Views/CommandPaletteDialog.axaml (30-70)`, `src/Noctis/Views/CoverFlowView.axaml.cs (120-280)`, `src/Noctis/Views/DuplicateFinderDialog.axaml (105-155)`, `src/Noctis/Views/FavoritesView.axaml (full) + .axaml.cs (85-205)`, `src/Noctis/Views/HomeView.axaml (grep of all list sites) + HomeViewModel caps`, `src/Noctis/Views/LibraryAlbumsView.axaml (full) + .axaml.cs (full)`, `src/Noctis/Views/LibraryArtistsView.axaml (full) + .axaml.cs (100-190)`, `src/Noctis/Views/LibraryArtistsView.axaml.cs (20-130)`, `src/Noctis/Views/LibraryFoldersView.axaml (full)`, `src/Noctis/Views/LibraryPlaylistsView.axaml (full)`, `src/Noctis/Views/LibrarySongsView.axaml (full) + .axaml.cs (full)`, `src/Noctis/Views/LibrarySongsView.axaml.cs (1-250)`, `src/Noctis/Views/LyricShareDialog.axaml.cs (full)`, `src/Noctis/Views/LyricsPanelView.axaml.cs (full)`, `src/Noctis/Views/LyricsStudioPanel.axaml (415-455) + .axaml.cs (1-60)`, `src/Noctis/Views/LyricsStudioPickerDialog.axaml (200-260)`, `src/Noctis/Views/LyricsView.axaml (1030-1080, 1090-1350) + .axaml.cs (90-150)`, `src/Noctis/Views/LyricsView.axaml.cs (full)`, `src/Noctis/Views/MainWindow.axaml (720-1118, 1250-1275) + .axaml.cs (495-555, 1750-1934)`, `src/Noctis/Views/MainWindow.axaml.cs (1-1580, 2280-2393)`, `src/Noctis/Views/MainWindow.axaml.cs (60-275, 380-800, 1380-1400, 2260-2393)`, `src/Noctis/Views/MetadataFinderDialog.axaml (100-146)`, `src/Noctis/Views/MetadataWindow.axaml (2070-2130, spinner sites)`, `src/Noctis/Views/MetadataWindow.axaml.cs (full)`, `src/Noctis/Views/MiniPlayerWindow.axaml (2280-2430, 536-552)`, `src/Noctis/Views/MiniPlayerWindow.axaml.cs (full)`, `src/Noctis/Views/OrganizeFilesDialog.axaml (140-175)`, `src/Noctis/Views/PlaybackBarView.axaml.cs (1-520, 1280-1320)`, `src/Noctis/Views/PlaylistView.axaml (286-385, 640-1339) + .axaml.cs (30-280, 440-826)`, `src/Noctis/Views/PlaylistView.axaml.cs (1-260, lifecycle greps)`, `src/Noctis/Views/QueueView.axaml (1-60, 196-316)`, `src/Noctis/Views/ReplayGainScannerDialog.axaml (40-95)`, `src/Noctis/Views/SendToFolderDialog.axaml (70-138)`, `src/Noctis/Views/ServerView.axaml (20-80, 150-280)`, `src/Noctis/Views/SettingsView.axaml (3820-3935, spinner sites)`, `src/Noctis/Views/SettingsView.axaml.cs (30-100, 255-300 + greps)`, `src/Noctis/Views/SidebarView.axaml (list sites)`, `src/Noctis/Views/SidebarView.axaml.cs (520-560)`, `src/Noctis/Views/StatisticsView.axaml (1-40, 370-414)`

### Settings, dialogs, popups, commands

**settings:** Open points and caveats:
1. Finding 1 (File.Move replace failing while settings.json is open elsewhere) relies on documented Windows/.NET behaviour (dotnet/runtime#114230). I did not reproduce it: no build or run, per the rules. It needs a runtime check, e.g. a stress test that toggles a setting while a background loop calls LoadSettingsAsync.
2. I did not measure the cost of the four per-tick slider saves (finding 4).
3. NoctisServerEnabled has no working control. Its only UI path, TurnOnNoctisServerCommand, sits in a card marked IsEnabled=False (SettingsView.axaml:4403), and Reset settings does not clear it either. git shows the toggle was added (5108b4d) and removed (09065c7) before v1.5.0, so no released build could turn the server on. A hand-edited settings.json with true would start a LAN HTTPS server that cannot be switched off from the UI. I did not report this as a finding.
4. ripgrep reported "binary file matches" for src/Noctis/ViewModels/AlbumDetailViewModel.cs, which hid matching lines from an earlier search. grep -P found no NUL byte, so the cause is unknown. Other auditors using the Grep tool may miss matches in that file.
5. Prior settings items re-checked and now fixed in the current code: AUDIT.md M18 (analysis toggle now calls StartBackfill :4460), M19/M20 (deletes and storage walk moved off-thread / async), M21 (ListenBrainz logout clears the flag :4840), L9 (avatar copy on Task.Run :446), L11 (marquee statics now broadcast, which causes finding 5), L13 (token persisted only when connected :2843). From AUDIT_2026-07-24: 0-playlists fixed (:376), Clear-cache artists folder fixed (:6373), folder normalization fixed (:5706), launch-at-login error fixed (:868), accent 8-digit hex fixed (:3475), Organize persistence fixed (:1819).
6. Do-not-regress items: nothing in this lane touches LibVLC caching, the session-volume debounce, RequestBringIntoView or the lyrics Grid. The Settings view uses BringIntoView only for search Enter and the dev download panel.
7. Not audited: the plugin per-toggle wiring inside PluginHost, MainWindowViewModel's shutdown ordering beyond confirming both saves go through _saveLock, and the Lyrics Studio dialog (it only mirrors prefs back through ApplyLyricsStudioSettings, Features.cs:336).

**dialogs:** Not determined / caveats:
(1) Whether Alt+F4 closes the WindowDecorations="None" dialogs on Windows. ReplayGainScannerDialog.axaml.cs:19-31 says it does. If so, Organize, Converter, YouTube and Lyrics Studio also keep working after such a close, because they do not cancel in OnClosing. Not reported as separate findings.
(2) Removed from the findings after checking: MetadataFinder Apply changes Album/AlbumArtist without recomputing AlbumId (MetadataFinderViewModel.cs:123-130; compare MetadataViewModel.cs:2307) and never calls _library.SaveAsync. The default-on folder watcher re-imports each atomically rewritten file within about 1.5 s and recomputes AlbumId, so this only matters with watching off or for files outside the watched roots.
(3) LibraryPlaylistsViewModel.SetCoverArt/RemoveCoverArt are not bound in any .axaml (dead). If they are ever wired up, they would also need to update the PlaylistNavItem.CoverArtPath that the tiles bind to.
(4) The DuplicateFinder delete confirmation uses ConfirmationDialog.ShowAsync(string), which is owned by MainWindow while DuplicateFinderDialog is itself modal. The ShowAsync(Window, …) overload's comment says nested modals need their own owner. I could not confirm a visible z-order or enable-state problem without running the app.
(5) Esc does nothing in ConfirmationDialog, RemoveFromLibraryDialog, Create/Edit/AddTo/AddSongs/CreateSmart playlist dialogs, MetadataWindow, Album/Playlist description, WrapDialog and LyricShareDialog. This is usability, not reported.
(6) MetadataViewModel was read only in the Save/Cancel/artwork/animated/rename sections, not the full 2892 lines. MiniPlayerWindow popups were only skimmed (they look instrumented and paired).
(7) The prior AUDIT.md H3 (LRC editor Save running synchronously on the UI thread) is fixed: Task.Run at LrcEditorViewModel.cs:265. The prior 07-24 organizer/watcher race is fixed by SuppressPaths (FileOrganizerService.cs:125). The new organizer finding is a different defect: the in-memory sidebar playlists are never remapped.
(8) Library behaviour is cited from Avalonia master (Button.cs, FocusManager.cs) and CommunityToolkit AsyncRelayCommand.cs. AUDIT.md notes that Avalonia 12 moved focus changes to pointer release (#21009); the fetched FocusManager source handles both press and release, so a clicked Button still takes focus. The LRC-editor Space finding still needs one runtime check.
(9) None of the do-not-regress areas (caching, volume path and debounce, RequestBringIntoView, lyrics Grid wrapping) are touched by any proposed fix.

**commands:** 1) Ruled out after checking: the Lyrics Studio sidebar page's tap mode does get Space. I first suspected the global Play/Pause tunnel handler would take it. Avalonia 12.1.2 runs same-element tunnel handlers in reverse registration order (EventRoute.RaiseEventImpl), so LyricsStudioPanel's later-registered handler runs first. The same mechanism causes the queue Ctrl+A finding, which is marked 'likely' with needs_runtime_check because it was not exercised live (live testing was not allowed).
2) Unhandled exceptions from async void handlers and from AsyncRelayCommand on the UI thread do not crash: App.axaml.cs:73-79 marks Dispatcher faults as handled and logs them. They do fail silently for the user.
3) Many catch blocks in scope log only through System.Diagnostics.Debug.WriteLine, which is [Conditional("DEBUG")] and compiled out of Release, so they leave no trace in shipped builds (listed under logging_gaps).
4) Possible risk, not confirmed: HomeView.axaml.cs:282-301, FavoritesView.axaml.cs:179-198 and LibraryArtistsView.axaml.cs:168-189 set the page or list Opacity to 0 and restore it only in a LayoutUpdated callback. The callback waits until Extent >= saved offset or 10 passes. If the content shrank below the saved offset (e.g., songs unfavorited from an album page before going Back) and fewer than 10 layout passes follow (idle, nothing playing), the list could stay invisible until something triggers a layout pass. I could not determine LayoutUpdated frequency without running the app.
5) No CanExecute problems found: only two commands use CanExecute (PlayerViewModel.ViewCurrentTrackAlbum, ThemeEditorViewModel.Save), and both are re-evaluated. Shared AsyncRelayCommand instances (e.g., ServerViewModel.PlayServerAlbum, AlbumDetail AddAlbumToQueue with its 1.5 s delay) disable every bound button while one execution runs. This is cosmetic, so it is not reported.
6) Prior-audit leads re-checked and now fixed: AUDIT.md queue page non-virtualized (QueueView is now a Grid), the AUDIT_2026-07-24 tray-opens-both-windows issue (ShowFromTray now closes the mini player), and the Repeat-All Next no-op (PlayerViewModel.cs:515-517 now wraps).
7) Out of lane but noticed: PlayerViewModel.RemoveCurrentTrackFromLibrary (PlayerViewModel.cs:1119-1133) trashes the file before RemoveTrackAsync. It relies on the ~1.75 s retry ladder after AdvanceQueue, and a crossfading outgoing track could still hold the handle longer. Not verified; this belongs to the playback lane.

_Files read (131):_ `AUDIT.md / AUDIT_2026-07-24.md (grep for overlaps: H3 LRC save now fixed; organizer watcher suppression fixed)`, `AUDIT.md and AUDIT_2026-07-24.md (settings items, used as leads; each re-verified against current code)`, `Avalonia 12.1.2 source: EventRoute.cs, Interactive.cs, ContextMenu.cs, ContentPresenter.cs, DataTemplate.cs, TextBox.cs, InputElement.cs`, `Command/IsEnabled bindings of the tool dialog .axaml files (Organize, Duplicate, MetadataFinder, AudioConverter, SendTo, BulkLyrics, YouTube, ReplayGain, PlaylistImport)`, `Repo-wide greps: [RelayCommand] (622), async void (65), CanExecute/NotifyCanExecuteChanged, ReflectionBinding/CompileBindings (none), $parent/ElementName command bindings, KeyDown handlers`, `src/Noctis.Core/Helpers/SearchText.cs (19-57)`, `src/Noctis.Core/Models/AppSettings.cs (full)`, `src/Noctis.Core/Models/Track.cs (1-125)`, `src/Noctis.Core/Services/IPersistenceService.cs (full)`, `src/Noctis.Core/Services/LibraryService.cs (255-444, 848-917, 960-999, 1120-1189, 1351-1394, 1779-1786, 2863-2873)`, `src/Noctis.Core/Services/LibraryService.cs:128-143, 1045-1064, 1395-1464, 2050-2139 (settings read-modify-write paths)`, `src/Noctis.Core/Services/LibraryWatcherService.cs (150-290, 438-474)`, `src/Noctis.Core/Services/MetadataService.cs (530-669)`, `src/Noctis.Core/Services/PersistenceService.cs (full)`, `src/Noctis.Core/Services/Sync/LibrarySyncService.cs:60-110`, `src/Noctis.Core/Services/WatchDebouncer.cs (40-79)`, `src/Noctis.UI/Controls/ColorPickerFlyout.axaml.cs (full)`, `src/Noctis.UI/Controls/MarqueeTextBlock.cs:34-37, 357-368, 426-463`, `src/Noctis/App.axaml (60-125)`, `src/Noctis/App.axaml.cs (25-102)`, `src/Noctis/App.axaml.cs (55-102)`, `src/Noctis/Controls/CachedViewLocator.cs (full)`, `src/Noctis/Helpers/AlbumTile.cs (1-80)`, `src/Noctis/Helpers/BadgeNamePrompt.cs (full)`, `src/Noctis/Helpers/DialogHelper.cs (full)`, `src/Noctis/Helpers/LibraryRemovalHelper.cs (full)`, `src/Noctis/Helpers/MultiSelectHelper.cs (50-85, 207-346)`, `src/Noctis/Helpers/TrackContextMenuBuilder.cs (1-470)`, `src/Noctis/Helpers/TrackContextMenuBuilder.cs (full)`, `src/Noctis/Helpers/WindowKeyForwarder.cs (full)`, `src/Noctis/Models/Shortcuts.cs (full)`, `src/Noctis/Noctis.csproj (compiled-bindings settings)`, `src/Noctis/Services/AutoMatchCoordinator.cs (full)`, `src/Noctis/Services/FileOrganizerService.cs (full)`, `src/Noctis/Services/LyricsStudio/ExistingLyricsLoader.cs (25-84)`, `src/Noctis/Services/LyricsStudio/WhisperModelManager.cs (55-104)`, `src/Noctis/Services/MetadataFinderService.cs:1-80`, `src/Noctis/Services/Plugins/PluginHost.cs (344-374)`, `src/Noctis/Services/ShareClipRenderer.cs (70-151)`, `src/Noctis/Services/ShortcutService.cs (35-140)`, `src/Noctis/Services/ShortcutService.cs:60-148`, `src/Noctis/Services/SmartPlaylistEvaluator.cs (1-160)`, `src/Noctis/Services/VlcAudioPlayer.cs:1386-1443, 1563-1632, 1964-2020, 2257-2294 (setters called by ApplyAudioSettings)`, `src/Noctis/Services/YouTube/YtDlpTool.cs:70-179`, `src/Noctis/ViewModels/AlbumDetailViewModel.cs (620-960)`, `src/Noctis/ViewModels/AlbumDetailViewModel.cs (698-782)`, `src/Noctis/ViewModels/AlbumDetailViewModel.cs (grep for tint/settings consumers)`, `src/Noctis/ViewModels/CommandPaletteViewModel.cs (full)`, `src/Noctis/ViewModels/CoverFlowViewModel.cs (commands)`, `src/Noctis/ViewModels/CreatePlaylistDialogViewModel.cs, EditPlaylistDialogViewModel.cs, AddToPlaylistDialogViewModel.cs, CreateSmartPlaylistDialogViewModel.cs, AddSongsDialogViewModel.cs, LrcEditorViewModel.cs, LyricsStudioViewModel.cs, LyricsStudioPageViewModel.cs, LyricsStudioPickerViewModel.cs, LyricsBackgroundPickerViewModel.cs, ThemeEditorViewModel.cs, CommandPaletteViewModel.cs, OrganizeFilesViewModel.cs, DuplicateFinderViewModel.cs, PlaylistImportViewModel.cs (all full)`, `src/Noctis/ViewModels/FavoritesViewModel.cs (460-600)`, `src/Noctis/ViewModels/HomeViewModel.cs (143-170, 331-341, 569-877, 950-1045)`, `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs (1040-1240)`, `src/Noctis/ViewModels/LibraryFoldersViewModel.cs (commands 61-487)`, `src/Noctis/ViewModels/LibraryPlaylistsViewModel.cs (36-377)`, `src/Noctis/ViewModels/LibraryPlaylistsViewModel.cs (command list)`, `src/Noctis/ViewModels/LibrarySongsViewModel.cs (290-470)`, `src/Noctis/ViewModels/LyricsStudioPageViewModel.cs (header)`, `src/Noctis/ViewModels/LyricsStudioViewModel.cs (905-985)`, `src/Noctis/ViewModels/LyricsViewModel.cs (1000-1130, 1340-1630, command list)`, `src/Noctis/ViewModels/LyricsViewModel.cs:865-964, 1100-1129 (settings writers/readers)`, `src/Noctis/ViewModels/MainWindowViewModel.cs (1-680, 1373-2930)`, `src/Noctis/ViewModels/MainWindowViewModel.cs (1340-1371, 2090-2129, 2320-2360)`, `src/Noctis/ViewModels/MetadataFinderViewModel.cs (40-170), YouTubeDownloadViewModel.cs (95-210), AudioConverterViewModel.cs (75-175), SendToFolderViewModel.cs (55-135), BulkLyricsViewModel.cs (45-115), ReplayGainScannerViewModel.cs (60-212), LyricShareViewModel.cs (520-929)`, `src/Noctis/ViewModels/MetadataHelper.cs (full)`, `src/Noctis/ViewModels/MetadataHelper.cs:95-109`, `src/Noctis/ViewModels/MetadataViewModel.cs (380-620, 1044-1083, 1400-1650, 2005-2735)`, `src/Noctis/ViewModels/PlayerViewModel.cs (420-1135, 1685-1700, 3165-3190)`, `src/Noctis/ViewModels/PlaylistViewModel.cs (75-94, 254-260, 330-390, 675-760, 1000-1030)`, `src/Noctis/ViewModels/PlaylistViewModel.cs (full)`, `src/Noctis/ViewModels/QueueViewModel.cs (full)`, `src/Noctis/ViewModels/ServerViewModel.cs (commands) + Views/ServerView.axaml.cs`, `src/Noctis/ViewModels/SettingsViewModel.ContentPacks.cs (full)`, `src/Noctis/ViewModels/SettingsViewModel.Features.cs (full)`, `src/Noctis/ViewModels/SettingsViewModel.cs (140-310, 3123-3170, 3300-3420, 5140-5280, 5925-6015, 6655-6715, 6760-6970)`, `src/Noctis/ViewModels/SettingsViewModel.cs (all 7015 lines, in chunks)`, `src/Noctis/ViewModels/SidebarViewModel.cs (1-943, full)`, `src/Noctis/ViewModels/SidebarViewModel.cs (150-630)`, `src/Noctis/ViewModels/TopBarViewModel.cs (1-663)`, `src/Noctis/Views/AddSongsDialog.axaml.cs (full)`, `src/Noctis/Views/AddToPlaylistDialog.axaml (170-230)`, `src/Noctis/Views/AlbumDescriptionDialog.axaml.cs (full)`, `src/Noctis/Views/AlbumDetailView.axaml (300-375)`, `src/Noctis/Views/AlbumDetailView.axaml.cs (18-135, 285-325, 403-418)`, `src/Noctis/Views/ArtistDetailView.axaml.cs (full)`, `src/Noctis/Views/BadgeNameDialog.axaml.cs (full)`, `src/Noctis/Views/CommandPaletteDialog.axaml.cs (full)`, `src/Noctis/Views/ConfirmationDialog.axaml.cs (full)`, `src/Noctis/Views/CoverFlowView.axaml.cs (full)`, `src/Noctis/Views/CreatePlaylistDialog.axaml.cs (full)`, `src/Noctis/Views/DuplicateFinderDialog.axaml.cs, MetadataFinderDialog.axaml.cs, OrganizeFilesDialog.axaml.cs, ReplayGainScannerDialog.axaml.cs, SendToFolderDialog.axaml.cs, AudioConverterDialog.axaml.cs, BulkLyricsDialog.axaml.cs, YouTubeDownloadDialog.axaml.cs, SpectrogramWindow.axaml.cs, PlaylistImportDialog.axaml.cs (all full)`, `src/Noctis/Views/EditPlaylistDialog.axaml.cs (full)`, `src/Noctis/Views/FavoritesView.axaml.cs (full)`, `src/Noctis/Views/HomeView.axaml.cs (full)`, `src/Noctis/Views/LibraryAlbumsView.axaml.cs (30-180)`, `src/Noctis/Views/LibraryArtistsView.axaml.cs (full)`, `src/Noctis/Views/LibraryPlaylistsView.axaml (command bindings)`, `src/Noctis/Views/LibrarySongsView.axaml.cs (38-325)`, `src/Noctis/Views/LrcEditorDialog.axaml.cs (full) + LrcEditorDialog.axaml (buttons)`, `src/Noctis/Views/LyricShareDialog.axaml.cs (full)`, `src/Noctis/Views/LyricsBackgroundPickerDialog.axaml.cs (full)`, `src/Noctis/Views/LyricsStudioDialog.axaml.cs (full) + .axaml header`, `src/Noctis/Views/LyricsStudioPanel.axaml (525-540, bindings survey)`, `src/Noctis/Views/LyricsStudioPanel.axaml.cs (full)`, `src/Noctis/Views/LyricsStudioPickerDialog.axaml.cs (full)`, `src/Noctis/Views/LyricsStudioView.axaml.cs (full)`, `src/Noctis/Views/LyricsView.axaml (1130-1200)`, `src/Noctis/Views/MainWindow.axaml (command bindings survey; 685-700)`, `src/Noctis/Views/MainWindow.axaml.cs (1-2041, 2383-2393)`, `src/Noctis/Views/MainWindow.axaml.cs (215-254)`, `src/Noctis/Views/MainWindow.axaml.cs:440-469, 1060-1103`, `src/Noctis/Views/MetadataWindow.axaml (buttons, tab visibility, footer)`, `src/Noctis/Views/MetadataWindow.axaml.cs (full)`, `src/Noctis/Views/MiniPlayerWindow.axaml.cs (140-339, 1174-1210)`, `src/Noctis/Views/PlaylistDescriptionDialog.axaml.cs (full)`, `src/Noctis/Views/PlaylistView.axaml.cs (73-826)`, `src/Noctis/Views/QueueView.axaml (200-316) + .cs`, `src/Noctis/Views/RemoveFromLibraryDialog.axaml.cs (50-95)`, `src/Noctis/Views/RemoveFromLibraryDialog.axaml.cs (full)`, `src/Noctis/Views/SettingsView.axaml (1290-1305)`, `src/Noctis/Views/SettingsView.axaml (lines 1190-5729 fully; 1-1189 are styles, checked via grep only)`, `src/Noctis/Views/SettingsView.axaml.cs (1-210)`, `src/Noctis/Views/SettingsView.axaml.cs (full)`, `src/Noctis/Views/SidebarView.axaml (295-320)`, `src/Noctis/Views/SidebarView.axaml.cs (148-195) + SidebarView.axaml context menu bindings`, `src/Noctis/Views/SidebarView.axaml.cs (full)`, `src/Noctis/Views/SongsViewOptionsDialog.axaml.cs (full)`, `src/Noctis/Views/StatisticsView.axaml (command bindings), ViewModels/StatisticsViewModel.cs (back wiring)`, `src/Noctis/Views/TextPromptDialog.axaml.cs (full)`, `src/Noctis/Views/ThemeEditorDialog.axaml.cs (full)`, `src/Noctis/Views/WrapDialog.axaml.cs (full)`

### Security

**sec-files:** Checked and still intact from earlier audits:
- **Plugin zip extraction:** safe against zip-slip. PluginInstaller.IsSafeEntryName rejects rooted, drive, colon, '..', '.' and control-character names. Extract re-checks each entry with GetFullPath and a prefix test into a fresh staging folder. .NET bounds decompression to the declared size.
- **Content packs:** limited to data file types, reject reparse points, and the plugin id regex makes the folder name safe.
- **Tag-derived paths (AUDIT_2026-07-24 §8 CRITICAL):**
  - TitleFormatter now maps '/' and strips leading dots and dashes.
  - AudioConverterService.ComputeOutputPath checks that the output stays inside the output folder.
  - FileOrganizePlanner sanitizes each segment (including '..' and reserved names).
  - FileOrganizerService moves sidecars, uses the watcher suppression window, protects music roots and keeps partial undo logs.
  - The metadata sidecar delete is now gated and goes to the trash.

Other checks:
- **SQL:** all queries are parameterized; the two interpolated statements use constant table and column names.
- **Serialization:** no Newtonsoft TypeNameHandling and no STJ polymorphism. YamlDotNet 15.3.0 deserializes into typed DTOs with no tag mappings.
- **Process.Start:** every call builds arguments with ArgumentList (or quotes that cannot be broken on Windows). The yt-dlp URL is passed positionally without '--', but inputs are validated by LooksLikeYouTubeUrl or built by WatchUrl.
- **m3u import:** paths are only matched against the library; no file system access, so no SMB/NTLM leak.

Not reported as findings:
- **TTML DTD:** TtmlParser.cs:55 says 'DTD stays prohibited (XDocument.Parse default)', which is wrong. dotnet/runtime XNode.GetXmlReaderSettings sets DtdProcessing.Parse with MaxCharactersFromEntities=1e7 (https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/System.Private.Xml.Linq/src/System/Xml/Linq/XNode.cs). External entities are not resolved (no XmlResolver), and sidecars have no size cap anyway, so the only effect is up to 10M characters of entity expansion from a tiny file. Still, fix the comment, or pass XmlReaderSettings { DtdProcessing = Prohibit } if this is ever reused for network XML.
- **Content packs can relabel the UI:** a pack declaring culture "en" overlays UI strings for every English user without approval, so a pack could relabel dangerous buttons (for example swap 'Keep Files' and 'Move to Recycle Bin'). This is a design risk, not reported.
- **ReDoS:** regexes run on tag values (AlbumTitle, AlbumTitleNormalizer, ExtractFeaturedNamesFromTitle) have polynomial, not exponential, backtracking and would need tags over 100k characters. They have no timeout or NonBacktracking option. Not verified.
- **Large images:** SkiaArtworkDecoder decodes PNG and WebP at native size (lines 45 and 53-55), so embedded art with huge declared dimensions could spike memory. Not verified.

Could not determine:
- Whether LRCLIB validates or limits published lyricsfile content. lrclib.net/docs is rendered with JS; a search summary says the field is stored as-is and present on every record. The local sidecar path of finding 1 is verified from code regardless.
- The Linux FreedesktopTrash fallback cannot move directories (File.Move on a folder fails). This is harmless because callers fall back to per-file trashing.

Lane crossing: the NoctisServer Content-Disposition finding belongs to the server lane; it is included because it was found while tracing path and file-name usage.

**sec-net:** Checked and not filed:
- LAN Web Remote (0.0.0.0): gated by private source address plus a per-session 64-bit token (constant-time compare). Reads are bounded (5 s CTS, 64 header lines, 4 KB lines, 16 slots), there are no file routes, and the page uses textContent (no XSS).
- Local API (127.0.0.1 only): gated by loopback peer, loopback Host header (DNS rebinding), 256-bit token hashed then compared in constant time, 16 KB body cap, Content-Length only, Guid-only artwork ids, image sniffing.
- Local API auth lockout is global (10 failures lock everyone for 30 s). Only a browser page could abuse it, and current Chrome (142+) and Firefox (153+) gate public→localhost behind the Local Network Access prompt (https://support.mozilla.org/en-US/kb/control-personal-device-local-network-permissions-firefox). Local processes can already read local-api.json. Not filed.
- UpdateService: GitHub host pinning, SHA256SUMS fail-closed on both install paths, random temp name, and downgrade only through the explicit version manager. All intact.
- MediaServer clients: plain HTTP limited to private hosts, Jellyfin password never persisted, artwork fetched with header auth, all bodies bounded. No ServerCertificateCustomValidationCallback override exists anywhere in the repo.
- PlatformHelper.OpenUrl allows only http/https.
- iTunes HLS parts and TIDAL/Deezer next-page links are host-allowlisted. Plugin zip install has a zip-slip guard. Prior AUDIT.md L15 (crash.log unredacted) is fixed (Program.cs:423).

Lower-priority observations, not filed:
1. Hostile or compromised Loon relay:
   - It picks BaseUrl, and WarmRelayCacheAsync (LoonClient.cs:214-259) then GETs a relay-chosen URL with the shared HttpClient, which gives limited LAN SSRF.
   - ResolveArtworkPath serves any file in the artwork folder, not only URLs this client minted. Album cover names are MD5("artist::album") (Track.cs:536-543), so a relay could probe whether the user owns a given album.
   - Fix: keep a per-connection set of minted paths.
2. NoctisServer:
   - adminRole is reported but never enforced, so any account can delete or replace playlists and push sync state. It is unclear whether that is intended.
   - getCoverArt ignores `size` and streams full-size originals, which the Loon comments say can be 25–30 MB.
   - ServerUserStore.Verify skips PBKDF2 for unknown users, a timing oracle (throttled).
   - ClientAuthenticated fires a UI Post plus Task.Delay(6500) on every request (SettingsViewModel.cs:1089).
3. TidalAuth.WaitForCodeAsync: accepts `?error=` without checking state (TidalAuth.cs:291-295), so any page able to reach 127.0.0.1:47474 can cancel an in-progress sign-in.
4. MetadataLookupApi.Escape escapes quotes but not backslashes (MusicBrainz query breaks on a tag ending in "\\").
5. DownloadInstallerAsync's catch deletes destinationPath even when the failure happened before anything was written. In the version manager this can remove a user's existing same-named installer in Downloads.

Not determined (no runtime allowed):
- Actual Windows HTTPS handshake failure. The evidence is the .NET 10 source and Microsoft's documentation.
- The exact VLC 3.0.23 lines present in the 40-line ring when an error fires. The format strings were verified in VLC 3.0.x source.
- Whether the Android client pins the fingerprint. No pinning code was found in Noctis.Android/Mobile; that is outside this scope.
- The production Loon relay itself.

The Discord presence char-length Truncate matches DiscordRPC's check (master ValidateString uses useBytes=false for Details/State), so no bug there.

**sec-data:** Dependency CVEs: `dotnet list <proj> package --vulnerable --include-transitive` ran successfully on all 5 requested projects plus src/Noctis.Server and plugins/Noctis.Plugins.Kawarp. Every project reported "has no vulnerable packages given the current sources" (source: api.nuget.org), so there are no dependency findings. Native pieces outside NuGet advisories: VideoLAN.LibVLC.Windows 3.0.23.1 is newer than the latest VideoLAN bulletin (VideoLAN-SB-VLC-322, fixed in 3.0.22; https://www.videolan.org/security/). SQLitePCLRaw.bundle_e_sqlite3 2.1.12 was published 2026-07 (https://libraries.io/nuget/SQLitePCLRaw.bundle_e_sqlite3), but I could not determine which SQLite version it bundles. Only fixed parameterized queries are used, so SQL-level SQLite CVEs are not reachable anyway.

Secrets in source (location only): src/Noctis/Services/LastFmService.cs:15-16 still holds the Last.fm API key and shared secret (<redacted>). This is prior AUDIT.md H6 / AUDIT_2026-07-24 §4 Critical. HANDOFF_AUDIT_FIXES.md:141-150 records it as a deliberate 2026-07-24 decision with 'Do not re-raise this as a standalone fix', so it is not reported as a finding. TidalAuth.cs:33 is a public PKCE client id (not a secret). DiscordPresenceService.cs:16 is a public application id. .github/workflows/dotnet.yml:496 has a throwaway CI password for an ephemeral container (<redacted>). crowdin.yml has no token. No committed key material was found (.pfx/.p12/.snk/.jks/.pem/.env).

Credential storage per OS (verified in current code): Windows uses DPAPI CurrentUser ('enc:dpapi:' prefix) for lastFmSessionKey, listenBrainzToken, sourceConnections[].tokenOrPassword (PersistenceService.cs:171-182) and the TIDAL refresh token (TidalAuth.cs:404). Protect fails open (plaintext), unprotect fails closed (empty string). macOS/Linux store plaintext (prior L18, still present, by design), mitigated by: data root chmod 0700 (PersistenceService.cs:82), every JSON file 0600 before rename (:599), settings.json.bak 0600 (:160). local-api.json token is plaintext by design (chmod 600 on Unix, never in settings.json). Server accounts are PBKDF2-SHA256 100k with per-user salt; API keys are stored only as SHA-256. server.pfx has a random password in server.pfx.key (owner-only on Unix). Loon HMAC secret is memory-only. Media-server stream URLs carrying credentials are not persisted: the queue stores ids only, and ExternalTrackPaths only covers IsExternal local files (PlayerViewModel.cs:1436). Plugin-declared 'string' settings (PluginSettingValues) are stored plaintext in settings.json on every OS. There is no secret setting type, so a plugin API key would sit unprotected. This is a design gap, not reported.

Log redaction: DebugLog.Write scrubs before the ring and the session.log/crashlog sink. crash.log is scrubbed (Program.cs:426) and the VLC diag file is scrubbed (VlcAudioPlayer.cs:5228). I found no new concrete path beyond the already-reported scheme-less Subsonic VLC lines. The AutoMix/Crossfade 'path={Path.GetFileName(filePath)}' lines (VlcAudioPlayer.cs:3016/3221/3398) would strip the scheme from a Subsonic URL, but remote streams are excluded before those calls (isPathless gates at :2661, :2698, :2767-2774), so they are unreachable. The BareSecretRegex '\b' before 'token' misses 'refresh_token=', but no log site emits that pair today. MediaServerUrl.TryNormalizeBase uses GetLeftPart(Authority), which keeps any user:pass@ userinfo and LogRedaction never strips userinfo. It is only reachable if a connection with userinfo succeeds, which HttpClient does not authenticate with, so not reported.

Plugin trust: this is full-trust in-process .NET, honestly disclosed (Plugins.Warning/SafetyNote). Collectible ALC, loaded from bytes. Zip-slip and link checks look correct. Restricted mode loads no code: only JSON manifests and content packs are read. Plugins receive TrackInfo/NowPlayingTrack.FilePath, which for Subsonic/Jellyfin streams contains credentials. That is not an escalation, since full-trust code can read settings.json and call DPAPI as the same user. Single-instance pipe uses CurrentUserOnly/ACL (SingleInstanceGuard.cs:131-248).

Minor observations, not reported: (1) the TIDAL loopback handler accepts '?error=' without checking state (TidalAuth.cs:291-295), so any web page can abort an in-progress TIDAL sign-in, which is login denial of service only. (2) YAML anchors/aliases let a small Lyricsfile reference one large words list from many lines (quadratic amplification). This is covered by the fix for the already-reported LyricsfileParser line/word-cap finding. (3) Noctis.Server 'user' subcommand creates <data>/server/users.db without the 0700 data-root chmod when run before any 'serve'. It holds hashes only.

Not determined: runtime confirmation of both findings (no app launch per rules). Finding 1 is a static trace through MigrateSettings → Activate → Start.

_Files read (127):_ `AUDIT_2026-07-24.md §8 and lyric-virtualization item; HANDOFF_AUDIT_FIXES.md:55-74`, `Dockerfile (full)`, `External sources checked: dotnet/runtime SslStreamPal.Windows.cs (release/10.0), MS SslStream troubleshooting doc, dotnet/runtime issues 23749/103101/114640, PR 132832; VLC 3.0.x src/input/demux.c, modules/access/http/h1conn.c + connmgr.c; yt-dlp README + release 2026.08.19 asset list; Lachee discord-rpc-csharp RichPresence.cs/StringTools.cs; GitHub releases API for heartached/Noctis asset sizes`, `Repo-wide greps: hardcoded key/secret/token literals, ProtectedData usage, DebugLog/DebugLogger sites with URLs/paths/bodies, Process.Start argument lists, NamedPipe options, IsExternal, TokenOrPassword, FilePath logging`, `SQL CommandText sites in SqliteLibraryIndexService.cs, SyncStore.cs, ServerUserStore.cs, AudioAnalysisStore.cs (grep)`, `crowdin.yml, .github/workflows/dotnet.yml:485-505 (secret scan)`, `docs/LOCAL-API.md (security section 355-385 + grep)`, `docs/PLUGINS.md:512-541`, `docs/SELF-HOSTING.md (grep)`, `dotnet list package --vulnerable --include-transitive: Noctis, Noctis.Core, Noctis.UI, Noctis.Core.Server, Noctis.Plugins.Abstractions, Noctis.Server, plugins/Noctis.Plugins.Kawarp`, `grep-level (HttpSafety usage, URL escaping, Process.Start): ArtistImageService.cs, ArtistInfoService.cs, SimilarArtistsService.cs, LastFmService.cs, ListenBrainzService.cs, LrcLibService.cs, NetEaseService.cs, DeezerMetadataService.cs, DeezerApi.cs, MetadataLookupApi.cs, MetadataFinderService.cs, RadioService.cs`, `plugins/Noctis.Plugins.Kawarp/KawarpPlugin.cs (file IO lines)`, `src/Noctis.Core.Server/Services/Server/LibraryServerAdapter.cs (full)`, `src/Noctis.Core.Server/Services/Server/LoginThrottle.cs (full)`, `src/Noctis.Core.Server/Services/Server/NoctisServer.cs (55-245)`, `src/Noctis.Core.Server/Services/Server/NoctisServer.cs (full)`, `src/Noctis.Core.Server/Services/Server/NoctisServer.cs:1-210`, `src/Noctis.Core.Server/Services/Server/ServerCertificate.cs (full)`, `src/Noctis.Core.Server/Services/Server/ServerUserStore.cs (full)`, `src/Noctis.Core/Helpers/AppPaths.cs (full)`, `src/Noctis.Core/Helpers/TitleFormatter.cs (full)`, `src/Noctis.Core/Models/AppSettings.cs (ports, LoonServerUrl)`, `src/Noctis.Core/Models/AppSettings.cs:39-54, 220-260, 575-620`, `src/Noctis.Core/Models/QueueState.cs:38-67`, `src/Noctis.Core/Models/SourceConnection.cs (full)`, `src/Noctis.Core/Models/Track.cs (IsExternal, ComputeAlbumId)`, `src/Noctis.Core/Services/DebugLog.cs (28-77)`, `src/Noctis.Core/Services/DebugLog.cs (full)`, `src/Noctis.Core/Services/DebugLogger.cs (20-84)`, `src/Noctis.Core/Services/DebugLogger.cs (full)`, `src/Noctis.Core/Services/EnhancedLrcParser.cs (full)`, `src/Noctis.Core/Services/HttpSafety.cs (full)`, `src/Noctis.Core/Services/LogRedaction.cs (full)`, `src/Noctis.Core/Services/MetadataService.cs (95-355, 1165-1200)`, `src/Noctis.Core/Services/PersistenceService.cs (294-302, 566-606)`, `src/Noctis.Core/Services/PersistenceService.cs (full)`, `src/Noctis.Core/Services/PersistenceService.cs / IPersistenceService.cs (artwork path helpers)`, `src/Noctis.Core/Services/TtmlParser.cs (full)`, `src/Noctis.Plugins.Abstractions/NoctisPlugin.cs (full)`, `src/Noctis.Plugins.Abstractions/PluginApi.cs (full)`, `src/Noctis.Server/Program.cs (full)`, `src/Noctis.UI/Localization/Loc.cs (full)`, `src/Noctis.UI/Localization/Strings.resx (Plugins.* keys)`, `src/Noctis/App.axaml.cs (60-99)`, `src/Noctis/Helpers/AlbumTitle.cs (full)`, `src/Noctis/Helpers/LibraryRemovalHelper.cs (full)`, `src/Noctis/Helpers/LyricsBackgroundOverrides.cs (full)`, `src/Noctis/Helpers/MediaExportHelper.cs (full)`, `src/Noctis/Helpers/PlatformHelper.cs (1-290)`, `src/Noctis/Helpers/PlatformHelper.cs (150-220, OpenUrl)`, `src/Noctis/Helpers/PngExportHelper.cs (full)`, `src/Noctis/Helpers/RecycleBin.cs (full)`, `src/Noctis/Helpers/SkiaArtworkDecoder.cs (full)`, `src/Noctis/Program.cs (240-370, HttpClient/DI registration)`, `src/Noctis/Program.cs:240-320, 380-437`, `src/Noctis/Services/AlbumTitleNormalizer.cs (full)`, `src/Noctis/Services/ArtistImageService.cs (path helpers)`, `src/Noctis/Services/AudioAnalysis/SideDecodeMeterFeed.cs (full)`, `src/Noctis/Services/AudioConverterService.cs (230-480)`, `src/Noctis/Services/AvaloniaLogBridge.cs (full)`, `src/Noctis/Services/CrashJournal.cs (full)`, `src/Noctis/Services/DiscordPresenceService.cs (full)`, `src/Noctis/Services/DuplicateFinderService.cs (full)`, `src/Noctis/Services/FileOrganizePlanner.cs (full)`, `src/Noctis/Services/FileOrganizerService.cs (full)`, `src/Noctis/Services/ITunesArtworkService.cs (1-300 + grep for host allowlist)`, `src/Noctis/Services/LastFmService.cs:1-260 + grep of URL/log sites`, `src/Noctis/Services/ListenBrainzService.cs:40-130`, `src/Noctis/Services/LocalApi/LocalApiDto.cs (grep for paths)`, `src/Noctis/Services/LocalApi/LocalApiEventHub.cs (full)`, `src/Noctis/Services/LocalApi/LocalApiTokenStore.cs (full)`, `src/Noctis/Services/Loon/LoonClient.cs (full)`, `src/Noctis/Services/Loon/LoonMessages.cs (full)`, `src/Noctis/Services/Loon/LoonProtobuf.cs (full)`, `src/Noctis/Services/Lyrics/LyricsWriter.cs (full)`, `src/Noctis/Services/LyricsStudio/ExistingLyricsLoader.cs (full)`, `src/Noctis/Services/LyricsStudio/WhisperModelManager.cs (full)`, `src/Noctis/Services/LyricsfileParser.cs (full)`, `src/Noctis/Services/MediaServer/JellyfinClient.cs (full)`, `src/Noctis/Services/MediaServer/MediaServerService.cs (55-228)`, `src/Noctis/Services/MediaServer/MediaServerUrl.cs (full)`, `src/Noctis/Services/MediaServer/MediaServerUrl.cs:26-108`, `src/Noctis/Services/MediaServer/SubsonicClient.cs (full)`, `src/Noctis/Services/MediaServer/SubsonicClient.cs:200-459`, `src/Noctis/Services/NavidromeMediaSourceConnector.cs (full)`, `src/Noctis/Services/NavidromeSyncService.cs (full)`, `src/Noctis/Services/PlaylistImportParser.cs (full)`, `src/Noctis/Services/PlaylistImportService.cs (full)`, `src/Noctis/Services/PlaylistInteropService.cs (full)`, `src/Noctis/Services/Plugins/ContentPack.cs (full)`, `src/Noctis/Services/Plugins/PluginHost.cs (full)`, `src/Noctis/Services/Plugins/PluginHost.cs (full, 1-1460)`, `src/Noctis/Services/Plugins/PluginInstaller.cs (full)`, `src/Noctis/Services/Plugins/PluginInstaller.cs (grep: zip-slip guard)`, `src/Noctis/Services/Plugins/PluginManifest.cs (full)`, `src/Noctis/Services/SendToFolderService.cs (full)`, `src/Noctis/Services/ShareClipRenderer.cs (60-135)`, `src/Noctis/Services/TidalAuth.cs (full)`, `src/Noctis/Services/TidalPlaylistLink.cs (grep: next-link host check)`, `src/Noctis/Services/UpdateService.cs (full)`, `src/Noctis/Services/VlcAudioPlayer.cs (2290-2320, 2440-2480, 2645-2684, 2995-3024, 5110-5219 + greps)`, `src/Noctis/Services/VlcAudioPlayer.cs:2540-2790, 2990-3025, 3200-3230, 3380-3405, 5040-5235`, `src/Noctis/Services/WebRemoteServer.LocalApi.cs (full)`, `src/Noctis/Services/WebRemoteServer.cs (full)`, `src/Noctis/Services/WrapArchiveService.cs (full)`, `src/Noctis/Services/YouTube/YouTubeImportService.cs (40-120)`, `src/Noctis/Services/YouTube/YouTubeImportService.cs (full)`, `src/Noctis/Services/YouTube/YtDlpParsing.cs (arg builders, SanitizeFileName, BuildFileName)`, `src/Noctis/Services/YouTube/YtDlpParsing.cs (full)`, `src/Noctis/Services/YouTube/YtDlpTool.cs (330-510)`, `src/Noctis/Services/YouTube/YtDlpTool.cs (full)`, `src/Noctis/ViewModels/LibraryPlaylistsViewModel.cs (235-345)`, `src/Noctis/ViewModels/LyricsBackgroundPickerViewModel.cs (300-360)`, `src/Noctis/ViewModels/LyricsViewModel.cs (1100-1310, 1690-1880, 2195-2460, 2518-2610, 2712-2945)`, `src/Noctis/ViewModels/MainWindowViewModel.cs (2640-2750)`, `src/Noctis/ViewModels/MainWindowViewModel.cs:300-375`, `src/Noctis/ViewModels/MetadataViewModel.cs (1030-1110, 1885-1945, 2440-2510, 2585-2675)`, `src/Noctis/ViewModels/OrganizeFilesViewModel.cs (full)`, `src/Noctis/ViewModels/PlayerViewModel.cs (1405-1634)`, `src/Noctis/ViewModels/PlayerViewModel.cs:1380-1510`, `src/Noctis/ViewModels/SettingsViewModel.cs (1040-1109, 1270-1462, 4600-4640, 6520-6620, 6672-6701, 6800-6923)`, `src/Noctis/ViewModels/SettingsViewModel.cs (200-330, 4515-4605)`, `src/Noctis/ViewModels/SettingsViewModel.cs:140-298, 982-1200, 1985-2015, 2590-2680, 2820-2870, 4880-4956, 5930-6400, 6660-6705, 6925-6970`, `src/Noctis/Views/LyricsView.axaml (1128-1143, ItemsPanel templates)`, `src/Noctis/Views/SettingsView.axaml.cs (395-445)`, `tests/Noctis.Tests/LogRedactionTests.cs (full)`, `tests/Noctis.Tests/NoctisServerTests.cs (grep)`

### Cross-platform

**xplat-general:** Could not determine / deliberately not reported:
1) ServerCertificate EphemeralKeySet on Windows is marked unverified. Microsoft documents the Schannel limitation, but I could not confirm whether current Windows 11 + TLS 1.3 + Kestrel still fail. Runtime check: enable the Noctis server on Windows and open https://127.0.0.1:<port>/ in a browser. A failed handshake confirms it. No audio is involved.
2) AppImage host-tool failures (finding 6) follow the code's own comment and AppRun, not a reproduction. Which libraries linuxdeploy actually bundles (glib/openssl) decides which tools break. Check with 'ldd' against the AppImage's usr/lib on SteamOS/Arch.
3) Magnitude of the Linux watcher startup freeze is unmeasured. The mechanism comes from the .NET source. Compare StartupTrace marks 'playlists-loaded' → 'initialize-async-done' on a Linux machine with a large or NAS library.
4) LibraryService.NormalizePath trims the root separator. Linux '/' becomes '' and is filtered out of the include roots, so a music folder of exactly '/' scans nothing. Windows 'E:\' becomes the drive-relative 'E:'. That resolves to the per-drive current directory when the app runs from drive E (e.g. portable copy on the same USB stick). Edge cases; the Windows part is not verified.
5) CrashJournal (CrashJournal.cs:110-117) assumes FileShare.Read stops a second instance from opening the journal. On Unix .NET maps that to a shared flock, so a fall-through second launch (Program.cs:55-59, only when the activation pipe is dead) could truncate the live journal. This path is rare.
6) Still present by design: CA1416 in NoWarn (Noctis.csproj:44; the prior-audit item still applies), and plaintext credentials off Windows (PersistenceService.cs:406-460, AUDIT.md L18). The 07-24 audit's 'macOS data in ~/.config' is obsolete: since .NET 8, SpecialFolder.ApplicationData on macOS maps to NSApplicationSupportDirectory (verified in dotnet/runtime release/8.0 and 10.0 Environment.GetFolderPathCore.Unix.cs).
7) Minor, not written up: the Linux ~/.config/autostart .desktop Exec line does not escape '%', '$' or backquotes. LyricsBackgroundPicker uses a fixed shared /tmp/noctis-backdrops directory (multi-user Linux). IsSystemDarkMode spawns gsettings/gdbus synchronously on the UI thread when the System theme is active (normally fast). Case-only renames in the metadata editor are skipped on every OS (MetadataViewModel.cs:1073/2607, OrdinalIgnoreCase). Remaining OrdinalIgnoreCase path comparisons (AddFolderPath, FolderTreeBuilder, VlcAudioPlayer standby path) only matter for case-sibling paths on Linux.
8) Finding 1 (metadata rename id loss) is outside the cross-platform lane but was reported because it loses user data on every platform.
9) Do-not-regress items (LibVLC caching, session-volume debounce, RequestBringIntoView, lyrics Grid/TextWrapping) were not touched by any proposed fix and were outside this sweep. Their current state was not re-verified here.

**xplat-linux:** Past Linux fixes, checked against current code (all still in place):
- "Linux silent track = lost un-park": RunVolumeFadeIn lands on the target on every exit path, using the insistent write (VlcAudioPlayer.cs:1322-1341). The seek restore uses GetTargetVlcVolume() instead of the live volume (4919-4922). The per-player reassert net SchedulePlayerVolumeReassert runs on the native path (4090-4096, 4143-4186).
- "Linux visualizer side decode feed": SideDecodeMeterFeed is registered (Program.cs:374-375), started in App.axaml.cs:190-191, and disposed at shutdown (236).
- The keep-alive never writes Mute/Volume on its own player, so the stream-restore poisoning fix is intact.
- The DO-NOT-REGRESS caching setup (--file-caching=DefaultCachingMs plus adaptive read-ahead) is untouched; nothing proposed here changes it.

Could not determine:
1. No Flatpak manifest exists in the repo (packaging/ holds only Windows managers), so Flatpak-specific behavior couldn't be audited. That includes MPRIS own-name permission, gio trash, autostart (a sandboxed ~/.config/autostart is never seen by the host session), and /dev/sr* access. The UpdateService hint ("Download the new version from GitHub") is also wrong for Flatpak users.
2. The Avalonia Hide() semantics in the resume finding were checked against the 12.0.0 tag, not 12.1.2.
3. Also unverified for the remap: on X11, unmapping a window moves it to the Withdrawn state, and per EWMH the WM drops _NET_WM_STATE. So the Hide/Show remap may also lose Maximized/FullScreen or Topmost (mini player) state.
4. PlatformHelper.ReadPortalColorScheme and ReadGSettings call StandardOutput.ReadToEnd() before WaitForExit(1000), so the 1 s cap does nothing. A hung xdg-desktop-portal (gdbus's default 25 s timeout) would freeze the UI thread in ResolveActiveThemeKey. The trigger is rare, so this is not listed as a finding.
5. The AppImage glibc floor (built on ubuntu-latest) means Ubuntu 22.04 and Debian 12 can't load the bundled libvlc. On those systems the AppImage's LD_LIBRARY_PATH also shadows any system libvlc, so they depend on the tarball path described in the high finding.
6. With pure ALSA and no dmix (no Pulse/PipeWire), the AppImage's default-on keep-alive stream could hold the hardware device and block real playback. Not verified.
7. FreedesktopTrash's comment says a cross-device move returns false. In fact .NET's File.Move on Unix falls back to copy+delete on EXDEV, so gio-less trashing from an external drive copies the file into home Trash. That is data-safe, just slow; directory trashing without gio always fails, which is fail-safe.
8. Every finding needs testing on Linux. None was run; the audit was read-only.

**xplat-mac:** No macOS machine was available, so every finding needs testing on macOS. Only the x86_64 ffmpeg finding is backed by an inspected binary; the rest come from code plus library source.

Checked and found OK in the current code:
- MacNowPlayingService objc interop:
  - The handler type string "q@:@" matches MPRemoteCommandHandlerStatus (NSInteger).
  - positionTime returns a double through objc_msgSend, which is valid on both arm64 and x86_64 (fpret is only needed for long double).
  - numberWithDouble: and setEnabled: (BOOL as I1) are marshalled correctly.
  - The NSMutableDictionary (new → release) and NSImage (alloc/init → release) retain counts balance; the other objects are autoreleased and drained by the main run loop.
  - All calls run on the UI/main thread: TryStart runs from MainWindow Loaded, updates and commands go through Dispatcher.UIThread.Post, and Dispose runs from OnWindowClosed.
  - The callback delegates are held in static fields, so the GC cannot collect them.
- Prior audit items:
  - AUDIT.md M24 (no Now Playing), M23 (NSAppleEventsUsageDescription, now at dotnet.yml:247), H7/H8 (libvlc: official VLC 3.0.23 universal lib/ and plugins/ now bundled and probed) and L25 (vulnerability audit now runs on macos-arm64) are fixed.
  - M7 (RemoveLyrics blocking the UI thread) is fixed: the lyrics page path awaits the writer lane. The Lyrics Studio Save path is a separate UI-thread trash call (reported above).
  - L18 is still present by design. Credentials are plaintext on macOS and Linux (PersistenceService.cs:406-460 says Keychain is out of scope), mitigated by a 0700 data directory and 0600 settings files. The Tidal refresh-token file (TidalAuth.cs:405) is written with the default umask but sits inside the 0700 directory.
- osx-arm64 native libraries present: SkiaSharp and HarfBuzz (NativeAssets.macOS through their packages), e_sqlite3 (SQLitePCLRaw 2.1.12 ships runtimes/osx-arm64), and Whisper.net.Runtime 1.9.1 (its targets copy runtimes/macos-arm64/*.dylib plus ggml-metal.metal when the TFM has no platform suffix, which is the case on the mac runner). libvlc comes from the universal VLC dmg; yt-dlp_macos is downloaded at runtime.
- Codesigning: the CI find covers every *.dylib (including the VLC plugins and Whisper libraries), ffmpeg and createdump, and a failed `codesign --verify` fails the build.
- Data paths: data lives in ~/.config/Noctis on macOS (SpecialFolder.ApplicationData), consistently across the app. That is not the macOS convention, but it is not a bug.

Could not determine, or left out as speculative:
- Avalonia issue #22285 (https://github.com/AvaloniaUI/Avalonia/issues/22285) affects Avalonia 12.1.2 on macOS 27: after a key-equivalent shortcut followed by a window switch, a top-level menu stops opening for good. Noctis installs a window-level NativeMenu on MainWindow (MainWindow.axaml.cs:920) and opens many dialog windows, so it is probably exposed. This is an upstream bug; needs testing on macOS 27.
- Ad-hoc signing means TCC grants are tied to the build's code hash, so Automation (Finder), Documents, Downloads, removable-volume and Local Network permissions will likely prompt again after every update.
- The Info.plist has no NSLocalNetworkUsageDescription. Apple's TN3179 says local network privacy can misbehave when the main executable's UUID is missing or shared, and every .NET apphost built from the same SDK has the same LC_UUID. I could not confirm whether LAN Navidrome, Subsonic or Noctis-server connections are blocked on macOS 15+.
- LSMinimumSystemVersion is 12.0, but the .NET 10 support matrix lists macOS 14, 15 and 26. I could not confirm whether the runtime actually fails on macOS 12 or 13.
- publish-macos.sh builds bare single-file binaries with no .app, libvlc or ffmpeg, so they only run when VLC.app is installed. CI does not use it; it is a developer script.
- CI deletes VLC's plugins.dat, so libvlc rescans about 300 plugin dylibs at every launch (partly hidden by the libvlc warm-up task). Not measured.
- The library scanner uses Directory.EnumerateFiles with its compatible options, which do not skip hidden files, so macOS AppleDouble files ('._song.mp3') on exFAT and SMB volumes are enumerated. TagLib rejects them silently, which costs time but causes no functional harm.
- There is no macOS audio-device-change handling in app code: the classic path relies on VLC's auhal module, and the keep-alive is off by default on macOS (VlcSilenceKeepAlive.cs:71-78). Not verified.
- Window chrome and custom decorations on dialogs were not reviewed visually.

_Files read (111):_ `.github/workflows/dotnet.yml (330-440; ffmpeg bundling grep)`, `.github/workflows/dotnet.yml (full)`, `.github/workflows/dotnet.yml:1-330 (build matrix, audit, ffmpeg bundling, macOS .app/.dmg packaging, Info.plist, codesign)`, `.github/workflows/package-managers.yml (grep: no Linux packaging)`, `AUDIT.md (macOS items H7, H8, M7, M23, M24, L18, L25 re-checked against current code)`, `AUDIT.md L24, AUDIT_2026-07-24.md watcher-readiness item (leads only)`, `Dockerfile (full)`, `External sources: LibVLCSharp 3.x Core.cs and Core.Desktop.cs; Avalonia release/12.1.2 native/Avalonia.Native/src/OSX (all 57 .mm/.h files grepped; app.mm, AvnView.mm, AvnWindow.mm, menu.mm, KeyTransform.mm read); AvaloniaNativeApplicationPlatform.cs, AvaloniaNativePlatform.cs, ClassicDesktopStyleApplicationLifetime.cs, PlatformHotkeyConfiguration.cs, IAvnMenuItem.cs; Whisper.net.Runtime and Whisper.net.Runtime.Metal 1.9.1 nupkg build targets; the pinned evermeet ffmpeg-8.1.2.zip (SHA-256 and Mach-O header checked in the scratch dir)`, `LibVLCSharp 3.10.0 net10.0 DLL string inspection (nuget cache)`, `Prior audits checked against current code: AUDIT.md M22 (fixed via PathComparison, but WatchDebouncer still case-insensitive), L21 (fixed: ArgumentList/TryRunHostTool), L24 (fixed: portal + kdeglobals), M24 (fixed: MacNowPlayingService), H7/H8 (NuGet dropped, bundled payload; see VLC.app-first finding); AUDIT_2026-07-24 ComputeFileId (still present)`, `README.md (180-237)`, `Repo-wide greps: OperatingSystem.Is*/RuntimeInformation/SupportedOSPlatform/#if, SpecialFolder, GetInvalidFileNameChars, backslash literals, Process.Start/UseShellExecute, OrdinalIgnoreCase on paths, ToLowerInvariant, Windows DllImports, Registry, file:// / new Uri, GetTempPath`, `Tmds.DBus.Protocol 0.94.1 DLL string inspection (NameOwnerWatcher present, so the login1 well-known-sender match works)`, `packaging/ (listing: chocolatey/scoop/winget only, no Linux manifests)`, `publish-macos.sh (full)`, `src/Noctis.Core.Server/Services/Server/ServerCertificate.cs (full)`, `src/Noctis.Core/Helpers/AppPaths.cs (full)`, `src/Noctis.Core/Helpers/AppWrittenSidecarRegistry.cs (full)`, `src/Noctis.Core/Helpers/PathComparison.cs (full)`, `src/Noctis.Core/Helpers/TitleFormatter.cs (full)`, `src/Noctis.Core/Noctis.Core.csproj (full)`, `src/Noctis.Core/Services/LibraryService.cs (100-640, 1040-1240, 1340-1420, 1985-2051, 2600-2700, 2855-2891)`, `src/Noctis.Core/Services/LibraryService.cs (106-131, 626-668, 850-952, 1038-1108)`, `src/Noctis.Core/Services/LibraryWatcherService.cs (full)`, `src/Noctis.Core/Services/LocalFileSystemSource.cs (full)`, `src/Noctis.Core/Services/MetadataService.cs (1-130, 355-640)`, `src/Noctis.Core/Services/MetadataService.cs:60-160, 290-340`, `src/Noctis.Core/Services/PersistenceService.cs (60-130, 400-490)`, `src/Noctis.Core/Services/PersistenceService.cs:60-170, 395-475`, `src/Noctis.Core/Services/WatchDebouncer.cs (full)`, `src/Noctis.Server/Program.cs (full)`, `src/Noctis/App.axaml.cs (140-260)`, `src/Noctis/App.axaml.cs:120-300`, `src/Noctis/Controls/SharedLibVlc.cs (full)`, `src/Noctis/Helpers/DialogHelper.cs (full)`, `src/Noctis/Helpers/DragFileBehavior.cs (100-160)`, `src/Noctis/Helpers/ExternalOpenApp.cs (full)`, `src/Noctis/Helpers/LibraryRemovalHelper.cs (full)`, `src/Noctis/Helpers/LibraryRemovalHelper.cs:40-160`, `src/Noctis/Helpers/MiniPlayerPin.cs (full)`, `src/Noctis/Helpers/MusicVideoLocator.cs (full)`, `src/Noctis/Helpers/PlatformHelper.cs (full)`, `src/Noctis/Helpers/RecycleBin.cs (full)`, `src/Noctis/Helpers/SingleInstanceGuard.cs (full)`, `src/Noctis/Helpers/StartupHelper.cs (full)`, `src/Noctis/Models/Shortcuts.cs (full)`, `src/Noctis/Noctis.csproj (full)`, `src/Noctis/Program.cs (full)`, `src/Noctis/Services/AudioAnalysis/SideDecodeMeterFeed.cs (40-165, 280-356)`, `src/Noctis/Services/AudioAnalysis/SideDecodeMeterFeed.cs (full)`, `src/Noctis/Services/AudioAnalysis/SideDecodeMeterFeed.cs:60-140`, `src/Noctis/Services/AudioCd/AudioCdService.cs (full)`, `src/Noctis/Services/AudioCd/SystemDriveProbe.cs (full)`, `src/Noctis/Services/AudioConverterService.cs (120-210)`, `src/Noctis/Services/AudioConverterService.cs (130-440)`, `src/Noctis/Services/AudioConverterService.cs:136-225`, `src/Noctis/Services/CrashJournal.cs (grep context 95-118)`, `src/Noctis/Services/DuplicateFinderService.cs:25-70`, `src/Noctis/Services/FileOrganizePlanner.cs (80-157)`, `src/Noctis/Services/FileOrganizerService.cs:230-282`, `src/Noctis/Services/FolderTreeBuilder.cs (full)`, `src/Noctis/Services/FuzzyTrackMatcher.cs (full)`, `src/Noctis/Services/IAudioKeepAlive.cs (full)`, `src/Noctis/Services/LinuxResumeWatcher.cs (full)`, `src/Noctis/Services/LocalApi/LocalApiTokenStore.cs (full)`, `src/Noctis/Services/Loon/LoonClient.cs (555-580)`, `src/Noctis/Services/Lyrics/LyricsWriter.cs:20-180`, `src/Noctis/Services/MacNowPlayingService.cs (full)`, `src/Noctis/Services/MprisService.cs (225-270)`, `src/Noctis/Services/MprisService.cs (full)`, `src/Noctis/Services/PlaylistImportParser.cs (full)`, `src/Noctis/Services/PlaylistInteropService.cs (full)`, `src/Noctis/Services/Plugins/PluginHost.cs (1155-1185)`, `src/Noctis/Services/Plugins/PluginManifest.cs (225-252)`, `src/Noctis/Services/Plugins/PluginManifest.cs:215-255`, `src/Noctis/Services/SendToFolderService.cs (full)`, `src/Noctis/Services/ShortcutService.cs (full)`, `src/Noctis/Services/SilentWavFile.cs (full)`, `src/Noctis/Services/SmtcService.cs (1-40)`, `src/Noctis/Services/TidalAuth.cs:370-410`, `src/Noctis/Services/UpdateService.cs (60-200, 355-775)`, `src/Noctis/Services/UpdateService.cs (80-190, 350-730)`, `src/Noctis/Services/UpdateService.cs:80-160, 355-655`, `src/Noctis/Services/VlcAudioPlayer.cs (380-1390, 2140-2240, 2640-2700, 2900-2970, 3100-3180, 3230-3520, 4060-4390, 4860-4950, 5240-5358)`, `src/Noctis/Services/VlcAudioPlayer.cs (380-760, 1210-1240, 1575-1615, 5240-5357)`, `src/Noctis/Services/VlcAudioPlayer.cs:380-640, 1540-1660, 5230-5357`, `src/Noctis/Services/VlcSilenceKeepAlive.cs (full)`, `src/Noctis/Services/Waveform/WaveformCache.cs (KeyFor/DefaultDirectory)`, `src/Noctis/Services/Waveform/WaveformCache.cs (full)`, `src/Noctis/Services/Waveform/WaveformService.cs (full)`, `src/Noctis/Services/YouTube/YtDlpParsing.cs (25-54)`, `src/Noctis/Services/YouTube/YtDlpParsing.cs:25-65`, `src/Noctis/Services/YouTube/YtDlpTool.cs (60-190, 310-510)`, `src/Noctis/Services/YouTube/YtDlpTool.cs:60-210`, `src/Noctis/ViewModels/LibraryPlaylistsViewModel.cs (255-355)`, `src/Noctis/ViewModels/LyricsBackgroundPickerViewModel.cs (330-380)`, `src/Noctis/ViewModels/LyricsStudioViewModel.cs:620-710`, `src/Noctis/ViewModels/LyricsViewModel.cs:1575-1655`, `src/Noctis/ViewModels/MainWindowViewModel.cs (640-720, 2600-2760)`, `src/Noctis/ViewModels/MetadataHelper.cs (ShowDialogOwned + callers)`, `src/Noctis/ViewModels/MetadataViewModel.cs (1040-1100, 2580-2720)`, `src/Noctis/ViewModels/PlayerViewModel.cs (700-800, 1900-1930, 3000-3025, grep for shuffle/Seeked)`, `src/Noctis/ViewModels/PlayerViewModel.cs:160-190, 505-645, 1530-1590, 1895-2075`, `src/Noctis/ViewModels/SettingsViewModel.cs (1494-1500, 3500-3555)`, `src/Noctis/ViewModels/SettingsViewModel.cs (465-490, 1480-1680, 2015-2055, 4690-4730, 5698-5790, 6860-6920)`, `src/Noctis/Views/ConfirmationDialog.axaml (full)`, `src/Noctis/Views/MainWindow.axaml.cs (1-330, 460-520, 735-860, 925-1135)`, `src/Noctis/Views/MainWindow.axaml.cs (260-340, 440-500, 860-920, 1190-1230, 1440-1530)`, `src/Noctis/Views/MainWindow.axaml.cs:100-160, 420-540, 730-800, 840-1090, 1500-1640`, `src/Noctis/Views/MiniPlayerWindow.axaml.cs (60-180, 1200-1330)`, `src/Noctis/Views/MiniPlayerWindow.axaml.cs (90-130, 960-1060)`

## Appendix C — Findings refuted by adversarial verification (not fixed)

- **P26** `src/Noctis/Services/Waveform/WaveformService.cs:35` — The waveform seek bar's network-location guard only works on Windows, so NAS/SMB/NFS mounts on macOS/Linux are decoded over the network
  - refuted (refute): WaveformService.cs:22-25: the XML doc for IsNetworkLocation states its scope as "UNC paths and (on Windows) mapped network drives". Line 35's early return off Windows is that documented design, not an accidental gap. The class-level exclusion (lines 10-12, 18-20) targets SourceType Smb/WebDav sources, which are still excluded on every OS. The feature is opt-in: AppSettings.cs:478 `WaveformSeekBarEnabled` defaults to false. The worker is also throttled (BelowNormal priority, DecodeStartDelay, per the comment at lines 64-69), so the claimed read-ahead starvation is speculative. Detecting nfs/cifs mounts on macOS/Linux would be a reasonable enhancement, but this is documented, intended scope.
- **S25** `src/Noctis/Views/LrcEditorDialog.axaml.cs:49` — The LRC editor closes on a backdrop click, Esc or X and throws away all unsaved timestamps without asking
  - refuted (refute): The quoted code is real: LrcEditorDialog.axaml.cs:40-53 closes on Esc, on X and on a backdrop click (the full-window Border is at LrcEditorDialog.axaml:55), and LrcEditorViewModel.cs:191-229 tracks no dirty state. But nothing can open the dialog. Its only entry point is LyricsViewModel.cs:2989-3016 ([RelayCommand] OpenLrcEditor), and a repo-wide search of src *.cs and *.axaml finds no reference to OpenLrcEditorCommand. There is no reflection-based command lookup either. The earlier AUDIT_2026-07-24.md:657 already records the whole LrcEditor feature as unreachable. The data loss cannot happen until someone rewires the entry point, so it is at most a latent low.
