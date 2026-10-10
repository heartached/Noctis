using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// Settings › Appearance › Lyrics Background Video › Modify, and Lyrics Studio's "Background
/// video". The rounded pill pop-up (owner 10-08): <see cref="Controls.PillDialogHost"/> draws
/// the blurred app behind and animates every close; the view model is set by the caller as
/// DataContext.
/// </summary>
public partial class LyricsBackgroundPickerDialog : Window
{
    /// <summary>Card height when the window has room for it.</summary>
    internal const double PreferredCardHeight = 640;
    /// <summary>The smallest the card gets on a very short window.</summary>
    internal const double MinCardHeight = 420;
    /// <summary>Space kept between the card and the window's top and bottom edges.</summary>
    private const double CardInset = 48;

    private LyricsBackgroundPickerViewModel? _vm;

    public LyricsBackgroundPickerDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as LyricsBackgroundPickerViewModel);
        FitCard();
    }

    private void Attach(LyricsBackgroundPickerViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm != null)
        {
            _vm.PickFile = null;
            _vm.CloseRequested -= OnCloseRequested;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _vm = vm;
        if (vm == null) return;
        vm.PickFile = PickVideoAsync;
        vm.CloseRequested += OnCloseRequested;
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    // PillDialogHost turns this into the animated close (once, however many paths ask).
    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    /// <summary>The YouTube panel opening puts the caret in its link box, ready to paste;
    /// folding it takes the caret back out, or Enter in the hidden link box would still run
    /// its Download key binding.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LyricsBackgroundPickerViewModel.ShowYouTube) || _vm is null) return;
        if (!_vm.ShowYouTube)
        {
            if (FocusManager?.GetFocusedElement() is Visual focused && YouTubePanel.IsVisualAncestorOf(focused))
                FocusManager.Focus(null); // Avalonia 12: Focus(null) clears
            return;
        }
        if (_vm.IsDownloading) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is { ShowYouTube: true } && LinkBox.IsEffectivelyVisible) LinkBox.Focus();
        }, DispatcherPriority.Loaded);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty) FitCard();
    }

    /// <summary>A fixed card height, so the card does not jump while a search fills or empties
    /// the list (it is centred, so every height change moved the header); the list takes what
    /// is left, and gives room to the YouTube panel when it folds out.</summary>
    private void FitCard()
    {
        // ClientSize already changes inside InitializeComponent, before the field is set.
        if (CardRoot is not { } card) return;
        var room = ClientSize.Height - CardInset;
        card.Height = room > 0 ? Math.Clamp(room, MinCardHeight, PreferredCardHeight) : PreferredCardHeight;
    }

    private async Task<string?> PickVideoAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localization.Loc.T("LyricsVideo.PickTitle"),
            AllowMultiple = false,
            FileTypeFilter = LyricsBackgroundOverrides.MediaFilter,
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the same way Close does (the close cancels a running download).
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_vm != null) _vm.CloseCommand.Execute(null);
            else Close();
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Every close (Close, Esc, Alt+F4, the owner going away) stops a running
    /// YouTube download, so nothing lands behind a closed dialog.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _vm?.CancelForClose();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Attach(null);
    }
}
