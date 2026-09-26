using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Opening Statistics recomputes every aggregate over the library and the play log. That
/// ran inline in Navigate on the UI thread (a visible stall on a large library); it now
/// runs in the background and only the results are applied on the UI thread.
/// </summary>
public class StatisticsRefreshOffUiThreadTests
{
    [AvaloniaFact]
    public void Refresh_ComputesOffTheUiThread_ThenShowsTheResults()
    {
        var library = new FakeLibraryService();
        var a = new Track { Title = "One", Artist = "Alpha", Album = "A", PlayCount = 3 };
        var b = new Track { Title = "Two", Artist = "Beta", Album = "B", PlayCount = 1 };
        library.TrackList.AddRange(new[] { a, b });
        var log = new ProbeList(new[] { Play(a), Play(b), Play(a) });
        var vm = new StatisticsViewModel(library, new FakePlayHistory(log));

        vm.Refresh();
        PumpUntil(() => vm.TotalTracks == 2);

        Assert.True(log.Reads > 0, "the play log was never read");
        Assert.False(log.ReadOnUiThread, "the play log was aggregated on the UI thread");
        Assert.Equal(2, vm.TotalTracks);
        Assert.True(vm.HasPlayHistory);
        Assert.Equal(3, vm.PlayLog.Count);
        Assert.Equal(new[] { "Alpha", "Beta" }, vm.TopArtists.Select(i => i.Label));
    }

    [AvaloniaFact]
    public void StaleRefresh_DoesNotOverwriteANewerOne()
    {
        var library = new FakeLibraryService();
        library.TrackList.Add(new Track { Title = "One", Artist = "Alpha", PlayCount = 1 });
        using var gate = new ManualResetEventSlim(false);
        var history = new FakePlayHistory(new ProbeList(Array.Empty<PlayHistoryEvent>(), gate),
                                          new ProbeList(Array.Empty<PlayHistoryEvent>()));
        var vm = new StatisticsViewModel(library, history);

        var first = vm.RefreshAsync(); // held on the gate while computing
        library.TrackList.Add(new Track { Title = "Two", Artist = "Beta", PlayCount = 1 });
        var second = vm.RefreshAsync();
        PumpUntil(() => second.IsCompleted);
        Assert.Equal(2, vm.TotalTracks);

        gate.Set();
        PumpUntil(() => first.IsCompleted);

        Assert.True(first.IsCompleted);
        Assert.Equal(2, vm.TotalTracks);
    }

    private static PlayHistoryEvent Play(Track t) => new()
    {
        TrackId = t.Id,
        Title = t.Title,
        Artist = t.Artist,
        PlayedAtUtc = DateTime.UtcNow.AddHours(-1)
    };

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FakePlayHistory : IPlayHistoryService
    {
        private readonly Queue<IReadOnlyList<PlayHistoryEvent>> _snapshots;
        private IReadOnlyList<PlayHistoryEvent> _current;

        public FakePlayHistory(params IReadOnlyList<PlayHistoryEvent>[] snapshots)
        {
            _snapshots = new Queue<IReadOnlyList<PlayHistoryEvent>>(snapshots);
            _current = snapshots[0];
        }

        // Each read hands out the next published snapshot (the last one repeats).
        public IReadOnlyList<PlayHistoryEvent> Events
        {
            get
            {
                if (_snapshots.Count > 0) _current = _snapshots.Dequeue();
                return _current;
            }
        }

        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>Play log that records which thread reads it and can hold readers on a gate.</summary>
    private sealed class ProbeList : IReadOnlyList<PlayHistoryEvent>
    {
        private readonly PlayHistoryEvent[] _items;
        private readonly ManualResetEventSlim? _gate;
        private int _reads;
        private volatile bool _readOnUiThread;

        public ProbeList(PlayHistoryEvent[] items, ManualResetEventSlim? gate = null)
        {
            _items = items;
            _gate = gate;
        }

        public int Reads => Volatile.Read(ref _reads);
        public bool ReadOnUiThread => _readOnUiThread;

        private void Touch()
        {
            Interlocked.Increment(ref _reads);
            if (Dispatcher.UIThread.CheckAccess()) _readOnUiThread = true;
            _gate?.Wait(TimeSpan.FromSeconds(10));
        }

        public int Count { get { Touch(); return _items.Length; } }
        public PlayHistoryEvent this[int index] { get { Touch(); return _items[index]; } }
        public IEnumerator<PlayHistoryEvent> GetEnumerator() { Touch(); return ((IEnumerable<PlayHistoryEvent>)_items).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
