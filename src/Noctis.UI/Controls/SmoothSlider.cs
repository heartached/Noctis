using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Noctis.Controls;

/// <summary>
/// A Slider whose thumb follows the pointer continuously while the setting it drives moves
/// in whole steps (10-12). Settings' knobs used to snap the thumb itself (IsSnapToTickEnabled
/// with a 0.1 / 1 / 50 step), and an int-typed setting snapped it too through the binding's
/// round trip, so dragging the Kawarp Blur (15 steps) or Minimum Line Opacity thumb jumped
/// from notch to notch. Bind the setting to <see cref="SteppedValue"/>, not Value: the thumb
/// (Value) is never rounded, SteppedValue is Value rounded to <see cref="Step"/>, and a
/// setting change from elsewhere (load, reset, double-tap default) moves the thumb only when
/// it lands on a different step than the one the thumb is already within. Shared (Noctis.UI)
/// so the phone's Settings sliders glide the same way (10-13).
/// </summary>
public class SmoothSlider : Slider
{
    /// <summary>Granularity of <see cref="SteppedValue"/>; 0 passes Value through unrounded.</summary>
    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<SmoothSlider, double>(nameof(Step));

    /// <summary>The value the setting sees: Value rounded to <see cref="Step"/> from Minimum.</summary>
    public static readonly StyledProperty<double> SteppedValueProperty =
        AvaloniaProperty.Register<SmoothSlider, double>(nameof(SteppedValue), defaultBindingMode: BindingMode.TwoWay);

    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public double SteppedValue { get => GetValue(SteppedValueProperty); set => SetValue(SteppedValueProperty, value); }

    protected override Type StyleKeyOverride => typeof(Slider);

    private bool _syncing;

    internal double Snap(double value)
    {
        // XAML sets Minimum before Maximum, so a range above the stock 0-100 (Queue Width,
        // 300-720) passes through Minimum > Maximum; Math.Clamp throws on that (10-29).
        var max = Math.Max(Minimum, Maximum);
        var v = Math.Clamp(value, Minimum, max);
        if (Step > 0)
            v = Math.Clamp(Minimum + Math.Round((v - Minimum) / Step) * Step, Minimum, max);
        // 0.1 steps accumulate binary noise (0.30000000000000004); the label and the saved
        // setting should read the step itself.
        return Math.Round(v, 6);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_syncing) return;

        if (change.Property == ValueProperty || change.Property == StepProperty)
        {
            var stepped = Snap(Value);
            if (stepped != SteppedValue)
                Sync(() => SetCurrentValue(SteppedValueProperty, stepped));
        }
        else if (change.Property == SteppedValueProperty)
        {
            if (Snap(Value) != Snap(SteppedValue))
                Sync(() => PlaceThumb(SteppedValue));
        }
        else if (change.Property == MinimumProperty || change.Property == MaximumProperty)
        {
            // A range set after the setting arrived (attribute order) re-places the thumb.
            if (Snap(Value) != Snap(SteppedValue))
                Sync(() => PlaceThumb(SteppedValue));
        }
    }

    /// <summary>
    /// Puts the thumb on a setting that arrived from outside, as a local value (10-13). A value
    /// set with SetCurrentValue does not survive a style re-evaluation: attaching the slider to
    /// the tree (its implicit theme) or detaching it put Value back to its coerced default, the
    /// Minimum, and the Value → SteppedValue sync above then wrote that Minimum into the setting
    /// (the phone's Settings set the lyrics text size to 80 % when opened and when closed).
    /// Drags still move Value through SetCurrentValue on top of this local value.
    /// </summary>
    private void PlaceThumb(double value) => SetValue(ValueProperty, value);

    private void Sync(Action set)
    {
        _syncing = true;
        try { set(); }
        finally { _syncing = false; }
    }
}
