namespace Noctis.Plugins.Chords.Analysis;

/// <summary>In-place radix-2 complex FFT with precomputed twiddles and bit reversal for one size.</summary>
internal sealed class Fft
{
    private readonly int _n;
    private readonly int[] _reverse;
    private readonly double[] _cos;
    private readonly double[] _sin;

    public Fft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("FFT size must be a power of two.", nameof(n));
        _n = n;
        _reverse = new int[n];
        var bits = 0;
        while ((1 << bits) < n) bits++;
        for (var i = 0; i < n; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            _reverse[i] = r;
        }
        _cos = new double[n / 2];
        _sin = new double[n / 2];
        for (var i = 0; i < n / 2; i++)
        {
            _cos[i] = Math.Cos(-2 * Math.PI * i / n);
            _sin[i] = Math.Sin(-2 * Math.PI * i / n);
        }
    }

    public int Size => _n;

    public void Transform(double[] re, double[] im)
    {
        for (var i = 0; i < _n; i++)
        {
            var j = _reverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var size = 2; size <= _n; size <<= 1)
        {
            var half = size >> 1;
            var step = _n / size;
            for (var start = 0; start < _n; start += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = _cos[k * step];
                    var wi = _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = re[b] * wr - im[b] * wi;
                    var ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
