using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using LibVLCSharp.Shared;
using Noctis.Services;
using Noctis.Services.AudioAnalysis;
using Noctis.Services.Dsd;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// DSD support: the PCM → DSF encoder (Convert dialog), the DSD → PCM decoder the player
/// feeds LibVLC with (LibVLC 3 has no DSD decoder), DSF/DSDIFF probing, and the WAV
/// stream <see cref="DsdPcmInput"/> serves.
/// </summary>
public class DsdTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-dsd-" + Guid.NewGuid().ToString("N"));

    public DsdTests(ITestOutputHelper o)
    {
        _o = o;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Stereo float PCM at 352.8 kHz: left a 997 Hz sine, right a 440 Hz one.</summary>
    private static MemoryStream SinePcm(double seconds, double amplitude)
    {
        var frames = (int)(DsdEncoder.PcmRate * seconds);
        var bytes = new byte[frames * 8];
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / DsdEncoder.PcmRate;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8), (float)(amplitude * Math.Sin(2 * Math.PI * 997 * t)));
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8 + 4), (float)(amplitude * Math.Sin(2 * Math.PI * 440 * t)));
        }
        return new MemoryStream(bytes);
    }

    private string EncodeSine(string name, int dsdRate, double seconds, double amplitude = 0.5)
    {
        var path = Path.Combine(_dir, name);
        using var pcm = SinePcm(seconds, amplitude);
        using var output = File.Create(path);
        var resets = DsdEncoder.Encode(pcm, 2, dsdRate, output);
        Assert.Equal(0, resets);
        return path;
    }

    private static float[] DecodeAll(string path, out DsdPcmDecoder decoderInfo)
    {
        var info = DsdStreamInfo.Probe(path)!;
        var decoder = new DsdPcmDecoder(info, File.OpenRead(path));
        decoderInfo = decoder;
        var pcm = new float[decoder.TotalFrames * info.Channels];
        var got = 0L;
        while (got < decoder.TotalFrames)
            got += decoder.Decode(got, 4096, pcm.AsSpan((int)(got * info.Channels)));
        decoder.Dispose();
        return pcm;
    }

    /// <summary>Amplitude of <paramref name="freq"/> and the SNR over 20 Hz–20 kHz (everything
    /// in band but the tone counts as noise), from a Blackman-Harris windowed FFT.</summary>
    private static (double amplitude, double snrDb) Analyse(float[] interleaved, int channels, int channel, int rate, double freq)
    {
        const int n = 65536;
        var frames = interleaved.Length / channels;
        var start = (frames - n) / 2;
        var re = new double[n];
        var im = new double[n];
        for (var i = 0; i < n; i++)
        {
            var x = 2 * Math.PI * i / (n - 1);
            var w = 0.35875 - 0.48829 * Math.Cos(x) + 0.14128 * Math.Cos(2 * x) - 0.01168 * Math.Cos(3 * x);
            re[i] = interleaved[(start + i) * channels + channel] * w;
        }
        Fft.Forward(re, im);
        double sig = 0, noise = 0;
        for (var k = 1; k < n / 2; k++)
        {
            var f = (double)k * rate / n;
            var p = re[k] * re[k] + im[k] * im[k];
            if (Math.Abs(f - freq) < 30) sig += p;
            else if (f >= 20 && f <= 20000) noise += p;
        }
        // Amplitude by projecting onto sin/cos over the same stretch (unwindowed).
        double s = 0, c = 0;
        for (var i = 0; i < n; i++)
        {
            var x = interleaved[(start + i) * channels + channel];
            var ph = 2 * Math.PI * freq * i / rate;
            s += x * Math.Sin(ph);
            c += x * Math.Cos(ph);
        }
        var amplitude = 2 * Math.Sqrt(s * s + c * c) / n;
        return (amplitude, 10 * Math.Log10(sig / noise));
    }

    [Theory]
    [InlineData(2822400, 88200)]
    [InlineData(5644800, 88200)]
    public void Encode_ThenDecode_GivesTheSourceBackClean(int dsdRate, int outRate)
    {
        var path = EncodeSine($"sine{dsdRate}.dsf", dsdRate, 1.0);
        var info = DsdStreamInfo.Probe(path)!;
        Assert.Equal(dsdRate, info.SampleRate);
        Assert.Equal(2, info.Channels);
        Assert.True(info.LsbFirst);
        Assert.Equal(DsdEncoder.PcmRate * (long)(dsdRate / DsdEncoder.PcmRate) / 8, info.BytesPerChannel);

        var pcm = DecodeAll(path, out var decoder);
        Assert.Equal(outRate, decoder.OutputRate);
        var (ampL, snrL) = Analyse(pcm, 2, 0, outRate, 997);
        var (ampR, snrR) = Analyse(pcm, 2, 1, outRate, 440);
        _o.WriteLine($"DSD{dsdRate / 44100}: L 997 Hz amp={ampL:0.0000} SNR={snrL:0.0} dB; R 440 Hz amp={ampR:0.0000} SNR={snrR:0.0} dB");
        // Unity round trip: the encoder writes full scale as 50 % modulation, the decoder adds +6 dB.
        Assert.InRange(ampL, 0.49, 0.51);
        Assert.InRange(ampR, 0.49, 0.51);
        // −6 dBFS tone vs. everything else in 20 Hz–20 kHz (window leakage included).
        Assert.True(snrL > 85 && snrR > 85, $"in-band SNR {snrL:0.0} / {snrR:0.0} dB");
    }

    [Fact]
    public void Decoder_AnyStartFrame_MatchesOnePass()
    {
        var path = EncodeSine("seek.dsf", 2822400, 0.3);
        var all = DecodeAll(path, out var d);
        var info = DsdStreamInfo.Probe(path)!;
        using var decoder = new DsdPcmDecoder(info, File.OpenRead(path));
        foreach (var start in new long[] { 0, 1, 1023, 5000, d.TotalFrames - 100 })
        {
            var part = new float[200 * 2];
            var got = decoder.Decode(start, 200, part);
            Assert.Equal((int)Math.Min(200, d.TotalFrames - start), got);
            for (var i = 0; i < got * 2; i++)
                Assert.Equal(all[start * 2 + i], part[i]);
        }
        Assert.Equal(0, decoder.Decode(d.TotalFrames, 10, new float[20]));
    }

    /// <summary>The same bits as DSDIFF (byte-interleaved, MSB first) decode to the same PCM.</summary>
    [Fact]
    public void Dff_DecodesLikeTheSameDsf()
    {
        var dsf = EncodeSine("same.dsf", 2822400, 0.2);
        var info = DsdStreamInfo.Probe(dsf)!;
        var raw = File.ReadAllBytes(dsf);
        var perChannel = (int)info.BytesPerChannel;
        var inter = new byte[perChannel * 2];
        for (var i = 0; i < perChannel; i++)
            for (var c = 0; c < 2; c++)
            {
                var src = info.DataOffset + (i / 4096) * 4096 * 2 + c * 4096 + i % 4096;
                inter[i * 2 + c] = ReverseBits(raw[src]);
            }
        var dff = Path.Combine(_dir, "same.dff");
        File.WriteAllBytes(dff, BuildDff(2822400, new[] { "SRGT", "SLFT" }, SwapPairs(inter)));

        var dffInfo = DsdStreamInfo.Probe(dff)!;
        Assert.False(dffInfo.LsbFirst);
        Assert.Equal(1, dffInfo.BlockSize);
        Assert.Equal(new[] { 1, 0 }, dffInfo.ChannelOrder); // right stored first: put back in L/R order
        var a = DecodeAll(dsf, out _);
        var b = DecodeAll(dff, out _);
        Assert.Equal(a, b);

        // The library scan reads the same file (DsdiffReader) with its duration.
        using var s = File.OpenRead(dff);
        var meta = DsdiffReader.TryRead(s)!;
        Assert.Equal(2822400, meta.SampleRate);
        Assert.InRange(meta.Duration.TotalSeconds, 0.19, 0.21);
    }

    private static byte[] SwapPairs(byte[] inter)
    {
        var o = new byte[inter.Length];
        for (var i = 0; i < inter.Length; i += 2) { o[i] = inter[i + 1]; o[i + 1] = inter[i]; }
        return o;
    }

    private static byte ReverseBits(byte b)
    {
        var r = 0;
        for (var i = 0; i < 8; i++) if ((b & (1 << i)) != 0) r |= 0x80 >> i;
        return (byte)r;
    }

    private static byte[] BuildDff(int rate, string[] ids, byte[] data, byte[]? id3 = null)
    {
        using var ms = new MemoryStream();
        void Id(string s) => ms.Write(System.Text.Encoding.ASCII.GetBytes(s));
        void U64(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); ms.Write(b); }
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); ms.Write(b); }
        Id("FRM8"); U64(0); Id("DSD ");
        Id("FVER"); U64(4); U32(0x01050000);
        var chnl = 2 + 4 * ids.Length;
        var prop = 4 + (12 + 4) + (12 + chnl) + (12 + 16);
        Id("PROP"); U64(prop); Id("SND ");
        Id("FS  "); U64(4); U32((uint)rate);
        Id("CHNL"); U64(chnl); U16((ushort)ids.Length); foreach (var i in ids) Id(i);
        Id("CMPR"); U64(16); Id("DSD "); ms.WriteByte(11); Id("uncompresse"); // 4+1+11 = 16
        Id("DSD "); U64(data.Length); ms.Write(data);
        if (id3 != null)
        {
            if (data.Length % 2 == 1) ms.WriteByte(0);
            Id("ID3 "); U64(id3.Length); ms.Write(id3);
            if (id3.Length % 2 == 1) ms.WriteByte(0);
        }
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(4), bytes.Length - 12);
        return bytes;
    }

    /// <summary>
    /// DSDIFF has no tag chunk of its own beyond DIIN, so taggers (foobar2000, JRiver, dBpoweramp)
    /// append an ID3v2 tag as a top-level "ID3 " chunk. The scan reads it: tags and the cover,
    /// not the filename/folder fallback (live test 11-04).
    /// </summary>
    [Fact]
    public void Dff_Id3Chunk_GivesTagsAndCover()
    {
        var id3 = new TagLib.Id3v2.Tag { Title = "Tone", Performers = new[] { "Noctis Test" }, AlbumArtists = new[] { "Noctis Test" },
            Album = "DSD Test", Track = 3, Year = 2026, Genres = new[] { "Test" } };
        id3.Pictures = new TagLib.IPicture[]
        {
            new TagLib.Picture(new TagLib.ByteVector(new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9, 9, 9 }))
                { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" },
        };
        var dff = Path.Combine(_dir, "tagged.dff");
        File.WriteAllBytes(dff, BuildDff(2822400, new[] { "SLFT", "SRGT" }, new byte[4096 * 2], id3.Render().Data));

        var track = new MetadataService().ReadTrackMetadata(dff, out var art)!;
        Assert.Equal("Tone", track.Title);
        Assert.Equal("Noctis Test", track.Artist);
        Assert.Equal("Noctis Test", track.AlbumArtist);
        Assert.Equal("DSD Test", track.Album);
        Assert.Equal(3, track.TrackNumber);
        Assert.Equal(2026, track.Year);
        Assert.Equal("Test", track.Genre);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9, 9, 9 }, art);
        Assert.Equal(art, new MetadataService().ExtractEmbeddedArt(dff));
        // The sound data is still found after the extra chunk.
        Assert.Equal(4096L, DsdStreamInfo.Probe(dff)!.BytesPerChannel);
    }

    /// <summary>The live library's DSF row (codec "DSF Audio" from TagLib) is Hi-Res Lossless,
    /// alone and on an album it shares with a 16/44.1 FLAC.</summary>
    [Fact]
    public void LiveDsfRow_IsHiResLossless_OnItsOwnAndOnTheAlbum()
    {
        var dsf = new Noctis.Models.Track { FilePath = @"A:\m\DSD Test Tone DSD64.dsf", Codec = "DSF Audio", SampleRate = 2822400, BitsPerSample = 1, Bitrate = 5646 };
        var flac = new Noctis.Models.Track { FilePath = @"A:\m\source.flac", Codec = "Flac Audio", SampleRate = 44100, BitsPerSample = 16 };
        Assert.Equal("Hi-Res Lossless", dsf.AudioQualityBadge);
        Assert.Equal("Lossless", flac.AudioQualityBadge);
        Assert.Equal("Hi-Res Lossless", new Noctis.Models.Album { Tracks = new() { flac, dsf } }.AudioQualityBadge);
    }

    [Fact]
    public void Probe_TruncatedDsf_KeepsOnlyWholeBlocks()
    {
        var path = EncodeSine("cut.dsf", 2822400, 0.2);
        var full = DsdStreamInfo.Probe(path)!;
        var bytes = File.ReadAllBytes(path);
        var cut = Path.Combine(_dir, "cut2.dsf");
        File.WriteAllBytes(cut, bytes.AsSpan(0, (int)full.DataOffset + 4096 * 2 * 3 + 100).ToArray());
        var info = DsdStreamInfo.Probe(cut)!;
        Assert.Equal(4096 * 3, info.BytesPerChannel);
        Assert.Null(DsdStreamInfo.Probe(Path.Combine(_dir, "missing.dsf")));
    }

    /// <summary>5.1 DSD: every channel decodes, in WAV order, with the WAV speaker mask. Each
    /// channel holds a constant level (all ones = +1 modulation → +2 after the +6 dB).</summary>
    [Fact]
    public void Multichannel_DecodesEveryChannel_InWavOrder()
    {
        const int per = 4096 * 2;
        // DSF type 7 (FL FR C LFE BL BR), already WAV order: channel c gets level[c].
        var level = new byte[] { 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00 };
        using var ms = new MemoryStream();
        var data = new byte[per * 6];
        for (var blk = 0; blk < per / 4096; blk++)
            for (var c = 0; c < 6; c++)
                data.AsSpan((blk * 6 + c) * 4096, 4096).Fill(level[c]);
        var head = new byte[92];
        System.Text.Encoding.ASCII.GetBytes("DSD ").CopyTo(head, 0);
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(4), 28);
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(12), 92 + data.Length);
        System.Text.Encoding.ASCII.GetBytes("fmt ").CopyTo(head, 28);
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(32), 52);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(40), 1);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(48), 7);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(52), 6);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(56), 2822400);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(60), 1);
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(64), per * 8L);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(72), 4096);
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(head, 80);
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(84), 12 + data.Length);
        var dsf = Path.Combine(_dir, "six.dsf");
        File.WriteAllBytes(dsf, head.Concat(data).ToArray());

        var info = DsdStreamInfo.Probe(dsf)!;
        Assert.Equal(6, info.Channels);
        Assert.Equal(0x3Fu, info.ChannelMask);
        var pcm = DecodeAll(dsf, out var d);
        var mid = d.TotalFrames / 2 * 6;
        for (var c = 0; c < 6; c++)
            Assert.InRange(pcm[mid + c], level[c] == 0xFF ? 1.99f : -2.01f, level[c] == 0xFF ? 2.01f : -1.99f);

        // DSDIFF stores C, LFE, SLFT, SRGT, LS, RS: decoded back into FL FR C LFE BL BR.
        var ids = new[] { "C   ", "LFE ", "SLFT", "SRGT", "LS  ", "RS  " };
        var fileLevel = new byte[] { 0xFF, 0xFF, 0x00, 0x00, 0xFF, 0x00 };
        var inter = new byte[per * 6];
        for (var i = 0; i < per; i++)
            for (var c = 0; c < 6; c++) inter[i * 6 + c] = fileLevel[c];
        var dff = Path.Combine(_dir, "six.dff");
        File.WriteAllBytes(dff, BuildDff(2822400, ids, inter));
        var dffInfo = DsdStreamInfo.Probe(dff)!;
        Assert.Equal(new[] { 2, 3, 0, 1, 4, 5 }, dffInfo.ChannelOrder);
        Assert.Equal(0x3Fu, dffInfo.ChannelMask);
        var dffPcm = DecodeAll(dff, out var dd);
        var dmid = dd.TotalFrames / 2 * 6;
        var expected = new[] { -2f, -2f, 2f, 2f, 2f, -2f }; // FL FR C LFE BL BR
        for (var c = 0; c < 6; c++) Assert.InRange(dffPcm[dmid + c], expected[c] - 0.01f, expected[c] + 0.01f);

        // Served as WAVE_FORMAT_EXTENSIBLE float with that mask.
        var header = DsdPcmInput.BuildHeader(6, 88200, 4, 0x3F, 1000);
        Assert.Equal(0xFFFE, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)));
        Assert.Equal(0x3Fu, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)));
    }

    [Fact]
    public void DecimationFor_KnownRates()
    {
        Assert.Equal(32, DsdPcmDecoder.DecimationFor(2822400));
        Assert.Equal(64, DsdPcmDecoder.DecimationFor(5644800));
        Assert.Equal(128, DsdPcmDecoder.DecimationFor(11289600));
        Assert.Equal(32, DsdPcmDecoder.DecimationFor(3072000)); // DSD64 48k family → 96 kHz
        Assert.Null(DsdPcmDecoder.DecimationFor(44100));
    }

    /// <summary>
    /// "Add to library": TagLib writes the ID3v2 chunk (tags + cover) onto the encoder's DSF,
    /// the scan reads it back, and the sound data is untouched.
    /// </summary>
    [Fact]
    public void EncodedDsf_TakesTagsAndCover_AndScansBackIn()
    {
        var path = EncodeSine("tagged.dsf", 2822400, 0.2);
        var before = DecodeAll(path, out _);
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Title = "Sine";
            f.Tag.Performers = new[] { "Noctis" };
            f.Tag.Album = "Tests";
            f.Tag.Pictures = new TagLib.IPicture[]
            {
                new TagLib.Picture(new TagLib.ByteVector(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 }))
                    { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" },
            };
            f.Save();
        }
        var track = new MetadataService().ReadTrackMetadata(path, out var art);
        Assert.NotNull(track);
        Assert.Equal("Sine", track!.Title);
        Assert.Equal("Noctis", track.Artist);
        Assert.Equal(2822400, track.SampleRate);
        Assert.NotNull(art);
        Assert.InRange(track.Duration.TotalSeconds, 0.19, 0.21);
        Assert.Equal(before, DecodeAll(path, out _));
    }

    /// <summary>The tag editor's write: .dsf takes it (ID3v2 chunk); .dff can't (no TagLib
    /// DSDIFF writer) and reports failure instead of throwing, so the editor can say why.</summary>
    [Fact]
    public void TagEditorWrite_DsfSaves_DffFailsCleanly()
    {
        var dsf = EncodeSine("edit.dsf", 2822400, 0.1);
        var meta = new MetadataService();
        var track = meta.ReadTrackMetadata(dsf)!;
        track.Title = "Edited";
        Assert.True(meta.WriteTrackMetadata(track));
        Assert.Equal("Edited", meta.ReadTrackMetadata(dsf)!.Title);

        var info = DsdStreamInfo.Probe(dsf)!;
        var dff = Path.Combine(_dir, "edit.dff");
        File.WriteAllBytes(dff, BuildDff(2822400, new[] { "SLFT", "SRGT" }, new byte[(int)info.BytesPerChannel * 2]));
        var dffTrack = meta.ReadTrackMetadata(dff)!;
        Assert.Equal("DSD", dffTrack.Codec);
        dffTrack.Title = "Edited";
        Assert.False(meta.WriteTrackMetadata(dffTrack));
    }

    /// <summary>ReplayGain measures DSD at the level the player decodes it (+6 dB over ffmpeg's).</summary>
    [Fact]
    public void ReplayGainScan_BoostsDsdLikeThePlayer()
    {
        Assert.StartsWith("volume=6.0206dB,ebur128=", ReplayGainScannerService.LoudnessFilter("a.dsf"));
        Assert.StartsWith("volume=6.0206dB,ebur128=", ReplayGainScannerService.LoudnessFilter("a.DFF"));
        Assert.StartsWith("ebur128=", ReplayGainScannerService.LoudnessFilter("a.flac"));
    }

    [Fact]
    public void PcmInput_ServesAWavThatReadsTheSameAtAnyOffset()
    {
        var path = EncodeSine("wav.dsf", 2822400, 0.2);
        var info = DsdStreamInfo.Probe(path)!;
        var input = new DsdPcmInput(path, info);
        Assert.True(input.Open(out var size));
        Assert.Equal((ulong)(input.HeaderLength + input.DataBytes), size);
        var whole = new byte[(int)size];
        var n = input.Produce(0, whole);
        Assert.Equal(whole.Length, n);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(whole, 0, 4));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(whole.AsSpan(20)));      // IEEE float
        Assert.Equal(88200, BinaryPrimitives.ReadInt32LittleEndian(whole.AsSpan(24)));
        foreach (var at in new long[] { 3, 44, 45, 47, 1001, (long)size - 7 })
        {
            var part = new byte[333];
            var got = input.Produce(at, part);
            Assert.Equal((int)Math.Min(333, (long)size - at), got);
            Assert.True(part.AsSpan(0, got).SequenceEqual(whole.AsSpan((int)at, got)), $"offset {at}");
        }
        Assert.Equal(0, input.Produce((long)size, new byte[10]));
        input.Close();
        input.Release();
    }

    /// <summary>
    /// Plays a DSF through the real player with no device (NOCTIS_AOUT=dummy): it opens,
    /// reports the file's length, the position moves, a seek lands, and LibVLC never says
    /// it can't identify the codec (what a .dsf did before, see DsdPcmInput).
    /// </summary>
    [Theory]
    [InlineData("dsf")]
    [InlineData("dff")]
    public void Player_PlaysDsd(string kind)
    {
        Assert.SkipUnless(NullWavePlayer.SilentMode, "needs NOCTIS_AOUT=dummy for the test run");
        var path = EncodeSine("play.dsf", 2822400, 12);
        if (kind == "dff")
        {
            var info = DsdStreamInfo.Probe(path)!;
            var raw = File.ReadAllBytes(path);
            var per = (int)info.BytesPerChannel;
            var inter = new byte[per * 2];
            for (var i = 0; i < per; i++)
                for (var c = 0; c < 2; c++)
                    inter[i * 2 + c] = ReverseBits(raw[info.DataOffset + (i / 4096) * 4096 * 2 + c * 4096 + i % 4096]);
            path = Path.Combine(_dir, "play.dff");
            File.WriteAllBytes(path, BuildDff(2822400, new[] { "SLFT", "SRGT" }, inter));
        }

        using var player = new VlcAudioPlayer();
        var lib = (LibVLC)typeof(VlcAudioPlayer).GetField("_libVlc", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(player)!;
        var codecErrors = 0;
        var vlcLines = new List<string>();
        lib.Log += (_, e) =>
        {
            if (e.Message.Contains("identify", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref codecErrors);
            if (e.Level >= LogLevel.Warning) lock (vlcLines) if (vlcLines.Count < 30) vlcLines.Add($"{e.Level} {e.Module}: {e.Message}");
        };
        long heard = 0;
        string? error = null;
        var ended = false;
        player.PositionChanged += (_, p) => Interlocked.Exchange(ref heard, (long)p.TotalMilliseconds);
        player.PlaybackError += (_, e) => error = e;
        player.TrackEnded += (_, _) => ended = true;
        player.Play(path);
        var sw = Stopwatch.StartNew();
        while (Interlocked.Read(ref heard) < 1500 && sw.ElapsedMilliseconds < 15000) Thread.Sleep(20);
        _o.WriteLine($"{kind}: started, heard={Interlocked.Read(ref heard)} after {sw.ElapsedMilliseconds} ms, duration={player.Duration}, error={error}");
        lock (vlcLines) foreach (var l in vlcLines) _o.WriteLine("  vlc " + l);
        Assert.Null(error);
        Assert.True(Interlocked.Read(ref heard) >= 1500, "position never moved");
        Assert.InRange(player.Duration.TotalSeconds, 11.9, 12.1);

        player.Seek(TimeSpan.FromSeconds(9));
        sw.Restart();
        while (Interlocked.Read(ref heard) < 9500 && sw.ElapsedMilliseconds < 8000) Thread.Sleep(20);
        _o.WriteLine($"{kind}: after seek heard={Interlocked.Read(ref heard)}");
        Assert.InRange(Interlocked.Read(ref heard), 9000, 12100);
        sw.Restart();
        while (!ended && sw.ElapsedMilliseconds < 10000) Thread.Sleep(50);
        _o.WriteLine($"{kind}: ended={ended} after {sw.ElapsedMilliseconds} ms, codecErrors={codecErrors}");
        Assert.True(ended, "never reached the end");
        Assert.Equal(0, codecErrors);
    }

    /// <summary>
    /// Gapless into a second DSD file: the standby prepare (which skips LibVLC's parse for
    /// DSD media) succeeds and the handoff doesn't fall back to a cold open.
    /// </summary>
    [Fact]
    public void Player_GaplessIntoNextDsd()
    {
        Assert.SkipUnless(NullWavePlayer.SilentMode, "needs NOCTIS_AOUT=dummy for the test run");
        var a = EncodeSine("a.dsf", 2822400, 4);
        var b = EncodeSine("b.dsf", 2822400, 4);
        var log = new List<string>();
        DebugLogger.IsEnabled = true;
        DebugLogger.EntryAdded += en =>
        {
            if (en.Action.StartsWith("Gapless.", StringComparison.Ordinal) || en.Action.StartsWith("AutoMix.Dual", StringComparison.Ordinal)
                || en.Action.StartsWith("Dsd.", StringComparison.Ordinal))
                lock (log) log.Add($"{en.Action} | {en.Metadata}");
        };
        using var player = new VlcAudioPlayer();
        player.SetGapless(true);
        long heard = 0;
        var ends = 0;
        string? error = null;
        player.PositionChanged += (_, p) => Interlocked.Exchange(ref heard, (long)p.TotalMilliseconds);
        player.PlaybackError += (_, e) => error = e;
        player.TrackEnded += (_, _) =>
        {
            if (Interlocked.Increment(ref ends) == 1) ThreadPool.QueueUserWorkItem(_ => player.Play(b));
        };
        player.Play(a);
        var sw = Stopwatch.StartNew();
        while (Interlocked.Read(ref heard) < 1000 && sw.ElapsedMilliseconds < 10000) Thread.Sleep(20);
        player.PrepareNext(b);
        while (Volatile.Read(ref ends) < 2 && sw.ElapsedMilliseconds < 20000) Thread.Sleep(50);
        lock (log) foreach (var l in log) _o.WriteLine(l);
        _o.WriteLine($"ends={ends} error={error} after {sw.ElapsedMilliseconds} ms");
        Assert.Null(error);
        Assert.Equal(2, Volatile.Read(ref ends));
        lock (log)
        {
            Assert.Contains(log, l => l.StartsWith("AutoMix.DualPrepared", StringComparison.Ordinal));
            Assert.DoesNotContain(log, l => l.StartsWith("Gapless.ColdFallback", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The Convert dialog's "DSD (.dsf)": a tagged FLAC with a cover goes through ffmpeg and the
    /// modulator; the DSF probes, carries the tags and the cover, scans back in, and ffmpeg's
    /// own DSD decoder hears the source's tone at the source's level (−6 dB: ffmpeg decodes
    /// at plain modulation level, Noctis' player adds the +6 dB back).
    /// </summary>
    [Fact]
    public async Task Converter_FlacToDsf_EndToEnd()
    {
        var converter = new AudioConverterService(() => string.Empty, new MetadataService());
        var ffmpeg = converter.GetFfmpegPath();
        Assert.SkipWhen(ffmpeg == null, "no ffmpeg on this machine");
        var cover = Path.Combine(_dir, "cover.png");
        var flac = Path.Combine(_dir, "src.flac");
        Assert.True(RunFfmpeg(ffmpeg!, "-y", "-f", "lavfi", "-i", "color=c=red:s=64x64", "-frames:v", "1", cover));
        Assert.True(RunFfmpeg(ffmpeg!, "-y", "-f", "lavfi", "-i", "aevalsrc=0.5*sin(2*PI*1000*t)|0.5*sin(2*PI*1000*t):s=44100:d=6",
            "-i", cover, "-map", "0:a", "-map", "1:v", "-c:v", "copy", "-disposition:v", "attached_pic",
            "-sample_fmt", "s32", "-metadata", "title=Tone", "-metadata", "artist=Noctis", "-metadata", "album=Tests", flac));
        var source = new MetadataService().ReadTrackMetadata(flac)!;

        var reports = new List<string>();
        var summary = await converter.ConvertAsync(new[] { source },
            new AudioConvertOptions { Format = "dsf", DsdRate = "DSD64", OutputFolder = _dir, FilenamePattern = "%title%", AppendFormatToTitle = true },
            new SyncProgress(p => { lock (reports) reports.Add(p.Status); }), CancellationToken.None);
        _o.WriteLine(string.Join(" | ", reports.Distinct()));
        Assert.Equal(1, summary.Converted);
        var dsf = Assert.Single(summary.OutputPaths);
        Assert.EndsWith(".dsf", dsf);
        Assert.Contains(reports, r => r.StartsWith("Converting… ", StringComparison.Ordinal) && r.EndsWith('%'));

        var info = DsdStreamInfo.Probe(dsf)!;
        Assert.Equal(2822400, info.SampleRate);
        var track = new MetadataService().ReadTrackMetadata(dsf, out var art)!;
        Assert.Equal("Tone (DSF)", track.Title);
        Assert.Equal("Noctis", track.Artist);
        Assert.Equal("Tests", track.Album);
        Assert.NotNull(art);
        Assert.InRange(track.Duration.TotalSeconds, 5.9, 6.2);

        // ffmpeg decodes our DSF: tone at half the source level, clean.
        var raw = Path.Combine(_dir, "back.f32");
        Assert.True(RunFfmpeg(ffmpeg!, "-y", "-i", dsf, "-ar", "88200", "-f", "f32le", "-c:a", "pcm_f32le", raw));
        var bytes = File.ReadAllBytes(raw);
        var pcm = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 4);
        var (amp, snr) = Analyse(pcm, 2, 0, 88200, 1000);
        _o.WriteLine($"ffmpeg decode of the DSF: 1 kHz amp={amp:0.0000} (source 0.5 → expected 0.25), SNR={snr:0.0} dB");
        Assert.InRange(amp, 0.24, 0.26);
        Assert.True(snr > 80, $"SNR {snr:0.0} dB");

        // The seek bar's waveform (ffmpeg) reads it too.
        var wave = new Noctis.Services.Waveform.FfmpegWaveformDecoder(converter).Decode(dsf, CancellationToken.None);
        Assert.NotNull(wave);
        Assert.True(wave!.BucketCount > 100);
        Assert.True(wave.LevelAt(wave.BucketCount / 2) > 0.5);
    }

    private sealed class SyncProgress(Action<ConvertProgress> report) : IProgress<ConvertProgress>
    {
        public void Report(ConvertProgress value) => report(value);
    }

    private static bool RunFfmpeg(string ffmpeg, params string[] args)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    /// <summary>
    /// Explicit (NOCTIS_DSD_SRC=a source file): converts it the way the Convert dialog does to
    /// DSD64 / DSD128 / DSD256 DSFs beside it, plus a DSD64 .dff copy, timing each conversion.
    /// Makes the live-test files.
    /// </summary>
    [Fact]
    public async Task Make_DsdFilesFromSource()
    {
        var src = Environment.GetEnvironmentVariable("NOCTIS_DSD_SRC");
        Assert.SkipWhen(string.IsNullOrEmpty(src) || !File.Exists(src), "set NOCTIS_DSD_SRC");
        var converter = new AudioConverterService(() => string.Empty, new MetadataService());
        var track = new MetadataService().ReadTrackMetadata(src!)!;
        var dir = Path.GetDirectoryName(Path.GetFullPath(src!))!;
        foreach (var rate in new[] { "DSD64", "DSD128", "DSD256" })
        {
            var sw = Stopwatch.StartNew();
            var summary = await converter.ConvertAsync(new[] { track },
                new AudioConvertOptions { Format = "dsf", DsdRate = rate, OutputFolder = dir, FilenamePattern = "%title% " + rate, OverwriteExisting = true },
                new SyncProgress(_ => { }), CancellationToken.None);
            var outPath = Assert.Single(summary.OutputPaths);
            _o.WriteLine($"{rate}: {track.Duration.TotalSeconds:0} s source converted in {sw.Elapsed.TotalSeconds:0.0} s -> {outPath}");
            if (rate == "DSD64")
            {
                var info = DsdStreamInfo.Probe(outPath)!;
                var raw = File.ReadAllBytes(outPath);
                var per = (int)info.BytesPerChannel;
                var inter = new byte[per * 2];
                for (var i = 0; i < per; i++)
                    for (var c = 0; c < 2; c++)
                        inter[i * 2 + c] = ReverseBits(raw[info.DataOffset + (i / 4096) * 4096 * 2 + c * 4096 + i % 4096]);
                var dff = Path.ChangeExtension(outPath, ".dff");
                // Carry the DSF's tags and cover over in an "ID3 " chunk, as DSDIFF taggers do.
                byte[] id3;
                using (var tagged = TagLib.File.Create(outPath))
                    id3 = ((TagLib.Id3v2.Tag)tagged.GetTag(TagLib.TagTypes.Id3v2, true)).Render().Data;
                File.WriteAllBytes(dff, BuildDff(info.SampleRate, new[] { "SLFT", "SRGT" }, inter, id3));
                _o.WriteLine("dff copy: " + dff);
            }
        }
    }

    /// <summary>Encoder speed on a 4-minute stereo song (explicit: NOCTIS_DSD_BENCH=1).</summary>
    [Theory]
    [InlineData("DSD64")]
    [InlineData("DSD128")]
    [InlineData("DSD256")]
    public void Bench_FourMinuteSong(string rate)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NOCTIS_DSD_BENCH") == "1", "set NOCTIS_DSD_BENCH=1");
        using var pcm = new SineStream(240, 0.5);
        using var output = new FileStream(Path.Combine(_dir, "bench.dsf"), FileMode.Create);
        var sw = Stopwatch.StartNew();
        DsdEncoder.Encode(pcm, 2, DsdEncoder.RateFor(rate)!.Value, output);
        _o.WriteLine($"{rate}: 240 s stereo encoded in {sw.Elapsed.TotalSeconds:0.0} s ({output.Length / 1048576.0:0} MB)");
    }

    /// <summary>Endless-free sine PCM generated on read (a 4-minute buffer would be 677 MB).</summary>
    private sealed class SineStream(double seconds, double amplitude) : Stream
    {
        private readonly long _frames = (long)(DsdEncoder.PcmRate * seconds);
        private long _frame;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length / 8, _frames - _frame);
            for (var i = 0; i < n; i++, _frame++)
            {
                var t = (double)_frame / DsdEncoder.PcmRate;
                BinaryPrimitives.WriteSingleLittleEndian(buffer[(i * 8)..], (float)(amplitude * Math.Sin(2 * Math.PI * 997 * t)));
                BinaryPrimitives.WriteSingleLittleEndian(buffer[(i * 8 + 4)..], (float)(amplitude * Math.Sin(2 * Math.PI * 440 * t)));
            }
            return n * 8;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
