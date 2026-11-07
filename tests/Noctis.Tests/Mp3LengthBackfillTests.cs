using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The v13 metadata-schema migration (11-07): MP3s indexed while TagLib's length was the only
/// one (ffmpeg's "Info" header in a 64 kbps frame ahead of 128 kbps audio: twice the length at
/// half the bitrate) get the length their header states, without a rescan (which skips
/// unchanged files).
/// </summary>
public class Mp3LengthBackfillTests : IDisposable
{
    private readonly FolderMetadataBackfillTests.BackfillTestPersistence _persistence = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public Mp3LengthBackfillTests() => Directory.CreateDirectory(_dir);

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

    // A 64 kbps "Info" frame stating 767 frames, then 766 frames of 128 kbps MPEG-1 Layer III.
    private string InfoHeadedMp3()
    {
        var header = new byte[144000 * 64 / 44100];
        header[0] = 0xFF; header[1] = 0xFB; header[2] = 5 << 4; header[3] = 0x40;
        "Info"u8.CopyTo(header.AsSpan(36));
        void Be(int at, int v) { header[at] = (byte)(v >> 24); header[at + 1] = (byte)(v >> 16); header[at + 2] = (byte)(v >> 8); header[at + 3] = (byte)v; }
        const int frames = 766;
        var frameLength = 144000 * 128 / 44100;
        Be(40, 3);
        Be(44, frames + 1);
        Be(48, header.Length + frames * frameLength);
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.mp3");
        using var fs = File.Create(path);
        fs.Write(header);
        var frame = new byte[frameLength];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 9 << 4; frame[3] = 0x40;
        for (var i = 0; i < frames; i++) fs.Write(frame);
        return path;
    }

    private static Track Indexed(string path, TimeSpan duration, int bitrate) => new()
    {
        Id = Guid.NewGuid(),
        FilePath = path,
        Title = "Ride",
        Artist = "Metro Boomin",
        AlbumArtist = "Metro Boomin",
        Album = "T187",
        AlbumId = Track.ComputeAlbumId("Metro Boomin", "T187"),
        Duration = duration,
        Bitrate = bitrate,
        FileSize = 1000,
        SourceType = SourceType.Local,
        LastModified = DateTime.UtcNow,
        DateAdded = DateTime.UtcNow,
    };

    [Fact]
    public async Task Load_V12Library_TakesTheLengthTheHeaderStates()
    {
        var track = Indexed(InfoHeadedMp3(), TimeSpan.FromSeconds(40.1), 64);
        _persistence.LibraryTracks.Add(track);
        _persistence.Settings.MetadataSchemaVersion = 12;

        await MakeLibrary().LoadAsync();
        await WaitUntil(() => _persistence.Settings.MetadataSchemaVersion == 13);

        Assert.Equal(13, _persistence.Settings.MetadataSchemaVersion);
        Assert.InRange(track.Duration.TotalSeconds, 20.0, 20.05);
        Assert.InRange(track.Bitrate, 127, 129);
    }

    [Fact]
    public void Apply_LeavesARowWithinASecond_AndIgnoresJunk()
    {
        var track = Indexed("x.mp3", TimeSpan.FromSeconds(20.5), 130);
        LibraryService.ApplyStatedMp3Length(track, $"{TimeSpan.FromSeconds(20.0).Ticks}|128");
        Assert.Equal(20.5, track.Duration.TotalSeconds, 3);
        Assert.Equal(130, track.Bitrate);

        LibraryService.ApplyStatedMp3Length(track, "garbage");
        LibraryService.ApplyStatedMp3Length(track, "0|0");
        Assert.Equal(20.5, track.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void Read_IsEmpty_ForAFileWithoutAHeader()
    {
        var path = Path.Combine(_dir, "plain.mp3");
        File.WriteAllBytes(path, new byte[10_000]);
        Assert.Equal(string.Empty, LibraryService.ReadStatedMp3Length(path));
    }
}
