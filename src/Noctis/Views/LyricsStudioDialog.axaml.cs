using Avalonia.Controls;
using Avalonia.Input;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsStudioDialog : Window
{
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
        vm.Closed += (_, _) => Close();
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
