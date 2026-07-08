using System;
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

    // Cap how often a decoded frame is pushed to the UI thread. The cover is a
    // decorative loop; pushing every source frame (up to 60 fps) floods the UI
    // thread with two 1.44 MB copies + an invalidate each and starves it under
    // load. ~15 fps looks fine and frees the thread for playback/render work.
    private const long MinDisplayIntervalMs = 66;

    private readonly Image _image;
    private Session? _session;

    // Bumped on every Refresh/Teardown (UI thread only). A background session
    // startup that finishes after the control was restarted sees a stale value
    // and shuts itself down instead of becoming current.
    private int _sessionGeneration;

    public AnimatedCoverImage()
    {
        InitializeComponent();
        _image = this.FindControl<Image>("FrameImage")!;
        // Teardown on detach, rebuild on re-attach: a TabControl detaches the
        // hosting tab's content when you switch tabs and re-attaches it on return,
        // and Source/IsActive don't change across that, so nothing else restarts us.
        AttachedToVisualTree += (_, _) => Refresh();
        DetachedFromVisualTree += (_, _) => Teardown();
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
        Teardown();

        var source = Source;
        if (!IsActive || string.IsNullOrEmpty(source) || !File.Exists(source))
            return;

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
                    ":no-audio", ":input-repeat=65535",
                    // Cap the cover's software video decode so it can't saturate every CPU
                    // core and starve the audio output thread (macOS CoreAudio/auhal render),
                    // which caused audible stutter when a cover was added mid-playback on
                    // Apple Silicon. One decode thread + skipping the H.264 deblock loop
                    // filter frees cores for audio; the cover is a small decorative loop so
                    // the minor quality/frame-pacing cost is irrelevant.
                    ":avcodec-threads=1", ":avcodec-skiploopfilter=all");
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

    private void Teardown()
    {
        _sessionGeneration++;
        var session = _session;
        _session = null;
        _image.Source = null;
        session?.ShutDown();
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
        private long _lastDisplayTicks;                 // frame-rate gate (see MinDisplayIntervalMs)

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
            // Throttle UI pushes to ~15 fps so the per-frame copies/invalidate
            // can't starve the UI thread while a track is playing.
            var now = Environment.TickCount64;
            if (now - _lastDisplayTicks < MinDisplayIntervalMs) return;
            _lastDisplayTicks = now;
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
                }
            }, DispatcherPriority.Render);
        }

        /// <summary>
        /// Stops and disposes the player on a worker thread (Stop blocks in LibVLC 3.x).
        /// Safe to call multiple times; called on the UI thread or a startup worker.
        /// </summary>
        public void ShutDown()
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
                Dispatcher.UIThread.Post(_bitmap.Dispose);
            });
        }

        // NEVER dispose the shared LibVLC — it's reused across surfaces.
    }
}
