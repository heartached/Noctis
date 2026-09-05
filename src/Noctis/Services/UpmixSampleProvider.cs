using NAudio.Wave;

namespace Noctis.Services;

/// <summary>How a stereo (or mono) stream is spread over a multi-channel speaker layout.</summary>
public enum UpmixMode
{
    /// <summary>Stereo stays stereo; extra speakers are silent (the OS mix format decides).</summary>
    Off,

    /// <summary>
    /// "Stereo everywhere": fronts as-is, the same pair on every rear/side pair, a −3 dB
    /// mono centre and a low-passed LFE. The MusicBee/Windows "speaker fill" behaviour.
    /// </summary>
    Duplicate,

    /// <summary>
    /// Matrix surround: fronts as-is, a −3 dB mono centre, rears carry the L−R ambience
    /// (what the Dolby Surround decoders extract), sides a quieter copy of the fronts,
    /// and a low-passed LFE.
    /// </summary>
    Surround,
}

/// <summary>
/// Expands a 1- or 2-channel float stream to the device's channel count in the
/// WAVEFORMATEXTENSIBLE order Windows uses for its speaker masks:
/// 2.1 = FL FR LFE · quad = FL FR BL BR · 5.0 = FL FR FC BL BR · 5.1 = FL FR FC LFE BL BR ·
/// 6.1 = FL FR FC LFE BC SL SR · 7.1 = FL FR FC LFE BL BR SL SR. Unknown slots stay silent.
/// The provider is a plain copy when the output has no more channels than the input.
/// </summary>
public sealed class UpmixSampleProvider : ISampleProvider
{
    internal enum Role { Silent, FL, FR, FC, LFE, BL, BR, SL, SR, BC }

    // −3 dB: a centred mono source keeps its perceived level across FL/FR/FC.
    private const float Minus3Db = 0.70710678f;

    private readonly ISampleProvider _source;
    private readonly int _inChannels;
    private readonly int _outChannels;
    private readonly UpmixMode _mode;
    private readonly Role[] _layout;
    private readonly Biquad _lfe;
    private float[] _scratch = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public UpmixMode Mode => _mode;

    public UpmixSampleProvider(ISampleProvider source, int outputChannels, UpmixMode mode)
    {
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("Upmix expects IEEE float input", nameof(source));
        _source = source;
        _inChannels = source.WaveFormat.Channels;
        if (_inChannels is not (1 or 2))
            throw new ArgumentException("Upmix expects mono or stereo input", nameof(source));
        _outChannels = Math.Clamp(outputChannels, 1, 32);
        _mode = mode;
        _layout = LayoutFor(_outChannels);
        // 2nd-order Butterworth low-pass at 120 Hz for the LFE feed.
        _lfe = Biquad.LowPass(source.WaveFormat.SampleRate, 120.0, 0.7071);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, _outChannels);
    }

    /// <summary>Speaker roles per slot for a channel count (Windows default masks).</summary>
    internal static Role[] LayoutFor(int channels) => channels switch
    {
        1 => new[] { Role.FC },
        2 => new[] { Role.FL, Role.FR },
        3 => new[] { Role.FL, Role.FR, Role.LFE },
        4 => new[] { Role.FL, Role.FR, Role.BL, Role.BR },
        5 => new[] { Role.FL, Role.FR, Role.FC, Role.BL, Role.BR },
        6 => new[] { Role.FL, Role.FR, Role.FC, Role.LFE, Role.BL, Role.BR },
        7 => new[] { Role.FL, Role.FR, Role.FC, Role.LFE, Role.BC, Role.SL, Role.SR },
        8 => new[] { Role.FL, Role.FR, Role.FC, Role.LFE, Role.BL, Role.BR, Role.SL, Role.SR },
        _ => Enumerable.Range(0, channels).Select(i => i switch { 0 => Role.FL, 1 => Role.FR, _ => Role.Silent }).ToArray(),
    };

    /// <summary>Slot index of a role name for tests/diagnostics (-1 when absent).</summary>
    internal static int SlotOf(int channels, string role) => Array.FindIndex(LayoutFor(channels), r => r.ToString() == role);

    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / _outChannels;
        if (frames <= 0) return 0;

        var need = frames * _inChannels;
        if (_scratch.Length < need) _scratch = new float[need];
        var read = _source.Read(_scratch, 0, need);
        var gotFrames = read / _inChannels;

        // Passthrough / plain copy when there is nothing to spread onto.
        if (_mode == UpmixMode.Off || _outChannels <= _inChannels)
        {
            for (var f = 0; f < gotFrames; f++)
            {
                var o = offset + f * _outChannels;
                var i = f * _inChannels;
                for (var c = 0; c < _outChannels; c++)
                    buffer[o + c] = c < _inChannels ? _scratch[i + c] : (_inChannels == 1 ? _scratch[i] : 0f);
            }
            return gotFrames * _outChannels;
        }

        var surround = _mode == UpmixMode.Surround;
        for (var f = 0; f < gotFrames; f++)
        {
            var i = f * _inChannels;
            var l = _scratch[i];
            var r = _inChannels == 2 ? _scratch[i + 1] : l;
            var mid = 0.5f * (l + r);
            var centre = mid * Minus3Db;
            var lfe = _lfe.Process(mid);
            var ambience = 0.5f * (l - r) * Minus3Db;

            var o = offset + f * _outChannels;
            for (var c = 0; c < _outChannels; c++)
            {
                buffer[o + c] = _layout[c] switch
                {
                    Role.FL => l,
                    Role.FR => r,
                    Role.FC => centre,
                    Role.LFE => lfe,
                    Role.BL => surround ? ambience : l * Minus3Db,
                    Role.BR => surround ? -ambience : r * Minus3Db,
                    Role.SL => surround ? l * 0.5f : l * Minus3Db,
                    Role.SR => surround ? r * 0.5f : r * Minus3Db,
                    Role.BC => surround ? 0f : centre,
                    _ => 0f,
                };
            }
        }
        return gotFrames * _outChannels;
    }

    /// <summary>Direct-form transposed biquad (mono).</summary>
    private sealed class Biquad
    {
        private readonly float _b0, _b1, _b2, _a1, _a2;
        private float _z1, _z2;

        private Biquad(float b0, float b1, float b2, float a1, float a2)
        {
            _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2;
        }

        public static Biquad LowPass(int sampleRate, double cutoffHz, double q)
        {
            var w0 = 2 * Math.PI * cutoffHz / sampleRate;
            var cos = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2 * q);
            var b0 = (1 - cos) / 2;
            var b1 = 1 - cos;
            var b2 = (1 - cos) / 2;
            var a0 = 1 + alpha;
            var a1 = -2 * cos;
            var a2 = 1 - alpha;
            return new Biquad((float)(b0 / a0), (float)(b1 / a0), (float)(b2 / a0), (float)(a1 / a0), (float)(a2 / a0));
        }

        public float Process(float x)
        {
            var y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}
