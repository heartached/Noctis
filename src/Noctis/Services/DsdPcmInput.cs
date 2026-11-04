using System.Buffers.Binary;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Noctis.Services.Dsd;

namespace Noctis.Services;

/// <summary>
/// A DSD file (.dsf / .dff) served to LibVLC as a seekable WAV of 32-bit float PCM at
/// 88.2 kHz (96 kHz for the 48 kHz family), decoded on the fly by <see cref="DsdPcmDecoder"/>.
///
/// Why: the LibVLC 3 the app ships cannot play DSD. Its avformat demuxer opens a .dsf but
/// VLC's codec table has no entry for FFmpeg's dsd_lsbf/dsd_msbf decoders ("adding es: audio
/// codec = undf", then "could not identify codec"), and its own demuxers don't know DSDIFF
/// at all. A WAV is something every LibVLC build plays — avformat's wav demuxer or VLC's
/// own (Linux system installs without the avformat plugin) — so the rest of the player
/// (seek, duration, gapless engine, crossfade, ReplayGain, EQ, exclusive output) works
/// unchanged. Each read decodes only the frames it covers, so a seek costs nothing and
/// memory stays constant. Native DSD output to a DAC (DoP / ASIO DSD) is out of scope.
///
/// Files whose PCM would pass the 4 GB a WAV can hold (≈100 min of stereo float) fall back
/// to 24-bit integer samples, and past that the WAV ends at the 4 GB mark.
/// </summary>
internal sealed class DsdPcmInput : MediaInput
{
    private const int WaveFormatPcm = 1, WaveFormatFloat = 3, WaveFormatExtensible = 0xFFFE;
    private const int FramesPerRead = 8192;

    private readonly string _path;
    private readonly DsdStreamInfo _info;
    private readonly object _gate = new();
    private readonly byte[] _header;
    private readonly long _dataBytes;
    private readonly int _frameBytes;
    private readonly int _sampleBytes;
    private readonly long _frames;
    private DsdPcmDecoder? _decoder;
    private float[] _pcm = Array.Empty<float>();
    private byte[] _bytes = Array.Empty<byte>();
    private long _position;
    private int _opens;
    private bool _released;

    public int OutputRate { get; }
    public long DataBytes => _dataBytes;
    public int HeaderLength => _header.Length;

    public DsdPcmInput(string path, DsdStreamInfo info)
    {
        _path = path;
        _info = info;
        CanSeek = true;
        var dec = DsdPcmDecoder.DecimationFor(info.SampleRate)
                  ?? throw new NotSupportedException($"DSD rate {info.SampleRate} Hz is not supported");
        OutputRate = info.SampleRate / dec;
        var frames = info.BytesPerChannel / (dec / 8);
        _sampleBytes = 4;
        var limit = uint.MaxValue - 128L;
        if (frames * info.Channels * 4 > limit) _sampleBytes = 3;
        _frameBytes = _sampleBytes * info.Channels;
        _frames = Math.Min(frames, limit / _frameBytes);
        _dataBytes = _frames * _frameBytes;
        _header = BuildHeader(info.Channels, OutputRate, _sampleBytes, info.ChannelMask, _dataBytes);
    }

    /// <summary>
    /// Media for a local DSD file, or null when it isn't one this can play (DST-compressed
    /// DSDIFF, an unknown rate, unreadable) — the caller then opens it by path as before.
    /// </summary>
    public static Media? TryCreateMedia(LibVLC libVlc, string path)
    {
        if (!DsdStreamInfo.IsDsdPath(path)) return null;
        var info = DsdStreamInfo.Probe(path);
        if (info == null || info.BytesPerChannel <= 0 || DsdPcmDecoder.DecimationFor(info.SampleRate) == null)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "Dsd.Unplayable", Path.GetFileName(path));
            return null;
        }
        DebugLogger.Info(DebugLogger.Category.Playback, "Dsd.Open",
            $"file={Path.GetFileName(path)}, rate={info.SampleRate}, ch={info.Channels}, lsb={info.LsbFirst}");
        return new DsdMedia(libVlc, new DsdPcmInput(path, info));
    }

    /// <summary>
    /// True for media made by <see cref="TryCreateMedia"/>. LibVLC 3 never pre-parses media
    /// that reads through callbacks (Parse returns Skipped for every option, measured on
    /// 3.0.23), so the player skips its parse wait for these: the format is known here and
    /// the length arrives with playback like any other file's.
    /// </summary>
    public static bool IsDsdMedia(Media? media) => media is DsdMedia;

    /// <summary>The PCM rate/channels a DSD media plays at (what a parse would have reported).</summary>
    public static bool TryGetFormat(Media? media, out int rate, out int channels)
    {
        if (media is DsdMedia d)
        {
            rate = d.Input.OutputRate;
            channels = d.Input._info.Channels;
            return true;
        }
        rate = channels = 0;
        return false;
    }

    public override bool Open(out ulong size)
    {
        lock (_gate)
        {
            size = (ulong)(_header.Length + _dataBytes);
            try
            {
                if (_decoder == null)
                {
                    var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
                    _decoder = new DsdPcmDecoder(_info, stream);
                }
                _opens++;
                _position = 0;
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "Dsd.OpenFailed", ex.Message);
                return false;
            }
        }
    }

    public override int Read(IntPtr buf, uint len)
    {
        lock (_gate)
        {
            if (_decoder == null) return -1;
            var want = (int)Math.Min(len, 1u << 20);
            if (_bytes.Length < want) _bytes = new byte[want];
            var n = Produce(_position, _bytes.AsSpan(0, want));
            if (n > 0) Marshal.Copy(_bytes, 0, buf, n);
            _position += n;
            return n;
        }
    }

    public override bool Seek(ulong offset)
    {
        lock (_gate)
        {
            _position = (long)Math.Min(offset, (ulong)(_header.Length + _dataBytes));
            return true;
        }
    }

    public override void Close()
    {
        lock (_gate)
        {
            if (_opens > 0) _opens--;
            if (_opens == 0)
            {
                _decoder?.Dispose();
                _decoder = null;
                _pcm = Array.Empty<float>();
                _bytes = Array.Empty<byte>();
            }
            if (_released && _opens == 0) Dispose();
        }
    }

    /// <summary>
    /// The media wrapper is gone. The GC handle LibVLC reaches this through is freed now, or
    /// at the last Close when an open stream (the player still finishing) needs it.
    /// </summary>
    internal void Release()
    {
        lock (_gate)
        {
            _released = true;
            if (_opens == 0) Dispose();
        }
    }

    /// <summary>Fills <paramref name="dest"/> with the WAV bytes at <paramref name="position"/>;
    /// returns the count (0 at the end). Internal for tests (needs an open decoder).</summary>
    internal int Produce(long position, Span<byte> dest)
    {
        var total = _header.Length + _dataBytes;
        if (position >= total || dest.Length == 0) return 0;
        var written = 0;
        if (position < _header.Length)
        {
            var h = (int)Math.Min(_header.Length - position, dest.Length);
            _header.AsSpan((int)position, h).CopyTo(dest);
            written = h;
            position += h;
        }
        var decoder = _decoder!;
        while (written < dest.Length && position < total)
        {
            var dataPos = position - _header.Length;
            var frame = dataPos / _frameBytes;
            var skip = (int)(dataPos - frame * _frameBytes);
            var frames = (int)Math.Min(FramesPerRead, Math.Min(_frames - frame,
                (dest.Length - written + skip + _frameBytes - 1) / _frameBytes));
            if (frames <= 0) break;
            var channels = _info.Channels;
            if (_pcm.Length < frames * channels) _pcm = new float[frames * channels];
            var got = decoder.Decode(frame, frames, _pcm);
            if (got <= 0) break;
            var chunk = got * _frameBytes - skip;
            var take = Math.Min(chunk, dest.Length - written);
            var outSpan = dest.Slice(written, take);
            var firstSample = skip / _sampleBytes;
            var sampleSkip = skip % _sampleBytes;
            WriteSamples(_pcm.AsSpan(0, got * channels), firstSample, sampleSkip, outSpan);
            written += take;
            position += take;
        }
        return written;
    }

    /// <summary>Encodes samples from <paramref name="first"/> on into <paramref name="dest"/>,
    /// dropping the first <paramref name="byteSkip"/> bytes (a read that starts mid-sample).</summary>
    private void WriteSamples(ReadOnlySpan<float> samples, int first, int byteSkip, Span<byte> dest)
    {
        Span<byte> tmp = stackalloc byte[4];
        var o = 0;
        for (var i = first; i < samples.Length && o < dest.Length; i++)
        {
            if (_sampleBytes == 4)
                BinaryPrimitives.WriteSingleLittleEndian(tmp, samples[i]);
            else
            {
                var v = (int)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 8388607.0);
                tmp[0] = (byte)v; tmp[1] = (byte)(v >> 8); tmp[2] = (byte)(v >> 16);
            }
            for (var b = byteSkip; b < _sampleBytes && o < dest.Length; b++) dest[o++] = tmp[b];
            byteSkip = 0;
        }
    }

    internal static byte[] BuildHeader(int channels, int rate, int sampleBytes, uint channelMask, long dataBytes)
    {
        var extensible = channels > 2;
        var fmtSize = extensible ? 40 : 16;
        var h = new byte[12 + 8 + fmtSize + 8];
        var s = h.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)Math.Min(uint.MaxValue, h.Length - 8 + dataBytes));
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], fmtSize);
        var f = s[20..];
        var tag = sampleBytes == 4 ? WaveFormatFloat : WaveFormatPcm;
        BinaryPrimitives.WriteUInt16LittleEndian(f, (ushort)(extensible ? WaveFormatExtensible : tag));
        BinaryPrimitives.WriteUInt16LittleEndian(f[2..], (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(f[4..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(f[8..], rate * channels * sampleBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(f[12..], (ushort)(channels * sampleBytes));
        BinaryPrimitives.WriteUInt16LittleEndian(f[14..], (ushort)(sampleBytes * 8));
        if (extensible)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(f[16..], 22);
            BinaryPrimitives.WriteUInt16LittleEndian(f[18..], (ushort)(sampleBytes * 8));
            BinaryPrimitives.WriteUInt32LittleEndian(f[20..], channelMask);
            // KSDATAFORMAT_SUBTYPE_PCM / _IEEE_FLOAT: {0000000x-0000-0010-8000-00aa00389b71}
            BinaryPrimitives.WriteUInt32LittleEndian(f[24..], (uint)tag);
            new byte[] { 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 }.CopyTo(f[28..]);
        }
        var d = s[(20 + fmtSize)..];
        "data"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt32LittleEndian(d[4..], (uint)dataBytes);
        return h;
    }

    /// <summary>The media for a <see cref="DsdPcmInput"/>; disposing it releases the input.</summary>
    private sealed class DsdMedia : Media
    {
        private readonly DsdPcmInput _input;

        public DsdMedia(LibVLC libVlc, DsdPcmInput input) : base(libVlc, input) => _input = input;

        public DsdPcmInput Input => _input;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _input.Release();
        }
    }
}
