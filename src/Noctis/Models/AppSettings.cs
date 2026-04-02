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

    /// <summary>Current theme: "Dark", "Light", or "System".</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Last volume level (0–100).</summary>
    public int Volume { get; set; } = 75;

    /// <summary>Whether to scan for new/changed music files on startup.</summary>
    public bool ScanOnStartup { get; set; } = true;

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

    /// <summary>Whether the sound enhancer is enabled.</summary>
    public bool SoundEnhancerEnabled { get; set; }

    /// <summary>Sound enhancer intensity level (0–100).</summary>
    public int SoundEnhancerLevel { get; set; } = 50;

    /// <summary>Whether loudness normalization (Sound Check) is enabled.</summary>
    public bool SoundCheckEnabled { get; set; }

    /// <summary>Whether long playback-bar track titles should scroll while playing.</summary>
    public bool TrackTitleMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long playback-bar artist names should scroll while playing.</summary>
    public bool ArtistMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long track titles in options menus should scroll.</summary>
    public bool MenuTitleMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long artist names in options menus should scroll.</summary>
    public bool MenuArtistMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long track titles in the CoverFlow view should scroll.</summary>
    public bool CoverFlowMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long artist names in the CoverFlow view should scroll.</summary>
    public bool CoverFlowArtistMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long album titles in the CoverFlow view should scroll.</summary>
    public bool CoverFlowAlbumMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long track titles in the Lyrics page should scroll.</summary>
    public bool LyricsTitleMarqueeEnabled { get; set; } = true;

    /// <summary>Whether long artist/album names in the Lyrics page should scroll.</summary>
    public bool LyricsArtistMarqueeEnabled { get; set; } = true;

    // ── Equalizer settings ──

    /// <summary>Whether the advanced equalizer is enabled.</summary>
    public bool EqualizerEnabled { get; set; } = true;

    /// <summary>Selected EQ preset index. -1 = Custom, 0+ = VLC built-in preset.</summary>
    public int EqualizerPresetIndex { get; set; } = 0; // 0 = VLC "Flat" preset

    /// <summary>Custom band amplitudes in dB (-12 to +12), one per VLC EQ band.</summary>
    public float[] EqualizerBands { get; set; } = new float[10];

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

    // ── Lyrics providers ──

    /// <summary>Whether LRCLIB online lyrics search is enabled.</summary>
    public bool LrcLibEnabled { get; set; } = true;

    /// <summary>Whether NetEase Cloud Music online lyrics search is enabled.</summary>
    public bool NetEaseEnabled { get; set; } = true;
}

