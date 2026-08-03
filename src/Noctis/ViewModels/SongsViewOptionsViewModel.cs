using CommunityToolkit.Mvvm.Input;

namespace Noctis.ViewModels;

/// <summary>One entry in the View Options "Sort by" list.</summary>
/// <param name="Key">Sort key understood by
/// <c>LibrarySongsViewModel.BuildFilteredAndSortedTracks</c>.</param>
/// <param name="Label">Display text; differs from the key where the key is a property
/// name ("IsFavorite" → "Favorite", "SampleRate" → "Sample Rate").</param>
public record SongSortOption(string Key, string Label);

/// <summary>
/// Backs the Songs View Options dialog. The state it edits has two owners — sort and
/// filter live on <see cref="LibrarySongsViewModel"/>, column visibility on
/// <see cref="SettingsViewModel"/> — so this composes both into a single
/// compiled-binding surface for the dialog.
/// <para>
/// It holds references, never copies: every setter writes straight through to the
/// owning view model, which is what makes the dialog apply live and lets it close
/// without an OK/Cancel commit step.
/// </para>
/// </summary>
public partial class SongsViewOptionsViewModel : ViewModelBase, IDisposable
{
    private readonly LibrarySongsViewModel _songs;
    private System.ComponentModel.PropertyChangedEventHandler? _songsPropertyChangedHandler;

    /// <summary>Column visibility flags, bound directly by the dialog.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>
    /// Every field the Songs list can sort by, in the dialog's display order. Keys must
    /// match the switch arms in <c>BuildFilteredAndSortedTracks</c>; an unknown key
    /// falls through to its title-ordered default rather than throwing.
    /// </summary>
    public IReadOnlyList<SongSortOption> SortOptions { get; } = new[]
    {
        new SongSortOption("Title", "Title"),
        new SongSortOption("Artist", "Artist"),
        new SongSortOption("Album", "Album"),
        new SongSortOption("Album Artist", "Album by Artist"),
        new SongSortOption("Genre", "Genre"),
        new SongSortOption("Time", "Time"),
        new SongSortOption("Plays", "Plays"),
        new SongSortOption("IsFavorite", "Favorite"),
        new SongSortOption("Rating", "Rating"),
        new SongSortOption("Year", "Year"),
        new SongSortOption("Bpm", "BPM"),
        new SongSortOption("Bitrate", "Bitrate"),
        new SongSortOption("SampleRate", "Sample Rate"),
        new SongSortOption("Date Added", "Date Added"),
    };

    public SongsViewOptionsViewModel(LibrarySongsViewModel songs, SettingsViewModel settings)
    {
        _songs = songs;
        Settings = settings;

        // Held in a field so Dispose can detach it: this view model is built fresh for
        // each dialog open while LibrarySongsViewModel lives for the whole process, so an
        // un-detachable lambda would leave one dead subscriber behind per open.
        _songsPropertyChangedHandler = (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(LibrarySongsViewModel.SortColumn):
                    OnPropertyChanged(nameof(SelectedSortOption));
                    break;
                case nameof(LibrarySongsViewModel.SortAscending):
                    OnPropertyChanged(nameof(IsAscending));
                    OnPropertyChanged(nameof(IsDescending));
                    break;
                case nameof(LibrarySongsViewModel.ShowOnlyFavorites):
                    OnPropertyChanged(nameof(ShowAllSongs));
                    OnPropertyChanged(nameof(ShowOnlyFavorites));
                    break;
            }
        };
        _songs.PropertyChanged += _songsPropertyChangedHandler;
    }

    public void Dispose()
    {
        if (_songsPropertyChangedHandler == null) return;
        _songs.PropertyChanged -= _songsPropertyChangedHandler;
        _songsPropertyChangedHandler = null;
    }

    /// <summary>
    /// Selected "Sort by" entry. Falls back to the Date Added entry when the persisted
    /// key isn't in the list, so a stale or hand-edited settings value still shows
    /// something selected instead of an empty ComboBox.
    /// </summary>
    public SongSortOption? SelectedSortOption
    {
        get => SortOptions.FirstOrDefault(o => o.Key == _songs.SortColumn)
               ?? SortOptions.FirstOrDefault(o => o.Key == "Date Added");
        set
        {
            if (value == null) return;
            _songs.SelectSortCommand.Execute(value.Key);
        }
    }

    public bool IsAscending => _songs.SortAscending;
    public bool IsDescending => !_songs.SortAscending;
    public bool ShowOnlyFavorites => _songs.ShowOnlyFavorites;
    public bool ShowAllSongs => !_songs.ShowOnlyFavorites;

    [RelayCommand]
    private void SetAscending() => _songs.SelectSortCommand.Execute("Ascending");

    [RelayCommand]
    private void SetDescending() => _songs.SelectSortCommand.Execute("Descending");

    [RelayCommand]
    private void SetAllSongs() => _songs.SetShowAllItemsCommand.Execute(null);

    [RelayCommand]
    private void SetOnlyFavorites() => _songs.SetShowOnlyFavoritesCommand.Execute(null);

    /// <summary>
    /// Returns every option in this dialog to its fresh-install value. Mirrors the
    /// defaults declared on <see cref="Models.AppSettings"/> — keep the two in step.
    /// </summary>
    [RelayCommand]
    private void RestoreDefaults()
    {
        Settings.ShowArtworkColumn = true;
        Settings.ShowArtistColumn = true;
        Settings.ShowAlbumColumn = true;
        Settings.ShowGenreColumn = true;
        Settings.ShowTimeColumn = true;
        Settings.ShowFavoritesColumn = true;
        Settings.ShowRatingColumn = true;
        Settings.ShowPlaysColumn = true;
        Settings.ShowBpmColumn = false;
        Settings.ShowBitrateColumn = false;
        Settings.ShowSampleRateColumn = false;

        _songs.SetShowAllItemsCommand.Execute(null);
        _songs.SelectSortCommand.Execute("Date Added");
        _songs.SelectSortCommand.Execute("Descending");
    }
}
