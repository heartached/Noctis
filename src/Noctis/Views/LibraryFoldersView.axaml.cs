using Avalonia.Controls;
using Avalonia.Input;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LibraryFoldersView : UserControl
{
    public LibraryFoldersView()
    {
        InitializeComponent();

        // Double-click a row to play it within the folder's track list.
        TrackList.DoubleTapped += OnTrackDoubleTapped;
    }

    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not LibraryFoldersViewModel vm) return;
        if (TrackList.SelectedItem is Track track)
            vm.PlayTrackCommand.Execute(track);
    }
}
