namespace Noctis.Models;

/// <summary>
/// Application settings, persisted to settings.json in %APPDATA%\Noctis\.
/// </summary>
public class AppSettings
{
    /// <summary>Directories to scan for music files.</summary>
    public List<string> MusicFolders { get; set; } = new();

    /// <summary>File paths explicitly removed from the library. Skipped during rescans.</summary>
    public List<string> ExcludedFilePaths { get; set; } = new();

    /// <summary>Persistent include/exclude rules for scanning.</summary>
    public List<FolderRule> FolderRules { get; set; } = new();

    /// <summary>Directory names to ignore while scanning (case-insensitive).</summary>
    public List<string> IgnoredFolderNames { get; set; } = new() { ".git", "node_modules", "$recycle.bin", "system volume information" };

    /// <summary>Current theme: "Gray", "Dark", "MidnightBlack", "Light", or "System".</summary>
    public string Theme { get; set; } = "Gray";

    /// <summary>
    /// Marker for the v2 theme migration. In v1, "Dark" denoted today's Gray colours.
    /// On first load post-upgrade, a stored "Dark" with this flag false is rewritten to "Gray"
    /// so existing users keep their visual; future explicit "Dark" picks set this true and
    /// are honoured as the new dark theme.
    /// </summary>
    public bool ThemeV2Migrated { get; set; } = false;

    /// <summary>Display name shown in the Settings profile section.</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>Username/handle shown beneath the profile name.</summary>
    public string ProfileUsername { get; set; } = string.Empty;

    /// <summary>Absolute path to the user's avatar image, or empty for the default placeholder.</summary>
    public string ProfileAvatarPath { get; set; } = string.Empty;

    /// <summary>Accent preset name ("Crimson", "Sunset", ..., or "Custom").</summary>
    public string AccentPresetName { get; set; } = "Crimson";

    /// <summary>Active accent colour as #RRGGBB hex (mirrors the preset; honoured when preset is "Custom").</summary>
    public string AccentColorHex { get; set; } = "#E74856";

    /// <summary>User-defined custom themes selectable from the Themes row.</summary>
    public List<CustomThemeDefinition> CustomThemes { get; set; } = new();

    /// <summary>Last volume level (0–100).</summary>
    public int Volume { get; set; } = 75;

    /// <summary>Whether to scan for new/changed music files on startup.</summary>
    public bool ScanOnStartup { get; set; } = true;

    /// <summary>
    /// Whether to continuously watch the media folders and update the library in
    /// near-real-time as files are added/removed/changed (FileSystemWatcher).
    /// </summary>
    public bool WatchFoldersEnabled { get; set; } = true;

    /// <summary>
    /// Folder/filename template for the auto-organize tool. Tokens:
    /// {AlbumArtist} {Artist} {Album} {Title} {TrackNo} {DiscNo} {Year} {Genre}.
    /// </summary>
    public string OrganizePattern { get; set; } = "{AlbumArtist}/{Album}/{TrackNo} {Title}";

    /// <summary>Destination root for organized files. Empty = first media folder.</summary>
    public string OrganizeTargetRoot { get; set; } = string.Empty;

    /// <summary>AcoustID application API key for fingerprint metadata lookups. Empty = disabled.</summary>
    public string AcoustIdApiKey { get; set; } = string.Empty;

    /// <summary>Optional explicit path to the Chromaprint <c>fpcalc</c> binary. Empty = search PATH.</summary>
    public string FpcalcPath { get; set; } = string.Empty;

    /// <summary>When true, the in-app updater also offers GitHub pre-releases. Off = stable channel only.</summary>
    public bool IncludePrereleaseUpdates { get; set; } = false;

    /// <summary>Default page index (0=Home, 1=Songs, 2=Albums, 3=Artists, 4=Genres, 5=Playlists, 6=Favorites).</summary>
    public int DefaultPageIndex { get; set; } = 0;

    /// <summary>Configured remote/local source connections.</summary>
    public List<SourceConnection> SourceConnections { get; set; } = new();

    /// <summary>Window dimensions and position for restore on next launch.</summary>
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public double WindowX { get; set; } = 100;
    public double WindowY { get; set; } = 100;
    public string MainWindowState { get; set; } = "Normal";

    // ── Playback settings ──

    /// <summary>Whether crossfade between tracks is enabled.</summary>
    public bool CrossfadeEnabled { get; set; }

    /// <summary>Crossfade duration in seconds (1–12, fractional allowed).</summary>
    public double CrossfadeDuration { get; set; } = 6;

    /// <summary>Master toggle for Apple-style song transitions (drives AutoMixTransitionMode).</summary>
    public bool SongTransitionsEnabled { get; set; }
    /// <summary>Transition style when enabled: "AutoMix" (key/tempo aware) or "Crossfade" (fixed duration).</summary>
    public string TransitionStyle { get; set; } = "Crossfade";
    /// <summary>AutoMix strength: "Subtle", "Balanced", or "Extended".</summary>
    public string SongTransitionStrength { get; set; } = "Balanced";
    /// <summary>Whether AutoMix beat-matches when BPM/key metadata is available.</summary>
    public bool SongTransitionBeatMatch { get; set; } = true;

    /// <summary>Whether loudness normalization (Sound Check) is enabled.</summary>
    public bool SoundCheckEnabled { get; set; }

    /// <summary>Windows only: WASAPI exclusive-mode output for bit-perfect playback.</summary>
    public bool ExclusiveAudioEnabled { get; set; }

    /// <summary>Gapless playback: natural track changes hand off to a pre-decoded
    /// standby player instead of an audible stop/start. On by default.</summary>
    public bool GaplessPlaybackEnabled { get; set; } = true;

    /// <summary>Whether long playback-bar track titles should scroll while playing.</summary>
    public bool TrackTitleMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long playback-bar artist names should scroll while playing.</summary>
    public bool ArtistMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long track titles in the CoverFlow view should scroll.</summary>
    public bool CoverFlowMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long artist names in the CoverFlow view should scroll.</summary>
    public bool CoverFlowArtistMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long album titles in the CoverFlow view should scroll.</summary>
    public bool CoverFlowAlbumMarqueeEnabled { get; set; } = true;

    /// <summary>Whether animated cover art (looping MP4/WebM) plays for the currently playing track.</summary>
    public bool EnableAnimatedCovers { get; set; } = true;

    /// <summary>Whether the playback bar shows a waveform seekbar (requires ffmpeg;
    /// falls back to the plain slider while the waveform generates).</summary>
    public bool WaveformSeekbarEnabled { get; set; }

    /// <summary>Opacity of the playback bar's glass fill (0 = fully transparent, 1 = solid).
    /// Controls only the background, not the bar's text/controls. Default 0.4 matches the
    /// original #66 alpha glass look.</summary>
    public double PlaybackBarBackgroundOpacity { get; set; } = 0.4;

    /// <summary>Whether the sidebar expands on hover (with its slide animation). When false the
    /// sidebar stays in the icon-only rail and never expands.</summary>
    public bool SidebarHoverExpand { get; set; } = true;

    /// <summary>When true, the album grids collapse multiple editions/issues of the same release
    /// (same album-artist + normalized base title) into a single representative tile. Hidden
    /// editions remain reachable via the album page's "Other Versions" section. Default off.</summary>
    public bool CollapseAlbumEditions { get; set; } = false;

    /// <summary>Whether long track titles in the Lyrics page should scroll.</summary>
    public bool LyricsTitleMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long artist/album names in the Lyrics page should scroll.</summary>
    public bool LyricsArtistMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long track titles in the mini player should scroll.</summary>
    public bool MiniPlayerTitleMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long album titles in the mini player should scroll.</summary>
    public bool MiniPlayerAlbumMarqueeEnabled { get; set; } = true;

    // ── Equalizer settings ──

    /// <summary>Whether the advanced equalizer is enabled.</summary>
    public bool EqualizerEnabled { get; set; } = true;

    /// <summary>Selected EQ preset index. -1 = Custom, 0+ = VLC built-in preset.</summary>
    public int EqualizerPresetIndex { get; set; } = 0; // 0 = VLC "Flat" preset

    /// <summary>Legacy 10-band graphic amplitudes in dB (-12 to +12). Still read to
    /// migrate pre-parametric settings and written as a downgrade-safe mirror of
    /// the applied curve; <see cref="ParametricEqBands"/> is the source of truth.</summary>
    public float[] EqualizerBands { get; set; } = new float[10];

    /// <summary>Parametric EQ bands (frequency / gain / Q). Null on settings files
    /// written before the parametric EQ existed — migrated from
    /// <see cref="EqualizerBands"/> on first load.</summary>
    public List<ParametricEqBand>? ParametricEqBands { get; set; }

    // ── Integration settings ──

    /// <summary>Whether Discord Rich Presence is enabled.</summary>
    public bool DiscordRichPresenceEnabled { get; set; }

    /// <summary>Loon server URL for Discord cover art (e.g. "wss://loon.example.com").</summary>
    public string LoonServerUrl { get; set; } = "http://noctis-loon.duckdns.org";

    /// <summary>Whether Last.fm scrobbling is enabled.</summary>
    public bool LastFmScrobblingEnabled { get; set; } = true;

    /// <summary>Last.fm session key for authenticated API calls.</summary>
    public string LastFmSessionKey { get; set; } = "";

    /// <summary>Last.fm username (populated after successful auth).</summary>
    public string LastFmUsername { get; set; } = "";

    /// <summary>Whether ListenBrainz scrobbling is enabled (independent of Last.fm).</summary>
    public bool ListenBrainzScrobblingEnabled { get; set; } = true;

    /// <summary>ListenBrainz user token (single-string credential pasted by the user from listenbrainz.org/profile/).</summary>
    public string ListenBrainzToken { get; set; } = "";

    /// <summary>ListenBrainz username (populated after a successful validate-token call).</summary>
    public string ListenBrainzUsername { get; set; } = "";

    /// <summary>
    /// Internal metadata schema version used for one-time library backfills
    /// when parsing rules are improved (for example explicit tag detection).
    /// </summary>
    public int MetadataSchemaVersion { get; set; }

    // ── Cache / Offline ──

    /// <summary>Enable stream-first on-demand cache.</summary>
    public bool StreamFirstEnabled { get; set; } = true;

    /// <summary>Enable automatic caching of recently streamed tracks.</summary>
    public bool AutoCacheEnabled { get; set; } = true;

    /// <summary>Maximum cache size in MB before eviction starts.</summary>
    public int OfflineCacheLimitMb { get; set; } = 4096;

    // ── Lyrics view ──

    /// <summary>Custom background color hex for lyrics view (empty = auto from album art).</summary>
    public string LyricsBackgroundColorHex { get; set; } = "";

    /// <summary>When true, the lyrics page shows the blurred album artwork as background.
    /// When false, the chosen solid/gradient color shows through instead.</summary>
    public bool LyricsShowArtworkBackground { get; set; } = true;

    // ── Lyrics providers ──

    /// <summary>Whether LRCLIB online lyrics search is enabled.</summary>
    public bool LrcLibEnabled { get; set; } = true;

    /// <summary>Whether NetEase Cloud Music online lyrics search is enabled.</summary>
    public bool NetEaseEnabled { get; set; } = true;

    // ── Audio Converter ──

    /// <summary>Override path to ffmpeg. Empty = auto-detect (app dir, then PATH).</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    // ── ReplayGain ──

    /// <summary>"Off", "Track", "Album", or "Auto" (album when same-album sequence else track).
    /// On by default ("Auto") for fresh installs; stored values are respected.</summary>
    public string ReplayGainMode { get; set; } = "Auto";

    /// <summary>Pre-amp in dB applied on top of the RG tag value.</summary>
    public double ReplayGainPreampDb { get; set; } = 0.0;

    // ── Audio analysis (BPM / key) ──

    /// <summary>Whether background BPM + musical-key analysis runs (scan-time + backfill).</summary>
    public bool BpmKeyAnalysisEnabled { get; set; } = true;

    /// <summary>When true, computed BPM/key are also written to file tags (TBPM/TKEY). Off by default.</summary>
    public bool WriteAnalysisToTags { get; set; } = false;
}
