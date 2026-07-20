using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Noctis.Services;

namespace Noctis.Controls;

/// <summary>
/// Plays a looping animated cover by decoding frames (via LibVLC video callbacks) into a
/// <see cref="WriteableBitmap"/> shown in a plain <c>Image</c> element — so it composes
/// inside transparent windows, clips to its parent's rounded border, and never spawns a
/// native output window. Software-decoded; fine for a small muted loop. Minor frame tearing
/// under load is accepted (single shared buffer).
///
/// All LibVLC calls (first-use core initialization, Play, Stop, Dispose) run on worker
/// threads: they block for hundreds of milliseconds and froze the UI when a cover was
/// applied or replaced. Each playback attempt is an isolated <see cref="Session"/> so a
/// stale startup can never corrupt the current one.
/// </summary>
public partial class AnimatedCoverImage : UserControl
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<AnimatedCoverImage, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<AnimatedCoverImage, bool>(nameof(IsActive), defaultValue: false);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    // VLC scales every frame into a square RV32 (BGRA in memory) buffer of this size.
    // Animated covers are square; a non-square source gets scaled to fit this square.
    private const int RenderSize = 600;
    private const int Stride = RenderSize * 4;
    private const int BufferBytes = Stride * RenderSize;

    private readonly Image _image;
    private Session? _session;

    // Bumped on every Refresh/Teardown (UI thread only). A background session
    // startup that finishes after the control was restarted sees a stale value
    // and shuts itself down instead of becoming current.
    private int _sessionGeneration;

    // Last decoded frame of the previous session, kept on screen while a
    // same-source session warms up (page re-attach), so the static cover
    // never flashes through. Disposed when the new session paints or the
    // source changes. _liveSource is the source of the running session.
    private WriteableBitmap? _lingerBitmap;
    private string? _lingerSource;
    private string? _liveSource;

    // Process-wide bridge-frame cache (UI thread only): the last decoded frame of
    // recently played covers, keyed by source path. On a track skip the control shows
    // the cached frame immediately while VLC warms up, instead of flashing the static
    // cover underneath. Cached bitmaps are owned by the cache and are not referenced
    // by any Image while they sit here; TakeCachedFrame transfers ownership out.
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

    public AnimatedCoverImage()
    {
        InitializeComponent();
        _image = this.FindControl<Image>("FrameImage")!;
        // Teardown on detach, rebuild on re-attach: a TabControl detaches the
        // hosting tab's content when you switch tabs and re-attaches it on return,
        // and Source/IsActive don't change across that, so nothing else restarts us.
        // The last frame is kept so the return trip doesn't flash the static cover.
        AttachedToVisualTree += (_, _) => Refresh();
        DetachedFromVisualTree += (_, _) => Teardown(keepLastFrame: true);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == IsActiveProperty)
            Refresh();
    }

    private void Refresh()
    {
        var source = Source;
        var active = IsActive && !string.IsNullOrEmpty(source) && File.Exists(source);
        // Bridge the VLC warm-up with the previous frame only when restarting
        // the same video; a different source must clear to the static cover.
        var keep = active && (source == _liveSource || source == _lingerSource);
        Teardown(keepLastFrame: keep);

        if (!active || string.IsNullOrEmpty(source))
            return;

        // Seamless track skip: bridge the warm-up with the last frame this source
        // rendered anywhere in the app, so the static cover never flashes through.
        if (_lingerBitmap == null && TakeCachedFrame(source) is { } cached)
        {
            _lingerBitmap = cached;
            _lingerSource = source;
            _image.Source = cached;
        }

        _liveSource = source;
        var generation = _sessionGeneration;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Session session;
            try
            {
                session = new Session(this);
            }
            catch
            {
                return; // LibVLC unavailable — leave the static cover in place
            }

            try
            {
                using var media = new Media(SharedLibVlc.Instance, source, FromType.FromPath,
                    ":no-audio", ":input-repeat=65535");
                session.Player.Play(media);
                DebugLogger.Info(DebugLogger.Category.Playback, "Cover.Play", $"src={Path.GetFileName(source)}");
            }
            catch
            {
                session.ShutDown();
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _sessionGeneration)
                {
                    // Control was torn down or restarted while this session was
                    // starting up — it must never become current.
                    session.ShutDown();
                    return;
                }
                _session = session;
            });
        });
    }

    private void Teardown(bool keepLastFrame = false)
    {
        _sessionGeneration++;
        var session = _session;
        _session = null;

        if (keepLastFrame)
        {
            // Leave whatever frame is showing (the ending session's bitmap or an
            // earlier lingering one) so a same-source restart is seamless.
            if (session != null && ReferenceEquals(_image.Source, session.Bitmap))
            {
                var previous = _lingerBitmap;
                _lingerBitmap = session.Bitmap;
                _lingerSource = _liveSource;
                if (previous != null)
                    Dispatcher.UIThread.Post(previous.Dispose);
                session.ShutDown(keepBitmap: true);
            }
            else
            {
                session?.ShutDown();
            }
        }
        else
        {
            // Retire whatever frame was showing into the bridge cache (keyed by its
            // source) so a later session for the same file starts seamlessly. A session
            // that never painted still holds uninitialized pixels — never cache those.
            var sessionPainted = session != null && ReferenceEquals(_image.Source, session.Bitmap);
            _image.Source = null;
            if (session != null && sessionPainted && _liveSource != null)
            {
                session.ShutDown(keepBitmap: true);
                CacheFrame(_liveSource, session.Bitmap);
            }
            else
            {
                session?.ShutDown();
            }

            var linger = _lingerBitmap;
            var lingerSource = _lingerSource;
            _lingerBitmap = null;
            _lingerSource = null;
            if (linger != null)
            {
                if (lingerSource != null)
                    CacheFrame(lingerSource, linger);
                else
                    Dispatcher.UIThread.Post(linger.Dispose);
            }
        }

        _liveSource = null;
    }

    /// <summary>Drops the lingering bridge frame once the live session has painted
    /// over it; the bitmap can only be disposed after it left the Image.</summary>
    private void ReleaseLingerFrame(WriteableBitmap current)
    {
        var linger = _lingerBitmap;
        if (linger == null || ReferenceEquals(linger, current)) return;
        _lingerBitmap = null;
        _lingerSource = null;
        Dispatcher.UIThread.Post(linger.Dispose);
    }

    /// <summary>
    /// One playback attempt: owns its MediaPlayer, native frame buffer, and bitmap.
    /// Constructed and played on a worker thread; shut down from the UI thread.
    /// </summary>
    private sealed class Session
    {
        private readonly AnimatedCoverImage _owner;
        public readonly MediaPlayer Player;
        private readonly IntPtr _buffer;                // native frame buffer VLC writes into
        private readonly byte[] _scratch = new byte[BufferBytes]; // managed hop for the IntPtr->IntPtr copy
        private readonly WriteableBitmap _bitmap;
        private volatile bool _framePending;            // coalesce UI invalidations
        private volatile bool _dead;

        /// <summary>The frame target; the owner adopts it as a bridge frame on teardown.</summary>
        public WriteableBitmap Bitmap => _bitmap;

        // Keep delegate instances alive for the player's lifetime — VLC stores raw
        // function pointers and will crash if these are garbage-collected.
        private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
        private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

        public Session(AnimatedCoverImage owner)
        {
            _owner = owner;
            _buffer = Marshal.AllocHGlobal(BufferBytes);
            _bitmap = new WriteableBitmap(new PixelSize(RenderSize, RenderSize), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            // The bitmap holds uninitialized pixels until the first decoded frame
            // arrives; OnDisplay assigns _image.Source on the first frame so no
            // garbage ever flashes.

            _lockCb = OnLock;
            _displayCb = OnDisplay;

            // Software decoding is required for the frame-callback path.
            Player = new MediaPlayer(SharedLibVlc.Instance) { EnableHardwareDecoding = false, Mute = true };
            Player.SetVideoFormat("RV32", RenderSize, RenderSize, Stride);
            Player.SetVideoCallbacks(_lockCb, null, _displayCb);
        }

        // VLC asks where to write the next frame; hand back the single shared buffer.
        private IntPtr OnLock(IntPtr opaque, IntPtr planes)
        {
            Marshal.WriteIntPtr(planes, _buffer);
            return _buffer; // picture id — unused
        }

        // A decoded frame is in _buffer. Push it to the WriteableBitmap on the UI thread.
        private void OnDisplay(IntPtr opaque, IntPtr picture)
        {
            if (_framePending || _dead) return;
            _framePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _framePending = false;
                // _dead is set on the UI thread before the shutdown worker is queued,
                // so any closure dequeued after ShutDown() always bails here — the
                // buffer/bitmap are only freed after that point.
                if (_dead) return;
                Marshal.Copy(_buffer, _scratch, 0, BufferBytes);
                using (var fb = _bitmap.Lock())
                    Marshal.Copy(_scratch, 0, fb.Address, BufferBytes);
                if (_owner._session == this)
                {
                    _owner._image.Source = _bitmap; // no-op after the first frame; reveals real content
                    _owner._image.InvalidateVisual();
                    _owner.ReleaseLingerFrame(_bitmap);
                }
            }, DispatcherPriority.Render);
        }

        /// <summary>
        /// Stops and disposes the player on a worker thread (Stop blocks in LibVLC 3.x).
        /// Safe to call multiple times; called on the UI thread or a startup worker.
        /// With <paramref name="keepBitmap"/> the owner adopted <see cref="Bitmap"/>
        /// as a bridge frame and now owns its disposal.
        /// </summary>
        public void ShutDown(bool keepBitmap = false)
        {
            if (_dead) return;
            _dead = true;
            DebugLogger.Info(DebugLogger.Category.Playback, "Cover.Stop");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Stop() is synchronous — after it returns, no more callbacks fire,
                // so the native buffer can be freed safely.
                try { Player.Stop(); } catch { }
                try { Player.SetVideoCallbacks(null, null, null); } catch { }
                try { Player.Dispose(); } catch { }
                Marshal.FreeHGlobal(_buffer);
                if (!keepBitmap)
                    Dispatcher.UIThread.Post(_bitmap.Dispose);
            });
        }

        // NEVER dispose the shared LibVLC — it's reused across surfaces.
    }
}
