using System.Text;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord Tangent, "album dates going missing": WAV albums read as year 0. TagLib# reads
/// the year only when the RIFF INFO ICRD field holds a bare "2019", and keeps the album in
/// DIRC, while ffmpeg, Windows and most taggers write a full ICRD date and IPRD.
/// The files here are laid out byte for byte the way ffmpeg writes them.
/// </summary>
public class WavInfoDateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-wavinfo-" + Guid.NewGuid().ToString("N"));

    public WavInfoDateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string WriteWav(string name, params (string Id, string Value)[] info)
    {
        static byte[] Chunk(string id, byte[] body)
        {
            var padded = body.Length % 2 == 1 ? body.Concat(new byte[1]).ToArray() : body;
            return Encoding.ASCII.GetBytes(id).Concat(BitConverter.GetBytes(body.Length)).Concat(padded).ToArray();
        }

        var fmt = new List<byte>();
        fmt.AddRange(BitConverter.GetBytes((short)1));      // PCM
        fmt.AddRange(BitConverter.GetBytes((short)1));      // mono
        fmt.AddRange(BitConverter.GetBytes(44100));
        fmt.AddRange(BitConverter.GetBytes(44100 * 2));
        fmt.AddRange(BitConverter.GetBytes((short)2));
        fmt.AddRange(BitConverter.GetBytes((short)16));

        var list = Encoding.ASCII.GetBytes("INFO").AsEnumerable();
        foreach (var (id, value) in info)
            list = list.Concat(Chunk(id, Encoding.UTF8.GetBytes(value + "\0")));

        var body = Encoding.ASCII.GetBytes("WAVE")
            .Concat(Chunk("fmt ", fmt.ToArray()))
            .Concat(Chunk("LIST", list.ToArray()))
            .Concat(Chunk("data", new byte[44100 * 2]))
            .ToArray();
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Chunk("RIFF", body));
        return path;
    }

    private static Track Read(string path) => new MetadataService().ReadTrackMetadata(path)!;

    [Fact]
    public void FullIcrdDate_GivesYearAndReleaseDate()
    {
        var t = Read(WriteWav("a.wav", ("INAM", "Song"), ("IPRD", "The Album"), ("ICRD", "2019-05-10")));
        Assert.Equal(2019, t.Year);
        Assert.Equal("2019-05-10", t.ReleaseDate);
    }

    [Fact]
    public void IprdAlbum_IsRead()
    {
        var t = Read(WriteWav("b.wav", ("INAM", "Song"), ("IPRD", "The Album"), ("ICRD", "2019")));
        Assert.Equal("The Album", t.Album);
        Assert.Equal(2019, t.Year);
    }

    [Fact]
    public void NoDate_StaysZero()
    {
        var t = Read(WriteWav("c.wav", ("INAM", "Song"), ("ICMT", "Released 2019-05-10")));
        // A date in the comment is not a release date. (The album falls back to the folder name.)
        Assert.Equal(0, t.Year);
        Assert.Equal(string.Empty, t.ReleaseDate);
    }

    [Fact]
    public void EditingAnotherField_KeepsTheFullDate()
    {
        var path = WriteWav("d.wav", ("INAM", "Song"), ("IPRD", "The Album"), ("ICRD", "2019-05-10"));
        var original = Read(path);
        var edited = Read(path);
        edited.Title = "Renamed";
        Assert.True(new MetadataService().WriteTrackMetadata(edited));

        var reread = Read(path);
        Assert.Equal("Renamed", reread.Title);
        Assert.Equal(2019, reread.Year);
        Assert.Equal("2019-05-10", reread.ReleaseDate);
        Assert.Equal(original.Album, reread.Album);
    }

    [Fact]
    public void RiffInfoWouldFill_OnlyWhenTheFileHasWhatTheTrackLacks()
    {
        var dated = WriteWav("e.wav", ("IPRD", "The Album"), ("ICRD", "2019-05-10"));
        var undated = WriteWav("f.wav", ("INAM", "Song"));

        Assert.True(MetadataService.RiffInfoWouldFill(dated, 0, "The Album"));        // read as year 0 before
        Assert.True(MetadataService.RiffInfoWouldFill(dated, 2019, "Unknown Album"));
        Assert.False(MetadataService.RiffInfoWouldFill(dated, 2019, "The Album"));
        // A WAV with no date keeps the unchanged fast path on every scan.
        Assert.False(MetadataService.RiffInfoWouldFill(undated, 0, "Unknown Album"));
    }

    [Theory]
    [InlineData("x.wav", 0, "A", true)]
    [InlineData("x.WAV", 2019, "Unknown Album", true)]
    [InlineData("x.wav", 2019, "A", false)]
    [InlineData("x.flac", 0, "A", false)]
    public void Rescan_RereadsOnlyWavsMissingYearOrAlbum(string file, int year, string album, bool expected)
    {
        var t = new Track { FilePath = Path.Combine("C:", "m", file), Year = year, Album = album };
        Assert.Equal(expected, LibraryService.NeedsWavInfoReread(t));
    }
}
