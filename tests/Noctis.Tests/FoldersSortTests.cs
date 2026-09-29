using System;
using System.Collections.Generic;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>GitHub #89: the Folders track pane can be ordered by file last-modified time.</summary>
public class FoldersSortTests
{
    // Given in folder order; "Tie1"/"Tie2" share a timestamp (a batch retag).
    private static List<Track> FolderOrder() => new()
    {
        new() { Title = "Mid",  LastModified = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
        new() { Title = "Tie1", LastModified = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
        new() { Title = "Old",  LastModified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        new() { Title = "Tie2", LastModified = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
    };

    [Fact]
    public void Default_KeepsFolderOrder()
    {
        var result = LibraryFoldersViewModel.SortTracks(FolderOrder(), "default");
        Assert.Equal(new[] { "Mid", "Tie1", "Old", "Tie2" }, result.Select(t => t.Title));
    }

    [Fact]
    public void ModifiedNewest_PutsNewestFirst_TiesKeepFolderOrder()
    {
        var result = LibraryFoldersViewModel.SortTracks(FolderOrder(), "modified-newest");
        Assert.Equal(new[] { "Tie1", "Tie2", "Mid", "Old" }, result.Select(t => t.Title));
    }

    [Fact]
    public void ModifiedOldest_PutsOldestFirst_TiesKeepFolderOrder()
    {
        var result = LibraryFoldersViewModel.SortTracks(FolderOrder(), "modified-oldest");
        Assert.Equal(new[] { "Old", "Mid", "Tie1", "Tie2" }, result.Select(t => t.Title));
    }

    [Fact]
    public void UnknownMode_FallsBackToFolderOrder()
    {
        var result = LibraryFoldersViewModel.SortTracks(FolderOrder(), "bogus");
        Assert.Equal(new[] { "Mid", "Tie1", "Old", "Tie2" }, result.Select(t => t.Title));
    }
}
