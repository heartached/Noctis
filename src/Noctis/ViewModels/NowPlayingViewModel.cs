using CommunityToolkit.Mvvm.Input;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the full "Now Playing" view shown in the content area.
/// This is essentially a passthrough to PlayerViewModel — all bindings
/// come from the shared Player instance.
/// </summary>
public partial class NowPlayingViewModel : ViewModelBase
{
    public PlayerViewModel Player { get; }

    /// <summary>Fires when the user wants to go back to the previous view.</summary>
    public event EventHandler? BackRequested;

    public NowPlayingViewModel(PlayerViewModel player)
    {
        Player = player;
    }

    [RelayCommand]
    private void GoBack()
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }
}
