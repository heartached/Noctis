using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace Noctis.Controls;

/// <summary>
/// A virtualized list whose rows, once built, are kept: scrolled out of view they are hidden, not
/// torn down. The plain ItemsControl recycles a row's container by taking it out of the panel and
/// putting it back for the next item, and the container then rebuilds its item template and
/// every control re-attaches its styles: in the Lyrics Studio's review that was ~30 ms per line
/// coming into view (LyricsStudioScrollPerfTests), the stutter of a fast scroll (owner 11-26).
/// <para>
/// Each item of <see cref="Rows"/> gets one host (a <see cref="ContentPresenter"/> showing it with
/// <see cref="ItemsControl.ItemTemplate"/>), and the hosts are the items the virtualizing panel
/// sees. A control that is its own item is only hidden when it scrolls away, and shown again as it
/// was. Hosts are cheap until shown: the template is applied when a host is first measured, so
/// picking a song still builds only the lines in view.
/// </para>
/// </summary>
public class KeptRowsItemsControl : ItemsControl
{
    public static readonly StyledProperty<IEnumerable?> RowsProperty =
        AvaloniaProperty.Register<KeptRowsItemsControl, IEnumerable?>(nameof(Rows));

    private readonly AvaloniaList<Control> _hosts = new();
    private INotifyCollectionChanged? _watched;

    public KeptRowsItemsControl() => ItemsSource = _hosts;

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    /// <summary>The items shown, one row each (in place of ItemsSource, which holds the rows' hosts).</summary>
    public IEnumerable? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    /// <summary>The host showing each item, in order (tests).</summary>
    public IReadOnlyList<Control> Hosts => _hosts;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty)
        {
            if (_watched is not null) _watched.CollectionChanged -= OnRowsChanged;
            _watched = change.NewValue as INotifyCollectionChanged;
            if (_watched is not null) _watched.CollectionChanged += OnRowsChanged;
            Rebuild();
        }
        else if (change.Property == ItemTemplateProperty)
        {
            foreach (var host in _hosts)
                if (host is ContentPresenter p) p.ContentTemplate = ItemTemplate;
        }
    }

    private Control Host(object? item) => new ContentPresenter { Content = item, ContentTemplate = ItemTemplate };

    private void Rebuild()
    {
        var hosts = new List<Control>();
        if (Rows is { } rows)
            foreach (var item in rows) hosts.Add(Host(item));
        _hosts.Clear();
        _hosts.AddRange(hosts);
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is { } added:
                var at = e.NewStartingIndex < 0 ? _hosts.Count : e.NewStartingIndex;
                var newHosts = new List<Control>(added.Count);
                foreach (var item in added) newHosts.Add(Host(item));
                _hosts.InsertRange(at, newHosts);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is { } removed && e.OldStartingIndex >= 0:
                _hosts.RemoveRange(e.OldStartingIndex, removed.Count);
                break;
            case NotifyCollectionChangedAction.Replace when e.NewItems is { } replaced && e.NewStartingIndex >= 0:
                for (var i = 0; i < replaced.Count; i++) _hosts[e.NewStartingIndex + i] = Host(replaced[i]);
                break;
            case NotifyCollectionChangedAction.Move when e.OldItems is { Count: 1 } && e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0:
                _hosts.Move(e.OldStartingIndex, e.NewStartingIndex);
                break;
            default:
                Rebuild();
                break;
        }
    }
}
