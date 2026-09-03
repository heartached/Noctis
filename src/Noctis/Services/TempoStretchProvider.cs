using System;
using NAudio.Wave;

namespace Noctis.Services;

/// <summary>
/// Pitch-preserving playback-speed change for the splice engine (WSOLA, the
/// SoundTouch shape: seek the best-correlated input offset, overlap-add, copy
/// a sequence, skip ahead by <c>rate × sequence</c>). Pull-based: every output
/// frame is built from <c>rate</c> input frames, so the segment underneath keeps
/// counting MEDIA frames and the engine's position stays truthful at any speed.
/// At rate 1.0 it is a transparent pass-through — the gapless path stays
/// bit-exact. Rates are meant for speech (0.5×–2×); LibVLC stays at 1× under
/// the engine because it was started with time-stretch disabled (a VLC rate
/// change there would shift pitch).
/// </summary>
public sealed class TempoStretchProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Func<double> _rate;
    private readonly int _ch;
    private readonly int _overlapFrames;
    private readonly int _seekFrames;
    private readonly int _seqFramesFast;   // rate >= 1
    private readonly int _seqFramesSlow;   // rate < 1: longer sequences hide the repeats

    private float[] _in = Array.Empty<float>();
    private int _inFrames;
    private float[] _out = Array.Empty<float>();
    private int _outSamples;
    private int _outRead;
    private readonly float[] _mid;
    private double _skipFract;
    private bool _active;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public TempoStretchProvider(ISampleProvider source, Func<double> rate,
        int overlapMs = 8, int seekMs = 15, int sequenceMs = 60)
    {
        _source = source;
        _rate = rate;
        _ch = Math.Clamp(source.WaveFormat.Channels, 1, 2);
        var sr = source.WaveFormat.SampleRate;
        _overlapFrames = Math.Max(8, sr * overlapMs / 1000);
        _seekFrames = Math.Max(8, sr * seekMs / 1000);
        _seqFramesFast = Math.Max(_overlapFrames * 3, sr * sequenceMs / 1000);
        _seqFramesSlow = Math.Max(_overlapFrames * 3, sr * (sequenceMs + 40) / 1000);
        _mid = new float[_overlapFrames * _ch];
    }

    /// <summary>Clamp range for a requested speed; outside it speech is unintelligible anyway.</summary>
    public static double ClampRate(double rate) =>
        double.IsFinite(rate) ? Math.Clamp(rate, 0.5, 2.0) : 1.0;

    public int Read(float[] buffer, int offset, int count)
    {
        var rate = ClampRate(_rate());
        if (Math.Abs(rate - 1.0) < 0.001)
        {
            if (_active) Reset();
            return _source.Read(buffer, offset, count);
        }
        _active = true;

        var seq = rate >= 1.0 ? _seqFramesFast : _seqFramesSlow;
        var nominalSkip = rate * (seq - _overlapFrames);
        var needFrames = Math.Max(seq + _seekFrames, (int)Math.Ceiling(nominalSkip) + _overlapFrames + 1);

        var written = 0;
        while (written < count)
        {
            var avail = _outSamples - _outRead;
            if (avail > 0)
            {
                var n = Math.Min(avail, count - written);
                Array.Copy(_out, _outRead, buffer, offset + written, n);
                _outRead += n;
                written += n;
                if (_outRead == _outSamples) { _outRead = 0; _outSamples = 0; }
                continue;
            }

            if (!FillInput(needFrames))
                break; // underrun / end of stream: hand back what exists (0 = hold)
            ProcessBlock(seq, nominalSkip);
        }
        return written;
    }

    private bool FillInput(int needFrames)
    {
        var needSamples = needFrames * _ch;
        if (_in.Length < needSamples)
        {
            var grown = new float[needSamples * 2];
            Array.Copy(_in, grown, _inFrames * _ch);
            _in = grown;
        }
        while (_inFrames * _ch < needSamples)
        {
            var n = _source.Read(_in, _inFrames * _ch, needSamples - _inFrames * _ch);
            if (n <= 0) return false;
            _inFrames += n / _ch;
        }
        return true;
    }

    private void ProcessBlock(int seq, double nominalSkip)
    {
        var ch = _ch;
        var ovl = _overlapFrames;
        var offFrames = SeekBestOverlap();
        var off = offFrames * ch;

        var outFrames = seq - ovl;
        var outSamples = outFrames * ch;
        if (_out.Length < outSamples) _out = new float[outSamples];

        // Overlap-add the tail of the previous block into the chosen input offset.
        for (var i = 0; i < ovl; i++)
        {
            var t = (float)i / ovl;
            for (var c = 0; c < ch; c++)
            {
                var k = i * ch + c;
                _out[k] = _mid[k] * (1f - t) + _in[off + k] * t;
            }
        }
        // Straight copy of the middle of the sequence.
        var copyFrames = seq - 2 * ovl;
        Array.Copy(_in, off + ovl * ch, _out, ovl * ch, copyFrames * ch);
        // Remember the sequence's tail for the next overlap.
        Array.Copy(_in, off + (seq - ovl) * ch, _mid, 0, ovl * ch);
        _outSamples = outSamples;
        _outRead = 0;

        _skipFract += nominalSkip;
        var skip = (int)_skipFract;
        _skipFract -= skip;
        skip = Math.Min(skip, _inFrames);
        var remaining = (_inFrames - skip) * ch;
        if (remaining > 0)
            Array.Copy(_in, skip * ch, _in, 0, remaining);
        _inFrames -= skip;
    }

    /// <summary>
    /// Best-correlated offset (frames) of the input against the previous tail:
    /// a coarse pass every 4 frames, then a fine pass around the winner.
    /// </summary>
    private int SeekBestOverlap()
    {
        var best = 0;
        var bestScore = double.NegativeInfinity;
        for (var off = 0; off < _seekFrames; off += 4)
        {
            var s = Correlate(off);
            if (s > bestScore) { bestScore = s; best = off; }
        }
        var lo = Math.Max(0, best - 3);
        var hi = Math.Min(_seekFrames - 1, best + 3);
        for (var off = lo; off <= hi; off++)
        {
            if (off == best) continue;
            var s = Correlate(off);
            if (s > bestScore) { bestScore = s; best = off; }
        }
        return best;
    }

    private double Correlate(int offFrames)
    {
        var n = _overlapFrames * _ch;
        var baseIdx = offFrames * _ch;
        double corr = 0, norm = 0;
        for (var i = 0; i < n; i++)
        {
            var x = _in[baseIdx + i];
            corr += _mid[i] * x;
            norm += x * x;
        }
        return corr / Math.Sqrt(norm + 1e-9);
    }

    private void Reset()
    {
        _active = false;
        _inFrames = 0;
        _outSamples = 0;
        _outRead = 0;
        _skipFract = 0;
        Array.Clear(_mid);
    }
}
