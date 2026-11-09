using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// Settings' pages inside the sheet: the top level and the pushed page (Music Folders, Lyrics,
/// Accent, Downloads...) share one area. A pushed page slides in from the right while the top
/// level drifts a little left and fades, and Back reverses it: the app's push (Motion), with a
/// fade rather than a shade because the two panes are see-through over one backdrop. The
/// header's hairline shows once the page showing has scrolled under it. The profile card's
/// "Synced 5 min ago" is re-read once a minute while the page shows, as the Account page's is.
/// </summary>
public partial class SettingsPage : UserControl
{
    private SettingsPageViewModel? _vm;
    private DispatcherTimer? _ticker;

    public SettingsPage()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        InitializeComponent();
        Services.FrameStallMonitor.AddTime("settings page built", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        PaneHost.SizeChanged += (_, _) => PlacePanes(animate: false);
        MainScroll.ScrollChanged += (_, _) => UpdateHairline();
        SubScroll.ScrollChanged += (_, _) => UpdateHairline();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as SettingsPageViewModel;
        if (_vm != null) _vm.PropertyChanged += OnViewModelChanged;
        // A kept view handed a new Settings (PageStack): it opens at its top, as a new one did.
        MainScroll.Offset = default;
        SubScroll.Offset = default;
        PlacePanes(animate: false);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ticker ??= new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background,
            (_, _) => (DataContext as SettingsPageViewModel)?.RefreshSyncTime());
        _ticker.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _ticker?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsPageViewModel.Pane)) return;
        if (_vm?.IsSubPane == true) SubScroll.Offset = default;   // a page always opens at its top
        PlacePanes(animate: true);
    }

    /// <summary>Puts both panes where the current page wants them, sliding when asked. The pane
    /// leaving is hidden once its slide ends, so only the page showing is in the tree's reach
    /// (touch, the screen reader).</summary>
    private void PlacePanes(bool animate)
    {
        var sub = _vm?.IsSubPane == true;
        var width = PaneHost.Bounds.Width > 0 ? PaneHost.Bounds.Width : 412;
        var main = Motion.TranslateX(sub ? -width * Motion.PushShift : 0);
        var page = Motion.TranslateX(sub ? 0 : width);

        MainScroll.IsVisible = true;
        SubScroll.IsVisible = true;
        MainScroll.IsHitTestVisible = !sub;
        SubScroll.IsHitTestVisible = sub;
        UpdateHairline();
        if (animate)
        {
            // Each run's end settles the panes; a later move drops the earlier runs, ends and all.
            Motion.To(MainScroll, Motion.Push, main, opacity: sub ? 0 : 1, done: Settle);
            Motion.To(SubScroll, Motion.Push, page, done: Settle);
            return;
        }
        Motion.Stop(MainScroll);
        Motion.Stop(SubScroll);
        MainScroll.RenderTransform = main;
        SubScroll.RenderTransform = page;
        MainScroll.Opacity = sub ? 0 : 1;
        Settle();
    }

    private void Settle()
    {
        var sub = _vm?.IsSubPane == true;
        MainScroll.IsVisible = !sub;
        SubScroll.IsVisible = sub;
    }

    /// <summary>How far the top level scrolls before the large title is gone and the header's
    /// small one shows in its place.</summary>
    internal const double TitleCollapseOffset = 36;

    private void UpdateHairline()
    {
        var sub = _vm?.IsSubPane == true;
        var showing = sub ? SubScroll : MainScroll;
        HeaderHairline.Opacity = showing.Offset.Y > 0.5 ? 1 : 0;
        // A pushed page names itself in the header at once; the top level's title waits for the large one to go.
        SettingsTitle.Opacity = sub || MainScroll.Offset.Y > TitleCollapseOffset ? 1 : 0;
    }
}
