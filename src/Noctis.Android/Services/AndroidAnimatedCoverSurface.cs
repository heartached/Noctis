using Android.Content;
using Android.Graphics;
using Android.Views;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using Avalonia.Android;
using Avalonia.Platform;
using Noctis.Mobile.Views;
using Noctis.Services;
using AUri = Android.Net.Uri;
using JFile = Java.IO.File;
using View = Android.Views.View;

namespace Noctis.Android.Services;

/// <summary>
/// Now Playing's animated cover on the phone (AnimatedCoverView hosts it over the still cover):
/// a TextureView fed by an ExoPlayer of its own. That player is NOT the music player and touches
/// nothing of it: its audio track type is disabled (no audio renderer runs, so no AudioTrack),
/// its volume is 0, and it is never given audio attributes, so it never asks for audio focus
/// (ExoPlayer handles focus only when told to); it has no MediaSession and no wake lock. It
/// loops one file (REPEAT_MODE_ONE: ExoPlayer starts the next pass before this one ends).
///
/// A TextureView, not a SurfaceView: it is part of the view hierarchy, so it can be clipped to the
/// cover's rounded corners (an outline) and faded, and it sits over the Avalonia surface without
/// z-order tricks. It stays see-through (alpha 0) until the first frame of a load or a resume has
/// reached it, then fades in, so the still cover under it shows until there is a picture. Not
/// clickable: touches fall through to Avalonia (the cover's swipe, the sheet's drag).
/// Main thread only (ExoPlayer's application looper is the main looper here).
/// </summary>
internal sealed class AndroidAnimatedCoverSurface : Java.Lang.Object, IAnimatedCoverSurface, TextureView.ISurfaceTextureListener
{
    // Player.REPEAT_MODE_ONE (the interface constant is not bound on IPlayer).
    private const int RepeatModeOne = 1;
    private const long FadeInMs = 250;
    // The still cover's corner: 10 on its 320 box (NowPlayingPage.axaml).
    private const float CornerShare = 10f / 320f;

    private readonly Context _context;
    private TextureView? _view;
    private Surface? _surface;
    private IExoPlayer? _player;
    private Listener? _listener;
    private string? _source;
    private bool _waitingForFrame;
    private int _videoWidth, _videoHeight;

    public AndroidAnimatedCoverSurface(Context context) => _context = context;

    public event EventHandler? Failed;

    public IPlatformHandle CreateView(IPlatformHandle parent)
    {
        DestroyView();
        var view = new TextureView(_context)
        {
            Alpha = 0f,
            Clickable = false,
            Focusable = false,
            ImportantForAccessibility = ImportantForAccessibility.No,
            OutlineProvider = new RoundCorners(),
            ClipToOutline = true,
        };
        view.SetOpaque(false);
        view.SurfaceTextureListener = this;
        _view = view;
        return new AndroidViewControlHandle(view);
    }

    public void DestroyView()
    {
        Release();
        if (_view is { } view) view.SurfaceTextureListener = null;
        _view = null;
        ReleaseSurface();
    }

    public void Play(string source)
    {
        try
        {
            if (_player is null) CreatePlayer();
            var player = _player!;
            if (!string.Equals(source, _source, StringComparison.Ordinal))
            {
                _source = source;
                _videoWidth = _videoHeight = 0;
                player.SetMediaItem(MediaItem.FromUri(ToUri(source)));
                player.Prepare();
            }
            if (_surface is not null) player.SetVideoSurface(_surface);
            // See-through until a frame of this pass is drawn (OnSurfaceTextureUpdated).
            _waitingForFrame = true;
            if (_view is { } view)
            {
                view.Animate()?.Cancel();
                view.Alpha = 0f;
            }
            player.PlayWhenReady = true;
        }
        catch (Exception ex)
        {
            DebugLog.Write("AnimatedCover", $"video player failed to start: {ex.GetType().Name}");
            Failed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Pause()
    {
        _waitingForFrame = false;
        if (_view is { } view)
        {
            view.Animate()?.Cancel();
            view.Alpha = 0f;
        }
        if (_player is { } player) player.PlayWhenReady = false;
    }

    public void Release()
    {
        _waitingForFrame = false;
        _source = null;
        if (_view is { } view)
        {
            view.Animate()?.Cancel();
            view.Alpha = 0f;
        }
        if (_player is not { } player) return;
        _player = null;
        try
        {
            if (_listener is not null) player.RemoveListener(_listener);
            player.ClearVideoSurface();
            player.Release();
        }
        catch (Exception ex)
        {
            DebugLog.Write("AnimatedCover", $"video player release failed: {ex.GetType().Name}");
        }
        player.Dispose();
        _listener?.Dispose();
        _listener = null;
    }

    private void CreatePlayer()
    {
        var player = new ExoPlayerBuilder(_context).Build()!;
        // No sound at all: the audio track is never selected, so no audio renderer, no AudioTrack.
        player.TrackSelectionParameters = player.TrackSelectionParameters!.BuildUpon()!
            .SetTrackTypeDisabled(C.TrackTypeAudio, true)!.Build()!;
        player.Volume = 0f;
        player.RepeatMode = RepeatModeOne;
        _listener = new Listener(this);
        player.AddListener(_listener);
        _player = player;
    }

    private static AUri ToUri(string source) =>
        source.StartsWith("content://", StringComparison.Ordinal) || source.StartsWith("file://", StringComparison.Ordinal)
            ? AUri.Parse(source)!
            : AUri.FromFile(new JFile(source))!;

    private void OnError(PlaybackException? error)
    {
        DebugLog.Write("AnimatedCover", $"video error {error?.ErrorCodeName}");
        Failed?.Invoke(this, EventArgs.Empty);
    }

    private void OnVideoSize(VideoSize size)
    {
        _videoWidth = (int)Math.Round(size.Width * (size.PixelWidthHeightRatio > 0 ? size.PixelWidthHeightRatio : 1f));
        _videoHeight = size.Height;
        ApplyCrop();
    }

    /// <summary>A TextureView stretches the picture to its box: a loop that is not square is
    /// scaled back to its own shape and cropped to fill the box, as the still cover's UniformToFill.</summary>
    private void ApplyCrop()
    {
        if (_view is not { } view || _videoWidth <= 0 || _videoHeight <= 0 || view.Width <= 0 || view.Height <= 0) return;
        float viewW = view.Width, viewH = view.Height;
        var scale = Math.Max(viewW / _videoWidth, viewH / _videoHeight);
        using var matrix = new Matrix();
        matrix.SetScale(_videoWidth * scale / viewW, _videoHeight * scale / viewH, viewW / 2, viewH / 2);
        view.SetTransform(matrix);
    }

    private void ReleaseSurface()
    {
        _surface?.Release();
        _surface?.Dispose();
        _surface = null;
    }

    // ── TextureView.ISurfaceTextureListener ──

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
    {
        ReleaseSurface();
        _surface = new Surface(surface);
        _player?.SetVideoSurface(_surface);
        ApplyCrop();
    }

    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        _player?.ClearVideoSurface();
        ReleaseSurface();
        return true;
    }

    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) => ApplyCrop();

    /// <summary>A new frame reached the view: the first of a load or a resume fades the video in.</summary>
    public void OnSurfaceTextureUpdated(SurfaceTexture surface)
    {
        if (!_waitingForFrame || _view is not { } view) return;
        _waitingForFrame = false;
        view.Animate()?.Alpha(1f)?.SetDuration(FadeInMs)?.Start();
    }

    /// <summary>The still cover's rounded corners, kept as the view's size changes (rotation, landscape).</summary>
    private sealed class RoundCorners : ViewOutlineProvider
    {
        public override void GetOutline(View? view, Outline? outline)
        {
            if (view is null || outline is null) return;
            outline.SetRoundRect(0, 0, view.Width, view.Height, view.Width * CornerShare);
        }
    }

    /// <summary>Java-side listener; every IPlayerListener method has a default body, so only these are overridden.</summary>
    private sealed class Listener : Java.Lang.Object, IPlayerListener
    {
        private readonly AndroidAnimatedCoverSurface _owner;
        public Listener(AndroidAnimatedCoverSurface owner) => _owner = owner;
        public void OnPlayerError(PlaybackException? error) => _owner.OnError(error);
        public void OnVideoSizeChanged(VideoSize? videoSize) { if (videoSize is not null) _owner.OnVideoSize(videoSize); }

        /// <summary>Disposed at once, as Media3AudioPlayer's: an undisposed peer per event batch
        /// makes the next minor GC run the Java GC bridge (the 11-17 lyrics stalls).</summary>
        public void OnEvents(IPlayer? player, PlayerEvents? events) => events?.Dispose();
    }
}
