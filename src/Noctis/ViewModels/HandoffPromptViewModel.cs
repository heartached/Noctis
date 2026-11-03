using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// "Continue from your phone: Grenade" (Pick Up on Computer): a small pill above the island, offered
/// when the window opens or comes to the front and the phone's queue is newer than anything played
/// here. Continue loads that queue at its place, paused (it never starts playing); the pill goes
/// by itself after <see cref="HoldDuration"/>, and what was answered, taken or ignored is not
/// offered again. It never blocks: the rest of the window stays live.
/// </summary>
public sealed partial class HandoffPromptViewModel : ObservableObject
{
    public static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(15);

    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private IDisposable? _hold;
    private PhoneHandoffInbox? _inbox;
    private Func<PhoneHandoff, bool>? _load;
    private Func<Guid, string?>? _titleOf;
    private Func<bool>? _isPlayingHere;
    private Func<DateTime> _utcNow = () => DateTime.UtcNow;
    private PhoneHandoff? _offered;

    [ObservableProperty] private bool _isShown;

    /// <summary>"Continue from your phone".</summary>
    [ObservableProperty] private string _headline = string.Empty;

    /// <summary>The song the phone was playing.</summary>
    [ObservableProperty] private string _detail = string.Empty;

    /// <param name="schedule">Runs the action once after the delay; the result cancels it. Defaults
    /// to a dispatcher timer; tests pass a manual clock.</param>
    public HandoffPromptViewModel(Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _schedule = schedule ?? ((delay, action) => DispatcherTimer.RunOnce(action, delay));
    }

    /// <summary>Wires the prompt to the inbox and the player. Anything null (a test, a host without
    /// the inbox) leaves it never showing.</summary>
    public void Attach(PhoneHandoffInbox inbox, Func<PhoneHandoff, bool> load, Func<Guid, string?> titleOf,
        Func<bool> isPlayingHere, Func<DateTime>? utcNow = null)
    {
        _inbox = inbox;
        _load = load;
        _titleOf = titleOf;
        _isPlayingHere = isPlayingHere;
        if (utcNow != null) _utcNow = utcNow;
    }

    /// <summary>The player here starts, pauses or stops: that is when playback here last happened.</summary>
    public void NotePlaybackHere() => _inbox?.NoteDesktopPlayback(_utcNow());

    /// <summary>The window opened or came to the front (or the library finished loading): offers the
    /// phone's queue when it is newer than the last playback here and its song is in the library.</summary>
    public void Check()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Check);
            return;
        }
        if (_inbox == null || IsShown) return;
        // Playing here now: nothing the phone has can be newer.
        var last = _isPlayingHere!() ? _utcNow() : _inbox.DesktopPlaybackUtc;
        if (_inbox.Pending(last) is not { } handoff || _titleOf!(handoff.CurrentId) is not { } title) return;
        _offered = handoff;
        Headline = Loc.T("Handoff.Headline");
        Detail = title;
        _hold?.Dispose();
        _hold = _schedule(HoldDuration, Dismiss);
        IsShown = true;
    }

    [RelayCommand]
    private void Continue()
    {
        var handoff = _offered;
        Dismiss();
        if (handoff != null) _load!(handoff);
    }

    [RelayCommand]
    private void Dismiss()
    {
        _hold?.Dispose();
        _hold = null;
        if (_offered != null) _inbox?.MarkHandled(_offered);
        _offered = null;
        IsShown = false;
    }
}
