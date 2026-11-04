using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Noctis.Services.Dsd;

/// <summary>
/// Turns DSD (1-bit, 2.8224 MHz and up) into 32-bit float PCM at 88.2 kHz (96 kHz for the
/// 48 kHz family) with a linear-phase low-pass FIR evaluated through per-byte lookup
/// tables: each byte of 8 one-bit samples is one table read, so an output sample costs
/// <c>4 × decimation</c> reads, not one multiply per tap.
///
/// Stateless between calls: <see cref="Decode"/> reads whatever stretch of the file the
/// requested frames need (plus the filter's reach on either side), so a seek is just a
/// different frame number and needs no warm-up.
///
/// Level: +6 dB (×2), so 50 % modulation — SACD's 0 dB reference level, and what
/// <see cref="DsdModulator"/> writes PCM full scale as — comes out at PCM full scale and a
/// DSD release plays as loud as its PCM master. Hotter passages (SACD allows up to ~70 %
/// modulation) come out above 1.0; the float output keeps them for the volume stage.
/// </summary>
internal sealed class DsdPcmDecoder : IDisposable
{
    /// <summary>Gain applied on top of the plain modulation-index level ffmpeg decodes at.</summary>
    public const double GainDb = 6.0206;
    private const double Gain = 2.0;
    /// <summary>DSD silence (0110 1001 in time order, MSB first): the filter's input outside the file.</summary>
    private const byte SilenceMsbFirst = 0x69, SilenceLsbFirst = 0x96;

    private readonly DsdStreamInfo _info;
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly float[] _table;
    private readonly int _groups;          // filter length in bytes (8 taps each)
    private readonly int _bytesPerFrame;   // DSD bytes per channel per output sample
    private byte[] _raw = Array.Empty<byte>();
    private byte[][] _chan;

    public int OutputRate { get; }
    public int Channels => _info.Channels;
    /// <summary>Output frames in the file.</summary>
    public long TotalFrames { get; }
    public DsdStreamInfo Info => _info;

    public DsdPcmDecoder(DsdStreamInfo info, Stream stream, bool ownsStream = true)
    {
        _info = info;
        _stream = stream;
        _ownsStream = ownsStream;
        var decimation = DecimationFor(info.SampleRate)
            ?? throw new NotSupportedException($"DSD rate {info.SampleRate} Hz is not supported");
        OutputRate = info.SampleRate / decimation;
        _bytesPerFrame = decimation / 8;
        _groups = 4 * decimation;
        _table = TableFor(decimation, info.LsbFirst);
        TotalFrames = info.BytesPerChannel / _bytesPerFrame;
        _chan = new byte[info.Channels][];
        for (var c = 0; c < _chan.Length; c++) _chan[c] = Array.Empty<byte>();
    }

    /// <summary>
    /// Bits per output sample for a DSD rate: the power of two from 32 up that brings it to
    /// 88.2/96 kHz or below. Null for rates that are not a whole multiple of it (not DSD).
    /// </summary>
    internal static int? DecimationFor(int dsdRate)
    {
        if (dsdRate < 1_000_000) return null;
        var dec = 32;
        while (dsdRate / dec > 96000) dec *= 2;
        return dsdRate % dec == 0 && dec <= 1024 ? dec : null;
    }

    /// <summary>
    /// Decodes <paramref name="frameCount"/> frames starting at <paramref name="frame"/> into
    /// <paramref name="dest"/> (interleaved, WAV channel order). Returns the frames written,
    /// fewer at the end of the file.
    /// </summary>
    public int Decode(long frame, int frameCount, Span<float> dest)
    {
        if (frame < 0) frame = 0;
        frameCount = (int)Math.Min(frameCount, Math.Max(0, TotalFrames - frame));
        if (frameCount <= 0) return 0;
        var channels = _info.Channels;
        if (dest.Length < frameCount * channels) throw new ArgumentException("destination too small", nameof(dest));

        // Bytes of each channel the frames reach: frame k is centred on byte k·bpf.
        var first = frame * _bytesPerFrame - _groups / 2;
        var span = (frameCount - 1) * _bytesPerFrame + _groups;
        FillChannels(first, span);

        var groups = _groups;
        var bpf = _bytesPerFrame;
        ref var table = ref MemoryMarshal.GetArrayDataReference(_table);
        for (var oc = 0; oc < channels; oc++)
        {
            ref var bytes = ref MemoryMarshal.GetArrayDataReference(_chan[_info.ChannelOrder[oc]]);
            for (var k = 0; k < frameCount; k++)
            {
                ref var b = ref Unsafe.Add(ref bytes, k * bpf);
                float acc0 = 0, acc1 = 0;
                var g = 0;
                for (; g + 1 < groups; g += 2)
                {
                    acc0 += Unsafe.Add(ref table, (g << 8) + Unsafe.Add(ref b, g));
                    acc1 += Unsafe.Add(ref table, ((g + 1) << 8) + Unsafe.Add(ref b, g + 1));
                }
                dest[k * channels + oc] = acc0 + acc1;
            }
        }
        return frameCount;
    }

    /// <summary>Loads <paramref name="count"/> bytes of every channel from byte <paramref name="first"/>
    /// on (which may lie before the start or past the end: those bytes are silence).</summary>
    private void FillChannels(long first, int count)
    {
        var channels = _info.Channels;
        for (var c = 0; c < channels; c++)
        {
            if (_chan[c].Length < count) _chan[c] = new byte[count];
            _chan[c].AsSpan(0, count).Fill(_info.LsbFirst ? SilenceLsbFirst : SilenceMsbFirst);
        }
        var from = Math.Max(0, first);
        var to = Math.Min(_info.BytesPerChannel, first + count);
        if (to <= from) return;

        var block = _info.BlockSize;
        if (block == 1)
        {
            // DSDIFF: one byte per channel in turn.
            var length = (int)((to - from) * channels);
            if (_raw.Length < length) _raw = new byte[length];
            _stream.Position = _info.DataOffset + from * channels;
            var got = _stream.ReadAtLeast(_raw.AsSpan(0, length), length, throwOnEndOfStream: false) / channels;
            var dst = (int)(from - first);
            for (var i = 0; i < got; i++)
                for (var c = 0; c < channels; c++)
                    _chan[c][dst + i] = _raw[i * channels + c];
            return;
        }

        // DSF: one block of each channel in turn.
        var firstBlock = from / block;
        var lastBlock = (to - 1) / block;
        var groupBytes = (long)block * channels;
        var total = (int)((lastBlock - firstBlock + 1) * groupBytes);
        if (_raw.Length < total) _raw = new byte[total];
        _stream.Position = _info.DataOffset + firstBlock * groupBytes;
        var read = _stream.ReadAtLeast(_raw.AsSpan(0, total), total, throwOnEndOfStream: false);
        for (var pos = from; pos < to;)
        {
            var blk = pos / block;
            var within = (int)(pos - blk * block);
            var run = (int)Math.Min(block - within, to - pos);
            var groupStart = (blk - firstBlock) * groupBytes;
            var dst = (int)(pos - first);
            for (var c = 0; c < channels; c++)
            {
                var src = groupStart + (long)c * block + within;
                var avail = (int)Math.Clamp(read - src, 0, run);
                if (avail > 0) _raw.AsSpan((int)src, avail).CopyTo(_chan[c].AsSpan(dst, avail));
            }
            pos += run;
        }
    }

    public void Dispose()
    {
        if (_ownsStream) _stream.Dispose();
    }

    // ── Filter ──

    private static readonly ConcurrentDictionary<(int, bool), float[]> Tables = new();

    private static float[] TableFor(int decimation, bool lsbFirst) =>
        Tables.GetOrAdd((decimation, lsbFirst), static key => BuildTable(key.Item1, key.Item2));

    /// <summary>
    /// The lookup table for a Kaiser-windowed sinc low-pass of <c>32 × decimation</c> taps at the
    /// DSD rate (cut-off 0.36 × output rate, ≈31.8 kHz at 88.2 kHz: flat through 20 kHz, about
    /// 100 dB down from 41 kHz, below the output's 44.1 kHz Nyquist). Entry [g·256 + b] is the filter's
    /// response to byte value b in byte slot g, each bit counting +1 or −1.
    /// </summary>
    private static float[] BuildTable(int decimation, bool lsbFirst)
    {
        var taps = BuildFilter(decimation);
        var groups = taps.Length / 8;
        var table = new float[groups * 256];
        for (var g = 0; g < groups; g++)
        {
            for (var b = 0; b < 256; b++)
            {
                double sum = 0;
                for (var t = 0; t < 8; t++)
                {
                    var bit = lsbFirst ? (b >> t) & 1 : (b >> (7 - t)) & 1;
                    sum += taps[g * 8 + t] * (bit != 0 ? 1.0 : -1.0);
                }
                table[g * 256 + b] = (float)sum;
            }
        }
        return table;
    }

    internal static double[] BuildFilter(int decimation)
    {
        var n = 32 * decimation;
        var fc = 0.36 / decimation; // cycles per DSD sample
        const double beta = 10.0;
        var h = new double[n];
        var mid = (n - 1) / 2.0;
        var i0Beta = BesselI0(beta);
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var x = i - mid;
            var sinc = Math.Abs(x) < 1e-12 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            var r = 2.0 * i / (n - 1) - 1.0;
            var w = BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0Beta;
            h[i] = sinc * w;
            sum += h[i];
        }
        for (var i = 0; i < n; i++) h[i] *= Gain / sum;
        return h;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (var k = 1; k < 64; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }
}
