using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class NowPlayingPage : UserControl
{
    /// <summary>The dither tile's side, and how far a pixel may move (in 8-bit levels).</summary>
    internal const int DitherSize = 64, DitherAmplitude = 5;

    private static IBrush? _dither;

    /// <summary>
    /// The wash's dither (the page's third layer): a 64 px tile of noise, each pixel black or
    /// white at a random alpha up to <see cref="DitherAmplitude"/>/255, tiled over the screen. A
    /// gradient this long (a 2400 px screen falls through ~110 levels) otherwise shows its 8-bit
    /// steps as bands; noise of a few levels, invisible as texture, scatters them. Made once per
    /// process. The pages draw the tile through DitherLayer (one Skia shader); this brush is its
    /// fallback where Skia is not drawing (headless tests).
    /// </summary>
    internal static IBrush Dither => _dither ??= MakeDither();

    /// <summary>The tile's pixels, BGRA premultiplied, row after row (DitherLayer draws the same tile).</summary>
    internal static byte[] DitherPixels()
    {
        var rng = new Random(0x5EED);   // fixed: the tile is the same every launch
        var pixels = new byte[DitherSize * DitherSize * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = (byte)rng.Next(DitherAmplitude + 1);
            var white = rng.Next(2) == 0;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = white ? a : (byte)0;   // premultiplied
            pixels[i + 3] = a;
        }
        return pixels;
    }

    private static IBrush MakeDither()
    {
        var bitmap = new WriteableBitmap(new PixelSize(DitherSize, DitherSize), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var pixels = DitherPixels();
        using (var fb = bitmap.Lock())
        {
            for (var y = 0; y < DitherSize; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * DitherSize * 4, fb.Address + y * fb.RowBytes, DitherSize * 4);
        }
        return new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            // Filled into its 64 dp cell: Stretch.None drew the 64 px bitmap at its pixel size on
            // the phone (24 dp at 2.625x), a patch per cell with bare gaps between (10-14).
            Stretch = Stretch.Fill,
            DestinationRect = new RelativeRect(0, 0, DitherSize, DitherSize, RelativeUnit.Absolute),
        };
    }

    public NowPlayingPage()
    {
        InitializeComponent();

        // The bar is a CapsuleSlider: it captures the finger itself and brackets the drag with
        // these, so a gesture taken away by the system (a notification, the shade) arrives as
        // ScrubCanceled. The ViewModel's seek watchdog stays the backstop for a gesture that
        // ends with no pointer event at all (device, 2026-09-22).
        SeekBar.ScrubStarted += (_, _) => (DataContext as ShellViewModel)?.Player.BeginSeek();
        SeekBar.ScrubCompleted += OnSeekCompleted;
        SeekBar.ScrubCanceled += (_, _) => (DataContext as ShellViewModel)?.Player.EndSeek();
        // The times follow the drag (PreviewSeek is a no-op unless a seek is held, so the
        // position pushes that move the bar between drags never come back through here).
        SeekBar.ValueChanged += OnSeekValueChanged;
        // Swipe the cover (or the record, the CD) sideways for the next or previous song.
        ArtworkSwipe = new TrackSwipeGesture(ArtworkBox, ArtworkBox, ArtworkGhostBox, GhostArtwork, this, gap: 24,
            () => _shell?.Player,
            () => _shell is { IsNowPlayingOpen: true, IsLyricsOpen: false, IsQueueOpen: false, IsMenuOpen: false });
        // A finger on the upper half (the sheet's drag-down, the cover's swipe) moves the cover
        // under the native video, which does not follow a transform: the video steps aside.
        AddHandler(PointerPressedEvent, OnAnyPress, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>The cover's sideways swipe (internal for tests).</summary>
    internal TrackSwipeGesture ArtworkSwipe { get; }

    private ShellViewModel? _shell;
    private NowPlayingViewModel? _player;
    private NowPlayingMotion? _motion;

    // The disc turns only while someone can see it: the page open (not closed, not under the
    // lyrics) and the app in front (ShellViewModel.IsSuspended: home, task switch, screen off).
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell != null) _shell.PropertyChanged -= OnShellChanged;
        if (_player != null) _player.PropertyChanged -= OnMotionInputChanged;
        if (_motion != null) _motion.PropertyChanged -= OnMotionInputChanged;
        _shell = DataContext as ShellViewModel;
        _player = _shell?.Player;
        _motion = _shell?.Motion;
        if (_shell != null) _shell.PropertyChanged += OnShellChanged;
        if (_player != null) _player.PropertyChanged += OnMotionInputChanged;
        if (_motion != null) _motion.PropertyChanged += OnMotionInputChanged;
        UpdateDiscActive();
        UpdateMotion();
    }

    private void OnShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsSuspended)) UpdateDiscActive();
        if (e.PropertyName == nameof(ShellViewModel.IsNowPlayingOpen)) _held = false;
        if (e.PropertyName is nameof(ShellViewModel.IsSuspended) or nameof(ShellViewModel.IsNowPlayingOpen)
            or nameof(ShellViewModel.IsLyricsOpen) or nameof(ShellViewModel.IsQueueOpen) or nameof(ShellViewModel.IsMenuOpen)
            or nameof(ShellViewModel.IsSheetOpen) or nameof(ShellViewModel.IsPromptOpen) or nameof(ShellViewModel.IsAlbumNoteOpen)
            or nameof(ShellViewModel.ShowsDiscArtwork) or nameof(ShellViewModel.AnimatedCovers))
            UpdateMotion();
    }

    private void OnMotionInputChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingViewModel.IsPlaying) or nameof(NowPlayingViewModel.CurrentTrack) or nameof(NowPlayingMotion.Path))
            UpdateMotion();
    }

    // A finger down on the cover or the sheet's upper half; up again anywhere (the release is
    // watched at the window: the sheet's drag captures the pointer above this page).
    private bool _held;
    private TopLevel? _releaseWatch;

    private void OnAnyPress(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var at = e.GetPosition(this);
        var onCover = ArtworkBox.Bounds.Contains(e.GetPosition(Layout)) || at.Y <= Bounds.Height / 2;
        if (!onCover) return;
        _held = true;
        if (_releaseWatch is null && TopLevel.GetTopLevel(this) is { } top)
        {
            _releaseWatch = top;
            top.AddHandler(PointerReleasedEvent, OnAnyRelease, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        UpdateMotion();
    }

    private void OnAnyRelease(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (!_held) return;
        _held = false;
        UpdateMotion();
    }

    /// <summary>
    /// Feeds the animated cover (MotionCover): the song's loop, and whether the cover is seen and
    /// still - the page landed and on top (nothing over it: lyrics, queue, a menu, a sheet, a
    /// question), the Cover style, the music playing (paused, the cover shrinks back, which the
    /// native video cannot follow), the app in front, no Battery Saver, no finger on it, no swipe.
    /// </summary>
    private void UpdateMotion()
    {
        WatchBatterySaver();
        if (MotionCover is not { } view) return;
        var shell = _shell;
        view.Source = shell?.Motion?.Path;
        view.IsSeen = IsMotionSeen(shell, IsVisible, _held || ArtworkSwipe?.IsSwiping == true);
    }

    private Noctis.Mobile.Services.IBatterySaver? _saver;

    /// <summary>Battery Saver turned on or off stops or starts the loop at once (it waited for a
    /// pause, a new song or a menu). Listened to only while the page shows: on Android, listening
    /// keeps a system broadcast receiver registered.</summary>
    private void WatchBatterySaver()
    {
        var saver = IsVisible ? _shell?.BatterySaver : null;
        if (ReferenceEquals(saver, _saver)) return;
        if (_saver != null) _saver.Changed -= OnBatterySaverChanged;
        _saver = saver;
        if (_saver != null) _saver.Changed += OnBatterySaverChanged;
    }

    private void OnBatterySaverChanged(object? sender, EventArgs e) => UpdateMotion();

    /// <summary>Whether Now Playing's cover is on screen, uncovered and still (internal for tests).</summary>
    internal static bool IsMotionSeen(ShellViewModel? shell, bool pageVisible, bool held) =>
        shell is { IsNowPlayingOpen: true, IsLyricsOpen: false, IsQueueOpen: false, IsMenuOpen: false, IsSheetOpen: false,
            IsPromptOpen: false, IsAlbumNoteOpen: false, IsSuspended: false, ShowsDiscArtwork: false }
        && shell.AnimatedCovers != AnimatedCoversSetting.Off
        && shell.Player.IsPlaying
        && shell.BatterySaver?.IsOn != true
        && pageVisible && !held;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            UpdateDiscActive();
            UpdateMotion();
        }
    }

    private void UpdateDiscActive()
    {
        if (Disc is { } disc) disc.IsActive = IsVisible && _shell is not { IsSuspended: true };
        if (TimelineRow is not null) FeedTimeline(TimelineRow, SeekBar, IsVisible);
    }

    /// <summary>
    /// The seek bar and its times follow the player's four position ticks a second, and each tick
    /// marked them for redraw even on a hidden page: a render pass every tick and, once a second,
    /// both times' text laid out again, for the closed Now Playing and its lyrics alike, all the
    /// while music played (MobileHiddenTimelineTests). While hidden, <paramref name="row"/> has no
    /// data (its bindings go quiet); shown again, it takes the page's data back and reads the
    /// current values before it is drawn. Never mid-drag. (A page on screen when the app goes to
    /// the background stays fed: the system's snapshot of it must not show a blank bar.)
    /// </summary>
    internal static void FeedTimeline(Control row, CapsuleSlider seekBar, bool seen)
    {
        if (seen) row.ClearValue(DataContextProperty);
        else if (!seekBar.IsScrubbing) row.DataContext = null;
    }

    private bool? _landscape;

    /// <summary>
    /// Rotation does not recreate the activity, so the page re-lays itself out on resize. In a
    /// landscape phone window (~412 dp tall) the portrait column leaves the cover's row almost
    /// nothing once the title, seek bar, transport, volume and bottom icons have their height;
    /// side by side, the cover gets the full height on the left and the controls fit on the right.
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyOrientation(e.NewSize.Width > e.NewSize.Height);
    }

    private void ApplyOrientation(bool landscape)
    {
        if (_landscape == landscape) return;
        _landscape = landscape;
        Layout.ColumnDefinitions = landscape ? new ColumnDefinitions("5*,6*") : new ColumnDefinitions("*");
        foreach (var child in Layout.Children)
            if (child != ArtworkBox && child != ArtworkGhostBox) Grid.SetColumn(child, landscape ? 1 : 0);
        // The swipe's incoming cover stands exactly where the cover does.
        foreach (var box in new Control[] { ArtworkBox, ArtworkGhostBox })
        {
            Grid.SetRow(box, landscape ? 0 : 1);
            Grid.SetRowSpan(box, landscape ? Layout.RowDefinitions.Count : 1);
            box.Margin = landscape ? new Thickness(0, 0, 24, 0) : new Thickness(0, 8);
        }
        // A 360 dp tall window (Galaxy S23 at 1080 px) leaves the right column no slack: with the
        // portrait gaps the Lyrics/Output/Queue row hung half below the screen. Landscape closes
        // the gaps between the rows; portrait keeps its breathing room.
        TitleRow.Margin = landscape ? new Thickness(0, 4, 0, 0) : new Thickness(0, 12, 0, 0);
        TransportRow.Margin = landscape ? new Thickness(0, 6) : new Thickness(0, 14);
        BottomIcons.Margin = landscape ? new Thickness(0, 4, 0, 0) : new Thickness(0, 12, 0, 0);
    }

    // Seek on release only: seeking per pixel during a drag stutters the decoder. Held
    // between ScrubStarted and here, the player's 4 Hz position ticks leave the bar alone.
    private void OnSeekCompleted(object? sender, EventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        vm.Player.EndSeek();
        vm.Player.Seek(TimeSpan.FromSeconds(SeekBar.Value));
    }

    private void OnSeekValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        => (DataContext as ShellViewModel)?.Player.PreviewSeek(TimeSpan.FromSeconds(e.NewValue));
}
