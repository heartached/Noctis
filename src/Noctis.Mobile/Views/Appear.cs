using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;

namespace Noctis.Mobile.Views;

/// <summary>
/// A short fade-and-settle as a control arrives: rail tiles (a new pin glides in rather than
/// popping) and the Library's chip views. Two steps — the start values with no transitions,
/// then the end values posted with transitions attached — because a code-built Animation
/// cannot drive RenderTransform on Avalonia 12, while a TransformOperationsTransition can.
/// </summary>
public static class Appear
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(Appear));

    public static bool GetIsEnabled(Control element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(260);

    static Appear()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            control.AttachedToVisualTree -= OnAttached;
            if (args.GetNewValue<bool>()) control.AttachedToVisualTree += OnAttached;
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // A virtualised row coming back on screen (RowHost.Arrives) has arrived already.
        if (sender is Control control && control.FindAncestorOfType<RowHost>() is not { Arrives: false }) Play(control);
    }

    /// <summary>Runs the arrival on <paramref name="control"/> now.</summary>
    public static void Play(Control control)
    {
        control.Transitions = null;
        control.Opacity = 0;
        control.RenderTransform = TransformOperations.Parse("translateY(6px) scale(0.96)");
        Dispatcher.UIThread.Post(() =>
        {
            var ease = new CubicBezierEase(0.32, 0.72, 0, 1);
            control.Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = Duration, Easing = ease },
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = Duration, Easing = ease },
            };
            control.Opacity = 1;
            control.RenderTransform = TransformOperations.Parse("translateY(0px) scale(1)");
        }, DispatcherPriority.Background);
    }
}
