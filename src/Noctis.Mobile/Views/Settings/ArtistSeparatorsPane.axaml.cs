using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Noctis.Mobile.Views;

/// <summary>
/// Artist Separators inside the Settings sheet; its view model is the page's state. The Add field
/// sits under the list, where the keyboard comes up: while it is open the pane gets room below and
/// the field's card scrolls into the part of the sheet left above it (as AccountPage does).
/// </summary>
public partial class ArtistSeparatorsPane : UserControl
{
    private IInputPane? _inputPane;
    private Thickness _restMargin;

    public ArtistSeparatorsPane()
    {
        InitializeComponent();
        _restMargin = Margin;
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
        Margin = _restMargin;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        var height = TopLevel.GetTopLevel(this)?.Bounds.Height ?? 0;
        var covered = e.NewState == InputPaneState.Open && height > 0 && NewSeparatorBox.IsFocused
            ? Math.Max(0, height - e.EndRect.Top)
            : 0;
        Margin = new Thickness(_restMargin.Left, _restMargin.Top, _restMargin.Right, _restMargin.Bottom + covered);
        if (covered > 0) KeepAboveKeyboard(covered);
    }

    /// <summary>Scrolls the Add card into the part of the sheet the keyboard leaves. BringIntoView
    /// alone does not: the sheet's viewport runs on under the keyboard.</summary>
    private void KeepAboveKeyboard(double covered) => Dispatcher.UIThread.Post(() =>
    {
        if (TopLevel.GetTopLevel(this) is not { } top || this.FindAncestorOfType<ScrollViewer>() is not { } scroll
            || AddSeparatorCard.TranslatePoint(default, scroll) is not { } at
            || scroll.TranslatePoint(default, top) is not { } scrollAt) return;
        var visibleBottom = Math.Min(scroll.Bounds.Height, top.Bounds.Height - covered - scrollAt.Y) - 12;
        var cardBottom = at.Y + AddSeparatorCard.Bounds.Height;
        if (cardBottom > visibleBottom) scroll.Offset = scroll.Offset.WithY(scroll.Offset.Y + cardBottom - visibleBottom);
    }, DispatcherPriority.Background);
}
