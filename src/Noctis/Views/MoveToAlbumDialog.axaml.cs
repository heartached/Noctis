using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>GitHub #239: the Move to Album picker (see <see cref="MoveToAlbumViewModel"/>).</summary>
public partial class MoveToAlbumDialog : Window
{
    private MoveToAlbumViewModel? _vm;

    public MoveToAlbumDialog()
    {
        InitializeComponent();
    }

    public MoveToAlbumDialog(MoveToAlbumViewModel vm) : this()
    {
        DataContext = vm;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.CloseRequested -= OnCloseRequested;
        }
        _vm = DataContext as MoveToAlbumViewModel;
        if (_vm == null) return;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.CloseRequested += OnCloseRequested;
    }

    // PillDialogHost turns the close into the animated one.
    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.CloseRequested -= OnCloseRequested;
        }
        base.OnClosed(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(FocusPane, DispatcherPriority.Loaded);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MoveToAlbumViewModel.IsCreatingNew))
            Dispatcher.UIThread.Post(FocusPane, DispatcherPriority.Loaded);
    }

    private void FocusPane()
    {
        if (!IsVisible || _vm == null) return;
        if (_vm.IsCreatingNew) NameBox.Focus();
        else SearchBox.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm != null)
        {
            e.Handled = true;
            if (_vm.IsCreatingNew) _vm.CancelCreateCommand.Execute(null);
            else _vm.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
