using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.Services.Scrobbling;

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
    public string Summary { get; private set; } = "Off";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsThroughComputer), nameof(RouteFootnote), nameof(LastFmSubtitle), nameof(ListenBrainzSubtitle),
        nameof(LastFmStatus), nameof(ListenBrainzStatus), nameof(LastFmConnected), nameof(ListenBrainzConnected), nameof(PhoneControlsEnabled))]
    private bool _isFromPhone;

    [ObservableProperty] private bool _sendPlays = true;
    [ObservableProperty] private bool _showNowPlaying = true;
    [ObservableProperty] private string _waitingText = "0 plays";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatusMessage))] private string _statusMessage = string.Empty;

    public bool IsThroughComputer => !IsFromPhone;
    public bool HasStatusMessage => StatusMessage.Length > 0;

    /// <summary>The Send Plays / Show Now Playing switches act on the phone's own sending only: they
    /// show in From This Phone mode alone (Through Computer, the phone sends nothing for them to stop).</summary>
    public bool PhoneControlsEnabled => IsFromPhone;

    /// <summary>The computer's services as its last sync said; null when it has not said.</summary>
    private ComputerScrobbling? Computer => _shell.Account is { IsSignedIn: true } account ? account.ComputerScrobbling : null;

    public string RouteFootnote => IsFromPhone
        ? "The phone counts your plays and sends them to Last.fm and ListenBrainz itself. Nothing is sent twice."
        : _shell.Account is not { IsSignedIn: true }
            ? "Sign in to your computer in Account, and plays go to it when the phone syncs."
        : !_shell.ShareHistoryWithComputer
            ? "Share with Computer is off in Listening History, so plays stay on this phone."
        : Computer is not { } c
            ? "Plays go to your computer when the phone syncs. An up-to-date Noctis there sends them on to Last.fm and ListenBrainz."
        : !c.LastFm && !c.ListenBrainz
            ? "Plays go to your computer when the phone syncs. Connect Last.fm or ListenBrainz there to send them on."
            : "Plays go to your computer when the phone syncs, and it sends the ones that count on. Songs only on this phone stay here. Nothing is sent twice.";

    public bool LastFmConnected => IsFromPhone ? _scrobbler.LastFmConnected : Computer?.LastFm == true;
    public bool ListenBrainzConnected => IsFromPhone ? _scrobbler.ListenBrainzConnected : Computer?.ListenBrainz == true;

    public string LastFmStatus => Status(LastFmConnected);
    public string ListenBrainzStatus => Status(ListenBrainzConnected);

    private string Status(bool connected) => IsFromPhone || Computer != null
        ? (connected ? "Connected" : "Not Connected")
        : "On Computer";

    public string LastFmSubtitle => !IsFromPhone ? ComputerSubtitle(LastFmConnected)
        : !_scrobbler.LastFmConnected ? "Share what you listen to"
        : _lastFmPlays is { } n ? $"{_scrobbler.LastFmUser} · {n.ToString("N0", CultureInfo.CurrentCulture)} scrobbles"
        : _scrobbler.LastFmUser;

    public string ListenBrainzSubtitle => !IsFromPhone ? ComputerSubtitle(ListenBrainzConnected)
        : _scrobbler.ListenBrainzConnected ? _scrobbler.ListenBrainzUser
        : "Open, free listening stats";

    private static string ComputerSubtitle(bool connected) => connected ? "On your computer" : "Set up on your computer";

    public string Footnote => "A play counts after half the song or 4 minutes. Plays made offline wait here until the next sync.";

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

    /// <summary>Re-reads everything from the scrobbler and the account (their events land here).</summary>
    private void Refresh()
    {
        _syncing = true;
        try
        {
            IsFromPhone = _scrobbler.FromPhone;
            SendPlays = _scrobbler.SendPlays;
            ShowNowPlaying = _scrobbler.ShowNowPlaying;
        }
        finally
        {
            _syncing = false;
        }
        var waiting = _scrobbler.PendingCount + (_shell.Account?.PendingPlayCount ?? 0);
        WaitingText = waiting == 1 ? "1 play" : $"{waiting} plays";
        var parts = new List<string>(2);
        if (LastFmConnected) parts.Add("Last.fm");
        if (ListenBrainzConnected) parts.Add("ListenBrainz");
        // Through Computer, a signed-in computer that has not said where it sends them: "Computer".
        var unknown = !_scrobbler.FromPhone && Computer == null && _shell.Account is { IsSignedIn: true } && _shell.ShareHistoryWithComputer;
        var summary = parts.Count > 0 ? string.Join(" + ", parts) : unknown ? "Computer" : "Off";
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
        if (url == null) { StatusMessage = "Couldn't reach Last.fm. Try again."; return; }
        OpenUrlRequested?.Invoke(url);
        _shell.ShowPrompt(new PromptViewModel(_shell, "Approve Noctis on Last.fm",
            "Sign in on Last.fm, tap Allow, then come back here and tap Done.",
            new[]
            {
                new PromptAction("Done", async _ =>
                {
                    if (!await _scrobbler.CompleteLastFmAuthAsync())
                        StatusMessage = "Last.fm hasn't heard from you yet. Approve Noctis in the browser, then try again.";
                }),
            }));
    }

    [RelayCommand]
    private void ListenBrainzTap()
    {
        StatusMessage = string.Empty;
        if (!IsFromPhone) { ShowComputerSetup(); return; }
        if (_scrobbler.ListenBrainzConnected) { AskDisconnect("ListenBrainz", lastFm: false); return; }
        _shell.ShowPrompt(new PromptViewModel(_shell, "Connect ListenBrainz",
            "Paste your user token from listenbrainz.org/profile.",
            new[]
            {
                new PromptAction("Connect", async prompt =>
                {
                    var failure = await _scrobbler.ConnectListenBrainzAsync(prompt.Entered);
                    StatusMessage = failure switch
                    {
                        null => string.Empty,
                        "unreachable" => "Couldn't reach ListenBrainz. Try again.",
                        _ => "ListenBrainz didn't accept that token.",
                    };
                }),
            }) { HasField = true, FieldPlaceholder = "User token" });
    }

    private void ShowComputerSetup() => _shell.ShowPrompt(new PromptViewModel(_shell, "Set up on your computer",
        "Connect Last.fm and ListenBrainz in Noctis on your computer. Or choose From This Phone to sign in here instead.",
        new[] { new PromptAction("OK", () => { }) }));

    private void AskDisconnect(string name, bool lastFm) => _shell.ShowPrompt(new PromptViewModel(_shell, $"Disconnect {name}?",
        "Plays waiting for it are dropped. Your scrobbles on the site stay.",
        new[] { new PromptAction("Disconnect", () => _ = _scrobbler.DisconnectAsync(lastFm), isDestructive: true) }));
}
