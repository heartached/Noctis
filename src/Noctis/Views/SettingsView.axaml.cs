using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using Noctis.Helpers;
using System.Collections.Generic;
using System.Linq;
using Noctis.ViewModels;
using Noctis.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Noctis.Views;

public partial class SettingsView : UserControl
{
    private const double PreampThumbSize = 14;
    private const double PreampDefault = 0.0;
    private const double CrossfadeDurationDefault = 6.0; // matches the Settings reset value

    private SettingsViewModel? _trackedViewModel;
    private readonly TranslateTransform _preampThumbTransform = new();
    private bool _isPreampDragging;
    private DateTime _lastPreampPressAt = DateTime.MinValue;
    private Point _lastPreampPressPosition;

    public SettingsView()
    {
        InitializeComponent();

        if (EqSavePresetButton.Flyout is Flyout eqPresetFlyout)
        {
            eqPresetFlyout.Opened += OnEqPresetFlyoutOpened;
            eqPresetFlyout.Closing += OnEqPresetFlyoutClosing;
        }

        // Wire up the Add Folder button to open a native folder picker
        AddFolderButton.Click += OnAddFolderClicked;

        // Settings search: the card index is built lazily on the first keystroke (all tab
        // panels exist by then) and applied on every change. Watch the property, not
        // TextChanged: the latter does not fire for a programmatic/bound Text set.
        SettingsSearchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) ApplySearch();
        };
        SettingsSearchBox.KeyDown += OnSearchBoxKeyDown;
        DataContextChanged += OnSettingsDataContextChanged;

        // Pre-amp pill slider: the visible track/fill/thumb are drawn on a Canvas and
        // positioned from code-behind. The real Slider is transparent and only handles
        // pointer input (drag + double-press-to-reset), mirroring the metadata volume slider.
        PreampThumb.RenderTransform = _preampThumbTransform;
        PreampSlider.AddHandler(InputElement.PointerPressedEvent, OnPreampPointerPressed, RoutingStrategies.Tunnel);
        PreampSlider.AddHandler(InputElement.PointerMovedEvent, OnPreampPointerMoved, RoutingStrategies.Tunnel);
        PreampSlider.AddHandler(InputElement.PointerReleasedEvent, OnPreampPointerReleased, RoutingStrategies.Tunnel);
        PreampSlider.PointerCaptureLost += OnPreampCaptureLost;

        // Double-clicking a slider's knob snaps it back to the shipped default (the
        // island width and both opacity sliders). Defaults come from a fresh AppSettings
        // so this can never drift from what a new install gets.
        var defaults = new Noctis.Models.AppSettings();
        AttachThumbDoubleTapReset(IslandWidthSlider, defaults.PlaybackBarWidth);
        AttachThumbDoubleTapReset(PlayerBarOpacitySlider, defaults.PlaybackBarBackgroundOpacity);
        AttachThumbDoubleTapReset(BackgroundBlurSlider, defaults.BackgroundBlurAmount);
        AttachThumbDoubleTapReset(TrackBoxOpacitySlider, defaults.PlaybackBarTrackBoxOpacity);
        AttachThumbDoubleTapReset(MiniPlayerOpacitySlider, defaults.MiniPlayerBackgroundOpacity);
        PreampSlider.PropertyChanged += OnPreampSliderPropertyChanged;
        PreampSlider.SizeChanged += (_, _) => UpdatePreampVisual();
        DispatcherTimer.RunOnce(UpdatePreampVisual, TimeSpan.FromMilliseconds(10));

        // Drop focus from the ListenBrainz token box when the user clicks anywhere
        // outside it, same behaviour as the top-bar search box. Tunnel so it runs
        // before inner controls handle the press.
        AddHandler(PointerPressedEvent, OnSettingsPointerPressed, RoutingStrategies.Tunnel);

        // The EQ preset combo lives inside SettingsScrollViewer; its momentum-scroll
        // behavior would otherwise consume wheel events routed through it, including
        // events over the open dropdown. Suspend it while the dropdown is open so the
        // wheel reaches the popup's own ScrollViewer (same fix as the genre combo).
        if (this.FindControl<ComboBox>("EqPresetCombo") is { } eqCombo)
        {
            eqCombo.DropDownOpened += OnEqPresetDropDownOpened;
            eqCombo.DropDownClosed += OnEqPresetDropDownClosed;
        }
    }

    // ── Settings search ──

    private SettingsSearchIndex? _searchIndex;

    /// <summary>Test hook: the index as built so far (null until the first query).</summary>
    internal SettingsSearchIndex? SearchIndexForTests => _searchIndex;
    internal void ApplySearchForTests() => ApplySearch();

    /// <summary>
    /// Section key → x:Name of its page panel. The index is keyed by the section key so the
    /// rail badge counts line up (they never did for "Account &amp; Sync", whose panel name
    /// differed from its key).
    /// </summary>
    internal static readonly (string Tab, string PanelName)[] TabPanels =
    {
        (SettingsViewModel.TabGeneral, "GeneralTabPanel"),
        (SettingsViewModel.TabAppearance, "AppearanceTabPanel"),
        (SettingsViewModel.TabPlayer, "PlayerTabPanel"),
        (SettingsViewModel.TabLyrics, "LyricsTabPanel"),
        (SettingsViewModel.TabShortcuts, "ShortcutsTabPanel"),
        (SettingsViewModel.TabAudio, "AudioTabPanel"),
        (SettingsViewModel.TabLibrary, "LibraryTabPanel"),
        (SettingsViewModel.TabAdvanced, "AdvancedTabPanel"),
        (SettingsViewModel.TabAccountDevices, "AccountSyncTabPanel"),
        (SettingsViewModel.TabIntegrations, "IntegrationsTabPanel"),
        (SettingsViewModel.TabPlugins, "PluginsTabPanel"),
        (SettingsViewModel.TabStatistics, "StatisticsTabPanel"),
        (SettingsViewModel.TabAbout, "AboutTabPanel"),
    };

    private SettingsSearchIndex EnsureSearchIndex()
    {
        if (_searchIndex is not null) return _searchIndex;
        var panels = new List<(string Tab, Control Panel)>();
        foreach (var (tab, name) in TabPanels)
        {
            if (this.FindControl<Control>(name) is { } panel)
                panels.Add((tab, panel));
        }
        return _searchIndex = SettingsSearchIndex.Build(panels);
    }

    /// <summary>Focus the rail's search box (Ctrl+F while the modal is open).</summary>
    public void FocusSearch()
    {
        SettingsSearchBox.Focus();
        SettingsSearchBox.SelectAll();
    }

    private void ApplySearch()
    {
        var query = SettingsSearchBox.Text ?? string.Empty;
        // Nothing to restore and nothing to hide: don't walk the tree for an empty box.
        if (_searchIndex is null && query.Trim().Length == 0) return;
        var index = EnsureSearchIndex();
        index.Apply(query);

        if (DataContext is not SettingsViewModel vm) return;
        var counts = index.CountByTab(query);
        foreach (var section in vm.Sections)
            section.MatchCount = counts.TryGetValue(section.Key, out var n) ? n : 0;

        // Typing something the current section doesn't contain jumps to the first section
        // that does, so the user never stares at an empty page.
        if (query.Trim().Length > 0 && !counts.ContainsKey(vm.SelectedSettingsTab))
        {
            var first = vm.Sections.FirstOrDefault(sct => sct.MatchCount > 0);
            if (first is not null) vm.SelectedSettingsTab = first.Key;
        }
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when !string.IsNullOrEmpty(SettingsSearchBox.Text):
                // First Escape clears the query; the next one closes Settings as usual.
                SettingsSearchBox.Text = string.Empty;
                e.Handled = true;
                break;
            case Key.Return when DataContext is SettingsViewModel vm:
                if (_searchIndex?.FirstMatch(SettingsSearchBox.Text ?? string.Empty, vm.SelectedSettingsTab) is { } hit)
                {
                    if (hit.Tab != vm.SelectedSettingsTab) vm.SelectedSettingsTab = hit.Tab;
                    hit.Card.BringIntoView();
                }
                e.Handled = true;
                break;
        }
    }

    // Enter in the ffmpeg path box re-probes the path so the user gets an explicit
    // "validate now" affordance after pasting. The Text binding already updates the
    // status live on each edit; this is a deliberate confirmation step.
    private void OnFfmpegPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && DataContext is SettingsViewModel vm)
        {
            vm.RefreshFfmpegStatus();
            e.Handled = true;
        }
    }

    // Enter in the profile name box commits the edit and drops focus, so the caret/edit
    // affordance disappears (same defocus target used when clicking outside the token box).
    private void OnProfileNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            SettingsScrollViewer?.Focus(NavigationMethod.Pointer);
            e.Handled = true;
        }
    }

    private void OnEqPresetDropDownOpened(object? sender, EventArgs e)
    {
        if (SettingsScrollViewer is not null)
            SmoothScrollBehavior.SetIsEnabled(SettingsScrollViewer, false);
    }

    private void OnEqPresetDropDownClosed(object? sender, EventArgs e)
    {
        if (SettingsScrollViewer is not null)
            SmoothScrollBehavior.SetIsEnabled(SettingsScrollViewer, true);
    }

    // ── Save EQ preset flyout (GitHub #95) ──

    // The flyout's content attaches each time it opens: start from a clean state, prefill
    // the selected user preset's name (so re-saving overwrites it) and focus the box.
    private void OnEqPresetNameBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox box || DataContext is not SettingsViewModel vm) return;
        vm.EqPresetSaveError = "";
        if (string.IsNullOrEmpty(vm.NewEqPresetName) && vm.UserEqPresetNames.Contains(vm.SelectedEqPresetName))
            vm.NewEqPresetName = vm.SelectedEqPresetName;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnEqPresetNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            SaveEqPresetFromFlyout();
            e.Handled = true;
        }
    }

    private void OnEqSavePresetClick(object? sender, RoutedEventArgs e) => SaveEqPresetFromFlyout();

    // Open/close motion: the custom theme editor's 180ms CubicEaseOut fade + 0.96 scale
    // (ThemeEditorDialog), played on the FlyoutPresenter. Same recipe as ColorPickerFlyout:
    // the first Closing is cancelled so the fade-out can play, then the real Hide runs.
    private static readonly TimeSpan EqPresetMotionDuration = TimeSpan.FromMilliseconds(180);
    private static readonly Avalonia.Media.Transformation.TransformOperations EqPresetShrunk =
        Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.96)");
    private static readonly Avalonia.Media.Transformation.TransformOperations EqPresetRest =
        Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
    private Control? _eqPresetMotionBody;
    private bool _eqPresetCloseHeld;

    private static Avalonia.Animation.Transitions BuildEqPresetMotion() => new()
    {
        new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = EqPresetMotionDuration, Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
        new Avalonia.Animation.TransformOperationsTransition { Property = RenderTransformProperty, Duration = EqPresetMotionDuration, Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
    };

    private void OnEqPresetFlyoutOpened(object? sender, EventArgs e)
    {
        _eqPresetCloseHeld = false;
        var body = EqPresetFlyoutBody.FindAncestorOfType<FlyoutPresenter>() ?? (Control)EqPresetFlyoutBody;
        if (!ReferenceEquals(_eqPresetMotionBody, body))
        {
            _eqPresetMotionBody = body;
            body.PropertyChanged += OnEqPresetMotionBodyPropertyChanged;
        }
        body.Transitions = null;
        body.RenderTransformOrigin = RelativePoint.Center;
        body.Opacity = 0;
        body.RenderTransform = EqPresetShrunk;
        body.Transitions = BuildEqPresetMotion();
        Dispatcher.UIThread.Post(() =>
        {
            body.Opacity = 1;
            body.RenderTransform = EqPresetRest;
        }, DispatcherPriority.Render);
    }

    private void OnEqPresetFlyoutClosing(object? sender, CancelEventArgs e)
    {
        if (_eqPresetCloseHeld)
        {
            _eqPresetCloseHeld = false;
            return;
        }
        if (_eqPresetMotionBody is not { } body) return;

        e.Cancel = true;
        _eqPresetCloseHeld = true;
        body.Transitions = BuildEqPresetMotion();
        Dispatcher.UIThread.Post(() =>
        {
            body.Opacity = 0;
            body.RenderTransform = EqPresetShrunk;
        }, DispatcherPriority.Render);
    }

    private void OnEqPresetMotionBodyPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != OpacityProperty || !_eqPresetCloseHeld) return;
        if (sender is not Control body || body.Opacity > 0.001) return;
        EqSavePresetButton.Flyout?.Hide();
    }

    /// <summary>Closes the flyout on success; a refused name keeps it open with the reason shown.</summary>
    private void SaveEqPresetFromFlyout()
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (vm.SaveUserEqPreset(vm.NewEqPresetName))
            EqSavePresetButton.Flyout?.Hide();
    }

    private void OnSettingsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var tokenBox = this.FindControl<TextBox>("ListenBrainzTokenBox");
        if (tokenBox is not { IsFocused: true })
            tokenBox = this.FindControl<TextBox>("ListenBrainzApiUrlBox");
        if (tokenBox is not { IsFocused: true })
            return;

        bool insideBox = false;
        if (e.Source is Visual clickSource)
        {
            Visual? v = clickSource;
            while (v != null)
            {
                if (ReferenceEquals(v, tokenBox)) { insideBox = true; break; }
                v = v.GetVisualParent();
            }
        }

        if (!insideBox)
            Dispatcher.UIThread.Post(() => SettingsScrollViewer.Focus(NavigationMethod.Pointer),
                DispatcherPriority.Background);
    }

    private void OnSettingsDataContextChanged(object? sender, EventArgs e)
    {
        if (_trackedViewModel != null)
            _trackedViewModel.PropertyChanged -= OnSettingsViewModelPropertyChanged;

        _trackedViewModel = DataContext as SettingsViewModel;

        if (_trackedViewModel != null)
            _trackedViewModel.PropertyChanged += OnSettingsViewModelPropertyChanged;
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // The media-folders card is the first card on the Library tab (the view model
            // already switched tabs), so landing there is just a scroll to the top.
            case nameof(SettingsViewModel.MediaFoldersScrollRequest):
            case nameof(SettingsViewModel.SelectedSettingsTab):
                ScrollToTop();
                Dispatcher.UIThread.Post(LogStyleProbe, DispatcherPriority.Background);
                break;

            // Web Remote / Local API turned on: their body glides open below the fold
            // (Local API is the page's last card), so follow the card down as it grows.
            case nameof(SettingsViewModel.IsWebRemoteCardOpen):
                if (_trackedViewModel?.IsWebRemoteCardOpen == true) FollowReveal(WebRemoteCard, WebRemoteBody);
                break;
            case nameof(SettingsViewModel.LocalApiRunning):
                if (_trackedViewModel?.LocalApiRunning == true) FollowReveal(LocalApiCard, LocalApiBody);
                break;

            // Version-manager download started: bring the progress bar + Cancel
            // button into view (the release list can push them off-screen).
            case nameof(SettingsViewModel.IsDevDownloading):
                if (_trackedViewModel?.IsDevDownloading == true)
                    Dispatcher.UIThread.Post(
                        () => DevDownloadPanel.BringIntoView(),
                        DispatcherPriority.Loaded);
                break;
        }
    }

    private EventHandler? _followReveal;

    /// <summary>
    /// Keeps <paramref name="card"/>'s bottom edge on screen while <paramref name="body"/>
    /// glides open: after every layout pass the scroller moves down by whatever the card
    /// has grown past the viewport, so the page scrolls in step with the reveal instead of
    /// jumping once (a single BringIntoView at the start sees the still-folded height).
    /// Never scrolls the card's top out of view. Ends when the reveal lands or reverses.
    /// </summary>
    private void FollowReveal(Control card, Noctis.Controls.CollapsibleContent body)
    {
        var scroller = SettingsScrollViewer;
        if (scroller is null || !card.IsEffectivelyVisible) return;

        if (_followReveal is not null) scroller.LayoutUpdated -= _followReveal;
        _followReveal = (_, _) =>
        {
            if (!body.IsOpen)
            {
                Stop();
                return;
            }
            if (card.TranslatePoint(default, scroller) is { } top)
            {
                const double gap = 16;
                var overflow = top.Y + card.Bounds.Height + gap - scroller.Viewport.Height;
                overflow = Math.Min(overflow, top.Y - gap); // keep the card's header in view
                if (overflow > 0.5)
                {
                    var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
                    var y = Math.Min(scroller.Offset.Y + overflow, max);
                    if (y > scroller.Offset.Y) scroller.Offset = new Vector(scroller.Offset.X, y);
                }
            }
            if (body.Reveal >= 0.999) Stop();
        };
        scroller.LayoutUpdated += _followReveal;

        void Stop()
        {
            if (_followReveal is not null) scroller.LayoutUpdated -= _followReveal;
            _followReveal = null;
        }
    }

    private void ScrollToTop()
    {
        Dispatcher.UIThread.Post(
            () => SettingsScrollViewer.Offset = new Vector(SettingsScrollViewer.Offset.X, 0),
            DispatcherPriority.Loaded);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        OnSettingsDataContextChanged(this, EventArgs.Empty);
        Dispatcher.UIThread.Post(LogStyleProbe, DispatcherPriority.Background);
    }

    internal static readonly HashSet<string> StyleProbeTabs = new(); // internal for tests

    /// <summary>
    /// macOS diagnostics (owner's Mac, 09-26): styled values that came out wrong there —
    /// square pill buttons and text boxes, invisible section titles, slider tracks gone —
    /// while cards and locally set values drew fine. One line per session in "Copy Logs"
    /// with what the styles actually resolved to, so wrong values and right-values-wrong-
    /// pixels can be told apart. Once per tab per session.
    /// </summary>
    private void LogStyleProbe()
    {
        var tab = (DataContext as SettingsViewModel)?.SelectedSettingsTab ?? "?";
        if (StyleProbeTabs.Contains(tab)) return;
        try
        {
            var all = this.GetVisualDescendants().ToList();
            var title = all.OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("section-title") && t.IsEffectivelyVisible);
            var pill = all.OfType<Button>().FirstOrDefault(b => b.Classes.Contains("pill-action") && b.IsEffectivelyVisible);
            var pillPresenter = pill?.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().FirstOrDefault();
            var box = all.OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible);
            var boxBorder = box?.GetVisualDescendants().OfType<Border>().FirstOrDefault(x => x.Name == "PART_BorderElement");
            var slider = all.OfType<Slider>().FirstOrDefault(s => s.Classes.Contains("accent-slider") && s.IsEffectivelyVisible);
            var track = slider?.GetVisualDescendants().OfType<RepeatButton>().FirstOrDefault(r => r.Name == "PART_IncreaseButton")
                ?.GetVisualDescendants().OfType<Border>().FirstOrDefault();
            if (title == null && pill == null && slider == null) return; // nothing realized yet
            StyleProbeTabs.Add(tab);
            DebugLog.Write("SettingsStyle",
                $"{tab}: title opacity={title?.Opacity:0.##} fg={title?.Foreground} | " +
                $"pill radius={pillPresenter?.CornerRadius} bg={pillPresenter?.Background} | " +
                $"textbox radius={box?.CornerRadius} border={boxBorder?.CornerRadius} | " +
                $"slider track opacity={track?.Opacity:0.##} radius={track?.CornerRadius} h={track?.Bounds.Height:0.#} bg={track?.Background}");
        }
        catch (Exception ex)
        {
            StyleProbeTabs.Add(tab);
            DebugLog.Write("SettingsStyle", $"{tab}: probe failed: {ex.Message}");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_trackedViewModel != null)
        {
            _trackedViewModel.PropertyChanged -= OnSettingsViewModelPropertyChanged;
            _trackedViewModel = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private async void OnPickAvatarClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not SettingsViewModel vm) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose Profile Picture",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif" }
                    }
                }
            });

            if (files.Count == 0) return;

            var sourcePath = files[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(sourcePath))
                return;

            // The view model copies the picture into its profile folder under a unique
            // name (see SetProfileAvatarAsync for why the name must change per pick).
            await vm.SetProfileAvatarAsync(sourcePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsView] Avatar pick failed: {ex.Message}");
        }
    }

    private async void OnModifyLyricsBackgroundClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            if (DataContext is not SettingsViewModel vm) return;
            var library = App.Services?.GetService<ILibraryService>();
            if (library == null) return;

            var ytDlp = App.Services?.GetService<Services.YouTube.YtDlpTool>();
            var ffmpeg = App.Services?.GetService<IAudioConverterService>();
            var dialog = new LyricsBackgroundPickerDialog
            {
                DataContext = new LyricsBackgroundPickerViewModel(vm, library, ytDlp, () => ffmpeg?.GetFfmpegPath())
            };
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                DialogHelper.SizeToOwner(dialog, owner);
                await dialog.ShowDialog(owner);
            }
            else dialog.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsView] Lyrics background picker failed: {ex.Message}");
        }
    }

    private void OnPreampSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.ReplayGainPreampDb = PreampDefault;
    }

    // Double-tapping the crossfade duration slider restores the default duration
    // (same affordance as the ReplayGain pre-amp slider).
    private void OnCrossfadeSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.CrossfadeDuration = CrossfadeDurationDefault;
            e.Handled = true;
        }
    }

    // Double-tapping an EQ gain slider resets that band to 0 dB (same affordance
    // as the ReplayGain pre-amp slider).
    private void OnEqGainSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Slider { DataContext: EqBandViewModel band })
        {
            band.GainDb = 0;
            e.Handled = true;
        }
    }

    private void OnEqPreampSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.EqPreampDb = 0;
            e.Handled = true;
        }
    }

    // Double-tapping the lyrics minimum-line-opacity slider restores the fresh-install
    // value (same affordance as the pre-amp and crossfade sliders).
    private void OnLyricsMinLineOpacitySliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.LyricsMinLineOpacity = Models.AppSettings.LyricsMinLineOpacityDefault;
            e.Handled = true;
        }
    }

    // Double-tapping the album page tint-strength slider restores the full cover colour.
    private void OnAlbumPageTintStrengthSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.AlbumPageTintStrength = Models.AppSettings.AlbumPageTintStrengthDefault;
            e.Handled = true;
        }
    }

    // Double-tapping a Drift knob (GitHub #111) restores the stock Drift look for that knob.
    private void OnDriftMovementSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.LyricsDriftMovement = Models.AppSettings.LyricsDriftKnobDefault;
            e.Handled = true;
        }
    }

    private void OnDriftSaturationSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.LyricsDriftSaturation = Models.AppSettings.LyricsDriftKnobDefault;
            e.Handled = true;
        }
    }

    private void OnDriftBlurSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.LyricsDriftBlur = Models.AppSettings.LyricsDriftKnobDefault;
            e.Handled = true;
        }
    }

    private void OnPreampSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty ||
            e.Property.Name is nameof(Bounds) or nameof(IsEnabled))
        {
            UpdatePreampVisual();
        }
    }

    private void UpdatePreampVisual()
    {
        if (PreampSlider == null ||
            PreampTrackBackground == null ||
            PreampTrackFill == null ||
            PreampThumb == null)
            return;

        PillSliderVisualHelper.UpdateVisual(
            PreampSlider,
            PreampTrackBackground,
            PreampTrackFill,
            PreampThumb,
            _preampThumbTransform,
            PreampThumbSize,
            enabledBackgroundOpacity: 0.4,
            disabledBackgroundOpacity: 0.2);
    }

    private void OnPreampPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;

        var position = e.GetPosition(slider);
        if (IsPreampDoublePress(position))
        {
            _isPreampDragging = false;
            _lastPreampPressAt = DateTime.MinValue;
            e.Pointer.Capture(null);
            if (DataContext is SettingsViewModel vm)
                vm.ReplayGainPreampDb = PreampDefault;
            e.Handled = true;
            return;
        }

        _lastPreampPressAt = DateTime.UtcNow;
        _lastPreampPressPosition = position;
        _isPreampDragging = true;
        e.Pointer.Capture(slider);
        slider.Value = PillSliderVisualHelper.GetValueFromPointer(slider, position, PreampThumbSize);
        e.Handled = true;
    }

    private void OnPreampPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPreampDragging) return;
        if (sender is not Slider slider) return;

        slider.Value = PillSliderVisualHelper.GetValueFromPointer(slider, e.GetPosition(slider), PreampThumbSize);
        e.Handled = true;
    }

    private void OnPreampPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPreampDragging) return;

        _isPreampDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>
    /// Resets <paramref name="slider"/> to <paramref name="defaultValue"/> on a
    /// double-click of its thumb. The track is excluded on purpose: a double-click there
    /// is two ordinary jumps, and turning it into a reset would surprise anyone clicking
    /// quickly to fine-tune. The two-way binding carries the value to the view model.
    /// </summary>
    private static void AttachThumbDoubleTapReset(Slider slider, double defaultValue)
    {
        slider.DoubleTapped += (_, e) =>
        {
            if (e.Source is not Visual source) return;
            if (source.FindAncestorOfType<Thumb>(includeSelf: true) == null) return;
            slider.Value = defaultValue;
            e.Handled = true;
        };
    }

    private void OnPreampCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isPreampDragging = false;
    }

    private bool IsPreampDoublePress(Point position)
    {
        var elapsed = DateTime.UtcNow - _lastPreampPressAt;
        if (elapsed > TimeSpan.FromMilliseconds(400))
            return false;

        var dx = position.X - _lastPreampPressPosition.X;
        var dy = position.Y - _lastPreampPressPosition.Y;
        return dx * dx + dy * dy <= 36;
    }

    private async void OnAddFolderClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not SettingsViewModel vm) return;

            // Get the top-level window for the dialog
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Music Folder",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                var path = folders[0].Path.LocalPath;
                await vm.AddFolderPath(path);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Failed to add folder: {ex.Message}");
        }
    }

}
