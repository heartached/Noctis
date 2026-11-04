using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// The glass prompt (<see cref="ShellViewModel.Prompt"/>): the pop-ups' glass card
/// (<see cref="MenuGlass"/>) rising out of the bottom edge with the menu's pop (PopMenu.PopFrom /
/// PopIn / PopOut), the dim behind it fading with it, as the Add to Playlist sheet and the album
/// note do. It keeps the prompt while it closes, so it never shrinks away empty; with a name to
/// type it focuses the field (the keyboard comes up) and rides above the keyboard.
/// </summary>
public partial class PromptSheet : UserControl
{
    private ShellViewModel? _shell;
    private IInputPane? _inputPane;
    // Bumped by every open and close: a deferred step of an older run sees it changed and stops.
    private int _run;

    public PromptSheet()
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

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Prompt)) Sync();
    }

    private void Sync()
    {
        if (_shell?.Prompt is { } prompt)
        {
            if (!IsVisible || !IsHitTestVisible || !ReferenceEquals(PromptBody.DataContext, prompt)) Open(prompt);
        }
        else Close();
    }

    /// <summary>The look of what it opened over: on a page painted in its cover's colour the light
    /// glass over a light tint and the dark glass over a dark one (the ⋯ menu's rule), over Now
    /// Playing the dark glass, else the app's theme.</summary>
    internal ThemeVariant? Theme() =>
        _shell?.CurrentPage is ITintedPage { Tint: { HasTint: true } tint } ? (tint.IsLight ? ThemeVariant.Light : ThemeVariant.Dark)
        : _shell is { IsNowPlayingOpen: true, IsQueueOpen: false } ? ThemeVariant.Dark
        : null;

    private void Open(PromptViewModel prompt)
    {
        var run = ++_run;
        PromptScope.RequestedThemeVariant = Theme();
        PromptBody.DataContext = prompt;
        IsVisible = true;
        IsHitTestVisible = true;
        UpdateLayout();

        PopMenu.PopFrom(PromptPop, PromptGlass, PromptBody, new RelativePoint(0.5, 1, RelativeUnit.Relative), PopMenu.SheetPose, Scrim);
        PopMenu.AfterFirstFrame(this, () =>
        {
            if (run != _run) return;
            PopMenu.PopIn(PromptPop, PromptGlass, PromptBody, Scrim);
            if (prompt.HasField)
            {
                // The name selected, ready to be typed over (Apple's rename field).
                PromptField.Focus();
                PromptField.SelectAll();
            }
        });
    }

    /// <summary>The pop reversed: inert at once (a fading card must never take a tap), the
    /// keyboard down, then gone.</summary>
    private void Close()
    {
        if (!IsVisible) return;
        var run = ++_run;
        IsHitTestVisible = false;
        // Focus off the field takes the keyboard down (AccountPage's way, onto a button).
        if (PromptField.IsFocused) PromptCancel.Focus();
        PopMenu.PopOut(PromptPop, PromptGlass, PromptBody, PopMenu.SheetPose, Scrim);
        DispatcherTimer.RunOnce(() =>
        {
            if (run != _run) return;
            IsVisible = false;
            PromptBody.DataContext = null;
            PopMenu.PopDone(PromptPop, PromptGlass, PromptBody, Scrim);
            PromptCard.Margin = default;
        }, TimeSpan.FromMilliseconds(PopMenu.PopCloseMs));
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (PromptBody.DataContext is PromptViewModel prompt) _shell?.ClosePrompt(prompt);
        e.Handled = true;
    }

    /// <summary>Enter on the keyboard does what the first action's pill does (Rename's Save).</summary>
    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || PromptBody.DataContext is not PromptViewModel prompt) return;
        e.Handled = true;
        if (prompt.CanConfirm) prompt.SubmitCommand.Execute(null);
    }

    /// <summary>The card sits on the bottom edge, so the keyboard the name field brings up would
    /// cover it (the Add to Playlist sheet's lesson, S23 10-05): it rides above the keyboard; the
    /// nav-bar padding it already has lies under the keyboard too.</summary>
    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        var height = TopLevel.GetTopLevel(this)?.Bounds.Height ?? 0;
        var covered = e.NewState == InputPaneState.Open && height > 0 ? Math.Max(0, height - e.EndRect.Top) : 0;
        PromptCard.Margin = new Thickness(0, 0, 0, Math.Max(0, covered - PromptCard.Padding.Bottom));
    }
}
