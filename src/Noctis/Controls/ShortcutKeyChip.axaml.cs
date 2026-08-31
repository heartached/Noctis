using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctis.ViewModels;

namespace Noctis.Controls;

/// <summary>
/// Key-cap chip for one Shortcuts row. Click to record; the next chord is handed to
/// <see cref="ShortcutRowViewModel.TryAssign"/>. Every key is swallowed while recording
/// so nothing leaks to the page (arrow keys would otherwise scroll the settings list,
/// Escape would close the modal). MainWindow's tunnelling shortcut handler stands down
/// while any row records — see <c>OnGlobalShortcutKeyDown</c>.
/// </summary>
public partial class ShortcutKeyChip : UserControl
{
    private ShortcutRowViewModel? Vm => DataContext as ShortcutRowViewModel;

    public ShortcutKeyChip()
    {
        InitializeComponent();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Vm is not { } vm) return;
        Focus();
        if (!vm.IsRecording) vm.BeginRecordCommand.Execute(null);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Vm is { IsRecording: true } vm)
        {
            e.Handled = vm.TryAssign(e.Key, e.KeyModifiers);
            return;
        }

        // Not recording: Enter / Space start a recording from the keyboard.
        if (e.Key is Key.Return or Key.Space && e.KeyModifiers == KeyModifiers.None && Vm is { } idle)
        {
            idle.BeginRecordCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        // The release of the recorded chord must not reach a Button underneath.
        if (Vm is { } vm && (vm.IsRecording || e.Key is Key.Return or Key.Space))
        {
            e.Handled = true;
            return;
        }
        base.OnKeyUp(e);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (Vm is { IsRecording: true } vm) vm.CancelRecordCommand.Execute(null);
    }
}
