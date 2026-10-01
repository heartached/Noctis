using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
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
}
