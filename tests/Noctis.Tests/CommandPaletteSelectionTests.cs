using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord 11-21 (Halil): moving through the Ctrl+K palette with the arrow keys showed no
/// highlighted row, and hovering showed nothing either. The app-wide album-grid rule in
/// Styles.axaml (ListBox:not(.track-list):not(.sidebar) ListBoxItem:selected / :pointerover ->
/// Transparent) cleared both on the palette's ListBox. The rows now paint their own states, the
/// palette is a PillDialogHost card (Liquid Glass while it is on), and Enter never runs a row
/// of the previous query.
/// </summary>
public class CommandPaletteSelectionTests
{
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
            var frame = new DispatcherFrame();
            using (DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(5), DispatcherPriority.Send))
                Dispatcher.UIThread.PushFrame(frame);
        }
        return condition();
    }

    /// <summary>The palette never runs an item in these tests, so the main view model is only
    /// held, never used.</summary>
    private static CommandPaletteViewModel NewVm(FakeLibraryService? library = null) =>
        new((MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel)),
            library ?? new FakeLibraryService());

    private static Border Row(ListBox list, int index)
    {
        var container = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(index));
        return container.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("palette-row"));
    }

    private static bool IsClear(IBrush? brush) => brush is null or ISolidColorBrush { Color.A: 0 };

    private static object Resource(Control from, string key) =>
        from.FindResource(from.ActualThemeVariant, key) ?? throw new InvalidOperationException(key);

    [AvaloniaFact]
    public void ArrowKeys_MoveAVisibleSelection_HoverShows_AndTheRowStaysInView()
    {
        EnsureAppStyles();
        var owner = new Window { Width = 1100, Height = 820 };
        owner.Show();
        var vm = NewVm();
        var dialog = new CommandPaletteDialog(vm) { Width = 1100, Height = 820 };
        try
        {
            _ = dialog.ShowDialog(owner);
            PillDialogHost? host = null;
            Assert.True(PumpUntil(() => (host = dialog.GetVisualDescendants().OfType<PillDialogHost>().FirstOrDefault()) is { Card.Opacity: > 0.999 }, 4000),
                "the palette card never opened");
            var list = dialog.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("palette-list"));
            var query = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "QueryBox");
            Assert.True(PumpUntil(() => query.IsFocused && list.ContainerFromIndex(1) != null), "query box not focused / rows not realized");
            Assert.True(vm.Results.Count > 3);

            // The card sits in the upper part of the window, not centred.
            var cardTop = host!.Card!.TranslatePoint(default, dialog)!.Value.Y;
            Assert.InRange(cardTop, 100, 140);

            var accent = Resource(list, "AccentColorBrush");
            Assert.Equal(0, vm.SelectedIndex);
            Assert.Same(accent, Row(list, 0).BorderBrush);
            Assert.False(IsClear(Row(list, 0).Background), "the selected row has no fill");
            Assert.True(IsClear(Row(list, 1).BorderBrush));

            // Arrow keys typed in the search box (where the focus is) move the selection.
            dialog.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            PumpUntil(() => vm.SelectedIndex == 1, 500);
            Assert.Equal(1, vm.SelectedIndex);
            Assert.Same(accent, Row(list, 1).BorderBrush);
            Assert.True(IsClear(Row(list, 0).BorderBrush), "the old row kept the selection ring");
            Assert.Equal(0, query.CaretIndex); // the caret did not move instead

            // Hover: the row under the pointer fills, without taking the selection.
            var hovered = Row(list, 3);
            var centre = hovered.TranslatePoint(new Point(hovered.Bounds.Width / 2, hovered.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseMove(centre);
            var hoverFill = Resource(list, "PillFieldBackground");
            Assert.True(PumpUntil(() => ReferenceEquals(Row(list, 3).Background, hoverFill), 500), "the hovered row did not fill");
            Assert.Equal(1, vm.SelectedIndex);

            // To the last row: it is scrolled into view.
            for (var i = 0; i < vm.Results.Count + 2; i++)
                dialog.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            var last = vm.Results.Count - 1;
            Assert.True(PumpUntil(() => vm.SelectedIndex == last && list.ContainerFromIndex(last) != null, 1000));
            var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            var lastRow = Row(list, last);
            Assert.Same(accent, lastRow.BorderBrush);
            var top = lastRow.TranslatePoint(default, scroller)!.Value.Y;
            Assert.InRange(top, 0, scroller.Bounds.Height - lastRow.Bounds.Height + 1);
        }
        finally
        {
            dialog.Close();
            PumpUntil(() => !dialog.IsVisible, 1000);
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Enter_RightAfterTyping_PicksFromTheTypedQuery_NotThePreviousRows()
    {
        var library = new FakeLibraryService();
        library.TrackList.Add(new Track { Title = "Zebra Crossing", FilePath = "z.mp3" });
        var vm = NewVm(library);
        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;
        Assert.NotEqual("Zebra Crossing", vm.Results[0].Title); // the pages list

        vm.Query = "zebra";
        await vm.ExecuteSelectedCommand.ExecuteAsync(null); // within the 200 ms debounce

        Assert.Equal("Zebra Crossing", vm.Results[vm.SelectedIndex].Title);
        Assert.Equal(1, closes);
    }

    [AvaloniaFact]
    public void OnlyTheFirstPickCounts_WhileThePaletteCloses()
    {
        var vm = NewVm();
        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;
        vm.ExecuteItemCommand.Execute(vm.Results[0]);
        vm.ExecuteItemCommand.Execute(vm.Results[1]);
        Assert.Equal(1, closes);
    }
}
