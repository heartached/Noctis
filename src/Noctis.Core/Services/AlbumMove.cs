using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// GitHub #239 "Move to Album": the tags a song takes when it is moved into another album that
/// already exists, or into a new album the user names. Albums come from the files' tags, so a
/// "new album" exists once a song carries its name; the move writes these values into each file
/// (through <see cref="IMetadataService.WriteTrackMetadata(Track, AlbumIdentityTags)"/>) so other
/// players and DAPs show the song where Noctis does.
///
/// Into an existing album the song takes the album-level tags its songs share, so no player
/// files it apart: Album, Album Artist, the compilation flag, the release-type choice, the year
/// and release date (when the album has one; a song keeps its own year otherwise, since a year
/// is never cleared by a save), the disc it lands on, and the album's sort names and
/// MusicBrainz release ids (read from the album's first file, see <see cref="AlbumIdentityTags"/>).
/// It goes after the album's last numbered song on its last disc; an album without track
/// numbers gets none added. The track total stays when the new number fits it, else it is
/// cleared rather than left wrong. Embedded cover art is never touched.
///
/// Into a new album the songs are numbered 1..N in the order given, on disc 1 of 1, as a
/// non-compilation without a release-type choice; the old album's sort names and MusicBrainz
/// release ids are cleared (they name the album the song left). Year and cover stay.
/// </summary>
public static class AlbumMove
{
    /// <summary>The values one song takes. <see cref="Year"/> 0 and an empty
    /// <see cref="ReleaseDate"/> keep the song's own.</summary>
    public sealed record Plan(
        Track Track,
        string Album,
        string AlbumArtist,
        int TrackNumber,
        int TrackCount,
        int DiscNumber,
        int DiscCount,
        bool IsCompilation,
        bool IsReleaseTypeOverridden,
        ReleaseType ReleaseType,
        int Year,
        string ReleaseDate);

    /// <summary>
    /// The values for moving <paramref name="moving"/> (in this order) into the album named
    /// <paramref name="album"/> by <paramref name="albumArtist"/>, whose songs (the moving ones
    /// left out) are <paramref name="targetTracks"/>: empty for a new album.
    /// </summary>
    public static List<Plan> PlanMove(IReadOnlyList<Track> moving, IReadOnlyList<Track> targetTracks,
        string album, string albumArtist)
    {
        var result = new List<Plan>(moving.Count);
        if (targetTracks.Count == 0)
        {
            for (var i = 0; i < moving.Count; i++)
            {
                var t = moving[i];
                result.Add(new Plan(t, album, albumArtist,
                    TrackNumber: i + 1, TrackCount: moving.Count, DiscNumber: 1, DiscCount: 1,
                    IsCompilation: false, IsReleaseTypeOverridden: false, ReleaseType: t.ReleaseType,
                    Year: 0, ReleaseDate: string.Empty));
            }
            return result;
        }

        var ordered = targetTracks.ToList();
        ordered.Sort(AlbumGrouping.AlbumOrder);
        var first = ordered[0];
        var disc = Math.Max(1, ordered.Max(t => t.DiscNumber));
        var discCount = Math.Max(disc, ordered.Max(t => t.DiscCount));
        var onDisc = ordered.Where(t => Math.Max(1, t.DiscNumber) == disc).ToList();
        var lastNumber = onDisc.Count == 0 ? 0 : onDisc.Max(t => t.TrackNumber);
        var total = onDisc.Count == 0 ? 0 : onDisc.Max(t => t.TrackCount);
        var dated = ordered.FirstOrDefault(t => t.Year > 0);

        for (var i = 0; i < moving.Count; i++)
        {
            var number = lastNumber > 0 ? lastNumber + 1 + i : 0;
            result.Add(new Plan(moving[i], album, albumArtist,
                TrackNumber: number,
                TrackCount: number > 0 && total >= number ? total : 0,
                DiscNumber: disc, DiscCount: discCount,
                IsCompilation: first.IsCompilation,
                IsReleaseTypeOverridden: first.IsReleaseTypeOverridden,
                ReleaseType: first.IsReleaseTypeOverridden ? first.ReleaseType : moving[i].ReleaseType,
                Year: dated?.Year ?? 0,
                ReleaseDate: dated?.ReleaseDate ?? string.Empty));
        }
        return result;
    }

    /// <summary>Puts <paramref name="plan"/>'s values on its track (the library side of a move).</summary>
    public static void Apply(Plan plan)
    {
        var t = plan.Track;
        t.Album = plan.Album;
        t.AlbumArtist = plan.AlbumArtist;
        t.TrackNumber = plan.TrackNumber;
        t.TrackCount = plan.TrackCount;
        t.DiscNumber = plan.DiscNumber;
        t.DiscCount = plan.DiscCount;
        t.IsCompilation = plan.IsCompilation;
        t.IsReleaseTypeOverridden = plan.IsReleaseTypeOverridden;
        t.ReleaseType = plan.ReleaseType;
        if (plan.Year > 0)
        {
            t.Year = plan.Year;
            t.ReleaseDate = plan.ReleaseDate;
        }
    }
}

/// <summary>
/// GitHub #239: the album-identity tags Noctis does not index but other players group or sort
/// albums by: album sort, album artist sort, and the MusicBrainz release, release group and
/// release artist ids (Jellyfin, Navidrome, Plex and Picard keep two releases apart by the
/// release id even under one album name). Null fields are removed from the file.
/// </summary>
public sealed record AlbumIdentityTags(
    string? AlbumSort,
    string[]? AlbumArtistsSort,
    string? MusicBrainzReleaseId,
    string? MusicBrainzReleaseGroupId,
    string? MusicBrainzReleaseArtistId)
{
    /// <summary>A new album: none of the old album's values.</summary>
    public static readonly AlbumIdentityTags None = new(null, null, null, null, null);
}
