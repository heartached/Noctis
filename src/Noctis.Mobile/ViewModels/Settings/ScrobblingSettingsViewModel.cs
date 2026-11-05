using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.Services.Scrobbling;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Settings → Scrobbling. Two routes: Through Computer (the phone's plays reach the computer on the
/// next sync, and the computer sends the ones that counted on to its own Last.fm / ListenBrainz) and
/// From This Phone (the phone counts and sends them itself, <see cref="PhoneScrobbler"/>). A play goes
/// one way only, chosen when it counts, so switching the route never sends a queued play twice or
/// loses one. In Through Computer mode the rows show the computer's services as its last sync told
/// (<see cref="INoctisAccountService.ComputerScrobbling"/>); a computer that does not say (an older
/// one, or before the first sync) gets "On Computer", not a guess.
/// </summary>
public sealed partial class ScrobblingSettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly PhoneScrobbler _scrobbler;
    private bool _syncing;
    private long? _lastFmPlays;
    private string? _lastFmPlaysFor;

    public ScrobblingSettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _scrobbler = shell.Scrobbler;
        _onChanged = (_, _) => shell.Marshal(Refresh);
        _scrobbler.Changed += _onChanged;
        if (shell.Account != null) shell.Account.StateChanged += _onChanged;
        shell.HistoryPrefsChanged += _onChanged;   // Share with Computer
        Refresh();
    }

    private readonly EventHandler _onChanged;

    /// <summary>The sheet closed: stop following the scrobbler, the account and the shell, which
    /// all outlive it (each Settings open makes a new pane).</summary>
    internal void Detach()
    {
        _scrobbler.Changed -= _onChanged;
        if (_shell.Account != null) _shell.Account.StateChanged -= _onChanged;
        _shell.HistoryPrefsChanged -= _onChanged;
    }

    /// <summary>Raised with the Last.fm approval page; the view opens it in the browser.</summary>
    public event Action<string>? OpenUrlRequested;

    /// <summary>"Last.fm", "ListenBrainz", "Last.fm + ListenBrainz" or "Off": where the plays end up,
    /// from the phone or through the computer; "Computer" when the computer has not said.</summary>
    public string Summary { get; private set; } = Loc.T("Mobile.Off");

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsThroughComputer), nameof(RouteFootnote), nameof(LastFmSubtitle), nameof(ListenBrainzSubtitle),
        nameof(LastFmStatus), nameof(ListenBrainzStatus), nameof(LastFmConnected), nameof(ListenBrainzConnected), nameof(PhoneControlsEnabled))]
    private bool _isFromPhone;

    [ObservableProperty] private bool _sendPlays = true;
    [ObservableProperty] private bool _showNowPlaying = true;

    /// <summary>Mixes and Recommendations (GitHub #177): Home's From Your Last.fm and Discover on
    /// Last.fm, read from the account this phone connected.</summary>
    [ObservableProperty] private bool _lastFmTaste = true;
    [ObservableProperty] private string _waitingText = Loc.Plural("Mobile.Count.Plays", 0);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatusMessage))] private string _statusMessage = string.Empty;

    public bool IsThroughComputer => !IsFromPhone;

    /// <summary>False with no account service: the Through Computer / From This Phone card hides and
    /// the phone sends its plays itself.</summary>
    public bool HasComputerRoute => _scrobbler.HasComputerRoute;
    public bool HasStatusMessage => StatusMessage.Length > 0;

    /// <summary>The Send Plays / Show Now Playing switches act on the phone's own sending only: they
    /// show in From This Phone mode alone (Through Computer, the phone sends nothing for them to stop).</summary>
    public bool PhoneControlsEnabled => IsFromPhone;

    /// <summary>The Mixes and Recommendations switch shows while this phone has Last.fm connected:
    /// Home reads that account's charts, whichever way the plays are sent.</summary>
    public bool ShowTasteSwitch => _scrobbler.LastFmConnected;

    /// <summary>The computer's services as its last sync said; null when it has not said.</summary>
    private ComputerScrobbling? Computer => _shell.Account is { IsSignedIn: true } account ? account.ComputerScrobbling : null;

    public string RouteFootnote => IsFromPhone
        ? Loc.T("Mobile.Scrobbling.RouteFromPhone")
        : _shell.Account is not { IsSignedIn: true }
            ? Loc.T("Mobile.Scrobbling.RouteSignIn")
        : !_shell.ShareHistoryWithComputer
            ? Loc.T("Mobile.Scrobbling.RouteNotShared")
        : Computer is not { } c
            ? Loc.T("Mobile.Scrobbling.RouteUnknown")
        : !c.LastFm && !c.ListenBrainz
            ? Loc.T("Mobile.Scrobbling.RouteNoServices")
            : Loc.T("Mobile.Scrobbling.RouteComputer");

    public bool LastFmConnected => IsFromPhone ? _scrobbler.LastFmConnected : Computer?.LastFm == true;
    public bool ListenBrainzConnected => IsFromPhone ? _scrobbler.ListenBrainzConnected : Computer?.ListenBrainz == true;

    public string LastFmStatus => Status(LastFmConnected);
    public string ListenBrainzStatus => Status(ListenBrainzConnected);

    private string Status(bool connected) => IsFromPhone || Computer != null
        ? (connected ? Loc.T("Mobile.Scrobbling.Connected") : Loc.T("Mobile.Scrobbling.NotConnected"))
        : Loc.T("Mobile.Scrobbling.OnComputer");

    public string LastFmSubtitle => !IsFromPhone ? ComputerSubtitle(LastFmConnected)
        : !_scrobbler.LastFmConnected ? Loc.T("Mobile.Scrobbling.LastFmPitch")
        : _lastFmPlays is { } n ? $"{_scrobbler.LastFmUser} · {Loc.Plural("Mobile.Count.Scrobbles", n)}"
        : _scrobbler.LastFmUser;

    public string ListenBrainzSubtitle => !IsFromPhone ? ComputerSubtitle(ListenBrainzConnected)
        : _scrobbler.ListenBrainzConnected ? _scrobbler.ListenBrainzUser
        : Loc.T("Mobile.Scrobbling.ListenBrainzPitch");

    private static string ComputerSubtitle(bool connected) => connected ? Loc.T("Mobile.Scrobbling.OnYourComputer") : Loc.T("Mobile.Scrobbling.SetUpOnComputer");

    public string Footnote => HasComputerRoute
        ? Loc.T("Mobile.Scrobbling.FootnoteSync")
        : Loc.T("Mobile.Scrobbling.FootnoteOnline");

    partial void OnIsFromPhoneChanged(bool value)
    {
        if (!_syncing) _ = _scrobbler.SetFromPhoneAsync(value);
    }

    partial void OnSendPlaysChanged(bool value)
    {
        if (!_syncing) _ = _scrobbler.SetSendPlaysAsync(value);
    }

    partial void OnShowNowPlayingChanged(bool value)
    {
        if (!_syncing) _ = _scrobbler.SetShowNowPlayingAsync(value);
    }

    partial void OnLastFmTasteChanged(bool value)
    {
        if (!_syncing) _ = _scrobbler.SetLastFmTasteEnabledAsync(value);
    }

    /// <summary>The UI language changed: the summary and every line re-read.</summary>
    public void Relabel() => Refresh();

    /// <summary>Re-reads everything from the scrobbler and the account (their events land here).</summary>
    private void Refresh()
    {
        _syncing = true;
        try
        {
            IsFromPhone = _scrobbler.FromPhone;
            SendPlays = _scrobbler.SendPlays;
            ShowNowPlaying = _scrobbler.ShowNowPlaying;
            LastFmTaste = _scrobbler.LastFmTasteEnabled;
        }
        finally
        {
            _syncing = false;
        }
        var waiting = _scrobbler.PendingCount + (_shell.Account?.PendingPlayCount ?? 0);
        WaitingText = Loc.Plural("Mobile.Count.Plays", waiting);
        var parts = new List<string>(2);
        if (LastFmConnected) parts.Add("Last.fm");
        if (ListenBrainzConnected) parts.Add("ListenBrainz");
        // Through Computer, a signed-in computer that has not said where it sends them: "Computer".
        var unknown = !_scrobbler.FromPhone && Computer == null && _shell.Account is { IsSignedIn: true } && _shell.ShareHistoryWithComputer;
        var summary = parts.Count > 0 ? string.Join(" + ", parts) : unknown ? Loc.T("Mobile.Scrobbling.Computer") : Loc.T("Mobile.Off");
        if (summary != Summary)
        {
            Summary = summary;
            OnPropertyChanged(nameof(Summary));
        }
        OnPropertyChanged(string.Empty);
        if (_scrobbler.LastFmConnected && IsFromPhone && _lastFmPlaysFor != _scrobbler.LastFmUser) _ = LoadLastFmPlaysAsync();
    }

    private async Task LoadLastFmPlaysAsync()
    {
        var user = _scrobbler.LastFmUser;
        _lastFmPlaysFor = user;
        _lastFmPlays = await _scrobbler.LastFmPlayCountAsync().ConfigureAwait(false);
        _shell.Marshal(() => OnPropertyChanged(nameof(LastFmSubtitle)));
    }

    [RelayCommand]
    private void SelectThroughComputer() => IsFromPhone = false;

    [RelayCommand]
    private void SelectFromPhone() => IsFromPhone = true;

    [RelayCommand]
    private async Task LastFmTapAsync()
    {
        StatusMessage = string.Empty;
        if (!IsFromPhone) { ShowComputerSetup(); return; }
        if (_scrobbler.LastFmConnected) { AskDisconnect("Last.fm", lastFm: true); return; }
        var url = await _scrobbler.BeginLastFmAuthAsync();
        if (url == null) { StatusMessage = Loc.T("Mobile.Scrobbling.LastFmUnreachable"); return; }
        OpenUrlRequested?.Invoke(url);
        _shell.ShowPrompt(new PromptViewModel(_shell, Loc.T("Mobile.Scrobbling.ApproveTitle"),
            Loc.T("Mobile.Scrobbling.ApproveDetail"),
            new[]
            {
                new PromptAction(Loc.T("Common.Done"), async _ =>
                {
                    if (!await _scrobbler.CompleteLastFmAuthAsync())
                        StatusMessage = Loc.T("Mobile.Scrobbling.LastFmNotApproved");
                }),
            }));
    }

    [RelayCommand]
    private void ListenBrainzTap()
    {
        StatusMessage = string.Empty;
        if (!IsFromPhone) { ShowComputerSetup(); return; }
        if (_scrobbler.ListenBrainzConnected) { AskDisconnect("ListenBrainz", lastFm: false); return; }
        _shell.ShowPrompt(new PromptViewModel(_shell, Loc.T("Mobile.Scrobbling.ConnectListenBrainz"),
            Loc.T("Mobile.Scrobbling.TokenDetail"),
            new[]
            {
                new PromptAction(Loc.T("Mobile.Scrobbling.Connect"), async prompt =>
                {
                    var failure = await _scrobbler.ConnectListenBrainzAsync(prompt.Entered);
                    StatusMessage = failure switch
                    {
                        null => string.Empty,
                        "unreachable" => Loc.T("Mobile.Scrobbling.ListenBrainzUnreachable"),
                        _ => Loc.T("Mobile.Scrobbling.TokenRejected"),
                    };
                }),
            }) { HasField = true, FieldPlaceholder = Loc.T("Mobile.Scrobbling.UserToken") });
    }

    private void ShowComputerSetup() => _shell.ShowPrompt(new PromptViewModel(_shell, Loc.T("Mobile.Scrobbling.SetUpOnComputer"),
        Loc.T("Mobile.Scrobbling.SetUpDetail"),
        new[] { new PromptAction(Loc.T("Mobile.OK"), () => { }) }));

    private void AskDisconnect(string name, bool lastFm) => _shell.ShowPrompt(new PromptViewModel(_shell, Loc.T("Mobile.Scrobbling.DisconnectQuestion", name),
        Loc.T("Mobile.Scrobbling.DisconnectDetail"),
        new[] { new PromptAction(Loc.T("Mobile.Scrobbling.Disconnect"), () => _ = _scrobbler.DisconnectAsync(lastFm), isDestructive: true) }));
}
