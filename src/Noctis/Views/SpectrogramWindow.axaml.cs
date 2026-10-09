using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Noctis.Controls;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class SpectrogramWindow : Window
{
    /// <summary>The plot fades in over this once the analysis lands (and the progress
    /// readout fades out over the same), on the pill pop-up's open curve.</summary>
    internal static readonly TimeSpan PlotFadeDuration = TimeSpan.FromMilliseconds(240);

    // Card width at full size: the composed image + the plot frame's padding + its margins.
    private const double CardMaxWidth = 1212;
    // Room kept around the card when the window is small.
    private const double WindowGutter = 48;

    private SpectrogramViewModel? _vm;

    public SpectrogramWindow()
    {
        InitializeComponent();

        // The plot slot is the composed image's own size, whether or not the image is there
        // yet: the card is the same size before and after the analysis lands (the Viewbox
        // scales slot and image together on a small window).
        var size = SpectrogramViewModel.ComposedSize;
        PlotSlot.Width = size.Width;
        PlotSlot.Height = size.Height;

        // Hidden until ready; both fades are code-built transitions on the shared easing
        // (SplineEasing is broken; CubicBezierEase is what the pop-ups use).
        PlotImage.Opacity = 0;
        PlotImage.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = PlotFadeDuration, Easing = PillDialogHost.OpenEase },
        };
        LoadingPanel.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = PlotFadeDuration, Easing = PillDialogHost.OpenEase },
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
            FitCardToWindow();
    }

    public SpectrogramWindow(SpectrogramViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        // PillDialogHost turns this into the animated close.
        vm.Closed += (_, _) => Close();
        vm.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.Dispose();
        };
        ApplyPhase();
    }

    /// <summary>Bounds the card by the window so it never runs off a small one; the plot
    /// scales down inside it.</summary>
    private void FitCardToWindow()
    {
        // ClientSize can change before InitializeComponent has run (base Window ctor).
        if (CardRoot is null) return;
        var client = ClientSize;
        if (client.Width <= 0 || client.Height <= 0) return;
        CardRoot.MaxWidth = Math.Max(320, Math.Min(CardMaxWidth, client.Width - WindowGutter));
        CardRoot.MaxHeight = Math.Max(280, client.Height - WindowGutter);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SpectrogramViewModel.IsReady) or nameof(SpectrogramViewModel.IsBusy)
            or nameof(SpectrogramViewModel.HasError))
            ApplyPhase();
    }

    /// <summary>Loading / error: the readout shows, the plot is hidden. Ready: the plot fades
    /// in while the readout fades out.</summary>
    private void ApplyPhase()
    {
        if (_vm is null) return;
        PlotImage.Opacity = _vm.IsReady ? 1 : 0;
        LoadingPanel.Opacity = _vm.IsReady ? 0 : 1;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_vm == null) return;
        // Axis labels sit on the black plot panel, so they are always light; the
        // theme text brush is only used when it reads on black (dark themes do).
        if (this.TryFindResource("SystemControlForegroundBaseHighBrush", out var brush) && brush is ISolidColorBrush solid
            && solid.Color.R + solid.Color.G + solid.Color.B > 3 * 128)
            _vm.AxisForeground = solid;
        else
            _vm.AxisForeground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        _ = _vm.RunAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm != null)
        {
            e.Handled = true;
            _vm.CloseCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// Every close route (Close, Esc, Alt+F4, the owner going away) stops the analysis the
    /// moment the close starts. PillDialogHost holds the window open for its close
    /// animation; without this, ffmpeg kept decoding through it (the view model was only
    /// disposed on Closed).
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _vm?.Cancel();
        base.OnClosing(e);
    }
}
