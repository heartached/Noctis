using System.Globalization;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Noctis.Helpers;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// Settings › Tab Bar. The code here is the ≡ handle's drag, the Library rows' Edit drag
/// (LibraryPage): the row follows the finger, the rows it passes step aside, and the release moves
/// it whole rows away (QueueGesture.TargetIndex), which puts the bar in the new order at once.
/// </summary>
public partial class TabBarPane : UserControl
{
    private static readonly TimeSpan Shift = TimeSpan.FromMilliseconds(220);

    private Control? _dragged;
    private int _from = -1;
    private int _over = -1;
    private double _startY;
    private double _rowHeight;

    public TabBarPane()
    {
        InitializeComponent();
    }

    private TabBarSettingsViewModel? Model => DataContext as TabBarSettingsViewModel;

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Model is not { } model || sender is not Control { DataContext: TabBarChoice choice } handle) return;
        var from = model.Choices.IndexOf(choice);
        if (from < 0 || TabBarList.ContainerFromIndex(from) is not { } container) return;
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
        // The sheet's ScrollViewer would otherwise take the vertical drag and scroll.
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (_dragged == null || Model is not { } model) return;
        var dy = e.GetPosition(this).Y - _startY;
        _dragged.RenderTransform = TranslateY(dy);
        var over = QueueGesture.TargetIndex(_from, dy, _rowHeight, model.Choices.Count);
        if (over != _over)
        {
            // Only when the row under the finger changes: a transition restarted every move stalls.
            _over = over;
            for (var i = 0; i < model.Choices.Count; i++)
            {
                if (i != _from && TabBarList.ContainerFromIndex(i) is { } other)
                    other.RenderTransform = TranslateY(LibraryPage.ShiftFor(i, _from, over) * _rowHeight);
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

    /// <summary>The release half of a handle drag: the row lands whole rows away, rounded.
    /// Internal for tests.</summary>
    internal void CommitDrag(int from, double dy, double rowHeight)
    {
        if (Model is not { } model) return;
        model.Move(from, QueueGesture.TargetIndex(from, dy, rowHeight, model.Choices.Count));
    }

    /// <summary>Every row back in its own place with no animation, before the list moves.</summary>
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
        var count = Model?.Choices.Count ?? 0;
        for (var i = 0; i < count; i++)
            if (TabBarList.ContainerFromIndex(i) is { } container) yield return container;
    }
}
