using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>Lyrics Studio › Choose songs. Same overlay card and fade/scale open-close as
/// <see cref="AddSongsDialog"/> and <see cref="LyricsBackgroundPickerDialog"/>.</summary>
public partial class LyricsStudioPickerDialog : Window
{
    private bool _closing;

    public LyricsStudioPickerDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LyricsStudioPickerViewModel vm)
                vm.CloseRequested += (_, _) => _ = CloseAnimatedAsync();
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _ = CloseAnimatedAsync();
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>A press on the dimmed backdrop (not the card) closes, like Esc and Cancel.</summary>
    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (ReferenceEquals(e.Source, DialogOverlay)) _ = CloseAnimatedAsync();
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e) => e.Handled = true;

    /// <summary>Child lookups for <see cref="OnTitleCellLayoutUpdated"/>, resolved once per cell and
    /// stashed in Tag: LayoutUpdated fires after every window layout pass and a cell's children never change.</summary>
    private sealed record TitleCellChildren(TextBlock Title, Border? ExplicitBadge);

    /// <summary>
    /// A row's title cell is an Auto,Auto grid so the E badge hugs the title; Auto columns measure
    /// unbounded, so the title's MaxWidth is capped to the cell minus the badge here and
    /// TextTrimming does the rest (the AddSongsDialog recipe). A cell not laid out is left alone.
    /// </summary>
    private void OnTitleCellLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not Grid cell || !cell.IsEffectivelyVisible || cell.Bounds.Width <= 0) return;
        if (cell.Tag is not TitleCellChildren children)
        {
            var title = cell.Children.OfType<TextBlock>().FirstOrDefault();
            if (title is null) return;
            children = new TitleCellChildren(title, cell.Children.OfType<Border>().FirstOrDefault());
            cell.Tag = children;
        }

        var reserved = 0.0;
        if (children.ExplicitBadge is { IsVisible: true } badge)
        {
            var width = badge.Bounds.Width > 0 ? badge.Bounds.Width : badge.DesiredSize.Width;
            reserved = width + badge.Margin.Left + badge.Margin.Right;
        }
        var max = Math.Max(0, cell.Bounds.Width - reserved);
        if (Math.Abs(children.Title.MaxWidth - max) > 0.5)
            children.Title.MaxWidth = max;
    }
}
