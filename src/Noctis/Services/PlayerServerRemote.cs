using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services.Server;
using Noctis.ViewModels;

namespace Noctis.Services;

/// <summary>
/// The phone's remote (NoctisServer.Remote.cs) over this window's player: every read and every
/// command runs on the UI thread through the same commands a click uses, as the Local API's
/// /playback routes do (WebRemoteServer.LocalApi.cs), so their side effects (AutoMix cancel,
/// logging, the queue's played rows) match.
/// </summary>
public sealed class PlayerServerRemote : IServerRemote
{
    private readonly PlayerViewModel _player;
    private readonly Func<Action, Task> _marshal;

    public PlayerServerRemote(PlayerViewModel player, Func<Action, Task>? marshal = null)
    {
        _player = player;
        _marshal = marshal ?? DefaultMarshal;
    }

    private static Task DefaultMarshal(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    public async Task<RemoteSnapshot> SnapshotAsync(int upNextLimit)
    {
        RemoteSnapshot snapshot = null!;
        await _marshal(() => snapshot = Snapshot(upNextLimit));
        return snapshot;
    }

    /// <summary>UI thread only.</summary>
    private RemoteSnapshot Snapshot(int upNextLimit)
    {
        var p = _player;
        var track = p.CurrentTrack;
        var upNext = new List<RemoteTrack>(Math.Min(Math.Max(0, upNextLimit), p.UpNext.Count));
        for (var i = 0; i < upNext.Capacity; i++) upNext.Add(ToRemote(p.UpNext[i]));
        return new RemoteSnapshot(
            track != null ? ToRemote(track) : null,
            p.State == PlaybackState.Playing,
            (long)p.Position.TotalMilliseconds,
            // The decoder's figure once known, the tag's before (LocalApiDto.NowPlaying's rule).
            p.Duration > TimeSpan.Zero ? (long)p.Duration.TotalMilliseconds : (long)(track?.Duration.TotalMilliseconds ?? 0),
            p.Volume,
            p.IsShuffleEnabled,
            p.RepeatMode switch { RepeatMode.All => RemoteRepeat.All, RepeatMode.One => RemoteRepeat.One, _ => RemoteRepeat.Off },
            upNext,
            p.UpNext.Count);
    }

    private static RemoteTrack ToRemote(Track t) =>
        // As every artist line shows it, not the stored credit ("A; B"; GitHub #187).
        new(t.Id, t.AlbumId, t.Title ?? string.Empty, t.ArtistCreditDisplay ?? string.Empty, t.Album ?? string.Empty,
            (long)t.Duration.TotalMilliseconds);

    public async Task<bool> ControlAsync(RemoteCommand command)
    {
        var done = false;
        await _marshal(() => done = Control(command));
        return done;
    }

    /// <summary>UI thread only.</summary>
    private bool Control(RemoteCommand c)
    {
        var p = _player;
        switch (c.Action)
        {
            case RemoteAction.Toggle:
                p.PlayPauseCommand.Execute(null);
                return true;
            case RemoteAction.Play:
                if (p.State != PlaybackState.Playing) p.PlayPauseCommand.Execute(null);
                return true;
            case RemoteAction.Pause:
                if (p.State == PlaybackState.Playing) p.PlayPauseCommand.Execute(null);
                return true;
            case RemoteAction.Next:
                p.NextCommand.Execute(null);
                return true;
            case RemoteAction.Previous:
                p.PreviousCommand.Execute(null);
                return true;
            case RemoteAction.Seek:
                if (p.CurrentTrack == null) return false;
                p.SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, c.PositionMs)));
                return true;
            case RemoteAction.Volume:
                p.Volume = Math.Clamp(c.Volume, 0, 100);
                return true;
            case RemoteAction.Shuffle:
                if (c.Shuffle == null || c.Shuffle != p.IsShuffleEnabled) p.ToggleShuffleCommand.Execute(null);
                return true;
            case RemoteAction.Repeat:
            {
                if (c.Repeat is not { } wanted)
                {
                    p.CycleRepeatCommand.Execute(null);
                    return true;
                }
                var mode = wanted switch { RemoteRepeat.All => RepeatMode.All, RemoteRepeat.One => RepeatMode.One, _ => RepeatMode.Off };
                // Through the command, as a click: at most three steps round the cycle.
                for (var i = 0; i < 3 && p.RepeatMode != mode; i++) p.CycleRepeatCommand.Execute(null);
                return true;
            }
            case RemoteAction.Jump:
                // The row the phone tapped must still hold the song it showed there.
                if (c.Index < 0 || c.Index >= p.UpNext.Count || p.UpNext[c.Index].Id != c.TrackId) return false;
                p.PlayFromUpNextAt(c.Index);
                return true;
        }
        return false;
    }
}
