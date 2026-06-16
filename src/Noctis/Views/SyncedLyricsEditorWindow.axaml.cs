using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// Roomy pop-out editor for synced lyrics. Hosts the same per-line editor as the
/// metadata window's Synced Lyrics tab, but in a wide window so the timestamp,
/// lyric, and nudge/clear buttons all fit without clipping. It binds to the same
/// <see cref="MetadataViewModel"/>, so edits flow straight back and persist when
/// the metadata window is saved.
/// </summary>
public partial class SyncedLyricsEditorWindow : Window
{
    public SyncedLyricsEditorWindow()
    {
        InitializeComponent();
        KeyDown += OnDialogKeyDown;
    }

    public SyncedLyricsEditorWindow(MetadataViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    public static async Task ShowAsync(MetadataViewModel vm, Window owner)
    {
        var dialog = new SyncedLyricsEditorWindow(vm);
        await dialog.ShowDialog(owner);
    }
}
