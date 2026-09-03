using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.Helpers;

namespace Noctis.Views;

/// <summary>
/// Single-field prompt ("Folder name", "Rename folder") in the ConfirmationDialog chrome.
/// <see cref="ShowAsync"/> returns the trimmed text, or null when cancelled.
/// </summary>
public partial class TextPromptDialog : Window
{
    public string? Result { get; private set; }

    private bool _closing;

    public TextPromptDialog()
    {
        InitializeComponent();
    }

    public TextPromptDialog(string title, string? initialText, string? hint, string confirmLabel) : this()
    {
        TitleText.Text = title;
        InputBox.Text = initialText ?? string.Empty;
        ConfirmButton.Content = confirmLabel;
        if (!string.IsNullOrWhiteSpace(hint))
        {
            HintText.Text = hint;
            HintText.IsVisible = true;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() =>
        {
            DialogOverlay.Opacity = 1;
            DialogCard.RenderTransform = TransformOperations.Parse("scale(1)");
            InputBox.Focus();
            InputBox.SelectAll();
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

    private void Confirm()
    {
        var text = InputBox.Text?.Trim() ?? string.Empty;
        Result = text;
        _ = CloseAnimatedAsync();
    }

    private void OnConfirmClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Confirm();

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = null;
        _ = CloseAnimatedAsync();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Confirm();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Result = null;
            _ = CloseAnimatedAsync();
        }
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e) => e.Handled = true;

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    /// <summary>Shows the prompt over the main window. Null = cancelled.</summary>
    public static async Task<string?> ShowAsync(string title, string? initialText = null, string? hint = null, string confirmLabel = "OK")
    {
        var dialog = new TextPromptDialog(title, initialText, hint, confirmLabel);

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else
        {
            return null;
        }

        return dialog.Result;
    }
}
