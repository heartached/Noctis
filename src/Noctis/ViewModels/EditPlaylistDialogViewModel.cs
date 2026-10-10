using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the Edit Playlist dialog.
/// </summary>
public partial class EditPlaylistDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _playlistName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _playlistDescription = string.Empty;

    [ObservableProperty] private bool _showNameRequiredError;
    [ObservableProperty] private string _playlistColor = "#808080";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomArt))]
    [NotifyPropertyChangedFor(nameof(HasCollageArt))]
    [NotifyPropertyChangedFor(nameof(HasSingleArt))]
    [NotifyPropertyChangedFor(nameof(ShowFallbackIcon))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
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

    /// <summary>Whether the playlist is pinned to the top of the sidebar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isPinned;

    /// <summary>Sidebar folder name (empty = no folder). Folders are created by typing a new name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _playlistFolder = string.Empty;

    /// <summary>Comma-separated existing folder names, shown as a hint under the folder box.</summary>
    public string ExistingFoldersHint { get; init; } = string.Empty;

    public bool HasExistingFolders => ExistingFolders.Count > 0;

    /// <summary>Existing sidebar folder names, offered as dropdown suggestions in the folder field.</summary>
    public IReadOnlyList<string> ExistingFolders { get; init; } = Array.Empty<string>();

    /// <summary>The header's second line: the sidebar row's "12 tracks · 45 min".</summary>
    public string MetaText { get; init; } = string.Empty;
    public bool HasMetaText => MetaText.Length > 0;

    /// <summary>
    /// The folder Save files the playlist into: trimmed, and in an existing folder's own spelling
    /// when it names one in another case. The sidebar groups folders ignoring case and labels the
    /// group with whichever member comes first, so typing "rock" renamed the "Rock" header.
    /// </summary>
    public string ResolvedFolder
    {
        get
        {
            var typed = (PlaylistFolder ?? string.Empty).Trim();
            if (typed.Length == 0) return string.Empty;
            return ExistingFolders.FirstOrDefault(f => string.Equals(f.Trim(), typed, StringComparison.OrdinalIgnoreCase))?.Trim()
                   ?? typed;
        }
    }

    /// <summary>What the dialog opened with (<see cref="ForPlaylist"/>); null when built
    /// field by field, which leaves Save offered as soon as there is a name.</summary>
    private sealed record Original(string Name, string Description, bool IsPinned, string Folder, string? Cover);
    private Original? _original;

    /// <summary>
    /// True once something Save would write differs from what the dialog opened with. The
    /// caller stamps ModifiedAt on every save, so an untouched Save moved the playlist's
    /// "Updated" date; it is not offered now.
    /// </summary>
    public bool HasChanges => _original is not { } o
        || !string.Equals((PlaylistName ?? string.Empty).Trim(), o.Name.Trim(), StringComparison.Ordinal)
        || !string.Equals((PlaylistDescription ?? string.Empty).Trim(), o.Description.Trim(), StringComparison.Ordinal)
        || IsPinned != o.IsPinned
        || !string.Equals(ResolvedFolder, o.Folder.Trim(), StringComparison.OrdinalIgnoreCase)
        || PendingCoverArtFile != null
        || (CoverArtRemoved && !string.IsNullOrEmpty(o.Cover));

    /// <summary>Save is offered for a named playlist with something changed, once.</summary>
    public bool CanSave => !_saved && !string.IsNullOrWhiteSpace(PlaylistName) && HasChanges;

    /// <summary>Set once Save has handed the edit over. Enter in the name field (a KeyBinding)
    /// still reaches Save while the dialog animates out.</summary>
    private bool _saved;

    /// <summary>The dialog for <paramref name="playlist"/>, pre-filled as SidebarViewModel opens it.</summary>
    public static EditPlaylistDialogViewModel ForPlaylist(Playlist playlist, PlaylistNavItem? navItem, IReadOnlyList<string> existingFolders)
    {
        var vm = new EditPlaylistDialogViewModel
        {
            PlaylistName = playlist.Name ?? string.Empty,
            PlaylistDescription = playlist.Description ?? string.Empty,
            PlaylistColor = playlist.Color,
            CoverArtPath = playlist.CoverArtPath,
            Art1 = navItem?.Art1,
            Art2 = navItem?.Art2,
            Art3 = navItem?.Art3,
            Art4 = navItem?.Art4,
            IsPinned = playlist.IsPinned,
            PlaylistFolder = playlist.Folder ?? string.Empty,
            ExistingFoldersHint = string.Join(", ", existingFolders),
            ExistingFolders = existingFolders,
            MetaText = navItem?.MetaText ?? string.Empty,
        };
        vm._original = new Original(vm.PlaylistName, vm.PlaylistDescription, vm.IsPinned, vm.PlaylistFolder, vm.CoverArtPath);
        vm.RaiseCanSave();
        return vm;
    }

    /// <summary>Fires when the user clicks Save with valid input.</summary>
    public event EventHandler<(string Name, string Description)>? PlaylistSaved;

    /// <summary>Fires when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (_saved) return;

        if (string.IsNullOrWhiteSpace(PlaylistName))
        {
            ShowNameRequiredError = true;
            return;
        }
        if (!HasChanges) return;

        ShowNameRequiredError = false;
        _saved = true;
        RaiseCanSave();
        PlaylistSaved?.Invoke(this, (PlaylistName.Trim(), (PlaylistDescription ?? string.Empty).Trim()));
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
            Title = Localization.Loc.T("EditPlaylist.ChooseCover"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png" } }
            }
        });

        if (files.Count == 0) return;

        var localPath = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(localPath)) return;

        UseCoverFile(localPath);
    }

    /// <summary>Takes <paramref name="path"/> as the new cover; Save copies it in.</summary>
    internal void UseCoverFile(string path)
    {
        PendingCoverArtFile = path;
        CoverArtRemoved = false;
        CoverArtPath = path;
        RaiseCanSave();
    }

    /// <summary>Back to the automatic cover (the collage of the playlist's albums).</summary>
    [RelayCommand]
    private void RemoveCoverArt()
    {
        PendingCoverArtFile = null;
        CoverArtRemoved = true;
        CoverArtPath = null;
        RaiseCanSave();
    }

    /// <summary>A blank name says so straight away: Save is greyed out and would not say why.</summary>
    partial void OnPlaylistNameChanged(string value)
    {
        ShowNameRequiredError = string.IsNullOrWhiteSpace(value);
    }

    /// <summary>The cover flags are plain properties; the observable ones notify on their own.</summary>
    private void RaiseCanSave()
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
    }
}
