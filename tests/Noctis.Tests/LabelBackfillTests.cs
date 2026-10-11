using Noctis.Models;
using Noctis.Services;
using Xunit;
using static Noctis.Tests.ReplayGainWriteTests;

namespace Noctis.Tests;

/// <summary>
/// The v11 metadata-schema migration: libraries indexed before the record label was read
/// (Track.Label) get it without a manual re-tag. Local files are re-read in place on load;
/// tracks the phone reaches through Android's document tree (content://) can't be opened
/// by path here, so they are marked for the next scan to re-read instead of skipping them
/// as unchanged.
/// </summary>
public class LabelBackfillTests : IDisposable
{
    private readonly FolderMetadataBackfillTests.BackfillTestPersistence _persistence = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public LabelBackfillTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _persistence.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private LibraryService MakeLibrary() =>
        new(new MetadataService(), _persistence, new SqliteLibraryIndexService(_persistence), new FolderMetadataBackfillTests.FakeAuditTrail());

    private static async Task WaitUntil(Func<bool> condition, int budgetMs = 30000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
            await Task.Delay(50);
    }

    private static Track Indexed(string path, DateTime modified) => new()
    {
        Id = Guid.NewGuid(),
        FilePath = path,
        Title = "Leave the Door Open",
        Artist = "Silk Sonic",
        AlbumArtist = "Silk Sonic",
        Album = "An Evening with Silk Sonic",
        AlbumId = Track.ComputeAlbumId("Silk Sonic", "An Evening with Silk Sonic"),
        Duration = TimeSpan.FromMinutes(4),
        FileSize = 1000,
        SourceType = SourceType.Local,
        LastModified = modified,
        DateAdded = modified,
    };

    private string LabelledFlac(string label)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.flac");
        File.Move(CreateFlac(_dir), path);
        using (var f = TagLib.File.Create(path))
        {
            ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true)).SetField("LABEL", label);
            f.Save();
        }
        return path;
    }

    [Fact]
    public async Task Load_V10Library_ReadsTheLabelOfALocalFile()
    {
        var track = Indexed(LabelledFlac("Aftermath Entertainment"), DateTime.UtcNow);
        _persistence.LibraryTracks.Add(track);
        _persistence.Settings.MetadataSchemaVersion = 10;

        await MakeLibrary().LoadAsync();
        await WaitUntil(() => track.Label.Length > 0 && _persistence.Settings.MetadataSchemaVersion == 12);

        Assert.Equal("Aftermath Entertainment", track.Label);
        Assert.Equal(12, _persistence.Settings.MetadataSchemaVersion);
    }

    [Fact]
    public async Task Load_V10Library_MarksPhoneDocumentTracksForTheNextScan_AndNothingElse()
    {
        var stamp = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var phone = Indexed("content://com.android.externalstorage.documents/tree/primary%3AMusic/document/primary%3AMusic%2Fa.flac", stamp);
        var labelled = Indexed("content://com.android.externalstorage.documents/tree/primary%3AMusic/document/primary%3AMusic%2Fb.flac", stamp);
        labelled.Label = "Atlantic";
        var streamed = Indexed("noctis-remote://tr-0011", stamp);
        _persistence.LibraryTracks.AddRange(new[] { phone, labelled, streamed });
        _persistence.Settings.MetadataSchemaVersion = 10;

        await MakeLibrary().LoadAsync();
        await WaitUntil(() => _persistence.Settings.MetadataSchemaVersion == 12);

        // A changed stamp is what makes the scan re-read instead of keeping the indexed track.
        Assert.NotEqual(stamp, phone.LastModified);
        Assert.Equal(stamp, labelled.LastModified);    // already has its label
        Assert.Equal(stamp, streamed.LastModified);    // the desktop's song: never folder-scanned
    }

    // Counts the label-only reads the pass makes, and can quit the app (cancel the pass)
    // from inside the Nth one — the owner closing Noctis halfway through.
    private sealed class CountingMetadata : MetadataService, IMetadataService
    {
        public readonly List<string> LabelReads = new();
        public int CancelOnRead;
        public LibraryService? Library;

        string IMetadataService.ReadLabel(string filePath)
        {
            LabelReads.Add(Path.GetFileName(filePath));
            if (LabelReads.Count == CancelOnRead)
                _ = Library!.PauseActiveScanForShutdownAsync(TimeSpan.Zero); // cancels synchronously
            return ReadLabel(filePath);
        }
    }

    private string Flac(string name, string? label)
    {
        var path = Path.Combine(_dir, name);
        File.Move(CreateFlac(_dir), path);
        if (label != null)
        {
            using var f = TagLib.File.Create(path);
            ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true)).SetField("LABEL", label);
            f.Save();
        }
        return path;
    }

    private async Task<(LibraryService Library, CountingMetadata Metadata)> LaunchAsync(int cancelOnRead = 0)
    {
        var metadata = new CountingMetadata { CancelOnRead = cancelOnRead };
        var library = new LibraryService(metadata, _persistence, new SqliteLibraryIndexService(_persistence),
            new FolderMetadataBackfillTests.FakeAuditTrail());
        metadata.Library = library;
        await library.LoadAsync();
        await library.BackgroundInit;
        await library.LabelBackfill;
        return (library, metadata);
    }

    [Fact]
    public void ReadLabel_MatchesTheFullRead()
    {
        var vorbisLabel = Flac("label.flac", "Def Jam");

        var organization = Flac("org.flac", null);
        using (var f = TagLib.File.Create(organization))
        {
            ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true)).SetField("ORGANIZATION", "Warp");
            f.Save();
        }

        var mp3 = Path.Combine(_dir, "tpub.mp3");
        File.Move(CreateMp3WithId3v2Only(_dir), mp3);
        using (var f = TagLib.File.Create(mp3))
        {
            f.Tag.Publisher = "XL Recordings";
            f.Save();
        }

        var unlabelled = Flac("none.flac", null);

        var metadata = new MetadataService();
        var expected = new Dictionary<string, string>
        {
            [vorbisLabel] = "Def Jam",
            [organization] = "Warp",
            [mp3] = "XL Recordings",
            [unlabelled] = "",
        };
        foreach (var (path, label) in expected)
        {
            Assert.Equal(label, metadata.ReadLabel(path));
            Assert.Equal(metadata.ReadTrackMetadata(path)!.Label, metadata.ReadLabel(path));
        }
        Assert.Equal("", metadata.ReadLabel(Path.Combine(_dir, "missing.flac")));
    }

    [Fact]
    public async Task Shutdown_MidPass_NextLaunchContinuesWithoutRereadingCheckedFiles()
    {
        var tracks = new[]
        {
            Indexed(Flac("a0.flac", null), DateTime.UtcNow),
            Indexed(Flac("a1.flac", "Def Jam"), DateTime.UtcNow),
            Indexed(Flac("a2.flac", null), DateTime.UtcNow),
            Indexed(Flac("a3.flac", null), DateTime.UtcNow),
            Indexed(Flac("a4.flac", "XL Recordings"), DateTime.UtcNow),
            Indexed(Flac("a5.flac", null), DateTime.UtcNow),
        };
        _persistence.LibraryTracks.AddRange(tracks);
        _persistence.Settings.MetadataSchemaVersion = 10;

        // Quit while the third file is being read.
        var (_, first) = await LaunchAsync(cancelOnRead: 3);
        Assert.Equal(new[] { "a0.flac", "a1.flac", "a2.flac" }, first.LabelReads);
        Assert.Equal(10, _persistence.Settings.MetadataSchemaVersion);   // not done: not stamped

        // The label found before the quit never reached library.json (this fake never saves):
        // the progress log has to bring it back.
        tracks[1].Label = string.Empty;

        var (_, second) = await LaunchAsync();
        // a0 and a2 had no label — they count as checked and are not opened again.
        Assert.Equal(new[] { "a3.flac", "a4.flac", "a5.flac" }, second.LabelReads);
        Assert.Equal("Def Jam", tracks[1].Label);
        Assert.Equal("XL Recordings", tracks[4].Label);
        Assert.Equal(12, _persistence.Settings.MetadataSchemaVersion);
        Assert.False(File.Exists(Path.Combine(_persistence.DataDirectory, "label-backfill.progress")));

        var (_, third) = await LaunchAsync();
        Assert.Empty(third.LabelReads);
    }

    [Fact]
    public async Task CompletedPass_FilesWithoutALabel_AreNotReadAgain()
    {
        _persistence.LibraryTracks.Add(Indexed(Flac("b0.flac", null), DateTime.UtcNow));
        _persistence.LibraryTracks.Add(Indexed(Flac("b1.flac", null), DateTime.UtcNow));
        _persistence.Settings.MetadataSchemaVersion = 10;

        var (_, first) = await LaunchAsync();
        Assert.Equal(2, first.LabelReads.Count);
        Assert.Equal(12, _persistence.Settings.MetadataSchemaVersion);

        var (_, second) = await LaunchAsync();
        Assert.Empty(second.LabelReads);
    }

    [Fact]
    public async Task Load_UpToDateSchema_LeavesLabelsAndStampsAlone()
    {
        var stamp = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var phone = Indexed("content://com.android.externalstorage.documents/tree/x/document/y.flac", stamp);
        _persistence.LibraryTracks.Add(phone);
        _persistence.Settings.MetadataSchemaVersion = 12;

        await MakeLibrary().LoadAsync();
        await Task.Delay(500);

        Assert.Equal(stamp, phone.LastModified);
    }
}
