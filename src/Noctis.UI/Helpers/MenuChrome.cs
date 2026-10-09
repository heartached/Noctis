using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Noctis.Helpers;

/// <summary>
/// Lets Styles.axaml give the parts of the Fluent menu templates the v2 menu look where a
/// plain style setter cannot reach. Avalonia ranks a value written in a control template
/// (BindingPriority.Template) ABOVE a style setter (BindingPriority.Style), and the Fluent
/// templates write these from resources: the MenuFlyoutPresenter card's padding
/// (FlyoutBorderThemePadding, 0), every menu's row margin (MenuFlyoutScrollerMargin, 0,4)
/// and the whole submenu card (MenuFlyoutPresenter* brushes, padding, FlyoutThemeMaxWidth).
/// So a setter such as <c>MenuItem /template/ Popup &gt; Border { Padding: 6 }</c> was
/// silently ignored. These attached properties, set by those styles, apply the value as a
/// local value instead, which outranks the template.
/// </summary>
public static class MenuChrome
{
    /// <summary>Padding for a Border / Decorator inside a menu template.</summary>
    public static readonly AttachedProperty<Thickness?> PaddingProperty =
        AvaloniaProperty.RegisterAttached<Control, Thickness?>("Padding", typeof(MenuChrome));

    public static void SetPadding(Control control, Thickness? value) => control.SetValue(PaddingProperty, value);
    public static Thickness? GetPadding(Control control) => control.GetValue(PaddingProperty);

    /// <summary>Margin for a part inside a menu template (the rows' ItemsPresenter).</summary>
    public static readonly AttachedProperty<Thickness?> MarginProperty =
        AvaloniaProperty.RegisterAttached<Control, Thickness?>("Margin", typeof(MenuChrome));

    public static void SetMargin(Control control, Thickness? value) => control.SetValue(MarginProperty, value);
    public static Thickness? GetMargin(Control control) => control.GetValue(MarginProperty);

    /// <summary>
    /// A submenu card (MenuItem's popup Border): the parent card's theme background
    /// (AppSidebarBackground, so a custom theme carries over), the hairline
    /// MenuV2BorderBrush border and a 300px cap. Brushes follow theme switches.
    /// </summary>
    public static readonly AttachedProperty<bool> SubmenuCardProperty =
        AvaloniaProperty.RegisterAttached<Border, bool>("SubmenuCard", typeof(MenuChrome));

    public static void SetSubmenuCard(Border border, bool value) => border.SetValue(SubmenuCardProperty, value);
    public static bool GetSubmenuCard(Border border) => border.GetValue(SubmenuCardProperty);

    public const double SubmenuMaxWidth = 300;

    static MenuChrome()
    {
        PaddingProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            var property = c switch
            {
                Border => Border.PaddingProperty,
                Decorator => Decorator.PaddingProperty,
                _ => null,
            };
            if (property is null) return;
            if (e.GetNewValue<Thickness?>() is { } t) c.SetValue(property, t);
            else c.ClearValue(property);
        });

        MarginProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (e.GetNewValue<Thickness?>() is { } t) c.SetValue(Layoutable.MarginProperty, t);
            else c.ClearValue(Layoutable.MarginProperty);
        });

        SubmenuCardProperty.Changed.AddClassHandler<Border>((b, e) =>
        {
            if (e.GetNewValue<bool>())
            {
                b.Bind(Border.BackgroundProperty, b.GetResourceObservable("AppSidebarBackground"));
                b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("MenuV2BorderBrush"));
                b.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                b.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
                b.SetValue(Layoutable.MaxWidthProperty, SubmenuMaxWidth);
            }
            else
            {
                b.ClearValue(Border.BackgroundProperty);
                b.ClearValue(Border.BorderBrushProperty);
                b.ClearValue(Border.BorderThicknessProperty);
                b.ClearValue(Border.CornerRadiusProperty);
                b.ClearValue(Layoutable.MaxWidthProperty);
            }
        });
    }
}
