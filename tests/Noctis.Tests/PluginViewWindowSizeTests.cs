using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Plugins;
using Noctis.Services.Plugins;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// API 1.5: a plugin view's window opens at the size the view asks for. Live (2560x1440 at 125%),
/// Album Roulette and A-B Loop resized their window in OnAttachedToVisualTree and still opened at
/// the host's 1000x680: Window.ShowCore attaches the content inside its first layout pass and then
/// writes the size it started with back to Width/Height.
/// </summary>
public class PluginViewWindowSizeTests : IDisposable
{
    private readonly PluginSandbox _box = new();
    private readonly List<Window> _windows = new();

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        _box.Dispose();
    }

    private const string Id = "dev.test.viewsize";

    private sealed class SizedView : IPluginViewProvider
    {
        public string Name => "Sized";
        public Func<Control> Create = () => new Border();
        public int Created;
        public Control CreateView() { Created++; return Create(); }
        public Size? PreferredWindowSize { get; set; }
        public Size? MinWindowSize { get; set; }
        public SizeToContent WindowSizeToContent { get; set; }
    }

    /// <summary>Built against the 1.3 kit: none of the size members.</summary>
    private sealed class OldView : IPluginViewProvider
    {
        public string Name => "Old";
        public Control CreateView() => new Border();
    }

    private sealed class ThrowingHint : IPluginViewProvider
    {
        public string Name => "Bad";
        public Control CreateView() => new Border();
        public Size? PreferredWindowSize => throw new InvalidOperationException("boom");
    }

    /// <summary>The 1.4 plugins' way: resizing the window while the view is being attached.</summary>
    private sealed class ResizesItsWindow : Border
    {
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (TopLevel.GetTopLevel(this) is Window w) { w.Width = 440; w.Height = 700; }
        }
    }

    private sealed class ThrowsOnMeasure : Control
    {
        protected override Size MeasureOverride(Size availableSize) => throw new InvalidOperationException("layout");
    }

    private (PluginHost Host, PluginViewEntry Entry) Register(IPluginViewProvider provider)
    {
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost();
        var script = new ScriptedPlugin { OnInit = h => h.RegisterView(provider) };
        var plugin = _box.AddInProcess(host, script, PluginSandbox.Manifest(id: Id, apiVersion: "1.5"));
        Assert.Equal(PluginStatus.Running, plugin.Status);
        return (host, plugin.Views.Single());
    }

    private PluginViewWindow Show(PluginViewEntry entry, double scaling = 1.0, Size? workArea = null)
    {
        var window = new PluginViewWindow(entry, new Border(), workArea);
        _windows.Add(window);
        if (Math.Abs(scaling - 1) > 1e-9) window.SetRenderScaling(scaling); // before Show, like a window opened on a 125% screen
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void Window_OpensAtTheHintedSize_InDips(double scaling)
    {
        var (_, entry) = Register(new SizedView { PreferredWindowSize = new Size(440, 700), MinWindowSize = new Size(360, 560) });
        var window = Show(entry, scaling);

        Assert.Equal(scaling, window.RenderScaling);
        Assert.Equal(new Size(440, 700), window.ClientSize);
        Assert.Equal((440d, 700d), (window.Width, window.Height));
        Assert.Equal((360d, 560d), (window.MinWidth, window.MinHeight));
        // In device pixels that is 550x875 at 125%, not the 1250x850 of the default.
        Assert.Equal(new PixelSize((int)(440 * scaling), (int)(700 * scaling)), PixelSize.FromSize(window.ClientSize, scaling));
    }

    [AvaloniaTheory]
    [InlineData(0)] // built against the 1.3 kit
    [InlineData(1)] // 1.5 members left at null
    [InlineData(2)] // a hint getter that throws
    public void Window_WithoutAHint_KeepsTheOldDefault(int kind)
    {
        IPluginViewProvider provider = kind switch { 0 => new OldView(), 1 => new SizedView(), _ => new ThrowingHint() };
        var (_, entry) = Register(provider);
        var window = Show(entry);
        Assert.Equal(PluginViewWindow.DefaultSize, window.ClientSize);
        Assert.Equal((480d, 360d), (window.MinWidth, window.MinHeight));
    }

    [AvaloniaFact]
    public void Window_EnforcesTheMinimum()
    {
        var (_, entry) = Register(new SizedView { PreferredWindowSize = new Size(460, 500), MinWindowSize = new Size(380, 320) });
        var window = Show(entry);
        Assert.Equal(new Size(460, 500), window.ClientSize);

        window.Width = 100;
        window.Height = 100;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new Size(380, 320), window.ClientSize);

        // The user can make it bigger again.
        window.Width = 900;
        window.Height = 800;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new Size(900, 800), window.ClientSize);
    }

    [AvaloniaFact]
    public void Window_HintWithoutAMinimum_IsNotClampedByTheDefaultMinimum()
    {
        // 440 wide is under the default 480 minimum: the minimum comes down to it.
        var (_, entry) = Register(new SizedView { PreferredWindowSize = new Size(440, 300) });
        var window = Show(entry);
        Assert.Equal(new Size(440, 300), window.ClientSize);
        Assert.Equal((440d, 300d), (window.MinWidth, window.MinHeight));
    }

    [AvaloniaFact]
    public void Window_SizeToContent_TakesTheViewsSize()
    {
        var view = new SizedView
        {
            Create = () => new Border { Width = 300, Height = 220 },
            MinWindowSize = new Size(200, 150),
            WindowSizeToContent = SizeToContent.WidthAndHeight,
        };
        var (host, entry) = Register(view);
        PluginViewWindow? window = null;
        host.ViewPresenter = (e, c) =>
        {
            window = new PluginViewWindow(e, c);
            _windows.Add(window);
            window.Show();
            return true;
        };
        Assert.True(entry.Open());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new Size(300, 220), window!.ClientSize);
    }

    [AvaloniaFact]
    public void ResizingTheWindowWhileAttached_IsUndoneByShow_WhichIsWhyTheHintExists()
    {
        var (_, entry) = Register(new OldView());
        var window = new PluginViewWindow(entry, new ResizesItsWindow());
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PluginViewWindow.DefaultSize, window.ClientSize);
    }

    [AvaloniaFact]
    public void Present_OpensTheViewAtItsHint()
    {
        var view = new SizedView { PreferredWindowSize = new Size(440, 700) };
        var (host, entry) = Register(view);
        Control? shown = null;
        host.ViewPresenter = (e, c) => { shown = c; return PluginViewWindow.Present(e, c); };
        Assert.True(entry.Open());
        Dispatcher.UIThread.RunJobs();
        var window = Assert.IsType<PluginViewWindow>(TopLevel.GetTopLevel(shown));
        _windows.Add(window);
        Assert.Equal(new Size(440, 700), window.ClientSize);
    }

    [AvaloniaFact]
    public void Present_WhenTheViewThrowsDuringLayout_LeavesNoDeadWindowBehind()
    {
        var view = new SizedView { Create = () => new ThrowsOnMeasure() };
        var (host, entry) = Register(view);
        host.ViewPresenter = PluginViewWindow.Present;

        Assert.False(entry.Open());
        // Before: Reactivate still pointed at the window that never showed, so every later Open
        // returned true without creating or showing anything.
        Assert.Null(entry.Reactivate);

        Control? shown = null;
        view.Create = () => shown = new Border();
        Assert.True(entry.Open());
        Assert.Equal(2, view.Created);
        var window = Assert.IsType<PluginViewWindow>(TopLevel.GetTopLevel(shown));
        _windows.Add(window);
        Assert.True(window.IsVisible);
    }

    [Theory]
    // No hint: the default, minimum 480x360.
    [InlineData(double.NaN, double.NaN, double.NaN, double.NaN, 1000, 680, 480, 360)]
    // Smaller than the default minimum: the minimum comes down to the hint.
    [InlineData(440, 300, double.NaN, double.NaN, 440, 300, 440, 300)]
    // A minimum above the hint wins.
    [InlineData(300, 200, 380, 320, 380, 320, 380, 320)]
    // Silly values: per axis default / floor.
    [InlineData(-5, 0, 10, 10, 1000, 680, 200, 150)]
    [InlineData(double.PositiveInfinity, 500, double.NaN, double.NaN, 1000, 500, 480, 360)]
    public void Resolve_Axes(double pw, double ph, double mw, double mh, double w, double h, double minW, double minH)
    {
        Size? preferred = double.IsNaN(pw) ? null : new Size(pw, ph);
        Size? min = double.IsNaN(mw) ? null : new Size(mw, mh);
        var r = PluginViewWindow.Resolve(new PluginViewSizeHint(preferred, min, SizeToContent.Manual), null);
        Assert.Equal((w, h, minW, minH), (r.Width, r.Height, r.MinWidth, r.MinHeight));
    }

    [Fact]
    public void Resolve_FitsTheScreensWorkArea()
    {
        // 1280x720 at 100%, taskbar 40: the default window and a too-tall hint fit inside.
        var area = new Size(1280, 680);
        Assert.Equal((1000d, 680d), Size2(PluginViewWindow.Resolve(PluginViewSizeHint.None, area)));
        Assert.Equal((440d, 680d), Size2(PluginViewWindow.Resolve(new PluginViewSizeHint(new Size(440, 900), new Size(360, 800), SizeToContent.Manual), area)));
        static (double, double) Size2(PluginViewWindow.WindowSize s) => (s.Width, s.Height);
    }
}
