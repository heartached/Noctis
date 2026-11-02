using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Cosmo, 11-02): "an easier way to make playlists, maybe by genre". The Playlists
/// page's New ▸ Playlist from a Genre or Artist dialog.
/// </summary>
public class QuickPlaylistTests
{
    private static Track T(string title, string artist, string genre, string album = "A", int track = 1) => new()
    {
        Title = title, Artist = artist, AlbumArtist = artist, Genre = genre, Album = album, TrackNumber = track,
        FilePath = $@"C:\m\{title}.mp3",
    };

    private static List<Track> Library() => new()
    {
        T("r2", "Band", "Rock", "Second", 2),
        T("r1", "Band", "Rock", "First", 1),
        T("rp", "Other", "rock; Pop"),
        T("p1", "Singer", "Pop"),
        T("j1", "Trio", "Jazz"),
        T("u1", "Unknown Artist", ""),
    };

    private sealed class Sink
    {
        public string? Name;
        public IReadOnlyList<Track>? Tracks;
        public Task Create(string name, IReadOnlyList<Track> tracks) { Name = name; Tracks = tracks; return Task.CompletedTask; }
    }

    [Fact]
    public void Genres_AreCountedAcrossMultiGenreTags_MostSongsFirst()
    {
        var vm = new QuickPlaylistViewModel(Library(), new Sink().Create);
        Assert.Equal(new[] { "Rock", "Pop", "Jazz" }, vm.Entries.Select(e => e.Name));
        Assert.Equal(3, vm.Entries[0].Count);
        Assert.Equal("3 songs", vm.Entries[0].CountText);
        Assert.Equal("1 song", vm.Entries[2].CountText);
    }

    [Fact]
    public void Artists_LeaveOutUnknownArtist_AndSearchFilters()
    {
        var vm = new QuickPlaylistViewModel(Library(), new Sink().Create) { ShowArtists = true };
        Assert.DoesNotContain(vm.Entries, e => e.Name == "Unknown Artist");
        Assert.Equal("Band", vm.Entries[0].Name);
        vm.SearchText = "tri";
        Assert.Equal("Trio", Assert.Single(vm.Entries).Name);
        vm.SearchText = "zzz";
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task AllSongs_MakesThePlaylistInAlbumOrder_AndCloses()
    {
        var sink = new Sink();
        var vm = new QuickPlaylistViewModel(Library(), sink.Create);
        var closed = false;
        vm.Closed += (_, _) => closed = true;
        await vm.AllSongsCommand.ExecuteAsync(vm.Entries.First(e => e.Name == "Rock"));
        Assert.Equal("Rock", sink.Name);
        Assert.Equal(new[] { "r1", "r2", "rp" }, sink.Tracks!.Select(t => t.Title));
        Assert.True(closed);
    }

    [Fact]
    public async Task Mix_IsNamedForItsSeed()
    {
        var sink = new Sink();
        var vm = new QuickPlaylistViewModel(Library(), sink.Create) { ShowArtists = true };
        await vm.MixCommand.ExecuteAsync(vm.Entries.First(e => e.Name == "Band"));
        Assert.Equal("Band Mix", sink.Name);
        Assert.Contains(sink.Tracks!, t => t.Title == "r1");
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    [AvaloniaFact]
    public void Dialog_OpensInPillHost_WithARowPerGenre()
    {
        EnsureAppStyles();
        var vm = new QuickPlaylistViewModel(Library(), new Sink().Create);
        var win = new QuickPlaylistDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(5); }
            var all = win.GetVisualDescendants().ToList();
            Assert.Single(all.OfType<PillDialogHost>());
            var mixButtons = all.OfType<Button>().Where(b => b.Command == vm.MixCommand).ToList();
            Assert.Equal(3, mixButtons.Count);
            Assert.True(all.OfType<Button>().Single(b => b.Name == "GenresTab").Classes.Contains("pill-primary"));
        }
        finally { win.Close(); }
    }
}
