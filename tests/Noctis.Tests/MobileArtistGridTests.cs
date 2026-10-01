using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>The Artists page as Apple Music's: circles three to a row, initials on a soft tint
/// without a cover, "Find in Artists" filtering live, and the A–Z strip jumping the grid.</summary>
public class MobileArtistGridTests
{
    private readonly ITestOutputHelper _output;

    public MobileArtistGridTests(ITestOutputHelper output) => _output = output;

    private static MobileFixtures.Rig RigWithArtists(params string[] names)
    {
        var rig = MobileFixtures.MakeRig();
        foreach (var name in names)
            rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = name, AlbumCount = 1, TrackCount = 3 });
        return rig;
    }

    private static ArtistListPageViewModel OpenArtists(MobileFixtures.Rig rig)
    {
        rig.Shell.OpenArtistsCommand.Execute(null);
        return Assert.IsType<ArtistListPageViewModel>(rig.Shell.CurrentPage);
    }

    /// <summary>Lots of artists over most letters: none under Q or X, a "#" group of digits,
    /// symbols and another script, accents and leading punctuation.</summary>
    private static string[] ManyArtists(int count)
    {
        var letters = "ABCDEFGHIJKLMNOPRSTUVWYZ";   // no Q, no X
        var names = Enumerable.Range(0, count - 5).Select(i => $"{letters[i % letters.Length]}rtist {i:D3}").ToList();
        names.AddRange(new[] { "2Pac", "!!!", "坂本龍一", "Ébano", "'Til Tuesday" });
        return names.ToArray();
    }

    private static Button ArtistButton(ShellView view, string name) =>
        MobileFixtures.Find<ArtistListPage>(view).GetVisualDescendants().OfType<Button>()
            .First(b => b.DataContext is ArtistListItem item && item.Name == name);

    [AvaloniaFact]
    public void Artists_AreCirclesThreeToARow_WithInitialsOnATintWhenThereIsNoCover()
    {
        using var rig = RigWithArtists("Bruno Mars", "Adele", "Anderson .Paak & Silk Sonic", "Silk Sonic", "ABBA");
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var page = OpenArtists(rig);
        window.UpdateLayout();

        Assert.Equal(new[] { "ABBA", "Adele", "Anderson .Paak & Silk Sonic", "Bruno Mars", "Silk Sonic" }, page.Artists.Select(a => a.Name));
        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(3, page.Rows[0].Artists.Count);

        var bruno = ArtistButton(view, "Bruno Mars");
        var avatar = bruno.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("avatar"));
        Assert.Equal(avatar.Bounds.Width, avatar.Bounds.Height);
        Assert.Equal(avatar.Bounds.Width / 2, avatar.CornerRadius.TopLeft);  // a circle
        Assert.NotNull(avatar.Background);                                   // the soft tint, not grey
        Assert.Equal("BM", bruno.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("initials")).Text);
        Assert.Equal("AP", ArtistIndex.Initials("Anderson .Paak & Silk Sonic"));
        Assert.Equal("A", ArtistIndex.Initials("ABBA"));
        Assert.Same(ArtistIndex.PlaceholderBrush("Bruno Mars"), ArtistIndex.PlaceholderBrush("Bruno Mars"));   // one colour per name

        // Three across, level, left to right; the name centred under its circle.
        var firstRow = page.Rows[0].Artists.Select(a => ArtistButton(view, a.Name).TranslatePoint(default, view)!.Value).ToList();
        Assert.True(firstRow[0].X < firstRow[1].X && firstRow[1].X < firstRow[2].X);
        Assert.All(firstRow, p => Assert.Equal(firstRow[0].Y, p.Y, 1));
        var name = bruno.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("artist-name"));
        Assert.Equal(Avalonia.Media.TextAlignment.Center, name.TextAlignment);
        window.Close();
    }

    [AvaloniaFact]
    public void TapOpensTheArtist_AndLongPressOpensItsSheet()
    {
        using var rig = RigWithArtists("Bruno Mars");
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        OpenArtists(rig);
        window.UpdateLayout();

        var button = ArtistButton(view, "Bruno Mars");
        LongPress.GetCommand(button)!.Execute(button.DataContext);
        Assert.NotNull(rig.Shell.Sheet);
        rig.Shell.CloseSheet();

        button.Command!.Execute(button.CommandParameter);
        Assert.IsType<ArtistPageViewModel>(rig.Shell.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void FindInArtists_FiltersAsYouType_IgnoringAccents_AndHidesTheIndex()
    {
        using var rig = RigWithArtists("Beyoncé", "Bruno Mars", "Anderson .Paak & Silk Sonic", "Silk Sonic", "Adele");
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var page = OpenArtists(rig);
        window.UpdateLayout();
        var strip = MobileFixtures.Named<Border>(view, "IndexStrip");
        Assert.True(strip.IsEffectivelyVisible);

        MobileFixtures.Named<TextBox>(view, "FindBox").Text = "beyonce";   // typed: the binding carries it
        window.UpdateLayout();
        Assert.Equal(new[] { "Beyoncé" }, page.Artists.Select(a => a.Name));
        Assert.False(strip.IsEffectivelyVisible);
        Assert.True(MobileFixtures.Named<Button>(view, "ClearFindButton").IsEffectivelyVisible);

        page.Query = "sonic";
        window.UpdateLayout();
        Assert.Equal(new[] { "Anderson .Paak & Silk Sonic", "Silk Sonic" }, page.Artists.Select(a => a.Name));
        Assert.Contains(MobileFixtures.Find<ArtistListPage>(view).GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "Silk Sonic" && t.IsEffectivelyVisible);

        page.Query = "zz";
        window.UpdateLayout();
        Assert.Empty(page.Rows);
        Assert.True(MobileFixtures.Named<TextBlock>(view, "NoResultsText").IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<TextBlock>(view, "NoArtistsText").IsEffectivelyVisible);

        page.ClearQueryCommand.Execute(null);
        window.UpdateLayout();
        Assert.Equal(5, page.Artists.Count);
        Assert.True(strip.IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<TextBlock>(view, "NoResultsText").IsEffectivelyVisible);
        window.Close();
    }

    [Fact]
    public void Order_IsAToZ_WithAccentsUnderTheirLetter_AndDigitsSymbolsAndOtherScriptsLast()
    {
        Assert.Equal("E", ArtistIndex.LetterOf("Ébano"));
        Assert.Equal("T", ArtistIndex.LetterOf("'Til Tuesday"));
        Assert.Equal("#", ArtistIndex.LetterOf("2Pac"));
        Assert.Equal("#", ArtistIndex.LetterOf("!!!"));
        Assert.Equal("#", ArtistIndex.LetterOf("坂本龍一"));

        using var rig = RigWithArtists("2Pac", "Zedd", "Ébano", "'Til Tuesday", "Adele", "坂本龍一", "Eagles");
        var page = OpenArtists(rig);
        var names = page.Artists.Select(a => a.Name).ToList();
        Assert.Equal(new[] { "Adele", "Eagles", "Ébano", "'Til Tuesday", "Zedd" }, names.Take(5));
        Assert.Equal(new[] { "2Pac", "坂本龍一" }.OrderBy(n => n), names.Skip(5).OrderBy(n => n));
    }

    [AvaloniaFact]
    public void RowIndexForLetter_IsTheLettersFirstRow_OrTheNextLetterInUse()
    {
        using var rig = RigWithArtists(ManyArtists(500));
        var page = OpenArtists(rig);
        var artists = page.Artists;

        int RowOfFirst(Func<ArtistListItem, bool> match) => artists.ToList().FindIndex(a => match(a)) / ArtistListPageViewModel.Columns;

        Assert.Equal(0, page.RowIndexForLetter("A"));
        Assert.Equal(RowOfFirst(a => ArtistIndex.LetterOf(a.Name) == "M"), page.RowIndexForLetter("M"));
        Assert.Equal(RowOfFirst(a => ArtistIndex.LetterOf(a.Name) == "R"), page.RowIndexForLetter("Q"));   // no Q: on to R
        Assert.Equal(RowOfFirst(a => ArtistIndex.LetterOf(a.Name) == "#"), page.RowIndexForLetter("#"));
        var list = artists.ToList();
        var ebano = list.FindIndex(a => a.Name == "Ébano");                   // filed among the E artists
        Assert.InRange(ebano, list.FindIndex(a => ArtistIndex.LetterOf(a.Name) == "E"), list.FindIndex(a => ArtistIndex.LetterOf(a.Name) == "F") - 1);
        Assert.Equal("#", ArtistIndex.LetterOf(artists[^1].Name));
    }

    [AvaloniaFact]
    public void TheStrip_JumpsTheGridToALetter_BySliding()
    {
        using var rig = RigWithArtists(ManyArtists(500));
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var page = OpenArtists(rig);
        window.UpdateLayout();
        var scroll = MobileFixtures.Named<ScrollViewer>(view, "ArtistScroll");
        var letters = MobileFixtures.Named<ItemsControl>(view, "IndexLetters");

        Point LetterPoint(string letter)
        {
            var i = ArtistIndex.OrderOf(letter);
            var step = letters.Bounds.Height / ArtistIndex.Letters.Count;
            return letters.TranslatePoint(new Point(letters.Bounds.Width / 2, (i + 0.5) * step), window)!.Value;
        }

        // Press on M, slide to S, lift: each letter crossed jumps; the grid ends on S's row.
        window.MouseDown(LetterPoint("M"), MouseButton.Left);
        window.UpdateLayout();
        Assert.Equal(page.RowIndexForLetter("M") * ArtistListPageViewModel.RowHeight, scroll.Offset.Y, 1);

        window.MouseMove(LetterPoint("S"));
        window.MouseUp(LetterPoint("S"), MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var target = page.RowIndexForLetter("S");
        Assert.Equal(target * ArtistListPageViewModel.RowHeight, scroll.Offset.Y, 1);

        // The first S artist's circle is realised and at the top of the grid.
        var first = page.Artists.First(a => ArtistIndex.LetterOf(a.Name) == "S");
        var top = ArtistButton(view, first.Name).TranslatePoint(default, scroll)!.Value.Y;
        Assert.InRange(top, 0, 10);
        window.Close();
    }

    [AvaloniaFact]
    public void FiveHundredArtists_LayOutQuickly_AndRealizeOnlyTheRowsOnScreen()
    {
        using var rig = RigWithArtists(ManyArtists(500));
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        var clock = Stopwatch.StartNew();
        OpenArtists(rig);
        window.UpdateLayout();
        clock.Stop();
        _output.WriteLine($"Artists page with 500 artists: open + first layout {clock.ElapsedMilliseconds} ms");

        var grid = MobileFixtures.Named<ItemsControl>(view, "ArtistGrid");
        var realized = grid.GetRealizedContainers().Count();
        _output.WriteLine($"Realized rows: {realized} of {((ArtistListPageViewModel)rig.Shell.CurrentPage!).Rows.Count}");
        Assert.InRange(realized, 3, 15);
        Assert.True(clock.ElapsedMilliseconds < 3000, $"open + layout took {clock.ElapsedMilliseconds} ms");

        clock.Restart();
        MobileFixtures.Find<ArtistListPage>(view).JumpToLetter("W");
        window.UpdateLayout();
        clock.Stop();
        _output.WriteLine($"Jump to W + layout {clock.ElapsedMilliseconds} ms");
        Assert.InRange(grid.GetRealizedContainers().Count(), 3, 15);
        window.Close();
    }
}
