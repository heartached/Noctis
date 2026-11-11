using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Plugin API 1.8 lyrics renderers: registering lists a renderer but never switches to it, the
/// user's choice is stored and survives the plugin going away, a renderer that throws hands the
/// lines back to Noctis and stops its plugin, an older host refuses the call, lyrics effects keep
/// their slot, and with no renderer chosen the lyrics lists keep the exact template their XAML
/// declares. A small renderer used only here draws a line from its word timing.
/// </summary>
[Collection("LyricsWordEffect")] // the renderer and effect slots are app-wide
public class PluginLyricsRendererTests : IDisposable
{
    private const string Id = "dev.test.plugin";
    private readonly PluginSandbox _box = new();

    public void Dispose()
    {
        PluginLyricLines.Set(null);
        LyricsWordEffect.Set(null);
        _box.Dispose();
    }

    private (PluginHost Host, LoadedPlugin Plugin, ScriptedPlugin Script) Load(ILyricsRenderer renderer)
    {
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost(appVersion: "1.7.0");
        var script = new ScriptedPlugin { OnInit = h => h.RegisterLyricsRenderer(renderer) };
        var loaded = _box.AddInProcess(host, script, PluginSandbox.Manifest(apiVersion: "1.8", minAppVersion: "1.7.0"));
        return (host, loaded, script);
    }

    private static LyricLine Line(string text, double startMs, params (string Text, double Start, double End)[] words) => new()
    {
        Text = text,
        Timestamp = TimeSpan.FromMilliseconds(startMs),
        Words = words.Length == 0 ? null : words.Select(w => new WordTiming
        {
            Text = w.Text, Start = TimeSpan.FromMilliseconds(w.Start), End = TimeSpan.FromMilliseconds(w.End),
        }).ToList(),
    };

    /// <summary>A lyrics list as the three views declare it: a Button per line (seek, a11y), its
    /// content Noctis's own drawing.</summary>
    private static (ItemsControl List, IDataTemplate Template) List(params LyricLine[] lines)
    {
        var template = new FuncDataTemplate<LyricLine>((line, _) => new Button
        {
            Classes = { "lyric-line-btn" },
            Content = new TextBlock { Text = line.Text, Tag = "built-in" },
        });
        var list = new ItemsControl { ItemsSource = lines, ItemTemplate = template, FontSize = 46, MaxWidth = 640 };
        PluginLyricLines.Attach(list, LyricsSurface.Page, () => null);
        return (list, template);
    }

    private static object? ContentOf(ItemsControl list, int index)
        => (list.ContainerFromIndex(index) as ContentPresenter)?.Child is Button b ? b.Content : null;

    // ── The kit ──

    [Fact]
    public void TheKit_IsApi18_AndAnOlderHostRefusesTheCall()
    {
        Assert.Equal("1.8", PluginApi.Version);
        IPluginHost old = new OldHost();
        var ex = Assert.Throws<NotSupportedException>(() => old.RegisterLyricsRenderer(new TimedWords()));
        Assert.Contains("1.8", ex.Message);
        Assert.Contains("minAppVersion", ex.Message);
        // A renderer that does not say which surfaces it draws on draws on all of them.
        ILyricsRenderer minimal = new Minimal();
        Assert.True(minimal.SupportsSurface(LyricsSurface.MiniPlayer));
    }

    [Fact]
    public void ALine_IsCopiedWithItsWordsLayersAndVoice()
    {
        var line = Line("Hello world", 1000, ("Hello ", 1000, 1400), ("world", 1400, 2000));
        line.EndTimestamp = TimeSpan.FromMilliseconds(2100);
        line.Voice = LyricVoice.Voice2;
        line.BackgroundWords = new List<WordTiming> { new() { Text = "(ooh)", Start = TimeSpan.FromMilliseconds(1500) } };
        line.Translation = "Hallo Welt";
        line.Transliteration = "  ";

        var info = PluginLyricLines.ToInfo(line);

        Assert.Equal("Hello world", info.Text);
        Assert.Equal((1000d, 2100d), (info.StartMs!.Value, info.EndMs!.Value));
        Assert.Equal(new[] { new LyricsLineWord("Hello ", 1000, 1400), new LyricsLineWord("world", 1400, 2000) }, info.Words);
        Assert.Equal(LyricsLineVoice.Second, info.Voice);
        Assert.Equal(new LyricsLineWord("(ooh)", 1500, null), Assert.Single(info.BackgroundWords));
        Assert.Equal("Hallo Welt", info.Translation);
        Assert.Null(info.Romanization);
        Assert.Empty(info.RomanizationWords);
        Assert.False(info.IsInterlude);

        var dots = PluginLyricLines.ToInfo(new LyricLine { Text = "...", IsIntroPlaceholder = true, Timestamp = TimeSpan.Zero });
        Assert.True(dots.IsInterlude);
        Assert.Equal("", dots.Text);
    }

    // ── Registration and the user's choice ──

    [AvaloniaFact]
    public void Registering_ListsTheRenderer_ButNeverSwitchesToIt()
    {
        var (host, loaded, _) = Load(new TimedWords());

        var entry = Assert.Single(host.LyricsRenderers);
        Assert.Equal(Id + "/Timed Words", entry.Key);
        Assert.Equal("Timed Words (Test Plugin)", entry.Label);
        Assert.Contains("Lyrics renderer: Timed Words", loaded.Extensions);
        Assert.Null(PluginLyricLines.Current);
        Assert.Equal("", _box.Settings.LyricsRenderer);
    }

    [AvaloniaFact]
    public void TheChoice_IsStored_SurvivesThePluginBeingOff_AndComesBackWithIt()
    {
        var (host, loaded, _) = Load(new TimedWords());
        var key = host.LyricsRenderers[0].Key;

        host.SelectLyricsRenderer(key);
        Assert.Equal(key, _box.Settings.LyricsRenderer);
        Assert.Same(host.LyricsRenderers[0], PluginLyricLines.Current);

        host.SetEnabled(loaded, false);
        Assert.Null(PluginLyricLines.Current);           // Noctis's own lines meanwhile
        Assert.Empty(host.LyricsRenderers);
        Assert.Equal(key, _box.Settings.LyricsRenderer); // the choice is kept

        host.SetEnabled(loaded, true);
        Assert.Same(host.LyricsRenderers[0], PluginLyricLines.Current);

        host.SelectLyricsRenderer("");
        Assert.Null(PluginLyricLines.Current);
        Assert.Equal("", _box.Settings.LyricsRenderer);
    }

    [Fact]
    public async Task TheChoice_IsSavedWithTheSettings()
    {
        var persistence = new PersistenceService(_box.Root);
        Assert.Equal("", new AppSettings().LyricsRenderer);
        await persistence.SaveSettingsAsync(new AppSettings { LyricsRenderer = Id + "/Timed Words" });
        Assert.Equal(Id + "/Timed Words", (await persistence.LoadSettingsAsync()).LyricsRenderer);
    }

    [AvaloniaFact]
    public void ALyricsEffect_KeepsItsSlot_WhenARendererIsChosen()
    {
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost(appVersion: "1.7.0");
        var effect = new NoMotion();
        var script = new ScriptedPlugin { OnInit = h => { h.RegisterLyricsEffect(effect); h.RegisterLyricsRenderer(new TimedWords()); } };
        _box.AddInProcess(host, script, PluginSandbox.Manifest(apiVersion: "1.8", minAppVersion: "1.7.0"));
        var before = LyricsWordEffect.Current;
        Assert.NotNull(before);

        host.SelectLyricsRenderer(host.LyricsRenderers[0].Key);

        Assert.NotNull(PluginLyricLines.Current);
        Assert.Same(before, LyricsWordEffect.Current); // still used by Noctis's own lines elsewhere
    }

    [AvaloniaFact]
    public void SettingsPicker_ShowsOnlyWithARenderer_StoresThePick_AndKeepsItWhileThePluginIsOff()
    {
        using var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoPlayHistory());
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost(appVersion: "1.7.0");
        vm.Plugins = host;
        Assert.False(vm.HasLyricsRendererChoice); // no renderer plugin: Settings as it always was
        Assert.Equal("", vm.SelectedLyricsRendererOption!.Key);

        var loaded = _box.AddInProcess(host, new ScriptedPlugin { OnInit = h => h.RegisterLyricsRenderer(new TimedWords()) },
            PluginSandbox.Manifest(apiVersion: "1.8", minAppVersion: "1.7.0"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.HasLyricsRendererChoice);
        Assert.Equal(new[] { "Noctis", "Timed Words (Test Plugin)" }, vm.LyricsRendererOptions.Select(o => o.Label));
        Assert.Equal("", vm.SelectedLyricsRendererOption!.Key); // listed, not chosen
        Assert.Null(PluginLyricLines.Current);

        vm.SelectedLyricsRendererOption = vm.LyricsRendererOptions[1];
        Assert.Equal(Id + "/Timed Words", vm.LyricsRenderer);
        Assert.Equal(Id + "/Timed Words", _box.Settings.LyricsRenderer);
        Assert.NotNull(PluginLyricLines.Current);

        host.SetEnabled(loaded, false);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(PluginLyricLines.Current);
        Assert.True(vm.HasLyricsRendererChoice);
        Assert.Equal(Id + "/Timed Words", vm.SelectedLyricsRendererOption!.Key);
        Assert.Equal("Timed Words (plugin off)", vm.SelectedLyricsRendererOption.Label);

        vm.SelectedLyricsRendererOption = vm.LyricsRendererOptions[0];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", _box.Settings.LyricsRenderer);
        Assert.False(vm.HasLyricsRendererChoice);
    }

    private sealed class NoPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    // ── The lyrics lists ──

    [AvaloniaFact]
    public void WithNoRendererChosen_TheListKeepsItsOwnTemplate_EvenWithARendererInstalled()
    {
        var (list, template) = List(Line("one", 0), Line("two", 1000));
        var window = new Window { Content = list, Width = 800, Height = 400 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Same(template, list.ItemTemplate);
            Assert.IsType<TextBlock>(ContentOf(list, 0));

            var (host, _, _) = Load(new TimedWords()); // installed and running, not chosen
            Dispatcher.UIThread.RunJobs();
            Assert.Same(template, list.ItemTemplate);
            Assert.False(PluginLyricLines.IsWrapped(list));
            Assert.Equal("built-in", ((TextBlock)ContentOf(list, 1)!).Tag);
            Assert.NotEmpty(host.LyricsRenderers);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheRealLyricsViews_KeepTheirXamlTemplate_WhenNoRendererIsChosen()
    {
        var panel = new LyricsPanelView();
        var items = panel.FindControl<ItemsControl>("PanelItemsControl")!;
        var declared = items.ItemTemplate;
        Assert.NotNull(declared);
        var window = new Window { Content = panel, Width = 400, Height = 600 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Same(declared, items.ItemTemplate);
            Assert.False(PluginLyricLines.IsWrapped(items));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AChosenRenderer_DrawsInsideNoctissButton_AndSwitchingBackRestoresTheTemplateItself()
    {
        var (list, template) = List(
            Line("Hello world", 1000, ("Hello ", 1000, 1400), ("world", 1400, 2000)),
            Line("plain line", 3000));
        var window = new Window { Content = list, Width = 800, Height = 400 };
        window.Show();
        try
        {
            var (host, _, _) = Load(new TimedWords());
            host.SelectLyricsRenderer(host.LyricsRenderers[0].Key);
            Dispatcher.UIThread.RunJobs();

            Assert.True(PluginLyricLines.IsWrapped(list));
            var line = Assert.IsType<PluginLyricLine>(ContentOf(list, 0));
            // The button is still Noctis's (click-to-seek, name, scale and blur live on it).
            Assert.Contains("lyric-line-btn", ((Button)((ContentPresenter)list.ContainerFromIndex(0)!).Child!).Classes);
            // Noctis lays the line out with the height the plugin measured.
            window.UpdateLayout();
            var view = (TimedWordsLine)line.View;
            Assert.True(view.MeasuredWidth is > 0 and <= 640, $"measured at {view.MeasuredWidth}");
            Assert.True(Math.Abs(view.Height - line.Bounds.Height) <= 1, $"{view.Height} measured, {line.Bounds.Height} laid out"); // layout rounding
            Assert.Equal(46, view.FontSize);

            host.SelectLyricsRenderer("");
            Dispatcher.UIThread.RunJobs();
            Assert.Same(template, list.ItemTemplate);
            Assert.IsType<TextBlock>(ContentOf(list, 0));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ThePluginLine_IsDrawnFromItsWordTiming()
    {
        var timed = Line("Hello world", 1000, ("Hello ", 1000, 1400), ("world", 1400, 2000));
        var (list, _) = List(timed);
        var window = new Window { Content = list, Width = 800, Height = 400 };
        window.Show();
        try
        {
            var (host, _, _) = Load(new TimedWords());
            host.SelectLyricsRenderer(host.LyricsRenderers[0].Key);
            Dispatcher.UIThread.RunJobs();
            var line = Assert.IsType<PluginLyricLine>(ContentOf(list, 0));
            var view = (TimedWordsLine)line.View;
            window.UpdateLayout();

            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(800, 200));
            bmp.Render(line);
            Assert.Equal(LyricsLineState.Upcoming, view.LastFrame.State); // no view model: position 0
            Assert.Equal(0, view.LastSung);
            Assert.False(view.LastAskedForMore);

            timed.IsActive = true;
            bmp.Render(line);
            Assert.Equal(LyricsLineState.Active, view.LastFrame.State);
            Assert.True(view.LastAskedForMore);
            Assert.Equal(2, view.LastDrawn); // both words, the first lit

            // The words light by their own times.
            Assert.Equal(0, TimedWordsLine.SungAt(view.Info, 999));
            Assert.Equal(1, TimedWordsLine.SungAt(view.Info, 1000));
            Assert.Equal(1, TimedWordsLine.SungAt(view.Info, 1399));
            Assert.Equal(2, TimedWordsLine.SungAt(view.Info, 1400));
        }
        finally { window.Close(); }
    }

    // ── Failures ──

    [AvaloniaTheory]
    [InlineData("create")]
    [InlineData("measure")]
    [InlineData("render")]
    public void ARendererThatThrows_HandsTheLinesBack_StopsItsPlugin_AndKeepsTheChoice(string where)
    {
        var (list, template) = List(Line("Hello world", 1000, ("Hello ", 1000, 1400), ("world", 1400, 2000)));
        var window = new Window { Content = list, Width = 800, Height = 400 };
        window.Show();
        try
        {
            var (host, loaded, _) = Load(new Throwing(where));
            var notices = new List<PluginNotice>();
            host.NotificationRequested += (_, n) => notices.Add(n);
            var key = host.LyricsRenderers[0].Key;
            host.SelectLyricsRenderer(key);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            if (ContentOf(list, 0) is PluginLyricLine drawn)
            {
                using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(800, 200));
                bmp.Render(drawn);
            }
            Dispatcher.UIThread.RunJobs();

            Assert.Null(PluginLyricLines.Current);
            Assert.Same(template, list.ItemTemplate);
            Assert.IsType<TextBlock>(ContentOf(list, 0));
            Assert.Equal(PluginStatus.Failed, loaded.Status);
            Assert.Contains("Lyrics renderer", loaded.Error);
            Assert.Contains(notices, n => n.PluginName == "Test Plugin");
            Assert.Equal(key, _box.Settings.LyricsRenderer); // never switched for the user
        }
        finally { window.Close(); }
    }

    // ── A renderer used only by these tests ──

    /// <summary>Draws each word of a line in full once its start has passed, dim before.</summary>
    private sealed class TimedWords : ILyricsRenderer
    {
        public string Name => "Timed Words";
        public ILyricsLineView? CreateLine(LyricsLineInfo line) => new TimedWordsLine(line);
    }

    private sealed class TimedWordsLine : ILyricsLineView
    {
        private TextLayout[] _layouts = Array.Empty<TextLayout>();
        private double[] _x = Array.Empty<double>();
        private IBrush? _brush;

        public TimedWordsLine(LyricsLineInfo info) => Info = info;

        public LyricsLineInfo Info { get; }
        public double MeasuredWidth, Height, FontSize;
        public LyricsLineFrame LastFrame;
        public int LastSung, LastDrawn;
        public bool LastAskedForMore;

        public static int SungAt(LyricsLineInfo info, double positionMs)
        {
            var n = 0;
            foreach (var w in info.Words) if (positionMs >= w.StartMs) n++;
            return n;
        }

        public Size Measure(double availableWidth, in LyricsLineStyle style)
        {
            MeasuredWidth = availableWidth;
            FontSize = style.FontSize;
            _brush = new SolidColorBrush(style.Foreground);
            var face = new Typeface(style.FontFamily, FontStyle.Normal, FontWeight.Bold);
            var texts = Info.Words.Count > 0 ? Info.Words.Select(w => w.Text).ToArray() : new[] { Info.Text };
            _layouts = new TextLayout[texts.Length];
            for (var i = 0; i < texts.Length; i++) _layouts[i] = new TextLayout(texts[i], face, style.FontSize, _brush);
            _x = new double[_layouts.Length];
            double x = 0, h = 0;
            for (var i = 0; i < _layouts.Length; i++)
            {
                _x[i] = x;
                x += _layouts[i].WidthIncludingTrailingWhitespace;
                h = Math.Max(h, _layouts[i].Height);
            }
            Height = h;
            return new Size(Math.Min(x, availableWidth), h);
        }

        public bool Render(DrawingContext context, Size size, in LyricsLineFrame frame)
        {
            LastFrame = frame;
            var sung = frame.State == LyricsLineState.Active || frame.State == LyricsLineState.Past
                ? (Info.Words.Count > 0 ? SungAt(Info, frame.PositionMs) : _layouts.Length)
                : 0;
            if (frame.State == LyricsLineState.Active && Info.Words.Count > 0 && sung == 0) sung = 1;
            for (var i = 0; i < _layouts.Length; i++)
            {
                using (context.PushOpacity(i < sung ? 1 : 0.4))
                    _layouts[i].Draw(context, new Point(_x[i], 0));
            }
            LastDrawn = _layouts.Length;
            LastSung = sung;
            LastAskedForMore = frame.State == LyricsLineState.Active && sung < Math.Max(1, Info.Words.Count);
            return LastAskedForMore;
        }
    }

    private sealed class Minimal : ILyricsRenderer
    {
        public string Name => "Minimal";
        public ILyricsLineView? CreateLine(LyricsLineInfo line) => null;
    }

    private sealed class Throwing : ILyricsRenderer
    {
        private readonly string _where;
        public Throwing(string where) => _where = where;
        public string Name => "Throws";
        public ILyricsLineView? CreateLine(LyricsLineInfo line)
            => _where == "create" ? throw new InvalidOperationException("boom") : new Line(_where);

        private sealed class Line : ILyricsLineView
        {
            private readonly string _where;
            public Line(string where) => _where = where;
            public Size Measure(double availableWidth, in LyricsLineStyle style)
                => _where == "measure" ? throw new InvalidOperationException("boom") : new Size(100, 40);
            public bool Render(DrawingContext context, Size size, in LyricsLineFrame frame)
                => throw new InvalidOperationException("boom");
        }
    }

    private sealed class NoMotion : ILyricsEffect
    {
        public string Name => "Still";
        public void GetLineDirections(IReadOnlyList<string> lines, Span<bool> rightToLeft) { }
        public LyricsWordMode GetMode(in LyricsWord word) => LyricsWordMode.Native;
        public LyricsLetterPose GetPose(in LyricsWord word, int letter) => LyricsLetterPose.Rest;
    }

    private sealed class OldHost : IPluginHost
    {
        public string AppVersion => "1.6.9";
        public string DataDirectory => "";
        public INowPlaying NowPlaying => null!;
        public IBeatSource Beat => null!;
        public ISpectrumSource Spectrum => null!;
        public void Log(string message) { }
        public void RegisterVisualLayer(IVisualLayerProvider provider) { }
    }
}
