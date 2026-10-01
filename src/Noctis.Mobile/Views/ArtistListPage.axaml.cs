using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// The Artists grid. The code here is the A–Z strip: a press on a letter, or a finger sliding
/// along the strip, scrolls the grid so that letter's first row sits at the top. Rows have a
/// fixed height (ArtistListPageViewModel.RowHeight), so the target offset is row × height and
/// the virtualising panel realises just the rows around it. The grid also tells the page which
/// rows are realised (on screen, plus the panel's small margin), so only those circles ask for
/// their artist's photo, and a row scrolled away drops its asks.
/// </summary>
public partial class ArtistListPage : UserControl
{
    private ArtistListPageViewModel? _vm;
    private bool _indexing;
    private string? _lastLetter;

    public ArtistListPage()
    {
        InitializeComponent();
        ArtistGrid.ContainerPrepared += (_, e) => ShowRow(e.Container);
        ArtistGrid.ContainerClearing += (_, e) => HideRow(e.Container);
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as ArtistListPageViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    /// <summary>The row each realised container shows: a recycled container may have lost its
    /// DataContext by the time it is cleared, and a re-prepared one first leaves its old row.</summary>
    private readonly Dictionary<Control, ArtistGridRow> _shownRows = new();

    private void ShowRow(Control container)
    {
        HideRow(container);
        if (container.DataContext is not ArtistGridRow row || _vm == null) return;
        _shownRows[container] = row;
        foreach (var item in row.Artists) _vm.Photos.Request(item);
    }

    private void HideRow(Control container)
    {
        if (!_shownRows.Remove(container, out var row) || _vm == null) return;
        foreach (var item in row.Artists) _vm.Photos.Cancel(item);
    }

    /// <summary>A new search starts the grid at its top, not wherever the full list was.</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArtistListPageViewModel.Query)) ArtistScroll.Offset = default;
    }

    /// <summary>Scrolls the grid to <paramref name="letter"/>'s first row (or the next letter
    /// in use; see RowIndexForLetter). Internal for tests.</summary>
    internal void JumpToLetter(string letter)
    {
        if (_vm == null) return;
        var row = _vm.RowIndexForLetter(letter);
        if (row < 0) return;
        // The ScrollViewer clamps an offset past the end to the last full screen.
        ArtistScroll.Offset = new Vector(0, row * ArtistListPageViewModel.RowHeight);
    }

    /// <summary>The strip letter under <paramref name="y"/> (strip coordinates), clamped to the strip.</summary>
    private string LetterAt(double y)
    {
        var letters = ArtistIndex.Letters;
        var top = IndexLetters.Bounds.Top;
        var step = IndexLetters.Bounds.Height / letters.Count;
        if (step <= 0) return letters[0];
        return letters[Math.Clamp((int)((y - top) / step), 0, letters.Count - 1)];
    }

    private void Index(PointerEventArgs e)
    {
        var letter = LetterAt(e.GetPosition(IndexStrip).Y);
        // Moves within one letter are many: jump once per letter.
        if (letter == _lastLetter) return;
        _lastLetter = letter;
        JumpToLetter(letter);
    }

    private void OnIndexPressed(object? sender, PointerPressedEventArgs e)
    {
        _indexing = true;
        _lastLetter = null;
        e.Pointer.Capture(IndexStrip);
        // The grid's ScrollViewer would take a vertical slide along the strip as a scroll.
        e.PreventGestureRecognition();
        Index(e);
        e.Handled = true;
    }

    private void OnIndexMoved(object? sender, PointerEventArgs e)
    {
        if (!_indexing) return;
        e.PreventGestureRecognition();
        Index(e);
        e.Handled = true;
    }

    private void OnIndexReleased(object? sender, PointerReleasedEventArgs e)
    {
        _indexing = false;
        e.Handled = true;
    }

    private void OnIndexCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _indexing = false;
}
