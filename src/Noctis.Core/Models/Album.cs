using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.Models;

/// <summary>
/// Represents an album, aggregated from tracks sharing the same AlbumId.
/// Not persisted directly — rebuilt from track data on load.
/// Observable so favorite-derived bindings (tile heart overlay, context-menu
/// Favorites items) can be re-evaluated in real time when favorites change.
/// </summary>
public class Album : ObservableObject
{
    /// <summary>Deterministic ID computed from AlbumArtist + Album name.</summary>
    public Guid Id { get; set; }

    /// <summary>Album title.</summary>
    public string Name
    {
        get => _name;
        set { _name = value; _searchNameKey = null; }
    }
    private string _name = "Unknown Album";

    /// <summary>Album artist. "Various Artists" if tracks have mixed artists.</summary>
    public string Artist
    {
        get => _artist;
        set { _artist = value; _searchArtistKey = null; }
    }
    private string _artist = "Unknown Artist";

    private string? _searchNameKey;
    private string? _searchArtistKey;

    /// <summary>Lazily cached <see cref="Noctis.Helpers.SearchText.Normalize"/> key for
    /// Name, mirroring <see cref="Track.SearchTitleKey"/>: the Albums-view search used to
    /// re-normalize every album and track string per keystroke. The setters above
    /// invalidate on reassignment.</summary>
    public string SearchNameKey => _searchNameKey ??= Helpers.SearchText.Normalize(_name);

    /// <summary>Lazily cached normalized search key for Artist. See <see cref="SearchNameKey"/>.</summary>
    public string SearchArtistKey => _searchArtistKey ??= Helpers.SearchText.Normalize(_artist);

    /// <summary>Release year (from the first track that has one).</summary>
    public int Year { get; set; }

    /// <summary>Genre (from the first track that has one).</summary>
    public string Genre { get; set; } = "Unknown";

    /// <summary>The album's genres for the header line, each one separate like the artist
    /// page's GENRE fact ("Alternative Rock · Alt-Pop · Indie", GitHub #136); empty for the
    /// "Unknown" placeholder.</summary>
    public string GenreDisplay => string.Equals(Genre?.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase)
        ? string.Empty
        : Track.DotGenres(Genre);

    /// <summary>Number of tracks in this album.</summary>
    public int TrackCount { get; set; }

    /// <summary>Sum of all track durations.</summary>
    public TimeSpan TotalDuration { get; set; }

    /// <summary>
    /// Path to cached artwork file on disk (%APPDATA%\Noctis\artwork\{id}.jpg).
    /// Null if no artwork was found.
    /// </summary>
    public string? ArtworkPath { get; set; }

    /// <summary>Tracks in this album, ordered by disc then track number. Only the songs whose
    /// own <see cref="Track.AlbumId"/> is this album: everything that edits, converts, moves or
    /// deletes "the album" acts on these. Songs linked in from another album are in
    /// <see cref="EditionTracks"/>.</summary>
    public List<Track> Tracks { get; set; } = new();

    private List<Track>? _editionTracks;

    /// <summary>What the album page lists and plays: <see cref="Tracks"/> plus the songs linked
    /// in from other albums (<see cref="Services.AlbumLinks"/>), in album order. The same list as
    /// <see cref="Tracks"/> when nothing is linked.</summary>
    public List<Track> EditionTracks
    {
        get => _editionTracks ?? Tracks;
        set => _editionTracks = value;
    }

    /// <summary>The songs <see cref="EditionTracks"/> borrows from other albums (same Track
    /// instances as on their own album); empty when nothing is linked.</summary>
    public List<Track> LinkedTracks { get; set; } = new();

    /// <summary>The albums whose songs this one lists too (Unlink Tracks From).</summary>
    public List<Guid> LinkedAlbumIds { get; set; } = new();

    /// <summary>Some of <see cref="EditionTracks"/> belong to another album.</summary>
    public bool HasLinkedTracks => LinkedTracks.Count > 0;

    private bool _isCurrent;
    private bool _isNowPlaying;

    /// <summary>The player's loaded track belongs to this album (PlayerViewModel keeps it in sync).</summary>
    public bool IsCurrent { get => _isCurrent; set => SetProperty(ref _isCurrent, value); }

    /// <summary>IsCurrent and the player is playing: the tile's hover button shows Pause.</summary>
    public bool IsNowPlaying { get => _isNowPlaying; set => SetProperty(ref _isNowPlaying, value); }

    /// <summary>Whether all tracks in this album are marked as favorites.</summary>
    public bool IsAllTracksFavorite => Tracks?.Count > 0 && Tracks.All(t => t.IsFavorite);

    /// <summary>Whether at least one track in this album is marked as a favorite.</summary>
    public bool HasFavoriteTrack => Tracks?.Any(t => t.IsFavorite) == true;

    /// <summary>Re-raises change notifications for the favorite-derived computed
    /// properties. Called by LibraryService whenever favorites change so bound
    /// album tiles update live instead of waiting for a view rebuild.</summary>
    public void NotifyFavoriteStateChanged()
    {
        OnPropertyChanged(nameof(HasFavoriteTrack));
        OnPropertyChanged(nameof(IsAllTracksFavorite));
    }

    /// <summary>Whether two builds of one album hold the same songs (the same Track objects in
    /// the same order) and the same album fields, so the older object can stand in for the
    /// newer one (GitHub #225, LibraryService's index rebuilds).</summary>
    public static bool SameContent(Album a, Album b) =>
        a.Id == b.Id
        && a.Name == b.Name
        && a.Artist == b.Artist
        && a.Year == b.Year
        && a.Genre == b.Genre
        && a.TrackCount == b.TrackCount
        && a.TotalDuration == b.TotalDuration
        && a.ArtworkPath == b.ArtworkPath
        && a.Tracks.Count == b.Tracks.Count
        && a.Tracks.Zip(b.Tracks).All(p => ReferenceEquals(p.First, p.Second));

    /// <summary>Grid-tile subtitle: "Artist · Year"; artist alone when the year is unknown.</summary>
    public string TileSubtitle => Year > 0 ? $"{ArtistDisplay} · {Year}" : ArtistDisplay;

    /// <summary>Artist as shown: separators read as commas (<see cref="ArtistCredit.Display"/>).</summary>
    public string ArtistDisplay => ArtistCredit.Display(Artist);

    /// <summary>Formatted total duration.</summary>
    public string TotalDurationFormatted =>
        TotalDuration.TotalHours >= 1
            ? $"{(int)TotalDuration.TotalHours}h {TotalDuration.Minutes}m"
            : $"{(int)TotalDuration.TotalMinutes} min";

    public const string MixedQualityBadge = "Mixed";
    public const string MixedQualityDescription =
        "This album mixes lossless and compressed tracks.";

    /// <summary>Audio quality badge for the album, determined from its tracks:
    /// "Lossless"/"Hi-Res Lossless", the best lossy track's codec (e.g. "AAC"), or
    /// "Mixed" when lossless and lossy tracks sit on the same album.</summary>
    public string AudioQualityBadge => IsMixedQuality
        ? MixedQualityBadge
        : GetRepresentativeQualityTrack()?.AudioQualityBadge ?? string.Empty;

    /// <summary>Tooltip sentence explaining the badge kind; empty when no badge.</summary>
    public string AudioQualityDescription => IsMixedQuality
        ? MixedQualityDescription
        : GetRepresentativeQualityTrack()?.AudioQualityDescription ?? string.Empty;

    /// <summary>
    /// True when the album has at least one lossless track and at least one badged
    /// lossy track. A single lossless track used to label the whole album "Lossless".
    /// </summary>
    public bool IsMixedQuality
    {
        get
        {
            if (Tracks == null || Tracks.Count == 0) return false;
            bool lossless = false, lossy = false;
            foreach (var track in Tracks)
            {
                if (track.IsLossless) lossless = true;
                else if (track.CodecShortName.Length > 0) lossy = true;
                if (lossless && lossy) return true;
            }
            return false;
        }
    }

    /// <summary>Detailed audio quality info for tooltip (e.g. "16-bit/44.1 kHz FLAC"
    /// or "256 kbps 44.1 kHz AAC" for lossy albums, "8 lossless · 2 lossy (MP3)" for mixed).</summary>
    public string AudioQualityDetailedInfo
    {
        get
        {
            if (IsMixedQuality) return MixedQualityDetailedInfo();

            return GetRepresentativeQualityTrack()?.QualityDetailLine ?? string.Empty;
        }
    }

    private string MixedQualityDetailedInfo()
    {
        var losslessCount = Tracks.Count(t => t.IsLossless);
        var lossy = Tracks.Where(t => !t.IsLossless && t.CodecShortName.Length > 0).ToList();
        var codecs = string.Join(", ", lossy.Select(t => t.CodecShortName).Distinct());
        return $"{losslessCount} lossless · {lossy.Count} lossy ({codecs})";
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
    ///   4. Track-count fallback on <see cref="KnownTrackTotal"/>: ≤2 → Single, 3–6 → EP, 7+ → Album.
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
            var count = KnownTrackTotal;
            if (count <= 2) return ReleaseType.Single;
            if (count <= 6) return ReleaseType.EP;
            return ReleaseType.Album;
        }
    }

    /// <summary>
    /// How many tracks the release is known to have: the files present, or more when the
    /// tags say so (TRACKTOTAL, or a track number past the files present). A partially
    /// downloaded 12-track album with 3 files is still an album, not an EP.
    /// </summary>
    private int KnownTrackTotal
    {
        get
        {
            var count = Tracks!.Count;
            foreach (var t in Tracks)
            {
                if (t.TrackCount > count) count = t.TrackCount;
                if (t.TrackNumber > count) count = t.TrackNumber;
            }
            return count;
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

    /// <summary>Record label from the first track whose tags name one (<see cref="Track.Label"/>);
    /// empty when none does. Unlike <see cref="LabelName"/>, never guessed from the copyright
    /// notice, whose holder is often the artist rather than the label.</summary>
    public string RecordLabel =>
        Tracks?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Label))?.Label.Trim()
        ?? string.Empty;

    /// <summary>
    /// Record label extracted from the copyright notice: strips ℗/©/(P)/(C) marks,
    /// years, and joiners from the front, then cuts at the first clause break — so
    /// "℗ 2014 Big Machine Records, LLC, under exclusive license…" yields
    /// "Big Machine Records". Empty when nothing name-like remains, so callers can
    /// hide the field instead of showing legalese.
    /// </summary>
    public string LabelName
    {
        get
        {
            var notice = Copyright;
            if (string.IsNullOrWhiteSpace(notice))
                return string.Empty;

            var s = System.Text.RegularExpressions.Regex.Replace(
                notice.Trim(),
                @"^(\s*(℗|©|\(\s*[PpCc]\s*\)|(19|20)\d{2}|[&+,.\-–—:]))+\s*",
                string.Empty);

            var cut = s.Length;
            foreach (var marker in new[]
            {
                ",", ";", " under ", " a division", " division of", " distributed",
                " marketed", " manufactured", " all rights", " ℗", " ©", " (p)", " (c)"
            })
            {
                var i = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (i > 0 && i < cut) cut = i;
            }

            s = s.Substring(0, cut).Trim().TrimEnd('.', ',', ';');
            return s.Length is > 1 and <= 48 ? s : string.Empty;
        }
    }

    /// <summary>Whether a clean label name could be derived from the copyright tag.</summary>
    public bool HasLabelName => !string.IsNullOrEmpty(LabelName);

    /// <summary>
    /// Formatted release date string. Prefers full date from RELEASETIME tag
    /// (formatted as "Month Day, Year"), falls back to just year.
    /// </summary>
    public string ReleaseDateFormatted => FormatReleaseDate("MMMM d, yyyy");

    /// <summary>Compact variant ("Oct 27, 2014") for tight surfaces like the
    /// description dialog's facts grid, where the full month name truncates.</summary>
    public string ReleaseDateShortFormatted => FormatReleaseDate("MMM d, yyyy");

    private string FormatReleaseDate(string format)
    {
        // Try to get a full date from the first track that has a RELEASETIME value
        var releaseDate = Tracks?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ReleaseDate))?.ReleaseDate;
        if (Track.TryParseReleaseDate(releaseDate, out var parsed))
            return parsed.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

        // Fall back to year only
        return Year > 0 ? Year.ToString() : string.Empty;
    }

    /// <summary>Whether release date info is available.</summary>
    public bool HasReleaseDate => !string.IsNullOrWhiteSpace(ReleaseDateFormatted);

    /// <summary>
    /// Kicker above the album title ("ALBUM" / "SINGLE" / "EP" …), from the resolved
    /// <see cref="ReleaseType"/>: an explicit RELEASETYPE / MusicBrainz tag or user
    /// override first, then the iTunes "- Single" / "- EP" title suffix, then the
    /// track count (1–2 Single, 3–6 EP, 7+ Album). "Other" reads as a plain album.
    /// </summary>
    public string ReleaseKindLabel => ReleaseType switch
    {
        ReleaseType.Single => "SINGLE",
        ReleaseType.EP => "EP",
        ReleaseType.Compilation => "COMPILATION",
        ReleaseType.Live => "LIVE ALBUM",
        ReleaseType.Remix => "REMIX ALBUM",
        ReleaseType.Soundtrack => "SOUNDTRACK",
        _ => "ALBUM",
    };

    /// <summary>Title-case kind for tile captions on the artist page ("Album · 2024",
    /// "Single", "EP", "Live Album"); the kicker keeps its upper-case form. Every word is
    /// capitalised: "Live album" read as a typo next to the other title-case captions
    /// (GitHub #122, 2026-10-10).</summary>
    public string ReleaseKindTitle
    {
        get
        {
            var label = ReleaseKindLabel;
            if (label == "EP") return label;
            return string.Join(' ', label.Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
        }
    }

    /// <summary>Artist-page tile caption: "Album · 1982"; the kind alone when the year is unknown.</summary>
    public string ReleaseKindYearLine => Year > 0 ? $"{ReleaseKindTitle} · {Year}" : ReleaseKindTitle;

    /// <summary>Album-page related tile caption: "1982 · 8 songs"; the count alone when the year is unknown.</summary>
    public string YearSongsLine => Year > 0 ? $"{Year} · {SongCountText}" : SongCountText;

    private string SongCountText => TrackCount == 1 ? "1 song" : $"{TrackCount} songs";

    /// <summary>
    /// Album year from its tracks in album order: the first track's, or the first dated
    /// track's when that one has none, so an undated WAV copy in first place (Discord
    /// Tangent, Coda kept as MP3 + WAV) no longer leaves the whole album at year 0.
    /// </summary>
    public static int ResolveYear(IReadOnlyList<Track> tracks)
    {
        foreach (var t in tracks)
            if (t.Year > 0) return t.Year;
        return 0;
    }

    /// <summary>
    /// Album genre from its tracks in album order: the first tagged track's, as
    /// <see cref="Genre"/> documents. The first track's alone left an album whose opener is
    /// untagged (a bonus intro, an undated WAV copy) with no genre at all.
    /// </summary>
    public static string ResolveGenre(IReadOnlyList<Track> tracks)
    {
        foreach (var t in tracks)
            if (!string.IsNullOrWhiteSpace(t.Genre)) return t.Genre;
        return string.Empty;
    }

    /// <summary>"12 tracks" / "1 track" for the album header's facts line.</summary>
    public string TrackCountText => TrackCount == 1 ? "1 track" : $"{TrackCount} tracks";

    /// <summary>Header duration with seconds ("55m 51s"), hours when long ("1h 12m").</summary>
    public string HeaderDurationFormatted => FormatHeaderDuration(TotalDuration);

    /// <summary>"55m 51s", or "1h 12m" from an hour up; the Artists list's Time column too.</summary>
    public static string FormatHeaderDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s";

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
