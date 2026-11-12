using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Noctis.Helpers;

/// <summary>
/// An ObservableCollection that supports batch operations with a single Reset notification.
/// Standard ObservableCollection fires N+1 events for Clear() + N × Add(), causing the UI
/// to re-layout N+1 times. This fires a single Reset event instead.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Replaces all items in the collection with the given items,
    /// firing a single CollectionChanged Reset event.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Adds multiple items to the collection, firing a single Reset event.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Appends multiple items to the end, firing a single Add event that carries them all
    /// and their starting index. Unlike <see cref="AddRange"/>'s Reset, an ItemsControl keeps
    /// the containers it already has and only realizes the new items: a list filled in
    /// slices through AddRange tore down and rebuilt every earlier row on each slice.
    /// </summary>
    public void AppendRange(IEnumerable<T> items)
    {
        var added = new List<T>(items);
        if (added.Count == 0) return;
        CheckReentrancy();
        var start = Items.Count;
        foreach (var item in added)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, start));
    }

    /// <summary>
    /// Inserts multiple items at <paramref name="index"/> in their order, firing a single Add
    /// event that carries them all and their starting index (see <see cref="AppendRange"/>).
    /// </summary>
    public void InsertRange(int index, IEnumerable<T> items)
    {
        var added = new List<T>(items);
        if (added.Count == 0) return;
        CheckReentrancy();
        var start = Math.Clamp(index, 0, Items.Count);
        for (var k = 0; k < added.Count; k++)
            Items.Insert(start + k, added[k]);

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, start));
    }

    /// <summary>
    /// Removes <paramref name="count"/> items from <paramref name="index"/>, firing a single
    /// Remove event that carries them all and their starting index: an ItemsControl drops
    /// those containers and keeps every other one.
    /// </summary>
    public void RemoveRange(int index, int count)
    {
        if (count <= 0) return;
        CheckReentrancy();
        var removed = new List<T>(count);
        for (var k = 0; k < count; k++)
            removed.Add(Items[index + k]);
        for (var k = 0; k < count; k++)
            Items.RemoveAt(index);

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

    /// <summary>
    /// Makes the collection hold <paramref name="items"/>, a run of the same ordered sequence
    /// it holds now moved up or down (a sliding window): the items both share stay in place
    /// and only the ends change, by range Remove/Add events. Items are matched by reference.
    /// Anything else (no overlap) is one <see cref="ReplaceAll"/>.
    /// </summary>
    public void ShiftTo(IReadOnlyList<T> items)
    {
        var current = Items;
        if (current.Count == 0 || items.Count == 0)
        {
            ReplaceAll(items);
            return;
        }

        int Find(IList<T> list, T item)
        {
            for (var i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], item)) return i;
            return -1;
        }

        // Moving down: the new first item is inside the current run.
        var head = Find(current, items[0]);
        if (head >= 0)
        {
            var shared = Math.Min(current.Count - head, items.Count);
            for (var k = 0; k < shared; k++)
                if (!ReferenceEquals(current[head + k], items[k])) { ReplaceAll(items); return; }
            RemoveRange(0, head);
            RemoveRange(shared, current.Count - shared);
            AppendRange(items.Skip(shared));
            return;
        }

        // Moving up: the current first item is inside the new run.
        var lead = Find(items as IList<T> ?? items.ToList(), current[0]);
        if (lead >= 0)
        {
            var shared = Math.Min(current.Count, items.Count - lead);
            for (var k = 0; k < shared; k++)
                if (!ReferenceEquals(current[k], items[lead + k])) { ReplaceAll(items); return; }
            RemoveRange(shared, current.Count - shared);
            InsertRange(0, items.Take(lead));
            AppendRange(items.Skip(lead + shared));
            return;
        }

        ReplaceAll(items);
    }
}
