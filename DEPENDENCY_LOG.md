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
