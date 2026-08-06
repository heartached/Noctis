using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Noctis.Views;

public partial class AddSongsDialog : Window
{
    private bool _closing;

    public AddSongsDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Settle to the open state on the next frame so the fade/scale
        // transitions animate it (same pattern as the Create Playlist dialog).
        Dispatcher.UIThread.Post(() =>
        {
            DialogOverlay.Opacity = 1;
            DialogCard.RenderTransform = TransformOperations.Parse("scale(1)");
            SearchBox.Focus();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Plays the fade/scale close animation, then closes the window.</summary>
    public async Task CloseAnimatedAsync()
    {
        if (_closing) return;
        _closing = true;
        DialogOverlay.Opacity = 0;
        DialogCard.RenderTransform = TransformOperations.Parse("scale(0.96)");
        await Task.Delay(200);
        Close();
    }

    /// <summary>
    /// Caps the title's MaxWidth to the cell width minus the explicit badge, so long
    /// titles ellipsize while the badge keeps hugging the title (Auto,Auto columns
    /// measure text unconstrained, which otherwise overflows the row).
    /// </summary>
    /// <summary>Child lookups for <see cref="OnTitleCellLayoutUpdated"/>, resolved once
    /// per cell and stashed in Tag: LayoutUpdated fires after EVERY window layout pass,
    /// and a template cell's children never change (recycling reuses the same Grid).</summary>
    private sealed record TitleCellChildren(TextBlock Title, Border? ExplicitBadge);

    private void OnTitleCellLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not Grid titleCell)
            return;

        if (titleCell.Tag is not TitleCellChildren children)
        {
            var title = titleCell.Children.OfType<TextBlock>().FirstOrDefault();
            if (title == null)
                return;

            children = new TitleCellChildren(title, titleCell.Children.OfType<Border>().FirstOrDefault());
            titleCell.Tag = children;
        }

        var reservedBadgeWidth = 0.0;

        if (children.ExplicitBadge?.IsVisible == true)
        {
            var badgeMargin = children.ExplicitBadge.Margin;
            var badgeWidth = children.ExplicitBadge.Bounds.Width > 0
                ? children.ExplicitBadge.Bounds.Width
                : children.ExplicitBadge.DesiredSize.Width;
            reservedBadgeWidth = badgeWidth + badgeMargin.Left + badgeMargin.Right;
        }

        var maxTitleWidth = Math.Max(0, titleCell.Bounds.Width - reservedBadgeWidth);
        if (Math.Abs(children.Title.MaxWidth - maxTitleWidth) > 0.5)
            children.Title.MaxWidth = maxTitleWidth;
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
    }
}
