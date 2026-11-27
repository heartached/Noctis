using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
/// <para>
/// The rest are then built ahead, a couple per idle tick (paused while the list scrolls, dropped
/// when the list changes): each goes through the panel's scroll-into-view path, whose scroll
/// request is swallowed here, so the row is realized, measured and then only hidden. The first
/// scroll through a new song finds its rows built.
/// </para>
/// </summary>
public class KeptRowsItemsControl : ItemsControl
{
    public static readonly StyledProperty<IEnumerable?> RowsProperty =
        AvaloniaProperty.Register<KeptRowsItemsControl, IEnumerable?>(nameof(Rows));

    private readonly AvaloniaList<Control> _hosts = new();
    private INotifyCollectionChanged? _watched;

    /// <summary>Rows built ahead per idle tick, at most (fewer when a tick runs past <see cref="PrebuildBudgetMs"/>).</summary>
    internal const int PrebuildPerTick = 3;
    internal const double PrebuildBudgetMs = 6;
    /// <summary>A scroll this recent pauses the building ahead, so it never lands in a scrolling frame.</summary>
    internal int PrebuildQuietMs { get; set; } = 200;

    private readonly DispatcherTimer _prebuild;
    private ScrollViewer? _scroller;
    private long _lastScroll;
    private bool _prebuilding;
    private int _prebuildNext;

    public KeptRowsItemsControl()
    {
        ItemsSource = _hosts;
        _prebuild = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        _prebuild.Tick += (_, _) => PrebuildTick();
        // The scroll a scroll-into-view asks for: swallowed while a row is built ahead.
        AddHandler(RequestBringIntoViewEvent, (_, e) => { if (_prebuilding) e.Handled = true; });
        AttachedToVisualTree += (_, _) =>
        {
            _scroller = this.FindAncestorOfType<ScrollViewer>();
            if (_scroller is not null) _scroller.ScrollChanged += OnScrollChanged;
            RestartPrebuild();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _prebuild.Stop();
            if (_scroller is not null) _scroller.ScrollChanged -= OnScrollChanged;
            _scroller = null;
        };
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y != 0 || e.OffsetDelta.X != 0) _lastScroll = Environment.TickCount64;
    }

    /// <summary>Rows are still waiting to be built ahead (tests).</summary>
    internal bool IsPrebuilding => _prebuild.IsEnabled;

    private void RestartPrebuild()
    {
        _prebuildNext = 0;
        if (_hosts.Count > 0 && this.IsAttachedToVisualTree()) _prebuild.Start();
        else _prebuild.Stop();
    }

    /// <summary>
    /// One idle tick of building ahead: the next rows not built yet (from the first one past the
    /// screen, so the way down is ready first), until a few are done or the tick's budget is spent.
    /// Internal so tests can run ticks without waiting on the timer.
    /// </summary>
    internal void PrebuildTick()
    {
        if (!IsEffectivelyVisible || ItemsPanelRoot is not VirtualizingStackPanel panel || _hosts.Count == 0) { _prebuild.Stop(); return; }
        if (Environment.TickCount64 - _lastScroll < PrebuildQuietMs) return;
        // Not while a row is being edited: scrolling a row into view re-lays the rows on screen,
        // and a hidden-and-shown row loses the keyboard focus (the caret left the line mid-typing).
        if (IsKeyboardFocusWithin) return;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var built = 0;
        var from = Math.Max(_prebuildNext, panel.LastRealizedIndex + 1);
        for (var pass = 0; pass < 2 && built < PrebuildPerTick; pass++)
        {
            for (var i = pass == 0 ? from : 0; i < _hosts.Count && built < PrebuildPerTick; i++)
            {
                if (_hosts[i].GetVisualParent() is not null) continue;
                _prebuilding = true;
                try { ScrollIntoView(i); }
                finally { _prebuilding = false; }
                built++;
                _prebuildNext = i + 1;
                if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds > PrebuildBudgetMs) return;
            }
        }
        if (built > 0) return;
        // All built. Scrolling a row into view realizes the rows up to it as well, so the panel now
        // holds the rows built ahead as shown; one more measure leaves only those in view shown
        // (here, in idle time, rather than in the first scroll's first frame).
        _prebuild.Stop();
        panel.InvalidateMeasure();
    }

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
        var old = new List<Control>(_hosts);
        _hosts.Clear();
        DropFromPanel(old);
        _hosts.AddRange(hosts);
        // A new list: building ahead starts over for it (the old list's rows are gone with it).
        RestartPrebuild();
    }

    /// <summary>
    /// Takes rows whose items are gone out of the panel. The panel only removes the rows it shows:
    /// a row that is its own item and was hidden when it scrolled away stays its child for good, so
    /// every song opened in the review left its rows behind (148 children after two songs).
    /// </summary>
    private void DropFromPanel(IEnumerable<Control> gone)
    {
        foreach (var host in gone)
            if (host.GetVisualParent() is Panel panel) panel.Children.Remove(host);
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
                if (!_prebuild.IsEnabled) RestartPrebuild();
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is { } removed && e.OldStartingIndex >= 0:
                var gone = new List<Control>(_hosts.Skip(e.OldStartingIndex).Take(removed.Count));
                _hosts.RemoveRange(e.OldStartingIndex, removed.Count);
                DropFromPanel(gone);
                break;
            case NotifyCollectionChangedAction.Replace when e.NewItems is { } replaced && e.NewStartingIndex >= 0:
                for (var i = 0; i < replaced.Count; i++)
                {
                    var was = _hosts[e.NewStartingIndex + i];
                    _hosts[e.NewStartingIndex + i] = Host(replaced[i]);
                    DropFromPanel(new[] { was });
                }
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
