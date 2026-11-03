using CommunityToolkit.Mvvm.Input;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// The album description tapped (owner 10-19: "make it a pop-up, a clean pop-up"): the whole
/// text on the menu's glass, rising from the bottom edge (AlbumNoteSheet), under the album's
/// cover, title and artist, with a close button. Closed by the button, a tap on the dim, a swipe
/// down on its top, or Back (<see cref="ShellViewModel.TryHandleBack"/>).
/// </summary>
public sealed partial class AlbumNoteViewModel
{
    private readonly ShellViewModel _shell;

    public AlbumNoteViewModel(ShellViewModel shell, string title, string artist, string? artworkPath, string text,
        bool isExplicit = false)
    {
        _shell = shell;
        Title = title;
        Artist = artist;
        ArtworkPath = artworkPath;
        Text = text;
        IsExplicit = isExplicit;
    }

    public string Title { get; }
    /// <summary>The album's E badge after its title, as on the album page's rows.</summary>
    public bool IsExplicit { get; }
    public string Artist { get; }
    public string? ArtworkPath { get; }
    public string Text { get; }

    [RelayCommand]
    private void Close() => _shell.CloseSheet();
}
