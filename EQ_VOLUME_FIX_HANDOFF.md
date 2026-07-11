# EQ / Volume Fix — Session Handoff

> Working note for continuing across sessions. **Dev-only, not for release** — delete before cutting a release. Tag this file with `@EQ_VOLUME_FIX_HANDOFF.md` in a new session to resume.

Branch: `cross-platform`  ·  App: Noctis (Avalonia + LibVLCSharp)  ·  Date started: 2026-06-27

---

## The bug (from Discord)

Two users on Windows reported v1.2.2 plays **much quieter** than earlier versions:
- **Tizian:** every song, both .flac/.mp3, default settings, v1.1.16 & v1.2.1 both louder than v1.2.2.
- **veil:** v1.2.2 much lower than before; same file in **other players (Apple Music, etc.) is louder**.

## Root cause (confirmed)

The equalizer is **always on** (default `EqualizerEnabled = true`, default preset = "Flat") and **there is no "Off" option in the UI** — the only choices are presets + Custom.

- VLC's "Flat" preset bakes in a **+12 dB preamp**. Every version up to v1.2.1 ran the default audio +12 dB hot (loud, but clipping-prone on hot masters).
- v1.2.2 added `if (vlcPresetIndex == 0) preamp = 0f;` in `SettingsViewModel.TryGetVlcPresetCurve` (~line 1086), zeroing that preamp.
- BUT the audio still routed through VLC's **enabled-but-flat** equalizer, which sits **slightly below native signal level**. Net: v1.2.2 ended up quieter than other players (which play at native level).

So the +12 dB was too hot; 0 dB *through the EQ filter* is too quiet; **true bypass (no EQ filter) is the correct native level.**

## Fix applied (Stage 1) — DONE & CONFIRMED

File: `src/Noctis/Services/VlcAudioPlayer.cs`, method `ApplyAdvancedEqualizerSnapshot` (~line 1053).

When the resolved curve is flat (all 10 bands `< 0.05` dB **and** preamp `< 0.05` dB), take the existing **bypass branch** (`SetEqualizer(null)` + dispose `_equalizer`) instead of applying a flat equalizer. Added an `isFlat` check and changed `if (enabled)` → `if (enabled && !isFlat)`.

- Builds clean (0 warnings/errors).
- **User-verified by ear: Noctis now matches Apple Music at full volume.** ✅
- Change is **uncommitted** in the working tree.

Why bypass works: the code already had a clean bypass path (used when EQ "disabled"). The UI just never triggered it. Other apply sites (`ApplyAdvancedEqualizerSnapshot`, play path ~1808, crossfade swap ~2118, standby prep ~1542) all guard `_equalizer != null` before applying, so when flat → `_equalizer == null` → they correctly skip → bypass preserved.

---

## Remaining work

### Stage 2 — keep the bypass across crossfade / AutoMix transitions — DONE (uncommitted), awaiting by-ear

The **AutoMix cleanup** (`VlcAudioPlayer.cs` ~2373) force-applies a flat equalizer (`new Equalizer()`) to the just-stopped inactive player to clear any leftover curve before reuse — but a flat VLC equalizer **attenuates** (same root cause). The two **swap paths** then only re-applied EQ `if (eqToApply != null)`, so when the curve is flat they never cleared that leftover `flatEq`. When that player became active on the **2nd** transition → it kept the attenuating `flatEq` → quietness returned.

Fix applied: at both swap paths, always set the now-active (playing) player's equalizer to the resolved state, calling `SetEqualizer(null)` in the flat/bypass case to clear the leftover `flatEq`:
- Crossfade swap — `Crossfade.SeqSwap` (~2130).
- Overlap swap — `Crossfade.OverlapSwap` (~2272).

Used `if (eqToApply != null) … else SetEqualizer(null)` (literal null) to match the existing bypass branch (~1113) and avoid a CS8604 nullable warning — the `MediaPlayer.SetEqualizer` overload param is non-nullable. Builds clean (0 warnings).

Scope rationale: the leftover `flatEq` only ever lands on a player via AutoMix cleanup, and that player sits as **standby** until a swap reactivates it — so only the swap paths needed the clear. The plain play path (~1816) and standby prep (~1549) were left unchanged (the active player never carries a cleanup `flatEq`; the standby may not be playing → `SetEqualizer(null)` could NRE there). The cleanup's `flatEq` (~2373) was left as-is (it's the NRE-safe way to neutralize the stopped player; the swap now clears it on reuse).

Verify by ear with **Song Transitions on** (Crossfade 6s + Gapless): play across **3+** track changes with the default Flat EQ and confirm it stays at native level (no step-down after the 2nd transition).

### UPDATE 2026-07-03 — `SetEqualizer(null)` always throws NRE; caused a runaway memory explosion (FIXED)

The bypass pattern this doc recommended (`SetEqualizer(null)`, line 49 above) **never worked**: LibVLCSharp dereferences the argument unconditionally, so the literal-null call throws `NullReferenceException` on every invocation. Because Stage 1 made the default Flat preset take the bypass branch, every launch hit the NRE inside `ApplyAdvancedEqualizerSnapshot`, and `ProcessAdvancedEqualizerQueue`'s finally-requeue then respawned the faulted task in a tight loop — ~10K faulted tasks/s, heap ballooning to 18+ GB, 2.2 GB crash.log of identical NREs (evidence: gcdump showed ~870K retained `NullReferenceException`s / ~862K faulted Tasks; crash.log stack pointed at line 1115).

Fixed 2026-07-03: all 5 `SetEqualizer(null)` sites replaced with **`UnsetEqualizer()`** (the correct LibVLCSharp API — bypass branch ×2, SeqSwap, OverlapSwap, ReleasePreparedNext), and the apply queue now counts a throwing apply as consumed (logs via DebugLogger instead of respawning). Note: v1.2.2 has the same NRE in its disabled-EQ branch — released users who turn the EQ off get the same storm.

### UPDATE 2026-07-06 — Stage 3: custom-EQ preamp make-up (root cause CONFIRMED from VLC source)

Verified against VLC 3.0.x `modules/audio_filter/equalizer.c`: the filter scales its
input by `EQZ_IN_FACTOR = 0.25` (**−12 dB**) and the preamp is the make-up —
`out = 10^(preamp/20) · (0.25·x + band_terms)`. So:
- Flat bands + preamp 0 = **−12 dB below native** (not "slightly below" as assumed
  earlier — this was the true magnitude of the v1.2.2 quietness).
- Flat bands + preamp 12 ≈ unity (VLC's own presets bake ~+12 into their table as
  make-up, NOT as a boost — the old "+12 dB hot" framing above was wrong;
  pre-v1.2.2 played ≈ native).
- The Custom parametric path passed **preamp 0** → engaging ANY custom band dropped
  the whole signal ~12 dB (user report: all-zero = normal loudness, move one band →
  everything gets quieter; Melon compensating "+5 dB on every band" on Discord).

Fix (2026-07-06, uncommitted): `SettingsViewModel.GetGraphicEqBands` custom path now
returns `ParametricEqMath.VlcEqUnityPreampDb` (12.0412 = 20·log10(4), exact unity)
instead of 0f. Guarded by `ParametricEqMathTests.VlcEqUnityPreamp_CancelsVlcInputFactor`.
Named presets keep their own VLC preamps (already unity make-up); Flat preset still
forces preamp 0 → registers flat → true bypass.

Side effect (intended): an all-zero **Custom** curve no longer bypasses (preamp is
non-zero) — it runs the EQ at exact unity. That means drags within Custom never
insert/remove the live audio filter, which is what caused the "audio cuts ~1s when
I start dragging a band" dropout. Remaining dropout cases: switching Flat preset ↔
Custom/named preset mid-play (live filter-chain rebuild, inherent to VLC).

By-ear verify: Custom all-zero == Flat == other players; then boost 16 kHz to +12 →
overall loudness must stay put (only treble rises). Also re-test Reset behavior —
with unity make-up, even a stuck live filter sits at native level, so "Reset doesn't
restore" should be gone for the custom path.

### UPDATE 2026-07-07 — EQ dropout + track-start cut: root cause CONFIRMED, fixed (committed)

User confirmed after Stage 3: level fix works, transitions fix works, but the ~1s audio
cut on EQ adjustment remained, and (separately reported) a split-second cut at the start
of the next track. Verified from VLC 3.0.x source: `libvlc_media_player_set_equalizer`
re-sets the aout's `audio-filter` var on every call, but `FilterCallback` (output.c) only
requests an output restart **when the string changes** (`strcmp`) — so value updates on an
engaged filter are seamless; the ONLY audible-cut mechanism is inserting/removing the
filter on a live player ("" ↔ "equalizer").

Two fixes (commit "stop live EQ filter removal from restarting the output"):
1. **AutoMix cleanup** (~2410) planted `new Equalizer()` on the stopped player → the next
   swap stripped it via `UnsetEqualizer` on the *just-started* incoming player → restart →
   the split-second cut at next-track start (bypass/Flat users with transitions on).
   Cleanup now calls `UnsetEqualizer()` while the player is stopped (safe post-4a144ec;
   the old NRE was LibVLCSharp null marshaling, not player state) → reused players start
   filterless → the swap's flat-case Unset is a ""→"" no-op.
2. **Apply-path hysteresis** (`ApplyAdvancedEqualizerSnapshot` else-branch): a flat/disabled
   curve landing while a track plays with a filter engaged now neutralizes it to
   unity-flat (preamp 12.0412, zero bands — exactly transparent: 4·(0.25·x)=x) instead of
   removing it. True removal happens only while nothing is playing (app start keeps the
   pure bypass for default users). Kills the Reset-/Flat-switch-while-playing dropout.

Remaining inherent cut (libvlc limitation, not fixable): the FIRST engage of the filter
mid-play when playback started fully bypassed (e.g. default Flat preset → user switches
to Custom/named preset while playing) — the filter must be inserted → one restart.

Also explained (NOT a bug): at full volume with a big band boost (+12 dB @16k), loud
passages exceed 0 dBFS and Windows' shared-mode limiter (CAudioLimiter) ducks briefly.
That's the OS preventing clipping; the alternative (auto-headroom preamp) is exactly the
"boost makes everything quieter" behavior Stage 3 removed. Leave as-is.

### New bug — live EQ manipulation goes silent then comes back quieter; Reset doesn't restore (LIKELY EXPLAINED by the NRE above — RE-TEST)

Because the unset call always threw, Reset→flat→bypass **never actually removed the live equalizer** — the non-flat curve stayed applied, which matches "stays quiet until restart" exactly. With `UnsetEqualizer()` in place, re-test by ear before investigating further.

### Original notes (pre-2026-07-03) — NEEDS INVESTIGATION

Reported after Stage 1:
1. Dragging a band / changing preset → audio **goes silent ~1-2s**, returns **quieter**.
2. Clicking **Reset** (→ flat → bypass) **does not restore** the original native level — stays quiet until (presumably) restart.

Hypotheses to check:
- `SetEqualizer(...)` / `SetEqualizer(null)` on a **live** stream forces VLC to rebuild the audio filter chain (the 1-2s silence) and may not restore native level mid-stream.
- The non-flat → flat transition now goes through `SetEqualizer(null)` (the new bypass branch) on a playing player — verify it actually removes the filter and returns to native level live.
- On Windows, volume is the **OS audio session level** (`_sessionVolume` / `WindowsSessionVolume`, `--aout=mmdevice`), separate from the EQ. A VLC aout rebuild on filter change might desync the session level / volume ramp (`_rampCurrentMilli`). Check whether the session level is being reset/halved.

First investigation step: determine if "Reset doesn't restore" reproduces **with Stage 1 reverted** (to know if Stage 1 introduced it or it's pre-existing and was masked by the old +12 dB). Use `NOCTIS_VLC_LOG=1` (writes a Desktop log) to trace volume/EQ apply order. See memory note "audio_session_volume_gotcha".

**Cheap by-ear discriminator (run this first — one playthrough):** drag a band so it goes quiet, then **skip to the next track WITHOUT clicking Reset**.
- Next track plays at **native** level → the EQ filter is only stuck on the *live* stream; VLC's mid-stream `SetEqualizer(null)` doesn't fully tear down the inserted filter until the aout reopens (next track). Fix direction: on a live flat/Reset transition, force a clean aout/filter-chain rebuild (heavier) — or accept that Reset lands on the next track.
- Next track is **still quiet** → it persists across tracks, i.e. session-level or leftover-EQ, not a live-only filter. Re-test after **Stage 2** first: if the user's repro involved a crossfade transition, Stage 2 may already cover the cross-track part, leaving only the live-stream case above.

Note on direction: the "reopen leaves the OS session at 100%" invariant (VlcAudioPlayer.cs ~2519) predicts an EQ-rebuild would make it *louder*, not quieter — so a pure session-desync is an unlikely sole cause of "quieter," pointing more at the live-filter-removal limitation. Re-asserting the session after a live `SetEqualizer` is still a correct defensive add, but confirm the mechanism with the discriminator/log before coding it.

---

## Key files & references

- `src/Noctis/Services/VlcAudioPlayer.cs`
  - `ApplyAdvancedEqualizerSnapshot` (~1053) — Stage 1 fix lives here.
  - `SetAdvancedEqualizer` (~1006) — public entry from settings.
  - AutoMix cleanup `inactivePlayer.SetEqualizer(flatEq)` (~2370) — Stage 2 target.
  - Play path EQ apply (~1808), crossfade swap (~2118), standby prep (~1542) — all guard `!= null`.
  - Volume math: `ApplyVolumeCurve`, `CurvedVolumeToLevelMilli`, `MilliToPlayerVolume`, `ApplyRampLevel` (~797). Windows volume = OS session via `_sessionVolume`.
- `src/Noctis/ViewModels/SettingsViewModel.cs`
  - `TryGetVlcPresetCurve` (~1072) — the `preamp = 0f` for Flat (~1086).
  - `GetGraphicEqBands` (~1043), `ApplyAudioSettings` (~985) → calls `SetAdvancedEqualizer(EqualizerEnabled, bands, preamp)`.
  - Preset list `EqPresetNames` (~371); load maps `EqualizerPresetIndex + 1` → `SelectedEqPresetIndex` (~782). Index 0 = Custom, 1 = Flat.
- `src/Noctis/Models/AppSettings.cs`
  - `EqualizerEnabled = true` (184), `EqualizerPresetIndex = 0` (187) — default = EQ on + Flat.
  - `ReplayGainMode = "Auto"` (276) — RG **on by default**; Auto turns down loud *tagged* tracks (ruled out for these reports: untagged test files = no-op).

## Build / test

- Build: `dotnet build src/Noctis/Noctis.csproj -v minimal` (build to a scratch `-o` dir if the app is running — it locks `bin/Debug`).
- Tests: `dotnet test tests/Noctis.Tests/Noctis.Tests.csproj -v minimal`.
- **User launches & verifies the app by ear themselves** (don't offer to run it). Verification = same track + same Windows volume, Noctis vs Apple Music (Sound Check off), ReplayGain off.
- Do **not** use `dotnet build -t:Compile` (skips XAML-IL → runtime crash).

## Do / don't

- Do NOT reintroduce a blanket preamp boost (e.g. restoring +12 dB) — it clips hot masters. Bypass-when-flat is the correct unity approach.
- Keep crossfade/AutoMix coherence intact (see `.claude/rules/audio.md`); don't call Play/Stop from VLC event handlers; preserve `_playbackLock`, seek throttling, end-of-track grace.
- Minimal diffs.
