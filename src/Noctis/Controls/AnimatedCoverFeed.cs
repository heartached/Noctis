using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Noctis.Services;

namespace Noctis.Controls;

/// <summary>
/// One looping decoder per animated-cover file, shared by every
/// <see cref="AnimatedCoverImage"/> showing that file (lyrics page, mini player, Cover Flow,
/// album page, …). Frames land in a single <see cref="WriteableBitmap"/> that all of them
/// display. Each consumer used to run its own LibVLC player: a 2160×2160 HEVC cover costs
/// ~0.5–0.75 GB and about one CPU core per software decoder, so five hosts of the same
/// clip cost five times that for one picture.
///
/// All LibVLC calls (first-use core initialization, Play, Stop, Dispose) run on worker
/// threads: they block for hundreds of milliseconds and froze the UI when a cover was
/// applied or replaced. Registry, leases and frame cache are UI-thread only.
/// </summary>
internal sealed class AnimatedCoverFeed
{
    // VLC scales every frame into a square RV32 (BGRA in memory) buffer of this size.
    // Animated covers are square; a non-square source gets scaled to fit this square.
    // Not sized per host: measured, a 400 px buffer cost the same RAM and CPU as 600 px —
    // decoding the 1080p/2160p source is the whole bill, not the scaled output.
    private const int RenderSize = 600;
    private const int Stride = RenderSize * 4;
    private const int BufferBytes = Stride * RenderSize;

    // Decoder threads per cover. LibVLC's default scales with the core count, and every
    // frame thread holds its own set of full-size pictures: a 2160×2160 HEVC cover
    // measured +750 MB at the default on a 24-thread CPU, +522 MB at 4 threads, with the
    // same ~1 core of CPU and full frame rate. 4 still leaves ~4x decode headroom there.
    private const string DecoderThreads = ":avcodec-threads=4";

    // A consumer that stops being visible releases its lease, but the decoder outlives
    // the last lease by this long so a hand-off (main window hidden as the mini player
    // opens, a form switch, a tab re-attach) keeps the running loop instead of restarting.
    private static readonly TimeSpan LingerAfterLastLease = TimeSpan.FromMilliseconds(300);

    private static readonly Dictionary<string, AnimatedCoverFeed> s_feeds = new(StringComparer.Ordinal);
    private static readonly AnimatedCoverLeases<AnimatedCoverImage> s_leases = new();

    public string Source { get; }

    private Session? _session;          // current decoder (starting or running)
    private WriteableBitmap? _frame;    // what every consumer shows; owned by the feed
    private bool _startQueued;
    private bool _closed;
    private int _lingerToken;
    private int _peakConsumers;

    private AnimatedCoverFeed(string source) => Source = source;

    /// <summary>Consumers currently leasing <paramref name="source"/> (tests, diagnostics).</summary>
    internal static int ConsumerCount(string source) => s_leases.CountOf(source);

    /// <summary>Whether a feed (decoding or lingering) exists for <paramref name="source"/>.</summary>
    internal static bool HasFeed(string source) => s_feeds.ContainsKey(source);

    /// <summary>
    /// Joins (or creates) the feed for <paramref name="source"/>. The consumer is handed
    /// the latest frame at once — the running loop's, or on a fresh start the last frame
    /// this file rendered anywhere in the app — so neither a track skip nor a re-attach
    /// flashes the static cover underneath.
    /// </summary>
    public static AnimatedCoverFeed Acquire(string source, AnimatedCoverImage consumer)
    {
        if (!s_feeds.TryGetValue(source, out var feed))
        {
            feed = new AnimatedCoverFeed(source) { _frame = TakeCachedFrame(source) };
            s_feeds[source] = feed;
        }

        s_leases.Acquire(source, consumer);
        feed._lingerToken++; // cancels a pending linger stop
        feed._peakConsumers = Math.Max(feed._peakConsumers, s_leases.CountOf(source));
        consumer.ShowFrame(feed._frame);
        feed.QueueStart();
        return feed;
    }

    /// <summary>
    /// Gives up <paramref name="consumer"/>'s lease. After the last one the decoder stops —
    /// at once when the consumer no longer wants this source (source changed, animation
    /// switched off: the file must be unlocked promptly for replace/remove), or after
    /// <see cref="LingerAfterLastLease"/> when it merely went out of sight.
    /// </summary>
    public void Release(AnimatedCoverImage consumer, bool linger)
    {
        var (_, wasLast) = s_leases.Release(consumer);
        if (!wasLast || _closed) return;

        if (!linger)
        {
            Close();
            return;
        }

        var token = ++_lingerToken;
        DispatcherTimer.RunOnce(() =>
        {
            if (token == _lingerToken && s_leases.CountOf(Source) == 0)
                Close();
        }, LingerAfterLastLease);
    }

    // Deferred to Background priority so consumers attaching in the same layout pass (a
    // window being shown) are counted before the decoder starts, and a lease taken and
    // dropped within one turn never starts one at all.
    private void QueueStart()
    {
        if (_startQueued || _session != null || _closed) return;
        _startQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _startQueued = false;
            if (_session != null || _closed || s_leases.CountOf(Source) == 0) return;
            Start();
        }, DispatcherPriority.Background);
    }

    private void Start()
    {
        var session = new Session(this);
        _session = session;
        var consumers = s_leases.CountOf(Source);
        ThreadPool.QueueUserWorkItem(_ => session.Open(consumers));
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        s_feeds.Remove(Source);

        var session = _session;
        var frame = _frame;
        _session = null;
        _frame = null;

        // A frame that was painted (by this loop or bridged in from the cache) goes back
        // to the cache so the next start of this file is seamless. The session keeps its
        // bitmap only when that bitmap IS the retired frame; it must never hand
        // uninitialized pixels to the cache.
        session?.ShutDown(keepBitmap: session != null && ReferenceEquals(frame, session.Bitmap),
            peakConsumers: _peakConsumers);
        if (frame != null)
            CacheFrame(Source, frame);
    }

    /// <summary>UI thread: a decoded frame is waiting in <paramref name="session"/>'s buffer.
    /// One copy into the shared bitmap, then every consumer repaints it.</summary>
    private void OnFrame(Session session)
    {
        if (_closed || !ReferenceEquals(_session, session)) return;

        session.CopyToBitmap();
        var bitmap = session.Bitmap;
        var consumers = s_leases.ConsumersOf(Source);
        if (ReferenceEquals(_frame, bitmap))
        {
            foreach (var consumer in consumers)
                consumer.InvalidateFrame();
            return;
        }

        // First real frame of this loop: swap it in for the bridge frame everywhere. The
        // bridge bitmap can only be disposed after every Image let go of it, and after the
        // render pass that may still reference it — hence the post.
        var bridge = _frame;
        _frame = bitmap;
        foreach (var consumer in consumers)
            consumer.ShowFrame(bitmap);
        if (bridge != null)
            Dispatcher.UIThread.Post(bridge.Dispose);
    }

    // Process-wide bridge-frame cache: the last decoded frame of recently played covers,
    // keyed by source path. On a track skip the feed shows the cached frame immediately
    // while VLC warms up, instead of flashing the static cover underneath. Cached bitmaps
    // are owned by the cache and are not referenced by any Image while they sit here;
    // TakeCachedFrame transfers ownership out.
    private const int FrameCacheCapacity = 8;
    private static readonly List<(string Source, WriteableBitmap Bitmap)> s_frameCache = new();

    private static void CacheFrame(string source, WriteableBitmap bitmap)
    {
        for (var i = 0; i < s_frameCache.Count; i++)
        {
            if (s_frameCache[i].Source == source)
            {
                var old = s_frameCache[i].Bitmap;
                s_frameCache.RemoveAt(i);
                if (!ReferenceEquals(old, bitmap))
                    Dispatcher.UIThread.Post(old.Dispose);
                break;
            }
        }

        s_frameCache.Add((source, bitmap));
        if (s_frameCache.Count > FrameCacheCapacity)
        {
            var evicted = s_frameCache[0].Bitmap;
            s_frameCache.RemoveAt(0);
            Dispatcher.UIThread.Post(evicted.Dispose);
        }
    }

    private static WriteableBitmap? TakeCachedFrame(string source)
    {
        for (var i = 0; i < s_frameCache.Count; i++)
        {
            if (s_frameCache[i].Source == source)
            {
                var bitmap = s_frameCache[i].Bitmap;
                s_frameCache.RemoveAt(i);
                return bitmap;
            }
        }

        return null;
    }

    /// <summary>
    /// One playback attempt: owns its MediaPlayer, frame buffer, and bitmap. Constructed
    /// on the UI thread (managed allocations only), opened and shut down on workers.
    /// </summary>
    private sealed class Session
    {
        private readonly AnimatedCoverFeed _feed;
        private readonly object _playerGate = new(); // Open vs. ShutDown on two workers
        private MediaPlayer? _player;

        // VLC decodes straight into this pinned array, so a frame is copied exactly once
        // (into the bitmap) instead of native buffer → managed scratch → bitmap.
        private readonly byte[] _pixels = GC.AllocateUninitializedArray<byte>(BufferBytes, pinned: true);
        private readonly IntPtr _pixelsAddress;
        private readonly WriteableBitmap _bitmap;
        private volatile bool _framePending;             // coalesce UI copies
        private long _lastFrameTimestamp;                // VLC display thread only
        private int _dead;

        /// <summary>The frame target; the feed adopts it as its frame on the first paint.</summary>
        public WriteableBitmap Bitmap => _bitmap;

        private bool IsDead => Volatile.Read(ref _dead) != 0;

        // Keep delegate instances alive for the player's lifetime — VLC stores raw
        // function pointers and will crash if these are garbage-collected.
        private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
        private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

        public Session(AnimatedCoverFeed feed)
        {
            _feed = feed;
            _pixelsAddress = Marshal.UnsafeAddrOfPinnedArrayElement(_pixels, 0);
            _bitmap = new WriteableBitmap(new PixelSize(RenderSize, RenderSize), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            // The bitmap holds uninitialized pixels until the first decoded frame
            // arrives; the feed only hands it to consumers from the first frame on, so
            // no garbage ever flashes.
            _lockCb = OnLock;
            _displayCb = OnDisplay;
        }

        /// <summary>Worker thread: creates the player and starts the muted loop.</summary>
        public void Open(int consumers)
        {
            var source = _feed.Source;
            lock (_playerGate)
            {
                if (IsDead) return; // closed before it started
                try
                {
                    // Software decoding is required for the frame-callback path.
                    var player = new MediaPlayer(SharedLibVlc.Instance) { EnableHardwareDecoding = false, Mute = true };
                    _player = player;
                    player.SetVideoFormat("RV32", RenderSize, RenderSize, Stride);
                    player.SetVideoCallbacks(_lockCb, null, _displayCb);
                    using var media = new Media(SharedLibVlc.Instance, source, FromType.FromPath,
                        ":no-audio", ":input-repeat=65535", DecoderThreads);
                    player.Play(media);
                    DebugLogger.Info(DebugLogger.Category.Playback, "Cover.Play",
                        $"src={Path.GetFileName(source)} | consumers={consumers}");
                    return;
                }
                catch
                {
                    // LibVLC unavailable or unreadable file — the static cover stays.
                }
            }
            ShutDown();
        }

        // VLC asks where to write the next frame; hand back the single shared buffer.
        // Minor tearing under load is accepted (one buffer).
        private IntPtr OnLock(IntPtr opaque, IntPtr planes)
        {
            Marshal.WriteIntPtr(planes, _pixelsAddress);
            return _pixelsAddress; // picture id — unused
        }

        // A decoded frame is in the buffer. At most one UI copy is in flight: a busy UI
        // thread drops frames instead of queueing them.
        private void OnDisplay(IntPtr opaque, IntPtr picture)
        {
            if (_framePending || IsDead) return;
            var now = Stopwatch.GetTimestamp();
            if (!AnimatedCoverPolicy.AcceptFrame(now, _lastFrameTimestamp, Stopwatch.Frequency)) return;
            _lastFrameTimestamp = now;
            _framePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _framePending = false;
                // _dead is set on the UI thread before the shutdown worker is queued,
                // so any closure dequeued after ShutDown() always bails here — the
                // bitmap is only freed after that point.
                if (IsDead) return;
                _feed.OnFrame(this);
            }, DispatcherPriority.Render);
        }

        /// <summary>UI thread: the one per-frame copy, pinned buffer → bitmap.</summary>
        public void CopyToBitmap()
        {
            using var fb = _bitmap.Lock();
            if (fb.RowBytes == Stride)
            {
                Marshal.Copy(_pixels, 0, fb.Address, BufferBytes);
                return;
            }

            for (var y = 0; y < RenderSize; y++)
                Marshal.Copy(_pixels, y * Stride, fb.Address + y * fb.RowBytes, Stride);
        }

        /// <summary>
        /// Stops and disposes the player on a worker thread (Stop blocks in LibVLC 3.x).
        /// Safe to call multiple times, from the UI thread or the opening worker. With
        /// <paramref name="keepBitmap"/> the feed adopted <see cref="Bitmap"/> as its frame
        /// and now owns its disposal.
        /// </summary>
        public void ShutDown(bool keepBitmap = false, int peakConsumers = 0)
        {
            if (Interlocked.Exchange(ref _dead, 1) != 0) return;
            DebugLogger.Info(DebugLogger.Category.Playback, "Cover.Stop",
                $"src={Path.GetFileName(_feed.Source)} | peakConsumers={peakConsumers}");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (_playerGate)
                {
                    var player = _player;
                    _player = null;
                    if (player != null)
                    {
                        // Stop() is synchronous — after it returns, no more callbacks
                        // fire, so the pinned buffer may be collected with the session.
                        try { player.Stop(); } catch { }
                        // Nulls are the documented way to detach LibVLCSharp's video
                        // callbacks; the parameters just aren't annotated nullable.
#pragma warning disable CS8625
                        try { player.SetVideoCallbacks(null, null, null); } catch { }
#pragma warning restore CS8625
                        try { player.Dispose(); } catch { }
                    }
                }
                if (!keepBitmap)
                    Dispatcher.UIThread.Post(_bitmap.Dispose);
            });
        }

        // NEVER dispose the shared LibVLC — it's reused across surfaces.
    }
}
