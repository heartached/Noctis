using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Noctis.Mobile.Views;

/// <summary>
/// Long-press → command, for rows and tiles. Uses Avalonia 12's InputElement.HoldingEvent
/// (raised by the gesture recogniser for touch after the platform's hold wait). A long press
/// on a Button must not also click it when the finger lifts, so the release that follows a
/// fired hold is marked handled on the tunnel route, before the Button's own release
/// handling runs. The parameter defaults to the control's DataContext.
/// </summary>
public static class LongPress
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(LongPress));

    public static readonly AttachedProperty<object?> CommandParameterProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("CommandParameter", typeof(LongPress));

    private static readonly AttachedProperty<bool> FiredProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Fired", typeof(LongPress));

    public static ICommand? GetCommand(Control element) => element.GetValue(CommandProperty);
    public static void SetCommand(Control element, ICommand? value) => element.SetValue(CommandProperty, value);
    public static object? GetCommandParameter(Control element) => element.GetValue(CommandParameterProperty);
    public static void SetCommandParameter(Control element, object? value) => element.SetValue(CommandParameterProperty, value);

    static LongPress()
    {
        CommandProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            control.RemoveHandler(InputElement.HoldingEvent, OnHolding);
            control.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
            if (args.NewValue is null) return;
            InputElement.SetIsHoldingEnabled(control, true);
            control.AddHandler(InputElement.HoldingEvent, OnHolding);
            control.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        });
    }

    private static void OnHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (sender is not Control control || e.HoldingState != HoldingState.Started) return;
        if (Fire(control)) e.Handled = true;
    }

    /// <summary>Runs the long-press command. Internal so tests can drive it without a touch screen.</summary>
    internal static bool Fire(Control control)
    {
        var command = control.GetValue(CommandProperty);
        var parameter = control.GetValue(CommandParameterProperty) ?? control.DataContext;
        if (command?.CanExecute(parameter) != true) return false;
        control.SetValue(FiredProperty, true);
        command.Execute(parameter);
        return true;
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control control || !control.GetValue(FiredProperty)) return;
        control.SetValue(FiredProperty, false);
        e.Handled = true;   // the finger lifting after a long press is not a tap
    }
}
