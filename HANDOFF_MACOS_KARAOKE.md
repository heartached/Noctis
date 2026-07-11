# macOS Bugs + Share-Video Karaoke — Session Handoff

> Working note for continuing in a later session. **Dev-only, not for release** — delete before cutting a release. Tag `@HANDOFF_MACOS_KARAOKE.md` in a new session to resume.

Branch: `cross-platform` · Date: 2026-07-07 · Sibling doc: `EQ_VOLUME_FIX_HANDOFF.md` (EQ/volume saga — closed on Windows, awaiting mac/Linux by-ear)

---

## Part 1 — macOS bugs (MOVED)

> **Moved to `HANDOFF_MACOS_BUGS.md` (2026-07-07)** — that file is now the single source of truth for the animated-cover stutter, the VLC.app-vs-bundled-lib finding, the Terminal TCC gotcha, and the mac/Linux verification checklist. The copy below is stale; don't work from it.

<details>
<summary>Stale original (superseded)</summary>

### 1a. Animated-artwork change stutters audio (OPEN — needs log + answers)

**Repro (user's MacBook, Apple Silicon):** while a track plays, add OR remove animated artwork (mp4/webm cover) → audio starts stuttering/cutting out; clicking another track fixes it. Regular (static) artwork unconfirmed. Reproduced on the 2026-07-07 build.

**Architecture (read these first):**
- `src/Noctis/Controls/AnimatedCoverImage.axaml.cs` — plays the cover by software-decoding frames via LibVLC video callbacks into a 600×600 `WriteableBitmap`; per-frame two × 1.44 MB copies on the UI thread; each Source/IsActive change = `Teardown()` (worker: `Player.Stop()` + `Dispose`) + new `Session` (worker: `new MediaPlayer` + `Play`, `:input-repeat=65535`).
- `src/Noctis/Controls/SharedLibVlc.cs` — the ONE process-wide LibVLC instance for covers (`--aout=none`), created lazily on first animated-cover use. Separate from the audio player's LibVLC.
- `src/Noctis/Services/AnimatedCoverService.cs` — import copies the mp4/webm to the covers cache dir; **never touches the audio file** (so it's not a file-rewrite-under-VLC problem).

**Evidence so far (user's aborted `NOCTIS_VLC_LOG=1` run):**
- Noctis on this Mac runs on **VLC.app's engine 3.0.23**, not the bundled nuget 3.0.21 (see 1b).
- VLC.app's `plugins.dat` cache is unreadable → every `new LibVLC(...)` full-scans/dlopens 461 modules. The cover instance is created lazily exactly when the first animated cover appears → one heavy dlopen storm at that moment (transient, so it can't alone explain "stutters until track change").
- No playback-time log captured yet (the run died to the Terminal TCC issue, 1c).

**Suspects, in order:**
1. Cover decode/instance contention with the audio LibVLC (CPU or global VLC state) — the `SharedLibVlc` comment already warns "multiple LibVLC instances fight over global VLC subsystem state".
2. VLC.app 3.0.23 engine behaving differently than the bundled lib (1b).
3. CoreAudio aout disturbance when cover MediaPlayers are created/stopped (would match "removal also stutters" if confirmed).

**Needed from the user (blocked on this):**
1. `launchctl setenv NOCTIS_VLC_LOG 1` → launch Noctis from Finder/Dock → play a track → add the animated cover → let it stutter ~10 s → quit → send `~/Desktop/noctis_vlc_diag.log` → `launchctl unsetenv NOCTIS_VLC_LOG`.
2. Does the stutter last the whole time the cover is looping, or only a moment after the change?
3. Does removing the cover stop the stutter immediately, or does it persist until a track switch?
4. Is the "fixing" track one with a static cover (would implicate the looping decode) or animated too (would implicate the aout)?

**Log reading guide:** look for `mmdevice`-equivalent CoreAudio (`auhal`) underrun/"playback too late"/flush lines and `[APP]` markers around the cover-change timestamp.

### 1b. FINDING: Noctis prefers the installed VLC.app over the bundled libvlc (by design — re-evaluate)

`VlcAudioPlayer.TryFindMacLibVlcPath()` (~line 3249) probes `/Applications/VLC.app/Contents/MacOS/lib` → `/opt/homebrew/lib` → `/usr/local/lib`, and only falls back to `Core.Initialize()` (bundled `VideoLAN.LibVLC.Mac` 3.0.21) when none exist. Sets `VLC_PLUGIN_PATH` via real `setenv`. Rationale in code: the nuget's layout shifts between versions; VLC.app was the "recommended path".

Consequences: users with VLC.app run a different engine version than we test/bundle (3.0.23 vs 3.0.21) and its plugin cache can be stale/missing (full plugin scan per LibVLC instance = 2 scans: audio + covers). shan (launch-fix reporter) presumably runs the bundled lib — so mac users are split across two engines.

**Candidate future fix (needs a Mac to verify, don't flip blind):** prefer the *bundled* dylibs when present (they're ad-hoc signed by our CI since ea234b9/cf18634), fall back to VLC.app. First verify the .app actually contains `libvlc.dylib`/`libvlccore.dylib`/plugins (check a CI artifact's `Contents/MacOS/`), and that `Core.Initialize(bundledPath)` + `VLC_PLUGIN_PATH` works on real hardware.

### 1c. GOTCHA: Terminal-launched Noctis can't play (TCC)

Launching `/Applications/Noctis.app/Contents/MacOS/Noctis` from Terminal makes the process inherit **Terminal's** file permissions → the music library is unreadable → every play fails (playbar appears/disappears instantly). Not a code bug. Use `launchctl setenv` + Finder launch for env-gated diagnostics (or give Terminal Full Disk Access).

### 1d. Awaiting by-eye/by-ear on macOS (shipped 2026-07-07, unverified there)

- Search-metadata button icon (was ⌕ text glyph → missing-glyph boxes on mac; now vector `SearchIcon`). Verify on Linux too.
- The Windows-verified audio fixes (EQ unity preamp, EQ-drag/track-start cut removal) — mac by-ear pass pending.
- macOS Apple Silicon launch fix: ✅ verified on a real downloaded build 2026-07-07.

</details>

---

## Part 2 — Karaoke word-sweep in Share Lyrics "Save Video" (IMPLEMENTED 2026-07-07, awaiting by-eye check)

> Shipped as 5 local commits `4c61095..2bfd77a` on `cross-platform` (build clean, 400/400 tests pass).
> New: `Services/KaraokeSweep.cs` (pure math), `ShareCardRenderer.RenderKaraokeFrames` (chrome drawn
> once, per-frame word sweep, 24 fps PNG sequence), `ShareClipRenderer.BuildFfmpegFrameArgs/RenderFramesAsync`,
> "Karaoke video" toggle in the card-options flyout (visible only when a selected line has word timings,
> default ON). Degrade rules: edited/hard-wrapped lines → whole-line highlight; toggle off or no word
> timings → old still-image path (unchanged). Verify per the checklist below; the design sketch that
> follows is what was built.

**Ask (user, 2026-07-07):** in the Share Lyrics popup, the exported *video* should show moving word-by-word (ELRC) lyrics like the lyrics page, with an option to enable it.

### Current pipeline (all still-image)

- `LyricShareViewModel` (~line 469-486): Save Video → renders ONE PNG card → `ShareClipRenderer.RenderAsync`.
- `src/Noctis/Services/ShareClipRenderer.cs`: ffmpeg loops the still (`-loop 1`) over the trimmed audio slice (`ShareClipTiming`), H.264 + AAC. `BuildFfmpegArgs` is pure and unit-tested.
- `src/Noctis/Services/ShareCardRenderer.cs`: Skia card renderer — `RenderLyricCardStyled(LyricCardSpec)`; helpers `WrapText`, `SanitizeForRender`, `Ellipsize` (pure, tested).
- Card options flyout lives in `LyricShareDialog.axaml` (~line 292, Format/Text/Sync group; summary string `CardOptionsSummary`).

### Design sketch

1. **Data**: pass the selected lines' word timings into the share VM. Word model: `Models/WordTiming.cs`; the lyrics page's `LyricLine`s carry them when the source is ELRC (`.lyricsfile` sidecar / word-level LRC). The share dialog currently receives plain text lines + line times (`ShareLineOption`, `GetClipTiming()`); extend with optional `IReadOnlyList<WordTiming>` per line.
2. **Frame renderer**: new `RenderLyricCardFrame(spec, tSeconds)` in `ShareCardRenderer` — identical card, but lyric text painted in two passes: dim full text, then bright text clipped to the sweep X for the active word (same visual language as the lyrics page's foreground-gradient sweep). Word X positions: tokenize EXACTLY as rendered (after `SanitizeForRender`, after `WrapText` row-splitting) and map ELRC words → rendered tokens by index; X = prefix-width measurement per row. Mismatched token counts (sanitizer edits, wrapping mid-line) must degrade gracefully to per-line highlight.
3. **Frame loop**: render N = fps × `ShareClipTiming.DurationSeconds` frames (24 fps is plenty) to a temp dir as a PNG sequence. Reuse the decoded artwork bitmap, typefaces, and SKSurface across frames — only the text layer changes. Progress → `StatusText` (`IsRendering` already exists).
4. **ffmpeg**: sibling of `BuildFfmpegArgs` taking `-framerate 24 -i frames-%05d.png` instead of `-loop 1`; keep the output `-t`/`-shortest` clamp semantics and culture-invariant formatting. Unit-test it next to the existing args test.
5. **UI/VM**: "Karaoke" toggle in the card options flyout (with `CardOptionsSummary` including it), only enabled when word timings exist for the selected lines; off → current still-image path untouched. Consider a phase-2 line-level highlight for synced-but-not-ELRC tracks.
6. **Perf guardrails**: 1080×1080 × ~500 frames — Skia draw is cheap, PNG encode dominates; all off the UI thread (existing pattern). Temp dir cleanup in `finally` like the current temp frame.

### Verification

- Build + unit tests (args builder, token-mapping helper if extracted pure).
- User renders a clip from an ELRC track (e.g. the GANG GANG test track) and checks the sweep by eye; a non-ELRC track must still export the current still video.

### Risks / notes

- The word-clip lesson from the lyrics page applies (see memory `elrc_lyrics_overhaul`): glyph clipping at word edges — prefer the foreground-gradient/clip-rect approach that finally worked there (`ProgressToSweepForegroundConverter` semantics), not OpacityMask.
- Don't let the karaoke path regress the still-card path — it stays the default.
