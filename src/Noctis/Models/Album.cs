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

    /// <summary>Audio quality badge for the album, determined from its tracks.
    /// Uses the first representative track's detailed badge (e.g. "Lossless 16-bit/44.1kHz FLAC").</summary>
    public string AudioQualityBadge
    {
        get
        {
            if (Tracks == null || Tracks.Count == 0) return string.Empty;
            Track? hiResTrack = null;
            Track? losslessTrack = null;
            foreach (var track in Tracks)
            {
                if (track.IsHiResLossless) { hiResTrack ??= track; }
                else if (track.IsLossless) { losslessTrack ??= track; }
            }
            if (hiResTrack != null) return hiResTrack.AudioQualityBadge;
            if (losslessTrack != null) return losslessTrack.AudioQualityBadge;
            return string.Empty;
        }
    }

    /// <summary>Detailed audio quality info for tooltip (e.g. "16-bit/44.1 kHz FLAC").</summary>
    public string AudioQualityDetailedInfo
    {
        get
        {
            var track = GetRepresentativeLosslessTrack();
            if (track == null) return string.Empty;

            var parts = new List<string>();
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

    /// <summary>Gets the representative lossless track for quality display (prefers Hi-Res).</summary>
    private Track? GetRepresentativeLosslessTrack()
    {
        if (Tracks == null || Tracks.Count == 0) return null;
        Track? hiResTrack = null;
        Track? losslessTrack = null;
        foreach (var track in Tracks)
        {
            if (track.IsHiResLossless) { hiResTrack ??= track; }
            else if (track.IsLossless) { losslessTrack ??= track; }
        }
        return hiResTrack ?? losslessTrack;
    }

    /// <summary>
    /// Album is explicit when at least one track has ITUNESADVISORY=1.
    /// Matches Apple Music's explicit album badge behavior.
    /// </summary>
    public bool IsExplicit => Tracks?.Any(t => t.IsExplicit) == true;

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
}
