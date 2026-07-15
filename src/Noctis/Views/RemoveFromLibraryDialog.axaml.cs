using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.Helpers;

namespace Noctis.Views;

/// <summary>User's choice from <see cref="RemoveFromLibraryDialog"/>.</summary>
public enum RemoveFromLibraryChoice
{
    Cancel,
    KeepFiles,
    Trash
}

public partial class RemoveFromLibraryDialog : Window
{
    public RemoveFromLibraryChoice Choice { get; private set; } = RemoveFromLibraryChoice.Cancel;

    private bool _closing;

    public RemoveFromLibraryDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Settle to the open state on the next frame so the fade/scale
        // transitions animate it (same pattern as the Settings modal).
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

    public RemoveFromLibraryDialog(int itemCount) : this()
    {
        // "Recycle Bin" on Windows, "Trash" on macOS/Linux — matches each OS's own naming.
        var binName = OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";
        TrashButton.Content = $"Move to {binName}";

        var noun = itemCount == 1 ? "track" : "tracks";
        MessageText.Text = $"Remove {itemCount} {noun} from your library?";
        SubText.Text = $"“Keep Files” leaves the files on disk. “Move to {binName}” also sends the files to the {binName}.";
    }

    private void OnTrashClick(object? sender, RoutedEventArgs e)
    {
        Choice = RemoveFromLibraryChoice.Trash;
        _ = CloseAnimatedAsync();
    }

    private void OnKeepClick(object? sender, RoutedEventArgs e)
    {
        Choice = RemoveFromLibraryChoice.KeepFiles;
        _ = CloseAnimatedAsync();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Choice = RemoveFromLibraryChoice.Cancel;
        _ = CloseAnimatedAsync();
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    public static async Task<RemoveFromLibraryChoice> ShowAsync(int itemCount)
    {
        var dialog = new RemoveFromLibraryDialog(itemCount);

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else
        {
            return RemoveFromLibraryChoice.Cancel;
        }

        return dialog.Choice;
    }
}
