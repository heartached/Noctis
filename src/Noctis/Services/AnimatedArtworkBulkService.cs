using Noctis.Models;

namespace Noctis.Services;

/// <summary>What one album's lookup came to.</summary>
public enum AnimatedArtworkLookup
{
    /// <summary>A clear match's loop was downloaded.</summary>
    Saved,
    /// <summary>The store answered, but nothing was clearly this album with a square loop.</summary>
    NoMatch,
    /// <summary>Not looked up (no album artist) or the store returned nothing at all.</summary>
    NoCandidates,
}

public sealed record AnimatedArtworkBulkProgress(int Done, int Total, Guid AlbumId, string Outcome);

public sealed record AnimatedArtworkBulkSummary(int Found, int NotFound, int Skipped, int Failed)
{
    /// <summary>Of <see cref="Skipped"/>: looked up in an earlier run and nothing was found then.</summary>
    public int CheckedBefore { get; init; }
}

/// <summary>
/// Animated artwork for many albums at once (Discord, Cosmo 11-02: "…and also adding animated
/// wallpaper, and just not having to do it by hand"). The same Apple Music lookup the Metadata
/// editor's Find animated artwork runs, minus the chooser: an album gets a loop only when the
/// store album is clearly the same one (title and artist both close, no karaoke/tribute look),
/// so a near miss is left alone rather than guessed. One album at a time, requests spaced out
/// (<see cref="PoliteGate"/>); albums that already have a cover.mp4/.webm or a saved loop are
/// skipped, and albums looked up before are remembered (<see cref="BulkRunLedger"/>) so a stopped
/// run resumes. Loops are saved album-scoped in the app's cache; files next to the music are
/// never written.
/// </summary>
public sealed class AnimatedArtworkBulkService
{
    private readonly Func<ITunesArtworkService.AnimatedArtworkQuery, CancellationToken, Task<IReadOnlyList<ITunesArtworkService.RankedAlbumCandidate>>> _find;
    private readonly Func<ITunesArtworkService.ArtworkCandidate, CancellationToken, Task<ITunesArtworkService.AnimatedArtworkAlbum?>> _open;
    private readonly Func<ITunesArtworkService.AnimatedArtworkVariant, string, CancellationToken, Task<bool>> _download;
    private readonly IAnimatedCoverService _covers;
    private readonly string? _ledgerPath;

    /// <summary>Spacing between two requests to Apple.</summary>
    internal TimeSpan RequestInterval { get; init; } = TimeSpan.FromSeconds(1.5);
    internal Func<DateTime>? Clock { get; init; }
    internal Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    /// <summary>Confident candidates opened per album before giving up on it.</summary>
    internal const int CandidatesToOpen = 2;

    public AnimatedArtworkBulkService(
        Func<ITunesArtworkService.AnimatedArtworkQuery, CancellationToken, Task<IReadOnlyList<ITunesArtworkService.RankedAlbumCandidate>>> find,
        Func<ITunesArtworkService.ArtworkCandidate, CancellationToken, Task<ITunesArtworkService.AnimatedArtworkAlbum?>> open,
        Func<ITunesArtworkService.AnimatedArtworkVariant, string, CancellationToken, Task<bool>> download,
        IAnimatedCoverService covers,
        string? ledgerPath)
    {
        _find = find;
        _open = open;
        _download = download;
        _covers = covers;
        _ledgerPath = ledgerPath;
    }

    /// <summary>The app's wiring: the iTunes/Apple Music service and the animated cover cache.</summary>
    public static AnimatedArtworkBulkService For(ITunesArtworkService itunes, IAnimatedCoverService covers, string? ledgerPath) => new(
        (q, ct) => itunes.FindAnimatedArtworkCandidatesAsync(q, 5, ct),
        (c, ct) => itunes.GetAnimatedArtworkAlbumAsync(c.ViewUrl, c, ct),
        (v, path, ct) => itunes.DownloadHlsVariantAsMp4Async(v, path, ct),
        covers, ledgerPath);

    /// <summary>The library's albums as track groups, one per album id (local songs with an album name).</summary>
    public static IReadOnlyList<IReadOnlyList<Track>> GroupAlbums(IEnumerable<Track> tracks)
        => tracks
            .Where(t => t.SourceType == SourceType.Local && t.AlbumId != Guid.Empty && HasAlbumName(t))
            .GroupBy(t => t.AlbumId)
            .Select(g => (IReadOnlyList<Track>)g.ToList())
            .OrderBy(g => AlbumArtist(g[0]), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g[0].Album, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static bool HasAlbumName(Track t)
        => !string.IsNullOrWhiteSpace(t.Album) && !t.Album.Equals("Unknown Album", StringComparison.OrdinalIgnoreCase);

    internal static string AlbumArtist(Track t)
        => !string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.AlbumArtist : t.Artist ?? string.Empty;

    /// <summary>True when the store album is clearly the local one: auto-picking needs both
    /// the title and the artist close, and nothing that looks like a karaoke/tribute record.</summary>
    internal static bool IsConfident(ITunesArtworkService.ArtworkCandidate c, string album, string artist)
        => artist.Length > 0
           && AnimatedArtworkMatcher.TitleSimilarity(c.CollectionName, album) >= AnimatedArtworkMatcher.SameTitle
           && AnimatedArtworkMatcher.ArtistSimilarity(c.ArtistName, artist) >= AnimatedArtworkMatcher.SameArtist
           && !AnimatedArtworkMatcher.LooksLikeJunk(c.CollectionName, c.ArtistName, album);

    public async Task<AnimatedArtworkBulkSummary> RunAsync(
        IReadOnlyList<IReadOnlyList<Track>> albums, bool recheck, IProgress<AnimatedArtworkBulkProgress>? progress, CancellationToken ct)
    {
        int found = 0, notFound = 0, skipped = 0, failed = 0, before = 0, done = 0;
        var gate = new PoliteGate(RequestInterval, Clock, Delay);
        var ledger = new BulkRunLedger(_ledgerPath, Clock);
        try
        {
            foreach (var album in albums)
            {
                ct.ThrowIfCancellationRequested();
                var rep = album[0];
                string outcome;
                if (_covers.Resolve(rep) is not null)
                {
                    skipped++;
                    outcome = "already has one";
                }
                else if (!recheck && ledger.CheckedRecently(rep.AlbumId, 0))
                {
                    skipped++;
                    before++;
                    outcome = "looked up before";
                }
                else
                {
                    try
                    {
                        var saved = await FindAndSaveAsync(album, gate, ct).ConfigureAwait(false);
                        if (saved) { found++; outcome = "animated artwork saved"; }
                        else { notFound++; outcome = "none found"; }
                        ledger.Record(rep.AlbumId, saved ? 1 : 0);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        failed++;
                        outcome = "failed";
                        DebugLog.Write(ITunesArtworkService.LogSource, $"bulk: {rep.Album}: {ex.Message}");
                    }
                }
                done++;
                progress?.Report(new AnimatedArtworkBulkProgress(done, albums.Count, rep.AlbumId, outcome));
                if (done % 10 == 0) ledger.Save();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped: what was saved stays, the ledger below lets the next run resume.
        }
        finally
        {
            ledger.Save();
        }
        return new AnimatedArtworkBulkSummary(found, notFound, skipped, failed) { CheckedBefore = before };
    }

    private async Task<bool> FindAndSaveAsync(IReadOnlyList<Track> album, PoliteGate gate, CancellationToken ct)
    {
        var rep = album[0];
        var temp = Path.Combine(Path.GetTempPath(), $"noctis-bulkanim-{Guid.NewGuid():N}.mp4");
        try
        {
            if (await FindAndDownloadAsync(album, gate, temp, ct).ConfigureAwait(false) != AnimatedArtworkLookup.Saved) return false;
            await _covers.ImportAsync(rep, temp, AnimatedCoverScope.Album).ConfigureAwait(false);
            return true;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp file, best effort */ }
        }
    }

    /// <summary>
    /// The one lookup both the whole-library run and the fetch on opening an album use: the
    /// store albums that clearly are this one (<see cref="IsConfident"/>; ranked with the
    /// album's year and song count as tie-breaks), the first with a square loop downloaded to
    /// <paramref name="destination"/>. Nothing is written unless the result is
    /// <see cref="AnimatedArtworkLookup.Saved"/>.
    /// </summary>
    internal async Task<AnimatedArtworkLookup> FindAndDownloadAsync(IReadOnlyList<Track> album, PoliteGate gate, string destination, CancellationToken ct)
    {
        var rep = album[0];
        var title = rep.Album.Trim();
        var artist = AlbumArtist(rep).Trim();
        if (title.Length == 0 || artist.Length == 0) return AnimatedArtworkLookup.NoCandidates;
        await gate.WaitTurnAsync(ct).ConfigureAwait(false);
        var query = new ITunesArtworkService.AnimatedArtworkQuery(title, artist, artist, Typed: false)
        {
            Year = album.Select(t => t.Year).FirstOrDefault(y => y > 0),
            TrackCount = album.Count,
        };
        var ranked = await _find(query, ct).ConfigureAwait(false);
        if (ranked.Count == 0) return AnimatedArtworkLookup.NoCandidates;
        foreach (var candidate in ranked.Where(r => IsConfident(r.Candidate, title, artist)).Take(CandidatesToOpen))
        {
            await gate.WaitTurnAsync(ct).ConfigureAwait(false);
            var page = await _open(candidate.Candidate, ct).ConfigureAwait(false);
            // Square loops only: a portrait-only loop would be squashed into the square cover.
            if (page is null || page.IsTallOnly || page.Variants.Count == 0) continue;

            // The 1080p tier when there is one (the editor's default pick), not the 4K file.
            var variant = page.Variants[0];
            await gate.WaitTurnAsync(ct).ConfigureAwait(false);
            if (!await _download(variant, destination, ct).ConfigureAwait(false))
            {
                try { if (File.Exists(destination)) File.Delete(destination); } catch { /* best effort */ }
                continue;
            }
            DebugLog.Write(ITunesArtworkService.LogSource, $"auto: \"{title}\" by {artist} -> \"{page.Title}\" ({variant.Label})");
            return AnimatedArtworkLookup.Saved;
        }
        return AnimatedArtworkLookup.NoMatch;
    }
}
