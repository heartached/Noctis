using System.Linq;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;
using static Noctis.Tests.LyricShareDialogTests;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26: in "Get genres for N albums" the options could not be clicked. Real presses
/// and releases on each control, the way a mouse does it. The round checkbox's label only took
/// a click on the glyphs themselves (its template had no background), and the album ticks are
/// off until Find proposed a genre, which they now show plainly with the status line saying so.
/// </summary>
public class DiscogsGenresDialogClickTests
{
    private static Track Song(string album) => new()
    {
        Id = Guid.NewGuid(), Title = "t", Artist = "Björk", AlbumArtist = "Björk", Album = album, Year = 1993, Genre = "Alternative", FilePath = string.Empty,
    };

    /// <summary>Answers every search with the Björk "Debut" master fixture, no network.</summary>
    private sealed class DebutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discogs", "bjork-debut-master.json"));
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            r.Headers.Add("x-discogs-ratelimit", "25");
            r.Headers.Add("x-discogs-ratelimit-remaining", "24");
            return Task.FromResult(r);
        }
    }

    private static (Noctis.Views.BulkDiscogsGenresDialog Win, BulkDiscogsGenresViewModel Vm) Open()
    {
        EnsureAppStyles();
        ComboBoxDropDownAnimator.Install();
        var service = new DiscogsGenreService(new HttpClient(new DebutHandler()), TimeSpan.Zero,
            delay: (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        var vm = new BulkDiscogsGenresViewModel(new[] { new[] { Song("Debut") }, new[] { Song("Debut") } },
            service, metadata: null, library: null, openUrl: _ => { });
        var win = new Noctis.Views.BulkDiscogsGenresDialog(vm) { Width = 1280, Height = 860 };
        win.Show();
        var host = win.GetVisualDescendants().OfType<Noctis.Controls.PillDialogHost>().Single();
        Assert.True(PumpUntil(() => host.Card is { Opacity: >= 1 } card
            && (card.RenderTransform is null || card.RenderTransform.Value.IsIdentity)), "open animation never settled");
        return (win, vm);
    }

    private static void Click(TopLevel win, Control c, double? x = null)
    {
        var at = c.TranslatePoint(new Point(x ?? c.Bounds.Width / 2, c.Bounds.Height / 2), win)!.Value;
        win.MouseMove(at);
        win.MouseDown(at, MouseButton.Left);
        win.MouseUp(at, MouseButton.Left);
        PumpUntil(() => false, 50);
    }

    private static CheckBox RowTick(Window win, BulkDiscogsGenresViewModel.Row row)
    {
        Assert.True(PumpUntil(() => win.GetVisualDescendants().OfType<CheckBox>().Any(c => ReferenceEquals(c.DataContext, row))));
        return win.GetVisualDescendants().OfType<CheckBox>().First(c => ReferenceEquals(c.DataContext, row));
    }

    private static void Close(Window win)
    {
        if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    // x = 14: the circle; x = 80, 200: on the label (a gap between words included).
    [AvaloniaTheory]
    [InlineData("ReleaseBox", 14)]
    [InlineData("ReleaseBox", 80)]
    [InlineData("ReleaseBox", 200)]
    [InlineData("AddBox", 14)]
    [InlineData("AddBox", 80)]
    [InlineData("AddBox", 200)]
    public void OptionCircle_TogglesOnClick_OnTheCircleAndTheLabel(string name, double x)
    {
        var (win, vm) = Open();
        try
        {
            var box = win.FindControl<CheckBox>(name)!;
            bool Bound() => name == "ReleaseBox" ? vm.UseRelease : vm.AddToExisting;
            Click(win, box, x);
            Assert.True(box.IsChecked);
            Assert.True(Bound());
            Click(win, box, x);
            Assert.False(box.IsChecked);
            Assert.False(Bound());
        }
        finally { Close(win); }
    }

    [AvaloniaTheory]
    [InlineData("SourceBox", 2)]
    [InlineData("MaxBox", 3)]
    public void DropDown_OpensAndPicks(string name, int target)
    {
        var (win, vm) = Open();
        try
        {
            var box = win.FindControl<ComboBox>(name)!;
            LyricShareComboClickTests.ClickAndPick(win, box, target);
            Assert.Equal(target, box.SelectedIndex);
            Assert.Equal(target, name == "SourceBox" ? vm.SourceIndex : vm.MaxIndex);
        }
        finally { Close(win); }
    }

    [AvaloniaFact]
    public void AlbumTick_BeforeFind_IsPlainlyOff_AndTheStatusSaysPressFind_ThenAfterFindAClickToggles()
    {
        var (win, vm) = Open();
        try
        {
            var row = vm.Rows[0];
            var tick = RowTick(win, row);
            Assert.False(tick.IsEffectivelyEnabled);
            Assert.True(tick.Opacity <= 0.35, $"a tick that cannot be used must look off (opacity {tick.Opacity})");
            Assert.Equal(Noctis.Localization.Loc.T("DiscogsGenres.ReadyHint"), vm.StatusMessage);

            Click(win, win.FindControl<Button>("StartButton")!);
            Assert.True(PumpUntil(() => row.State == BulkDiscogsGenresViewModel.RowState.Found && !vm.IsRunning), $"Find never finished: {row.State} / {vm.StatusMessage}");
            Assert.True(PumpUntil(() => tick.IsEffectivelyEnabled));
            Assert.Equal(1, tick.Opacity);
            Assert.True(row.IsIncluded);

            Click(win, tick);
            Assert.False(row.IsIncluded);
            Click(win, tick);
            Assert.True(row.IsIncluded);
        }
        finally { Close(win); }
    }
}
