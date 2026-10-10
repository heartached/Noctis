using System.Reflection;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Regression tests for the album/track Metadata editor (MetadataViewModel).
/// These cover the album-scoped fan-out fixes: Work &amp; Movement, play count,
/// Options change-detection (no per-track clobber), and album-wide artwork.
/// The VM is driven with lightweight service fakes; Save() mutates the Track
/// models in place, which is what we assert on.
/// </summary>
public class MetadataViewModelTests
{
    // ── Album scope: Work & Movement ──

    [Fact]
    public async Task AlbumScope_WorkAndMovement_FansOutToAllTracks_WhenChanged()
    {
        var tracks = Album("Symphony", "Composer", 3);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);
        await vm.InitializeAsync();

        vm.UseWorkAndMovement = true;
        vm.WorkName = "Symphony No. 5";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.All(tracks, t =>
        {
            Assert.True(t.UseWorkAndMovement);
            Assert.Equal("Symphony No. 5", t.WorkName);
        });
    }

    [Fact]
    public async Task AlbumScope_WorkName_Untouched_PreservesPerTrackValues()
    {
        var tracks = Album("Symphony", "Composer", 2);
        tracks[0].WorkName = "Work A";
        tracks[1].WorkName = "Work B";

        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);
        await vm.InitializeAsync();

        // Mixed WorkName loads blank; never touch it. Change an unrelated field.
        vm.Comment = "edited";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Work A", tracks[0].WorkName);
        Assert.Equal("Work B", tracks[1].WorkName);
    }

    // ── Year: 0 is "unknown" to WriteTrackMetadata, so emptying the field clears it explicitly ──

    [Fact]
    public async Task AlbumScope_EmptyingTheYear_ClearsItInTheFiles()
    {
        var tracks = Album("Coda", "Led Zeppelin", 2);
        foreach (var t in tracks) t.Year = 1982;
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);
        await vm.InitializeAsync();

        vm.Year = "";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.All(tracks, t => Assert.Equal(0, t.Year));
        Assert.Equal(tracks.Select(t => t.FilePath).OrderBy(x => x), meta.ClearedYearPaths.OrderBy(x => x));
    }

    [Fact]
    public async Task AlbumScope_OtherEdit_DoesNotClearTheYear()
    {
        var tracks = Album("Coda", "Led Zeppelin", 2);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);
        await vm.InitializeAsync();

        vm.Comment = "edited";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotEmpty(meta.WrittenTagPaths);
        Assert.Empty(meta.ClearedYearPaths);
    }

    // ── Album scope: play count ──

    [Fact]
    public async Task AlbumScope_ResetPlayCount_IsStagedUntilSave()
    {
        var tracks = Album("A", "X", 3);
        tracks[0].PlayCount = 5; tracks[0].LastPlayed = DateTime.UtcNow;
        tracks[1].PlayCount = 3; tracks[1].LastPlayed = DateTime.UtcNow;
        tracks[2].PlayCount = 8; tracks[2].LastPlayed = DateTime.UtcNow;

        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);
        await vm.InitializeAsync();

        vm.ResetPlayCountCommand.Execute(null);

        // Staged only: the display shows zero, but nothing touches the Track
        // models yet — Cancel must discard the reset.
        Assert.Equal("0", vm.PlayCountDisplay);
        Assert.All(tracks, t =>
        {
            Assert.NotEqual(0, t.PlayCount);
            Assert.NotNull(t.LastPlayed);
        });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.All(tracks, t =>
        {
            Assert.Equal(0, t.PlayCount);
            Assert.Null(t.LastPlayed);
        });
    }

    [Fact]
    public void AlbumScope_PlayCountDisplay_ShowsAlbumTotal()
    {
        var tracks = Album("A", "X", 3);
        tracks[0].PlayCount = 5;
        tracks[1].PlayCount = 3;
        tracks[2].PlayCount = 8;

        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);

        Assert.Equal("16", vm.PlayCountDisplay);
    }

    // ── Album scope: Options change-detection (the data-loss fix) ──

    [Fact]
    public async Task AlbumScope_UnchangedOptions_DoNotClobberPerTrackValues()
    {
        var tracks = Album("A", "X", 3);
        tracks[0].VolumeAdjust = 0;   tracks[0].EqPreset = "Rock";
        tracks[1].VolumeAdjust = -20; tracks[1].EqPreset = "Jazz";
        tracks[2].VolumeAdjust = 10;  tracks[2].EqPreset = string.Empty;

        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);
        await vm.InitializeAsync();

        // Change only a Details field; never open/touch Options.
        vm.Comment = "edited";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0, tracks[0].VolumeAdjust);   Assert.Equal("Rock", tracks[0].EqPreset);
        Assert.Equal(-20, tracks[1].VolumeAdjust); Assert.Equal("Jazz", tracks[1].EqPreset);
        Assert.Equal(10, tracks[2].VolumeAdjust);  Assert.Equal(string.Empty, tracks[2].EqPreset);
    }

    [Fact]
    public async Task AlbumScope_ChangedVolume_FansOutToAllTracks()
    {
        var tracks = Album("A", "X", 2);
        tracks[0].VolumeAdjust = 0;
        tracks[1].VolumeAdjust = -20;

        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);
        await vm.InitializeAsync();

        vm.VolumeAdjust = 50;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.All(tracks, t => Assert.Equal(50, t.VolumeAdjust));
    }

    // ── Artwork: album-wide on both album and single-track edits ──

    [Fact]
    public async Task AlbumScope_AddArtwork_WritesEveryTrack()
    {
        var tracks = Album("A", "X", 3);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);
        await vm.InitializeAsync();

        SetNewArtwork(vm, new byte[] { 1, 2, 3 });
        await vm.SaveCommand.ExecuteAsync(null);

        foreach (var t in tracks)
            Assert.Contains(t.FilePath, meta.WrittenArtPaths);
    }

    [Fact]
    public async Task TrackScope_AddArtwork_WritesAllAlbumTracks()
    {
        // Single-track edit: artwork should still apply to the whole album,
        // resolved from the library (mirrors the Remove path).
        var album = Album("A", "X", 3);
        using var p = new TestPersistenceService();
        var meta = new FakeMetadataService();
        var library = new FakeLibraryService { TrackList = album.ToList() };
        var vm = new MetadataViewModel(album[0], meta, library, p, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);
        await vm.InitializeAsync();

        SetNewArtwork(vm, new byte[] { 9, 9 });
        await vm.SaveCommand.ExecuteAsync(null);

        foreach (var t in album)
            Assert.Contains(t.FilePath, meta.WrittenArtPaths);
    }

    // A cover write that fails (the playing file LibVLC holds, a read-only file) used to be
    // ignored: the dialog closed as saved and the failed track was stamped with the new
    // cover's fingerprint, so the old cover came back on its next re-read.

    [Fact]
    public async Task AddArtwork_FailedWrite_KeepsDialogOpenAndTrackFingerprint()
    {
        var tracks = Album("A", "X", 3);
        foreach (var t in tracks) t.ArtworkHash = "OLD";
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);
        await vm.InitializeAsync();
        meta.FailArtPaths.Add(tracks[1].FilePath);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        var art = new byte[] { 1, 2, 3 };
        SetNewArtwork(vm, art);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.Contains(Path.GetFileName(tracks[1].FilePath), vm.SaveErrorMessage);
        Assert.Equal("OLD", tracks[1].ArtworkHash);
        Assert.Equal(TrackArtwork.Fingerprint(art), tracks[0].ArtworkHash);
        Assert.Equal(TrackArtwork.Fingerprint(art), tracks[2].ArtworkHash);
    }

    [Fact]
    public async Task RemoveArtwork_FailedWrite_KeepsDialogOpenAndTrackFingerprint()
    {
        var tracks = Album("A", "X", 2);
        foreach (var t in tracks) t.ArtworkHash = "OLD";
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);
        await vm.InitializeAsync();
        meta.FailArtPaths.Add(tracks[0].FilePath);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.RemoveArtworkCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.Contains(Path.GetFileName(tracks[0].FilePath), vm.SaveErrorMessage);
        Assert.Equal("OLD", tracks[0].ArtworkHash);
        Assert.Null(tracks[1].ArtworkHash);
    }

    // ── Tag writes only happen when a tag actually changed ────────────────────
    // Every save used to rewrite every album track's audio file unconditionally. Saving an
    // animated cover — which is a separate sidecar file and never touches the audio tags —
    // therefore rewrote all 20 FLACs of an album, and the one the player held open failed,
    // so the dialog reported "Couldn't write tags" for an edit that needed no tag write.

    [Fact]
    public async Task Save_WithNothingChanged_WritesNoTags()
    {
        var tracks = Album("A", "X", 3);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);
        await vm.InitializeAsync();

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(meta.WrittenTagPaths);
    }

    [Fact]
    public async Task Save_WithOnlyAnAnimatedCover_WritesNoTags()
    {
        // The reported bug: the audio files are irrelevant to this edit.
        var tracks = Album("A", "X", 3);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);
        await vm.InitializeAsync();

        SetNewAnimatedCover(vm, Path.Combine(Path.GetTempPath(), "cover.mp4"));
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(meta.WrittenTagPaths);
        Assert.Empty(vm.SaveErrorMessage);
    }

    [Fact]
    public async Task Save_WithAChangedTag_StillWritesEveryAlbumTrack()
    {
        // The guard must not cost the fan-out: an album-scoped Details edit still has to
        // reach every track's file.
        var tracks = Album("A", "X", 3);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);
        await vm.InitializeAsync();

        vm.Genre = "Reggaeton";
        await vm.SaveCommand.ExecuteAsync(null);

        foreach (var t in tracks)
            Assert.Contains(t.FilePath, meta.WrittenTagPaths);
    }

    [Fact]
    public async Task Save_WithAChangedTag_WritesOnlyTheTracksThatChanged()
    {
        // A per-track edit must not drag the rest of the album's files through a rewrite.
        var tracks = Album("A", "X", 3);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);
        await vm.InitializeAsync();

        tracks[1].Title = "Renamed by the user";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new[] { tracks[1].FilePath }, meta.WrittenTagPaths);
    }

    [Fact]
    public async Task Save_WithOnlyAPlayCountReset_WritesNoTags()
    {
        // Play count lives in the library journal, not the file's tags.
        var tracks = Album("A", "X", 2);
        tracks[0].PlayCount = 5;
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);
        await vm.InitializeAsync();

        vm.ResetPlayCountCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(meta.WrittenTagPaths);
    }

    [Fact]
    public async Task Save_WithAChangedRating_WritesTheTagBecauseRatingIsStoredInIt()
    {
        // Rating is not journal-only: WriteTrackMetadata puts it in the file, so it has to
        // count as a tag change or the rating would never reach disk.
        var tracks = Album("A", "X", 1);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);
        await vm.InitializeAsync();

        vm.Rating = 4;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains(tracks[0].FilePath, meta.WrittenTagPaths);
    }

    // ── Genre: free text (GitHub #123) ──

    [Fact]
    public void Genre_OffListValue_KeepsGenre_WithoutPollutingTheSuggestions()
    {
        // The genre box is editable now: an off-list value (typed, or Deezer's "Rap/Hip Hop"
        // from an online search) shows as its text. Adding every value to the suggestions, as
        // the read-only ComboBox needed, would list each keystroke ("R", "Ra", "Rap", …).
        var album = Album("A", "X", 1);
        using var p = new TestPersistenceService();
        var meta = new FakeMetadataService();
        var library = new FakeLibraryService { TrackList = album.ToList() };
        var vm = new MetadataViewModel(album[0], meta, library, p, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);
        var before = vm.GenreOptions.ToList();

        vm.Genre = "R";
        vm.Genre = "Ra";
        vm.Genre = "Rap/Hip Hop";

        Assert.Equal("Rap/Hip Hop", vm.Genre);
        Assert.Equal(before, vm.GenreOptions);
    }

    [Fact]
    public void GenreOptions_HoldLibraryGenres_AndBuiltIns_DedupedCaseInsensitively()
    {
        var album = Album("A", "X", 2);
        album[0].Genre = "Hyperpop";
        var other = Album("B", "Y", 3);
        other[0].Genre = "  hyperpop ";   // same genre, other spelling: listed once
        other[1].Genre = "rock";          // case variant of the built-in "Rock": listed once
        other[2].Genre = "Shoegaze";
        using var p = new TestPersistenceService();
        var library = new FakeLibraryService { TrackList = album.Concat(other).ToList() };
        var vm = new MetadataViewModel(album[0], new FakeMetadataService(), library, p, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);

        var options = vm.GenreOptions.ToList();
        Assert.Contains("Hyperpop", options);       // the edited track's spelling wins
        Assert.Contains("Shoegaze", options);       // a library genre outside the built-in list
        Assert.Contains("Jazz", options);           // the built-in list is still offered
        Assert.Single(options, o => o.Equals("hyperpop", StringComparison.OrdinalIgnoreCase));
        Assert.Single(options, o => o.Equals("rock", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(options, o => o.Length == 0 || o != o.Trim());
        Assert.Equal(options.OrderBy(o => o, StringComparer.CurrentCultureIgnoreCase), options);
    }

    [Fact]
    public async Task TrackScope_TypedNewGenre_IsSavedTrimmed()
    {
        var album = Album("A", "X", 1);
        album[0].Genre = "Pop";
        using var p = new TestPersistenceService();
        var meta = new FakeMetadataService();
        var vm = new MetadataViewModel(album[0], meta, new FakeLibraryService { TrackList = album.ToList() }, p,
            new FakeAnimatedCoverService(), albumScoped: false, albumTracks: null);
        await vm.InitializeAsync();

        vm.Genre = "  Bedroom Pop  ";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Bedroom Pop", album[0].Genre);
        Assert.Contains(album[0].FilePath, meta.WrittenTagPaths);
    }

    [Fact]
    public async Task AlbumScope_TypedNewGenre_GoesToEveryTrack_AndAPickedOneStillWorks()
    {
        var tracks = Album("A", "X", 3);
        tracks[0].Genre = "Rock"; tracks[1].Genre = "Pop"; tracks[2].Genre = "Rock"; // "Mixed"
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);
        await vm.InitializeAsync();
        Assert.Equal(string.Empty, vm.Genre);

        vm.Genre = " Math Rock ";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.All(tracks, t => Assert.Equal("Math Rock", t.Genre));
        Assert.Equal(3, meta.WrittenTagPaths.Distinct().Count());

        // A suggestion picked from the list saves the same way.
        using var p2 = new TestPersistenceService();
        var vm2 = NewAlbumVm(tracks, p2, out _, out _);
        await vm2.InitializeAsync();
        vm2.Genre = vm2.GenreOptions.First(g => g == "Jazz");
        await vm2.SaveCommand.ExecuteAsync(null);
        Assert.All(tracks, t => Assert.Equal("Jazz", t.Genre));
    }

    [Fact]
    public async Task AlbumScope_MixedGenreLeftBlank_IsNotOverwritten()
    {
        // Whitespace typed into a "Mixed" genre box trims to nothing: no change, each track
        // keeps its own genre.
        var tracks = Album("A", "X", 2);
        tracks[0].Genre = "Rock"; tracks[1].Genre = "Pop";
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);
        await vm.InitializeAsync();

        vm.Genre = "   ";
        vm.Year = "1999";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Rock", tracks[0].Genre);
        Assert.Equal("Pop", tracks[1].Genre);
    }

    // ── Options: start/stop time parsing ──

    [Fact]
    public async Task TrackScope_StartTime_BareNumberParsesAsSeconds()
    {
        // "45" must mean 45 seconds — the TimeSpan.TryParse fallback reads it as 45 days.
        var album = Album("A", "X", 1);
        using var p = new TestPersistenceService();
        var vm = new MetadataViewModel(album[0], new FakeMetadataService(),
            new FakeLibraryService { TrackList = album.ToList() }, p, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);
        await vm.InitializeAsync();

        vm.HasStartTime = true;
        vm.StartTime = "45";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(45_000, album[0].StartTimeMs);
    }

    [Fact]
    public async Task TrackScope_StopTimePastAnHour_SurvivesAnUnrelatedSave()
    {
        // Start/stop times were shown as m:ss.fff, which drops the hours: a 1:05:00 stop
        // time loaded as "5:00.000", and saving any other field cut it to 5 minutes.
        var album = Album("A", "X", 1);
        album[0].Duration = TimeSpan.FromMinutes(70);
        album[0].StopTimeMs = 3_900_000;
        album[0].StartTimeMs = 3_660_000;
        using var p = new TestPersistenceService();
        var vm = new MetadataViewModel(album[0], new FakeMetadataService(),
            new FakeLibraryService { TrackList = album.ToList() }, p, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);
        await vm.InitializeAsync();

        vm.Comment = "edited";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(3_900_000, album[0].StopTimeMs);
        Assert.Equal(3_660_000, album[0].StartTimeMs);
    }

    [Fact]
    public async Task TrackScope_EnablingStopTime_DefaultsToTheFullLengthOfALongTrack()
    {
        var album = Album("A", "X", 1);
        album[0].Duration = TimeSpan.FromMinutes(65);
        using var p = new TestPersistenceService();
        var vm = new MetadataViewModel(album[0], new FakeMetadataService(),
            new FakeLibraryService { TrackList = album.ToList() }, p, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);
        await vm.InitializeAsync();

        vm.HasStopTime = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(3_900_000, album[0].StopTimeMs);
    }

    /// <summary>GitHub #95: user EQ presets join the Options dropdown. Names match
    /// case-insensitively, so a tag spelled "BASS" selects the listed "Bass" once.</summary>
    [Fact]
    public void UserEqPresets_AreListedOnce_AndTheTagSelectsTheListedSpelling()
    {
        var album = Album("A", "X", 1);
        album[0].EqPreset = "BASS";
        using var p = new TestPersistenceService();
        var vm = new MetadataViewModel(album[0], new FakeMetadataService(),
            new FakeLibraryService { TrackList = album.ToList() }, p, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);

        vm.SetUserEqPresets(new[] { "Bass", "Studio" });

        Assert.Single(vm.EqPresetOptions, n => string.Equals(n, "bass", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Bass", vm.SelectedEqPreset);
        Assert.Contains("Studio", vm.EqPresetOptions);
        Assert.Equal(MetadataViewModel.OptionsEqPresets, vm.EqPresetOptions.Take(MetadataViewModel.OptionsEqPresets.Length));
    }

    // ── Timestamp Lyrics: first manual timing pass must survive Save ──

    [Fact]
    public async Task TimestampEdit_OnPlainOnlyTrack_SavesSyncedLyrics()
    {
        var album = Album("A", "X", 1);
        album[0].Lyrics = "line one\nline two";

        using var p = new TestPersistenceService();
        var vm = new MetadataViewModel(album[0], new FakeMetadataService(),
            new FakeLibraryService { TrackList = album.ToList() }, p, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);
        await vm.InitializeAsync();

        // Lines seeded from the plain lyrics; no synced lyrics exist yet.
        Assert.Equal(2, vm.SyncedLyricLines.Count);
        Assert.False(vm.HasCustomSyncedLyrics);

        vm.SyncedLyricLines[0].TimestampText = "0:10.00";

        Assert.True(vm.HasCustomSyncedLyrics);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("[00:10.00]line one", album[0].SyncedLyrics);
    }

    // ── Import file (Discord ask): .lrc → synced, .txt → plain, both survive Save ──

    [Fact]
    public async Task ImportLyricsText_RoutesByContent_AndSaves()
    {
        var album = Album("A", "X", 1);
        using var p = new TestPersistenceService();
        var vm = new MetadataViewModel(album[0], new FakeMetadataService(),
            new FakeLibraryService { TrackList = album.ToList() }, p, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);
        await vm.InitializeAsync();
        Assert.False(vm.HasCustomSyncedLyrics);

        const string Lrc = "[00:01.00]first" + "\n" + "[00:05.50]second";
        const string Plain = "just words" + "\n" + "more words";

        // Timestamped text lands on the synced tab, CRLF normalized.
        vm.ImportLyricsText("[00:01.00]first" + "\r\n" + "[00:05.50]second" + "\r\n", "song.lrc");
        Assert.True(vm.HasCustomSyncedLyrics);
        Assert.Equal(Lrc, vm.SyncedLyrics);
        Assert.Equal(2, vm.SyncedLyricLines.Count);
        Assert.Equal("Imported song.lrc", vm.SyncedLyricsSearchStatus);

        // Plain text lands on the plain tab and says so.
        vm.ImportLyricsText(Plain, "song.txt");
        Assert.True(vm.HasCustomLyrics);
        Assert.Equal(Plain, vm.Lyrics);
        Assert.Contains("plain lyrics", vm.SyncedLyricsSearchStatus);

        // Empty file: nothing changes.
        vm.ImportLyricsText("   ", "blank.lrc");
        Assert.Equal(Lrc, vm.SyncedLyrics);
        Assert.Equal("blank.lrc is empty.", vm.SyncedLyricsSearchStatus);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(Lrc, album[0].SyncedLyrics);
        Assert.Equal(Plain, album[0].Lyrics);
    }

    // ── Multi-select rename: lyric sidecars follow the audio file ──

    [Fact]
    public async Task MultiSelectRename_MovesLyricSidecarsWithFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-md-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var tracks = Album("A", "X", 2);
            tracks[0].FilePath = Path.Combine(dir, "a.flac");
            tracks[1].FilePath = Path.Combine(dir, "b.flac");
            File.WriteAllText(tracks[0].FilePath, "audio");
            File.WriteAllText(tracks[1].FilePath, "audio");
            File.WriteAllText(Path.Combine(dir, "a.lrc"), "[00:01.00] hi");

            using var p = new TestPersistenceService();
            var vm = new MetadataViewModel(tracks[0], new FakeMetadataService(),
                new FakeLibraryService { TrackList = tracks.ToList() }, p, new FakeAnimatedCoverService(),
                albumScoped: true, albumTracks: tracks.ToList(), multiSelect: true);
            await vm.InitializeAsync();

            vm.ApplyRename = true; // default pattern: "%tracknumber2% - %title%"
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(Path.Combine(dir, "01 - Track 1.flac"), tracks[0].FilePath);
            Assert.True(File.Exists(Path.Combine(dir, "01 - Track 1.lrc")));
            Assert.False(File.Exists(Path.Combine(dir, "a.lrc")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task MultiSelectRename_MovesWordTimedSidecarsWithFile()
    {
        // The lyrics page reads .lyricsfile and .elrc (Lyrics Studio word timings) by the
        // song's basename too; a rename that left them behind detached them from the song.
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-md-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var tracks = Album("A", "X", 2);
            tracks[0].FilePath = Path.Combine(dir, "a.flac");
            tracks[1].FilePath = Path.Combine(dir, "b.flac");
            File.WriteAllText(tracks[0].FilePath, "audio");
            File.WriteAllText(tracks[1].FilePath, "audio");
            File.WriteAllText(Path.Combine(dir, "a.elrc"), "[00:01.00]<00:01.00>hi");
            File.WriteAllText(Path.Combine(dir, "a.lyricsfile"), "lines: []");

            using var p = new TestPersistenceService();
            var vm = new MetadataViewModel(tracks[0], new FakeMetadataService(),
                new FakeLibraryService { TrackList = tracks.ToList() }, p, new FakeAnimatedCoverService(),
                albumScoped: true, albumTracks: tracks.ToList(), multiSelect: true);
            await vm.InitializeAsync();

            vm.ApplyRename = true; // default pattern: "%tracknumber2% - %title%"
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.True(File.Exists(Path.Combine(dir, "01 - Track 1.elrc")));
            Assert.True(File.Exists(Path.Combine(dir, "01 - Track 1.lyricsfile")));
            Assert.False(File.Exists(Path.Combine(dir, "a.elrc")));
            Assert.False(File.Exists(Path.Combine(dir, "a.lyricsfile")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task MultiSelectRename_RelocatesRenamedTracksInLibrary()
    {
        // A track's id is the hash of its path. Only reassigning FilePath kept the old
        // id, so the watcher (and the next scan) imported each renamed file as a new
        // track and the old one's play counts, favorites and playlist entries were lost.
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-md-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var tracks = Album("A", "X", 2);
            tracks[0].FilePath = Path.Combine(dir, "a.flac");
            tracks[1].FilePath = Path.Combine(dir, "b.flac");
            File.WriteAllText(tracks[0].FilePath, "audio");
            File.WriteAllText(tracks[1].FilePath, "audio");

            using var p = new TestPersistenceService();
            var library = new FakeLibraryService { TrackList = tracks.ToList() };
            var vm = new MetadataViewModel(tracks[0], new FakeMetadataService(),
                library, p, new FakeAnimatedCoverService(),
                albumScoped: true, albumTracks: tracks.ToList(), multiSelect: true);
            await vm.InitializeAsync();

            vm.ApplyRename = true; // default pattern: "%tracknumber2% - %title%"
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(new[]
            {
                (Path.Combine(dir, "a.flac"), Path.Combine(dir, "01 - Track 1.flac")),
                (Path.Combine(dir, "b.flac"), Path.Combine(dir, "02 - Track 2.flac")),
            }, library.Relocated);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Sets the process-wide App.Services to observe the rename loop, so it runs in its
    /// own non-parallel collection and always restores the previous provider.
    /// </summary>
    [Collection("App.Services global")]
    public class RenameThreading
    {
        [Fact]
        public async Task MultiSelectRename_MovesFilesOffTheUiContext()
        {
            // The per-file moves (plus sidecar probes) ran inline after the tag writes'
            // awaits resumed on the UI context, freezing the window for the whole batch.
            var dir = Path.Combine(Path.GetTempPath(), $"noctis-md-rename-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var previousServices = App.Services;
            var watcher = new RecordingWatcher();
            try
            {
                App.Services = new SingleServiceProvider(watcher);
                var tracks = Album("A", "X", 2);
                tracks[0].FilePath = Path.Combine(dir, "a.flac");
                tracks[1].FilePath = Path.Combine(dir, "b.flac");
                File.WriteAllText(tracks[0].FilePath, "audio");
                File.WriteAllText(tracks[1].FilePath, "audio");

                using var p = new TestPersistenceService();
                var vm = new MetadataViewModel(tracks[0], new FakeMetadataService(),
                    new FakeLibraryService { TrackList = tracks.ToList() }, p, new FakeAnimatedCoverService(),
                    albumScoped: true, albumTracks: tracks.ToList(), multiSelect: true);
                await vm.InitializeAsync();
                vm.ApplyRename = true; // default pattern: "%tracknumber2% - %title%"

                // Start Save on a UI-like context so its awaits resume there, as the
                // Save button's command does on the Avalonia dispatcher.
                var ui = new UiContext();
                var done = new TaskCompletionSource();
                ui.Post(async _ =>
                {
                    try { await vm.SaveCommand.ExecuteAsync(null); done.SetResult(); }
                    catch (Exception ex) { done.SetException(ex); }
                }, null);
                await done.Task;

                Assert.Equal(2, watcher.Calls);
                Assert.Equal(0, watcher.CallsOnUiContext);
                Assert.Equal(Path.Combine(dir, "01 - Track 1.flac"), tracks[0].FilePath);
                Assert.Equal(Path.Combine(dir, "02 - Track 2.flac"), tracks[1].FilePath);
                Assert.True(File.Exists(tracks[0].FilePath));
                Assert.True(File.Exists(tracks[1].FilePath));
            }
            finally
            {
                App.Services = previousServices;
                Directory.Delete(dir, true);
            }
        }

        /// <summary>Runs posted work on the pool with itself installed as the current context.</summary>
        private sealed class UiContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state) =>
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    SetSynchronizationContext(this);
                    try { d(state); }
                    finally { SetSynchronizationContext(null); }
                });
        }

        private sealed class SingleServiceProvider(object service) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType.IsInstanceOfType(service) ? service : null;
        }

        /// <summary>The rename suppresses the watcher right before each move, so its
        /// calls show which context the moves run on.</summary>
        private sealed class RecordingWatcher : ILibraryWatcherService
        {
            private int _calls, _callsOnUi;
            public int Calls => _calls;
            public int CallsOnUiContext => _callsOnUi;
            public void Refresh() { }
            public void SuppressPaths(IEnumerable<string> paths, TimeSpan window)
            {
                Interlocked.Increment(ref _calls);
                if (SynchronizationContext.Current is UiContext) Interlocked.Increment(ref _callsOnUi);
            }
            public void Dispose() { }
        }
    }

    // ── Artwork chooser: iTunes search term construction ──
    // "Choose Artwork — From Apple Music" for the album "7" (Lil Nas X) showed George
    // Strait and Beach House records: the title-only query surfaced every album named
    // "7", and the artist-enriched one carried the full multi-artist tag string, which
    // over-specifies iTunes's AND-matched free-text search into returning nothing.

    [Fact]
    public void ArtworkSearchTerm_CarriesPrimaryArtistAndAlbum()
    {
        // Track.GetPrimaryArtist reduces the tag to its first credited artist. The
        // bare word "x" stopped being a default separator with GitHub #51, so the
        // full "Lil Nas X" survives, while "Billy Ray Cyrus" must not be sent.
        Assert.Equal("Lil Nas X 7",
            ITunesArtworkService.BuildAlbumSearchTerm("Lil Nas X feat. Billy Ray Cyrus", "7"));
    }

    [Fact]
    public void ArtworkSearchTerm_WithoutAnArtist_IsTheAlbumAlone()
    {
        Assert.Equal("7", ITunesArtworkService.BuildAlbumSearchTerm("", "7"));
        Assert.Equal("7", ITunesArtworkService.BuildAlbumSearchTerm(null, "7"));
        Assert.Equal("7", ITunesArtworkService.BuildAlbumSearchTerm("   ", "7"));
    }

    [Fact]
    public void ArtworkSearchTerm_StripsJoinedArtistsNotTheAlbumText()
    {
        // Only the artist side is reduced to the primary credit; the album text is the
        // user's tag and goes through untouched.
        Assert.Equal("Rema Rave & Roses",
            ITunesArtworkService.BuildAlbumSearchTerm("Rema, Selena Gomez", "Rave & Roses"));
    }

    [Fact]
    public void ArtworkRanking_AcceptsTheStoreCreditForAMultiArtistTag()
    {
        // Ranking corroborates candidates against the tag's artist. The store credits
        // "Lil Nas X"; the shorter credit can never *contain* the longer tag string, so
        // the comparison has to run both ways or the real album ties with the strangers.
        Assert.True(ITunesArtworkService.IsLikelySameArtist(
            "Lil Nas X", "Lil Nas X feat. Billy Ray Cyrus"));
    }

    // ── Open fast: the ctor touches no files; InitializeAsync loads behind the shown window ──
    // Owner 10-08: the window froze 230–880 ms on lyric sidecar reads in the ctor and
    // waited 1–2 s for tag reads before showing.

    [Fact]
    public async Task Open_CtorReadsNoSidecars_InitializeAsyncLoadsLrcAndTxt()
    {
        using var dir = new TempSongDir();
        File.WriteAllText(dir.Side(".lrc"), "[00:01.00]hi");
        File.WriteAllText(dir.Side(".txt"), "plain words");
        var covers = new CountingAnimatedCoverService { CoverPath = dir.Side(".mp4") };
        var vm = dir.NewVm(new FakeMetadataService(), covers);

        // Open state: in-memory Track fields only, nothing from the folder yet.
        Assert.Equal("Song", vm.Title);
        Assert.Equal(string.Empty, vm.SyncedLyrics);
        Assert.Equal(string.Empty, vm.Lyrics);
        Assert.Equal(0, covers.ResolveCalls);
        Assert.False(vm.HasAnimatedCover);

        await vm.InitializeAsync();

        Assert.Equal("[00:01.00]hi", vm.SyncedLyrics);
        Assert.True(vm.HasCustomSyncedLyrics);
        Assert.Single(vm.SyncedLyricLines);
        Assert.Equal("plain words", vm.Lyrics);
        Assert.True(vm.HasCustomLyrics);
        Assert.Equal(1, covers.ResolveCalls);
        Assert.True(vm.HasAnimatedCover);
        // The loaded sidecars are the baseline, not edits.
        Assert.Equal(0, vm.ChangeCount);
    }

    [Fact]
    public async Task Open_IsLoading_TrueUntilInitializeAsyncCompletes()
    {
        using var dir = new TempSongDir();
        var vm = dir.NewVm(new FakeMetadataService(), new CountingAnimatedCoverService());
        var raised = new List<bool>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsLoading)) raised.Add(vm.IsLoading); };

        Assert.True(vm.IsLoading);
        Assert.False(vm.SaveCommand.CanExecute(null));

        await vm.InitializeAsync();

        Assert.False(vm.IsLoading);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Equal(new[] { false }, raised);
    }

    [Fact]
    public async Task Open_SaveWhileLoading_WritesNothing()
    {
        using var dir = new TempSongDir();
        var meta = new FakeMetadataService();
        var vm = dir.NewVm(meta, new CountingAnimatedCoverService());
        var saved = false;
        vm.ChangesSaved += (_, _) => saved = true;

        vm.Title = "Edited";
        // ExecuteAsync bypasses CanExecute, as a key binding would.
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Song", dir.Track.Title);
        Assert.Empty(meta.WrittenTagPaths);
        Assert.False(saved);
        Assert.False(File.Exists(dir.Side(".txt")));
        Assert.False(File.Exists(dir.Side(".lrc")));
    }

    [Fact]
    public async Task Open_LoadFails_IsLoadingEndsFalse_AndUnreadSidecarIsNeverTrashed()
    {
        using var dir = new TempSongDir();
        File.WriteAllText(dir.Side(".lrc"), "[00:01.00]hand timed");
        var trashed = new List<string>();
        var vm = dir.NewVm(new FakeMetadataService { ThrowOnReadFileInfo = true },
            new CountingAnimatedCoverService(), trashed);

        await Assert.ThrowsAsync<IOException>(vm.InitializeAsync);
        Assert.False(vm.IsLoading);

        // The sidecar was never read: removing "nothing" and saving must not trash it.
        vm.RemoveSyncedLyricsCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(trashed);
        Assert.True(File.Exists(dir.Side(".lrc")));
    }

    [Fact]
    public async Task Open_UnreadableLrc_IsNotTrashedOnSave_ButAReadableOneIs()
    {
        using var dir = new TempSongDir();
        File.WriteAllText(dir.Side(".lrc"), "[00:01.00]hand timed");
        var trashed = new List<string>();
        var vm = dir.NewVm(new FakeMetadataService(), new CountingAnimatedCoverService(), trashed);

        // Locked while the load reads it: File.Exists is true, the read throws.
        using (new FileStream(dir.Side(".lrc"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await vm.InitializeAsync();

        Assert.Equal(string.Empty, vm.SyncedLyrics);
        vm.RemoveSyncedLyricsCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Empty(trashed);
        Assert.True(File.Exists(dir.Side(".lrc")));

        // Control: the same removal on a sidecar that DID load trashes it.
        var vm2 = dir.NewVm(new FakeMetadataService(), new CountingAnimatedCoverService(), trashed);
        await vm2.InitializeAsync();
        Assert.Equal("[00:01.00]hand timed", vm2.SyncedLyrics);
        vm2.RemoveSyncedLyricsCommand.Execute(null);
        await vm2.SaveCommand.ExecuteAsync(null);
        Assert.Contains("song.lrc", trashed);
    }

    [Fact]
    public async Task Open_EditTypedBeforeLoad_IsKeptAndCounted()
    {
        using var dir = new TempSongDir();
        File.WriteAllText(dir.Side(".lrc"), "[00:01.00]hi");
        File.WriteAllText(dir.Side(".txt"), "disk words");
        using var gate = new ManualResetEventSlim();
        var vm = dir.NewVm(new FakeMetadataService { ReadFileInfoGate = gate }, new CountingAnimatedCoverService());
        var loading = vm.InitializeAsync(); // as the window opens: load started, held mid-read

        vm.HasCustomLyrics = true; // the plain box is enabled by this switch
        vm.Lyrics = "typed words";
        gate.Set();
        await loading;

        // The typed plain lyrics survive the load; the untouched synced tab loads.
        Assert.Equal("typed words", vm.Lyrics);
        Assert.Equal("[00:01.00]hi", vm.SyncedLyrics);
        Assert.True(vm.PlainLyricsChanged);
        Assert.False(vm.SyncedLyricsChanged);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("typed words", File.ReadAllText(dir.Side(".txt")));
        Assert.Equal("[00:01.00]hi", File.ReadAllText(dir.Side(".lrc")));
    }

    [Fact]
    public async Task Open_CustomLyricsSwitchedOnBeforeLoad_ShowsAndKeepsTheTxt()
    {
        // Switching Custom Lyrics on before the .txt arrived must not leave the tab empty:
        // saving that empty tab would read as "the user cleared the lyrics" and trash the .txt.
        using var dir = new TempSongDir();
        File.WriteAllText(dir.Side(".txt"), "disk words");
        using var gate = new ManualResetEventSlim();
        var vm = dir.NewVm(new FakeMetadataService { ReadFileInfoGate = gate }, new CountingAnimatedCoverService());
        var loading = vm.InitializeAsync();

        vm.HasCustomLyrics = true;
        gate.Set();
        await loading;

        Assert.Equal("disk words", vm.Lyrics);
        Assert.True(vm.HasCustomLyrics);
        vm.Year = "2001";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("disk words", File.ReadAllText(dir.Side(".txt")));
        Assert.Equal("disk words", dir.Track.Lyrics);
    }

    [Fact]
    public async Task Open_RemoveOnEmptySyncedTabBeforeLoad_DoesNotTrashTheLoadedLrc()
    {
        using var dir = new TempSongDir();
        File.WriteAllText(dir.Side(".lrc"), "[00:01.00]hand timed");
        var trashed = new List<string>();
        using var gate = new ManualResetEventSlim();
        var vm = dir.NewVm(new FakeMetadataService { ReadFileInfoGate = gate }, new CountingAnimatedCoverService(), trashed);
        var loading = vm.InitializeAsync();

        vm.RemoveSyncedLyricsCommand.Execute(null); // nothing there yet to remove
        gate.Set();
        await loading;

        Assert.Equal("[00:01.00]hand timed", vm.SyncedLyrics);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Empty(trashed);
        Assert.Equal("[00:01.00]hand timed", File.ReadAllText(dir.Side(".lrc")));
    }

    [Fact]
    public async Task AlbumScope_OptionsOnlySave_LeavesTheFirstTracksLyricsAndTagsAlone()
    {
        // The album dialog has no lyric tabs, yet Save copied the first track's .txt sidecar
        // into its Lyrics and so rewrote that file's tags (and both sidecars) on a save that
        // needed no tag write at all — the playing file then failed with "Couldn't write tags".
        using var dir = new TempSongDir();
        var tracks = Album("A", "X", 2);
        tracks[0].FilePath = dir.Side(".flac");
        tracks[1].FilePath = Path.Combine(dir.Dir, "other.flac");
        File.WriteAllText(dir.Side(".txt"), "disk words");
        File.WriteAllText(dir.Side(".lrc"), "[00:01.00]hi");
        var lrcWritten = File.GetLastWriteTimeUtc(dir.Side(".lrc"));
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);
        await vm.InitializeAsync();

        vm.SkipWhenShuffling = !vm.SkipWhenShuffling;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(meta.WrittenTagPaths);
        Assert.Equal(string.Empty, tracks[0].Lyrics);
        Assert.Equal(string.Empty, tracks[0].SyncedLyrics);
        Assert.Equal(lrcWritten, File.GetLastWriteTimeUtc(dir.Side(".lrc")));
    }

    [Fact]
    public async Task AlbumScope_RespelledEqPreset_IsNotFannedOutOnAnUnrelatedSave()
    {
        // SetUserEqPresets points the selection at the listed spelling ("rock" -> "Rock").
        // The album save compared that case-sensitively to the loaded value and stamped the
        // first track's preset over every other track's own EQ on a Comment-only save.
        var tracks = Album("A", "X", 2);
        tracks[0].EqPreset = "rock";
        tracks[1].EqPreset = "Jazz";
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);
        vm.SetUserEqPresets(Array.Empty<string>());
        await vm.InitializeAsync();
        Assert.Equal("Rock", vm.SelectedEqPreset);

        vm.Comment = "edited";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Jazz", tracks[1].EqPreset);
    }

    /// <summary>A song file plus sidecars in a throwaway folder.</summary>
    private sealed class TempSongDir : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"noctis-md-open-{Guid.NewGuid():N}");
        public Track Track { get; }
        private readonly TestPersistenceService _persistence = new();

        public TempSongDir()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Side(".flac"), "audio");
            Track = new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "A", Album = "B", FilePath = Side(".flac") };
            Track.AlbumId = Track.ComputeAlbumId(Track.AlbumArtist, Track.Album);
        }

        public string Side(string ext) => Path.Combine(Dir, "song" + ext);

        public MetadataViewModel NewVm(FakeMetadataService meta, IAnimatedCoverService covers, List<string>? trashed = null) =>
            new(Track, meta, new FakeLibraryService { TrackList = new List<Track> { Track } }, _persistence, covers,
                albumScoped: false, albumTracks: null)
            {
                TrashFile = p => { trashed?.Add(Path.GetFileName(p)); return true; },
            };

        public void Dispose()
        {
            _persistence.Dispose();
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    private sealed class CountingAnimatedCoverService : IAnimatedCoverService
    {
        public string? CoverPath { get; init; }
        public int ResolveCalls;
        public string? Resolve(Track track) { Interlocked.Increment(ref ResolveCalls); return CoverPath; }
        public Task<string> ImportAsync(Track track, string sourcePath, AnimatedCoverScope scope)
            => Task.FromResult(string.Empty);
        public Task RemoveAsync(Track track, AnimatedCoverScope scope) => Task.CompletedTask;
    }

    // ── Helpers ──

    // ── Change tracking (rail dots + footer summary) ──

    [Fact]
    public void ChangeTracking_FreshDialog_HasNoChanges()
    {
        var tracks = Album("A", "X", 3);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);

        Assert.Equal(0, vm.ChangeCount);
        Assert.False(vm.HasChanges);
        Assert.False(vm.DetailsChanged);
        Assert.False(vm.OptionsChanged);
    }

    [Fact]
    public void ChangeTracking_EditThenRevert_CountsThenClears()
    {
        var tracks = Album("A", "X", 3);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);
        var original = vm.Genre;

        vm.Genre = "Latin";
        Assert.Equal(1, vm.ChangeCount);
        Assert.True(vm.DetailsChanged);

        vm.Comment = "edited";
        Assert.Equal(2, vm.ChangeCount);

        vm.Genre = original;
        Assert.Equal(1, vm.ChangeCount);
        Assert.True(vm.DetailsChanged);

        vm.Comment = string.Empty;
        Assert.Equal(0, vm.ChangeCount);
        Assert.False(vm.DetailsChanged);
    }

    [Fact]
    public void ChangeTracking_SectionsAreIndependent()
    {
        var tracks = Album("A", "X", 2);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);

        vm.SkipWhenShuffling = !vm.SkipWhenShuffling;

        Assert.True(vm.OptionsChanged);
        Assert.False(vm.DetailsChanged);
        Assert.Equal(1, vm.ChangeCount);
    }

    [Fact]
    public void ChangeTracking_StagedPlayCountReset_CountsAsDetailsChange()
    {
        var tracks = Album("A", "X", 2);
        tracks[0].PlayCount = 4;
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);

        vm.ResetPlayCountCommand.Execute(null);

        Assert.True(vm.DetailsChanged);
        Assert.Equal(1, vm.ChangeCount);
    }

    [Fact]
    public void ChangeTracking_AlbumSummary_NamesTheFileCount()
    {
        var tracks = Album("A", "X", 5);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);

        vm.Year = "1999";

        Assert.Contains("5", vm.ChangeSummary);
        Assert.Contains("1", vm.ChangeSummary);
    }

    private static List<Track> Album(string album, string albumArtist, int count)
    {
        var list = new List<Track>();
        for (int i = 1; i <= count; i++)
        {
            var t = new Track
            {
                Title = $"Track {i}",
                Album = album,
                AlbumArtist = albumArtist,
                Artist = albumArtist,
                TrackNumber = i,
                FilePath = Path.Combine(Path.GetTempPath(), "noctistest", album, $"track{i}.flac"),
            };
            t.AlbumId = Track.ComputeAlbumId(t.AlbumArtist, t.Album);
            list.Add(t);
        }
        return list;
    }

    private static MetadataViewModel NewAlbumVm(
        List<Track> tracks, IPersistenceService persistence,
        out FakeMetadataService meta, out FakeLibraryService library)
    {
        meta = new FakeMetadataService();
        library = new FakeLibraryService { TrackList = tracks.ToList() };
        return new MetadataViewModel(tracks[0], meta, library, persistence, new FakeAnimatedCoverService(),
            albumScoped: true, albumTracks: tracks.ToList());
    }

    private static void SetNewAnimatedCover(MetadataViewModel vm, string sourcePath)
    {
        var field = typeof(MetadataViewModel).GetField("_newAnimatedCoverSource",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(vm, sourcePath);
    }

    private static void SetNewArtwork(MetadataViewModel vm, byte[] data)
    {
        var field = typeof(MetadataViewModel).GetField("_newArtworkData",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(vm, data);
    }

    // ── Service fakes ──

    private sealed class FakeMetadataService : IMetadataService
    {
        private readonly object _gate = new();
        public List<string> WrittenArtPaths { get; } = new();

        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt)
        {
            embeddedArt = null;
            return null;
        }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public List<string> WrittenTagPaths { get; } = new();

        public bool WriteTrackMetadata(Track track)
        {
            lock (_gate) WrittenTagPaths.Add(track.FilePath);
            return true;
        }

        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null)
        {
            lock (_gate) WrittenTagPaths.Add(targetFilePath);
            return true;
        }
        public bool WriteRating(string filePath, int rating, bool isDisliked) => true;

        public List<string> ClearedYearPaths { get; } = new();

        public bool ClearYear(string filePath)
        {
            lock (_gate) ClearedYearPaths.Add(filePath);
            return true;
        }

        bool IMetadataService.WriteAdvancedFields(string filePath,
            Noctis.Services.AdvancedTagIO.AdvancedFields fields,
            Noctis.Services.AdvancedTagIO.AdvancedFields original) => true;
        /// <summary>Makes the window's background load fail, to test its failure path.</summary>
        public bool ThrowOnReadFileInfo { get; init; }
        /// <summary>Holds the background load mid-read until set, to edit "before it lands".</summary>
        public ManualResetEventSlim? ReadFileInfoGate { get; init; }

        public AudioFileInfo? ReadFileInfo(string filePath)
        {
            ReadFileInfoGate?.Wait(TimeSpan.FromSeconds(10));
            if (ThrowOnReadFileInfo) throw new IOException("simulated read failure");
            return null;
        }

        /// <summary>Paths whose cover write fails, as SaveTagsAtomically reports it (false).</summary>
        public HashSet<string> FailArtPaths { get; } = new();

        public bool WriteAlbumArt(string filePath, byte[]? imageData)
        {
            lock (_gate) WrittenArtPaths.Add(filePath);
            return !FailArtPaths.Contains(filePath);
        }
    }

    private sealed class FakeAnimatedCoverService : IAnimatedCoverService
    {
        public string? Resolve(Track track) => null;
        public Task<string> ImportAsync(Track track, string sourcePath, AnimatedCoverScope scope)
            => Task.FromResult(string.Empty);
        public Task RemoveAsync(Track track, AnimatedCoverScope scope) => Task.CompletedTask;
    }

    private sealed class FakeLibraryService : ILibraryService
    {
        public List<Track> TrackList { get; set; } = new();
        public IReadOnlyList<Track> Tracks => TrackList;
        public IReadOnlyList<Album> Albums => Array.Empty<Album>();
        public IReadOnlyList<Artist> Artists => Array.Empty<Artist>();

        public event EventHandler? LibraryUpdated { add { } remove { } }
        public event EventHandler<int>? ScanProgress { add { } remove { } }
        public event EventHandler? FavoritesChanged { add { } remove { } }
        public event EventHandler<List<string>>? MusicFoldersChanged { add { } remove { } }
        public event EventHandler<string[]>? ScanAborted { add { } remove { } }

        public Task ScanAsync(IEnumerable<string> folders, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseActiveScanForShutdownAsync(TimeSpan timeout) => Task.CompletedTask;
        public Task ImportFilesAsync(IEnumerable<string> filePaths, CancellationToken ct = default, IProgress<int>? progress = null) => Task.CompletedTask;
        public Track? GetTrackById(Guid id) => null;
        public Album? GetAlbumById(Guid id) => null;
        public IReadOnlyList<Album> GetAlbumsByArtist(string artistName) => Array.Empty<Album>();
        public Task RemoveTrackAsync(Guid id) => Task.CompletedTask;
        public Task RemoveTracksAsync(IEnumerable<Guid> ids) => Task.CompletedTask;
        public List<(string oldPath, string newPath)> Relocated { get; } = new();
        public Task<IReadOnlyDictionary<Guid, Guid>> RelocateTracksAsync(
            IReadOnlyList<(string oldPath, string newPath)> moves, CancellationToken ct = default)
        {
            Relocated.AddRange(moves);
            return Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(new Dictionary<Guid, Guid>());
        }
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
        public Task SaveTrackUserStateAsync(IReadOnlyCollection<Track> tracks) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
        public Task RebuildIndexAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void NotifyFavoritesChanged() { }
        public void NotifyFavoritesChanged(IReadOnlyCollection<Track>? changed) { }
        public Task SetTracksRatingAsync(IReadOnlyList<Track> tracks, int rating) => Task.CompletedTask;
        public Task SetTracksDislikedAsync(IReadOnlyList<Track> tracks, bool isDisliked) => Task.CompletedTask;
        public Task SetTracksSnoozedAsync(IReadOnlyList<Track> tracks, DateTime? until) => Task.CompletedTask;
        public Task SetTracksBadgeAsync(IReadOnlyList<Track> tracks, string? badge) => Task.CompletedTask;
        public IReadOnlyList<string> GetBadgeNames() => Array.Empty<string>();
        public void NotifyMetadataChanged() { }
        public Task<int> ApplyMergeFeaturedFromTitlesAsync(bool enabled, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ApplyArtistCreditJoinAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> BackfillMissingArtworkAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}

[CollectionDefinition("App.Services global", DisableParallelization = true)]
public class AppServicesCollection { }
