using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class ContextSheet : UserControl
{
    private IInputPane? _inputPane;

    public ContextSheet()
    {
        InitializeComponent();
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        (DataContext as ShellViewModel)?.CloseSheet();
        e.Handled = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _inputPane = TopLevel.GetTopLevel(this)?.InputPane;
        if (_inputPane != null) _inputPane.StateChanged += OnInputPaneChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_inputPane != null) _inputPane.StateChanged -= OnInputPaneChanged;
        _inputPane = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// The card sits on the bottom edge, so the keyboard opened by the playlist picker's "New
    /// playlist" box covered all of it, the box included (S23, 2026-10-05). Lift the card above
    /// the keyboard; the nav-bar padding it already has lies under the keyboard too.
    /// </summary>
    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        var height = TopLevel.GetTopLevel(this)?.Bounds.Height ?? 0;
        var covered = e.NewState == InputPaneState.Open && height > 0 ? Math.Max(0, height - e.EndRect.Top) : 0;
        SheetCard.Margin = new Thickness(0, 0, 0, Math.Max(0, covered - SheetCard.Padding.Bottom));
    }
}
