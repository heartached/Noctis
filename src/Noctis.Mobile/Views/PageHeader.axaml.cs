using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Noctis.Mobile.Views;

/// <summary>
/// The list pages' shared header (PageHeader.axaml): back label, tools, large title. Its
/// DataContext is the page (<see cref="ViewModels.MobilePage"/>: Title, BackTitle).
/// </summary>
public partial class PageHeader : UserControl
{
    /// <summary>The page's tools (its glass pill), at the back row's right end.</summary>
    public static readonly StyledProperty<Control?> ToolsProperty =
        AvaloniaProperty.Register<PageHeader, Control?>(nameof(Tools));

    /// <summary>Hides the back row (but keeps its room) while Select mode's bar covers it.</summary>
    public static readonly StyledProperty<bool> IsNavHiddenProperty =
        AvaloniaProperty.Register<PageHeader, bool>(nameof(IsNavHidden));

    public Control? Tools
    {
        get => GetValue(ToolsProperty);
        set => SetValue(ToolsProperty, value);
    }

    public bool IsNavHidden
    {
        get => GetValue(IsNavHiddenProperty);
        set => SetValue(IsNavHiddenProperty, value);
    }

    public PageHeader()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToolsProperty)
        {
            // The header's logical child, so the tools take the page as their DataContext.
            if (change.OldValue is Control old) LogicalChildren.Remove(old);
            if (change.NewValue is Control tools && tools.Parent == null) LogicalChildren.Add(tools);
        }
        else if (change.Property == IsNavHiddenProperty)
        {
            Classes.Set("nav-hidden", change.GetNewValue<bool>());
        }
    }
}
