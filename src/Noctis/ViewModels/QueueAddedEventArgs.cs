using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// One user "Add to Queue" / "Play Next" (see <see cref="PlayerViewModel.TracksQueued"/>):
/// how many tracks, the first of them, the album / playlist / folder they came from (if
/// the caller named one) and whether they went to the front of the queue.
/// </summary>
public sealed class QueueAddedEventArgs : EventArgs
{
    public QueueAddedEventArgs(int count, Track firstTrack, string? sourceName, bool playNext)
    {
        Count = count;
        FirstTrack = firstTrack;
        SourceName = sourceName;
        PlayNext = playNext;
    }

    public int Count { get; }
    public Track FirstTrack { get; }
    public string? SourceName { get; }
    public bool PlayNext { get; }
}
