using System.Runtime.CompilerServices;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Undo Grouping follow-up (owner 11-27): the grouping and Undo Grouping writes songs' files have
/// not taken yet (<see cref="Track.PendingAlbumWrite"/>), found by file path, so any other save of
/// such a file (a rating, lyrics, Edit Info, ReplayGain, BPM) writes the pending one first in the
/// same save (MetadataService.SaveTagsAtomicallyOrThrow) instead of overwriting or dropping it.
/// Each library registers how to find its songs; held weakly (tests make many libraries).
/// </summary>
public static class PendingAlbumWrites
{
    /// <summary>A song's pending write, and what to call once the file has taken it.</summary>
    public sealed class Entry
    {
        private readonly Action<Track, AlbumFileWrite, RawTagSnapshot?> _honoured;

        internal Entry(Track track, AlbumFileWrite write, Action<Track, AlbumFileWrite, RawTagSnapshot?> honoured)
        {
            Track = track;
            Write = write;
            _honoured = honoured;
        }

        public Track Track { get; }
        public AlbumFileWrite Write { get; }

        /// <summary>The file took the write; <paramref name="replaced"/> is what its album tags
        /// replaced there.</summary>
        public void Honoured(RawTagSnapshot? replaced) => _honoured(Track, Write, replaced);
    }

    private sealed record Source(Func<string, Track?> Find, Action<Track, AlbumFileWrite, RawTagSnapshot?> Honoured);

    private static readonly ConditionalWeakTable<object, Source> Sources = new();

    /// <summary>Registers <paramref name="owner"/>'s songs: <paramref name="find"/> gives the song
    /// at a path (null when none), <paramref name="honoured"/> is told when its file took the
    /// pending write.</summary>
    public static void Register(object owner, Func<string, Track?> find, Action<Track, AlbumFileWrite, RawTagSnapshot?> honoured)
        => Sources.AddOrUpdate(owner, new Source(find, honoured));

    /// <summary>The pending album write of the song at <paramref name="path"/>, if there is one.</summary>
    public static Entry? Find(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var (_, source) in (IEnumerable<KeyValuePair<object, Source>>)Sources)
        {
            Track? track;
            try { track = source.Find(path); }
            catch { continue; }
            if (track?.PendingAlbumWrite is { } write)
                return new Entry(track, write, source.Honoured);
        }
        return null;
    }
}
