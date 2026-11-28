using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// The lyrics page's Background Color pop-up (Artwork / Solid / Gradient and their swatches),
/// in the shared rounded pill shell. It works on the page's own LyricsViewModel: every pick
/// runs the same command the old strip under the player bar ran, so the page behind repaints
/// at once and the choice is saved as before. Nothing is pending, so Done, Escape and a click
/// outside the card all just close.
/// </summary>
public partial class LyricsBackgroundColorDialog : Window
{
    // Switching modes: the shared pill slides to the chosen label and that mode's choices
    // fade in, rising a few pixels. Ease-out-quint, as the queue toast settles.
    private static readonly Avalonia.Animation.Easings.Easing Settle = new CubicBezierEase(0.22, 1, 0.36, 1);
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);
    private const double FadeRise = 8;

    public LyricsBackgroundColorDialog()
    {
        InitializeComponent();
        // On the window, not the host: outside the card the host draws nothing hit-testable,
        // so a press there reaches the window alone (as in SongsViewOptionsDialog).
        AddHandler(PointerPressedEvent, OnBackdropPointerPressed, RoutingStrategies.Tunnel);

        SegmentGrid.SizeChanged += (_, _) => PlaceIndicator(animate: false);
        foreach (var segment in Segments)
            segment.PropertyChanged += (_, e) =>
            {
                if (e.Property == ToggleButton.IsCheckedProperty && e.NewValue is true) PlaceIndicator(animate: IsLoaded);
            };
        foreach (var panel in new Control[] { ArtworkPanel, SolidPanel, GradientPanel })
            panel.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && e.NewValue is true && IsLoaded) FadeIn(panel);
            };

        // A pick repaints the page behind; once the picks stop, the blur is re-taken.
        DataContextChanged += (_, _) => WatchPicks(DataContext as LyricsViewModel);
        Closed += (_, _) => WatchPicks(null);
    }

    /// <summary>How long after the last pick the blur is re-taken: the page repaints at once
    /// (the background is a plain binding), and a quick run of picks costs one capture.</summary>
    private static readonly TimeSpan BackdropRetakeDelay = TimeSpan.FromMilliseconds(150);
    private LyricsViewModel? _watched;
    private int _pickGeneration;

    /// <summary>The wait between the last pick and the re-take (Task.Delay). Tests swap in a
    /// wait they end themselves: timing it by the wall clock made "nothing while picking"
    /// depend on how long the test thread's next frames took (a slow frame under load ran past
    /// the 150 ms and the re-take landed inside the check).</summary>
    internal Func<TimeSpan, Task> RetakeDelay { get; set; } = Task.Delay;

    private void WatchPicks(LyricsViewModel? vm)
    {
        if (_watched != null) _watched.PropertyChanged -= OnPickChanged;
        _watched = vm;
        if (vm != null) vm.PropertyChanged += OnPickChanged;
    }

    private async void OnPickChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LyricsViewModel.FullBackgroundBrush) or nameof(LyricsViewModel.IsColorModeArtwork)
            or nameof(LyricsViewModel.IsColorModeSolid) or nameof(LyricsViewModel.IsColorModeGradient))) return;
        // Only the last of a quick run of picks re-takes it (and none once closed).
        var generation = ++_pickGeneration;
        await RetakeDelay(BackdropRetakeDelay);
        if (generation != _pickGeneration || _watched is null) return;
        await Host.RefreshBackdropAsync();
    }

    private RadioButton[] Segments => new[] { ArtworkSegment, SolidSegment, GradientSegment };

    /// <summary>Puts the shared pill on the checked label's laid-out box (thirds round to
    /// whole pixels): in place at first, sliding after.</summary>
    private void PlaceIndicator(bool animate)
    {
        var checkedSegment = Array.Find(Segments, s => s.IsChecked == true);
        SegIndicator.IsVisible = checkedSegment is not null;
        if (checkedSegment is not { Bounds: { Width: > 0 } cell }) return;
        SegIndicator.Width = cell.Width;
        SegIndicator.Height = cell.Height;
        SegIndicator.Transitions = animate
            ? new Transitions { new TransformOperationsTransition { Property = RenderTransformProperty, Duration = SlideDuration, Easing = Settle } }
            : null;
        SegIndicator.RenderTransform = Translate(cell.X, cell.Y);
    }

    /// <summary>
    /// Fades a mode's choices in. Two steps: the start (clear, a little low) with no
    /// transitions, then the end once they are on (a code-built Animation can't drive
    /// RenderTransform here; a transition can).
    /// </summary>
    private static void FadeIn(Control panel)
    {
        panel.Transitions = null;
        panel.Opacity = 0;
        panel.RenderTransform = Translate(0, FadeRise);
        Dispatcher.UIThread.Post(() =>
        {
            panel.Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = FadeDuration, Easing = Settle },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = FadeDuration, Easing = Settle },
            };
            panel.Opacity = 1;
            panel.RenderTransform = Translate(0, 0);
        });
    }

    private static TransformOperations Translate(double x, double y)
    {
        var builder = TransformOperations.CreateBuilder(1);
        builder.AppendTranslate(x, y);
        return builder.Build();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Handled here so the key never reaches anything else; PillDialogHost turns the
        // Close into the animated one.
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnDoneClick(object? sender, RoutedEventArgs e) => Close();

    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Inside the card (its rounded corners included) is the card's business.
        if (new Rect(PickerCard.Bounds.Size).Contains(e.GetPosition(PickerCard))) return;
        e.Handled = true;
        Close();
    }
}
