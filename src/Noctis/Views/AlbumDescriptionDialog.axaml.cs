using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// The album's description pop-up (owner 10-08 redesign): the rounded pill pop-up with the
/// blurred app behind, a compact header (cover, title, artist, fact chips) and one filled
/// editor. Opened from "more" it shows the text ready to read, from "Edit Description" / "Add
/// a description" with the caret in it. Save closes it (the view model's
/// AlbumDescriptionSaved); Cancel, Esc and Alt+F4 close it and drop the edit.
/// </summary>
public partial class AlbumDescriptionDialog : Window
{
    private AlbumDetailViewModel? _vm;
    private bool _saved;

    public AlbumDescriptionDialog()
    {
        InitializeComponent();
        // Ctrl+Enter anywhere in the card saves. Tunnel, so the editor never turns it into a
        // new line; PillDialogHost's own tunnel handler sits above this one and stops keys
        // while the close plays, so a late Ctrl+Enter can't save twice.
        CardRoot.AddHandler(KeyDownEvent, OnCardKeyDown, RoutingStrategies.Tunnel);
        DescriptionEditor.TextChanged += (_, _) => UpdateFooter();
    }

    public AlbumDescriptionDialog(AlbumDetailViewModel vm) : this()
    {
        DataContext = vm;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.AlbumDescriptionSaved -= OnSaved;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _vm = DataContext as AlbumDetailViewModel;
        if (_vm != null)
        {
            _vm.AlbumDescriptionSaved += OnSaved;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }
        FactChips.ItemsSource = _vm is null ? null : DescriptionDialogs.AlbumChips(_vm.Album);
        UpdateFooter();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName == nameof(AlbumDetailViewModel.Album))
            FactChips.ItemsSource = DescriptionDialogs.AlbumChips(_vm.Album);
        else if (e.PropertyName == nameof(AlbumDetailViewModel.AlbumDescriptionDialogText))
            UpdateFooter();
    }

    protected override void OnOpened(EventArgs e)
    {
        // Before base.OnOpened: PillDialogHost reads ContentReady in the Opened event it
        // raises, and holds the fade until the cover has decoded (within its budget).
        DialogHost.ContentReady = DescriptionDialogs.WhenImagesShown(CoverImage);
        base.OnOpened(e);
        if (_vm?.IsAlbumDescriptionEditing == true)
            Dispatcher.UIThread.Post(() => DescriptionDialogs.FocusAtEnd(DescriptionEditor), DispatcherPriority.Loaded);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_vm != null)
        {
            _vm.AlbumDescriptionSaved -= OnSaved;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            // Closed without saving: drop the edit, so the page never holds a half-typed text.
            if (!_saved && !_vm.SaveAlbumDescriptionEditCommand.IsRunning)
                _vm.CancelAlbumDescriptionEditCommand.Execute(null);
        }
        base.OnClosed(e);
    }

    private void OnSaved(object? sender, EventArgs e)
    {
        _saved = true;
        Close();
    }

    private void UpdateFooter()
    {
        var text = DescriptionEditor.Text ?? string.Empty;
        CountText.Text = DescriptionDialogs.FormatCount(text,
            removesOnSave: _vm?.HasAlbumDescriptionDialogText == true);
        ClearButton.IsVisible = text.Length > 0;
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        DescriptionEditor.Clear();
        DescriptionEditor.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (!DescriptionDialogs.IsSaveGesture(e)) return;
        e.Handled = true;
        var save = _vm?.SaveAlbumDescriptionEditCommand;
        if (save?.CanExecute(null) == true) save.Execute(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Esc closes like Cancel (the host animates it out); not while a save is in flight.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            if (_vm?.SaveAlbumDescriptionEditCommand.IsRunning != true) Close();
            return;
        }
        base.OnKeyDown(e);
    }

    public static async Task ShowAsync(AlbumDetailViewModel vm)
    {
        var dialog = new AlbumDescriptionDialog(vm);

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
    }
}

/// <summary>What the album and playlist description pop-ups share: the fact chips, the live
/// count, the save gesture and the cover-ready signal.</summary>
internal static class DescriptionDialogs
{
    /// <summary>Album facts as chips: year · genre · N songs · quality · label, each only when known.</summary>
    internal static IReadOnlyList<string> AlbumChips(Album? album)
    {
        var chips = new List<string>();
        if (album is null) return chips;
        if (album.Year > 0) chips.Add(album.Year.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(album.Genre) && !string.Equals(album.Genre.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase))
            chips.Add(album.Genre.Trim());
        chips.Add(SongsText(album.TrackCount));
        if (!string.IsNullOrEmpty(album.AudioQualityBadge)) chips.Add(album.AudioQualityBadge);
        if (album.HasLabelName) chips.Add(album.LabelName);
        return chips;
    }

    /// <summary>Playlist facts as chips: N songs · duration · size, each only when known.</summary>
    internal static IReadOnlyList<string> PlaylistChips(PlaylistViewModel? playlist)
    {
        var chips = new List<string>();
        if (playlist is null) return chips;
        chips.Add(SongsText(playlist.TrackCount));
        if (playlist.TrackCount > 0 && !string.IsNullOrWhiteSpace(playlist.TotalDuration)) chips.Add(playlist.TotalDuration);
        if (playlist.TrackCount > 0 && !string.IsNullOrWhiteSpace(playlist.TotalSize)) chips.Add(playlist.TotalSize);
        return chips;
    }

    internal static string SongsText(int count)
        => count == 1 ? Loc.T("DescriptionDialog.Song") : Loc.T("DescriptionDialog.Songs", count);

    /// <summary>
    /// The footer's live count of what Save would store (the trimmed text): "1,234 characters ·
    /// 210 words". Empty: "No description", or "Saving removes the description" when there is
    /// one to remove. Characters are counted as the reader sees them (an emoji is one).
    /// </summary>
    internal static string FormatCount(string? text, bool removesOnSave)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return Loc.T(removesOnSave ? "DescriptionDialog.RemovesOnSave" : "DescriptionDialog.Empty");
        var chars = new StringInfo(trimmed).LengthInTextElements;
        var words = CountWords(trimmed);
        var charText = chars == 1 ? Loc.T("DescriptionDialog.Character") : Loc.T("DescriptionDialog.Characters", chars);
        var wordText = words == 1 ? Loc.T("DescriptionDialog.Word") : Loc.T("DescriptionDialog.Words", words);
        return charText + " · " + wordText;
    }

    internal static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Ctrl+Enter (Cmd+Enter on macOS). Plain Enter is a new line in the editor.</summary>
    internal static bool IsSaveGesture(KeyEventArgs e)
        => e.Key == Key.Enter && e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta;

    internal static void FocusAtEnd(TextBox editor)
    {
        editor.Focus();
        editor.CaretIndex = editor.Text?.Length ?? 0;
    }

    /// <summary>
    /// Completes once every shown cover image has its bitmap (an image that is hidden, has no
    /// path, or already shows one does not count). PillDialogHost.ContentReady: the card fades
    /// in with the cover in place instead of over an empty well that then pops in. A decode
    /// that never lands is covered by the host's wait budget.
    /// </summary>
    internal static Task WhenImagesShown(params CachedImage[] images)
    {
        var pending = images
            .Where(i => i.IsEffectivelyVisible && !string.IsNullOrEmpty(i.SourcePath) && i.Source is null)
            .Select(WhenSourceSet)
            .ToList();
        return pending.Count == 0 ? Task.CompletedTask : Task.WhenAll(pending);
    }

    private static Task WhenSourceSet(Image image)
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != Image.SourceProperty || image.Source is null) return;
            image.PropertyChanged -= OnChanged;
            shown.TrySetResult();
        }
        image.PropertyChanged += OnChanged;
        return shown.Task;
    }
}
