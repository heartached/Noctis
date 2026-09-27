using System.Text;
using Noctis.Models;
using Noctis.Services.AudioAnalysis;
using Xunit;
using static Noctis.Tests.ReplayGainWriteTests;

namespace Noctis.Tests.AudioAnalysis;

/// <summary>
/// The BPM/key backfill wrote its tags with an in-place file.Save(); on Linux/macOS that
/// corrupts the player's open read of a playing/prepared track (the audio stops early
/// while the clock keeps ticking). Same checks as <see cref="ReplayGainWriteTests"/>.
/// </summary>
public class AudioAnalysisTagWriteTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public AudioAnalysisTagWriteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Write_replaces_the_file_instead_of_rewriting_it_in_place()
    {
        var path = CreateFlac(_dir);
        var original = File.ReadAllBytes(path);
        var track = new Track { FilePath = path, Bpm = 128, MusicalKey = "A minor" };

        if (OperatingSystem.IsWindows())
        {
            // Windows refuses to rename over a file another handle has open, so check
            // the file identity instead: a rename-over gives the path a new file ID.
            var before = WindowsFileId(path);
            Assert.True(AudioAnalysisCoordinator.TryWriteTags(track));
            Assert.NotEqual(before, WindowsFileId(path));
        }
        else
        {
            using var player = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Assert.True(AudioAnalysisCoordinator.TryWriteTags(track));

            var seen = new byte[original.Length + 1];
            var read = 0;
            int n;
            while ((n = player.Read(seen, read, seen.Length - read)) > 0) read += n;
            Assert.Equal(original, seen.AsSpan(0, read).ToArray());
        }

        using (var f = TagLib.File.Create(path))
        {
            Assert.Equal(128u, f.Tag.BeatsPerMinute);
            var xiph = (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, false);
            Assert.Equal("A minor", xiph.GetFirstField("INITIALKEY"));
        }
        Assert.Empty(Directory.GetFiles(_dir, ".noctis-*"));
    }

    [Fact]
    public void Flac_gets_only_a_vorbis_comment()
    {
        var path = CreateFlac(_dir);

        Assert.True(AudioAnalysisCoordinator.TryWriteTags(
            new Track { FilePath = path, Bpm = 96, MusicalKey = "C major" }));

        var types = TagTypesOnDisk(path);
        Assert.Equal(TagLib.TagTypes.Xiph, types & (TagLib.TagTypes.Xiph | TagLib.TagTypes.Id3v2
            | TagLib.TagTypes.Ape | TagLib.TagTypes.Id3v1));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("fLaC", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Mp3_with_only_id3v2_gets_no_ape_or_id3v1()
    {
        var path = CreateMp3WithId3v2Only(_dir);

        Assert.True(AudioAnalysisCoordinator.TryWriteTags(
            new Track { FilePath = path, Bpm = 140, MusicalKey = "F# minor" }));

        var types = TagTypesOnDisk(path);
        Assert.True(types.HasFlag(TagLib.TagTypes.Id3v2), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Ape), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Id3v1), types.ToString());
        var bytes = File.ReadAllBytes(path);
        Assert.False(Contains(bytes, "APETAGEX"));
        Assert.NotEqual("TAG", Encoding.ASCII.GetString(bytes, bytes.Length - 128, 3));

        using var f = TagLib.File.Create(path);
        Assert.Equal(140u, f.Tag.BeatsPerMinute);
        Assert.Equal("Quevedo", f.Tag.Title);
    }
}
