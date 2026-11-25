using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// Listening History inside the Settings sheet. The log is read again each time the page comes
/// into view (the numbers are a week's, and plays have landed since it was last open). A tap on
/// This Week's card outside the day bars shows the week again.
/// </summary>
public partial class ListeningHistoryPane : UserControl
{
    public ListeningHistoryPane()
    {
        InitializeComponent();
        WeekCard.Tapped += OnWeekCardTapped;
    }

    private void OnWeekCardTapped(object? sender, TappedEventArgs e)
    {
        // A day bar is a button with its own command; this is the rest of the card.
        for (var v = e.Source as Visual; v != null && v != WeekCard; v = v.GetVisualParent())
            if (v is Button) return;
        (DataContext as ListeningHistoryViewModel)?.ClearDayCommand.Execute(null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as ListeningHistoryViewModel)?.Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            (DataContext as ListeningHistoryViewModel)?.Refresh();
    }
}
