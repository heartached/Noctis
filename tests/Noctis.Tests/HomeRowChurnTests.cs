using System.Collections.Specialized;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Navigating to Home rebuilds the time-aware rows on every visit by design (they
/// track the clock and the play log). The rebuild must NOT fire a collection Reset
/// when the resolved rows are unchanged: every Reset re-materializes every item
/// container in the row on the UI thread, which reads as a stutter on each Home
/// visit on slow renderers (issue #31).
/// </summary>
public class HomeRowChurnTests
{
    private static Track T() => new() { Title = "t" };

    private static (BulkObservableCollection<Track> Row, List<NotifyCollectionChangedAction> Events) SeededRow(params Track[] tracks)
    {
        var row = new BulkObservableCollection<Track>();
        row.ReplaceAll(tracks);
        var events = new List<NotifyCollectionChangedAction>();
        row.CollectionChanged += (_, e) => events.Add(e.Action);
        return (row, events);
    }

    [Fact]
    public void SameInstancesSameOrder_FiresNothing()
    {
        var a = T();
        var b = T();
        var (row, events) = SeededRow(a, b);

        HomeViewModel.ReplaceRowIfChanged(row, new List<Track> { a, b });

        Assert.Empty(events);
        Assert.Equal(new[] { a, b }, row);
    }

    [Fact]
    public void EmptyToEmpty_FiresNothing()
    {
        var (row, events) = SeededRow();

        HomeViewModel.ReplaceRowIfChanged(row, new List<Track>());

        Assert.Empty(events);
    }

    [Fact]
    public void ChangedMembership_FiresSingleReset()
    {
        var a = T();
        var b = T();
        var c = T();
        var (row, events) = SeededRow(a, b);

        HomeViewModel.ReplaceRowIfChanged(row, new List<Track> { a, c });

        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, events);
        Assert.Equal(new[] { a, c }, row);
    }

    [Fact]
    public void ReorderedSameInstances_FiresReset()
    {
        var a = T();
        var b = T();
        var (row, events) = SeededRow(a, b);

        HomeViewModel.ReplaceRowIfChanged(row, new List<Track> { b, a });

        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, events);
        Assert.Equal(new[] { b, a }, row);
    }

    /// <summary>
    /// Comparison must be by reference, not by Id: a rescan can rebuild Track
    /// instances with the same Id but fresh metadata, and skipping the Reset there
    /// would leave the row bound to stale objects. Same instance = provably same
    /// bindings; anything else replaces.
    /// </summary>
    [Fact]
    public void SameIdDifferentInstance_FiresReset()
    {
        var id = Guid.NewGuid();
        var stale = new Track { Id = id, Title = "old" };
        var fresh = new Track { Id = id, Title = "new" };
        var (row, events) = SeededRow(stale);

        HomeViewModel.ReplaceRowIfChanged(row, new List<Track> { fresh });

        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, events);
        Assert.Same(fresh, Assert.Single(row));
    }
}
