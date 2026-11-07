using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// The Add to Playlist list (<see cref="ShellViewModel.IsPlaylistPickerOpen"/>): the menu's glass
/// card (<see cref="MenuGlass"/>) rising out of the bottom edge with the menu's pop, and dropping
/// back the same way, the dim behind it fading with it. The card keeps the item's content while it
/// closes, so it never shrinks away empty.
/// </summary>
public partial class ContextSheet : UserControl
{
    private IInputPane? _inputPane;
    private ShellViewModel? _shell;
    // Bumped by every open and close: a deferred step of an older run sees it changed and stops.
    private int _run;

    public ContextSheet()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell != null) _shell.PropertyChanged -= OnShellChanged;
        _shell = DataContext as ShellViewModel;
        if (_shell != null) _shell.PropertyChanged += OnShellChanged;
        Sync();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsPlaylistPickerOpen) or nameof(ShellViewModel.Sheet)) Sync();
    }

    private void Sync()
    {
        if (_shell is { IsPlaylistPickerOpen: true, Sheet: { } sheet })
        {
            if (!IsVisible || !IsHitTestVisible || !ReferenceEquals(SheetContent.Content, sheet)) Open(sheet);
        }
        else Close();
    }

    private void Open(ContextSheetViewModel sheet)
    {
        var run = ++_run;
        // The theme of the menu it was picked in (Now Playing's dark glass, or the page's).
        SheetScope.RequestedThemeVariant = (this.GetVisualParent() as Panel)?.Children.OfType<PopMenu>().FirstOrDefault()?.Scope.RequestedThemeVariant;
        SheetContent.Content = sheet;
        IsVisible = true;
        IsHitTestVisible = true;
        UpdateLayout();

        PopMenu.PopFrom(SheetPop, SheetGlass, SheetBody, new RelativePoint(0.5, 1, RelativeUnit.Relative), PopMenu.SheetPose, Scrim);
        PopMenu.AfterFirstFrame(this, () =>
        {
            if (run != _run) return;
            PopMenu.PopIn(SheetPop, SheetGlass, SheetBody, Scrim);
        });
    }

    /// <summary>The pop reversed: inert at once (a fading card must never take a tap), then gone.</summary>
    private void Close()
    {
        if (!IsVisible) return;
        var run = ++_run;
        IsHitTestVisible = false;
        PopMenu.PopOut(SheetPop, SheetGlass, SheetBody, PopMenu.SheetPose, Scrim);
        DispatcherTimer.RunOnce(() =>
        {
            if (run != _run) return;
            IsVisible = false;
            // Kept, as the pop menu's: the next sheet of the same kind recycles the built view.
            PopMenu.PopDone(SheetPop, SheetGlass, SheetBody, Scrim);
        }, TimeSpan.FromMilliseconds(PopMenu.PopCloseMs));
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        (DataContext as ShellViewModel)?.CloseSheet();
        e.Handled = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _inputPane = TopLevel.GetTopLevel(this)?.InputPane;
        if (_inputPane != null) _inputPane.StateChanged += OnInputPaneChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_inputPane != null) _inputPane.StateChanged -= OnInputPaneChanged;
        _inputPane = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// The card sits on the bottom edge, so the keyboard opened by the playlist picker's "New
    /// playlist" box covered all of it, the box included (S23, 2026-10-05). Lift the card above
    /// the keyboard; the nav-bar padding it already has lies under the keyboard too.
    /// </summary>
    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        var height = TopLevel.GetTopLevel(this)?.Bounds.Height ?? 0;
        var covered = e.NewState == InputPaneState.Open && height > 0 ? Math.Max(0, height - e.EndRect.Top) : 0;
        SheetCard.Margin = new Thickness(0, 0, 0, Math.Max(0, covered - SheetCard.Padding.Bottom));
    }
}
