using System;
using System.Collections.Generic;

namespace Noctis.Controls;

/// <summary>
/// Which consumers hold a lease on which animated-cover source. Pure bookkeeping (no
/// LibVLC, no Avalonia) so the sharing rules are unit-testable: one decoder per source,
/// started by the first lease and stopped after the last one is released. A consumer
/// leases at most one source; leasing another first gives up the old one. UI thread only.
/// </summary>
internal sealed class AnimatedCoverLeases<T> where T : class
{
    private readonly Dictionary<string, List<T>> _bySource = new(StringComparer.Ordinal);
    private readonly Dictionary<T, string> _byConsumer = new(ReferenceEqualityComparer.Instance);

    /// <summary>Leases <paramref name="source"/> for <paramref name="consumer"/>. True when
    /// it is the source's first consumer, i.e. nothing was decoding it before.</summary>
    public bool Acquire(string source, T consumer)
    {
        if (_byConsumer.TryGetValue(consumer, out var current))
        {
            if (current == source) return false;
            Release(consumer);
        }

        if (!_bySource.TryGetValue(source, out var consumers))
            _bySource[source] = consumers = new List<T>();
        consumers.Add(consumer);
        _byConsumer[consumer] = source;
        return consumers.Count == 1;
    }

    /// <summary>Drops the consumer's lease. <c>WasLast</c> is true when that left its
    /// source with no consumers — the moment its decoder may stop.</summary>
    public (string? Source, bool WasLast) Release(T consumer)
    {
        if (!_byConsumer.Remove(consumer, out var source))
            return (null, false);

        var consumers = _bySource[source];
        consumers.Remove(consumer);
        if (consumers.Count > 0)
            return (source, false);

        _bySource.Remove(source);
        return (source, true);
    }

    public int CountOf(string source) => _bySource.TryGetValue(source, out var c) ? c.Count : 0;

    public IReadOnlyList<T> ConsumersOf(string source)
        => _bySource.TryGetValue(source, out var c) ? c : Array.Empty<T>();

    public string? SourceOf(T consumer) => _byConsumer.TryGetValue(consumer, out var s) ? s : null;

    /// <summary>Number of sources with at least one consumer (one decoder each).</summary>
    public int SourceCount => _bySource.Count;
}

/// <summary>Pure rules for when an animated cover may decode and which frames it shows.</summary>
internal static class AnimatedCoverPolicy
{
    /// <summary>
    /// A consumer holds a decoder only while someone could actually see it. Hidden hosts
    /// used to keep decoding: IsVisible=False does not detach a control, so the unused
    /// mini-player forms, the inactive Cover Flow modes and every page of a hidden or
    /// minimized window each ran their own full software decode.
    /// </summary>
    public static bool ShouldDecode(bool isActive, bool hasSource, bool isAttached,
        bool isEffectivelyVisible, bool windowShown, bool windowMinimized)
        => isActive && hasSource && isAttached && isEffectivelyVisible && windowShown && !windowMinimized;

    /// <summary>Shortest gap between two frames pushed to the UI (~35 fps ceiling). Covers
    /// are 24–30 fps, which pass untouched with room for timer jitter; a 60 fps clip shows
    /// every other frame instead of copying and re-rendering twice per display refresh.</summary>
    public const double MinFrameIntervalMs = 28;

    public static bool AcceptFrame(long nowTimestamp, long lastAcceptedTimestamp, long timestampFrequency)
        => lastAcceptedTimestamp == 0
           || (nowTimestamp - lastAcceptedTimestamp) * 1000.0 / timestampFrequency >= MinFrameIntervalMs;
}
