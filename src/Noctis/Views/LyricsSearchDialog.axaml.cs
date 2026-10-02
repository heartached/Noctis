using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsSearchDialog : Window
{
    private bool _closing;

    public LyricsSearchDialog()
    {
        InitializeComponent();
    }

    public LyricsSearchDialog(LyricsSearchViewModel vm) : this()
    {
        DataContext = vm;
        vm.Closed += (_, _) => _ = CloseAnimatedAsync();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Settle to the open state on the next frame so the fade/scale transitions
        // animate it (same pattern as RemoveFromLibraryDialog and the Settings modal).
        Dispatcher.UIThread.Post(() =>
        {
            DialogOverlay.Opacity = 1;
            DialogCard.RenderTransform = TransformOperations.Parse("scale(1)");
        }, DispatcherPriority.Loaded);
    }

    private async Task CloseAnimatedAsync()
    {
        if (_closing) return;
        _closing = true;
        DialogOverlay.Opacity = 0;
        DialogCard.RenderTransform = TransformOperations.Parse("scale(0.96)");
        await Task.Delay(200);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the same way the header X does (and cancels a running search).
        if (e.Key == Key.Escape && DataContext is LyricsSearchViewModel vm)
        {
            e.Handled = true;
            vm.CloseCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
