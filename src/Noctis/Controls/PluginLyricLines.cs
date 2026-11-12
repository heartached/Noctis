using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services.Plugins;
using Noctis.ViewModels;

namespace Noctis.Controls;

/// <summary>
/// The lyrics renderer in use (plugin API 1.8, IPluginHost.RegisterLyricsRenderer), or none, and
/// how the three lyrics lists (page, side panel, mini player) put it to use.
///
/// With no renderer chosen nothing here touches a list: its ItemTemplate stays the very one its
/// XAML declares. With one, the list's ItemTemplate is wrapped: the XAML template still builds the
/// line's button (click-to-seek, screen reader name, active scale, depth blur, duet alignment, the
/// glide's transforms) and only the button's content is the plugin's line. Switching back puts the
/// XAML template object itself back.
/// </summary>
public static class PluginLyricLines
{
    private static PluginLyricsRenderer? _current;

    /// <summary>The renderer the lists use, or null for Noctis's own lines.</summary>
    public static PluginLyricsRenderer? Current => _current;

    /// <summary>Raised on the UI thread after <see cref="Current"/> changed.</summary>
    public static event EventHandler? Changed;

    /// <summary>A renderer threw (UI thread, possibly mid-layout or mid-frame: act later).</summary>
    public static event Action<PluginLyricsRenderer, Exception>? RendererFailed;

    /// <summary>Call on the UI thread.</summary>
    public static void Set(PluginLyricsRenderer? renderer)
    {
        if (ReferenceEquals(_current, renderer)) return;
        _current = renderer;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static void ReportFailure(PluginLyricsRenderer renderer, Exception ex) => RendererFailed?.Invoke(renderer, ex);

    // How deep renderer code is on the (UI thread's) stack right now.
    private static int _pluginCalls;

    /// <summary>The renderer may be called: it is the one in use and it has not failed. A line or a
    /// template built for a renderer since withdrawn (plugin off, uninstalled, faulted, another
    /// one picked) never calls into it again, even while it is still on screen for a frame.</summary>
    internal static bool IsLive(PluginLyricsRenderer renderer) => ReferenceEquals(_current, renderer) && !renderer.Failed;

    /// <summary>Marks a call into renderer code; pair it with <see cref="EndCall"/> in a finally.</summary>
    internal static long BeginCall()
    {
        _pluginCalls++;
        return Stopwatch.GetTimestamp();
    }

    internal static void EndCall() => _pluginCalls--;

    /// <summary>A call that held the UI thread past the host's <see cref="PluginHost.CallbackBudget"/>
    /// fails the renderer, as a slow callback stops any plugin (<see cref="PluginHost.Guard"/>).</summary>
    internal static TimeoutException? Overran(PluginLyricsRenderer renderer, string what, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        return elapsed <= renderer.Host.CallbackBudget
            ? null
            : new TimeoutException($"{what} blocked Noctis for {(long)elapsed.TotalMilliseconds} ms.");
    }

    /// <summary>
    /// Lets <paramref name="list"/> follow <see cref="Current"/> while it is in the visual tree.
    /// <paramref name="viewModel"/> finds the lyrics view model the list shows.
    /// </summary>
    public static void Attach(ItemsControl list, LyricsSurface surface, Func<LyricsViewModel?> viewModel)
    {
        var follower = new ListFollower(list, surface, viewModel);
        list.AttachedToVisualTree += (_, _) => follower.Start();
        list.DetachedFromVisualTree += (_, _) => follower.Stop();
        if (list.IsAttachedToVisualTree()) follower.Start();
    }

    /// <summary>Tests: the list's state as <see cref="Attach"/> leaves it.</summary>
    internal static bool IsWrapped(ItemsControl list) => list.ItemTemplate is LineTemplate;

    private sealed class ListFollower
    {
        private readonly ItemsControl _list;
        private readonly LyricsSurface _surface;
        private readonly Func<LyricsViewModel?> _viewModel;
        private IDataTemplate? _builtIn;      // the XAML template, while it is wrapped
        private PluginLyricsRenderer? _applied;
        private bool _started;

        public ListFollower(ItemsControl list, LyricsSurface surface, Func<LyricsViewModel?> viewModel)
        {
            _list = list;
            _surface = surface;
            _viewModel = viewModel;
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            Changed += OnChanged;
            Apply();
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            Changed -= OnChanged;
            // While away the list keeps nothing of the plugin (a template or a line that could call it,
            // or hold its assembly after it is switched off); it is wrapped again when it comes back.
            // After the detach, not during it: the list's lines are being detached right now.
            if (_applied is not null) Dispatcher.UIThread.Post(() => { if (!_started) Unwrap(); });
        }

        // Changed can fire from inside renderer code (a plugin withdrawing its renderer mid-measure or
        // mid-frame): swapping the template then would change the tree during a layout or render pass.
        private void OnChanged(object? sender, EventArgs e)
        {
            if (_pluginCalls > 0) Dispatcher.UIThread.Post(() => { if (_started) Apply(); });
            else Apply();
        }

        private void Unwrap()
        {
            if (_applied is null) return;
            _list.ItemTemplate = _builtIn;
            _builtIn = null;
            _applied = null;
        }

        private void Apply()
        {
            var want = _current;
            if (want is not null && !Supports(want, _surface)) want = null;
            if (ReferenceEquals(want, _applied)) return;   // nothing chosen, nothing applied: untouched
            if (want is null)
            {
                Unwrap();
                return;
            }
            _builtIn ??= _list.ItemTemplate;
            _applied = want;
            _list.ItemTemplate = new LineTemplate(_builtIn, want, _surface, _viewModel);
        }

        private static bool Supports(PluginLyricsRenderer renderer, LyricsSurface surface)
        {
            if (!IsLive(renderer)) return false;
            bool supports;
            var started = BeginCall();
            try { supports = renderer.Renderer.SupportsSurface(surface); }
            catch (Exception ex)
            {
                ReportFailure(renderer, ex);
                return false;
            }
            finally { EndCall(); }
            if (Overran(renderer, "SupportsSurface", started) is { } slow)
            {
                ReportFailure(renderer, slow);
                return false;
            }
            return supports;
        }
    }

    /// <summary>The XAML line template with the plugin's line as the button's content.</summary>
    private sealed class LineTemplate : IDataTemplate
    {
        private readonly IDataTemplate? _builtIn;
        private readonly PluginLyricsRenderer _renderer;
        private readonly LyricsSurface _surface;
        private readonly Func<LyricsViewModel?> _viewModel;

        public LineTemplate(IDataTemplate? builtIn, PluginLyricsRenderer renderer, LyricsSurface surface, Func<LyricsViewModel?> viewModel)
        {
            _builtIn = builtIn;
            _renderer = renderer;
            _surface = surface;
            _viewModel = viewModel;
        }

        public bool Match(object? data) => data is LyricLine;

        public Control? Build(object? param)
        {
            var shell = _builtIn?.Build(param);
            if (param is not LyricLine line || shell is not ContentControl button || !IsLive(_renderer)) return shell;
            ILyricsLineView? view;
            var started = BeginCall();
            try { view = _renderer.Renderer.CreateLine(ToInfo(line)); }
            catch (Exception ex)
            {
                ReportFailure(_renderer, ex);
                return shell;   // this line as Noctis draws it; the list switches back right after
            }
            finally { EndCall(); }
            if (Overran(_renderer, "CreateLine", started) is { } slow)
            {
                ReportFailure(_renderer, slow);
                return shell;
            }
            if (view is not null) button.Content = new PluginLyricLine(line, view, _renderer, _surface, _viewModel);
            return shell;
        }
    }

    /// <summary>The plain copy of a line a renderer gets.</summary>
    internal static LyricsLineInfo ToInfo(LyricLine line) => new(
        line.IsIntroPlaceholder ? "" : line.Text ?? "",
        line.Timestamp?.TotalMilliseconds,
        line.EndTimestamp?.TotalMilliseconds,
        Words(line.Words))
    {
        Voice = line.Voice switch
        {
            LyricVoice.Voice2 => LyricsLineVoice.Second,
            LyricVoice.Group => LyricsLineVoice.Group,
            _ => LyricsLineVoice.Main,
        },
        IsInterlude = line.IsIntroPlaceholder,
        BackgroundWords = Words(line.BackgroundWords),
        Romanization = string.IsNullOrWhiteSpace(line.Transliteration) ? null : line.Transliteration,
        RomanizationWords = Words(line.TransliterationWords),
        Translation = string.IsNullOrWhiteSpace(line.Translation) ? null : line.Translation,
    };

    private static IReadOnlyList<LyricsLineWord> Words(IReadOnlyList<WordTiming>? words)
    {
        if (words is null || words.Count == 0) return Array.Empty<LyricsLineWord>();
        var copy = new LyricsLineWord[words.Count];
        for (var i = 0; i < copy.Length; i++)
            copy[i] = new LyricsLineWord(words[i].Text, words[i].Start.TotalMilliseconds, words[i].End?.TotalMilliseconds);
        return Array.AsReadOnly(copy);
    }
}

/// <summary>
/// One line drawn by a plugin's <see cref="ILyricsLineView"/>. Noctis calls the view only from
/// here (measure and render), so whatever it throws is caught: the line stops drawing and the
/// renderer is reported (Noctis's own lines come back). Frames: the view asks for the next one by
/// returning true from Render; otherwise the line redraws only when its state, size or style changes.
/// What the view draws is clipped to the line's box (plus <see cref="ClipBleedEm"/> for a glow), so
/// it cannot paint over the rest of the window; the height it asks for is capped at
/// <see cref="MaxHeightEm"/>.
/// </summary>
internal sealed class PluginLyricLine : Control
{
    /// <summary>How far past its box (in font sizes) a line may draw: room for a glow or a lifted word.</summary>
    internal const double ClipBleedEm = 0.5;

    /// <summary>The tallest a line may measure, in font sizes (many wrapped rows plus every layer
    /// still fit); a taller ask is cut to it rather than stretching the list without end.</summary>
    internal const double MaxHeightEm = 40;

    private readonly LyricLine _line;
    private readonly ILyricsLineView _view;
    private readonly PluginLyricsRenderer _renderer;
    private readonly LyricsSurface _surface;
    private readonly Func<LyricsViewModel?> _viewModel;
    private readonly Action<TimeSpan> _onFrame;
    private LyricsViewModel? _vm;
    private bool _frameRequested;
    private bool _failed;

    static PluginLyricLine()
    {
        AffectsMeasure<PluginLyricLine>(TextElement.FontSizeProperty, TextElement.FontFamilyProperty);
    }

    public PluginLyricLine(LyricLine line, ILyricsLineView view, PluginLyricsRenderer renderer, LyricsSurface surface, Func<LyricsViewModel?> viewModel)
    {
        _line = line;
        _view = view;
        _renderer = renderer;
        _surface = surface;
        _viewModel = viewModel;
        _onFrame = OnFrame;
        ClipToBounds = false;
        // The line dims with its distance from the sung one, as Noctis's own layers do (same fade).
        Opacity = line.LineOpacity;
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromSeconds(0.5), Easing = new CubicEaseInOut() },
        };
    }

    internal ILyricsLineView View => _view;
    internal bool HasFailed => _failed;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _line.PropertyChanged += OnLineChanged;
        _vm = _viewModel();
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        Opacity = _line.LineOpacity;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _line.PropertyChanged -= OnLineChanged;
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = null;
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LyricLine.LineOpacity):
                Opacity = _line.LineOpacity;
                break;
            case nameof(LyricLine.IsActive):
            case nameof(LyricLine.IsLeading):
            case nameof(LyricLine.InterludeProgress):
            case nameof(LyricLine.IsRightToLeft):
                InvalidateVisual();
                break;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LyricsViewModel.LyricsPrimaryFg):
            case nameof(LyricsViewModel.LyricsAccentFg):
            case nameof(LyricsViewModel.ShowBackgroundVocals):
            case nameof(LyricsViewModel.ShowRomanization):
            case nameof(LyricsViewModel.ShowTranslations):
                InvalidateMeasure();
                InvalidateVisual();
                break;
        }
    }

    private LyricsLineStyle Style()
    {
        var vm = _vm ?? _viewModel();
        // The mini player draws its lyrics white on its own backdrop; the page and the panel in
        // the lyrics view's text colour.
        var fg = _surface == LyricsSurface.MiniPlayer ? Colors.White : ColorOf(vm?.LyricsPrimaryFg, Colors.White);
        return new LyricsLineStyle(
            GetValue(TextElement.FontFamilyProperty),
            GetValue(TextElement.FontSizeProperty),
            fg,
            ColorOf(vm?.LyricsAccentFg, fg),
            _surface,
            vm?.ShowBackgroundVocals ?? true,
            vm?.ShowRomanization ?? true,
            vm?.ShowTranslations ?? true);
    }

    private static Color ColorOf(IBrush? brush, Color fallback) => brush is ISolidColorBrush s ? s.Color : fallback;

    internal LyricsLineFrame Frame()
    {
        var vm = _vm ?? _viewModel();
        var position = vm?.LyricsClockMs ?? 0;
        LyricsLineState state;
        if (_line.Timestamp is not { } start) state = LyricsLineState.Unsynced;
        else if (_line.IsActive) state = LyricsLineState.Active;
        else state = position < start.TotalMilliseconds ? LyricsLineState.Upcoming : LyricsLineState.Past;
        return new LyricsLineFrame(position, state, _line.IsLeading, _line.InterludeProgress, _line.IsRightToLeft,
            vm?.Player.IsPlaying ?? false, Style());
    }

    private double FontSizeOrDefault()
    {
        var size = GetValue(TextElement.FontSizeProperty);
        return double.IsFinite(size) && size > 0 ? size : 12;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_failed || !PluginLyricLines.IsLive(_renderer)) return default;
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 1000;
        Size size;
        var started = PluginLyricLines.BeginCall();
        try { size = _view.Measure(width, Style()); }
        catch (Exception ex)
        {
            Fail(ex);
            return default;
        }
        finally { PluginLyricLines.EndCall(); }
        if (PluginLyricLines.Overran(_renderer, "Measure", started) is { } slow)
        {
            Fail(slow);
            return default;
        }
        var w = double.IsFinite(size.Width) ? Math.Clamp(size.Width, 0, width) : width;
        var h = double.IsFinite(size.Height) ? Math.Clamp(size.Height, 0, MaxHeightEm * FontSizeOrDefault()) : 0;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The compositor applies the clip around everything Render records, whatever the view
        // pushes or leaves open on the context.
        var clip = new Rect(finalSize).Inflate(ClipBleedEm * FontSizeOrDefault());
        if (Clip is not RectangleGeometry current || current.Rect != clip) Clip = new RectangleGeometry(clip);
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        if (_failed || !PluginLyricLines.IsLive(_renderer)) return;
        bool more;
        var started = PluginLyricLines.BeginCall();
        try { more = _view.Render(context, Bounds.Size, Frame()); }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }
        finally { PluginLyricLines.EndCall(); }
        if (PluginLyricLines.Overran(_renderer, "Render", started) is { } slow)
        {
            Fail(slow);
            return;
        }
        if (more && !_frameRequested && TopLevel.GetTopLevel(this) is { } top)
        {
            _frameRequested = true;
            top.RequestAnimationFrame(_onFrame);
        }
    }

    private void OnFrame(TimeSpan _)
    {
        _frameRequested = false;
        if (!_failed && PluginLyricLines.IsLive(_renderer) && IsEffectivelyVisible && this.IsAttachedToVisualTree()) InvalidateVisual();
    }

    private void Fail(Exception ex)
    {
        if (_failed) return;
        _failed = true;
        PluginLyricLines.ReportFailure(_renderer, ex);
    }
}
