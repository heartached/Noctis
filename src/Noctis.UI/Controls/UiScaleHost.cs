using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Helpers;

namespace Noctis.Controls;

/// <summary>
/// The layout transform <see cref="UiScale"/> splices into each window's template around its
/// content (GitHub #145). It picks the scale it applies on every measure: the requested
/// interface size, capped so the window's fit size (its own designed minimum, or what the
/// window asked for through <see cref="UiScale.FitSizeProperty"/>) stays on screen. At 100%
/// it carries no transform, so layout is the same as without it.
/// </summary>
public sealed class UiScaleHost : LayoutTransformControl
{
    private readonly Window _window;
    private readonly double _baseMinWidth;
    private readonly double _baseMinHeight;
    private double _requested = 1.0;
    private double _applied = 1.0;

    public UiScaleHost(Window window)
    {
        _window = window;
        // The window's designed minimum, before any scale grew it.
        _baseMinWidth = window.MinWidth;
        _baseMinHeight = window.MinHeight;
        window.Opened += (_, _) => UpdateWindowMinimum();
        // A closed window's Screens are disposed; it takes no further scale changes.
        window.Closed += (_, _) => IsClosed = true;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == UiScale.FitSizeProperty) InvalidateMeasure();
        };
    }

    /// <summary>The interface size asked for (1 = 100%).</summary>
    public double Requested
    {
        get => _requested;
        set
        {
            if (value == _requested) return;
            _requested = value;
            UpdateWindowMinimum();
            InvalidateMeasure();
        }
    }

    /// <summary>True once the window has closed (UiScale drops the host then).</summary>
    internal bool IsClosed { get; private set; }

    /// <summary>The scale on the content right now.</summary>
    public double Applied => _applied;

    protected override Size MeasureOverride(Size availableSize)
    {
        var fit = UiScale.GetFitSize(_window);
        fit = new Size(Max(fit.Width, _baseMinWidth), Max(fit.Height, _baseMinHeight));
        var eff = UiScale.Effective(_requested, availableSize, fit);
        if (eff != _applied)
        {
            _applied = eff;
            // Before the base measure, which reads the new matrix right away.
            LayoutTransform = eff == 1.0 ? null : new ScaleTransform(eff, eff);
            UiScale.SetApplied(_window, eff);
        }
        return base.MeasureOverride(availableSize);
    }

    private static double Max(double a, double b)
        => double.IsFinite(a) ? (double.IsFinite(b) ? Math.Max(a, b) : a) : (double.IsFinite(b) ? b : 0);

    /// <summary>
    /// Grows the window's minimum with the scale, so it cannot be dragged smaller than its
    /// scaled content needs; capped at the screen's working area (the fit cap covers a
    /// screen too small for it).
    /// </summary>
    private void UpdateWindowMinimum()
    {
        if (IsClosed || (_baseMinWidth <= 0 && _baseMinHeight <= 0)) return;
        var factor = Math.Max(1.0, _requested);
        var minW = _baseMinWidth * factor;
        var minH = _baseMinHeight * factor;
        if (_window.Screens?.ScreenFromWindow(_window) is { } screen && screen.Scaling > 0)
        {
            minW = Math.Min(minW, screen.WorkingArea.Width / screen.Scaling);
            minH = Math.Min(minH, screen.WorkingArea.Height / screen.Scaling);
        }
        _window.MinWidth = Math.Max(_baseMinWidth, minW);
        _window.MinHeight = Math.Max(_baseMinHeight, minH);
    }
}
