using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class RadioServiceTests
{
    private static Track T(string artist, string genre, int year, int bpm, string key, bool disliked = false, DateTime? snooze = null)
        => new() { Id = Guid.NewGuid(), Artist = artist, AlbumArtist = artist, Genre = genre, Year = year, Bpm = bpm, MusicalKey = key, IsDisliked = disliked, SnoozedUntil = snooze };

    [Fact]
    public void RanksSameArtistAndGenreAboveUnrelated()
    {
        var seed = T("Alpha", "Rock", 2010, 120, "A minor");
        var similar = T("Alpha", "Rock", 2011, 122, "E minor");
        var unrelated = T("Zeta", "Polka", 1975, 80, "C major");
        var svc = new RadioService();
        var result = svc.BuildSimilar(seed, new[] { similar, unrelated }, 2, new HashSet<Guid> { seed.Id });
        Assert.Equal(similar.Id, result[0].Id);
    }

    [Fact]
    public void ExcludesDislikedAndSnoozed()
    {
        var seed = T("Alpha", "Rock", 2010, 120, "A minor");
        var disliked = T("Alpha", "Rock", 2010, 120, "A minor", disliked: true);
        var snoozed = T("Alpha", "Rock", 2010, 120, "A minor", snooze: DateTime.UtcNow.AddDays(5));
        var ok = T("Alpha", "Rock", 2010, 121, "A minor");
        var svc = new RadioService();
        var result = svc.BuildSimilar(seed, new[] { disliked, snoozed, ok }, 10, new HashSet<Guid> { seed.Id });
        Assert.DoesNotContain(result, t => t.Id == disliked.Id);
        Assert.DoesNotContain(result, t => t.Id == snoozed.Id);
        Assert.Contains(result, t => t.Id == ok.Id);
    }

    [Fact]
    public void EmptyLibraryReturnsEmpty()
    {
        var seed = T("Alpha", "Rock", 2010, 120, "A minor");
        var svc = new RadioService();
        var result = svc.BuildSimilar(seed, Array.Empty<Track>(), 25, new HashSet<Guid> { seed.Id });
        Assert.Empty(result);
    }

    [Fact]
    public void OmitsTracksInExcludeSet()
    {
        var seed = T("Alpha", "Rock", 2010, 120, "A minor");
        var excluded = T("Alpha", "Rock", 2011, 121, "A minor");
        var ok = T("Alpha", "Rock", 2012, 122, "A minor");
        var svc = new RadioService();
        var result = svc.BuildSimilar(seed, new[] { excluded, ok }, 25, new HashSet<Guid> { seed.Id, excluded.Id });
        Assert.DoesNotContain(result, t => t.Id == excluded.Id);
        Assert.Contains(result, t => t.Id == ok.Id);
    }

    [Fact]
    public void AllCandidatesExcludedReturnsEmpty()
    {
        var seed = T("Alpha", "Rock", 2010, 120, "A minor");
        var a = T("Alpha", "Rock", 2011, 121, "A minor");
        var b = T("Alpha", "Rock", 2012, 122, "A minor");
        var svc = new RadioService();
        var result = svc.BuildSimilar(seed, new[] { a, b }, 25, new HashSet<Guid> { seed.Id, a.Id, b.Id });
        Assert.Empty(result);
    }
}
