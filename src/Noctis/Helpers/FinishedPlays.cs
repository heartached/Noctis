using System.ComponentModel;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Helpers;

/// <summary>
/// Holds the songs whose play was counted until they stop being the playing song (the next
/// one starts, or playback stops: the queue ran out, Stop after current), then hands them
/// to <c>apply</c>. A counted play raises no
/// LibraryUpdated, so a list ranked by plays re-ranks from this instead (Discord, Andre 10-28),
/// Apple Music's way: the count shows in place at once, the row moves once its song is over.
/// <c>apply</c> returns false to keep the songs for the next <see cref="Flush"/>.
/// UI thread only, like PlayCounted.
/// </summary>
public sealed class FinishedPlays : IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly Func<List<Track>, bool> _apply;
    private readonly Dictionary<Guid, Track> _pending = new();

    public FinishedPlays(PlayerViewModel player, Func<List<Track>, bool> apply)
    {
        _player = player;
        _apply = apply;
        _player.PlayCounted += OnPlayCounted;
        _player.TrackStarted += OnTrackStarted;
        _player.PropertyChanged += OnPlayerPropertyChanged;
    }

    /// <summary>A counted song that is still playing: its row holds its place until it is over.</summary>
    public bool IsHeld(Track track) => _pending.ContainsKey(track.Id);

    private void OnPlayCounted(object? sender, Track track)
    {
        _pending[track.Id] = track;
        Flush();
    }

    private void OnTrackStarted(object? sender, Track track) => Flush();

    // The last song of the queue ending (or Stop after current) starts no next song: without
    // this its row never moved, and a later replay counted it again where it stood
    // (Discord, Andre 11-04: a 17 among the 15s).
    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.State) or nameof(PlayerViewModel.CurrentTrack))
            Flush();
    }

    public void Flush()
    {
        if (_pending.Count == 0) return;
        // By id: the queue can hold a pre-reload copy of the library's track.
        // Stopped: the song is over even while it stays loaded (Keep played songs).
        var currentId = _player.State == PlaybackState.Stopped ? null : _player.CurrentTrack?.Id;
        var finished = _pending.Values.Where(t => t.Id != currentId).ToList();
        if (finished.Count == 0 || !_apply(finished)) return;
        foreach (var t in finished) _pending.Remove(t.Id);
    }

    public void Dispose()
    {
        _player.PlayCounted -= OnPlayCounted;
        _player.TrackStarted -= OnTrackStarted;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
    }
}
