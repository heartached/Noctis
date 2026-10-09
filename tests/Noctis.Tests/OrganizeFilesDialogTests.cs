using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: Organize Files in the rounded pill pop-up of the Metadata editor / Find
/// Metadata (blurred app behind, filled pill fields, solid accent Apply, quiet Close), with
/// fewer words: tag chips instead of the token list, a live example path under the pattern.
/// </summary>
public class OrganizeFilesDialogTests
{
    private readonly ITestOutputHelper _o;
    public OrganizeFilesDialogTests(ITestOutputHelper o) => _o = o;

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

    private static Track[] Tracks() => Enumerable.Range(1, 3)
        .Select(i => new Track
        {
            Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
            Album = "Album", TrackNumber = i, FilePath = TestPaths.Primary("Music", $"song{i}.flac"),
        })
        .ToArray();

    [AvaloniaFact]
    public void OrganizeFiles_OpensInPillHost_WithResolvedStyles_AndBoundActions()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            using var persistence = new TestPersistenceService();
            var settings = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
            var root = TestPaths.Primary("Organized");
            settings.OrganizeTargetRoot = root;
            var vm = new OrganizeFilesViewModel(Tracks(), new PlannerOrganizer(), settings);
            Assert.True(PumpUntil(() => vm.Rows.Count == 3), "preview never filled");

            var win = new OrganizeFilesDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
            win.Show();
            var closed = 0;
            win.Closed += (_, _) => closed++;
            try
            {
                var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
                Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropLayer!.Opacity > 0.999),
                    "open animation never settled");
                Assert.Equal(new CornerRadius(30), host.CornerRadius);

                var all = win.GetVisualDescendants().ToList();
                // The old outlined / text-button chrome is gone.
                Assert.DoesNotContain(all.OfType<Button>(), b => b.Classes.Contains("text-btn") || b.Classes.Contains("accent-btn")
                                                                 || b.Classes.Contains("pill-outline") || b.Classes.Contains("dialog-close"));

                // Filled pill fields, resolved from PillDialog.axaml (pattern + destination).
                var fields = all.OfType<TextBox>().Where(t => t.Classes.Contains("pill-field")).ToList();
                Assert.Equal(2, fields.Count);
                foreach (var field in fields)
                {
                    var chrome = field.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                    Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(chrome.Background));
                    Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
                }

                // Footer: Apply is the solid accent pill, Close the quiet one; both bound.
                var apply = all.OfType<Button>().Single(b => b.Command == vm.ApplyCommand);
                PillDialogHostTests.AssertSolidAccent(apply);
                Assert.True(apply.IsEffectivelyEnabled, "3 files to move, yet Apply is disabled");
                var close = all.OfType<Button>().Single(b => b.Command == vm.CloseCommand);
                Assert.Contains("pill-secondary", close.Classes);
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(close.Background));
                Assert.Contains("pill-secondary", all.OfType<Button>().Single(b => b.Command == vm.PreviewCommand).Classes);
                var undo = all.OfType<Button>().Single(b => b.Command == vm.UndoLastCommand);
                Assert.False(undo.IsEffectivelyEnabled, "Undo enabled with nothing to undo");

                // The preview sits in a filled well and its rows are realized.
                var list = all.OfType<ItemsControl>().Single(ic => ReferenceEquals(ic.ItemsSource, vm.Rows));
                Assert.Contains(list.GetVisualAncestors().OfType<Border>(), b => b.Classes.Contains("pill-well"));
                Assert.Equal(3, list.GetRealizedContainers().Count());

                // Localized footer count.
                Assert.Equal(Loc.T("OrganizeFiles.Summary", 3, 0), vm.StatusMessage);

                // Live example follows the pattern, before any Update preview.
                Assert.Equal(Path.Combine("Bad Bunny", "Album", "01 Song 1") + ".flac", vm.SampleTarget);
                vm.Pattern = "{Artist}/{Title}";
                Assert.Equal(Path.Combine("Bad Bunny", "Song 1") + ".flac", vm.SampleTarget);
                PumpUntil(() => false, 50);
                var sample = win.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == vm.SampleTarget);
                Assert.True(sample.IsEffectivelyVisible);
                _o.WriteLine($"sample: {sample.Text}");

                // A tag chip adds its token (appended until the pattern has had the caret).
                var year = win.GetVisualDescendants().OfType<Button>()
                    .Single(b => b.DataContext is OrganizeFilesViewModel.PatternToken { Label: "Year" });
                Assert.Contains("pill-secondary", year.Classes);
                year.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                // Spaced from the tag before it, so the values don't glue ("Song 12024").
                Assert.Equal("{Artist}/{Title} {Year}", vm.Pattern);
                // After a separator it goes in as is.
                vm.Pattern = "{Artist}/";
                year.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("{Artist}/{Year}", vm.Pattern);

                // Esc closes like Close: animated, exactly once.
                win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.True(host.IsClosing);
                Assert.True(PumpUntil(() => closed > 0, 2000), "window never closed");
                PumpUntil(() => false, 250);
                Assert.Equal(1, closed);
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        });
    }

    /// <summary>Real Skia only (NOCTIS_TEST_SKIA=1): a PNG of the dialog over a busy owner,
    /// for eyeballing the layout. Saved under the system temp folder.</summary>
    [AvaloniaFact]
    public void Probe_SavesTheOrganizeDialog()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var stripes = new StackPanel();
            var colors = new[] { "#E74856", "#2D7DD2", "#F4D35E", "#3BB273", "#7B2CBF", "#FF8C42" };
            for (var i = 0; i < 18; i++)
                stripes.Children.Add(new Border { Height = 50, Background = new SolidColorBrush(Color.Parse(colors[i % colors.Length])) });
            var owner = new Window { Width = 1100, Height = 820, Content = stripes, RequestedThemeVariant = ThemeVariant.Dark };
            owner.Show();
            PumpUntil(() => false, 100);

            using var persistence = new TestPersistenceService();
            var settings = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
            settings.OrganizeTargetRoot = TestPaths.Primary("Organized");
            var tracks = Enumerable.Range(1, 30).Select(i => new Track
            {
                Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
                Album = "nadie sabe lo que va a pasar mañana", TrackNumber = i,
                FilePath = TestPaths.Primary("Music", $"song{i}.flac"),
            }).ToArray();
            var vm = new OrganizeFilesViewModel(tracks, new PlannerOrganizer(), settings);
            Assert.True(PumpUntil(() => vm.Rows.Count == 30));
            var win = new OrganizeFilesDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
            _ = win.ShowDialog(owner);
            try
            {
                var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
                Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropBitmap != null, 3000));
                PumpUntil(() => false, 150);
                var path = Path.Combine(Path.GetTempPath(), "noctis-organize-files-dialog.png");
                win.CaptureRenderedFrame()!.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                _o.WriteLine(path);
            }
            finally
            {
                if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
                owner.Close();
            }
        });
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>The real planner with no files on disk; nothing is ever moved.</summary>
    private sealed class PlannerOrganizer : IFileOrganizerService
    {
        public IReadOnlyList<OrganizeMove> Plan(IEnumerable<Track> tracks, string pattern, string targetRoot)
            => FileOrganizePlanner.Plan(tracks, pattern, targetRoot, _ => false);
        public Task<OrganizeResult> ApplyAsync(IReadOnlyList<OrganizeMove> moves, CancellationToken ct = default)
            => throw new NotSupportedException();
        public bool CanUndo => false;
        public Task<OrganizeResult> UndoLastAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
