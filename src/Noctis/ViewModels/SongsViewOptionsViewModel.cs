using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;

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
    /// <para>
    /// Labels are the top bar's Sort menu entries (MainWindow.axaml, the same Main.* keys),
    /// so both surfaces name a field with the same, translated word. They were English
    /// literals: in Turkish the menu said "Eklenme Tarihi" while this list said "Date Added".
    /// </para>
    /// </summary>
    public IReadOnlyList<SongSortOption> SortOptions { get; } = new[]
    {
        new SongSortOption("Title", Loc.T("Main.Title")),
        new SongSortOption("Artist", Loc.T("Main.Artist")),
        new SongSortOption("Album", Loc.T("Main.Album")),
        new SongSortOption("Album Artist", Loc.T("Main.AlbumByArtist")),
        new SongSortOption("Genre", Loc.T("Main.Genre")),
        new SongSortOption("Time", Loc.T("Main.Time")),
        new SongSortOption("Plays", Loc.T("Main.Plays")),
        new SongSortOption("IsFavorite", Loc.T("Main.Favorite")),
        new SongSortOption("Rating", Loc.T("Main.Rating")),
        new SongSortOption("Year", Loc.T("Main.Year")),
        new SongSortOption("Bpm", Loc.T("Main.BPM")),
        new SongSortOption("Bitrate", Loc.T("Main.Bitrate")),
        new SongSortOption("SampleRate", Loc.T("Main.SampleRate")),
        new SongSortOption("Date Added", Loc.T("Main.DateAdded")),
        new SongSortOption("Date Modified", Loc.T("Main.DateModified")),
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

    // The two segmented pills are RadioButton pairs bound two-way. Only a segment being
    // checked acts; the partner's uncheck (false) is the group's echo and is ignored, so a
    // pick runs exactly one SelectSort / filter change.

    public bool IsAscending
    {
        get => _songs.SortAscending;
        set { if (value) SetAscending(); }
    }

    public bool IsDescending
    {
        get => !_songs.SortAscending;
        set { if (value) SetDescending(); }
    }

    public bool ShowOnlyFavorites
    {
        get => _songs.ShowOnlyFavorites;
        set { if (value) SetOnlyFavorites(); }
    }

    public bool ShowAllSongs
    {
        get => !_songs.ShowOnlyFavorites;
        set { if (value) SetAllSongs(); }
    }

    [RelayCommand]
    private void SetAscending() => _songs.SelectSortCommand.Execute("Ascending");

    [RelayCommand]
    private void SetDescending() => _songs.SelectSortCommand.Execute("Descending");

    [RelayCommand]
    private void SetAllSongs() => _songs.SetShowAllItemsCommand.Execute(null);

    [RelayCommand]
    private void SetOnlyFavorites() => _songs.SetShowOnlyFavoritesCommand.Execute(null);

    /// <summary>
    /// Returns every option in this dialog to its fresh-install value, read from
    /// <see cref="AppSettings"/> itself rather than restated here, so the two can't drift.
    /// </summary>
    [RelayCommand]
    private void RestoreDefaults()
    {
        var defaults = new AppSettings();
        Settings.ShowArtworkColumn = defaults.ShowArtworkColumn;
        Settings.ShowArtistColumn = defaults.ShowArtistColumn;
        Settings.ShowAlbumColumn = defaults.ShowAlbumColumn;
        Settings.ShowGenreColumn = defaults.ShowGenreColumn;
        Settings.ShowTimeColumn = defaults.ShowTimeColumn;
        Settings.ShowFavoritesColumn = defaults.ShowFavoritesColumn;
        Settings.ShowRatingColumn = defaults.ShowRatingColumn;
        Settings.ShowPlaysColumn = defaults.ShowPlaysColumn;
        Settings.ShowBpmColumn = defaults.ShowBpmColumn;
        Settings.ShowBitrateColumn = defaults.ShowBitrateColumn;
        Settings.ShowSampleRateColumn = defaults.ShowSampleRateColumn;

        if (defaults.SongsShowOnlyFavorites) _songs.SetShowOnlyFavoritesCommand.Execute(null);
        else _songs.SetShowAllItemsCommand.Execute(null);
        _songs.SelectSortCommand.Execute(defaults.SongsSortColumn);
        _songs.SelectSortCommand.Execute(defaults.SongsSortAscending ? "Ascending" : "Descending");
    }
}
