using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Noctis.Services;

/// <summary>
/// A local AAC/MP4, Ogg Opus or Ogg Vorbis file served to LibVLC with its encoder trims removed (see
/// <see cref="ContainerGaplessInfo"/>): the file's own bytes, with the few patched bytes laid
/// over them, so LibVLC decodes the priming packet and the gapless engine trims the head and
/// the tail itself. Only opened while the engine renders (nothing else would trim).
/// </summary>
internal sealed class ContainerTrimInput : MediaInput
{
    private readonly string _path;
    private readonly IReadOnlyList<(long Offset, byte[] Bytes)> _patches;
    private readonly object _gate = new();
    private FileStream? _stream;
    private byte[] _buffer = Array.Empty<byte>();
    private long _position;
    private int _opens;
    private bool _released;

    public ContainerGaplessInfo Info { get; }

    private ContainerTrimInput(string path, ContainerGaplessInfo info)
    {
        _path = path;
        _patches = info.Patches;
        Info = info;
        CanSeek = true;
    }

    /// <summary>
    /// Media for a local file that carries encoder trims LibVLC would mishandle, or null
    /// (no such trims, not a candidate, unreadable): the caller opens it by path as before.
    /// </summary>
    public static Media? TryCreateMedia(LibVLC libVlc, string path)
    {
        if (!ContainerGaplessInfo.IsCandidatePath(path) || !ContainerGaplessInfo.TryRead(path, out var info) || info == null)
            return null;
        DebugLogger.Info(DebugLogger.Category.Playback, "Gapless.TrimOpen",
            $"file={Path.GetFileName(path)}, kind={info.Kind}, timescale={info.Timescale}, lead={info.LeadSamples}, tail={info.TailSamples}, patches={info.Patches.Count}");
        return new TrimMedia(libVlc, new ContainerTrimInput(path, info));
    }

    /// <summary>True for media made by <see cref="TryCreateMedia"/>: like DSD media, it reads
    /// through callbacks, which LibVLC 3 never pre-parses.</summary>
    public static bool IsTrimMedia(Media? media) => media is TrimMedia;

    /// <summary>The trims the engine applies for this media at <paramref name="outputSampleRate"/> (0, 0 for other media).</summary>
    public static (int Lead, int Tail) TrimFrames(Media? media, int outputSampleRate) =>
        media is TrimMedia t ? (t.Input.Info.LeadFrames(outputSampleRate), t.Input.Info.TailFrames(outputSampleRate)) : (0, 0);

    /// <summary>The channel count read from the container (what a parse would have reported).</summary>
    public static bool TryGetChannels(Media? media, out int channels)
    {
        channels = media is TrimMedia t ? t.Input.Info.Channels : 0;
        return channels > 0;
    }

    public override bool Open(out ulong size)
    {
        lock (_gate)
        {
            size = 0;
            try
            {
                _stream ??= new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
                size = (ulong)_stream.Length;
                _opens++;
                _position = 0;
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "Gapless.TrimOpenFailed", ex.Message);
                return false;
            }
        }
    }

    public override int Read(IntPtr buf, uint len)
    {
        lock (_gate)
        {
            if (_stream == null) return -1;
            var want = (int)Math.Min(len, 1u << 20);
            if (_buffer.Length < want) _buffer = new byte[want];
            try
            {
                _stream.Position = _position;
                var n = _stream.Read(_buffer, 0, want);
                if (n <= 0) return 0;
                Overlay(_patches, _position, _buffer.AsSpan(0, n));
                Marshal.Copy(_buffer, 0, buf, n);
                _position += n;
                return n;
            }
            catch (IOException ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "Gapless.TrimReadFailed", ex.Message);
                return -1;
            }
        }
    }

    public override bool Seek(ulong offset)
    {
        lock (_gate)
        {
            _position = (long)offset;
            return true;
        }
    }

    public override void Close()
    {
        lock (_gate)
        {
            if (_opens > 0) _opens--;
            if (_opens == 0)
            {
                _stream?.Dispose();
                _stream = null;
                _buffer = Array.Empty<byte>();
            }
            if (_released && _opens == 0) Dispose();
        }
    }

    internal void Release()
    {
        lock (_gate)
        {
            _released = true;
            if (_opens == 0) Dispose();
        }
    }

    /// <summary>Lays the patched bytes over <paramref name="data"/>, the file's bytes from
    /// <paramref name="position"/> on. Pure; internal for tests.</summary>
    internal static void Overlay(IReadOnlyList<(long Offset, byte[] Bytes)> patches, long position, Span<byte> data)
    {
        foreach (var (offset, bytes) in patches)
        {
            var from = Math.Max(offset, position);
            var to = Math.Min(offset + bytes.Length, position + data.Length);
            for (var i = from; i < to; i++)
                data[(int)(i - position)] = bytes[i - offset];
        }
    }

    private sealed class TrimMedia : Media
    {
        public TrimMedia(LibVLC libVlc, ContainerTrimInput input) : base(libVlc, input) => Input = input;

        public ContainerTrimInput Input { get; }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) Input.Release();
        }
    }
}
