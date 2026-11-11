using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Noctis.Helpers;

/// <summary>
/// Brings a bound list up to date item by item when only the objects changed, not the order.
/// A library rebuild (every metadata save, every re-read file) hands out new Album and Track
/// objects for the same songs. Lists that compared by reference then cleared and refilled
/// themselves, the page's content collapsed for a moment, and the scroll viewer clamped its
/// offset to the top (GitHub #223: Edit Info or Find Lyrics on an artist page scrolled far
/// down jumped back to the top). Replacing an item in place keeps the list's length, so the
/// page keeps its height and its scroll position.
/// </summary>
public static class InPlaceUpdate
{
    /// <summary>
    /// When <paramref name="target"/> holds the same keys in the same order as
    /// <paramref name="next"/>, swaps in each changed object (one Replace each) and returns
    /// true. Returns false, touching nothing, when the keys or their order differ.
    /// </summary>
    public static bool TryReplace<T, TKey>(ObservableCollection<T> target, IList<T> next, Func<T, TKey> key)
    {
        if (target.Count != next.Count) return false;
        var comparer = EqualityComparer<TKey>.Default;
        for (var i = 0; i < next.Count; i++)
        {
            if (!comparer.Equals(key(target[i]), key(next[i]))) return false;
        }
        for (var i = 0; i < next.Count; i++)
        {
            if (!ReferenceEquals(target[i], next[i])) target[i] = next[i];
        }
        return true;
    }
}
