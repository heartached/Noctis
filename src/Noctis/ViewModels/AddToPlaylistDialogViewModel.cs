using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
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
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCreateCommand))]
    private string _newPlaylistName = string.Empty;
    [ObservableProperty] private string _newPlaylistDescription = string.Empty;
    [ObservableProperty] private bool _showNameRequiredError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackCountText))]
    private int _trackCount;

    /// <summary>Set once a playlist was picked or a new one requested. The dialog animates out
    /// after that, and a second pick or Create meanwhile (Enter in the name, a double click)
    /// raised the result event again.</summary>
    private bool _done;

    public ObservableCollection<PlaylistNavItem> Playlists { get; }

    public bool HasPlaylists => Playlists.Count > 0;

    /// <summary>Dialog title follows the mode: list view vs inline create form (localized; it was
    /// hard-coded English while the keys were translated).</summary>
    public string DialogTitle => IsCreatingNew
        ? Loc.T("AddToPlaylist.CreateNewPlaylist")
        : Loc.T("AddToPlaylist.AddPlaylist");

    /// <summary>"Adding 1 song" / "Adding 3 songs" under the title.</summary>
    public string TrackCountText => TrackCount == 1
        ? Loc.T("AddToPlaylist.AddingOneSong")
        : Loc.T("AddToPlaylist.AddingSongs", TrackCount);

    /// <summary>The existing-playlists section is hidden while the create form is open.</summary>
    public bool ShowPlaylistList => HasPlaylists && !IsCreatingNew;

    public bool ShowEmptyState => !HasPlaylists && !IsCreatingNew;

    public AddToPlaylistDialogViewModel(ObservableCollection<PlaylistNavItem> playlists, int trackCount)
    {
        // Smart playlists are filled by their rules, so they can't take added tracks.
        Playlists = new ObservableCollection<PlaylistNavItem>(playlists.Where(p => !p.IsSmartPlaylist));
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
        if (playlist == null || _done || IsCreatingNew) return;
        _done = true;
        PlaylistSelected?.Invoke(this, playlist);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowCreate()
    {
        if (_done || IsCreatingNew) return;
        // The form opens empty. Cleared here rather than on Back, so the text doesn't blink
        // out while the form is still fading away.
        NewPlaylistName = string.Empty;
        NewPlaylistDescription = string.Empty;
        ShowNameRequiredError = false;
        IsCreatingNew = true;
    }

    [RelayCommand]
    private void CancelCreate()
    {
        if (_done) return;
        IsCreatingNew = false;
        ShowNameRequiredError = false;
    }

    private bool CanConfirmCreate() => !string.IsNullOrWhiteSpace(NewPlaylistName);

    [RelayCommand(CanExecute = nameof(CanConfirmCreate))]
    private void ConfirmCreate()
    {
        if (_done) return;
        // Execute() doesn't consult CanExecute, so a blank name is still refused here.
        if (string.IsNullOrWhiteSpace(NewPlaylistName))
        {
            ShowNameRequiredError = true;
            return;
        }

        ShowNameRequiredError = false;
        _done = true;
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
