using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Noctis.Models;

/// <summary>
/// Represents a single audio track in the library.
/// All metadata is populated from TagLib# during library scanning.
/// </summary>
public partial class Track : ObservableObject
{
    /// <summary>Stable unique identifier, generated on first scan.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Absolute filesystem path to the audio file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Track title from ID3/Vorbis tag. Falls back to filename if tag is missing.</summary>
    public string Title
    {
        get => _title;
        set { _title = value; _searchTitleKey = null; }
    }
    private string _title = string.Empty;

    /// <summary>Performing artist (TPE1 / ARTIST tag).</summary>
    public string Artist
    {
        get => _artist;
        set { _artist = value; _searchArtistKey = null; _primaryArtist = null; }
    }
    private string _artist = "Unknown Artist";

    /// <summary>Album artist (TPE2), used to group "Various Artists" compilations.</summary>
    public string AlbumArtist { get; set; } = "Unknown Artist";

    /// <summary>Album name (TALB tag).</summary>
    public string Album
    {
        get => _album;
        set { _album = value; _searchAlbumKey = null; }
    }
    private string _album = "Unknown Album";

    private string? _searchTitleKey;
    private string? _searchArtistKey;
    private string? _searchAlbumKey;

    /// <summary>
    /// Lazily cached <see cref="Noctis.Helpers.SearchText.Normalize"/> key for Title.
    /// Search used to re-normalize Title/Artist/Album twice per matching track per
    /// keystroke — hundreds of thousands of throwaway strings at 100k tracks. Costs
    /// three short strings per track; the setters above invalidate on metadata edits.
    /// </summary>
    [JsonIgnore] public string SearchTitleKey => _searchTitleKey ??= Helpers.SearchText.Normalize(_title);

    /// <summary>Lazily cached normalized search key for Artist. See <see cref="SearchTitleKey"/>.</summary>
    [JsonIgnore] public string SearchArtistKey => _searchArtistKey ??= Helpers.SearchText.Normalize(_artist);

    /// <summary>Lazily cached normalized search key for Album. See <see cref="SearchTitleKey"/>.</summary>
    [JsonIgnore] public string SearchAlbumKey => _searchAlbumKey ??= Helpers.SearchText.Normalize(_album);

    /// <summary>Genre tag value.</summary>
    public string Genre { get; set; } = string.Empty;

    /// <summary>Track number within the disc (TRCK tag).</summary>
    public int TrackNumber { get; set; }

    /// <summary>Disc number for multi-disc albums.</summary>
    public int DiscNumber { get; set; } = 1;

    /// <summary>Release year.</summary>
    public int Year { get; set; }

    /// <summary>Full release date string from RELEASETIME/TDRL tag (e.g., "2014-10-27" or "2014-10-27T00:00:00Z").</summary>
    public string ReleaseDate { get; set; } = string.Empty;

    /// <summary>Track duration as reported by TagLib#.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Computed album identifier: deterministic hash of AlbumArtist + Album.
    /// Allows grouping tracks into albums without a separate album table.
    /// </summary>
    public Guid AlbumId { get; set; }

    /// <summary>File size in bytes, used for change detection during rescan.</summary>
    public long FileSize { get; set; }

    /// <summary>Last-modified timestamp from the filesystem, used for incremental scanning.</summary>
    public DateTime LastModified { get; set; }

    /// <summary>Whether the track is marked as explicit (ITUNESADVISORY=1).</summary>
    [ObservableProperty]
    private bool _isExplicit;

    /// <summary>Source type where this track came from.</summary>
    public SourceType SourceType { get; set; } = SourceType.Local;

    /// <summary>Source-side identifier (e.g., Navidrome track ID). Empty for local-only tracks.</summary>
    public string SourceTrackId { get; set; } = string.Empty;

    /// <summary>Source connection identifier for remote tracks.</summary>
    public string SourceConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// True for tracks streamed from a media server (FilePath is an http(s) URL,
    /// not a local file). File-only paths — tag writes, sidecars, file operations —
    /// must no-op for these.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsRemoteStream => SourceType is SourceType.Navidrome or SourceType.Jellyfin or SourceType.Plex;

    /// <summary>Timestamp of when this track was first discovered by a library scan.</summary>
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    /// <summary>Transient flag: true when the track was just drag-and-drop imported this session.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsRecentImport { get; set; }

    /// <summary>Transient flag: true when this track is the one currently loaded in the player.
    /// Drives the now-playing row highlight in flat track lists. Not persisted.</summary>
    [ObservableProperty]
    [property: System.Text.Json.Serialization.JsonIgnore]
    private bool _isNowPlaying;

    /// <summary>Transient 1-based position within the list view currently displaying this track
    /// (set by the Folders pane). Drives the leading row-number column. Not persisted.</summary>
    [ObservableProperty]
    [property: System.Text.Json.Serialization.JsonIgnore]
    private int _rowNumber;

    // ── Extended metadata ──

    /// <summary>Composer(s) of the track.</summary>
    public string Composer { get; set; } = string.Empty;

    /// <summary>Total number of tracks on the disc.</summary>
    public int TrackCount { get; set; }

    /// <summary>Total number of discs in the album.</summary>
    public int DiscCount { get; set; } = 1;

    /// <summary>Beats per minute (TBPM tag). 0 = unset. Observable so the background
    /// tempo/key backfill lights up the Songs BPM column without a full library refresh.</summary>
    [ObservableProperty]
    private int _bpm;

    /// <summary>Musical key tag value (TKEY / INITIALKEY / KEY). Empty = unset. Observable
    /// for the same reason as <see cref="Bpm"/>.</summary>
    [ObservableProperty]
    private string _musicalKey = string.Empty;

    // ── Lyrics (lazy, store-backed) ──
    //
    // These two strings used to be plain persisted properties, which made them
    // 56% of a measured 46 MB library.json (5,891 tracks) and permanently
    // resident in RAM for every track. They are now [JsonIgnore]d and backed by
    // Services.LyricsStore (one small file per track id, bounded LRU): the
    // getter reads through lazily, the setter records a pending value that
    // PersistenceService flushes to the store on every library save (writes
    // were already only persisted at the next library save, so the timing is
    // unchanged). A track with no store attached (unit tests, freshly scanned
    // tracks before their first save) behaves exactly like the old in-memory
    // string properties via the pending-override fields.

    private Services.LyricsStore? _lyricsStore;
    private string? _lyricsOverride;          // set this session, pending commit
    private string? _syncedLyricsOverride;
    private string? _legacyLyrics;            // parsed from pre-store library.json
    private string? _legacySyncedLyrics;
    private bool _lyricsAssigned;

    /// <summary>Plain/unsynced lyrics text for the track. Lazy: first read on a
    /// store-attached track loads from disk (sync by design — the getter has too
    /// many consumers to make async; hot paths read it off the UI thread).</summary>
    [JsonIgnore]
    public string Lyrics
    {
        get => _lyricsOverride ?? _legacyLyrics ?? ReadStoredLyrics().Plain;
        set { _lyricsOverride = value ?? string.Empty; _lyricsAssigned = true; }
    }

    /// <summary>Time-synced lyrics in LRC format. See <see cref="Lyrics"/>.</summary>
    [JsonIgnore]
    public string SyncedLyrics
    {
        get => _syncedLyricsOverride ?? _legacySyncedLyrics ?? ReadStoredLyrics().Synced;
        set { _syncedLyricsOverride = value ?? string.Empty; _lyricsAssigned = true; }
    }

    private Services.LyricsStore.LyricsPair ReadStoredLyrics()
        => _lyricsStore?.Read(Id) ?? Services.LyricsStore.LyricsPair.Empty;

    // Set-only shims keep old library.json (which still carries inline lyrics)
    // deserializing: they capture the legacy values for the one-time migration
    // below. Having no getter, they are never serialized, so every save emits
    // lyric-free JSON.
    [JsonPropertyName("lyrics")]
    public string? LegacyLyricsFromJson { set => _legacyLyrics = value; }

    [JsonPropertyName("syncedLyrics")]
    public string? LegacySyncedLyricsFromJson { set => _legacySyncedLyrics = value; }

    /// <summary>
    /// One-time migration hook, called per track while the library streams in.
    /// Moves inline lyrics from the old JSON into the store (write-if-absent —
    /// an existing store entry is newer by construction and must win) and
    /// releases the in-memory copies. Crash-safe: library.json keeps its inline
    /// lyrics until the first save after a fully migrated load, so an
    /// interrupted migration simply continues on the next launch.
    /// </summary>
    public void MigrateLegacyLyricsToStore(Services.LyricsStore store)
    {
        _lyricsStore = store;
        var plain = _legacyLyrics ?? string.Empty;
        var synced = _legacySyncedLyrics ?? string.Empty;
        _legacyLyrics = null;
        _legacySyncedLyrics = null;
        if (plain.Length == 0 && synced.Length == 0) return;
        try
        {
            store.WriteIfAbsent(Id, plain, synced);
        }
        catch
        {
            // Store write failed (disk full, permissions): keep the values as a
            // pending edit so the commit on every library save retries — the
            // lyrics must not be lost when the next save emits lyric-free JSON.
            _lyricsOverride = plain;
            _syncedLyricsOverride = synced;
            _lyricsAssigned = true;
        }
    }

    /// <summary>
    /// Flushes a pending lyric edit to the store; called for every track on
    /// every library save, before the (lyric-free) JSON hits disk. No-op unless
    /// a setter ran since the last commit. An assigned all-empty pair deletes
    /// the store entry (RemoveLyrics), matching the old field-clear semantics.
    /// </summary>
    public void CommitLyricsToStore(Services.LyricsStore store)
    {
        _lyricsStore ??= store;
        if (!_lyricsAssigned) return;
        try
        {
            var current = store.Read(Id);
            var plain = _lyricsOverride ?? current.Plain;
            var synced = _syncedLyricsOverride ?? current.Synced;
            if (plain != current.Plain || synced != current.Synced)
                store.Write(Id, plain, synced);
            _lyricsOverride = null;
            _syncedLyricsOverride = null;
            _lyricsAssigned = false;
        }
        catch
        {
            // Keep the pending flags: the edit stays readable in memory and the
            // next library save retries the flush.
        }
    }

    /// <summary>
    /// Call BEFORE reassigning <see cref="Id"/> (file relocation recomputes the
    /// path-derived id). Lifts the currently stored lyrics into pending values
    /// so the next commit re-files them under the new id.
    /// </summary>
    public void PrepareLyricsForIdChange()
    {
        if (_lyricsStore == null) return;
        var current = _lyricsStore.Read(Id);
        _lyricsOverride ??= _legacyLyrics ?? current.Plain;
        _syncedLyricsOverride ??= _legacySyncedLyrics ?? current.Synced;
        if (_lyricsOverride.Length > 0 || _syncedLyricsOverride.Length > 0)
            _lyricsAssigned = true;
    }

    /// <summary>Whether this track is part of a compilation album.</summary>
    public bool IsCompilation { get; set; }

    /// <summary>
    /// Album release classification for this track's release. Auto-detected from tags
    /// during scan; persisted with the library so the value survives without re-reading
    /// the file. <see cref="IsReleaseTypeOverridden"/> distinguishes a user override
    /// from an auto-detected value.
    /// </summary>
    public ReleaseType ReleaseType { get; set; } = ReleaseType.Album;

    /// <summary>True when the user has explicitly set the release type from the
    /// metadata Options tab; suppresses auto-detection on subsequent scans.</summary>
    public bool IsReleaseTypeOverridden { get; set; }

    /// <summary>True when ReleaseType was populated from a real tag value (RELEASETYPE,
    /// MUSICBRAINZ_ALBUM_TYPE, or the iTunes album-name suffix). Distinguishes an
    /// explicit "Album" from the unset default so the album-grouping fallback in
    /// <see cref="Services.LibraryService"/> can apply the track-count heuristic
    /// only when no tag information was present.</summary>
    public bool ReleaseTypeFromTag { get; set; }

    /// <summary>Grouping tag (e.g., a sub-genre or classical work grouping).</summary>
    public string Grouping { get; set; } = string.Empty;

    /// <summary>If true, library views should show the composer alongside the artist for this track.</summary>
    public bool ShowComposerInAllViews { get; set; }

    /// <summary>If true, this track is part of a classical Work with Movements; library views should prefer Work/Movement display over Title.</summary>
    public bool UseWorkAndMovement { get; set; }

    /// <summary>Name of the Work this track belongs to (e.g., "Symphony No. 9").</summary>
    public string WorkName { get; set; } = string.Empty;

    /// <summary>Title of the Movement (e.g., "Allegro ma non troppo").</summary>
    public string MovementName { get; set; } = string.Empty;

    /// <summary>Movement number within the Work (1-based).</summary>
    public int MovementNumber { get; set; }

    /// <summary>Total number of Movements in the Work.</summary>
    public int MovementCount { get; set; }

    /// <summary>If true, skip this track during shuffle playback.</summary>
    public bool SkipWhenShuffling { get; set; }

    /// <summary>If true, remember the playback position when switching away.</summary>
    public bool RememberPlaybackPosition { get; set; }

    /// <summary>Media kind classification for this track.</summary>
    public string MediaKind { get; set; } = "Music";

    /// <summary>Custom start time in milliseconds. 0 = disabled (play from beginning).</summary>
    public long StartTimeMs { get; set; }

    /// <summary>Custom stop time in milliseconds. 0 = disabled (play to end).</summary>
    public long StopTimeMs { get; set; }

    /// <summary>Per-track volume adjustment (-100 to +100). 0 = no adjustment.</summary>
    public int VolumeAdjust { get; set; }

    /// <summary>Per-track EQ preset name. Empty = use global setting.</summary>
    public string EqPreset { get; set; } = string.Empty;

    /// <summary>Saved playback position in milliseconds (for RememberPlaybackPosition).</summary>
    public long SavedPositionMs { get; set; }

    /// <summary>Number of times this track has been played.</summary>
    [ObservableProperty]
    private int _playCount;

    /// <summary>Date and time when this track was last played.</summary>
    public DateTime? LastPlayed { get; set; }

    /// <summary>User rating from 0 to 5 stars. Observable so star displays update live.</summary>
    [ObservableProperty]
    private int _rating;

    /// <summary>Apple Music-style "not liked" flag (suggest less of this).</summary>
    [ObservableProperty]
    private bool _isDisliked;

    /// <summary>When set and in the future, the track is hidden from shuffle and radio until this time.</summary>
    public DateTime? SnoozedUntil { get; set; }
    /// <summary>True when the track is currently snoozed (SnoozedUntil in the future).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSnoozed => SnoozedUntil is { } until && until > DateTime.UtcNow;

    /// <summary>Whether this track is marked as a favorite.</summary>
    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>UTC timestamp when this track was last favorited. Null if never favorited.</summary>
    public DateTime? FavoritedAt { get; set; }

    partial void OnIsFavoriteChanged(bool value)
    {
        if (value)
        {
            // Only stamp when no timestamp exists: JSON deserialization replays
            // IsFavorite=true on every library load, and unconditionally stamping
            // here overwrote every favorite's history with load time (the
            // favorites grid then scattered after each restart). The persisted
            // FavoritedAt may be applied before or after this setter; ??= is
            // correct in both orders.
            FavoritedAt ??= DateTime.UtcNow;
        }
        else
        {
            // Clearing on unfavorite makes a later re-favorite stamp fresh.
            FavoritedAt = null;
        }
    }

    /// <summary>
    /// Raises a change notification for every property. Most metadata fields are
    /// plain non-notifying properties (they only change via the metadata editor),
    /// so views bound directly to a Track instance call-site-refresh through this
    /// after a save instead of requiring a full list rebuild.
    /// </summary>
    public void NotifyMetadataUpdated() => OnPropertyChanged(string.Empty);

    /// <summary>Cached album artwork path, populated from album data during index build. Not persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AlbumArtworkPath { get; set; }

    /// <summary>Whether this track has album artwork available.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAlbumArt => !string.IsNullOrEmpty(AlbumArtworkPath);

    /// <summary>User comment or notes about the track.</summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>Copyright notice from file tags (e.g., "℗ 2014 Taylor Swift").</summary>
    public string Copyright { get; set; } = string.Empty;

    // ── Audio quality properties ──

    /// <summary>Audio bitrate in kbps.</summary>
    public int Bitrate { get; set; }

    /// <summary>Sample rate in Hz (e.g., 44100, 48000).</summary>
    public int SampleRate { get; set; }

    /// <summary>Bits per sample (e.g., 16, 24).</summary>
    public int BitsPerSample { get; set; }

    /// <summary>Audio codec description from TagLib# (e.g., "FLAC", "Apple Lossless", "MPEG Audio Layer 3").</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Canonical album-artist for Various-Artists compilations.</summary>
    public const string VariousArtists = "Various Artists";

    /// <summary>
    /// Resolves the effective album-artist used for both grouping and display.
    /// An explicit album-artist tag always wins (it already groups correctly).
    /// Otherwise a compilation-flagged track is filed under <see cref="VariousArtists"/>
    /// so a Various-Artists release stays as one album instead of fragmenting into
    /// one album per performer; non-compilation tracks fall back to the performer.
    /// Mirrors iTunes/Apple Music behavior.
    /// </summary>
    public static string ResolveAlbumArtist(string? explicitAlbumArtist, string? performer, bool isCompilation)
    {
        if (!string.IsNullOrWhiteSpace(explicitAlbumArtist))
            return explicitAlbumArtist;
        if (isCompilation)
            return VariousArtists;
        if (!string.IsNullOrWhiteSpace(performer))
            return performer;
        return "Unknown Artist";
    }

    /// <summary>
    /// Generates a deterministic album ID from AlbumArtist and Album name.
    /// </summary>
    public static Guid ComputeAlbumId(string albumArtist, string album)
    {
        // Deterministic GUID v5 using a simple hash approach
        var key = $"{albumArtist.Trim().ToLowerInvariant()}::{album.Trim().ToLowerInvariant()}";
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }

    /// <summary>
    /// The degenerate album bucket every track without a real album identity falls
    /// into. It is shared library-wide — anything keyed by AlbumId (cached artwork,
    /// most notably) must never treat it as a real album, because data attached to
    /// it surfaces on every untagged track at once. ComputeAlbumId lowercases, so
    /// this one GUID covers any casing of the placeholder names.
    /// </summary>
    public static readonly Guid UnknownAlbumBucketId = ComputeAlbumId("Unknown Artist", "Unknown Album");

    /// <summary>
    /// True when <paramref name="album"/> is a real album name — not blank and not the
    /// "Unknown Album" placeholder. The placeholder must be matched by VALUE, not just
    /// emptiness: files exported by other tools arrive with a literal
    /// <c>TALB=Unknown Album</c> tag (seen in the field), which lands in the same
    /// shared bucket as a missing tag.
    /// </summary>
    public static bool IsRealAlbumName(string? album) =>
        !string.IsNullOrWhiteSpace(album) &&
        !album.Trim().Equals("Unknown Album", StringComparison.OrdinalIgnoreCase);

    /// <summary>Primary display artist derived from the first credited artist token.
    /// Lazily cached like <see cref="SearchArtistKey"/>: the uncached regex-backed parse
    /// ran once per track per index rebuild, which repeats every ~1.5 s during scans.
    /// The Artist setter invalidates, covering the Merge Featured toggle rewriting
    /// Artist at runtime.</summary>
    [JsonIgnore]
    public string PrimaryArtist => _primaryArtist ??= GetPrimaryArtist(Artist);
    private string? _primaryArtist;

    // Everything below this point is computed from the persisted fields above. All of it
    // is [JsonIgnore]d: none of these have setters, so they can never round-trip, and
    // serializing them roughly doubled the size of library.json (AudioQualityDescription
    // alone emitted a 65-character English sentence per track).

    /// <summary>Formatted duration string (m:ss or h:mm:ss).</summary>
    [JsonIgnore]
    public string DurationFormatted =>
        Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");

    /// <summary>Formatted bitrate string.</summary>
    [JsonIgnore]
    public string BitrateFormatted => Bitrate > 0 ? $"{Bitrate} kbps" : "N/A";

    /// <summary>Formatted sample rate string.</summary>
    [JsonIgnore]
    public string SampleRateFormatted => SampleRate > 0 ? $"{SampleRate / 1000.0:#.###} kHz" : "N/A";

    /// <summary>Formatted bits per sample string.</summary>
    [JsonIgnore]
    public string BitsPerSampleFormatted => BitsPerSample > 0 ? $"{BitsPerSample} bit" : "N/A";

    // ── Lossless detection ──

    /// <summary>
    /// Whether this track's format is lossless, determined by codec analysis.
    /// Uses the actual codec string from TagLib# for M4A/MP4 containers
    /// (ALAC = lossless, AAC = lossy), with file extension as fallback.
    /// </summary>
    [JsonIgnore]
    public bool IsLossless
    {
        get
        {
            // Check codec string first (most reliable for container formats like M4A)
            var codecLower = (Codec ?? string.Empty).ToLowerInvariant();
            if (codecLower.Contains("flac") || codecLower.Contains("alac") ||
                codecLower.Contains("lossless") || codecLower.Contains("pcm") ||
                codecLower.Contains("wavpack") || codecLower.Contains("monkey"))
                return true;

            // Fall back to file extension for explicitly lossless formats
            var ext = Path.GetExtension(FilePath).ToLowerInvariant();
            return ext switch
            {
                ".flac" or ".wav" or ".aiff" or ".aif" or ".aifc" or ".ape" or ".wv" or ".alac" => true,
                // M4A/MP4 should only be considered lossless when codec parsing identifies ALAC.
                ".m4a" or ".mp4" => codecLower.Contains("alac") || codecLower.Contains("lossless"),
                _ => false
            };
        }
    }

    /// <summary>
    /// Whether this track is Hi-Res Lossless.
    /// Hi-Res Lossless: 24-bit at sample rates above 48 kHz (88.2, 96, 176.4, 192 kHz).
    /// 24-bit/48 kHz and below is standard Lossless, not Hi-Res.
    /// </summary>
    [JsonIgnore]
    public bool IsHiResLossless =>
        IsLossless &&
        BitsPerSample >= 24 &&
        SampleRate > 48000;

    /// <summary>Audio quality badge text: "Lossless", "Hi-Res Lossless",
    /// or the short codec name for lossy formats (e.g. "AAC", "MP3").</summary>
    [JsonIgnore]
    public string AudioQualityBadge
    {
        get
        {
            if (IsHiResLossless) return "Hi-Res Lossless";
            if (IsLossless) return "Lossless";
            return CodecShortName;
        }
    }

    /// <summary>Tooltip sentence explaining the badge kind; empty when no badge.</summary>
    [JsonIgnore]
    public string AudioQualityDescription
    {
        get
        {
            if (IsLossless) return LosslessDescription;
            if (CodecShortName.Length > 0) return LossyDescription;
            return string.Empty;
        }
    }

    public const string LosslessDescription =
        "Lossless audio preserves more detail from the original recording.";
    public const string LossyDescription =
        "Compressed audio that balances sound quality with smaller file size.";

    /// <summary>Short codec label for badge display (e.g. "FLAC", "ALAC", "AAC", "MP3").</summary>
    [JsonIgnore]
    public string CodecShortName
    {
        get
        {
            var c = (Codec ?? string.Empty).ToLowerInvariant();
            if (c.Contains("flac")) return "FLAC";
            if (c.Contains("alac") || c.Contains("apple lossless")) return "ALAC";
            if (c.Contains("aiff")) return "AIFF";
            if (c.Contains("wavpack")) return "WV";
            if (c.Contains("monkey")) return "APE";
            if (c.Contains("pcm") || c.Contains("wav")) return "WAV";
            if (c.Contains("layer 3") || c.Contains("mp3")) return "MP3";
            if (c.Contains("aac") || c.Contains("mp4a")) return "AAC";
            if (c.Contains("opus")) return "OPUS";
            if (c.Contains("vorbis")) return "OGG";
            if (c.Contains("wma") || c.Contains("windows media")) return "WMA";

            // Fallback to file extension when the codec string matched nothing
            var ext = Path.GetExtension(FilePath).ToLowerInvariant();
            return ext switch
            {
                ".flac" => "FLAC",
                // M4A/MP4 containers default to lossy AAC; only a codec string
                // identifying lossless content means ALAC (mirrors IsLossless).
                ".m4a" or ".mp4" => c.Contains("lossless") ? "ALAC" : "AAC",
                ".wav" => "WAV",
                ".aiff" or ".aif" or ".aifc" => "AIFF",
                ".ape" => "APE",
                ".wv" => "WV",
                ".alac" => "ALAC",
                ".mp3" => "MP3",
                ".aac" => "AAC",
                ".opus" => "OPUS",
                ".ogg" or ".oga" => "OGG",
                ".wma" or ".asf" => "WMA",
                _ => ""
            };
        }
    }

    /// <summary>Detailed audio quality info for tooltip (e.g. "16-bit/96 kHz ALAC").</summary>
    [JsonIgnore]
    public string AudioQualityDetailedInfo => FormatQualityDetail().TrimStart();

    private string FormatQualityDetail()
    {
        var sb = new System.Text.StringBuilder();
        if (!IsLossless && Bitrate > 0)
            sb.Append($" {Bitrate} kbps");
        if (BitsPerSample > 0 && SampleRate > 0)
            sb.Append($" {BitsPerSample}-bit/{SampleRate / 1000.0:0.#}kHz");
        else if (BitsPerSample > 0)
            sb.Append($" {BitsPerSample}-bit");
        else if (SampleRate > 0)
            sb.Append($" {SampleRate / 1000.0:0.#}kHz");

        var codec = CodecShortName;
        if (!string.IsNullOrEmpty(codec))
            sb.Append($" {codec}");

        return sb.ToString();
    }

    /// <summary>
    /// True when a track artist line should be shown in album track rows:
    /// show for collaborations or when the track artist differs from album artist.
    /// </summary>
    [JsonIgnore]
    public bool ShouldShowArtistSubtitleInAlbum
    {
        get
        {
            // Always show the subtitle when the user wants composer visible in all views.
            if (ShowComposerInAllViews && !string.IsNullOrWhiteSpace(Composer))
                return true;

            var trackArtists = ParseArtistTokens(Artist);
            if (trackArtists.Length == 0)
                return false;

            var albumArtists = ParseArtistTokens(AlbumArtist);
            if (albumArtists.Length > 0 && TokensEqual(trackArtists, albumArtists))
                return false;

            if (trackArtists.Length > 1)
                return true;

            if (albumArtists.Length == 0)
                return false;

            return !TokensEqual(trackArtists, albumArtists);
        }
    }

    private static bool TokensEqual(string[] left, string[] right)
    {
        if (left.Length != right.Length)
            return false;

        var set = left.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.SetEquals(right);
    }

    /// <summary>Available media kind values for the Options tab dropdown.</summary>
    public static readonly string[] AvailableMediaKinds = { "Music", "Podcast", "Audiobook", "Voice Memo", "Music Video" };

    public static string GetPrimaryArtist(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var tokens = ParseArtistTokens(value);
        return tokens.Length > 0 ? tokens[0] : value.Trim();
    }

    internal static string[] ParseArtistTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

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

    /// <summary>
    /// Artist text to show in list views. When ShowComposerInAllViews is set and a Composer exists,
    /// returns "Artist — Composer". Falls back to raw Artist otherwise.
    /// </summary>
    [JsonIgnore]
    public string ArtistDisplay =>
        ShowComposerInAllViews && !string.IsNullOrWhiteSpace(Composer)
            ? $"{Artist} \u2014 {Composer}"
            : Artist;

    /// <summary>
    /// Title text to show in list views. When UseWorkAndMovement is set and a Work/Movement exists,
    /// returns "Work: Movement" (or just Work / Movement when one is missing). Falls back to Title.
    /// </summary>
    [JsonIgnore]
    public string TitleDisplay
    {
        get
        {
            if (!UseWorkAndMovement) return Title;

            var hasWork = !string.IsNullOrWhiteSpace(WorkName);
            var hasMovement = !string.IsNullOrWhiteSpace(MovementName);

            if (hasWork && hasMovement) return $"{WorkName}: {MovementName}";
            if (hasWork) return WorkName;
            if (hasMovement) return MovementName;
            return Title;
        }
    }

    public override string ToString() => $"{Artist} - {Title}";
}
