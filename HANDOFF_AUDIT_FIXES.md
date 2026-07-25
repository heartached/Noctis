# Handoff — audit fix campaign

**Branch:** `cross-platform` · **Base:** `b48dd7e` · **27 commits, not pushed**
**Source of truth for remaining work:** `AUDIT_2026-07-24.md` (257 findings, `file:line` each)

Last verified state: build clean (0 warnings), **547/547 tests pass** (run from the default
output dir — running with `-p:OutDir` to a scratch path false-fails `IconResourceReferenceTests`,
which looks for the repo root relative to the output dir).

---

## ⚠️ Uncommitted right now

| File | What | Status |
|---|---|---|
| `src/Noctis/Views/SidebarView.axaml.cs` | **User's own pre-existing work** (popup topmost fix) | Leave alone — not mine |
| `AUDIT_2026-07-24.md`, `HANDOFF_*.md` | Untracked docs | Intentionally untracked |

Nothing else is outstanding — the CI hardening that was uncommitted in the previous
handoff is now `0298e27`.

---

## Progress

| Severity | Total | Fixed | Left |
|---|---:|---:|---:|
| Critical | 6 | 5 | **1** |
| High | 41 | 41 | **0** |
| Medium | 141 | ~125 | ~16 |
| Low | 69 | ~50 | ~19 |
| **Total** | **257** | **~221** | **~36** |

Everything left is medium/low and listed below; each one is either a deliberate skip
with a stated reason, or blocked on you.

---

## Blocked on you (not code problems)

1. **By-eye check** — one surface left after the `a792616` revert: the **Playlists sort
   chip** in the top bar, beside New and the Library / Cover Flow toggle.
   Two others only appear on failure: the inline launch-at-login error (Settings →
   General, when the OS refuses the registration) and the Clear Queue confirmation
   (queue popup, ≥5 tracks).

---

## Deliberately not attempted (each needs your judgement or a non-Windows box)

**Layout architecture — wants a by-eye pass**
- **AlbumDetail track-list virtualization** (`AlbumDetailView.axaml:469`) — the
  non-virtualizing panel is documented as deliberate (nested in an outer ScrollViewer).
  Fixing it is a scroll-architecture redesign, not a bug fix.
- **Responsive grid column counts** (§7, `LibraryAlbumsViewModel.cs:31`, Favorites `:23`,
  Artists `:19`) — the 5/5/7 constants feed the row builders *and* the tile-size maths in
  each view's `OnSizeChanged`. Making them a function of width means rebuilding rows on
  resize and binding `UniformGrid.Columns` per row template. Mechanical, but it changes how
  every grid looks at every window size, and `.claude/rules/ui.md` says to preserve the
  `FilteredAlbumRows` virtualization design.
- **Playlists grid virtualization** (`LibraryPlaylistsView.axaml:25`) — same shape: the fix
  is to restructure into the row-based ListBox pattern Albums/Artists use.
- **Lyrics list virtualization** — the *cap* is in (3000 lines, `MaxLyricLines`), which
  closes the unbounded case. Actually virtualizing the ItemsControl would collide with the
  melisma clip fix (four deliberate `ClipToBounds=False` walls) and the cascade-stagger
  animation, both of which walk realized containers.

**Needs a mac/Linux build to evaluate**
- **CA1416** — still in `NoWarn`, now with the reason recorded in the csproj. It is clean
  on the Windows TFM; the non-Windows TFM has never been built with it visible. CI now runs
  tests on linux-x64 and macos-arm64, so one build with it removed will produce the list.
- **macOS libVLC bump** (3.0.21 → 3.1.3.1) — flagged `unverified` in the audit.
- **macOS Now Playing / media keys** — no equivalent of SmtcService/MprisService exists;
  this is new native interop, not a fix.
- **macOS data root** (`~/.config` → `~/Library/Application Support`) — needs a migration
  that moves an existing install's data, verified on real hardware.

**Structural**
- **`ComputeFileId` case-folding** (`LibraryService.cs:~1808`) — correct fix changes every
  track GUID on Linux/macOS, orphaning ratings *and* playlist/queue references. Needs a real
  migration.
- **First paint blocks on the native libvlc load** (§10, `App.axaml.cs:98`) — the fix is
  `Lazy<IAudioPlayer>` / a factory so the VM graph can be built without forcing
  `Core.Initialize()`. Every `_audioPlayer.X` call site in `VlcAudioPlayer`'s consumers has
  to go through the lazy, and `.claude/rules/audio.md` governs that path. Real win, wants
  its own session.
- **One OS thread per seek burst** (§1, `VlcAudioPlayer.cs:3123`) — parking a single
  prioritised thread on an event changes the seek-worker lifecycle the audio rules
  explicitly protect. LOW severity; not worth the regression risk in a sweep.
- **`DominantColorExtractor` RTB→PNG→decode round trip** — moving it off the UI thread is
  Avalonia-backend-sensitive and would shift colours subtly. (The two worst parts of that
  chain — unbounded decode and ~29k `GetPixel` P/Invokes — *were* fixed in
  `ShareCardRenderer`.)
- **`MenuOpenAnimation` wrong-presenter** — needs `PopupFlyoutBase.Popup` (protected);
  `MenuFlyout` doesn't expose the presenter as a logical child either. Two approaches
  tried, both reverted, limitation documented in the file.
- **`ProgressToSweepForegroundConverter` per-frame brush alloc** — cannot cache on the
  converter: it's an `x:Key` singleton shared across every word cell, so a cached brush
  would make all words render the last-written word's offsets. Needs the gradient moved
  onto the word cell (bind `GradientStop.Offset` directly). Reverted + documented in-file.
- **Big migrations** — Avalonia 12 / .NET 10 / SkiaSharp 4, VLC plugin-tree pruning,
  ReadyToRun, code signing + notarisation, Keychain/libsecret credential storage.
- **`brew`/`apt` unpinned installs** in the release job (icon generation). Removing them
  means committing pre-generated `.icns`/`.png`; the existing code is visibly tuned for
  icon quality, so it wants a by-eye check.

**Features, not fixes** (audit lists them under Medium/Low, but each is new UI)
- **Settings search** (§2) — the doc comment claiming one is gone; the box is not built.
  7 tabs × ~50 hand-laid cards need per-control metadata before a filter can exist.
- **Manual lyrics search + result picker** (§6) — `SearchLyricsAsync` already returns
  several candidates and `FetchLrcLibAsync` takes `FirstOrDefault`; the missing part is an
  editable artist/title field and a result list on the no-lyrics state.
- **Lyric font-size control** (§6) — size is derived from window dimensions, panel is
  hard-coded to 25, and `SoftWrapText`'s width is a fixed 25 chars that would have to
  follow it.
- **Folders tab multi-select / bulk actions** (§7).
- **Remote-sources UI** (§2) — `SourceConnections` is consumed by `UnifiedLibraryService`
  and `NavidromeSyncService` but nothing can create one. Five other persisted-but-dead
  fields (`DefaultPageIndex`, `StreamFirstEnabled`, `AutoCacheEnabled`,
  `OfflineCacheLimitMb`, `ProfileUsername`) are scaffolding for unshipped features —
  deleting them is your call, not a bug fix.
- **Chocolatey release automation** (§12) — the stale-checksum foot-gun now fails loudly
  with instructions instead of silently; automating the job in `package-managers.yml` is
  the real fix.

**Reverted by the user — do not re-add** (`a792616`)
- Queue-popup **shuffle toggle** and **Save as Playlist** action.
- Lyrics-page **sync-offset pill**, **Edit Sync**, **Save to File** (and the whole
  `LyricsSyncOffsetMs` backend).
- Settings **Web Remote port field** and **equalizer on/off toggle**.
- Wrap **"Partial year" chip**.

  These closed the audit's "implemented but no entry point" findings (§5 shuffle +
  SaveQueueAsPlaylist, §6 LRC editor + Save to File, §2 EqualizerEnabled + WebRemotePort)
  and §7's partial-Wrap indicator. The user does not want the controls. Those findings are
  **open and staying open** — the commands still exist and are still unreachable. The
  non-UI behaviour underneath was kept: ephemeral-port fallback, Wrap coverage tracking +
  startup archiving, `WebRemotePort` load clamping.

**Left as-is on purpose**
- **The Last.fm key + secret stay in source** (`LastFmService.cs:15-16`), the audit's
  remaining Critical. Decided 2026-07-24, deliberately, after working the argument through:
  rotating only helps if the credential can be secret afterwards, and a desktop app cannot
  hold one — the replacement pair is public the day it ships, so you land in the identical
  position having made every user reconnect and broken scrobbling on every installed copy.
  Rotation is only coherent bundled with moving the secret behind the Loon relay, which is
  its own decision (availability cost: scrobbling would stop depending only on Last.fm).
  Revisit if Last.fm ever flags abuse of the app registration. **Do not re-raise this as a
  standalone fix.**
- `IPlaylistInteropService.ImportM3uAsync` (§7) still has no production caller, but its
  decoder is now shared with the live import path and it carries two test files. Deleting
  it is churn; the in-file comment says so.

---

## Two audit findings are wrong

1. **"More By Artist reshuffles — separator mismatch"** (§7) — both key sites use `\0`;
   the auditing agent misread the escape as a space. Verified at byte level.
2. **"Settings are loaded twice during startup"** (§5, `MainWindow.axaml.cs:120`) —
   `SettingsViewModel.LoadAsync` has had an `if (_settingsLoaded) return;` guard since
   before `b48dd7e`, and the two calls are strictly sequential (`MainWindow.axaml.cs:158`
   then `:161`), so the second is a no-op. No double read.

Treat the rest of the audit as high-but-not-perfect confidence.

---

## Conventions to keep

- Conventional-commit subject; body explains **root cause and user-visible symptom**, not
  just the change. Match the existing style (`git log b48dd7e..HEAD`).
- **No attribution trailers** — `.claude/settings.json` sets `attribution.commit: ""`, and
  release hygiene forbids Claude co-author lines in this repo.
- Comments explain *why the old code was wrong*, not what the new code does.
- Verify with `dotnet test tests/Noctis.Tests/Noctis.Tests.csproj -v minimal` (default output
  dir). Never claim green without the command output.
- `rtk dotnet …` does not work here — call `dotnet` directly.
- `Grep` silently returns "No matches" on files with non-ASCII bytes
  (`AlbumDetailViewModel.cs`, `DuplicateMatcher.cs`); re-check with `grep -a`.
- The Bash tool's CWD resets to the outer `Downloads\Noctis` wrapper between calls — use
  absolute paths or prefix with `cd /c/Users/okfer/Downloads/Noctis/Noctis &&`.
