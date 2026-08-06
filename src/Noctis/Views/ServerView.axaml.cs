using Avalonia.Controls;
using Avalonia.Input;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class ServerView : UserControl
{
    public ServerView()
    {
        InitializeComponent();
    }

    private ServerViewModel? Vm => DataContext as ServerViewModel;

    private void OnAlbumTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: Track track })
            Vm?.PlayAlbumTrackCommand.Execute(track);
    }

    private void OnSearchTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: Track track })
            Vm?.PlaySearchTrackCommand.Execute(track);
    }
}
