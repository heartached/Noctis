using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    public RemoveFromLibraryDialog()
    {
        InitializeComponent();
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
        Close();
    }

    private void OnKeepClick(object? sender, RoutedEventArgs e)
    {
        Choice = RemoveFromLibraryChoice.KeepFiles;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Choice = RemoveFromLibraryChoice.Cancel;
        Close();
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
