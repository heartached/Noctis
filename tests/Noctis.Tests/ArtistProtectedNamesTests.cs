using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.MetadataSearch;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "Keep these names together" (Discord, aaron 2026-10-18): names like "Tyler, The Creator"
/// contain a default separator and were cut in two; removing "," broke "A, B" collaboration
/// credits instead. Protected names now come out whole wherever a credit is split.
/// Pure overloads only here (parallel-safe).
/// </summary>
public class ArtistProtectedNamesTests
{
    private static readonly string[] Seps = ArtistCredit.DefaultSeparators.ToArray();
    private static readonly string[] Names = ArtistCredit.DefaultProtectedNames.ToArray();

    [Theory]
    [InlineData("Tyler, The Creator", new[] { "Tyler, The Creator" })]                                   // alone
    [InlineData("Tyler, The Creator, Kali Uchis", new[] { "Tyler, The Creator", "Kali Uchis" })]         // at start
    [InlineData("Kali Uchis, Tyler, The Creator", new[] { "Kali Uchis", "Tyler, The Creator" })]         // at end
    [InlineData("A, Tyler, The Creator; B", new[] { "A", "Tyler, The Creator", "B" })]                   // in the middle
    [InlineData("Tyler, The Creator feat. Kali Uchis", new[] { "Tyler, The Creator", "Kali Uchis" })]    // word separator after
    [InlineData("Kali Uchis feat. Tyler, The Creator", new[] { "Kali Uchis", "Tyler, The Creator" })]    // word separator before
    [InlineData("tyler, the creator", new[] { "tyler, the creator" })]                                   // case-insensitive, kept as tagged
    [InlineData("Tyler,The Creator / X", new[] { "Tyler,The Creator", "X" })]                            // spacing differs
    [InlineData("Tyler, The Creator, Tyler, The Creator", new[] { "Tyler, The Creator" })]               // de-duplicated
    [InlineData("Crosby, Stills, Nash & Young", new[] { "Crosby, Stills, Nash & Young" })]               // longer default wins
    [InlineData("Crosby, Stills & Nash", new[] { "Crosby, Stills & Nash" })]
    [InlineData("Earth, Wind & Fire, Emerson, Lake & Palmer", new[] { "Earth, Wind & Fire", "Emerson, Lake & Palmer" })]
    [InlineData("AC/DC", new[] { "AC/DC" })]
    [InlineData("AC/DC / Metallica", new[] { "AC/DC", "Metallica" })]
    [InlineData("Now, Now", new[] { "Now, Now" })]
    [InlineData("A, B", new[] { "A", "B" })]                                                             // collaborations still split
    public void DefaultNames_StayWhole_OtherCreditsStillSplit(string credit, string[] expected)
        => Assert.Equal(expected, ArtistCredit.Split(credit, Seps, Names));

    [Theory]
    [InlineData("Now, Now Every Children", new[] { "Now", "Now Every Children" })]      // name runs on: not a whole name
    [InlineData("The Tyler, The Creator Tribute", new[] { "The Tyler", "The Creator Tribute" })]
    [InlineData("Tyler, The Creators", new[] { "Tyler", "The Creators" })]
    public void OnlyWholeNames_BoundedBySeparatorsOrEdges_AreKept(string credit, string[] expected)
        => Assert.Equal(expected, ArtistCredit.Split(credit, Seps, Names));

    [Fact]
    public void AShorterName_InsideALongerOne_PicksWhatTheCreditBounds()
    {
        var names = new[] { "A, B", "A, B, C" };
        Assert.Equal(new[] { "A, B, C" }, ArtistCredit.Split("A, B, C", Seps, names));
        Assert.Equal(new[] { "A, B", "D" }, ArtistCredit.Split("A, B, D", Seps, names));
        // The longer name fails its end boundary ("Cx"), the shorter one still applies.
        Assert.Equal(new[] { "A, B", "Cx" }, ArtistCredit.Split("A, B, Cx", Seps, names));
        Assert.Equal(new[] { "X", "A, B, C" }, ArtistCredit.Split("X; A, B, C", Seps, names));
    }

    [Fact]
    public void RemovedDefault_SplitsAgain()
    {
        var withoutTyler = Names.Where(n => n != "Tyler, The Creator").ToArray();
        Assert.Equal(new[] { "Tyler", "The Creator" }, ArtistCredit.Split("Tyler, The Creator", Seps, withoutTyler));
        Assert.Equal(new[] { "Tyler", "The Creator" }, ArtistCredit.Split("Tyler, The Creator", Seps, Array.Empty<string>()));
        // The two-argument overload protects nothing (historical behaviour): a tight "/" splits.
        Assert.Equal(new[] { "AC", "DC" }, ArtistCredit.Split("AC/DC", Seps));
    }

    [Fact]
    public void ProtectedNames_SurviveUserAddedSeparators()
    {
        var withAmp = Seps.Append("&").ToArray();
        Assert.Equal(new[] { "Earth, Wind & Fire", "Simon", "Garfunkel" },
            ArtistCredit.Split("Earth, Wind & Fire, Simon & Garfunkel", withAmp, Names));
    }

    [Fact]
    public void NormalizeProtectedNames_TrimsDedupesAndKeepsEmpty()
    {
        Assert.Equal(new[] { "Tyler, The Creator", "AC/DC" },
            ArtistCredit.NormalizeProtectedNames(new[] { " Tyler, The Creator ", "", "tyler, the creator", "AC/DC", null! }));
        Assert.Empty(ArtistCredit.NormalizeProtectedNames(Array.Empty<string>()));
        Assert.Empty(ArtistCredit.NormalizeProtectedNames(null));
    }

    [Fact]
    public void DefaultNames_EachContainADefaultSeparator()
    {
        // The list only exists to undo a split; a name with no separator would be dead weight.
        // ("Now, Now" without protection even collapses to the one name "Now".)
        foreach (var name in Names)
            Assert.NotEqual(new[] { name }, ArtistCredit.Split(name, Seps));
    }

    [Fact]
    public void Signature_ChangesWithTheNameList()
    {
        var a = ArtistCredit.BuildSignature(ArtistGroupMode.Artist, Seps, Names);
        Assert.Equal(a, ArtistCredit.BuildSignature(ArtistGroupMode.Artist, Seps));        // 2-arg = defaults
        Assert.Equal(a, ArtistCredit.BuildSignature(ArtistGroupMode.Artist, Seps, Names.Select(n => " " + n)));
        Assert.NotEqual(a, ArtistCredit.BuildSignature(ArtistGroupMode.Artist, Seps, Names.Skip(1)));
        Assert.NotEqual(a, ArtistCredit.BuildSignature(ArtistGroupMode.Artist, Seps, Names.Append("Now, Now, Now")));
        Assert.NotEqual(a, ArtistCredit.BuildSignature(ArtistGroupMode.Artist, Seps, Array.Empty<string>()));
    }

    [Fact]
    public void FreshSettings_ShipTheDefaults()
        => Assert.Equal(Names, new AppSettings().ArtistProtectedNames);
}

/// <summary>Process-wide configuration: same non-parallel collection as
/// <see cref="ArtistCreditConfigurationTests"/>; always restores the defaults.</summary>
[Collection("ArtistCredit global configuration")]
public class ArtistProtectedNamesConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public ArtistProtectedNamesConfigurationTests() => ArtistCredit.ResetToDefaults();

    public void Dispose()
    {
        ArtistCredit.ResetToDefaults();
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void ActiveConfiguration_KeepsNamesWhole_InSplitTagWritesDisplayAndSpans()
    {
        Assert.Equal(new[] { "Tyler, The Creator", "Kali Uchis" }, ArtistCredit.Split("Tyler, The Creator, Kali Uchis"));
        Assert.Equal("Tyler, The Creator", Track.GetPrimaryArtist("Tyler, The Creator feat. Kali Uchis"));

        // Tag writes: symbols split, protected names and word separators don't.
        Assert.Equal(new[] { "Tyler, The Creator", "Kali Uchis" }, ArtistCredit.SplitForTag("Tyler, The Creator, Kali Uchis"));
        Assert.Equal(new[] { "Tyler, The Creator feat. X" }, ArtistCredit.SplitForTag("Tyler, The Creator feat. X"));
        Assert.Equal(new[] { "Earth, Wind & Fire", "A" }, ArtistCredit.SplitForTag("Earth, Wind & Fire; A"));

        // Display: a protected name is shown exactly as tagged; the rest still reads as commas.
        Assert.Equal("Tyler,The Creator, Kali Uchis", ArtistCredit.Display("Tyler,The Creator; Kali Uchis"));

        // Island hit-testing: one span per artist.
        var spans = ArtistCreditSpans.Locate("Tyler, The Creator, Kali Uchis");
        Assert.Equal(new[] { "Tyler, The Creator", "Kali Uchis" }, spans.Select(s => s.Name));
        Assert.Equal("Tyler, The Creator", ArtistCreditSpans.NameAt("Tyler, The Creator, Kali Uchis", 8));

        // Library filter: an artist page for the protected name matches its collaboration.
        Assert.True(LibraryAlbumsViewModel.ContainsArtistToken("Tyler, The Creator, Kali Uchis", "Tyler, The Creator"));
        Assert.False(LibraryAlbumsViewModel.ContainsArtistToken("Tyler, The Creator, Kali Uchis", "Tyler"));
    }

    [Fact]
    public void MetadataSearch_PrimaryName_IsTheWholeProtectedName()
    {
        Assert.Equal(new[] { "earthwindandfire", "kaliuchis" }, MatchText.SplitArtists("Earth, Wind & Fire feat. Kali Uchis"));
        ArtistCredit.Configure(ArtistGroupMode.Artist, ArtistCredit.DefaultSeparators, Array.Empty<string>());
        Assert.Equal("earth", MatchText.SplitArtists("Earth, Wind & Fire")[0]);
    }

    [Fact]
    public void Configure_NameListChange_BumpsVersionAndSignature_NullKeepsTheList()
    {
        var track = new Track { Artist = "Tyler, The Creator, Kali Uchis" };
        Assert.Equal("Tyler, The Creator", track.PrimaryArtist);
        var v0 = ArtistCredit.Version;
        var sig0 = ArtistCredit.Signature;

        // Separator-only Configure keeps the names (and no-ops when nothing changed).
        ArtistCredit.Configure(ArtistGroupMode.Artist, ArtistCredit.DefaultSeparators);
        ArtistCredit.Configure(ArtistGroupMode.Artist, ArtistCredit.DefaultSeparators, null);
        Assert.Equal(v0, ArtistCredit.Version);
        Assert.Equal(ArtistCredit.DefaultProtectedNames, ArtistCredit.ProtectedNames);

        ArtistCredit.Configure(ArtistGroupMode.Artist, ArtistCredit.DefaultSeparators, new[] { "AC/DC" });
        Assert.NotEqual(v0, ArtistCredit.Version);
        Assert.NotEqual(sig0, ArtistCredit.Signature); // the index cache rebuilds on next start
        Assert.Equal(ArtistCredit.BuildSignature(ArtistGroupMode.Artist, ArtistCredit.DefaultSeparators, new[] { "AC/DC" }),
            ArtistCredit.Signature);
        Assert.Equal("Tyler", track.PrimaryArtist); // cached parse invalidated by the version bump

        ArtistCredit.ResetToDefaults();
        Assert.Equal(sig0, ArtistCredit.Signature);
        Assert.Equal("Tyler, The Creator", track.PrimaryArtist);
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private SettingsViewModel CreateSettings(FakeLibraryService? library = null) => new(
        new PersistenceService(_root), library ?? new FakeLibraryService(), new NoOpPlayHistoryService());

    [AvaloniaFact]
    public async Task OldSettingsFile_WithoutTheList_LoadsTheDefaults()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), """{ "artistGroupMode": "Artist" }""");
        var loaded = await new PersistenceService(_root).LoadSettingsAsync();
        Assert.Equal(ArtistCredit.DefaultProtectedNames, loaded.ArtistProtectedNames);

        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.Equal(ArtistCredit.DefaultProtectedNames, vm.ArtistProtectedNames);
        Assert.Equal(ArtistCredit.DefaultProtectedNames, ArtistCredit.ProtectedNames);
    }

    [AvaloniaFact]
    public async Task AddRemoveReset_RegroupAndPersist_AndAnEmptiedListStaysEmpty()
    {
        var library = new FakeLibraryService();
        var vm = CreateSettings(library);
        await vm.LoadAsync();
        var regroups = library.MetadataChangedCount;

        vm.NewArtistProtectedName = "  Alpha, Beta Band ";
        vm.AddArtistProtectedNameCommand.Execute(null);
        Assert.Equal(string.Empty, vm.NewArtistProtectedName);
        Assert.Equal("Alpha, Beta Band", vm.ArtistProtectedNames.Last());
        Assert.Equal(new[] { "Alpha, Beta Band" }, ArtistCredit.Split("Alpha, Beta Band"));
        Assert.Equal(regroups + 1, library.MetadataChangedCount);

        // Blank and case-insensitive duplicates are ignored (no regroup).
        vm.NewArtistProtectedName = "   ";
        vm.AddArtistProtectedNameCommand.Execute(null);
        vm.NewArtistProtectedName = "alpha, beta band";
        vm.AddArtistProtectedNameCommand.Execute(null);
        Assert.Equal(ArtistCredit.DefaultProtectedNames.Count + 1, vm.ArtistProtectedNames.Count);
        Assert.Equal(regroups + 1, library.MetadataChangedCount);

        vm.RemoveArtistProtectedNameCommand.Execute("Tyler, The Creator");
        Assert.Equal(new[] { "Tyler", "The Creator" }, ArtistCredit.Split("Tyler, The Creator"));
        Assert.Equal(regroups + 2, library.MetadataChangedCount);
        await vm.SaveAsync();

        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.DoesNotContain("Tyler, The Creator", reloaded.ArtistProtectedNames);
        Assert.Contains("Alpha, Beta Band", reloaded.ArtistProtectedNames);

        foreach (var name in reloaded.ArtistProtectedNames.ToList())
            reloaded.RemoveArtistProtectedNameCommand.Execute(name);
        Assert.Empty(reloaded.ArtistProtectedNames);
        await reloaded.SaveAsync();
        var empty = CreateSettings();
        await empty.LoadAsync();
        Assert.Empty(empty.ArtistProtectedNames);
        Assert.Empty(ArtistCredit.ProtectedNames);

        empty.ResetArtistProtectedNamesCommand.Execute(null);
        Assert.Equal(ArtistCredit.DefaultProtectedNames, empty.ArtistProtectedNames);
        Assert.Equal(ArtistCredit.DefaultProtectedNames, ArtistCredit.ProtectedNames);
    }
}
