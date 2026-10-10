using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// The playlist's description pop-up: the same design as <see cref="AlbumDescriptionDialog"/>
/// (rounded pill pop-up, blurred app behind, cover + name + fact chips, one filled editor).
/// Save closes it (the view model's DescriptionSaved); Cancel, Esc and Alt+F4 drop the edit.
/// </summary>
public partial class PlaylistDescriptionDialog : Window
{
    private PlaylistViewModel? _vm;
    private bool _saved;

    public PlaylistDescriptionDialog()
    {
        InitializeComponent();
        // Ctrl+Enter anywhere in the card saves (tunnel: never a new line; stopped by
        // PillDialogHost while the close plays).
        CardRoot.AddHandler(KeyDownEvent, OnCardKeyDown, RoutingStrategies.Tunnel);
        DescriptionEditor.TextChanged += (_, _) => UpdateFooter();
    }

    public PlaylistDescriptionDialog(PlaylistViewModel vm) : this()
    {
        DataContext = vm;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.DescriptionSaved -= OnSaved;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _vm = DataContext as PlaylistViewModel;
        if (_vm != null)
        {
            _vm.DescriptionSaved += OnSaved;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }
        FactChips.ItemsSource = DescriptionDialogs.PlaylistChips(_vm);
        UpdateFooter();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlaylistViewModel.TrackCount):
            case nameof(PlaylistViewModel.TotalDuration):
            case nameof(PlaylistViewModel.TotalSize):
                FactChips.ItemsSource = DescriptionDialogs.PlaylistChips(_vm);
                break;
            case nameof(PlaylistViewModel.PlaylistDescription):
                UpdateFooter();
                break;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        // Before base.OnOpened: PillDialogHost reads ContentReady in the Opened event.
        DialogHost.ContentReady = DescriptionDialogs.WhenImagesShown(
            SingleCover, Collage1, Collage2, Collage3, Collage4, CustomCover);
        base.OnOpened(e);
        if (_vm?.IsDescriptionEditing == true)
            Dispatcher.UIThread.Post(() => DescriptionDialogs.FocusAtEnd(DescriptionEditor), DispatcherPriority.Loaded);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_vm != null)
        {
            _vm.DescriptionSaved -= OnSaved;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            // Closed without saving: drop the edit.
            if (!_saved && !_vm.SaveDescriptionEditCommand.IsRunning)
                _vm.CancelDescriptionEditCommand.Execute(null);
        }
        base.OnClosed(e);
    }

    private void OnSaved(object? sender, EventArgs e)
    {
        _saved = true;
        Close();
    }

    private void UpdateFooter()
    {
        var text = DescriptionEditor.Text ?? string.Empty;
        CountText.Text = DescriptionDialogs.FormatCount(text, removesOnSave: _vm?.HasDescription == true);
        ClearButton.IsVisible = text.Length > 0;
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        DescriptionEditor.Clear();
        DescriptionEditor.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (!DescriptionDialogs.IsSaveGesture(e)) return;
        e.Handled = true;
        var save = _vm?.SaveDescriptionEditCommand;
        if (save?.CanExecute(null) == true) save.Execute(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Esc closes like Cancel (the host animates it out); not while a save is in flight.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            if (_vm?.SaveDescriptionEditCommand.IsRunning != true) Close();
            return;
        }
        base.OnKeyDown(e);
    }

    public static async Task ShowAsync(PlaylistViewModel vm)
    {
        var dialog = new PlaylistDescriptionDialog(vm);

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
    }
}
