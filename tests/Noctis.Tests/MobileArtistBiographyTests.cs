using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The artist page's biography (Discord #mobile-beta: "could it be implemented for the artist
/// biographies too"): the album page's description paragraph for an artist, asked once by name
/// as the page opens, cancelled when it closes, folded to three lines with MORE, the whole text
/// on the album description's glass sheet; nothing at all when there is no source, no bio, or the
/// artist is a placeholder.
/// </summary>
public class MobileArtistBiographyTests
{
    private const string LongBio =
        "Silk Sonic is an American musical superduo consisting of Bruno Mars and Anderson .Paak. " +
        "Their debut album was released in November 2021, preceded by the singles Leave the Door Open, " +
        "Skate and Smokin Out the Window, and it went on to win four Grammy Awards including Record of the Year. " +
        "The record is a love letter to 1970s soul, recorded with Bootsy Collins as the host of the evening.";

    private sealed class FakeBiographies(string? text) : IArtistBiographySource
    {
        public CancellationToken Token;
        public List<string> Asked { get; } = new();
        public TaskCompletionSource<string?>? Pending;

        public Task<string?> GetBiographyAsync(string artist, CancellationToken ct)
        {
            Asked.Add(artist);
            Token = ct;
            return Pending?.Task ?? Task.FromResult(text);
        }
    }

    private static MobileFixtures.Rig Rig(string artist, IArtistBiographySource? biographies)
    {
        var tracks = Enumerable.Range(1, 3).Select(i =>
        {
            var t = MobileFixtures.Song($"Song {i}", artist: artist);
            t.TrackNumber = i;
            return t;
        }).ToArray();
        var album = MobileFixtures.MakeAlbum("An Evening with Silk Sonic", artist, tracks);
        var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        if (biographies == null) return rig;
        // ArtistBiographies is init-only: rebuild the shell around the same fakes with it set.
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            TintFactory = rig.Shell.TintFactory,
            ArtistBiographies = biographies,
        };
        return new MobileFixtures.Rig
        {
            Shell = shell, Library = rig.Library, Player = rig.Player, Persistence = rig.Persistence,
            History = rig.History, Root = rig.Root,
        };
    }

    private static void Pump(int frames = 40)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);   // the frame clock is the wall clock
        }
    }

    [AvaloniaFact]
    public void Biography_IsHidden_WithoutASource_OrWhenThereIsNone()
    {
        foreach (var source in new IArtistBiographySource?[] { null, new FakeBiographies(null), new FakeBiographies("   ") })
        {
            using var rig = Rig("Silk Sonic", source);
            var window = MobileFixtures.Mount(rig.Shell, out var view);
            rig.Shell.OpenArtistCommand.Execute("Silk Sonic");
            window.UpdateLayout();
            var vm = Assert.IsType<ArtistPageViewModel>(rig.Shell.CurrentPage);
            Assert.Null(vm.Biography);
            Assert.False(vm.HasBiography);
            Assert.False(MobileFixtures.Named<Button>(MobileFixtures.Find<ArtistPage>(view), "BiographyBlock").IsVisible);
            window.Close();
        }
    }

    [Theory]
    [InlineData("Unknown Artist")]
    [InlineData("Various Artists")]
    public async Task PlaceholderArtists_AreNeverAsked(string name)
    {
        var source = new FakeBiographies(LongBio);
        using var rig = Rig(name, source);
        rig.Shell.OpenArtistCommand.Execute(name);
        var vm = Assert.IsType<ArtistPageViewModel>(rig.Shell.CurrentPage);
        await vm.BiographyLoad;
        Assert.Empty(source.Asked);
        Assert.Null(vm.Biography);
    }

    [AvaloniaFact]
    public async Task Biography_IsAskedOnceByName_AndCancelledWhenThePageCloses()
    {
        var source = new FakeBiographies(null) { Pending = new TaskCompletionSource<string?>() };
        using var rig = Rig("Silk Sonic", source);
        rig.Shell.OpenArtistsCommand.Execute(null);
        rig.Shell.OpenArtistCommand.Execute("Silk Sonic");
        var vm = Assert.IsType<ArtistPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal(new[] { "Silk Sonic" }, source.Asked);
        Assert.False(source.Token.IsCancellationRequested);

        rig.Shell.NavigateBackCommand.Execute(null);
        Assert.True(source.Token.IsCancellationRequested);
        source.Pending.SetResult(LongBio);   // lands after the close: dropped
        await vm.BiographyLoad;
        Assert.Null(vm.Biography);
    }

    /// <summary>The album description's look: three lines with MORE over the end of the last, under
    /// the buttons and above the carousel; a tap pops the whole text on the glass sheet under the
    /// artist's name, and Back closes the sheet only.</summary>
    [AvaloniaFact]
    public void Biography_FoldsToThreeLines_UnderTheButtons_AndATapPopsTheWholeText()
    {
        using var rig = Rig("Silk Sonic", new FakeBiographies("  " + LongBio + "\n"));
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenArtistCommand.Execute("Silk Sonic");
        window.UpdateLayout();
        window.UpdateLayout();

        var page = MobileFixtures.Find<ArtistPage>(view);
        var vm = Assert.IsType<ArtistPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal(LongBio, vm.Biography);   // trimmed
        var block = MobileFixtures.Named<Button>(page, "BiographyBlock");
        var clip = MobileFixtures.Named<Border>(page, "BiographyClip");
        var text = MobileFixtures.Named<TextBlock>(page, "BiographyText");
        var more = MobileFixtures.Named<Border>(page, "BiographyMore");
        Assert.True(block.IsVisible);
        Assert.Equal(LongBio, text.Text);
        Assert.True(text.Bounds.Height > 3 * 20, $"sample too short to fold: {text.Bounds.Height}");
        Assert.Equal(AlbumPage.DescriptionLines * 20, clip.Bounds.Height, 1);
        Assert.True(more.IsVisible);
        Assert.Equal(0, text.Opacity);   // the folded stand-in shows instead

        // Between the button row and the carousel.
        var buttons = MobileFixtures.Named<StackPanel>(page, "ButtonRow");
        var cards = MobileFixtures.Named<ScrollViewer>(page, "CardScroll");
        var blockTop = block.TranslatePoint(new Point(0, 0), page)!.Value.Y;
        Assert.True(blockTop >= buttons.TranslatePoint(new Point(0, buttons.Bounds.Height), page)!.Value.Y);
        Assert.True(cards.TranslatePoint(new Point(0, 0), page)!.Value.Y >= blockTop + block.Bounds.Height);

        block.Command!.Execute(null);
        Pump(20);
        window.UpdateLayout();
        var note = Assert.IsType<AlbumNoteViewModel>(rig.Shell.AlbumNote);
        Assert.Equal("Silk Sonic", note.Title);
        Assert.Equal("Biography", note.Artist);
        Assert.Equal(LongBio, note.Text);
        Assert.False(note.IsExplicit);
        var sheet = view.FindControl<AlbumNoteSheet>("Note")!;
        Assert.True(sheet.IsVisible);
        Assert.Equal(LongBio, MobileFixtures.Named<TextBlock>(sheet, "NoteText").Text);

        Assert.True(rig.Shell.TryHandleBack());
        Assert.Null(rig.Shell.AlbumNote);
        Assert.Same(vm, rig.Shell.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void ShortBiography_ShowsWhole_WithoutMore()
    {
        using var rig = Rig("Silk Sonic", new FakeBiographies("A short note."));
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenArtistCommand.Execute("Silk Sonic");
        window.UpdateLayout();
        window.UpdateLayout();

        var page = MobileFixtures.Find<ArtistPage>(view);
        var text = MobileFixtures.Named<TextBlock>(page, "BiographyText");
        Assert.Equal(text.Bounds.Height, MobileFixtures.Named<Border>(page, "BiographyClip").Bounds.Height, 1);
        Assert.False(MobileFixtures.Named<Border>(page, "BiographyMore").IsVisible);
        Assert.Equal(1, text.Opacity);
        window.Close();
    }
}
