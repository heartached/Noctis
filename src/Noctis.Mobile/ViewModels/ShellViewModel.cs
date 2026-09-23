using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Root of the phone UI: three tabs, one stack of pages pushed over the active tab's root,
/// the Now Playing / Lyrics / Queue overlays and the mini bar. Every Android Back decision
/// is made here (<see cref="TryHandleBack"/>) so it is testable on Windows.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(LibraryViewModel library, NowPlayingViewModel player, LyricsPageViewModel lyrics)
    {
        Library = library;
        Player = player;
        Lyrics = lyrics;
        Player.PropertyChanged += OnPlayerChanged;
    }

    public LibraryViewModel Library { get; }
    public NowPlayingViewModel Player { get; }
    public LyricsPageViewModel Lyrics { get; }

    /// <summary>Pages pushed over the active tab's root, oldest first. A tab switch clears it.</summary>
    public ObservableCollection<MobilePage> Pages { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMiniBarVisible))]
    private bool _isNowPlayingOpen;

    [ObservableProperty] private bool _isQueueOpen;
    [ObservableProperty] private bool _isLyricsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSelected), nameof(IsLibrarySelected), nameof(IsSearchSelected),
        nameof(IsHomeRootVisible), nameof(IsLibraryRootVisible), nameof(IsSearchRootVisible))]
    private MobileTab _selectedTab = MobileTab.Library;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPage), nameof(IsHomeRootVisible), nameof(IsLibraryRootVisible), nameof(IsSearchRootVisible))]
    private MobilePage? _currentPage;

    /// <summary>
    /// System-bar insets (status bar on top, navigation/gesture bar at the bottom) from the
    /// Android insets manager; zero in headless tests. ShellView keeps it current.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopSafePadding), nameof(BottomSafePadding))]
    private Thickness _safeArea;

    public bool IsHomeSelected => SelectedTab == MobileTab.Home;
    public bool IsLibrarySelected => SelectedTab == MobileTab.Library;
    public bool IsSearchSelected => SelectedTab == MobileTab.Search;

    public bool HasPage => CurrentPage != null;
    public bool IsHomeRootVisible => IsHomeSelected && !HasPage;
    public bool IsLibraryRootVisible => IsLibrarySelected && !HasPage;
    public bool IsSearchRootVisible => IsSearchSelected && !HasPage;

    public Thickness TopSafePadding => new(0, SafeArea.Top, 0, 0);
    public Thickness BottomSafePadding => new(0, 0, 0, SafeArea.Bottom);

    /// <summary>The floating pill: whenever a track is loaded and Now Playing is not covering it.</summary>
    public bool IsMiniBarVisible => Player.HasTrack && !IsNowPlayingOpen;

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingViewModel.HasTrack) or nameof(NowPlayingViewModel.CurrentTrack))
            OnPropertyChanged(nameof(IsMiniBarVisible));
    }

    /// <summary>Push <paramref name="page"/> over the current tab. A page opened from Now
    /// Playing (the artist link) closes the player first.</summary>
    public void Navigate(MobilePage page)
    {
        if (IsNowPlayingOpen) CloseNowPlaying();
        Pages.Add(page);
        CurrentPage = page;
    }

    /// <summary>Pop the top page. False when the tab root is already showing.</summary>
    public bool GoBack()
    {
        if (Pages.Count == 0) return false;
        var top = Pages[^1];
        Pages.RemoveAt(Pages.Count - 1);
        top.OnClosed();
        CurrentPage = Pages.Count > 0 ? Pages[^1] : null;
        return true;
    }

    public void PopToRoot()
    {
        while (GoBack()) { }
    }

    [RelayCommand] private void NavigateBack() => GoBack();

    /// <summary>A tab tap always lands on that tab's root, so re-tapping the current tab pops to it.</summary>
    [RelayCommand]
    private void SelectTab(MobileTab tab)
    {
        PopToRoot();
        SelectedTab = tab;
    }

    [RelayCommand] private void OpenNowPlaying() => IsNowPlayingOpen = true;

    [RelayCommand]
    private void CloseNowPlaying()
    {
        // The Queue sits above Now Playing, so closing the page must take it down too;
        // otherwise the Queue overlay is left floating over the Library page.
        IsQueueOpen = false;
        IsLyricsOpen = false;
        IsNowPlayingOpen = false;
    }

    [RelayCommand] private void ToggleQueue() => IsQueueOpen = !IsQueueOpen;

    [RelayCommand] private void ToggleLyrics() => IsLyricsOpen = !IsLyricsOpen;

    /// <summary>
    /// Android Back: the topmost thing closes first — Queue, Lyrics, Now Playing, then pushed
    /// pages, then a non-start tab returns to Library. Returns whether the press was consumed;
    /// only at the Library root does the activity fall through to the system default (finish).
    /// </summary>
    public bool TryHandleBack()
    {
        if (IsQueueOpen) { IsQueueOpen = false; return true; }
        if (IsLyricsOpen) { IsLyricsOpen = false; return true; }
        if (IsNowPlayingOpen) { IsNowPlayingOpen = false; return true; }
        if (GoBack()) return true;
        if (SelectedTab != MobileTab.Library) { SelectedTab = MobileTab.Library; return true; }
        return false;
    }

    /// <summary>Tap on a song row: play the song list from that row.</summary>
    [RelayCommand]
    private void PlaySong(Track? track)
    {
        if (track == null) return;
        var songs = Library.Songs.ToList();
        var index = songs.IndexOf(track);
        if (index < 0) return;
        Player.PlayTracks(songs, index);
    }

    public async Task InitializeAsync()
    {
        await Library.InitializeAsync();
        // Lyrics settings before the queue restore: restoring sets CurrentTrack, which loads
        // that track's lyrics with the saved split-word and layer preferences.
        await Lyrics.InitializeAsync();
        await Player.RestoreStateAsync();
    }

    public Task SaveStateAsync() => Player.SaveStateAsync();
}
