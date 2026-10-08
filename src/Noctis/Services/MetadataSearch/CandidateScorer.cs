using Noctis.Models;

namespace Noctis.Services.MetadataSearch;

/// <summary>
/// One explainable 0..1 confidence per candidate (owner 10-08: Search metadata revamp). The old
/// path ranked on title+artist text alone, so a radio edit, a live take and a karaoke cover all
/// tied with the album version; duration and ISRC are what actually tell versions apart.
///
/// Track scope — weighted mean over the signals both sides have:
///   title 0.40 · artist 0.25 · album 0.10 · duration 0.20 · year 0.05 · track/disc # 0.05
/// then ×0.8 when the candidate is a different version (live/remix/edit…) than the query
/// (×0.95 when its duration matches within 2 s),
/// ×0.5 when the base titles barely agree (&lt; 0.5), and an ISRC match lifts it to
/// 0.96 + 0.04×score (decisive: the same master recording). A various-artists compilation
/// placement costs ×0.95 unless the query is one.
///
/// Album scope — album title 0.30 · album artist 0.25 · track count 0.15 · local tracks
/// matched 0.20 · matched durations 0.05 · year 0.05.
/// </summary>
public static class CandidateScorer
{
    public const double IsrcFloor = 0.96;

    public static MetadataCandidate Score(MetadataQuery q, MetadataCandidate c)
        => q.AlbumScope ? ScoreAlbum(q, c) : ScoreTrack(q, c);

    public static MetadataCandidate ScoreTrack(MetadataQuery q, MetadataCandidate c)
    {
        var notes = new List<string>();
        double sum = 0, weights = 0;
        void Add(double w, double s) { sum += w * s; weights += w; }

        var qt = MatchText.AnalyzeTitle(q.Title);
        var ct = MatchText.AnalyzeTitle(c.Title);
        double titleSim = 0;
        if (qt.Base.Length > 0)
        {
            titleSim = MatchText.Ratio(qt.Base, ct.Base);
            Add(0.40, titleSim);
            if (titleSim >= 0.95) notes.Add("Title match");
        }

        var qArtist = q.Artist.Length > 0 ? q.Artist : q.AlbumArtist;
        if (qArtist.Length > 0 && c.Artist.Length > 0)
        {
            var a = MatchText.ArtistSimilarity(qArtist, c.Artist);
            Add(0.25, a);
            if (a >= 0.9) notes.Add("Artist match");
        }

        if (q.Album.Length > 0 && c.Album.Length > 0)
        {
            var al = MatchText.AlbumSimilarity(q.Album, c.Album);
            Add(0.10, al);
            if (al >= 0.9) notes.Add("Same album");
        }

        double? durationScore = null;
        if (q.Duration is { } qd && qd > TimeSpan.Zero && c.Duration is { } cd && cd > TimeSpan.Zero)
        {
            durationScore = MatchText.DurationScore(qd, cd);
            Add(0.20, durationScore.Value);
            notes.Add(DurationNote(qd, cd));
        }

        if (q.Year is > 0 && c.Year is > 0)
        {
            var dy = Math.Abs(q.Year.Value - c.Year.Value);
            Add(0.05, dy == 0 ? 1.0 : dy == 1 ? 0.7 : 0.2);
            if (dy == 0) notes.Add($"Year {c.Year}");
        }

        if (q.TrackNumber is > 0 && c.TrackNumber is > 0)
        {
            var same = q.TrackNumber == c.TrackNumber && (q.DiscNumber ?? 1) == (c.DiscNumber ?? 1);
            Add(0.05, same ? 1.0 : 0.3);
            if (same) notes.Add($"Track {c.TrackNumber}");
        }

        var score = weights > 0 ? sum / weights : 0;

        // A different take of the song is a different recording, even with identical text.
        var diff = qt.Markers.Except(ct.Markers).Concat(ct.Markers.Except(qt.Markers)).ToList();
        if (diff.Count > 0 && qt.Base.Length > 0)
        {
            // Softer when the length matches to the second: an untagged radio edit in the
            // library is still the radio edit.
            score *= durationScore >= 1.0 ? 0.95 : 0.8;
            notes.Add("Different version (" + string.Join(", ", diff) + ")");
        }
        if (qt.Base.Length > 0 && titleSim < 0.5) score *= 0.5;

        // The same recording placed on a various-artists compilation is rarely the release the
        // user's file came from (Deezer's ISRC lookup returns whichever release it likes).
        if (IsVariousArtists(c.AlbumArtist) && !IsVariousArtists(q.AlbumArtist))
        {
            score *= 0.95;
            notes.Add("Compilation");
        }

        if (q.Isrc.Length > 0 && MatchText.SameIsrc(q.Isrc, c.Isrc))
        {
            score = IsrcFloor + (1 - IsrcFloor) * score;
            notes.Insert(0, "ISRC match");
        }

        return c with { Confidence = Math.Clamp(score, 0, 1), MatchNotes = notes };
    }

    public static MetadataCandidate ScoreAlbum(MetadataQuery q, MetadataCandidate c)
    {
        var notes = new List<string>();
        double sum = 0, weights = 0;
        void Add(double w, double s) { sum += w * s; weights += w; }

        if (q.Album.Length > 0)
        {
            var al = MatchText.AlbumSimilarity(q.Album, c.Album);
            Add(0.30, al);
            if (al >= 0.95) notes.Add("Album title match");
        }

        var qArtist = q.AlbumArtist.Length > 0 ? q.AlbumArtist : q.Artist;
        var cArtist = c.AlbumArtist.Length > 0 ? c.AlbumArtist : c.Artist;
        if (qArtist.Length > 0 && cArtist.Length > 0)
        {
            var a = MatchText.ArtistSimilarity(qArtist, cArtist);
            Add(0.25, a);
            if (a >= 0.9) notes.Add("Artist match");
        }

        var local = q.AlbumTracks;
        var candCount = c.TrackCount ?? (c.Tracks.Count > 0 ? c.Tracks.Count : (int?)null);
        if (local.Count > 0 && candCount is > 0)
        {
            var d = Math.Abs(candCount.Value - local.Count);
            Add(0.15, d == 0 ? 1.0 : d <= 2 ? 0.6 : Math.Max(0, 1.0 - (double)d / local.Count) * 0.5);
            notes.Add(d == 0 ? $"Same track count ({local.Count})" : $"{candCount} tracks vs {local.Count} local");
        }

        var tracks = c.Tracks;
        if (local.Count > 0 && c.Tracks.Count > 0)
        {
            var map = MapTracks(local, c.Tracks);
            tracks = map.Tracks;
            Add(0.20, (double)map.Matched / local.Count);
            notes.Add($"{map.Matched}/{local.Count} tracks matched");
            if (map.DurationScore is { } ds) Add(0.05, ds);
        }
        else if (local.Count > 0)
        {
            // No track list from this source: neither proof nor disproof, so it must not outrank
            // a release whose list was checked and matched.
            Add(0.20, 0.5);
            notes.Add("Track list not checked");
        }

        int? qYear = q.Year;
        if (qYear is not > 0)
        {
            var localYear = local.Select(t => t.Year).FirstOrDefault(y => y > 0);
            qYear = localYear > 0 ? localYear : null;
        }
        if (qYear is > 0 && c.Year is > 0)
        {
            var dy = Math.Abs(qYear.Value - c.Year.Value);
            Add(0.05, dy == 0 ? 1.0 : dy == 1 ? 0.7 : 0.2);
            if (dy == 0) notes.Add($"Year {c.Year}");
        }

        var score = weights > 0 ? sum / weights : 0;
        return c with { Confidence = Math.Clamp(score, 0, 1), MatchNotes = notes, Tracks = tracks };
    }

    private static bool IsVariousArtists(string? s)
        => MatchText.Fold(s) is "variousartists" or "various" or "va";

    private static string DurationNote(TimeSpan q, TimeSpan c)
    {
        var d = (int)Math.Round(Math.Abs((q - c).TotalSeconds));
        return d <= 10 ? $"Duration ±{d} s" : $"Duration off by {d} s";
    }

    /// <summary>Result of mapping a release's tracks onto the local album's tracks.</summary>
    public sealed record TrackMapping(IReadOnlyList<CandidateTrack> Tracks, int Matched, double? DurationScore);

    /// <summary>
    /// Maps candidate tracks onto local tracks, one-to-one, best pairs first. A pair scores
    /// 0.55 title + 0.25 duration + 0.20 disc/track number; pairs under 0.55 stay unmatched,
    /// so a translated title still pairs on number+duration only when both agree exactly-ish.
    /// </summary>
    public static TrackMapping MapTracks(IReadOnlyList<Track> local, IReadOnlyList<CandidateTrack> cand)
    {
        var pairs = new List<(int L, int C, double S, double? D)>();
        for (var i = 0; i < local.Count; i++)
        {
            var lt = local[i];
            var lBase = MatchText.AnalyzeTitle(lt.Title).Base;
            for (var j = 0; j < cand.Count; j++)
            {
                var ctk = cand[j];
                var title = lBase.Length == 0 ? 0 : MatchText.Ratio(lBase, MatchText.AnalyzeTitle(ctk.Title).Base);
                double? dur = lt.Duration > TimeSpan.Zero && ctk.Duration is { } cd && cd > TimeSpan.Zero
                    ? MatchText.DurationScore(lt.Duration, cd) : null;
                double num = 0;
                if (lt.TrackNumber > 0 && ctk.TrackNumber is > 0 && lt.TrackNumber == ctk.TrackNumber)
                    num = Math.Max(1, lt.DiscNumber) == (ctk.DiscNumber ?? 1) ? 1.0 : 0.3;
                var s = 0.55 * title + 0.25 * (dur ?? 0.5) + 0.20 * num;
                if (s >= 0.55) pairs.Add((i, j, s, dur));
            }
        }

        var usedL = new HashSet<int>();
        var usedC = new Dictionary<int, Guid>();
        var durs = new List<double>();
        foreach (var p in pairs.OrderByDescending(p => p.S))
        {
            if (usedL.Contains(p.L) || usedC.ContainsKey(p.C)) continue;
            usedL.Add(p.L);
            usedC[p.C] = local[p.L].Id;
            if (p.D is { } d) durs.Add(d);
        }

        var tracks = cand.Select((t, j) => t with { MatchedLocalTrackId = usedC.TryGetValue(j, out var id) ? id : null })
                         .ToList();
        return new TrackMapping(tracks, usedL.Count, durs.Count > 0 ? durs.Average() : null);
    }
}
