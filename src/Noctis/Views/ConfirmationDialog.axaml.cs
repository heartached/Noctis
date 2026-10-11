using Avalonia;
using Avalonia.Controls;
using Noctis.Helpers;

namespace Noctis.Views;

/// <summary>A richer confirmation: title, detail bullets, a quiet note and an optional checkbox.</summary>
public sealed record ConfirmationRequest(
    string Message,
    string? Title = null,
    IReadOnlyList<string>? Details = null,
    string? Note = null,
    string? ConfirmText = null,
    string? OptionText = null,
    bool OptionChecked = false,
    double Width = 380);

/// <summary>What the user chose; <see cref="OptionChecked"/> is the checkbox state.</summary>
public readonly record struct ConfirmationResult(bool Confirmed, bool OptionChecked);

public partial class ConfirmationDialog : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmationDialog()
    {
        InitializeComponent();
    }

    public ConfirmationDialog(string message) : this()
    {
        MessageText.Text = message;
    }

    public ConfirmationDialog(ConfirmationRequest request) : this(request.Message)
    {
        // Width is the whole card as before; the content sits inside the 24px side margins.
        DialogCard.Width = Math.Max(0, request.Width - 48);
        if (!string.IsNullOrWhiteSpace(request.Title)) { TitleText.Text = request.Title; TitleText.IsVisible = true; }
        if (request.Details is { Count: > 0 } details) { DetailsList.ItemsSource = details; DetailsList.IsVisible = true; }
        if (!string.IsNullOrWhiteSpace(request.Note)) { NoteText.Text = request.Note; NoteText.IsVisible = true; }
        if (!string.IsNullOrWhiteSpace(request.ConfirmText)) ConfirmButton.Content = request.ConfirmText;
        if (!string.IsNullOrWhiteSpace(request.OptionText))
        {
            OptionCheck.Content = request.OptionText;
            OptionCheck.IsChecked = request.OptionChecked;
            OptionCheck.IsVisible = true;
        }
    }

    /// <summary>Shows <paramref name="request"/> over the main window.</summary>
    public static async Task<ConfirmationResult> ShowAsync(ConfirmationRequest request)
    {
        if (Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not Window owner)
            return new ConfirmationResult(false, false);
        var dialog = new ConfirmationDialog(request);
        DialogHelper.SizeToOwner(dialog, owner);
        await dialog.ShowDialog(owner);
        return new ConfirmationResult(dialog.Confirmed, dialog.OptionCheck.IsChecked == true);
    }

    private void OnConfirmClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    /// <summary>Confirmation owned by another dialog (a modal on top of a modal needs its own owner).</summary>
    public static async Task<bool> ShowAsync(Window owner, string message)
    {
        var dialog = new ConfirmationDialog(message);
        DialogHelper.SizeToOwner(dialog, owner);
        await dialog.ShowDialog(owner);
        return dialog.Confirmed;
    }

    public static async Task<bool> ShowAsync(string message)
    {
        var dialog = new ConfirmationDialog(message);

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else
        {
            return false;
        }

        return dialog.Confirmed;
    }
}
