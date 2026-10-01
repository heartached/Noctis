using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's icon dictionaries (PhoneIcons, TabIcons, LibraryIcons) are one family on a 24x24
/// grid. PathIcon stretches its Data's bounds to fill the icon box, so every glyph carries the
/// grid's corners as zero-area contours: the bounds are then the whole grid, a box of N dp draws
/// the glyph at N/24 whatever its shape, and the stroke weight stays the same from glyph to glyph.
/// </summary>
public class MobileIconSetTests
{
    private static readonly string[] Dictionaries = { "PhoneIcons", "TabIcons", "LibraryIcons" };

    private static ResourceDictionary Load(string name) =>
        (ResourceDictionary)new ResourceInclude(new Uri("avares://Noctis.Mobile/"))
        {
            Source = new Uri($"avares://Noctis.Mobile/Views/{name}.axaml")
        }.Loaded;

    [AvaloniaFact]
    public void EveryPhoneGlyph_SpansTheWhole24Grid()
    {
        foreach (var name in Dictionaries)
        {
            var dictionary = Load(name);
            Assert.NotEmpty(dictionary.Keys);
            foreach (var key in dictionary.Keys)
            {
                var geometry = Assert.IsAssignableFrom<Geometry>(dictionary[key]);
                Assert.True(geometry.Bounds == new Rect(0, 0, 24, 24), $"{name}/{key} bounds {geometry.Bounds}");
            }
        }
    }

    /// <summary>The button keys the phone views use; they must all exist (pages bind them as
    /// DynamicResources, which fail silently to an empty icon).</summary>
    [AvaloniaFact]
    public void ThePhoneButtonKeys_AreAllDefined()
    {
        var phone = Load("PhoneIcons");
        var keys = new[]
        {
            "PhPlayFill", "PhPauseFill", "PhForwardFill", "PhRewindFill", "PhShuffle", "PhRepeat", "PhRepeatOne",
            "PhHeart", "PhHeartFill", "PhMore", "PhBack", "PhClose", "PhChevronRight", "PhCheck", "PhSearch",
            "PhPerson", "PhList", "PhChevronDown", "PhLyrics", "PhVolumeLow", "PhVolumeHigh", "PhPlayNext",
            "PhAddToQueue", "PhAddToPlaylist", "PhDownload", "PhTrash", "PhPin",
        };
        Assert.Empty(keys.Where(k => !phone.ContainsKey(k)));
    }

    /// <summary>A mistyped DynamicResource key leaves an empty button, not an error: every icon on
    /// the mini player, Now Playing, the Queue sheet and the long-press sheet resolves a glyph.</summary>
    [AvaloniaFact]
    public void EveryIcon_OnThePlayerSurfacesAndTheSheet_ResolvesItsGlyph()
    {
        var songs = Enumerable.Range(0, 3).Select(i => MobileFixtures.Song($"S{i}")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        // The device app merges Noctis.UI's icons too (the cover placeholders still use them).
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        rig.Shell.Player.PlayTracks(songs, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ToggleQueueCommand.Execute(null);
        rig.Shell.OpenTrackSheetCommand.Execute(songs[1]);
        window.UpdateLayout();

        var missing = view.GetVisualDescendants().OfType<PathIcon>()
            .Where(icon => icon.Data == null)
            .Select(icon => icon.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => !string.IsNullOrEmpty(c.Name))?.Name ?? "?")
            .ToList();
        Assert.Empty(missing);
        Assert.True(view.GetVisualDescendants().OfType<PathIcon>().Count() > 30);
        window.Close();
    }
}
