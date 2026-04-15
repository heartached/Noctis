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
    public string Title { get; set; } = string.Empty;

    /// <summary>Performing artist (TPE1 / ARTIST tag).</summary>
    public string Artist { get; set; } = "Unknown Artist";

    /// <summary>Album artist (TPE2), used to group "Various Artists" compilations.</summary>
    public string AlbumArtist { get; set; } = "Unknown Artist";

    /// <summary>Album name (TALB tag).</summary>
    public string Album { get; set; } = "Unknown Album";

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
    public bool IsExplicit { get; set; }

    /// <summary>Source type where this track came from.</summary>
    public SourceType SourceType { get; set; } = SourceType.Local;

    /// <summary>Source-side identifier (e.g., Navidrome track ID). Empty for local-only tracks.</summary>
    public string SourceTrackId { get; set; } = string.Empty;

    /// <summary>Source connection identifier for remote tracks.</summary>
    public string SourceConnectionId { get; set; } = string.Empty;

    /// <summary>Timestamp of when this track was first discovered by a library scan.</summary>
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    /// <summary>Transient flag: true when the track was just drag-and-drop imported this session.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsRecentImport { get; set; }

    // ── Extended metadata ──

    /// <summary>Composer(s) of the track.</summary>
    public string Composer { get; set; } = string.Empty;

    /// <summary>Total number of tracks on the disc.</summary>
    public int TrackCount { get; set; }

    /// <summary>Total number of discs in the album.</summary>
    public int DiscCount { get; set; } = 1;

    /// <summary>Beats per minute (TBPM tag). 0 = unset.</summary>
    public int Bpm { get; set; }

    /// <summary>Plain/unsynced lyrics text for the track.</summary>
    public string Lyrics { get; set; } = string.Empty;

    /// <summary>Time-synced lyrics in LRC format.</summary>
    public string SyncedLyrics { get; set; } = string.Empty;

    /// <summary>Whether this track is part of a compilation album.</summary>
    public bool IsCompilation { get; set; }

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
    public int PlayCount { get; set; }

    /// <summary>Date and time when this track was last played.</summary>
    public DateTime? LastPlayed { get; set; }

    /// <summary>User rating from 0 to 5 stars.</summary>
    public int Rating { get; set; }

    /// <summary>Offline cache state for this track.</summary>
    public OfflineState OfflineState { get; set; } = OfflineState.None;

    /// <summary>Whether this track is marked as a favorite.</summary>
    [ObservableProperty]
    private bool _isFavorite;

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

    /// <summary>Formatted duration string (m:ss or h:mm:ss).</summary>
    public string DurationFormatted =>
        Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");

    /// <summary>Formatted bitrate string.</summary>
    public string BitrateFormatted => Bitrate > 0 ? $"{Bitrate} kbps" : "N/A";

    /// <summary>Formatted sample rate string.</summary>
    public string SampleRateFormatted => SampleRate > 0 ? $"{SampleRate / 1000.0:#.###} kHz" : "N/A";

    /// <summary>Formatted bits per sample string.</summary>
    public string BitsPerSampleFormatted => BitsPerSample > 0 ? $"{BitsPerSample} bit" : "N/A";

    // ── Lossless detection ──

    /// <summary>
    /// Whether this track's format is lossless, determined by codec analysis.
    /// Uses the actual codec string from TagLib# for M4A/MP4 containers
    /// (ALAC = lossless, AAC = lossy), with file extension as fallback.
    /// </summary>
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
    public bool IsHiResLossless =>
        IsLossless &&
        BitsPerSample >= 24 &&
        SampleRate > 48000;

    /// <summary>Audio quality badge text: "Lossless" or "Hi-Res Lossless".</summary>
    public string AudioQualityBadge
    {
        get
        {
            if (IsHiResLossless) return "Hi-Res Lossless";
            if (IsLossless) return "Lossless";
            return string.Empty;
        }
    }

    /// <summary>Short codec label for badge display (e.g. "FLAC", "ALAC", "WAV").</summary>
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

            // Fallback to file extension (only called when IsLossless)
            var ext = Path.GetExtension(FilePath).ToLowerInvariant();
            return ext switch
            {
                ".flac" => "FLAC",
                ".m4a" or ".mp4" => "ALAC",
                ".wav" => "WAV",
                ".aiff" or ".aif" or ".aifc" => "AIFF",
                ".ape" => "APE",
                ".wv" => "WV",
                ".alac" => "ALAC",
                _ => ""
            };
        }
    }

    /// <summary>Detailed audio quality info for tooltip (e.g. "16-bit/96 kHz ALAC").</summary>
    public string AudioQualityDetailedInfo => FormatQualityDetail().TrimStart();

    private string FormatQualityDetail()
    {
        var sb = new System.Text.StringBuilder();
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
