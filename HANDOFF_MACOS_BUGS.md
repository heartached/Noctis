# macOS Bugs — Session Handoff

> Working note for continuing in a later session. **Dev-only, not for release** — delete before cutting a release. Tag `@HANDOFF_MACOS_BUGS.md` in a new session to resume.
>
> Split out of `HANDOFF_MACOS_KARAOKE.md` (2026-07-07) — the karaoke share-video feature there is done and user-confirmed; this file carries the still-open macOS work.

Branch: `cross-platform` · Date: 2026-07-07 · Sibling doc: `EQ_VOLUME_FIX_HANDOFF.md` (EQ/volume — closed on Windows, awaiting mac/Linux by-ear)

---

## 1. Animated-artwork change stutters audio (OPEN — blocked on log + answers)

**Repro (user's MacBook, Apple Silicon):** while a track plays, add OR remove animated artwork (mp4/webm cover) → audio starts stuttering/cutting out; clicking another track fixes it. Regular (static) artwork unconfirmed. Reproduced on the 2026-07-07 build.

**Architecture (read these first):**
- `src/Noctis/Controls/AnimatedCoverImage.axaml.cs` — plays the cover by software-decoding frames via LibVLC video callbacks into a 600×600 `WriteableBitmap`; per-frame two × 1.44 MB copies on the UI thread; each Source/IsActive change = `Teardown()` (worker: `Player.Stop()` + `Dispose`) + new `Session` (worker: `new MediaPlayer` + `Play`, `:input-repeat=65535`).
- `src/Noctis/Controls/SharedLibVlc.cs` — the ONE process-wide LibVLC instance for covers (`--aout=none`), created lazily on first animated-cover use. Separate from the audio player's LibVLC.
- `src/Noctis/Services/AnimatedCoverService.cs` — import copies the mp4/webm to the covers cache dir; **never touches the audio file** (so it's not a file-rewrite-under-VLC problem).

**Evidence so far (user's aborted `NOCTIS_VLC_LOG=1` run):**
- Noctis on this Mac runs on **VLC.app's engine 3.0.23**, not the bundled nuget 3.0.21 (see §2).
- VLC.app's `plugins.dat` cache is unreadable → every `new LibVLC(...)` full-scans/dlopens 461 modules. The cover instance is created lazily exactly when the first animated cover appears → one heavy dlopen storm at that moment (transient, so it can't alone explain "stutters until track change").
- No playback-time log captured yet (the run died to the Terminal TCC issue, §3).

**Suspects, in order:**
1. Cover decode/instance contention with the audio LibVLC (CPU or global VLC state) — the `SharedLibVlc` comment already warns "multiple LibVLC instances fight over global VLC subsystem state".
2. VLC.app 3.0.23 engine behaving differently than the bundled lib (§2).
3. CoreAudio aout disturbance when cover MediaPlayers are created/stopped (would match "removal also stutters" if confirmed).

**Needed from the user (blocked on this):**
1. `launchctl setenv NOCTIS_VLC_LOG 1` → launch Noctis from Finder/Dock → play a track → add the animated cover → let it stutter ~10 s → quit → send `~/Desktop/noctis_vlc_diag.log` → `launchctl unsetenv NOCTIS_VLC_LOG`.
2. Does the stutter last the whole time the cover is looping, or only a moment after the change?
3. Does removing the cover stop the stutter immediately, or does it persist until a track switch?
4. Is the "fixing" track one with a static cover (would implicate the looping decode) or animated too (would implicate the aout)?

**Log reading guide:** look for `mmdevice`-equivalent CoreAudio (`auhal`) underrun/"playback too late"/flush lines and `[APP]` markers around the cover-change timestamp.

## 2. FINDING: Noctis prefers the installed VLC.app over the bundled libvlc (by design — re-evaluate)

`VlcAudioPlayer.TryFindMacLibVlcPath()` (~line 3249) probes `/Applications/VLC.app/Contents/MacOS/lib` → `/opt/homebrew/lib` → `/usr/local/lib`, and only falls back to `Core.Initialize()` (bundled `VideoLAN.LibVLC.Mac` 3.0.21) when none exist. Sets `VLC_PLUGIN_PATH` via real `setenv`. Rationale in code: the nuget's layout shifts between versions; VLC.app was the "recommended path".

Consequences: users with VLC.app run a different engine version than we test/bundle (3.0.23 vs 3.0.21) and its plugin cache can be stale/missing (full plugin scan per LibVLC instance = 2 scans: audio + covers). shan (launch-fix reporter) presumably runs the bundled lib — so mac users are split across two engines.

**Candidate future fix (needs a Mac to verify, don't flip blind):** prefer the *bundled* dylibs when present (they're ad-hoc signed by our CI since ea234b9/cf18634), fall back to VLC.app. First verify the .app actually contains `libvlc.dylib`/`libvlccore.dylib`/plugins (check a CI artifact's `Contents/MacOS/`), and that `Core.Initialize(bundledPath)` + `VLC_PLUGIN_PATH` works on real hardware.

## 3. GOTCHA: Terminal-launched Noctis can't play (TCC)

Launching `/Applications/Noctis.app/Contents/MacOS/Noctis` from Terminal makes the process inherit **Terminal's** file permissions → the music library is unreadable → every play fails (playbar appears/disappears instantly). Not a code bug. Use `launchctl setenv` + Finder launch for env-gated diagnostics (or give Terminal Full Disk Access).

## 4. Awaiting by-eye/by-ear on macOS (and Linux where noted)

- Search-metadata button icon (was ⌕ text glyph → missing-glyph boxes on mac; now vector `SearchIcon`). Verify on Linux too.
- The Windows-verified audio fixes (EQ unity preamp, EQ-drag/track-start cut removal) — mac by-ear pass pending.
- **NEW (2026-07-07 pm):** CJK font fallback on share cards (`ShareCardRenderer.SplitFallbackRuns` → CoreText on mac, fontconfig on Linux). Korean/Japanese lyrics card must show real glyphs, not boxes — verified on Windows only.
- **NEW (2026-07-07 pm):** karaoke share video + live dialog preview — confirmed on Windows; a quick mac/Linux sanity pass (export one clip, watch the preview animate) is enough.
- macOS Apple Silicon launch fix: ✅ verified on a real downloaded build 2026-07-07 (not yet in any published release — v1.2.2 dmg predates it).
