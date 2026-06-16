using System;
using System.Collections.Generic;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class PlaylistSortTests
{
    private static List<Track> Sample() => new()
    {
        new() { Title = "Cherry", Artist = "Beta", Album = "Zed",   Duration = TimeSpan.FromSeconds(200), DateAdded = new DateTime(2026, 1, 1) },
        new() { Title = "Apple",  Artist = "Alpha", Album = "Yarn", Duration = TimeSpan.FromSeconds(100), DateAdded = new DateTime(2026, 3, 1) },
        new() { Title = "Banana", Artist = "Gamma", Album = "Xyz",  Duration = TimeSpan.FromSeconds(300), DateAdded = new DateTime(2026, 2, 1) },
    };

    [Fact]
    public void SortTracks_Manual_PreservesGivenOrder()
    {
        var input = Sample();
        var result = PlaylistViewModel.SortTracks(input, PlaylistSortMode.Manual);
        Assert.Equal(new[] { "Cherry", "Apple", "Banana" }, result.Select(t => t.Title));
    }

    [Fact]
    public void SortTracks_Title_SortsAlphabetically()
    {
        var result = PlaylistViewModel.SortTracks(Sample(), PlaylistSortMode.Title);
        Assert.Equal(new[] { "Apple", "Banana", "Cherry" }, result.Select(t => t.Title));
    }

    [Fact]
    public void SortTracks_Artist_SortsByArtist()
    {
        var result = PlaylistViewModel.SortTracks(Sample(), PlaylistSortMode.Artist);
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, result.Select(t => t.Artist));
    }

    [Fact]
    public void SortTracks_Duration_SortsAscending()
    {
        var result = PlaylistViewModel.SortTracks(Sample(), PlaylistSortMode.Duration);
        Assert.Equal(new[] { "Apple", "Cherry", "Banana" }, result.Select(t => t.Title));
    }

    [Fact]
    public void SortTracks_RecentlyAdded_NewestFirst()
    {
        var result = PlaylistViewModel.SortTracks(Sample(), PlaylistSortMode.RecentlyAdded);
        Assert.Equal(new[] { "Apple", "Banana", "Cherry" }, result.Select(t => t.Title));
    }
}
