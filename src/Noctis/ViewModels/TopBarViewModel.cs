using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Manages the top search bar state.
/// Search text changes are debounced and forwarded to the active content ViewModel.
/// </summary>
public partial class TopBarViewModel : ViewModelBase
{
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isSearchFocused;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitleDisplay))]
    private string _currentTabName = "Library";

    /// <summary>Header title: reflects the Cover Flow / Collage view when active, otherwise the section name.
    /// Collage is a Cover Flow sub-mode, so its label only applies while Cover Flow is active.</summary>
    public string PageTitleDisplay => IsCoverFlowMode ? (IsCollageMode ? "Cover Collage" : "Cover Flow") : CurrentTabName;
    [ObservableProperty] private string _searchWatermark = "Search in Library";
    [ObservableProperty] private bool _isSearchVisible = true;
    // The search pill is a persistent (non-light-dismiss) popup: it stays open while
    // the user browses/filters and only closes explicitly (toggle, Esc, or navigating
    // to a page without search).
    [ObservableProperty] private bool _isSearchOpen;

    // Back button (shown in detail views like Album Detail, Genre Detail, etc.)
    [ObservableProperty] private bool _isBackButtonVisible;
    [ObservableProperty] private bool _isGenericBackButtonVisible;
    [ObservableProperty] private bool _isAlbumDetailBackButtonVisible;
    [ObservableProperty] private string _backButtonText = "";
    [ObservableProperty] private string _backButtonDisplayText = "Back";
    [ObservableProperty] private string _albumDetailBackButtonDisplayText = "Back";
    [ObservableProperty] private ICommand? _backCommand;

    // Optional title shown next to the Back button (e.g., "More By {Artist}")
    [ObservableProperty] private string _backContextTitle = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBarContent))]
    private bool _isBackContextTitleVisible;

    /// <summary>
    /// True when the top bar actually renders something (title, context title, chips,
    /// action buttons, or the view-mode toggle). Detail pages like Album Detail show
    /// none of these — Back/Search live in the sidebar rail — so the bar row collapses
    /// there instead of leaving a fixed empty strip above the content.
    /// </summary>
    public bool HasBarContent =>
        IsPageTitleVisible
        || IsBackContextTitleVisible
        || HasReleaseTypeChips
        || PageActionsVisible
        || HasPlaylistActions
        || HasViewModeToggle
        || HasArtistActions
        || HasFavoritesActions;

    public void ShowBackButton(string text, ICommand command, string? contextTitle = null)
    {
        var displayText = string.IsNullOrWhiteSpace(text) ? "Back" : text;
        BackButtonText = displayText;
        BackButtonDisplayText = displayText;
        BackCommand = command;
        IsBackButtonVisible = true;
        IsGenericBackButtonVisible = true;
        IsAlbumDetailBackButtonVisible = false;
        BackContextTitle = contextTitle ?? string.Empty;
        IsBackContextTitleVisible = !string.IsNullOrWhiteSpace(contextTitle);
    }

    public void ShowAlbumDetailBackButton(string text, ICommand command)
    {
        var displayText = string.IsNullOrWhiteSpace(text) ? "Back" : text;
        AlbumDetailBackButtonDisplayText = displayText;
        BackButtonText = displayText;
        BackButtonDisplayText = displayText;
        BackCommand = command;
        IsBackButtonVisible = true;
        IsGenericBackButtonVisible = false;
        IsAlbumDetailBackButtonVisible = true;
        BackContextTitle = string.Empty;
        IsBackContextTitleVisible = false;
    }

    public void HideBackButton()
    {
        IsBackButtonVisible = false;
        IsGenericBackButtonVisible = false;
        IsAlbumDetailBackButtonVisible = false;
        BackCommand = null;
        BackButtonText = "";
        BackButtonDisplayText = "Back";
        AlbumDetailBackButtonDisplayText = "Back";
        BackContextTitle = "";
        IsBackContextTitleVisible = false;
        UpdatePageTitleVisibility();
    }

    // Page title (shown on left when back button is hidden and on a library page)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBarContent))]
    private bool _isPageTitleVisible;

    // Page action buttons (set by navigation for pages that have them)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageActionsVisible))]
    [NotifyPropertyChangedFor(nameof(HasBarContent))]
    private bool _hasPageActions;
    [ObservableProperty] private ICommand? _pageShuffleCommand;
    [ObservableProperty] private ICommand? _pageQueueCommand;

    /// <summary>Page actions (Shuffle/Queue/Options) are hidden in Cover Flow mode — they don't apply there.</summary>
    public bool PageActionsVisible => HasPageActions && !IsCoverFlowMode;

    // Playlist-specific action buttons
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBarContent))]
    private bool _hasPlaylistActions;
    [ObservableProperty] private ICommand? _pageCreatePlaylistCommand;
    [ObservableProperty] private ICommand? _pageCreateSmartPlaylistCommand;
    [ObservableProperty] private ICommand? _pageImportPlaylistCommand;

    // Global view mode toggle (Library / Cover Flow) — shown on Home, Songs, Albums, Artists, Folders, Playlists, Favorites
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBarContent))]
    private bool _hasViewModeToggle;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageActionsVisible))]
    [NotifyPropertyChangedFor(nameof(PageTitleDisplay))]
    [NotifyPropertyChangedFor(nameof(HasBarContent))]
    private bool _isCoverFlowMode;
    [ObservableProperty] private ICommand? _setLibraryModeCommand;
    [ObservableProperty] private ICommand? _setCoverFlowModeCommand;

    // Cover Flow sub-mode (Carousel / Collage) — icon segment shown on the pill only while Cover Flow is active
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitleDisplay))]
    private bool _isCollageMode;
    [ObservableProperty] private ICommand? _toggleCollageModeCommand;

    // Artist discography action buttons
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBarContent))]
    private bool _hasArtistActions;
    [ObservableProperty] private ICommand? _pageShuffleArtistCommand;
    [ObservableProperty] private ICommand? _pagePlayArtistCommand;

    // Favorites action buttons
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBarContent))]
    private bool _hasFavoritesActions;
    [ObservableProperty] private ICommand? _pageShuffleFavoritesCommand;
    [ObservableProperty] private ICommand? _pagePlayFavoritesCommand;
    [ObservableProperty] private bool _pageShowOnlyFavorites;
    [ObservableProperty] private bool _pageSortAscending = true;
    [ObservableProperty] private string _pageSortColumn = string.Empty;
    [ObservableProperty] private ICommand? _pageSetShowAllItemsCommand;
    [ObservableProperty] private ICommand? _pageSetShowOnlyFavoritesCommand;
    [ObservableProperty] private ICommand? _pageSortCommand;

    // Computed inverses for non-compiled binding compatibility
    public bool PageShowAllItems => !PageShowOnlyFavorites;
    public bool PageSortDescending => !PageSortAscending;

    // Per-field active-sort flags used to render checkmarks in the Sort By submenu.
    // Only sorts without a clickable column header live in the menu; the rest are
    // sorted directly from the Songs page column headers.
    public bool PageSortByYear      => string.Equals(PageSortColumn, "Year",       StringComparison.OrdinalIgnoreCase);
    public bool PageSortByDateAdded => string.Equals(PageSortColumn, "Date Added", StringComparison.OrdinalIgnoreCase);

    partial void OnPageShowOnlyFavoritesChanged(bool value) => OnPropertyChanged(nameof(PageShowAllItems));
    partial void OnPageSortAscendingChanged(bool value) => OnPropertyChanged(nameof(PageSortDescending));
    partial void OnPageSortColumnChanged(string value)
    {
        OnPropertyChanged(nameof(PageSortByYear));
        OnPropertyChanged(nameof(PageSortByDateAdded));
    }

    public void ShowPageActions(
        ICommand shuffleCommand,
        ICommand queueCommand,
        bool showOnlyFavorites,
        bool sortAscending,
        string sortColumn,
        ICommand setShowAllItemsCommand,
        ICommand setShowOnlyFavoritesCommand,
        ICommand sortCommand)
    {
        PageShuffleCommand = shuffleCommand;
        PageQueueCommand = queueCommand;
        PageShowOnlyFavorites = showOnlyFavorites;
        PageSortAscending = sortAscending;
        PageSortColumn = sortColumn ?? string.Empty;
        PageSetShowAllItemsCommand = setShowAllItemsCommand;
        PageSetShowOnlyFavoritesCommand = setShowOnlyFavoritesCommand;
        PageSortCommand = sortCommand;
        HasPageActions = true;
    }

    public void HidePageActions()
    {
        HasPageActions = false;
        PageShuffleCommand = null;
        PageQueueCommand = null;
        PageSetShowAllItemsCommand = null;
        PageSetShowOnlyFavoritesCommand = null;
        PageSortCommand = null;
    }

    public void ShowPlaylistActions(ICommand createPlaylistCommand, ICommand createSmartPlaylistCommand, ICommand importPlaylistCommand)
    {
        PageCreatePlaylistCommand = createPlaylistCommand;
        PageCreateSmartPlaylistCommand = createSmartPlaylistCommand;
        PageImportPlaylistCommand = importPlaylistCommand;
        HasPlaylistActions = true;
    }

    public void HidePlaylistActions()
    {
        HasPlaylistActions = false;
        PageCreatePlaylistCommand = null;
        PageCreateSmartPlaylistCommand = null;
        PageImportPlaylistCommand = null;
    }

    public void ShowArtistActions(ICommand shuffleCommand, ICommand playCommand)
    {
        PageShuffleArtistCommand = shuffleCommand;
        PagePlayArtistCommand = playCommand;
        HasArtistActions = true;
    }

    public void HideArtistActions()
    {
        HasArtistActions = false;
        PageShuffleArtistCommand = null;
        PagePlayArtistCommand = null;
    }

    public void ShowFavoritesActions(ICommand shuffleCommand, ICommand playCommand)
    {
        PageShuffleFavoritesCommand = shuffleCommand;
        PagePlayFavoritesCommand = playCommand;
        HasFavoritesActions = true;
    }

    public void HideFavoritesActions()
    {
        HasFavoritesActions = false;
        PageShuffleFavoritesCommand = null;
        PagePlayFavoritesCommand = null;
    }

    public void ShowViewModeToggle(ICommand setLibraryMode, ICommand setCoverFlowMode, bool isCoverFlowMode,
        ICommand? toggleCollageMode = null, bool isCollageMode = false)
    {
        SetLibraryModeCommand = setLibraryMode;
        SetCoverFlowModeCommand = setCoverFlowMode;
        IsCoverFlowMode = isCoverFlowMode;
        ToggleCollageModeCommand = toggleCollageMode;
        IsCollageMode = isCollageMode;
        HasViewModeToggle = true;
    }

    public void HideViewModeToggle()
    {
        HasViewModeToggle = false;
        SetLibraryModeCommand = null;
        SetCoverFlowModeCommand = null;
        IsCoverFlowMode = false;
        ToggleCollageModeCommand = null;
        IsCollageMode = false;
    }

    // Release-type filter chips (All / Albums / Singles / EPs / Other) — shown next
    // to the title on the Albums page. The collection and command are owned by
    // LibraryAlbumsViewModel; the top bar just mirrors them for placement.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBarContent))]
    private bool _hasReleaseTypeChips;
    [ObservableProperty] private ObservableCollection<ReleaseTypeChip>? _releaseTypeChips;
    [ObservableProperty] private ICommand? _releaseTypeChipCommand;

    // Quality chips + sort dropdown shown alongside the release-type strip;
    // owned by LibraryAlbumsViewModel, mirrored here for top-bar placement.
    [ObservableProperty] private ObservableCollection<QualityChip>? _qualityChips;
    [ObservableProperty] private ICommand? _qualityChipCommand;
    [ObservableProperty] private ICommand? _albumSortCommand;
    [ObservableProperty] private string _albumSortLabel = "Default";

    // Dropdown variants of the release-type / quality filters (albums grid top bar).
    [ObservableProperty] private ICommand? _releaseTypeFilterCommand;
    [ObservableProperty] private string _releaseTypeFilterLabel = "All";
    [ObservableProperty] private ICommand? _qualityFilterCommand;
    [ObservableProperty] private string _qualityFilterLabel = "All";

    public void ShowReleaseTypeChips(ObservableCollection<ReleaseTypeChip> chips, ICommand selectCommand,
        ObservableCollection<QualityChip>? qualityChips = null, ICommand? qualityCommand = null,
        ICommand? sortCommand = null, ICommand? releaseTypeFilterCommand = null, ICommand? qualityFilterCommand = null)
    {
        ReleaseTypeChips = chips;
        ReleaseTypeChipCommand = selectCommand;
        QualityChips = qualityChips;
        QualityChipCommand = qualityCommand;
        AlbumSortCommand = sortCommand;
        ReleaseTypeFilterCommand = releaseTypeFilterCommand;
        QualityFilterCommand = qualityFilterCommand;
        HasReleaseTypeChips = true;
    }

    public void HideReleaseTypeChips()
    {
        HasReleaseTypeChips = false;
    }

    private void UpdatePageTitleVisibility()
    {
        IsPageTitleVisible = !IsBackButtonVisible
            && CurrentTabName is not ("Lyrics" or "Queue");
    }

    /// <summary>Fires when the debounced search text changes.</summary>
    public event EventHandler<string>? SearchTextChanged;

    private CancellationTokenSource? _debounceToken;

    partial void OnCurrentTabNameChanged(string value)
    {
        IsSearchVisible = value is not ("Home" or "Settings" or "Lyrics");
        if (!IsSearchVisible) IsSearchOpen = false;
        SearchWatermark = $"Search in {value}";
        UpdatePageTitleVisibility();
    }

    partial void OnIsBackButtonVisibleChanged(bool value)
    {
        UpdatePageTitleVisibility();
    }

    partial void OnSearchTextChanged(string value)
    {
        // Cancel and dispose previous debounce
        _debounceToken?.Cancel();
        _debounceToken?.Dispose();
        _debounceToken = new CancellationTokenSource();
        var token = _debounceToken.Token;

        // Debounce: wait 300ms before applying the filter
        Task.Delay(300, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
            {
                DebugLogger.Info(DebugLogger.Category.Search, "SearchDebounced", $"query=\"{value}\"");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SearchTextChanged?.Invoke(this, value);
                });
            }
        }, token);
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        IsSearchFocused = false;
    }

    [RelayCommand]
    private void FocusSearch()
    {
        IsSearchFocused = true;
    }

    /// <summary>Raised when search should open/focus (Ctrl+F), even if already open.</summary>
    public event EventHandler? SearchOpenRequested;

    [RelayCommand]
    private void OpenSearch()
    {
        if (!IsSearchVisible) return;
        IsSearchOpen = true;
        SearchOpenRequested?.Invoke(this, EventArgs.Empty);
    }
}
