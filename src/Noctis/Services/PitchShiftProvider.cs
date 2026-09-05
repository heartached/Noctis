using NAudio.Wave;

namespace Noctis.Services;

/// <summary>
/// Pitch shift by variable-ratio resampling: reading <c>ratio</c> input frames per output
/// frame raises the pitch by <c>ratio</c> (and speeds the media up by the same factor; the
/// WSOLA stage downstream restores the tempo — see <see cref="GaplessSpliceProvider"/>).
/// 4-point Hermite interpolation; a pure pass-through at ratio 1 so the gapless seam stays
/// bit-exact when no shift is requested. Render thread only.
/// </summary>
public sealed class PitchShiftProvider : ISampleProvider
{
    public const double MinRatio = 0.5;   // −12 semitones
    public const double MaxRatio = 2.0;   // +12 semitones

    private readonly ISampleProvider _source;
    private readonly Func<double> _ratio;
    private readonly int _channels;

    // Input frames buffered ahead of the read position. Frame 0 is one frame of
    // history so the interpolator always has x[i-1]; _pos is the fractional read
    // position in frames relative to that buffer.
    private float[] _in = new float[4096];
    private int _inFrames;
    private double _pos = 1.0;
    private bool _ended;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public PitchShiftProvider(ISampleProvider source, Func<double> ratio)
    {
        _source = source;
        _ratio = ratio;
        _channels = source.WaveFormat.Channels;
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("PitchShiftProvider expects IEEE float input", nameof(source));
    }

    /// <summary>2^(semitones/12), clamped to the supported range; non-finite → 1.</summary>
    public static double RatioFromSemitones(double semitones) =>
        double.IsFinite(semitones) ? Math.Clamp(Math.Pow(2.0, Math.Clamp(semitones, -12, 12) / 12.0), MinRatio, MaxRatio) : 1.0;

    public static double ClampRatio(double ratio) =>
        double.IsFinite(ratio) ? Math.Clamp(ratio, MinRatio, MaxRatio) : 1.0;

    public int Read(float[] buffer, int offset, int count)
    {
        var ratio = ClampRatio(_ratio());
        var frames = count / _channels;
        if (frames <= 0) return 0;

        if (Math.Abs(ratio - 1.0) < 1e-9)
        {
            // Unity: hand back any buffered frames verbatim, then stream straight through.
            var produced = DrainBufferedAtUnity(buffer, offset, frames);
            if (produced < frames && !_ended)
            {
                var n = _source.Read(buffer, offset + produced * _channels, (frames - produced) * _channels);
                produced += n / _channels;
            }
            return produced * _channels;
        }

        var outFrames = 0;
        while (outFrames < frames)
        {
            var i = (int)Math.Floor(_pos);
            // Need frames i-1 .. i+2 present.
            if (i + 2 >= _inFrames)
            {
                if (_ended || !Fill(i + 3 - _inFrames + 512)) break;
                continue;
            }

            var t = (float)(_pos - i);
            var o = offset + outFrames * _channels;
            var b = (i - 1) * _channels;
            for (var c = 0; c < _channels; c++)
            {
                var x0 = _in[b + c];
                var x1 = _in[b + _channels + c];
                var x2 = _in[b + 2 * _channels + c];
                var x3 = _in[b + 3 * _channels + c];
                buffer[o + c] = Hermite(x0, x1, x2, x3, t);
            }
            _pos += ratio;
            outFrames++;
        }

        Compact();
        return outFrames * _channels;
    }

    private int DrainBufferedAtUnity(float[] buffer, int offset, int frames)
    {
        var i = (int)Math.Round(_pos);
        var available = Math.Max(0, _inFrames - i);
        var take = Math.Min(frames, available);
        if (take > 0)
            Array.Copy(_in, i * _channels, buffer, offset, take * _channels);
        // Whatever remains stays buffered; reset position bookkeeping for the next shift.
        var rest = available - take;
        if (rest > 0)
        {
            Array.Copy(_in, (i + take - 1) * _channels, _in, 0, (rest + 1) * _channels);
            _inFrames = rest + 1;
        }
        else
        {
            _inFrames = 0;
        }
        _pos = 1.0;
        return take;
    }

    /// <summary>Reads at least <paramref name="wantFrames"/> more frames from the source; false at end of stream.</summary>
    private bool Fill(int wantFrames)
    {
        if (_inFrames == 0)
        {
            // Seed one frame of silent history so x[i-1] exists for the first output.
            EnsureCapacity(1 + wantFrames);
            Array.Clear(_in, 0, _channels);
            _inFrames = 1;
            _pos = 1.0;
        }
        EnsureCapacity(_inFrames + wantFrames);
        var got = _source.Read(_in, _inFrames * _channels, wantFrames * _channels);
        if (got <= 0)
        {
            _ended = true;
            return false;
        }
        _inFrames += got / _channels;
        return true;
    }

    private void EnsureCapacity(int frames)
    {
        var need = frames * _channels;
        if (_in.Length >= need) return;
        var grown = new float[Math.Max(need, _in.Length * 2)];
        Array.Copy(_in, grown, _inFrames * _channels);
        _in = grown;
    }

    /// <summary>Drops consumed frames, keeping one frame of history before the read position.</summary>
    private void Compact()
    {
        var keepFrom = (int)Math.Floor(_pos) - 1;
        if (keepFrom <= 0) return;
        var remaining = _inFrames - keepFrom;
        if (remaining > 0)
            Array.Copy(_in, keepFrom * _channels, _in, 0, remaining * _channels);
        _inFrames = Math.Max(0, remaining);
        _pos -= keepFrom;
    }

    private static float Hermite(float x0, float x1, float x2, float x3, float t)
    {
        var c0 = x1;
        var c1 = 0.5f * (x2 - x0);
        var c2 = x0 - 2.5f * x1 + 2f * x2 - 0.5f * x3;
        var c3 = 0.5f * (x3 - x0) + 1.5f * (x1 - x2);
        return ((c3 * t + c2) * t + c1) * t + c0;
    }
}
