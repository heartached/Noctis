using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// Edit Info saves that are still writing files after their window closed (Discord 11-23:
/// "at least let us click outside and have the changes saved in the background"). A save
/// waits for every earlier one that touches any of its songs, so two saves in a row on the
/// same files land in order, and an editor opening on a song being saved waits for that
/// save before it reads the song's files (<see cref="WhenSavedAsync"/>). Songs are matched
/// by Track instance: the editor always edits the library's own instance
/// (<see cref="MetadataHelper.EditedInstance"/>).
/// </summary>
internal static class MetadataSaveQueue
{
    private sealed class Job(HashSet<Track> tracks)
    {
        public HashSet<Track> Tracks { get; } = tracks;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly List<Job> Pending = new();

    /// <summary>Saves still writing.</summary>
    public static int PendingCount
    {
        get { lock (Pending) return Pending.Count; }
    }

    /// <summary>
    /// Runs <paramref name="write"/> once every pending save sharing a song with
    /// <paramref name="tracks"/> has finished. The returned task completes when this save
    /// has; it faults only if <paramref name="write"/> throws.
    /// </summary>
    public static Task RunAsync(IEnumerable<Track> tracks, Func<Task> write)
    {
        var job = new Job(new HashSet<Track>(tracks, ReferenceEqualityComparer.Instance));
        Task[] before;
        lock (Pending)
        {
            before = Overlapping(job.Tracks);
            Pending.Add(job);
        }
        return RunAfterAsync(before, job, write);
    }

    /// <summary>Completes once no save touching any of <paramref name="tracks"/> is pending.</summary>
    public static Task WhenSavedAsync(IEnumerable<Track> tracks)
    {
        Task[] pending;
        lock (Pending)
        {
            if (Pending.Count == 0) return Task.CompletedTask;
            pending = Overlapping(new HashSet<Track>(tracks, ReferenceEqualityComparer.Instance));
        }
        return pending.Length == 0 ? Task.CompletedTask : Task.WhenAll(pending);
    }

    /// <summary>Completes once every save pending now has finished (app shutdown).</summary>
    public static Task WhenAllSavedAsync()
    {
        lock (Pending)
            return Pending.Count == 0 ? Task.CompletedTask : Task.WhenAll(Pending.Select(j => j.Done.Task).ToArray());
    }

    private static Task[] Overlapping(HashSet<Track> tracks) =>
        Pending.Where(j => j.Tracks.Overlaps(tracks)).Select(j => j.Done.Task).ToArray();

    private static async Task RunAfterAsync(Task[] before, Job job, Func<Task> write)
    {
        try
        {
            // Done never faults (see the finally below), so this only waits.
            if (before.Length > 0) await Task.WhenAll(before);
            await write();
        }
        finally
        {
            lock (Pending) Pending.Remove(job);
            job.Done.TrySetResult();
        }
    }
}
