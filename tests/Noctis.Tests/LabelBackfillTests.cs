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
        await WaitUntil(() => track.Label.Length > 0 && _persistence.Settings.MetadataSchemaVersion == 11);

        Assert.Equal("Aftermath Entertainment", track.Label);
        Assert.Equal(11, _persistence.Settings.MetadataSchemaVersion);
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
        await WaitUntil(() => _persistence.Settings.MetadataSchemaVersion == 11);

        // A changed stamp is what makes the scan re-read instead of keeping the indexed track.
        Assert.NotEqual(stamp, phone.LastModified);
        Assert.Equal(stamp, labelled.LastModified);    // already has its label
        Assert.Equal(stamp, streamed.LastModified);    // the desktop's song: never folder-scanned
    }

    [Fact]
    public async Task Load_UpToDateSchema_LeavesLabelsAndStampsAlone()
    {
        var stamp = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var phone = Indexed("content://com.android.externalstorage.documents/tree/x/document/y.flac", stamp);
        _persistence.LibraryTracks.Add(phone);
        _persistence.Settings.MetadataSchemaVersion = 11;

        await MakeLibrary().LoadAsync();
        await Task.Delay(500);

        Assert.Equal(stamp, phone.LastModified);
    }
}
