# DEPENDENCY_LOG — Audit Phase 3 (branch `cross-platform`)

Order followed: CVE-driven security bumps → patch → minor → majors listed-not-done. Build + full suite (1115 tests) run after every bump; `dotnet list package --vulnerable --include-transitive` clean at the end (win-x64 host graph; mac/linux graphs audited by CI since L25).

## Step 1 — security (named CVEs): nothing to bump

Every named CVE in the audit table is already fixed at the current pin — verified per row in AUDIT.md:
- SkiaSharp 2.88.9 — CVE-2023-4863 fixed in 2.88.6, not affected. HOLD (2.88.x is what Avalonia 11.3.18 resolves).
- System.Text.Json 8.0.5 — CVE-2024-30105 fixed in 8.0.4, CVE-2024-43485 fixed in 8.0.5; both contained. (8.0.6 below is non-security servicing.)
- SQLitePCLRaw.bundle_e_sqlite3 2.1.12 — bundles SQLite 3.53.3, clear of CVE-2025-6965 (<3.50.2) and CVE-2025-70873 (<=3.51.1). KEEP; 3.x bundle is a major, incompatible with Microsoft.Data.Sqlite 8.x/9.x.
- Tmds.DBus.Protocol 0.21.3 — IS the backported fix release for CVE-2026-39959; Avalonia.FreeDesktop 11.3.18 requires >=0.21.3 so resolution can't regress. KEEP (0.9x is a version-scheme jump off the branch Avalonia pins).
- EXCEPTION with no bump remedy: VideoLAN.LibVLC.Mac — see decision block at the bottom.

## Step 2 — patch bumps

- Microsoft.Data.Sqlite | 8.0.11 → 8.0.29 | 7947b88 | Library loads; play a few tracks; ratings/play counts/favorites survive a restart; Settings → Rebuild Index completes. Verified after bump: SQLitePCLRaw still resolves 2.1.12 (explicit float beats 8.0.29's transitive 2.1.6 pin — SQLite native stays 3.53.3).
- System.Text.Json | 8.0.5 → 8.0.6 | 3df74c4 | settings.json round-trips (change a setting, restart); lyrics online search returns results; Check for Updates works. Non-security servicing release; no behavior changes documented.

## Step 3 — minor bumps

- YamlDotNet | 15.1.6 → 15.3.0 | d2fc33d | Open a track that has a .lyricsfile sidecar (word-level sync) → word timing renders as before. Parser uses a fixed DTO + IgnoreUnmatchedProperties; suite covers it; no API changes hit our code (build clean, no source changes needed).
- Microsoft.NET.Test.Sdk | 18.7.0 → 18.8.1 | 85e074d | Nothing to hand-test — test-time only, never ships. Suite ran 1115/0 on the new SDK.

No audio-related package changed in this phase (LibVLCSharp 3.10.0, LibVLCSharp.Avalonia 3.10.0, VideoLAN.LibVLC.Windows 3.0.23.1, NAudio.Core/Wasapi 2.3.0 are all already at latest stable and were not touched), so the audio hand-test battery is not triggered by any of these bumps. LibVLC init flags untouched.

## Round 2 (2026-08-05, user-approved) — retarget + mac libvlc

- net8.0 → net10.0 LTS (all 3 projects + CI SDK 10.0.x) | 32df2b2 | Zero source changes needed; suite 1115/0 on net10.0. HAND-TEST: launch the app, play a track, open Settings — general smoke; SMTC overlay still appears on media keys (WinRT projections on the net10 windows TFM). NOTE: your system dotnet is still SDK 8.0.423 — install .NET 10 SDK (e.g. `winget install Microsoft.DotNet.SDK.10`) or the repo won't build outside this session (I used a user-local SDK at %LOCALAPPDATA%\Microsoft\dotnet).
- System.Text.Json package ref REMOVED | 3488280 | On net10.0 the in-box 10.0.x copy supersedes the 8.0.6 package; the SDK flags the ref as inert (NU1510). Same hand-tests as the 8.0.6 bump row above. 0-warnings baseline restored; `--vulnerable --include-transitive` clean on the new graph.
- VideoLAN.LibVLC.Mac REMOVED (no bump existed) | 1b71b28 | CI now bundles official VLC 3.0.23 universal dylibs+plugins (pinned URL + SHA-256 from download.videolan.org) into Noctis.app at Contents/MacOS/libvlc/{lib,plugins}; VlcAudioPlayer probes it after VLC.app. VERIFY: next CI run's mac legs restore clean (NU1603 tripwire disarmed by the removal) and package the dmg; then a real-mac smoke test on a machine WITHOUT VLC.app — app launches and plays audio on Apple Silicon (closes H7's unverified half).

## Step 4 — major bumps: NOT done, listed for your decision

- Avalonia / Avalonia.Desktop / Avalonia.Themes.Fluent / Avalonia.Fonts.Inter 11.3.18 → 12.1.1 — real migration: SkiaSharp jumps to 3.x with it, text stack changes, our Fluent accent-key overrides (App.axaml.cs, Assets/Styles.axaml, theme files) need smoke-testing, Avalonia.Diagnostics has NO 12.x (replaced by AvaloniaUI.DiagnosticsSupport — a package swap), Avalonia.Labs.Lottie must move to 12.0.2 and Avalonia.Headless.XUnit in lockstep.
- SkiaSharp 2.88.9 → 3.119.4 (or 4.151.0) — only ever together with Avalonia 12.
- xunit 2.9.3 → v3 — v2 is deprecated/security-fix-only per maintainers; v3 is a test-project migration.
- Microsoft.Extensions.DependencyInjection 8.0.1, System.Security.Cryptography.ProtectedData 8.0.0, Microsoft.Data.Sqlite 8.0.29 → 10.x line — ride the .NET 10 retarget below.
- ~~.NET 8 → .NET 10 LTS retarget (AUDIT M26)~~ — DONE in Round 2 (32df2b2).
- YamlDotNet 15.3.0 → 18.1.0 — three majors, each with breaking API changes; no driver.
- Tmds.DBus.Protocol 0.21.3 → 0.94.x — do not move while Avalonia pins the 0.21 branch.
- VideoLAN.LibVLC.Windows: hold 3.0.23.1; 3.0.24-beta1 in progress upstream — re-check on release. LibVLCSharp 4.x exists only as feedz.io previews, not on nuget.org.

## RESOLVED (was: DECISION NEEDED) — VideoLAN.LibVLC.Mac (AUDIT H7/H8)

User chose option (a); implemented in 1b71b28 (see Round 2 above). Original analysis kept below for the record.

## Original analysis — VideoLAN.LibVLC.Mac (AUDIT H7/H8, security-relevant, no bump exists)

The csproj pins 3.0.21, which does not exist on nuget.org; restore floats to the abandoned 2019 payload 3.1.3.1 (predates every VLC security fix from ~3.0.8 on, incl. CVE-2024-46461 and the 3.0.22 batch). No safe nuget.org target exists. Options per the audit:
  (a) Drop the package and bundle official libvlc 3.0.23 macOS dylibs+plugins in the CI .app packaging step (real fix; CI work + a mac smoke test);
  (b) Hard-require VLC.app on macOS — VlcAudioPlayer already prefers /Applications/VLC.app dylibs when present (VlcAudioPlayer.cs:308-327), so this is mostly removing the pin + a user-facing "install VLC" error path.
Note: the L25 CI change (-warnaserror:NU1603) will turn the macOS leg red on the next push precisely because of this pin — that's the intended tripwire, not a regression.

## Round 3 (2026-11-09) — full sweep + dependabot backlog (branch `chore/1109-deps` from main 9c15c6b8)

Target release: 1.7.0. Order followed: security → patch → minor → majors. Full solution build (incl. Android, `--no-incremental`) after each group, compared line by line against the 9c15c6b8 baseline (1047 warning lines; ends at 1046: the NU1510 line gone, nothing new). Full suite after each group (baseline 9899 pass / 2 known-flaky fail / 94 skip; final in the last line of this section). `dotnet list package --vulnerable --include-transitive`: clean for all 18 projects, before and after.

### Security

- VideoLAN.LibVLC.Windows | 3.0.23.1 → 3.0.24 | cc376359 | VLC 3.0.24 is VideoLAN's security release (VideoLAN-SB-VLC-3024: 50+ fixes in VLC, 74+ in bundled libs; FFmpeg 4.4 → 8.1.2 inside libvlc). AUDIO HAND-TEST (Windows): play FLAC, MP3 (gapless album: no gap/click at boundaries), ALAC/AAC, a hi-res 96k/192k file, a 5.1 FLAC (each channel in its speaker), a DSD file; seek a lot, next/prev quickly; EQ + normalization + crossfade on; switch output device. First launch after the update rebuilds plugins.dat (VlcPluginCache); the 2nd launch should idle with ~3 plugin modules again.
- macOS bundled VLC dylibs (CI) | 3.0.23 → 3.0.24 universal dmg | cc376359 | URL + SHA-256 2c8e89f7…beb3c (matches download.videolan.org's .sha256; re-hashed locally; the dmg still has VLC.app/Contents/MacOS/{lib,plugins}). VERIFY: next CI macOS legs package the dmg; mac smoke test on a machine without VLC.app (shorter version of the battery above).
- website: astro 7.3.5 → 7.4.8, sharp 0.35.4 → 0.35.5, `npm audit fix` | 0222b3a1 | Clears 5 high advisories (sharp/librsvg CVE-2026-96889, source-map-js, undici, vite, zod). `npm run build:fast` output identical apart from asset hashes/whitespace. HAND-TEST: after deploy, open home, downloads and changelog pages.

### Patch

- Avalonia / .Desktop / .HarfBuzz / .Themes.Fluent / .Fonts.Inter / .Android / .Skia (Kawarp) / .Headless.XUnit | 12.1.3 → 12.1.6 | 831f42a1 (+ 3cbd6151, 50732e9e) | Fixes only. Follow-ups: 12.1.5 gave TreeViewItemAutomationPeer its own ExpandCollapse members, so our FoldingTreeView peer's are marked `new` (3cbd6151, behaviour unchanged); 12.1.5 fixed the dispatcher/loop clock gap upstream (#22496) and removed the field our phone workaround patched, so DispatcherClock is deleted (50732e9e). HAND-TEST: desktop smoke (menus, flyouts, popups, ComboBoxes, text boxes, Folders tree expand/collapse incl. with Narrator, mini player). PHONE (UNVERIFIED on a device): idle on Home after a scan with animations finished — the UI thread must not spin (the 10-14 bug); timers/animations still on time.
- LibVLCSharp | 3.10.1 → 3.10.2 | fffdd009 | Changelog unreachable (code.videolan.org blocks automated fetches); covered by the audio battery above.
- Tmds.DBus.Protocol | 0.95.1 → 0.95.2 | 0379c1ce (+ 93c1db2a, b12ae157) | Fd-handling fix; `Notification.Exception` renamed `CompletionReason`. Following the rename exposed LinuxTrayHost reading `.Exception` on ordinary signals (it throws unless IsCompletion), fixed in b12ae157. LINUX HAND-TEST: MPRIS media keys, sleep/resume handling, tray icon appears/disappears when the tray host (e.g. a panel) restarts.
- SQLitePCLRaw.bundle_e_sqlite3 | 2.1.13 → 2.1.14 | cadb57d5 | Native SQLite 3.53.3 → 3.54.0 (read with sqlite3_libversion). Superseded by the 3.0.5 row below.
- AndroidX Media3 ExoPlayer/Session 1.11.0 → 1.11.1, Core.Ktx 1.19.0.1 → 1.19.1 | 502e522c | PHONE HAND-TEST: playback, lock-screen/notification controls, Bluetooth next/prev.

### Minor

- Avalonia.Labs.Lottie | 12.0.2 → 12.1.0 | e086127c | Now on stable SkiaSharp/Skottie 3.119.4. HAND-TEST: every Lottie animation.
- Microsoft.NET.Test.Sdk | 18.10.1 → 18.11.0 | 615104f3 | Test-time only.
- AndroidX Core.SplashScreen | 1.0.1.15 → 1.2.0.3 | 76d7008a | PHONE HAND-TEST: cold start shows the splash then the app, no white flash.
- GitHub Actions, all SHA-pinned | checkout 4.4.0 → 7.0.1 (d8c3c690), setup-dotnet 4.3.1 → 6.1.0 (c82e57cd), upload-artifact 4.6.2 → 7.0.2 + download-artifact 4.3.0 → 8.0.2 (22dbd043), setup-node 4.4.0 → 7.1.0 (598fb958), cloudflare/wrangler-action 4.0.0 → 4.1.3 (ad28be5c) | The majors are node24/ESM moves. Checked against our use: no pull_request_target/workflow_run (checkout v7's fork block n/a); every git push runs on the host (checkout v6's separate credentials file works with host git); artifacts are named, zipped multi-file uploads downloaded by name or pattern + merge-multiple (download v8 now errors on a digest mismatch, which we want); setup-node keeps explicit `cache: npm` and package.json has no packageManager field. UNVERIFIED until CI runs: dotnet.yml (push/PR), deploy-website.yml (release/push), package-managers.yml (release), plugin-submission.yml (issue events).

### Majors landed (build clean, suite green, no migration work)

- Microsoft.Data.Sqlite (+ .Core in the Mixxx plugin) | 8.0.31 → 10.0.14 | 361730b7 | .NET 8 support ends 2026-11-10. The three 10.0 breaking changes are DateTimeOffset/GetDateTime time-zone handling; no SQLite store reads GetDateTime/GetDateTimeOffset or writes DateTimeOffset (dates are ISO strings/longs). HAND-TEST: library loads; play counts/ratings/favorites/last-played survive a restart; Settings → Rebuild Index; phone sync; Mixxx import.
- System.Security.Cryptography.ProtectedData | 8.0.0 → 10.0.14 | 3d836580 | HAND-TEST: Last.fm/ListenBrainz still signed in after a restart (DPAPI tokens decrypt).
- Microsoft.Extensions.DependencyInjection 8.0.1 | REMOVED | 4ebfa0a3 | NU1510: the AspNetCore.App framework reference already supplies the 10.0.x copy. HAND-TEST: app starts.
- YamlDotNet | 15.3.0 → 18.1.0 | 41410d6a | Breaking changes are in extension APIs we don't use; 18.1 adds a default max recursion depth (hardening for untrusted .lyricsfile sidecars). HAND-TEST: a track with a .lyricsfile shows word-synced lyrics.
- SQLitePCLRaw.bundle_e_sqlite3 | 2.1.14 → 3.0.5 + `SQLite` native 3.54.1 pinned | 2159b4d4 | v3 has no code changes in .core; the native library is now its own package, and bundle 3.0.5 alone would have DOWNGRADED SQLite to 3.53.4, hence the explicit pin. Verified: Windows e_sqlite3.dll reports 3.54.1; the Android APK carries libe_sqlite3.so from the same package. Same hand-tests as the Microsoft.Data.Sqlite row, plus the phone library/history.

### Reverted

- NAudio.Core / NAudio.Wasapi | 2.3.0 → 2.4.0 | 3a7d10ea, reverted in 5fa1b0ea | 2.4.0 made BufferedWaveProvider.BufferDuration read-only (CS0200 in WasapiGainOutput.CreateInputChain). Not worth reshaping the WASAPI chain for a minor we don't need.

### Held (majors that need real migration)

- xunit.v3 3.2.2 → 4.0.3 / xunit.runner.visualstudio 3.1.5 → 4.0.1 — tried in a scratch build: needs `[assembly: Parallelization(Mode = None)]` instead of CollectionBehavior; defaults to MTP v2 (`dotnet test` refuses the VSTest target on the .NET 10 SDK, so CI's trx logger/filter calls need the MTP form or the xunit.v3.mtp-off package); and even with mtp-off every [AvaloniaFact] fails discovery because Avalonia.Headless.XUnit 12.1.6 is built on xunit.v3.extensibility.core 3.2.2 (MissingMethodException in TestIntrospectionHelper). Blocked until Avalonia ships a Headless.XUnit for xunit 4.
- NAudio 3.x — Span-based ISampleProvider/IWaveProvider Read signature across ~27 audio files (WASAPI gapless chain): a real audio migration.
- SkiaSharp 3.119.4 → 4.155.0 — Avalonia.Skia 12.1.6 depends on SkiaSharp 3.119.4; not before Avalonia moves.
- DiscordRichPresence 1.143.0 is unlisted on nuget.org; 1.6.1.70 stays.
- Linux AppImage libvlc comes from Ubuntu 22.04's distro packages (not pinned here); 3.0.24 reaches Linux only via Ubuntu.

### Dependabot PRs

- #140 (Sqlite/ProtectedData 10.0.13; failed NU1605 because only Noctis.Core moved) — superseded by 361730b7 + 3d836580; close.
- #120 (Avalonia 12.1.4 in Abstractions/UI/Kawarp only; failed NU1605) — superseded by 831f42a1; close.
- #48 (LibVLCSharp(.Avalonia) 3.10.0 → 3.10.1) — stale (main already on 3.10.1, .Avalonia no longer referenced), superseded by fffdd009; close.
- #20 (SQLitePCLRaw 3.0.4 in the app only; CI logs expired) — superseded by 2159b4d4; close.
- #43 / #15 / #14 / #13 / #12 (setup-node, setup-dotnet, upload-artifact, checkout, download-artifact) — superseded by the SHA-pinned commits above at newer versions; close once this branch is merged.

### Plugin note

Avalonia's and Microsoft.Data.Sqlite's assembly versions carry the patch number (12.1.6.0, 10.0.14.0). Plugins use the host's copy, so a plugin rebuilt from this branch references the new versions: set its minAppVersion to the release that ships this branch (next app version: 1.7.0) before publishing it. UNVERIFIED whether an older host refuses such a plugin (expected: the default load context won't bind a higher requested version than it has).

Final (2026-11-11, HEAD after f28102d3): full solution build 0 errors, 1046 warning lines (baseline 1047; only NU1510 gone). Full suite 9896 pass / 5 fail / 94 skip; the 5 (BlurredBackdropTests, timing) pass when re-run alone (7/0/2) and passed in every earlier group run. The previous final run hit TopBarCornerOrderTests failing by test order (fixed in f28102d3: the class now installs AccessibleNames itself) plus 3 timing tests that passed alone.
