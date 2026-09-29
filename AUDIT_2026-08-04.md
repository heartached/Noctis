# Noctis Audit — Phase 1 (read-only investigation)

Date: 2026-08-04. Scope: `src/Noctis` (+ `tests/`, `tools/NoctisCoverProxy` where noted) at the current working tree; no files were modified.

Method: 53 subagents — 14 domain/settings/cross-platform finders and 4 dependency researchers (web-cited), followed by adversarial verification of every code finding (a verifier re-opened every cited line and actively tried to refute the claim). Raw tally: 67 confirmed, 1 refuted (dropped, listed in Appendix C), 2 uncertain (kept, downgraded to unverified). Duplicate reports of the same defect by independent auditors were merged (noted inline). Dependency-table version/CVE rows are web-research citations, not adversarially re-verified.

Findings: **71** — 0 critical, 8 high, 26 medium, 37 low.

## Summary of findings

| ID | Title | Severity | Confidence | Area |
|---|---|---|---|---|
| H1 | Cancelled crossfade/AutoMix transition strands volume state: slider goes dead and the next seek mutes the OS session | high | confirmed | Audio pipeline |
| H2 | Windows single-player crossfade fallback fades the incoming track up to 100% session volume (full blast), ignoring the user's level until a post-fade snap-down | high | likely | Audio pipeline |
| H3 | LRC editor Save copies and rewrites the entire audio file synchronously on the UI thread | high | confirmed | Perf / UI thread |
| H4 | Queue page defeats ListBox virtualization: entire UpNext queue realized at once | high | confirmed | Perf / render |
| H5 | PlaylistViewModel.LoadTracks does full-library work synchronously on the UI thread (smart-playlist eval per LibraryUpdated, per-track regex suggestions, per-item ObservableCollection adds) | high | likely | Large-library scaling |
| H6 | Hardcoded Last.fm API key and shared secret in source | high | confirmed | Security / network |
| H7 | Bundled macOS libvlc (VideoLAN.LibVLC.Mac 3.0.21) unlikely to serve Apple Silicon without VLC.app installed — playback fails on a fresh arm64 install | high | unverified | Cross-platform |
| H8 | VideoLAN.LibVLC.Mac pins a version that does not exist on nuget.org; macOS builds silently restore a 2019-era libvlc missing 7 years of security fixes | high | confirmed | Dependencies |
| M1 | Outgoing track's natural EndReached during a crossfade fade-out is stamped with the NEW session id — TrackEnded fires mid-transition and double-advances the queue (a track gets skipped) | medium | likely | Audio pipeline |
| M2 | Pause() bypasses the playback lock and ThreadPool serialization — a pause landing during a track change or transition swap is silently overridden (audio keeps playing while UI shows paused) | medium | likely | Audio pipeline |
| M3 | PrepareNext holds _playbackLock across a non-cancellable 8-second media parse — Play/Resume/Stop (and thus the audible track change) queue behind it | medium | confirmed | Audio pipeline |
| M4 | Remote (media-server) streams can never use gapless/crossfade — every track boundary pays the fixed 1.2 s EndReached grace plus a network parse of the next track | medium | confirmed | Audio pipeline |
| M5 | ArtworkPathConverter does synchronous disk read + JPEG decode on the UI thread; used in Home and Albums item templates | medium | confirmed | Perf / UI thread |
| M6 | Metadata editor constructor performs two TagLib file parses, sidecar reads, and a full-resolution artwork decode on the UI thread | medium | confirmed | Perf / UI thread |
| M7 | RemoveLyrics blocks the UI thread with .Wait() on file deletes plus an OS trash operation (child process with 15s timeout on macOS/Linux) | medium | confirmed | Perf / UI thread |
| M8 | EqVisualizer: 60fps DispatcherTimer animates layout Height, keyed to GLOBAL play state, keeps running on hidden rows | medium | likely | Perf / render |
| M9 | ServerView albums grid unvirtualized; load-more pages accumulate realized 512px-image tiles | medium | confirmed | Perf / render |
| M10 | Ungated INFINITE loading-spinner animations run while hidden; MainWindow instance runs for app lifetime | medium | likely | Perf / render |
| M11 | AlbumDetailView _bgHandler is never re-wired on in-place VM swap — old AlbumDetailViewModel in navigation history roots the discarded view | medium | confirmed | Leaks & timers |
| M12 | Album art is persisted at original resolution with no downscale — unbounded artwork directory and maximum-cost decodes; extractor deliberately picks the largest embedded payload | medium | confirmed | Large-library scaling |
| M13 | Albums-view search re-normalizes (Unicode FormD fold) every track title and artist on each keystroke instead of using the cached per-track search keys | medium | confirmed | Large-library scaling |
| M14 | Command palette Refresh scans all tracks/albums/artists synchronously on the dispatcher thread, allocating a PaletteItem and doing a resource lookup per match | medium | likely | Large-library scaling |
| M15 | Playlist import fuzzy matching is O(entries x library) full Levenshtein per pair — the length-based early-out its comment claims is not implemented, and cancellation is never checked | medium | likely | Large-library scaling |
| M16 | SQLite tracks mirror is write-only (nothing ever SELECTs it) yet is fully rewritten row-by-row after every scan; startup still parses the entire library JSON | medium | confirmed | Large-library scaling |
| M17 | Track.PrimaryArtist is an uncached Regex.Split+LINQ property evaluated once per track in loops that repeat every 1.5 s during scans | medium | confirmed | Large-library scaling |
| M18 | 'Analyze Tempo & Key' toggle is inert when switched on mid-session — analysis only starts on the next LibraryUpdated event | medium | confirmed | Settings (Audio/Library) |
| M19 | Clear Artwork Cache and Reset Everything perform synchronous recursive directory deletes on the UI thread | medium | likely | Settings (Audio/Library) |
| M20 | Scan completion walks the entire artwork cache synchronously on the UI thread (Scan Library / Add Folder / Remove Folder / Rebuild Index) | medium | likely | Settings (Audio/Library) |
| M21 | ListenBrainz Logout leaves the hidden scrobbling flag armed - typing a token afterwards silently scrobbles with an unvalidated token while the UI shows disconnected | medium | confirmed | Settings (Stats/Integrations/About) |
| M22 | Case-insensitive path matching on Linux: watcher removals and exclusion sets can hit tracks in a case-differing sibling path | medium | confirmed | Cross-platform |
| M23 | macOS 'Move to Trash' drives Finder via osascript but the app bundle declares no NSAppleEventsUsageDescription — TCC may deny it and every trash attempt fails | medium | unverified | Cross-platform |
| M24 | macOS has no media-key / Now Playing integration at all (SMTC is Windows-only, MPRIS is Linux-only, nothing fills the gap) | medium | confirmed | Cross-platform |
| M25 | TagLibSharp 2.3.0 has an upstream report of corrupting MP4/M4V files when writing tags, and the app's tag editor writes through exactly this path | medium | unverified | Dependencies |
| M26 | .NET 8 LTS support ends 2026-11-10 (~3 months away); all three projects target net8.0 | medium | confirmed | Dependencies |
| L1 | Metadata Save path calls RecycleBin.TryMoveToTrash inline on the UI thread when lyrics were removed | low | confirmed | Perf / UI thread |
| L2 | LibraryPlaylistsView realizes every playlist tile (unvirtualized UniformGrid in ScrollViewer) | low | confirmed | Perf / render |
| L3 | Per-row LayoutUpdated handlers run LINQ on every layout pass in Songs/Playlist/AddSongs row templates | low | likely | Perf / render |
| L4 | LyricsView subscribes three different, inconsistent event sets across OnDataContextChanged / OnAttachedToVisualTree / OnDetachedFromVisualTree — Player.PropertyChanged is missing for the whole first visit and LyricsSwapPending/Swapped are never removed on detach | low | likely | Leaks & timers |
| L5 | LyricsViewModel 100ms sync timer restarts and runs for entire playback sessions while no lyrics surface is visible, defeating its own visibility gate | low | confirmed | Leaks & timers |
| L6 | HomeViewModel is the only library view without IsActive gating — full-library rebuilds run on every LibraryUpdated/FavoritesChanged even while Home is hidden | low | confirmed | Large-library scaling |
| L7 | Single heart-click broadcasts favorite-state PropertyChanged to every album in the library (8 call sites use the parameterless NotifyFavoritesChanged despite the targeted overload existing) | low | confirmed | Large-library scaling |
| L8 | 'System' theme resolves the OS light/dark mode once and never tracks OS theme changes while the app runs | low | confirmed | Settings (General/Appearance) |
| L9 | Avatar picker copies the chosen image synchronously on the UI thread | low | confirmed | Settings (General/Appearance) |
| L10 | Profile settings are persisted but consumed nowhere outside the Settings card; ProfileUsername has a full persistence path with no UI at all | low | confirmed | Settings (General/Appearance) |
| L11 | Seven marquee toggles (Cover Flow, Lyrics page, Mini Player) do not take effect when turned ON until the text or layout next changes | low | confirmed | Settings (General/Appearance) |
| L12 | 'Save analysis to file tags' never writes tags for tracks that were already analyzed before it was enabled | low | confirmed | Settings (Audio/Library) |
| L13 | Typed-but-never-validated ListenBrainz token is persisted by any unrelated settings save and re-armed into the service at next startup, contradicting the handler's persist-on-Connect contract | low | confirmed | Settings (Stats/Integrations/About) |
| L14 | Apple media-host allowlist is a substring match on the whole URL, and HLS playlist part URLs are never host-checked | low | confirmed | Security / network |
| L15 | crash.log writes raw exception text, bypassing LogRedaction | low | confirmed | Security / network |
| L16 | iTunes JSON responses parsed from the network stream without the HttpSafety byte cap | low | confirmed | Security / network |
| L17 | NoctisCoverProxy: unauthenticated publish of arbitrary bytes served as image/jpeg, unbounded aggregate memory, and an unused per-connection secret | low | confirmed | Security / network |
| L18 | Scrobbler tokens and media-server passwords stored in plaintext at rest on macOS/Linux (DPAPI is Windows-only) | low | confirmed | Security / network |
| L19 | Web remote runs over cleartext HTTP with the bearer token in the URL query | low | confirmed | Security / network |
| L20 | File organizer sanitization misses Windows reserved device names (CON, NUL, COM1…) | low | confirmed | Security / files |
| L21 | Linux xdg-open invocations use the argument-string overload — paths with spaces split into multiple arguments | low | likely | Security / files |
| L22 | M3U export writes raw tag metadata — newline in a Title/Artist tag injects arbitrary playlist entries | low | likely | Security / files |
| L23 | SMB media-source scan has no symlink/junction cycle guard (unbounded recursion) | low | confirmed | Security / files |
| L24 | Linux 'System' theme detection is GNOME-only — KDE and other desktops always resolve to dark | low | confirmed | Cross-platform |
| L25 | CI dependency-vulnerability audit runs only on win-x64, so the macOS-conditional native libVLC package is never audited | low | confirmed | Dependencies |
| L26 | Microsoft.Data.Sqlite 8.0.11 is 18 patch releases behind the still-supported 8.0.x line (8.0.29) | low | confirmed | Dependencies |
| L27 | xunit 2.9.3 pins a line its maintainers have deprecated (v2 is security-fix only) | low | confirmed | Dependencies |
| L28 | Converters/ArtistTokensConverter.cs is dead: declared as a XAML resource whose key is never used, superseded by the view-model building ArtistTokenItem[] directly | low | confirmed | Dead code |
| L29 | Converters/TrackPlaylistCommandParameterConverter.cs is dead: single reference is a resource declaration whose key is never used | low | confirmed | Dead code |
| L30 | Entire offline-cache subsystem is dead: IOfflineCacheService/OfflineCacheService registered in DI but never resolved, all members have zero callers | low | confirmed | Dead code |
| L31 | IAlbumArtworkSearch interface and its forwarding DI registration are dead — every consumer uses the concrete ITunesArtworkService | low | confirmed | Dead code |
| L32 | IMediaSourceConnector layer is dead weight: Local/Smb/WebDav connectors referenced only by their DI registrations, and no connector method is reachable at runtime | low | confirmed | Dead code |
| L33 | IUnifiedLibraryService/UnifiedLibraryService registered in DI but never resolved; both interface members have zero callers | low | confirmed | Dead code |
| L34 | Unused duplicate converter resource declarations: GuidEquals in PlaylistView.axaml and VolumeToIcon in LyricsView.axaml | low | confirmed | Dead code |
| L35 | Inter-ExtraBold.ttf (746 KB) embedded in every build but its FontFamily resource is never used | low | confirmed | Dead code |
| L36 | Nine StreamGeometry icon keys in Assets/Icons.axaml are never referenced anywhere | low | confirmed | Dead code |
| L37 | Unreferenced PNG assets 'Previous ICON.png' and 'Pause ICON.png' embedded in the binary | low | confirmed | Dead code |

## High findings

### [H1] Cancelled crossfade/AutoMix transition strands volume state: slider goes dead and the next seek mutes the OS session
Severity: high
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/VlcAudioPlayer.cs:1982-1992`
  ```csharp
  _transitionInFlight = true;
  var crossfadeStarted = _sessionVolume != null
      ? (_crossfadeOverlap
          ? TryStartOverlapFade(filePath, sessionId, cancel)
          : TryStartSequentialFade(filePath, sessionId, cancel))
      : TryStartPreparedAutoMix(filePath, targetVolume, sessionId, cancel, instantHandoff: false);
  if (crossfadeStarted)
  {
      Interlocked.Exchange(ref _pendingSeekMs, -1);
      return;
  }
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:2412-2418`
  ```csharp
  FadeSessionLevelBlocking(startMilli, 0, fadeOutMs, cancel);
  if (_disposed || cancel.IsCancellationRequested)
  {
      sessionVolume.SetLevel(userMilli / 1000.0); // a new Play() cancelled us; restore + let it take over
      ReleasePreparedNext();
      return true;
  }
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:1059-1064`
  ```csharp
  if (_disposed || cancel.IsCancellationRequested)
  {
      sv.SetLevel(toMilli / 1000.0);
      Volatile.Write(ref _rampCurrentMilli, toMilli);
      return;
  }
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:637-640`
  ```csharp
  if (_disposed) return;
  _userVolume = Math.Clamp(value, 0, 100);
  if (_transitionInFlight && _currentMedia != null)
      return;
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:3470-3483`
  ```csharp
  var savedMilli = Volatile.Read(ref _rampCurrentMilli);
  if (savedMilli < 0)
      savedMilli = CurvedVolumeToLevelMilli(
          ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100))));
  ...
  sv.SetLevel(savedMilli / 4 / 1000.0);
  _player.Time = targetMs;
  Thread.Sleep(SeekFadeMs);
  if (!sv.SetLevel(savedMilli / 1000.0))
  ```
- `src/Noctis/ViewModels/PlayerViewModel.cs:349-350`
  ```csharp
  CancelAutoMixTransition("user seeked");
  _audioPlayer.Seek(target);
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:2286-2291`
  ```csharp
  if (cancel.IsCancellationRequested || _disposed)
  {
      SetPlayerVolumeGuarded(_player, finalVolume);
      ReleasePreparedNext();
      return true;
  }
  ```
  _Verifier line corrections: Two precision notes, neither verdict-changing: (1) the full-mute-after-seek variant requires the sequential path (_crossfadeOverlap == false) cancelled during its fade-out — the overlap path strands _rampCurrentMilli at blendMilli (VlcAudioPlayer.cs:2590 write, :2598 session restore), giving a partial volume drop rather than silence, and the AutoMix no-silence sequential mode has only a 150 ms fade-out window (:2404) versus up to 3-6 s for plain crossfade (:2396); the dual-fade AutoMix path (:2286-2290) runs only when _sessionVolume == null (non-Windows), so it latches the flag but cannot produce the session-mute. (2) The armed-flag quote spans 1982-1991 (cited 1982-1992), and the 'settings changed' handlers are at PlayerViewModel.cs:2246-2256. The dead-slider effect occurs on all three cancellation paths and persists until the next track change or Stop._

Why it matters: PlayInternal arms `_transitionInFlight = true` (line 1982) and only clears it on the fall-through (1995), after a completed fade (2318/2491/2637), in Stop() (3044), or at the next PlayInternal (1948). Every cancellation checkpoint inside TryStartSequentialFade (2413-2418), TryStartPreparedAutoMix (2286-2291) and TryStartOverlapFade (2596-2601) returns true WITHOUT clearing it, and PlayInternal then returns immediately. Cancellation is triggered by CancelSkipCts() via CancelPreparedNext(), which PlayerViewModel calls for 'user paused' (line 256), 'user seeked' (349), 'shuffle changed' (442), 'repeat changed' (504), 'settings changed' (2247-2256) — none of which is followed by a new Play(). Result 1: the latched flag makes the Volume/VolumeAdjust/CommitVolume/ReapplyVolume setters (637-640, 654, 687, 1648) silently swallow every write — the volume slider is dead until the next track change. Result 2 (Windows session path): the cancelled fade-out has already written `_rampCurrentMilli = 0` (1062; also its normal completion leaves 0); the checkpoint restores the session level (2415) but never restores `_rampCurrentMilli`. The next in-place seek's duck/restore reads `savedMilli = 0` (0 passes the `< 0` guard at 3471) and restores the session to 0.0 — total silence, unrecoverable via the (dead) slider until the next track change (where ScheduleSessionVolumeReassert/ReapplySessionVolume finally rewrites the level from _userVolume).

Proposed fix: On every cancellation exit that returns true from TryStartSequentialFade / TryStartOverlapFade / TryStartPreparedAutoMix: set `_transitionInFlight = false` and, on the session path, `Volatile.Write(ref _rampCurrentMilli, userMilli)` to match the SetLevel restore. Alternatively centralize in PlayInternal: after `if (crossfadeStarted)`, when `cancel.IsCancellationRequested`, clear the flag and resync the ramp baseline before returning.

Risk if we fix it: Low — the change only restores bookkeeping on paths that are already aborted; no live fade is affected. Verify pause-during-crossfade and seek-during-crossfade by ear on Windows (slider responds, no mute after seek).

---

### [H2] Windows single-player crossfade fallback fades the incoming track up to 100% session volume (full blast), ignoring the user's level until a post-fade snap-down
Severity: high
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/Services/VlcAudioPlayer.cs:1965-1972`
  ```csharp
  var canTransitionFade = _crossfadeEnabled && hadPreviousMedia && !_player.Mute &&
                          _wasapiOut == null && !_exclusiveModeEnabled && !startPaused && !isRemote;
  var fadeOutMs = canTransitionFade && _player.IsPlaying
      ? Math.Clamp(_crossfadeDurationMs / 2, 100, 6000)
      : 0;
  var fadeInMs = canTransitionFade
      ? Math.Clamp(_crossfadeDurationMs - fadeOutMs, 100, 12000)
      : 0;
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:2098-2103`
  ```csharp
  if (fadeInMs > 0)
  {
      // Single-player approximation of crossfade: fade out old track, then fade in new one.
      SetPlayerVolumeGuarded(_player, 0);
      FadePlayerVolumeBlocking(0, targetVolume, fadeInMs, cancel);
  }
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:2814-2817`
  ```csharp
  private int GetTargetVlcVolume() =>
      _sessionVolume != null || _wasapiOut != null || _exclusiveModeEnabled
          ? 100
          : ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100)));
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:2116-2119`
  ```csharp
  // Windows mmdevice: _player.Volume IS the OS session, so setting it to
  // 100 (targetVolume) opens the new track's session at full volume — the
  // "volume blips to full for ~1s on track change" bug, because the float
  // reassert below only catches up once the new session appears.
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:2162-2164`
  ```csharp
  // The new output session opens at 100% — push the user level onto it
  // as soon as it appears so there's no full-volume blip on track start.
  ScheduleSessionVolumeReassert(sessionId);
  ```
- `src/Noctis/ViewModels/SettingsViewModel.cs:1352`
  ```csharp
  _audioPlayer?.SetCrossfade(SongTransitionsEnabled && IsCrossfadeStyle, (int)Math.Round(CrossfadeDuration));
  ```
  _Verifier line corrections: All cited file:line references verified exact (VlcAudioPlayer.cs:1965-1972, 2098-2103, 2814-2817, 2116-2119, 2162-2164; SettingsViewModel.cs:1352). One minor correction to the narrative: the 'shared session' comment cited as 2712-2718 actually spans VlcAudioPlayer.cs:2713-2720. Additional load-bearing lines confirmed during verification: VlcAudioPlayer.cs:2366-2373 (TryStartSequentialFade standby guard returning false), 1983-1996 (fallthrough with _transitionInFlight cleared at 1995), 2053-2054 (old track faded to 0 first), 973-1005 (FadePlayerVolumeBlocking raw player.Volume writes), 545-547 (_sessionVolume non-null by default on Windows), 2957-2978 (reassert worker); PlayerViewModel.cs:831 (ReplaceQueueAndPlay → CancelAutoMixTransition), 2264-2276 (conditional disarm skipped when mode==Crossfade and nothing pending)._

Why it matters: With Song Transitions (Crossfade style) enabled, `_crossfadeEnabled` stays true persistently (SettingsViewModel:1352). Any manual track selection (ReplaceQueueAndPlay first calls CancelAutoMixTransition → CancelPreparedNext, releasing the standby) reaches PlayInternal with `canTransitionFade == true` but NO prepared standby, so TryStartSequentialFade returns false at its `!_standbyPrepared` guard (2366-2373) and playback falls to the single-player path. There, `targetVolume = GetTargetVlcVolume()` is hard-coded to 100 on the session path (2814-2817), and FadePlayerVolumeBlocking(0, 100, fadeInMs) drives `_player.Volume` — which this file's own verified comments state IS the shared mmdevice session (2116-2119, 2712-2718) — from silence to FULL session amplitude over up to 12 s. ScheduleSessionVolumeReassert only runs after the blocking fade returns (2164), so with the slider at e.g. 25% the incoming track swells to ~+18 dB above the chosen level and then snaps down when the reassert lands. During the fade `_transitionInFlight` is already false (cleared at 1995), so a concurrent slider drag additionally fights the fade on the same session (the exact per-stream collision the sequential fade was built to avoid).

Proposed fix: On the session path, fade to the session-equivalent user level instead of 100: use `GetSessionOpenVolume()` as the fade-in target (mirroring TryStartPreparedAutoMix's `finalVolume`), or better, drive the fade through `FadeSessionLevelBlocking(0, userMilli, fadeInMs, cancel)` and keep `_player.Volume` pinned. Keep `targetVolume` (=curved user volume) for the non-session per-player path, which is already correct.

Risk if we fix it: Medium-low — must keep `_lastWrittenVolume`/`_rampCurrentMilli` in sync after the fade (as the prepared-standby path does at 2308-2316) so the next slider write isn't misjudged; verify by ear on Windows that manual track changes with Song Transitions on fade to the slider level with no snap. The mmdevice player-volume→session mapping is asserted by this file's comments and shipped fixes but was not observed at runtime here — a quick listen at a low slider setting confirms.

---

### [H3] LRC editor Save copies and rewrites the entire audio file synchronously on the UI thread
Severity: high
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/LrcEditorViewModel.cs:243-244, 280-286`
  ```csharp
  [RelayCommand]
  private void Save()
  ...
      var tempPath = lrcPath + ".tmp";
      File.WriteAllText(tempPath, lrc, new UTF8Encoding(false));
      File.Move(tempPath, lrcPath, overwrite: true);
  }
  
  // Best-effort metadata write.
  try { _metadata.WriteTrackMetadata(_track); } catch { }
  ```
- `src/Noctis/Services/MetadataService.cs:374-376, 457-470`
  ```csharp
  public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null)
  {
      return SaveTagsAtomically(targetFilePath, file =>
  ...
      File.Copy(targetFilePath, tempPath, overwrite: true);
      ...
      using (var file = TagLib.File.Create(tempPath))
      {
          applyTags(file);
          file.Save();
      }
      File.Move(tempPath, targetFilePath, overwrite: true);
  ```
- `src/Noctis/ViewModels/MetadataViewModel.cs:1970-1974`
  ```csharp
  // Write metadata to file tags (plain lyrics go to USLT tag). Album-scoped
  // edits must be written to every track, not just Tracks[0]. Each write
  // opens and rewrites the audio file, so run the batch on a worker thread —
  // doing it on the UI thread froze the app for seconds on large albums
  ```
- `src/Noctis/Views/LrcEditorDialog.axaml:199`
  ```xml
  Command="{Binding SaveCommand}"/>
  ```

Why it matters: Save() is a non-async [RelayCommand] bound to a dialog button, so it runs entirely on the UI thread. It calls WriteTrackMetadata, whose SaveTagsAtomically path does File.Copy of the WHOLE audio file (a FLAC/WAV can be 50-500 MB), then a TagLib parse + full tag rewrite, then File.Move — all before the command returns. The codebase itself documents (MetadataViewModel.cs:1970-1974, 2002-2005) that this exact call 'froze the app for seconds' when run inline; the metadata editor was fixed with Task.Run (MetadataViewModel.cs:1998) but the LRC editor's Save was not.

Proposed fix: Make Save an async Task command and wrap the sidecar write + _metadata.WriteTrackMetadata(_track) in await Task.Run(...), mirroring MetadataViewModel.cs:1998; set StatusText/raise Saved after the await (the async RelayCommand resumes on the UI context).

Risk if we fix it: Low. SaveCommand becomes AsyncRelayCommand (same XAML binding works); only ordering assumption is that StatusText/Saved fire after the write, which the await preserves. Double-click re-entry is already prevented by AsyncRelayCommand's default CanExecute.

---

### [H4] Queue page defeats ListBox virtualization: entire UpNext queue realized at once
Severity: high
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Views/QueueView.axaml:46-48`
  ```xml
  <ScrollViewer HorizontalScrollBarVisibility="Disabled"
                VerticalScrollBarVisibility="Auto">
      <StackPanel Margin="16">
  ```
- `src/Noctis/Views/QueueView.axaml:214-216`
  ```xml
  <ListBox Classes="track-list"
           ItemsSource="{Binding UpNext}"
           Margin="0,0,0,24">
  ```
- `src/Noctis/ViewModels/QueueViewModel.cs:18-22`
  ```csharp
  /// <summary>Reference to UpNext from the player (same collection, shared binding).</summary>
  public ObservableCollection<Track> UpNext => _player.UpNext;
  
  /// <summary>Reference to History from the player.</summary>
  public ObservableCollection<Track> History => _player.History;
  ```
- `src/Noctis/ViewModels/PlayerViewModel.cs:139`
  ```csharp
  public BulkObservableCollection<Track> UpNext { get; } = new();
  ```
  _Verifier line corrections: TrimHistory declaration is src/Noctis/ViewModels/PlayerViewModel.cs:1817, cap loop `while (History.Count > 50)` at :1820 (finder cited 1820 — accurate). Additional supporting evidence: src/Noctis/ViewModels/PlayerViewModel.cs:851-853 `for (int i = startIndex + 1; i < tracks.Count; i++) upNextTracks.Add(tracks[i]); UpNext.ReplaceAll(upNextTracks);` proves UpNext holds the full remaining queue; src/Noctis/Assets/Styles.axaml:713-717 proves no ItemsPanel/MaxHeight override on ListBox.track-list; src/Noctis/Views/LibrarySongsView.axaml.cs:280 and src/Noctis/ViewModels/CommandPaletteViewModel.cs:101 prove the page is reachable._

Why it matters: Both queue-page ListBoxes sit inside a StackPanel inside a page-level ScrollViewer. The StackPanel measures children with infinite height, so each ListBox's internal ScrollViewer expands to full content height and its VirtualizingStackPanel sees an unbounded viewport, realizing a container for every item. UpNext is the full remaining queue with no cap (PlayerViewModel.UpNext is unbounded; 'Shuffle All' on a large library puts thousands to tens of thousands of tracks in it — the repo's own scale audit targets 40k+ libraries). Opening the Queue page then materializes tens of thousands of row templates in one layout pass: a multi-second freeze and large memory spike. The codebase clearly knows the correct pattern — the MainWindow queue popup (MainWindow.axaml:883-895) hosts the same collection in a star-sized Grid and is commented 'Queue track list (virtualized)'. History is capped at 50 (PlayerViewModel.TrimHistory, line 1820), so UpNext is the scale problem.

Proposed fix: Restructure QueueView so the UpNext ListBox is the scrolling element: replace the outer ScrollViewer+StackPanel with a Grid (RowDefinitions="Auto,Auto,*,Auto,Auto") placing the Now Playing header/card and section titles in Auto rows and the UpNext ListBox in the star row so its internal ScrollViewer gets a finite viewport and virtualization works. History (max 50) can stay in a bounded region or a second star row.

Risk if we fix it: Layout restructure changes the page's scroll model: Now Playing/History no longer scroll away with the queue. If the single-scroll-surface look must be kept, an alternative is capping the realized rows (e.g. incremental 'show more'), which changes UX. Pure XAML change, no logic risk.

---

### [H5] PlaylistViewModel.LoadTracks does full-library work synchronously on the UI thread (smart-playlist eval per LibraryUpdated, per-track regex suggestions, per-item ObservableCollection adds)
Severity: high
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/PlaylistViewModel.cs:206-215`
  ```csharp
  if (_isSmartPlaylist)
      _library.LibraryUpdated += OnLibraryUpdated;
  ...
  private void OnLibraryUpdated(object? sender, EventArgs e)
  {
      Dispatcher.UIThread.Post(() => LoadTracks());
  }
  ```
- `src/Noctis/ViewModels/PlaylistViewModel.cs:274-297`
  ```csharp
  if (_playlist.IsSmartPlaylist)
  {
      resolved = SmartPlaylistEvaluator.Evaluate(_playlist, _library.Tracks);
  }
  ...
  foreach (var track in SortTracks(resolved.ToList(), SortMode))
      Tracks.Add(track);
  ```
- `src/Noctis/ViewModels/PlaylistViewModel.cs:358-364`
  ```csharp
  var candidates = _library.Tracks
      .Where(t => !inPlaylist.Contains(t.Id)
                  && Track.ParseArtistTokens(t.Artist).Any(playlistArtists.Contains))
      .ToList();
  
  foreach (var pick in candidates.OrderBy(_ => Random.Shared.Next()).Take(3))
  ```
- `src/Noctis/Models/Track.cs:704-718`
  ```csharp
  internal static string[] ParseArtistTokens(string? value)
  {
      ...
      return Regex
          .Split(
              value,
              @"\s*(?:,|;|/|&|\bfeat\.?\b|\bft\.?\b|\bfeaturing\b|\band\b|\bwith\b|\bx\b)\s*",
              RegexOptions.IgnoreCase)
          .Select(v => v.Trim())
          .Where(v => !string.IsNullOrWhiteSpace(v))
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();
  }
  ```
- `src/Noctis/ViewModels/PlaylistViewModel.cs:148`
  ```csharp
  public ObservableCollection<Track> Tracks { get; } = new();
  ```
  _Verifier line corrections: All cited file:line references verified correct. Additional supporting evidence: src/Noctis/ViewModels/MainWindowViewModel.cs:1242-1252 (DisposeViewIfTransient skips views in nav history, so hidden smart-playlist VMs keep reloading during scans); src/Noctis/Services/LibraryService.cs:22,202,214 (1.5 s progressive publish loop); src/Noctis/Services/SmartPlaylistEvaluator.cs:19-34 (full-library pass); src/Noctis/ViewModels/LibrarySongsViewModel.cs:413-447 (the generation-guarded Task.Run + ReplaceAll pattern this VM lacks). One correction to the finder's fix_risk text only (not the evidence): there is no 'list current before navigation paints' comment at PlaylistViewModel.cs:120-122 — those lines are the ModifiedDateValue getter._

Why it matters: LoadTracks is entirely synchronous on the UI thread and is invoked on playlist open, every sort change, every debounced search keystroke (ApplyFilter -> LoadTracks), every PlaylistTracksChanged, and — for smart playlists — on every LibraryUpdated, which fires every ~1.5 s during a scan (LibraryService.ProgressivePublishMs = 1500). At 50k+ tracks: (a) SmartPlaylistEvaluator.Evaluate is an O(n x rules) pass over the whole library on the UI thread; (b) for manual playlists RebuildSuggestions runs Track.ParseArtistTokens — an uncached Regex.Split + LINQ chain — once per library track on the UI thread (estimate: ~3-8 us per call => roughly 150-400 ms freeze per playlist open at 50k, plus allocation churn; guarded only by a membership key so it re-runs on every membership change); (c) results are pushed into a plain ObservableCollection via Clear() + per-item Add — one CollectionChanged event per row, so a large smart playlist raises thousands of UI notifications per reload. Every other library view in the repo already fixed this pattern (BulkObservableCollection.ReplaceAll + Task.Run + generation guard in LibrarySongsViewModel.ApplyFilterAndSort), which is strong evidence this VM was simply left behind.

Proposed fix: Mirror LibrarySongsViewModel: run Evaluate/suggestions/sort inside Task.Run with a generation counter, switch Tracks to BulkObservableCollection<Track> and ReplaceAll the result, and gate the smart-playlist LibraryUpdated handler behind an IsActive flag like the other views. Cache or precompute artist tokens for the suggestion scan (a lazily cached Track.ArtistTokens mirroring SearchTitleKey).

Risk if we fix it: Moderate: LoadTracks currently guarantees the bound list is current before navigation paints (comment at line 120-122); an async rebuild must keep a synchronous fast path or accept one frame of stale rows. Drag-reorder and selection state must survive ReplaceAll.

---

### [H6] Hardcoded Last.fm API key and shared secret in source
Severity: high
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/LastFmService.cs:14-18`
  ```csharp
  // Last.fm API credentials — register at https://www.last.fm/api/account/create
  private const string ApiKey = "<REDACTED>";
  private const string ApiSecret = "<REDACTED>";
  private const string ApiBase = "https://ws.audioscrobbler.com/2.0/";
  ```
- `src/Noctis/Services/LastFmService.cs:702-711`
  ```csharp
  private static string GenerateSignature(SortedDictionary<string, string> parameters)
  {
      var sb = new StringBuilder();
      foreach (var kvp in parameters)
          sb.Append(kvp.Key).Append(kvp.Value);
      sb.Append(ApiSecret);
  
      var bytes = Encoding.UTF8.GetBytes(sb.ToString());
      var hash = MD5.HashData(bytes);
  ```
- `src/Noctis/Services/LastFmService.cs:15-16`
  ```csharp
  private const string ApiKey = "<REDACTED>";
  private const string ApiSecret = "<REDACTED>";
  ```
- `src/Noctis/Services/LastFmService.cs:707`
  ```csharp
  sb.Append(ApiSecret);
  ```
  _Verifier line corrections: src/Noctis/Services/LastFmService.cs:14-17 (credentials block; original citation said 14-18) and src/Noctis/Services/LastFmService.cs:702-712 (GenerateSignature; quoted portion is 702-710)._

Why it matters: The Last.fm API key AND the signing secret ship in cleartext in the source (and therefore in every distributed binary and the public GitHub repo). The secret is what authenticates request signatures as "this app"; anyone can extract it and forge signed auth.getSession / scrobble traffic under Noctis's API identity, risking rate-limit abuse or revocation of the app's key for all users. Project memory notes this pair has been in public git history since the initial commit and rotation is pending.

_Also found independently by the Settings (Stats/Integrations/About) auditor (verdict: CONFIRMED)._

Proposed fix: Rotate the Last.fm credentials, then inject them at build time (CI secret -> generated source or MSBuild constant) instead of committing them. Note a desktop scrobbler cannot fully hide the secret from a determined reverse-engineer — the goal is keeping it out of the public repo and enabling rotation.

Risk if we fix it: Low. Rotating invalidates nothing user-side except that old builds stop authenticating new sessions; existing session keys keep working per Last.fm docs.

---

### [H7] Bundled macOS libvlc (VideoLAN.LibVLC.Mac 3.0.21) unlikely to serve Apple Silicon without VLC.app installed — playback fails on a fresh arm64 install
Severity: high
Confidence: unverified (verifier could not decide from code alone)
Evidence:
- `src/Noctis/Noctis.csproj:74`
  ```xml
  <PackageReference Include="VideoLAN.LibVLC.Mac" Version="3.0.21" Condition="$([MSBuild]::IsOSPlatform('OSX'))" />
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:3786-3798`
  ```csharp
  string[] candidates =
  {
      "/Applications/VLC.app/Contents/MacOS/lib",
      "/opt/homebrew/lib",
      "/usr/local/lib",
  };
  foreach (var dir in candidates)
  {
      if (File.Exists(Path.Combine(dir, "libvlc.dylib")))
          return dir;
  }
  return null;
  ```
  _Verifier line corrections: src/Noctis/Noctis.csproj:74 (conditional VideoLAN.LibVLC.Mac 3.0.21); src/Noctis/Services/VlcAudioPlayer.cs:3786-3798 (TryFindMacLibVlcPath candidates), 306-331 (fallback Core.Initialize() with no plugin path when null), 3825-3829 (mac missing-libvlc message); .github/workflows/dotnet.yml:157-236 (mac packaging bundles no libvlc, unlike Linux at 293-349); src/Noctis/Program.cs:131-142 (missing-libvlc message shown only on Windows; macOS gets stderr/crash-log only); README.md:149 (project documents VLC.app as the macOS requirement); AUDIT_2026-07-24.md:1424 (prior audit: 'known arm64-slice/libvlccore packaging problem', unverified)._

Why it matters: When no VLC.app/homebrew libvlc is found, the constructor falls back to Core.Initialize() against whatever the NuGet package placed in the bundle. The code's own comment (VlcAudioPlayer.cs:308-313) calls the package layout unreliable, and the project's prior investigation (macOS Apple Silicon launch fix notes) found the bundled libvlc to be x86_64-only without plugins — an arm64 process cannot load it, so a fresh Apple Silicon install without VLC.app either throws the 'libvlc is required' startup error or has no audio. The package payload is not in this repo, so this cannot be proven by code reading alone.

Proposed fix: Either bundle a universal/arm64 libvlc + plugins in the CI mac packaging step, or detect the failure and show a first-run prompt directing the user to install VLC.app (the message at BuildLibVlcMissingMessage:3825-3828 already exists but only fires when the load throws).

Risk if we fix it: Packaging-only change; must keep the codesign step covering the new dylibs (workflow already signs nested Mach-O depth-first).

To confirm: Inspect Contents/MacOS of a released arm64 .app for libvlc.dylib architecture (`lipo -info`) and a plugins/ dir; or launch on an Apple Silicon Mac with no VLC.app and attempt playback. / On a released arm64 Noctis.app: `lipo -info` on any libvlc*.dylib inside Contents/MacOS (or confirm none is present in the publish output at all) and check for a plugins/ dir; or launch on an Apple Silicon Mac with no VLC.app installed and observe whether startup throws / playback works. Equivalently, inspect the runtimes/ layout of the VideoLAN.LibVLC.Mac 3.0.21 nupkg on any machine.

---

### [H8] VideoLAN.LibVLC.Mac pins a version that does not exist on nuget.org; macOS builds silently restore a 2019-era libvlc missing 7 years of security fixes
Severity: high
Confidence: confirmed (web-research finding — version/CVE claims cited, not code-adversarially verified)
Evidence:
- `src/Noctis/Noctis.csproj:73-74`
  ```xml
  <PackageReference Include="VideoLAN.LibVLC.Windows" Version="3.0.23.1" Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
  <PackageReference Include="VideoLAN.LibVLC.Mac" Version="3.0.21" Condition="$([MSBuild]::IsOSPlatform('OSX'))" />
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:308-311`
  ```csharp
  // On macOS the VideoLAN.LibVLC.Mac NuGet has shifting layouts between
  // versions; if VLC.app is installed (recommended path), point the
  // loader at its dylibs directly so playback works regardless of
  // which package version restore picked.
  ```

Why it matters: nuget.org's authoritative version index for VideoLAN.LibVLC.Mac (https://api.nuget.org/v3-flatcontainer/videolan.libvlc.mac/index.json) contains only 3.0.0-alpha, 3.0.0-alpha1, 3.1.2-alpha, 3.1.2, 3.1.3, 3.1.3.1 — no 3.0.21, and nothing published since 2019-09-30. The repo has no nuget.config or RestoreSources override (verified by search), so nuget.org is the only feed. PackageReference Version="3.0.21" is a minimum bound; NuGet's lowest-applicable rule resolves it to 3.1.2 (2018-11-14) with only an NU1603 warning, so macOS release artifacts bundle a ~2019 nightly libvlc that predates the security fixes in VLC 3.0.8 through 3.0.23 — including CVE-2024-46461 (fixed in 3.0.21) and the 3.0.22 batch VideoLAN describes as its largest-ever set of security fixes. The code comment at VlcAudioPlayer.cs:308-311 shows the team already works around the unpredictable payload by preferring VLC.app dylibs at runtime, but users without VLC.app installed run the stale bundled build against arbitrary local media files (demuxer overflow fixes are exactly what the 3.0.22 batch contains).

Proposed fix: Report-only recommendation: drop the VideoLAN.LibVLC.Mac PackageReference (it is abandoned upstream) and instead have the macOS CI packaging step fetch the official VLC 3.0.23 macOS payload (pinned URL + SHA-256, same pattern the workflow already uses for ffmpeg) and copy its lib/ + plugins/ into Noctis.app; or make VLC.app an explicit install requirement and fail fast without it.

Risk if we fix it: Medium: macOS bundling interacts with the existing codesign/notarization steps (every nested Mach-O must be signed depth-first per the workflow comments) and with TryFindMacLibVlcPath/VLC_PLUGIN_PATH logic; needs a real macOS smoke test.

---

## Medium findings

### [M1] Outgoing track's natural EndReached during a crossfade fade-out is stamped with the NEW session id — TrackEnded fires mid-transition and double-advances the queue (a track gets skipped)
Severity: medium
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/Services/VlcAudioPlayer.cs:1933-1934`
  ```csharp
  var sessionId = Interlocked.Increment(ref _playbackSessionId);
  _positionTimer.Stop();
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:3219-3222`
  ```csharp
  var deadline = DateTime.UtcNow.AddMilliseconds(EndReachedGraceMs).Ticks;
  Interlocked.Exchange(ref _endReachedSessionId, sessionId);
  Interlocked.Exchange(ref _endReachedDeadlineTicksUtc, deadline);
  _positionTimer.Start();
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:3311-3317`
  ```csharp
  if (DateTime.UtcNow.Ticks >= endDeadlineTicks &&
      Interlocked.CompareExchange(ref _endReachedDeadlineTicksUtc, 0, endDeadlineTicks) == endDeadlineTicks)
  {
      _positionTimer.Stop();
      if (pendingEndSessionId == CurrentSessionId)
          TrackEnded?.Invoke(this, EventArgs.Empty);
  }
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:2412, 2427-2428, 2451`
  ```csharp
  FadeSessionLevelBlocking(startMilli, 0, fadeOutMs, cancel);
  ...
  Interlocked.Exchange(ref _lastPlayStartTicksUtc, DateTime.UtcNow.Ticks);
  _standbyPlayer.Play(_standbyMedia);
  ...
  ResetEndReachedPending();  // only AFTER fade-out + standby warmup + swap
  ```
- `src/Noctis/ViewModels/PlayerViewModel.cs:2060-2064`
  ```csharp
  // A seek can land directly inside the fade window without ever crossing the
  // approach band; without a prepared snapshot the validator below would cancel
  // on every tick and the track would end with no transition at all.
  var preloadLead = TimeSpan.FromSeconds(Math.Clamp(plan.Duration.TotalSeconds + 2, 3, 8));
  if (position >= fadeStart - preloadLead && _autoMixPreparedTrackId != nextTrack.Id)
  ```
- `src/Noctis/ViewModels/PlayerViewModel.cs:2297-2305`
  ```csharp
  private void OnTrackEnded(object? sender, EventArgs e)
  {
      DebugLogger.Info(DebugLogger.Category.Playback, "TrackEnded", ...);
      Dispatcher.UIThread.Post(() =>
      {
          CancelNaturalEndFallback();
          AdvanceQueue();
      });
  }
  ```
  _Verifier line corrections: All cited file:line references are accurate. Additional confirming anchors: src/Noctis/Services/VlcAudioPlayer.cs:79 (EndReachedGraceMs=1200), 81 (StandbyWarmupTimeoutMs=650), 2196/2210 (TryStartPreparedAutoMix entry reset, no post-swap reset before method end at 2349), 2594-2607 (overlap hold up to 6000ms with outgoing playing through its ending), src/Noctis/ViewModels/PlayerViewModel.cs:1503-1516 (AdvanceQueue re-entrancy guard is synchronous-only), 1565-1573 (UpNext[0] played on the spurious advance)._

Why it matters: Threads: VLC's native event thread (fires EndReached for the still-playing OUTGOING player, which is `_player` until the swap) vs. the ThreadPool crossfade worker holding `_playbackLock`. PlayInternal increments the session id for track N+1 BEFORE the transition runs (1933). TryStartSequentialFade clears pending end-state on entry (2377) but only re-clears AFTER fade-out (up to 3 s) + standby warmup (≤650 ms) + swap (2451). If the outgoing track N reaches its real end during that window — normal when the transition was entered with little remaining time, which the seek-into-fade-window path explicitly supports (PlayerViewModel 2060-2079) — OnEndReachedCore runs: `sender == _player` still holds (pre-swap), the <500 ms stale window doesn't apply (`_lastPlayStartTicksUtc` is only bumped at 2427, after the fade-out), so the grace deadline is armed with `sessionId = CurrentSessionId` = N+1's session and the position timer is restarted (3222). 1.2 s later the timer thread's deadline check passes (`pendingEndSessionId == CurrentSessionId` — both are N+1's id) and TrackEnded fires while N+1's transition is still committing. PlayerViewModel then AdvanceQueue()s again: UpNext[0] is now N+2, so N+1 is audibly cut short/skipped.

Proposed fix: Suppress grace arming for an EndReached that belongs to a superseded input: in OnEndReachedCore, ignore the event when `_transitionInFlight` is true (the transition owns the advance), or stamp `_endReachedSessionId` with the session id captured when the outgoing player started rather than CurrentSessionId. Also add the missing post-swap `ResetEndReachedPending()` to TryStartPreparedAutoMix (the sequential/overlap paths have one at 2451/2617; the gapless/AutoMix per-player path does not).

Risk if we fix it: Medium — EndReached handling is delicate (the stale-window and short-track carve-outs at 3181-3196 exist for real regressions). Ignoring events while `_transitionInFlight` must not stall the queue if a transition faults before clearing the flag (see finding 1's latch — fix that first). Reproduce by seeking deep into an armed crossfade window and watching for the 'TrackEnded' log during 'Crossfade.Seq*' markers.

---

### [M2] Pause() bypasses the playback lock and ThreadPool serialization — a pause landing during a track change or transition swap is silently overridden (audio keeps playing while UI shows paused)
Severity: medium
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/Services/VlcAudioPlayer.cs:2980-2994`
  ```csharp
  public void Pause()
  {
      if (_disposed) return;
      _keepAlive?.NotifyActivity();
      CancelSkipCts();
      CancelPreparedNext();
  
      if (_player.IsPlaying)
      {
          ResetEndReachedPending();
          _player.Pause();
          _isPaused = true;
          _positionTimer.Stop();
      }
  }
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:12-16`
  ```csharp
  ///   - VLC fires EndReached/EncounteredError on its own internal thread.
  ///   - You MUST NOT call Play/Stop/Pause from inside those handlers (deadlock).
  ///   - All VLC state-changing calls go through ThreadPool to avoid blocking UI.
  ///   - A SemaphoreSlim serializes Play/Stop to prevent overlapping operations.
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:2293-2296, 2318-2320`
  ```csharp
  var outgoingPlayer = _player;
  var outgoingMedia = _currentMedia;
  _player = _standbyPlayer;
  SetPlayerVolumeGuarded(_player, finalVolume);
  ...
  _transitionInFlight = false;
  _isPaused = false;
  _positionTimer.Start();
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:2062-2063`
  ```csharp
  oldMedia?.Dispose();
  _isPaused = false;
  ```
- `src/Noctis/ViewModels/PlayerViewModel.cs:255-258`
  ```csharp
  case PlaybackState.Playing:
      CancelAutoMixTransition("user paused");
      _audioPlayer.Pause();
      State = PlaybackState.Paused;
  ```
  _Verifier line corrections: All cited file:line references are accurate (2293-2296/2318-2320 sit inside TryStartPreparedAutoMix, method start 2196). Correction to scope: the override windows are (a) last-cancel-checkpoint to _isPaused=false — src/Noctis/Services/VlcAudioPlayer.cs:2286->2319, warmup-return->2450, 2603->2616, milliseconds wide; and (b) _player.Stop() at 2056 until VLC asserts IsPlaying after Play() at 2093 — during which Pause()'s guard at 2987 drops the click entirely. Pauses landing in the seconds-wide fade loops are instead converted into a transition abort by the cancelled _skipCts (2984, 1844 observed at 2413/2286/2596/2675), which is a separate VM-vs-loaded-media mismatch, not the claimed override._

Why it matters: Unlike Play/Resume/Stop/PrepareNext (all queued to ThreadPool and serialized by `_playbackLock`), Pause() executes `_player.Pause()` and `_isPaused = true` inline on the caller's thread (the UI thread via the PlayPause RelayCommand) with no lock. Two concrete interleavings: (1) UI thread runs Pause() while the crossfade worker (holding the lock) is between its last cancellation checkpoint (2286) and the swap — Pause() reads the pre-swap `_player` (the outgoing), pauses it, sets `_isPaused = true`; the worker then swaps `_player` to the standby and overwrites `_isPaused = false` (2319) and restarts the timer — the incoming track plays audibly while PlayerViewModel.State says Paused (line 258). (2) UI thread runs Pause() while a plain PlayInternal worker is between `_player.Play(_currentMedia)` (2093) and its later steps — PlayInternal already forced `_isPaused = false` at 2063, so the user's pause is dropped depending on ordering. In both cases the user's pause click is lost during exactly the moments (track change, end-of-track transition) when clicks cluster; pressing pause again recovers. It also violates the class's own documented serialization rule (lines 12-16).

Proposed fix: Route Pause through the same ThreadPool + `_playbackLock` pattern as Resume() (3007-3029), and have transition commit points re-check a captured 'pause requested' intent (analogous to `_restartPausedRequest`) instead of unconditionally writing `_isPaused = false`.

Risk if we fix it: Medium-low — queuing adds a few ms of pause latency and requires care that the lock isn't held for seconds by a fade when pause arrives (the CancelSkipCts already makes fades bail within one step, so the wait is short once finding 1's cancel paths are fixed). Pause is never called from VLC event threads today, so no deadlock exposure is added.

---

### [M3] PrepareNext holds _playbackLock across a non-cancellable 8-second media parse — Play/Resume/Stop (and thus the audible track change) queue behind it
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/VlcAudioPlayer.cs:1772-1775`
  ```csharp
  ThreadPool.QueueUserWorkItem(_ =>
  {
      try { _playbackLock.Wait(); }
      catch (ObjectDisposedException) { return; }
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:1800-1806`
  ```csharp
  var parseTask = media.Parse(MediaParseOptions.ParseLocal, timeout: 8000);
  if (!parseTask.Wait(8000) || parseTask.Result != MediaParsedStatus.Done)
  {
      media.Dispose();
      DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.DualPrepareFailed", $"path={Path.GetFileName(normalizedPath)}");
      return;
  }
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:3001-3006`
  ```csharp
  // Queued to the ThreadPool like every other playback entry point (Play, Stop,
  // PrepareNext, SetExclusiveMode). This used to take _playbackLock inline, and
  // PlayerViewModel.PlayPause is a [RelayCommand] — i.e. it ran on the UI thread.
  // Lock holders include PrepareNext's non-cancellable parseTask.Wait(8000) and the
  // native _player.Stop(), so unpausing while the next track was being prepared
  // from a slow or network path froze the window for up to 8 seconds.
  ```
- `src/Noctis/ViewModels/PlayerViewModel.cs:2190-2205`
  ```csharp
  if (remaining <= TimeSpan.FromSeconds(GaplessPrepareLeadSeconds) &&
      _autoMixPreparedTrackId != nextTrack.Id &&
      !string.IsNullOrWhiteSpace(nextTrack.FilePath))
  {
      ...
      _audioPlayer.PrepareNext(
          nextTrack.FilePath,
          nextTrack.StartTimeMs > 0 ? nextTrack.StartTimeMs : -1);
  }
  ```
  _Verifier line corrections: All cited file:line references are accurate. Additional anchors: src/Noctis/Services/VlcAudioPlayer.cs:1836 (lock released in finally), 1844 (CancelSkipCts inline but parse observes no token), 2021-2033 (PlayInternal's contrasting cancellable parse), 1763-1764 (path inactive in exclusive/WASAPI-sink mode), src/Noctis/ViewModels/PlayerViewModel.cs:2158 (GaplessPrepareLeadSeconds = 8.0)._

Why it matters: PrepareNext runs its `media.Parse(...).Wait(8000)` while holding `_playbackLock`, and the wait observes no cancellation token (`_skipCts` is not linked, unlike PlayInternal's parse at 2021-2026). The UI-freeze half of this was fixed by queueing Resume (comment at 3001-3006 documents the lock-holder), but the audio-latency half remains: PrepareNext fires ~8 s before every track end (PlayerViewModel GaplessPrepareLeadSeconds) and on the AutoMix approach band, so a user Next/Previous/Resume/Stop issued in that window on a slow device (NAS, spun-down HDD, USB wake-up) waits for up to 8 s of parse before the queued PlayInternal can run — heard as the app 'ignoring' the skip and then changing tracks late. CancelPreparedNext cannot shortcut it either, since it also queues behind the same lock (1846-1861).

Proposed fix: Parse outside the lock: create+parse the Media first (with a token linked to `_skipCts` so a user skip aborts the wait), then take `_playbackLock` only to install the parsed media into the standby fields (and re-validate `_currentMedia != null` / path under the lock, disposing the media if superseded).

Risk if we fix it: Medium — the standby install must still be atomic with respect to PlayInternal's TryStartPreparedAutoMix reads; keeping the field mutation under the lock while hoisting only the parse preserves that. Behavior on fast local disks is unchanged; test rapid skip-spam during the 8s-before-end window.

---

### [M4] Remote (media-server) streams can never use gapless/crossfade — every track boundary pays the fixed 1.2 s EndReached grace plus a network parse of the next track
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/VlcAudioPlayer.cs:1756-1764`
  ```csharp
  public void PrepareNext(string filePath, long startPositionMs = -1)
  {
      if (_disposed || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
          return;
  
      // The WASAPI callback sinks are single-stream: standby warmup would play
      // the second player through LibVLC's own output, bypassing the sink. Skip.
      if (_wasapiOut != null || _exclusiveModeEnabled)
          return;
  ```
- `src/Noctis/ViewModels/PlayerViewModel.cs:2210-2211`
  ```csharp
  if (string.IsNullOrWhiteSpace(nextTrack.FilePath) || !File.Exists(nextTrack.FilePath))
      return false;
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:79`
  ```csharp
  private const int EndReachedGraceMs = 1200;
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:3219-3222`
  ```csharp
  var deadline = DateTime.UtcNow.AddMilliseconds(EndReachedGraceMs).Ticks;
  Interlocked.Exchange(ref _endReachedSessionId, sessionId);
  Interlocked.Exchange(ref _endReachedDeadlineTicksUtc, deadline);
  _positionTimer.Start();
  ```
- `src/Noctis/Services/VlcAudioPlayer.cs:2016-2023`
  ```csharp
  var media = new Media(_libVlc, filePath, isRemote ? FromType.FromLocation : FromType.FromPath);
  ...
  var parseTask = media.Parse(isRemote ? MediaParseOptions.ParseNetwork : MediaParseOptions.ParseLocal, timeout: 8000);
  ```
  _Verifier line corrections: All original citations correct: src/Noctis/Services/VlcAudioPlayer.cs:79 (EndReachedGraceMs=1200), 1756-1764 (PrepareNext File.Exists gate), 1873-1875 (IsRemoteStreamPath), 1962/1966/2001 (!isRemote gates in PlayInternal), 2016-2023 (FromLocation + ParseNetwork), 3219-3222 and 3311-3317 (grace deadline → TrackEnded); src/Noctis/ViewModels/PlayerViewModel.cs:2210-2211 (gapless File.Exists bail), plus additionally 2100-2105 (TryAdvanceForAutoMix File.Exists cancel — crossfade also excluded) and 1466 (Play(track.FilePath)); src/Noctis/Services/MediaServer/SubsonicClient.cs:322 and JellyfinClient.cs:315 (FilePath = BuildStreamUrl → http URL). CORRECTION to the gap magnitude: src/Noctis/ViewModels/PlayerViewModel.cs:164-167 and 1952-2009 — the natural-end fallback timer (armed within the last 0.75 s, fires 1400 ms later) usually advances the queue ~0.65-0.75 s after nominal track end, before the 1.2 s grace elapses; OnTrackEnded at 2297-2305 cancels it when the grace wins instead. So the per-boundary dead air is ~0.7 s (typical) to ~1.2 s (fallback not armed) plus synchronous network open/ParseNetwork of the next track — still no gapless/crossfade possible for remote streams. Exact audible gap on a real server should be confirmed with NOCTIS_VLC_LOG=1 timing, but the structural defect needs no runtime data._

Why it matters: For http(s) tracks, `File.Exists` is false, so PrepareNext returns immediately (1758) and PlayerViewModel's TryAdvanceForGapless bails at its own File.Exists check (2210) — the early-advance/standby machinery never engages (PlayInternal also gates: `!isRemote`, 1959-1966/2001). Every remote track boundary therefore takes the EndReached path: VLC fires EndReached, the player waits the full fixed 1200 ms grace before raising TrackEnded (3219-3222 → 3311-3317), and only then does PlayInternal open and network-parse the next stream (2016-2035). Net inter-track silence ≥ ~1.2 s plus network open/parse time on every transition when streaming from a media server (Navidrome/WebDAV/SMB-over-http connectors). The same fixed 1.2 s gap hits local playback whenever the 0.3 s gapless handoff window is missed (busy UI thread coalesces the 10 Hz position posts; TryAdvanceForGapless needs a tick to land within GaplessHandoffLeadSeconds=0.3 of the end).

Proposed fix: For remote streams, pre-open and ParseNetwork the next track's Media during the current track's tail (a remote-capable PrepareNext that skips File.Exists for IsRemoteStreamPath), and/or shorten the grace when the queue has a validated next item (the grace exists to let the aout drain and keep lyrics alive — with a next track ready, TrackEnded could fire as soon as position stops advancing).

Risk if we fix it: Medium — shortening the grace risks re-introducing the early-cut the 1200 ms window was added for (EndReached fires before the output buffer fully drains); prefetching remote media must not hold _playbackLock (see finding 5) and must scrub token-bearing URLs from logs as PlayInternal already does. Actual audible gap length should be confirmed with NOCTIS_VLC_LOG=1 timing on a real media-server setup.

---

### [M5] ArtworkPathConverter does synchronous disk read + JPEG decode on the UI thread; used in Home and Albums item templates
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Converters/ArtworkPathConverter.cs:10-11, 20-26`
  ```csharp
  /// Note: This converter loads synchronously (cache hit = instant, miss = blocks UI).
  /// For virtualized lists, prefer the CachedImage control which loads asynchronously.
  ...
  var cached = ArtworkCache.TryGet(path);
  if (cached != null)
      return cached;
  
  // Synchronous load + cache
  return ArtworkCache.LoadAndCache(path);
  ```
- `src/Noctis/Services/ArtworkCache.cs:138-140`
  ```csharp
  Bitmap bitmap;
  using (var stream = File.OpenRead(path))
      bitmap = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
  ```
- `src/Noctis/Views/HomeView.axaml:114 (also 400, 445, 490)`
  ```xml
  <Image Source="{Binding Track.AlbumArtworkPath, Converter={StaticResource ArtworkConverter}}"
  ```
- `src/Noctis/Views/LibraryAlbumsView.axaml:104`
  ```xml
  <Image Source="{Binding Track.AlbumArtworkPath, Converter={StaticResource ArtworkConverter}}"
  ```
- `src/Noctis/Services/ArtworkCache.cs:50, 139-140`
  ```csharp
  private const int DecodeWidth = 512;
  ...
  using (var stream = File.OpenRead(path))
      bitmap = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
  ```
- `src/Noctis/Views/LibraryAlbumsView.axaml:104-107`
  ```xml
  <Image Source="{Binding Track.AlbumArtworkPath, Converter={StaticResource ArtworkConverter}}"
         Stretch="UniformToFill"
         RenderOptions.BitmapInterpolationMode="HighQuality"
         IsVisible="{Binding Track.AlbumArtworkPath, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
  ```
- `src/Noctis/Views/HomeView.axaml:114-115`
  ```xml
  <Image Source="{Binding Track.AlbumArtworkPath, Converter={StaticResource ArtworkConverter}}"
         Stretch="UniformToFill"
  ```

Why it matters: IValueConverter.Convert runs on the UI thread during binding evaluation. On a cache miss the converter does File.OpenRead + Bitmap.DecodeToWidth(512) inline. HomeView uses it in four ItemsControl item templates (ranked chart, history/time-rotation pills) and LibraryAlbumsView in one; EditPlaylistDialog.axaml:165 also uses it. First navigation to Home with a cold ArtworkCache performs one blocking file read + image decode per distinct cover, serially, during the layout pass — a stall proportional to the number of distinct covers on screen (typically ~10-40 decodes). The converter's own doc comment states the defect and names the correct alternative (CachedImage, which decodes via Task.Run — Controls/CachedImage.cs:122).

_Also found independently by the Perf / render auditor (verdict: CONFIRMED)._

Proposed fix: Replace the Image+ArtworkConverter usages in HomeView.axaml (4 sites), LibraryAlbumsView.axaml:104, and EditPlaylistDialog.axaml:165 with the existing CachedImage control, which does the decode on a background thread and keeps the previous frame.

Risk if we fix it: Low-medium. CachedImage is already used elsewhere for exactly this; risk is cosmetic (brief placeholder before art appears, template property differences). The IsNullOrEmpty placeholder TextBlock logic beside each Image must be kept consistent.

---

### [M6] Metadata editor constructor performs two TagLib file parses, sidecar reads, and a full-resolution artwork decode on the UI thread
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/MetadataViewModel.cs:381-385`
  ```csharp
  LoadFromTrack();
  LoadFileInfo();
  LoadArtwork();
  LoadAnimatedCover();
  LoadAdvancedFields();
  ```
- `src/Noctis/ViewModels/MetadataViewModel.cs:985, 1036, 1056-1057, 2294`
  ```csharp
  var info = _metadata.ReadFileInfo(_track.FilePath);
  ...
  try { cachedData = File.ReadAllBytes(artPath); } catch { }
  ...
  using var ms = new MemoryStream(preferredData);
  ArtworkPreview = new Bitmap(ms);
  ...
  var fields = AdvancedTagIO.ReadAll(_track.FilePath);
  ```
- `src/Noctis/Services/MetadataService.cs:550-554`
  ```csharp
  public AudioFileInfo? ReadFileInfo(string filePath)
  {
      try
      {
          using var file = TagLib.File.Create(filePath);
  ```
- `src/Noctis/ViewModels/MetadataHelper.cs:136, 158`
  ```csharp
  public static async Task OpenMetadataWindow(Track track, bool albumScoped = false)
  ...
  var vm = new MetadataViewModel(track, metadata, library, persistence, animatedCovers, albumScoped, albumTracks, itunes, lrcLib, autoMatch: ...);
  ```
  _Verifier line corrections: All cited file:line references are accurate as given; add MetadataViewModel.cs:2291 ('if (_albumScoped) return;') qualifying that the second TagLib parse occurs only in the non-album-scoped (single-track) editor path._

Why it matters: OpenMetadataWindow is awaited from [RelayCommand]s in many VMs (e.g. PlayerViewModel.cs:764, HomeViewModel.cs:410), and the MetadataViewModel construction happens before any await that leaves the UI thread. The ctor synchronously: (1) reads .lrc/.txt sidecars via File.ReadAllText (lines 910/925), (2) opens + parses the audio file with TagLib (ReadFileInfo), (3) reads the cached cover with File.ReadAllBytes and decodes it at FULL resolution with new Bitmap(ms) (a 3000x3000 embedded/cached cover decodes to ~36 MB), and (4) opens + parses the audio file a second time (AdvancedTagIO.ReadAll). Every 'Edit metadata' click therefore stalls the window for the combined IO+parse+decode time — typically tens to hundreds of ms on an SSD, seconds on HDD/NAS.

Proposed fix: Move the ctor loads into an async initialization: construct the VM with cheap in-memory state, show the window, then populate FileInfo/Artwork/Advanced fields from `await Task.Run(...)` (ArtworkPreview via Bitmap.DecodeToWidth at display size instead of full-res new Bitmap).

Risk if we fix it: Medium. Fields briefly show empty/loading on open, and Save paths compare against _originalAdvancedFields/_loadedTagSignatures which must not run before the async load completes — needs a loaded gate like SettingsViewModel's _settingsLoaded.

---

### [M7] RemoveLyrics blocks the UI thread with .Wait() on file deletes plus an OS trash operation (child process with 15s timeout on macOS/Linux)
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/LyricsViewModel.cs:1407-1408, 1425, 1449, 1457`
  ```csharp
  [RelayCommand]
  private void RemoveLyrics()
  ...
      EnqueueLyricsFileWork(() =>
      ...
          if (!File.Exists(lrcPath) || TrashSidecarFile(lrcPath))
      ...
      }).Wait();
  ```
- `src/Noctis/ViewModels/LyricsViewModel.cs:1382-1388`
  ```csharp
  internal static Task EnqueueLyricsFileWork(Action work)
  {
      lock (_lyricsWriteQueueLock)
      {
          var task = _lyricsWriteQueue.ContinueWith(
              _ => work(), CancellationToken.None,
              TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
  ```
- `src/Noctis/Helpers/RecycleBin.cs:190-196`
  ```csharp
  using var p = Process.Start(psi);
  if (p == null) return false;
  if (!p.WaitForExit(15000))
  {
      try { p.Kill(true); } catch { /* best effort */ }
      return false;
  }
  ```
- `src/Noctis/ViewModels/MainWindowViewModel.cs:2050`
  ```csharp
  removeLyrics: () => _lyricsVm.RemoveLyricsCommand.Execute(null),
  ```

Why it matters: RemoveLyrics is a sync [RelayCommand] executed from the lyrics page top-bar action (MainWindowViewModel.cs:2050), i.e. on the UI thread. The .Wait() at line 1457 blocks that thread until (a) every previously queued item on the FIFO writer lane drains (the lane is deliberately ordered behind in-flight lyrics writes), and (b) the deletes plus TrashSidecarFile complete. TrashSidecarFile is RecycleBin.TryMoveToTrash: SHFileOperation on Windows (can take hundreds of ms; recycle on slow/AV-scanned or network volumes is slow), and on macOS/Linux an osascript/gio child process that is waited on for up to 15 seconds — so a hung Finder/gio freezes the whole window for 15s.

Proposed fix: Make RemoveLyrics an async Task command: replace `EnqueueLyricsFileWork(...).Wait()` with `await EnqueueLyricsFileWork(...)`. The removal-stamp + lane-FIFO ordering guarantees are unchanged; the in-memory reset code after the await still runs after the deletes, on the UI context.

Risk if we fix it: Low. The method's own comments say ordering is enforced by the stamp and the lane, not by the synchronous wait; the only behavioral change is that the UI stays responsive while the deletes run. Re-entry during the await is possible but harmless (stamp bump + idempotent deletes).

---

### [M8] EqVisualizer: 60fps DispatcherTimer animates layout Height, keyed to GLOBAL play state, keeps running on hidden rows
Severity: medium
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/Controls/EqVisualizer.axaml.cs:112-123`
  ```csharp
  private DispatcherTimer EnsureTimer()
  {
      if (_animTimer == null)
      {
          _animTimer = new DispatcherTimer(DispatcherPriority.Render)
          {
              Interval = TimeSpan.FromMilliseconds(16)
          };
  ```
- `src/Noctis/Controls/EqVisualizer.axaml.cs:173-180`
  ```csharp
  private static void SetBar(Rectangle? bar, double t, int idx)
  {
      if (bar == null) return;
      var s = Math.Sin(2 * Math.PI * Frequencies[idx] * t + Phases[idx]);
      // Map sin in [-1,1] to [MinHeight, MaxHeight].
      var h = BarMin + (BarMax - BarMin) * (s * 0.5 + 0.5);
      bar.Height = h;
  }
  ```
- `src/Noctis/Views/AlbumDetailView.axaml:696-702`
  ```xml
  IsPlaying="{Binding $parent[UserControl].((vm:AlbumDetailViewModel)DataContext).IsPlayerPlaying, Mode=OneWay}">
      <controls:EqVisualizer.IsVisible>
          <MultiBinding Converter="{StaticResource GuidEquals}" Mode="OneWay">
              <Binding Path="Id" />
              <Binding Path="$parent[UserControl].((vm:AlbumDetailViewModel)DataContext).CurrentPlayingTrackId" />
  ```
- `src/Noctis/Views/LibraryFoldersView.axaml:150-151`
  ```xml
  IsVisible="{Binding IsNowPlaying}"
  IsPlaying="{Binding $parent[UserControl].((vm:LibraryFoldersViewModel)DataContext).Player.IsPlaying, Mode=OneWay}" />
  ```
- `src/Noctis/Controls/EqVisualizer.axaml.cs:70-77`
  ```csharp
  protected override void OnAttachedToLogicalTree(...)
  {
      base.OnAttachedToLogicalTree(e);
      // Recycled rows re-attach without a template re-apply or an IsPlaying
      // change; restart the oscillation or the bars come back frozen.
      if (_initialized && IsPlaying)
          StartAnimating();
  }
  ```
  _Verifier line corrections: All original citations accurate. Correction to the 'why': Controls/EqVisualizer.axaml:5-6 fixes the control at Width=34/Height=22 and line 16 fixes BarsPanel Height=12, so bar-Height changes invalidate measure only within the control's subtree — the 'forces a layout pass per frame for the whole window / up the ancestor chain' claim is overstated. A layout pass still executes per 16ms tick and LayoutUpdated subscribers in the window (AlbumDetailView.axaml.cs:36 and :38, confirmed) are re-raised per pass, so the secondary cost is real but smaller than stated. Timers accumulate only while playback continues (pause flips IsPlaying globally, flattening and stopping all initialized instances) and are all released on page navigation via detach._

Why it matters: Three compounding problems. (1) The animation writes Rectangle.Height — a layout property — 5 bars per tick at 60fps, so every tick invalidates measure up the ancestor chain and forces a layout pass per frame for the whole window while the playing row is on screen (each pass also re-raises every LayoutUpdated subscription in the window, e.g. AlbumDetailView.axaml.cs:35-38 arrow handlers and the per-row title-cell handlers). (2) IsPlaying is bound to the GLOBAL player state (AlbumDetailViewModel.cs:182 'IsPlayerPlaying = _player.State == PlaybackState.Playing'), not to per-row now-playing; only IsVisible is per-row. Nothing in EqVisualizer stops the timer when it becomes invisible (StopAnimating is only called on detach, IsPlaying=false, or flatten-complete). So once a row's EQ has been initialized (it was visible when its track played), a track change flips IsVisible off but leaves the 16ms timer running on the now-hidden control. In AlbumDetailView the track list is deliberately non-virtualized (StackPanel ItemsPanel, lines 552-555), so listening through an album accumulates one hidden 60fps layout-invalidating timer per previously-played track until the page is left. (3) In the virtualized Folders list, OnAttachedToLogicalTree restarts the oscillation on any recycled, already-initialized instance whenever the global IsPlaying is true — including containers reused for non-playing tracks whose EQ is invisible. The saving grace (why 'likely' not 'confirmed'): Avalonia does not apply templates to never-measured invisible controls, so instances that were never visible stay uninitialized and never start timers; the leak is limited to instances that were visible at least once.

Proposed fix: In EqVisualizer, stop/park the timer when the control is not effectively visible: handle IsVisible/IsEffectivelyVisible changes (or check IsEffectivelyVisible at the top of OnAnimTick and StopAnimating when false). Additionally, drive the bars via RenderTransform (ScaleTransform.ScaleY on fixed-height bars) instead of Height so the animation is render-only and never schedules layout passes.

Risk if we fix it: Visibility gating is low-risk but must re-start the timer when the control becomes visible again (mirror the recycled-row restart comment at lines 72-76 — that path exists precisely because missed restarts froze bars). Switching Height to ScaleY changes the bar look (scaling stretches from center/edge; needs RenderTransformOrigin at bottom) and interacts with the pause-flatten easing — verify visually.

---

### [M9] ServerView albums grid unvirtualized; load-more pages accumulate realized 512px-image tiles
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Views/ServerView.axaml:33-40`
  ```xml
  <ScrollViewer Grid.Row="1" HorizontalScrollBarVisibility="Disabled">
      <StackPanel Margin="0,0,10,115">
          <ItemsControl ItemsSource="{Binding Albums}">
              <ItemsControl.ItemsPanel>
                  <ItemsPanelTemplate>
                      <WrapPanel/>
                  </ItemsPanelTemplate>
              </ItemsControl.ItemsPanel>
  ```
- `src/Noctis/Views/ServerView.axaml:64-66`
  ```xml
  <ctrl:CachedImage SourcePath="{Binding ArtworkPath}"
                    DecodeWidth="512"
                    Stretch="UniformToFill"
  ```
- `src/Noctis/ViewModels/ServerViewModel.cs:32, 202-206`
  ```csharp
  private const int AlbumPageSize = 60;
  ...
  HasMoreAlbums = page.Count == AlbumPageSize;
  ...
      Albums.Add(tile);
  ```
  _Verifier line corrections: src/Noctis/ViewModels/ServerViewModel.cs:32,202,206 (evidence range '202-206' is right: 202 HasMoreAlbums check, 206 Albums.Add); src/Noctis/Controls/CachedImage.cs:122-132 (Source pinning); ServerViewModel.cs:150,351 (only Clear sites, neither on load-more or navigation)_

Why it matters: The server albums grid is a plain ItemsControl with a WrapPanel inside a ScrollViewer — no virtualization, unlike the local Albums view which uses the row-virtualized ListBox pattern (LibraryAlbumsView.axaml:34 comment: 'outer ListBox virtualizes rows'). Pages of 60 tiles accumulate in `Albums` with each Load More, and every accumulated tile stays realized: a 188px Button with a DecodeWidth=512 CachedImage (~1MB bitmap each, pinned by the Image.Source reference even if the LRU evicts its cache entry). For a Jellyfin/Navidrome library browsed a few hundred albums deep, each further page makes every WrapPanel measure/arrange pass proportionally slower and holds hundreds of MB of decoded bitmaps. Situational (server mode + deep paging), hence medium.

Proposed fix: Reuse the local albums pattern: chunk server albums into fixed-column rows hosted in a virtualized ListBox (or ItemsRepeater with a uniform-grid layout), keeping the existing load-more trigger. Also consider DecodeWidth 256 for 164px tiles.

Risk if we fix it: Row-chunking requires responsive column count handling (current WrapPanel adapts to width automatically; UniformGrid rows need a column count source). Load-more scroll-position preservation must be retested.

---

### [M10] Ungated INFINITE loading-spinner animations run while hidden; MainWindow instance runs for app lifetime
Severity: medium
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/Views/MainWindow.axaml:1137, 1149-1161`
  ```xml
  <Border IsVisible="{Binding IsDropImporting}"
  ...
  <Style Selector="Ellipse.loading-spinner">
      ...
      <Style.Animations>
          <Animation Duration="0:0:0.85" IterationCount="INFINITE" Easing="LinearEasing">
              <KeyFrame Cue="0%"><Setter Property="RotateTransform.Angle" Value="0"/></KeyFrame>
              <KeyFrame Cue="100%"><Setter Property="RotateTransform.Angle" Value="360"/></KeyFrame>
          </Animation>
      </Style.Animations>
  ```
- `src/Noctis/Views/LyricsView.axaml:945-949`
  ```xml
  <!-- Animations keyed on [IsVisible=True] so they only run while
       searching (same pattern as the previous pulse text). -->
  <Style Selector="StackPanel#SearchingIndicator[IsVisible=True] Ellipse.dot1">
      <Style.Animations>
          <Animation Duration="0:0:0.9" IterationCount="INFINITE"
  ```
- `src/Noctis/Views/SettingsView.axaml:43, 51-52`
  ```xml
  <Style Selector="Ellipse.loading-spinner">
  ...
  <Style.Animations>
      <Animation Duration="0:0:0.85" IterationCount="INFINITE" Easing="LinearEasing">
  ```
  _Verifier line corrections: src/Noctis/Views/MainWindow.axaml:1137, 1149-1166; src/Noctis/Views/SettingsView.axaml:43-56 with instances at 1090, 1250, 2504, 2681 (4, not 5); src/Noctis/Views/MetadataWindow.axaml:17 with instances at 477, 512, 947, 1046, 1238, 1323 (6, not 7); src/Noctis/Views/LyricShareDialog.axaml:110, 390; gated counter-pattern at src/Noctis/Views/LyricsView.axaml:945-949._

Why it matters: The spinner styles' selectors (`Ellipse.loading-spinner`) match on class only, so the INFINITE rotation animation is active whenever the Ellipse is in the visual tree, regardless of the IsVisible binding on the Ellipse or its parent — style selector matching does not consider visibility. The repo itself documents this exact hazard: LyricsView gates its infinite dot animations with `[IsVisible=True]` explicitly 'so they only run while searching'. The MainWindow drop-import spinner (line 1166) lives directly in the always-attached main window behind an IsVisible=false Border, so its animation clock ticks and writes RotateTransform.Angle every frame for the entire app session, keeping the animation/render timer busy when the app is otherwise idle (battery/CPU baseline). The same ungated pattern exists in SettingsView (5 instances, e.g. lines 1090, 2504, 2681 — all IsVisible-bound but animated regardless while Settings is open), MetadataWindow (7 instances), and LyricShareDialog (transient, minor).

Proposed fix: Gate each spinner animation on visibility, mirroring the LyricsView pattern: change the selector to `Ellipse.loading-spinner[IsVisible=True]` (and for spinners hidden via a parent, bind the Ellipse's own IsVisible to the same condition as the parent so the selector can see it, e.g. MainWindow's to IsDropImporting).

Risk if we fix it: Minimal: when visibility flips on, the animation restarts from 0 degrees instead of resuming mid-rotation — imperceptible for a spinner. Must ensure the IsVisible condition reaches the Ellipse itself where only an ancestor is currently bound.

---

### [M11] AlbumDetailView _bgHandler is never re-wired on in-place VM swap — old AlbumDetailViewModel in navigation history roots the discarded view
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Views/AlbumDetailView.axaml.cs:321-336`
  ```csharp
  protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
  {
      base.OnAttachedToVisualTree(e);
      if (DataContext is AlbumDetailViewModel vm2)
      {
          ...
          _bgHandler = (_, args) =>
          {
              if (args.PropertyName == nameof(AlbumDetailViewModel.BackgroundBrush))
                  AlbumGradientBg.Opacity = ((AlbumDetailViewModel)DataContext!).BackgroundBrush != null ? 1 : 0;
          };
          vm2.PropertyChanged += _bgHandler;
  ```
- `src/Noctis/Views/AlbumDetailView.axaml.cs:275-287`
  ```csharp
  /// When Avalonia reuses this view across AlbumDetailViewModel swaps (e.g. clicking
  /// an album in the Other Versions / More By Artist sections), neither
  /// OnDetachedFromVisualTree nor OnAttachedToVisualTree fire — so the underlying
  /// ScrollViewer keeps the previous album's physical scroll offset. ...
  private void OnAlbumDataContextChanged(object? sender, EventArgs e)
  {
      if (_trackedVm != null)
          _trackedVm.SavedScrollOffset = TrackScrollViewer.Offset.Y;
  ```
- `src/Noctis/Views/AlbumDetailView.axaml.cs:244-252`
  ```csharp
  protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
  {
      CancelPendingScrollRestore();
      if (_bgHandler != null && DataContext is AlbumDetailViewModel bgVm)
      {
          bgVm.PropertyChanged -= _bgHandler;
          _bgHandler = null;
      }
  ```
- `src/Noctis/ViewModels/MainWindowViewModel.cs:1862-1875 and 1361-1363`
  ```csharp
  var detail = new AlbumDetailViewModel(album, Player, _persistence, _library, Sidebar, _lastFm, Settings);
  ...
  // (DisposeViewIfTransient skips anything still in history), so they stay
  // subscribed to LibraryUpdated ...
  private const int MaxNavigationHistory = 30;
  ```

Why it matters: AlbumDetailView is NOT in App.axaml.cs's CachedViewLocator (which lists only the singleton section views), so it is presenter-managed. When the user clicks an album in "Other Versions" / "More By Artist", MainWindowViewModel creates a NEW AlbumDetailViewModel (line 1862) while the SAME attached view is reused with only a DataContext swap — the file's own comment (lines 275-281) documents that neither attach nor detach fires. _bgHandler is subscribed only in OnAttachedToVisualTree (to the FIRST VM, A) and unsubscribed only in OnDetachedFromVisualTree from the CURRENT DataContext (the LAST VM, B). OnAlbumDataContextChanged (282-319) contains no _bgHandler code. So after A→B swap plus navigate-away: the unsubscribe targets B (a no-op) and VM A — retained alive in the navigation history, still subscribed to LibraryUpdated and player PropertyChanged per PlaylistViewModel/AlbumDetailViewModel design — keeps _bgHandler in its PropertyChanged invocation list. The lambda captures the view (AlbumGradientBg, DataContext), so the entire discarded AlbumDetailView visual tree (virtualized track ListBoxes, related-album carousels) stays rooted until VM A is evicted from the 30-entry history and disposed (AlbumDetailViewModel.Dispose, line 856, removes its own outbound subscriptions but cannot remove other objects' subscriptions TO it — the view is freed only when the VM itself becomes garbage). Secondary defect: VM B (and every later swapped-in VM) is never monitored, so a BackgroundBrush change on the visible album would not update AlbumGradientBg. Today BackgroundBrush is only ever assigned null (ViewModels/AlbumDetailViewModel.cs line 350, field line 64), so the visual half is latent — the leak edge is the live problem.

Proposed fix: Move the _bgHandler wiring into OnAlbumDataContextChanged so it follows the DataContext like the scroll logic does: before `_trackedVm = newVm`, run `if (_trackedVm != null && _bgHandler != null) _trackedVm.PropertyChanged -= _bgHandler;` then subscribe the handler to newVm (creating it once). In OnDetachedFromVisualTree, unsubscribe from `_trackedVm` (the VM actually holding the handler) instead of `DataContext`.

Risk if we fix it: Low. The handler only toggles AlbumGradientBg.Opacity; re-wiring per DataContext change matches the pattern already used by PlaylistView/LibraryFoldersView. Ensure the initial `vm2.BackgroundBrush != null` opacity seed also runs on swap.

---

### [M12] Album art is persisted at original resolution with no downscale — unbounded artwork directory and maximum-cost decodes; extractor deliberately picks the largest embedded payload
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/PersistenceService.cs:334-347`
  ```csharp
  public void SaveArtwork(Guid albumId, byte[] imageData)
  {
      ...
      var path = GetArtworkPath(albumId);
      File.WriteAllBytes(path, imageData);
  }
  ```
- `src/Noctis/Services/MetadataService.cs:258-271`
  ```csharp
  public byte[]? ExtractAlbumArt(string filePath)
  {
      // 1. Try embedded artwork first (most reliable), unless disabled in Settings.
      // Prefer FrontCover if present; within each bucket pick largest payload.
      ...
      var bestEmbedded = SelectBestEmbeddedPicture(file.Tag.Pictures);
      if (bestEmbedded != null)
          return bestEmbedded;
  ```
- `src/Noctis/Services/LibraryService.cs:672-675`
  ```csharp
  var artBytes = _metadata.ExtractAlbumArt(rep.FilePath);
  if (artBytes is { Length: > 0 })
  {
      _persistence.SaveArtwork(g.Key, artBytes);
  ```
  _Verifier line corrections: src/Noctis/Services/PersistenceService.cs:334-352 (raw File.WriteAllBytes at :346); src/Noctis/Services/MetadataService.cs:340-370 (largest-payload selection); LibraryService.cs:856 is the drop-import save path, not a metadata-refresh path; remote-fetch path MediaServerService.cs:203-205 is the only size-bounded save._

Why it matters: Every album's cover is written to disk byte-for-byte as extracted, and the selector explicitly prefers the largest embedded payload. Nothing in the write path (SaveArtwork, scan-time save at LibraryService.cs:300-305, metadata-refresh save at :851-856) resizes or re-encodes. The on-disk artwork store therefore scales with album count x original cover size with no cap (earlier real-library observation recorded in project notes: 8.8 GB for ~1,079 covers, single 57 MB cover — treat as an estimate/prior observation, not re-measured here). The in-memory side is fine (ArtworkCache decodes via Bitmap.DecodeToWidth at <=512px with a 256 MB LRU budget, ArtworkCache.cs:47-50,140), but every cache miss must still open and decode the original full-size image before downscaling, so grid scrolling past the cache budget pays full-size JPEG/PNG decode cost per tile.

Proposed fix: Downscale/re-encode at save time in SaveArtwork (e.g. cap the longest edge at ~1024px, JPEG re-encode) — one central choke point already exists; optionally a one-off migration pass over the existing artwork directory.

Risk if we fix it: Moderate: lossy re-encode is irreversible for users who want original-quality covers (share-card/poster features render covers large — verify their source); a settings opt-out or a higher cap mitigates. Migration must not touch user-supplied custom art if stored in the same directory.

---

### [M13] Albums-view search re-normalizes (Unicode FormD fold) every track title and artist on each keystroke instead of using the cached per-track search keys
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs:529-533`
  ```csharp
  filtered = filtered.Where(a =>
      MatchesSearch(a.Name, q, qNoSpaces) ||
      MatchesSearch(a.Artist, q, qNoSpaces) ||
      a.Tracks.Any(t => MatchesSearch(t.Title, q, qNoSpaces) ||
                        MatchesSearch(t.Artist, q, qNoSpaces)));
  ```
- `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs:1096-1100`
  ```csharp
  if (source.Contains(query, StringComparison.OrdinalIgnoreCase))
      return true;
  
  if (RemoveWhitespace(source).Contains(queryNoSpaces, StringComparison.OrdinalIgnoreCase))
      return true;
  ```
- `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs:1125-1130`
  ```csharp
  var trackArtistRank = album.Tracks.Count == 0
      ? 1000
      : album.Tracks.Min(t => RankMatch(t.Artist, query, queryNoSpaces));
  var trackTitleRank = album.Tracks.Count == 0
      ? 1000
      : album.Tracks.Min(t => RankMatch(t.Title, query, queryNoSpaces));
  ```
- `src/Noctis/Models/Track.cs:51-56`
  ```csharp
  /// Search used to re-normalize Title/Artist/Album twice per matching track per
  /// keystroke — hundreds of thousands of throwaway strings at 100k tracks. Costs
  /// three short strings per track; the setters above invalidate on metadata edits.
  /// </summary>
  [JsonIgnore] public string SearchTitleKey => _searchTitleKey ??= Helpers.SearchText.Normalize(_title);
  ```
  _Verifier line corrections: src/Noctis/ViewModels/LibraryAlbumsViewModel.cs:1256 (RemoveWhitespace => SearchText.Normalize) is the load-bearing line and should be added to the evidence; background execution is ThreadPool.QueueUserWorkItem at :456, not Task.Run._

Why it matters: SearchText.Normalize does a FormD Unicode decomposition plus per-char category checks and a StringBuilder allocation per call (Helpers/SearchText.cs:19-36). In the Albums view, MatchesSearch calls RemoveWhitespace(source) (= Normalize) for every track title and artist that misses the fast Contains path, and GetAlbumSearchRank runs two more Min scans over album.Tracks whose RankMatch normalizes each string again (line 1142). Per debounced keystroke over 50k tracks that is on the order of 100k-400k Normalize calls (estimate). The work runs off the UI thread with a generation guard (RebuildFilteredRows, lines 442-482) so it will not freeze the UI, but it burns CPU/GC and delays search results appearing. LibrarySongsViewModel was already converted to the cached Track.SearchTitleKey/SearchArtistKey for exactly this reason (its comment at lines 580-583 quantifies the problem) — the Albums view was not.

Proposed fix: Use t.SearchTitleKey / t.SearchArtistKey in LibraryAlbumsViewModel.MatchesSearch/RankMatch (both already exist and invalidate on metadata edits), and add an Album-level cached key for Name/Artist.

Risk if we fix it: Low: same normalization function, same semantics; Songs view proves the pattern. Ranking ties must remain stable.

---

### [M14] Command palette Refresh scans all tracks/albums/artists synchronously on the dispatcher thread, allocating a PaletteItem and doing a resource lookup per match
Severity: medium
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/CommandPaletteViewModel.cs:49-54`
  ```csharp
  // Debounced. Refresh() scans _library.Tracks, .Albums and .Artists in full,
  // allocating a PaletteItem per match and then sorting — on the dispatcher thread,
  // synchronously, once per character. On a 50k-track library that is ~50k string
  // comparisons plus allocations per keystroke, so typing in Ctrl+K stuttered.
  // 200ms matches the debounce every other search surface in the app already uses.
  ```
- `src/Noctis/ViewModels/CommandPaletteViewModel.cs:154-166`
  ```csharp
  foreach (var track in _library.Tracks)
  {
      var score = MatchScore(track.Title, query);
      if (score <= 0) continue;
      var t = track;
      scored.Add((new PaletteItem
      {
          Title = t.Title,
          Subtitle = $"{t.ArtistDisplay} · Song",
          Category = "Song",
          Icon = Icon("SongsIcon"),
  ```
- `src/Noctis/ViewModels/CommandPaletteViewModel.cs:78-79`
  ```csharp
  private static object? Icon(string key) =>
      Application.Current?.TryFindResource(key, out var res) == true ? res : null;
  ```
- `src/Noctis/ViewModels/CommandPaletteViewModel.cs:199-203`
  ```csharp
  foreach (var (item, _) in scored
               .OrderByDescending(x => x.Score)
               .ThenBy(x => x.Item.Title, StringComparer.OrdinalIgnoreCase)
               .Take(MaxResults))
      Results.Add(item);
  ```
  _Verifier line corrections: src/Noctis/ViewModels/CommandPaletteViewModel.cs:199-203 — note .NET 8 LINQ OrderBy+Take does a partial (not full) sort, though all matches are still buffered and keyed; add src/Noctis/Views/CommandPaletteDialog.axaml:36-37 (TwoWay TextBox binding) as proof the refresh originates on the UI thread._

Why it matters: The 200 ms debounce reduced how often Refresh runs, but each Refresh is still a synchronous dispatcher-thread pass over every track, album and artist. For a broad query (a user pausing after typing one or two characters), tens of thousands of tracks can match: each match allocates a PaletteItem, an interpolated subtitle string, a closure, and calls Application.TryFindResource (a resource-dictionary walk) — then the full match set is sorted just to Take(MaxResults). The stall is per-pause rather than per-character now, but it is still a full-library UI-thread scan whose cost scales with matches, and MaxResults could be selected with a bounded top-K instead of a full sort. Estimate: a 1-char query matching 20k tracks means 20k allocations + 20k resource lookups + a 20k-element sort on the UI thread.

Proposed fix: Hoist the category icon lookups out of the loop (they are constant per category), build only (Track, score) tuples during scanning and materialize PaletteItems just for the final Take(MaxResults), keep a running top-K (e.g. bounded insertion) instead of sorting all matches, and move the scan into Task.Run with a generation guard like every other search surface.

Risk if we fix it: Low for the icon/top-K changes; the Task.Run change needs the same stale-result guard the other views use (pattern already established).

---

### [M15] Playlist import fuzzy matching is O(entries x library) full Levenshtein per pair — the length-based early-out its comment claims is not implemented, and cancellation is never checked
Severity: medium
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/Services/FuzzyTrackMatcher.cs:85-94`
  ```csharp
  Track? best = null;
  double bestScore = 0;
  foreach (var n in norm)
  {
      var titleSim = Ratio(et, n.title);
      if (titleSim < 0.5) continue; // cheap prune: titles must be in the ballpark
      var artistSim = ea.Length == 0 || n.artist.Length == 0 ? 0.5 : Ratio(ea, n.artist);
      var score = 0.65 * titleSim + 0.35 * artistSim;
      if (score > bestScore) { bestScore = score; best = n.track; }
  }
  ```
- `src/Noctis/Services/FuzzyTrackMatcher.cs:143-152`
  ```csharp
  private static int Levenshtein(string a, string b)
  {
      // Length-based early-out: if lengths differ by more than half the longer length,
      // the ratio can't clear our prune threshold anyway.
      var n = a.Length;
      var m = b.Length;
      if (n == 0) return m;
      if (m == 0) return n;
  
      var prev = new int[m + 1];
  ```
- `src/Noctis/Services/PlaylistImportService.cs:23-27`
  ```csharp
  var library = _library.Tracks.ToList();
  return Task.Run(() =>
  {
      var parsed = PlaylistImportParser.Parse(filePath);
      var matches = FuzzyTrackMatcher.Match(parsed.Entries, library);
  ```
  _Verifier line corrections: Add: src/Noctis/ViewModels/PlaylistImportViewModel.cs:46 — the only caller invokes AnalyzeAsync(path) with no CancellationToken, so cancellation is unavailable end-to-end. All original citations correct as given (FuzzyTrackMatcher.cs:85-94, 143-152; PlaylistImportService.cs:23-27)._

Why it matters: The 'cheap prune' at line 90 is not cheap: Ratio() unconditionally computes the full O(|a| x |b|) dynamic-programming Levenshtein (allocating two int arrays per call) before the 0.5 threshold is tested. The comment inside Levenshtein describes a length-difference early-out ('if lengths differ by more than half the longer length, the ratio can't clear our prune threshold') but the code below it never performs that check — the described optimization is missing. Every import entry that fails the path/filename and exact-key lookups therefore runs a full Levenshtein against all 50k library titles. Estimate: a 1,000-entry text/csv playlist with mostly-unmatched entries is ~50M Levenshtein computations (~billions of cell updates plus ~100M short-lived int[] allocations) — minutes of background CPU. It runs inside Task.Run so the UI stays alive, but the CancellationToken is only used to schedule the task; the loops never observe it, so a closed dialog cannot stop the burn.

Proposed fix: Implement the documented length early-out in Ratio/Levenshtein (if abs(n-m)/max > 0.5 return 0 before allocating), reuse the two int arrays across calls, thread ct.ThrowIfCancellationRequested into the per-entry loop, and optionally index candidates by first-letter/length bucket to shrink the scan set.

Risk if we fix it: Low: the early-out is mathematically implied by the existing 0.5 prune (dist >= |n-m| so ratio <= 1 - |n-m|/max); array reuse is single-threaded within Match. Cancellation change only affects an operation the user already abandoned.

---

### [M16] SQLite tracks mirror is write-only (nothing ever SELECTs it) yet is fully rewritten row-by-row after every scan; startup still parses the entire library JSON
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/SqliteLibraryIndexService.cs:172-198`
  ```csharp
  foreach (var track in tracks)
  {
      ct.ThrowIfCancellationRequested();
      pId.Value = track.Id.ToString("N");
      ...
      await cmd.ExecuteNonQueryAsync(ct);
  }
  ```
- `src/Noctis/Services/SqliteLibraryIndexService.cs:241`
  ```csharp
  cmd.CommandText = "SELECT COUNT(*) FROM tracks;";
  ```
- `src/Noctis/Services/LibraryService.cs:539`
  ```csharp
  await _sqliteIndex.ReplaceAllAsync(_tracks, ct);
  ```
- `src/Noctis/Services/PersistenceService.cs:246-254`
  ```csharp
  var tracks = new List<Track>();
  await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
      FileShare.Read, bufferSize: 65536, useAsync: true);
  await foreach (var track in JsonSerializer.DeserializeAsyncEnumerable<Track>(stream, JsonOptions))
  ```
  _Verifier line corrections: Add src/Noctis/Services/LibraryService.cs:1404 (RebuildIndexAsync) as a fourth ReplaceAllAsync call site; note the loop is inside a single transaction (src/Noctis/Services/SqliteLibraryIndexService.cs:105 BeginTransactionAsync, :200 CommitAsync), so write amplification is per-row command execution, not per-row WAL commits._

Why it matters: Grep over the repo shows the only reads of the `tracks` table are SELECT COUNT(*) (line 241) — every consumer of track data goes through the in-memory List<Track> loaded from library.json. Yet ReplaceAllAsync (DELETE FROM tracks + one awaited ExecuteNonQueryAsync per track) runs after every completed scan (LibraryService.cs:539), every scan checkpoint (:609), and every relocate (:1049). At 50k tracks that is 50k awaited single-row commands per scan (estimate: several seconds of background DB work plus WAL churn) maintaining a mirror nothing reads. Meanwhile the class doc calls it the 'scalable backing store for large libraries', but startup remains a full deserialize of every Track from JSON (streaming, so O(1) buffer but O(n) allocation/parse) — the SQLite index does not reduce startup cost at all. This is dead-weight write amplification for the stated 50k+ target: the user-state journal (track_user_state) is the only part of library.db that is actually read.

Proposed fix: Either implement the read path (load tracks from SQLite at startup and demote library.json to backup/export) or stop mirroring scan results entirely and keep only the user-state journal. If the mirror stays, batch inserts with multi-row VALUES or a prepared statement loop without per-row await overhead.

Risk if we fix it: High for the read-path option (migration, corruption fallback, ordering semantics all move); near-zero for deleting the unread mirror writes, but that forecloses the future read path the table was built for — a product decision.

---

### [M17] Track.PrimaryArtist is an uncached Regex.Split+LINQ property evaluated once per track in loops that repeat every 1.5 s during scans
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Models/Track.cs:487-489`
  ```csharp
  /// <summary>Primary display artist derived from the first credited artist token.</summary>
  [JsonIgnore]
  public string PrimaryArtist => GetPrimaryArtist(Artist);
  ```
- `src/Noctis/Services/LibraryService.cs:1525-1529`
  ```csharp
  foreach (var track in tracks)
  {
      var primaryArtist = track.PrimaryArtist;
      if (string.IsNullOrWhiteSpace(primaryArtist))
          primaryArtist = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown Artist" : track.Artist.Trim();
  ```
- `src/Noctis/Services/LibraryService.cs:22, 200-214`
  ```csharp
  private const int ProgressivePublishMs = 1500;
  ...
  while (!pubCt.IsCancellationRequested)
  {
      await Task.Delay(ProgressivePublishMs, pubCt).ConfigureAwait(false);
      ...
      await RebuildIndexesAsync(persistCache: false).ConfigureAwait(false);
      LibraryUpdated?.Invoke(this, EventArgs.Empty);
  }
  ```
- `src/Noctis/ViewModels/HomeViewModel.cs:175-178`
  ```csharp
  foreach (var t in allTracks)
  {
      if (t.PlayCount <= 0) continue;
      var name = t.PrimaryArtist;
  ```
  _Verifier line corrections: Scan publisher rebuild is guarded by a new-tracks check at src/Noctis/Services/LibraryService.cs:205 (and :789 for drop-import) — effectively every 1.5 s only while the scan is producing tracks; the artwork-backfill publisher at LibraryService.cs:648-652 rebuilds unconditionally every 1.5 s. ParseArtistTokens implementation at src/Noctis/Models/Track.cs:704-718; GetPrimaryArtist at Track.cs:695-702; per-album GetPrimaryArtist also in the album sort at LibraryService.cs:1509._

Why it matters: PrimaryArtist runs ParseArtistTokens (interpreted Regex.Split + Select/Where/Distinct/ToArray) on every access with no caching, even though the adjacent SearchTitleKey/SearchArtistKey properties (Track.cs:46-62) demonstrate the repo's lazy-cache-with-setter-invalidation pattern. RebuildIndexesCoreAsync reads it once per track per rebuild, and rebuilds run on every progressive-publish tick (fixed 1.5 s interval, not scaled with library size) from both the scan publisher and the artwork publisher, plus on every removal/import/metadata change. Estimate (labeled as estimate): at 50k tracks that is 50k regex executions plus ~200k+ transient string/array allocations per rebuild, i.e. every 1.5 s for the duration of a scan — sustained background CPU and GC pressure that competes with scan I/O and the UI. HomeViewModel's top-artists aggregation repeats the same per-track cost on every (un-gated) Home refresh.

Proposed fix: Cache the token array / primary artist on Track (invalidated by the Artist setter, exactly like _searchArtistKey), or hoist a per-rebuild Dictionary<string,string> memo keyed by the raw Artist string (few unique values vs. track count).

Risk if we fix it: Low: pure memoization; the Artist setter already exists as the invalidation point. Must keep the '\bx\b' token behavior unchanged (known separate correctness issue with artists like 'Lil Nas X').

---

### [M18] 'Analyze Tempo & Key' toggle is inert when switched on mid-session — analysis only starts on the next LibraryUpdated event
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/SettingsViewModel.cs:2226-2230`
  ```csharp
  partial void OnBpmKeyAnalysisEnabledChanged(bool value)
  {
      if (_suspendSettingPersistence) return;
      _ = SaveAsync();
  }
  ```
- `src/Noctis/App.axaml.cs:139-141`
  ```csharp
  var analysisCoordinator = Services!.GetRequiredService<Noctis.Services.AudioAnalysis.AudioAnalysisCoordinator>();
  var library = Services!.GetRequiredService<ILibraryService>();
  library.LibraryUpdated += (_, _) => analysisCoordinator.StartBackfill();
  ```
- `src/Noctis/Services/AudioAnalysis/AudioAnalysisCoordinator.cs:52-54`
  ```csharp
  public void StartBackfill()
  {
      if (!_settings().BpmKeyAnalysisEnabled || !_analysis.IsAvailable) return;
  ```
  _Verifier line corrections: src/Noctis/ViewModels/SettingsViewModel.cs:2226-2230; src/Noctis/App.axaml.cs:139-141 (sole call site) and 146-150 (no eager call, by design comment); src/Noctis/Services/AudioAnalysis/AudioAnalysisCoordinator.cs:52-67 (gate + single-flight), 90-210 (no mid-pass enabled re-check)_

Why it matters: The change handler only persists the flag. The sole StartBackfill call site in the app is the LibraryUpdated subscription (grep-verified: no other caller outside the coordinator). So a user who enables 'Detect BPM and key in the background' on a static library sees nothing happen — no analysis until a scan, an import, a watcher-triggered library change, or a restart (the initial library load raises LibraryUpdated, per the comment at App.axaml.cs:146-150). The card's description promises background detection starts working; AutoMix/Track Radio quality silently stays degraded for the whole session. Symmetrically, toggling OFF does not Stop() a pass already running (it only stops future files from being applied? No — the enabled check is only at StartBackfill entry, so a running pass completes fully).

Proposed fix: In OnBpmKeyAnalysisEnabledChanged, when value is true resolve the coordinator and call StartBackfill() (it is already a guarded no-op when unavailable/running); when false, optionally call Stop().

Risk if we fix it: Low — StartBackfill is lock-guarded, single-flight, and a no-op when disabled or ffmpeg is missing; calling it from the handler cannot double-run a pass.

---

### [M19] Clear Artwork Cache and Reset Everything perform synchronous recursive directory deletes on the UI thread
Severity: medium
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/SettingsViewModel.cs:3748-3757`
  ```csharp
  [RelayCommand]
  private void ClearArtworkCache()
  {
      try
      {
          var artworkDir = Path.Combine(_persistence.DataDirectory, "artwork");
          if (Directory.Exists(artworkDir))
          {
              Directory.Delete(artworkDir, true);
              Directory.CreateDirectory(artworkDir);
  ```
- `src/Noctis/ViewModels/SettingsViewModel.cs:3457-3462`
  ```csharp
  var artworkDir = Path.Combine(_persistence.DataDirectory, "artwork");
  if (Directory.Exists(artworkDir))
  {
      Directory.Delete(artworkDir, true);
      Directory.CreateDirectory(artworkDir);
      Directory.CreateDirectory(Path.Combine(artworkDir, "artists"));
  ```
  _Verifier line corrections: src/Noctis/ViewModels/SettingsViewModel.cs:3748-3757 (ClearArtworkCache), 3457-3462 (reset artwork delete), 3477/3492/3507/3522 (lyrics_cache, playlist_covers, cache, audit deletes); src/Noctis/Views/SettingsView.axaml:3070,3094 (button bindings)_

Why it matters: ClearArtworkCache is a synchronous RelayCommand executed on the UI thread; Directory.Delete(recursive:true) over the whole artwork cache (one file per album + artists subdir; scales with library size) blocks the dispatcher for the entire delete. ConfirmResetLibrary (async but running its non-awaited statements on the UI context) does the same for artwork, lyrics_cache (3477), playlist_covers (3492), cache (3507) and audit (3522) back-to-back. On an HDD or a multi-thousand-album library this is a multi-second freeze with no progress indication.

Proposed fix: Wrap the delete/recreate blocks in await Task.Run(...) (ClearArtworkCache becomes an async Task command), keeping the SetScanStatus/RefreshStorageInfo tail on the UI thread.

Risk if we fix it: Low — the deletes have no UI-thread dependencies; only re-entrancy (double-click) needs the usual command-disable while running.

---

### [M20] Scan completion walks the entire artwork cache synchronously on the UI thread (Scan Library / Add Folder / Remove Folder / Rebuild Index)
Severity: medium
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/SettingsViewModel.cs:3329, 3339-3346`
  ```csharp
  try { await prior.ConfigureAwait(true); } catch { /* prior was cancelled */ }
  ...
              await _library.ScanAsync(MusicFolders, cts.Token);
              if (cts.IsCancellationRequested) return;
  
              SetScanStatus(...);
              RefreshLibraryStats();
              RefreshStorageInfo(forceRefresh: true);
  ```
- `src/Noctis/ViewModels/SettingsViewModel.cs:3070-3079`
  ```csharp
  public void RefreshStorageInfo(bool forceRefresh = false)
  {
      var dataDir = _persistence.DataDirectory;
      ...
      long artworkSize = GetDirectorySize(Path.Combine(dataDir, "artwork"), forceRefresh);
  ```
- `src/Noctis/ViewModels/SettingsViewModel.cs:3156-3159`
  ```csharp
  long size = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
      .Sum(f => new FileInfo(f).Length);
  ```
  _Verifier line corrections: src/Noctis/ViewModels/SettingsViewModel.cs:3329,3339-3346 (scan tail); 3384 (RebuildIndex tail); 3070-3079 (sync RefreshStorageInfo); 3143-3158 (GetDirectorySize forceRefresh bypass + walk, Sum at 3156-3157); 3092-3123 (unused-here async variant)_

Why it matters: RunScanCoreAsync stays on the UI SynchronizationContext (ConfigureAwait(true) and the UI-bound property writes around it prove the continuation runs on the dispatcher thread). The synchronous RefreshStorageInfo variant with forceRefresh:true bypasses the 5s _dirSizeCache and re-enumerates every file under data/artwork plus a FileInfo.Length stat per file, on the UI thread, at the exact moment the scan finishes. The same call sits in RebuildIndex (line 3384). An async variant that does this work in Task.Run already exists (RefreshStorageInfoAsync, 3092-3123) but these paths do not use it. On a large library (thousands of cached covers, or artwork on a NAS/slow disk) this is a visible freeze right after pressing Scan Library or adding/removing a folder — the same defect class the repo already fixed once for Settings-open (the RefreshStorageInfoAsync doc comment) and for issue #31.

Proposed fix: Replace RefreshStorageInfo(forceRefresh: true) at SettingsViewModel.cs:3346 and 3384 with an awaited/fire-and-forget async variant: add a forceRefresh parameter to RefreshStorageInfoAsync and call `_ = RefreshStorageInfoAsync(forceRefresh: true)`.

Risk if we fix it: Low — the async variant already exists and marshals results back to the UI thread; only the two call sites change. Storage rows update a few hundred ms later, which is invisible.

---

### [M21] ListenBrainz Logout leaves the hidden scrobbling flag armed - typing a token afterwards silently scrobbles with an unvalidated token while the UI shows disconnected
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/SettingsViewModel.cs:2562-2572`
  ```csharp
  private void LogoutListenBrainz()
  {
      _listenBrainz?.Logout();
      IsListenBrainzConnected = false;
      ListenBrainzToken = "";
      ListenBrainzUsername = "";
      ListenBrainzStatusText = "Not connected";
      ListenBrainzError = "";
      _ = SaveAsync();
  }
  ```
- `src/Noctis/ViewModels/SettingsViewModel.cs:2518-2525`
  ```csharp
  partial void OnListenBrainzTokenChanged(string value)
  {
      ListenBrainzError = "";
      // Just keep the in-memory service in sync; the user must hit "Connect"
      // to validate and persist. Don't autosave keystroke-by-keystroke.
      _listenBrainz?.Configure(value);
  }
  ```
- `src/Noctis/Services/ListenBrainzService.cs:29`
  ```csharp
  public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_userToken);
  ```
- `src/Noctis/ViewModels/MainWindowViewModel.cs:2479-2480`
  ```csharp
  if (_listenBrainz.IsAuthenticated && Settings.ListenBrainzScrobblingEnabled)
      _ = _listenBrainz.UpdateNowPlayingAsync(track);
  ```
- `src/Noctis/Views/SettingsView.axaml:2403`
  ```xml
  <Grid IsVisible="{Binding IsListenBrainzConnected}" ColumnDefinitions="*,Auto" Margin="54,0,0,0">
  ```
  _Verifier line corrections: src/Noctis/ViewModels/SettingsViewModel.cs:2563-2572 (LogoutListenBrainz method body; :2562 is the [RelayCommand] attribute). All other citations exact as given: SettingsViewModel.cs:2518-2525 (quote omits the comment at :2520 but is otherwise verbatim), ListenBrainzService.cs:29, MainWindowViewModel.cs:2479-2480 (plus a second identical gate at :2565 feeding ScrobbleAsync at :2588), SettingsView.axaml:2403. Supporting: MainWindowViewModel.cs:113 'public SettingsViewModel Settings { get; }' proves the gate reads the stuck VM property; MainWindowViewModel.cs:252 'Settings.SetListenBrainz(listenBrainz)' proves the keystroke-Configure hits the scrobbling service instance; SettingsViewModel.cs:3696 proves factory reset resets the flag while logout does not._

Why it matters: LogoutListenBrainz clears the token and username but never sets ListenBrainzScrobblingEnabled=false (the only writers are the load path :1031, the connect path :2549, and factory reset :3696). After logout the Enable Scrobbling toggle disappears (IsVisible=IsListenBrainzConnected) while its value stays true, so the user cannot see or change it. Because IsAuthenticated is merely token-non-blank and OnListenBrainzTokenChanged calls Configure() on every keystroke, typing any characters into the token box - without ever clicking Connect - makes the scrobble gate in MainWindowViewModel pass again: now-playing updates and scrobbles are POSTed with the unvalidated token while the card still shows the Connect state (IsListenBrainzConnected stays false because username is empty). A typo'd token that happens to be someone else's valid token posts listens to their account.

Proposed fix: In LogoutListenBrainz set ListenBrainzScrobblingEnabled = false alongside the other clears (mirrors how connect sets it true). Optionally also gate the MainWindowViewModel scrobble checks on the connected/validated state rather than raw token presence.

Risk if we fix it: Low. One extra assignment in the logout path; SaveAsync already runs there. Reconnecting re-sets the flag true at SettingsViewModel.cs:2549, so no user loses scrobbling by the change.

---

### [M22] Case-insensitive path matching on Linux: watcher removals and exclusion sets can hit tracks in a case-differing sibling path
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/LibraryWatcherService.cs:414-421`
  ```csharp
  var removeSet = new HashSet<string>(batch.ToRemove, StringComparer.OrdinalIgnoreCase);
  var dirPrefixes = batch.ToRemoveDirs ...
  var ids = _library.Tracks
      .Where(t => removeSet.Contains(t.FilePath) ||
                  dirPrefixes.Any(p => t.FilePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
  ```
- `src/Noctis/Services/LibraryService.cs:2276-2282`
  ```csharp
  if (normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase))
      return true;
  return normalizedPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || ...
  ```
- `src/Noctis/Services/LibraryService.cs:2171-2172`
  ```csharp
  var visited = new HashSet<string>(
      OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
  ```
  _Verifier line corrections: LoonClient citation is Services/Loon/LoonClient.cs:414-417 (not Services/LoonClient.cs:417); all other file:line citations are accurate as given (src/Noctis/Services/LibraryWatcherService.cs:414-421, src/Noctis/Services/LibraryService.cs:2276-2282, 2171-2172, 131-134, 1063)._

Why it matters: Path comparisons are OrdinalIgnoreCase almost everywhere (watch-batch removals, ExcludedFilePaths sets at LibraryService.cs:131-134 and 1063, exclusion-root matching IsUnderRoot, watcher suppression dictionaries at LibraryWatcherService.cs:35/97/380). On ext4 two sibling paths differing only in case are distinct files/dirs: deleting ~/Music/test/ also removes library tracks under ~/Music/Test/ (RemoveTracksAsync additionally blacklists them in ExcludedFilePaths per the comment at LibraryWatcherService.cs:150-153, so they stay gone after a rescan), and an exclusion rule for /music/rock also excludes /music/Rock. The codebase demonstrably knows the right pattern — the scan cycle guard (2171-2172), AudioConverterService.IsUnder (397-399), and LoonClient (417) all switch comparer on Linux — but the removal/exclusion paths never got it.

Proposed fix: Introduce one PathComparison helper (Linux → Ordinal, else OrdinalIgnoreCase) and use it for removeSet/dirPrefixes in LibraryWatcherService.ApplyBatchAsync, the ExcludedFilePaths HashSets, IsUnderRoot, and the watcher suppression/_lastSeenSize/_importAttempts dictionaries.

Risk if we fix it: Low on Windows/macOS (behavior unchanged); on Linux, previously-case-mismatched exclusion rules would stop matching — that is the correct behavior but could resurface tracks a user excluded with wrong casing.

---

### [M23] macOS 'Move to Trash' drives Finder via osascript but the app bundle declares no NSAppleEventsUsageDescription — TCC may deny it and every trash attempt fails
Severity: medium
Confidence: unverified (verifier could not decide from code alone)
Evidence:
- `src/Noctis/Helpers/RecycleBin.cs:103-108`
  ```csharp
  private static bool MacTrash(string path) => RunProcess(
      "osascript",
      "-e", "on run argv",
      "-e", "tell application \"Finder\" to delete (POSIX file (item 1 of argv) as alias)",
      "-e", "end run",
      path);
  ```
- `.github/workflows/dotnet.yml:193-209`
  ```
  cat > "$APP/Contents/Info.plist" <<PLIST
  ...
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>NSHighResolutionCapable</key><true/>
  </dict>   # ← no NSAppleEventsUsageDescription anywhere
  ```

Why it matters: Since macOS Mojave, sending Apple Events to another app (Finder) requires Automation consent attributed to the responsible app bundle, and the usage-description key governs whether the consent flow can even run. The CI-generated Info.plist contains no NSAppleEventsUsageDescription, so the osascript call may be denied (error -1743) — RunProcess then returns false and every 'Move file to Trash' silently reports 'couldn't trash'. The code fails safe (file untouched, RecycleBin.cs:9-12), so no data loss, but the feature can be dead on macOS.

Proposed fix: Add <key>NSAppleEventsUsageDescription</key> to the CI Info.plist; longer term prefer the NSFileManager trashItemAtURL API (via objc interop) or the same freedesktop-style fallback of moving into ~/.Trash, which needs no Apple Events at all.

Risk if we fix it: Plist addition is zero-risk; switching to ~/.Trash moves must handle cross-volume paths like the Linux fallback already does.

To confirm: A run on macOS 12+: remove a track with 'Move file to Trash' from the .dmg-installed bundle and observe whether a consent prompt appears / the file reaches Trash; or check Console.app for tccd denials. / Run on macOS 12+ from the CI-built .dmg bundle: trigger 'Move file to Trash' and observe whether an Automation consent prompt appears and whether the file reaches Trash; check Console.app/tccd logs for a kTCCServiceAppleEvents denial attributed to com.heartached.noctis.

---

### [M24] macOS has no media-key / Now Playing integration at all (SMTC is Windows-only, MPRIS is Linux-only, nothing fills the gap)
Severity: medium
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/SmtcService.cs:294-297`
  ```csharp
  #else
      public SmtcService(PlayerViewModel player, IntPtr windowHandle) { }
      public void Dispose() { }
  #endif
  ```
- `src/Noctis/Services/MprisService.cs:48-50`
  ```csharp
  public static MprisService? TryStart(PlayerViewModel player)
  {
      if (!OperatingSystem.IsLinux()) return null;
  ```
- `src/Noctis/Views/MainWindow.axaml.cs:303-304`
  ```csharp
  _smtc = new SmtcService(vm.Player, TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
  _mpris = MprisService.TryStart(vm.Player);
  ```

Why it matters: On macOS the SmtcService compiles to an empty stub (plain net8.0 TFM) and MprisService returns null, and no MPNowPlayingInfoCenter/MPRemoteCommandCenter equivalent exists anywhere in the repo (grep for it returns nothing). So hardware media keys, AirPods play/pause, and the Control Center Now Playing widget are completely dead on macOS — the same features the code goes to great lengths to provide on Windows and Linux.

Proposed fix: Add a macOS counterpart service (MPNowPlayingInfoCenter + MPRemoteCommandCenter via objc interop, or a maintained nowplaying-bridge helper) wired at the same MainWindow init point; at minimum document the gap in the README/macOS notes.

Risk if we fix it: New objc-interop surface on macOS only; zero risk to Windows/Linux since it would follow the same TryStart-null pattern.

---

### [M25] TagLibSharp 2.3.0 has an upstream report of corrupting MP4/M4V files when writing tags, and the app's tag editor writes through exactly this path
Severity: medium
Confidence: unverified (web-research finding — version/CVE claims cited, not code-adversarially verified)
Evidence:
- `src/Noctis/Noctis.csproj:86`
  ```xml
  <PackageReference Include="TagLibSharp" Version="2.3.0" />
  ```
- `src/Noctis/Services/MetadataService.cs:465-468`
  ```csharp
  using (var file = TagLib.File.Create(tempPath))
  {
      applyTags(file);
      file.Save();
  ```

Why it matters: mono/taglib-sharp issue #340 reports that TagLibSharp v2.3.0 adds 200-400 bytes of garbage into MP4 atoms when updating Tag.Title on MP4/M4V files. The app's metadata editor saves tags format-agnostically via TagLib.File.Create(...).Save(), and .m4a (MP4 container) is a common music format. The app's temp-copy-then-replace safeguard (MetadataService.cs:445-468) protects against crashes mid-write but NOT against this bug: a corrupted temp copy would replace the good original. No CVE exists; there is no newer TagLibSharp release containing a fix, and upstream has been dormant since 2022.

Proposed fix: Report-only audit: no dependency change available (2.3.0 is the latest release). If confirmed, mitigation would have to be app-side: after Save() on an MP4-container file, re-parse the temp copy with TagLib and verify Properties/duration before replacing the original, aborting the swap on parse failure.

Risk if we fix it: Low — a post-save verification step only adds a read pass; it cannot corrupt anything, worst case it falsely blocks a save.

To confirm: Reproduce upstream issue #340 against a real .m4a with this app's save path (edit title, then byte-compare/parse the MP4 atom tree), since the upstream report is for .mp4/.m4v title updates and may depend on specific atom layouts.

---

### [M26] .NET 8 LTS support ends 2026-11-10 (~3 months away); all three projects target net8.0
Severity: medium
Confidence: confirmed (web-research finding — version/CVE claims cited, not code-adversarially verified)
Evidence:
- `src/Noctis/Noctis.csproj:8-9`
  ```xml
  <TargetFramework Condition="!$([MSBuild]::IsOSPlatform('Windows'))">net8.0</TargetFramework>
  <TargetFramework Condition="$([MSBuild]::IsOSPlatform('Windows'))">net8.0-windows10.0.19041.0</TargetFramework>
  ```
- `tools/NoctisCoverProxy/NoctisCoverProxy.csproj:4`
  ```xml
  <TargetFramework>net8.0</TargetFramework>
  ```
- `tests/Noctis.Tests/Noctis.Tests.csproj:5-6`
  ```xml
  <TargetFramework Condition="!$([MSBuild]::IsOSPlatform('Windows'))">net8.0</TargetFramework>
  <TargetFramework Condition="$([MSBuild]::IsOSPlatform('Windows'))">net8.0-windows10.0.19041.0</TargetFramework>
  ```

Why it matters: Per Microsoft's official support policy (fetched 2026-08-04), .NET 8 LTS ends support 2026-11-10. After that the runtime, the ASP.NET Core 8 shared framework the cover proxy rides on, and the 8.0.x lines of Microsoft.Extensions.*/Microsoft.Data.Sqlite/System.Security.Cryptography.ProtectedData receive no further security fixes. .NET 10 is the current LTS (released 2025-11-11, supported until 2028-11-14); .NET 9 STS ends the same day as .NET 8, so 10 is the only sensible target. Source: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core

Proposed fix: Plan a coordinated net10.0 retarget of all three csproj files (net10.0-windows10.0.19041.0 on Windows), bumping Microsoft.Extensions.DependencyInjection, Microsoft.Data.Sqlite, and System.Security.Cryptography.ProtectedData to their 10.0.x lines in the same pass. Avalonia.Headless.XUnit 11.3.18 already ships a .NET 10 target, suggesting the Avalonia 11.3 line supports it.

Risk if we fix it: Moderate: a TFM bump must be validated across all 5 RIDs and both TFM branches (WinRT/SMTC projections, LibVLC interop, CI legs on macOS/Linux); netstandard2.0 deps (TagLibSharp, LibVLCSharp, NAudio) are unaffected, but this is a release-scale change, not a quick patch.

---

## Low findings

### [L1] Metadata Save path calls RecycleBin.TryMoveToTrash inline on the UI thread when lyrics were removed
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/MetadataViewModel.cs:2031-2032, 2046-2047`
  ```csharp
  else if (SyncedLyricsWereRemoved && File.Exists(lrcPath))
      Helpers.RecycleBin.TryMoveToTrash(lrcPath);
  ...
  else if (PlainLyricsWereRemoved && File.Exists(txtPath))
      Helpers.RecycleBin.TryMoveToTrash(txtPath);
  ```
- `src/Noctis/Helpers/RecycleBin.cs:103-108, 192`
  ```csharp
  private static bool MacTrash(string path) => RunProcess(
      "osascript",
      "-e", "on run argv",
      "-e", "tell application \"Finder\" to delete (POSIX file (item 1 of argv) as alias)",
  ...
  if (!p.WaitForExit(15000))
  ```
  _Verifier line corrections: src/Noctis/ViewModels/MetadataViewModel.cs:2031-2032 and 2046-2047 (trash calls); src/Noctis/ViewModels/MetadataViewModel.cs:1844-1853 ([RelayCommand] Save -> SaveInternalAsync, UI entry point; no ConfigureAwait anywhere in the file); src/Noctis/Helpers/RecycleBin.cs:51-56 (synchronous TrashCore dispatch), 103-108 (osascript/Finder), 190-197 (WaitForExit(15000)); src/Noctis/Helpers/LibraryRemovalHelper.cs:47-48 (off-thread precedent). Note: the finder's argument that 'await File.WriteAllTextAsync proves UI context' is loose — the proof is the UI-thread command entry plus absence of ConfigureAwait(false); conclusion unchanged._

Why it matters: These statements sit between awaits in the async Save flow of MetadataViewModel, which resumes on the UI SynchronizationContext (the surrounding writes at 2030/2045 use await File.WriteAllTextAsync, proving the method is on the UI context). TryMoveToTrash is synchronous: SHFileOperation on Windows, and on macOS/Linux a child process (osascript / gio) waited on for up to 15 seconds. The same helper's cost is why library-file trashing was explicitly moved off-thread (LibraryRemovalHelper.cs:47-48 'Moves the tracks' files to the OS trash off the UI thread'). Only triggers when the user removed lyrics in the editor, so the path is rare.

Proposed fix: Wrap both calls in `await Task.Run(() => Helpers.RecycleBin.TryMoveToTrash(path));`.

Risk if we fix it: Minimal. The calls are already best-effort inside try/catch with no result consumed.

---

### [L2] LibraryPlaylistsView realizes every playlist tile (unvirtualized UniformGrid in ScrollViewer)
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Views/LibraryPlaylistsView.axaml:23-31`
  ```xml
  <ScrollViewer HorizontalScrollBarVisibility="Disabled"
                VerticalScrollBarVisibility="Auto">
      <ItemsControl ItemsSource="{Binding FilteredPlaylists}"
                    Margin="0,0,0,115">
          <ItemsControl.ItemsPanel>
              <ItemsPanelTemplate>
                  <UniformGrid Columns="5" VerticalAlignment="Top" />
              </ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
  ```
  _Verifier line corrections: src/Noctis/Views/LibraryPlaylistsView.axaml:23-31 (as cited); supporting: LibraryPlaylistsView.axaml:148-171 (per-tile CachedImages), src/Noctis/Views/FavoritesView.axaml:23 (virtualized contrast pattern), src/Noctis/Views/LibraryPlaylistsView.axaml.cs:34,78 (scroll-restore migration risk)_

Why it matters: All playlists realize at once (tiles with artwork and context menus). Unlike Albums/Artists/Favorites — which chunk into rows hosted in a virtualized ListBox — this view has no virtualization. Playlist counts are typically tens, so impact is small; it only becomes measurable for power users with hundreds of playlists (first navigation cost and full-grid measure per pass).

Proposed fix: Only if playlist counts warrant it: adopt the same row-chunking + outer virtualized ListBox pattern used by LibraryAlbumsView/FavoritesView.

Risk if we fix it: Requires building a PlaylistRow model and migrating the scroll-restore code in LibraryPlaylistsView.axaml.cs (lines 34/78 target the UserControl's LayoutUpdated). Not worth the churn unless large playlist counts are a real user scenario.

---

### [L3] Per-row LayoutUpdated handlers run LINQ on every layout pass in Songs/Playlist/AddSongs row templates
Severity: low
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/Views/LibrarySongsView.axaml:229-233`
  ```xml
  <Grid Grid.Column="0"
        ColumnDefinitions="Auto,Auto,Auto"
        ClipToBounds="True"
        VerticalAlignment="Center"
        LayoutUpdated="OnTitleCellLayoutUpdated">
  ```
- `src/Noctis/Views/LibrarySongsView.axaml.cs:245-252`
  ```csharp
  var title = titleCell.Children.OfType<TextBlock>().FirstOrDefault();
  if (title == null)
      return;
  ...
  var explicitBadge = titleCell.Children.OfType<Border>()
      .FirstOrDefault(b => b.Classes.Contains("explicit-badge"));
  ```
- `src/Noctis/Views/LibrarySongsView.axaml.cs:262-264`
  ```csharp
  var maxTitleWidth = Math.Max(0, titleCell.Bounds.Width - reservedWidth);
  if (Math.Abs(title.MaxWidth - maxTitleWidth) > 0.5)
      title.MaxWidth = maxTitleWidth;
  ```

Why it matters: In Avalonia, Layoutable.LayoutUpdated subscribes to the LayoutManager and is raised after EVERY completed layout pass of the window, not only when the subscribing control's own layout changed. Each realized song row's title cell (LibrarySongsView.axaml:233, PlaylistView.axaml:900, AddSongsDialog.axaml:185) therefore runs enumerator allocations and LINQ (OfType/FirstOrDefault/Classes.Contains) on every layout pass anywhere — including the per-frame passes generated during scrolling, and the 60fps passes generated by the EqVisualizer finding while music plays. The MaxWidth write additionally invalidates the TextBlock's measure, scheduling one extra layout pass per real width change (bounded — the 0.5px guard makes it converge). Cost per handler is microseconds, so this is hygiene rather than user-visible jank on its own, but it multiplies by ~30 visible rows x layout-pass rate and amplifies any other per-frame layout source.

Proposed fix: Replace the LayoutUpdated subscription with a SizeChanged handler on the title cell (fires only when that cell's bounds actually change, which is the only input the computation uses) plus IsVisible-change of the badge/thumb; or cache the child lookups per Grid (e.g. in Tag) to remove the LINQ from the hot path.

Risk if we fix it: This handler is the shipped fix for issue #30 (title overflow); SizeChanged does not fire when only the badge's visibility changes without a size change, so badge/art visibility toggles need an explicit re-run hook or the ellipsis can go stale. Needs the issue-#30 regression scenario retested.

---

### [L4] LyricsView subscribes three different, inconsistent event sets across OnDataContextChanged / OnAttachedToVisualTree / OnDetachedFromVisualTree — Player.PropertyChanged is missing for the whole first visit and LyricsSwapPending/Swapped are never removed on detach
Severity: low
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/Views/LyricsView.axaml.cs:472-480`
  ```csharp
  if (DataContext is LyricsViewModel vm)   // OnDataContextChanged
  {
      vm.PropertyChanged += OnViewModelPropertyChanged;
      vm.OpenBackgroundColorRequested += OnOpenBackgroundColorRequested;
      vm.LyricsSwapPending += OnLyricsSwapPending;
      vm.LyricsSwapped += OnLyricsSwapped;
      vm.Player.Seeked += OnPlayerSeeked;
      _subscribedVm = vm;
  }
  ```
- `src/Noctis/Views/LyricsView.axaml.cs:210-222`
  ```csharp
  // Re-subscribe on re-attach (detach unsubscribed; DataContextChanged
  // won't fire again when the DataContext is unchanged).
  if (_subscribedVm == null)
  {
      vm.PropertyChanged += OnViewModelPropertyChanged;
      vm.OpenBackgroundColorRequested += OnOpenBackgroundColorRequested;
      vm.Player.PropertyChanged += OnPlayerPropertyChanged;
      vm.Player.Seeked += OnPlayerSeeked;
      _subscribedVm = vm;
  }
  ```
- `src/Noctis/Views/LyricsView.axaml.cs:257-264`
  ```csharp
  if (_subscribedVm != null)   // OnDetachedFromVisualTree
  {
      _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
      _subscribedVm.OpenBackgroundColorRequested -= OnOpenBackgroundColorRequested;
      _subscribedVm.Player.PropertyChanged -= OnPlayerPropertyChanged;
      _subscribedVm.Player.Seeked -= OnPlayerSeeked;
      _subscribedVm = null;
  }
  ```
- `src/Noctis/Views/LyricsView.axaml.cs:674-680`
  ```csharp
  private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
  {
      // The layer's visibility is bound in XAML; this only parks/resumes the timer.
      if (e.PropertyName == nameof(PlayerViewModel.LyricsFlowingLightEnabled) &&
          DataContext is LyricsViewModel vm)
          UpdateMeshAnimationState(vm);
  }
  ```

Why it matters: LyricsView is a cached singleton view (App.axaml.cs CachedViewLocator, line 55) over the singleton LyricsViewModel. The three lifecycle hooks manage three DIFFERENT subscription sets. On first creation, DataContext is assigned before attach, so OnDataContextChanged runs first and sets _subscribedVm — making OnAttachedToVisualTree skip its block. Result: vm.Player.PropertyChanged (the only place OnPlayerPropertyChanged is hooked) is not subscribed during the entire first visit to the lyrics page, so toggling the flowing-light setting from the Settings modal overlay (which covers, not detaches, the page) does not start/stop the mesh drift until the page is left and re-entered. Conversely, OnDetachedFromVisualTree never removes LyricsSwapPending/LyricsSwapped, so while the cached view sits detached, every track change still runs OnLyricsSwapPending/OnLyricsSwapped → FadeLyricsHost, allocating a Transitions object and animating opacity on a detached tree (wasted work; no memory leak because both objects are app-lifetime). The asymmetry itself is proven by the quoted code; 'likely' only because the DataContextChanged-before-attach ordering on first creation is inferred from Avalonia's presenter behavior plus the file's own comments (210-211, 253-256) rather than observed at runtime.

Proposed fix: Extract SubscribeVm(vm)/UnsubscribeVm(vm) helpers covering ONE canonical set (PropertyChanged, OpenBackgroundColorRequested, LyricsSwapPending, LyricsSwapped, Player.PropertyChanged, Player.Seeked) and call them from all three hooks, and make OnLyricsSwapPending/Swapped early-return when this.GetVisualRoot() == null.

Risk if we fix it: Low, but test the cached-view lifecycle both ways (first visit, revisit, DataContext re-set) since the current asymmetry accidentally avoids double-subscribing the swap events; a naive unify that subscribes in both DataContextChanged and attach without the _subscribedVm guard would duplicate handlers.

---

### [L5] LyricsViewModel 100ms sync timer restarts and runs for entire playback sessions while no lyrics surface is visible, defeating its own visibility gate
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/LyricsViewModel.cs:2988-2994`
  ```csharp
  public void SetLyricsSurfaceVisible(bool visible)
  {
      _visibleLyricsSurfaces = Math.Max(0, _visibleLyricsSurfaces + (visible ? 1 : -1));
      if (_visibleLyricsSurfaces == 0)
          _lyricsSyncTimer.Stop();
      else if (_hasSyncedLyrics && _player.State == Models.PlaybackState.Playing)
          _lyricsSyncTimer.Start();
  }
  ```
- `src/Noctis/ViewModels/LyricsViewModel.cs:1854-1860`
  ```csharp
  if (e.PropertyName == nameof(PlayerViewModel.State))
  {
      if (_player.State == Models.PlaybackState.Playing && _hasSyncedLyrics && IsSyncTabSelected)
          _lyricsSyncTimer.Start();
      else
          _lyricsSyncTimer.Stop();
  }
  ```
- `src/Noctis/ViewModels/LyricsViewModel.cs:1730-1734 and 549-555`
  ```csharp
  // Start sync timer only if synced lyrics exist and sync tab is active
  if (_hasSyncedLyrics && IsSyncTabSelected)
      _lyricsSyncTimer.Start();
  ...
  _lyricsSyncTimer.Tick += (_, _) =>
  {
      if (_hasSyncedLyrics && _player.State == Models.PlaybackState.Playing)
          UpdateActiveLine(GetPlaybackPosition());
      UpdateWordClockSubscription();
  };
  ```
- `src/Noctis/Views/LyricsView.axaml.cs:192-196`
  ```csharp
  // Tell the VM a lyrics surface is on screen, so the 100ms sync timer and the
  // per-frame word clock only run while something can actually display them.
  if (DataContext is LyricsViewModel attachVm)
  {
      attachVm.SetLyricsSurfaceVisible(true);
  ```
  _Verifier line corrections: src/Noctis/ViewModels/LyricsViewModel.cs:2988-2995 (SetLyricsSurfaceVisible incl. closing brace); other citations (1854-1860, 1730-1734, 549-555, LyricsView.axaml.cs:192-196) verified exact._

Why it matters: LyricsViewModel is an app-lifetime singleton subscribed to _player.TrackStarted and _player.PropertyChanged from its constructor (lines 571, 577). SetLyricsSurfaceVisible stops the timer when the last lyrics surface (page or side panel) leaves the screen — but the very next TrackStarted (line 1731-1734), play/pause State change (1856-1859), LibraryUpdated (1756-1759) or online-lyrics apply (1658-1659) unconditionally calls _lyricsSyncTimer.Start() with no IsAnyLyricsSurfaceVisible check, and the Tick body never self-stops on surface count. So for any playback session whose tracks have synced lyrics, the 100ms UI-thread DispatcherTimer runs continuously even when the lyrics page and panel are both closed, doing GetPlaybackPosition + line-cursor advance every tick and, on each active-line change, UpdateLineOpacities over every LyricLine (per-line property sets). This directly contradicts the stated design in both LyricsView.axaml.cs (192-196) and LyricsPanelView.axaml.cs (62-63). Impact is steady small UI-thread work (10 Hz), not a leak — the timer belongs to a singleton.

Proposed fix: Gate every _lyricsSyncTimer.Start() call site on IsAnyLyricsSurfaceVisible (or add `if (!IsAnyLyricsSurfaceVisible) { _lyricsSyncTimer.Stop(); return; }` at the top of the Tick handler). Re-opening a surface already restarts the timer via SetLyricsSurfaceVisible/EnsureLyricsForCurrentTrack.

Risk if we fix it: Medium-low: other features read ActiveLineIndex indirectly (share dialog, panel re-anchor on open). The reopen path (SetLyricsSurfaceVisible → Start + EnsureLyricsForCurrentTrack → RefreshActiveLyricPosition) already resyncs, but verify the mini-share/word-clock paths don't depend on the timer running while hidden.

---

### [L6] HomeViewModel is the only library view without IsActive gating — full-library rebuilds run on every LibraryUpdated/FavoritesChanged even while Home is hidden
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/HomeViewModel.cs:87-94`
  ```csharp
  _libraryUpdatedHandler = (_, _) => { _isDirty = true; Dispatcher.UIThread.Post(() =>
  {
      _refreshDebounce.Stop();
      _refreshDebounce.Start();
  }); };
  _favoritesChangedHandler = (_, _) => { _isDirty = true; Dispatcher.UIThread.Post(Refresh); };
  _library.LibraryUpdated += _libraryUpdatedHandler;
  _library.FavoritesChanged += _favoritesChangedHandler;
  ```
- `src/Noctis/ViewModels/HomeViewModel.cs:140-148`
  ```csharp
  var allTracks = _library.Tracks;
  if (allTracks.Count > 0)
  {
      var top = await Task.Run(() =>
          allTracks
              .Where(t => t.PlayCount > 0)
              .OrderByDescending(t => t.PlayCount)
              .Take(6)
              .ToList());
  ```
- `src/Noctis/ViewModels/LibrarySongsViewModel.cs:89-94`
  ```csharp
  _libraryUpdatedHandler = (_, _) =>
  {
      _isDirty = true;
      if (_isActive)
          Dispatcher.UIThread.Post(Refresh);
  };
  ```
  _Verifier line corrections: Original citations correct. Supporting additions: src/Noctis/ViewModels/MainWindowViewModel.cs:74-83 (UpdateSectionActiveFlags omits _homeVm), MainWindowViewModel.cs:286 (eager construction), src/Noctis/Models/Track.cs:489 and 704-718 (uncached regex-backed PrimaryArtist). Nuance: HomeViewModel.cs:177 limits the PrimaryArtist regex to tracks with PlayCount>0, though the aggregation loop still walks every track._

Why it matters: Songs/Albums/Artists/Folders/Favorites all gate their LibraryUpdated rebuild behind IsActive (hidden views just mark dirty and catch up on activation). HomeViewModel has no IsActive member at all (grep confirms), so during a scan — LibraryUpdated every ~1.5 s — Home re-runs its full refresh every ~2 s (500 ms debounce) regardless of what page is showing: two complete library passes (top-songs filter+sort, top-artists aggregation that reads the uncached regex-backed t.PrimaryArtist per track) plus RefreshTimeAwareRowsAsync over the play log. The FavoritesChanged handler additionally posts Refresh directly with no debounce, so each heart click anywhere in the app triggers a full hidden-Home rebuild. The heavy passes are inside Task.Run, so this wastes background CPU/GC rather than freezing the UI — hence low severity — but it multiplies with the other per-publish costs during scans of a 50k library.

Proposed fix: Add the same IsActive property (set from MainWindowViewModel.OnCurrentViewChanged like the other views), gate both handlers, and route FavoritesChanged through the existing 500 ms debounce.

Risk if we fix it: Low: the dirty-flag catch-up pattern is already established in five sibling view models; only risk is a missed activation path (the sibling implementations refresh in the IsActive setter to cover this).

---

### [L7] Single heart-click broadcasts favorite-state PropertyChanged to every album in the library (8 call sites use the parameterless NotifyFavoritesChanged despite the targeted overload existing)
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/LibraryService.cs:1292-1317`
  ```csharp
  public void NotifyFavoritesChanged() => NotifyFavoritesChanged(null);
  /// ... Doing it for *every*
  /// album meant two PropertyChanged raises per album on a single heart click — 20,000
  /// on a 10,000-album library, each causing realized tiles to re-evaluate
  /// Tracks.Any(t => t.IsFavorite).
  ...
  else
  {
      foreach (var album in _albums)
          album.NotifyFavoriteStateChanged();
  }
  ```
- `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs:928-934`
  ```csharp
  private async Task ToggleTrackFavorite(Track track)
  {
      track.IsFavorite = !track.IsFavorite;
      await _library.SaveTrackUserStateAsync(new[] { track });
      _library.NotifyFavoritesChanged();
  }
  ```
- `src/Noctis/Models/Album.cs:44-55`
  ```csharp
  public bool IsAllTracksFavorite => Tracks?.Count > 0 && Tracks.All(t => t.IsFavorite);
  public bool HasFavoriteTrack => Tracks?.Any(t => t.IsFavorite) == true;
  ...
  public void NotifyFavoriteStateChanged()
  {
      OnPropertyChanged(nameof(HasFavoriteTrack));
      OnPropertyChanged(nameof(IsAllTracksFavorite));
  }
  ```
- `src/Noctis/ViewModels/LibrarySongsViewModel.cs:375-376`
  ```csharp
  await _library.SaveTrackUserStateAsync(tracks);
  _library.NotifyFavoritesChanged(tracks);
  ```
  _Verifier line corrections: 9 parameterless call sites, not 8: src/Noctis/ViewModels/LibraryAlbumsViewModel.cs:933,1016; src/Noctis/ViewModels/FavoritesViewModel.cs:430; src/Noctis/ViewModels/HomeViewModel.cs:397,551; src/Noctis/ViewModels/PlaylistViewModel.cs:639; src/Noctis/ViewModels/AlbumDetailViewModel.cs:675,690,816. Broadcast else-branch at src/Noctis/Services/LibraryService.cs:1313-1317; FavoritesChanged raise (fires for both overloads) at LibraryService.cs:1319. Virtualization mitigation: src/Noctis/Views/LibraryAlbumsView.axaml:34-35 (outer ListBox virtualizes rows). Un-debounced Home refresh: src/Noctis/ViewModels/HomeViewModel.cs:92._

Why it matters: The doc comment on NotifyFavoritesChanged(changed) documents exactly why the broadcast is harmful at scale (20,000 raises on a 10,000-album library, each bound tile re-running Tracks.All/Any over its tracks — in aggregate an O(total tracks) UI-thread pass). The targeted overload exists and LibrarySongsViewModel uses it, but eight call sites still call the parameterless broadcast with the changed tracks in hand: LibraryAlbumsViewModel.cs:933 and 1016, FavoritesViewModel.cs:430, HomeViewModel.cs:397 and 551, PlaylistViewModel.cs:639, and AlbumDetailViewModel.cs:675, 690, 816 (verified via grep). Additionally the FavoritesChanged event this raises triggers HomeViewModel's un-gated, un-debounced full Refresh (HomeViewModel.cs:92) on every heart click.

Proposed fix: Pass the already-available changed track list at each call site (e.g. _library.NotifyFavoritesChanged(new[] { track }) / NotifyFavoritesChanged(changed)), matching LibrarySongsViewModel.ToggleFavorite.

Risk if we fix it: Low: overload exists and is proven in production use; the only behavioral difference is that albums untouched by the change no longer re-raise — which is the intent. FavoritesViewModel.RemoveItemFavorite builds the exact 'changed' list two lines above.

---

### [L8] 'System' theme resolves the OS light/dark mode once and never tracks OS theme changes while the app runs
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/SettingsViewModel.cs:1858-1866`
  ```csharp
  private string ResolveActiveThemeKey()
  {
      if (!string.IsNullOrEmpty(ActiveCustomThemeId)) return "Custom:" + ActiveCustomThemeId;
      if (IsLightTheme) return "Light";
      if (IsDarkTheme) return "Dark";
      if (IsMidnightTheme) return "Midnight";
      if (IsSystemTheme) return IsSystemDarkMode() ? "Gray" : "Light";
      return "Gray";
  }
  ```
- `src/Noctis/ViewModels/SettingsViewModel.cs:1094,1628,1645,1825,3730`
  ```csharp
  ThemeChanged?.Invoke(this, ResolveActiveThemeKey()); // only call sites: settings load, ApplyCustomTheme, DeleteCustomTheme, ApplyTheme, reset-to-defaults
  ```

Why it matters: ResolveActiveThemeKey snapshots PlatformHelper.IsSystemDarkMode() only when a theme command runs, at settings load, or on reset. A repo-wide grep finds no subscription to Avalonia's IPlatformSettings.ColorValuesChanged (zero matches for 'PlatformSettings'/'ColorValues') and App.SetTheme pins RequestedThemeVariant explicitly, so an OS dark/light switch (including scheduled auto-switching) while Noctis runs leaves the app on the stale variant until restart or re-clicking the System tile. The tile's label promises OS-following behavior it only half delivers.

Proposed fix: When IsSystemTheme is active, subscribe to TopLevel.GetTopLevel(...).PlatformSettings.ColorValuesChanged (or Application.ActualThemeVariantChanged with RequestedThemeVariant=Default) and re-invoke ThemeChanged with the re-resolved key.

Risk if we fix it: Low — guarded by IsSystemTheme; must avoid re-entrancy with SetTheme's own variant writes.

---

### [L9] Avatar picker copies the chosen image synchronously on the UI thread
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Views/SettingsView.axaml.cs:213-222`
  ```csharp
  foreach (var existing in System.IO.Directory.EnumerateFiles(dir, "avatar.*"))
  {
      if (!string.Equals(existing, target, StringComparison.OrdinalIgnoreCase))
      {
          try { System.IO.File.Delete(existing); } catch { }
      }
  }
  
  System.IO.File.Copy(sourcePath, target, overwrite: true);
  vm.ProfileAvatarPath = target;
  ```

Why it matters: OnPickAvatarClick is an async void UI handler; after the awaited file picker returns, Directory.EnumerateFiles, File.Delete and File.Copy all run synchronously on the UI thread. The filter accepts *.gif/*.webp with no size limit, so picking a large animated GIF (tens of MB, or a file on a slow network share) freezes the window for the duration of the copy.

Proposed fix: Wrap the delete+copy in await Task.Run(...), then set vm.ProfileAvatarPath on return.

Risk if we fix it: Minimal — the handler is already async; only ordering with the ProfileAvatarPath assignment must be kept.

---

### [L10] Profile settings are persisted but consumed nowhere outside the Settings card; ProfileUsername has a full persistence path with no UI at all
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/SettingsViewModel.cs:122-128`
  ```csharp
  [ObservableProperty] private string _profileName = string.Empty;
  [ObservableProperty] private string _profileUsername = string.Empty;
  [ObservableProperty] private string _profileAvatarPath = string.Empty;
  
  partial void OnProfileNameChanged(string value) { if (_settingsLoaded) QueueSettingsSave(); }
  partial void OnProfileUsernameChanged(string value) { if (_settingsLoaded) _ = SaveAsync(); }
  partial void OnProfileAvatarPathChanged(string value) { if (_settingsLoaded) _ = SaveAsync(); }
  ```
- `src/Noctis/Models/AppSettings.cs:31-38`
  ```csharp
  public string ProfileName { get; set; } = string.Empty;
  
  /// <summary>Username/handle shown beneath the profile name.</summary>
  public string ProfileUsername { get; set; } = string.Empty;
  
  public string ProfileAvatarPath { get; set; } = string.Empty;
  ```

Why it matters: Repo-wide grep (including a binary-safe grep of AlbumDetailViewModel.cs) shows ProfileName/ProfileAvatarPath referenced only by the Settings profile card itself (SettingsView.axaml:823-843, SettingsView.axaml.cs:222) plus the save/load code — no Home greeting, sidebar, tray or share feature reads them, so the 'profile' is decorative. ProfileUsername is worse: it has an observable property, a save handler, and AppSettings round-trip (SyncToSettings :1203, load :889) but zero UI bindings anywhere — a dead setting that can never be set by a user.

Proposed fix: Either surface the profile somewhere (e.g. Home greeting/tray tooltip) or drop ProfileUsername's dead property and field; at minimum document the card as self-contained.

Risk if we fix it: None for documentation; removing ProfileUsername requires keeping the JSON field tolerated on load for old settings files (extra JSON properties are ignored by System.Text.Json by default).

---

### [L11] Seven marquee toggles (Cover Flow, Lyrics page, Mini Player) do not take effect when turned ON until the text or layout next changes
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/SettingsViewModel.cs:1393-1399`
  ```csharp
  Controls.MarqueeTextBlock.GlobalCoverFlowScrollEnabled = CoverFlowMarqueeEnabled;
  Controls.MarqueeTextBlock.GlobalCoverFlowArtistScrollEnabled = CoverFlowArtistMarqueeEnabled;
  Controls.MarqueeTextBlock.GlobalCoverFlowAlbumScrollEnabled = CoverFlowAlbumMarqueeEnabled;
  Controls.MarqueeTextBlock.GlobalLyricsTitleScrollEnabled = LyricsTitleMarqueeEnabled;
  Controls.MarqueeTextBlock.GlobalLyricsArtistScrollEnabled = LyricsArtistMarqueeEnabled;
  Controls.MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = MiniPlayerTitleMarqueeEnabled;
  Controls.MarqueeTextBlock.GlobalMiniPlayerAlbumScrollEnabled = MiniPlayerAlbumMarqueeEnabled;
  ```
- `src/Noctis/Controls/MarqueeTextBlock.cs:20-26`
  ```csharp
  public static bool GlobalCoverFlowScrollEnabled { get; set; } = true;
  // ... six more plain static bool properties — no change notification
  ```
- `src/Noctis/Controls/MarqueeTextBlock.cs:344-351`
  ```csharp
  private void OnTick(object? sender, EventArgs e)
  {
      if (!IsScrollEnabled || _overflow <= OverflowThreshold || VisualRoot == null)
      {
          StopTimer();
          ResetAndRecalc();
          return;
      }
  ```
- `src/Noctis/Controls/MarqueeTextBlock.cs:216-224`
  ```csharp
  protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
  {
      base.OnPropertyChanged(change);
  
      if (change.Property == TextProperty)
      {
          _textBlock.Text = Text;
          ResetAndRecalc();
      }
  ```

Why it matters: The seven statics are plain bools with no notification, and RecalcAndStart/ResetAndRecalc only run on visual-tree attach, Text/FontSize/FontWeight/MaxDisplayWidth/InlineContent changes, or viewport bounds changes (MarqueeTextBlock.cs:203, 216-251, 263). Turning a toggle OFF is live because the running timer's OnTick re-checks IsScrollEnabled and stops; turning it ON has no trigger, so an already-overflowing title on the Cover Flow, Lyrics page or an open Mini Player stays static until the next track change, resize, or page re-attach. The user flips the toggle and sees nothing happen.

Proposed fix: After setting the statics in ApplyPlayerSettings, broadcast a refresh — e.g. add a static event MarqueeTextBlock.GlobalSettingsChanged that each attached instance subscribes to in OnAttached (unsubscribing in OnDetached) and handles with ResetAndRecalc(); raise it from ApplyPlayerSettings.

Risk if we fix it: Low — additive event; must unsubscribe on detach to avoid leaking recycled controls.

---

### [L12] 'Save analysis to file tags' never writes tags for tracks that were already analyzed before it was enabled
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/AudioAnalysis/AudioAnalysisCoordinator.cs:48-49`
  ```csharp
  public static bool NeedsAnalysis(Track t) =>
      t.Bpm <= 0 || string.IsNullOrWhiteSpace(t.MusicalKey);
  ```
- `src/Noctis/Services/AudioAnalysis/AudioAnalysisCoordinator.cs:163-168`
  ```csharp
  if (changed)
  {
      anyWritten = true;
      if (_settings().WriteAnalysisToTags && !ct.IsCancellationRequested)
      {
          if (TryWriteTags(track))
  ```
- `src/Noctis/ViewModels/SettingsViewModel.cs:2232-2236`
  ```csharp
  partial void OnWriteAnalysisToTagsChanged(bool value)
  {
      if (_suspendSettingPersistence) return;
      _ = SaveAsync();
  }
  ```

Why it matters: Tag writing only happens inside a backfill pass, and only for tracks where the library value was just filled in (`changed`). Tracks whose Bpm/MusicalKey were already populated by an earlier pass are filtered out by NeedsAnalysis and never revisited, so enabling the sub-toggle after analysis has run writes nothing for the existing library — only tracks added or invalidated later get TBPM/TKEY. The UI text ('Write detected BPM/key into file tags') gives no hint the setting is forward-only, so a user syncing tags to another device sees it silently do nothing.

Proposed fix: Either document the forward-only behavior in the setting description, or on enable schedule a tag-backfill pass over tracks whose values exist in the analysis store but are absent from file tags (the store already has per-file results to draw from).

Risk if we fix it: A retroactive tag backfill rewrites many user files at once (sync churn, mtime changes, interacts with the scan mtime fast-path); the description-only fix is risk-free.

---

### [L13] Typed-but-never-validated ListenBrainz token is persisted by any unrelated settings save and re-armed into the service at next startup, contradicting the handler's persist-on-Connect contract
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/SettingsViewModel.cs:1301-1302`
  ```csharp
  _settings.ListenBrainzScrobblingEnabled = ListenBrainzScrobblingEnabled;
  _settings.ListenBrainzToken = ListenBrainzToken ?? string.Empty;
  ```
- `src/Noctis/ViewModels/SettingsViewModel.cs:2522-2524`
  ```csharp
  // Just keep the in-memory service in sync; the user must hit "Connect"
  // to validate and persist. Don't autosave keystroke-by-keystroke.
  _listenBrainz?.Configure(value);
  ```
- `src/Noctis/ViewModels/SettingsViewModel.cs:1032-1036`
  ```csharp
  ListenBrainzToken = _settings.ListenBrainzToken;
  ListenBrainzUsername = _settings.ListenBrainzUsername;
  if (_listenBrainz != null && !string.IsNullOrEmpty(_settings.ListenBrainzToken))
  {
      _listenBrainz.Configure(_settings.ListenBrainzToken);
  ```
  _Verifier line corrections: All citations exact as given: src/Noctis/ViewModels/SettingsViewModel.cs:1301-1302 (inside SyncToSettings, declared :1181, invoked from SaveAsync :1128 before persist at :1129), :2522-2524, :1032-1036. Supporting: SettingsViewModel.cs:1119-1129 (SaveAsync merge→sync→persist order), src/Noctis/Services/PersistenceService.cs:167-174 (DPAPI ProtectField 'listenBrainzToken', Windows CurrentUser scope) and :135 (UnprotectSecret on load)._

Why it matters: SyncToSettings runs inside every SaveAsync, and it unconditionally copies the VM's ListenBrainzToken into AppSettings. So flipping ANY other setting (a theme, a marquee toggle) while an unvalidated token sits in the box persists that token to settings.json - despite the change handler's explicit design comment that only Connect should persist it. On the next launch the load path calls _listenBrainz.Configure(token) for any non-empty stored token with no validation, making the service IsAuthenticated. Combined with finding 1's stuck-true scrobbling flag, this extends the silent-unvalidated-scrobbling window across restarts; on its own it means a token the user typed and abandoned survives in the settings file (DPAPI-protected on Windows, plaintext at rest on mac/Linux per PersistenceService.TryRestrictToOwner comments).

Proposed fix: Persist the token from a dedicated field only on successful TestListenBrainz (e.g. copy to a _validatedToken that SyncToSettings writes), or clear ListenBrainzToken persistence when IsListenBrainzConnected is false.

Risk if we fix it: Low-medium: must not break the legitimate round-trip (validated token saved on Connect, reloaded at startup). Gating on IsListenBrainzConnected at save time is the smallest safe change.

---

### [L14] Apple media-host allowlist is a substring match on the whole URL, and HLS playlist part URLs are never host-checked
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/ITunesArtworkService.cs:527-529`
  ```csharp
  internal static bool IsAppleMediaHost(string url)
      => url.Contains("mvod.itunes.apple.com", StringComparison.OrdinalIgnoreCase) ||
         url.Contains("mzstatic.com", StringComparison.OrdinalIgnoreCase);
  ```
- `src/Noctis/Services/ITunesArtworkService.cs:246-263`
  ```csharp
  if (!line.StartsWith("#", StringComparison.Ordinal))
      parts.Add(new Uri(baseUri, line));
  ...
  foreach (var part in parts.DistinctBy(p => p.ToString()))
  {
      var data = await DownloadAsync(part.ToString(), ct, MaxAnimatedCoverBytes);
  ```
  _Verifier line corrections: src/Noctis/Services/ITunesArtworkService.cs:527-529 (exact); :246-259 (HLS part resolution and download; original citation said 246-263); supporting: :242 (#EXT-X-MAP URI), :493 (sole allowlist call site), :559 (master-variant URI resolution, unchecked), :127 (256 MB cap)._

Why it matters: The allowlist intended to pin animated-cover downloads to Apple hosts matches the substring anywhere in the URL, so https://evil.example/path?x=mzstatic.com or https://mzstatic.com.evil.example/ passes. Separately, segment URIs inside a fetched .m3u8 are resolved with new Uri(baseUri, line) — an absolute URL in the playlist targets any host — and DownloadAsync then fetches it (up to MaxAnimatedCoverBytes = 256 MB into memory per part, ITunesArtworkService.cs:127). Exploitation requires attacker-influenced content inside Apple-served HTML/playlists or a TLS break, so practical risk is low, but the control does not do what its name claims.

Proposed fix: Parse with Uri and compare uri.Host (equals or EndsWith ".mzstatic.com" / "mvod.itunes.apple.com"), and apply the same host check to each resolved HLS part before DownloadAsync.

Risk if we fix it: Low — pure tightening; only risk is rejecting a legitimate new Apple CDN host, which the existing fallback logging would surface.

---

### [L15] crash.log writes raw exception text, bypassing LogRedaction
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Program.cs:355-356`
  ```csharp
  var entry = $"[{DateTime.UtcNow:O}] {source}: {ex}\n---\n";
  File.AppendAllText(crashPath, entry);
  ```
- `src/Noctis/Services/DebugLog.cs:51-55`
  ```csharp
  public static void Write(string source, string message)
  {
      // This log leaves the machine via "Copy Logs" — no auth-bearing URLs
      // (media-server stream tokens) may ever be stored in it.
      message = LogRedaction.Scrub(message);
  ```
  _Verifier line corrections: src/Noctis/Program.cs:355-356 (unscrubbed append), src/Noctis/Program.cs:364 (same exception scrubbed via DebugLog), src/Noctis/Services/DebugLog.cs:51-55, src/Noctis/Services/VlcAudioPlayer.cs:3747-3753, src/Noctis/ViewModels/SettingsViewModel.cs:4290-4292_

Why it matters: Every other sink is scrubbed: DebugLog scrubs before its ring and before the CrashJournal disk sink (DebugLog.cs:55), and the VLC diag file scrubs (VlcAudioPlayer.cs:3752-3753). crash.log is the one file that appends ex.ToString() verbatim. An exception whose message embeds a URL or token (e.g. UriFormatException echoing the input, or any future message interpolating a stream URL that carries Subsonic t/s or Jellyfin api_key) would land unredacted in a file users are pointed at for bug reports (OpenLogsFolder, SettingsViewModel.cs:4290-4292). Whether a token-bearing exception actually occurs in practice is unverified.

Proposed fix: Wrap the entry: File.AppendAllText(crashPath, LogRedaction.Scrub(entry)).

Risk if we fix it: Minimal — Scrub only strips URL query strings and token-style pairs; stack traces are untouched.

---

### [L16] iTunes JSON responses parsed from the network stream without the HttpSafety byte cap
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/ITunesArtworkService.cs:455-458`
  ```csharp
  using var resp = await _http.GetAsync(url, ct);
  if (!resp.IsSuccessStatusCode) return;
  await using var stream = await resp.Content.ReadAsStreamAsync(ct);
  using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
  ```
- `src/Noctis/Services/HttpSafety.cs:5-11`
  ```csharp
  /// Bounded readers for responses from external services (LRCLIB, NetEase,
  /// Deezer, iTunes, Last.fm, artist images). A compromised or misbehaving
  /// endpoint must not be able to allocate unbounded memory or fill the disk;
  /// every remote payload gets a hard byte cap...
  ```
  _Verifier line corrections: src/Noctis/Services/ITunesArtworkService.cs:289-293, 392-396, 455-458 (unbounded parse sites); src/Noctis/Services/ITunesArtworkService.cs:348 (same file using HttpSafety for HTML); src/Noctis/Program.cs:246-252 (shared HttpClient, Timeout only, no MaxResponseContentBufferSize); src/Noctis/Services/HttpSafety.cs:5-15 (stated policy and 4 MB text cap)_

Why it matters: Three iTunes call sites (ITunesArtworkService.cs:292, 395, 457) bypass HttpSafety.ReadStringBoundedAsync and parse the response stream directly. Because GetAsync defaults to ResponseContentRead, HttpClient buffers the whole body in memory (default cap 2 GB) before parsing — so a compromised or MITM'd endpoint could force large allocations, contradicting the codebase's own stated policy that every remote payload gets a hard byte cap. Every other service in the repo goes through HttpSafety.

Proposed fix: Route these three reads through HttpSafety.ReadStringBoundedAsync (or ReadBytesBoundedAsync) and parse the bounded string, matching LookupUrl/SearchUrl usage elsewhere.

Risk if we fix it: Low — mechanical change; the 4 MB text cap comfortably covers iTunes lookup/search payloads.

---

### [L17] NoctisCoverProxy: unauthenticated publish of arbitrary bytes served as image/jpeg, unbounded aggregate memory, and an unused per-connection secret
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `tools/NoctisCoverProxy/WebSocketHandler.cs:40-51`
  ```csharp
  var clientId = Guid.NewGuid().ToString("N");
  var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
  
  // Send hello
  var hello = JsonSerializer.Serialize(new
  {
      type = "hello",
      client_id = clientId,
      secret,
      base_url = _publicBaseUrl,
  });
  ```
- `tools/NoctisCoverProxy/CoverArtStore.cs:22-33`
  ```csharp
  public void Put(string key, byte[] jpegBytes)
  {
      _entries[key] = new CacheEntry(jpegBytes, "image/jpeg", DateTime.UtcNow + _ttl);
  }
  
  public (byte[] Bytes, string ContentType)? Get(string key)
  {
      if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
      {
          // Refresh TTL on access
          _entries[key] = entry with { ExpiresAt = DateTime.UtcNow + _ttl };
  ```
- `tools/NoctisCoverProxy/WebSocketHandler.cs:73-85`
  ```csharp
  case "publish":
  {
      var contentId = doc.RootElement.GetProperty("content_id").GetString()!;
      var binaryResult = await ReceiveBinaryAsync(ws, binaryBuffer, ct);
      if (binaryResult == null) continue;
      var jpegBytes = binaryBuffer[..binaryResult.Value].ToArray();
      var key = $"{clientId}/{contentId}";
      _store.Put(key, jpegBytes);
  ```
  _Verifier line corrections: tools/NoctisCoverProxy/Program.cs:18-29 and 32-39 (no auth on /ws and /art); tools/NoctisCoverProxy/WebSocketHandler.cs:27, 40-51, 73-95, 115; tools/NoctisCoverProxy/CoverArtStore.cs:22-33; HMAC counterpart at src/Noctis/Services/Loon/LoonClient.cs:740-745 (path corrected from LoonClient.cs); re-encode at src/Noctis/Services/Loon/LoonClient.cs:686-733_

Why it matters: The /ws endpoint accepts any connection with no auth; each connection can hold up to 2 MB per content_id with no per-client entry limit and no global memory cap, so a public deployment can be OOM'd by many connections/ids. Published bytes are not validated as an image (contrast HttpSafety.LooksLikeImage and LoonClient's re-encode) yet are served publicly as image/jpeg from the operator's domain, and the TTL is refreshed on every GET so a third party can keep hostile content alive indefinitely. The generated per-connection secret is sent in hello but never verified by any handler — the HMAC scheme the in-app LoonClient implements (LoonClient.cs:740-745) has no counterpart here. Positive: this proxy makes no outbound requests at all — it cannot be used for SSRF. Note the production relay is a separate loon server not in this repo; this tool's real-world exposure depends on whether it is deployed.

Proposed fix: Require the hello secret (HMAC over clientId/path) on the /art GET route, validate published bytes with a JPEG magic-byte check, cap entries per client and total store bytes, and make Get() not refresh TTL (or only refresh for the owning client).

Risk if we fix it: Low for the tool itself; if any deployed client relies on the current unauthenticated /art URLs, adding HMAC breaks them until clients are updated.

---

### [L18] Scrobbler tokens and media-server passwords stored in plaintext at rest on macOS/Linux (DPAPI is Windows-only)
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/PersistenceService.cs:390-405`
  ```csharp
  private static string ProtectSecret(string value)
  {
      if (string.IsNullOrEmpty(value) || !OperatingSystem.IsWindows() ||
          value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
          return value;
      try
      {
          var bytes = System.Security.Cryptography.ProtectedData.Protect(...
  ```
- `src/Noctis/Services/PersistenceService.cs:77-81`
  ```csharp
  // On macOS/Linux the credential fields in settings.json are stored in plaintext
  // (DPAPI is Windows-only), and the default umask leaves the data root at 0755 and
  // the files inside at 0644 — readable by every other local account. Tighten the
  // directory to owner-only; individual files are chmod'd after each write.
  TryRestrictToOwner(DataDirectory, isDirectory: true);
  ```
- `src/Noctis/Services/PersistenceService.cs:408-413`
  ```csharp
  private static string UnprotectSecret(string value)
  {
      if (string.IsNullOrEmpty(value) || !value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
          return value;
      if (!OperatingSystem.IsWindows())
          return string.Empty;
  ```
- `src/Noctis/Services/PersistenceService.cs:390-394`
  ```csharp
  private static string ProtectSecret(string value)
  {
      if (string.IsNullOrEmpty(value) || !OperatingSystem.IsWindows() ||
          value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
          return value;
  ```
- `src/Noctis/Services/PersistenceService.cs:96-102`
  ```csharp
  File.SetUnixFileMode(path, mode);
  }
  catch
  {
      // Filesystem may not support Unix modes (e.g. a mounted exFAT volume).
      // Losing the hardening is not worth failing the save over.
  ```
  _Verifier line corrections: src/Noctis/Services/PersistenceService.cs:390-406 (ProtectSecret; quoted portion is 390-397), :77-81 (exact), :408-413 (exact), :173-180 (ProtectField call sites); src/Noctis/Services/MediaServer/SubsonicClient.cs:43-44 (raw password kept)._

Why it matters: LastFmSessionKey, ListenBrainzToken, and SourceConnection.TokenOrPassword (including the raw Subsonic password, which SubsonicClient.ConnectAsync deliberately keeps for token derivation, SubsonicClient.cs:44) are written to settings.json unencrypted on macOS/Linux. Mitigations are real but partial: chmod 0700/0600 hardening (best-effort, silently skipped on e.g. exFAT), and protect-fails-open / unprotect-fails-closed semantics. Any process running as the user, backup tooling, or a copied settings.json exposes the credentials.

_Also found independently by the Cross-platform auditor (verdict: CONFIRMED)._

Proposed fix: Integrate macOS Keychain and libsecret (or a keyed file cipher derived from a per-user OS secret) behind the existing ProtectSecret/UnprotectSecret seam; the enc: prefix scheme already supports adding new provider prefixes.

Risk if we fix it: Medium — Keychain/libsecret add native dependencies and failure modes (locked keyrings, headless sessions); the existing fail-open path must be preserved to avoid losing users' sessions.

---

### [L19] Web remote runs over cleartext HTTP with the bearer token in the URL query
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/ViewModels/SettingsViewModel.cs:392`
  ```csharp
  WebRemoteUrl = $"http://{ip}:{_webRemote.Port}/?k={_webRemote.Token}";
  ```
- `src/Noctis/Services/WebRemoteServer.cs:43-50`
  ```csharp
  public void Start(int port)
  {
      Stop();
      Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
      _cts = new CancellationTokenSource();
      _listener = new TcpListener(IPAddress.Any, port);
  ```
  _Verifier line corrections: src/Noctis/Services/WebRemoteServer.cs:43-48 (quoted Start snippet ends at the TcpListener construction on line 48, not 50). Additional supporting lines: WebRemoteServer.cs:184-193 (plain HTTP response writing, no TLS), WebRemoteServer.cs:381-382 (client page carries token in location.search on every request)._

Why it matters: The remote is off by default, gated to private source IPs (WebRemoteServer.cs:146-148), and every route requires a per-session 64-bit random token compared in constant time (WebRemoteServer.cs:283-287) — a considered LAN-only design that defeats CSRF/DNS-rebinding. The residual exposure is inherent to the transport: the token and all control traffic cross the LAN in cleartext, so any host that can sniff the segment (open Wi-Fi, hostile AP) can capture the token and control playback; the token also lands in the phone browser's history via the query string. Impact is limited to play/pause/seek/volume and reading queue metadata (BuildStatus, WebRemoteServer.cs:306-324) — no filesystem or credential access.

Proposed fix: No change strictly required given the threat model; if hardening is wanted, regenerate the token on a visible schedule and document the open-Wi-Fi caveat next to the toggle in Settings.

Risk if we fix it: None for the documentation option; TLS on a hand-rolled LAN server would add self-signed-cert friction that likely outweighs the risk.

---

### [L20] File organizer sanitization misses Windows reserved device names (CON, NUL, COM1…)
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/FileOrganizePlanner.cs:132-139`
  ```csharp
  private static string Sanitize(string segment)
  {
      var sb = new StringBuilder(segment.Length);
      foreach (var ch in segment)
          sb.Append(ch < 32 || Array.IndexOf(InvalidChars, ch) >= 0 ? '_' : ch);
      // Windows: a path component may not end with a space or dot.
      return sb.ToString().Trim().TrimEnd('.', ' ').Trim();
  }
  ```
  _Verifier line corrections: src/Noctis/Services/FileOrganizePlanner.cs:132-139 (Sanitize, exactly as quoted); src/Noctis/Services/FileOrganizePlanner.cs:104-105 (empty-segment → 'Unknown' fallback, cited as line 105 — correct); src/Noctis/Services/FileOrganizerService.cs:113-134 (per-file try/catch: reserved-name failure is caught, listed in errors, batch continues); src/Noctis/Services/FileOrganizerService.cs:117-118 and :127 (Directory.CreateDirectory / File.Move receive the unfiltered target)._

Why it matters: Sanitize neutralizes separators, invalid characters, trailing dots/spaces — and '..' collapses to empty and becomes 'Unknown' (BuildRelativePath line 105), so path traversal via tags is genuinely blocked. But it does not check Windows reserved device names: an album or artist tag of exactly 'CON', 'NUL', 'AUX', 'COM1' etc. (or 'CON.something') passes through unchanged and becomes a directory/file component under the organize root. On Windows, .NET 8 can create such entries via extended-length paths, after which Explorer and many tools cannot open or delete them; alternatively the move fails mid-batch. Auto-organize runs over tag values from arbitrary downloaded files, so a hostile or merely odd tag can strand files in a folder the user cannot manage.

Proposed fix: In Sanitize, after trimming, compare the stem (text before the first '.') case-insensitively against the reserved-name set and prefix/suffix an underscore on match — same spot the existing trailing-dot rule lives.

Risk if we fix it: Minimal — only affects the tiny set of reserved names; existing organized layouts are untouched (planner re-run would mark them Move once, as with any rename rule change).

---

### [L21] Linux xdg-open invocations use the argument-string overload — paths with spaces split into multiple arguments
Severity: low
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/Helpers/PlatformHelper.cs:150-153`
  ```csharp
  else if (IsLinux)
  {
      Process.Start("xdg-open", folderPath);
  }
  ```
- `src/Noctis/Helpers/PlatformHelper.cs:44-47`
  ```csharp
  var parent = Path.GetDirectoryName(filePath);
  if (!string.IsNullOrEmpty(parent))
      Process.Start("xdg-open", parent);
  ```
- `src/Noctis/Helpers/PlatformHelper.cs:31-38`
  ```csharp
  // ArgumentList, not a hand-quoted string: a filename containing
  // a quote would otherwise split into extra arguments.
  Process.Start(new ProcessStartInfo
  {
      FileName = "open",
      ArgumentList = { "-R", filePath },
      UseShellExecute = false
  });
  ```
  _Verifier line corrections: src/Noctis/Helpers/PlatformHelper.cs:150-153, 44-47, 31-38, 61-73 (dbus-send array element at line 71); callers: src/Noctis/ViewModels/LibraryFoldersViewModel.cs:355, src/Noctis/ViewModels/SettingsViewModel.cs:3783 and 4292_

Why it matters: The macOS branch in the same file deliberately uses ArgumentList because the string form splits on quoting/whitespace, but both Linux branches still pass the raw path as the arguments string. .NET's Unix argument parsing splits that string on unquoted whitespace, so any Linux music folder containing a space (extremely common) makes OpenFolder and the ShowInFileManager fallback hand xdg-open a truncated path — the wrong location opens or nothing happens. A path component beginning with '-' would additionally be consumed as an option; xdg-open has no dangerous options, so this is a correctness defect with only a trivial argument-injection surface. Related: the dbus-send branch builds "array:string:file://{filePath}" (lines 63-73) where dbus-send's array syntax splits elements on commas, so a comma in the path breaks the D-Bus route too and forces the buggy fallback.

Proposed fix: Use ProcessStartInfo.ArgumentList for both xdg-open calls (matching the macOS branch), and percent-encode/escape the file URI (or use commas-safe quoting) in the dbus-send array argument.

Risk if we fix it: Minimal — mechanical switch to the already-used ArgumentList pattern; behavior for space-free paths unchanged.

---

### [L22] M3U export writes raw tag metadata — newline in a Title/Artist tag injects arbitrary playlist entries
Severity: low
Confidence: likely (adversarially re-verified)
Evidence:
- `src/Noctis/Services/PlaylistInteropService.cs:18-26`
  ```csharp
  foreach (var track in ordered)
  {
      ct.ThrowIfCancellationRequested();
      var seconds = (int)Math.Round(track.Duration.TotalSeconds);
      sb.AppendLine($"#EXTINF:{seconds},{track.Artist} - {track.Title}");
      sb.AppendLine(PortablePath(baseDir, track.FilePath));
  }
  ```
- `src/Noctis/Services/MetadataService.cs:147-149`
  ```csharp
  var title = string.IsNullOrWhiteSpace(tag.Title)
      ? Path.GetFileNameWithoutExtension(filePath)
      : tag.Title;
  ```
  _Verifier line corrections: src/Noctis/Services/PlaylistInteropService.cs:18-24 (raw interpolation, AppendLine at line 22); src/Noctis/Services/MetadataService.cs:147-149 (tag.Title verbatim); src/Noctis/Models/Track.cs:20-33 (no setter sanitization); src/Noctis/ViewModels/LibraryPlaylistsViewModel.cs:223-254 (reachable via ExportPlaylistCommand)_

Why it matters: Track.Title/Artist come straight from TagLib tags with no control-character stripping, and ExportM3uAsync (reachable via ExportPlaylistCommand, src/Noctis/ViewModels/LibraryPlaylistsViewModel.cs:254) interpolates them into the .m3u line-oriented format. A downloaded audio file with a crafted tag containing "\n" injects attacker-chosen lines into the exported playlist — including entry lines such as UNC paths (\\attacker\share\x.mp3), which some third-party Windows players will dereference on open (NTLM hash disclosure pattern). Noctis's own re-import is inert: PlaylistImportParser/FuzzyTrackMatcher only match entries against the in-memory library and perform no file I/O on entry paths (no File./Directory. calls in FuzzyTrackMatcher.cs).

Proposed fix: Sanitize the interpolated fields in ExportM3uAsync, e.g. strip \r/\n (and leading '#') from Artist/Title before writing the #EXTINF line.

Risk if we fix it: Minimal — only affects exported playlist text; titles legitimately containing newlines are already malformed for the m3u format.

---

### [L23] SMB media-source scan has no symlink/junction cycle guard (unbounded recursion)
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Services/SmbMediaSourceConnector.cs:68-90`
  ```csharp
  private static IEnumerable<string> EnumerateAudioFiles(string root)
  {
      var stack = new Stack<string>();
      stack.Push(root);
      while (stack.Count > 0)
      {
          var current = stack.Pop();
          ...
          foreach (var dir in dirs)
              stack.Push(dir);
  ```
- `src/Noctis/Services/LibraryService.cs:2167-2179`
  ```csharp
  // Cycle guard keyed on the RESOLVED path: a junction/symlink pointing at
  // an ancestor re-enters the tree under an ever-growing logical path, so
  // the walked path alone never repeats and the DFS loops forever.
  var visited = new HashSet<string>(...);
  ...
  if (!visited.Add(ResolveRealPath(current))) continue;
  ```
  _Verifier line corrections: src/Noctis/Services/SmbMediaSourceConnector.cs:68-98 (unguarded DFS); src/Noctis/Services/LibraryService.cs:2167-2179 (guarded contrast); src/Noctis/Services/LibraryWatcherService.cs:194-203 (MaxRecursionDepth=64); src/Noctis/Program.cs:257 (DI registration); src/Noctis/Services/UnifiedLibraryService.cs:51-85 (sole ScanAsync caller, itself uncalled)_

Why it matters: The main library scanner was explicitly hardened against directory-link cycles (visited set keyed on the resolved real path, with a comment stating the DFS 'loops forever' without it), but SmbMediaSourceConnector.EnumerateAudioFiles — registered in DI at src/Noctis/Program.cs:257 and used when scanning an SMB media source — is a plain DFS with no visited set and no depth bound. A share (or local path added as an SMB source) containing a directory symlink pointing at an ancestor makes the walk re-enter the tree under an ever-growing path until path-length syscalls start failing, i.e. an effectively unbounded scan over the network. LibraryWatcherService bounds the same risk with MaxRecursionDepth = 64 (src/Noctis/Services/LibraryWatcherService.cs:197-203), making this the only unguarded recursive walk left.

Proposed fix: Mirror LibraryService: add a HashSet<string> visited keyed on the resolved link target (or set EnumerationOptions.MaxRecursionDepth like LibraryWatcherService) in SmbMediaSourceConnector.EnumerateAudioFiles.

Risk if we fix it: Low — additive guard identical to the pattern already proven in LibraryService; worst case a deliberately symlinked share is enumerated once per real directory instead of repeatedly.

---

### [L24] Linux 'System' theme detection is GNOME-only — KDE and other desktops always resolve to dark
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Helpers/PlatformHelper.cs:196-206, 213`
  ```csharp
  if (IsLinux)
  {
      var colorScheme = ReadGSettings("org.gnome.desktop.interface", "color-scheme");
      ...
      var gtkTheme = ReadGSettings("org.gnome.desktop.interface", "gtk-theme");
      ...
  }
  ...
  return true;   // default to dark
  ```
  _Verifier line corrections: src/Noctis/Helpers/PlatformHelper.cs:196-206 (GNOME-only gsettings probes), 213 (dark fallback when both probes fail); PlatformHelper.cs:216-239 ReadGSettings returns null on missing binary/nonzero exit; consumer SettingsViewModel.cs:1864,1868-1870. Note: with GNOME schemas present on KDE, color-scheme='default' yields LIGHT (line 200-201), not dark — the constant is only reached when gsettings is absent/fails._

Why it matters: IsSystemDarkMode shells out to gsettings against GNOME schemas only. On KDE (kdeglobals/plasma theme), or any box without gsettings/the GNOME schemas installed, both reads fail and the method returns the hardcoded dark default — a KDE user with a light desktop who picks the 'System' theme (SettingsViewModel.cs:1870) always gets dark. Silent and cosmetic, hence low.

Proposed fix: Add a KDE probe (read ~/.config/kdeglobals ColorScheme, or the xdg-desktop-portal org.freedesktop.appearance color-scheme setting via D-Bus, which covers GNOME and KDE uniformly) before falling back to dark.

Risk if we fix it: Portal D-Bus read is the standard, low-risk route; a bad parse just lands on the existing dark default.

---

### [L25] CI dependency-vulnerability audit runs only on win-x64, so the macOS-conditional native libVLC package is never audited
Severity: low
Confidence: confirmed (web-research finding — version/CVE claims cited, not code-adversarially verified)
Evidence:
- `.github/workflows/dotnet.yml:68-73`
  ```
  # Windows only: the graph is identical across targets except the OS-conditional
  # native libVLC references, and those are invisible to an audit run on the wrong
  # host anyway (a Windows run never evaluates VideoLAN.LibVLC.Mac — see the audit
  # note in AUDIT_2026-07-24.md).
  - name: Audit dependencies
    if: matrix.rid == 'win-x64'
  ```

Why it matters: The `dotnet list package --vulnerable` gate is gated to win-x64, and the workflow's own comment acknowledges VideoLAN.LibVLC.Mac is never evaluated. Combined with finding 1 (the Mac reference silently floating to a 2019 package), the one dependency most in need of scrutiny is structurally invisible to the pipeline, and NU1603 (approximate-match resolution) from the mac restore is not surfaced or failed on.

Proposed fix: Report-only recommendation: run the audit step on one macOS leg as well, and add -warnaserror:NU1603 (or TreatWarningsAsErrors for NU1603) to the restore so a nonexistent pinned version fails loudly instead of floating.

Risk if we fix it: Low: CI-only change; worst case is a red leg that exposes the existing resolution problem.

---

### [L26] Microsoft.Data.Sqlite 8.0.11 is 18 patch releases behind the still-supported 8.0.x line (8.0.29)
Severity: low
Confidence: confirmed (web-research finding — version/CVE claims cited, not code-adversarially verified)
Evidence:
- `src/Noctis/Noctis.csproj:97`
  ```xml
  <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.11" />
  ```

Why it matters: NuGet's flat-container index shows the 8.0.x line continued to 8.0.29 (published 2026-07-14, verified listed on nuget.org). 8.0.11 dates to Nov 2024. No CVE forces the bump, but sitting 18 servicing releases back on an active LTS line forfeits bug fixes for free. Verified that 8.0.29 still declares SQLitePCLRaw.bundle_e_sqlite3 >= 2.1.6, so the repo's explicit 2.1.12 native-engine float (the CVE-2025-6965 mitigation) remains both required and effective after updating.

Proposed fix: Bump Microsoft.Data.Sqlite 8.0.11 -> 8.0.29 in src/Noctis/Noctis.csproj; keep the adjacent SQLitePCLRaw.bundle_e_sqlite3 2.1.12 reference (and its twin in tests/Noctis.Tests/Noctis.Tests.csproj) exactly as-is.

Risk if we fix it: Minimal: same-major servicing update of the managed provider; the native SQLite engine version is unchanged because the explicit bundle pin governs it. A normal test pass suffices.

---

### [L27] xunit 2.9.3 pins a line its maintainers have deprecated (v2 is security-fix only)
Severity: low
Confidence: confirmed (web-research finding — version/CVE claims cited, not code-adversarially verified)
Evidence:
- `tests/Noctis.Tests/Noctis.Tests.csproj:14-15`
  ```xml
  <PackageReference Include="xunit" Version="2.9.3" />
  <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
  ```

Why it matters: The xunit NuGet listing marks all v2 versions deprecated ('no longer maintained') and states the package 'will only be updated for security issues. All future feature work has moved onto v3.' 2.9.3 (2025-01-08) is the terminal v2 release. Dev-time only — nothing ships to users — so this is hygiene, not exposure. Source: https://www.nuget.org/packages/xunit

Proposed fix: No urgent action. When convenient, migrate the test project to xunit.v3 (per xunit.net/docs/getting-started/v3/migration); xunit.runner.visualstudio 3.1.5 already supports v3.

Risk if we fix it: Moderate if undertaken: v3 renames packages and changes runner integration, and the suite is known order-dependent (per project memory), so a migration needs a careful full-suite pass — which is why it is not recommended as part of this audit's patch set.

---

### [L28] Converters/ArtistTokensConverter.cs is dead: declared as a XAML resource whose key is never used, superseded by the view-model building ArtistTokenItem[] directly
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Converters/ArtistTokensConverter.cs:12-14`
  ```csharp
  public class ArtistTokensConverter : IValueConverter
  {
      public static readonly ArtistTokensConverter Instance = new();
  ```
- `src/Noctis/Views/AlbumDetailView.axaml:16`
  ```xml
  <conv:ArtistTokensConverter x:Key="ArtistTokens" />
  ```
- `src/Noctis/ViewModels/AlbumDetailViewModel.cs:165-167`
  ```csharp
  var tokens = Track.ParseArtistTokens(album.Artist);
  ...
  ArtistTokens = tokens.Select((name, i) => new ArtistTokenItem(name, IsLast: i == tokens.Length - 1)).ToArray();
  ```
  _Verifier line corrections: src/Noctis/Converters/ArtistTokensConverter.cs:12-14; src/Noctis/Views/AlbumDetailView.axaml:16; src/Noctis/ViewModels/AlbumDetailViewModel.cs:165-167,237-240_

Why it matters: Checks run: (1) word-boundary grep -a of `ArtistTokensConverter` across all src+tests+tools source files: only the declaring file and the AlbumDetailView.axaml:16 resource entry. (2) XAML key usage: grep for `Resource ArtistTokens}` (StaticResource/DynamicResource) across all .axaml and .cs: zero hits; grep for FindResource("ArtistTokens")/TryGetResource: zero. (3) Broad substring grep of `ArtistTokens` shows only the unrelated Track.ParseArtistTokens helper and the AlbumDetailViewModel.ArtistTokens property — the VM now produces the ArtistTokenItem[] itself (AlbumDetailViewModel.cs:167, searched with grep -a to defeat the known binary-skip gotcha on that file), which is exactly what the converter was for. (4) `Instance` field: never referenced. (5) DI/reflection: none. The class and its resource declaration are both dead.

Proposed fix: Delete src/Noctis/Converters/ArtistTokensConverter.cs and the resource entry at Views/AlbumDetailView.axaml:16.

Risk if we fix it: Very low. Key is provably unused; removal is compile- and XAML-safe.

---

### [L29] Converters/TrackPlaylistCommandParameterConverter.cs is dead: single reference is a resource declaration whose key is never used
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Converters/TrackPlaylistCommandParameterConverter.cs:8-11`
  ```csharp
  /// <summary>
  /// Builds the Add-to-playlist command parameter for track context menus.
  /// </summary>
  public sealed class TrackPlaylistCommandParameterConverter : IMultiValueConverter
  ```
- `src/Noctis/Views/AlbumDetailView.axaml:17`
  ```xml
  <conv:TrackPlaylistCommandParameterConverter x:Key="TrackPlaylistParamConverter" />
  ```
- `src/Noctis/Converters/TrackPlaylistCommandParameterConverter.cs:11-13`
  ```csharp
  public sealed class TrackPlaylistCommandParameterConverter : IMultiValueConverter
  {
      public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
  ```

Why it matters: Checks run: (1) substring grep -a of `TrackPlaylistCommandParameterConverter` and of the key `TrackPlaylistParamConverter` across every .cs and .axaml under src/Noctis and tests (bin/obj excluded): exactly ONE hit total — the resource declaration at AlbumDetailView.axaml:17. No `{StaticResource TrackPlaylistParamConverter}` usage, no code-behind FindResource, no DI, no reflection/nameof/typeof, no tests. Add-to-playlist context menus are built in code by Helpers/TrackContextMenuBuilder instead, so the converter's job moved there.

_Also found independently by the Dead code auditor (verdict: CONFIRMED)._

Proposed fix: Delete src/Noctis/Converters/TrackPlaylistCommandParameterConverter.cs and the resource entry at Views/AlbumDetailView.axaml:17.

Risk if we fix it: Very low. Provably unused.

---

### [L30] Entire offline-cache subsystem is dead: IOfflineCacheService/OfflineCacheService registered in DI but never resolved, all members have zero callers
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Program.cs:231`
  ```csharp
  services.AddSingleton<IOfflineCacheService, OfflineCacheService>();
  ```
- `src/Noctis/Services/OfflineCacheService.cs:10`
  ```csharp
  public sealed class OfflineCacheService : IOfflineCacheService
  ```
- `src/Noctis/Models/Track.cs:369`
  ```csharp
  public OfflineState OfflineState { get; set; } = OfflineState.None;
  ```
- `src/Noctis/Services/LibraryService.cs:1597`
  ```csharp
  target.OfflineState = source.OfflineState;
  ```

Why it matters: Checks run: (1) word-boundary grep -a of `IOfflineCacheService` and `OfflineCacheService` across all 539 .cs/.axaml/.csproj files in src/Noctis, tests/Noctis.Tests, tools (bin/obj excluded) — only hits are the Program.cs:231 registration and the `: IOfflineCacheService` implements clause; no constructor injection, no GetService/GetRequiredService, no typeof/nameof/string-literal, no test refs. (2) Member-level grep for `ResolvePlaybackPathAsync|PinAsync|UnpinAsync|EnforceLimitsAsync` across src+tests: zero callers (only unrelated `TogglePinAsync` for sidebar playlists). (3) MS.Extensions.DI singletons are lazily constructed, so the never-resolved service is never even instantiated — its ctor (which creates cache directories) never runs. The companion Models/OfflineState.cs enum is vestigial too: the only non-declaration refs are Track.cs:369 (default `None`) and LibraryService.cs:1597 (copy); values Cached/Pinned/Failed are never assigned or read anywhere. ~250 lines (OfflineCacheService.cs 229 + IOfflineCacheService.cs 19) of dead, carefully-commented code that misleads maintainers into thinking streaming/pinning exists.

Proposed fix: Delete src/Noctis/Services/OfflineCacheService.cs, src/Noctis/Services/IOfflineCacheService.cs, the Program.cs:231 registration, Models/OfflineState.cs, the Track.OfflineState property (Track.cs:369), and the copy at LibraryService.cs:1597. Alternatively keep only if a streaming-pinning feature is genuinely planned, and note it as inactive.

Risk if we fix it: Low. Removal is compile-safe (zero references). Only risk is discarding scaffolding for a planned remote-streaming feature; Track.OfflineState also appears in persisted JSON, but removing a JSON property is backward-compatible with System.Text.Json.

---

### [L31] IAlbumArtworkSearch interface and its forwarding DI registration are dead — every consumer uses the concrete ITunesArtworkService
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Program.cs:281`
  ```csharp
  services.AddSingleton<IAlbumArtworkSearch>(sp => sp.GetRequiredService<ITunesArtworkService>());
  ```
- `src/Noctis/Services/IAlbumArtworkSearch.cs:11-17`
  ```csharp
  public interface IAlbumArtworkSearch
  {
      Task<IReadOnlyList<ITunesArtworkService.ArtworkCandidate>> SearchAlbumsAsync(
          string artist, string album, int limit = 8, CancellationToken ct = default);
  
      Task<IReadOnlyList<ITunesArtworkService.AnimatedArtworkVariant>> SearchAnimatedArtworkVariantsAsync(
          string albumViewUrl, CancellationToken ct = default);
  ```
- `src/Noctis/ViewModels/MetadataHelper.cs:125`
  ```csharp
  var itunes = App.Services!.GetService<ITunesArtworkService>();
  ```
  _Verifier line corrections: src/Noctis/Program.cs:281; src/Noctis/Services/IAlbumArtworkSearch.cs:11-17; src/Noctis/Services/ITunesArtworkService.cs:15; src/Noctis/ViewModels/MetadataHelper.cs:125,156; src/Noctis/ViewModels/MetadataViewModel.cs:367; src/Noctis/Program.cs:272_

Why it matters: Checks run: word-boundary grep -a of `IAlbumArtworkSearch` across all src+tests+tools source files returns exactly 2 hits: the Program.cs:281 forwarding registration and `ITunesArtworkService : IAlbumArtworkSearch` (ITunesArtworkService.cs:15). Nothing injects it, nothing calls GetService/GetRequiredService<IAlbumArtworkSearch>, no typeof/nameof/string hits, no test refs (the doc comment's stated purpose — unit-testing against the small interface — never materialized). All real consumers resolve the concrete class: Program.cs:272 registers ITunesArtworkService directly and MetadataHelper.cs:125/156 resolve it by concrete type; MetadataViewModel references the nested ArtworkCandidate/AnimatedArtworkVariant types through the concrete class name.

Proposed fix: Delete src/Noctis/Services/IAlbumArtworkSearch.cs, the Program.cs:281 registration, and the `: IAlbumArtworkSearch` clause on ITunesArtworkService.

Risk if we fix it: Very low. Zero consumers; compile-safe.

---

### [L32] IMediaSourceConnector layer is dead weight: Local/Smb/WebDav connectors referenced only by their DI registrations, and no connector method is reachable at runtime
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Program.cs:256-259`
  ```csharp
  services.AddSingleton<IMediaSourceConnector, LocalMediaSourceConnector>();
  services.AddSingleton<IMediaSourceConnector, SmbMediaSourceConnector>();
  services.AddSingleton<IMediaSourceConnector, WebDavMediaSourceConnector>();
  services.AddSingleton<IMediaSourceConnector, NavidromeMediaSourceConnector>();
  ```
- `src/Noctis/Services/NavidromeSyncService.cs:16-21`
  ```csharp
  IEnumerable<IMediaSourceConnector> connectors,
  IAuditTrailService auditTrail)
  {
      _persistence = persistence;
      _navidromeConnector = connectors.FirstOrDefault(c => c.SourceType == SourceType.Navidrome)!;
      _auditTrail = auditTrail;
  ```
- `src/Noctis/ViewModels/MainWindowViewModel.cs:2483`
  ```csharp
  _ = _syncService.PushPlayStateAsync(track);
  ```
- `src/Noctis/Services/LocalMediaSourceConnector.cs:5-9`
  ```csharp
  /// <summary>
  /// Local filesystem connector placeholder for unified source orchestration.
  /// Local scanning remains owned by LibraryService.
  /// </summary>
  public sealed class LocalMediaSourceConnector : IMediaSourceConnector
  ```
  _Verifier line corrections: why-narrative correction only: UnifiedLibraryService calls ValidateConnectionAsync (UnifiedLibraryService.cs:63) and ScanAsync (UnifiedLibraryService.cs:66) but never OpenTrackStreamAsync; the sole OpenTrackStreamAsync call site is NavidromeMediaSourceConnector.cs:230 inside its own unreachable DownloadTrackAsync._

Why it matters: Checks run: (1) word-boundary grep -a of `LocalMediaSourceConnector`, `SmbMediaSourceConnector`, `WebDavMediaSourceConnector` across all src+tests+tools source files: each has exactly ONE reference — its Program.cs registration. No tests, no XAML, no reflection/string hits. (2) The only IEnumerable<IMediaSourceConnector> consumers are NavidromeSyncService (alive via MainWindowViewModel's ISyncService ctor param) and the never-resolved UnifiedLibraryService — so at startup all four connectors ARE constructed, then three are immediately discarded by FirstOrDefault(SourceType.Navidrome). (3) Method reachability: NavidromeSyncService only invokes _navidromeConnector.ValidateConnectionAsync inside PullAsync, and grep of `PullAsync|PushPlaylistAsync` across src+tests shows ZERO callers (the sole ISyncService call anywhere is PushPlayStateAsync at MainWindowViewModel.cs:2483, which just appends an audit event). UnifiedLibraryService, the only ScanAsync/OpenTrackStreamAsync caller, is never resolved. Net: all four IMediaSourceConnector method surfaces (ValidateConnectionAsync/ScanAsync/OpenTrackStreamAsync/DownloadTrackAsync) are runtime-unreachable; NavidromeMediaSourceConnector (286 lines) survives only through its static BuildSubsonicUrl used by tests/Noctis.Tests/NavidromeConnectorTests.cs — i.e. test-only. The live remote-server feature is the separate Services/MediaServer stack (IMediaServerService with Jellyfin/Subsonic clients, Program.cs:261). Total: ~500 lines of unreachable connector code plus 2 dead ISyncService members (PullAsync, PushPlaylistAsync).

Proposed fix: Delete LocalMediaSourceConnector.cs (41 lines), SmbMediaSourceConnector.cs (107), WebDavMediaSourceConnector.cs (50) and their Program.cs:256-258 registrations. Either delete NavidromeMediaSourceConnector.cs + IMediaSourceConnector.cs and simplify NavidromeSyncService to drop the connector dependency and the uncalled PullAsync/PushPlaylistAsync (updating ISyncService and NavidromeConnectorTests), or explicitly mark the layer as planned-feature scaffolding.

Risk if we fix it: Low-to-medium. The three placeholder connectors are compile-safe to remove (single-reference each). Trimming ISyncService/NavidromeMediaSourceConnector touches a live class (NavidromeSyncService) and a test file, so it needs a build+test pass; the memory notes a cross-platform branch exists, so deletions on main could conflict with unmerged work.

---

### [L33] IUnifiedLibraryService/UnifiedLibraryService registered in DI but never resolved; both interface members have zero callers
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Program.cs:241`
  ```csharp
  services.AddSingleton<IUnifiedLibraryService, UnifiedLibraryService>();
  ```
- `src/Noctis/Services/UnifiedLibraryService.cs:8`
  ```csharp
  public sealed class UnifiedLibraryService : IUnifiedLibraryService
  ```
- `src/Noctis/Services/IUnifiedLibraryService.cs:8-12`
  ```csharp
  public interface IUnifiedLibraryService
  {
      Task<IReadOnlyList<Track>> GetUnifiedTracksAsync(CancellationToken ct = default);
      Task RefreshRemoteSourcesAsync(CancellationToken ct = default);
  }
  ```

Why it matters: Checks run: (1) word-boundary grep -a of both names across all src/Noctis + tests + tools source files: exactly 2 hits each — the Program.cs:241 registration and the implements clause. (2) Member grep `GetUnifiedTracksAsync|RefreshRemoteSourcesAsync` across src+tests (bin/obj excluded): zero callers outside the declaring files. (3) No typeof/nameof/string-literal/reflection hits, no XAML hits, no test refs. Because DI singletons are lazy and nothing resolves the interface, the class is never constructed. This 86-line service is also the only code that would ever call IMediaSourceConnector.ScanAsync (see connector finding) — its deadness is what makes most of the connector layer unreachable.

Proposed fix: Delete src/Noctis/Services/UnifiedLibraryService.cs, src/Noctis/Services/IUnifiedLibraryService.cs, and the Program.cs:241 registration.

Risk if we fix it: Low. Zero references; compile-safe removal. Only cost is losing scaffolding if a unified local+remote library view is still planned.

---

### [L34] Unused duplicate converter resource declarations: GuidEquals in PlaylistView.axaml and VolumeToIcon in LyricsView.axaml
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Views/PlaylistView.axaml:16`
  ```xml
  <conv:GuidEqualsConverter x:Key="GuidEquals" />
  ```
- `src/Noctis/Views/LyricsView.axaml:17`
  ```xml
  <conv:VolumeToIconConverter x:Key="VolumeToIcon"/>
  ```
- `src/Noctis/Views/AlbumDetailView.axaml:698`
  ```xml
  <MultiBinding Converter="{StaticResource GuidEquals}" Mode="OneWay">
  ```
  _Verifier line corrections: src/Noctis/Views/PlaylistView.axaml:16 (unused declaration); src/Noctis/Views/LyricsView.axaml:17 (unused declaration); consumers self-satisfied by src/Noctis/Views/AlbumDetailView.axaml:14 -> :698 and src/Noctis/Views/PlaybackBarView.axaml:16 -> :1119_

Why it matters: The converter CLASSES are alive, but each is declared as a resource in two files while the key is used in only one: grep of `Resource GuidEquals}` across all .axaml/.cs returns a single hit (AlbumDetailView.axaml:698) — the PlaylistView.axaml:16 declaration is unused; grep of `Resource VolumeToIcon}` returns a single hit (PlaybackBarView.axaml:1119) — the LyricsView.axaml:17 declaration is unused. Code-behind lookups checked: no FindResource/TryGetResource of either key in any .cs. Hygiene only: each unused declaration instantiates one converter object per view load.

Proposed fix: Remove the resource entry at Views/PlaylistView.axaml:16 and Views/LyricsView.axaml:17.

Risk if we fix it: Very low, but verify by launching the Playlist and Lyrics views after removal; StaticResource misses fail at XAML load, so any overlooked consumer would surface immediately and loudly.

---

### [L35] Inter-ExtraBold.ttf (746 KB) embedded in every build but its FontFamily resource is never used
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/App.axaml:40`
  ```xml
  <FontFamily x:Key="InterExtraBold">avares://Noctis/Assets/Fonts/Inter-ExtraBold.ttf#Inter Extra Bold</FontFamily>
  ```
- `src/Noctis/Noctis.csproj:115`
  ```xml
  <AvaloniaResource Include="Assets\**" />
  ```

Why it matters: Case-insensitive search `grep -rni "extrabold|extra bold"` across all .cs and .axaml in src/ and tests/ returns exactly one hit: the App.axaml definition itself. So neither the resource key "InterExtraBold" nor the direct `avares://...#Inter Extra Bold` form is referenced anywhere (the sibling key InterSemiBold IS used, from Assets/Styles.axaml). The .ttf is 746,208 bytes on disk and is embedded into the shipped binary via the csproj Assets\** AvaloniaResource glob, so every distributed build carries ~730 KB of dead font data.

Proposed fix: Delete Assets/Fonts/Inter-ExtraBold.ttf and the InterExtraBold FontFamily declaration on App.axaml line 40.

Risk if we fix it: Low. No reference exists in any searched form (key name, PostScript name, filename). Only residual risk is a future design intent to use the ExtraBold weight; re-adding is trivial. Run the app once to confirm no startup resource error (none expected since nothing resolves the key).

---

### [L36] Nine StreamGeometry icon keys in Assets/Icons.axaml are never referenced anywhere
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Assets/Icons.axaml:28-31 (representative)`
  ```xml
  <StreamGeometry x:Key="SidebarIcon">M3 6h18v2H3V6zm0 5h18v2H3v-2zm0 5h18v2H3v-2z</StreamGeometry>
  ...
  <StreamGeometry x:Key="BookmarkIcon">M6.19 21.854a.75.75...</StreamGeometry>
  ```
- `src/Noctis/Assets/Icons.axaml:85, 88, 106, 115, 124, 134, 137`
  ```xml
  x:Key="ShieldIcon" / x:Key="WrenchIcon" / x:Key="SearchGlobeIcon" / x:Key="GitHubIcon" / x:Key="AudioDeviceIcon" / x:Key="SidePanelIcon" / x:Key="ExpandIcon"
  ```

Why it matters: For each of the 51 x:Key entries in Icons.axaml, a repo-wide search (`grep -rl "\b<Key>\b"` over .cs and .axaml in src/Noctis, tests, tools, binary-safe re-check with `grep -rna`) found consumers for 42 of them, but zero occurrences outside Icons.axaml for these nine: AudioDeviceIcon, BookmarkIcon, ExpandIcon, GitHubIcon, SearchGlobeIcon, ShieldIcon, SidePanelIcon, SidebarIcon, WrenchIcon. Dynamic lookups were audited: every TryGetResource/FindResource/TryFindResource call site (LottieToggle.axaml.cs:30, VolumeToIconConverter.cs:30, AlbumContextMenuBuilder.cs:76/110, TrackContextMenuBuilder.cs:104/141, CommandPaletteViewModel.cs:79, LyricsViewModel.cs:454, MainWindow.axaml.cs:244) receives a literal string that the same word-boundary grep would have matched, and a search for concatenated/interpolated keys ending in "Icon" found none. Icons.axaml is merged into Application.Resources (App.axaml line 37), so these nine geometries are parsed into the app-level dictionary on startup and never consumed.

Proposed fix: Delete the nine unused StreamGeometry entries from Assets/Icons.axaml.

Risk if we fix it: Very low. Runtime cost of keeping them is trivial (small parse/memory overhead), and removal cannot break anything since no lookup exists; the cost of removal is only losing ready-made geometries if a future feature wants them.

---

### [L37] Unreferenced PNG assets 'Previous ICON.png' and 'Pause ICON.png' embedded in the binary
Severity: low
Confidence: confirmed (adversarially re-verified)
Evidence:
- `src/Noctis/Noctis.csproj:115`
  ```xml
  <AvaloniaResource Include="Assets\**" />
  ```
- `src/Noctis/Assets/Icons/Previous ICON.png:n/a (binary, 8,063 bytes)`
  ```
  (no code reference exists; sibling icons are referenced URL-encoded, e.g. Helpers/AlbumContextMenuBuilder.cs:45 "avares://Noctis/Assets/Icons/Play%20ICON.png")
  ```
  _Verifier line corrections: src/Noctis/Noctis.csproj:115 (<AvaloniaResource Include="Assets\**" />); src/Noctis/Assets/Icons/Previous ICON.png (8,063 bytes) and src/Noctis/Assets/Icons/Pause ICON.png (4,585 bytes) — zero references in any encoding/case across src/, tests/, tools/; convention example Helpers/AlbumContextMenuBuilder.cs:45 confirmed; vector replacements at src/Noctis/Assets/Icons.axaml:38,40,42 (PreviousIcon/PlayIcon/PauseIcon StreamGeometry keys); test blind spot at tests/Noctis.Tests/IconResourceReferenceTests.cs:42-56._

Why it matters: All 31 files under Assets/ were checked against both their literal filename and the URL-encoded form the codebase actually uses (e.g. "Play%20ICON.png") across .cs, .axaml, .csproj in src/, tests/, and tools/. 29 assets have references; "Previous ICON.png" and "Pause ICON.png" have zero in either form, and a case-insensitive regex sweep (`previous[^\"']*\.png|pause[^\"']*\.png`) plus a search for dynamically-built icon paths (interpolation/concatenation into "Assets/Icons/") found nothing. The transport UI uses the PlayIcon/PauseIcon/PreviousIcon StreamGeometry keys from Icons.axaml instead, so these PNGs are superseded leftovers — but the csproj Assets\** glob still embeds both (12.6 KB combined) into every build. Note tests/Noctis.Tests/IconResourceReferenceTests.cs only validates that references resolve to files, not that files are referenced, so it will not flag these.

Proposed fix: Delete Assets/Icons/Previous ICON.png and Assets/Icons/Pause ICON.png.

Risk if we fix it: Very low. No reference in any encoding or case exists; IconResourceReferenceTests will still pass since it only checks the reference-to-file direction.

---

## Settings surface

Every interactive control in the Settings dialog, per tab. `Applies` = whether a change takes effect immediately (`live`), only at next start (`restart`), partially (`partial`), or is consumed by nothing (`never`).

| Tab | Control | Location | Writes | Read by | Applies | Flag | Notes |
|---|---|---|---|---|---|---|---|
| General | Avatar button (Change avatar) | SettingsView.axaml:796-804 + SettingsView.axaml.cs:177 (OnPickAvatarClick) | action: copies picked image to DataRoot\profile then sets ProfileAvatarPath (axaml.cs:222) -> AppSettings.ProfileAvatarPath:38 -> settings.json | only SettingsView.axaml:823 CachedImage on this same page; no other consumer found repo-wide | live | writes_unread | persists round-trip (load SettingsViewModel.cs:890, save 1204) but nothing outside Settings page shows the avatar |
| General | Your name TextBox | SettingsView.axaml:832-843 + SettingsViewModel.cs:126 | ProfileName -> AppSettings.ProfileName:32 -> settings.json (debounced QueueSettingsSave 250ms, SettingsViewModel.cs:1520) | no runtime consumer found anywhere outside Settings page (Home greeting was rejected per history) | never | writes_unread | Enter key defocuses only (SettingsView.axaml.cs:78); load 888 save 1202 round-trip OK yet value is decorative |
| General | Open Noctis when computer starts | SettingsView.axaml:858 + SettingsViewModel.cs:260 | not in AppSettings by design; ApplyLaunchAtStartup (VM:274) -> StartupHelper.SetEnabled writes OS autostart entry | OS at login; Program.cs:100 parses --startup args; load re-reads StartupHelper.IsEnabled() (VM:941) | live | none | OS entry is source of truth; on failure toggle snaps back and LaunchAtStartupError text shows (VM:285-293) |
| General | Start minimized to tray | SettingsView.axaml:872 (IsEnabled=LaunchAtStartup, line 869) + SettingsViewModel.cs:297 | StartMinimizedToTray -> AppSettings:155 -> settings.json; also re-registers autostart with --minimized flag (VM:303) | Program.cs:100 -> App.StartMinimizedAtLogin -> MainWindow.axaml.cs:313 and 597 | restart | none | read once from process args at launch; only affects next login start; disabled unless launch-at-startup on |
| General | Minimize to tray | SettingsView.axaml:878 + SettingsViewModel.cs:254 | MinimizeToTray -> AppSettings:147 -> settings.json (load VM:937, save 1250) | MainWindow.axaml.cs:547 direct read on WindowState change -> Hide() | live | none | needs tray icon present; skipped while mini player is open (MainWindow.axaml.cs:548) |
| General | Close to tray | SettingsView.axaml:884 + SettingsViewModel.cs:255 | CloseToTray -> AppSettings:150 -> settings.json (load VM:938, save 1251) | MainWindow.axaml.cs:757 direct read in OnMainWindowClosing -> cancel close plus Hide | live | none | user-initiated closes only; OS shutdown and tray Exit pass through; queue snapshot saved on hide (line 763) |
| General | Restore last played track | SettingsView.axaml:894 + SettingsViewModel.cs:256 | RestoreLastTrackOnStartup -> AppSettings:159 -> settings.json (load VM:943, save 1253) | MainWindowViewModel.cs:507 startup-only read -> Player.RestoreQueueStateAsync | restart | none | consumed once at app init; flipping mid-session only changes next launch |
| General | Animate long track titles (PLAYER BAR) | SettingsView.axaml:914 + SettingsViewModel.cs:2049 | TrackTitleMarqueeEnabled -> AppSettings:129 -> settings.json; mirrored to PlayerViewModel via ApplyPlayerSettings (VM:1384) | PlaybackBarView.axaml.cs:207 PropertyChanged listener plus reads at 289 and 416 | live | none | playbar restarts or stops marquee immediately on property change |
| General | Animate long artist names (PLAYER BAR) | SettingsView.axaml:920 + SettingsViewModel.cs:2055 | ArtistMarqueeEnabled -> AppSettings:132 -> settings.json; mirrored VM:1385 | PlaybackBarView.axaml.cs:213 PropertyChanged listener plus reads at 417 and 529 | live | none | same live path as title marquee |
| General | Animate long track titles (COVER FLOW) | SettingsView.axaml:929 + SettingsViewModel.cs:2061 | CoverFlowMarqueeEnabled -> AppSettings:135 -> settings.json; static MarqueeTextBlock.GlobalCoverFlowScrollEnabled (VM:1393) | MarqueeTextBlock.cs:278 read inside RecalcAndStart:306 | partial | other | static bool has no change notification; running marquee keeps scrolling until next text or layout recalc |
| General | Animate long artist names (COVER FLOW) | SettingsView.axaml:935 + SettingsViewModel.cs:2067 | CoverFlowArtistMarqueeEnabled -> AppSettings:138 -> settings.json; static VM:1394 | MarqueeTextBlock.cs:277 via RecalcAndStart:306 | partial | other | same lazy-static propagation as cover flow title |
| General | Animate long album titles (COVER FLOW) | SettingsView.axaml:941 + SettingsViewModel.cs:2073 | CoverFlowAlbumMarqueeEnabled -> AppSettings:141 -> settings.json; static VM:1395 | MarqueeTextBlock.cs:276 via RecalcAndStart:306 | partial | other | same lazy-static propagation |
| General | Animate long track titles (LYRICS PAGE) | SettingsView.axaml:950 + SettingsViewModel.cs:2079 | LyricsTitleMarqueeEnabled -> AppSettings:246 -> settings.json; static VM:1396 | MarqueeTextBlock.cs:272 via RecalcAndStart:306 | partial | other | same lazy-static propagation |
| General | Animate long artist/album names (LYRICS PAGE) | SettingsView.axaml:956 + SettingsViewModel.cs:2085 | LyricsArtistMarqueeEnabled -> AppSettings:249 -> settings.json; static VM:1397 | MarqueeTextBlock.cs:272 via RecalcAndStart:306 | partial | other | same lazy-static propagation |
| General | Animate long track titles (MINI PLAYER) | SettingsView.axaml:965 + SettingsViewModel.cs:2091 | MiniPlayerTitleMarqueeEnabled -> AppSettings:252 -> settings.json; static VM:1398 | MarqueeTextBlock.cs:274 via RecalcAndStart:306 | partial | other | same lazy-static propagation |
| General | Animate long album titles (MINI PLAYER) | SettingsView.axaml:971 + SettingsViewModel.cs:2097 | MiniPlayerAlbumMarqueeEnabled -> AppSettings:255 -> settings.json; static VM:1399 | MarqueeTextBlock.cs:274 via RecalcAndStart:306 | partial | other | same lazy-static propagation |
| General | Hover to expand sidebar | SettingsView.axaml:984 + SettingsViewModel.cs:1971 | SidebarHoverExpand -> AppSettings:224 -> settings.json (load VM:962, save 1272) | MainWindow.axaml.cs:473 direct VM read inside IsPointerOver handler | live | none | re-read on every hover event; save inside handler is still gated by _settingsLoaded check in SaveAsync:1121 |
| General | Collapse Album Editions | SettingsView.axaml:1000 + SettingsViewModel.cs:1997 | CollapseAlbumEditions -> AppSettings:236 -> settings.json (load VM:964, save 1274) | LibraryAlbumsViewModel.cs:267 PropertyChanged -> RebuildFilteredRows; direct reads 559 and 599 | live | none | dirty-flag while Albums view hidden; catch-up rebuild on activation (LibraryAlbumsViewModel.cs:262-272) |
| General | Merge Featured Artists From Titles | SettingsView.axaml:1014 + SettingsViewModel.cs:2003 | MergeFeaturedFromTitles -> AppSettings:243 -> settings.json; mirrors MetadataService.MergeFeaturedFromTitles static (MetadataService.cs:967) | MetadataService.cs:983 during tag parse; LibraryService.ApplyMergeFeaturedFromTitlesAsync:2025 rewrites indexed library; LibraryService.cs:1659 re-mirrors on scan | live | heavy_sync_toggle | flip triggers async full-library credit rewrite; turning OFF re-reads file tags in background with scan-status text (VM:2022-2047) |
| Appearance | Theme: Dark (tile) | SettingsView.axaml:1426; SettingsViewModel.cs:1612→1818 | ApplyTheme("Dark") → AppSettings.Theme (AppSettings.cs:21) → settings.json (PersistenceService.cs:46); save SVM:1826, sync SVM:1183-1189, load SVM:849-861 | ThemeChanged → MainWindow.axaml.cs:256 → App.SetTheme; re-tints Liquid Glass if on (MainWindow:262) | live | none | Exclusive with other theme tiles via SetActiveThemeFlags SVM:1829 |
| Appearance | Theme: Gray (tile) | SettingsView.axaml:1448; SettingsViewModel.cs:1611 | Same path as Dark; AppSettings.Theme="Gray" | ThemeChanged → MainWindow.axaml.cs:256 → App.SetTheme | live | none | Default theme; also the fallback after deleting the active custom theme (SVM:1643) |
| Appearance | Theme: Midnight (tile) | SettingsView.axaml:1470; SettingsViewModel.cs:1615 | Same path; AppSettings.Theme="Midnight" | ThemeChanged → MainWindow.axaml.cs:256 → App.SetTheme | live | none | Legacy "MidnightBlack" value migrated to "Dark" on load (SVM:855-859) |
| Appearance | Theme: Light (tile) | SettingsView.axaml:1492; SettingsViewModel.cs:1613 | Same path; AppSettings.Theme="Light" | ThemeChanged → MainWindow.axaml.cs:256 → App.SetTheme | live | none | none |
| Appearance | Theme: System (tile) | SettingsView.axaml:1514; SettingsViewModel.cs:1614 | Same path; AppSettings.Theme="System" | ThemeChanged → MainWindow.axaml.cs:256 → App.SetTheme via ResolveActiveThemeKey | live | none | OS variant picks Gray vs Light (SVM:141) |
| Appearance | Custom theme tiles (group, click to apply) | SettingsView.axaml:1547-1592; SettingsViewModel.cs:1618 | ApplyCustomTheme(id) → Theme="Custom:id" + accent (SVM:1189,1627); CustomThemes list SVM:1191-1199 → AppSettings.cs:47; load SVM:864-885 | ThemeChanged+AccentChanged → MainWindow.axaml.cs:256/267 → App.SetTheme/SetAccent | live | none | Stale Custom:id on load falls back to Gray and persists (SVM:874-880) |
| Appearance | Custom tile context menu: Edit | SettingsView.axaml:1562; SettingsViewModel.cs:1652 | action — opens ThemeEditorDialog (SVM:1677); result updates tile then SaveAsync (SVM:1692-1718) | Re-applies via ApplyCustomTheme only if edited theme was active (SVM:1715-1716) | live | none | Editing a non-active theme saves without stealing activation |
| Appearance | Custom tile context menu: Delete | SettingsView.axaml:1565; SettingsViewModel.cs:1634 | DeleteCustomTheme(id) → removes from CustomThemes → persisted via SyncToSettings SVM:1191; save SVM:1648 | If deleted theme active: falls back Gray + Crimson accent, fires ThemeChanged (SVM:1640-1646) | live | none | No confirmation prompt before delete |
| Appearance | "+ Custom" add tile | SettingsView.axaml:1595; SettingsViewModel.cs:1652 | action — opens ThemeEditorDialog with null id; new tile added SVM:1702 then activated SVM:1716 | New theme auto-applied via ApplyCustomTheme → ThemeChanged → App.SetTheme | live | none | Theme editor dialog itself out of scope per task |
| Appearance | Accent preset swatch group (12-col grid) | SettingsView.axaml:1631-1663; SettingsViewModel.cs:1740→1759 | ApplyAccent → AppSettings.AccentColorHex/AccentPresetName (AppSettings.cs:44/41); direct write SVM:1763-1764 + re-sync SVM:1205-1206; load SVM:893-903 | AccentChanged → MainWindow.axaml.cs:267 → App.SetAccent | live | none | Save debounced 250ms via QueueSettingsSave (SVM:1516-1527) |
| Appearance | Custom Color picker tile (rainbow ring) | SettingsView.axaml:1667 (ColorPickerFlyout, Hex TwoWay); SettingsViewModel.cs:179 | OnCustomAccentHexChanged → ApplyAccent(hex,"Custom") → same accent fields; PickerColor mirror SVM:170-177 | AccentChanged → MainWindow.axaml.cs:267 → App.SetAccent; live preview per drag sample | live | none | Hex normalized to 6 digits only (SVM:1803-1816); invalid input silently ignored |
| Appearance | Liquid Glass (toggle) | SettingsView.axaml:1721; SettingsViewModel.cs:1976 | LiquidGlassEnabled → AppSettings.LiquidGlassEnabled (AppSettings.cs:231); sync SVM:1273, load SVM:963 | LiquidGlassChanged → MainWindow.axaml.cs:274 → ApplyLiquidGlass(on) | live | none | Card hidden on Linux via IsLiquidGlassSupported (SVM:412); event fires even during load |
| Appearance | Animated Artwork (toggle) | SettingsView.axaml:1735; SettingsViewModel.cs:2103 | EnableAnimatedCovers → AppSettings.EnableAnimatedCovers (AppSettings.cs:144); sync SVM:1246, load SVM:933 | Direct XAML bindings to VM prop: CoverFlowView.axaml:93/610, LyricsView.axaml:544, mini player MainWindow.axaml.cs:65-67 | live | none | Handler only saves; propagation is pure INPC binding, no event needed |
| Appearance | Flowing Lyrics Background (toggle) | SettingsView.axaml:1749; SettingsViewModel.cs:2108 | LyricsFlowingLightEnabled → AppSettings.cs:333; sync SVM:1247, load SVM:934 | ApplyPlayerSettings SVM:1390 → PlayerViewModel → LyricsView.axaml:456 + LyricsView.axaml.cs:668/677 | live | none | Effect only visible in artwork color mode (LyricsView.axaml.cs:668) |
| Appearance | Fullscreen Lyrics Focus (toggle) | SettingsView.axaml:1763; SettingsViewModel.cs:2114 | LyricsFullScreenFocusEnabled → AppSettings.cs:337; sync SVM:1248, load SVM:935 | ApplyPlayerSettings SVM:1391 → PlayerViewModel → LyricsViewModel.cs:240/1876 | live | none | Live but only observable on the fullscreen lyrics page by design |
| Appearance | Join Split Words (toggle) | SettingsView.axaml:1777; SettingsViewModel.cs:2120 | LyricsJoinSplitWords → AppSettings.cs:342; sync SVM:1249, load SVM:936 | ApplyPlayerSettings SVM:1392 → PlayerViewModel → LyricsViewModel.cs:1883 re-parse + 1954 | live | none | Toggle triggers live lyric re-parse of current track |
| Appearance | Player Bar Opacity (slider + % readout) | SettingsView.axaml:1792; SettingsViewModel.cs:1984 | PlaybackBarBackgroundOpacity → AppSettings.cs:213 (clamped 0-1); sync SVM:1271, load SVM:961 | ApplyPlayerSettings SVM:1386 → PlayerViewModel.IslandBackgroundOpacity → PlaybackBarView.axaml:455 brush opacity | live | none | Save debounced (QueueSettingsSave SVM:1994); % TextBlock at 1797 is read-only display |
| Audio | Song Transitions (toggle) | src/Noctis/Views/SettingsView.axaml:1834; handler ViewModels/SettingsViewModel.cs:1915 | SongTransitionsEnabled -> AppSettings.SongTransitionsEnabled (SettingsViewModel.cs:1229) + back-compat CrossfadeEnabled (1234); saved via SaveAsync -> PersistenceService.SaveSettingsAsync | OnSongTransitionsEnabledChanged -> ApplyAudioSettings (1349) -> VlcAudioPlayer.SetCrossfade (VlcAudioPlayer.cs:1740) + ApplyAutoMixToPlayer -> PlayerViewModel.AutoMixTransitionMode (1365); loaded at startup (917) and re-applied in SetAudioPlayer (810) | live | none | Effect is at the next track transition by nature. Card disabled while Exclusive Mode on (CanUseSongTransitions, SettingsViewModel.cs:459). |
| Audio | Transition Style: AutoMix (radio) | src/Noctis/Views/SettingsView.axaml:1843; handler ViewModels/SettingsViewModel.cs:219 (IsAutoMixStyle) + 1922 (OnTransitionStyleChanged) | TransitionStyle="AutoMix" -> AppSettings.TransitionStyle (1230) | OnTransitionStyleChanged -> ApplyAudioSettings + ApplyPlayerSettings -> PlayerViewModel.AutoMixTransitionMode (1365); loaded at 918 | live | none |  |
| Audio | Transition Style: Crossfade (radio) | src/Noctis/Views/SettingsView.axaml:1853; handler ViewModels/SettingsViewModel.cs:218 + 1922 | TransitionStyle="Crossfade" -> AppSettings.TransitionStyle (1230) | Same path as AutoMix radio; VlcAudioPlayer.SetCrossfade armed via ApplyAudioSettings (1352) | live | none |  |
| Audio | Crossfade Duration (slider, 1-12s) | src/Noctis/Views/SettingsView.axaml:1867; handler ViewModels/SettingsViewModel.cs:1945; double-tap reset Views/SettingsView.axaml.cs:238 (default 6.0) | CrossfadeDuration -> AppSettings.CrossfadeDuration clamped 1-12 (1235); debounced QueueSettingsSave (1955) | ApplyAudioSettings -> VlcAudioPlayer.SetCrossfade -> _crossfadeDurationMs (VlcAudioPlayer.cs:1744), read at next transition; loaded clamped at 916 | live | none |  |
| Audio | Sound Check (toggle) | src/Noctis/Views/SettingsView.axaml:1892; handler ViewModels/SettingsViewModel.cs:1958 | SoundCheckEnabled -> AppSettings.SoundCheckEnabled (1236) | ApplyAudioSettings -> VlcAudioPlayer.SetNormalization (VlcAudioPlayer.cs:1251) stores _normalizationEnabled; applied as media options (:audio-replay-gain-*) only when new Media is created (1793-1797, 2073-2077) | partial | none | Currently-playing track is unaffected; takes effect from the next track. Persist + load round-trip verified (save 1236, load 923). |
| Audio | Gapless Playback (toggle) | src/Noctis/Views/SettingsView.axaml:1906; handler ViewModels/SettingsViewModel.cs:2210 | GaplessPlaybackEnabled -> AppSettings.GaplessPlaybackEnabled (1282) | VlcAudioPlayer.SetGapless (VlcAudioPlayer.cs:1750) + PlayerViewModel.GaplessEnabled (2213); loaded at 980 | live | none |  |
| Audio | Autoplay (toggle) | src/Noctis/Views/SettingsView.axaml:1920; handler ViewModels/SettingsViewModel.cs:2217 | AutoplayEnabled -> AppSettings.AutoplayEnabled (1283) | PlayerViewModel.AutoplayEnabled set live (2221); player reads it at each queue exhaustion; loaded at 981 | live | none |  |
| Audio | Exclusive Mode (toggle, Windows only) | src/Noctis/Views/SettingsView.axaml:1937; handler ViewModels/SettingsViewModel.cs:1964 | ExclusiveAudioEnabled -> AppSettings.ExclusiveAudioEnabled (1286) | VlcAudioPlayer.SetExclusiveMode (VlcAudioPlayer.cs:1286) rebuilds both MediaPlayers on a worker thread under _playbackLock, resumes position (1299-1317, RebuildOutputModeLocked 1324); status fed back via OutputModeChanged -> ExclusiveAudioStatus (SettingsViewModel.cs:801-808); loaded at 984 gated by IsExclusiveAudioSupported | live | none | Card hidden off-Windows (IsExclusiveAudioSupported, SettingsViewModel.cs:450). Rebuild is async, not UI-blocking. |
| Audio | Analyze Tempo & Key (toggle) | src/Noctis/Views/SettingsView.axaml:1952; handler ViewModels/SettingsViewModel.cs:2226 | BpmKeyAnalysisEnabled -> AppSettings.BpmKeyAnalysisEnabled (1284) | AudioAnalysisCoordinator.StartBackfill checks _settings().BpmKeyAnalysisEnabled (Services/AudioAnalysis/AudioAnalysisCoordinator.cs:54); StartBackfill is ONLY invoked from LibraryUpdated (App.axaml.cs:141) | partial | handler only persists; no consumer is notified on enable | Turning ON mid-session starts nothing until the next LibraryUpdated (scan/import/watcher batch) or restart. Turning OFF does not cancel a running pass either (no Stop() call). See finding. |
| Audio | Save analysis to file tags (sub-toggle) | src/Noctis/Views/SettingsView.axaml:1962; handler ViewModels/SettingsViewModel.cs:2232 | WriteAnalysisToTags -> AppSettings.WriteAnalysisToTags (1285) | AudioAnalysisCoordinator per-track during a backfill pass (AudioAnalysisCoordinator.cs:166) | partial | only affects tracks analyzed after enabling; never retroactive | Tracks already holding detected BPM/key are excluded by NeedsAnalysis (Bpm<=0 filter, line 48-49), so their tags are never written. See finding. |
| Audio | Equalizer preset (ComboBox) | src/Noctis/Views/SettingsView.axaml:1981; handler ViewModels/SettingsViewModel.cs:2704 (OnSelectedEqPresetNameChanged) | SelectedEqPresetName/Index -> AppSettings.EqualizerPresetIndex (1289) + ParametricEqBands (1290) + legacy EqualizerBands mirror (1294); debounced QueueEqualizerSave | ApplyEqualizer (1402) -> VlcAudioPlayer.SetAdvancedEqualizer -> ApplyAdvancedEqualizerSnapshot applies to live + standby players (VlcAudioPlayer.cs:1160-1245); loaded at 989-998 | live | none | Flat curve applied as true bypass to avoid VLC's -12dB EQ input scaling (comment at VlcAudioPlayer.cs ~1163-1180). |
| Audio | Equalizer Reset (button) | src/Noctis/Views/SettingsView.axaml:1989; handler ViewModels/SettingsViewModel.cs:2778 (ResetEqualizerCommand) | action: preset=Flat, bands flattened; persisted via QueueEqualizerSave | ApplyEqualizer -> player, same path as preset combo | live | none |  |
| Audio | EQ band Frequency (NumericUpDown, per band) | src/Noctis/Views/SettingsView.axaml:2014; handler ViewModels/EqBandViewModel.cs:34 -> SettingsViewModel.cs:2741 (OnEqBandEdited) | EqBandViewModel.FrequencyHz -> AppSettings.ParametricEqBands (1290); switches preset to Custom (2749) | OnEqBandEdited -> ApplyEqualizer -> live player | live | none |  |
| Audio | EQ band Gain (slider, per band) | src/Noctis/Views/SettingsView.axaml:2022; handler ViewModels/EqBandViewModel.cs:46; double-tap reset Views/SettingsView.axaml.cs:249 (GainDb=0) | EqBandViewModel.GainDb (0.1 dB snap) -> AppSettings.ParametricEqBands | OnEqBandEdited -> ApplyEqualizer; standby-player update skipped while dragging (VlcAudioPlayer.cs:1199-1212) | live | none |  |
| Audio | EQ band Q (NumericUpDown, per band) | src/Noctis/Views/SettingsView.axaml:2032; handler ViewModels/EqBandViewModel.cs (OnQChanged) -> OnEqBandEdited | EqBandViewModel.Q -> AppSettings.ParametricEqBands | OnEqBandEdited -> ApplyEqualizer | live | none |  |
| Audio | EQ remove band (x button, per band) | src/Noctis/Views/SettingsView.axaml:2040; handler ViewModels/SettingsViewModel.cs:2769 (RemoveEqBandCommand) | removes band -> ParametricEqBands on next save | OnEqBandEdited -> ApplyEqualizer; gated by CanRemoveEqBand (486) | live | none |  |
| Audio | + Add Band (button) | src/Noctis/Views/SettingsView.axaml:2052; handler ViewModels/SettingsViewModel.cs:2758 (AddEqBandCommand) | adds neutral 1 kHz band -> ParametricEqBands | OnEqBandEdited -> ApplyEqualizer; gated by CanAddEqBand (485) | live | none |  |
| Audio | ffmpeg path (TextBox) | src/Noctis/Views/SettingsView.axaml:2079; handler ViewModels/SettingsViewModel.cs:2150 (OnFfmpegPathChanged); Enter key Views/SettingsView.axaml.cs:67 | FfmpegPath -> AppSettings.FfmpegPath immediately (2157) + SyncToSettings (1279); debounced save + debounced ffmpeg -version probe (QueueFfmpegProbe 2168) | AudioConverterService.GetFfmpegPath via lazy accessor (Program.cs:291-294 reads Settings.GetSettings().FfmpegPath); also AudioAnalysisService via converter; status shown async off-thread (RefreshFfmpegStatus 2252-2280) | live | none |  |
| Audio | ffmpeg Browse (button) | src/Noctis/Views/SettingsView.axaml:2087; handler ViewModels/SettingsViewModel.cs:2284 (BrowseFfmpegAsync) | action: file picker -> sets FfmpegPath (2299), which persists via the TextBox path | same as ffmpeg path TextBox | live | none |  |
| Audio | ReplayGain (toggle) | src/Noctis/Views/SettingsView.axaml:2107; handler ViewModels/SettingsViewModel.cs:2204 (OnReplayGainEnabledChanged) | mirrors to ReplayGainMode (last active mode, default "Auto" per line 434, or "Off") -> AppSettings.ReplayGainMode (1280) | OnReplayGainModeChanged (2188) -> VlcAudioPlayer.ApplyReplayGain (VlcAudioPlayer.cs:1596) -> ReapplyVolume immediately; loaded 975/979 | live | none | Toggle itself is not persisted; it is derived from ReplayGainMode != Off on load (979). Round-trip correct. |
| Audio | ReplayGain Mode (ComboBox: Off/Track/Album/Auto) | src/Noctis/Views/SettingsView.axaml:2113; handler ViewModels/SettingsViewModel.cs:2188 | ReplayGainMode -> AppSettings.ReplayGainMode (1280) | VlcAudioPlayer.ApplyReplayGain: reads RG tags of current file (cached per path, 1670-1686) and rescales session volume live (ReapplyVolume 1646) | live | none | Option strings match the lowercase switch in ApplyReplayGain ("track"/"album"/"auto") — verified. |
| Audio | ReplayGain Pre-amp (slider, -12..+12 dB) | src/Noctis/Views/SettingsView.axaml:2148 (custom canvas visuals 2125-2147); handler ViewModels/SettingsViewModel.cs:2238; double-tap reset Views/SettingsView.axaml.cs:230 (0.0); drag plumbing Views/SettingsView.axaml.cs:39-46 | ReplayGainPreampDb -> AppSettings.ReplayGainPreampDb (1281); debounced QueueSettingsSave | ApplyReplayGain(mode, value) live per tick; per-tick TagLib read avoided by _rgCache (VlcAudioPlayer.cs:1661-1686); loaded clamped -12..12 (978) | live | none |  |
| Library | Add Folder (button) | src/Noctis/Views/SettingsView.axaml:2623; handler Views/SettingsView.axaml.cs:33 + 345 (OnAddFolderClicked) -> ViewModels/SettingsViewModel.cs:3181 (AddFolderPath) | MusicFolders + AppSettings.MusicFolders (3217) + SaveAsync | MusicFoldersChanged -> MainWindowViewModel.cs:325 -> LibraryWatcherService.Refresh (rebuilds watchers); auto-scan via RunLibraryScanAsync (3226); duplicate/nested-root guards 3197-3214 | live | none |  |
| Library | Remove folder (x button, per folder row) | src/Noctis/Views/SettingsView.axaml:2652; handler ViewModels/SettingsViewModel.cs:3246 (RemoveFolderCommand) | MusicFolders + AppSettings.MusicFolders (3250) + SaveAsync | MusicFoldersChanged -> watcher Refresh; auto re-scan drops removed tracks (3256) | live | none |  |
| Library | Scan Library (button) | src/Noctis/Views/SettingsView.axaml:2688; handler ViewModels/SettingsViewModel.cs:3300 (RescanCommand) -> RunLibraryScanAsync (3309) | action: LibraryService.ScanAsync over MusicFolders | library scan; supersedes in-flight scans (3316-3322); drives IsScanning/ScanStatusText | live | post-scan completion runs a synchronous recursive artwork-dir size walk on the UI thread | RefreshStorageInfo(forceRefresh: true) at 3346 runs on the UI-context continuation (ConfigureAwait(true) at 3329) and enumerates every file under data/artwork synchronously (3156-3159). See finding. |
| Library | Organize Files: Organize (button) | src/Noctis/Views/SettingsView.axaml:2712; handler ViewModels/SettingsViewModel.cs:3284 (OpenOrganizeFilesCommand) | action: opens organize dialog (MetadataHelper.OpenOrganizeFilesDialog) | FileOrganizerService via dialog; OrganizePattern/OrganizeTargetRoot persisted separately (1211-1212) | live | none |  |
| Library | Find Duplicates: Find (button) | src/Noctis/Views/SettingsView.axaml:2732; handler ViewModels/SettingsViewModel.cs:3288 (OpenDuplicateFinderCommand) | action: opens duplicate-finder dialog | DuplicateFinderService via dialog | live | none |  |
| Library | Find Metadata: Find (button) | src/Noctis/Views/SettingsView.axaml:2752; handler ViewModels/SettingsViewModel.cs:3292 (OpenMetadataFinderCommand) | action: opens metadata-finder dialog | MetadataFinderService via dialog | live | none |  |
| Library | Import Playlist: Import (button) | src/Noctis/Views/SettingsView.axaml:2772; handler ViewModels/SettingsViewModel.cs:3296 (OpenPlaylistImportCommand) | action: opens playlist-import dialog | PlaylistImportService via dialog | live | none |  |
| Library | Scan on Startup (toggle) | src/Noctis/Views/SettingsView.axaml:2792; handler ViewModels/SettingsViewModel.cs:1875 | ScanOnStartup -> AppSettings.ScanOnStartup (1877, re-applied in SyncToSettings 1208) | MainWindowViewModel.cs:514 at startup only (Settings.GetSettings().ScanOnStartup && MusicFolders.Count > 0) | restart | none | Restart-time read is the setting's intent; load path verified (905). |
| Library | Watch Folders (toggle) | src/Noctis/Views/SettingsView.axaml:2808; handler ViewModels/SettingsViewModel.cs:1881 | WatchFoldersEnabled -> AppSettings.WatchFoldersEnabled (1883, SyncToSettings 1209) | ILibraryWatcherService.Refresh() called from the handler (1886); LibraryWatcherService.Refresh reads settings.WatchFoldersEnabled and rebuilds FileSystemWatchers (Services/LibraryWatcherService.cs:46-90); also refreshed at startup (MainWindowViewModel.cs:530) | live | none |  |
| Library | Use Embedded Artwork (toggle) | src/Noctis/Views/SettingsView.axaml:2824; handler ViewModels/SettingsViewModel.cs:1889 | UseEmbeddedArtwork -> AppSettings.UseEmbeddedArtwork (1895, SyncToSettings 1210) | MetadataService.UseEmbeddedArtwork static mirror set even during load (1893); enabling kicks _library.BackfillMissingArtworkAsync (1899, single-flight guarded LibraryService.cs:689-692) | live | none | Turning off intentionally keeps already-cached covers (fill-once design, comment 1897-1898). |
| Library | Un-snooze (button, per snoozed track) | src/Noctis/Views/SettingsView.axaml:2867; handler ViewModels/SettingsViewModel.cs:2809 (UnsnoozeCommand) | action: LibraryService.SetTracksSnoozedAsync(track, null) | shuffle/radio exclusion logic; list refreshed (2813) | live | none |  |
| Library | Restore (button, per removed track) | src/Noctis/Views/SettingsView.axaml:2918; handler ViewModels/SettingsViewModel.cs:2848 (RestoreRemovedTrackCommand) | action: LibraryService.ImportFilesAsync drops ExcludedFilePaths tombstone + re-adds track | library; list refreshed via RefreshRemovedTracksAsync (re-reads settings from disk because LibraryService owns ExcludedFilePaths, 2824-2846) | live | none |  |
| Library | Open Data Folder (button) | src/Noctis/Views/SettingsView.axaml:2954; handler ViewModels/SettingsViewModel.cs:3775 (OpenDataFolderCommand) | action: PlatformHelper.OpenFolder(DataDirectory) | OS file manager | live | none |  |
| Library | Reset Settings (button) | src/Noctis/Views/SettingsView.axaml:3044; handler ViewModels/SettingsViewModel.cs:3398 (ShowResetConfirmCommand) | action: shows confirm UI with real playlist count (3405-3411) | IsResetConfirmVisible drives the confirm panel | live | none |  |
| Library | Reset confirm: Cancel (button) | src/Noctis/Views/SettingsView.axaml:3062; handler ViewModels/SettingsViewModel.cs:3418 (CancelResetCommand) | action: hides confirm panel | IsResetConfirmVisible | live | none |  |
| Library | Reset confirm: Reset Everything (button) | src/Noctis/Views/SettingsView.axaml:3069; handler ViewModels/SettingsViewModel.cs:3421 (ConfirmResetLibraryCommand) | action: clears library/playlists/queue/artwork/lyrics/covers/cache/audit/crash/index, writes default AppSettings (3562), resets whole VM, ApplyAudioSettings + ApplyPlayerSettings (3724-3727) | everything; SettingsReset event (3745) | live | multiple synchronous recursive Directory.Delete calls run on the UI thread | Directory.Delete(artworkDir, true) etc. at 3460, 3477, 3492, 3507, 3522 execute between awaits on the UI dispatcher. See finding (shared with Clear Cache). |
| Library | Clear Artwork Cache: Clear Cache (button) | src/Noctis/Views/SettingsView.axaml:3093; handler ViewModels/SettingsViewModel.cs:3748 (ClearArtworkCacheCommand) | action: deletes data/artwork recursively, recreates artwork + artwork/artists, invalidates dir-size cache, refreshes storage rows | artwork rebuilt on next scan; ArtistImageService dir precreated (3763) | live | synchronous recursive Directory.Delete of the whole artwork cache on the UI thread | Plain (non-async) RelayCommand: Directory.Delete(artworkDir, true) at 3756 blocks the UI for the duration of the delete. See finding. |
| Statistics | Statistics tab selector button | src/Noctis/Views/SettingsView.axaml:757-759; handler ViewModels/SettingsViewModel.cs:118-119, 100-107 | nothing (SelectedSettingsTab, session-only) | OnSelectedSettingsTabChanged: RefreshLibraryStats() -> RefreshLibraryStatsAsync (snapshot on UI thread, compute in Task.Run, SettingsViewModel.cs:2875-2888) + RefreshPlaylistCountAsync (fire-and-forget) | live | none | Stats recompute is explicitly off-thread; no UI-stall risk found. |
| Statistics | View All Stats -> button | src/Noctis/Views/SettingsView.axaml:3337-3343; handler ViewModels/SettingsViewModel.cs:4348-4351 | action | OpenStatisticsRequested event, subscribed in ViewModels/MainWindowViewModel.cs:353 (navigates to full stats page) | live | none | Only interactive control on the Statistics tab; everything else (TotalSongs, TotalArtists, TotalAlbums, TotalListeningTime, LosslessCount, HiResCount, LossyCount, TotalPlaylists, TotalPlays, TimeListened, AvgTrackLength, LikedTracks, TopArtists, TopAlbums lists) is read-only display. |
| Integrations | Integrations tab selector button | src/Noctis/Views/SettingsView.axaml:761-763; handler ViewModels/SettingsViewModel.cs:118-119 | nothing | tab visibility bindings (IsIntegrationsTabVisible) | live | none |  |
| Integrations | Web Remote toggle | src/Noctis/Views/SettingsView.axaml:2181-2183; handler ViewModels/SettingsViewModel.cs:363-367 | WebRemoteEnabled -> AppSettings.WebRemoteEnabled (Models/AppSettings.cs:162) via SyncToSettings:1254; reloaded at SettingsViewModel.cs:944 | UpdateWebRemoteState (SettingsViewModel.cs:369-405) starts/stops WebRemoteServer (TcpListener, Services/WebRemoteServer.cs:43-48); port-conflict falls back to ephemeral port | live | none | Also re-armed at startup: the load at :944 fires the same change handler (persisted true starts the server; _player is already set per comment at :1086-1089). Displayed URL (axaml:2189) includes the auth token as ?k= query param — LAN-only HTTP by design. |
| Integrations | Discord Rich Presence toggle | src/Noctis/Views/SettingsView.axaml:2217-2220; handler ViewModels/SettingsViewModel.cs:2304-2346 | DiscordRichPresenceEnabled -> AppSettings.DiscordRichPresenceEnabled (Models/AppSettings.cs:278) via SyncToSettings:1295; reloaded :1019, startup connect :1076-1083 | HandleDiscordToggleAsync (connect + Loon relay + presence republish on enable; Clear/Disconnect/Loon-disconnect on disable); _discord.IsEnabled closure (:1074) lets the service's reconnect loop observe the live value; MainWindowViewModel.cs:2469-2501 publishes presence | live | none | Toggle auto-reverts if Discord connect fails (:2324-2328). Suspend-guarded during load (:2306) so startup connect is done once explicitly. |
| Integrations | Last.fm Connect button | src/Noctis/Views/SettingsView.axaml:2260-2281; handler ViewModels/SettingsViewModel.cs:2415-2448 (LoginLastFm) | action; on success LastFmScrobblingEnabled=true, LastFmUsername, _settings.LastFmSessionKey (:2463-2468) then SaveAsync (session key DPAPI-protected at rest, Services/PersistenceService.cs:173) | opens browser auth URL, polls CompleteAuthAsync for 2 min (:2450-2499); LastFmService holds session for scrobbling | live | none | Stale-poll dead-button fixed via CTS cancel (:2424-2426). Auth uses API key/secret hardcoded in Services/LastFmService.cs:15-16 — see finding. |
| Integrations | Last.fm Logout button | src/Noctis/Views/SettingsView.axaml:2285-2295; handler ViewModels/SettingsViewModel.cs:2501-2509 | action; clears service session + LastFmUsername, SaveAsync (SyncToSettings:1297-1299 persists cleared username + null session key) | LastFmService.Logout(); scrobble gates at MainWindowViewModel.cs:2475/2564 go false via IsAuthenticated | live | none | Leaves LastFmScrobblingEnabled=true while its toggle is hidden (IsVisible=IsLastFmConnected, axaml:2299). Harmless for Last.fm because re-arming requires the full browser auth (which sets it true anyway) — unlike ListenBrainz, see finding. |
| Integrations | Last.fm Enable Scrobbling toggle | src/Noctis/Views/SettingsView.axaml:2304-2306; handler ViewModels/SettingsViewModel.cs:2410-2413 | LastFmScrobblingEnabled -> AppSettings.LastFmScrobblingEnabled (Models/AppSettings.cs:286) via SyncToSettings:1296; reloaded :1020 | MainWindowViewModel.cs:2475 (now-playing) and :2564 (scrobble gate) read the live VM property | live | none | Only visible while connected. |
| Integrations | ListenBrainz user-token TextBox | src/Noctis/Views/SettingsView.axaml:2348-2355; handler ViewModels/SettingsViewModel.cs:2518-2525 | ListenBrainzToken; persisted by SyncToSettings:1302 on ANY SaveAsync (DPAPI-protected at rest, Services/PersistenceService.cs:174); reloaded :1032 and Configure()d at startup :1034-1036 | ListenBrainzService.Configure(value) per keystroke — service IsAuthenticated becomes true on any non-blank token (Services/ListenBrainzService.cs:29) | partial | typed-but-never-validated token arms the service immediately and is persisted by unrelated saves, contradicting the handler's own persist-on-Connect comment | View-level focus-drop plumbing at Views/SettingsView.axaml.cs:99-119. No Enter->Connect KeyBinding (media-server fields have one). See findings 1 and 2. |
| Integrations | ListenBrainz Connect button | src/Noctis/Views/SettingsView.axaml:2366-2387; handler ViewModels/SettingsViewModel.cs:2527-2560 (TestListenBrainz) | action; on valid token sets ListenBrainzUsername, IsListenBrainzConnected, ListenBrainzScrobblingEnabled=true, SaveAsync (:2542-2551) | ListenBrainzService.ValidateTokenAsync; inline error text (axaml:2357-2361) on failure | live | none |  |
| Integrations | ListenBrainz Logout button | src/Noctis/Views/SettingsView.axaml:2390-2399; handler ViewModels/SettingsViewModel.cs:2562-2572 | action; clears service token, ListenBrainzToken, ListenBrainzUsername, SaveAsync | ListenBrainzService.Logout() | live | does NOT clear ListenBrainzScrobblingEnabled — flag stays true but its toggle becomes invisible (axaml:2403), so typing any token later silently resumes scrobbling with an unvalidated token | See finding 1. |
| Integrations | ListenBrainz Enable Scrobbling toggle | src/Noctis/Views/SettingsView.axaml:2408-2410; handler ViewModels/SettingsViewModel.cs:2513-2516 | ListenBrainzScrobblingEnabled -> AppSettings.ListenBrainzScrobblingEnabled (Models/AppSettings.cs:296) via SyncToSettings:1301; reloaded :1031 | MainWindowViewModel.cs:2479/2565 scrobble gates (live VM property) | live | none | Hidden while disconnected yet retains its value — part of finding 1. |
| Integrations | Music Server: SERVER TYPE ComboBox | src/Noctis/Views/SettingsView.axaml:2450-2454 | MediaServerType (VM prop; no change handler). Persisted only inside the SourceConnection rebuilt on successful connect (SyncToSettings:1305-1314); restored at load :1043-1065 | ConnectMediaServer maps it via MediaServerOptionToSourceType (SettingsViewModel.cs:2618-2627) and stores it as connection.Name (:2654) | live | none | Consumed on Connect click; intentionally not persisted until a connection succeeds. |
| Integrations | Music Server: SERVER ADDRESS TextBox | src/Noctis/Views/SettingsView.axaml:2459-2467 (Enter KeyBinding -> ConnectMediaServerCommand at :2465) | MediaServerUrl (VM prop); persisted in the connection on successful connect (SyncToSettings:1305-1314) | ConnectMediaServer (:2628-2656); normalized back into the box after connect (:2656) | live | none | Consumed on Connect click / Enter. |
| Integrations | Music Server: USERNAME TextBox | src/Noctis/Views/SettingsView.axaml:2471-2479 (Enter KeyBinding :2477) | MediaServerUsername (VM prop); persisted in the connection on successful connect | ConnectMediaServer (:2633-2645) | live | none |  |
| Integrations | Music Server: PASSWORD TextBox | src/Noctis/Views/SettingsView.axaml:2483-2492 (Enter KeyBinding :2490) | MediaServerPassword (VM prop); NEVER persisted from the bound field — cleared after connect (SettingsViewModel.cs:2657); stored credential lives in the connection object, DPAPI-protected at rest (Services/PersistenceService.cs:177-180) | ConnectMediaServer (:2633-2645) | live | none | Good hygiene: bound field wiped, at-rest protection applied. |
| Integrations | Music Server: Connect button | src/Noctis/Views/SettingsView.axaml:2515-2537; handler ViewModels/SettingsViewModel.cs:2622-2668 | action; on success rebuilds AppSettings.SourceConnections (SyncToSettings:1305-1314) + SaveAsync | IMediaServerService.ConnectAsync + SetActiveConnection; MediaServerConnectionChanged event (:2661) notifies the shell | live | none | Async; busy state disables form; transient errors auto-dismiss after 3s (:2584-2595). |
| Integrations | Music Server: Disconnect button | src/Noctis/Views/SettingsView.axaml:2539-2546; handler ViewModels/SettingsViewModel.cs:2670-2681 | action; removes connection from SourceConnections on save | SetActiveConnection(null) + MediaServerConnectionChanged event | live | none |  |
| Integrations | LRCLIB toggle | src/Noctis/Views/SettingsView.axaml:2562-2564; handler ViewModels/SettingsViewModel.cs:2126-2130 | LrcLibEnabled -> AppSettings.LrcLibEnabled (Models/AppSettings.cs:347) via SyncToSettings:1276; reloaded :968 | ViewModels/LyricsViewModel.cs:973-974 — loads settings.json fresh from disk at each lyrics search | live | none | Applies on the next lyrics search. Tiny inherent race: the handler's SaveAsync is fire-and-forget, so a search started milliseconds after the toggle could still read the old on-disk value — not user-provable without runtime tracing, not flagged. |
| Integrations | NetEase Cloud Music toggle | src/Noctis/Views/SettingsView.axaml:2571-2573; handler ViewModels/SettingsViewModel.cs:2132-2136 | NetEaseEnabled -> AppSettings.NetEaseEnabled (Models/AppSettings.cs:350) via SyncToSettings:1287; reloaded :985 | ViewModels/LyricsViewModel.cs:975 (fresh disk read per search) | live | none |  |
| Integrations | Deezer toggle | src/Noctis/Views/SettingsView.axaml:2589-2591; handler ViewModels/SettingsViewModel.cs:2144-2148 | DeezerEnabled -> AppSettings.DeezerEnabled (Models/AppSettings.cs:355) via SyncToSettings:1277; reloaded :971 | Services/MetadataFinderService.cs:34 and Services/AutoMatchCoordinator.cs:40, both via Func<AppSettings> resolving to the live GetSettings() instance (Program.cs:286, 303) | live | none |  |
| Integrations | MusicBrainz toggle | src/Noctis/Views/SettingsView.axaml:2598-2600; handler ViewModels/SettingsViewModel.cs:2138-2142 | MusicBrainzEnabled -> AppSettings.MusicBrainzEnabled (Models/AppSettings.cs:358) via SyncToSettings:1278; reloaded :972 | Services/MetadataFinderService.cs:58 (fallback source), same live Func<AppSettings> | live | none |  |
| About | About tab selector button | src/Noctis/Views/SettingsView.axaml:765-768; handler ViewModels/SettingsViewModel.cs:114-115 | nothing | when DeveloperMode is on, re-fetches the GitHub release list on every About-tab open (RefreshReleasesAsync) | live | none |  |
| About | Version number (click-to-copy button) | src/Noctis/Views/SettingsView.axaml:1044-1054; handler ViewModels/SettingsViewModel.cs:4306-4323 | action (clipboard: version + OS/arch) | clipboard; 'Copied!' confirmation via VersionCopied (1.5s) | live | none | Pre-Release badge next to it (axaml:1060-1070) is display-only, driven by UpdateService.IsPrereleaseBuild (Services/UpdateService.cs:48). |
| About | Check for Updates pill | src/Noctis/Views/SettingsView.axaml:1083-1098; handler ViewModels/SettingsViewModel.cs:3826-3889 | action | UpdateService.CheckForUpdateAsync(IncludePrereleaseUpdates, 15s timeout); result drives IsUpToDate / IsUpdateAvailable / status text; startup silent check at MainWindowViewModel.cs:560 feeds the same badge | live | none |  |
| About | Update Available button (in-app installer path) | src/Noctis/Views/SettingsView.axaml:1102-1110; handler ViewModels/SettingsViewModel.cs:3892-3950 | action | re-checks then DownloadInstallerAsync with requireChecksums:true; progress via Dispatcher-posted Progress<double> | live | none | Visible only when IsUpdateAvailable && CanInstallInApp (SettingsViewModel.cs:693). |
| About | Update button (manual / Scoop-portable path) | src/Noctis/Views/SettingsView.axaml:1114-1123; handler ViewModels/SettingsViewModel.cs:4327-4333 | action | opens the GitHub release page for LatestVersionTag (or /releases/latest) | live | none | Visible when IsUpdateAvailable && !CanInstallInApp (:697); tooltip carries the package-manager update command (ExternalUpdateHint, Services/UpdateService.cs:90). |
| About | Install & Restart button | src/Noctis/Views/SettingsView.axaml:1126-1133; handler ViewModels/SettingsViewModel.cs:3959-3980 | action | UpdateService.LaunchInstaller + desktop.TryShutdown(0) so the graceful-save handler runs (comment :3965-3968) | live | none |  |
| About | Cancel button (update download) | src/Noctis/Views/SettingsView.axaml:1152-1157; handler ViewModels/SettingsViewModel.cs:3953-3956 | action | cancels _updateCts; download loop surfaces 'Download cancelled.' | live | none |  |
| About | Star on GitHub link-button | src/Noctis/Views/SettingsView.axaml:1162-1173; handler ViewModels/SettingsViewModel.cs:4295-4298 | action | PlatformHelper.OpenUrl(GitHub repo URL) | live | none |  |
| About | Join Discord link-button | src/Noctis/Views/SettingsView.axaml:1174-1186; handler ViewModels/SettingsViewModel.cs:4336-4339 | action | PlatformHelper.OpenUrl(discord invite) | live | none |  |
| About | Official Website link-button | src/Noctis/Views/SettingsView.axaml:1187-1200; handler ViewModels/SettingsViewModel.cs:4342-4345 | action | PlatformHelper.OpenUrl(https://noctisapp.cc/) | live | none |  |
| About | Include pre-release updates toggle | src/Noctis/Views/SettingsView.axaml:1215-1217; handler ViewModels/SettingsViewModel.cs:1902-1906 | IncludePrereleaseUpdates -> AppSettings.IncludePrereleaseUpdates (Models/AppSettings.cs:77) via handler + SyncToSettings:1217 (explicit re-apply after the merge-from-disk rebase, comment :1213-1216); reloaded :910 | every CheckForUpdateAsync call (:3805, :3848, :3908) including the startup silent check | live | none | Round-trip verified against the MergeExternalSettingChangesAsync revert trap. |
| About | Developer Mode toggle | src/Noctis/Views/SettingsView.axaml:1236-1238; handler ViewModels/SettingsViewModel.cs:4036-4051 | DeveloperMode -> AppSettings.DeveloperMode (Models/AppSettings.cs:80) via handler :4038 + SyncToSettings:1218; reloaded :911 | reveals dev panel (axaml:1241); DebugLog.VlcBridgeEnabled=value (:4043, live LibVLC log mirroring); on-enable loads crash banner + logs + RefreshReleasesAsync; About-tab open re-fetches (:114-115) | live | none | Startup load with persisted true fires the same handler (releases fetched once at startup); SaveAsync no-ops during load via the _settingsLoaded/_suspendSettingPersistence guard (:1121). |
| About | Version manager: per-release Install/Download button | src/Noctis/Views/SettingsView.axaml:1314-1320; handler ViewModels/SettingsViewModel.cs:4127-4196 (+ DownloadReleaseToDownloadsAsync :4203-4261) | action | DownloadInstallerAsync with requireChecksums:true then LaunchInstaller + TryShutdown; non-self-installable copies download to Downloads (path-traversal-hardened asset name, :4225-4230) or open the release page | live | none |  |
| About | Show N older versions button | src/Noctis/Views/SettingsView.axaml:1328-1341; handler ViewModels/SettingsViewModel.cs:4114-4119 | action | appends the remaining fetched releases to DevReleases | live | none |  |
| About | Cancel button (dev download) | src/Noctis/Views/SettingsView.axaml:1355-1358; handler ViewModels/SettingsViewModel.cs:4264 | action | cancels _devCts | live | none |  |
| About | Copy Logs button | src/Noctis/Views/SettingsView.axaml:1373-1375; handler ViewModels/SettingsViewModel.cs:4267-4279 | action (clipboard: preserved crash block + live session log) | clipboard; DevLogsCopied confirmation | live | none |  |
| About | Clear button (debug logs) | src/Noctis/Views/SettingsView.axaml:1376-1378; handler ViewModels/SettingsViewModel.cs:4282-4288 | action | CrashJournal.ClearPreserved() + DebugLog.Clear(), pane re-rendered | live | none |  |
| About | Open Folder button (logs / app data) | src/Noctis/Views/SettingsView.axaml:1379-1382; handler ViewModels/SettingsViewModel.cs:4292 | action | PlatformHelper.OpenFolder(AppPaths.DataRoot) | live | none |  |

Coverage notes:
- **general-appearance**: Tabs covered in full. General: src/Noctis/Views/SettingsView.axaml lines 790-1019 (GeneralTabPanel) — 19 interactive controls: avatar button, name TextBox, 5 startup/tray toggles, 10 text-animation/sidebar toggles, 2 library-behavior toggles. Appearance: lines 1409-1806 (AppearanceTabPanel) — 5 built-in theme buttons, custom-theme tiles (dynamic ItemsControl) with Edit/Delete context-menu items, '+ Custom' tile, 12-column accent-swatch grid (dynamic, App.AccentPresets), custom-accent ColorPickerFlyout, 4-5 toggles (Liquid Glass hidden on Linux via IsLiquidGlassSupported), 1 opacity slider. Persistence round-trips verified control-by-control against SettingsViewModel.LoadAsync (:844-1116), SyncToSettings (:1181-1325, which must re-apply every VM-owned field because MergeExternalSettingChangesAsync re-bases on disk first) and PersistenceService.LoadSettingsAsync/SaveSettingsAsync (Services/PersistenceService.cs:107-183); every field in these two tabs is written AND reloaded. LaunchAtStartup is deliberately not in AppSettings (OS registry/plist/.desktop is the source of truth; load reads StartupHelper.IsEnabled()). Not fully traced: internals of ThemeEditorDialog (own file, opened by '+ Custom'/Edit — its controls are outside SettingsView.axaml and were treated as an action target, not enumerated); the shared tab-strip buttons (axaml:738-768) are navigation chrome, not settings, and were not given rows. NOTE ON OUTPUT SHAPE: the schema listed 'rows' once; the complete 35-row control inventory was supplied as the first 'rows' array in this call — if the harness kept only the last duplicate key (empty array), re-request and I will resend with rows populated once.
- **audio-library**: Audio tab: src/Noctis/Views/SettingsView.axaml lines 1811-2163 (AudioTabPanel start to closing StackPanel before IntegrationsTabPanel at 2168) — walked top-to-bottom; 22 interactive controls inventoried (Song Transitions toggle, 2 style radios, crossfade slider, Sound Check, Gapless, Autoplay, Exclusive Mode, Analyze Tempo & Key + tag-write sub-toggle, EQ preset combo, EQ reset, per-band freq/gain/Q + remove, add band, ffmpeg textbox + browse, ReplayGain toggle/mode/pre-amp). Library tab: lines 2611-3108 (LibraryTabPanel start to its closing StackPanel before StatisticsTabPanel at 3113) — 17 interactive controls (Add Folder, per-folder remove, Scan Library, Organize, Find Duplicates, Find Metadata, Import Playlist, Scan on Startup, Watch Folders, Use Embedded Artwork, per-row Un-snooze and Restore, Open Data Folder, Reset Settings + Cancel + Reset Everything, Clear Cache). Persistence round-trips verified for every persisted field via three concrete points each: SyncToSettings write (SettingsViewModel.cs 1208-1294), LoadAsync read (905-998), and consumer; the reflection-based MergeExternalSettingChangesAsync/SyncToSettings re-apply covers all Audio/Library fields, so none are lost to the known merge-revert trap. Assignment caveats: (1) the brief listed 'keep-alive' under Audio — no keep-alive control exists anywhere in SettingsView.axaml (grep for KeepAlive/keep-alive: zero hits); WasapiSilenceKeepAlive has no settings UI, so there is nothing to trace. (2) The brief listed 'lyrics options' under Library — the LYRICS provider section (axaml 2551-2610, LrcLib/NetEase toggles) physically sits inside IntegrationsTabPanel (2168-2610), i.e. the Integrations auditor's tab, and the lyrics appearance toggles (flowing background 1750, fullscreen focus 1764, join split words 1778, lyrics marquees 950/956) are in the Appearance tab; none are rendered in the Library tab, so they are outside my two panels — flagging here so the Integrations/Appearance auditor does not skip them. Display-only elements (ExclusiveAudioStatus text, FfmpegStatus, scan spinner/status, storage size rows, snoozed/removed empty states) were traced but not counted as controls. The tab-selector buttons at 749-756 belong to the Settings shell, not to my tabs' content. Nothing in my two panels was left untraced.
- **stats-integrations-about**: Tab ranges walked top-to-bottom in src/Noctis/Views/SettingsView.axaml: About = 1024-1404 (AboutTabPanel; 17 interactive controls + tab-strip button), Integrations = 2168-2606 (IntegrationsTabPanel; 20 interactive controls + tab-strip button), Statistics = 3113-3345 (StatisticsTabPanel; 1 interactive control + tab-strip button, everything else read-only stat displays). Tab-strip buttons at 757-768 included. Persistence round-trips were verified against the actual code paths: SettingsViewModel.SaveAsync (guarded by _settingsLoaded/_suspendSettingPersistence, :1119-1139), the MergeExternalSettingChangesAsync rebase (:1159-1179) plus SyncToSettings re-apply (:1181-1325) for every field in my tabs (IncludePrereleaseUpdates :1217, DeveloperMode :1218, WebRemoteEnabled :1254, LrcLib/Deezer/MusicBrainz :1276-1278, NetEase :1287, Discord :1295, LastFm trio :1296-1299, ListenBrainz trio :1301-1303, SourceConnections rebuild :1305-1314), and the load path SyncFromSettings inside LoadAsync (:836-1116). PersistenceService.LoadSettingsAsync/SaveSettingsAsync verified for at-rest protection and corrupt-file recovery (:107-183). Runtime consumers confirmed by reading them (LyricsViewModel :973-975, MetadataFinderService :34/58, AutoMatchCoordinator :40, MainWindowViewModel :2475-2565, WebRemoteServer :43-48, ListenBrainzService :29, UpdateService members). Not fully traceable from code alone (would need runtime): actual GitHub API behavior of the update/version-manager buttons, browser-open behavior of the link buttons on each OS, whether Last.fm session keys survive a key rotation, and the millisecond-scale race between a lyrics-provider toggle's fire-and-forget save and an immediately-following lyrics search (noted on the LRCLIB row, not filed as a finding). No control in the three tabs was left untraced; none were found unbound or writing dead settings.

## Cross-platform

All behavior below is from code reading only — macOS/Linux builds were not executed. `Verified: code-read` = behavior is unambiguous from the code; `unverified` = needs the manual checklist.

### Platform-conditional branches

| File | Lines | Windows | macOS | Linux | Verified | Notes |
|---|---|---|---|---|---|---|
| src/Noctis/Noctis.csproj | 8-10 | net8.0-windows TFM; WinRT projections available; defines WINDOWS symbol (enables SmtcService real body); app.manifest + .ico applied | plain net8.0; SmtcService compiles to stub; VideoLAN.LibVLC.Mac 3.0.21 restored (csproj:74, only when building ON macOS) | plain net8.0; SmtcService stub; no libvlc NuGet — system libvlc or AppImage bundle | code-read | CI builds each OS on its own runner so host-OS TFM condition is safe. NoWarn CA1416 hides platform-compat analyzer on the non-Windows TFM (csproj:37 comment admits it has never been evaluated there). |
| src/Noctis/Program.cs | 25-29 | STA apartment for OLE drag-and-drop | no-op | no-op | code-read |  |
| src/Noctis/Program.cs | 133-142 | native user32 MessageBox on fatal startup error | stderr only — a Finder-launched app shows NOTHING visible; user must find crash.log | stderr only — same invisibility when launched from a .desktop entry | code-read | Fail path is silent on non-Windows GUI launches (crash.log in data root is the only trace). |
| src/Noctis/Program.cs | 175-214 | no explicit fallback — system font manager resolves CJK automatically | explicit CJK/emoji fallback chain (stock fonts, always present) | Noto CJK fallback chain — tofu returns if Noto CJK packages are NOT installed; plus opt-in X11 software-render escape hatch | unverified | Font names must match installed family names; needs a run per OS with CJK lyrics to confirm. |
| src/Noctis/Services/SmtcService.cs | 2, 22, 294-297 | media overlay + hardware media keys + flyout scrubber; init failure (Windows N) degrades gracefully | compiles to empty stub — NO media integration | empty stub (MprisService covers Linux instead) | code-read | WINDOWS symbol comes from the net8.0-windows TFM. Constructed unconditionally at Views/MainWindow.axaml.cs:303. |
| src/Noctis/Services/MprisService.cs | 48-60 | returns null (SMTC used) | returns null — nothing replaces it (media keys dead, see finding) | full MPRIS D-Bus player: media keys, playerctl, GNOME/KDE widgets, Raise/Quit, Seek, Volume/Shuffle/LoopStatus set; reconnects after session-bus restart (5 bounded attempts) | unverified | Behavior clear from code but D-Bus interactions (name grab, widget metadata, art URL, reconnect) need a live Linux desktop to confirm. |
| src/Noctis/Views/MainWindow.axaml.cs | 894-904 | taskbar thumbnail buttons (prev/play/next/favorite) via comctl32/user32 P/Invoke | no-op | no-op | code-read | TaskbarIntegrationService (user32/gdi32/comctl32/dwmapi DllImports, registry read at :281) is only ever constructed behind this guard. |
| src/Noctis/Views/MainWindow.axaml.cs | 170-209 | Acrylic/Mica blur-behind with translucent surface overlay | same hint chain (maps to vibrancy where Avalonia supports it) — visual result unconfirmed | forced OFF even if settings.json carries LiquidGlassEnabled=true; toggle also hidden (SettingsViewModel:412) | unverified | Windows path verified in use; macOS rendering result needs a run. |
| src/Noctis/Views/MiniPlayerWindow.axaml.cs | 43-47 | transparent rounded-card mini player | transparent rounded-card mini player | opaque square-corner card (compositor-dependent transparency avoided, issue #26) | code-read |  |
| src/Noctis/Helpers/DialogHelper.cs | 25-33 | overlay extends upward to also dim the native title bar (Position/FrameSize math) | overlay covers client area only — title bar stays undimmed (accepted trade-off per comment) | same as macOS | code-read |  |
| src/Noctis/Helpers/RecycleBin.cs | 53-55, 63-73, 103-115 | shell32 Recycle Bin, silent, no dialogs; false on failure | AppleScript to Finder — subject to TCC automation consent (see finding); false on failure, file untouched | gio trash; if gio missing, hand-rolled ~/.local/share/Trash impl (honors XDG_DATA_HOME); cross-device move deliberately refused | unverified | Never permanently deletes as fallback — good. Windows path is exercised daily; mac/Linux need runs. |
| src/Noctis/Helpers/StartupHelper.cs | 39-41, 63-65 | registry Run value; source of truth is the OS entry; returns false on locked-down HKCU | LaunchAgent plist; genuinely refuses (returns false) when not running from a .app bundle (dotnet run / bare publish) | XDG autostart .desktop; prefers $APPIMAGE path so autostart survives self-update; tar.gz uses ProcessPath | unverified | Any other OS: SetEnabled returns false. --startup/--minimized parsed at Program.cs:99-100; window hides only if tray icon initialized (MainWindow.axaml.cs:313-316). |
| src/Noctis/Helpers/SingleInstanceGuard.cs | 167-191 | named mutex + ACL'd named pipe; second launch surfaces window / forwards files | mutex emulation + Unix domain socket with CurrentUserOnly (0700); same surface-on-relaunch behavior | same as macOS | unverified | Client (line 91) also passes CurrentUserOnly, matching the server on Unix — required for the socket paths to line up. Mutex-held-but-pipe-dead now launches anyway (Program.cs:41-58). |
| src/Noctis/Helpers/PlatformHelper.cs | 19-54, 94-123, 128-159, 165-214 | all four work natively | open/-R and defaults-read paths; all best-effort, swallow failures | file-manager select via D-Bus with xdg-open fallback; dark-mode detection is GNOME-gsettings-only — KDE/others fall through to 'return true' (always dark) | code-read | See low finding: on non-GNOME Linux the 'System' theme永远 resolves dark. |
| src/Noctis/Services/PersistenceService.cs | 88-103, 390-425 | lastFmSessionKey / listenBrainzToken / media-server tokenOrPassword DPAPI-encrypted (CurrentUser) with 'dpapi:' prefix; NTFS ACLs from %APPDATA% | secrets plaintext at rest; dir chmod 0700, files 0600 (silently skipped on exFAT etc.); a settings.json copied FROM Windows loses its credentials (empty string) | same as macOS | code-read |  |
| src/Noctis/Services/MetadataService.cs | 482-488 | no-op (ACL inheritance) | preserves mode bits across the atomic temp-then-move tag save | same | code-read | The atomic-save path itself exists because in-place TagLib Save() corrupted the player's open read on mac/Linux (comment at 490-493). |
| src/Noctis/Services/UpdateService.cs | 97-116, 339-370, 433-436, 516-628 | silent Inno update with elevation; Scoop copies steered to 'scoop update noctis' | downloads per-arch .dmg and opens it — user drags app manually; unknown-arch Macs get x64 asset | AppImage in-place swap via detached shell that waits for process exit; tar.gz/arm64 → external download hint | unverified | Downloads pinned to GitHub HTTPS + SHA256SUMS fail-closed. Registry access only inside IsWindows branch (TryGetInnoInstallLocation called from line 100). |
| src/Noctis/Services/VlcAudioPlayer.cs | 303-331 | NAudio MMDeviceEnumerator coclass pinned first (COM-binding order bug fix); bundled VideoLAN.LibVLC.Windows loaded | prefers installed VLC.app dylibs + sets VLC_PLUGIN_PATH via libc setenv (Environment.SetEnvironmentVariable alone doesn't reach getenv); else falls back to NuGet-bundled payload — arm64 viability unverified (see finding) | Core.Initialize() → system libvlc; missing lib throws with distro-specific install instructions (BuildLibVlcMissingMessage:3814-3832) | code-read |  |
| src/Noctis/Services/VlcAudioPlayer.cs | 420-439 | avformat forced (O(1) VBR-MP3 seek), speex resampler, WASAPI mmdevice aout | avformat forced (VideoLAN payload has the plugin); default aout (CoreAudio); no speex flags (VLC.app builds may lack the module) | avformat only when NOCTIS_BUNDLED_VLC=1 (AppRun exports it in the AppImage — verified in .github/workflows/dotnet.yml:372-377); system-libvlc keeps native demuxers so Arch's split plugin packaging still plays (issue #26); seek quality on VBR MP3 is therefore WORSE on system-libvlc installs | code-read | ShouldForceAvformatDemux (3811-3812) is unit-tested via InternalsVisibleTo. |
| src/Noctis/Services/VlcAudioPlayer.cs | 498-561 | float session-volume ramp (click-free); silent WASAPI keep-alive ON by default (first-play stutter fix); optional per-sample gain sink | session volume null → coarser integer 0-100 LibVLC volume ramp; keep-alive OFF unless NOCTIS_KEEPALIVE=1 (it corrupted CoreAudio output on Apple Silicon — VlcSilenceKeepAlive.cs:43-47) | integer volume ramp; keep-alive OFF by default (poisoned Pulse stream-restore historically; VlcSilenceKeepAlive.cs:48-56) | code-read | Cold-start first-play stutter mitigation is therefore Windows-only by default — deliberate, documented. |
| src/Noctis/Services/VlcAudioPlayer.cs | 960-971, 1286-1291, 2088-2089 | 1ms multimedia timer during volume ramps; WASAPI exclusive mode available | ramp runs at default timer granularity; exclusive silently coerced off | same as macOS; Settings hides the toggle (IsExclusiveAudioSupported, SettingsViewModel:450) | code-read | TryEndHighResTimer has no guard but is only called when TryBegin returned true, and catches DllNotFoundException anyway. |
| src/Noctis/Services/WasapiGainOutput.cs | 99-131 | NAudio WASAPI shared/exclusive sink | null → callers fall back to LibVLC output | null → same fallback | code-read | Same guard pattern in WasapiSilenceKeepAlive.cs:72 and WindowsSessionVolume.cs:47. |
| src/Noctis/Services/AudioConverterService.cs | 145, 154, 396-402 | ffmpeg.exe next to app or on PATH (';' separated); case-insensitive containment check | ffmpeg on PATH (':'); case-insensitive containment (matches default APFS) | ffmpeg on PATH; case-SENSITIVE containment — correct for ext4 | code-read | One of only three call sites that adapt string comparison to the filesystem (with LibraryService:2172 and LoonClient:417); the rest of the codebase does not — see finding. |
| src/Noctis/Services/LibraryService.cs | 2171-2172 | symlink/junction cycle guard, case-insensitive | case-insensitive (APFS default) | case-sensitive — two dirs differing only in case are both scanned | code-read | Contrast: excluded-file/root sets in the same file (lines 124-134, 2278-2282) are unconditionally OrdinalIgnoreCase — see finding. |
| src/Noctis/Services/Loon/LoonClient.cs | 414-417 | case-insensitive artwork-dir containment | Ordinal — technically wrong for default case-insensitive APFS, but both paths are produced internally from the same PersistenceService root, so no practical divergence | Ordinal, correct | code-read |  |
| src/Noctis/ViewModels/SettingsViewModel.cs | 412, 450 | both Settings cards visible | Liquid Glass card visible; Exclusive Mode card hidden | both hidden | code-read |  |
| src/Noctis/ViewModels/MainWindowViewModel.cs | 2253-2255 | Known-folder Music | ~/Music via special folder | XDG_MUSIC_DIR when set; falls back to ~/Music if the special folder resolves empty | code-read |  |
| src/Noctis/ViewModels/DuplicateFinderViewModel.cs | 75 (also Views/RemoveFromLibraryDialog.axaml.cs:55) | label says Recycle Bin | label says Trash | label says Trash | code-read | Cosmetic only. |
| src/Noctis/Views/MainWindow.axaml.cs | 637-719, 1197-1198 | notification-area icon; F11 native fullscreen toggle | menu-bar extra via Avalonia; F11 requires Fn or 'function keys as standard' (OS binds F11 to Show Desktop); WindowState.FullScreen enters native fullscreen Space | tray only where the DE supports StatusNotifier (GNOME needs an extension); on failure _trayIcon stays null and close-to-tray / minimize-to-tray / start-minimized all correctly degrade (guards at 313, 545, 754) | unverified | Tray degradation logic is code-read solid; actual icon rendering per DE needs runs. |

### Manual test checklist

#### macOS

- [ ] Apple Silicon, NO VLC.app installed: fresh install from the .dmg, launch, play any track — **expected:** Track plays. Failure modes to record: startup error dialog 'libvlc is required...' or silent no-audio — would confirm the bundled-libvlc arch/plugins finding. Then install VLC.app and re-test (should always work).
- [ ] Press the play/pause media key (F8/touchbar) while Noctis plays; open Control Center → Now Playing — **expected:** Per current code: nothing happens and no Now Playing entry exists (confirms the macOS media-integration gap finding). If another app grabs the key instead, that's the same gap.
- [ ] Songs → right-click a track → Remove from Library with 'Move file to Trash' checked — **expected:** File appears in Trash. Watch for: an 'Noctis wants to control Finder' consent prompt (allow it, retest), or a silent 'couldn't trash' failure — either confirms the osascript/TCC finding. Also test a second time after denying the prompt.
- [ ] Settings → toggle 'Open Noctis when computer starts' (running from /Applications/Noctis.app), then log out/in — **expected:** ~/Library/LaunchAgents/com.heartached.noctis.plist exists; app launches at login (via open), honoring 'start minimized' if set. Toggling from a non-bundled run (dotnet run) must visibly revert the toggle to off.
- [ ] Open a confirm dialog (e.g. delete playlist) windowed and maximized — **expected:** Dim overlay exactly covers the window content (title bar stays undimmed — accepted); no offset/misalignment on Retina scaling.
- [ ] Enter fullscreen with Fn+F11 (or F11 with function-keys-standard), play lyrics view, press Esc — **expected:** Native fullscreen Space; Esc leaves fullscreen first (not the lyrics view); window returns to prior size.
- [ ] Play a track with Korean/Japanese/Chinese title + lyrics — **expected:** No tofu boxes — PingFang/Hiragino/Apple SD Gothic fallback engages in list views, playbar, and lyrics page.
- [ ] Settings → About → check for updates on an older build — **expected:** Detects release, downloads the -osx-arm64.dmg on Apple Silicon (x64 on Intel), verifies SHA256, opens the mounted dmg for drag-install.
- [ ] Launch Noctis a second time from Finder while it runs; also double-click an .mp3 associated with Noctis — **expected:** No second instance: the running window comes to front; the file starts playing in the existing instance.
- [ ] Drag the volume slider slowly from 100 to 0 during playback (wired + Bluetooth) — **expected:** Smooth, click-free fade — this path is the integer LibVLC ramp (no WASAPI session volume); report any zipper noise.

#### Linux

- [ ] System-libvlc install on Arch WITHOUT vlc-plugin-ffmpeg: play mp3/flac/m4a; then same tracks in the AppImage — **expected:** System install: everything plays (avformat not forced); VBR MP3 seeking may be sluggish — expected. AppImage: plays AND seeks instantly (NOCTIS_BUNDLED_VLC=1 forces avformat).
- [ ] playerctl metadata + hardware media keys on GNOME and KDE; then `systemctl --user restart dbus` (or kill the session bus) and press a media key ~30s later — **expected:** playerctl shows title/artist/album/art/position; keys control playback; after bus restart MPRIS reconnects within ~2-30s (bounded backoff) and keys work again.
- [ ] Case-sensitivity probe: inside a watched folder create 'Test/a.flac' and 'test/b.flac' (two dirs differing only in case), let both import, then delete ONLY 'test/' — **expected:** Only b.flac leaves the library. If a.flac disappears too, the OrdinalIgnoreCase watcher-removal finding is confirmed live.
- [ ] Remove a track with 'Move file to Trash' on a machine WITHOUT gio (minimal install), and once for a file on a different mount than $HOME — **expected:** No-gio: file lands in ~/.local/share/Trash/files with a matching .trashinfo. Different mount: operation reports failure and the file is left in place (never silently destroyed).
- [ ] Autostart: toggle on from the AppImage AND from a tar.gz extract; inspect ~/.config/autostart/noctis.desktop; relogin (with and without 'start minimized') — **expected:** AppImage: Exec points at the .AppImage path with --startup [--minimized]; app autostarts, minimized variant starts hidden in tray (or visible if the DE has no tray). tar.gz: Exec points at the extracted binary.
- [ ] Tray behavior on stock GNOME (no AppIndicator extension) with 'Close to tray' enabled — **expected:** Tray init fails silently; clicking window close actually QUITS the app (close-to-tray correctly disabled when no tray icon exists) — the app must never vanish with no window and no tray.
- [ ] AppImage self-update: run an older AppImage, accept the in-app update; test once with /tmp on tmpfs — **expected:** App exits, the AppImage file is replaced in place (survives cross-filesystem mv), is executable, and relaunches automatically.
- [ ] Open mini player on X11 without a compositor and on Wayland — **expected:** Opaque dark card with square corners — no garbage/see-through surface; Liquid Glass card absent in Settings.
- [ ] Sign in to Last.fm, then `ls -l` the data dir and open settings.json — **expected:** Session key visible in PLAINTEXT (known limitation) but settings.json is mode 0600 and the Noctis dir 0700. On a FAT/exFAT-mounted NOCTIS_DATA_DIR the chmod silently does nothing — verify you accept that.
- [ ] KDE with a light theme: set Noctis theme to 'System' — **expected:** Per current code it resolves DARK (gsettings-only detection). Confirms the low-severity dark-mode finding.
- [ ] Second-launch activation + 'Open with Noctis': launch the AppImage twice; open an .mp3 from the file manager while running — **expected:** Window surfaces, no duplicate player; the file plays in the running instance. Check $TMPDIR CoreFxPipe socket is not world-accessible.
- [ ] CJK lyrics without Noto CJK installed, then after `pacman -S noto-fonts-cjk` — **expected:** Before: tofu boxes are EXPECTED (fallback names miss); after: correct glyphs. Documents the font dependency.

#### Windows

- [ ] SMTC: play, press keyboard media keys, open the volume/media flyout, scrub from the flyout, check album art — **expected:** Overlay shows title/artist/album + art, keys work, flyout scrub seeks; still works while minimized to tray.
- [ ] settings.json after Last.fm/ListenBrainz sign-in — **expected:** lastFmSessionKey / listenBrainzToken values start with the 'dpapi:' prefix (encrypted), not plaintext.
- [ ] Exclusive Mode toggle during playback (shared → exclusive → shared) on a device already held exclusively by another app — **expected:** Output rebuilds, resumes at position; in-use device degrades to 'WASAPI Shared (exclusive unavailable)' status, no crash.
- [ ] Autostart: toggle on with 'start minimized', check HKCU\Software\Microsoft\Windows\CurrentVersion\Run, relogin — **expected:** Value '"<path>\Noctis.exe" --startup --minimized'; app starts hidden in tray at login.

## Dependencies

Report-only; no csproj/lockfile was touched. `Target` = recommended version (latest patch within the current major unless noted).

| Package | Current | Target | Type | CVE | Risk | Notes |
|---|---|---|---|---|---|---|
| Avalonia | 11.3.18 | 11.3.18 (already latest 11.3.x patch) | major | none found (GitHub Advisory Database query 'avalonia' returns 0 advisories across the NuGet ecosystem) | low | Pinned at src/Noctis/Noctis.csproj:47. NuGet flatcontainer confirms 11.3.18 is the final 11.3.x release; latest stable major is 12.1.1 (2026-07-29). No patch/minor gap within v11; moving to 12.x is a deliberate migration (see breaking_notes), not a routine bump. **Sources:** https://api.nuget.org/v3-flatcontainer/avalonia/index.json ; https://www.nuget.org/packages/Avalonia ; https://github.com/advisories?query=avalonia |
| Avalonia.Desktop | 11.3.18 | 11.3.18 (already latest 11.3.x patch) | major | none found (same GitHub Advisory Database query, 0 results) | low | src/Noctis/Noctis.csproj:48. Ships in lockstep with the core Avalonia package; flatcontainer confirms 11.3.18 exists and highest stable is 12.1.1. Must move together with Avalonia on any upgrade. **Sources:** https://api.nuget.org/v3-flatcontainer/avalonia.desktop/index.json ; https://github.com/advisories?query=avalonia |
| Avalonia.Themes.Fluent | 11.3.18 | 11.3.18 (already latest 11.3.x patch) | major | none found (same GitHub Advisory Database query, 0 results) | low | src/Noctis/Noctis.csproj:50. Flatcontainer confirms 11.3.18 is the highest 11.3.x; highest stable is 12.1.1. Elevated migration sensitivity for this app: it overrides Fluent SystemControl* accent keys at runtime (src/Noctis/App.axaml.cs:406-407 sets SystemControlHighlightAccentBrush/2) and in Assets/Styles.axaml:24-25,128-129 plus Assets/Themes/Dark.axaml:17-18 and Midnight.axaml:17-18 — key stability in the v12 Fluent theme was NOT explicitly confirmed in the changelog material read and must be smoke-tested during any v12 move. **Sources:** https://api.nuget.org/v3-flatcontainer/avalonia.themes.fluent/index.json ; https://github.com/advisories?query=avalonia |
| Avalonia.Fonts.Inter | 11.3.18 | 11.3.18 (already latest 11.3.x patch) | major | none found (same GitHub Advisory Database query, 0 results) | low | src/Noctis/Noctis.csproj:51. Lockstep package; flatcontainer confirms 11.3.18 exists and 12.x up to 12.1.1 is available. v12 text stack changes (SkiaSharp 3, HarfBuzz shaper default/config, font weight/stretch matching fixes) mean font rendering should be visually re-verified on any major upgrade. **Sources:** https://api.nuget.org/v3-flatcontainer/avalonia.fonts.inter/index.json ; https://github.com/advisories?query=avalonia |
| Avalonia.Diagnostics | 11.3.18 (Debug-only, condition at src/Noctis/Noctis.csproj:52) | 11.3.18 | none | none found (same GitHub Advisory Database query, 0 results) | low | 11.3.18 is the highest version that exists — NuGet flatcontainer shows NO 12.x of this package. In Avalonia 12 it is replaced by AvaloniaUI.DiagnosticsSupport (per the official v12 breaking-changes doc), so a v12 migration requires swapping this PackageReference, not bumping it. Debug-only condition already limits release exposure. **Sources:** https://api.nuget.org/v3-flatcontainer/avalonia.diagnostics/index.json ; https://docs.avaloniaui.net/docs/avalonia12-breaking-changes ; https://github.com/advisories?query=avalonia |
| Avalonia.Labs.Lottie | 11.3.1 | 11.3.1 (already latest 11.x release) | major | none found (GitHub Advisory Database query 'avalonia' returns 0 advisories; no Labs-specific advisories either) | low | src/Noctis/Noctis.csproj:49. Flatcontainer shows 11.3.1 is the last 11.x; newer releases (12.0.0, 12.0.2) target Avalonia 12 only, so it can only move together with the core Avalonia upgrade. Avalonia.Labs release notes describe the 12.0.0 bump as 'Update to v12' compatibility with no Lottie-specific breaking changes. Used by Controls/LottieToggle.axaml, Views/SettingsView.axaml, Views/MetadataWindow.axaml; App.axaml.cs:339 notes LottieToggle re-resolves accent brushes — retest after any theme/Avalonia change. **Sources:** https://api.nuget.org/v3-flatcontainer/avalonia.labs.lottie/index.json ; https://github.com/AvaloniaUI/Avalonia.Labs/releases ; https://github.com/advisories?query=avalonia |
| LibVLCSharp | 3.10.0 | 3.10.0 (already latest stable, published 2026-06-17) | none | none found | low | Latest stable on the 3.x line. The 3.10.0 [BREAKING CHANGE] only affects UWP/WinUI VideoView packaging, not Avalonia. 4.x exists only as previews distributed via feedz.io (not nuget.org) — no stable 4.x to move to. **Sources:** https://www.nuget.org/packages/LibVLCSharp ; https://raw.githubusercontent.com/videolan/libvlcsharp/3.x/NEWS |
| LibVLCSharp.Avalonia | 3.10.0 | 3.10.0 (already latest stable, published 2026-06-17) | none | none found | low | Current. 3.10.0 fixed Avalonia VideoView updates when detached from the visual tree; 3.9.7 added Avalonia 12 compatibility (app is on Avalonia 11.3.18, unaffected). **Sources:** https://www.nuget.org/packages/LibVLCSharp.Avalonia ; https://raw.githubusercontent.com/videolan/libvlcsharp/3.x/NEWS |
| VideoLAN.LibVLC.Windows | 3.0.23.1 | 3.0.23.1 (already latest stable, published 2026-04-16); watch for 3.0.24 (3.0.24-beta1 in progress upstream) | none | none found affecting 3.0.23/3.0.23.1. Historical, already fixed in this version: CVE-2024-46461 (mms integer overflow, affects <=3.0.20, fixed 3.0.21); the large 3.0.22 security batch (VideoLAN-SB-VLC-322) shipped without CVE IDs assigned | low | Current. Upstream 3.0.24-beta1 contains 'Fix mmdevice crashes, leaks and deadlocks' (the exact aout this app forces), an Audio EQ high-frequency fix, and an FFmpeg 4.4->8.1 jump — bump promptly when a 3.0.24.x NuGet lands and re-verify --demux=avformat seek behavior (see breaking_notes). **Sources:** https://www.nuget.org/packages/VideoLAN.LibVLC.Windows ; https://raw.githubusercontent.com/videolan/vlc/3.0.x/NEWS ; https://www.videolan.org/security/ ; https://github.com/advisories/GHSA-3hwv-fr9j-3wjq |
| VideoLAN.LibVLC.Mac | 3.0.21 pinned in src/Noctis/Noctis.csproj:74 — this version DOES NOT EXIST on nuget.org; PackageReference min-version semantics make restore float up to the abandoned 2019-era 3.1.x package (lowest match: 3.1.2, published 2018-11-14; newest available: 3.1.3.1, 2019-09-30) | No safe nuget.org target exists (package abandoned at 3.1.3.1/2019). Recommended: stop using this package; bundle official libvlc 3.0.23 macOS dylibs+plugins in the CI .app packaging step, or hard-require VLC.app (the code already prefers it when installed) | unknown | Effective 2019-era payload predates every VLC security fix from ~3.0.8 onward, including CVE-2024-46461 (fixed in 3.0.21) and the 3.0.22 batch VideoLAN calls 'the release with the most security fixes ever' (VideoLAN-SB-VLC-321/322 — no CVE IDs assigned for most items) | high | The pin's intent (VLC 3.0.21) is unfulfillable from nuget.org (no nuget.config/custom feed exists in the repo, so nuget.org is the only source). Runtime mitigation: VlcAudioPlayer prefers /Applications/VLC.app dylibs when present (VlcAudioPlayer.cs:308-327), so users with VLC installed get a current libvlc; users relying on the bundled payload get a 7-year-old x86_64-only build. See findings. **Sources:** https://api.nuget.org/v3-flatcontainer/videolan.libvlc.mac/index.json ; https://www.nuget.org/packages/VideoLAN.LibVLC.Mac ; https://raw.githubusercontent.com/videolan/vlc/3.0.x/NEWS ; https://www.videolan.org/security/ |
| NAudio.Core | 2.3.0 | 2.3.0 (already latest stable) | none | none found | low | Latest stable on nuget.org (version index ends at 2.3.0 stable; a 3.0.0-preview line exists, latest 3.0.0-preview.19 — no stable 3.x). No advisories located for NAudio. **Sources:** https://api.nuget.org/v3-flatcontainer/naudio.core/index.json ; https://github.com/naudio/NAudio/releases |
| NAudio.Wasapi | 2.3.0 | 2.3.0 (already latest stable) | none | none found | low | Latest stable. Hygiene FYI: upstream once mistakenly published NAudio.Wasapi 22.0.0 (2023-08-22, since unlisted by the owner). Harmless here — Version="2.3.0" resolves to exactly 2.3.0 under NuGet lowest-applicable rules and unlisted versions are never auto-selected. **Sources:** https://api.nuget.org/v3-flatcontainer/naudio.wasapi/index.json ; https://www.nuget.org/packages/NAudio.Wasapi/22.0.0 |
| SkiaSharp | 2.88.9 | 2.88.9 (hold — latest patch of the 2.88.x line; do NOT roll to 3.x/4.x while on Avalonia 11.3.x) | none | CVE-2023-4863 (libwebp heap overflow) affects SkiaSharp < 2.88.6, fixed 2.88.6 — 2.88.9 NOT affected. No other advisories listed for the SkiaSharp NuGet package. | low | 2.88.9 is the final 2.88.x release (NuGet flat-container index). Latest 3.x stable is 3.119.4 (2026-05-25); latest overall is 4.151.0 (2026-07-31). Avalonia.Skia 11.3.18 declares floors SkiaSharp >= 2.88.9, SkiaSharp.NativeAssets.Linux >= 2.88.9, HarfBuzzSharp >= 8.3.1.1, so the app's explicit 2.88.9 pin exactly matches Avalonia's resolved version — correct place to be. See breaking_notes for the 3.x roll-forward constraints. csproj: src/Noctis/Noctis.csproj:91. **Sources:** https://www.nuget.org/packages/Avalonia.Skia/11.3.18 ; https://api.nuget.org/v3-flatcontainer/skiasharp/index.json ; https://security.snyk.io/vuln/SNYK-DOTNET-SKIASHARP-5922114 ; https://github.com/mono/SkiaSharp/issues/2608 ; https://advisories.gitlab.com/pkg/nuget/skiasharp/ |
| TagLibSharp | 2.3.0 | 2.3.0 (already latest — no newer release exists) | none | none found — GitHub Advisory Database returns 0 advisories for TagLibSharp. Note: taglib C++ CVEs (e.g. CVE-2023-47466, WAV crash in taglib < 2.0) are a DIFFERENT codebase and do not apply to the .NET port. | med | Upstream is dormant: 2.3.0 released 2022-07-30 and nothing since — malformed-file parser fixes are not coming. The app runs it over every user-imported file (src/Noctis/Services/MetadataService.cs, ExtendedTagIO.cs, AdvancedTagIO.cs, ReplayGainScannerService.cs), so parser robustness rests entirely on the app's own try/catch. Known non-CVE upstream defect in exactly this version: mono/taglib-sharp#340 — v2.3.0 reported to corrupt MP4/M4V files (garbage bytes into atoms) when updating tags; see findings. **Sources:** https://www.nuget.org/packages/TagLibSharp ; https://github.com/advisories?query=taglibsharp ; https://github.com/mono/taglib-sharp/issues/340 ; https://www.cvedetails.com/vendor/16817/Taglib.html |
| YamlDotNet | 15.1.6 | 15.3.0 (latest of the 15.x line, 2024-06-16) | minor | CVE-2018-1000210 (unsafe type instantiation during Deserialize → code execution) affects YamlDotNet <= 4.3.2, fixed 5.0.0 — 15.1.6 NOT affected. No other advisories listed. | low | Latest stable overall is 18.1.0 (2026-06-26) — three majors ahead (16/17/18 each carried breaking API changes), so within-major 15.3.0 is the low-risk move. App usage is narrow and safe: src/Noctis/Services/LyricsfileParser.cs:16-19 builds a plain DeserializerBuilder with IgnoreUnmatchedProperties into a fixed DTO — no tag mappings, no arbitrary-type resolution, so even hostile lyric files can't trigger type-instantiation tricks. **Sources:** https://www.nuget.org/packages/YamlDotNet ; https://github.com/advisories/GHSA-rpch-cqj9-h65r ; https://advisories.gitlab.com/pkg/nuget/yamldotnet/ |
| System.Text.Json | 8.0.5 | 8.0.6 (latest 8.0.x, released 2025-07-08) | patch | CVE-2024-30105 (DeserializeAsyncEnumerable DoS) affects 7.0.0 through 8.0.3, fixed 8.0.4. CVE-2024-43485 ([JsonExtensionData] algorithmic-complexity DoS) affects 8.0.0 through 8.0.4, fixed 8.0.5. Current 8.0.5 ALREADY CONTAINS BOTH FIXES — no known vulnerability outstanding in this pin. (Recent Sonatype flagging of CVE-2024-43485 against 9.x/10.x is a scanner false positive per dotnet/runtime#119773.) | low | 8.0.6 is a non-security servicing update (no vulnerability disclosed for 8.0.5→8.0.6); latest overall is 10.0.10, but 9.x/10.x is a major bump unnecessary for a net8.0 app. Risk matters here because the app deserializes REMOTE JSON (UpdateService, LrcLibService, NetEaseService, LastFmService), not just local settings — the two DoS CVEs were exactly this attack shape, and both are patched at the current pin. csproj: src/Noctis/Noctis.csproj:96. **Sources:** https://advisories.gitlab.com/pkg/nuget/system.text.json/ ; https://www.nuget.org/packages/System.Text.Json/8.0.6 ; https://api.nuget.org/v3-flatcontainer/system.text.json/index.json ; https://github.com/dotnet/runtime/issues/119773 |
| DiscordRichPresence | 1.6.1.70 | 1.6.1.70 (already latest, released 2025-08-04) | none | none found for DiscordRichPresence (Lachee/discord-rpc-csharp). Do not confuse with the MALICIOUS lookalike package Discord_Rpc.Net (MAL-2024-11506, versions 1.0.0-1.2.1.25) — a different NuGet ID that this app does not reference. | low | Current pin is the newest release on NuGet; nothing to do. Library talks only to the local Discord client over a named pipe — small attack surface. csproj: src/Noctis/Noctis.csproj:56. **Sources:** https://www.nuget.org/packages/DiscordRichPresence ; https://github.com/Lachee/discord-rpc-csharp ; https://vulert.com/vuln-db/nuget-discord-rpc-net-177793 |
| CommunityToolkit.Mvvm (app) | 8.4.2 | 8.4.2 (already latest stable) | none | none found | low | Latest stable on NuGet is 8.4.2 (2026-03-25); no newer major exists. Nothing to do. **Sources:** https://www.nuget.org/packages/CommunityToolkit.Mvvm ; https://api.nuget.org/v3-flatcontainer/communitytoolkit.mvvm/index.json |
| Microsoft.Extensions.DependencyInjection (app) | 8.0.1 | 8.0.1 (latest 8.0.x — no change now) | major | none found | low | 8.0.1 is the final 8.0.x. Latest stable majors: 9.0.18 / 10.0.10. The 8.x Microsoft.Extensions line stops receiving fixes when .NET 8 leaves support 2026-11-10; bump to 10.0.x as part of the .NET 10 retarget, not standalone. **Sources:** https://api.nuget.org/v3-flatcontainer/microsoft.extensions.dependencyinjection/index.json ; https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection |
| Microsoft.Data.Sqlite (app) | 8.0.11 | 8.0.29 | patch | none found against the managed package itself; native-engine CVEs (CVE-2025-6965, CVE-2025-70873) are governed by the SQLitePCLRaw bundle row below | low | 18 patch releases behind on the still-supported 8.0.x line (8.0.29 shipped 2026-07-14). VERIFIED: 8.0.29 still declares SQLitePCLRaw.bundle_e_sqlite3 >= 2.1.6, so the repo's explicit 2.1.12 float must be kept after the bump. Latest majors: 9.0.18 / 10.0.10 (10.x rides EF Core 10 / .NET 10). dotnet/efcore states Microsoft.Data.Sqlite 11.0 will switch the native bundle to SQLite3MC.PCLRaw because e_sqlite3 native builds lag on security fixes — worth tracking. SQL surface in this app is app-authored (Services/SqliteLibraryIndexService.cs, Services/AudioAnalysis/AudioAnalysisStore.cs), not attacker-injected, which keeps engine-CVE exposure low. **Sources:** https://www.nuget.org/packages/Microsoft.Data.Sqlite/8.0.29 ; https://api.nuget.org/v3-flatcontainer/microsoft.data.sqlite/index.json ; https://github.com/dotnet/efcore/issues/38257 |
| SQLitePCLRaw.bundle_e_sqlite3 (app + tests, same pin) | 2.1.12 | 2.1.12 (latest 2.1.x — keep) | major | CVE-2025-6965 / GHSA-2m69-gcr7-jv3q (SQLite < 3.50.2, integer truncation in findOrCreateAggInfoColumn, CVSS 7.2 High) — NOT affected at this pin; CVE-2025-70873 / GHSA-p36r-6g67-869c (SQLite <= 3.51.1, zipfile-extension heap info disclosure, CVSS 7.5) — also not affected | low | FLOAT VERIFIED: per the SQLitePCL.raw GitHub release notes, 2.1.12 references SQLitePCLRaw.lib.e_sqlite3 3.53.3, i.e. bundles SQLite 3.53.3 >= 3.50.2, so the csproj's stated purpose (escaping the SQLite < 3.50.2 pinned by Microsoft.Data.Sqlite's transitive 2.1.6) is achieved; 3.53.3 also clears CVE-2025-70873 (<= 3.51.1). 2.1.12 is the last 2.1.x; the new 3.x major (3.0.5, SQLite 3.53.4) re-versions the lib packages around the SQLite version — do NOT jump the bundle to 3.x while Microsoft.Data.Sqlite 8.x/9.x is built against the 2.x API. Minor date discrepancy between the NuGet listing (2026-07-14) and the GitHub tag (Jul 19) — both mid-July 2026. **Sources:** https://github.com/ericsink/SQLitePCL.raw/releases ; https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3 ; https://seclists.org/oss-sec/2025/q3/149 ; https://github.com/advisories/GHSA-p36r-6g67-869c |
| System.Security.Cryptography.ProtectedData (app) | 8.0.0 | 8.0.0 (only 8.0.x release — no change now) | major | none found | low | 8.0.0 is the sole stable 8.0.x (confirmed via flat-container version list). Latest majors: 9.0.18 / 10.0.10. Thin, stable DPAPI wrapper (used only in Services/PersistenceService.cs for scrobbler-token encryption, runtime-guarded to Windows); bump alongside the .NET 10 retarget. 8.0.x fix support ends with .NET 8 on 2026-11-10. **Sources:** https://api.nuget.org/v3-flatcontainer/system.security.cryptography.protecteddata/index.json ; https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData |
| Tmds.DBus.Protocol (app) | 0.21.3 | 0.21.3 (patched backport branch — keep) | minor | CVE-2026-39959 / GHSA-xrw6-gwf8-vvr9 (CVSS 7.1 High: D-Bus signal spoofing, unix-fd resource exhaustion, DoS via malformed message bodies) — affects < 0.21.3 and 0.22.0 through < 0.92.0; 0.21.3 IS the backported fix release, so the current pin is PATCHED | low | The pin is exactly right: 0.21.3 (2026-04-08) backports the 0.92.0 security fixes onto the 0.21 branch Avalonia uses, and Avalonia.FreeDesktop 11.3.18 itself now requires >= 0.21.3, so resolution cannot regress below the fix. Latest stable is 0.94.2 (2026-06-17) — a version-scheme jump with API changes; no reason to move while Avalonia pins the 0.21 branch. Runtime exposure is Linux-only (Services/MprisService.cs, session bus). **Sources:** https://github.com/tmds/Tmds.DBus/security/advisories/GHSA-xrw6-gwf8-vvr9 ; https://github.com/advisories/GHSA-xrw6-gwf8-vvr9 ; https://github.com/tmds/Tmds.DBus/releases ; https://api.nuget.org/v3-flatcontainer/tmds.dbus.protocol/index.json ; https://www.nuget.org/packages/Avalonia.FreeDesktop/11.3.18 |
| Microsoft.NET.Test.Sdk (tests) | 18.7.0 | 18.8.1 | minor | none found | low | Dev/test-time only, never ships. 18.8.1 released 2026-07-14; 18.7.0 is one minor behind. Low-value, low-risk bump. **Sources:** https://www.nuget.org/packages/Microsoft.NET.Test.Sdk |
| xunit (tests) | 2.9.3 | 2.9.3 (final v2 release) | major | none found | low | 2.9.3 (2025-01-08) is the last v2 release; NuGet marks the v2 line deprecated / 'no longer maintained' and states it 'will only be updated for security issues — all future feature work has moved onto v3'. Dev-time only, so no shipping risk, but plan an xunit.v3 migration eventually (package renames, runner changes; xunit.runner.visualstudio 3.x already supports v3). **Sources:** https://www.nuget.org/packages/xunit |
| xunit.runner.visualstudio (tests) | 3.1.5 | 3.1.5 (already latest stable) | none | none found | low | Latest stable (2025-09-27). Nothing to do. **Sources:** https://www.nuget.org/packages/xunit.runner.visualstudio |
| Avalonia.Headless.XUnit (tests) | 11.3.18 | 11.3.18 (latest 11.3.x; must stay locked to the app's Avalonia pin) | major | none found | low | Current pin equals the latest 11.3.x (2026-06-23) and matches the app's Avalonia 11.3.18 by design (csproj comment). Avalonia 12.1.1 exists (2026-07-29) — that decision belongs to the app-side Avalonia audit (another agent); this package must only ever move in lockstep with it. **Sources:** https://www.nuget.org/packages/Avalonia.Headless.XUnit |
| NoctisCoverProxy — ASP.NET Core shared framework (no NuGet PackageReferences in csproj) | net8.0 via Microsoft.NET.Sdk.Web (patch level = installed .NET 8 runtime/SDK) | n/a for packages; retarget to net10.0 before .NET 8 EOL 2026-11-10 | none | none applicable at package level — framework-provided ASP.NET Core 8.0.x is patched via runtime servicing, which ends 2026-11-10 | low | tools/NoctisCoverProxy/NoctisCoverProxy.csproj contains zero PackageReference items; its entire ASP.NET surface comes from the Microsoft.AspNetCore.App shared framework, so its security posture is exactly the deployed .NET 8 runtime's patch level and it dies with .NET 8 support in Nov 2026. **Sources:** https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core ; tools/NoctisCoverProxy/NoctisCoverProxy.csproj (read locally) |

### Breaking / behavior changes relevant to this app

- **Avalonia (11.3.18 -> recommended target 11.3.18)** — No delta: 11.3.18 is the newest 11.3.x patch on NuGet, so there are no release notes between current and the recommended within-major target. Everything below documents the OPTIONAL 11.3.18 -> 12.1.1 major path for future planning. _(source: https://api.nuget.org/v3-flatcontainer/avalonia/index.json)_
- **Avalonia 12.0.0 — bindings** — BREAKING: compiled bindings are ON by default in v12 (this app already sets AvaloniaUseCompiledBindingsByDefault=true at src/Noctis/Noctis.csproj:28, so low impact); the binding plugin system is REMOVED and DataAnnotations validation is disabled by default; IBinding removed in favor of BindingBase; InstancedBinding replaced by BindingExpressionBase; C# code should use CompiledBinding/ReflectionBinding instead of Binding. Any reflection-based bindings in code-behind need review. _(source: https://docs.avaloniaui.net/docs/avalonia12-breaking-changes)_
- **Avalonia 12.0.0 — headless testing** — BREAKING for tests/Noctis.Tests: Avalonia.Headless.XUnit in v12 requires xUnit v3 (project currently uses xunit 2.9.3 + Avalonia.Headless.XUnit 11.3.18, tests/Noctis.Tests/Noctis.Tests.csproj:14,19), forcing an xunit v2->v3 migration. Headless platform now defaults to the HarfBuzz text shaper (#20561) — glyph/measure results in headless pixel-probe tests may shift. New HeadlessWindow.SetRenderScaling API (#20888). The known order-dependent headless suite should be re-baselined. _(source: https://github.com/AvaloniaUI/Avalonia/releases/tag/12.0.0 ; https://docs.avaloniaui.net/docs/avalonia12-breaking-changes)_
- **Avalonia 12.0.0 — rendering & text/glyph layout** — SkiaSharp upgraded to 3.0, 2.88 support dropped — this app references SkiaSharp explicitly for color extraction (csproj comment 'transitive dep of Avalonia.Skia, explicit for compile-time access') and must bump it in lockstep. Direct2D1 backend removed (Skia only). Configurable text shaper: .UseHarfBuzz() required alongside .UseSkia(). Universal GlyphTypeface implementation, corrected font Weight/Stretch matching (#20773), TextOptions introduced (#20107), Border clip regression fix (#20648), BitmapInterpolationMode.HighQuality downscaling fix (#19513), Type 1 fonts no longer supported. Expect small text-metric and rendering diffs; app has glyph-sensitive lyric layout (TextBlock stretch/glyph-top workarounds). _(source: https://github.com/AvaloniaUI/Avalonia/releases/tag/12.0.0 ; https://docs.avaloniaui.net/docs/avalonia12-breaking-changes)_
- **Avalonia 12.0.0 — composition/animation & transforms** — Behavior changes relevant to the app's TransformOperationsTransition-based dialog/panel animations (used across Views/*.axaml, e.g. ConfirmationDialog.axaml:42): animations are now stopped when the visual detaches from the tree (#20995), animation processing is disabled when the visual is not visible (#20820), new Animation PlaybackBehavior (#20966), CompositionVisual gains a Translation property (#20836), TransformToVisual/adorner positioning fixes (#20691). Re-verify dialog open/close morphs and the queue-popup/lyrics-panel animations after migrating. _(source: https://github.com/AvaloniaUI/Avalonia/releases/tag/12.0.0)_
- **Avalonia 12.0.0 — virtualization & ScrollViewer & input** — VirtualizingStackPanel viewport shrink/grow regression fixed (#20870) and ghost items in virtualized ItemsControls eliminated (#20700/#20784) — directly relevant to this app's 100k-track virtualized lists and SmoothScrollBehavior. Scroll inertia now dispatched via animation requests (#18997). Input: focus change now happens on pointer RELEASE (#21009), touch/pen selection triggers on release not press, focus-change cancellation and new focus traversal APIs added, GotFocusEventArgs replaced by FocusChangedEventArgs, gesture events moved from Gestures to InputElement. The app's tunneled Space-key/Button workaround and click-vs-drag row logic should be retested. _(source: https://github.com/AvaloniaUI/Avalonia/releases/tag/12.0.0 ; https://docs.avaloniaui.net/docs/avalonia12-breaking-changes)_
- **Avalonia 12.0.0 — platform/API removals** — Requires .NET 8+ (app is net8.0 — fine); netstandard2.0 targets removed; BinaryFormatter removed; PropertyPath removed; obsolete API members removed across assemblies; clipboard API redesigned around IAsyncDataTransfer; TopLevel no longer guaranteed at the visual-tree root (use TopLevel.GetTopLevel(Visual)); Avalonia.Diagnostics replaced by AvaloniaUI.DiagnosticsSupport (see rows). Fluent theme: the material read documents NO explicit rename of SystemControl* accent resource keys, but it also does not guarantee stability — the app's accent-key overrides (App.axaml.cs:406-407 and theme axaml files) and its on-accent checked-foreground overrides are UNVERIFIED against v12 and need a by-eye pass. _(source: https://docs.avaloniaui.net/docs/avalonia12-breaking-changes ; https://github.com/AvaloniaUI/Avalonia/releases/tag/12.0.0)_
- **Avalonia 12.1.0/12.1.1** — No documented breaking changes, but default rendering behavior changes: region dirty-rect clipping DISABLED by default and stencil buffers ENABLED by default; text fallback itemization reworked plus InterWordJustification fixes (glyph layout may shift); composition-aware geometry/drawing change detection; per-visual AABB hit-testing performance work; touch/pen capture semantics aligned with mouse (input behavior change). New TableView control. 12.1.1 (2026-07-29) is the current stable head. _(source: https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.0 ; https://www.nuget.org/packages/Avalonia)_
- **Avalonia.Labs.Lottie 11.3.1 -> 12.0.2** — Only relevant if/when the app moves to Avalonia 12: the Labs 12.0.0 release is described as 'Update to v12' compatibility with no Lottie-specific breaking changes documented; 12.0.2 is the current head. No 11.x releases newer than 11.3.1 exist. _(source: https://github.com/AvaloniaUI/Avalonia.Labs/releases ; https://api.nuget.org/v3-flatcontainer/avalonia.labs.lottie/index.json)_
- **VideoLAN.LibVLC.Windows (upcoming 3.0.24)** — VLC 3.0.24-beta1 changelog: 'Fix mmdevice crashes, leaks and deadlocks' — this is the exact output module the app forces via "--aout=mmdevice" (VlcAudioPlayer.cs:436-438, default when NOCTIS_AOUT unset) and the module implicated in the app's documented Bluetooth 'playback too late -> flushing buffers' spiral (comments at VlcAudioPlayer.cs:371-376, 431-435). Also in 3.0.24-beta1: FFmpeg upgraded 4.4 -> 8.1, which replaces the demuxer the app hard-forces with "--demux=avformat" (VlcAudioPlayer.cs:420-423) for its VBR-MP3/M4A O(1)-seek fix — re-verify seek behavior on bump; plus 'Fix Audio EQ filter High Frequency parameter' (app drives the libvlc equalizer, _equalizerLock paths) and 'Fix LibVLC media list player race'. Bump when 3.0.24.x reaches nuget.org. _(source: https://raw.githubusercontent.com/videolan/vlc/3.0.x/NEWS)_
- **LibVLCSharp / libVLC 4.x line (preview only)** — VLC 4.0 removes the DirectSound plugin ('Remove the DirectSound plugin (API obsolete after Windows 7)'). The app's written fallback plan for mmdevice regressions — 'If that artifact regresses, switch this back to --aout=directsound' (VlcAudioPlayer.cs:375-376) and the NOCTIS_AOUT A/B override that suggests directsound/waveout (VlcAudioPlayer.cs:431-437) — ceases to exist on 4.x. Any future 4.x migration loses that escape hatch. _(source: https://raw.githubusercontent.com/videolan/vlc/master/NEWS)_
- **LibVLCSharp / libVLC 4.x line (preview only)** — VLC 4.0's new output clock ('By default, the audio output will drive the output clock: no more audio resampling or flush when the audio is late or early') eliminates the exact 3.x failure mode ('playback too late -> flushing buffers', VlcAudioPlayer.cs:382-384) that the app's deepened 1000ms "--file/disc/live/network-caching" flags (VlcAudioPlayer.cs:396-414, NOCTIS_CACHING override) exist to mitigate. On any 4.x migration the caching depth and the whole Bluetooth-starvation workaround should be re-tuned from scratch. 4.0 also 'Improve[s] ... start-paused ... handling', which touches the ":start-paused" per-media option the app relies on for silent restarts (VlcAudioPlayer.cs:2083-2084, 2094-2095). _(source: https://raw.githubusercontent.com/videolan/vlc/master/NEWS)_
- **LibVLCSharp / libVLC 4.x line (preview only)** — VLC 4.0 adds native 'WASAPI: exclusive mode and 24-bit output support' and 'MMDevice: add default-device selection and passthrough control', plus 'Fix MMDevice crash when all devices are removed during playback and deadlocks'. This overlaps the app's entire NAudio-based custom sink: exclusive mode via PrepareExclusiveOutputFor + WasapiOut(AudioClientShareMode.Exclusive) (VlcAudioPlayer.cs:2088-2089; WasapiGainOutput.cs:190) and the NOCTIS_WASAPI amem route via SetAudioCallbacks (VlcAudioPlayer.cs:499, 514, 1379). That route is built on the documented VLC 3.x amem constraint 'VLC 3.x's amem module hard-rejects any format but "S16N"' (VlcAudioPlayer.cs:1371-1375, 2855) — a 3.x-specific behavior that must be re-verified before any 4.x move. Also: 4.0 removes the bandlimited resampler; the app's explicit "--audio-resampler=speex" + "--speex-resampler-quality=10" (VlcAudioPlayer.cs:429-430) are not listed as removed but should be re-validated. Note LibVLCSharp 4 is preview-only, distributed via feedz.io, not nuget.org — no action possible today. _(source: https://raw.githubusercontent.com/videolan/vlc/master/NEWS ; https://www.nuget.org/packages/LibVLCSharp)_
- **LibVLCSharp 3.10.0 (current)** — The only breaking change in 3.10.0 ('UWP and WinUI: the LibVLCSharp package no longer ships the UWP and WinUI VideoView targets') does not affect this Avalonia app; 3.10.0 also fixed Avalonia VideoView detach updates. 3.9.7's note 'Windows ARM64 native library loading with x64 fallback (requires VideoLAN.LibVLC.Windows 3.0.23.1)' is already satisfied by the app's exact pin pair (3.10.0 + 3.0.23.1) — keep these two versions moving together. _(source: https://raw.githubusercontent.com/videolan/libvlcsharp/3.x/NEWS)_
- **VideoLAN.LibVLC.Mac** — None of the researched 3.0.21-3.0.23 changes (avformat/FLAC seek fixes, security batches) reach macOS users at all unless VLC.app is installed: the nuget payload actually restored is the 2019 3.1.x package (see finding 1), and the in-code claim that 'Windows/macOS ship VideoLAN's full plugin payload' justifying forced "--demux=avformat" (VlcAudioPlayer.cs:3802-3804 doc + 420-423) is not verifiable for that stale package. VLC 3.0.22's 'Prevent FLAC seeking logic get stuck' and the CVE-2024-46461 mms fix are absent from the bundled mac build. _(source: https://api.nuget.org/v3-flatcontainer/videolan.libvlc.mac/index.json ; https://raw.githubusercontent.com/videolan/vlc/3.0.x/NEWS ; https://github.com/advisories/GHSA-3hwv-fr9j-3wjq)_
- **SkiaSharp (2.88.x -> 3.x/4.x) — Avalonia unification constraint** — Avalonia 11.x officially targets SkiaSharp 2.88.x; SkiaSharp 3 is only 'best-effort' compatible (tracking issue AvaloniaUI/Avalonia#15503) and must be rolled forward MANUALLY: every SkiaSharp.* package — the managed SkiaSharp AND SkiaSharp.NativeAssets.Linux (which Avalonia.Skia 11.3.18 floors at 2.88.9, and which this app needs for its linux-x64/linux-arm64 RIDs, src/Noctis/Noctis.csproj:13) — must be bumped to the SAME 3.x version, or the managed/native libSkiaSharp mismatch crashes at startup (reported in AvaloniaUI/Avalonia#15575). HarfBuzzSharp is not a blocker: Avalonia 11.3.18 already floors it at 8.3.1.1, the line SkiaSharp 3.x pairs with. SkiaSharp 4.x is NOT compatible with Avalonia 11.3.x at all (mono/SkiaSharp#3865). Practical rule for this repo: stay on 2.88.9 until Avalonia moves its own dependency. _(source: https://github.com/AvaloniaUI/Avalonia/issues/15503 ; https://github.com/AvaloniaUI/Avalonia/issues/15575 ; https://github.com/mono/SkiaSharp/issues/3865 ; https://www.nuget.org/packages/Avalonia.Skia/11.3.18)_
- **SkiaSharp (2.88.x -> 3.x) — app-code impact (text APIs)** — This app's direct Skia code would compile on 3.x but hit the deprecation wall: all SKPaint text/font members (TextSize, Typeface, MeasureText, canvas.DrawText(string,x,y,SKPaint)) are obsoleted in 3.x via a compat layer and become COMPILE ERRORS in 4.x (mono/SkiaSharp#3732) — migration is to SKFont + DrawText(text,x,y,SKFont,SKPaint). Concretely affected: src/Noctis/Services/ShareCardRenderer.cs:1187-1223 (MeasureTextFallback/DrawTextFallback swap paint.Typeface per run, call paint.MeasureText and canvas.DrawText(text,x,y,paint)) and ShareCardRenderer.cs:278/285 (titlePaint.TextSize). Behavior changes even on 3.x: new SKPaint().Typeface returns SKTypeface.Default instead of null (mono/skiasharp#3736) and new SKFont() defaults to SKTypeface.Empty (MeasureText returns 0, draws nothing) — ShareCardRenderer's ReferenceEquals(runs[0].Face, paint.Typeface) run-splitting and paint.Typeface! null-forgiveness assume 2.x semantics. Also src/Noctis/Services/Loon/LoonClient.cs:714 uses SKFilterQuality, obsoleted in 3.x in favor of SKSamplingOptions. _(source: https://github.com/mono/SkiaSharp/issues/3732 ; https://github.com/mono/skiasharp/issues/3736 ; https://github.com/mono/SkiaSharp/discussions/3163 ; https://www.mrumpler.at/the-trouble-with-text-rendering-in-skiasharp-and-harfbuzz/)_
- **SkiaSharp (2.88.x -> 3.x) — bitmap decode / color extraction** — No source-verified breaking changes to the SKBitmap/SKCodec/SKImage decode surface itself were found for 2.88->3.x (the vendored codecs are updated to Skia m119, but the decode APIs the app uses — SKBitmap, SKImageInfo, SKImage, SKEncodedImageFormat in LoonClient.cs and ShareCardRenderer.cs — carry no documented signature breaks; resampling of scaled draws moves from SKFilterQuality to SKSamplingOptions). Note the app's dominant-color extraction (src/Noctis/Services/DominantColorExtractor.cs:1-5) actually uses Avalonia's WriteableBitmap/RenderTargetBitmap, not SkiaSharp directly, so it only rides Skia transitively through Avalonia and is insulated from the managed API break — its exposure is solely the native-assets unification constraint above. _(source: https://mono.github.io/SkiaSharp/docs/releases/3.119.0.html ; https://github.com/mono/SkiaSharp/issues/2544)_
- **SQLitePCLRaw.bundle_e_sqlite3** — 2.x -> 3.x is a new major: the 3.0.x bundles re-version the native lib packages around the SQLite version itself (3.0.5 pulls package ID 'SQLite' 3.53.4) and rework package IDs. Microsoft.Data.Sqlite 8.x/9.x resolve against the 2.x-era SQLitePCLRaw API surface — do not float the bundle to 3.x independently of a Microsoft.Data.Sqlite major upgrade. Stay on 2.1.12 (already CVE-clean). _(source: https://github.com/ericsink/SQLitePCL.raw/releases)_
- **Microsoft.Data.Sqlite** — 9.x/10.x majors ride EF Core release trains (10.x pairs with .NET 10); take them with the framework retarget, not standalone. Additionally, dotnet/efcore states Microsoft.Data.Sqlite 11.0 will swap the default native bundle from SQLitePCLRaw.bundle_e_sqlite3 to SQLite3MC.PCLRaw because e_sqlite3 native builds lag upstream SQLite security fixes — the repo's manual-float pattern will need rethinking at that point. _(source: https://github.com/dotnet/efcore/issues/38257)_
- **Tmds.DBus.Protocol** — 0.21.x -> 0.92+/0.94.x is a deliberate version-scheme jump carrying behavior changes from the security rework (signal-sender verification against well-known-name ownership, 16-fd-per-message cap, malformed-body exception handling). 0.21.3 is the maintained backport branch Avalonia's FreeDesktop backend pins (>= 0.21.3), so upgrading past it unilaterally risks NU1608-style divergence from Avalonia for no security gain. _(source: https://github.com/tmds/Tmds.DBus/releases ; https://github.com/tmds/Tmds.DBus/security/advisories/GHSA-xrw6-gwf8-vvr9)_
- **xunit** — v2 -> xunit.v3 is a full framework migration (new 'xunit.v3' package IDs, new runner model), not a version bump; xunit.net publishes a dedicated migration guide. v2 remains usable but receives security fixes only. _(source: https://www.nuget.org/packages/xunit)_
- **Avalonia.Headless.XUnit** — 12.x exists (12.1.1, 2026-07-29) but this package must move only in lockstep with the app's Avalonia pin; a mismatched headless-harness major against Avalonia 11.3.18 would break the layout/geometry regression tests it exists for. Defer to the Avalonia-owning agent's verdict on 11.3 -> 12. _(source: https://www.nuget.org/packages/Avalonia.Headless.XUnit)_

## Appendix A — Current LibVLC initialization flags (reference)

LibVLC initialization flags (quoted verbatim as required). src/Noctis/Services/VlcAudioPlayer.cs:396-459 — base args list (403-414): "--no-video", "--no-osd", "--no-spu", "--input-repeat=0", "--no-audio-time-stretch", $"--file-caching={cachingMs}", $"--disc-caching={cachingMs}", $"--live-caching={cachingMs}", $"--network-caching={cachingMs}" with cachingMs defaulting to 1000 (NOCTIS_CACHING override, 396-398); conditional "--demux=avformat" (420-423, gated by ShouldForceAvformatDemux — everywhere except plain-Linux system libvlc, per issue #26); Windows-only (427-438): "--audio-resampler=speex", "--speex-resampler-quality=10", $"--aout={aout}" (default "mmdevice", NOCTIS_AOUT override); "--verbose=2" when NOCTIS_VLC_LOG=1 (443-444); NOCTIS_VLC_EXTRA tokens appended last (451-457); constructed at 459 `_libVlc = new LibVLC(vlcArgs.ToArray());`. Per-media options: ":audio-replay-gain-mode=track", ":audio-replay-gain-preamp=0.0", ":audio-replay-gain-default=-7.0" (normalization; 1795-1797 and 2075-2077), ":start-paused" (2084, paused restarts), ":input-repeat=65535" (VlcSilenceKeepAlive.cs:75). A second, audio-less LibVLC exists for animated covers: Controls/SharedLibVlc.cs:29 `new LibVLC("--quiet", "--no-video-title-show", "--aout=none")` — cannot touch the audio device by construction. No finding proposes changing any flag.

## Appendix B — Audit coverage notes (what was checked and found solid)

### perf-blocking
Scope searched: all of src/Noctis (ViewModels, Views, Controls, Converters, Services on UI call paths, Helpers, App.axaml.cs, Program.cs), excluding build/packaging trees. AlbumDetailViewModel.cs was searched via Bash grep -a per the binary-skip gotcha (only cheap File.Exists checks found). Patterns swept: .Result / .Wait( / GetAwaiter().GetResult / Thread.Sleep / Task.WaitAll-Any, sync file IO + TagLib + Bitmap decode in VM ctors, [RelayCommand] non-async methods, [ObservableProperty] change handlers, view event handlers, converters, Dispatcher callbacks; Dispatcher.UIThread.Invoke (sync) has zero usages; a multiline grep for UIThread.Post callbacks containing IO found none; sync HttpClient.Send / WebClient: none (all network is async). Checked and cleared (not findings): VlcAudioPlayer — every playback entry point (Play/Resume/Stop/SetExclusiveMode/PrepareNext/CancelPreparedNext) queues to ThreadPool under _playbackLock, so its Thread.Sleep / parseTask.Wait(8000) / .Result sites run on worker threads; Dispose blocks up to 3s but only at shutdown by design (line 3609). VlcAudioPlayer.Pause() (2980-2994) does run native LibVLC calls inline on the UI thread (PlayerViewModel.PlayPause:257) while Resume was deliberately moved to ThreadPool — but Pause takes no managed lock and any stall would depend on libvlc-internal locking, which is not provable from this repo, so it is deliberately NOT reported; a UI hang trace captured while pausing during a track transition would confirm or refute it. Also cleared: settings saves (debounced/async SaveAsync), storage-size walk (cached + off-thread, issue #31 fix intact), drop-import folder enumeration (Task.Run, MainWindowViewModel:820), rating tag writes (LibraryService.QueueRatingTagWrites Task.Run), MetadataViewModel/MetadataFinderViewModel batch tag writes (Task.Run), library file trashing (LibraryRemovalHelper Task.Run), lyrics probe (documented off-UI-thread, called via Task.Run), WrapViewModel/LyricShareViewModel renders (Task.Run), PreBlurredArtworkConverter (bounded: source is the 768px Player.AlbumArt, cached per-bitmap), IconKeyToGeometryConverter (cached small assets). Minor UI-thread IO noted but below finding threshold: SettingsView avatar File.Copy (Views/SettingsView.axaml.cs:221), SidebarViewModel playlist-cover File.Copy (SidebarViewModel.cs:666), ~10 File.Exists stat calls per track change in PlayerViewModel.LoadAlbumArt + AnimatedCoverService.Resolve (only material on network-mounted libraries), iTunes artwork-search thumbnail new Bitmap(ms) on UI continuation (small images). Not covered in depth: pure-CPU LINQ/collection churn on the UI thread (out of this domain's IO/wait focus), and tools/NoctisCoverProxy (not UI).

### perf-render
Scope covered: all ItemsControl/ListBox/TreeView usages in the 55 .axaml files under src/Noctis (excluding build dirs), every converter in Converters/, all Controls/ (CachedImage, EqVisualizer, MarqueeTextBlock, HighlightTextBlock, AnimatedCoverImage, SmoothScrollBehavior in Helpers/), all IterationCount=INFINITE animations, all ScrollChanged/LayoutUpdated/PropertyChanged-for-Offset handlers, and all DispatcherTimer/RequestAnimationFrame loops in Views/. AlbumDetailViewModel.cs was searched via Bash grep -a per the binary-skip gotcha.

Verified CLEAN (no finding): main library surfaces virtualize correctly — LibrarySongsView (ListBox fills DockPanel, LibrarySongsView.axaml:109), LibraryAlbumsView/LibraryArtistsView/FavoritesView (row-chunked outer ListBox + inner UniformGrid), PlaylistView track list (star-row Grid, 'the only scrolling region (virtualized)' comment at line 788), LibraryFoldersView track list, MainWindow queue popup ListBox. AlbumDetailView's per-disc ListBoxes are deliberately non-virtualized (StackPanel ItemsPanel with documented rationale, AlbumDetailView.axaml:548-555) — bounded per album, accepted tradeoff, not reported except as it amplifies the EqVisualizer finding. Bounded collections behind unvirtualized ItemsControls confirmed via ViewModels: AddSongsDialog Results capped at 100, StatisticsView PlayLog capped at 100, Home rows capped (6 top songs/10 albums/6 artists), History capped at 50. Per-frame machinery that is correctly gated: PlaybackBarView marquee (16ms timer only while overflowing+playing+opacity>0, with an explicit lyrics-page opacity kill), MarqueeTextBlock (stops on detach/no-overflow), LyricsView mesh-gradient timer (stopped on detach and on mode change), SmoothScrollBehavior (RequestAnimationFrame chain self-terminates on settle), LyricsView searching-dots and intro-dots animations (gated via [IsVisible=True]/.active selectors), PreBlurredArtworkConverter (per-bitmap cached, replaced a per-frame BlurEffect). LyricsView/LyricsPanelView unvirtualized lyric-line + per-word trees are the app's core animated surface with extensive documented perf work; no new defect identified there beyond what's already engineered.

Not verifiable statically (would need runtime data): actual frame-time impact of the hidden-spinner animations and hidden EqVisualizer timers (needs an idle-window frame counter or profiler), the exact LayoutUpdated per-pass fan-out count, and Home first-paint stall magnitude from synchronous artwork decodes (depends on disk and art sizes).

### leaks
Scope: src/Noctis only (excluded worktrees/bin/obj/etc. by passing explicit paths). Searches run: all `new DispatcherTimer`, `new System.Threading.Timer`, `new System.Timers.Timer`, `PeriodicTimer` (none), `new FileSystemWatcher`, `while(...)` + `Task.Delay` loops, `new CancellationTokenSource`, `new HttpClient`, `static event`, `GetObservable(...).Subscribe`, `DispatcherTimer.Run*`, and all ` += ` subscriptions across Views/, Controls/, Helpers/, ViewModels/ (AlbumDetailViewModel.cs additionally searched with Bash grep -an due to its binary-detection quirk). Architecture established from App.axaml.cs CachedViewLocator (12 cached singleton views) and MainWindowViewModel (section VMs are app-lifetime singletons; transient = PlaylistViewModel, AlbumDetailViewModel, LyricShareViewModel, MetadataViewModel, LrcEditorViewModel, WrapViewModel, dialog VMs; navigation history capped at 30 with DisposeViewIfTransient). Verified CLEAN with quoted-code checks: PlaylistViewModel/AlbumDetailViewModel Dispose (unsubscribe player/library/sidebar/settings); PlaylistView, LibraryFoldersView, LibrarySongsView, LibraryAlbumsView, LibraryArtistsView, SettingsView, LyricsPanelView (all pair subscribe/unsubscribe across DataContextChanged + attach/detach); LyricShareDialog Closed→Detach (stops 30fps anim timer, cancels export/preview CTS, unsubscribes player/lines); MainWindow taskbar RebindCurrentTrack (unsubscribes previous Track); LyricsViewModel per-track Track.PropertyChanged (3 unsubscribe sites); static-event subscribers (LottieToggle OnLoaded/OnUnloaded, CachedImage attach/detach for ArtworkCache.Invalidated, remainder are app-lifetime singletons); timers with proven stop/dispose: PlayerViewModel seek/library-save/natural-end (dispose-before-recreate or one-shot self-clear), PlayHistoryService save debounce (Dispose before new), VlcAudioPlayer _positionTimer (Stop+Dispose at 3587-3588), LibraryWatcherService (FSWs disposed on every Refresh and in Dispose; flush/reconcile timers disposed), EqVisualizer/MarqueeTextBlock/AutoScrollBehavior (attach/detach paired), MenuOpenAnimation/SidebarView/MiniPlayerWindow/LyricsView recenter+dismiss+follow timers (one-shot, self-stopping), MemoryTracer (opt-in, app-lifetime), Discord reconnect loop (bounded, CTS cancelled/disposed), single DI-singleton HttpClient. Not verified (needs runtime): exact Avalonia ordering of DataContextChanged vs attach on first view creation (affects finding 3's first-visit claim); whether Window.Loaded can re-fire on tray hide/show for MainWindow.InitializeOnLoadedAsync (no guard exists, but duplicate tray icons/SMTC would be loudly visible and are not reported, so treated as non-firing and not flagged). PlaylistViewModel's subscribe condition (_isSmartPlaylist) vs dispose condition (_playlist.IsSmartPlaylist) was checked and proven equivalent (IsSmartPlaylist only ever set at playlist creation).

### scaling
SCOPE COVERED (repo root C:\Users\okfer\Downloads\Noctis\Noctis, app source src/Noctis, read-only): full-collection load path (LibraryService/PersistenceService/SqliteLibraryIndexService/UnifiedLibraryService), scan pipeline (ScanCoreAsync, progressive publish, artwork extraction/backfill), index rebuild (RebuildIndexesCoreAsync), search/filter in Songs/Albums/Artists/Favorites/Playlist/CommandPalette/TopBar, ArtworkCache + CachedImage, AnimatedCoverService, ArtistImageService, DuplicateFinderService/DuplicateMatcher, FuzzyTrackMatcher + PlaylistImportService, PlaylistViewModel + SmartPlaylistEvaluator, HomeViewModel, FavoritesViewModel, SidebarViewModel, queue paths in PlayerViewModel/QueueViewModel, AlbumDetailViewModel (searched via bash grep due to the known binary-skip issue).

KEY ARCHITECTURE ANSWERS: (1) The whole track list IS materialized in memory — LibraryService.cs:29 `private List<Track> _tracks = new();` exposed at :57 as `IReadOnlyList<Track> Tracks`; nothing is paged at the data layer; every view snapshots it with ToList(). Lyrics were already moved out of Track to a lazy per-track store, so resident size per track is modest; at 50k this is an accepted-design memory cost, not a defect. (2) Startup = streaming JSON deserialize of every track (PersistenceService.cs:249) + index-cache fast path (TryRestoreFromCacheAsync) — the SQLite 'scalable backing store' is write-only (finding above). (3) Bitmap memory IS bounded: ArtworkCache is a real LRU with a 256 MB byte budget + 2000-entry cap, DecodeToWidth(512) thumbnails, GC memory-pressure accounting, and a global-counter touch discipline (ArtworkCache.cs:47-50, 140, 162) — no finding there; on-disk artwork is the unbounded side (finding 4). (4) Scan is parallel with mtime/size skip and streaming enumeration; per-file artwork save is guarded by an albumArtClaimed TryAdd so it is once per album, not N+1 per track.

CLEAN AREAS (checked, no finding): DuplicateMatcher is O(n) hash grouping off-thread; LibrarySongsViewModel is the reference implementation (debounce, cached search keys, background rebuild, BulkObservableCollection, IsActive gating); LibraryArtistsViewModel and LibraryAlbumsViewModel rebuilds are off-thread with generation guards; queue operations use bulk ReplaceAll/AddRange; SidebarViewModel favorites count is off-thread; ArtistImageService sweep is off-thread and single-flighted; AnimatedCoverService holds no caches.

MINOR ITEMS NOT PROMOTED TO FINDINGS: AlbumDetailViewModel.RefreshFromLibrary runs on every LibraryUpdated with a linear _library.Albums.FirstOrDefault (GetAlbumById would be O(1)) and an unconditional Tracks.ReplaceAll — O(albums) per 1.5 s tick while an album page is open during a scan, cheap in absolute terms; FavoritesViewModel.Refresh is a deliberate sync UI-thread full-library Where(IsFavorite) while active (documented trade-off for first-paint correctness, simple field reads, estimated single-digit ms at 50k); the fixed 1.5 s progressive-publish interval does not scale with N (each tick re-sorts the whole snapshot and rebuilds indexes — cost captured within the PrimaryArtist finding); UnifiedLibraryService.GetUnifiedTracksAsync sorts the full library per call but has no callers in app code.

LIMITS: All wall-clock/allocation magnitudes are estimates from code inspection and are labeled as such per finding; no profiler or runtime measurement was run (read-only audit). Prior project notes (2026-07-30 scale audit) informed where to look, but every claim above was re-verified against current code; items those notes listed as open that are NOW FIXED and therefore excluded: LibraryArtistsViewModel.ApplyFilter is no longer sync on the UI thread, FavoritesViewModel now has IsActive gating.

### audio
LibVLC initialization flags (quoted verbatim as required). src/Noctis/Services/VlcAudioPlayer.cs:396-459 — base args list (403-414): "--no-video", "--no-osd", "--no-spu", "--input-repeat=0", "--no-audio-time-stretch", $"--file-caching={cachingMs}", $"--disc-caching={cachingMs}", $"--live-caching={cachingMs}", $"--network-caching={cachingMs}" with cachingMs defaulting to 1000 (NOCTIS_CACHING override, 396-398); conditional "--demux=avformat" (420-423, gated by ShouldForceAvformatDemux — everywhere except plain-Linux system libvlc, per issue #26); Windows-only (427-438): "--audio-resampler=speex", "--speex-resampler-quality=10", $"--aout={aout}" (default "mmdevice", NOCTIS_AOUT override); "--verbose=2" when NOCTIS_VLC_LOG=1 (443-444); NOCTIS_VLC_EXTRA tokens appended last (451-457); constructed at 459 `_libVlc = new LibVLC(vlcArgs.ToArray());`. Per-media options: ":audio-replay-gain-mode=track", ":audio-replay-gain-preamp=0.0", ":audio-replay-gain-default=-7.0" (normalization; 1795-1797 and 2075-2077), ":start-paused" (2084, paused restarts), ":input-repeat=65535" (VlcSilenceKeepAlive.cs:75). A second, audio-less LibVLC exists for animated covers: Controls/SharedLibVlc.cs:29 `new LibVLC("--quiet", "--no-video-title-show", "--aout=none")` — cannot touch the audio device by construction. No finding proposes changing any flag.

Files read in full: Services/VlcAudioPlayer.cs (all 3833 lines), Services/WasapiSilenceKeepAlive.cs, Services/VlcSilenceKeepAlive.cs, Services/WindowsSessionVolume.cs, Services/WasapiGainOutput.cs, Services/AutoMixTransitionPlanner.cs, Controls/SharedLibVlc.cs. PlayerViewModel.cs read in the playback-relevant sections (commands 247-361, seek 1287-1351, PlayTrack 1384-1492, advance/AutoMix/gapless 1494-2305, natural-end fallback constants 161-165). Settings wiring spot-checked (SettingsViewModel.ApplyAudioSettings 1349-1374).

Verified compliant (no findings): VLC event handlers OnEndReached/OnError never call Play/Stop/Pause (they only read properties, manage timers, and raise events; both are exception-wrapped for the native-thread unwind hazard — 3153-3247). PlaybackError→AdvanceQueue and TrackEnded→AdvanceQueue both go through Dispatcher.UIThread.Post and the queued Play() path, so no VLC-thread state calls. Only PlayerViewModel subscribes to the player events, and its handlers post to the UI thread (coalesced position updates). AudioPlay callback size arithmetic (the fixed 0x80131506 over-read), the EQ apply queue's retry-storm fix, the seek worker's dedicated above-normal-priority thread, DrainPositionTimerCallback/DrainSeekWorker before disposals, and WasapiGainOutput's fault-aware backpressure all check out as claimed by their comments. WindowsSessionVolume/WasapiSilenceKeepAlive COM vtables match the real interface layouts; the keep-alive session-exclusion handshake is ordered correctly (id published before Start).

Noted but not filed as findings: (a) exclusive mode (and NOCTIS_WASAPI=1) disables PrepareNext entirely (VlcAudioPlayer.cs:1763), so the VM's 0.3 s-early gapless advance commits without a player-side standby — TryStartPreparedAutoMix falls through and the plain stop/parse/play path both truncates ~0.3 s of the outgoing track and pays open latency; magnitude unverified (needs by-ear/log timing on real hardware). (b) PlayInternal's parse timeout path (pure 8 s expiry, not skip-cancel) leaks the Media object and surfaces a generic "operation was canceled" error via the outer catch (2021-2033 vs 2183-2186) — hygiene. (c) The 10 Hz TryAdvanceForAutoMix planner call is cheap (string Contains over a few tag fields) — no stall risk found. Not audited: SmtcService/MprisService command marshaling into PlayerViewModel, AudioConverterService, ReplayGainScannerService, EmphasisBell, WebRemoteServer playback control paths, and there is no Services/AudioAnalysis/ directory in this tree (the key-files list names one; silence profiles are metadata-only estimates in AutoMixTransitionPlanner.EstimateSilenceProfile).

### security-app
Domain: file handling & deserialization security. Areas examined with evidence, found SOLID (no finding): (1) Playlist import — PlaylistImportParser (m3u/csv/json) resolves entries only against the in-memory library via FuzzyTrackMatcher (no file I/O on entry paths); malformed JSON/CSV exceptions are caught in PlaylistImportViewModel.cs:44-62. (2) Drop import — MainWindowViewModel.MoveFileIntoManagedRoot (lines 2298-2320) uses Path.GetFileName + explicit GetFullPath containment check. (3) File organize — FileOrganizePlanner sanitizes each template segment independently ('AC/DC' cannot escape, '..' collapses to 'Unknown'); only the reserved-device-name gap reported. (4) YamlDotNet (LyricsfileParser.cs:16-19) uses the default DeserializerBuilder with IgnoreUnmatchedProperties and no custom tag/type mappings — cannot instantiate arbitrary CLR types; parse wrapped in try/catch. (5) TTML — XDocument.Parse keeps DTD prohibited (TtmlParser.cs:47-58), catch-all on parse, explicit nesting limit; no other XmlDocument/XmlSerializer/BinaryFormatter usage in the app. (6) All persistence is System.Text.Json without polymorphism; PersistenceService.LoadJsonWithOutcomeAsync catches corrupt files and quarantines them (lines 442-509). (7) Update path is hardened: URLs pinned to GitHub HTTPS (IsTrustedGitHubUrl, UpdateService.cs:647-656), random temp filename vs TOCTOU (line 432), SHA-256 fail-closed with requireChecksums:true at both install call sites (SettingsViewModel.cs:3924, 4164); the dev 'save to Downloads' path (4241) omits requireChecksums but never launches the file. (8) SQLite — SqliteLibraryIndexService and AudioAnalysisStore are fully parameterized ($-parameters throughout; the only non-parameterized statements are constant DELETE/SELECT COUNT). (9) Process.Start — ffmpeg/ffprobe/dbus/trash calls use ArgumentList or constant Arguments; OpenUrl restricts to http/https before ShellExecute; Windows explorer /select uses the quoted single-string form (quotes are invalid in NTFS names). (10) Loon relay artwork requests — ResolveArtworkPath (LoonClient.cs:431-450) does GetFullPath + root-prefix containment. (11) Artwork/animated-cover/lyrics cache filenames are GUID-derived ({albumId}.jpg, {trackId}.json); lyrics sidecars derive strictly from the track's own path via Path.ChangeExtension. (12) Network reads are size-bounded via HttpSafety.ReadStringBoundedAsync/ReadBytesBoundedAsync; LrcLib/NetEase JSON errors are wrapped as LyricsProviderException. (13) tools/NoctisCoverProxy stores covers in-memory only (CoverArtStore) — no filesystem paths from network input. Not covered: TagLib#'s own robustness against malicious media files (third-party), macOS/Linux runtime confirmation of findings 3, and AlbumDetailViewModel was additionally grepped via bash (grep -a) for Process.Start/Path.Combine patterns — no hits relevant to this domain.

### security-net
ENDPOINT INVENTORY (all outbound HTTP via one shared HttpClient — Program.cs:246-255: default handler (OS cert validation), 15s timeout, UA "Noctis/1.0"; no per-service handlers anywhere). Deezer: https://api.deezer.com/search/artist (ArtistImageService.cs:10), /search, /track/{id}, /album/{id} (DeezerApi.cs:25-32; HTTP side in DeezerMetadataService) + image CDN URLs taken from API JSON. LRCLIB: https://lrclib.net/api/get|search (LrcLibService.cs:10,36,80). NetEase: https://music.163.com/api/search/get/web + /api/song/lyric with spoofed browser UA + Referer (NetEaseService.cs:16-17,66-67). Last.fm: https://ws.audioscrobbler.com/2.0/ (LastFmService.cs:17; scrobbles POSTed, session key in query for GET calls — HTTPS only). ListenBrainz: https://api.listenbrainz.org/1 with token in Authorization header (ListenBrainzService.cs:18,50,107). MusicBrainz: https://musicbrainz.org/ws/2/recording, 1 req/s gate (MetadataLookupApi.cs:26, MetadataFinderService.cs:75-93). Apple/iTunes: itunes.apple.com/search|lookup, music.apple.com/us/search HTML, a5.mzstatic.com hi-res rewrite, mvod.itunes.apple.com HLS (ITunesArtworkService.cs:17-19,804). GitHub updater: api.github.com releases + asset download, host-pinned via IsTrustedGitHubUrl (HTTPS + github.com/*.github.com/*.githubusercontent.com only, UpdateService.cs:647-656), SHA-256 manifest verification fail-closed for normal updates (UpdateService.cs:419-428,483-498), random temp filename vs TOCTOU. Loon relay: default https://noctis-loon.duckdns.org → wss (AppSettings.cs:281, SettingsViewModel.cs:2348-2359); LoonClient refuses cleartext ws:// to non-loopback (LoonClient.cs:234-241), path-traversal-guards relay-requested artwork paths (ResolveArtworkPath, LoonClient.cs:431-450), caps inbound WS messages at 4 MB and throttles request handlers. Media servers (user-configured): Subsonic /rest/*.view with salted md5 token, never raw password in URL (SubsonicClient.cs:179-199); Jellyfin /Users/AuthenticateByName (password sent once, only AccessToken+UserId kept, JellyfinClient.cs:43-94), stream URLs carry api_key because LibVLC can't set headers (JellyfinClient.cs:207-211); plain http restricted to private/LAN hosts (MediaServerUrl.cs:51-58,62-76), Navidrome sync connector stricter https-or-loopback (NavidromeMediaSourceConnector.cs:245-254) and persists tokenless navidrome:// FilePaths (line 150). WebDAV connector is a stub (OPTIONS probe only). Inbound: WebRemoteServer (TcpListener all interfaces, private-source-IP + token gated). Discord presence is local IPC (DiscordRpcClient, ApplicationId only — public by nature, DiscordPresenceService.cs:16). tools/NoctisCoverProxy: serves /ws + /art/{clientId}/{contentId}; it makes NO outbound fetches — it cannot be used as an SSRF relay (clients push bytes over WS; Program.cs:18-39).

TLS: zero hits for ServerCertificateCustomValidationCallback / DangerousAcceptAnyServerCertificateValidator / RemoteCertificateValidationCallback / CheckCertificateRevocation across src and tools. No cert pinning (beyond the GitHub host pin). No plain-http service endpoints in code; the only http:// strings are the LAN web remote URL, user-configured LAN servers, a plist DTD string (StartupHelper.cs:137), and the legacy Loon default which is auto-upgraded to https (SettingsViewModel.cs:2358-2359).

DOWNLOAD HANDLING: HttpSafety (4 MB text / 24 MB image caps, spoofed-Content-Length-safe preallocation, magic-byte LooksLikeImage) is used by LRCLIB, NetEase, Last.fm, Deezer, MusicBrainz, Subsonic/Jellyfin artwork, artist images, GitHub JSON. Exceptions noted in findings (three iTunes stream parses); intentional exceptions: installer download streams to disk (GitHub-pinned + hash-verified), offline pin/DownloadTrackAsync stream user's own server to the cache dir with SHA1-hex filenames (OfflineCacheService.cs:182-187 — no traversal possible), animated covers use a 256 MB cap (MaxAnimatedCoverBytes). Artwork writes land under the app data artwork/ tree with app-generated names.

LOG LEAKAGE: LogRedaction.Scrub strips URL query strings + token-style pairs; applied in DebugLog.Write BEFORE the ring and the CrashJournal disk sink (DebugLog.cs:51-63) and in the VLC diag writer (VlcAudioPlayer.cs:3747-3753); the VLC→DebugLog bridge routes through the scrubbed Write. AvaloniaLogBridge forwards through DebugLog (scrubbed). DebugLogger is in-memory only, disabled by default, mirrors to Debug output only in DEBUG builds. Queue persistence stores track IDs only (PlayerViewModel.cs:997-1010), so Jellyfin api_key / Subsonic t&s stream URLs are not written to queue.json; server artwork is materialized to local files precisely so "no auth-bearing image URLs exist" (MediaServerService.cs:10-13, JellyfinClient.cs:180-182). Loon connect logging prints the server URL without credentials (URL carries none). One unscrubbed sink (crash.log) is reported as a finding.

NOT VERIFIED / OUT OF SCOPE: the production loon relay server (Oracle VM) is not in this repo — only the client and the simplified NoctisCoverProxy tool were auditable; whether NoctisCoverProxy is actually deployed is unknown. Runtime confirmation of the HttpClient.Timeout-vs-streamed-body behavior relies on the repo's own empirical comments. SmbMediaSourceConnector (non-HTTP) and DiscordPresenceService IPC internals were only skimmed for network egress (none found). AlbumDetailViewModel.cs was covered via the repo-wide greps for endpoints/handlers/secrets (no URL or client hits besides UI code).

### deadcode-services
Scope: dead-code sweep of Services/ (119 files + AudioAnalysis/Loon/MediaServer subdirs), Models/ (37), Helpers/ (32), Converters/ (28) in src/Noctis. Method: extracted all 392 declared type names (class/interface/record/struct/enum) from those directories, then cross-referenced each with word-boundary grep -a (binary-skip-proof, covering the known AlbumDetailViewModel.cs gotcha) against all 539 .cs/.axaml/.csproj files in src/Noctis, tests/Noctis.Tests, and tools (bin/obj/artifacts excluded). Every candidate with <=2 external references was manually adjudicated against the 5 mandated checks (full grep, DI registration in Program.cs/App.axaml.cs, XAML resource/key/selector usage, reflection/typeof/nameof/string literals, test-only status); the 3-4 reference band was eyeballed for comment-only references (none found). Zero-external-reference nested types (COM interop structs, JSON DTOs, Loon protobuf messages, ShareCardRenderer internals, etc.) were spot-verified as used within their declaring files and correctly NOT flagged. Findings adjudicated as ALIVE and not reported include: all remaining converters (resource keys verified used), AutoplayService/RadioService (direct `new` in PlayerViewModel), MemoryTracer (App.axaml.cs env-gated), MprisService/SmtcService/TaskbarIntegrationService (MainWindow code-behind), AuditTrailService (injected into LibraryService), all AudioAnalysis types (Program.cs factory registrations + App.axaml.cs coordinator resolution), and the MediaServer stack. Test-only-but-intended surface noted, not flagged: DiscordPresenceService.ResolveArtworkKey, UpdateService.PickLatestRelease, JellyfinClient/SubsonicClient statics (their classes are otherwise alive). NOT covered: method-level dead code inside large live classes (VlcAudioPlayer, SettingsViewModel, PlayerViewModel), unused private members without resources, and directories outside the domain (Views/, ViewModels/, Controls/). One caveat spanning all fixes: memory notes an unmerged cross-platform branch; deletions on this branch could conflict with it.

### deadcode-ui
Scope covered (read-only, repo root C:\Users\okfer\Downloads\Noctis\Noctis, searches limited to src/Noctis, tests, tools; obj/bin excluded). (1) Views: all 46 Views/*.axaml checked — every one is wired via App.axaml DataTemplates (lines 61-106), the CachedViewLocator factory table in App.axaml.cs (lines 43-56), or direct `new` in a caller; low-reference views were individually traced to their instantiation site (CommandPaletteDialog→MainWindowViewModel, WrapDialog→StatisticsViewModel, ThemeEditorDialog→SettingsViewModel, LrcEditorDialog→LyricsViewModel, SongsViewOptionsDialog→MainWindowViewModel, MiniPlayerWindow→MainWindow.axaml.cs). No dead views. (2) ViewModels: all 49 files reference-counted; every class has external consumers (DI/new/DataContext). No dead ViewModels. (3) Controls: all 9 control classes used; the three UserControl .axaml files self-load via x:Class, EqVisualizer.axaml is StyleIncluded in App.axaml. (4) XAML resource files: App.axaml merges Icons.axaml and includes Styles.axaml + EqVisualizer.axaml; Assets/Themes/Dark.axaml and Midnight.axaml are loaded by App.SetTheme (App.axaml.cs lines 268-269). No orphan dictionaries. (5) Named resources: all 51 Icons.axaml keys, all 90 Styles.axaml keys (System*/OverlayCornerRadius treated as Fluent-consumed overrides, conservatively excluded; TrackListBoxItemTheme is referenced in-file at Styles.axaml:716), all theme-overlay keys, and all 39 view-local x:Key entries were checked, including string-based C# lookups (every FindResource/TryGetResource/TryFindResource site uses literal keys; no concatenated/interpolated key construction; only reflection is COM interop). (6) Assets: all 31 files checked in literal AND %20-URL-encoded forms (the encoding the code actually uses — a literal-only search gives false dead positives). Bonus sweep of Converters/ (adjacent, since converters live in XAML resources): all classes used except the one reported. AlbumDetailViewModel.cs binary-grep gotcha handled with `grep -an` via Bash. Not covered (other auditors' domains): Services/, Helpers/, Models/ dead code; dead members/properties inside live classes; unused x:Name elements; unreachable UI (a wired view that no user gesture can reach) was not assessed beyond ServerViewModel construction being confirmed.

### crossplat
Scope swept: every hit for OperatingSystem.Is*, RuntimeInformation.IsOSPlatform/OSArchitecture, #if directives, DllImport, Registry, and Environment.GetFolderPath/env-var reads under src/Noctis (338 .cs files) plus tools/NoctisCoverProxy (which contains NO platform-sensitive code — pure network, no filesystem/env usage) — excluded dirs honored throughout. AlbumDetailViewModel.cs was additionally searched via Bash grep -a (binary-skip gotcha): only PlatformHelper.ShowInFileManager calls, all covered. Environment.OSVersion: zero hits. The .github/workflows/dotnet.yml was consulted read-only to resolve two facts the app code depends on (mac Info.plist keys; AppRun exporting NOCTIS_BUNDLED_VLC=1 — confirmed at workflow lines 372-380). Verified-safe areas not listed as branches: no hardcoded drive letters or backslash separators in path logic (all Path.Combine/SpecialFolder); temp files via Path.GetTempPath; DragFileBehavior uses StorageProvider (cross-platform); WebRemoteServer uses TcpListener (no Windows URL-ACL dependency); Discord RPC delegates pipe-vs-unix-socket to the DiscordRichPresence library; close-to-tray/minimize-to-tray/start-minimized all guard on _trayIcon != null so tray-less Linux DEs degrade to normal window behavior; CoreAudioComInterop and all NAudio types are only reachable behind IsWindows guards; winmm timeEndPeriod call is reachable only after a successful Windows-guarded timeBeginPeriod. Not verifiable from code (needs runs, reflected in the checklist): everything by-ear (volume ramp quality on the integer path, keep-alive absence on mac/Linux first play), Avalonia backend behavior (tray icon rendering per DE, .ico decoding on non-Windows tray backends, F11/native-fullscreen interaction on macOS, font-fallback engagement), MPRIS against real desktop widgets, TCC/AppleEvents consent, AppImage swap across filesystems, and the VideoLAN.LibVLC.Mac package payload (arch/plugins) since NuGet contents are not in the repo.

### deps-avalonia
Versions verified on 2026-08-04 directly against the NuGet v3 flatcontainer API for all six assigned packages (plus nuget.org package pages); current pins confirmed in src/Noctis/Noctis.csproj:47-52 and tests/Noctis.Tests/Noctis.Tests.csproj:19. CVE check: GitHub Advisory Database query for 'avalonia' returns 0 advisories (https://github.com/advisories?query=avalonia); OSV.dev could not be queried (its query endpoint is POST-only and this audit is restricted to read-only GET fetches), so 'none found' rests on the GitHub Advisory Database alone. Changelog coverage: read the 12.0.0 and 12.1.0 GitHub release pages and the official v12 breaking-changes doc; the intermediate patch releases 12.0.1-12.0.5 and 12.1.1 were NOT read individually (they are bugfix patches; their content is subsumed by the cumulative picture but any patch-level behavior tweak there is uncovered). There is no 11.3.x delta to read since 11.3.18 is the newest 11.3 patch. Unverified items flagged inline: stability of Fluent SystemControl* accent resource keys under v12, and visual/metric impact of the v12 text-stack changes on this app's lyric rendering — both need a hands-on migration spike, not static analysis.

### deps-audio
EXTRA DUTY — verbatim LibVLC flag inventory from src/Noctis/Services/VlcAudioPlayer.cs: init argv (lines 403-414): "--no-video", "--no-osd", "--no-spu", "--input-repeat=0", "--no-audio-time-stretch", "--file-caching={cachingMs}", "--disc-caching={cachingMs}", "--live-caching={cachingMs}", "--network-caching={cachingMs}" (cachingMs default 1000, NOCTIS_CACHING override, lines 396-398); "--demux=avformat" (line 423, Windows/macOS always, Linux only with NOCTIS_BUNDLED_VLC=1 via ShouldForceAvformatDemux lines 420-422); Windows-only "--audio-resampler=speex", "--speex-resampler-quality=10" (429-430) and "--aout={aout}" defaulting to mmdevice with NOCTIS_AOUT override (436-438); "--verbose=2" when NOCTIS_VLC_LOG=1 (443-444); arbitrary NOCTIS_VLC_EXTRA tokens appended last (451-457); constructed at line 459 (_libVlc = new LibVLC(vlcArgs.ToArray())). Per-media options: ":audio-replay-gain-mode=track", ":audio-replay-gain-preamp=0.0", ":audio-replay-gain-default=-7.0" (1795-1797 standby path and 2075-2077 main path, when normalization enabled); ":start-paused" (2084). NAudio/WASAPI usage: NOCTIS_WASAPI=1 experimental per-sample-gain sink routes LibVLC audio through SetAudioCallbacks/amem (lines 499, 514, 1379; S16N-only constraint documented 1371-1375 and 2855); exclusive mode PrepareExclusiveOutputFor (2088-2089); WasapiGainOutput.cs uses WasapiOut(Shared, eventSync, 50ms latency) line 159 and WasapiOut(Exclusive, eventSync, 100ms) line 190 via NAudio.CoreAudioApi/NAudio.Wave; WindowsSessionVolume.cs drives ISimpleAudioVolume through hand-rolled COM interop (CoreAudioComInterop). macOS loader prefers VLC.app dylibs + VLC_PLUGIN_PATH setenv before falling back to the NuGet payload (296-331, 3781-3799). VERIFICATION PERFORMED: authoritative nuget.org flat-container version indexes fetched for all six assigned packages; VLC 3.0.x NEWS (3.0.21->3.0.24-beta1) and VLC master (4.0-dev) NEWS read in raw form; LibVLCSharp 3.x NEWS read in raw form; VideoLAN security bulletin index checked (SB-VLC-321/322 carry no CVE IDs; CVE-2024-46461 confirmed via GitHub advisory GHSA-3hwv-fr9j-3wjq as fixed in 3.0.21); CVE searches for LibVLCSharp and NAudio found none. NOT VERIFIED (needs runtime/CI data): the exact version macOS restore resolves for the phantom VideoLAN.LibVLC.Mac 3.0.21 pin (predicted 3.1.2 by NuGet lowest-applicable rule; confirm via a mac-leg project.assets.json or restore log showing NU1603); whether the resolved 3.1.x mac payload ships the avformat plugin (would confirm/deny that forced --demux=avformat can work without VLC.app on mac); content of LibVLCSharp 4 previews (feedz.io feed not enumerated). No repo files were modified; no dotnet commands were run.

### deps-media-parsing
Checked all 5 assigned packages against nuget.org (including the flat-container version indexes for exact latest-in-line versions), the GitHub Advisory Database, GitLab advisory mirror, and Snyk, on 2026-08-04. Local usage verified by reading src/Noctis/Noctis.csproj plus the consuming services (ShareCardRenderer.cs, Loon/LoonClient.cs, TaskbarIntegrationService.cs, DominantColorExtractor.cs, LyricsfileParser.cs, MetadataService.cs; JSON deserialization sites enumerated across 11 services). Extra duties covered: SkiaSharp 2.88->3.x breaking changes with the exact app lines that hit them, the Avalonia 11.3.18 SkiaSharp/HarfBuzzSharp pins (floors: SkiaSharp >= 2.88.9, NativeAssets.Linux >= 2.88.9, HarfBuzzSharp >= 8.3.1.1) and the native-assets unification constraint; TagLibSharp CVEs (none found in GHSA; C++ taglib CVEs confirmed inapplicable); System.Text.Json 8.x CVE ranges with confirmation that 8.0.5 already contains both fixes. Limits: could not retrieve the SkiaSharp GHSA advisory page directly (404 on guessed ID; affected-range < 2.88.6 taken from Snyk + mono/SkiaSharp#2608 — 2.88.9 postdates the fix either way); System.Text.Json 8.0.6 change content could not be confirmed beyond 'no vulnerability disclosed for it' (NuGet page lists no advisory); the TagLibSharp MP4-write corruption finding is an upstream report not reproduced here, hence unverified. No files were modified and no package commands were run.

### deps-platform
Scope covered: all assigned app packages in src/Noctis/Noctis.csproj (CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, Microsoft.Data.Sqlite, SQLitePCLRaw.bundle_e_sqlite3, System.Security.Cryptography.ProtectedData, Tmds.DBus.Protocol); YamlDotNet skipped per assignment (another agent). Full audit of tests/Noctis.Tests/Noctis.Tests.csproj (Microsoft.NET.Test.Sdk, xunit, xunit.runner.visualstudio, Avalonia.Headless.XUnit, SQLitePCLRaw twin pin) and tools/NoctisCoverProxy/NoctisCoverProxy.csproj (zero PackageReferences — framework-only ASP.NET Core via Microsoft.NET.Sdk.Web; audited as shared-framework exposure). Other app packages (Avalonia core/Fluent/Inter/Lottie/Diagnostics, LibVLCSharp, VideoLAN.LibVLC.*, NAudio.*, TagLibSharp, SkiaSharp, System.Text.Json, DiscordRichPresence) are outside my assignment. Extra duty answered: .NET 8 LTS end-of-support is 2026-11-10 (~3 months from today, 2026-08-04); .NET 9 STS ends the same day; .NET 10 is the current LTS (released 2025-11-11, supported to 2028-11-14) — source https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core. Microsoft.Data.Sqlite 8.x DOES have newer patches: 8.0.29 (2026-07-14, still pinning bundle_e_sqlite3 >= 2.1.6) — sources https://api.nuget.org/v3-flatcontainer/microsoft.data.sqlite/index.json and https://www.nuget.org/packages/Microsoft.Data.Sqlite/8.0.29. Method/caveats: versions taken from nuget.org gallery pages cross-checked against the authoritative api.nuget.org flat-container indexes on 2026-08-04; CVE claims verified against GitHub advisories, the upstream Tmds.DBus advisory, and oss-sec (CVE-2025-6965). No CVE IDs were inferred; 'none found' means no advisory surfaced in the searches performed. Read-only throughout: no files modified, no dotnet commands run. One residual uncertainty noted in-row: NuGet vs GitHub publish dates for SQLitePCLRaw 2.1.12 differ by a few days (2026-07-14 vs Jul 19 tag); the bundled SQLite 3.53.3 claim comes from the GitHub release notes and is the load-bearing fact.

## Appendix C — Findings refuted by adversarial verification (dropped)

- **SmbMediaSourceConnector track identity hash lowercases and backslash-normalizes paths on every OS — case-distinct files collide on Linux shares** — The quoted code is real (src/Noctis/Services/SmbMediaSourceConnector.cs:101-106 and 43-45: ToLowerInvariant + '/'->'\\' before MD5), and two case-distinct paths do produce identical SourceTrackId values. But the claimed harm — 'the unified library can conflate them' — has no code path anywhere in the repo. Exhaustive search of every SourceTrackId consumer shows nothing keys, dedupes, or matches on it for SMB tracks: (1) UnifiedLibraryService.cs:32-48 builds the unified view by concatenating _localLibrary.Tracks with per-connection lists ('all.AddRange(tracks)') and never reads SourceTrackId — 
