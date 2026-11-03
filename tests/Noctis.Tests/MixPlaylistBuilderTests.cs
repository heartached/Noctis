using System;
using System.Collections.Generic;
using System.Linq;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Cosmo, 11-02): quick playlists — a mix from a song, artist or genre, built offline
/// from the library (no AI, no network).
/// </summary>
public class MixPlaylistBuilderTests
{
    private static Track T(string title, string artist, string genre, int year = 0, int bpm = 0, Guid? album = null) => new()
    {
        Title = title, Artist = artist, Genre = genre, Year = year, Bpm = bpm, AlbumId = album ?? Guid.NewGuid(),
        FilePath = $@"C:\m\{title}.mp3",
    };

    private static List<Track> Library()
    {
        var lib = new List<Track>();
        for (var i = 0; i < 12; i++) lib.Add(T($"Rock {i}", $"Rock Band {i % 6}", "Rock", 1990 + i, 120 + i));
        for (var i = 0; i < 12; i++) lib.Add(T($"Jazz {i}", $"Jazz Trio {i % 6}", "Jazz", 1960 + i, 90 + i));
        for (var i = 0; i < 6; i++) lib.Add(T($"Adele {i}", "Adele", "Pop", 2011 + i));
        for (var i = 0; i < 6; i++) lib.Add(T($"Pop {i}", $"Pop Star {i}", "Pop", 2012 + i));
        return lib;
    }

    [Fact]
    public void SongMix_OpensWithTheSeed_AndStaysInItsGenre()
    {
        var lib = Library();
        var seed = lib.First(t => t.Title == "Rock 3");
        var mix = MixPlaylistBuilder.Build(MixSeed.FromSong(seed), lib, size: 10, randomSeed: 1);
        Assert.Same(seed, mix[0]);
        Assert.Equal(10, mix.Count);
        Assert.All(mix, t => Assert.Equal("Rock", t.Genre));
        Assert.Equal(mix.Count, mix.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void GenreMix_OnlyThatGenre_AndNoArtistTakesOver()
    {
        var lib = Library();
        for (var i = 0; i < 20; i++) lib.Add(T($"Many {i}", "Prolific", "Jazz"));
        var mix = MixPlaylistBuilder.Build(MixSeed.FromGenre("jazz"), lib, size: 30, randomSeed: 2);
        Assert.All(mix, t => Assert.Equal("Jazz", t.Genre));
        Assert.True(mix.Count(t => t.Artist == "Prolific") <= MixPlaylistBuilder.MaxPerArtist);
    }

    [Fact]
    public void ArtistMix_LeadsWithTheArtist_ThenSimilarGenres()
    {
        var lib = Library();
        var mix = MixPlaylistBuilder.Build(MixSeed.FromArtist("Adele"), lib, size: 9, randomSeed: 3);
        Assert.Equal(9, mix.Count);
        Assert.True(mix.Count(t => t.Artist == "Adele") >= 3);
        Assert.All(mix, t => Assert.Equal("Pop", t.Genre));
    }

    [Fact]
    public void UnknownArtistOrGenre_GivesNothing()
    {
        Assert.Empty(MixPlaylistBuilder.Build(MixSeed.FromArtist("Nobody"), Library()));
        Assert.Empty(MixPlaylistBuilder.Build(MixSeed.FromGenre("Polka"), Library()));
    }

    [Fact]
    public void DislikedAndSnoozedSongs_StayOut()
    {
        var lib = Library();
        lib.First(t => t.Title == "Jazz 1").IsDisliked = true;
        lib.First(t => t.Title == "Jazz 2").SnoozedUntil = DateTime.UtcNow.AddDays(3);
        var mix = MixPlaylistBuilder.Build(MixSeed.FromGenre("Jazz"), lib, size: 50, randomSeed: 4);
        Assert.DoesNotContain(mix, t => t.Title is "Jazz 1" or "Jazz 2");
        Assert.Equal(10, mix.Count);
    }

    [Fact]
    public void LovedAndPlayedSongs_AreFavouredAmongEquals()
    {
        var lib = new List<Track>();
        for (var i = 0; i < 40; i++) lib.Add(T($"S{i}", $"A{i}", "Rock"));
        var loved = lib[37];
        loved.IsFavorite = true;
        loved.PlayCount = 200;
        loved.Rating = 5;
        var hits = Enumerable.Range(0, 20)
            .Count(r => MixPlaylistBuilder.Build(MixSeed.FromGenre("Rock"), lib, size: 5, randomSeed: r).Contains(loved));
        Assert.Equal(20, hits);
    }

    [Fact]
    public void Order_DoesNotRepeatAnArtistBackToBack_WhenItCanAvoidIt()
    {
        var a = Enumerable.Range(0, 3).Select(i => T($"a{i}", "A", "Rock")).ToList();
        var b = Enumerable.Range(0, 3).Select(i => T($"b{i}", "B", "Rock")).ToList();
        var ordered = MixPlaylistBuilder.Order(a.Concat(b).ToList());
        for (var i = 1; i < ordered.Count; i++)
            Assert.NotEqual(ordered[i - 1].Artist, ordered[i].Artist);
    }

    [Fact]
    public void Names_SayWhatTheMixIsFrom()
    {
        Assert.Equal("Mix from Hello", MixSeed.FromSong(new Track { Title = "Hello" }).PlaylistName);
        Assert.Equal("Adele Mix", MixSeed.FromArtist("Adele").PlaylistName);
        Assert.Equal("Rock Mix", MixSeed.FromGenre("Rock").PlaylistName);
    }
}
