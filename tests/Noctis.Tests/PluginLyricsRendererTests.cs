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

    private IDisposable? _registration; // what the last Load's RegisterLyricsRenderer returned

    private (PluginHost Host, LoadedPlugin Plugin, ScriptedPlugin Script) Load(ILyricsRenderer renderer, string pluginName = "Test Plugin")
    {
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost(appVersion: "1.7.0");
        var script = new ScriptedPlugin { OnInit = h => _registration = h.RegisterLyricsRenderer(renderer) };
        var loaded = _box.AddInProcess(host, script, PluginSandbox.Manifest(name: pluginName, apiVersion: "1.8", minAppVersion: "1.7.0"));
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
        Assert.True(PluginApi.Minor >= 8); // 1.9 added the media servers' albums on top
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

    // ── What a hostile or buggy renderer cannot do ──

    /// <summary>A list on screen with <paramref name="renderer"/> chosen.</summary>
    private (ItemsControl List, IDataTemplate Template, Window Window, PluginHost Host, LoadedPlugin Plugin) Shown(ILyricsRenderer renderer, int lines = 1)
    {
        var (list, template) = List(Enumerable.Range(0, lines).Select(i => Line("Hello world " + i, 1000 + i * 1000)).ToArray());
        var window = new Window { Content = list, Width = 800, Height = 400 };
        window.Show();
        var (host, loaded, _) = Load(renderer);
        host.SelectLyricsRenderer(host.LyricsRenderers[0].Key);
        Dispatcher.UIThread.RunJobs();
        return (list, template, window, host, loaded);
    }

    private static void RenderOnce(Control control)
    {
        using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(800, 200));
        bmp.Render(control);
    }

    [AvaloniaTheory]
    [InlineData(1e308, 1e308)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity)]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(-50, -50)]
    public void AnAbsurdSize_IsBounded_AndTheListStillLaysOut(double width, double height)
    {
        var renderer = new Hostile { MeasureWith = _ => new Size(width, height) };
        var (list, _, window, _, loaded) = Shown(renderer, lines: 3);
        try
        {
            window.UpdateLayout(); // must not throw (an infinite list height would)
            var line = Assert.IsType<PluginLyricLine>(ContentOf(list, 0));
            Assert.InRange(line.Bounds.Width, 0, 640);
            Assert.InRange(line.Bounds.Height, 0, PluginLyricLine.MaxHeightEm * 46);
            Assert.True(double.IsFinite(list.DesiredSize.Height));
            Assert.Equal(PluginStatus.Running, loaded.Status); // bounded, not a fault
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ALine_IsClippedToItsOwnBox_SoItCannotPaintOverTheWindow()
    {
        var renderer = new Hostile
        {
            MeasureWith = _ => new Size(200, 40),
            RenderWith = (ctx, _) => { ctx.FillRectangle(Brushes.Red, new Rect(-5000, -5000, 10000, 10000)); return false; },
        };
        var (list, _, window, _, _) = Shown(renderer);
        try
        {
            window.UpdateLayout();
            var line = Assert.IsType<PluginLyricLine>(ContentOf(list, 0));
            var clip = Assert.IsType<RectangleGeometry>(line.Clip).Rect;
            var bounds = new Rect(line.Bounds.Size);
            Assert.True(clip.Contains(bounds), $"clip {clip} must hold the line {bounds}");
            var bleed = PluginLyricLine.ClipBleedEm * 46;
            Assert.True(clip.Left >= -bleed - 0.5 && clip.Top >= -bleed - 0.5
                        && clip.Right <= bounds.Right + bleed + 0.5 && clip.Bottom <= bounds.Bottom + bleed + 0.5,
                $"clip {clip} reaches past the line {bounds}");
            RenderOnce(line);
            Assert.True(renderer.Renders > 0); // it drew; whatever it drew stays inside the clip
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ARenderThatLeavesAStateOpen_DoesNotBreakTheFrame()
    {
        var renderer = new Hostile { RenderWith = (ctx, _) => { ctx.PushOpacity(0.5); ctx.PushTransform(Matrix.CreateScale(3, 3)); return false; } };
        var (list, _, window, _, loaded) = Shown(renderer);
        try
        {
            window.UpdateLayout();
            var line = Assert.IsType<PluginLyricLine>(ContentOf(list, 0));
            // The app's own path: the compositor records the line's frame.
            var before = renderer.Renders;
            line.InvalidateVisual();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Assert.True(renderer.Renders > before, "the compositor drew the line");
            // The states it left open stay in the line's own recording: the clip still holds.
            Assert.IsType<RectangleGeometry>(line.Clip);
            Assert.Equal(PluginStatus.Running, loaded.Status);
            // (A RenderTargetBitmap of the line, Avalonia's immediate renderer, throws "Wrong
            // Push/Pop state order" here; Noctis never draws the lyrics lists that way.)
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("create")]
    [InlineData("measure")]
    [InlineData("render")]
    public void ARendererThatHoldsTheUiThread_IsDropped_LikeAnySlowCallback(string where)
    {
        var (list, template) = List(Line("Hello world", 1000));
        var window = new Window { Content = list, Width = 800, Height = 400 };
        window.Show();
        try
        {
            var renderer = new Hostile();
            var (host, loaded, _) = Load(renderer);
            host.CallbackBudget = TimeSpan.FromMilliseconds(40);
            void Slow() => Thread.Sleep(150);
            if (where == "create") renderer.OnCreate = Slow;
            if (where == "measure") renderer.MeasureWith = _ => { Slow(); return new Size(100, 40); };
            if (where == "render") renderer.RenderWith = (_, _) => { Slow(); return false; };
            host.SelectLyricsRenderer(host.LyricsRenderers[0].Key);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            if (ContentOf(list, 0) is PluginLyricLine drawn) RenderOnce(drawn);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(PluginLyricLines.Current);
            Assert.Same(template, list.ItemTemplate);
            Assert.Equal(PluginStatus.Failed, loaded.Status);
            Assert.Contains("blocked Noctis", loaded.Error);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AWithdrawnRenderer_IsNeverCalledAgain_EvenByALineStillAround()
    {
        var renderer = new Hostile();
        var (list, template, window, _, _) = Shown(renderer);
        try
        {
            window.UpdateLayout();
            var line = Assert.IsType<PluginLyricLine>(ContentOf(list, 0));
            RenderOnce(line);
            var (measures, renders) = (renderer.Measures, renderer.Renders);

            _registration!.Dispose();               // the plugin withdraws it
            Dispatcher.UIThread.RunJobs();
            Assert.Same(template, list.ItemTemplate);

            line.InvalidateMeasure();
            line.Measure(new Size(640, double.PositiveInfinity));
            RenderOnce(line);                       // a frame already asked for, a stale reference
            Assert.Equal((measures, renders), (renderer.Measures, renderer.Renders));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ADetachedList_LetsGoOfTheRenderer_AndTakesItBackWhenShownAgain()
    {
        var renderer = new Hostile();
        var (list, template, window, host, loaded) = Shown(renderer);
        try
        {
            Assert.True(PluginLyricLines.IsWrapped(list));
            window.Content = null;                    // the lyrics view closes
            Dispatcher.UIThread.RunJobs();
            Assert.Same(template, list.ItemTemplate); // holds nothing of the plugin while away
            Assert.IsNotType<PluginLyricLine>(ContentOf(list, 0));

            window.Content = list;                    // and opens again
            Dispatcher.UIThread.RunJobs();
            Assert.True(PluginLyricLines.IsWrapped(list));

            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            host.SetEnabled(loaded, false);           // the plugin goes while the view is closed
            var creates = renderer.Creates;
            window.Content = list;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Same(template, list.ItemTemplate);
            Assert.Equal(creates, renderer.Creates);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("measure")]
    [InlineData("render")]
    public void ARendererWithdrawingItselfMidFrame_DoesNotSwapTheListDuringThePass(string where)
    {
        var renderer = new Hostile();
        var wrappedDuringCall = new List<bool>();
        ItemsControl? shown = null;
        void Withdraw()
        {
            if (shown is null) return;
            _registration?.Dispose();
            wrappedDuringCall.Add(PluginLyricLines.IsWrapped(shown));
        }
        if (where == "measure") renderer.MeasureWith = _ => { Withdraw(); return new Size(100, 40); };
        else renderer.RenderWith = (_, _) => { Withdraw(); return false; };
        var (list, template, window, _, _) = Shown(renderer);
        shown = list;
        try
        {
            var line = Assert.IsType<PluginLyricLine>(ContentOf(list, 0));
            line.InvalidateMeasure();
            window.UpdateLayout();
            RenderOnce(line);
            Assert.NotEmpty(wrappedDuringCall);
            Assert.All(wrappedDuringCall, Assert.True); // the list is left alone mid-pass
            Dispatcher.UIThread.RunJobs();
            Assert.Same(template, list.ItemTemplate);   // and handed back right after
            Assert.Null(PluginLyricLines.Current);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CommunityPluginsOff_TakesTheRendererAway_AndOnBringsItBack()
    {
        var (list, template, window, host, _) = Shown(new Hostile());
        try
        {
            host.SetCommunityPluginsEnabled(false);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(PluginLyricLines.Current);
            Assert.Empty(host.LyricsRenderers);
            Assert.Same(template, list.ItemTemplate);

            host.SetCommunityPluginsEnabled(true);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(PluginLyricLines.Current);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ARendererName_IsCleaned_AndBounded()
    {
        var (host, _, _) = Load(new Hostile { Name = "  Ev‮il\nName​ " + new string('x', 500) + "\t" });
        var entry = Assert.Single(host.LyricsRenderers);
        Assert.StartsWith("Evil Name ", entry.Name);
        Assert.InRange(entry.Name.Length, 1, PluginHost.MaxLyricsRendererNameLength);
        Assert.DoesNotContain(entry.Name, c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);
        Assert.True(PluginHost.IsValidLyricsRendererKey(entry.Key));
    }

    [AvaloniaFact]
    public void AStoredChoice_ThatIsNotARendererKey_IsDropped()
    {
        var (host, _, _) = Load(new Hostile());
        var key = host.LyricsRenderers[0].Key;
        foreach (var bad in new[] { new string('a', 5000), Id + "/Bad\nName", "no-slash", "/Name", Id + "/", Id + "/‮evil" })
        {
            host.SelectLyricsRenderer(bad);
            Assert.Equal("", _box.Settings.LyricsRenderer);
            Assert.Null(PluginLyricLines.Current);
        }
        host.SelectLyricsRenderer(key);
        Assert.Equal(key, _box.Settings.LyricsRenderer);
    }

    [AvaloniaFact]
    public void SettingsPicker_DropsABadStoredChoice_AndNeverShowsARendererAsNoctisItself()
    {
        using var persistence = new TestPersistenceService();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoPlayHistory());
        _box.Approve((Id, Array.Empty<string>()));
        var host = _box.NewHost(appVersion: "1.7.0");
        vm.Plugins = host;
        _box.AddInProcess(host, new ScriptedPlugin { OnInit = h => h.RegisterLyricsRenderer(new Hostile { Name = "Noctis" }) },
            PluginSandbox.Manifest(name: "Noctis", apiVersion: "1.8", minAppVersion: "1.7.0"));
        Dispatcher.UIThread.RunJobs();
        var labels = vm.LyricsRendererOptions.Select(o => o.Label).ToList();
        Assert.Equal(2, labels.Count);
        Assert.Equal(1, labels.Count(l => string.Equals(l, "Noctis", StringComparison.OrdinalIgnoreCase)));

        vm.LyricsRenderer = new string('a', 5000);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", vm.LyricsRenderer);
        Assert.Equal(2, vm.LyricsRendererOptions.Count);
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

    /// <summary>Does whatever a test tells it to, and counts the calls.</summary>
    private sealed class Hostile : ILyricsRenderer
    {
        public string Name { get; init; } = "Hostile";
        public Func<double, Size> MeasureWith = _ => new Size(100, 40);
        public Func<DrawingContext, Size, bool> RenderWith = (_, _) => false;
        public Action? OnCreate;
        public int Creates, Measures, Renders;

        public ILyricsLineView? CreateLine(LyricsLineInfo line)
        {
            Creates++;
            OnCreate?.Invoke();
            return new View(this);
        }

        private sealed class View(Hostile r) : ILyricsLineView
        {
            public Size Measure(double availableWidth, in LyricsLineStyle style) { r.Measures++; return r.MeasureWith(availableWidth); }
            public bool Render(DrawingContext context, Size size, in LyricsLineFrame frame) { r.Renders++; return r.RenderWith(context, size); }
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
