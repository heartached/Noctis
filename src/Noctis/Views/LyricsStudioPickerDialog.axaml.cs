using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>Lyrics Studio › Choose songs. Same overlay card and fade/scale open-close as
/// <see cref="AddSongsDialog"/> and <see cref="LyricsBackgroundPickerDialog"/>.</summary>
public partial class LyricsStudioPickerDialog : Window
{
    private bool _closing;

    public LyricsStudioPickerDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LyricsStudioPickerViewModel vm)
                vm.CloseRequested += (_, _) => _ = CloseAnimatedAsync();
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() =>
        {
            DialogOverlay.Opacity = 1;
            DialogCard.RenderTransform = TransformOperations.Parse("scale(1)");
            SearchBox.Focus();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Plays the fade/scale close animation, then closes the window.</summary>
    public async Task CloseAnimatedAsync()
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
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _ = CloseAnimatedAsync();
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>A press on the dimmed backdrop (not the card) closes, like Esc and Cancel.</summary>
    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (ReferenceEquals(e.Source, DialogOverlay)) _ = CloseAnimatedAsync();
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e) => e.Handled = true;
}
