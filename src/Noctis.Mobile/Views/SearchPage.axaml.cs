using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class SearchPage : UserControl
{
    private ShellViewModel? _shell;

    public SearchPage()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell != null) _shell.PropertyChanged -= OnShellChanged;
        _shell = (DataContext as SearchPageViewModel)?.Shell;
        if (_shell != null) _shell.PropertyChanged += OnShellChanged;
    }

    /// <summary>
    /// Arriving on the tab (the tab bar, the mini bar's button, the Library field) puts the caret
    /// in the box and the keyboard up, as a search tab should. Keyed to the tab switch, not to
    /// becoming visible: Back from a page opened from the results shows this page again, and
    /// the keyboard popping up then would cover the results the user came back to.
    /// </summary>
    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SelectedTab) && _shell?.IsSearchSelected == true)
            Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Loaded);
    }
}
