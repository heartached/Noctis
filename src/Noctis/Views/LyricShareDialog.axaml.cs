using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricShareDialog : Window
{
    // One save/copy/export at a time: a second click while the first still renders (or its
    // picker is up) would render and ask again.
    private bool _busy;

    /// <summary>A save or copy is in flight (tests).</summary>
    internal bool IsBusy => _busy;

    public LyricShareDialog()
    {
        InitializeComponent();
        // Rows: a tap picks or drops the line, a double-click edits it in place. Handled on
        // the list so the row template stays plain markup.
        LineItems.AddHandler(TappedEvent, OnLineTapped);
        LineItems.AddHandler(DoubleTappedEvent, OnLineDoubleTapped);
        LineItems.AddHandler(KeyDownEvent, OnEditKeyDown);
        LineItems.AddHandler(KeyDownEvent, OnLineKeyDown);
        LineItems.AddHandler(LostFocusEvent, OnEditLostFocus);
    }

    public LyricShareDialog(LyricShareViewModel vm) : this()
    {
        DataContext = vm;
        vm.ScrollToLineRequested += OnScrollToLine;
        vm.AnimatedFrameRendered += OnAnimatedFrameRendered;
        Closed += (_, _) =>
        {
            vm.ScrollToLineRequested -= OnScrollToLine;
            vm.AnimatedFrameRendered -= OnAnimatedFrameRendered;
            vm.Detach();
        };
    }

    /// <summary>The live-preview WriteableBitmap is mutated in place each frame; the
    /// Image doesn't know, so repaint it explicitly.</summary>
    private void OnAnimatedFrameRendered() => AnimatedPreviewImage.InvalidateVisual();

    private LyricShareViewModel? Vm => DataContext as LyricShareViewModel;

    /// <summary>Brings the currently-playing lyric line into view during playback sync.</summary>
    private void OnScrollToLine(int index)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (index < 0 || index >= LineItems.ItemCount)
                return;
            LineItems.ContainerFromIndex(index)?.BringIntoView();
        });
    }

    private static SelectableLyricLine? LineOf(object? source)
        => (source as StyledElement)?.DataContext as SelectableLyricLine;

    private static bool IsInEditBox(object? source)
        => source is Visual v && (v is TextBox || v.FindAncestorOfType<TextBox>() != null);

    private void OnLineTapped(object? sender, TappedEventArgs e)
    {
        if (IsInEditBox(e.Source) || LineOf(e.Source) is not { IsEditing: false } line || Vm is not { } vm)
            return;
        vm.ToggleLine(line);
        e.Handled = true;
    }

    private void OnLineDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsInEditBox(e.Source) || LineOf(e.Source) is not { } line || Vm is not { } vm)
            return;
        e.Handled = true;
        StartEdit(vm, line);
    }

    private void StartEdit(LyricShareViewModel vm, SelectableLyricLine line)
    {
        vm.BeginEdit(line);
        Dispatcher.UIThread.Post(() =>
        {
            var index = vm.Lines.IndexOf(line);
            if (index < 0 || LineItems.ContainerFromIndex(index) is not { } row) return;
            if (row.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The focusable row (Panel.sl-line) of line <paramref name="index"/>.</summary>
    private Panel? RowAt(int index)
        => LineItems.ContainerFromIndex(index) is { } container
            ? container.GetVisualDescendants().OfType<Panel>().FirstOrDefault(p => p.Classes.Contains("sl-line"))
            : null;

    /// <summary>
    /// Keyboard access to the lines (they were mouse-only: no row could take focus, so Tab
    /// skipped the list and nothing could be picked or edited without a pointer).
    /// Space/Enter pick or drop the focused line, F2 edits it, Up/Down/Home/End move along.
    /// </summary>
    private void OnLineKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || IsInEditBox(e.Source) || e.Source is not Panel row || !row.Classes.Contains("sl-line")
            || LineOf(row) is not { } line || Vm is not { } vm)
            return;
        var index = vm.Lines.IndexOf(line);
        int target;
        switch (e.Key)
        {
            case Key.Space:
            case Key.Enter:
                vm.ToggleLine(line);
                e.Handled = true;
                return;
            case Key.F2:
                StartEdit(vm, line);
                e.Handled = true;
                return;
            case Key.Up: target = index - 1; break;
            case Key.Down: target = index + 1; break;
            case Key.Home: target = 0; break;
            case Key.End: target = vm.Lines.Count - 1; break;
            default: return;
        }
        e.Handled = true;
        if (target < 0 || target >= vm.Lines.Count || target == index) return;
        LineItems.ContainerFromIndex(target)?.BringIntoView();
        RowAt(target)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>Enter keeps the edit, Esc puts the line back (and does not close the dialog).</summary>
    private void OnEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox box || LineOf(box) is not { IsEditing: true } line) return;
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        line.EndEdit(keep: e.Key == Key.Enter);
        e.Handled = true;
        // The box hides; give the focus back to its row so the keyboard is not stranded.
        if (Vm is { } vm && RowAt(vm.Lines.IndexOf(line)) is { } row)
            row.Focus(NavigationMethod.Directional);
    }

    private void OnEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox box && LineOf(box) is { IsEditing: true } line)
            line.EndEdit(keep: true);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Esc stops a running video export first (the dialog stays, saying Cancelled); when
        // nothing runs it closes, animated like every pill pop-up.
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (Vm is { IsRendering: true } vm) vm.CancelExport();
            else Close();
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Any close (X, Esc, Alt+F4, the owner going away) stops a running export
    /// right away, not only once the close animation has played.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Vm?.CancelExport();
        base.OnClosing(e);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        if (_busy || Vm is not { HasSelection: true } vm) return;
        _busy = true;
        try
        {
            // Re-render at export resolution — the preview is a small bitmap. It renders while
            // the picker is up: awaited first, the picker opened 0.3-0.9 s after the click.
            var png = vm.RenderExportPngAsync();
            var status = await PngExportHelper.SavePngAsync(this, png, vm.SuggestedFileName);
            if (status != null)
                vm.ReportStatus(status);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricShareDialog] Save failed: {ex.Message}");
            Vm?.ReportStatus(Localization.Loc.T("ShareLyrics.SaveFailed"));
        }
        finally { _busy = false; }
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        if (_busy || Vm is not { HasSelection: true } vm) return;
        _busy = true;
        try
        {
            // Same full-resolution re-render as Save — the preview is only a small bitmap.
            if (await vm.RenderExportPngAsync() is not { } png) return;
            var status = await PngExportHelper.CopyPngAsync(this, png, vm.SuggestedFileName);
            if (status != null)
                vm.ReportStatus(status);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricShareDialog] Copy failed: {ex.Message}");
            Vm?.ReportStatus(Localization.Loc.T("ShareLyrics.CopyFailed"));
        }
        finally { _busy = false; }
    }

    private async void OnSaveVideoClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        if (_busy || Vm is not { CanExportVideo: true } vm) return;
        _busy = true;
        try
        {
            var path = await MediaExportHelper.PickMp4PathAsync(this, vm.SuggestedVideoFileName);
            if (path is null)
                return; // cancelled

            // The export guards itself (IsRendering); Copy / Save Card stay usable meanwhile.
            _busy = false;
            var status = await vm.ExportClipAsync(path);
            vm.ReportStatus(status);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricShareDialog] Video export failed: {ex.Message}");
            Vm?.ReportStatus(Localization.Loc.T("ShareLyrics.VideoFailed"));
        }
        finally { _busy = false; }
    }

    /// <summary>Picks a picture or clip for the Lyrics Page background.</summary>
    private async void OnChooseBackgroundClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        if (_busy || Vm is not { } vm) return;
        _busy = true;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localization.Loc.T("Picker.ChooseShareBackground"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(Localization.Loc.T("Picker.ImageOrVideo"))
                    {
                        Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.mp4", "*.mov", "*.webm", "*.m4v" },
                    },
                    new FilePickerFileType(Localization.Loc.T("Picker.AllFiles")) { Patterns = new[] { "*" } },
                },
            });
            if (files.Count == 0) return;
            // A pick that is not a local file (a cloud entry with no path) cannot be read by ffmpeg.
            if (files[0].TryGetLocalPath() is not { } path)
            {
                vm.ReportStatus(Localization.Loc.T("ShareLyrics.BackgroundNotLocal"));
                return;
            }
            await vm.SetPageBackgroundFileAsync(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricShareDialog] Background pick failed: {ex.Message}");
        }
        finally { _busy = false; }
    }

    public static async Task ShowAsync(LyricShareViewModel vm)
    {
        var dialog = new LyricShareDialog(vm);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }
}
