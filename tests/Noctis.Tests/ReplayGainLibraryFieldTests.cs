using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #151: the Songs list's ReplayGain column needs the file's ReplayGain on the track.
/// The scan copies it from the tags it already reads, existing libraries get it from one light
/// resumable background pass, and both match what the player reads at playback.
/// </summary>
public class ReplayGainLibraryFieldTests : IDisposable
{
    private readonly FolderMetadataBackfillTests.BackfillTestPersistence _persistence = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "rg151-" + Guid.NewGuid().ToString("N"));

    public ReplayGainLibraryFieldTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _persistence.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Flac(string name, string? gain = null, string? peak = null, string? album = null)
    {
        var path = Path.Combine(_dir, name);
        File.Move(ReplayGainWriteTests.CreateFlac(_dir), path);
        if (gain != null || peak != null || album != null)
        {
            using var f = TagLib.File.Create(path);
            var xiph = (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true);
            if (gain != null) xiph.SetField("REPLAYGAIN_TRACK_GAIN", gain);
            if (peak != null) xiph.SetField("REPLAYGAIN_TRACK_PEAK", peak);
            if (album != null) xiph.SetField("REPLAYGAIN_ALBUM_GAIN", album);
            f.Save();
        }
        return path;
    }

    private string Mp3(string name, string gain, string peak)
    {
        var path = Path.Combine(_dir, name);
        File.Move(ReplayGainWriteTests.CreateMp3WithId3v2Only(_dir), path);
        using var f = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
        id3.AddFrame(new TagLib.Id3v2.UserTextInformationFrame("REPLAYGAIN_TRACK_GAIN") { Text = new[] { gain } });
        id3.AddFrame(new TagLib.Id3v2.UserTextInformationFrame("replaygain_track_peak") { Text = new[] { peak } });
        f.Save();
        return path;
    }

    private static Track Row(string path) => new()
    {
        Id = Guid.NewGuid(),
        FilePath = path,
        Title = Path.GetFileNameWithoutExtension(path),
        Artist = "A",
        AlbumArtist = "A",
        Album = "B",
        AlbumId = Track.ComputeAlbumId("A", "B"),
        Duration = TimeSpan.FromMinutes(3),
        FileSize = 1000,
        SourceType = SourceType.Local,
        LastModified = DateTime.UtcNow,
        DateAdded = DateTime.UtcNow,
    };

    // ── Model + library.json ─────────────────────────────────────────────

    [Fact]
    public async Task Track_ReplayGain_RoundTripsThroughLibraryJson_AndAnOlderLibraryLoadsUnread()
    {
        var root = Path.Combine(_dir, "data");
        var persistence = new PersistenceService(root);
        var tagged = Row("C:/m/a.flac");
        tagged.ApplyReplayGain(new ReplayGainValues(-7.84, 0.988123, -6.1));
        var none = Row("C:/m/b.flac");
        none.ApplyReplayGain(ReplayGainValues.None);
        var unread = Row("C:/m/c.flac");
        var nan = Row("C:/m/d.flac");
        nan.ApplyReplayGain(new ReplayGainValues(double.NaN, double.PositiveInfinity, null)); // "nan dB" tag
        await persistence.SaveLibraryAsync(new List<Track> { tagged, none, unread, nan });

        var json = await File.ReadAllTextAsync(Path.Combine(root, "library.json"));
        var loaded = (await new PersistenceService(root).LoadLibraryAsync())!;
        var byId = loaded.ToDictionary(t => t.Id);

        Assert.Equal(-7.84, byId[tagged.Id].ReplayGainTrackDb);
        Assert.Equal(0.988123, byId[tagged.Id].ReplayGainTrackPeak);
        Assert.Equal(-6.1, byId[tagged.Id].ReplayGainAlbumDb);
        Assert.True(byId[tagged.Id].ReplayGainChecked);
        Assert.True(byId[none.Id].ReplayGainChecked);
        Assert.Null(byId[none.Id].ReplayGainTrackDb);
        Assert.False(byId[unread.Id].ReplayGainChecked);
        Assert.True(byId[nan.Id].ReplayGainChecked);
        Assert.Null(byId[nan.Id].ReplayGainTrackDb);
        Assert.Null(byId[nan.Id].ReplayGainTrackPeak);
        // Nothing written for the empty fields: no cost per track for files without gain.
        Assert.Equal(1, CountOf(json, "\"replayGainTrackDb\""));
        Assert.Equal(3, CountOf(json, "\"replayGainChecked\""));

        // A library.json from before the field existed.
        var oldRoot = Path.Combine(_dir, "old");
        Directory.CreateDirectory(oldRoot);
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "library.json"),
            $"[{{\"id\":\"{id}\",\"filePath\":\"C:/m/old.flac\",\"title\":\"Old\",\"bpm\":120}}]");
        var old = Assert.Single((await new PersistenceService(oldRoot).LoadLibraryAsync())!);
        Assert.Equal(id, old.Id);
        Assert.False(old.ReplayGainChecked);
        Assert.Null(old.ReplayGainTrackDb);
        Assert.Equal(string.Empty, old.ReplayGainFormatted);
    }

    private static int CountOf(string s, string needle)
    {
        var n = 0;
        for (var i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = s.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void ReplayGainFormatted_OneDecimal_WithSign_BlankWithoutGain()
    {
        var t = new Track();
        Assert.Equal(string.Empty, t.ReplayGainFormatted);
        t.ApplyReplayGain(new ReplayGainValues(-7.23, null, null));
        Assert.Equal((-7.2).ToString("0.0") + " dB", t.ReplayGainFormatted);
        t.ApplyReplayGain(new ReplayGainValues(3.0, null, null));
        Assert.Equal("+" + 3.0.ToString("0.0") + " dB", t.ReplayGainFormatted);
    }

    // ── Scan ─────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_FillsReplayGainFromTheTagsItReads_SameAsThePlayerAndTheLightRead()
    {
        var flac = Flac("tagged.flac", "-7.84 dB", "0.988123", "-6.10 dB");
        var mp3 = Mp3("tagged.mp3", "+2.50 dB", "0.5");
        var plain = Flac("plain.flac");
        var metadata = new MetadataService();

        var a = metadata.ReadTrackMetadata(flac)!;
        Assert.True(a.ReplayGainChecked);
        Assert.Equal(-7.84, a.ReplayGainTrackDb);
        Assert.Equal(0.988123, a.ReplayGainTrackPeak);
        Assert.Equal(-6.10, a.ReplayGainAlbumDb);

        var b = metadata.ReadTrackMetadata(mp3)!;
        Assert.Equal(2.5, b.ReplayGainTrackDb);
        Assert.Equal(0.5, b.ReplayGainTrackPeak);   // TXXX description matched case-insensitively
        Assert.Null(b.ReplayGainAlbumDb);

        var c = metadata.ReadTrackMetadata(plain)!;
        Assert.True(c.ReplayGainChecked);            // read, none: the backfill skips it
        Assert.Null(c.ReplayGainTrackDb);

        foreach (var (path, track) in new[] { (flac, a), (mp3, b), (plain, c) })
        {
            // Playback reads the same gains it did before.
            Assert.Equal((track.ReplayGainTrackDb, track.ReplayGainAlbumDb), VlcAudioPlayer.ReadReplayGainTags(path));
            // The backfill's light read (FLAC: the comment block alone) agrees with the full read.
            Assert.Equal(new ReplayGainValues(track.ReplayGainTrackDb, track.ReplayGainTrackPeak, track.ReplayGainAlbumDb),
                metadata.ReadReplayGain(path));
        }
        Assert.Null(metadata.ReadReplayGain(Path.Combine(_dir, "missing.flac")));
    }

    // ── Backfill ─────────────────────────────────────────────────────────

    private sealed class CountingMetadata : MetadataService, IMetadataService
    {
        public readonly List<string> Reads = new();
        public readonly List<string> LabelReads = new();
        public int CancelOnRead;
        public LibraryService? Library;

        string IMetadataService.ReadLabel(string filePath)
        {
            lock (Reads) LabelReads.Add(Path.GetFileName(filePath) + "@" + Reads.Count);
            return ReadLabel(filePath);
        }

        ReplayGainValues? IMetadataService.ReadReplayGain(string filePath)
        {
            lock (Reads) Reads.Add(Path.GetFileName(filePath));
            if (Reads.Count == CancelOnRead)
                _ = Library!.PauseActiveScanForShutdownAsync(TimeSpan.Zero); // the owner quits mid-pass
            return ReadReplayGain(filePath);
        }
    }

    private async Task<CountingMetadata> LaunchAsync(int cancelOnRead = 0)
    {
        var metadata = new CountingMetadata { CancelOnRead = cancelOnRead };
        var library = new LibraryService(metadata, _persistence, new SqliteLibraryIndexService(_persistence),
            new FolderMetadataBackfillTests.FakeAuditTrail());
        metadata.Library = library;
        await library.LoadAsync();
        await library.BackgroundInit;
        await library.LabelBackfill;
        return metadata;
    }

    private string ProgressFile => Path.Combine(_persistence.DataDirectory, "replaygain-backfill.progress");

    [Fact]
    public async Task Backfill_ReadsUnreadTracksOnce_MarksFilesWithoutGain_SkipsMissingAndStreamed()
    {
        _persistence.Settings.MetadataSchemaVersion = 13; // no label/genre/MP3-length pass pending
        var tagged = Row(Flac("a.flac", "-7.84 dB", "0.98", "-6.10 dB"));
        var plain = Row(Flac("b.flac"));
        var missing = Row(Path.Combine(_dir, "gone.flac"));
        var offline = Row("Q:\\NoSuchDrive\\x.flac");
        var streamed = Row("http://server/rest/stream?id=1");
        var known = Row(Flac("c.flac", "-1.00 dB"));
        known.ApplyReplayGain(new ReplayGainValues(-4.0, null, null)); // read earlier: left alone
        _persistence.LibraryTracks.AddRange(new[] { tagged, plain, missing, offline, streamed, known });

        var first = await LaunchAsync();

        Assert.Equal(new[] { "a.flac", "b.flac" }, first.Reads.OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(tagged.ReplayGainChecked);
        Assert.Equal(-7.84, tagged.ReplayGainTrackDb);
        Assert.Equal(0.98, tagged.ReplayGainTrackPeak);
        Assert.Equal(-6.10, tagged.ReplayGainAlbumDb);
        Assert.True(plain.ReplayGainChecked);
        Assert.Null(plain.ReplayGainTrackDb);
        Assert.False(missing.ReplayGainChecked);   // tried again next launch
        Assert.False(offline.ReplayGainChecked);
        Assert.False(streamed.ReplayGainChecked);
        Assert.Equal(-4.0, known.ReplayGainTrackDb);
        Assert.False(File.Exists(ProgressFile));
        Assert.Equal(13, _persistence.Settings.MetadataSchemaVersion); // stamps nothing

        // Next launch: nothing readable is opened again; the missing file came back meanwhile.
        File.Move(Flac("back.flac", "+1.25 dB"), missing.FilePath);
        var second = await LaunchAsync();
        Assert.Equal(new[] { "gone.flac" }, second.Reads);
        Assert.Equal(1.25, missing.ReplayGainTrackDb);

        var third = await LaunchAsync();
        Assert.Empty(third.Reads);
    }

    [Fact]
    public async Task Backfill_ShutdownMidPass_NextLaunchContinuesWithoutRereadingCheckedFiles()
    {
        _persistence.Settings.MetadataSchemaVersion = 13;
        var tracks = new[]
        {
            Row(Flac("a0.flac")),
            Row(Flac("a1.flac", "-3.00 dB")),
            Row(Flac("a2.flac")),
            Row(Flac("a3.flac", "-5.50 dB")),
        };
        _persistence.LibraryTracks.AddRange(tracks);

        // Quit while the second file is being read.
        var first = await LaunchAsync(cancelOnRead: 2);
        Assert.Equal(new[] { "a0.flac", "a1.flac" }, first.Reads);
        Assert.True(File.Exists(ProgressFile));

        // What the first run found never reached library.json (this fake never saves): the
        // progress log has to bring it back.
        foreach (var t in tracks.Take(2))
        {
            t.ReplayGainChecked = false;
            t.ReplayGainTrackDb = null;
        }

        var second = await LaunchAsync();
        Assert.Equal(new[] { "a2.flac", "a3.flac" }, second.Reads);
        Assert.All(tracks, t => Assert.True(t.ReplayGainChecked));
        Assert.Equal(new double?[] { null, -3.0, null, -5.5 }, tracks.Select(t => t.ReplayGainTrackDb));
        Assert.False(File.Exists(ProgressFile));

        var third = await LaunchAsync();
        Assert.Empty(third.Reads);
    }

    /// <summary>GitHub #158: the pass ran last, behind the label and genre passes (each a read
    /// of most of the library), so the column stayed blank for most songs until both were done.</summary>
    [Fact]
    public async Task Backfill_RunsBeforeTheLabelPass()
    {
        _persistence.Settings.MetadataSchemaVersion = 10; // label (v11), genre (v12) and MP3 length (v13) passes pending
        var tracks = new[] { Row(Flac("a.flac", "-3.00 dB")), Row(Flac("b.flac", "-5.50 dB")) };
        _persistence.LibraryTracks.AddRange(tracks);

        var run = await LaunchAsync();

        Assert.Equal(new[] { "a.flac", "b.flac" }, run.Reads);
        Assert.Equal(new double?[] { -3.0, -5.5 }, tracks.Select(t => t.ReplayGainTrackDb));
        // Every label read came after both ReplayGain reads.
        Assert.Equal(new[] { "a.flac@2", "b.flac@2" }, run.LabelReads);
        Assert.Equal(13, _persistence.Settings.MetadataSchemaVersion);
    }

    /// <summary>GitHub #158: rows on screen are read at once (no wait for the pass), once each,
    /// and the pass then skips them instead of opening the file again.</summary>
    [Fact]
    public async Task ReadReplayGainSoon_ReadsUnreadRowsNow_ThePassSkipsThemAfter()
    {
        _persistence.Settings.MetadataSchemaVersion = 13;
        var shown = Row(Flac("a.flac", "-7.84 dB", "0.98", "-6.10 dB"));
        var plain = Row(Flac("b.flac"));
        var known = Row(Flac("c.flac", "-1.00 dB"));
        known.ApplyReplayGain(new ReplayGainValues(-4.0, null, null)); // read earlier: left alone
        var streamed = Row("http://server/rest/stream?id=1");
        var later = Row(Flac("d.flac", "+1.25 dB"));
        _persistence.LibraryTracks.AddRange(new[] { shown, plain, known, streamed, later });

        var metadata = new CountingMetadata();
        var library = new LibraryService(metadata, _persistence, new SqliteLibraryIndexService(_persistence),
            new FolderMetadataBackfillTests.FakeAuditTrail());
        library.ReadReplayGainSoon(new[] { shown, plain, known, streamed, shown });
        await library.ReplayGainSoonTask.WaitAsync(TimeSpan.FromSeconds(10));
        library.ReadReplayGainSoon(new[] { shown, plain }); // scrolled back: already read
        await library.ReplayGainSoonTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "a.flac", "b.flac" }, metadata.Reads);
        Assert.Equal(-7.84, shown.ReplayGainTrackDb);
        Assert.Equal(0.98, shown.ReplayGainTrackPeak);
        Assert.Equal(-6.10, shown.ReplayGainAlbumDb);
        Assert.True(plain.ReplayGainChecked);
        Assert.Null(plain.ReplayGainTrackDb);
        Assert.Equal(-4.0, known.ReplayGainTrackDb);
        Assert.False(streamed.ReplayGainChecked);

        // The startup pass opens only what is still unread.
        await library.LoadAsync();
        await library.BackgroundInit;
        await library.LabelBackfill;
        Assert.Equal(new[] { "a.flac", "b.flac", "d.flac" }, metadata.Reads);
        Assert.Equal(1.25, later.ReplayGainTrackDb);
    }

    [Fact]
    public void ProgressValue_RoundTrips_AndIsNeverBlank()
    {
        foreach (var v in new[] { new ReplayGainValues(-7.84, 0.988123, -6.1), ReplayGainValues.None, new ReplayGainValues(1.0 / 3, null, null) })
        {
            var encoded = LibraryService.EncodeReplayGain(v);
            Assert.False(string.IsNullOrWhiteSpace(encoded));
            Assert.Equal(v, LibraryService.DecodeReplayGain(encoded));
        }
    }
}
