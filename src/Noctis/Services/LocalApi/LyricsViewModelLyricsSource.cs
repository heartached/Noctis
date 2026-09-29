using System.Collections.Specialized;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Services.LocalApi;

/// <summary>
/// Serves the Local API the lyrics the app is showing (sidecar, embedded, online or
/// plugin — whatever <see cref="LyricsViewModel"/> ended up loading), so a stream overlay
/// matches the lyrics page exactly. The view model is resolved lazily: the Local API can
/// start before the main window's view models exist. UI thread only.
/// </summary>
public sealed class LyricsViewModelLyricsSource : ILocalApiLyricsSource
{
    private readonly Func<LyricsViewModel?> _resolve;
    private LyricsViewModel? _hooked;
    private EventHandler? _changed;

    public LyricsViewModelLyricsSource(Func<LyricsViewModel?> resolve) => _resolve = resolve;

    public event EventHandler? Changed
    {
        add { _changed += value; Hook(); }
        remove { _changed -= value; }
    }

    private LyricsViewModel? Resolve()
    {
        Hook();
        return _hooked;
    }

    private void Hook()
    {
        if (_hooked != null) return;
        var vm = _resolve();
        if (vm == null) return;
        _hooked = vm;
        vm.LyricLines.CollectionChanged += OnLinesChanged;
        vm.UnsyncedLines.CollectionChanged += OnLinesChanged;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        _changed?.Invoke(this, EventArgs.Empty);

    public LocalApiLyrics? Snapshot()
    {
        var vm = Resolve();
        var track = vm?.Player.CurrentTrack;
        if (vm == null || track == null || vm.LoadedTrackId != track.Id)
            return null;
        return Build(track.Id, vm.LyricLines, vm.UnsyncedLines);
    }

    /// <summary>Pure conversion, shared with tests.</summary>
    public static LocalApiLyrics? Build(Guid trackId, IReadOnlyList<LyricLine> lines, IReadOnlyList<LyricLine> unsynced)
    {
        var synced = new List<LocalApiLyricLine>();
        foreach (var l in lines)
        {
            if (l.IsIntroPlaceholder || !l.Timestamp.HasValue) continue;
            IReadOnlyList<LocalApiLyricWord>? words = null;
            if (l.HasWords)
                words = l.Words!
                    .Select(w => new LocalApiLyricWord(
                        (long)w.Start.TotalMilliseconds,
                        w.End.HasValue ? (long)w.End.Value.TotalMilliseconds : null,
                        w.Text))
                    .ToList();
            synced.Add(new LocalApiLyricLine(
                (long)l.Timestamp.Value.TotalMilliseconds,
                l.EndTimestamp.HasValue ? (long)l.EndTimestamp.Value.TotalMilliseconds : null,
                l.Text,
                words));
        }

        var plainSource = unsynced.Count > 0 ? unsynced : lines;
        var plain = string.Join("\n", plainSource.Where(l => !l.IsIntroPlaceholder).Select(l => l.Text));

        if (synced.Count == 0 && string.IsNullOrWhiteSpace(plain))
            return null;

        if (synced.Count > 0)
            return new LocalApiLyrics(trackId, true, synced.Any(l => l.Words is { Count: > 0 }), synced, plain);

        var plainLines = plainSource.Where(l => !l.IsIntroPlaceholder)
            .Select(l => new LocalApiLyricLine(null, null, l.Text, null)).ToList();
        return new LocalApiLyrics(trackId, false, false, plainLines, plain);
    }
}
