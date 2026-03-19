using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the Create Playlist dialog.
/// </summary>
public partial class CreatePlaylistDialogViewModel : ViewModelBase
{
    [ObservableProperty] private string _playlistName = string.Empty;
    [ObservableProperty] private string _playlistDescription = string.Empty;
    [ObservableProperty] private bool _showNameRequiredError;

    /// <summary>Fires when the user clicks Create with valid input.</summary>
    public event EventHandler<(string Name, string Description)>? PlaylistCreated;

    /// <summary>Fires when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Create()
    {
        // Validate name is not empty
        if (string.IsNullOrWhiteSpace(PlaylistName))
        {
            ShowNameRequiredError = true;
            return;
        }

        ShowNameRequiredError = false;
        PlaylistCreated?.Invoke(this, (PlaylistName.Trim(), PlaylistDescription.Trim()));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnPlaylistNameChanged(string value)
    {
        // Hide error when user starts typing
        if (!string.IsNullOrWhiteSpace(value))
        {
            ShowNameRequiredError = false;
        }
    }
}
