using System.ComponentModel;
using System.Globalization;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Noctis.Helpers;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// The Library tab root: header, the list rows, then the Pinned / Recently Added / On Repeat /
/// Recently Played sections. The code here is the rows' Edit mode motion: rows switched off
/// arrive with the check marks, and a ≡ handle drags a row into place, the rows it passes
/// stepping aside (the Queue page's handle drag, with live room-making).
/// </summary>
public partial class LibraryPage : UserControl
{
    private static readonly TimeSpan Shift = TimeSpan.FromMilliseconds(220);

    private ShellViewModel? _shell;
    private LibraryRowsViewModel? _rows;

    // The handle drag in flight: the row's container, where it started and the row it is over.
    private Control? _dragged;
    private int _from = -1;
    private int _over = -1;
    private double _startY;
    private double _rowHeight;

    public LibraryPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_rows != null) _rows.PropertyChanged -= OnRowsChanged;
            _shell = DataContext as ShellViewModel;
            _rows = _shell?.LibraryRows;
            if (_rows != null) _rows.PropertyChanged += OnRowsChanged;
        };
    }

    /// <summary>Into Edit: the rows that were switched off join the list, settling in as the
    /// checks slide across (the slide itself is the row styles' work).</summary>
    private void OnRowsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryRowsViewModel.IsEditing) || _rows == null) return;
        if (!_rows.IsEditing)
        {
            EndDrag();
            return;
        }
        foreach (var row in _rows.Rows.Where(r => !r.IsShown))
            if (LibraryRowList.ContainerFromItem(row) is ContentPresenter { Child: { } button }) Appear.Play(button);
    }

    // ── Drag to reorder (handle) ───────────────────────────────────

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_rows is not { IsEditing: true } rows || sender is not Control { DataContext: LibraryRowItem row } handle) return;
        var from = rows.Rows.IndexOf(row);
        if (from < 0 || LibraryRowList.ContainerFromIndex(from) is not { } container) return;
        EndDrag();
        _dragged = container;
        _from = _over = from;
        _startY = e.GetPosition(this).Y;
        _rowHeight = container.Bounds.Height;
        var ease = new CubicBezierEase(0.32, 0.72, 0, 1);
        foreach (var other in Containers())
            other.Transitions = ReferenceEquals(other, container)
                ? null
                : new Transitions { new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Shift, Easing = ease } };
        container.ZIndex = 1;               // drawn above the rows it passes
        e.Pointer.Capture(handle);
        // The page's ScrollViewer would otherwise take the vertical drag and scroll.
        e.PreventGestureRecognition();
        e.Handled = true;                   // the press is not a tap on the row (which toggles it)
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (_dragged == null || _rows == null) return;
        var dy = e.GetPosition(this).Y - _startY;
        _dragged.RenderTransform = TranslateY(dy);
        var over = QueueGesture.TargetIndex(_from, dy, _rowHeight, _rows.Rows.Count);
        if (over != _over)
        {
            // Only when the row under the finger changes: a transition restarted every move stalls.
            _over = over;
            for (var i = 0; i < _rows.Rows.Count; i++)
            {
                if (i != _from && LibraryRowList.ContainerFromIndex(i) is { } other)
                    other.RenderTransform = TranslateY(ShiftFor(i, _from, over) * _rowHeight);
            }
        }
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    private void OnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragged == null) return;
        var dy = e.GetPosition(this).Y - _startY;
        var (from, height) = (_from, _rowHeight);
        EndDrag();
        CommitDrag(from, dy, height);
        e.Handled = true;
    }

    private void OnHandleCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    /// <summary>How many rows a row at <paramref name="index"/> steps while the row from
    /// <paramref name="from"/> hovers over <paramref name="over"/>: up one, down one or none.</summary>
    internal static int ShiftFor(int index, int from, int over)
    {
        if (from < over && index > from && index <= over) return -1;
        if (from > over && index >= over && index < from) return 1;
        return 0;
    }

    /// <summary>The release half of a handle drag: the row lands whole rows away, rounded.
    /// Internal for tests.</summary>
    internal void CommitDrag(int from, double dy, double rowHeight)
    {
        if (_rows is not { IsEditing: true } rows) return;
        rows.Move(from, QueueGesture.TargetIndex(from, dy, rowHeight, rows.Rows.Count));
    }

    /// <summary>Puts every row back in its own place with no animation, before the list moves
    /// (or does not), so nothing glides back across the screen.</summary>
    private void EndDrag()
    {
        if (_dragged == null) return;
        foreach (var container in Containers())
        {
            container.Transitions = null;
            container.RenderTransform = null;
            container.ZIndex = 0;
        }
        _dragged = null;
        _from = _over = -1;
    }

    // Invariant: a comma-decimal culture's "12,5px" does not parse.
    private static ITransform TranslateY(double y) =>
        TransformOperations.Parse(string.Create(CultureInfo.InvariantCulture, $"translateY({y:0.##}px)"));

    private IEnumerable<Control> Containers()
    {
        var count = _rows?.Rows.Count ?? 0;
        for (var i = 0; i < count; i++)
            if (LibraryRowList.ContainerFromIndex(i) is { } container) yield return container;
    }
}
