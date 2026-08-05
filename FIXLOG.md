# FIXLOG — Audit Phase 2 (branch `cross-platform`)

One line per finding: ID | commit | files | what to test by hand. Deferred/skipped items listed at the bottom with reasons. Test suite: 1115 tests, run after every batch — 1115/0 at completion.

Totals: 57 finding-commits (51 code/CI fixes + dead-code removals −701 lines), 8 deferred with plans, 0 findings disproved during implementation (1 fix — L23 — landed on code later proven unreachable and deleted; see note).

## Committed

- H1 | 8f191cf | VlcAudioPlayer.cs | Crossfade ON: pause or seek mid-fade → volume slider must stay responsive; a later timeline click must never mute audio.
- H2 | 8d0a1d8 | VlcAudioPlayer.cs | Volume ~25%, Crossfade ON, double-click a different song mid-play → fade-in tops out at slider loudness (no swell past it, no snap-down).
- M1 | 65126a5 | VlcAudioPlayer.cs | Crossfade/AutoMix ON, seek into a track's final seconds → incoming track plays in full; no track skipped (log: "VLC.EndReached.IgnoredTransition").
- M2 | 186a063 | VlcAudioPlayer.cs | Press Next then Pause immediately, ~10 tries → audio always stops within a beat and matches the transport UI.
- M3 | 85ab528 | VlcAudioPlayer.cs | On a slow source (NAS/HDD), press Next during last ~8 s of a track → skip is prompt (was: ignored up to ~8 s). Fast disk: gapless/crossfade handoffs still engage.
- H3 | 15f6071 | LrcEditorViewModel.cs | LRC editor on a 100+ MB FLAC → Save: window stays responsive during the write; "Saved" appears after; .lrc + .lrc.bak next to the track.
- M5 | f8e915c | HomeView.axaml, LibraryAlbumsView.axaml, EditPlaylistDialog.axaml | Cold start → Home: page appears instantly, covers pop in async (no first-navigation hitch); same on Albums ranked pills + Edit Playlist cover preview.
- M6 | f43ea43 | MetadataViewModel.cs, MetadataHelper.cs | Get Info on a track on slow disk → window opens immediately, codec/cover/advanced fields fill in a moment later; open→instant Save clears nothing (Copyright/advanced/artwork intact).
- M7 | d4af045 | LyricsViewModel.cs (+test call sites) | Lyrics page → Remove lyrics: click is instant (was hard freeze), sidecar lands in Recycle Bin, replaying the track doesn't resurrect lyrics.
- L1 | d188254 | MetadataViewModel.cs | Metadata editor → clear lyrics → Save: no freeze during the sidecar trash move; .lrc in Recycle Bin.
- H4 | b6cf6ed | QueueView.axaml | Queue thousands of tracks → open Queue page: opens instantly, memory flat, smooth scroll. NOTE: History now scrolls in its own max-240px region (by-eye check); queue POPUP animation untouched.
- M8 | ccf77d5 | EqVisualizer.axaml.cs | Album page, play through 4-5 tracks → CPU drops after each track change (was: hidden 60fps timers accumulate); bars still animate on the playing row, ease flat on pause.
- M9 | 7aea1e5 | ServerView.axaml, ServerViewModel.cs | Server section, Load More ×5 → memory stays flat, scroll smooth. NOTE: tiles now 5 fixed per row like local Albums (by-eye check).
- M10 | 93af0c6 | MainWindow.axaml, SettingsView.axaml, MetadataWindow.axaml, LyricShareDialog.axaml | Idle on Home → baseline CPU drops; every spinner still spins when shown (drop-import, scan, update check, metadata save/search, artwork/animated download, lyric share).
- L3 | b584dbb | LibrarySongsView.axaml.cs, PlaylistView.axaml.cs, AddSongsDialog.axaml.cs | Hygiene, behavior unchanged: long titles still ellipsize (issue #30), E badge/NEW pill hug title end after fast scroll + window resize.
- H5 | 3f1d765 | PlaylistViewModel.cs | Large smart + manual playlists: type in Find-in-Playlist, switch sorts, scan while open → no freezes; drag-reorder still persists; suggestions rail still works.
- M13 | e5a9ca2 | Album.cs, LibraryAlbumsViewModel.cs | Albums search: same results (incl. "dont" → "Don't"), faster; less CPU churn holding keys down.
- M14 | bf2ebdd | CommandPaletteViewModel.cs | Ctrl+K on 50k library: type 1 char → no stall; rapid type/delete → results match final text; Enter executes highlighted row.
- M15 | 73d55e8 | FuzzyTrackMatcher.cs, PlaylistImportService.cs, PlaylistImportViewModel.cs | Import a big mostly-unmatched CSV → much faster; closing dialog mid-match drops CPU immediately. NOTE: near-threshold fuzzy matches whose lengths differ >2× can flip to unmatched (audit's intended semantics).
- M16 | f9ddf3b | LibraryService.cs | Full scan of big library → no multi-second SQLite burst at scan end; quit mid-scan exits faster; ratings/plays/favorites still survive restart.
- M17 | 82de260 | Track.cs | Toggle Merge Featured off/on → artist names update both ways everywhere; scan-time CPU lower between publish ticks.
- L6 | 789231b | HomeViewModel.cs, MainWindowViewModel.cs | Scan running while on Songs → less background CPU; navigate to Home mid-scan → content current; heart from Songs → Home hearts correct.
- L7 | 1988660 | AlbumDetailViewModel.cs, FavoritesViewModel.cs, HomeViewModel.cs, LibraryAlbumsViewModel.cs, PlaylistViewModel.cs | Heart one track from each surface → instant flip, owning album tile updates, other tiles don't churn.
- M11 | 8a2852c | AlbumDetailView.axaml.cs | Album detail → click a related album (in-place swap) → visuals identical; leak gone (old view collectible — needs profiler to observe). Back-nav gradient/scroll unchanged.
- L4 | ef46900 | LyricsView.axaml.cs | First-ever lyrics visit: toggle Flowing Light over it → reacts immediately (was: only after re-entry). Skip track + leave mid-fade + return → NOT blank. Panel keeps updating when page left.
- L5 | f0b258e | LyricsViewModel.cs (+1 test) | Lyrics hidden for a minute → open page/panel: instantly anchored on current line, follows normally; slider-hold tracking still works. (Timer win needs profiler.)
- M18 | 1d6d0ca | SettingsViewModel.cs | Toggle "Analyze Tempo & Key" ON (no scan running) → backfill starts within ~1s, BPM/keys appear; OFF now cancels a running pass.
- M19 | b13c699 | SettingsViewModel.cs | "Clear Artwork Cache" / "Reset Everything" on a huge cache → no freeze; status text appears when done; button self-disables while running.
- M20 | 030d866 | SettingsViewModel.cs | Scan Library / Add-Remove Folder / Rebuild Index → no UI hitch when "N tracks found." appears; Storage rows update a beat later.
- M21 | 1fbe362 | SettingsViewModel.cs | ListenBrainz: Connect → Logout → type junk token, don't Connect, play a track → nothing scrobbled (was: silent scrobbles with junk token).
- L8 | 87c30bd | SettingsViewModel.cs, MainWindow.axaml.cs | System theme active + flip Windows Dark↔Light → app follows immediately (glass re-tints); non-System themes unaffected by OS flips.
- L9 | e8efb12 | SettingsView.axaml.cs | Pick a huge avatar GIF from a network share → no freeze; avatar appears when copy completes.
- L11 | 4d98de6 | MarqueeTextBlock.cs, SettingsViewModel.cs | Overflowing title on Cover Flow/Mini Player/Lyrics → flip matching marquee toggle → starts/stops scrolling immediately; per-surface gating intact.
- L12 | 00d0ce0 | SettingsView.axaml | Description-only: "Save analysis to file tags" now says it applies to tracks analyzed from now on. (Retroactive backfill = risky mass rewrite, per audit.)
- L13 | 3017639 | SettingsViewModel.cs | Type token, don't Connect, flip unrelated setting → settings.json keeps token empty; restart shows empty box. Connect+restart still shows "Connected as <user>".
- L15 | 2c52571 | Program.cs | Force a crash with a URL+query in the exception → crash.log shows "?[redacted]"; scrubber failure still writes the raw entry (crash never lost).
- L16 | e1b7dac | ITunesArtworkService.cs | Artwork search still returns results (4 MB bounded reads); oversize response degrades to "no results".
- L14 | e41733e | ITunesArtworkService.cs | Animated Artwork download 1080p + 2160p still succeeds end-to-end; non-Apple hosts in HLS parts now rejected.
- L20 | 4c88059 | FileOrganizePlanner.cs | Tag Album=CON, Title=NUL → organizer preview shows _CON\..\_NUL.mp3; move completes, file openable.
- L21 | 3a436a3 | PlatformHelper.cs | Linux-only: "Show in file manager" on a path with spaces + comma opens the right folder. Windows unchanged.
- L22 | 70c60fe | PlaylistInteropService.cs | Title containing a newline + UNC path → exported .m3u keeps EXTINF on one line, no injected entry.
- L23 | bdb738b | SmbMediaSourceConnector.cs | MOOT — the dead-code batch (L32) later proved this connector unreachable from any UI path and deleted it; no test needed.
- L19 | b137354 | SettingsView.axaml | Docs-only per audit: Web Remote subtitle now carries the cleartext-LAN caveat. (Token already rotates per session; mid-session rotation would break QR pairing.)
- M22 | 0bb05d1 | PathComparison.cs (new), LibraryWatcherService.cs, LibraryService.cs | Windows regression: delete watched folder → tracks leave; Keep-Files removals stay excluded. Linux fix: case-differing sibling dirs no longer cross-matched (needs Linux run; CI legs cover comparers).
- L24 | 5429200 | PlatformHelper.cs | Windows: System theme still follows OS toggle. Linux KDE: System resolves Plasma light/dark via portal → kdeglobals (needs Linux run). Known wart: KDE with GNOME schemas installed still answers via gsettings first.
- L25 | 3896c59 | .github/workflows/dotnet.yml | Verify on next push: "Audit dependencies" runs on win/mac/linux legs. INTENDED: mac leg goes RED via -warnaserror:NU1603 if the LibVLC.Mac ghost pin doesn't resolve (H8 tripwire).
- M23 | 83f0c47 | .github/workflows/dotnet.yml (Info.plist template) | Next CI-built .dmg on a real mac: "Move file to Trash" → one-time Finder Automation consent prompt, file reaches Trash. Local check = YAML/plist text only.
- L28 | 7e5d003 | ArtistTokensConverter.cs + AlbumDetailView.axaml resource | Album detail artist token pills still render/click.
- L29 | b338d06 | TrackPlaylistCommandParameterConverter.cs + resource | Album detail context menus unaffected.
- L34 | 437a09a | PlaylistView.axaml, LyricsView.axaml (duplicate resource declarations) | Playlist GUID-dependent UI + volume icon in playbar unaffected.
- L31 | 2febc32 | IAlbumArtworkSearch.cs + Program.cs registration | Artwork search still works (concrete ITunesArtworkService).
- L33 | cfafb01 | UnifiedLibraryService.cs, IUnifiedLibraryService.cs + registration | App boots; library loads.
- L30 | bc92706 | OfflineCacheService + models + Track.OfflineState | App boots; existing library.json with stale offlineState field loads fine (ignored on read).
- L32 | 3b5147d | Local/Smb/WebDav connectors + registrations (IMediaSourceConnector + Navidrome kept — test-referenced) | Server section (Jellyfin/Navidrome) still connects/browses — it uses the separate MediaServer stack.
- L35 | 3d74a10 | Inter-ExtraBold.ttf (746 KB) + App.axaml FontFamily | Typography unchanged anywhere (font was never referenced).
- L36 | 7a75511 | 9 StreamGeometry keys in Icons.axaml | All icons still render (42 live keys kept; variable-lookup sites audited).
- L37 | a5e7c8e | Previous ICON.png, Pause ICON.png | Transport buttons unchanged (they use StreamGeometry icons).

## Deferred (needs your go-ahead — bigger than the audit estimated)

- M4 | remote-stream gapless/crossfade requires remote-capable PrepareNext (FromLocation/ParseNetwork), lifting !isRemote gates in VlcAudioPlayer + 2 File.Exists bails in PlayerViewModel, grace tuning, URL token scrubbing — a ~150+ line cross-file feature needing a real media server to verify.
- L2 | playlist-tile virtualization: audit's own risk note says "not worth the churn unless large playlist counts are real"; fix is ~130 lines (chunked-row model + XAML restructure + scroll-restore migration) for a view holding tens of items. Evidence re-verified accurate; deferred on cost/benefit.
- M12 | artwork downscale-on-persist: metadata editor exports the persisted store bytes verbatim (its "source of truth"), and user-applied custom covers land in the same dir under the same {albumId} name — a safe fix needs provenance tracking + format-preserving capped re-encode + migration + opt-out setting (~4-5 files) and a product decision on lossy re-encoding.
- L10 | profile name/avatar consumed nowhere: product fork — (a) surface the profile somewhere (Home greeting was already rejected; anything else is UI design), (b) delete the dead ProfileUsername plumbing (~5 sites, forecloses (a)), or (c) accept the card as self-contained. Pick a branch.
- L17 | cover proxy hardening IMPLEMENTED ON DISK but uncommittable — tools/ is gitignored and the proxy was never git-tracked. JPEG magic check + store caps + no-TTL-refresh build clean in tools/NoctisCoverProxy. Decide: un-ignore tools/ to commit. HMAC auth further deferred (no verifiable consumer of the JSON /art protocol; could break a deployed client).
- H6 | Last.fm key+secret (LastFmService.cs:15-16): needs YOUR rotation at last.fm/api/account first; then MSBuild-generated secrets file from NOCTIS_LASTFM_KEY/_SECRET env (CI secret), empty-string fallback keeps local builds working. csproj change = Phase 3.
- L18 | keychain-backed secret storage (macOS Keychain + libsecret behind the ProtectSecret seam) is feature-sized with locked-keyring failure modes; 0700/0600 permission hardening already shipped. Needs hands-on mac/Linux testing.
- M24 | macOS Now Playing/media keys: new MacNowPlayingService mirroring MprisService (TryStart-null off-mac), either ~400-700 lines of objc_msgSend interop (MPNowPlayingInfoCenter + MPRemoteCommandCenter, block trampolines are the hard part — repo has zero objc interop today) or a bundled Swift helper (+CI signing step). Hardware-bound verification (real mac, media keys, Control Center).

## Phase 3 (dependency changes — excluded from this phase by rule)

- H7, H8 (VideoLAN.LibVLC.Mac ghost pin), M25 (TagLibSharp), M26 (.NET 8 EOL retarget), L26 (Microsoft.Data.Sqlite 8.0.29), L27 (xunit v2).
