using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>The saved playlists (playlists.json), pinned first; searchable and sortable from
/// the header's tools, as a song list is.</summary>
public sealed partial class PlaylistListPageViewModel : MobilePage, ISortable
{
    public PlaylistListPageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        SortChoices = new[]
        {
            new SortChoice("Mobile.Sort.Title", PlaylistSort.Title) { IsSelected = true },
            new SortChoice("Mobile.Sort.RecentlyUpdated", PlaylistSort.RecentlyUpdated),
            new SortChoice("Mobile.RecentlyAdded", PlaylistSort.RecentlyAdded),
        };
        ApplySavedSort();
        Shell.ListSortChanged += OnListSortChanged;
        Shell.Library.Refreshed += OnRefreshed;
        Refresh();
    }

    /// <summary>The key the list's sort is saved under (the tab's list and Library's Playlists page share it).</summary>
    internal const string SortKey = "Nav.Playlists";

    public ShellViewModel Shell { get; }

    public override string Title => Loc.T("Nav.Playlists");

    /// <summary>The playlists the page shows, in order (the search and sort applied).</summary>
    public BulkObservableCollection<Playlist> Playlists { get; } = new();

    /// <summary>The same, as rows: the cover and "1 song" / "12 songs".</summary>
    public BulkObservableCollection<PlaylistRow> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaylists))]
    private int _playlistCount;

    public bool HasPlaylists => PlaylistCount > 0;

    /// <summary>The search found nothing (the tools stay up so it can be cleared).</summary>
    public bool ShowNoMatches => HasPlaylists && Playlists.Count == 0;

    /// <summary>The "Find in Playlists" text (FindField); the list follows it as it changes.</summary>
    [ObservableProperty] private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => Refresh();

    public IReadOnlyList<SortChoice> SortChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSorted))]
    private PlaylistSort _sort = PlaylistSort.Title;

    public bool IsSorted => Sort != PlaylistSort.Title;

    public bool HasSortDirection => false;
    public bool IsDescending => false;
    /// <summary>The toolbar's sort button: the sort menu on the glass pop menu, out of the button.</summary>
    [RelayCommand]
    private void ShowSortMenu() => Shell.ShowOptions(OptionsMenuViewModel.ForSort(Shell, this));

    public void ChooseDirection(bool descending) { }

    public void ChooseSort(SortChoice choice)
    {
        foreach (var c in SortChoices) c.IsSelected = ReferenceEquals(c, choice);
        Sort = (PlaylistSort)choice.Key;
        Refresh();
        Shell.SaveListSort(SortKey, Sort, false, PlaylistSort.Title);
    }

    /// <summary>Puts the list in its saved sort (by name when none is saved); true when that changed it.</summary>
    private bool ApplySavedSort()
    {
        if (!Shell.TryGetListSort<PlaylistSort>(SortKey, out var sort, out _)) sort = PlaylistSort.Title;
        if (sort == Sort) return false;
        foreach (var c in SortChoices) c.IsSelected = Equals(c.Key, sort);
        Sort = sort;
        return true;
    }

    private void OnListSortChanged(object? sender, string? key)
    {
        if ((key == null || key == SortKey) && ApplySavedSort()) Refresh();
    }

    public void Refresh()
    {
        IEnumerable<Playlist> list = Shell.Library.Playlists;
        PlaylistCount = Shell.Library.Playlists.Count;
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var match = new MobileSearch.Matcher(FilterText);
            list = list.Where(p => match.Hit(p.Name, SearchText.Normalize(p.Name)));
        }
        var pinnedFirst = list.OrderByDescending(p => p.IsPinned);
        var sorted = Sort switch
        {
            PlaylistSort.RecentlyUpdated => pinnedFirst.ThenByDescending(p => p.ModifiedAt),
            PlaylistSort.RecentlyAdded => pinnedFirst.ThenByDescending(p => p.CreatedAt),
            _ => pinnedFirst.ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        // Only when something changed: a refresh of the same playlists must not Reset the list.
        MobileLibrary.ReplaceIfChanged(Playlists, sorted.ToList());
        MobileLibrary.ReplaceIfChanged(Rows, Playlists.Select(p => new PlaylistRow(p, Shell.Library.PlaylistArtwork(p))).ToList());
        OnPropertyChanged(nameof(ShowNoMatches));
    }

    [RelayCommand]
    private void Open(Playlist? playlist) => Shell.OpenPlaylistCommand.Execute(playlist);

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    public override void OnClosed()
    {
        Shell.Library.Refreshed -= OnRefreshed;
        Shell.ListSortChanged -= OnListSortChanged;
    }
}

public enum PlaylistSort { Title, RecentlyUpdated, RecentlyAdded }

/// <summary>A playlist row: its cover (the first of its songs that has one) and its song count.</summary>
public sealed record PlaylistRow(Playlist Playlist, string? ArtworkPath)
{
    public string Name => Playlist.Name;
    public string Subtitle => Loc.Plural("Mobile.Count.Songs", Playlist.TrackIds.Count);
}
