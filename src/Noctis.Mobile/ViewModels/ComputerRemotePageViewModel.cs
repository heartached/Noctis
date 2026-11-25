using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Services;
using Noctis.Mobile.Services.Account;

namespace Noctis.Mobile.ViewModels;

/// <summary>Where the remote stands with the computer.</summary>
/// <remarks><see cref="ServerError"/>: the computer answered, but with an HTTP error or a reply the
/// phone could not read (not "can't reach": it was reached).</remarks>
public enum RemoteStatus { Connecting, Ready, Unreachable, NotAllowed, Outdated, SignedOut, CertificateChanged, ServerError }

/// <summary>One of the computer's Up Next songs; a tap plays it there.</summary>
public sealed record ComputerQueueRow(int Index, Guid Id, string Title, string Artist);

/// <summary>
/// Settings › Account › Remote (Discord #mobile-beta 11-17): the computer's own player, driven from
/// the phone like the desktop's web remote. Shows the computer's song, cover, place and volume,
/// Shuffle and Repeat and its Up Next; every press goes to the computer
/// (<see cref="INoctisAccountService.ControlComputerAsync"/>) and the phone's own player is never
/// touched.
/// <para>Only while it is seen: the page asks the computer once a second while it is the top
/// page, Now Playing is not over it and the app is in front (<see cref="ShellViewModel.IsSuspended"/>);
/// any of those ending stops the loop, and <see cref="OnClosed"/> stops it for good. No timer: one
/// awaited delay between asks. Up Next is fetched only when it can have changed (a new song, a
/// skip, a jump, Shuffle) and every <see cref="QueueRefreshPolls"/> asks, not on every one.</para>
/// </summary>
public sealed partial class ComputerRemotePageViewModel : MobilePage
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>After a failed ask: the computer is asked again this much later (while seen).</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(4);

    /// <summary>Up Next is re-read every this many asks even when nothing says it changed (songs
    /// queued on the computer itself).</summary>
    internal const int QueueRefreshPolls = 10;

    private readonly INoctisAccountService _account;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _loopCts;
    private bool _closed;
    private bool _queueStale = true;
    private int _pollsSinceQueue;
    private Guid? _trackId;
    private Guid? _coverAlbumId;
    private int _coverAsk;
    // Answers are applied in the order their asks were sent: a poll that left before a press and
    // lands after it would put the old state back for a second.
    private long _sent;
    private long _applied;

    /// <param name="delay">Tests pass a delay they control; the app waits for real.</param>
    public ComputerRemotePageViewModel(ShellViewModel shell, INoctisAccountService account,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Shell = shell;
        _account = account;
        _delay = delay ?? Task.Delay;
        ComputerName = account.Account is { } a ? AccountPageViewModel.ServerLabel(a) : string.Empty;
        Shell.PropertyChanged += OnShellChanged;
        UpdateActivity();
    }

    public ShellViewModel Shell { get; }
    public override string Title => Loc.T("Mobile.Remote.Title");

    public string ComputerName { get; }

    /// <summary>The ask loop is running (tests).</summary>
    internal bool IsRunning => _loopCts != null;

    /// <summary>The running loop; tests await it after closing.</summary>
    internal Task Loop { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(ShowsMessage), nameof(IsConnecting), nameof(MessageTitle), nameof(MessageText),
        nameof(CanRetry), nameof(ShowsPlayer), nameof(ShowsNothingPlaying))]
    private RemoteStatus _status = RemoteStatus.Connecting;

    public bool IsReady => Status == RemoteStatus.Ready;
    public bool IsConnecting => Status == RemoteStatus.Connecting;

    /// <summary>The hero in place of the player: connecting, or why it cannot be reached.</summary>
    public bool ShowsMessage => Status != RemoteStatus.Ready;

    public bool CanRetry => Status is RemoteStatus.Unreachable or RemoteStatus.NotAllowed or RemoteStatus.Outdated or RemoteStatus.CertificateChanged
        or RemoteStatus.ServerError;

    public string MessageTitle => Status switch
    {
        RemoteStatus.Connecting => Loc.T("Mobile.Remote.Connecting", ComputerName),
        RemoteStatus.SignedOut => Loc.T("Mobile.Remote.Title"),
        RemoteStatus.ServerError => Loc.T("Mobile.Remote.ServerErrorTitle", ComputerName),
        _ => Loc.T("Mobile.Remote.CantReach", ComputerName),
    };

    public string MessageText => Status switch
    {
        RemoteStatus.Unreachable => Loc.T("Mobile.Account.Error.Unreachable"),
        RemoteStatus.NotAllowed => Loc.T("Mobile.Remote.NotAllowed", ComputerName),
        RemoteStatus.Outdated => Loc.T("Mobile.Remote.Outdated", ComputerName),
        RemoteStatus.CertificateChanged => Loc.T("Mobile.Account.Error.CertificateChanged"),
        RemoteStatus.SignedOut => Loc.T("Mobile.Account.Error.SignedOut"),
        RemoteStatus.ServerError => Loc.T("Mobile.Remote.ServerError", ComputerName),
        _ => string.Empty,
    };

    public bool ShowsPlayer => IsReady && HasTrack;
    public bool ShowsNothingPlaying => IsReady && !HasTrack;
    public string NothingPlayingText => Loc.T("Mobile.Remote.NothingPlaying", ComputerName);
    public string UpNextTitle => Loc.T("Mobile.Remote.UpNext", ComputerName);

    // ── The computer's song ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPlayer), nameof(ShowsNothingPlaying))]
    private bool _hasTrack;

    [ObservableProperty] private string _songTitle = string.Empty;
    [ObservableProperty] private string _artist = string.Empty;
    [ObservableProperty] private string? _artworkPath;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isShuffleOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeatOn), nameof(IsRepeatOne))]
    private ComputerRepeat _repeat;

    public bool IsRepeatOn => Repeat != ComputerRepeat.Off;
    public bool IsRepeatOne => Repeat == ComputerRepeat.One;

    /// <summary>Seconds, for the seek bar (its Maximum).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingText))]
    private double _durationSeconds;

    /// <summary>Seconds, for the seek bar (its Value). Left alone while the bar is dragged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText), nameof(RemainingText))]
    private double _positionSeconds;

    public string ElapsedText => NowPlayingViewModel.FormatTime(TimeSpan.FromSeconds(PositionSeconds));
    public string RemainingText => "-" + NowPlayingViewModel.FormatTime(TimeSpan.FromSeconds(Math.Max(0, DurationSeconds - PositionSeconds)));

    /// <summary>0..1 for the volume bar (the computer's 0-100).</summary>
    [ObservableProperty] private double _volumeLevel;

    /// <summary>The seek bar is under a finger: answers do not move it.</summary>
    public bool IsSeeking { get; set; }

    /// <summary>The volume bar is under a finger: answers do not move it.</summary>
    public bool IsChangingVolume { get; set; }

    // ── Up Next ──

    public ObservableCollection<ComputerQueueRow> UpNext { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpNext), nameof(UpNextMoreText), nameof(HasUpNextMore))]
    private int _upNextTotal;

    public bool HasUpNext => UpNextTotal > 0;
    public bool HasUpNextMore => UpNextTotal > UpNext.Count;
    public string UpNextMoreText => Loc.T("Mobile.Remote.AndMore", UpNextTotal - UpNext.Count);

    // ── Seen or not ──

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.CurrentPage) or nameof(ShellViewModel.IsSuspended)
            or nameof(ShellViewModel.IsNowPlayingOpen) or nameof(ShellViewModel.IsLyricsOpen) or nameof(ShellViewModel.IsQueueOpen))
            UpdateActivity();
    }

    /// <summary>The page is on screen: the top page, nothing of the player over it, the app in front.</summary>
    private bool IsSeen => !_closed && ReferenceEquals(Shell.CurrentPage, this) && !Shell.IsSuspended && !Shell.IsPlayerUp;

    private void UpdateActivity()
    {
        if (IsSeen) Start();
        else Stop();
    }

    private void Start()
    {
        if (_loopCts != null) return;
        if (!_account.IsSignedIn)
        {
            Status = RemoteStatus.SignedOut;
            return;
        }
        _loopCts = new CancellationTokenSource();
        // Back on screen: Up Next may have changed while away, and the first answer is applied as
        // a new song, so a cover that failed or was cut off by leaving is asked for again (one
        // that shows is kept: LoadCover skips the album it has).
        _queueStale = true;
        _trackId = null;
        Loop = RunAsync(_loopCts.Token);
    }

    private void Stop()
    {
        var cts = _loopCts;
        if (cts == null) return;
        _loopCts = null;
        cts.Cancel();
        cts.Dispose();
    }

    public override void OnClosed()
    {
        _closed = true;
        Shell.PropertyChanged -= OnShellChanged;
        Stop();
    }

    // ── The loop ──

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                if (++_pollsSinceQueue >= QueueRefreshPolls) _queueStale = true;
                var withQueue = _queueStale;
                var ticket = ++_sent;
                var state = await _account.GetComputerPlaybackAsync(withQueue, ct);
                if (ct.IsCancellationRequested) return;
                Apply(state, withQueue, ticket);
                wait = PollInterval;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                var status = Fail(ex);
                // Nothing a wait fixes: the page stops asking until Try Again.
                if (status is RemoteStatus.NotAllowed or RemoteStatus.Outdated or RemoteStatus.SignedOut or RemoteStatus.CertificateChanged)
                {
                    StopIfCurrent(ct);
                    return;
                }
                wait = RetryInterval;
            }
            try
            {
                await _delay(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void StopIfCurrent(CancellationToken ct)
    {
        if (_loopCts is { } cts && cts.Token == ct) Stop();
    }

    /// <summary>A failed ask or press: the status that says why. Kinds that mean "try again in a
    /// moment" (a refused jump, a busy computer) leave a showing player as it is.</summary>
    private RemoteStatus Fail(Exception ex)
    {
        // Anything but the service's own errors is a reply it could not make sense of (the
        // service turns every failure to connect into Unreachable): not "can't reach".
        var kind = ex is NoctisServerException n ? n.Kind : NoctisErrorKind.Server;
        if (ex is not NoctisServerException) DebugLog.Write("Remote", $"Ask failed: {ex.GetType().Name}");
        Status = kind switch
        {
            NoctisErrorKind.RemoteNotAllowed => RemoteStatus.NotAllowed,
            NoctisErrorKind.Outdated => RemoteStatus.Outdated,
            NoctisErrorKind.SignedOut => RemoteStatus.SignedOut,
            // Every address of the computer was tried (the service moves to one that answers as
            // the pinned computer): only a new sign-in trusts a new security code.
            NoctisErrorKind.CertificateChanged => RemoteStatus.CertificateChanged,
            NoctisErrorKind.Server when Status == RemoteStatus.Ready => RemoteStatus.Ready,
            // It answered: an HTTP error or a reply that could not be read.
            NoctisErrorKind.Server => RemoteStatus.ServerError,
            _ => RemoteStatus.Unreachable,
        };
        return Status;
    }

    internal void Apply(ComputerPlayback state, bool withQueue, long ticket)
    {
        if (ticket < _applied) return;   // an older ask landing late
        _applied = ticket;
        Status = RemoteStatus.Ready;

        var track = state.Track;
        HasTrack = track != null;
        if (track?.Id != _trackId)
        {
            _trackId = track?.Id;
            SongTitle = track?.Title ?? string.Empty;
            Artist = track?.Artist ?? string.Empty;
            // A new song: the computer's Up Next moved on with it.
            if (!withQueue) _queueStale = true;
            LoadCover(track?.AlbumId);
        }
        IsPlaying = state.Playing;
        IsShuffleOn = state.Shuffle;
        Repeat = state.Repeat;
        DurationSeconds = (track != null && state.DurationMs > 0 ? state.DurationMs : track?.DurationMs ?? 0) / 1000.0;
        if (!IsSeeking) PositionSeconds = Math.Min(state.PositionMs / 1000.0, Math.Max(DurationSeconds, 0));
        if (!IsChangingVolume) VolumeLevel = state.Volume / 100.0;

        if (withQueue && state.UpNext is { } upNext)
        {
            _queueStale = false;
            _pollsSinceQueue = 0;
            ReplaceUpNext(upNext);
            UpNextTotal = Math.Max(state.UpNextTotal, upNext.Count);
            OnPropertyChanged(nameof(HasUpNextMore));
            OnPropertyChanged(nameof(UpNextMoreText));
        }
    }

    /// <summary>Rows are rebuilt only where they differ: an unchanged Up Next costs nothing.</summary>
    private void ReplaceUpNext(IReadOnlyList<ComputerTrack> upNext)
    {
        for (var i = 0; i < upNext.Count; i++)
        {
            var t = upNext[i];
            if (i < UpNext.Count && UpNext[i].Id == t.Id && UpNext[i].Title == t.Title && UpNext[i].Artist == t.Artist) continue;
            var row = new ComputerQueueRow(i, t.Id, t.Title, t.Artist);
            if (i < UpNext.Count) UpNext[i] = row;
            else UpNext.Add(row);
        }
        while (UpNext.Count > upNext.Count) UpNext.RemoveAt(UpNext.Count - 1);
    }

    private async void LoadCover(Guid? albumId)
    {
        if (albumId == _coverAlbumId) return;
        _coverAlbumId = albumId;
        // By ask, not album: a fetch cut off by leaving the page lands after the page came back
        // and asked for the same album again, and must not clear that ask.
        var ask = ++_coverAsk;
        if (albumId is not { } id)
        {
            ArtworkPath = null;
            return;
        }
        try
        {
            var path = await _account.GetComputerCoverAsync(id, _loopCts?.Token ?? CancellationToken.None);
            if (ask == _coverAsk) ArtworkPath = path;
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) DebugLog.Write("Remote", $"Cover failed: {ex.GetType().Name}");
            // Asked again with the next new song, or when the page comes back.
            if (ask == _coverAsk)
            {
                ArtworkPath = null;
                _coverAlbumId = null;
            }
        }
    }

    // ── Presses ──

    private async Task SendAsync(ComputerCommand command, bool changesQueue)
    {
        if (Status != RemoteStatus.Ready || _loopCts is not { } cts) return;
        var ct = cts.Token;
        if (changesQueue) _queueStale = true;
        try
        {
            var withQueue = _queueStale;
            var ticket = ++_sent;
            var state = await _account.ControlComputerAsync(command, withQueue, ct);
            if (!ct.IsCancellationRequested) Apply(state, withQueue, ticket);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            // A jump whose row moved on, say: the next ask brings Up Next as it is now.
            _queueStale = true;
            if (Fail(ex) is RemoteStatus.NotAllowed or RemoteStatus.Outdated or RemoteStatus.SignedOut or RemoteStatus.CertificateChanged) StopIfCurrent(ct);
        }
    }

    [RelayCommand]
    private Task TogglePlayPause()
    {
        if (IsReady && HasTrack) IsPlaying = !IsPlaying;   // the glyph answers at once; the computer's answer settles it
        return SendAsync(ComputerCommand.Toggle, changesQueue: false);
    }

    [RelayCommand] private Task Next() => SendAsync(ComputerCommand.Next, changesQueue: true);
    [RelayCommand] private Task Previous() => SendAsync(ComputerCommand.Previous, changesQueue: true);

    [RelayCommand]
    private Task ToggleShuffle()
    {
        if (IsReady) IsShuffleOn = !IsShuffleOn;
        return SendAsync(ComputerCommand.ToggleShuffle, changesQueue: true);
    }

    [RelayCommand] private Task CycleRepeat() => SendAsync(ComputerCommand.CycleRepeat, changesQueue: false);

    [RelayCommand]
    private Task PlayUpNext(ComputerQueueRow? row) =>
        row == null ? Task.CompletedTask : SendAsync(ComputerCommand.Jump(row.Index, row.Id), changesQueue: true);

    /// <summary>The seek bar let go at <paramref name="seconds"/>.</summary>
    public Task SeekAsync(double seconds)
    {
        IsSeeking = false;
        PositionSeconds = Math.Clamp(seconds, 0, Math.Max(0, DurationSeconds));
        return SendAsync(ComputerCommand.Seek((long)(PositionSeconds * 1000)), changesQueue: false);
    }

    /// <summary>The volume bar let go (or tapped) at <paramref name="level"/> (0..1).</summary>
    public Task SetVolumeAsync(double level)
    {
        IsChangingVolume = false;
        VolumeLevel = Math.Clamp(level, 0, 1);
        return SendAsync(ComputerCommand.Volume((int)Math.Round(VolumeLevel * 100)), changesQueue: false);
    }

    [RelayCommand]
    private void Retry()
    {
        Stop();
        Status = RemoteStatus.Connecting;
        UpdateActivity();
    }
}
