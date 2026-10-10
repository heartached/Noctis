using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Localization;

namespace Noctis.ViewModels;

/// <summary>
/// The "Added to Queue" / "Playing Next" confirmation pill above the playback island.
/// <see cref="Show"/> fills the text and holds it for <see cref="HoldDuration"/>; a second
/// add while it is up replaces the text and restarts the hold (one pill, never a stack).
/// The motion (slide/fade, the tick drawing in) lives in <c>QueueToastView</c>.
/// </summary>
public sealed partial class QueueToastViewModel : ObservableObject
{
    public static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(2);

    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private IDisposable? _hold;

    /// <summary>True while the pill should be on screen.</summary>
    [ObservableProperty] private bool _isShown;

    /// <summary>"Added to Queue" or "Playing Next".</summary>
    [ObservableProperty] private string _headline = string.Empty;

    /// <summary>The track title, the album / playlist name, or "3 songs".</summary>
    [ObservableProperty] private string _detail = string.Empty;

    /// <summary>Bumped on every <see cref="Show"/>, so the view replays the tick for a
    /// repeat add that lands while the pill is already up (IsShown stays true).</summary>
    [ObservableProperty] private int _showCount;

    /// <param name="schedule">Runs the action once after the delay; the result cancels it.
    /// Defaults to a dispatcher timer. Tests pass a manual clock.</param>
    public QueueToastViewModel(Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _schedule = schedule ?? ((delay, action) => DispatcherTimer.RunOnce(action, delay));
    }

    /// <summary>Subscribes to <paramref name="player"/>'s user queue adds.</summary>
    public void Attach(PlayerViewModel player) => player.TracksQueued += (_, e) => Show(e);

    public void Show(QueueAddedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(e));
            return;
        }

        var (headline, detail) = Describe(e);
        Headline = headline;
        Detail = detail;
        _hold?.Dispose();
        _hold = _schedule(HoldDuration, Hide);
        ShowCount++;
        IsShown = true;
    }

    public void Hide()
    {
        _hold?.Dispose();
        _hold = null;
        IsShown = false;
    }

    /// <summary>Headline and detail line for one add: a single track names itself; a batch
    /// names its album / playlist / folder when the caller gave one, else its size.</summary>
    public static (string Headline, string Detail) Describe(QueueAddedEventArgs e)
    {
        var headline = Loc.T(e.PlayNext ? "QueueToast.PlayingNext" : "QueueToast.AddedToQueue");
        string detail;
        if (e.Count == 1)
            detail = string.IsNullOrWhiteSpace(e.FirstTrack.Title) ? Loc.T("QueueToast.OneSong") : e.FirstTrack.Title;
        else if (!string.IsNullOrWhiteSpace(e.SourceName))
            detail = e.SourceName!;
        else
            detail = Loc.T("QueueToast.SongCount", e.Count);
        return (headline, detail);
    }
}
