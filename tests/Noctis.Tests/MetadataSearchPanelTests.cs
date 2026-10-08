using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.MetadataSearch;
using Noctis.ViewModels;
using Noctis.ViewModels.MetadataSearch;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: Search metadata revamp — the editor's Find online panel against a fake
/// search service: query prefill, ranked results with per-provider status, the before/after
/// rows (changed, unchanged, blocked, only-fill-empty), apply into the edit fields without
/// saving, undo, the cover download/stage, album track mapping, cancellation, the
/// unavailable state and waiting for the editor's background load.
/// </summary>
public class MetadataSearchPanelTests
{
    // ── Fixtures ──

    private static Track T(string title, int n, Guid albumId, int seconds = 200) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
        Album = "nadie sabe", AlbumId = albumId, TrackNumber = n, TrackCount = 3, DiscNumber = 1,
        Genre = "Latin", Year = 2023, Duration = TimeSpan.FromSeconds(seconds),
        FilePath = "C:/m/does-not-exist/" + title + ".flac",
    };

    private static async Task<MetadataViewModel> SingleVm(FakeSearch? search, Track? track = null, bool load = true)
    {
        track ??= T("monaco", 2, Guid.NewGuid());
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var vm = new MetadataViewModel(track, new NullMetadataService(), lib, new TestPersistenceService(),
            new FakeAnimatedCoverService(), metadataSearch: search);
        if (load) await vm.InitializeAsync();
        return vm;
    }

    private static async Task<(MetadataViewModel vm, List<Track> tracks)> AlbumVm(FakeSearch search)
    {
        var albumId = Guid.NewGuid();
        var tracks = new List<Track> { T("NADIE SABE", 1, albumId, 377), T("monaco", 2, albumId, 267), T("FINA", 3, albumId, 216) };
        // Featured artist on one track: the album's Artist reads "Mixed".
        tracks[2].Artist = "Bad Bunny & Young Miko";
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var vm = new MetadataViewModel(tracks[0], new NullMetadataService(), lib, new TestPersistenceService(),
            new FakeAnimatedCoverService(), albumScoped: true, albumTracks: tracks, metadataSearch: search);
        await vm.InitializeAsync();
        return (vm, tracks);
    }

    private static MetadataCandidate Cand(string provider, double confidence, string title = "MONACO") => new()
    {
        Provider = provider, ProviderId = provider + confidence, Confidence = confidence,
        Title = title, Artist = "Bad Bunny", Album = "nadie sabe lo que va a pasar mañana",
        AlbumArtist = "Bad Bunny", ReleaseDate = "2023-10-13", Year = 2023, Genre = "Latin",
        TrackNumber = 2, TrackCount = 22, DiscNumber = 1, DiscCount = 1,
        Composer = "Benito Martínez", Label = "Rimas Entertainment", Copyright = "℗ 2023 Rimas",
        Isrc = "QM6MZ2370002", Explicit = true,
        MatchNotes = new[] { "Duration ±1 s" },
        ArtworkUrl = new Uri("https://example.test/cover.jpg"), ArtworkSize = 1000,
    };

    private static byte[] Jpeg(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(new SKColor(200, 40, 60));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    // ── Tests ──

    [AvaloniaFact]
    public async Task Open_PrefillsTheQueryFromTheEditFields_AndSearches()
    {
        var search = new FakeSearch();
        var vm = await SingleVm(search);
        vm.Title = "MONACO (edited)";

        vm.OpenSearchPanelCommand.Execute(null);

        var panel = vm.SearchPanel;
        Assert.True(panel.IsOpen);
        Assert.Equal("MONACO (edited)", panel.QueryTitle);
        Assert.Equal("Bad Bunny", panel.QueryArtist);
        Assert.Equal("nadie sabe", panel.QueryAlbum);
        var q = Assert.Single(search.Queries);
        Assert.False(q.AlbumScope);
        Assert.Equal("MONACO (edited)", q.Title);
        Assert.Equal(TimeSpan.FromSeconds(200), q.Duration);
        Assert.Equal(2, q.TrackNumber);
        Assert.Empty(q.Providers); // every chip on = "all enabled"
    }

    [AvaloniaFact]
    public async Task Query_CarriesTheUsersTrackAndDiscTotals_SoTheEngineCanMatchTheEdition()
    {
        // Owner 10-08: an ISRC match on the 15-track standard release proposed
        // "Track count 17 → 15" for a 17-track copy; the engine needs the local totals.
        var search = new FakeSearch();
        var vm = await SingleVm(search);
        vm.TrackCount = "17";
        vm.DiscCount = "1";

        vm.OpenSearchPanelCommand.Execute(null);

        var q = Assert.Single(search.Queries);
        Assert.Equal(17, q.TrackCount);
        Assert.Equal(1, q.DiscCount);
    }

    [AvaloniaFact]
    public async Task AlbumScope_PrefillsAlbumAndAlbumArtist_AndSendsTheLocalTracks()
    {
        var search = new FakeSearch();
        var (vm, tracks) = await AlbumVm(search);
        vm.OpenSearchPanelCommand.Execute(null);

        Assert.True(vm.SearchPanel.AlbumScope);
        Assert.Equal("nadie sabe", vm.SearchPanel.QueryAlbum);
        Assert.Equal("Bad Bunny", vm.SearchPanel.QueryAlbumArtist);
        var q = Assert.Single(search.Queries);
        Assert.True(q.AlbumScope);
        Assert.Equal(string.Empty, q.Title);
        Assert.Equal(tracks.Count, q.AlbumTracks.Count);
    }

    [AvaloniaFact]
    public async Task Search_SortsBestFirst_AndReportsEveryProvider()
    {
        var search = new FakeSearch
        {
            Result = new MetadataSearchResult
            {
                Candidates = new[] { Cand("MusicBrainz", 0.55), Cand("Deezer", 0.93), Cand("Deezer", 0.71) },
                Providers = new[]
                {
                    new ProviderStatus("Deezer", ProviderOutcome.Ok, 2),
                    new ProviderStatus("MusicBrainz", ProviderOutcome.TimedOut, 1),
                    new ProviderStatus("Apple Music", ProviderOutcome.Failed, 0, "offline"),
                },
            },
        };
        var vm = await SingleVm(search);
        vm.SearchPanel.Open();
        var panel = vm.SearchPanel;

        Assert.True(panel.HasResults);
        Assert.Equal(new[] { 0.93, 0.71, 0.55 }, panel.Candidates.Select(c => c.Candidate.Confidence));
        Assert.Same(panel.Candidates[0], panel.SelectedCandidate);
        Assert.True(panel.Candidates[0].IsStrong);
        Assert.True(panel.Candidates[2].IsWeak);
        Assert.Equal("93%", panel.Candidates[0].ConfidenceText);
        Assert.Equal("Deezer 2 · MusicBrainz timed out · Apple Music offline", panel.ProviderStatusLine);
        Assert.True(panel.ProviderStatuses[2].IsProblem);
    }

    [AvaloniaFact]
    public async Task NoResults_ShowsTheEmptyState_WithProviderStatus()
    {
        var search = new FakeSearch
        {
            Result = new MetadataSearchResult
            {
                Providers = new[] { new ProviderStatus("Deezer", ProviderOutcome.NoResults, 0) },
            },
        };
        var vm = await SingleVm(search);
        vm.SearchPanel.Open();
        Assert.True(vm.SearchPanel.IsEmpty);
        Assert.True(vm.SearchPanel.ShowMessage);
        Assert.Null(vm.SearchPanel.SelectedCandidate);
        Assert.Equal("Deezer 0", vm.SearchPanel.ProviderStatusLine);
    }

    [AvaloniaFact]
    public async Task EverySourceDown_ReadsAsFailed_NotAsNoMatches()
    {
        var search = new FakeSearch
        {
            Result = new MetadataSearchResult
            {
                Providers = new[]
                {
                    new ProviderStatus("Deezer", ProviderOutcome.Failed, 0, "offline"),
                    new ProviderStatus("MusicBrainz", ProviderOutcome.TimedOut, 0),
                    new ProviderStatus("Apple Music", ProviderOutcome.Disabled, 0),
                },
            },
        };
        var vm = await SingleVm(search);
        vm.SearchPanel.Open();
        Assert.True(vm.SearchPanel.IsFailed);
        Assert.True(vm.SearchPanel.ShowMessage);

        // One source answering with nothing is a real "no matches".
        search.Result = new MetadataSearchResult
        {
            Providers = new[]
            {
                new ProviderStatus("Deezer", ProviderOutcome.Failed, 0, "offline"),
                new ProviderStatus("MusicBrainz", ProviderOutcome.NoResults, 0),
            },
        };
        await vm.SearchPanel.SearchAsync();
        Assert.True(vm.SearchPanel.IsEmpty);
    }

    [AvaloniaFact]
    public async Task ProviderChips_FilterTheQuery_AndTheLastOneStaysOn()
    {
        var search = new FakeSearch();
        var vm = await SingleVm(search);
        var panel = vm.SearchPanel;
        panel.Open();

        panel.Providers[1].IsSelected = false; // re-runs the search without MusicBrainz
        Assert.Equal(new[] { "Deezer", "Apple Music" }, search.Queries.Last().Providers);

        panel.Providers[0].IsSelected = false;
        panel.Providers[2].IsSelected = false; // the last one on: refused
        Assert.True(panel.Providers[2].IsSelected);
        Assert.Equal(new[] { "Apple Music" }, search.Queries.Last().Providers);
    }

    [AvaloniaFact]
    public async Task ProviderChip_ChangedMidSearch_RestartsWithTheNewSources()
    {
        // The running search used to finish with the old sources while the chips showed new ones.
        var search = new FakeSearch { Gate = true };
        var vm = await SingleVm(search);
        var panel = vm.SearchPanel;
        panel.Open();
        Assert.True(panel.IsSearching);

        panel.Providers[0].IsSelected = false;
        Assert.Equal(2, search.Queries.Count);
        Assert.True(search.Tokens[0].IsCancellationRequested);
        Assert.Equal(new[] { "MusicBrainz", "Apple Music" }, search.Queries[1].Providers);
    }

    [AvaloniaFact]
    public async Task Selecting_BuildsChangedUnchangedAndBlockedRows()
    {
        var search = new FakeSearch { Result = One(Cand("Deezer", 0.9)) };
        var vm = await SingleVm(search);
        vm.SearchPanel.Open();
        var rows = vm.SearchPanel.Rows.ToDictionary(r => r.Field);

        // Changed: title case differs, album longer, composer was empty.
        Assert.True(rows[MetadataSearchField.Title].IsChanged);
        Assert.Equal("monaco", rows[MetadataSearchField.Title].Current);
        Assert.Equal("MONACO", rows[MetadataSearchField.Title].New);
        Assert.True(rows[MetadataSearchField.Album].IsChanged);
        Assert.True(rows[MetadataSearchField.Composer].IsFill);
        Assert.Equal("—", rows[MetadataSearchField.Composer].Current);
        // Unchanged: same artist, year, genre, numbers ("2" vs 2).
        Assert.False(rows[MetadataSearchField.Artist].IsChanged);
        Assert.False(rows[MetadataSearchField.Year].IsChanged);
        Assert.False(rows[MetadataSearchField.TrackNumber].IsChanged);
        // The test file has no readable extended tags: label/ISRC/date/explicit are shown but blocked.
        Assert.True(rows[MetadataSearchField.Label].IsBlocked);
        Assert.False(rows[MetadataSearchField.Label].IsChecked);
        Assert.True(rows[MetadataSearchField.Isrc].IsBlocked);
        Assert.True(rows[MetadataSearchField.Explicit].IsBlocked);
        // Copyright lives in the main tag write: storable.
        Assert.False(rows[MetadataSearchField.Copyright].IsBlocked);
        Assert.True(rows[MetadataSearchField.Copyright].IsChecked);

        // Unchanged rows and rows this editor can't store hide until "Show all fields";
        // the summary counts only what can change.
        var panel = vm.SearchPanel;
        Assert.DoesNotContain(panel.VisibleRows, r => r.Field == MetadataSearchField.Artist);
        Assert.DoesNotContain(panel.VisibleRows, r => r.Field == MetadataSearchField.Label);
        Assert.All(panel.VisibleRows, r => Assert.True(r.CanToggle));
        Assert.Equal($"{panel.ChangeableFieldCount + 1} changes", panel.FieldSummary); // + the cover
        Assert.True(panel.ChangeableFieldCount < panel.ChangedFieldCount);
        Assert.False(panel.HasActiveOptions);
        panel.ShowUnchanged = true;
        Assert.True(panel.HasActiveOptions);
        Assert.Contains(panel.VisibleRows, r => r.Field == MetadataSearchField.Artist);
        Assert.Contains(panel.VisibleRows, r => r.Field == MetadataSearchField.Label);
        Assert.Equal(panel.Rows.Count, panel.VisibleRows.Count);
    }

    [AvaloniaFact]
    public async Task MasterTick_ReadsAllSomeNone_AndTogglesEverything()
    {
        var search = new FakeSearch { Result = One(Cand("Deezer", 0.9)), Artwork = Jpeg(600, 600) };
        var vm = await SingleVm(search);
        var panel = vm.SearchPanel;
        panel.Open();
        Assert.True(panel.HasSelectable);
        // Conservative defaults: some ticked, some not.
        Assert.Null(panel.AllSelected);

        // Partial → all (fields and the cover).
        panel.ToggleAllCommand.Execute(null);
        Assert.True(panel.AllSelected);
        Assert.All(panel.Rows.Where(r => r.CanToggle), r => Assert.True(r.IsChecked));
        Assert.True(panel.UseArtwork);

        // All → none.
        panel.ToggleAllCommand.Execute(null);
        Assert.False(panel.AllSelected);
        Assert.Equal(0, panel.SelectedChangeCount);
        Assert.Equal("Apply", panel.ApplyText);

        panel.Rows.First(r => r.CanToggle).IsChecked = true;
        Assert.Null(panel.AllSelected);
        Assert.Equal("Apply 1", panel.ApplyText);
    }

    [AvaloniaFact]
    public async Task SourcesMenu_ShowsEachSourcesOutcome_AndOnlyProblemsSurface()
    {
        var search = new FakeSearch
        {
            Result = new MetadataSearchResult
            {
                Candidates = new[] { Cand("Deezer", 0.9) },
                Providers = new[]
                {
                    new ProviderStatus("Deezer", ProviderOutcome.Ok, 2),
                    new ProviderStatus("MusicBrainz", ProviderOutcome.NoResults, 0),
                    new ProviderStatus("Apple Music", ProviderOutcome.Failed, 0, "offline"),
                },
            },
        };
        var vm = await SingleVm(search);
        var panel = vm.SearchPanel;
        panel.Open();

        Assert.Equal(new[] { "2", "0", "offline" }, panel.Providers.Select(p => p.StatusText));
        Assert.True(panel.Providers[0].IsOk);
        Assert.True(panel.Providers[2].IsProblem);
        Assert.True(panel.ShowProviderProblem);
        Assert.Equal("Apple Music offline", panel.ProviderProblemText);
        Assert.Equal("Source: Deezer", panel.FooterText);
        Assert.Equal(3, panel.SelectedSourceCount);

        panel.Providers[1].IsSelected = false;
        Assert.Equal(2, panel.SelectedSourceCount);
    }

    [AvaloniaFact]
    public async Task DefaultTicks_FillEmpty_ButOverwriteOnlyRecordingFacts()
    {
        // Owner 10-08 "fix both": a different edition pre-ticked Track count 17 → 15, and
        // MusicBrainz credit names replaced full composer names.
        var track = T("monaco", 2, Guid.NewGuid());
        track.TrackCount = 17;
        track.DiscCount = 2;
        track.Composer = "Benito Antonio Martínez Ocasio";
        var search = new FakeSearch { Result = One(Cand("MusicBrainz", 0.95) with { TrackCount = 15, DiscCount = 1, Bpm = 136, Genre = "Música Urbana" }) };
        var vm = await SingleVm(search, track);
        var panel = vm.SearchPanel;
        panel.Open();
        var rows = panel.Rows.ToDictionary(r => r.Field);

        // Overwrites of edition-, credit- or taste-dependent values start unticked…
        foreach (var f in new[]
                 {
                     MetadataSearchField.TrackCount, MetadataSearchField.DiscCount, MetadataSearchField.Composer,
                     MetadataSearchField.Album, MetadataSearchField.Genre,
                 })
        {
            Assert.True(rows[f].CanToggle, f.ToString());
            Assert.False(rows[f].IsChecked, f.ToString());
        }
        // …a fix to the recording itself stays ticked, and so does filling a gap.
        Assert.True(rows[MetadataSearchField.Title].IsChecked);   // monaco → MONACO
        Assert.True(rows[MetadataSearchField.Bpm].IsFill);
        Assert.True(rows[MetadataSearchField.Bpm].IsChecked);
        Assert.True(rows[MetadataSearchField.Copyright].IsChecked);

        // The master tick and "Only fill empty" still reach them; turning the latter off
        // goes back to the conservative defaults.
        panel.SelectAllCommand.Execute(null);
        Assert.True(rows[MetadataSearchField.TrackCount].IsChecked);
        panel.OnlyFillEmpty = true;
        Assert.False(rows[MetadataSearchField.Title].IsChecked);
        Assert.True(rows[MetadataSearchField.Bpm].IsChecked);
        panel.OnlyFillEmpty = false;
        Assert.True(rows[MetadataSearchField.Title].IsChecked);
        Assert.False(rows[MetadataSearchField.Composer].IsChecked);
        Assert.False(rows[MetadataSearchField.TrackCount].IsChecked);
    }

    [Fact]
    public void SafeOverwrite_AlbumNamesOnlyInTheAlbumEditor()
    {
        Assert.False(MetadataSearchPanelViewModel.IsSafeOverwrite(MetadataSearchField.Album, albumScope: false));
        Assert.True(MetadataSearchPanelViewModel.IsSafeOverwrite(MetadataSearchField.Album, albumScope: true));
        Assert.True(MetadataSearchPanelViewModel.IsSafeOverwrite(MetadataSearchField.AlbumArtist, albumScope: true));
        foreach (var f in new[]
                 {
                     MetadataSearchField.Year, MetadataSearchField.ReleaseDate, MetadataSearchField.TrackNumber,
                     MetadataSearchField.TrackCount, MetadataSearchField.DiscNumber, MetadataSearchField.DiscCount,
                     MetadataSearchField.Composer, MetadataSearchField.Label, MetadataSearchField.Copyright,
                     MetadataSearchField.Barcode, MetadataSearchField.Genre,
                 })
        {
            Assert.False(MetadataSearchPanelViewModel.IsSafeOverwrite(f, albumScope: false), f.ToString());
            Assert.False(MetadataSearchPanelViewModel.IsSafeOverwrite(f, albumScope: true), f.ToString());
        }
    }

    [AvaloniaFact]
    public async Task OnlyFillEmpty_TicksJustTheEmptyFields_AndSelectNoneAll()
    {
        var search = new FakeSearch { Result = One(Cand("Deezer", 0.9)) };
        var vm = await SingleVm(search);
        var panel = vm.SearchPanel;
        panel.Open();

        panel.OnlyFillEmpty = true;
        var ticked = panel.Rows.Where(r => r.IsChecked).Select(r => r.Field).ToHashSet();
        Assert.Contains(MetadataSearchField.Composer, ticked);
        Assert.Contains(MetadataSearchField.Copyright, ticked);
        Assert.DoesNotContain(MetadataSearchField.Title, ticked);
        Assert.DoesNotContain(MetadataSearchField.Album, ticked);

        panel.SelectNoneCommand.Execute(null);
        Assert.Equal(0, panel.SelectedChangeCount);
        Assert.False(panel.ApplyCommand.CanExecute(null));
        panel.SelectAllCommand.Execute(null);
        Assert.All(panel.Rows.Where(r => r.CanToggle), r => Assert.True(r.IsChecked));
        Assert.All(panel.Rows.Where(r => r.IsBlocked), r => Assert.False(r.IsChecked));
    }

    [AvaloniaFact]
    public async Task Apply_WritesOnlyTickedFields_WithoutSaving_AndUndoRestores()
    {
        var search = new FakeSearch { Result = One(Cand("Deezer", 0.9)) };
        var track = T("monaco", 2, Guid.NewGuid());
        var vm = await SingleVm(search, track);
        var panel = vm.SearchPanel;
        panel.Open();
        Assert.Equal(0, vm.ChangeCount);

        // Tick just title, composer and copyright (the album and the rest stay as they are).
        panel.SelectNoneCommand.Execute(null);
        foreach (var f in new[] { MetadataSearchField.Title, MetadataSearchField.Composer, MetadataSearchField.Copyright })
            panel.Rows.Single(r => r.Field == f).IsChecked = true;
        Assert.Equal(3, panel.SelectedChangeCount);
        Assert.Equal("Apply 3", panel.ApplyText);
        panel.ApplyCommand.Execute(null);

        Assert.False(panel.IsOpen);
        Assert.Equal("MONACO", vm.Title);
        Assert.Equal("nadie sabe", vm.Album);           // unticked: untouched
        Assert.Equal("Benito Martínez", vm.Composer);
        Assert.Equal("℗ 2023 Rimas", vm.Copyright);
        Assert.Equal(3, vm.ChangeCount);                 // the footer counts them
        Assert.True(vm.DetailsChanged);
        Assert.Equal("monaco", track.Title);             // nothing saved
        Assert.True(vm.HasSearchApplied);
        Assert.True(vm.CanUndoSearchApply);
        Assert.Equal("Applied 3 changes", vm.SearchAppliedText);

        // A field edited again after the apply keeps the newer edit through Undo.
        vm.Composer = "Bad Bunny";
        vm.UndoSearchApplyCommand.Execute(null);
        Assert.Equal("monaco", vm.Title);
        Assert.Equal(string.Empty, vm.Copyright);
        Assert.Equal("Bad Bunny", vm.Composer);
        Assert.Equal(1, vm.ChangeCount);
        Assert.False(vm.HasSearchApplied);
        Assert.False(vm.CanUndoSearchApply);
    }

    [AvaloniaFact]
    public async Task Artwork_DownloadsOnlyWhenWanted_StagesTheBytes_AndUndoRemovesIt()
    {
        var bytes = Jpeg(1000, 1000);
        var search = new FakeSearch { Result = One(Cand("Deezer", 0.9)), Artwork = bytes };
        var vm = await SingleVm(search);
        var panel = vm.SearchPanel;
        Assert.False(vm.HasArtwork);

        panel.Open();
        // No cover yet, so the candidate's is pre-chosen and fetched once.
        Assert.True(panel.HasNewArtwork);
        Assert.True(panel.UseArtwork);
        Assert.Equal(1, search.ArtworkCalls);
        Assert.False(panel.IsDownloadingArtwork);
        Assert.Equal("1000 × 1000", panel.NewArtworkSize);
        Assert.True(panel.HasNewArtworkPreview);
        // Toggling off and on again reuses the download.
        panel.UseArtwork = false;
        panel.UseArtwork = true;
        Assert.Equal(1, search.ArtworkCalls);

        panel.SelectNoneCommand.Execute(null);
        panel.UseArtwork = true;
        Assert.Equal(1, panel.SelectedChangeCount);
        panel.ApplyCommand.Execute(null);

        Assert.True(vm.HasArtwork);
        Assert.True(vm.ArtworkChanged);
        Assert.Equal("1000 × 1000", vm.ArtworkDimensions);
        Assert.Equal(1, vm.ChangeCount);

        vm.UndoSearchApplyCommand.Execute(null);
        Assert.False(vm.HasArtwork);
        Assert.False(vm.ArtworkChanged);
        Assert.Equal(0, vm.ChangeCount);
    }

    [AvaloniaFact]
    public async Task Artwork_FailedDownload_UnticksAndSays()
    {
        var search = new FakeSearch { Result = One(Cand("Deezer", 0.9)), Artwork = null };
        var vm = await SingleVm(search);
        vm.SearchPanel.Open();
        Assert.False(vm.SearchPanel.UseArtwork);
        Assert.Equal("Download failed", vm.SearchPanel.ArtworkNote);
    }

    [AvaloniaFact]
    public async Task AlbumScope_MapsTracks_AndAppliesOnlyIncludedOnSave()
    {
        var search = new FakeSearch();
        var (vm, tracks) = await AlbumVm(search);
        var release = Cand("Deezer", 0.95) with
        {
            Title = string.Empty, Album = "nadie sabe lo que va a pasar mañana", Artist = "Bad Bunny",
            TrackNumber = null, Composer = string.Empty,
            Tracks = new[]
            {
                new CandidateTrack { Title = "NADIE SABE", TrackNumber = 1, DiscNumber = 1, Duration = TimeSpan.FromSeconds(378), MatchedLocalTrackId = tracks[0].Id },
                new CandidateTrack { Title = "MONACO", TrackNumber = 2, DiscNumber = 1, Duration = TimeSpan.FromSeconds(267), MatchedLocalTrackId = tracks[1].Id },
                new CandidateTrack { Title = "FINA", TrackNumber = 4, DiscNumber = 1, Duration = TimeSpan.FromSeconds(216), MatchedLocalTrackId = tracks[2].Id },
                new CandidateTrack { Title = "HIBIKI", TrackNumber = 5, DiscNumber = 1, Duration = TimeSpan.FromSeconds(208) },
            },
        };
        search.Result = One(release);
        var panel = vm.SearchPanel;
        panel.Open();

        // Album-wide rows: no per-track title/number rows; label etc. are blocked; the
        // release artist over a "Mixed" artist waits for an explicit tick.
        var rows = panel.Rows.ToDictionary(r => r.Field);
        Assert.False(rows.ContainsKey(MetadataSearchField.Title));
        Assert.False(rows.ContainsKey(MetadataSearchField.TrackNumber));
        Assert.True(rows[MetadataSearchField.Label].IsBlocked);
        Assert.Equal("Mixed", rows[MetadataSearchField.Artist].Current);
        Assert.False(rows[MetadataSearchField.Artist].IsChecked);
        Assert.True(rows[MetadataSearchField.Album].IsChecked);
        Assert.True(rows[MetadataSearchField.Explicit].CanToggle); // the "Album is explicit" box

        var tr = panel.TrackRows;
        Assert.Equal(3, tr.Count);
        Assert.All(tr, r => Assert.True(r.IsMatched));
        Assert.False(tr[0].IsChanged);                // same title and number
        Assert.Equal("+1 s", tr[0].DeltaText);
        Assert.True(tr[1].TitleChanges);              // monaco → MONACO
        Assert.True(tr[2].NumberChanges);             // 3 → 4
        Assert.True(tr[1].IsIncluded);
        Assert.Equal("+1 not in your library", panel.TrackExtraNote);

        tr[2].IsIncluded = false;
        // (The explicit flag is written per file by Save, which the fake files can't take.)
        rows[MetadataSearchField.Explicit].IsChecked = false;
        panel.ApplyCommand.Execute(null);

        // Staged, not written: the tracks are untouched until Save, and counted.
        Assert.Equal("monaco", tracks[1].Title);
        Assert.Equal("nadie sabe lo que va a pasar mañana", vm.Album);
        Assert.True(vm.ChangeCount >= 2);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("MONACO", tracks[1].Title);
        Assert.Equal(3, tracks[2].TrackNumber);       // excluded row kept its number
        Assert.Equal("nadie sabe lo que va a pasar mañana", tracks[2].Album);
        Assert.Equal("Bad Bunny & Young Miko", tracks[2].Artist); // Mixed artist untouched
    }

    [AvaloniaFact]
    public async Task AlbumScope_UndoDropsTheStagedTracks()
    {
        var search = new FakeSearch();
        var (vm, tracks) = await AlbumVm(search);
        search.Result = One(Cand("Deezer", 0.9) with
        {
            Tracks = new[] { new CandidateTrack { Title = "MONACO", TrackNumber = 2, MatchedLocalTrackId = tracks[1].Id } },
        });
        vm.SearchPanel.Open();
        vm.SearchPanel.SelectNoneCommand.Execute(null);
        vm.SearchPanel.TrackRows.Single(r => r.Local == tracks[1]).IsIncluded = true;
        vm.SearchPanel.ApplyCommand.Execute(null);
        Assert.Equal(1, vm.ChangeCount);

        vm.UndoSearchApplyCommand.Execute(null);
        Assert.Equal(0, vm.ChangeCount);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("monaco", tracks[1].Title);
    }

    [Fact]
    public void TrackMatching_FallsBackToNumbersThenPosition()
    {
        var id = Guid.NewGuid();
        var locals = new List<Track> { T("a", 1, id), T("b", 2, id), T("c", 0, id) };
        var remote = new List<CandidateTrack>
        {
            new() { Title = "B", TrackNumber = 2 }, new() { Title = "A", TrackNumber = 1 }, new() { Title = "C", TrackNumber = 9 },
        };
        var m = MetadataSearchPanelViewModel.MatchTracks(locals, remote);
        Assert.Equal("A", m[locals[0]].Title);
        Assert.Equal("B", m[locals[1]].Title);
        Assert.Equal("C", m[locals[2]].Title); // no number: same count, so by position
    }

    [AvaloniaFact]
    public async Task ReSearch_CancelsTheRunningOne_AndCloseCancelsToo()
    {
        var search = new FakeSearch { Gate = true };
        var vm = await SingleVm(search);
        var panel = vm.SearchPanel;
        panel.Open();
        Assert.True(panel.IsSearching);
        var first = search.Tokens[0];

        var second = panel.SearchAsync();
        Assert.True(first.IsCancellationRequested);
        Assert.False(search.Tokens[1].IsCancellationRequested);

        // The cancelled run finishing late must not overwrite the newer one.
        search.Release(0, One(Cand("Old", 0.99)));
        search.Release(1, One(Cand("Deezer", 0.8)));
        await second;
        Assert.Equal("Deezer", Assert.Single(panel.Candidates).Provider);

        _ = panel.SearchAsync();
        panel.Close();
        Assert.True(search.Tokens[2].IsCancellationRequested);
        Assert.False(panel.IsOpen);
    }

    [AvaloniaFact]
    public async Task NoService_ShowsUnavailable()
    {
        var vm = await SingleVm(search: null);
        vm.OpenSearchPanelCommand.Execute(null);
        Assert.True(vm.SearchPanel.IsOpen);
        Assert.True(vm.SearchPanel.IsUnavailable);
        Assert.False(vm.SearchPanel.IsAvailable);
        Assert.Equal("Search unavailable", vm.SearchPanel.MessageTitle);
    }

    [AvaloniaFact]
    public async Task WhileTheEditorLoads_TheSearchWaits_ThenRuns()
    {
        var search = new FakeSearch { Result = One(Cand("Deezer", 0.9)) };
        var vm = await SingleVm(search, load: false);
        Assert.True(vm.IsLoading);

        vm.SearchPanel.Open();
        Assert.True(vm.SearchPanel.IsWaitingForLoad);
        Assert.Empty(search.Queries);

        await vm.InitializeAsync();
        Assert.Single(search.Queries);
        Assert.True(vm.SearchPanel.HasResults);
    }

    [AvaloniaFact]
    public async Task MultiSelect_HasNoFindOnline()
    {
        var id = Guid.NewGuid();
        var tracks = new List<Track> { T("a", 1, id), T("b", 2, Guid.NewGuid()) };
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var search = new FakeSearch();
        var vm = new MetadataViewModel(tracks[0], new NullMetadataService(), lib, new TestPersistenceService(),
            new FakeAnimatedCoverService(), albumScoped: true, albumTracks: tracks, multiSelect: true, metadataSearch: search);
        await vm.InitializeAsync();
        Assert.False(vm.ShowSearchMetadata);
        vm.OpenSearchPanelCommand.Execute(null);
        Assert.False(vm.SearchPanel.IsOpen);
        Assert.Empty(search.Queries);
    }

    private static MetadataSearchResult One(MetadataCandidate c) => new()
    {
        Candidates = new[] { c },
        Providers = new[] { new ProviderStatus(c.Provider, ProviderOutcome.Ok, 1) },
    };

    // ── Fakes ──

    internal sealed class FakeSearch : IMetadataSearchService
    {
        public IReadOnlyList<string> Providers { get; set; } = new[] { "Deezer", "MusicBrainz", "Apple Music" };
        public MetadataSearchResult Result { get; set; } = new();
        public byte[]? Artwork { get; set; }
        public int ArtworkCalls { get; private set; }
        public List<MetadataQuery> Queries { get; } = new();
        public List<CancellationToken> Tokens { get; } = new();
        /// <summary>When set, each search waits until <see cref="Release"/> (or cancellation).</summary>
        public bool Gate { get; set; }
        private readonly List<TaskCompletionSource<MetadataSearchResult>> _pending = new();

        public Task<MetadataSearchResult> SearchAsync(MetadataQuery query, CancellationToken ct = default)
        {
            Queries.Add(query);
            Tokens.Add(ct);
            if (!Gate) return Task.FromResult(Result);
            var tcs = new TaskCompletionSource<MetadataSearchResult>();
            _pending.Add(tcs);
            return tcs.Task;
        }

        /// <summary>Completes run <paramref name="index"/>, even if it was cancelled — a
        /// provider that ignores the token must not be able to clobber newer results.</summary>
        public void Release(int index, MetadataSearchResult result) => _pending[index].TrySetResult(result);

        public Task<byte[]?> DownloadArtworkAsync(MetadataCandidate candidate, CancellationToken ct = default)
        {
            ArtworkCalls++;
            return Task.FromResult(Artwork);
        }
    }

    private sealed class NullMetadataService : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => true;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => true;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => true;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields, AdvancedTagIO.AdvancedFields original) => true;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => true;
    }
}
