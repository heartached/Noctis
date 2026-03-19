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
    [ObservableProperty] private string _currentTabName = "Library";
    [ObservableProperty] private bool _isSearchVisible = true;

    // Back button (shown in detail views like Album Detail, Genre Detail, etc.)
    [ObservableProperty] private bool _isBackButtonVisible;
    [ObservableProperty] private string _backButtonText = "";
    [ObservableProperty] private ICommand? _backCommand;

    public void ShowBackButton(string text, ICommand command)
    {
        BackButtonText = text;
        BackCommand = command;
        IsBackButtonVisible = true;
    }

    public void HideBackButton()
    {
        IsBackButtonVisible = false;
        BackCommand = null;
        BackButtonText = "";
        UpdatePageTitleVisibility();
    }

    // Page title (shown on left when back button is hidden and on a library page)
    [ObservableProperty] private bool _isPageTitleVisible;

    // Page action buttons (set by navigation for pages that have them)
    [ObservableProperty] private bool _hasPageActions;
    [ObservableProperty] private ICommand? _pageShuffleCommand;
    [ObservableProperty] private ICommand? _pageQueueCommand;

    // Playlist-specific action buttons
    [ObservableProperty] private bool _hasPlaylistActions;
    [ObservableProperty] private ICommand? _pageCreateSmartPlaylistCommand;

    // Albums view mode toggle (Library / Up Next)
    [ObservableProperty] private bool _hasAlbumsViewModeToggle;
    [ObservableProperty] private bool _isAlbumsCoverFlowMode;
    [ObservableProperty] private ICommand? _albumsSetLibraryModeCommand;
    [ObservableProperty] private ICommand? _albumsSetCoverFlowModeCommand;

    // Artist discography action buttons
    [ObservableProperty] private bool _hasArtistActions;
    [ObservableProperty] private ICommand? _pageShuffleArtistCommand;
    [ObservableProperty] private ICommand? _pagePlayArtistCommand;
    [ObservableProperty] private bool _pageShowOnlyFavorites;
    [ObservableProperty] private bool _pageSortAscending = true;
    [ObservableProperty] private ICommand? _pageSetShowAllItemsCommand;
    [ObservableProperty] private ICommand? _pageSetShowOnlyFavoritesCommand;
    [ObservableProperty] private ICommand? _pageSortCommand;

    // Computed inverses for non-compiled binding compatibility
    public bool PageShowAllItems => !PageShowOnlyFavorites;
    public bool PageSortDescending => !PageSortAscending;

    partial void OnPageShowOnlyFavoritesChanged(bool value) => OnPropertyChanged(nameof(PageShowAllItems));
    partial void OnPageSortAscendingChanged(bool value) => OnPropertyChanged(nameof(PageSortDescending));

    public void ShowPageActions(
        ICommand shuffleCommand,
        ICommand queueCommand,
        bool showOnlyFavorites,
        bool sortAscending,
        ICommand setShowAllItemsCommand,
        ICommand setShowOnlyFavoritesCommand,
        ICommand sortCommand)
    {
        PageShuffleCommand = shuffleCommand;
        PageQueueCommand = queueCommand;
        PageShowOnlyFavorites = showOnlyFavorites;
        PageSortAscending = sortAscending;
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

    public void ShowPlaylistActions(ICommand createSmartPlaylistCommand)
    {
        PageCreateSmartPlaylistCommand = createSmartPlaylistCommand;
        HasPlaylistActions = true;
    }

    public void HidePlaylistActions()
    {
        HasPlaylistActions = false;
        PageCreateSmartPlaylistCommand = null;
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

    /// <summary>Fires when the albums view mode toggle changes.</summary>
    public event EventHandler<bool>? AlbumsViewModeChanged;

    public void ShowAlbumsViewModeToggle(ICommand setLibraryMode, ICommand setCoverFlowMode, bool isCoverFlowMode)
    {
        AlbumsSetLibraryModeCommand = setLibraryMode;
        AlbumsSetCoverFlowModeCommand = setCoverFlowMode;
        IsAlbumsCoverFlowMode = isCoverFlowMode;
        HasAlbumsViewModeToggle = true;
    }

    public void HideAlbumsViewModeToggle()
    {
        HasAlbumsViewModeToggle = false;
        AlbumsSetLibraryModeCommand = null;
        AlbumsSetCoverFlowModeCommand = null;
    }

    partial void OnIsAlbumsCoverFlowModeChanged(bool value)
    {
        AlbumsViewModeChanged?.Invoke(this, value);
    }

    private void UpdatePageTitleVisibility()
    {
        IsPageTitleVisible = !IsBackButtonVisible
            && CurrentTabName is not ("Home" or "Settings" or "Lyrics" or "Queue");
    }

    /// <summary>Fires when the debounced search text changes.</summary>
    public event EventHandler<string>? SearchTextChanged;

    private CancellationTokenSource? _debounceToken;

    partial void OnCurrentTabNameChanged(string value)
    {
        IsSearchVisible = value is not ("Home" or "Settings" or "Lyrics");
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
}
