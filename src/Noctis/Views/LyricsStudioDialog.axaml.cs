using System;
using Avalonia.Controls;
using Avalonia.Input;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsStudioDialog : Window
{
    private readonly LyricsStudioViewModel? _vm;
    /// <summary>The Studio's own Close ran (round X, Esc, or on the window's behalf below).</summary>
    private bool _vmClosed;
    private bool _windowClosed;

    public LyricsStudioDialog()
    {
        InitializeComponent();
    }

    public LyricsStudioDialog(LyricsStudioViewModel vm) : this()
    {
        // Confirm is set before DataContext so the panel (which only fills a null Confirm)
        // leaves this owner-bound prompt in place. Tap-mode keys live in the panel.
        vm.Confirm = message => ConfirmationDialog.ShowAsync(this, message);
        DataContext = vm;
        _vm = vm;
        vm.Closed += OnStudioClosed;
    }

    /// <summary>The Studio is done (drafts kept, run stopped): close; PillDialogHost animates it out.</summary>
    private void OnStudioClosed(object? sender, EventArgs e)
    {
        _vmClosed = true;
        if (!_windowClosed) Close();
    }

    /// <summary>
    /// Closed by the window itself (Alt+F4) rather than the round X or Esc: the Studio's own
    /// Close still runs — it stops a run (which otherwise went on unseen with the speech model
    /// loaded) and keeps the open song's draft.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _windowClosed = true;
        base.OnClosed(e);
        if (_vm is not { } vm) return;
        if (!_vmClosed)
        {
            _vmClosed = true;
            vm.CloseCommand.Execute(null);
        }
        vm.Closed -= OnStudioClosed;
    }

    /// <summary>
    /// Esc closes like the round X (reviews are kept as drafts). Not while a run is going — Esc
    /// there is too easy to press by accident to throw away a transcription — and tap mode's
    /// own Esc is handled by the panel first.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled && DataContext is LyricsStudioViewModel { IsRunning: false } vm)
        {
            e.Handled = true;
            vm.CloseCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
