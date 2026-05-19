using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace Noctis.Controls;

/// <summary>
/// Plays a looping animated cover by decoding frames (via LibVLC video callbacks) into a
/// <see cref="WriteableBitmap"/> shown in a plain <c>Image</c> element — so it composes
/// inside transparent windows, clips to its parent's rounded border, and never spawns a
/// native output window. Software-decoded; fine for a small muted loop. Minor frame tearing
/// under load is accepted (single shared buffer).
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
    private MediaPlayer? _player;
    private IntPtr _buffer = IntPtr.Zero;          // native frame buffer VLC writes into
    private byte[]? _scratch;                       // managed hop for the IntPtr->IntPtr copy
    private WriteableBitmap? _bitmap;
    private volatile bool _framePending;            // coalesce UI invalidations

    // Keep delegate instances alive for the player's lifetime — VLC stores raw
    // function pointers and will crash if these are garbage-collected.
    private MediaPlayer.LibVLCVideoLockCb? _lockCb;
    private MediaPlayer.LibVLCVideoDisplayCb? _displayCb;

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
        var shouldPlay = IsActive && !string.IsNullOrEmpty(Source) && File.Exists(Source);
        if (!shouldPlay)
        {
            Teardown();
            return;
        }

        try
        {
            EnsurePlayer();
            using var media = new Media(SharedLibVlc.Instance, Source!, FromType.FromPath,
                ":no-audio", ":input-repeat=65535");
            _player!.Play(media);
        }
        catch
        {
            Teardown();
        }
    }

    private void EnsurePlayer()
    {
        if (_player != null) return;

        _buffer = Marshal.AllocHGlobal(BufferBytes);
        _scratch = new byte[BufferBytes];
        _bitmap = new WriteableBitmap(new PixelSize(RenderSize, RenderSize), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        // Don't show the bitmap yet — it holds uninitialized pixels until the first
        // decoded frame arrives; assigning it now flashes garbage on a tab switch.
        // OnDisplay assigns _image.Source on the first frame.

        // Software decoding is required for the frame-callback path.
        _player = new MediaPlayer(SharedLibVlc.Instance) { EnableHardwareDecoding = false, Mute = true };
        _lockCb = OnLock;
        _displayCb = OnDisplay;
        _player.SetVideoFormat("RV32", RenderSize, RenderSize, Stride);
        _player.SetVideoCallbacks(_lockCb, null, _displayCb);
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
        if (_framePending) return;
        _framePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _framePending = false;
            var bmp = _bitmap;
            var scratch = _scratch;
            // Safe against use-after-free: Teardown() also runs on the UI thread and
            // completes fully (stopping the player and zeroing _buffer) before any posted
            // closure is dequeued, so _buffer is either still valid or already zero here.
            if (bmp == null || scratch == null || _buffer == IntPtr.Zero) return;
            Marshal.Copy(_buffer, scratch, 0, BufferBytes);
            using (var fb = bmp.Lock())
                Marshal.Copy(scratch, 0, fb.Address, BufferBytes);
            _image.Source = bmp; // no-op after the first frame; reveals real content
            _image.InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    private void Teardown()
    {
        // Stop() is synchronous in LibVLC 3.x — after it returns, no more callbacks fire,
        // so it is safe to free the native buffer afterwards. Any UI-thread frame closure
        // already posted by OnDisplay will see _buffer == IntPtr.Zero and bail.
        var player = _player;
        _player = null;
        if (player != null)
        {
            try { player.Stop(); } catch { }
            try { player.SetVideoCallbacks(null, null, null); } catch { }
            try { player.Dispose(); } catch { }
        }
        _lockCb = null;
        _displayCb = null;

        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _scratch = null;

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }

        // NEVER dispose the shared LibVLC — it's reused across surfaces.
    }
}
