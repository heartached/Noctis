using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

    /// <summary>The second field's trimmed text (two-field form), or null when cancelled.</summary>
    public string? SecondResult { get; private set; }

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
            InputBox.Focus();
            InputBox.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    // PillDialogHost animates the close on every path.
    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    /// <summary>Two labelled fields (GitHub #178: album name and album artist).</summary>
    public TextPromptDialog(string title, string firstLabel, string? firstText, string secondLabel, string? secondText,
        string? hint, string confirmLabel) : this(title, firstText, hint, confirmLabel)
    {
        InputLabel.Text = firstLabel;
        InputLabel.IsVisible = true;
        Avalonia.Automation.AutomationProperties.SetName(InputBox, firstLabel);
        SecondLabel.Text = secondLabel;
        SecondLabel.IsVisible = true;
        SecondBox.Text = secondText ?? string.Empty;
        SecondBox.IsVisible = true;
    }

    private void Confirm()
    {
        var text = InputBox.Text?.Trim() ?? string.Empty;
        Result = text;
        SecondResult = SecondBox.IsVisible ? SecondBox.Text?.Trim() ?? string.Empty : null;
        CloseOnce();
    }

    private void OnConfirmClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Confirm();

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = null;
        SecondResult = null;
        CloseOnce();
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
            SecondResult = null;
            CloseOnce();
        }
    }

    /// <summary>Shows the prompt over the main window. Null = cancelled.</summary>
    public static async Task<string?> ShowAsync(string title, string? initialText = null, string? hint = null, string? confirmLabel = null)
    {
        var dialog = new TextPromptDialog(title, initialText, hint, confirmLabel ?? Noctis.Localization.Loc.T("Mobile.OK"));

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

    /// <summary>Shows the two-field prompt over the main window. Null = cancelled.</summary>
    public static async Task<(string First, string Second)?> ShowTwoAsync(string title, string firstLabel, string? firstText,
        string secondLabel, string? secondText, string? hint = null, string? confirmLabel = null)
    {
        if (Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not Window owner)
            return null;

        var dialog = new TextPromptDialog(title, firstLabel, firstText, secondLabel, secondText, hint,
            confirmLabel ?? Noctis.Localization.Loc.T("Mobile.OK"));
        DialogHelper.SizeToOwner(dialog, owner);
        await dialog.ShowDialog(owner);
        return dialog.Result is { } first && dialog.SecondResult is { } second ? (first, second) : null;
    }
}
