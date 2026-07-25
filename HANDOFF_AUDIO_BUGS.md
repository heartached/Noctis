# Handoff: 2 open audio bugs (updated 2026-07-16, mac session)

## State of the branches (NOT pushed)

- `cross-platform`: 8 unpushed commits, latest two from the mac session:
  - `ca91f35` fix(ui): wrap dev session log lines instead of horizontal scrolling
  - `ee28209` feat(logs): mirror VLC warnings/errors into the dev session log (Copy Logs now captures audio-engine warnings while Developer Mode is on — no more NOCTIS_VLC_LOG env-var dance for users)
- `main`: same two changes cherry-picked (`4014764`, `5dbdbb5`), build-verified.
- Next step: push both branches from the Windows PC and cut the release for shan.
- This clone has `core.autocrlf=input` set locally (silences the CRLF churn from moving between machines — leave it).
- macOS test baseline is 468/476: the 8 failures are `FolderTreeBuilderTests`/`FileOrganizePlannerTests` hardcoding `C:\` paths. Pre-existing, worth fixing on this branch. Windows baseline 476/476.

## Bug 1: macOS audio corruption (shan, Apple Silicon) — NOT reproducible locally

Tried on the user's Apple Silicon MacBook with keep-alive forced ON (`NOCTIS_KEEPALIVE=1`) + VLC logging: **no corruption on built-in speakers, Bluetooth AirPods, wired headphones, or with a 48 kHz Opus file**. VLC diag log spotless (no underruns/errors across 10 cover-decoder sessions, seeks, track changes). The keep-alive-as-root-cause theory is unconfirmed; the bug is machine-specific to shan.

New evidence from shan (Discord, 07-16 evening):
- Happens on their **speakers AND Bluetooth** (so not device-specific on their end).
- **Screen capture does NOT contain the distortion** while the speakers audibly distort → corruption is injected downstream of the app's audio stream, at the CoreAudio/device level. Consistent with a second stream interfering, still compatible with keep-alive theory.
- Pattern repeats **every second** (matches the 1 s silent-WAV loop).
- Track was **Opus** (Evanescence album badge) — codec ruled out locally, user's Opus test was clean.

Questions sent to shan (awaiting answers):
1. VLC.app version. 2. macOS version + Mac model. 3. **Play the same song in VLC.app directly — distorts too or not?** (the key isolation test). 4. EQ/normalization/crossfade on? 5. Distortion from first second or only after a while?

Plan: release the update (keep-alive off by default on macOS, 8d98fcf), have shan test. If fixed, done. If not: shan enables dev mode, reproduces, pastes Copy Logs (the new bridge captures VLC warnings/errors in-app).

## Bug 2: rare ~1s dropout on Windows 11 (user's machine) — unchanged

Leading suspect: VLC mmdevice "playback too late → flushing" (documented ~line 391 `NOCTIS_AOUT` comment in VlcAudioPlayer.cs). Evidence first: run with dev mode on (the new VLC log bridge captures the warnings now) or `NOCTIS_VLC_LOG=1`, note dropout timestamps, look for flush/late/underrun lines. Ask: Bluetooth or wired? A/B with `NOCTIS_AOUT=directsound` if confirmed.

Note: user confirmed the known EQ-change dropout on macOS too (~1-2 s cut when nudging an EQ band mid-playback = aout rebuild, documented ~line 1142). Same mechanism as the Bug 2 suspect.

## Mac machine state (left clean)

- `launchctl` env vars unset, Desktop zip and Opus test file deleted.
- `/Applications/Noctis.app` contains a current self-contained arm64 build of cross-platform (ad-hoc signed). Refresh recipe: `dotnet publish -c Release -r osx-arm64 --self-contained`, replace `Contents/MacOS/*`, `codesign --force --deep -s - /Applications/Noctis.app`.
- Repo + NuGet cache had `com.apple.quarantine` stripped (Gatekeeper was blocking native dylibs). Re-apply `xattr -dr com.apple.quarantine` if files get re-copied to a Mac.

**Verification commands**:
- `dotnet build src/Noctis/Noctis.csproj -v minimal`
- `dotnet test tests/Noctis.Tests/Noctis.Tests.csproj -v minimal`
