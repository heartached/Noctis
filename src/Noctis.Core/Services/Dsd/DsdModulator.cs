namespace Noctis.Services.Dsd;

/// <summary>
/// One channel of PCM → DSD: a linear ×N interpolator into a 5th-order 1-bit sigma-delta
/// modulator (error-feedback form, three cascaded sections).
///
/// Noise transfer function: zeros spread over 0–20 kHz at the roots of the 5th Legendre
/// polynomial (the spread that minimises in-band noise power), poles of a Butterworth
/// high-pass placed so the NTF's out-of-band gain peaks at 1.5 (Lee's stability rule for
/// 1-bit loops). Designed with scipy (butter + bisection on the cut-off for ‖NTF‖∞ = 1.5)
/// at 2.8224 MHz; the poles move by less than 3e-4 at 5.6448 MHz, so only the zeros are
/// recomputed per rate. In-band noise floor at DSD64: −110 dB at 1–10 kHz, −90 dB at 20 kHz.
///
/// Level: PCM full scale is written as 50 % modulation (SACD's 0 dB reference), the input
/// clipped at 55 %; above roughly 60 % a 5th-order loop goes unstable. If the loop state
/// still runs away (|quantiser input| &gt; 8, never seen in the 0.55 tests) it is reset
/// and <see cref="Resets"/> counts it.
/// </summary>
internal sealed class DsdModulator
{
    public const double Scale = 0.5;
    public const double Limit = 0.55;

    // Poles (denominator of each section, a1 / a2).
    private const double A1 = -0.7766457073458661;
    private const double B1 = -1.6144685961577883, B2 = 0.666320566880254;
    private const double C1 = -1.8000795016992561, C2 = 0.8578927473965408;
    // Legendre P5 roots > 0 (the 0 root is the first-order section's DC zero).
    private const double R1 = 0.5384693101056831, R2 = 0.9061798459386640;

    private readonly double _zb, _zc; // −2·cos(zero angle) of sections B and C
    private double _sa, _sb1, _sb2, _sc1, _sc2;
    private double _prev;
    private uint _rng = 0x9E3779B9;

    public long Resets { get; private set; }

    public DsdModulator(int dsdRate)
    {
        var wb = 2 * Math.PI * 20000.0 / dsdRate;
        _zb = -2 * Math.Cos(R1 * wb);
        _zc = -2 * Math.Cos(R2 * wb);
    }

    /// <summary>
    /// Modulates <paramref name="pcm"/> (one channel, full scale ±1) upsampled by
    /// <paramref name="up"/> (a multiple of 8) into <paramref name="dst"/>, 8 samples per
    /// byte, first sample in bit 0 (DSF order). <paramref name="dst"/> must hold
    /// <c>pcm.Length × up / 8</c> bytes.
    /// </summary>
    public void Process(ReadOnlySpan<float> pcm, int up, Span<byte> dst)
    {
        if (up % 8 != 0 || up <= 0) throw new ArgumentOutOfRangeException(nameof(up));
        if (dst.Length < pcm.Length * up / 8) throw new ArgumentException("destination too small", nameof(dst));

        double sa = _sa, sb1 = _sb1, sb2 = _sb2, sc1 = _sc1, sc2 = _sc2, prev = _prev;
        double zb = _zb, zc = _zc;
        var rng = _rng;
        var inv = 1.0 / up;
        var o = 0;
        for (var i = 0; i < pcm.Length; i++)
        {
            var target = Math.Clamp(pcm[i] * Scale, -Limit, Limit);
            if (double.IsNaN(target)) target = 0;
            var step = (target - prev) * inv;
            var x = prev;
            for (var j = 0; j < up; j += 8)
            {
                var acc = 0;
                for (var bit = 0; bit < 8; bit++)
                {
                    x += step;
                    // Tiny shaped dither breaks up idle tones in silence (−80 dB before shaping).
                    rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                    var v = x + sa + sb1 + sc1 + (int)rng * 4.656612873077393e-14;
                    double q;
                    if (v >= 0) { q = 1; acc |= 1 << bit; }
                    else q = -1;
                    var e = q - v;
                    // Section A: zero at DC, real pole.
                    var oa = e + sa;
                    sa = -e - A1 * oa;
                    // Section B / C: complex zero pair on the unit circle, complex pole pair.
                    var ob = oa + sb1;
                    sb1 = zb * oa - B1 * ob + sb2;
                    sb2 = oa - B2 * ob;
                    var oc = ob + sc1;
                    sc1 = zc * ob - C1 * oc + sc2;
                    sc2 = ob - C2 * oc;
                    if (Math.Abs(v) > 8)
                    {
                        sa = sb1 = sb2 = sc1 = sc2 = 0;
                        Resets++;
                    }
                }
                dst[o++] = (byte)acc;
            }
            prev = target;
        }
        _sa = sa; _sb1 = sb1; _sb2 = sb2; _sc1 = sc1; _sc2 = sc2; _prev = prev;
        _rng = rng;
    }
}
