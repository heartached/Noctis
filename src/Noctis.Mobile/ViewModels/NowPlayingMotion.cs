using System;
using System.ComponentModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Mobile.Services;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// The animated cover of the song on Now Playing (<see cref="Path"/>; null = the still cover).
/// Looked up only while Now Playing is open with the Cover style and Animated Covers on, so
/// songs that only pass through the queue unseen never download a loop; again on each new song
/// and each change of the setting. A lookup still running for the previous song is cancelled.
/// </summary>
public sealed partial class NowPlayingMotion : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly AnimatedCoverSource _source;
    private CancellationTokenSource? _lookup;
    private Track? _lookedUp;
    private AnimatedCoversSetting _lookedUpWith;

    /// <summary>The loop to play (file path or content:// URI), or null.</summary>
    [ObservableProperty] private string? _path;

    public NowPlayingMotion(ShellViewModel shell, AnimatedCoverSource source)
    {
        _shell = shell;
        _source = source;
        shell.PropertyChanged += OnShellChanged;
        shell.Player.PropertyChanged += OnPlayerChanged;
        Refresh();
    }

    /// <summary>The lookup in flight (tests await it).</summary>
    internal System.Threading.Tasks.Task Pending { get; private set; } = System.Threading.Tasks.Task.CompletedTask;

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsNowPlayingOpen) or nameof(ShellViewModel.AnimatedCovers)
            or nameof(ShellViewModel.NowPlayingArtwork))
            Refresh();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingViewModel.CurrentTrack)) Refresh();
    }

    private void Refresh()
    {
        var track = _shell.Player.CurrentTrack;
        var setting = _shell.AnimatedCovers;
        if (track is null || setting == AnimatedCoversSetting.Off)
        {
            Cancel();
            _lookedUp = null;
            Path = null;
            return;
        }
        // Asked already: done unless it found nothing and Now Playing opens again (back on Wi-Fi,
        // the computer reachable again); asking again for a song with no loop costs a folder
        // listing at most (AnimatedCoverSource remembers the computer's "none").
        if (ReferenceEquals(track, _lookedUp) && setting == _lookedUpWith && (Path is not null || _lookup is not null)) return;
        if (!ReferenceEquals(track, _lookedUp)) Path = null;
        // Wait for a look at the cover: nothing is fetched for a song nobody opened Now Playing on.
        if (!_shell.IsNowPlayingOpen || _shell.ShowsDiscArtwork) return;
        Cancel();
        _lookedUp = track;
        _lookedUpWith = setting;
        var cts = _lookup = new CancellationTokenSource();
        Pending = LookUpAsync(track, setting, cts);
    }

    private async System.Threading.Tasks.Task LookUpAsync(Track track, AnimatedCoversSetting setting, CancellationTokenSource cts)
    {
        var path = await _source.ResolveAsync(track, setting, cts.Token);
        // Ended: no longer running, also when the song changed meanwhile (left set, that song
        // coming back was never looked up again and kept its still cover).
        if (ReferenceEquals(_lookup, cts)) _lookup = null;
        if (cts.IsCancellationRequested || !ReferenceEquals(_shell.Player.CurrentTrack, track)) return;
        Path = path;
    }

    /// <summary>An album's chosen loop changed (Choose / Remove Animated Cover): the playing song
    /// is looked up afresh, now if Now Playing shows it, else when it opens.</summary>
    public void Reload()
    {
        Cancel();
        _lookedUp = null;
        Path = null;
        Refresh();
    }

    private void Cancel()
    {
        _lookup?.Cancel();
        _lookup = null;
    }
}
