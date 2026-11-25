using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// "Playing from …" (Discord 11-24, sina 4.3.7 / 4.3.4): the page a queue was started from (an
/// album, playlist, artist, Songs…), shown as a link in the queue drawer's header and, with
/// Settings › Player › Song Title Opens Where It Plays From, the target of the island title's
/// click. A page sets it with <see cref="WithSource"/> right before it starts the queue; any
/// other start (radio, command palette, a dropped file…) leaves it empty. Add to Queue / Play
/// Next do not change it, and a song they added is not "from" it (the title opens its album).
/// </summary>
public partial class PlayerViewModel
{
    /// <summary>Where the queue was started from; null when unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaySourceName), nameof(PlayingFromText), nameof(PlayingFromAccessibleName), nameof(HasPlaySource), nameof(TitleClickTip))]
    private PlaySource? _playSource;

    /// <summary>Settings › Player › Show Where Music Plays From (on by default).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaySource))]
    private bool _showPlaySource = true;

    /// <summary>Settings › Player › Song Title Opens Where It Plays From (off by default: the
    /// title opens the song's album, as before).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleClickTip))]
    private bool _titleOpensPlaySource;

    /// <summary>The source the next queue start takes (<see cref="WithSource"/>).</summary>
    private PlaySource? _pendingPlaySource;

    /// <summary>The songs of the list the source started, so a song added later with Play Next
    /// is not shown as "from" it. Null after a restart (not saved): every song counts.</summary>
    private HashSet<Guid>? _playSourceTrackIds;

    private Action<PlaySource, Track>? _openPlaySourceAction;

    /// <summary>Injected by MainWindowViewModel: opens the source's page and shows the song there.</summary>
    public void SetOpenPlaySourceAction(Action<PlaySource, Track> action) => _openPlaySourceAction = action;

    /// <summary>Names <paramref name="source"/> as where the next queue start
    /// (ReplaceQueueAndPlay, PlayAll, PlayFromRow, PlayShuffled) comes from, and returns this
    /// player so a page can write <c>_player.WithSource(…).PlayAll(tracks)</c>.</summary>
    public PlayerViewModel WithSource(PlaySource? source)
    {
        _pendingPlaySource = source;
        return this;
    }

    /// <summary>Takes the pending source for a queue being started from <paramref name="tracks"/>.</summary>
    private void ApplyPendingPlaySource(IList<Track> tracks)
    {
        var source = _pendingPlaySource;
        _pendingPlaySource = null;
        _playSourceTrackIds = source == null ? null : tracks.Select(t => t.Id).ToHashSet();
        if (Equals(PlaySource, source)) RaisePlaySourceState(); // same page again: the song set changed
        else PlaySource = source;
    }

    /// <summary>A queue start that never happened (nothing playable) must not leave its source
    /// pending for the next, unrelated one.</summary>
    private void DropPendingPlaySource() => _pendingPlaySource = null;

    private void ClearPlaySource()
    {
        _pendingPlaySource = null;
        _playSourceTrackIds = null;
        PlaySource = null;
    }

    /// <summary>The playing song belongs to the list the source started.</summary>
    public bool IsCurrentFromPlaySource =>
        PlaySource != null && CurrentTrack is { } t && (_playSourceTrackIds == null || _playSourceTrackIds.Contains(t.Id));

    /// <summary>The "Playing from …" link is shown.</summary>
    public bool HasPlaySource => ShowPlaySource && IsCurrentFromPlaySource;

    /// <summary>The source's name in the current language: the album, playlist, artist, genre or
    /// folder name, or the sidebar's name for a library section.</summary>
    public string? PlaySourceName => PlaySource is not { } s ? null
        : !string.IsNullOrWhiteSpace(s.Name) ? s.Name
        : Loc.T(s.Kind switch
        {
            PlaySourceKind.Songs => "Nav.Songs",
            PlaySourceKind.Albums => "Nav.Albums",
            PlaySourceKind.Favorites => "Nav.Favorites",
            PlaySourceKind.Inbox => "Nav.Inbox",
            PlaySourceKind.AudioCd => "Nav.AudioCd",
            PlaySourceKind.Server => "Nav.Server",
            PlaySourceKind.Folder => "Nav.Folders",
            PlaySourceKind.Artist => "Nav.Artists",
            PlaySourceKind.Genre => "Nav.Genres",
            PlaySourceKind.Playlist => "Nav.Playlists",
            _ => "Nav.Home",
        });

    /// <summary>"Playing from Abbey Road".</summary>
    public string PlayingFromText => PlaySourceName is { } name ? Loc.T("PlaybackBar.PlayingFrom", name) : string.Empty;

    /// <summary>The drawer link's accessible name: its text, or (hidden, no source) what it is
    /// for, so screen readers never meet a nameless button.</summary>
    public string PlayingFromAccessibleName => PlaySourceName is null ? Loc.T("Settings.ShowPlaySource") : PlayingFromText;

    /// <summary>The link's name before a player is bound (the binding's fallback).</summary>
    public static string PlayingFromFallbackName => Loc.T("Settings.ShowPlaySource");

    /// <summary>The island title's tooltip: where a click goes.</summary>
    public string TitleClickTip => TitleOpensPlaySource && IsCurrentFromPlaySource
        ? PlayingFromText
        : Loc.T("PlaybackBar.ViewAlbumTip");

    /// <summary>The UI language changed: section names re-read.</summary>
    public void RefreshPlaySourceText()
    {
        OnPropertyChanged(nameof(PlaySourceName));
        OnPropertyChanged(nameof(PlayingFromText));
        OnPropertyChanged(nameof(PlayingFromAccessibleName));
        OnPropertyChanged(nameof(TitleClickTip));
    }

    private void RaisePlaySourceState()
    {
        OnPropertyChanged(nameof(IsCurrentFromPlaySource));
        OnPropertyChanged(nameof(HasPlaySource));
        OnPropertyChanged(nameof(TitleClickTip));
        OpenPlaySourceCommand.NotifyCanExecuteChanged();
        OpenTitleTargetCommand.NotifyCanExecuteChanged();
    }

    partial void OnPlaySourceChanged(PlaySource? value) => RaisePlaySourceState();

    partial void OnTitleOpensPlaySourceChanged(bool value) => OpenTitleTargetCommand.NotifyCanExecuteChanged();

    private bool CanOpenPlaySource() => IsCurrentFromPlaySource;

    /// <summary>The "Playing from …" link: opens the source's page at the playing song.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenPlaySource))]
    private void OpenPlaySource()
    {
        if (PlaySource is not { } source || CurrentTrack is not { } track) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _openPlaySourceAction?.Invoke(source, track),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private bool CanOpenTitleTarget() =>
        (TitleOpensPlaySource && IsCurrentFromPlaySource) || CanViewCurrentTrackAlbum();

    /// <summary>The island title's click: the song's album, or with Song Title Opens Where It
    /// Plays From on, the page it plays from (the album when it has none, or for a song added
    /// to the queue later).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTitleTarget))]
    private void OpenTitleTarget()
    {
        if (TitleOpensPlaySource && IsCurrentFromPlaySource) OpenPlaySource();
        else ViewCurrentTrackAlbum();
    }
}
