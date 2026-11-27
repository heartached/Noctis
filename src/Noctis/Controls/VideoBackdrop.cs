using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibVLCSharp.Shared;
using Noctis.Helpers;
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

    /// <summary>True once the current clip's first frame is on screen, false again when it is
    /// torn down: what still art over or under the clip cross-fades on (artist page hero).</summary>
    public static readonly DirectProperty<VideoBackdrop, bool> HasFrameProperty =
        AvaloniaProperty.RegisterDirect<VideoBackdrop, bool>(nameof(HasFrame), o => o.HasFrame);
    private bool _hasFrame;
    public bool HasFrame
    {
        get => _hasFrame;
        private set => SetAndRaise(HasFrameProperty, ref _hasFrame, value);
    }

    /// <summary>Music-video mode: the clip's playback speed, following the song's (Playback
    /// speed 0.75x-2x). Without it a sped-up song drifts past the tolerance every tick and the
    /// clip stutters from re-seek to re-seek.</summary>
    public static readonly StyledProperty<double> RateProperty =
        AvaloniaProperty.Register<VideoBackdrop, double>(nameof(Rate), defaultValue: 1.0);
    public double Rate
    {
        get => GetValue(RateProperty);
        set => SetValue(RateProperty, value);
    }

    /// <summary>Long-side cap of the decode buffer for this instance (default
    /// <see cref="MaxLongSide"/>); the full-size music video view raises it so the clip
    /// is not upscaled from 960 px. Read when a clip starts.</summary>
    public static readonly StyledProperty<int> MaxDecodeSideProperty =
        AvaloniaProperty.Register<VideoBackdrop, int>(nameof(MaxDecodeSide), defaultValue: MaxLongSide);
    public int MaxDecodeSide
    {
        get => GetValue(MaxDecodeSideProperty);
        set => SetValue(MaxDecodeSideProperty, value);
    }

    /// <summary>A stream (http/https source) broke: an error, its end long before the clip's,
    /// or no frame for a while as the song plays. Executed once per started stream, on the UI
    /// thread, with the failed <see cref="Source"/>; the owner resolves a fresh URL (11-25).</summary>
    public static readonly StyledProperty<ICommand?> StreamFailedCommandProperty =
        AvaloniaProperty.Register<VideoBackdrop, ICommand?>(nameof(StreamFailedCommand));
    public ICommand? StreamFailedCommand
    {
        get => GetValue(StreamFailedCommandProperty);
        set => SetValue(StreamFailedCommandProperty, value);
    }

    /// <summary>Name in the Developer Mode log lines ("ArtistVideo.Start", "Backdrop.Stop").</summary>
    public string LogName { get; set; } = "Backdrop";

    /// <summary>
    /// Free-running backdrop only (artist page hero): when the decoder keeps falling under
    /// <see cref="SlowFps"/> for <see cref="SlowWindows"/> seconds in a row while it plays (a
    /// machine too slow to decode the clip in software), the clip stops, the still art stays,
    /// and the same file is not started again this session.
    /// </summary>
    public bool StopWhenSlow { get; set; }

    /// <summary>Most frames a second handed to the UI (0 = every frame the decoder shows).</summary>
    public int MaxFps { get; set; }

    internal const double SlowFps = 10;
    internal const int SlowWindows = 3;

    /// <summary>Clips stopped as too slow to decode here; not started again this session.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> SlowSources = new(StringComparer.OrdinalIgnoreCase);
    internal static bool IsKnownSlow(string? source) => source != null && SlowSources.ContainsKey(source);
    internal static void MarkSlow(string source) => SlowSources[source] = 0;
    internal static void ClearKnownSlowForTests() => SlowSources.Clear();

    private static int s_liveSessions, s_active;

    /// <summary>LibVLC players alive right now (created, not yet disposed), app-wide.</summary>
    internal static int LiveSessionCount => Volatile.Read(ref s_liveSessions);

    /// <summary>Backdrops that want a player now (starting or playing), app-wide: what
    /// <see cref="LiveSessionCount"/> settles at once the thread pool has caught up.</summary>
    internal static int ActiveCount => Volatile.Read(ref s_active);

    /// <summary>Tests: count a start (<see cref="ActiveCount"/>) but never touch LibVLC.</summary>
    internal static bool SkipDecoderForTests;

    /// <summary>Frames this instance put on screen (tests).</summary>
    internal int FramesPresented { get; private set; }

    private bool _countedActive;

    // ── One video at a time the user looks at (owner 11-26) ──

    /// <summary>
    /// Rank among the backdrops that want a player: one with a lower rank holds its frame
    /// (paused, the frame stays) while any with a higher rank wants one, and plays on once those
    /// are gone. The lyrics page's backdrops and anything new keep the default; the artist page's
    /// hero is <see cref="HeroPriority"/> and yields to them.
    /// </summary>
    public int Priority { get; set; } = DefaultPriority;

    public const int DefaultPriority = 1;
    public const int HeroPriority = 0;

    /// <summary>Backdrops that want a player now (UI thread), what <see cref="ActiveCount"/> counts.</summary>
    private static readonly List<VideoBackdrop> Registered = new();

    /// <summary>A backdrop of a higher <see cref="Priority"/> wants a player.</summary>
    internal bool IsYielding { get; private set; }

    /// <summary>The player holds its frame: paused, minimized or yielding.</summary>
    internal bool IsHeld => IsPaused || _hostWindow.IsMinimized || IsYielding;

    /// <summary>Backdrops that want a player and are not holding (tests).</summary>
    internal static int PlayingCount => Registered.Count(b => !b.IsHeld);

    private static void Register(VideoBackdrop backdrop, bool on)
    {
        if (on) Registered.Add(backdrop);
        else Registered.Remove(backdrop);
        var top = Registered.Count == 0 ? int.MinValue : Registered.Max(b => b.Priority);
        foreach (var b in Registered.ToList())
        {
            var yielding = b.Priority < top;
            if (yielding == b.IsYielding) continue;
            b.IsYielding = yielding;
            b.ApplyHold();
        }
        if (!on) backdrop.IsYielding = false;
    }

    private void ApplyHold() => _session?.SetPaused(IsHeld);

    /// <summary>The clip's own size, known once a player started on it (0x0 otherwise).</summary>
    public static readonly DirectProperty<VideoBackdrop, PixelSize> ClipPixelSizeProperty =
        AvaloniaProperty.RegisterDirect<VideoBackdrop, PixelSize>(nameof(ClipPixelSize), o => o.ClipPixelSize);
    private PixelSize _clipPixelSize;
    public PixelSize ClipPixelSize
    {
        get => _clipPixelSize;
        private set => SetAndRaise(ClipPixelSizeProperty, ref _clipPixelSize, value);
    }

    /// <summary>The decoder's rate in one-second windows (<see cref="SlowStep"/>).</summary>
    internal struct SlowMeter
    {
        public long Start, Frames;
        public int SlowRun;
        public bool Fired;
    }

    /// <summary>
    /// One decoded frame at <paramref name="now"/> (ticks of <paramref name="frequency"/>): the
    /// rate is taken over one-second windows, and <see cref="SlowWindows"/> windows in a row
    /// under <see cref="SlowFps"/> return that rate, once. Null otherwise.
    /// </summary>
    internal static double? SlowStep(ref SlowMeter m, long now, long frequency)
    {
        if (m.Fired) return null;
        if (m.Start == 0) { m.Start = now; m.Frames = 0; return null; }
        m.Frames++;
        var seconds = (now - m.Start) / (double)frequency;
        if (seconds < 1) return null;
        var fps = m.Frames / seconds;
        m.Start = now;
        m.Frames = 0;
        m.SlowRun = fps < SlowFps ? m.SlowRun + 1 : 0;
        if (m.SlowRun < SlowWindows) return null;
        m.Fired = true;
        return fps;
    }

    public const int SyncToleranceMs = 350;

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
    /// the same aspect ratio, long side capped at <paramref name="maxLongSide"/> (default
    /// <see cref="MaxLongSide"/>), both sides even (VLC's chroma planes want even dimensions)
    /// and at least 2 px. Unknown dimensions (0) fall back to a 16:9 buffer at the cap.
    /// </summary>
    public static (int Width, int Height) FitBuffer(int width, int height, int maxLongSide = MaxLongSide)
    {
        if (maxLongSide < 2) maxLongSide = MaxLongSide;
        if (width <= 0 || height <= 0) { width = maxLongSide; height = maxLongSide * 9 / 16; }
        var scale = Math.Min(1.0, (double)maxLongSide / Math.Max(width, height));
        var w = Math.Max(2, (int)Math.Round(width * scale)) & ~1;
        var h = Math.Max(2, (int)Math.Round(height * scale)) & ~1;
        return (Math.Max(2, w), Math.Max(2, h));
    }

    private Session? _session;
    private int _generation;
    private WriteableBitmap? _current;   // frame currently painted (owned by the session)
    private bool _currentDrawn;          // Render has picked _current up since it was set
    private readonly HostWindowWatch _hostWindow;

    public VideoBackdrop()
    {
        ClipToBounds = true;
        // Created before the handlers below so it has found the window by the time they run.
        _hostWindow = new HostWindowWatch(this, OnHostMinimizedChanged);
        AttachedToVisualTree += (_, _) => Refresh();
        DetachedFromVisualTree += (_, _) => Teardown();
    }

    // Park the decoder while the window can't be seen; resume where it left off.
    private void OnHostMinimizedChanged() => ApplyHold();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == IsActiveProperty)
            Refresh();
        else if (change.Property == IsPausedProperty)
            ApplyHold();
        else if (change.Property == SyncPositionProperty)
        {
            if (SyncPosition is { } pos) _session?.SyncTo((long)pos.TotalMilliseconds);
        }
        else if (change.Property == RateProperty)
            _session?.SetRate(Rate);
        else if (change.Property == StretchProperty || change.Property == CornerRadiusProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var frame = _current;
        if (frame == null) return;
        _currentDrawn = true;
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

    /// <summary>An http(s) stream (a music video from YouTube, 11-25) rather than a file.</summary>
    internal static bool IsRemoteSource(string? source) =>
        !string.IsNullOrEmpty(source)
        && (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    private void Refresh()
    {
        var source = Source;
        var remote = IsRemoteSource(source);
        var active = IsActive && !string.IsNullOrEmpty(source) && (remote || File.Exists(source))
                     && this.IsAttachedToVisualTree() && !IsKnownSlow(source);
        Teardown();
        if (!active || string.IsNullOrEmpty(source)) return;

        _countedActive = true;
        Interlocked.Increment(ref s_active);
        Register(this, true);
        if (SkipDecoderForTests) return;

        var generation = _generation;
        var logName = LogName;
        var stopWhenSlow = StopWhenSlow && SyncPosition == null;
        var maxFps = MaxFps;
        var startPaused = IsHeld;
        var synced = SyncPosition != null;
        var rate = Rate;
        var maxSide = MaxDecodeSide;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Session session;
            var hasVideo = false;
            try
            {
                // Probe the clip's dimensions first so the buffer keeps its aspect ratio.
                // A stream is probed over the network (a little longer; on a timeout the
                // buffer falls back to 16:9, which is what music videos are anyway).
                using var probe = new Media(SharedLibVlc.Instance, source, remote ? FromType.FromLocation : FromType.FromPath);
                probe.Parse(remote ? MediaParseOptions.ParseNetwork : MediaParseOptions.ParseLocal, timeout: remote ? 6000 : 3000)
                    .GetAwaiter().GetResult();
                int vw = 0, vh = 0;
                foreach (var track in probe.Tracks)
                {
                    if (track.TrackType != TrackType.Video) continue;
                    hasVideo = true;
                    vw = (int)track.Data.Video.Width;
                    vh = (int)track.Data.Video.Height;
                    break;
                }
                var (bw, bh) = FitBuffer(vw, vh, maxSide);
                session = new Session(this, bw, bh, probe.Duration, remote, source, generation)
                {
                    LogName = logName, ClipSize = $"{vw}x{vh}", ClipPixels = new PixelSize(vw, vh), StopWhenSlow = stopWhenSlow,
                    MinFrameTicks = maxFps > 0 ? Stopwatch.Frequency / maxFps : 0,
                };
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
                var from = remote ? FromType.FromLocation : FromType.FromPath;
                using var media = !synced
                    ? new Media(SharedLibVlc.Instance, source, from, ":no-audio", ":input-repeat=65535")
                    : remote
                        ? new Media(SharedLibVlc.Instance, source, from, ":no-audio", ":network-caching=1500")
                        : new Media(SharedLibVlc.Instance, source, from, ":no-audio");
                session.Player.Play(media);
                if (synced) session.SetRate(rate);
                if (startPaused) session.SetPaused(true);
                // A stream URL carries the user's IP: never into the log.
                DebugLogger.Info(DebugLogger.Category.Playback, logName + ".Start",
                    (remote ? "src=<stream>" : $"src={Path.GetFileName(source)}")
                    + $" clip={session.ClipSize} decode={session.Width}x{session.Height}{(startPaused ? " (paused)" : "")}");
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
                ClipPixelSize = session.ClipPixels;
                // Held (yielding, paused) since this start was queued: VLC takes the latest.
                session.SetPaused(IsHeld);
                // Line the clip up with the song as it is NOW. The source switches while the
                // track changes, before the song's position resets, so a position captured at
                // start-up could still be the previous song's.
                if (SyncPosition is { } now)
                {
                    if (Rate != rate) session.SetRate(Rate); // speed moved while starting up
                    session.SyncTo((long)now.TotalMilliseconds, force: true);
                }
            });
        });
    }

    private void Teardown()
    {
        if (_countedActive)
        {
            _countedActive = false;
            Interlocked.Decrement(ref s_active);
            Register(this, false);
        }
        _generation++;
        var session = _session;
        _session = null;
        _current = null;
        ClipPixelSize = default;
        HasFrame = false;
        session?.ShutDown();
        InvalidateVisual();
    }

    /// <summary>The decoded <paramref name="frame"/> goes on screen: the one UI-thread step of
    /// every frame. Only this control is invalidated (its own bounds repaint, nothing else).</summary>
    private void Present(WriteableBitmap frame)
    {
        _current = frame;
        _currentDrawn = false;
        HasVideoStream = true;
        HasFrame = true;
        FramesPresented++;
        InvalidateVisual();
    }

    /// <summary>Tests: a frame source without LibVLC through the same presentation step.</summary>
    internal void PresentForTests(WriteableBitmap frame) => Present(frame);

    /// <summary>The decoder could not keep up (<see cref="StopWhenSlow"/>): stop, keep the art.</summary>
    private void OnTooSlow(Session session, double fps)
    {
        if (!ReferenceEquals(_session, session) || Source is not { } source) return;
        MarkSlow(source);
        DebugLogger.Warn(DebugLogger.Category.Playback, LogName + ".TooSlow",
            $"decoder {fps:0.#} fps at {session.Width}x{session.Height} (clip {session.ClipSize}): stopped, still image kept for this clip");
        Teardown();
        HasVideoStream = false;
    }

    private sealed class Session
    {
        private readonly VideoBackdrop _owner;
        public readonly MediaPlayer Player;
        private readonly int _stride;
        private readonly int _bufferBytes;
        private readonly IntPtr _buffer;
        private readonly byte[] _scratch;
        // Frames rotate through these. A bitmap that is on screen is never written again
        // while the compositor can still draw it: the render thread repaints parts of the
        // page on its own (the animating lyrics), and a frame written in place showed up
        // in those parts one step before the rest of the page: a visible seam.
        private readonly WriteableBitmap[] _bitmaps = new WriteableBitmap[3];
        private int _nextBitmap;
        private volatile bool _framePending;
        private volatile bool _dead;
        private volatile bool _paused;
        private long _lengthMs;

        public readonly int Width, Height;
        public string LogName { get; init; } = "Backdrop";
        public string ClipSize { get; init; } = "?";
        public PixelSize ClipPixels { get; init; }
        public bool StopWhenSlow { get; init; }
        /// <summary>Shortest gap between two frames handed to the UI (Stopwatch ticks, 0 = none).</summary>
        public long MinFrameTicks { get; init; }

        // Stats for the Stop log line and the slow check. Decoder side on VLC's thread, the
        // presented side on the UI thread; play time counts only while not paused.
        private long _decoded, _presented, _uiTicks, _lastHanded;
        private long _playTicks, _playSince;
        private SlowMeter _slow;
        private int _slowReset;
        private volatile bool _handedAny;
        /// <summary>Pictures still shown while paused: a seek's new picture (and one spare).</summary>
        private int _pausedShows;
        private void AllowPausedShow() => Interlocked.Exchange(ref _pausedShows, 2);

        // Delegates must stay alive for the player's lifetime (VLC keeps raw pointers).
        private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
        private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

        public Session(VideoBackdrop owner, int width, int height, long lengthMs, bool remote = false, string? source = null, int generation = 0)
        {
            _owner = owner;
            _lengthMs = lengthMs;
            _source = source;
            _ownerGeneration = generation;
            Width = width;
            Height = height;
            _playSince = Stopwatch.GetTimestamp();
            _stride = width * 4;
            _bufferBytes = _stride * height;
            _buffer = Marshal.AllocHGlobal(_bufferBytes);
            _scratch = new byte[_bufferBytes];
            for (var i = 0; i < _bitmaps.Length; i++)
                _bitmaps[i] = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _lockCb = OnLock;
            _displayCb = OnDisplay;
            Player = new MediaPlayer(SharedLibVlc.Instance) { EnableHardwareDecoding = false, Mute = true };
            Player.SetVideoFormat("RV32", (uint)width, (uint)height, (uint)_stride);
            Player.SetVideoCallbacks(_lockCb, null, _displayCb);
            if (remote)
            {
                _gate = new StreamSyncGate();
                _capTimer = new Timer(_ => { if (!_dead && _gate.OnCapElapsed()) KickRemoteSync(); });
                // VLC's event thread: no player calls in here, only flags and a post.
                Player.EncounteredError += OnPlayerError;
                Player.EndReached += OnPlayerEndReached;
                // Cache progress while it starts or re-buffers: slow, not stalled (StreamSyncGate).
                Player.Buffering += OnPlayerBuffering;
            }
            Interlocked.Increment(ref s_liveSessions); // ShutDown takes it off once disposed
        }

        public void SetPaused(bool paused)
        {
            if (_dead) return;
            var wasPaused = _paused;
            _paused = paused;
            if (wasPaused && !paused) _gate?.Resumed();
            if (wasPaused != paused)
            {
                var now = Stopwatch.GetTimestamp();
                if (paused) Interlocked.Add(ref _playTicks, now - Interlocked.Exchange(ref _playSince, 0));
                else Interlocked.Exchange(ref _playSince, now);
                Interlocked.Exchange(ref _slowReset, 1); // the slow check starts over
            }
            ThreadPool.QueueUserWorkItem(_ => { try { Player.SetPause(paused); } catch { } });
        }

        /// <summary>Plays the clip at <paramref name="rate"/> (clamped to VLC's sane range).</summary>
        public void SetRate(double rate)
        {
            if (_dead || double.IsNaN(rate) || rate <= 0) return;
            var r = (float)Math.Clamp(rate, 0.25, 4.0);
            _rate = r;
            ThreadPool.QueueUserWorkItem(_ => { try { Player.SetRate(r); } catch { } });
        }

        private int _syncInFlight;
        // A stream (11-25 follow-ups): seeks and failure checks go through the gate, see
        // StreamSyncGate. Null for a local clip, which keeps the plain path below.
        private readonly string? _source;
        private readonly int _ownerGeneration;
        private readonly StreamSyncGate? _gate;
        private readonly Timer? _capTimer;
        private volatile float _rate = 1f;
        private int _landCheck;
        private int _failed;

        /// <summary>
        /// Keeps the clip within <see cref="SyncToleranceMs"/> of the song. Reading and
        /// setting the native time happens off the UI thread; at most one check runs at a
        /// time so a busy position stream cannot queue seeks. An ended clip is restarted
        /// (see <see cref="PlanSync"/>).
        /// </summary>
        public void SyncTo(long targetMs, bool force = false)
        {
            if (_dead) return;
            if (_gate != null)
            {
                _gate.Want(targetMs, force, !_paused, _rate);
                if (!_paused && !_gate.IsSeeking && _gate.IsStalled(targetMs, Interlocked.Read(ref _lengthMs)))
                    Fail("stalled");
                KickRemoteSync();
                return;
            }
            if (Interlocked.CompareExchange(ref _syncInFlight, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var length = Player.Length;
                    if (length > 0) _lengthMs = length; // 0 once ended — keep the last known
                    switch (PlanSync(Player.State == VLCState.Ended, Player.Time, targetMs, _lengthMs, SyncToleranceMs, force))
                    {
                        case SyncAction.Seek:
                            AllowPausedShow();
                            Player.Time = targetMs;
                            break;
                        case SyncAction.Restart:
                            AllowPausedShow();
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

        /// <summary>A stream's sync: the latest request only, one seek in flight at a time; the
        /// landing (first frame at the target) or the safety cap runs whatever came meanwhile.</summary>
        private void KickRemoteSync()
        {
            var gate = _gate;
            if (_dead || gate == null || !gate.HasWork) return;
            if (Interlocked.CompareExchange(ref _syncInFlight, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    while (!_dead && gate.TryTake(out var target, out var jumped))
                    {
                        var length = Player.Length;
                        if (length > 0) _lengthMs = length;
                        var (action, to) = gate.Plan(target, jumped, Player.State == VLCState.Ended, Player.Time,
                            _lengthMs, !_paused, _rate);
                        if (action == SyncAction.None) continue;
                        gate.SeekIssued(to);
                        AllowPausedShow();
                        _capTimer?.Change(StreamSyncGate.SeekCapMs + 50, Timeout.Infinite);
                        if (action == SyncAction.Restart)
                        {
                            Player.Stop();
                            Player.Play();
                        }
                        Player.Time = to;
                        if (action == SyncAction.Restart && _paused) Player.SetPause(true);
                    }
                }
                catch { }
                finally { Interlocked.Exchange(ref _syncInFlight, 0); }
                KickRemoteSync(); // a request that arrived while this one finished
            });
        }

        /// <summary>A frame showed while a seek is in flight: read the clock off VLC's thread
        /// and let the gate decide whether the seek landed.</summary>
        private void CheckLanding()
        {
            if (Interlocked.CompareExchange(ref _landCheck, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (!_dead && _gate!.OnFrame(Player.Time)) KickRemoteSync(); }
                catch { }
                finally { Interlocked.Exchange(ref _landCheck, 0); }
            });
        }

        private void OnPlayerError(object? sender, EventArgs e) => Fail("error");

        private void OnPlayerBuffering(object? sender, MediaPlayerBufferingEventArgs e)
        {
            if (!_dead) _gate?.NoteBuffering(e.Cache);
        }

        private void OnPlayerEndReached(object? sender, EventArgs e)
        {
            if (_gate != null && StreamSyncGate.IsPrematureEnd(_gate.LastTargetMs, Interlocked.Read(ref _lengthMs)))
                Fail("ended early");
        }

        /// <summary>Tells the owner, once, that this stream broke (it re-resolves the URL).</summary>
        private void Fail(string reason)
        {
            if (_dead || _gate == null || Interlocked.Exchange(ref _failed, 1) != 0) return;
            DebugLogger.Warn(DebugLogger.Category.Playback, "Backdrop.StreamFailed",
                _gate.IsAwaitingFrame ? reason + " (no first frame)" : reason);
            Dispatcher.UIThread.Post(() =>
            {
                if (_dead || _owner._generation != _ownerGeneration) return;
                var command = _owner.StreamFailedCommand;
                if (command?.CanExecute(_source) == true) command.Execute(_source);
            });
        }

        private IntPtr OnLock(IntPtr opaque, IntPtr planes)
        {
            Marshal.WriteIntPtr(planes, _buffer);
            return _buffer;
        }

        private void OnDisplay(IntPtr opaque, IntPtr picture)
        {
            if (_gate != null && !_dead)
            {
                _gate.NoteFrame();
                if (_gate.IsSeeking) CheckLanding();
            }
            if (_dead) return;
            // Paused, VLC keeps re-displaying the same picture (~12.5 a second, measured): each
            // one was copied twice and repainted the page (and, under the Settings sheet, made
            // its blur retake the whole app). Only the first picture and a seek's land on screen.
            if (_paused && _handedAny && Interlocked.Decrement(ref _pausedShows) < 0)
            {
                Interlocked.Exchange(ref _pausedShows, 0);
                return;
            }
            var now = Stopwatch.GetTimestamp();
            _decoded++;
            if (StopWhenSlow && !_paused) CheckSlow(now);
            if (_framePending) return;
            if (MinFrameTicks > 0 && now - _lastHanded < MinFrameTicks) return; // over MaxFps
            _lastHanded = now;
            _handedAny = true;
            _framePending = true;
            // Taken here, on VLC's thread, while the picture is complete: VLC starts writing
            // the next one into the same buffer as soon as this callback returns.
            Marshal.Copy(_buffer, _scratch, 0, _bufferBytes);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (_dead || _owner._session != this) return;
                    // The frame before this one has not been painted yet: keep it and drop
                    // this one, so the bitmaps already on screen stay untouched.
                    if (_owner._current != null && !_owner._currentDrawn) return;
                    var started = Stopwatch.GetTimestamp();
                    var bitmap = _bitmaps[_nextBitmap];
                    _nextBitmap = (_nextBitmap + 1) % _bitmaps.Length;
                    using (var fb = bitmap.Lock())
                        Marshal.Copy(_scratch, 0, fb.Address, _bufferBytes);
                    _owner.Present(bitmap); // first real frame reveals the clip
                    _presented++;
                    _uiTicks += Stopwatch.GetTimestamp() - started;
                }
                finally { _framePending = false; }
            }, DispatcherPriority.Render);
        }

        /// <summary>VLC's thread, per decoded frame while playing (see <see cref="SlowStep"/>).</summary>
        private void CheckSlow(long now)
        {
            if (Interlocked.Exchange(ref _slowReset, 0) == 1) _slow = default;
            if (SlowStep(ref _slow, now, Stopwatch.Frequency) is not { } fps) return;
            Dispatcher.UIThread.Post(() => { if (!_dead) _owner.OnTooSlow(this, fps); });
        }

        /// <summary>The Stop log line: how long it played and at what rates.</summary>
        private string Stats()
        {
            var play = Interlocked.Read(ref _playTicks);
            var since = Interlocked.Read(ref _playSince);
            if (since != 0) play += Stopwatch.GetTimestamp() - since;
            var seconds = play / (double)Stopwatch.Frequency;
            var shown = Interlocked.Read(ref _presented);
            var ui = shown > 0 ? Interlocked.Read(ref _uiTicks) * 1000.0 / Stopwatch.Frequency / shown : 0;
            return seconds > 0.05
                ? $"decode={Width}x{Height} played {seconds:0.#} s: decoder {Interlocked.Read(ref _decoded) / seconds:0.#} fps, shown {shown / seconds:0.#} fps, UI {ui:0.00} ms/frame"
                : $"decode={Width}x{Height} never played";
        }

        public void ShutDown()
        {
            if (_dead) return;
            _dead = true;
            DebugLogger.Info(DebugLogger.Category.Playback, LogName + ".Stop", Stats());
            try { _capTimer?.Dispose(); } catch { }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (_gate != null)
                {
                    try { Player.EncounteredError -= OnPlayerError; } catch { }
                    try { Player.EndReached -= OnPlayerEndReached; } catch { }
                    try { Player.Buffering -= OnPlayerBuffering; } catch { }
                }
                try { Player.Stop(); } catch { }
#pragma warning disable CS8625
                try { Player.SetVideoCallbacks(null, null, null); } catch { }
#pragma warning restore CS8625
                try { Player.Dispose(); } catch { }
                Marshal.FreeHGlobal(_buffer);
                Interlocked.Decrement(ref s_liveSessions);
                Dispatcher.UIThread.Post(() => { foreach (var b in _bitmaps) b.Dispose(); });
            });
        }
    }
}
