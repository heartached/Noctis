using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// The Lyrics Studio surface shared by the per-song dialog and the sidebar page. Owns the
/// tap-mode keys (Space stamps the next word, Esc leaves) and, when the host set none, a
/// confirmation prompt over the main window.
/// </summary>
public partial class LyricsStudioPanel : UserControl
{
    /// <summary>Child lookups for <see cref="OnTitleCellLayoutUpdated"/>, resolved once per cell and
    /// stashed in Tag: LayoutUpdated fires after every layout pass and a cell's children never change.</summary>
    private sealed record TitleCellChildren(Control Title, Control? ExplicitBadge);

    /// <summary>
    /// A title cell is an Auto,Auto grid so the E badge hugs the title; Auto columns measure
    /// unbounded, so the title's MaxWidth is capped to the cell minus the badge here and
    /// TextTrimming does the rest (the AddSongsDialog recipe). The cell must take its width from
    /// its parent (stretched), never from the title it caps. A cell that is not laid out (its view
    /// hidden) is left alone: its 0 width would cap the title to nothing until the next pass.
    /// </summary>
    private void OnTitleCellLayoutUpdated(object? sender, System.EventArgs e)
    {
        if (sender is not Grid cell || !cell.IsEffectivelyVisible || cell.Bounds.Width <= 0) return;
        if (cell.Tag is not TitleCellChildren children)
        {
            // Suffix match: names are unique per scope, so a second cell outside a template is "Working…".
            var title = cell.Children.FirstOrDefault(c => c.Name?.EndsWith("TitleBox") == true);
            if (title is null) return;
            children = new TitleCellChildren(title, cell.Children.FirstOrDefault(c => c.Name?.EndsWith("ExplicitBadge") == true));
            cell.Tag = children;
        }

        var reserved = 0.0;
        if (children.ExplicitBadge is { IsVisible: true } badge)
        {
            var width = badge.Bounds.Width > 0 ? badge.Bounds.Width : badge.DesiredSize.Width;
            reserved = width + badge.Margin.Left + badge.Margin.Right;
        }
        var max = System.Math.Max(0, cell.Bounds.Width - reserved);
        if (System.Math.Abs(children.Title.MaxWidth - max) > 0.5)
            children.Title.MaxWidth = max;
    }

    /// <summary>Title, subtitle and the round X. The sidebar page hides it and draws its own header.</summary>
    public static readonly StyledProperty<bool> ShowHeaderProperty =
        AvaloniaProperty.Register<LyricsStudioPanel, bool>(nameof(ShowHeader), true);

    public bool ShowHeader
    {
        get => GetValue(ShowHeaderProperty);
        set => SetValue(ShowHeaderProperty, value);
    }

    /// <summary>The queue header's Choose songs button; the sidebar page shows it and handles <see cref="ChooseSongsRequestedEvent"/>.</summary>
    public static readonly StyledProperty<bool> ShowChooseSongsProperty =
        AvaloniaProperty.Register<LyricsStudioPanel, bool>(nameof(ShowChooseSongs));

    public bool ShowChooseSongs
    {
        get => GetValue(ShowChooseSongsProperty);
        set => SetValue(ShowChooseSongsProperty, value);
    }

    /// <summary>Bubbles from the queue header's Choose songs button to the page that owns the picker.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ChooseSongsRequestedEvent =
        RoutedEvent.Register<LyricsStudioPanel, RoutedEventArgs>(nameof(ChooseSongsRequested), RoutingStrategies.Bubble);

    public event System.EventHandler<RoutedEventArgs>? ChooseSongsRequested
    {
        add => AddHandler(ChooseSongsRequestedEvent, value);
        remove => RemoveHandler(ChooseSongsRequestedEvent, value);
    }

    private void OnChooseSongsClick(object? sender, RoutedEventArgs e) =>
        RaiseEvent(new RoutedEventArgs(ChooseSongsRequestedEvent, this));

    /// <summary>Review row in ELRC: Enter reads the typed words and times back, Esc puts the line back.</summary>
    private void OnLineEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ReviewLine { ShowWordTags: true } line }) return;
        if (e.Key == Key.Enter) { line.CommitRowText(); e.Handled = true; }
        else if (e.Key == Key.Escape) e.Handled = line.RevertRowText();
    }

    /// <summary>Typing in a row picks its line for the nudge bar (a word of it stays picked).</summary>
    private void OnLineEditGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: ReviewLine line } && DataContext is LyricsStudioViewModel vm) vm.SelectLine(line);
    }

    /// <summary>A click on a row's own space (not its pill, text or chips, which handle it) picks the line.</summary>
    private void OnReviewRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: ReviewLine line } && DataContext is LyricsStudioViewModel vm) vm.SelectLine(line);
    }

    /// <summary>Leaving a row applies its typing; a refused edit shows the line again, marked with why.</summary>
    private void OnLineEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ReviewLine line } && !line.CommitRowText())
            line.RevertRowText(keepError: true);
    }

    /// <summary>The lyrics box's Import: one lyrics file (.txt, .lrc, .elrc or .ttml), or null when cancelled.</summary>
    private async System.Threading.Tasks.Task<string?> PickLyricsFileAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Noctis.Localization.Loc.T("Picker.ImportLyrics"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Noctis.Localization.Loc.T("Picker.Lyrics")) { Patterns = new[] { "*.txt", "*.lrc", "*.elrc", "*.ttml" } },
                FilePickerFileTypes.All,
            },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public LyricsStudioPanel()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            // The dialog sets Confirm over itself before the panel attaches; the page has no
            // window of its own, so the prompt goes over the main window.
            if (DataContext is LyricsStudioViewModel { Confirm: null } vm)
                vm.Confirm = message => ConfirmationDialog.ShowAsync(message);
            if (DataContext is LyricsStudioViewModel { PickLyricsFile: null } studio)
                studio.PickLyricsFile = PickLyricsFileAsync;
            WireLyricsPagePreview();
            if (this.IsAttachedToVisualTree()) { WatchModel(); WatchLivePreview(); }
        };
        AttachedToVisualTree += (_, _) => { WireLyricsPagePreview(); HookKeys(); WatchModel(); WatchLivePreview(); };
        DetachedFromVisualTree += (_, _) => { UnhookKeys(); UnwatchModel(); _liveWatch?.SuspendLivePreview(); UnwatchLivePreview(); };
    }

    // ── Live preview pane (10-14) ──
    // The pane shows the review through the app's lyrics view model, in a second copy of the
    // lyrics side panel's view. The panel wires it (the view model never holds the window) and
    // follows the player while on screen, so the pane knows when the song under review plays.

    /// <summary>The lyrics the pane renders; null = the main window's (tests set their own).</summary>
    internal LyricsViewModel? PreviewLyrics { get; set; }

    private LyricsStudioViewModel? _liveWatch;
    private PlayerViewModel? _livePlayer;
    private LyricsViewModel? _liveLyrics;

    /// <summary>The app's lyrics: the main window's, or for the per-song dialog its owner's.</summary>
    private LyricsViewModel? ResolveLyrics()
    {
        if (PreviewLyrics is { } own) return own;
        var top = TopLevel.GetTopLevel(this) as Window;
        if (top?.DataContext is MainWindowViewModel main) return main.Lyrics;
        return top?.Owner is Window { DataContext: MainWindowViewModel owner } ? owner.Lyrics : null;
    }

    private void WatchLivePreview()
    {
        UnwatchLivePreview();
        if (DataContext is not LyricsStudioViewModel vm || ResolveLyrics() is not { } lyrics) return;
        _liveWatch = vm;
        _liveLyrics = lyrics;
        if (vm.UpdateLivePreview is null)
        {
            vm.ClearLivePreview = lyrics.ClearPreview;
            vm.UpdateLivePreview = lyrics.UpdatePreview;
        }
        vm.PropertyChanged += OnStudioPropertyChanged;
        _livePlayer = lyrics.Player;
        _livePlayer.PropertyChanged += OnLivePlayerPropertyChanged;
        SyncLivePreviewView();
        vm.ResumeLivePreview();
    }

    private void UnwatchLivePreview()
    {
        if (_liveWatch is not null) _liveWatch.PropertyChanged -= OnStudioPropertyChanged;
        if (_livePlayer is not null) _livePlayer.PropertyChanged -= OnLivePlayerPropertyChanged;
        _liveWatch = null;
        _livePlayer = null;
    }

    private void OnStudioPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsStudioViewModel.IsLivePreviewOpen)) SyncLivePreviewView();
    }

    private void OnLivePlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.CurrentTrack) or nameof(PlayerViewModel.IsPlayingMusicVideoAudio))
            _liveWatch?.RefreshLivePreview();
    }

    /// <summary>
    /// Builds the pane's lyrics view the first time it opens (its karaoke layout is not built for a
    /// Studio that never shows it), and keeps it counted as a lyrics surface only while open:
    /// hiding does not detach it (the side panel's own SetShown reason).
    /// </summary>
    private void SyncLivePreviewView()
    {
        if (_liveWatch is not { } vm || _liveLyrics is not { } lyrics) return;
        var open = vm.IsLivePreviewOpen;
        if (LivePreviewHost.Content is not LyricsPanelView view)
        {
            if (!open) return;
            view = new LyricsPanelView { DataContext = lyrics };
            view.SetCloseAction(() => { if (DataContext is LyricsStudioViewModel studio) studio.ShowLivePreview = false; },
                Noctis.Localization.Loc.T("LyricsStudio.HidePreview"));
            LivePreviewHost.Content = view;
        }
        view.SetShown(open);
    }

    // The model banner follows the shared model manager while the panel is on screen: a download
    // started or finished in another Studio (the per-song dialog over the page) updates this one.
    // Subscribed by the view, not the view model, so a replaced Studio is never kept alive.
    private LyricsStudioViewModel? _modelWatch;

    private void WatchModel()
    {
        UnwatchModel();
        if (DataContext is not LyricsStudioViewModel vm) return;
        _modelWatch = vm;
        vm.Models.StateChanged += OnModelStateChanged;
        vm.RefreshModelState();
    }

    private void UnwatchModel()
    {
        if (_modelWatch is null) return;
        _modelWatch.Models.StateChanged -= OnModelStateChanged;
        _modelWatch = null;
    }

    private void OnModelStateChanged(object? sender, System.EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _modelWatch?.RefreshModelState());

    // The Studio's keys listen on the window, tunnelled, while the panel is on screen: a key
    // only reaches a handler on the panel itself when focus is inside it, and clicking blank
    // space (or the app's click-away unfocus) leaves focus outside, so [ and ] went dead (09-24).
    private TopLevel? _keyHost;

    private void HookKeys()
    {
        UnhookKeys();
        _keyHost = TopLevel.GetTopLevel(this);
        _keyHost?.AddHandler(KeyDownEvent, OnHostKeyDown, RoutingStrategies.Tunnel);
        _keyHost?.AddHandler(KeyUpEvent, OnHostKeyUp, RoutingStrategies.Tunnel);
    }

    private void UnhookKeys()
    {
        _keyHost?.RemoveHandler(KeyDownEvent, OnHostKeyDown);
        _keyHost?.RemoveHandler(KeyUpEvent, OnHostKeyUp);
        _keyHost = null;
    }

    /// <summary>Tunnelled so a focused Button cannot swallow Space first; typing in a TextBox is left alone.</summary>
    private void OnHostKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || DataContext is not LyricsStudioViewModel m) return;
        if (e.Source is TextBox) return;
        // [ and ] shift every line 0.1 s (the "Shift all lines" buttons), outside text boxes.
        // Every Studio key leaves typing alone; Ctrl+, (Settings) and Alt/Shift+arrows (back, seek) are not used.
        if (m.HasReview && e.KeyModifiers == KeyModifiers.None && e.Key is Key.OemOpenBrackets or Key.OemCloseBrackets)
        {
            (e.Key == Key.OemOpenBrackets ? m.NudgeEarlierCommand : m.NudgeLaterCommand).Execute(null);
            e.Handled = true;
            return;
        }
        // , and . nudge the selected line or word 10 ms (Shift: 100 ms); / plays from just before it.
        if (m is { HasReview: true, HasNudgeTarget: true } && e.Key is Key.OemComma or Key.OemPeriod
            && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            var ms = e.KeyModifiers == KeyModifiers.Shift ? 100 : 10;
            m.NudgeSelectionBy(System.TimeSpan.FromMilliseconds(e.Key == Key.OemComma ? -ms : ms));
            e.Handled = true;
            return;
        }
        if (m is { HasReview: true, HasNudgeTarget: true } && e.Key == Key.OemQuestion && e.KeyModifiers == KeyModifiers.None)
        {
            m.AuditionCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (!m.IsTapping) return;
        if (e.Key == Key.Space) { m.TapCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { m.CancelTapCommand.Execute(null); e.Handled = true; }
    }

    private void OnHostKeyUp(object? sender, KeyEventArgs e)
    {
        if (IsEffectivelyVisible && DataContext is LyricsStudioViewModel { IsTapping: true } && e.Key == Key.Space && e.Source is not TextBox) e.Handled = true;
    }

    /// <summary>
    /// Preview (09-23): only the sidebar page can show the lyrics page — the per-song dialog is
    /// modal and would sit on top of it — so the hook is set when the panel is in the main window.
    /// </summary>
    private void WireLyricsPagePreview()
    {
        if (DataContext is not LyricsStudioViewModel { ShowOnLyricsPage: null } vm) return;
        if (TopLevel.GetTopLevel(this) is not Window { DataContext: MainWindowViewModel main }) return;
        vm.ShowOnLyricsPage = (track, synced) =>
        {
            main.Lyrics.ShowPreview(track, synced);
            main.ShowLyricsPage();
            return System.Threading.Tasks.Task.CompletedTask;
        };
        vm.ClearLyricsPagePreview = main.Lyrics.ClearPreview;
    }
}
