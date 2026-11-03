using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Noctis.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Settings ▸ Appearance ▸ Interface Size (GitHub #145): the whole UI drawn larger or smaller
/// on every platform, applied live. The OS text-size settings never reach an Avalonia app
/// (macOS "Larger Text" has no effect), and Avalonia has no per-window render-scaling
/// override, so the scale is a layout transform:
/// <list type="bullet">
/// <item>Every window gets a <see cref="UiScaleHost"/> (a LayoutTransformControl) spliced
/// into its template around PART_ContentPresenter, once, when the template is applied. One
/// hook covers the main window and every pop-up window without touching their XAML, and the
/// window's own content, names, DataContext and code-behind stay exactly as they were.
/// Text, icons, list rows, spacing and hit-testing all scale together, because layout
/// itself runs at the smaller size and the result is transformed.</item>
/// <item>Popups (menus, flyouts, combo drop-downs, tooltips) are separate hosts outside that
/// transform. While the scale is not 100% a global style turns on Popup.InheritsTransform,
/// Avalonia's own mechanism: the popup host takes the scale of its placement target and
/// sizes itself to match. At 100% it is off (the default), so popups behave as before.</item>
/// <item>A window can ask to keep a designed size on screen (<see cref="FitSizeProperty"/>:
/// the main window's minimum layout size, a pop-up's card); the host then scales only as far
/// as that still fits, never below 100%.</item>
/// </list>
/// At 100% the host carries no transform at all (verified pixel-identical by
/// UiScaleTests). UI thread only.
/// </summary>
public static class UiScale
{
    /// <summary>Sanity bounds; the Settings choices are AppSettings.UiScaleChoices.</summary>
    public const double MinFactor = 0.5;
    public const double MaxFactor = 3.0;

    private static double _factor = 1.0;
    private static bool _installed;
    private static Application? _styledApp;
    private static readonly List<WeakReference<UiScaleHost>> Hosts = new();

    /// <summary>The scale the user asked for (1 = 100%).</summary>
    public static double Factor => _factor;

    /// <summary>Raised after <see cref="Factor"/> changed.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Opt a window out (set in its XAML before it is shown): it keeps 1:1 layout. For a
    /// window whose code sizes the window from its content's own sizes (the mini player).
    /// </summary>
    public static readonly AttachedProperty<bool> ExcludeProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("Exclude", typeof(UiScale));
    public static bool GetExclude(Window w) => w.GetValue(ExcludeProperty);
    public static void SetExclude(Window w, bool value) => w.SetValue(ExcludeProperty, value);

    /// <summary>
    /// A size, in unscaled DIPs, that has to stay on screen inside the window: the scale is
    /// capped at what still fits it (but never pushed below 100% by it). A NaN or zero side
    /// is ignored.
    /// </summary>
    public static readonly AttachedProperty<Size> FitSizeProperty =
        AvaloniaProperty.RegisterAttached<Window, Size>("FitSize", typeof(UiScale));
    public static Size GetFitSize(Window w) => w.GetValue(FitSizeProperty);
    public static void SetFitSize(Window w, Size value) => w.SetValue(FitSizeProperty, value);

    /// <summary>
    /// The scale actually applied to a window's content right now (the host writes it).
    /// Code that sizes content from the window's ClientSize divides by it — see
    /// <see cref="ContentSize"/> — and can watch it for changes.
    /// </summary>
    public static readonly AttachedProperty<double> AppliedProperty =
        AvaloniaProperty.RegisterAttached<Window, double>("Applied", typeof(UiScale), 1.0);
    public static double GetApplied(Window w) => w.GetValue(AppliedProperty);

    internal static void SetApplied(Window w, double value) => w.SetValue(AppliedProperty, value);

    /// <summary>The room the window's content lays out in: ClientSize in scaled units.</summary>
    public static Size ContentSize(Window w)
    {
        var f = GetApplied(w);
        return f > 0 && f != 1 ? w.ClientSize / f : w.ClientSize;
    }

    /// <summary>
    /// Device pixels per layout unit for a control: the platform's render scaling times the
    /// interface size its window applies. Artwork decode sizes use it so a scaled-up cover is
    /// decoded at the size it is drawn, not upscaled from the 100% decode.
    /// </summary>
    public static double DeviceScaling(Visual? visual)
    {
        var top = visual is null ? null : TopLevel.GetTopLevel(visual);
        var render = top?.RenderScaling ?? 1.0;
        // A popup root takes its target's scale through InheritsTransform: the global
        // factor is what it carries (the pop-up windows' fit cap only ever lowers it).
        var ui = top is Window w ? GetApplied(w) : _factor;
        return render * ui;
    }

    /// <summary>Splices a host into every window from now on. Idempotent (a second
    /// Application instance, as the headless tests make, gets the popup style too).</summary>
    public static void Install()
    {
        if (!_installed)
        {
            _installed = true;
            TemplatedControl.TemplateAppliedEvent.AddClassHandler<Window>(OnWindowTemplateApplied);
        }
        UpdatePopupStyle();
    }

    /// <summary>Applies a new scale to every open window, live.</summary>
    public static void SetFactor(double factor)
    {
        if (!double.IsFinite(factor)) factor = 1.0;
        factor = Math.Clamp(factor, MinFactor, MaxFactor);
        if (factor == _factor) return;
        _factor = factor;
        UpdatePopupStyle();
        for (var i = Hosts.Count - 1; i >= 0; i--)
        {
            if (Hosts[i].TryGetTarget(out var host) && !host.IsClosed) host.Requested = factor;
            else Hosts.RemoveAt(i);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// The scale a window applies: the requested one, capped so <paramref name="fit"/> still
    /// fits in <paramref name="available"/> — but a cap never takes it below 100% (or below
    /// the request, when that is under 100%).
    /// </summary>
    public static double Effective(double requested, Size available, Size fit)
    {
        var eff = requested;
        if (fit.Width > 0 && double.IsFinite(fit.Width) && double.IsFinite(available.Width))
            eff = Math.Min(eff, available.Width / fit.Width);
        if (fit.Height > 0 && double.IsFinite(fit.Height) && double.IsFinite(available.Height))
            eff = Math.Min(eff, available.Height / fit.Height);
        var floor = Math.Min(1.0, requested);
        // Hundredths: a fit cap that wobbles in the last digits must not re-run layout.
        return Math.Round(Math.Max(eff, floor), 2);
    }

    private static void OnWindowTemplateApplied(Window window, TemplateAppliedEventArgs e)
    {
        if (GetExclude(window)) return;
        // The theme's window template ends in VisualLayerManager > PART_ContentPresenter (the
        // layers above it — overlay popups, adorners — stay unscaled; popups scale themselves
        // through InheritsTransform). A template of another shape is left alone.
        if (e.NameScope.Find<ContentPresenter>("PART_ContentPresenter") is not { } presenter
            || presenter.GetVisualParent() is not Decorator decorator
            || !ReferenceEquals(decorator.Child, presenter))
            return;

        var host = new UiScaleHost(window) { Requested = _factor };
        decorator.Child = null;
        host.Child = presenter;
        decorator.Child = host;
        Hosts.Add(new WeakReference<UiScaleHost>(host));
    }

    /// <summary>
    /// The popups' InheritsTransform switch. A style added to or removed from the application
    /// later does not reach a popup that was already styled (a ContextMenu keeps its Popup
    /// across opens — measured in UiScaleTests), so the style is installed once and its
    /// setter is bound to this switch instead.
    /// </summary>
    private sealed class PopupSwitch : IObservable<bool>
    {
        private readonly List<IObserver<bool>> _observers = new();
        private bool _value;

        public bool Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value;
                foreach (var o in _observers.ToArray()) o.OnNext(value);
            }
        }

        public IDisposable Subscribe(IObserver<bool> observer)
        {
            _observers.Add(observer);
            observer.OnNext(_value);
            return new Unsubscriber(() => _observers.Remove(observer));
        }

        private sealed class Unsubscriber(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private static readonly PopupSwitch PopupInherit = new();

    private static void UpdatePopupStyle()
    {
        PopupInherit.Value = _factor != 1.0;
        if (!_installed || Application.Current is not { } app || ReferenceEquals(app, _styledApp)) return;
        _styledApp = app;
        app.Styles.Add(new Style(x => x.OfType<Popup>())
        {
            Setters =
            {
                new Setter(Popup.InheritsTransformProperty, PopupInherit.ToBinding()),
            },
        });
    }
}
