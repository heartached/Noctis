using Avalonia.Controls;
using Avalonia.Input;

namespace Noctis.Views;

/// <summary>
/// Add Songs in the rounded pill pop-up (PillDialogHost: the blurred app behind and the shared
/// open/close animation). Every close — Cancel, Add, Esc, Alt+F4 — is a plain Close() the host
/// turns into the animated one, played once.
/// </summary>
public partial class AddSongsDialog : Window
{
    public AddSongsDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Closes the dialog. PillDialogHost turns the close into the animated one (it plays once
    /// however many closes arrive). The caller reads the pick from the view model's SongsChosen
    /// event, so nothing rides on Close(result).
    /// </summary>
    public Task CloseAnimatedAsync()
    {
        Close();
        return Task.CompletedTask;
    }

    /// <summary>Esc closes like Cancel.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// The search box does not take the caret on open (a focused pill field wears the accent
    /// ring, which read as an outline on a box nobody had touched), so typing anywhere else in
    /// the dialog starts the search instead of going nowhere. Whitespace is left alone: Space on
    /// a focused row ticks it.
    /// </summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!e.Handled && !string.IsNullOrWhiteSpace(e.Text) && !SearchBox.IsFocused)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.Text = (SearchBox.Text ?? string.Empty) + e.Text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            return;
        }
        base.OnTextInput(e);
    }

    /// <summary>Child lookups for <see cref="OnTitleCellLayoutUpdated"/>, resolved once per cell and
    /// stashed in Tag: LayoutUpdated fires after every window layout pass and a cell's children never
    /// change (a recycled row keeps its cell and only swaps the data).</summary>
    private sealed record TitleCellChildren(TextBlock Title, Border? ExplicitBadge);

    /// <summary>
    /// A row's title cell is an Auto,Auto grid so the E badge hugs the title; Auto columns measure
    /// unbounded, so the title's MaxWidth is capped to the cell minus the badge here and
    /// TextTrimming does the rest. A cell not laid out is left alone.
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
