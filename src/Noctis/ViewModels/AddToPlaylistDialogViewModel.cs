using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the unified "Add to Playlist" dialog. Lets the user pick an
/// existing playlist or create a new one inline; the host wires the actual
/// add/create work through events.
/// </summary>
public partial class AddToPlaylistDialogViewModel : ViewModelBase
{
    [ObservableProperty] private bool _isCreatingNew;
    [ObservableProperty] private string _newPlaylistName = string.Empty;
    [ObservableProperty] private string _newPlaylistDescription = string.Empty;
    [ObservableProperty] private bool _showNameRequiredError;
    [ObservableProperty] private int _trackCount;

    public ObservableCollection<PlaylistNavItem> Playlists { get; }

    public bool HasPlaylists => Playlists.Count > 0;

    /// <summary>Dialog title follows the mode: list view vs inline create form.</summary>
    public string DialogTitle => IsCreatingNew ? "Create New Playlist" : "Add to Playlist";

    /// <summary>The existing-playlists section is hidden while the create form is open.</summary>
    public bool ShowPlaylistList => HasPlaylists && !IsCreatingNew;

    public bool ShowEmptyState => !HasPlaylists && !IsCreatingNew;

    public AddToPlaylistDialogViewModel(ObservableCollection<PlaylistNavItem> playlists, int trackCount)
    {
        Playlists = playlists;
        TrackCount = trackCount;
    }

    /// <summary>Fires when the user picks an existing playlist row.</summary>
    public event EventHandler<PlaylistNavItem>? PlaylistSelected;

    /// <summary>Fires when the user submits the inline "create new" form.</summary>
    public event EventHandler<(string Name, string Description)>? NewPlaylistRequested;

    /// <summary>Fires when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void SelectPlaylist(PlaylistNavItem? playlist)
    {
        if (playlist == null) return;
        PlaylistSelected?.Invoke(this, playlist);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowCreate()
    {
        IsCreatingNew = true;
        ShowNameRequiredError = false;
    }

    [RelayCommand]
    private void CancelCreate()
    {
        IsCreatingNew = false;
        NewPlaylistName = string.Empty;
        NewPlaylistDescription = string.Empty;
        ShowNameRequiredError = false;
    }

    [RelayCommand]
    private void ConfirmCreate()
    {
        if (string.IsNullOrWhiteSpace(NewPlaylistName))
        {
            ShowNameRequiredError = true;
            return;
        }

        ShowNameRequiredError = false;
        NewPlaylistRequested?.Invoke(this, (NewPlaylistName.Trim(), NewPlaylistDescription.Trim()));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsCreatingNewChanged(bool value)
    {
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(ShowPlaylistList));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    partial void OnNewPlaylistNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            ShowNameRequiredError = false;
    }
}
