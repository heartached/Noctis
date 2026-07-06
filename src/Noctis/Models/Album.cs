namespace Noctis.Models;

/// <summary>
/// Represents an album, aggregated from tracks sharing the same AlbumId.
/// Not persisted directly — rebuilt from track data on load.
/// </summary>
public class Album
{
    /// <summary>Deterministic ID computed from AlbumArtist + Album name.</summary>
    public Guid Id { get; set; }

    /// <summary>Album title.</summary>
    public string Name { get; set; } = "Unknown Album";

    /// <summary>Album artist. "Various Artists" if tracks have mixed artists.</summary>
    public string Artist { get; set; } = "Unknown Artist";

    /// <summary>Release year (from the first track that has one).</summary>
    public int Year { get; set; }

    /// <summary>Genre (from the first track that has one).</summary>
    public string Genre { get; set; } = "Unknown";

    /// <summary>Number of tracks in this album.</summary>
    public int TrackCount { get; set; }

    /// <summary>Sum of all track durations.</summary>
    public TimeSpan TotalDuration { get; set; }

    /// <summary>
    /// Path to cached artwork file on disk (%APPDATA%\Noctis\artwork\{id}.jpg).
    /// Null if no artwork was found.
    /// </summary>
    public string? ArtworkPath { get; set; }

    /// <summary>Tracks in this album, ordered by disc then track number.</summary>
    public List<Track> Tracks { get; set; } = new();

    /// <summary>Whether all tracks in this album are marked as favorites.</summary>
    public bool IsAllTracksFavorite => Tracks?.Count > 0 && Tracks.All(t => t.IsFavorite);

    /// <summary>Whether at least one track in this album is marked as a favorite.</summary>
    public bool HasFavoriteTrack => Tracks?.Any(t => t.IsFavorite) == true;

    /// <summary>Formatted total duration.</summary>
    public string TotalDurationFormatted =>
        TotalDuration.TotalHours >= 1
            ? $"{(int)TotalDuration.TotalHours}h {TotalDuration.Minutes}m"
            : $"{(int)TotalDuration.TotalMinutes} min";

    /// <summary>Audio quality badge for the album, determined from its tracks:
    /// "Lossless"/"Hi-Res Lossless", or the best lossy track's codec (e.g. "AAC").</summary>
    public string AudioQualityBadge =>
        GetRepresentativeQualityTrack()?.AudioQualityBadge ?? string.Empty;

    /// <summary>Tooltip sentence explaining the badge kind; empty when no badge.</summary>
    public string AudioQualityDescription =>
        GetRepresentativeQualityTrack()?.AudioQualityDescription ?? string.Empty;

    /// <summary>Detailed audio quality info for tooltip (e.g. "16-bit/44.1 kHz FLAC"
    /// or "256 kbps 44.1 kHz AAC" for lossy albums).</summary>
    public string AudioQualityDetailedInfo
    {
        get
        {
            var track = GetRepresentativeQualityTrack();
            if (track == null) return string.Empty;

            var parts = new List<string>();
            if (!track.IsLossless && track.Bitrate > 0)
                parts.Add($"{track.Bitrate} kbps");
            if (track.BitsPerSample > 0 && track.SampleRate > 0)
                parts.Add($"{track.BitsPerSample}-bit/{track.SampleRate / 1000.0:0.###} kHz");
            else if (track.BitsPerSample > 0)
                parts.Add($"{track.BitsPerSample}-bit");
            else if (track.SampleRate > 0)
                parts.Add($"{track.SampleRate / 1000.0:0.###} kHz");

            var codec = track.CodecShortName;
            if (!string.IsNullOrEmpty(codec))
                parts.Add(codec);

            return string.Join(" ", parts);
        }
    }

    /// <summary>Gets the representative track for quality display:
    /// Hi-Res Lossless > Lossless > best (highest-bitrate) badged lossy track.</summary>
    private Track? GetRepresentativeQualityTrack()
    {
        if (Tracks == null || Tracks.Count == 0) return null;
        Track? hiResTrack = null;
        Track? losslessTrack = null;
        Track? lossyTrack = null;
        foreach (var track in Tracks)
        {
            if (track.IsHiResLossless) { hiResTrack ??= track; }
            else if (track.IsLossless) { losslessTrack ??= track; }
            else if (track.CodecShortName.Length > 0 &&
                     (lossyTrack == null || track.Bitrate > lossyTrack.Bitrate))
            {
                lossyTrack = track;
            }
        }
        return hiResTrack ?? losslessTrack ?? lossyTrack;
    }

    /// <summary>
    /// Album is explicit when at least one track has ITUNESADVISORY=1.
    /// Matches Apple Music's explicit album badge behavior.
    /// </summary>
    public bool IsExplicit => Tracks?.Any(t => t.IsExplicit) == true;

    /// <summary>
    /// Resolved release classification for the whole album. Priority:
    ///   1. Any track with <see cref="Track.IsReleaseTypeOverridden"/> wins.
    ///   2. The first non-Album <see cref="Track.ReleaseType"/> drawn from a tag (<see cref="Track.ReleaseTypeFromTag"/>).
    ///   3. Any explicit "Album" tag short-circuits the heuristic.
    ///   4. Track-count fallback: ≤2 tracks → Single, 3–6 → EP, 7+ → Album.
    /// </summary>
    public ReleaseType ReleaseType
    {
        get
        {
            if (Tracks == null || Tracks.Count == 0) return ReleaseType.Album;

            // 1. User override always wins.
            var overridden = Tracks.FirstOrDefault(t => t.IsReleaseTypeOverridden);
            if (overridden != null) return overridden.ReleaseType;

            // 2. First track with a non-default tag-derived type.
            var tagged = Tracks.FirstOrDefault(t => t.ReleaseTypeFromTag && t.ReleaseType != ReleaseType.Album);
            if (tagged != null) return tagged.ReleaseType;

            // 3. Explicit "Album" tag short-circuits the heuristic.
            if (Tracks.Any(t => t.ReleaseTypeFromTag && t.ReleaseType == ReleaseType.Album))
                return ReleaseType.Album;

            // 4. Track-count fallback (IsCompilation also handled here so the
            //    Albums view can filter compilations even without tags).
            if (IsCompilation) return ReleaseType.Compilation;
            var count = Tracks.Count;
            if (count <= 2) return ReleaseType.Single;
            if (count <= 6) return ReleaseType.EP;
            return ReleaseType.Album;
        }
    }

    /// <summary>Whether this album is composed entirely of compilation-flagged tracks.</summary>
    public bool IsCompilation => Tracks?.Count > 0 && Tracks.All(t => t.IsCompilation);

    /// <summary>Copyright notice from the first track that has one.</summary>
    public string Copyright =>
        Tracks?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Copyright))?.Copyright
        ?? string.Empty;

    /// <summary>Whether copyright info is available for display.</summary>
    public bool HasCopyright => !string.IsNullOrWhiteSpace(Copyright);

    /// <summary>
    /// Formatted release date string. Prefers full date from RELEASETIME tag
    /// (formatted as "Month Day, Year"), falls back to just year.
    /// </summary>
    public string ReleaseDateFormatted
    {
        get
        {
            // Try to get a full date from the first track that has a RELEASETIME value
            var releaseDate = Tracks?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ReleaseDate))?.ReleaseDate;
            if (!string.IsNullOrWhiteSpace(releaseDate))
            {
                if (DateTime.TryParse(releaseDate, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                {
                    return dt.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
                }
                // Try parsing date-only formats like "2024/11/29" or "2024-11-29"
                var cleaned = releaseDate.Replace('/', '-');
                if (DateTime.TryParseExact(cleaned, new[] { "yyyy-MM-dd", "yyyy-M-d", "dd-MM-yyyy", "MM-dd-yyyy" },
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt2))
                {
                    return dt2.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            // Fall back to year only
            return Year > 0 ? Year.ToString() : string.Empty;
        }
    }

    /// <summary>Whether release date info is available.</summary>
    public bool HasReleaseDate => !string.IsNullOrWhiteSpace(ReleaseDateFormatted);

    public override string ToString() => $"{Artist} - {Name}";

    /// <summary>
    /// Deterministically selects the track whose embedded/folder art represents the album,
    /// using the same ordering as the album track list (lowest disc, then track, then title;
    /// disc/track 0 sink appropriately). Returns null for an empty set. Keeping this stable
    /// ensures the cached cover does not vary between scans for mixed-art albums.
    /// </summary>
    public static Track? SelectArtworkRepresentative(IReadOnlyList<Track>? tracks)
    {
        if (tracks == null || tracks.Count == 0) return null;
        return tracks
            .OrderBy(t => t.DiscNumber <= 0 ? 1 : t.DiscNumber)
            .ThenBy(t => t.TrackNumber <= 0 ? int.MaxValue : t.TrackNumber)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .First();
    }
}
