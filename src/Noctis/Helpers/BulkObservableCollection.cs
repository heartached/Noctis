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
}
