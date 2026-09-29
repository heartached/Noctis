using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// S24: the LRC editor stamped on a bubbling window KeyDown, so once a clicked button
/// (Play/Pause, a row's nudge or clear) held focus the Button took Space and clicked itself:
/// Space paused the song instead of stamping the line.
/// </summary>
public class LrcEditorSpaceKeyTests
{
    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("DismissIcon", null, out _)) return;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
    }

    [AvaloniaFact]
    public void Space_StampsTheLine_EvenWhenPlayPauseHasFocus()
    {
        EnsureAppResources();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        player.State = PlaybackState.Playing;
        player.Position = TimeSpan.FromSeconds(7);
        var vm = new LrcEditorViewModel(new Track { Title = "T", Artist = "A" }, player, new StubMetadata(),
            null, "First line\nSecond line");

        var dialog = new LrcEditorDialog(vm);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        // The normal flow: click Play/Pause (it keeps focus), then tap Space to stamp.
        var playPause = dialog.GetVisualDescendants().OfType<Button>()
            .First(b => ReferenceEquals(b.Command, player.PlayPauseCommand));
        playPause.Focus();
        dialog.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        dialog.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TimeSpan.FromSeconds(7), vm.Lines[0].Timestamp);
        Assert.Equal(1, vm.SelectedIndex);
        Assert.Equal(PlaybackState.Playing, player.State);
        dialog.Close();
    }
}
