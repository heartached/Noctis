using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Select-all in the Add Songs picker. It is scoped to the rows currently on screen,
/// skips tracks already in the playlist, and has to preserve the tick-order contract
/// Add() depends on (selection order, not library order).
/// </summary>
public class AddSongsSelectAllTests
{
    private static Track Make(string title) =>
        new() { Id = Guid.NewGuid(), Title = title, Artist = "A", Album = "B" };

    private static AddSongsDialogViewModel MakeVm(IReadOnlyList<Track> library, params Guid[] alreadyIn)
        => new(library, alreadyIn);

    [Fact]
    public void ToggleSelectAll_SelectsEveryVisibleRow()
    {
        var library = new[] { Make("One"), Make("Two"), Make("Three") };
        var vm = MakeVm(library);

        vm.ToggleSelectAllCommand.Execute(null);

        Assert.Equal(3, vm.SelectedCount);
        Assert.True(vm.HasSelection);
        Assert.True(vm.AreAllResultsSelected);
        Assert.All(vm.Results, r => Assert.True(r.IsSelected));
    }

    [Fact]
    public void ToggleSelectAll_WhenAllSelected_ClearsThem()
    {
        var library = new[] { Make("One"), Make("Two") };
        var vm = MakeVm(library);

        vm.ToggleSelectAllCommand.Execute(null);
        vm.ToggleSelectAllCommand.Execute(null);

        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.HasSelection);
        Assert.All(vm.Results, r => Assert.False(r.IsSelected));
    }

    [Fact]
    public async Task ToggleSelectAll_SkipsTracksAlreadyInPlaylist()
    {
        // Shuffle mode hides already-added tracks outright, so search mode is where an
        // IsInPlaylist row is actually on screen and has to be left untouched.
        var library = new[] { Make("Song One"), Make("Song Two"), Make("Song Three") };
        var vm = MakeVm(library, library[1].Id);

        vm.SearchText = "Song";
        await WaitForResultsAsync(vm, expected: 3);

        vm.ToggleSelectAllCommand.Execute(null);

        Assert.Equal(2, vm.SelectedCount);
        var already = vm.Results.Single(r => r.Track.Id == library[1].Id);
        Assert.True(already.IsInPlaylist);
        Assert.False(already.IsSelected);
        // All *selectable* rows are ticked, so the button should read as fully selected.
        Assert.True(vm.AreAllResultsSelected);
    }

    /// <summary>Search is debounced (250ms); poll rather than sleep a fixed amount.</summary>
    private static async Task WaitForResultsAsync(AddSongsDialogViewModel vm, int expected)
    {
        for (var i = 0; i < 100 && vm.Results.Count != expected; i++)
            await Task.Delay(20);
        Assert.Equal(expected, vm.Results.Count);
    }

    [Fact]
    public void ToggleSelectAll_KeepsEarlierManualPicksFirst()
    {
        var library = new[] { Make("One"), Make("Two"), Make("Three") };
        var vm = MakeVm(library);

        // Tick the last row by hand, then select all: the manual pick must stay first,
        // matching how Add() projects tracks in tick order.
        var last = vm.Results.Last();
        vm.ToggleSelectCommand.Execute(last);
        vm.ToggleSelectAllCommand.Execute(null);

        IReadOnlyList<Track>? chosen = null;
        vm.SongsChosen += (_, tracks) => chosen = tracks;
        vm.AddCommand.Execute(null);

        Assert.NotNull(chosen);
        Assert.Equal(3, chosen!.Count);
        Assert.Equal(last.Track.Id, chosen[0].Id);
    }

    [Fact]
    public void SelectAllLabel_FlipsWithState()
    {
        var vm = MakeVm(new[] { Make("One") });

        Assert.True(vm.HasSelectableResults);
        Assert.Equal("Select all", vm.SelectAllText);

        vm.ToggleSelectAllCommand.Execute(null);
        Assert.Equal("Deselect all", vm.SelectAllText);
    }

    [Fact]
    public void SelectAllHidden_WhenNothingIsSelectable()
    {
        var track = Make("One");
        var vm = MakeVm(new[] { track }, track.Id);

        Assert.False(vm.HasSelectableResults);
        Assert.False(vm.AreAllResultsSelected);
    }
}
