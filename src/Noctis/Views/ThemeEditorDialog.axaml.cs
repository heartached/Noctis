using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class ThemeEditorDialog : Window
{
    public CustomThemeDefinition? Result { get; private set; }

    private bool _closing;

    public ThemeEditorDialog()
    {
        InitializeComponent();
    }

    public ThemeEditorDialog(ThemeEditorViewModel vm) : this()
    {
        DataContext = vm;
        vm.Saved += def => { Result = def; _ = CloseAnimatedAsync(); };
        vm.Cancelled += () => { Result = null; _ = CloseAnimatedAsync(); };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Settle to the open state on the next frame so the fade/scale
        // transitions animate it (same pattern as the create-playlist dialogs).
        Dispatcher.UIThread.Post(() =>
        {
            DialogOverlay.Opacity = 1;
            DialogCard.RenderTransform = TransformOperations.Parse("scale(1)");
            NameTextBox.Focus();
        }, DispatcherPriority.Loaded);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_closing)
        {
            e.Handled = true;
            (DataContext as ThemeEditorViewModel)?.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Plays the fade/scale close animation, then closes the window.</summary>
    private async Task CloseAnimatedAsync()
    {
        if (_closing) return;
        _closing = true;
        DialogOverlay.Opacity = 0;
        DialogCard.RenderTransform = TransformOperations.Parse("scale(0.96)");
        await Task.Delay(200);
        Close();
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
    }
}
