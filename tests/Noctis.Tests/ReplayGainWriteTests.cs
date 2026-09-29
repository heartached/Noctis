using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Spark, Linux): "songs don't end properly after putting them through
/// ReplayGain". The scanner saved its tags in place — on Linux that corrupts the
/// player's open read of the track (audio stops early while the clock keeps ticking) —
/// and WriteCustomField created every tag type, so a FLAC gained a leading ID3v2 and a
/// trailing APEv2 (ffmpeg "invalid sync code", VLC loses the last fraction of a second)
/// and an MP3 gained APEv2.
/// </summary>
public class ReplayGainWriteTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public ReplayGainWriteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // "fLaC" + a lone STREAMINFO block (1 s, 44.1 kHz, stereo, 16-bit) + filler audio:
    // enough for TagLib# to open it as a FLAC with no Vorbis comment block yet.
    internal static string CreateFlac(string dir)
    {
        var path = Path.Combine(dir, "rg.flac");
        var bytes = new List<byte>();
        bytes.AddRange("fLaC"u8.ToArray());
        bytes.AddRange(new byte[] { 0x80, 0x00, 0x00, 34 }); // last block, STREAMINFO, 34 bytes
        bytes.AddRange(new byte[] { 0x10, 0x00, 0x10, 0x00 }); // min/max block size 4096
        bytes.AddRange(new byte[6]);                           // min/max frame size unknown
        ulong packed = (44100UL << 44) | (1UL << 41) | (15UL << 36) | 44100UL;
        for (var shift = 56; shift >= 0; shift -= 8) bytes.Add((byte)(packed >> shift));
        bytes.AddRange(new byte[16]);                          // MD5
        var audio = new byte[4096];
        audio[0] = 0xFF;
        audio[1] = 0xF8;
        bytes.AddRange(audio);
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    // Silent MPEG-1 Layer III frames behind an ID3v2 tag, and nothing else — no ID3v1
    // trailer, no APEv2 (written by hand: a TagLib# save would add ID3v1 itself).
    internal static string CreateMp3WithId3v2Only(string dir)
    {
        var path = Path.Combine(dir, "rg.mp3");
        var id3 = new TagLib.Id3v2.Tag { Title = "Quevedo" };
        var bytes = new List<byte>(id3.Render().Data);
        const int frameLength = 417; // 144 * 128000 / 44100, no padding
        var frames = new byte[frameLength * 20];
        for (var i = 0; i < frames.Length; i += frameLength)
        {
            frames[i] = 0xFF;
            frames[i + 1] = 0xFB;
            frames[i + 2] = 0x90;
        }
        bytes.AddRange(frames);
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    internal static TagLib.TagTypes TagTypesOnDisk(string path)
    {
        using var f = TagLib.File.Create(path);
        return f.TagTypesOnDisk;
    }

    internal static bool Contains(byte[] haystack, string needle)
        => Encoding.Latin1.GetString(haystack).Contains(needle, StringComparison.Ordinal);

    [Fact]
    public void Flac_gets_only_a_vorbis_comment()
    {
        var path = CreateFlac(_dir);

        var (ok, error) = ReplayGainScannerService.WriteReplayGainTags(path, -7.84, 0.891251, -6.1, 0.977237);
        Assert.True(ok, error);

        var types = TagTypesOnDisk(path);
        Assert.True(types.HasFlag(TagLib.TagTypes.Xiph), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Id3v2), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Ape), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Id3v1), types.ToString());

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("fLaC", Encoding.ASCII.GetString(bytes, 0, 4)); // no ID3v2 header in front
        Assert.False(Contains(bytes, "APETAGEX"));

        var fields = AdvancedTagIO.ReadAll(path);
        Assert.Equal("-7.84 dB", fields.ReplayGainTrackGain);
        Assert.Equal("0.891251", fields.ReplayGainTrackPeak);
        Assert.Equal("-6.10 dB", fields.ReplayGainAlbumGain);
        Assert.Equal("0.977237", fields.ReplayGainAlbumPeak);
    }

    [Fact]
    public void Mp3_with_only_id3v2_gets_no_ape_or_id3v1()
    {
        var path = CreateMp3WithId3v2Only(_dir);

        var (ok, error) = ReplayGainScannerService.WriteReplayGainTags(path, -3.5, 1.02, null, null);
        Assert.True(ok, error);

        var types = TagTypesOnDisk(path);
        Assert.True(types.HasFlag(TagLib.TagTypes.Id3v2), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Ape), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Id3v1), types.ToString());

        var bytes = File.ReadAllBytes(path);
        Assert.False(Contains(bytes, "APETAGEX"));
        Assert.NotEqual("TAG", Encoding.ASCII.GetString(bytes, bytes.Length - 128, 3));

        var fields = AdvancedTagIO.ReadAll(path);
        Assert.Equal("-3.50 dB", fields.ReplayGainTrackGain);
        Assert.Equal("1.020000", fields.ReplayGainTrackPeak);
        Assert.Equal(string.Empty, fields.ReplayGainAlbumGain);

        var (track, album) = VlcAudioPlayer.ReadReplayGainTags(path);
        Assert.Equal(-3.5, track);
        Assert.Null(album);
    }

    [Fact]
    public void Mp3_existing_ape_tag_is_still_updated()
    {
        // Foreign tag types are no longer created, but one the file already carries
        // (mp3gain writes APEv2) must not be left holding a stale gain.
        var path = CreateMp3WithId3v2Only(_dir);
        using (var f = TagLib.File.Create(path))
        {
            ((TagLib.Ape.Tag)f.GetTag(TagLib.TagTypes.Ape, true)).SetValue("REPLAYGAIN_TRACK_GAIN", "+9.00 dB");
            f.Save();
        }

        var (ok, error) = ReplayGainScannerService.WriteReplayGainTags(path, -2.25, 0.5, null, null);
        Assert.True(ok, error);

        using var reread = TagLib.File.Create(path);
        var ape = (TagLib.Ape.Tag)reread.GetTag(TagLib.TagTypes.Ape, false);
        Assert.Equal("-2.25 dB", ape.GetItem("REPLAYGAIN_TRACK_GAIN").ToString());
    }

    private static TagLib.IPicture FakeCover(int size)
    {
        var data = new byte[size];
        new Random(7).NextBytes(data);
        data[0] = 0x89; data[1] = (byte)'P'; data[2] = (byte)'N'; data[3] = (byte)'G';
        return new TagLib.Picture(new TagLib.ByteVector(data))
        {
            Type = TagLib.PictureType.FrontCover,
            MimeType = "image/png",
        };
    }

    [Fact]
    public void Mp3_cover_copied_into_ape_by_old_scans_is_stripped()
    {
        // Spark again, after 1.5.6: scans up to 1.5.5 created an APEv2 tag, and TagLib#
        // filled it with a copy of the existing tags, cover art included. The megabytes of
        // image appended after the audio make ffmpeg/VLC size the song by bitrate x file
        // size (a 3:47 song read as 8:34), so it hangs on a silent tail. Rerunning the scan
        // on 1.5.6 kept that tag, since existing foreign tags are only updated.
        var path = CreateMp3WithId3v2Only(_dir);
        var cover = FakeCover(200_000);
        using (var f = TagLib.File.Create(path))
        {
            f.GetTag(TagLib.TagTypes.Id3v2, true).Pictures = new[] { cover };
            var ape = (TagLib.Ape.Tag)f.GetTag(TagLib.TagTypes.Ape, true);
            ape.Pictures = new[] { cover };
            ape.SetValue("REPLAYGAIN_TRACK_GAIN", "+9.00 dB");
            f.Save();
        }
        var damagedSize = new FileInfo(path).Length;

        var (ok, error) = ReplayGainScannerService.WriteReplayGainTags(path, -2.25, 0.5, null, null);
        Assert.True(ok, error);

        Assert.True(new FileInfo(path).Length < damagedSize - 150_000,
            $"{damagedSize} -> {new FileInfo(path).Length}");
        using var reread = TagLib.File.Create(path);
        var ape2 = (TagLib.Ape.Tag)reread.GetTag(TagLib.TagTypes.Ape, false);
        Assert.Empty(ape2.Pictures);
        Assert.Equal("-2.25 dB", ape2.GetItem("REPLAYGAIN_TRACK_GAIN").ToString());
        var id3Pictures = reread.GetTag(TagLib.TagTypes.Id3v2, false).Pictures;
        Assert.Single(id3Pictures);
        Assert.Equal(cover.Data.Count, id3Pictures[0].Data.Count);
    }

    [Fact]
    public void Mp3_cover_held_only_in_ape_moves_to_id3v2()
    {
        var path = CreateMp3WithId3v2Only(_dir);
        var cover = FakeCover(50_000);
        using (var f = TagLib.File.Create(path))
        {
            ((TagLib.Ape.Tag)f.GetTag(TagLib.TagTypes.Ape, true)).Pictures = new[] { cover };
            f.Save();
        }

        var (ok, error) = ReplayGainScannerService.WriteReplayGainTags(path, -2.25, 0.5, null, null);
        Assert.True(ok, error);

        using var reread = TagLib.File.Create(path);
        Assert.Empty(reread.GetTag(TagLib.TagTypes.Ape, false).Pictures);
        var id3Pictures = reread.GetTag(TagLib.TagTypes.Id3v2, false).Pictures;
        Assert.Single(id3Pictures);
        Assert.Equal(cover.Data.Count, id3Pictures[0].Data.Count);
    }

    [Fact]
    public void Write_replaces_the_file_instead_of_rewriting_it_in_place()
    {
        // The player keeps the track open while it is scanned. An atomic temp-copy +
        // rename swaps a new file in under the name and leaves that open handle on the
        // untouched original; an in-place save rewrites the bytes under it.
        var path = CreateFlac(_dir);
        var original = File.ReadAllBytes(path);

        if (OperatingSystem.IsWindows())
        {
            // Windows refuses to rename over a file another handle has open, so check
            // the file identity instead: a rename-over gives the path a new file ID.
            var before = WindowsFileId(path);
            var (ok, error) = ReplayGainScannerService.WriteReplayGainTags(path, -1.0, 0.25, null, null);
            Assert.True(ok, error);
            Assert.NotEqual(before, WindowsFileId(path));
        }
        else
        {
            using var player = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var (ok, error) = ReplayGainScannerService.WriteReplayGainTags(path, -1.0, 0.25, null, null);
            Assert.True(ok, error);

            var seen = new byte[original.Length + 1];
            var read = 0;
            int n;
            while ((n = player.Read(seen, read, seen.Length - read)) > 0) read += n;
            Assert.Equal(original, seen.AsSpan(0, read).ToArray());
        }

        Assert.Equal("-1.00 dB", AdvancedTagIO.ReadAll(path).ReplayGainTrackGain);
        Assert.Empty(Directory.GetFiles(_dir, ".noctis-*"));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    internal static ulong WindowsFileId(string path)
    {
        using var handle = File.OpenHandle(path);
        Assert.True(GetFileInformationByHandle(handle, out var info));
        return ((ulong)info.IndexHigh << 32) | info.IndexLow;
    }

    [Fact]
    public void Advisory_write_on_flac_does_not_add_id3v2_or_ape()
    {
        // Same helper, other caller: the album editor's explicit toggle.
        var path = CreateFlac(_dir);
        using (var f = TagLib.File.Create(path))
        {
            AdvancedTagIO.WriteAdvisory(f, 1);
            f.Save();
        }

        var types = TagTypesOnDisk(path);
        Assert.True(types.HasFlag(TagLib.TagTypes.Xiph), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Id3v2), types.ToString());
        Assert.False(types.HasFlag(TagLib.TagTypes.Ape), types.ToString());
    }
}
