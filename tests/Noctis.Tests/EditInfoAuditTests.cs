using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-25: "verify everything in the info for albums/tracks is working properly and that
/// there are no saving/out of sync or weird bugs inside of the boxes". Every Edit Info field is
/// typed, saved, read back from the file (every library format) and shown again by a reopened
/// editor. Temp copies of the silence fixtures only.
/// </summary>
public class EditInfoAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "editinfo-" + Guid.NewGuid().ToString("N"));

    public EditInfoAuditTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Create(string ext)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "." + ext);
        var fixture = ext switch
        {
            "oga" => "ogg",
            "alac" or "mp4" or "m4b" => "m4a",
            "aif" => "aiff",
            "asf" => "wma",
            _ => ext,
        };
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", "Silence", "silence." + fixture), path, true);
        return path;
    }

    // .dsf and .ape need hand-built files (AdvancedReleaseDateWriteTests); the fixture formats
    // cover every tag family the library writes (ID3v2, Xiph, MP4, ASF, APE, RIFF INFO).
    public static IEnumerable<object[]> FixtureFormats() =>
        MetadataService.SupportedExtensions
            .Where(e => e is not ".dff" and not ".dsf" and not ".ape")
            .Select(e => new object[] { e.TrimStart('.') });

    private (Track Track, FakeLibraryService Library) Load(string path)
    {
        var track = new MetadataService().ReadTrackMetadata(path)!;
        Assert.NotNull(track);
        var library = new FakeLibraryService();
        library.TrackList.Add(track);
        return (track, library);
    }

    private static MetadataViewModel Open(Track track, ILibraryService library, IMetadataService? meta = null) =>
        new(track, meta ?? new MetadataService(), library, new TestPersistenceService(), new FakeAnimatedCoverService());

    // ── 1. Every field: typed → saved → in the file → in the library → shown again ──

    [Theory]
    [MemberData(nameof(FixtureFormats))]
    public async Task EveryDetailsField_RoundTrips_ThroughTheFile_AndTheReopenedEditor(string ext)
    {
        var path = Create(ext);
        var (track, library) = Load(path);
        var vm = Open(track, library);
        await vm.InitializeAsync();

        vm.Title = "Edited Title";
        vm.SongVersion = "Live";
        vm.Album = "Edited Album";
        vm.AlbumArtist = "Edited AlbumArtist";
        vm.Artist = "Edited Artist";
        vm.Composer = "Edited Composer";
        vm.Grouping = "Edited Grouping";
        vm.Genre = "Shoegaze";
        vm.Year = "1999";
        vm.TrackNumber = "3";
        vm.TrackCount = "12";
        vm.DiscNumber = "2";
        vm.DiscCount = "3";
        vm.Bpm = "128";
        vm.Comment = "Edited Comment";
        vm.IsCompilation = true;
        vm.ShowComposerInAllViews = true;
        vm.UseWorkAndMovement = true;
        vm.WorkName = "Edited Work";
        vm.MovementName = "Edited Movement";
        vm.MovementNumber = "2";
        vm.MovementCount = "4";
        vm.Rating = 4;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.SaveErrorMessage);

        var file = new MetadataService().ReadTrackMetadata(path)!;
        var failures = new List<string>();
        void Check(string field, object? expected, object? memory, object? onDisk)
        {
            if (!Equals(expected, memory)) failures.Add($"{field}: library has '{memory}', expected '{expected}'");
            if (!Equals(expected, onDisk)) failures.Add($"{field}: file has '{onDisk}', expected '{expected}'");
        }
        Check("Title", "Edited Title", track.Title, file.Title);
        Check("Version", "Live", track.Version, file.Version);
        Check("Album", "Edited Album", track.Album, file.Album);
        Check("AlbumArtist", "Edited AlbumArtist", track.AlbumArtist, file.AlbumArtist);
        Check("Artist", "Edited Artist", track.Artist, file.Artist);
        Check("Composer", "Edited Composer", track.Composer, file.Composer);
        Check("Grouping", "Edited Grouping", track.Grouping, file.Grouping);
        Check("Genre", "Shoegaze", track.Genre, file.Genre);
        Check("Year", 1999, track.Year, file.Year);
        Check("TrackNumber", 3, track.TrackNumber, file.TrackNumber);
        Check("TrackCount", 12, track.TrackCount, file.TrackCount);
        Check("DiscNumber", 2, track.DiscNumber, file.DiscNumber);
        Check("DiscCount", 3, track.DiscCount, file.DiscCount);
        Check("Bpm", 128, track.Bpm, file.Bpm);
        Check("Comment", "Edited Comment", track.Comment, file.Comment);
        Check("IsCompilation", true, track.IsCompilation, file.IsCompilation);
        Check("ShowComposerInAllViews", true, track.ShowComposerInAllViews, file.ShowComposerInAllViews);
        Check("UseWorkAndMovement", true, track.UseWorkAndMovement, file.UseWorkAndMovement);
        Check("WorkName", "Edited Work", track.WorkName, file.WorkName);
        Check("MovementName", "Edited Movement", track.MovementName, file.MovementName);
        Check("MovementNumber", 2, track.MovementNumber, file.MovementNumber);
        Check("MovementCount", 4, track.MovementCount, file.MovementCount);
        Check("Rating", 4, track.Rating, file.Rating);
        Assert.True(failures.Count == 0, ext + ":\n" + string.Join("\n", failures));

        var again = Open(track, library);
        await again.InitializeAsync();
        Assert.Equal("Edited Title", again.Title);
        Assert.Equal("Live", again.SongVersion);
        Assert.Equal("Edited Grouping", again.Grouping);
        Assert.Equal("1999", again.Year);
        Assert.Equal("3", again.TrackNumber);
        Assert.Equal("12", again.TrackCount);
        Assert.Equal("128", again.Bpm);
        Assert.Equal("Edited Comment", again.Comment);
        Assert.Equal(0, again.ChangeCount);
    }

    /// <summary>The checkboxes and pickers stored as Noctis' own flags: Show composer without
    /// Work &amp; Movement, Not Liked, and the Options › release type.</summary>
    [Theory]
    [MemberData(nameof(FixtureFormats))]
    public async Task Flags_RoundTrip_ThroughTheFile(string ext)
    {
        var path = Create(ext);
        var (track, library) = Load(path);
        var vm = Open(track, library);
        await vm.InitializeAsync();

        vm.ShowComposerInAllViews = true;
        vm.IsCompilation = true;
        vm.IsDisliked = true;
        vm.ReleaseTypeOverride = "EP";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.SaveErrorMessage);

        var file = new MetadataService().ReadTrackMetadata(path)!;
        var failures = new List<string>();
        if (!file.ShowComposerInAllViews) failures.Add("ShowComposerInAllViews");
        if (!file.IsCompilation) failures.Add("IsCompilation");
        if (!file.IsDisliked) failures.Add("IsDisliked");
        if (!file.IsReleaseTypeOverridden || file.ReleaseType != ReleaseType.EP) failures.Add($"ReleaseType ({file.ReleaseType}, overridden {file.IsReleaseTypeOverridden})");
        Assert.True(failures.Count == 0, ext + " lost: " + string.Join(", ", failures));
    }

    [Theory]
    [MemberData(nameof(FixtureFormats))]
    public async Task CustomTag_AddedThenRemoved_RoundTrips(string ext)
    {
        var path = Create(ext);
        var (track, library) = Load(path);
        var vm = Open(track, library);
        await vm.InitializeAsync();
        vm.AddCustomTagCommand.Execute(null);
        vm.CustomTags[^1].Key = "NOCTISAUDIT";
        vm.CustomTags[^1].Value = "kept";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.SaveErrorMessage);

        var again = Open(track, library);
        await again.InitializeAsync();
        var item = Assert.Single(again.CustomTags, t => t.Key.Equals("NOCTISAUDIT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("kept", item.Value);

        again.RemoveCustomTagCommand.Execute(item);
        await again.SaveCommand.ExecuteAsync(null);
        var third = Open(track, library);
        await third.InitializeAsync();
        Assert.DoesNotContain(third.CustomTags, t => t.Key.Equals("NOCTISAUDIT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A second edit of a box whose value lives in a custom field (COMPILATION,
    /// RATING, NOCTIS_DISLIKED, NOCTIS_RELEASETYPE, PERFORMER on Vorbis / ID3 TXXX). Those
    /// fields were also listed as Custom Tags, and the custom-tag write after the tag write put
    /// the value the editor opened with back.</summary>
    [Theory]
    [MemberData(nameof(FixtureFormats))]
    public async Task SecondEdit_OfABoxStoredInACustomField_IsNotUndone(string ext)
    {
        var path = Create(ext);
        var (track, library) = Load(path);
        var vm = Open(track, library);
        await vm.InitializeAsync();
        vm.IsCompilation = true; vm.Rating = 4; vm.IsDisliked = true; vm.ReleaseTypeOverride = "EP"; vm.Performer = "First";
        vm.ShowComposerInAllViews = true; vm.UseWorkAndMovement = true; vm.WorkName = "W1";
        await vm.SaveCommand.ExecuteAsync(null);

        var again = Open(track, library);
        await again.InitializeAsync();
        again.IsCompilation = false; again.Rating = 2; again.IsDisliked = false; again.ReleaseTypeOverride = "Auto"; again.Performer = "Second";
        again.ShowComposerInAllViews = false; again.UseWorkAndMovement = false; again.WorkName = "W2";
        await again.SaveCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, again.SaveErrorMessage);

        var file = new MetadataService().ReadTrackMetadata(path)!;
        var failures = new List<string>();
        if (file.IsCompilation) failures.Add("IsCompilation stayed on");
        if (file.Rating != 2) failures.Add($"Rating {file.Rating}");
        if (file.IsDisliked) failures.Add("IsDisliked stayed on");
        if (file.IsReleaseTypeOverridden) failures.Add($"ReleaseType override stayed {file.ReleaseType}");
        if (file.ShowComposerInAllViews) failures.Add("ShowComposer stayed on");
        if (file.UseWorkAndMovement) failures.Add("UseWorkAndMovement stayed on");
        if (file.WorkName != "W2") failures.Add($"WorkName {file.WorkName}");
        if (AdvancedTagIO.ReadAll(path).Performer != "Second") failures.Add($"Performer {AdvancedTagIO.ReadAll(path).Performer}");
        Assert.True(failures.Count == 0, ext + ": " + string.Join(", ", failures));
    }

    // ── 2. Album mode: only what was changed, on every song ──

    [Fact]
    public async Task AlbumScope_ChangedFields_GoToEverySong_OthersKeepTheirOwn()
    {
        var a = Load(Create("mp3")).Track;
        var b = Load(Create("flac")).Track;
        a.Title = "One"; a.TrackNumber = 1; a.Comment = "first"; a.Composer = "C1";
        b.Title = "Two"; b.TrackNumber = 2; b.Comment = "second"; b.Composer = "C2";
        foreach (var t in new[] { a, b })
        {
            t.Album = "A"; t.AlbumArtist = "X"; t.AlbumId = Track.ComputeAlbumId("X", "A");
            new MetadataService().WriteTrackMetadata(t);
        }
        var library = new FakeLibraryService();
        library.TrackList.AddRange(new[] { a, b });
        var vm = new MetadataViewModel(a, new MetadataService(), library, new TestPersistenceService(), new FakeAnimatedCoverService(),
            albumScoped: true, albumTracks: new List<Track> { a, b });
        await vm.InitializeAsync();
        Assert.Equal(string.Empty, vm.Comment);   // mixed: blank with a "Mixed" hint
        Assert.NotEqual(string.Empty, vm.CommentWatermark);

        vm.Year = "2010";
        vm.TrackCount = "2";
        vm.Album = "A (Deluxe)";
        vm.IsCompilation = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.SaveErrorMessage);

        foreach (var (t, title, n, comment, composer) in new[] { (a, "One", 1, "first", "C1"), (b, "Two", 2, "second", "C2") })
        {
            var file = new MetadataService().ReadTrackMetadata(t.FilePath)!;
            Assert.Equal((title, n, comment, composer), (file.Title, file.TrackNumber, file.Comment, file.Composer));
            Assert.Equal((2010, 2, "A (Deluxe)", true), (file.Year, file.TrackCount, file.Album, file.IsCompilation));
            Assert.Equal(Track.ComputeAlbumId("X", "A (Deluxe)"), t.AlbumId);
        }
    }

    // ── 3. Dirty state ──

    /// <summary>A freshly opened, fully tagged song says "No changes yet", and saving it
    /// untouched rewrites nothing. Typing and reverting, or ticking a box twice, ends there too.</summary>
    [Theory]
    [MemberData(nameof(FixtureFormats))]
    public async Task FreshDialog_HasNoChanges_AndRevertedEditsCountNone(string ext)
    {
        var path = Create(ext);
        var (track, library) = Load(path);
        var vm = Open(track, library);
        await vm.InitializeAsync();
        vm.Title = "T"; vm.Year = "2001"; vm.Grouping = "G"; vm.Comment = "C"; vm.Bpm = "99";
        vm.IsCompilation = true; vm.Isrc = "X"; vm.Rating = 2;
        await vm.SaveCommand.ExecuteAsync(null);

        var again = Open(track, library);
        await again.InitializeAsync();
        Assert.Equal(0, again.ChangeCount);

        again.Title = "T2"; again.Title = "T";
        again.IsCompilation = false; again.IsCompilation = true;
        again.Genre = "Jazz"; again.Genre = track.Genre;
        Assert.Equal(0, again.ChangeCount);

        var written = File.GetLastWriteTimeUtc(path);
        await again.SaveCommand.ExecuteAsync(null);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task ReadOnlyFile_SaveReportsTheFile()
    {
        var path = Create("mp3");
        var (track, library) = Load(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var vm = Open(track, library);
            await vm.InitializeAsync();
            vm.Title = "Edited";
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Contains(Path.GetFileName(path), vm.SaveErrorMessage);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    // ── 6. The boxes themselves ──

    /// <summary>"Track 3 of 12", "Disc 1 of 2", "Movement 2 of 4" and the play count's
    /// "(Last Played …)" were English in every language.</summary>
    [Fact]
    public void OfLabels_AndLastPlayed_AreLocalized()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Noctis.sln"))) root = root.Parent!;
        var xaml = File.ReadAllText(Path.Combine(root.FullName, "src", "Noctis", "Views", "MetadataWindow.axaml"));
        Assert.DoesNotContain("Text=\"of\"", xaml);
        var vmSource = File.ReadAllText(Path.Combine(root.FullName, "src", "Noctis", "ViewModels", "MetadataViewModel.cs"));
        Assert.DoesNotContain("(Last Played ", vmSource);

        foreach (var resx in Directory.GetFiles(Path.Combine(root.FullName, "src", "Noctis.UI", "Localization"), "Strings*.resx"))
        {
            var text = File.ReadAllText(resx);
            Assert.Contains("name=\"Metadata.Of\"", text);
            Assert.Contains("name=\"Metadata.PlayCountLastPlayed\"", text);
        }
    }

    // ── 5. Out of sync: the song keeps playing (and gets rated) while its editor is open ──

    /// <summary>The play count is not a box (only Reset), yet Save wrote back the count the
    /// editor read when it opened: a play counted meanwhile (the song being edited is often
    /// the one playing) was undone, and the "Last Played" date then disagreed with it.</summary>
    [Fact]
    public async Task PlaysCountedWhileTheEditorIsOpen_SurviveASave()
    {
        var (track, library) = Load(Create("flac"));
        track.PlayCount = 5;
        var vm = Open(track, library);
        await vm.InitializeAsync();

        track.PlayCount++; // PlayerViewModel counts the play on the library's Track
        vm.Title = "Edited";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(6, track.PlayCount);
    }

    [Fact]
    public async Task LyricsAddedElsewhereWhileTheEditorIsOpen_SurviveAnUnrelatedSave()
    {
        // Lyrics Studio / Find Lyrics can give the song lyrics while its Edit Info is open;
        // Save wrote the dialog's (empty) lyric tabs back over them.
        var (track, library) = Load(Create("flac"));
        var vm = Open(track, library);
        await vm.InitializeAsync();

        track.Lyrics = "line one\nline two";
        track.SyncedLyrics = "[00:01.00]line one\n[00:02.00]line two";
        vm.Title = "Edited";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("line one\nline two", track.Lyrics);
        Assert.Equal("[00:01.00]line one\n[00:02.00]line two", track.SyncedLyrics);
    }

    [Fact]
    public async Task LyricsEditedInTheEditor_StillSave()
    {
        var (track, library) = Load(Create("flac"));
        var vm = Open(track, library);
        await vm.InitializeAsync();

        vm.HasCustomLyrics = true;
        vm.Lyrics = "typed here";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("typed here", track.Lyrics);
    }

    [Fact]
    public async Task RatingGivenElsewhereWhileTheEditorIsOpen_SurvivesAnUnrelatedSave()
    {
        var (track, library) = Load(Create("flac"));
        var vm = Open(track, library);
        await vm.InitializeAsync();

        track.Rating = 5;       // now-playing stars
        track.IsDisliked = true; // a song menu's Not Liked
        vm.Title = "Edited";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(5, track.Rating);
        Assert.True(track.IsDisliked);
        Assert.Equal(5, new MetadataService().ReadTrackMetadata(track.FilePath)!.Rating);
    }

    [Fact]
    public async Task RatingChangedInTheEditor_StillWins()
    {
        var (track, library) = Load(Create("flac"));
        var vm = Open(track, library);
        await vm.InitializeAsync();

        vm.Rating = 3;
        vm.IsDisliked = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(3, track.Rating);
        Assert.True(track.IsDisliked);
    }

    [Fact]
    public async Task PlayCountReset_StillResets()
    {
        var (track, library) = Load(Create("flac"));
        track.PlayCount = 5;
        track.LastPlayed = DateTime.UtcNow;
        var vm = Open(track, library);
        await vm.InitializeAsync();

        vm.ResetPlayCountCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0, track.PlayCount);
        Assert.Null(track.LastPlayed);
    }

    // ── 4. Number boxes: nothing typed by mistake may silently wipe or corrupt a value ──

    [Theory]
    [InlineData("abc")]          // letters
    [InlineData("-5")]           // negative
    [InlineData("99999999999")]  // more than an int holds
    [InlineData("19 99")]
    public async Task Year_Unreadable_KeepsTheSongsYear(string typed)
    {
        var path = Create("flac");
        var (track, library) = Load(path);
        track.Year = 1999;
        new MetadataService().WriteTrackMetadata(track);
        var vm = Open(track, library);
        await vm.InitializeAsync();

        vm.Year = typed;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1999, track.Year);
        Assert.Equal(1999, new MetadataService().ReadTrackMetadata(path)!.Year);
    }

    [Theory]
    [InlineData(" 2004 ", 2004)]
    [InlineData("", 0)]          // emptied on purpose: cleared
    public async Task Year_SpacesAreIgnored_AndAnEmptyBoxClears(string typed, int expected)
    {
        var path = Create("flac");
        var (track, library) = Load(path);
        track.Year = 1999;
        new MetadataService().WriteTrackMetadata(track);
        var vm = Open(track, library);
        await vm.InitializeAsync();

        vm.Year = typed;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(expected, track.Year);
        Assert.Equal(expected, new MetadataService().ReadTrackMetadata(path)!.Year);
    }

    [Theory]
    [InlineData("3/12", 3)]      // pasted "track/total"
    [InlineData(" 4 ", 4)]
    [InlineData("x", 7)]         // unreadable: kept
    [InlineData("-2", 7)]
    [InlineData("", 0)]          // emptied: cleared
    public async Task TrackNumber_Box(string typed, int expected)
    {
        var path = Create("flac");
        var (track, library) = Load(path);
        track.TrackNumber = 7;
        new MetadataService().WriteTrackMetadata(track);
        var vm = Open(track, library);
        await vm.InitializeAsync();

        vm.TrackNumber = typed;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(expected, track.TrackNumber);
        Assert.Equal(expected, new MetadataService().ReadTrackMetadata(path)!.TrackNumber);
    }

    [Fact]
    public async Task Bpm_Decimal_IsRounded_NotWiped()
    {
        var path = Create("flac");
        var (track, library) = Load(path);
        track.Bpm = 90;
        new MetadataService().WriteTrackMetadata(track);
        var vm = Open(track, library);
        await vm.InitializeAsync();

        vm.Bpm = "120.4";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(120, track.Bpm);
    }

    [Fact]
    public async Task AlbumScope_UnreadableYear_KeepsEachSongsYear()
    {
        var a = Load(Create("flac")).Track;
        var b = Load(Create("flac")).Track;
        foreach (var t in new[] { a, b }) { t.Album = "A"; t.AlbumArtist = "X"; t.AlbumId = Track.ComputeAlbumId("X", "A"); }
        a.Year = 1990; b.Year = 1991;
        var library = new FakeLibraryService();
        library.TrackList.AddRange(new[] { a, b });
        var vm = new MetadataViewModel(a, new MetadataService(), library, new TestPersistenceService(), new FakeAnimatedCoverService(),
            albumScoped: true, albumTracks: new List<Track> { a, b });
        await vm.InitializeAsync();

        vm.Year = "19x0";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1990, a.Year);
        Assert.Equal(1991, b.Year);
    }

    [Theory]
    [MemberData(nameof(FixtureFormats))]
    public async Task EveryAdvancedField_RoundTrips_ThroughTheFile(string ext)
    {
        var path = Create(ext);
        var (track, library) = Load(path);
        var vm = Open(track, library);
        await vm.InitializeAsync();

        vm.TitleSort = "TS"; vm.ArtistSort = "AS"; vm.AlbumSort = "ALS"; vm.AlbumArtistSort = "AAS"; vm.ComposerSort = "CS";
        vm.Performer = "Perf"; vm.Conductor = "Cond"; vm.Lyricist = "Lyr"; vm.Publisher = "Pub"; vm.EncodedBy = "Enc";
        vm.Isrc = "USRC17607839"; vm.CatalogNumber = "CAT-1"; vm.Barcode = "0123456789012";
        vm.SelectedAdvisory = "Explicit"; vm.Language = "eng"; vm.Mood = "Calm"; vm.AdvDescription = "Desc";
        vm.Copyright = "(C) 2020";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.SaveErrorMessage);

        Assert.True(track.IsExplicit, "the library row's explicit flag did not follow the advisory");
        var adv = AdvancedTagIO.ReadAll(path);
        var failures = new List<string>();
        void Check(string field, string expected, string actual)
        {
            if (expected != actual) failures.Add($"{field}: file has '{actual}', expected '{expected}'");
        }
        Check("TitleSort", "TS", adv.TitleSort);
        Check("ArtistSort", "AS", adv.ArtistSort);
        Check("AlbumSort", "ALS", adv.AlbumSort);
        Check("AlbumArtistSort", "AAS", adv.AlbumArtistSort);
        Check("ComposerSort", "CS", adv.ComposerSort);
        Check("Performer", "Perf", adv.Performer);
        Check("Conductor", "Cond", adv.Conductor);
        Check("Lyricist", "Lyr", adv.Lyricist);
        Check("Publisher", "Pub", adv.Publisher);
        Check("EncodedBy", "Enc", adv.EncodedBy);
        Check("Isrc", "USRC17607839", adv.Isrc);
        Check("CatalogNumber", "CAT-1", adv.CatalogNumber);
        Check("Barcode", "0123456789012", adv.Barcode);
        Check("Advisory", "1", adv.ItunesAdvisory.ToString());
        Check("Language", "eng", adv.Language);
        Check("Mood", "Calm", adv.Mood);
        Check("Description", "Desc", adv.Description);
        var file = new MetadataService().ReadTrackMetadata(path)!;
        Check("Copyright", "(C) 2020", file.Copyright ?? "");
        if (!file.IsExplicit) failures.Add("IsExplicit: a rescan reads the song as not explicit");
        Assert.True(failures.Count == 0, ext + ":\n" + string.Join("\n", failures));

        // Every value is in its own box: none of them is listed again under Custom Tags.
        var again = Open(track, library);
        await again.InitializeAsync();
        Assert.True(again.CustomTags.Count == 0, ext + " lists " + string.Join(", ", again.CustomTags.Select(t => t.Key)));
    }
}
