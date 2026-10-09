using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctis.Helpers;
using Noctis.Localization;

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
    /// <summary>The answer; the first button wins (PillDialogHost absorbs later closes).</summary>
    public RemoveFromLibraryChoice Choice { get; private set; } = RemoveFromLibraryChoice.Cancel;

    private bool _answered;

    public RemoveFromLibraryDialog()
    {
        InitializeComponent();
    }

    public RemoveFromLibraryDialog(int itemCount) : this()
    {
        // "Recycle Bin" on Windows, "Trash" on macOS/Linux — matches each OS's own naming.
        // Localized (these used to be English literals that overwrote the translated XAML).
        var windows = OperatingSystem.IsWindows();
        var binName = Loc.T(windows ? "RemoveFromLibrary.RecycleBin" : "RemoveFromLibrary.Trash");
        TrashButton.Content = Loc.T(windows ? "RemoveFromLibrary.MoveRecycleBin" : "RemoveFromLibrary.MoveTrash");

        MessageText.Text = itemCount == 1
            ? Loc.T("RemoveFromLibrary.QuestionOne")
            : Loc.T("RemoveFromLibrary.QuestionMany", itemCount);
        SubText.Text = Loc.T(itemCount == 1 ? "RemoveFromLibrary.DetailOne" : "RemoveFromLibrary.DetailMany", binName);
    }

    /// <summary>Records the answer and closes; PillDialogHost plays the close once.</summary>
    private void Answer(RemoveFromLibraryChoice choice)
    {
        if (_answered) return;
        _answered = true;
        Choice = choice;
        Close();
    }

    private void OnTrashClick(object? sender, RoutedEventArgs e) => Answer(RemoveFromLibraryChoice.Trash);

    private void OnKeepClick(object? sender, RoutedEventArgs e) => Answer(RemoveFromLibraryChoice.KeepFiles);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Answer(RemoveFromLibraryChoice.Cancel);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape answers Cancel, as in the other pill pop-ups.
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Answer(RemoveFromLibraryChoice.Cancel);
            return;
        }
        base.OnKeyDown(e);
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
