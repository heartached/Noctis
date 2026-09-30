using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Noctis.Services;

/// <summary>
/// An endless silent WAV (48 kHz stereo 16-bit) served to LibVLC from memory, the
/// source <see cref="VlcSilenceKeepAlive"/> plays. It never reaches end-of-stream, so
/// the keep-alive stream renders at real time like a long track instead of looping.
///
/// GitHub #70: the old source was a 1 s silence.wav with :input-repeat. A clip no
/// longer than the input's pts-delay (--file-caching, 1000 ms) never finishes VLC's
/// buffering phase, so the demuxer read it whole, hit EOF and repeated after one
/// INPUT_IDLE_SLEEP (100 ms): ten seeks a second, each one flushing and re-starting
/// the PulseAudio stream. On PipeWire every restart logged "starting late" plus
/// "playback way too late … flushing buffers", the lateness climbed to ~7 s and never
/// recovered (≈48 warnings/s, the session log full in two minutes). An input that
/// never ends is never repeated, sought or flushed.
///
/// The header declares a data size of 0 ("unknown length"): libavformat's wav demuxer
/// then reads to EOF (wavdec.c data_end = INT64_MAX) and VLC's own wav demuxer only
/// honours a data size when the stream size is known, which this one never reports.
/// <see cref="MediaInput.CanSeek"/> is true on purpose: LibVLC's imem access reports
/// pace control only when a seek callback exists, and without it the input thread
/// would demux as fast as it can read instead of following its clock. Any offset
/// "seeks": past the header every byte is zero.
/// </summary>
internal sealed class EndlessSilenceInput : MediaInput
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private static readonly byte[] Header = BuildHeader();
    private readonly byte[] _scratch = new byte[64 * 1024];
    private long _position;

    public EndlessSilenceInput()
    {
        CanSeek = true;
    }

    public override bool Open(out ulong size)
    {
        _position = 0;
        size = ulong.MaxValue; // unknown: the stream never ends
        return true;
    }

    public override int Read(IntPtr buf, uint len)
    {
        var count = Produce(_position, _scratch, (int)Math.Min(len, (uint)_scratch.Length));
        Marshal.Copy(_scratch, 0, buf, count);
        _position += count;
        return count;
    }

    public override bool Seek(ulong offset)
    {
        _position = (long)Math.Min(offset, long.MaxValue);
        return true;
    }

    public override void Close() { }

    /// <summary>
    /// Fills <paramref name="dest"/>[0..<paramref name="count"/>) with the stream bytes
    /// found at <paramref name="position"/>: the header's bytes, then silence forever.
    /// Returns <paramref name="count"/> (always &gt; 0 for count &gt; 0 — 0 would mean
    /// end-of-stream to LibVLC). Pure; internal for tests.
    /// </summary>
    internal static int Produce(long position, byte[] dest, int count)
    {
        Array.Clear(dest, 0, count);
        if (position < Header.Length)
        {
            var n = (int)Math.Min(Header.Length - position, count);
            Array.Copy(Header, position, dest, 0, n);
        }
        return count;
    }

    internal static byte[] HeaderBytes() => (byte[])Header.Clone();

    private static byte[] BuildHeader()
    {
        using var ms = new MemoryStream();
        SilentWavFile.Write(ms, seconds: 0, SampleRate, Channels); // data size 0 = unknown length
        return ms.ToArray();
    }
}
