using System.Runtime.InteropServices;
using Android.Runtime;
using AndroidX.Media3.Common;
using AndroidX.Media3.Common.Audio;
using Java.Nio;
using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>
/// ReplayGain inside Media3's audio sink: every PCM buffer, after decoding and before the AudioTrack,
/// goes through <see cref="ReplayGainStage"/> on ExoPlayer's playback thread. Why here and not the
/// player's volume: see <see cref="ReplayGainStage"/> (boosts past 1, and the exact first sample of
/// a gapless song).
///
/// Which song a buffer belongs to: the renderers reconfigure the sink at every new stream with the
/// stream's timeline and period (MediaCodecAudioRenderer.onOutputFormatChanged, which
/// MediaCodecRenderer re-raises after each stream change; the FFmpeg renderer the same), and
/// DefaultAudioSink applies that configuration once the previous stream has drained, flushing its
/// processors with a StreamMetadata naming the period (Media3 1.11; read from the bytecode). The
/// period's window holds our MediaItem, whose id is the library path the levels are kept by.
/// Not every flush carries that: DefaultAudioSink.reset() (each time the player is re-prepared, a
/// tapped song or Next) runs AudioProcessingPipeline.reset(), which flushes every processor with
/// StreamMetadata.DEFAULT (no timeline, no period), and BaseAudioProcessor.reset() flushes again the
/// same way; the stream's own flush follows before its first buffer. A flush without a period makes
/// the stage follow the player's transition (<see cref="ReplayGainStage.Announce"/>) until a flush
/// names the stream. Which of the two a song started on is logged at its first buffer, once per
/// stream start, with the level applied: the reset flushes in between print nothing.
///
/// 16-bit (Media3's default) and float PCM; anything else passes untouched (the processor reports
/// itself inactive). A buffer at unity is copied natively; otherwise its samples go through scratch
/// arrays that grow once and are reused, so a buffer allocates nothing managed.
/// </summary>
internal sealed class ReplayGainAudioProcessor : BaseAudioProcessor
{
    private readonly ReplayGainStage _stage;
    private int _encoding;
    private float[] _floats = Array.Empty<float>();
    private short[] _shorts = Array.Empty<short>();
    // The stream start not logged yet (set by a flush, logged at the first buffer after it).
    private string? _pendingStartPath;

    public ReplayGainAudioProcessor(ReplayGainStage stage)
    {
        _stage = stage;
    }

    protected override AudioProcessorAudioFormat? OnConfigure(AudioProcessorAudioFormat? inputAudioFormat)
    {
        if (inputAudioFormat != null && inputAudioFormat.Encoding is C.EncodingPcm16bit or C.EncodingPcmFloat)
            return inputAudioFormat;
        DebugLog.WriteOnce("Audio", "ReplayGain.Format", $"ReplayGain passes over PCM encoding {inputAudioFormat?.Encoding}");
        return AudioProcessorAudioFormat.NotSet;
    }

    /// <summary>A new stream (a song starting, gapless or not), a seek or a new format: the format the
    /// configure step chose is in force from here, and so is the level of the song the stream is.</summary>
    protected override void OnFlush(AudioProcessorStreamMetadata? streamMetadata)
    {
        var format = InputAudioFormat;
        if (format != null)
        {
            _encoding = format.Encoding;
            _stage.Configure(format.SampleRate, format.ChannelCount);
        }
        var mediaId = MediaIdOf(streamMetadata, out var named);
        _stage.BeginStream(mediaId);
        _pendingStartPath = mediaId != null
            ? "exact: the sink named the song at its first sample"
            : named
                ? "fallback: the sink's stream had no readable song, following the player"
                : "fallback: the sink named no stream, following the player";
    }

    /// <summary>"ReplayGain: start 'Song' -7.84 dB, exact: the sink named the song at its first sample".
    /// Playback thread, at the first buffer after a flush.</summary>
    private void LogStreamStart()
    {
        var path = _pendingStartPath;
        _pendingStartPath = null;
        if (path == null) return;
        DebugLog.Write("Audio", $"ReplayGain: start '{_stage.LabelFor(_stage.MediaId)}' {Db(_stage.TargetGain)}, {path}");
    }

    private static string Db(float gain) =>
        gain == 1f ? "0.00 dB" : (20 * Math.Log10(gain)).ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture) + " dB";

    /// <summary>The library path of the item the stream belongs to: its period's window's MediaItem id.
    /// Null when the flush did not say (<paramref name="named"/> false: no timeline or period, as
    /// StreamMetadata.DEFAULT from a reset) or the period's item could not be read (true).
    /// Once per flush, never per buffer.</summary>
    private static string? MediaIdOf(AudioProcessorStreamMetadata? metadata, out bool named)
    {
        named = false;
        try
        {
            var timeline = metadata?.Timeline;
            var uid = metadata?.PeriodUid;
            if (timeline == null || uid == null || timeline.IsEmpty) return null;
            named = true;
            using var period = timeline.GetPeriodByUid(uid, new Timeline.Period());
            if (period == null) return null;
            using var window = timeline.GetWindow(period.WindowIndex, new Timeline.Window());
            return window?.MediaItem?.MediaId;
        }
        catch (Exception ex)
        {
            DebugLog.WriteOnce("Audio", "ReplayGain.Period", $"ReplayGain could not read the stream's item: {ex.GetType().Name}");
            return null;
        }
    }

    public override void QueueInput(ByteBuffer? inputBuffer)
    {
        if (inputBuffer == null) return;
        var bytes = inputBuffer.Remaining();
        if (bytes <= 0) return;
        var output = ReplaceOutputBuffer(bytes)!;
        var unity = !_stage.Update();
        if (_pendingStartPath != null) LogStreamStart();
        if (unity)
        {
            output.Put(inputBuffer);
            output.Flip();
            return;
        }
        var width = _encoding == C.EncodingPcmFloat ? 4 : 2;
        var count = bytes / width;
        var input = JNIEnv.GetDirectBufferAddress(inputBuffer.Handle);
        var target = JNIEnv.GetDirectBufferAddress(output.Handle);
        if (input == IntPtr.Zero || target == IntPtr.Zero || count * width != bytes)
        {
            // Not seen from Media3 (direct buffers, whole samples): pass it as it is rather than guess.
            output.Put(inputBuffer);
            output.Flip();
            return;
        }
        input += inputBuffer.Position();
        if (width == 4)
        {
            if (_floats.Length < count) _floats = new float[count];
            Marshal.Copy(input, _floats, 0, count);
            _stage.Process(_floats.AsSpan(0, count));
            Marshal.Copy(_floats, 0, target, count);
        }
        else
        {
            if (_shorts.Length < count) _shorts = new short[count];
            Marshal.Copy(input, _shorts, 0, count);
            _stage.Process(_shorts.AsSpan(0, count));
            Marshal.Copy(_shorts, 0, target, count);
        }
        inputBuffer.Position(inputBuffer.Limit());
        output.Limit(bytes);
    }
}
