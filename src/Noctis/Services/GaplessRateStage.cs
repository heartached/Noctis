using System.Collections.Concurrent;
using NAudio.Dsp;
using NAudio.Wave;

namespace Noctis.Services;

// ── The splice engine's own sample-rate conversion ──────────────────────
//
// LibVLC hands every input over at the file's own rate (see VlcAudioPlayer.EngineSetupFormat)
// and the engine converts to the device rate here, AFTER the splice: one resampler runs
// on across a natural seam between two tracks at the same rate, so nothing is held back at
// a track end. VLC's own resampler could not do that: it sat in every input's chain, held
// half its filter (2.7-2.9 ms) and dropped it when the input ended (harness 11-19).
//
// What a stage holds is played out ("drained") up to the input's last frame when the input
// ends and no track at the same rate follows; the next track then starts on the frame after.

/// <summary>
/// WDL windowed-sinc settings for the engine and the delay they add. Pure apart from the
/// latency cache.
/// </summary>
internal static class EngineResampler
{
    // Taps per output sample when converting up or between equal-ish rates; a down-conversion
    // widens the filter by the ratio, so its stop band stays where it is in output terms.
    internal const int BaseSincSize = 64;
    private const int MaxSincSize = 512;
    private const int SincInterpSize = 32;

    private static readonly ConcurrentDictionary<(int In, int Out), double> Latencies = new();

    /// <summary>The sinc length for <paramref name="inRate"/> → <paramref name="outRate"/>.</summary>
    internal static int SincSize(int inRate, int outRate)
    {
        var ratio = Math.Max(1.0, (double)inRate / Math.Max(1, outRate));
        var size = (int)Math.Ceiling(BaseSincSize * ratio);
        size = (size + 7) & ~7;
        return Math.Clamp(size, BaseSincSize, MaxSincSize);
    }

    internal static WdlResampler Create(int inRate, int outRate)
    {
        var r = new WdlResampler();
        r.SetMode(true, 0, true, SincSize(inRate, outRate), SincInterpSize);
        r.SetFilterParms();
        // Input-driven: the stage hands over exactly the frames it has. In output-driven mode
        // NAudio's WDL takes any shortfall against what it asked for as the end of the input
        // and pads zeros to deliver the full request: at every underrun and every track end
        // it would make up audio and move the timeline.
        r.SetFeedMode(true);
        r.SetRates(inRate, outRate);
        return r;
    }

    /// <summary>
    /// Feeds <paramref name="inFrames"/> interleaved frames of <paramref name="input"/> and
    /// writes what comes out to <paramref name="output"/> (grown when needed). Returns the frames made.
    /// </summary>
    internal static int Process(WdlResampler r, float[] input, int inFrames, int channels, ref float[] output, double outPerIn)
    {
        if (inFrames <= 0) return 0;
        var maxOut = (int)Math.Ceiling(inFrames * outPerIn) + MaxSincSize;
        if (output.Length < maxOut * channels) output = new float[maxOut * channels];
        r.ResamplePrepare(inFrames, channels, out var inBuf, out var inOff);
        Array.Copy(input, 0, inBuf, inOff, inFrames * channels);
        return r.ResampleOut(output, 0, inFrames, maxOut, channels);
    }

    /// <summary>
    /// How many output frames a fresh resampler emits before the first input frame's
    /// position (its zero pre-fill plus the filter's centre), measured once per rate pair
    /// with an impulse. 0 when the rates match (no resampler then).
    /// </summary>
    internal static double LatencyFrames(int inRate, int outRate) =>
        inRate == outRate || inRate <= 0 || outRate <= 0 ? 0 : Latencies.GetOrAdd((inRate, outRate), k => Measure(k.In, k.Out));

    private static double Measure(int inRate, int outRate)
    {
        var r = Create(inRate, outRate);
        var impulseAt = SincSize(inRate, outRate) * 2;
        var input = new float[impulseAt * 4];
        input[impulseAt] = 1f;
        var made = new float[1];
        var produced = Process(r, input, input.Length, 1, ref made, (double)outRate / inRate);
        var output = made;
        var peak = 0;
        for (var i = 1; i < produced; i++)
            if (Math.Abs(output[i]) > Math.Abs(output[peak])) peak = i;
        double frac = 0;
        if (peak > 0 && peak < produced - 1)
        {
            double a = output[peak - 1], b = output[peak], c = output[peak + 1];
            var den = a - 2 * b + c;
            if (den != 0) frac = 0.5 * (a - c) / den;
        }
        return peak + frac - (double)impulseAt * outRate / inRate;
    }
}

/// <summary>
/// Reads the segment a <see cref="GaplessRateStage"/> is fed from, at the segment's rate and
/// channels; zeros once the stage drains. Counts the real frames handed over.
/// </summary>
internal sealed class GaplessSegmentFeed : ISampleProvider
{
    public GaplessSegmentFeed(GaplessTrackSegment segment)
    {
        Segment = segment;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(segment.SampleRate, segment.Channels);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>The segment read now; swapped to the next one at a same-format natural seam.</summary>
    public GaplessTrackSegment Segment { get; set; }

    /// <summary>Real frames read since the stage was built (across every segment it was fed).</summary>
    public long FramesRead { get; private set; }

    /// <summary>Hand over silence instead of the segment (the stage drains what it holds).</summary>
    public bool Draining { get; set; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (Draining)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] = 0f;
            return count;
        }
        var n = Segment.Read(buffer, offset, count);
        FramesRead += n / WaveFormat.Channels;
        return n;
    }
}

/// <summary>
/// The engine's rate converter for one run of same-format segments (see the file comment):
/// WDL windowed sinc fed exactly what the segment holds, its output queued and handed out as
/// asked, its start delay dropped so the first frame out is the first frame in, and a drain
/// that plays out the end of the input up to its last frame. A pass-through when the rates
/// match. Render thread only.
/// </summary>
internal sealed class GaplessRateStage : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly GaplessSegmentFeed _feed;
    private readonly WdlResampler? _wdl;
    private readonly int _inRate;
    private readonly int _channels;
    private readonly double _outPerIn;
    private readonly double _latency;   // output frames (see EngineResampler.LatencyFrames)
    private float[] _in = new float[4096];
    private float[] _made = new float[4096];
    private float[] _queue = new float[8192];
    private int _queued;                // samples made and not handed out yet
    private long _rawOut;               // frames the resampler made, a dropped head included
    private int _skipFrames;            // head frames still to drop (the start delay)
    private long _drainEnd = -1;        // raw frame to drain up to (-1 = not draining)

    public WaveFormat WaveFormat { get; }

    public GaplessRateStage(ISampleProvider source, GaplessSegmentFeed feed, int outRate, int channels)
    {
        _source = source;
        _feed = feed;
        _inRate = source.WaveFormat.SampleRate;
        _channels = channels;
        _outPerIn = (double)outRate / _inRate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outRate, channels);
        if (_inRate != outRate)
        {
            _wdl = EngineResampler.Create(_inRate, outRate);
            _latency = EngineResampler.LatencyFrames(_inRate, outRate);
        }
        _skipFrames = Math.Max(0, (int)Math.Round(_latency));
    }

    public int InputRate => _inRate;
    public bool Resamples => _wdl != null;

    /// <summary>The stage's start delay in output frames (0 without a resampler).</summary>
    public double LatencyFrames => _latency;

    /// <summary>Whether a drain was asked for and everything up to the input's end is out.</summary>
    public bool Drained => _wdl == null || (_drainEnd >= 0 && _rawOut >= _drainEnd && _queued == 0);

    /// <summary>A drain is in progress (see <see cref="BeginDrain"/>).</summary>
    public bool IsDraining => _drainEnd >= 0;

    /// <summary>
    /// The input has ended: play out what the resampler holds, up to the output frame of the
    /// last input frame (rounded to the output grid), with silence fed behind it.
    /// </summary>
    public void BeginDrain()
    {
        if (_wdl == null || _drainEnd >= 0) return;
        _drainEnd = (long)Math.Round(_feed.FramesRead * _outPerIn + _latency);
        _feed.Draining = true;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var ch = _channels;
        count -= count % ch;
        var written = 0;
        if (_wdl == null)
            return _source.Read(buffer, offset, count);

        while (true)
        {
            // Hand out what is queued. Element stores, never Array.Copy: the render buffer can
            // be NAudio's WaveBuffer pun (a byte[] read as float[]), which Array.Copy rejects.
            var take = Math.Min(_queued, count - written);
            for (var i = 0; i < take; i++)
                buffer[offset + written + i] = _queue[i];
            if (take > 0)
            {
                _queued -= take;
                Array.Copy(_queue, take, _queue, 0, _queued);
                written += take;
            }
            if (written >= count) break;
            if (_drainEnd >= 0 && _rawOut >= _drainEnd) break;

            // Make more: about what is still wanted, from what the segment holds (zeros while draining).
            var inFrames = (int)Math.Ceiling(((count - written) / ch + _skipFrames) / _outPerIn) + 1;
            if (_in.Length < inFrames * ch) _in = new float[inFrames * ch];
            var got = _source.Read(_in, 0, inFrames * ch) / ch;
            if (got <= 0) break; // the segment ran dry: underrun, or its end
            var made = EngineResampler.Process(_wdl, _in, got, ch, ref _made, _outPerIn);
            var from = 0;
            if (_drainEnd >= 0) made = (int)Math.Min(made, _drainEnd - _rawOut);
            _rawOut += made;
            var skip = Math.Min(_skipFrames, made);
            _skipFrames -= skip;
            from = skip * ch;
            var keep = (made - skip) * ch;
            if (keep <= 0) { if (got < inFrames && _drainEnd < 0) break; continue; }
            if (_queue.Length < _queued + keep) Array.Resize(ref _queue, Math.Max(_queue.Length * 2, _queued + keep));
            Array.Copy(_made, from, _queue, _queued, keep);
            _queued += keep;
        }
        return written;
    }
}