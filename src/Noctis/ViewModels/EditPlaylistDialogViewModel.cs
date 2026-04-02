using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the Edit Playlist dialog.
/// </summary>
public partial class EditPlaylistDialogViewModel : ViewModelBase
{
    [ObservableProperty] private string _playlistName = string.Empty;
    [ObservableProperty] private string _playlistDescription = string.Empty;
    [ObservableProperty] private bool _showNameRequiredError;
    [ObservableProperty] private string _playlistColor = "#808080";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomArt))]
    [NotifyPropertyChangedFor(nameof(HasCollageArt))]
    [NotifyPropertyChangedFor(nameof(HasSingleArt))]
    [NotifyPropertyChangedFor(nameof(ShowFallbackIcon))]
    private string? _coverArtPath;

    /// <summary>Up to 4 unique album art paths for collage display.</summary>
    [ObservableProperty] private string? _art1;
    [ObservableProperty] private string? _art2;
    [ObservableProperty] private string? _art3;
    [ObservableProperty] private string? _art4;

    public bool HasCustomArt => !string.IsNullOrEmpty(CoverArtPath);
    public bool HasCollageArt => !HasCustomArt && Art1 != null && Art2 != null;
    public bool HasSingleArt => !HasCustomArt && Art1 != null && Art2 == null;
    public bool ShowFallbackIcon => !HasCustomArt && Art1 == null;

    /// <summary>Pending cover art file chosen by the user (null = no change, empty = remove).</summary>
    public string? PendingCoverArtFile { get; private set; }
    public bool CoverArtRemoved { get; private set; }

    /// <summary>Fires when the user clicks Save with valid input.</summary>
    public event EventHandler<(string Name, string Description)>? PlaylistSaved;

    /// <summary>Fires when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(PlaylistName))
        {
            ShowNameRequiredError = true;
            return;
        }

        ShowNameRequiredError = false;
        PlaylistSaved?.Invoke(this, (PlaylistName.Trim(), PlaylistDescription.Trim()));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task SetCoverArt(Avalonia.Visual visual)
    {
        var topLevel = TopLevel.GetTopLevel(visual);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Cover Art",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png" } }
            }
        });

        if (files.Count == 0) return;

        var localPath = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(localPath)) return;

        PendingCoverArtFile = localPath;
        CoverArtRemoved = false;
        CoverArtPath = localPath;
    }

    [RelayCommand]
    private void RemoveCoverArt()
    {
        PendingCoverArtFile = null;
        CoverArtRemoved = true;
        CoverArtPath = null;
    }

    partial void OnPlaylistNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            ShowNameRequiredError = false;
    }
}
