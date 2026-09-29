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
}
