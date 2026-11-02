using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Cosmo, 11-02): "some of the songs are missing but they really just have a slightly
/// different name, so maybe let us add and remove songs". The import's review step: a missing
/// entry offers its closest library songs and a search, a matched one can be left out, any
/// library song can be added, and the playlist keeps the file's order.
/// </summary>
public class PlaylistImportReviewTests
{
    private static Track T(string title, string artist) => new() { Title = title, Artist = artist, FilePath = $@"C:\m\{title}.mp3" };

    private static readonly Track Hello = T("Hello", "Adele");
    private static readonly Track Yellow = T("Yellow", "Coldplay");
    private static readonly Track Stairway = T("Stairway to Heaven (Remaster)", "Led Zeppelin");
    private static readonly Track Kashmir = T("Kashmir", "Led Zeppelin");

    private sealed class Service : IPlaylistImportService
    {
        public IReadOnlyList<Guid>? CreatedIds;
        public string? LastQuery;

        public Task<PlaylistImportPreview> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            var entries = new[]
            {
                new PlaylistImportEntry("Hello", "Adele", ""),
                new PlaylistImportEntry("Stairway", "Led Zeppelin", ""),
                new PlaylistImportEntry("Yellow", "Coldplay", ""),
            };
            var matches = new List<MatchResult>
            {
                new(entries[0], Hello, 1),
                new(entries[1], null, 0.6) { Candidates = new[] { new MatchCandidate(Stairway, 0.6), new MatchCandidate(Kashmir, 0.46) } },
                new(entries[2], Yellow, 1),
            };
            return Task.FromResult(new PlaylistImportPreview
            {
                SuggestedName = "Road Trip",
                MatchedTrackIds = new[] { Hello.Id, Yellow.Id },
                MatchedLabels = new[] { "Adele – Hello", "Coldplay – Yellow" },
                MissingLabels = new[] { "Led Zeppelin – Stairway" },
                Matches = matches,
            });
        }

        public Task<PlaylistImportPreview> AnalyzeLinkAsync(string url, CancellationToken ct = default) => AnalyzeAsync(url, ct);

        public Task<Guid> CreateAsync(string name, IReadOnlyList<Guid> matchedTrackIds, CancellationToken ct = default)
        {
            CreatedIds = matchedTrackIds;
            return Task.FromResult(Guid.NewGuid());
        }

        public IReadOnlyList<Track> SearchLibrary(string query, int max)
        {
            LastQuery = query;
            return FuzzyTrackMatcher.Search(query, new[] { Hello, Yellow, Stairway, Kashmir }, max);
        }
    }

    private sealed class NoTidal : ITidalAuthService
    {
        public bool IsConnected => false;
        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<bool> LoginAsync(CancellationToken ct = default) => Task.FromResult(false);
        public void Disconnect() { }
    }

    private static async Task<(PlaylistImportViewModel Vm, Service Service)> Loaded()
    {
        var service = new Service();
        var vm = new PlaylistImportViewModel(service, new NoTidal());
        await vm.LoadFileAsync("C:/x.csv");
        return (vm, service);
    }

    [Fact]
    public async Task MissingRow_OffersItsCandidates()
    {
        var (vm, _) = await Loaded();
        var missing = vm.Rows[0];
        Assert.True(missing.IsMissing);
        Assert.True(missing.ShowCandidates);
        Assert.Equal(new[] { Stairway, Kashmir }, missing.Candidates.Select(c => c.Track));
        Assert.Equal("2 of 3 matched", vm.StatusMessage);
    }

    [Fact]
    public async Task PickingACandidate_MatchesTheRow_AndKeepsTheFilesOrder()
    {
        var (vm, service) = await Loaded();
        var missing = vm.Rows[0];
        missing.PickCommand.Execute(missing.Candidates[0]);

        Assert.True(missing.IsMatched);
        Assert.False(missing.ShowCandidates);
        Assert.Equal("Stairway to Heaven (Remaster) · Led Zeppelin", missing.PickLabel);
        Assert.Equal("3 of 3 matched", vm.StatusMessage);
        Assert.Equal(0, vm.MissingCount);

        await vm.CreateCommand.ExecuteAsync(null);
        // File order (Hello, Stairway, Yellow), not the list's missing-first order.
        Assert.Equal(new[] { Hello.Id, Stairway.Id, Yellow.Id }, service.CreatedIds);
        Assert.Equal("Created “Road Trip” with 3 tracks.", vm.StatusMessage);
    }

    [Fact]
    public async Task Change_PutsTheRowBackToMissing()
    {
        var (vm, _) = await Loaded();
        var missing = vm.Rows[0];
        missing.PickCommand.Execute(missing.Candidates[1]);
        missing.UnpickCommand.Execute(null);
        Assert.True(missing.IsMissing);
        Assert.True(missing.ShowCandidates);
        Assert.Equal("2 of 3 matched", vm.StatusMessage);
    }

    [Fact]
    public async Task Search_StartsFromTheEntry_AndAResultCanBePicked()
    {
        var (vm, service) = await Loaded();
        var missing = vm.Rows[0];
        missing.ToggleSearchCommand.Execute(null);
        Assert.True(missing.IsSearchOpen);
        Assert.False(missing.ShowCandidates);
        Assert.Equal("Stairway Led Zeppelin", missing.SearchText);
        Assert.Equal("Stairway Led Zeppelin", service.LastQuery);

        missing.SearchText = "kashmir";
        var hit = Assert.Single(missing.SearchResults);
        missing.PickCommand.Execute(hit);
        Assert.Same(Kashmir, missing.Picked);
        Assert.False(missing.IsSearchOpen);
    }

    [Fact]
    public async Task LeavingAMatchedSongOut_DropsItFromThePlaylist()
    {
        var (vm, service) = await Loaded();
        var hello = vm.Rows.Single(r => r.Title == "Hello");
        Assert.True(hello.CanToggleIncluded);
        hello.ToggleIncludedCommand.Execute(null);
        Assert.False(hello.IsIncluded);
        Assert.Equal("1 of 3 matched", vm.StatusMessage);

        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal(new[] { Yellow.Id }, service.CreatedIds);
    }

    [Fact]
    public async Task LeavingEverythingOut_DisablesCreate()
    {
        var (vm, _) = await Loaded();
        foreach (var r in vm.Rows.Where(r => r.IsMatched)) r.ToggleIncludedCommand.Execute(null);
        Assert.False(vm.CanCreate);
    }

    [Fact]
    public async Task AddingALibrarySong_AppendsItAfterTheImportedOnes()
    {
        var (vm, service) = await Loaded();
        vm.AddSearchText = "kashmir";
        var hit = Assert.Single(vm.AddSearchResults);
        vm.AddSongCommand.Execute(hit);
        Assert.Equal(string.Empty, vm.AddSearchText);
        Assert.Single(vm.AddedSongs);
        Assert.Equal("2 of 3 matched · 1 added", vm.StatusMessage);

        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal(new[] { Hello.Id, Yellow.Id, Kashmir.Id }, service.CreatedIds);
    }

    [Fact]
    public async Task RemovingAnAddedSong_TakesItBackOut()
    {
        var (vm, _) = await Loaded();
        vm.AddSearchText = "kashmir";
        vm.AddSongCommand.Execute(vm.AddSearchResults[0]);
        vm.RemoveAddedSongCommand.Execute(vm.AddedSongs[0]);
        Assert.Empty(vm.AddedSongs);
        Assert.Equal("2 of 3 matched", vm.StatusMessage);
    }

    [Fact]
    public void RealService_ReportsEveryEntryWithCandidates()
    {
        // The preview the real service builds carries the per-entry results the review reads.
        var lib = new[] { Hello, Stairway };
        var entries = new[] { new PlaylistImportEntry("Hello", "Adele", ""), new PlaylistImportEntry("Stairway to Heaven (Live)", "Led Zeppelin", "") };
        var matches = FuzzyTrackMatcher.Match(entries, lib);
        Assert.Same(Hello, matches[0].Match);
        Assert.Null(matches[1].Match);
        Assert.Same(Stairway, matches[1].Candidates[0].Track);
    }
}
