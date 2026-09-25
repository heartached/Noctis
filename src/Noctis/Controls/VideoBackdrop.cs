using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibVLCSharp.Shared;
using Noctis.Services;

namespace Noctis.Controls;

/// <summary>
/// Full-bleed looping video / GIF background (lyrics page). Same mechanism as
/// <see cref="AnimatedCoverImage"/> — LibVLC decodes frames into a <see cref="WriteableBitmap"/>
/// shown by a plain <c>Image</c>, so the clip composes under the lyrics, clips to the
/// page and never spawns a native video window — but tuned for a backdrop:
/// <list type="bullet">
/// <item>The decode buffer keeps the clip's aspect ratio and is capped at
/// <see cref="MaxLongSide"/> px on the long side (a 4K clip decodes at 960px — behind a
/// blur-strength scrim nothing above that is visible, and it keeps the per-frame copy
/// to ~2 MB instead of ~33 MB).</item>
/// <item>Frames are coalesced: at most one UI invalidation is in flight, so a 60 fps
/// clip on a busy UI thread drops frames instead of queueing them.</item>
/// <item>Decoding stops whenever the control is detached, <see cref="IsActive"/> is
/// false, or the hosting window is minimized — a hidden backdrop costs nothing.</item>
/// </list>
/// Software-decoded (required for the callback path); GIF plays through LibVLC's
/// image/avformat demuxers like any video.
/// </summary>
public sealed class VideoBackdrop : Control
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<VideoBackdrop, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<VideoBackdrop, bool>(nameof(IsActive), defaultValue: true);

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

    /// <summary>Holds the clip on its current frame (Settings: pause the video with playback).
    /// Combined with the window-minimized park; the decoder resumes where it stopped.</summary>
    public static readonly StyledProperty<bool> IsPausedProperty =
        AvaloniaProperty.Register<VideoBackdrop, bool>(nameof(IsPaused));

    public bool IsPaused
    {
        get => GetValue(IsPausedProperty);
        set => SetValue(IsPausedProperty, value);
    }

    /// <summary>
    /// Music-video mode: the clip follows this playback position instead of looping
    /// freely. Null (default) keeps the free-running backdrop behaviour. The clip is
    /// re-seeked whenever it drifts more than <see cref="SyncToleranceMs"/> from it.
    /// </summary>
    public static readonly StyledProperty<TimeSpan?> SyncPositionProperty =
        AvaloniaProperty.Register<VideoBackdrop, TimeSpan?>(nameof(SyncPosition));
    public TimeSpan? SyncPosition
    {
        get => GetValue(SyncPositionProperty);
        set => SetValue(SyncPositionProperty, value);
    }

    /// <summary>UniformToFill (backdrop) or Uniform (music video inside the artwork slot).</summary>
    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<VideoBackdrop, Stretch>(nameof(Stretch), defaultValue: Stretch.UniformToFill);
    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <summary>Corner radius of the painted frame (music video: flat or rounded).</summary>
    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<VideoBackdrop, double>(nameof(CornerRadius));
    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Music video audio: the clip's own audio is what plays, so the picture is held
    /// within <see cref="TightSyncToleranceMs"/> (lip-sync) instead of <see cref="SyncToleranceMs"/>.</summary>
    public static readonly StyledProperty<bool> TightSyncProperty =
        AvaloniaProperty.Register<VideoBackdrop, bool>(nameof(TightSync));
    public bool TightSync
    {
        get => GetValue(TightSyncProperty);
        set => SetValue(TightSyncProperty, value);
    }

    /// <summary>False once the current source turned out to have no video stream (or could
    /// not be read): the page keeps the cover up instead of an empty frame.</summary>
    public static readonly DirectProperty<VideoBackdrop, bool> HasVideoStreamProperty =
        AvaloniaProperty.RegisterDirect<VideoBackdrop, bool>(nameof(HasVideoStream), o => o.HasVideoStream);
    private bool _hasVideoStream = true;
    public bool HasVideoStream
    {
        get => _hasVideoStream;
        private set => SetAndRaise(HasVideoStreamProperty, ref _hasVideoStream, value);
    }

    public const int SyncToleranceMs = 350;
    public const int TightSyncToleranceMs = 150;

    private int CurrentSyncToleranceMs => TightSync ? TightSyncToleranceMs : SyncToleranceMs;

    /// <summary>What a sync request does to the clip.</summary>
    internal enum SyncAction { None, Seek, Restart }

    /// <summary>
    /// A sync request against the clip's state: seek when it drifted past the tolerance (or
    /// when forced); restart when it has ENDED — VLC ignores a seek after the end, so a song
    /// replayed or sought back would otherwise leave the clip frozen — unless the song is
    /// already past the clip's end (<paramref name="lengthMs"/>; 0 = unknown, never restart).
    /// </summary>
    internal static SyncAction PlanSync(bool ended, long timeMs, long targetMs, long lengthMs, int toleranceMs, bool force)
    {
        if (ended)
            return lengthMs > 0 && targetMs < lengthMs - toleranceMs ? SyncAction.Restart : SyncAction.None;
        return force || Math.Abs(timeMs - targetMs) > toleranceMs ? SyncAction.Seek : SyncAction.None;
    }

    /// <summary>Long-side cap for the decode buffer.</summary>
    public const int MaxLongSide = 960;

    /// <summary>
    /// Decode-buffer size for a clip of <paramref name="width"/>×<paramref name="height"/>:
    /// the same aspect ratio, long side capped at <see cref="MaxLongSide"/>, both sides
    /// even (VLC's chroma planes want even dimensions) and at least 2 px. Unknown
    /// dimensions (0) fall back to a 16:9 buffer at the cap.
    /// </summary>
    public static (int Width, int Height) FitBuffer(int width, int height)
    {
        if (width <= 0 || height <= 0) { width = MaxLongSide; height = MaxLongSide * 9 / 16; }
        var scale = Math.Min(1.0, (double)MaxLongSide / Math.Max(width, height));
        var w = Math.Max(2, (int)Math.Round(width * scale)) & ~1;
        var h = Math.Max(2, (int)Math.Round(height * scale)) & ~1;
        return (Math.Max(2, w), Math.Max(2, h));
    }

    private Session? _session;
    private int _generation;
    private WriteableBitmap? _current;   // frame currently painted (owned by the session)
    private Window? _window;
    private bool _windowMinimized;

    public VideoBackdrop()
    {
        ClipToBounds = true;
        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window != null)
            {
                _window.PropertyChanged += OnWindowPropertyChanged;
                _windowMinimized = _window.WindowState == WindowState.Minimized;
            }
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_window != null)
            {
                _window.PropertyChanged -= OnWindowPropertyChanged;
                _window = null;
            }
            Teardown();
        };
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty) return;
        var minimized = _window?.WindowState == WindowState.Minimized;
        if (minimized == _windowMinimized) return;
        _windowMinimized = minimized;
        // Park the decoder while the window can't be seen; resume where it left off.
        _session?.SetPaused(minimized || IsPaused);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == IsActiveProperty)
            Refresh();
        else if (change.Property == IsPausedProperty)
            _session?.SetPaused(IsPaused || _windowMinimized);
        else if (change.Property == SyncPositionProperty)
        {
            if (SyncPosition is { } pos) _session?.SyncTo((long)pos.TotalMilliseconds, CurrentSyncToleranceMs);
        }
        else if (change.Property == StretchProperty || change.Property == CornerRadiusProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var frame = _current;
        if (frame == null) return;
        // UniformToFill: scale the frame to cover the control, centred, clipped by bounds.
        var bounds = Bounds;
        var src = frame.Size;
        if (src.Width <= 0 || src.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        var scale = Stretch == Stretch.Uniform
            ? Math.Min(bounds.Width / src.Width, bounds.Height / src.Height)
            : Math.Max(bounds.Width / src.Width, bounds.Height / src.Height);
        var w = src.Width * scale;
        var h = src.Height * scale;
        var dest = new Rect((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
        var radius = CornerRadius;
        if (radius > 0)
        {
            var clipRect = Stretch == Stretch.Uniform ? dest : new Rect(bounds.Size);
            using (context.PushClip(new RoundedRect(clipRect, radius)))
                context.DrawImage(frame, new Rect(src), dest);
        }
        else
        {
            context.DrawImage(frame, new Rect(src), dest);
        }
    }

    private void Refresh()
    {
        var source = Source;
        var active = IsActive && !string.IsNullOrEmpty(source) && File.Exists(source)
                     && this.IsAttachedToVisualTree();
        Teardown();
        if (!active || string.IsNullOrEmpty(source)) return;

        var generation = _generation;
        var startPaused = _windowMinimized || IsPaused;
        var synced = SyncPosition != null;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Session session;
            var hasVideo = false;
            try
            {
                // Probe the clip's dimensions first so the buffer keeps its aspect ratio.
                using var probe = new Media(SharedLibVlc.Instance, source, FromType.FromPath);
                probe.Parse(MediaParseOptions.ParseLocal, timeout: 3000).GetAwaiter().GetResult();
                int vw = 0, vh = 0;
                foreach (var track in probe.Tracks)
                {
                    if (track.TrackType != TrackType.Video) continue;
                    hasVideo = true;
                    vw = (int)track.Data.Video.Width;
                    vh = (int)track.Data.Video.Height;
                    break;
                }
                var (bw, bh) = FitBuffer(vw, vh);
                session = new Session(this, bw, bh, probe.Duration);
            }
            catch
            {
                // LibVLC unavailable or unreadable clip — leave the artwork backdrop
                Dispatcher.UIThread.Post(() => { if (generation == _generation) HasVideoStream = false; });
                return;
            }

            try
            {
                // A synced music video plays once and follows the song; a backdrop loops.
                // Its start position is applied once it is attached below.
                using var media = !synced
                    ? new Media(SharedLibVlc.Instance, source, FromType.FromPath, ":no-audio", ":input-repeat=65535")
                    : new Media(SharedLibVlc.Instance, source, FromType.FromPath, ":no-audio");
                session.Player.Play(media);
                if (startPaused) session.SetPaused(true);
                DebugLogger.Info(DebugLogger.Category.Playback, "Backdrop.Play", $"src={Path.GetFileName(source)}");
            }
            catch
            {
                session.ShutDown();
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _generation)
                {
                    session.ShutDown(); // restarted or torn down while starting up
                    return;
                }
                _session = session;
                HasVideoStream = hasVideo;
                // Line the clip up with the song as it is NOW. The source switches while the
                // track changes, before the song's position resets, so a position captured at
                // start-up could still be the previous song's.
                if (SyncPosition is { } now)
                    session.SyncTo((long)now.TotalMilliseconds, CurrentSyncToleranceMs, force: true);
            });
        });
    }

    private void Teardown()
    {
        _generation++;
        var session = _session;
        _session = null;
        _current = null;
        session?.ShutDown();
        InvalidateVisual();
    }

    private sealed class Session
    {
        private readonly VideoBackdrop _owner;
        public readonly MediaPlayer Player;
        private readonly int _stride;
        private readonly int _bufferBytes;
        private readonly IntPtr _buffer;
        private readonly byte[] _scratch;
        private readonly WriteableBitmap _bitmap;
        private volatile bool _framePending;
        private volatile bool _dead;
        private volatile bool _paused;
        private long _lengthMs;

        // Delegates must stay alive for the player's lifetime (VLC keeps raw pointers).
        private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
        private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

        public Session(VideoBackdrop owner, int width, int height, long lengthMs)
        {
            _owner = owner;
            _lengthMs = lengthMs;
            _stride = width * 4;
            _bufferBytes = _stride * height;
            _buffer = Marshal.AllocHGlobal(_bufferBytes);
            _scratch = new byte[_bufferBytes];
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _lockCb = OnLock;
            _displayCb = OnDisplay;
            Player = new MediaPlayer(SharedLibVlc.Instance) { EnableHardwareDecoding = false, Mute = true };
            Player.SetVideoFormat("RV32", (uint)width, (uint)height, (uint)_stride);
            Player.SetVideoCallbacks(_lockCb, null, _displayCb);
        }

        public void SetPaused(bool paused)
        {
            if (_dead) return;
            _paused = paused;
            ThreadPool.QueueUserWorkItem(_ => { try { Player.SetPause(paused); } catch { } });
        }

        private int _syncInFlight;

        /// <summary>
        /// Keeps the clip within <paramref name="toleranceMs"/> of the song. Reading and
        /// setting the native time happens off the UI thread; at most one check runs at a
        /// time so a busy position stream cannot queue seeks. An ended clip is restarted
        /// (see <see cref="PlanSync"/>).
        /// </summary>
        public void SyncTo(long targetMs, int toleranceMs, bool force = false)
        {
            if (_dead) return;
            if (Interlocked.CompareExchange(ref _syncInFlight, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var length = Player.Length;
                    if (length > 0) _lengthMs = length; // 0 once ended — keep the last known
                    switch (PlanSync(Player.State == VLCState.Ended, Player.Time, targetMs, _lengthMs, toleranceMs, force))
                    {
                        case SyncAction.Seek:
                            Player.Time = targetMs;
                            break;
                        case SyncAction.Restart:
                            Player.Stop();
                            Player.Play();
                            Player.Time = targetMs;
                            if (_paused) Player.SetPause(true);
                            break;
                    }
                }
                catch { }
                finally { Interlocked.Exchange(ref _syncInFlight, 0); }
            });
        }

        private IntPtr OnLock(IntPtr opaque, IntPtr planes)
        {
            Marshal.WriteIntPtr(planes, _buffer);
            return _buffer;
        }

        private void OnDisplay(IntPtr opaque, IntPtr picture)
        {
            if (_framePending || _dead) return;
            _framePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _framePending = false;
                if (_dead) return;
                Marshal.Copy(_buffer, _scratch, 0, _bufferBytes);
                using (var fb = _bitmap.Lock())
                    Marshal.Copy(_scratch, 0, fb.Address, _bufferBytes);
                if (_owner._session == this)
                {
                    _owner._current = _bitmap; // first real frame reveals the clip
                    _owner.HasVideoStream = true;
                    _owner.InvalidateVisual();
                }
            }, DispatcherPriority.Render);
        }

        public void ShutDown()
        {
            if (_dead) return;
            _dead = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Player.Stop(); } catch { }
#pragma warning disable CS8625
                try { Player.SetVideoCallbacks(null, null, null); } catch { }
#pragma warning restore CS8625
                try { Player.Dispose(); } catch { }
                Marshal.FreeHGlobal(_buffer);
                Dispatcher.UIThread.Post(_bitmap.Dispose);
            });
        }
    }
}
