using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Noctis.Mobile.Views;

public partial class SearchPage : UserControl
{
    public SearchPage()
    {
        InitializeComponent();
    }

    /// <summary>Arriving on the tab puts the caret in the box (and the keyboard up), as a search tab should.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
            Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Loaded);
    }
}
