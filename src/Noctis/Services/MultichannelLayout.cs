using NAudio.Wave;

namespace Noctis.Services;

/// <summary>
/// Settings → Audio → Multichannel output (GitHub #200): 4–8 channel playback on a surround
/// device instead of LibVLC's stereo downmix.
///
/// LibVLC 3's amem output (the audio callbacks every Noctis sink is fed from) hands over
/// samples in VLC's INTERNAL channel order and never reorders them: amem.c Start() only sets
/// <c>i_physical_channels</c> from the pinned channel count (4 = AOUT_CHANS_4_0, 5 = 5_0,
/// 6 = 5_1, 7 = L R C RC ML MR LFE, 8 = 7_1) and calls no aout_CheckChannelReorder. VLC's
/// internal order is pi_vlc_chan_order_wg4 (L R ML MR RL RR RC C LFE), Windows' is the
/// WAVEFORMATEXTENSIBLE bit order (FL FR FC LFE BL BR … SL SR), so 5.1 arrives as
/// L R RL RR C LFE and must be shuffled to FL FR FC LFE BL BR before WASAPI sees it.
/// Measured against the bundled LibVLC 3.0.23 (one sine per channel through amem,
/// D:/NoctisWT/art-1108/200/probe): exactly the orders below, a stereo source pinned to N
/// channels comes out as FL/FR with the rest silent, and a 5.1(side) source lands its side
/// pair in the rear slots.
/// </summary>
public static class MultichannelLayout
{
    /// <summary>Settings → Audio → Multichannel output as saved when Noctis started. Program.Main
    /// sets it before the player is built; the engine's channel count is fixed for the session.</summary>
    public static bool EnabledAtLaunch { get; set; }

    /// <summary>Channel counts multichannel output renders discretely (quad … 7.1). 3 (2.1) is left
    /// out: VLC's 2.1 has no centre, so a 3-channel FLAC (L R C) loses its centre there.</summary>
    public static bool IsSupported(int channels) => channels is >= 4 and <= 8;

    // For each WAVEFORMATEXTENSIBLE slot, the amem slot it comes from.
    private static readonly int[][] s_waveFromAmem =
    {
        Array.Empty<int>(), new[] { 0 }, new[] { 0, 1 }, new[] { 0, 1, 2 },
        new[] { 0, 1, 2, 3 },                // quad: L R RL RR       -> FL FR BL BR
        new[] { 0, 1, 4, 2, 3 },             // 5.0:  L R RL RR C     -> FL FR FC BL BR
        new[] { 0, 1, 4, 5, 2, 3 },          // 5.1:  L R RL RR C LFE -> FL FR FC LFE BL BR
        new[] { 0, 1, 5, 6, 4, 2, 3 },       // 6.1:  L R ML MR RC C LFE -> FL FR FC LFE BC SL SR
        new[] { 0, 1, 6, 7, 4, 5, 2, 3 },    // 7.1:  L R ML MR RL RR C LFE -> FL FR FC LFE BL BR SL SR
    };

    /// <summary>The amem slot each output slot is read from (identity up to 4 channels).</summary>
    internal static int[] WaveFromAmem(int channels) =>
        channels >= 0 && channels < s_waveFromAmem.Length ? s_waveFromAmem[channels] : Enumerable.Range(0, channels).ToArray();

    /// <summary>True when amem's order differs from Windows' for this channel count.</summary>
    public static bool NeedsReorder(int channels) => channels is >= 5 and <= 8;

    /// <summary>Reorders interleaved amem S16 frames into WAVEFORMATEXTENSIBLE order, in place.</summary>
    public static void ToWaveOrder(Span<short> interleaved, int channels)
    {
        if (!NeedsReorder(channels)) return;
        var map = s_waveFromAmem[channels];
        Span<short> frame = stackalloc short[8];
        for (var i = 0; i + channels <= interleaved.Length; i += channels)
        {
            var src = interleaved.Slice(i, channels);
            src.CopyTo(frame);
            for (var c = 0; c < channels; c++) src[c] = frame[map[c]];
        }
    }

    /// <summary>Windows' default speaker mask for the order above (KSAUDIO_SPEAKER_*).</summary>
    public static int StandardMask(int channels) => channels switch
    {
        1 => 0x4,     // FC
        2 => 0x3,     // FL FR
        4 => 0x33,    // FL FR BL BR
        5 => 0x37,    // FL FR FC BL BR
        6 => 0x3F,    // FL FR FC LFE BL BR
        7 => 0x70F,   // FL FR FC LFE BC SL SR
        8 => 0x63F,   // FL FR FC LFE BL BR SL SR
        _ => 0,
    };

    /// <summary>
    /// The mask to describe our data with on a device whose mix format has <paramref name="deviceMask"/>.
    /// 5.1 has two equally valid Windows layouts (rears 0x3F, sides 0x60F) and VLC's 5.1 fills the
    /// same slots either way, so the device's own one is kept there and no remix is needed; any
    /// other mismatch keeps the standard mask and shared mode's AUTOCONVERTPCM remaps it.
    /// </summary>
    public static int MaskFor(int channels, int deviceMask) =>
        channels == 6 && deviceMask == 0x60F ? 0x60F : StandardMask(channels);

    /// <summary>The speaker mask of a WASAPI mix format (0 when it is not WAVEFORMATEXTENSIBLE).</summary>
    public static int MaskOf(WaveFormat format)
    {
        if (format is not WaveFormatExtensible ext) return 0;
        var field = typeof(WaveFormatExtensible).GetField("dwChannelMask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(ext) is int mask ? mask : 0;
    }

    /// <summary>
    /// The channel count a shared-mode sink renders at: the device's own when multichannel output is
    /// on, the device has a supported surround layout and Headphone surround (which needs a stereo
    /// output to binauralize into) is off; otherwise stereo (mono for a mono device when allowed).
    /// </summary>
    public static int SharedChannels(int deviceChannels, bool multichannel, bool headphoneSurround, bool allowMono = true)
    {
        if (multichannel && !headphoneSurround && IsSupported(deviceChannels)) return deviceChannels;
        return deviceChannels >= 2 || !allowMono ? 2 : 1;
    }

    /// <summary>
    /// Float samples presented to WASAPI as WAVEFORMATEXTENSIBLE with an explicit speaker mask
    /// (32-bit float, or 24/16-bit PCM for exclusive mode): a plain WAVEFORMATEX above two
    /// channels leaves the speaker mapping to the driver. Integer conversion matches NAudio's
    /// SampleToWaveProvider16/24 (clip to +/-1, scale by 32767 / 8388607), so the stereo and
    /// multichannel paths render identical samples.
    /// </summary>
    public sealed class MaskedWaveProvider : IWaveProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _bytesPerSample;
        private float[] _scratch = Array.Empty<float>();

        public MaskedWaveProvider(ISampleProvider source, int bits, int channelMask)
        {
            if (bits is not (16 or 24 or 32)) throw new ArgumentOutOfRangeException(nameof(bits));
            _source = source;
            _bytesPerSample = bits / 8;
            WaveFormat = new WaveFormatExtensible(source.WaveFormat.SampleRate, bits, source.WaveFormat.Channels, channelMask);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var samples = count / _bytesPerSample;
            if (_scratch.Length < samples) _scratch = new float[samples];
            var read = _source.Read(_scratch, 0, samples);
            switch (_bytesPerSample)
            {
                case 4:
                    Buffer.BlockCopy(_scratch, 0, buffer, offset, read * 4);
                    break;
                case 3:
                    for (int i = 0, o = offset; i < read; i++, o += 3)
                    {
                        var v = (int)(Math.Clamp(_scratch[i], -1f, 1f) * 8388607.0);
                        buffer[o] = (byte)v;
                        buffer[o + 1] = (byte)(v >> 8);
                        buffer[o + 2] = (byte)(v >> 16);
                    }
                    break;
                default:
                    for (int i = 0, o = offset; i < read; i++, o += 2)
                    {
                        var v = (short)(Math.Clamp(_scratch[i], -1f, 1f) * 32767);
                        buffer[o] = (byte)v;
                        buffer[o + 1] = (byte)(v >> 8);
                    }
                    break;
            }
            return read * _bytesPerSample;
        }
    }
}
