using System;
using System.Collections.Generic;
using Avalonia.Threading;

namespace Noctis.Helpers;

/// <summary>
/// Fills an observable collection in slices across dispatcher turns instead of in one
/// synchronous burst. A non-virtualized ItemsControl inflates one row template per
/// item the moment it is added — thirty search rows or a hundred queue rows, each
/// with artwork, all on the frame the mini player's drawer opens, was the "lags for a
/// second" on the Search/Queue buttons: the sheet could not even start its slide
/// until every row existed. The first slice lands immediately (the sheet never opens
/// empty), the rest arrive a few rows per idle turn under the resize animation.
/// </summary>
public static class StreamingFill
{
    public const int DefaultFirstChunk = 8;
    public const int DefaultChunk = 10;

    /// <summary>Pure slicing: the first slice is <paramref name="first"/> items, the rest <paramref name="chunk"/> each.</summary>
    public static List<List<T>> Chunks<T>(IReadOnlyList<T> items, int first = DefaultFirstChunk, int chunk = DefaultChunk)
    {
        first = Math.Max(1, first);
        chunk = Math.Max(1, chunk);
        var result = new List<List<T>>();
        var i = 0;
        var size = first;
        while (i < items.Count)
        {
            var take = Math.Min(size, items.Count - i);
            var slice = new List<T>(take);
            for (var k = 0; k < take; k++) slice.Add(items[i + k]);
            result.Add(slice);
            i += take;
            size = chunk;
        }
        return result;
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with <paramref name="items"/>: the first slice
    /// synchronously, the remainder posted one slice per Background-priority turn.
    /// <paramref name="generation"/> is re-read before every slice so a newer fill of the
    /// same collection cancels the in-flight one. While <paramref name="gate"/> returns
    /// false the remaining slices wait (polled every <see cref="GateRetryMs"/>) — the mini
    /// player holds the gate closed for the length of its drawer slide, because every row
    /// that exists during a window-resize animation is re-laid-out on every tick.
    /// </summary>
    public static void Into<T>(BulkObservableCollection<T> target, IEnumerable<T> items,
        int generation, Func<int> currentGeneration, int first = DefaultFirstChunk, int chunk = DefaultChunk,
        Func<bool>? gate = null)
    {
        var list = items as IReadOnlyList<T> ?? new List<T>(items);
        var slices = Chunks(list, first, chunk);
        if (slices.Count == 0)
        {
            target.ReplaceAll(Array.Empty<T>());
            return;
        }
        target.ReplaceAll(slices[0]);
        if (slices.Count == 1) return;

        var next = 1;
        void Step()
        {
            if (currentGeneration() != generation || next >= slices.Count) return;
            if (gate != null && !gate())
            {
                DispatcherTimer.RunOnce(Step, TimeSpan.FromMilliseconds(GateRetryMs), DispatcherPriority.Background);
                return;
            }
            target.AddRange(slices[next++]);
            if (next < slices.Count)
                Dispatcher.UIThread.Post(Step, DispatcherPriority.Background);
        }
        Dispatcher.UIThread.Post(Step, DispatcherPriority.Background);
    }

    /// <summary>How often a closed gate is re-checked.</summary>
    public const int GateRetryMs = 50;
}
