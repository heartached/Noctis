using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Helpers;

/// <summary>
/// Holds the songs whose play was counted until their song is over, then hands them to
/// <c>apply</c>. A counted play raises no LibraryUpdated, so a list ranked by plays
/// re-ranks from this instead (Discord, Andre 10-28), Apple Music's way: the count shows
/// in place at once, the row moves once its song ends (<see cref="PlayerViewModel.TrackEnding"/>),
/// not when the next one starts. <c>apply</c> returns false to keep the songs for the
/// next <see cref="Flush"/>. UI thread only, like PlayCounted.
/// </summary>
public sealed class FinishedPlays : IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly Func<List<Track>, bool> _apply;
    private readonly Func<bool>? _reRankAtEnd;
    private readonly Dictionary<Guid, Track> _pending = new();

    /// <param name="reRankAtEnd">False defers the move to the next song's start (the old
    /// on-change behaviour): <see cref="PlayerViewModel.TrackEnding"/> is ignored and the
    /// rows move on <see cref="PlayerViewModel.TrackStarted"/>. Null means at song end.</param>
    public FinishedPlays(PlayerViewModel player, Func<List<Track>, bool> apply, Func<bool>? reRankAtEnd = null)
    {
        _player = player;
        _apply = apply;
        _reRankAtEnd = reRankAtEnd;
        _player.PlayCounted += OnPlayCounted;
        _player.TrackEnding += OnTrackEnding;
        _player.TrackStarted += OnTrackStarted;
    }

    private void OnPlayCounted(object? sender, Track track)
    {
        _pending[track.Id] = track;
        Flush();
    }

    /// <summary>The leaving song is still <see cref="PlayerViewModel.CurrentTrack"/> here,
    /// so it is included explicitly — unlike <see cref="Flush"/>, which holds the current one back.</summary>
    private void OnTrackEnding(object? sender, Track leaving)
    {
        if (_pending.Count == 0 || !(_reRankAtEnd?.Invoke() ?? true)) return;
        // By id: the queue can hold a pre-reload copy of the library's track.
        var currentId = _player.CurrentTrack?.Id;
        var finished = _pending.Values.Where(t => t.Id == leaving.Id || t.Id != currentId).ToList();
        if (finished.Count == 0) return;
        if (!_apply(finished)) return;
        foreach (var t in finished) _pending.Remove(t.Id);
    }

    private void OnTrackStarted(object? sender, Track track) => Flush();

    public void Flush()
    {
        if (_pending.Count == 0) return;
        // By id: the queue can hold a pre-reload copy of the library's track.
        var currentId = _player.CurrentTrack?.Id;
        var finished = _pending.Values.Where(t => t.Id != currentId).ToList();
        if (finished.Count == 0 || !_apply(finished)) return;
        foreach (var t in finished) _pending.Remove(t.Id);
    }

    public void Dispose()
    {
        _player.PlayCounted -= OnPlayCounted;
        _player.TrackEnding -= OnTrackEnding;
        _player.TrackStarted -= OnTrackStarted;
    }
}
