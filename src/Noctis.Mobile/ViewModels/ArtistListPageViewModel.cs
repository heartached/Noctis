using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// One artist row: the Core artist, the album cover that stands in for it, and its photo once
/// the phone has one (<see cref="ShellViewModel.ArtistPhotos"/>; set when the row comes on
/// screen, so it notifies). Equal by artist and cover, not photo: a refresh that changes
/// nothing else leaves the grid alone.
/// </summary>
public sealed record ArtistListItem(Artist Artist, string? CoverPath) : INotifyPropertyChanged
{
    private string? _photoPath;

    public string Name => Artist.Name;

    /// <summary>The artist's photo on the phone; null until (and unless) there is one.</summary>
    public string? PhotoPath
    {
        get => _photoPath;
        set
        {
            if (_photoPath == value) return;
            _photoPath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PhotoPath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ArtworkPath)));
        }
    }

    /// <summary>What the circle shows: the photo, else the cover.</summary>
    public string? ArtworkPath => PhotoPath ?? CoverPath;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Equals(ArtistListItem? other) =>
        other is not null && ReferenceEquals(Artist, other.Artist) && CoverPath == other.CoverPath;

    public override int GetHashCode() => HashCode.Combine(Artist, CoverPath);

    public string Subtitle =>
        $"{Artist.AlbumCount} album{(Artist.AlbumCount == 1 ? "" : "s")} · {Artist.TrackCount} song{(Artist.TrackCount == 1 ? "" : "s")}";

    /// <summary>What a circle without a picture shows ("Bruno Mars" → "BM").</summary>
    public string Initials => ArtistIndex.Initials(Name);

    /// <summary>The soft tint behind <see cref="Initials"/>.</summary>
    public IBrush PlaceholderBrush => ArtistIndex.PlaceholderBrush(Name);
}

/// <summary>One line of the Artists grid. Rows (not a wrap panel) so the grid virtualises, as
/// the album grid does (AlbumRow). Equal when it holds the same artists, so a refresh that
/// changes nothing here (a heart, a pin) leaves the grid alone.</summary>
public sealed record ArtistGridRow(IReadOnlyList<ArtistListItem> Artists)
{
    public bool Equals(ArtistGridRow? other) => other != null && Artists.SequenceEqual(other.Artists);

    public override int GetHashCode() => Artists.Count == 0 ? 0 : Artists[0].GetHashCode();
}

/// <summary>
/// All artists as Apple Music's Artists page: circles three to a row, A to Z with "#" (digits,
/// symbols, other scripts) last, a "Find in Artists" field that filters as you type, and an
/// A–Z strip whose letters jump to <see cref="RowIndexForLetter"/>.
/// </summary>
public sealed partial class ArtistListPageViewModel : MobilePage
{
    public const int Columns = 3;

    /// <summary>Every grid row's height (circle, name, gap): fixed, so a letter's row sits at
    /// row × height and the strip's jump needs no measuring.</summary>
    public const double RowHeight = 150;

    // Every artist in grid order, with its index letter and search key worked out once per refresh.
    private List<(ArtistListItem Item, string Letter, string Key)> _all = new();
    private List<(ArtistListItem Item, string Letter, string Key)> _shown = new();

    public ArtistListPageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        Photos = new ArtistPhotoRequests(() => shell.ArtistPhotos);
        Shell.Library.Refreshed += OnRefreshed;
        Refresh();
    }

    /// <summary>The photo asks of the circles on screen (the view reports rows coming and going).</summary>
    internal ArtistPhotoRequests Photos { get; }

    public ShellViewModel Shell { get; }

    public override string Title => Loc.T("Nav.Artists");

    /// <summary>The artists the grid shows (all, or the search's matches), in grid order.</summary>
    public IReadOnlyList<ArtistListItem> Artists => _shown.Select(x => x.Item).ToList();

    public BulkObservableCollection<ArtistGridRow> Rows { get; } = new();

    public IReadOnlyList<string> IndexLetters => ArtistIndex.Letters;

    /// <summary>The "Find in Artists" text; the grid follows it as it changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltering))]
    private string _query = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArtists), nameof(ShowNoResults), nameof(ShowIndex))]
    private int _artistCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoResults), nameof(ShowIndex))]
    private int _resultCount;

    public bool HasArtists => ArtistCount > 0;

    public bool IsFiltering => !string.IsNullOrWhiteSpace(Query);

    public bool ShowNoResults => HasArtists && ResultCount == 0;

    /// <summary>The A–Z strip: over the whole list only; a search's few matches need no index.</summary>
    public bool ShowIndex => ResultCount > 0 && !IsFiltering;

    public void Refresh()
    {
        var library = Shell.Library.Service;
        var artwork = MobileLibrary.ArtistArtwork(library);
        _all = library.Artists
            .Select(a => (Item: Photos.Fill(new ArtistListItem(a, artwork.GetValueOrDefault(a.Name))), Letter: ArtistIndex.LetterOf(a.Name),
                Key: SearchText.Normalize(a.Name)))
            .OrderBy(x => ArtistIndex.OrderOf(x.Letter))
            .ThenBy(x => ArtistIndex.SortName(x.Item.Name), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ArtistCount = _all.Count;
        ApplyFilter();
    }

    partial void OnQueryChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(ShowIndex));
    }

    /// <summary>The desktop's search rule (MobileSearch): accent/case/punctuation-insensitive
    /// substring of the name, or a raw substring for a query of punctuation only.</summary>
    private void ApplyFilter()
    {
        var raw = Query.Trim();
        if (raw.Length == 0)
        {
            _shown = _all;
        }
        else
        {
            var key = SearchText.Normalize(raw);
            _shown = _all.Where(x => key.Length > 0
                    ? x.Key.Contains(key, StringComparison.Ordinal)
                    : x.Item.Name.Contains(raw, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        MobileLibrary.ReplaceIfChanged(Rows, _shown.Select(x => x.Item).Chunk(Columns).Select(chunk => new ArtistGridRow(chunk)).ToList());
        ResultCount = _shown.Count;
        OnPropertyChanged(nameof(Artists));
    }

    /// <summary>
    /// The grid row a strip letter jumps to: the row holding the first artist filed under that
    /// letter or, when none is, under the next letter that has one (Apple's strip behaves the
    /// same); past the last letter in use, the last row. -1 with nothing listed.
    /// </summary>
    public int RowIndexForLetter(string letter)
    {
        if (_shown.Count == 0) return -1;
        var order = ArtistIndex.OrderOf(letter);
        if (order < 0) return -1;
        var index = _shown.FindIndex(x => ArtistIndex.OrderOf(x.Letter) >= order);
        return (index < 0 ? _shown.Count - 1 : index) / Columns;
    }

    [RelayCommand] private void ClearQuery() => Query = string.Empty;

    [RelayCommand]
    private void Open(ArtistListItem? item)
    {
        if (item != null) Shell.OpenArtistCommand.Execute(item.Name);
    }

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    public override void OnClosed()
    {
        Photos.CancelAll();
        Shell.Library.Refreshed -= OnRefreshed;
    }
}
