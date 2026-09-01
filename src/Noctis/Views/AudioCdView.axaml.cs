using Avalonia.Controls;
using Avalonia.Input;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class AudioCdView : UserControl
{
    public AudioCdView()
    {
        InitializeComponent();
    }

    private AudioCdViewModel? Vm => DataContext as AudioCdViewModel;

    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: Track track })
            Vm?.PlayTrackCommand.Execute(track);
    }
}
