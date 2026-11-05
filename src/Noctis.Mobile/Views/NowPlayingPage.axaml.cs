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
    /// process, drawn as a tiled image, so it costs nothing per track or per frame.
    /// </summary>
    internal static IBrush Dither => _dither ??= MakeDither();

    private static IBrush MakeDither()
    {
        var bitmap = new WriteableBitmap(new PixelSize(DitherSize, DitherSize), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var rng = new Random(0x5EED);   // fixed: the tile is the same every launch
        var row = new byte[DitherSize * 4];
        using (var fb = bitmap.Lock())
        {
            for (var y = 0; y < DitherSize; y++)
            {
                for (var x = 0; x < DitherSize; x++)
                {
                    var a = (byte)rng.Next(DitherAmplitude + 1);
                    var white = rng.Next(2) == 0;
                    row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = white ? a : (byte)0;   // premultiplied
                    row[x * 4 + 3] = a;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
            }
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
        WashDither.Background = Dither;

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
    }

    /// <summary>The cover's sideways swipe (internal for tests).</summary>
    internal TrackSwipeGesture ArtworkSwipe { get; }

    private ShellViewModel? _shell;

    // The disc turns only while someone can see it: the page open (not closed, not under the
    // lyrics) and the app in front (ShellViewModel.IsSuspended: home, task switch, screen off).
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell != null) _shell.PropertyChanged -= OnShellChanged;
        _shell = DataContext as ShellViewModel;
        if (_shell != null) _shell.PropertyChanged += OnShellChanged;
        UpdateDiscActive();
    }

    private void OnShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsSuspended)) UpdateDiscActive();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) UpdateDiscActive();
    }

    private void UpdateDiscActive()
    {
        if (Disc is { } disc) disc.IsActive = IsVisible && _shell is not { IsSuspended: true };
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
