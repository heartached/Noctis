using Avalonia;
using Avalonia.Controls;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// Listening History inside the Settings sheet. The log is read again each time the page comes
/// into view (the numbers are a week's, and plays have landed since it was last open).
/// </summary>
public partial class ListeningHistoryPane : UserControl
{
    public ListeningHistoryPane()
    {
        InitializeComponent();
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
