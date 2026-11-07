using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// Discord "Trouble Playing Long MP3 File" (10-26): a VBR MP3 with no Xing/VBRI header states
// no length, so TagLib guessed from the first frame (65 minutes read as 14:37) and LibVLC from
// the first few (62:27, with every seek clamped short of the real end). Mp3FrameScan counts
// the frames of such a file and leaves every other MP3 to the existing readers.
public class Mp3FrameScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "mp3scan-" + Guid.NewGuid().ToString("N"));

    public Mp3FrameScanTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // MPEG-1 Layer III, 44.1 kHz, stereo frames: index 9 = 128 kbps, 11 = 192 kbps.
    private static void WriteFrame(Stream s, int bitrateIndex, string? tag = null)
    {
        var kbps = new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }[bitrateIndex];
        var length = 144000 * kbps / 44100;
        var frame = new byte[length];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = (byte)(bitrateIndex << 4);
        frame[3] = 0x40;
        if (tag != null) Encoding.ASCII.GetBytes(tag).CopyTo(frame, 4 + 32);
        s.Write(frame);
    }

    private static MemoryStream Vbr(int frames, string? firstTag = null, bool id3 = false, bool id3v1 = false, bool junk = false)
    {
        var ms = new MemoryStream();
        if (id3)
        {
            // ID3v2.4 header with a 2000-byte body (syncsafe size).
            ms.Write(new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0x0F, 0x50 });
            ms.Write(new byte[2000]);
        }
        for (var i = 0; i < frames; i++)
        {
            WriteFrame(ms, i % 3 == 0 ? 11 : 9, i == 0 ? firstTag : null);
            if (junk && i == frames / 2) ms.Write(new byte[333]);
        }
        if (id3v1)
        {
            var tag = new byte[128];
            Encoding.ASCII.GetBytes("TAG").CopyTo(tag, 0);
            ms.Write(tag);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void HeaderlessVbr_CountsEveryFrame()
    {
        Assert.True(Mp3FrameScan.TryMeasureHeaderlessVbr(Vbr(3000), out var length, out var kbps));
        Assert.Equal(3000 * 1152 / 44100.0, length.TotalSeconds, 3);
        Assert.InRange(kbps, 140, 156); // one 192 to two 128s: ~149
    }

    [Fact]
    public void Id3Tags_JunkMidFile_AreSkipped()
    {
        Assert.True(Mp3FrameScan.TryMeasureHeaderlessVbr(Vbr(3000, id3: true, id3v1: true, junk: true), out var length, out _));
        Assert.Equal(3000 * 1152 / 44100.0, length.TotalSeconds, 3);
    }

    [Fact]
    public void LongerThanOneReadBlock_CountsAcrossBlocks()
    {
        // ~5 MB of frames: the walk refills its 1 MB block several times.
        const int frames = 10_000;
        Assert.True(Mp3FrameScan.TryMeasureHeaderlessVbr(Vbr(frames), out var length, out _));
        Assert.Equal(frames * 1152 / 44100.0, length.TotalSeconds, 2);
    }

    // MPEG-2 Layer III, 22.05 kHz, stereo (the Discord 10-28 file): 576 samples a frame.
    // Index 8 = 64 kbps, 10 = 96 kbps.
    private static void WriteMpeg2Frame(Stream s, int bitrateIndex)
    {
        var kbps = new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 }[bitrateIndex];
        var frame = new byte[72000 * kbps / 22050];
        frame[0] = 0xFF;
        frame[1] = 0xF3;
        frame[2] = (byte)(bitrateIndex << 4);
        frame[3] = 0x40;
        s.Write(frame);
    }

    [Fact]
    public void Mpeg2_22kHz_CountsFramesOf576Samples()
    {
        var ms = new MemoryStream();
        for (var i = 0; i < 3000; i++) WriteMpeg2Frame(ms, i % 3 == 0 ? 10 : 8);
        ms.Position = 0;
        Assert.True(Mp3FrameScan.TryMeasureHeaderlessVbr(ms, out var length, out _));
        Assert.Equal(3000 * 576 / 22050.0, length.TotalSeconds, 3);
    }

    [Fact]
    public void AFormatChangeMidFile_CountsBothParts()
    {
        // Two recordings joined: 22.05 kHz MPEG-2, then 44.1 kHz MPEG-1. Counting only frames
        // shaped like the first stopped at the join and the player clamped every seek there
        // (harness 10-28: a seek to 42:58 went to 31:12 on a joined 61-minute file).
        var ms = new MemoryStream();
        for (var i = 0; i < 1500; i++) WriteMpeg2Frame(ms, i % 3 == 0 ? 10 : 8);
        for (var i = 0; i < 1500; i++) WriteFrame(ms, i % 3 == 0 ? 11 : 9);
        ms.Position = 0;
        Assert.True(Mp3FrameScan.TryMeasureHeaderlessVbr(ms, out var length, out _));
        Assert.Equal(1500 * 576 / 22050.0 + 1500 * 1152 / 44100.0, length.TotalSeconds, 3);
    }

    [Fact]
    public void TwoConstantBitrateParts_OfDifferentRates_AreCounted()
    {
        // Each part is CBR, but at its own bitrate: a length guessed from the first frame's
        // 64 kbps read the 128 kbps part as twice its length. The probes only looked for
        // frames shaped like the first one, found none in the second part and called it CBR.
        var ms = new MemoryStream();
        for (var i = 0; i < 1500; i++) WriteMpeg2Frame(ms, 8);
        for (var i = 0; i < 1500; i++) WriteFrame(ms, 9);
        ms.Position = 0;
        Assert.True(Mp3FrameScan.TryMeasureHeaderlessVbr(ms, out var length, out _));
        Assert.Equal(1500 * 576 / 22050.0 + 1500 * 1152 / 44100.0, length.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("Xing")]
    [InlineData("Info")]
    public void XingOrInfoHeader_IsLeftToTheExistingReaders(string tag)
    {
        // Written at the stereo MPEG-1 side-info offset (4 + 32) — where the header lives.
        Assert.False(Mp3FrameScan.TryMeasureHeaderlessVbr(Vbr(3000, firstTag: tag), out _, out _));
    }

    [Fact]
    public void Cbr_IsLeftToTheExistingReaders()
    {
        var ms = new MemoryStream();
        for (var i = 0; i < 3000; i++) WriteFrame(ms, 9);
        ms.Position = 0;
        Assert.False(Mp3FrameScan.TryMeasureHeaderlessVbr(ms, out _, out _));
    }

    // ffmpeg's mp3 muxer writes its CBR "Info" header in a frame of the smallest bitrate that
    // holds it (64 kbps) ahead of 128 kbps audio. TagLib skips "Info" and guessed the length from
    // that frame: 20 s files read as 40 s at 64 kbps (found live 11-07).
    private static MemoryStream InfoHeaded(int audioFrames, string tag = "Info", bool statesBytes = true)
    {
        var ms = new MemoryStream();
        var header = new byte[144000 * 64 / 44100];
        header[0] = 0xFF; header[1] = 0xFB; header[2] = 5 << 4; header[3] = 0x40;
        Encoding.ASCII.GetBytes(tag).CopyTo(header, 4 + 32);
        var flags = statesBytes ? 3 : 1;
        var audioBytes = header.Length + audioFrames * (144000 * 128 / 44100);
        void Be(int at, int v) { header[at] = (byte)(v >> 24); header[at + 1] = (byte)(v >> 16); header[at + 2] = (byte)(v >> 8); header[at + 3] = (byte)v; }
        Be(4 + 32 + 4, flags);
        Be(4 + 32 + 8, audioFrames + 1);
        if (statesBytes) Be(4 + 32 + 12, audioBytes);
        ms.Write(header);
        for (var i = 0; i < audioFrames; i++) WriteFrame(ms, 9);
        ms.Position = 0;
        return ms;
    }

    [Theory]
    [InlineData("Info", true)]
    [InlineData("Xing", true)]
    [InlineData("Info", false)]
    public void StatedLength_ComesFromTheHeader_NotTheHeaderFramesBitrate(string tag, bool statesBytes)
    {
        Assert.True(Mp3FrameScan.TryReadStatedLength(InfoHeaded(766, tag, statesBytes), out var duration, out var kbps));
        Assert.InRange(duration.TotalSeconds, 20.0, 20.05); // 767 frames x 1152 / 44100
        Assert.InRange(kbps, 127, 129);
    }

    [Fact]
    public void StatedLength_IsFalse_WithoutAHeader()
    {
        var ms = new MemoryStream();
        for (var i = 0; i < 300; i++) WriteFrame(ms, 9);
        ms.Position = 0;
        Assert.False(Mp3FrameScan.TryReadStatedLength(ms, out _, out _));
        Assert.False(Mp3FrameScan.TryReadStatedLength(Vbr(300), out _, out _));
    }

    [Fact]
    public void Import_FfmpegCbrMp3_GetsItsRealLengthAndBitrate()
    {
        var ffmpeg = FindFfmpeg();
        Assert.SkipWhen(ffmpeg == null, "no ffmpeg on PATH");
        // ffmpeg defaults (no -b:a): the Info frame goes out at 64 kbps; an explicit 128k puts it at 128.
        var cbr = Encode(ffmpeg!, "cbr-info.mp3");

        var track = new MetadataService().ReadTrackMetadata(cbr, out _);

        Assert.NotNull(track);
        Assert.InRange(track!.Duration.TotalSeconds, 39.9, 40.1);
        Assert.InRange(track.Bitrate, 127, 130);
    }

    [Fact]
    public void NotAnMp3_IsFalse()
    {
        Assert.False(Mp3FrameScan.TryMeasureHeaderlessVbr(new MemoryStream(new byte[100_000]), out _, out _));
        Assert.False(Mp3FrameScan.TryMeasureHeaderlessVbr(new MemoryStream(), out _, out _));
    }

    private static string? FindFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var c = Path.Combine(dir.Trim(), exe); if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    private string Encode(string ffmpeg, string name, params string[] codecArgs)
    {
        var path = Path.Combine(_root, name);
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi",
                     "-i", "aevalsrc=0.5*sin(2*PI*(200*t+t*t*20)):s=44100:d=40", "-ac", "2", "-c:a", "libmp3lame" })
            psi.ArgumentList.Add(a);
        foreach (var a in codecArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        Assert.True(File.Exists(path), "ffmpeg made no test file");
        return path;
    }

    [Fact]
    public void Import_HeaderlessVbrMp3_GetsItsRealLength()
    {
        var ffmpeg = FindFfmpeg();
        Assert.SkipWhen(ffmpeg == null, "no ffmpeg on PATH");
        var noXing = Encode(ffmpeg!, "vbr-noxing.mp3", "-q:a", "2", "-write_xing", "0");
        var withXing = Encode(ffmpeg!, "vbr-xing.mp3", "-q:a", "2");

        var counted = new MetadataService().ReadTrackMetadata(noXing, out _);
        var headed = new MetadataService().ReadTrackMetadata(withXing, out _);

        Assert.NotNull(counted);
        Assert.NotNull(headed);
        // 40 s of audio; LAME adds under a frame of delay + padding.
        Assert.InRange(counted!.Duration.TotalSeconds, 40.0, 40.1);
        Assert.InRange(headed!.Duration.TotalSeconds, 39.9, 40.1);
    }

    // Discord 11-05 (Tangent, 1.6.7 "better, still present"): the damage is found before
    // playback reaches it, so the player can switch decoders ahead of it instead of at the wedge.
    [Fact]
    public void FindDamage_ReportsTheJunkBetweenFrames_InMediaTime()
    {
        var damage = Mp3FrameScan.FindDamage(Vbr(3000, id3: true, id3v1: true, junk: true), minMinutes: 0);
        var run = Assert.Single(damage);
        var frameMs = 1152 * 1000.0 / 44100;
        // The junk follows frame 1500 (0-based): the cut is where that frame starts, the
        // resume where the next frame starts; the junk itself carries no time.
        Assert.InRange(run.CutMs, (long)(1500 * frameMs) - 1, (long)(1500 * frameMs) + 1);
        Assert.InRange(run.ResumeMs, (long)(1501 * frameMs), (long)(1501 * frameMs) + 2);
    }

    [Fact]
    public void FindDamage_IgnoresCleanFiles_TagsAndSongLengthFiles()
    {
        Assert.Empty(Mp3FrameScan.FindDamage(Vbr(3000, id3: true, id3v1: true), minMinutes: 0));
        // A song-length file is not walked at all (78 s here).
        Assert.Empty(Mp3FrameScan.FindDamage(Vbr(3000, junk: true)));

        // Two files joined with the second one's ID3v2 tag in between: not damage.
        var joined = new MemoryStream();
        Vbr(500).CopyTo(joined);
        joined.Write(new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0x01, 0x00 });
        joined.Write(new byte[128]);
        Vbr(500).CopyTo(joined);
        joined.Position = 0;
        Assert.Empty(Mp3FrameScan.FindDamage(joined, minMinutes: 0));
    }

    // Tangent's file at 31:14, rebuilt on an encoded file (see MakeDamagedMp3): junk, two frames
    // whose audio data is noise, more junk: one run, from the frame before the first junk to
    // the frame after the second.
    [Fact]
    public void FindDamage_OnAnEncodedFile_ReportsTangentsKindOfDamage()
    {
        var ffmpeg = FindFfmpeg();
        Assert.SkipWhen(ffmpeg == null, "no ffmpeg on PATH");
        var path = Mp3DamageCrunchHarnessTests.MakeDamagedMp3(ffmpeg!, _root, seconds: 60, damageAtS: 30, out var n);
        using var stream = File.OpenRead(path);
        var run = Assert.Single(Mp3FrameScan.FindDamage(stream, minMinutes: 0));
        var frameMs = 576 * 1000.0 / 22050;
        Assert.InRange(run.CutMs, (long)(n * frameMs) - 1, (long)(n * frameMs) + 1);
        Assert.InRange(run.ResumeMs, (long)((n + 3) * frameMs), (long)((n + 3) * frameMs) + 2);
        // The clean encode has none.
        using var clean = File.OpenRead(Path.Combine(_root, "clean.mp3"));
        Assert.Empty(Mp3FrameScan.FindDamage(clean, minMinutes: 0));
    }

    [Fact]
    public void FindDamage_MergesJunkScatteredBetweenFrames_IntoOneRun()
    {
        var ms = new MemoryStream();
        for (var i = 0; i < 2000; i++)
        {
            WriteFrame(ms, i % 3 == 0 ? 11 : 9);
            if (i is >= 1000 and < 1040 && i % 4 == 0) ms.Write(new byte[300]); // ~1 s of scattered junk
        }
        ms.Position = 0;
        var run = Assert.Single(Mp3FrameScan.FindDamage(ms, minMinutes: 0));
        var frameMs = 1152 * 1000.0 / 44100;
        Assert.InRange(run.CutMs, (long)(1000 * frameMs) - 1, (long)(1000 * frameMs) + 1);
        Assert.InRange(run.ResumeMs, (long)(1037 * frameMs), (long)(1037 * frameMs) + 2);
    }
}
