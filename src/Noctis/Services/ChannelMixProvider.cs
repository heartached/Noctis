using System;
using System.Threading;
using NAudio.Wave;

namespace Noctis.Services;

/// <summary>
/// Mono Audio, Balance and the sleep timer's fade-out (Settings → Audio, Settings → Player).
/// One shared state object the UI writes and every render stage reads: the gapless engine's
/// sink (Windows and Linux) and the WASAPI callback sinks each run a
/// <see cref="ChannelMixProvider"/> over it. Writes swap an immutable snapshot, so the render
/// thread reads one consistent set of values with a single volatile load and never allocates.
/// </summary>
public sealed class ChannelMixSettings
{
    /// <summary>The state the player's render stages follow.</summary>
    public static ChannelMixSettings Shared { get; } = new();

    internal sealed record State(bool Mono, float Balance, float FadeGain, int FadeRampMs);

    private State _state = new(false, 0f, 1f, ChannelMixProvider.RampMs);

    internal State Current => Volatile.Read(ref _state);

    public bool Mono => Current.Mono;

    /// <summary>-1 (left only) … 0 (centre) … +1 (right only).</summary>
    public float Balance => Current.Balance;

    /// <summary>The playback fade gain the stages are heading to (1 = no fade).</summary>
    public float FadeGainTarget => Current.FadeGain;

    public void SetChannelMix(bool mono, double balance)
    {
        var b = double.IsFinite(balance) ? (float)Math.Clamp(balance, -1.0, 1.0) : 0f;
        var s = Current;
        if (s.Mono == mono && s.Balance == b) return;
        Volatile.Write(ref _state, s with { Mono = mono, Balance = b });
    }

    /// <summary>
    /// Sets the playback fade gain (0..1), reached linearly over <paramref name="rampMs"/>
    /// (never faster than <see cref="ChannelMixProvider.RampMs"/>, so a jump cannot click).
    /// A separate layer from the user's volume: the volume setting is never touched.
    /// </summary>
    public void SetFadeGain(double gain, int rampMs)
    {
        var g = double.IsFinite(gain) ? (float)Math.Clamp(gain, 0.0, 1.0) : 1f;
        var ramp = Math.Clamp(rampMs, ChannelMixProvider.RampMs, 60_000);
        var s = Current;
        if (s.FadeGain == g && s.FadeRampMs == ramp) return;
        Volatile.Write(ref _state, s with { FadeGain = g, FadeRampMs = ramp });
    }
}

/// <summary>
/// The render stage for <see cref="ChannelMixSettings"/>. Stereo in, stereo out:
/// <list type="bullet">
/// <item>Mono: both channels carry (L + R) / 2. Averaging, not summing, so the result never
/// exceeds the louder input sample and cannot clip.</item>
/// <item>Balance, a linear balance law (not a pan law): the side the slider points to stays
/// at unity, the other side scales linearly to silence at the end (balance +0.4 → left
/// ×0.6, right ×1). Centre leaves both channels at unity, so switching the setting on at
/// centre changes nothing, and no position ever boosts a channel.</item>
/// <item>The playback fade gain multiplies both channels.</item>
/// </list>
/// Every change ramps linearly (mono/balance over <see cref="RampMs"/>, the fade over its own
/// length), per frame, so toggling never clicks. At the identity state (stereo, centre, no
/// fade, no ramp running) Read returns the source samples untouched: the stage is bit-exact
/// when unused. <c>allowChannelMix: false</c> (Exclusive Mode) ignores mono/balance so the
/// output stays bit-perfect, and only the fade applies. Mono streams get the fade only. A
/// multichannel stream (Multichannel output, Windows' speaker order) applies mono and balance to
/// each left/right speaker pair (front, rear, side); centre, LFE and back centre get the fade only.
/// </summary>
public sealed class ChannelMixProvider : ISampleProvider
{
    /// <summary>Ramp for mono/balance changes, and the shortest fade-gain ramp.</summary>
    public const int RampMs = 20;

    private readonly ISampleProvider _source;
    private readonly ChannelMixSettings _settings;
    private readonly bool _allowChannelMix;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _mixRampFrames;
    // Left/right slot pairs mono and balance act on (flattened), and the remaining slots.
    private readonly int[] _pairs;
    private readonly int[] _others;

    // Render-thread state.
    private ChannelMixSettings.State? _seen;
    // Mix coefficients: mono amount (0 stereo … 1 mono), left gain, right gain.
    private float _m, _gl = 1f, _gr = 1f;
    private float _mFrom, _glFrom = 1f, _grFrom = 1f;
    private float _mTo, _glTo = 1f, _grTo = 1f;
    private int _mixPos, _mixLen; // _mixPos < _mixLen while ramping
    private float _f = 1f, _fFrom = 1f, _fTo = 1f;
    private int _fadePos, _fadeLen;

    public ChannelMixProvider(ISampleProvider source, ChannelMixSettings settings, bool allowChannelMix = true)
    {
        _source = source;
        _settings = settings;
        _allowChannelMix = allowChannelMix;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _sampleRate = Math.Max(1, source.WaveFormat.SampleRate);
        _mixRampFrames = Math.Max(1, _sampleRate * RampMs / 1000);
        (_pairs, _others) = SpeakerPairs(_channels);
    }

    /// <summary>Left/right pairs in Windows' speaker order (see <see cref="UpmixSampleProvider.LayoutFor"/>).</summary>
    internal static (int[] Pairs, int[] Others) SpeakerPairs(int channels)
    {
        if (channels == 2) return (new[] { 0, 1 }, Array.Empty<int>());
        if (channels < 2) return (Array.Empty<int>(), Array.Empty<int>());
        var layout = UpmixSampleProvider.LayoutFor(channels);
        var pairs = new List<int>();
        var paired = new bool[channels];
        foreach (var (l, r) in new[]
                 {
                     (UpmixSampleProvider.Role.FL, UpmixSampleProvider.Role.FR),
                     (UpmixSampleProvider.Role.BL, UpmixSampleProvider.Role.BR),
                     (UpmixSampleProvider.Role.SL, UpmixSampleProvider.Role.SR),
                 })
        {
            int li = Array.IndexOf(layout, l), ri = Array.IndexOf(layout, r);
            if (li < 0 || ri < 0) continue;
            pairs.Add(li);
            pairs.Add(ri);
            paired[li] = paired[ri] = true;
        }
        var others = new List<int>();
        for (var c = 0; c < channels; c++)
            if (!paired[c]) others.Add(c);
        return (pairs.ToArray(), others.ToArray());
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>True when Read passes samples through untouched (identity, no ramp running).</summary>
    public bool IsBypassed => _mixPos >= _mixLen && _fadePos >= _fadeLen && IsIdentity;

    private bool IsIdentity => _m == 0f && _gl == 1f && _gr == 1f && _f == 1f;

    /// <summary>
    /// Left/right gains for a balance position (-1 … +1) under the linear balance law above.
    /// </summary>
    public static (float Left, float Right) BalanceGains(double balance)
    {
        var b = double.IsFinite(balance) ? Math.Clamp(balance, -1.0, 1.0) : 0.0;
        return b >= 0 ? ((float)(1.0 - b), 1f) : (1f, (float)(1.0 + b));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var n = _source.Read(buffer, offset, count);
        Follow();
        if (n <= 0 || IsBypassed) return n;

        var ch = _channels;
        var frames = n / ch;
        var end = offset + frames * ch;
        if (_pairs.Length > 0 && (_mixPos < _mixLen || _m != 0f || _gl != 1f || _gr != 1f))
        {
            var pairs = _pairs;
            var others = _others;
            for (var i = offset; i < end; i += ch)
            {
                StepMix();
                var f = StepFade();
                for (var p = 0; p < pairs.Length; p += 2)
                {
                    var li = i + pairs[p];
                    var ri = i + pairs[p + 1];
                    var l = buffer[li];
                    var r = buffer[ri];
                    if (_m != 0f)
                    {
                        var mid = (l + r) * 0.5f;
                        l += _m * (mid - l);
                        r += _m * (mid - r);
                    }
                    buffer[li] = l * _gl * f;
                    buffer[ri] = r * _gr * f;
                }
                if (f != 1f)
                    for (var o = 0; o < others.Length; o++) buffer[i + others[o]] *= f;
            }
        }
        else
        {
            for (var i = offset; i < end; i += ch)
            {
                var f = StepFade();
                if (f == 1f) continue;
                for (var c = 0; c < ch; c++) buffer[i + c] *= f;
            }
        }
        return n;
    }

    // Picks up a new settings snapshot and starts ramps from wherever the current values are.
    private void Follow()
    {
        var s = _settings.Current;
        if (ReferenceEquals(s, _seen)) return;
        _seen = s;

        float m = 0f, gl = 1f, gr = 1f;
        if (_allowChannelMix && _pairs.Length > 0)
        {
            m = s.Mono ? 1f : 0f;
            (gl, gr) = BalanceGains(s.Balance);
        }
        if (m != _mTo || gl != _glTo || gr != _grTo)
        {
            _mFrom = _m; _glFrom = _gl; _grFrom = _gr;
            _mTo = m; _glTo = gl; _grTo = gr;
            _mixPos = 0;
            _mixLen = _mixRampFrames;
        }

        if (s.FadeGain != _fTo)
        {
            _fFrom = _f;
            _fTo = s.FadeGain;
            _fadePos = 0;
            _fadeLen = Math.Max(_mixRampFrames, (int)Math.Min(int.MaxValue, (long)_sampleRate * s.FadeRampMs / 1000));
        }
    }

    private void StepMix()
    {
        if (_mixPos >= _mixLen) return;
        _mixPos++;
        if (_mixPos >= _mixLen)
        {
            _m = _mTo; _gl = _glTo; _gr = _grTo;
            return;
        }
        var t = (float)_mixPos / _mixLen;
        _m = _mFrom + (_mTo - _mFrom) * t;
        _gl = _glFrom + (_glTo - _glFrom) * t;
        _gr = _grFrom + (_grTo - _grFrom) * t;
    }

    private float StepFade()
    {
        if (_fadePos >= _fadeLen) return _f;
        _fadePos++;
        _f = _fadePos >= _fadeLen ? _fTo : _fFrom + (_fTo - _fFrom) * ((float)_fadePos / _fadeLen);
        return _f;
    }
}
