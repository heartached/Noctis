using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Track = Noctis.Models.Track;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: the Lyrics Studio's pop-ups in the rounded pill family — Choose songs &amp;
/// albums and the per-song Studio open in PillDialogHost (blurred app behind, shared open/close),
/// the format is one segmented pill, fields and neutral buttons are filled pills with no rim,
/// and the Studio's Settings popover is a pill card with a filled Language box.
/// </summary>
public class LyricsStudioPickerDialogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-picker-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioPickerDialogTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    /// <summary>Real-time pump that yields between frames (the Studio's run resumes on the UI thread).</summary>
    private static async Task<bool> PumpUntilAsync(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            await Task.Delay(8);
        }
        return condition();
    }

    /// <summary>Open and settled: the pill card faded in, or (no host) a few layout passes.</summary>
    private static void Settle(Window win)
    {
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().SingleOrDefault();
        if (host is not null)
            Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropLayer!.Opacity > 0.999), "open animation never settled");
        else
            PumpUntil(() => false, 300);
        for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); win.UpdateLayout(); }
    }

    /// <summary>A real click (pointer down + up over the control's centre), as a user's.</summary>
    private static void Click(TopLevel top, Control c)
    {
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), top)!.Value;
        top.MouseMove(p);
        top.MouseDown(p, MouseButton.Left);
        top.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The format option labelled with <paramref name="key"/> (a chip or a segment: both are ToggleButtons).</summary>
    private static ToggleButton FormatOption(Visual root, string key) =>
        root.GetVisualDescendants().OfType<ToggleButton>().Single(t => Equals(t.Content, Loc.T(key)) && t.IsEffectivelyVisible);

    private static FakeLibraryService Library(int songs)
    {
        var lib = new FakeLibraryService();
        for (var i = 0; i < songs; i++)
            lib.TrackList.Add(new Track { Title = $"Song {i:00}", Artist = "Artist", Album = "Album", AlbumId = Guid.NewGuid(), FilePath = $@"C:\m\{i}.mp3" });
        return lib;
    }

    // ── Choose songs & albums: the format ────────────────────────────────

    /// <summary>
    /// Clicking the format that is already chosen keeps it. The two options were independent
    /// ToggleButtons bound to WordTimings / LineTimings, so a click on the lit "Word timings"
    /// chip un-toggled it, wrote WordTimings = false, and switched the picker to line timings.
    /// </summary>
    [AvaloniaFact]
    public void Picker_ClickingTheChosenFormat_KeepsIt()
    {
        EnsureAppStyles();
        var vm = new LyricsStudioPickerViewModel(Library(3), wordTimings: true, detectFormats: t => t.Select(_ => LyricsFormat.None).ToList());
        var win = new LyricsStudioPickerDialog { DataContext = vm, RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        try
        {
            Settle(win);
            var word = FormatOption(win, "LyricsStudioPicker.WordTimings");
            var line = FormatOption(win, "LyricsStudioPicker.LineTimings");
            Assert.True(word.IsChecked);

            Click(win, word);
            Assert.True(vm.WordTimings, "a click on the chosen Word timings switched the picker to line timings");
            Assert.True(word.IsChecked);
            Assert.False(line.IsChecked);

            Click(win, line);
            Assert.False(vm.WordTimings);
            Assert.True(line.IsChecked);
            Assert.False(word.IsChecked);

            Click(win, line);
            Assert.False(vm.WordTimings);
            Assert.True(line.IsChecked);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    // ── Choose songs & albums: the pill pop-up ───────────────────────────

    private static readonly Color PillFill = Color.Parse("#1CFFFFFF"); // Dark PillFieldBackground

    private static LyricsStudioPickerViewModel Picker(FakeLibraryService lib, IReadOnlyList<Track> suggestions)
    {
        var vm = new LyricsStudioPickerViewModel(lib, wordTimings: true,
            detectFormats: t => t.Select((_, i) => i % 2 == 0 ? LyricsFormat.Lrc : LyricsFormat.None).ToList(),
            suggest: _ => Task.FromResult(suggestions));
        vm.SuggestionsLoad.Wait(TimeSpan.FromSeconds(5));
        vm.FormatScan.Wait(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs(); // the scan posts its labels to the UI thread
        return vm;
    }

    /// <summary>Opened as LyricsStudioView opens it (the view model's CloseRequested closes it).</summary>
    private static (LyricsStudioPickerDialog win, PillDialogHost host) OpenPicker(LyricsStudioPickerViewModel vm)
    {
        var win = new LyricsStudioPickerDialog { DataContext = vm, RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        return (win, host);
    }

    private static T Named<T>(Window win, string name) where T : Control =>
        win.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static List<Button> Rows(Window win) =>
        win.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("pick-row") && b.IsEffectivelyVisible).ToList();

    [AvaloniaFact]
    public void Picker_OpensInPillHost_WithAFilledSearch_ASegmentedFormat_AndPillButtons()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var lib = Library(4);
            var vm = Picker(lib, lib.TrackList.ToList());
            var (win, host) = OpenPicker(vm);
            try
            {
                Settle(win);
                Assert.Equal(new CornerRadius(30), host.CornerRadius);

                // Search: the filled pill, no outline at rest — and not focused on open, so it
                // does not come up wearing the accent ring.
                var search = Named<TextBox>(win, "SearchBox");
                Assert.Contains("pill-field", search.Classes);
                Assert.False(search.IsFocused);
                var chrome = search.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                Assert.Equal(PillFill, AccentTestHarness.ColorOf(chrome.Background));
                Assert.Equal(0, AccentTestHarness.ColorOf(chrome.BorderBrush).A);
                Assert.Equal(new CornerRadius(999), chrome.CornerRadius);

                // Format: two segments on one filled track, the chosen one lit, a one-line hint.
                var word = Named<RadioButton>(win, "WordTimingsSegment");
                var line = Named<RadioButton>(win, "LineTimingsSegment");
                Assert.Same(word.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("pill-well")),
                            line.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("pill-well")));
                Assert.True(word.IsChecked);
                Assert.Equal(1.0, word.Opacity, 3);
                Assert.Equal(0.6, line.Opacity, 3);
                var hint = Named<TextBlock>(win, "FormatHintText");
                Assert.Equal(Loc.T("StudioPicker.HintWord"), hint.Text);
                Assert.Equal(Loc.T("LyricsStudioPicker.FormatHint"), ToolTip.GetTip(hint));
                vm.LineTimings = true;
                PumpUntil(() => false, 30);
                Assert.True(line.IsChecked);
                Assert.Equal(Loc.T("StudioPicker.HintLine"), hint.Text);
                vm.WordTimings = true;
                PumpUntil(() => false, 30);

                // Footer: quiet Cancel, solid accent Add — off until something is ticked.
                var cancel = win.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.CancelCommand);
                Assert.Contains("pill-secondary", cancel.Classes);
                Assert.Equal(PillFill, AccentTestHarness.ColorOf(cancel.Background));
                var add = win.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.ConfirmCommand);
                PillDialogHostTests.AssertSolidAccent(add);
                Assert.False(add.IsEnabled);
                Assert.Equal(Loc.T("LyricsStudioPicker.Add"), add.Content);
                var count = Named<TextBlock>(win, "SelectionCount");
                Assert.False(count.IsVisible);

                // Select all is a quiet pill that turns into Clear.
                var selectAll = Named<Button>(win, "SelectAllButton");
                Assert.Contains("pill-secondary", selectAll.Classes);
                Assert.Equal(Loc.T("LyricsStudioPicker.SelectAll"), selectAll.Content);
                vm.ToggleSelectCommand.Execute(vm.Results[1]);
                vm.ToggleSelectCommand.Execute(vm.Results[2]);
                vm.ToggleSelectCommand.Execute(vm.Results[3]);
                PumpUntil(() => false, 30);
                Assert.True(count.IsVisible);
                Assert.Equal("3 selected", count.Text);
                Assert.True(add.IsEnabled);
                Assert.Equal("Add to Studio (3)", add.Content);
                selectAll.Command!.Execute(null);
                PumpUntil(() => false, 30);
                Assert.Equal("4 selected", count.Text);
                Assert.Equal(Loc.T("StudioPicker.Clear"), selectAll.Content);
            }
            finally { win.Close(); PumpUntil(() => !win.IsVisible); }
        });
    }

    /// <summary>Each row's tag (LRC / No lyrics / 2 songs) and check sit in one column down the list.</summary>
    [AvaloniaFact]
    public async Task Picker_Rows_TagsAndChecksLineUp_ForSongsAndAlbums()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albumId = Guid.NewGuid();
        var songs = new[] { "Fantasía", "Fantasía (Remix)", "Fantasía (A Much Longer Title That Has To Trim)" }
            .Select(t => new Track { Title = t, Artist = "Alex Sensation", Album = "Fantasía - Single", AlbumId = albumId, FilePath = $@"C:\m\{Guid.NewGuid():N}.mp3" })
            .ToList();
        lib.TrackList.AddRange(songs);
        ((List<Album>)lib.Albums).Add(new Album { Id = albumId, Name = "Fantasía - Single", Artist = "Alex Sensation", Tracks = songs.ToList() });
        var vm = Picker(lib, Array.Empty<Track>());
        vm.SearchText = "fantas";
        await vm.SearchRefresh;
        vm.FormatScan.Wait(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();

        var (win, _) = OpenPicker(vm);
        try
        {
            Settle(win);
            var list = Named<ItemsControl>(win, "ResultsList");
            var rows = Rows(win);
            Assert.Equal(4, rows.Count); // the album and its three songs
            var tagRights = new List<double>();
            var checkLefts = new List<double>();
            foreach (var row in rows)
            {
                var tag = row.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("state-pill"));
                var check = row.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("selection-circle"));
                Assert.True(tag.IsEffectivelyVisible);
                var tagBox = tag.TranslatePoint(new Point(0, 0), list)!.Value;
                var checkBox = check.TranslatePoint(new Point(0, 0), list)!.Value;
                tagRights.Add(tagBox.X + tag.Bounds.Width);
                checkLefts.Add(checkBox.X);
                Assert.Equal(tagBox.Y + tag.Bounds.Height / 2, checkBox.Y + check.Bounds.Height / 2, 1); // same line
            }
            Assert.All(tagRights, x => Assert.Equal(tagRights[0], x, 1));
            Assert.All(checkLefts, x => Assert.Equal(checkLefts[0], x, 1));
            Assert.Equal("3 songs", vm.Results[0].StateText);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    /// <summary>
    /// Select all ticks the song rows on show only — never the songs a search has hidden — and
    /// Clear unticks those same rows; ticks made in one search stay through the next, and the
    /// footer count covers them all.
    /// </summary>
    [AvaloniaFact]
    public async Task Picker_SelectAll_TicksOnlyTheRowsOnShow_AndPicksSurviveSearches()
    {
        var lib = new FakeLibraryService();
        var albumId = Guid.NewGuid();
        var a = new Track { Title = "Fantasía", Artist = "Alex Sensation", Album = "Fantasía - Single", AlbumId = albumId, FilePath = @"C:\m\a.mp3" };
        var b = new Track { Title = "Fantasía (Remix)", Artist = "Alex Sensation", Album = "Fantasía - Single", AlbumId = albumId, FilePath = @"C:\m\b.mp3" };
        var lean = new Track { Title = "Lean", Artist = "Amenazzy", Album = "Lean", AlbumId = Guid.NewGuid(), FilePath = @"C:\m\l.mp3" };
        var other = new Track { Title = "Otra", Artist = "Someone", Album = "Otra", AlbumId = Guid.NewGuid(), FilePath = @"C:\m\o.mp3" };
        lib.TrackList.AddRange(new[] { a, b, lean, other });
        ((List<Album>)lib.Albums).Add(new Album { Id = albumId, Name = "Fantasía - Single", Artist = "Alex Sensation", Tracks = new List<Track> { a, b } });
        var vm = Picker(lib, new[] { a, b, lean, other });

        vm.SearchText = "fantas";
        await vm.SearchRefresh;
        Assert.Contains(vm.Results, r => r.IsAlbum);
        vm.ToggleSelectAllCommand.Execute(null);
        Assert.Equal(new[] { a.Id, b.Id }, vm.PickedTracks.Select(t => t.Id)); // not Lean, not Otra
        Assert.True(vm.Results.Single(r => r.IsAlbum).IsSelected);
        Assert.Equal(Loc.T("StudioPicker.Clear"), vm.SelectAllText);

        // Another search: the earlier picks stay, Select all adds only what is on show now.
        vm.SearchText = "lean";
        await vm.SearchRefresh;
        Assert.False(vm.Results.Single().IsSelected);
        Assert.Equal("2 selected", vm.SelectionText);
        Assert.Equal(Loc.T("LyricsStudioPicker.SelectAll"), vm.SelectAllText);
        vm.ToggleSelectAllCommand.Execute(null);
        Assert.Equal("3 selected", vm.SelectionText);
        // Clear unticks the rows on show, not the picks the search hides.
        vm.ToggleSelectAllCommand.Execute(null);
        Assert.Equal(new[] { a.Id, b.Id }, vm.PickedTracks.Select(t => t.Id));

        // Back to the suggestions: the picks show ticked there too.
        vm.SearchText = "";
        await vm.SearchRefresh;
        Assert.Equal(new[] { true, true, false, false }, vm.Results.Select(r => r.IsSelected));
        Assert.Equal("Add to Studio (2)", vm.AddButtonText);
    }

    /// <summary>A long list builds only the rows in view.</summary>
    [AvaloniaFact]
    public void Picker_LongList_IsVirtualized()
    {
        EnsureAppStyles();
        var lib = Library(80);
        var vm = Picker(lib, lib.TrackList.ToList());
        var (win, _) = OpenPicker(vm);
        try
        {
            Settle(win);
            Assert.Equal(80, vm.Results.Count);
            var list = Named<ItemsControl>(win, "ResultsList");
            Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
            var built = Rows(win).Count;
            Assert.InRange(built, 5, 30);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    /// <summary>The empty states: a search with no local match, nothing missing the format, and the first load.</summary>
    [AvaloniaFact]
    public async Task Picker_EmptyStates_NoMatches_NothingMissing_AndLoading()
    {
        EnsureAppStyles();
        var lib = Library(3);
        var pending = new TaskCompletionSource<IReadOnlyList<Track>>();
        var vm = new LyricsStudioPickerViewModel(lib, wordTimings: true,
            detectFormats: t => t.Select(_ => LyricsFormat.None).ToList(),
            suggest: _ => pending.Task);
        var (win, _) = OpenPicker(vm);
        try
        {
            Settle(win);
            // Still reading the library: says so; no prompt yet.
            Assert.True(Named<TextBlock>(win, "LoadingText").IsVisible);
            Assert.False(Named<StackPanel>(win, "PromptState").IsVisible);

            // Nothing is missing the format: the prompt to search instead.
            pending.SetResult(Array.Empty<Track>());
            await vm.SuggestionsLoad;
            PumpUntil(() => false, 30);
            Assert.False(Named<TextBlock>(win, "LoadingText").IsVisible);
            Assert.True(Named<StackPanel>(win, "PromptState").IsVisible);
            Assert.False(Named<Button>(win, "SelectAllButton").IsVisible);

            // A search with no local match: "No matches", no Select all, Add stays off.
            vm.SearchText = "zzzz";
            await vm.SearchRefresh;
            PumpUntil(() => false, 30);
            Assert.True(Named<StackPanel>(win, "NoResultsState").IsVisible);
            Assert.False(Named<StackPanel>(win, "PromptState").IsVisible);
            Assert.Empty(Rows(win));
            Assert.False(Named<Button>(win, "SelectAllButton").IsVisible);
            Assert.False(win.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.ConfirmCommand).IsEnabled);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    /// <summary>Add hands the pick over once — a second Add while the card closes is ignored — and the card animates out once.</summary>
    [AvaloniaFact]
    public void Picker_Add_HandsThePickOverOnce_AndAnimatesOut()
    {
        EnsureAppStyles();
        var lib = Library(3);
        var vm = Picker(lib, lib.TrackList.ToList());
        var picks = new List<LyricsStudioPick>();
        vm.Confirmed += (_, p) => picks.Add(p);
        var (win, host) = OpenPicker(vm);
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Settle(win);

        vm.ToggleSelectCommand.Execute(vm.Results[2]);
        vm.ToggleSelectCommand.Execute(vm.Results[0]);
        vm.LineTimings = true;
        vm.ConfirmCommand.Execute(null);
        Assert.True(host.IsClosing);
        Assert.True(win.IsVisible);
        vm.ConfirmCommand.Execute(null); // a second Add mid-close
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        var pick = Assert.Single(picks);
        Assert.Equal(new[] { lib.TrackList[2].Id, lib.TrackList[0].Id }, pick.Tracks.Select(t => t.Id));
        Assert.False(pick.WordTimings);
    }

    [AvaloniaFact]
    public void Picker_Escape_ClosesAnimated_ExactlyOnce_WithoutAPick()
    {
        EnsureAppStyles();
        var lib = Library(3);
        var vm = Picker(lib, lib.TrackList.ToList());
        var picks = 0;
        vm.Confirmed += (_, _) => picks++;
        vm.ToggleSelectCommand.Execute(vm.Results[0]);
        var (win, host) = OpenPicker(vm);
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Settle(win);

        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.True(host.IsClosing);
        if (closed == 0) win.Close(); // rides along
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.Equal(0, picks);
    }

    // ── The Studio: toolbar format, the per-song dialog ──────────────────

    private Track SongWithLrc(string title)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8] + ".mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:05.00]First line here\n[00:12.50]Second line here\n");
        return new Track { Title = title, Artist = "A Boogie wit da Hoodie", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
    }

    private static LyricsStudioViewModel Studio(ILyricsStudioEngine engine, params Track[] tracks)
    {
        var vm = new LyricsStudioViewModel(tracks, engine, new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        vm.PickLyricsFile = () => Task.FromResult<string?>(null);
        return vm;
    }

    private static async Task Wait(SemaphoreSlim semaphore)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!semaphore.Wait(0))
        {
            if (DateTime.UtcNow > end) throw new TimeoutException();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    /// <summary>The same as the picker's: the toolbar's chosen format chip, clicked, switched the format.</summary>
    [AvaloniaFact]
    public void StudioToolbar_ClickingTheChosenFormat_KeepsIt()
    {
        EnsureAppStyles();
        var vm = Studio(new GateEngine(_root), SongWithLrc("Ballin"));
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false };
        var win = new Window { Width = 1200, Height = 800, Content = panel, RequestedThemeVariant = ThemeVariant.Dark };
        win.Show();
        try
        {
            Settle(win);
            Assert.True(vm.WordTimings);
            var word = FormatOption(panel, "LyricsStudioPicker.WordTimings");
            var line = FormatOption(panel, "LyricsStudioPicker.LineTimings");

            Click(win, word);
            Assert.True(vm.WordTimings, "a click on the chosen Word timings switched the Studio to line timings");
            Assert.True(word.IsChecked);

            Click(win, line);
            Assert.False(vm.WordTimings);
            Click(win, line);
            Assert.False(vm.WordTimings);
            Assert.True(line.IsChecked);
            Assert.False(word.IsChecked);
        }
        finally { win.Close(); }
    }

    /// <summary>
    /// The Settings popover is the pill pop-over card (the editor's search pop-ups) with a filled
    /// Language box — no outline at rest — one kind of option row, and the drop-down motion the
    /// Find online pop-overs use.
    /// </summary>
    [AvaloniaFact]
    public void StudioSettingsPopover_IsAPillCard_WithAFilledLanguageBox()
    {
        EnsureAppStyles();
        var vm = Studio(new GateEngine(_root), SongWithLrc("Ballin"));
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false };
        var win = new Window { Width = 1200, Height = 800, Content = panel, RequestedThemeVariant = ThemeVariant.Dark };
        win.Show();
        try
        {
            Settle(win);
            var settings = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StudioSettingsButton");
            var flyout = Assert.IsType<Flyout>(settings.Flyout);
            Assert.True(Noctis.Helpers.DropDownFlyoutMotion.GetEnable(flyout));
            flyout.ShowAt(settings);
            PumpUntil(() => false, 400);

            var card = Assert.IsType<Border>(flyout.Content);
            Assert.Equal("StudioSettingsCard", card.Name);
            Assert.Contains("pill-popover", card.Classes);
            Assert.Equal(new CornerRadius(24), card.CornerRadius);
            Assert.Contains(card.GetVisualAncestors().OfType<FlyoutPresenter>(), p => p.Classes.Contains("studio-options-pop"));

            var language = card.GetVisualDescendants().OfType<ComboBox>().Single();
            Assert.Equal("StudioLanguageBox", language.Name);
            Assert.Contains("pill-field", language.Classes);
            Assert.DoesNotContain("pill", language.Classes);
            var chrome = language.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Background");
            Assert.Equal(PillFill, AccentTestHarness.ColorOf(chrome.Background));
            Assert.Equal(0, AccentTestHarness.ColorOf(chrome.BorderBrush).A);
            Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
            Assert.Same(vm.SelectedLanguage, language.SelectedItem);

            var options = card.GetVisualDescendants().OfType<CheckBox>().ToList();
            Assert.Equal(5, options.Count);
            Assert.All(options, o => Assert.True(o.Classes.Contains("metadata-pill-checkbox") && o.Classes.Contains("studio-option")));
            flyout.Hide();
        }
        finally { win.Close(); }
    }

    /// <summary>The Studio's neutral pills (Settings, Choose songs, the review's shift pill) are filled, with no 1.5px rim.</summary>
    [AvaloniaFact]
    public void StudioNeutralPills_AreFilled_WithNoRim()
    {
        EnsureAppStyles();
        var vm = Studio(new GateEngine(_root), SongWithLrc("Ballin"));
        vm.Selected = vm.Queue[0];
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false, ShowChooseSongs = true };
        var win = new Window { Width = 1400, Height = 800, Content = panel, RequestedThemeVariant = ThemeVariant.Dark };
        win.Show();
        try
        {
            Settle(win);
            Assert.True(vm.HasReview);
            var settings = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StudioSettingsButton");
            var choose = panel.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Loc.T("LyricsStudio.ChooseSongs")));
            var preview = panel.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("pill-outline") && b.IsEffectivelyVisible).ToList();
            Assert.NotEmpty(preview);
            foreach (var b in preview.Append(settings).Append(choose))
            {
                Assert.Equal(PillFill, AccentTestHarness.ColorOf(b.Background));
                Assert.Equal(default, b.BorderThickness);
                var presenter = b.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
                Assert.Equal(PillFill, AccentTestHarness.ColorOf(presenter.Background));
            }
            var nudge = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("nudge-pill"));
            Assert.Equal(PillFill, AccentTestHarness.ColorOf(nudge.Background));
            Assert.Equal(default, nudge.BorderThickness);
        }
        finally { win.Close(); }
    }

    /// <summary>
    /// The per-song Studio opens in the pill host; Esc closes it (animated, once) only when no
    /// run is going, and the round X closes it the same way.
    /// </summary>
    [AvaloniaFact]
    public async Task StudioDialog_OpensInPillHost_EscClosesWhenIdle_NotWhileRunning()
    {
        EnsureAppStyles();
        var engine = new GateEngine(_root);
        var vm = Studio(engine, SongWithLrc("Ballin"));
        var win = new LyricsStudioDialog(vm) { Width = 1200, Height = 860, RequestedThemeVariant = ThemeVariant.Dark };
        var closed = 0;
        win.Closed += (_, _) => closed++;
        win.Show();
        try
        {
            Settle(win);
            var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
            Assert.Equal(new CornerRadius(30), host.CornerRadius);
            Assert.Single(win.GetVisualDescendants().OfType<LyricsStudioPanel>());

            var run = vm.StartCommand.ExecuteAsync(null);
            await Wait(engine.Started);
            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(host.IsClosing); // not while the song runs
            Assert.True(vm.IsRunning);

            engine.Finish();
            await run;
            Assert.False(vm.IsRunning);
            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(host.IsClosing);
            Assert.True(win.IsVisible); // animating out
            Assert.True(await PumpUntilAsync(() => closed > 0, 2000));
            await PumpUntilAsync(() => false, 250);
            Assert.Equal(1, closed);
        }
        finally
        {
            engine.Finish();
            if (win.IsVisible) win.Close();
        }
    }

    [AvaloniaFact]
    public void StudioDialog_RoundX_ClosesAnimated_Once()
    {
        EnsureAppStyles();
        var vm = Studio(new GateEngine(_root), SongWithLrc("Ballin"));
        var win = new LyricsStudioDialog(vm) { Width = 1200, Height = 860, RequestedThemeVariant = ThemeVariant.Dark };
        var closed = 0;
        win.Closed += (_, _) => closed++;
        win.Show();
        Settle(win);
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        var x = win.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("dialog-close"));
        Assert.Same(vm.CloseCommand, x.Command);
        x.Command!.Execute(null);
        Assert.True(host.IsClosing);
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
    }

    /// <summary>
    /// The per-song Studio closed by the window itself (Alt+F4) while a song runs: the run used to
    /// go on with nobody watching — the speech model loaded, the engine working — because only the
    /// round X / Esc ran the view model's Close (cancel the run, keep the draft).
    /// </summary>
    [AvaloniaFact]
    public async Task StudioDialog_ClosedByTheWindow_WhileRunning_StopsTheRun()
    {
        EnsureAppStyles();
        var engine = new GateEngine(_root);
        var vm = Studio(engine, SongWithLrc("Ballin"));
        var win = new LyricsStudioDialog(vm) { Width = 1200, Height = 860, RequestedThemeVariant = ThemeVariant.Dark };
        var closed = 0;
        win.Closed += (_, _) => closed++;
        win.Show();
        try
        {
            Settle(win);
            var run = vm.StartCommand.ExecuteAsync(null);
            await Wait(engine.Started);
            Assert.True(vm.IsRunning);

            win.Close(); // what Alt+F4 asks of the window
            Assert.True(await PumpUntilAsync(() => closed > 0, 2000), "the window never closed");
            Assert.True(await PumpUntilAsync(() => !vm.IsRunning, 3000), "the run went on after its window closed");
            Assert.True(engine.Cancelled);
            await run;
            Assert.Equal(1, closed);
        }
        finally
        {
            engine.Finish();
            if (win.IsVisible) win.Close();
        }
    }

    /// <summary>Holds each song until Finish (or a cancel); the lines are the song's own.</summary>
    private sealed class GateEngine(string root) : ILyricsStudioEngine
    {
        private TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; } = StudioTestModel.Installed(root);
        public SemaphoreSlim Started { get; } = new(0);
        public volatile bool Cancelled;
        public IDisposable OpenSession(WhisperModelSize model) => new Handle();
        public void Finish() => _finish.TrySetResult();

        public async Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
        {
            _finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Started.Release();
            progress?.Report(new LyricsStudioProgress(LyricsStudioStage.Listening, 0.11));
            try { await _finish.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            var lines = (options.SourceLines ?? new[] { "la la la" })
                .Select((t, i) => new AlignedLine(t, TimeSpan.FromSeconds(5 + i * 7), TimeSpan.FromSeconds(9 + i * 7),
                    t.Split(' ').Select((w, j) => new AlignedWord(w, TimeSpan.FromSeconds(5 + i * 7 + j * 0.5), TimeSpan.FromSeconds(5.4 + i * 7 + j * 0.5))).ToList(),
                    0.9, false))
                .ToList();
            return new LyricsStudioResult(track, lines, LyricsStudioSource.ExistingLyrics, 0.9, "en", lines.Count);
        }

        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
