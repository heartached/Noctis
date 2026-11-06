using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-12: the phone draws EXACTLY the desktop's icon for every function both apps have.
/// Each phone key below is rasterized next to the desktop glyph it stands for, the way the
/// desktop draws it on its 24 grid (filled, or stroked like LineIcon/MenuV2 for the MenuLine*
/// line icons), and the two must cover the same pixels. A phone glyph redrawn by hand, or a
/// desktop icon changed without its phone twin, fails here.
/// </summary>
public class MobileDesktopIconParityTests
{
    /// <summary>Phone dictionary, phone key, desktop key, desktop stroke width (0 = filled; negative =
    /// stroked at that width and filled, as MenuV2 draws a favourite's heart).</summary>
    public static readonly TheoryData<string, string, string, double> Pairs = new()
    {
        // Player bar
        { "PhoneIcons", "PhPlayFill", "PlayIcon", 0 },
        { "PhoneIcons", "PhPauseFill", "PauseIcon", 0 },
        { "PhoneIcons", "PhForwardFill", "FastForwardIcon", 0 },
        { "PhoneIcons", "PhRewindFill", "RewindIcon", 0 },
        { "PhoneIcons", "PhShuffle", "ShuffleIcon", 0 },
        { "PhoneIcons", "PhRepeat", "RepeatAllIcon", 0 },
        { "PhoneIcons", "PhRepeatOne", "RepeatOneIcon", 0 },
        { "PhoneIcons", "PhVolumeLow", "SpeakerLowIcon", 0 },
        { "PhoneIcons", "PhVolumeHigh", "SpeakerHighIcon", 0 },
        { "PhoneIcons", "PhLyrics", "MenuLineLyrics", 2.25 },
        { "PhoneIcons", "PhQueue", "MenuLineQueue", 2.25 },
        { "LibraryIcons", "LibOnRepeatIcon", "RepeatAllIcon", 0 },
        // Favourite heart (HeartIcon's pair) and the artist star
        { "PhoneIcons", "PhHeart", "HeartOutlineIcon", 0 },
        { "PhoneIcons", "PhHeartFill", "FavoritesFilledIcon", 0 },
        { "TabIcons", "TabFavoritesIcon", "HeartOutlineIcon", 0 },
        { "TabIcons", "TabFavoritesFilledIcon", "FavoritesFilledIcon", 0 },
        { "PhoneIcons", "PhStar", "MenuLineStar", 2.25 },
        { "PhoneIcons", "PhStarFill", "StarIcon", 0 },
        // Common buttons
        { "PhoneIcons", "PhMore", "MoreDotsIcon", 0 },
        { "LibraryIcons", "LibMoreIcon", "MoreDotsIcon", 0 },
        { "PhoneIcons", "PhSearch", "SearchIcon", 0 },
        { "TabIcons", "TabSearchIcon", "SearchIcon", 0 },
        { "TabIcons", "TabSearchFilledIcon", "SearchIcon", 0 },
        { "PhoneIcons", "PhBack", "ChevronLeftIcon", 0 },
        { "PhoneIcons", "PhChevronRight", "ChevronRightIcon", 0 },
        { "LibraryIcons", "LibChevronIcon", "ChevronRightIcon", 0 },
        { "PhoneIcons", "PhCheck", "CheckmarkIcon", 0 },
        { "PhoneIcons", "PhClose", "MenuLineClose", 2.25 },
        { "LibraryIcons", "QueueCloseIcon", "MenuLineClose", 2.25 },
        { "PhoneIcons", "PhPerson", "RailPersonIcon", 0 },
        { "PhoneIcons", "PhSort", "SortIcon", 0 },
        { "PhoneIcons", "PhFilter", "FilterIcon", 0 },
        { "PhoneIcons", "PhPlus", "AddIcon", 0 },
        // Song menu (the phone's long-press sheet)
        { "PhoneIcons", "PhPlayNext", "MenuLinePlayNext", 2.25 },
        { "PhoneIcons", "PhAddToQueue", "MenuLineAddToQueue", 2.25 },
        { "PhoneIcons", "PhAddToPlaylist", "MenuLinePlaylistAdd", 2.25 },
        { "PhoneIcons", "PhTrash", "MenuLineTrash", 2.25 },
        { "PhoneIcons", "PhPin", "MenuLinePin", 2.25 },
        { "PhoneIcons", "PhAlbum", "MenuLineAlbum", 2.25 },
        { "PhoneIcons", "PhArtist", "MenuLineArtist", 2.25 },
        { "PhoneIcons", "PhMenuHeart", "MenuLineHeart", 2.25 },
        { "PhoneIcons", "PhMenuHeartFill", "MenuLineHeart", -2.25 },
        { "LibraryIcons", "LibPinIcon", "PinFilledIcon", 0 },
        { "LibraryIcons", "QueueReorderIcon", "LineGrip", 3.6 },
        // The Library ⋯ menu and the Add to Playlist list, at the menus' 2.25u weight (owner 10-13)
        { "PhoneIcons", "PhFolder", "MenuLineFolder", 2.25 },
        { "PhoneIcons", "PhRescan", "PluginsRefreshGlyph", 2.25 },
        { "PhoneIcons", "PhSettings", "SettingsIcon", -0.75 },
        { "PhoneIcons", "PhPlaylist", "MenuLinePlaylist", 2.25 },
        // Playlist editing (owner 10-20): Rename, Remove from Playlist (Delete Playlist is PhTrash)
        { "PhoneIcons", "PhEdit", "MenuLineEdit", 2.25 },
        { "PhoneIcons", "PhPlaylistRemove", "MenuLinePlaylistRemove", 2.25 },
        // The lossless waveform (the album page's badge, Now Playing's quality badge): the mini player's
        { "PhoneIcons", "PhLossless", "LosslessWaveIcon", 0 },
        // Account: the desktop account card's glyphs (SettingsView.axaml)
        { "AccountPage", "AccountPhoneIcon", "AccountPhoneIcon", 0 },
        { "AccountPage", "AccountEyeIcon", "AccountEyeIcon", 0 },
        { "AccountPage", "AccountEyeOffIcon", "AccountEyeOffIcon", 0 },
    };

    private const int Scale = 8; // 24 grid -> 192 px

    [Theory]
    [MemberData(nameof(Pairs))]
    public void PhoneGlyph_DrawsTheDesktopGlyph(string dictionary, string phoneKey, string desktopKey, double strokeWidth)
    {
        var phone = Geometries(Path.Combine("src", "Noctis.Mobile", "Views", dictionary + ".axaml"))[phoneKey];
        var desktop = DesktopGeometries()[desktopKey];

        using var phoneMask = Rasterize(phone, 0);
        using var desktopMask = Rasterize(desktop, strokeWidth);

        int differ = 0, covered = 0;
        for (var y = 0; y < phoneMask.Height; y++)
        for (var x = 0; x < phoneMask.Width; x++)
        {
            var a = phoneMask.GetPixel(x, y).Alpha > 127;
            var b = desktopMask.GetPixel(x, y).Alpha > 127;
            if (a || b) covered++;
            if (a != b) differ++;
        }
        Assert.True(covered > 0, $"{phoneKey} draws nothing");
        // Edge pixels may flip between a stroke and its expanded outline; a different glyph
        // differs by far more than 2% of the shape.
        Assert.True(differ <= covered * 0.02, $"{phoneKey} vs desktop {desktopKey}: {differ} of {covered} pixels differ");
    }

    /// <summary>
    /// The Home tab (owner 10-13) draws the desktop sidebar's Home: a PNG mask (Home ICON.png,
    /// 48 px over the 24-grid, no vector exists), traced. Drawn at the PNG's own size, the outline
    /// glyph's coverage stays within half a pixel's alpha of the PNG everywhere and carries the
    /// same ink; the filled glyph keeps the same silhouette (it covers every pixel the PNG does
    /// outside the door).
    /// </summary>
    [Fact]
    public void TabHome_TracesTheDesktopsHomePng()
    {
        var icons = Geometries(Path.Combine("src", "Noctis.Mobile", "Views", "TabIcons.axaml"));
        using var png = SKBitmap.Decode(Path.Combine(RepoRoot(), "src", "Noctis.UI", "Assets", "Icons", "Home ICON.png"));
        var scale = png.Width / 24;
        using var outline = Rasterize(icons["TabHomeIcon"], 0, scale);
        using var filled = Rasterize(icons["TabHomeFilledIcon"], 0, scale);

        double inkPhone = 0, inkDesktop = 0, worst = 0;
        int missed = 0, inked = 0;
        for (var y = 0; y < png.Height; y++)
        for (var x = 0; x < png.Width; x++)
        {
            var a = outline.GetPixel(x, y).Alpha / 255.0;
            var b = png.GetPixel(x, y).Alpha / 255.0;
            inkPhone += a;
            inkDesktop += b;
            worst = Math.Max(worst, Math.Abs(a - b));
            if (b > 0.5)
            {
                inked++;
                if (filled.GetPixel(x, y).Alpha < 128) missed++;
            }
        }
        Assert.True(worst < 0.5, $"a pixel differs by {worst:0.00} alpha");
        Assert.InRange(inkPhone / inkDesktop, 0.95, 1.05);
        // Only the door's frame, knocked out of the filled house, may go uncovered.
        Assert.True(missed <= inked * 0.12, $"{missed} of {inked} desktop pixels outside the filled house");
    }

    /// <summary>
    /// The desktop draws its sidebar's Songs, Albums, Artists and Playlists from 48 px PNG masks
    /// (IconKeyToGeometryConverter; no vector exists): the phone's Library rows, Search chips and
    /// Playlists tab draw those masks traced to vectors. Drawn at the PNG's own size each covers
    /// the same pixels (intersection over union of the inked pixels) and carries the same ink.
    /// </summary>
    [Theory]
    [InlineData("LibraryIcons", "ChipSongsIcon", "Songs ICON.png")]
    [InlineData("LibraryIcons", "ChipAlbumsIcon", "Albums ICON.png")]
    [InlineData("LibraryIcons", "ChipArtistsIcon", "Artists ICON.png")]
    [InlineData("LibraryIcons", "ChipPlaylistsIcon", "Playlist icon.png")]
    [InlineData("TabIcons", "TabPlaylistsIcon", "Playlist icon.png")]
    [InlineData("TabIcons", "TabHomeIcon", "Home ICON.png")]
    public void TracedGlyph_CoversTheDesktopsPngMask(string dictionary, string key, string png)
    {
        var data = Geometries(Path.Combine("src", "Noctis.Mobile", "Views", dictionary + ".axaml"))[key];
        using var mask = SKBitmap.Decode(Path.Combine(RepoRoot(), "src", "Noctis.UI", "Assets", "Icons", png));
        using var phone = Rasterize(data, 0, mask.Width / 24);
        int both = 0, either = 0;
        double inkPhone = 0, inkDesktop = 0;
        for (var y = 0; y < mask.Height; y++)
        for (var x = 0; x < mask.Width; x++)
        {
            var a = phone.GetPixel(x, y).Alpha / 255.0;
            var b = mask.GetPixel(x, y).Alpha / 255.0;
            inkPhone += a;
            inkDesktop += b;
            if (a > 0.5 && b > 0.5) both++;
            if (a > 0.5 || b > 0.5) either++;
        }
        var iou = (double)both / either;
        Assert.True(iou >= 0.8 && inkPhone / inkDesktop is > 0.85 and < 1.15,
            $"{key} vs {png}: IoU {iou:0.00}, ink {inkPhone / inkDesktop:0.00}");
    }

    /// <summary>The selected Playlists tab is the traced mask's silhouette with the note's head
    /// filled in: it covers every inked pixel of the outline glyph.</summary>
    [Fact]
    public void TabPlaylistsFilled_IsTheTracedOutline_FilledIn()
    {
        var icons = Geometries(Path.Combine("src", "Noctis.Mobile", "Views", "TabIcons.axaml"));
        using var outline = Rasterize(icons["TabPlaylistsIcon"], 0, 2);
        using var filled = Rasterize(icons["TabPlaylistsFilledIcon"], 0, 2);
        int inked = 0, missed = 0;
        for (var y = 0; y < outline.Height; y++)
        for (var x = 0; x < outline.Width; x++)
            if (outline.GetPixel(x, y).Alpha > 127)
            {
                inked++;
                if (filled.GetPixel(x, y).Alpha < 64) missed++;
            }
        Assert.True(inked > 0 && missed <= inked * 0.01, $"{missed} of {inked} outline pixels left out of the filled glyph");
    }

    /// <summary>
    /// Bug (10-20, emulator): the Lyrics screen's Output button drew nothing - it still named
    /// PhOutput, dropped from PhoneIcons when Now Playing's Output moved to the desktop's
    /// speaker. Every glyph a phone view names must be one the phone loads: its own icon
    /// dictionaries and page resources, or Noctis.UI's Icons.axaml (AndroidApp.axaml merges only that).
    /// </summary>
    [Fact]
    public void EveryGlyphThePhoneNames_IsOneItLoads()
    {
        var views = Path.Combine(RepoRoot(), "src", "Noctis.Mobile", "Views");
        var defined = new HashSet<string>(Geometries(Path.Combine("src", "Noctis.UI", "Assets", "Icons.axaml")).Keys);
        var used = new List<(string File, string Key)>();
        foreach (var file in Directory.EnumerateFiles(views, "*.axaml"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"<StreamGeometry x:Key=""(\w+)"""))
                defined.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"(?:Data=""|Property=""Data"" Value="")\{(?:Dynamic|Static)Resource (\w+)\}"))
                used.Add((Path.GetFileName(file), m.Groups[1].Value));
        }
        Assert.NotEmpty(used);
        var missing = used.Where(u => !defined.Contains(u.Key)).Select(u => $"{u.File}: {u.Key}").ToList();
        Assert.True(missing.Count == 0, "glyphs the phone never loads:\n" + string.Join("\n", missing));
    }

    private static SKBitmap Rasterize(string data, double strokeWidth, int scale = Scale)
    {
        var nonZero = data.StartsWith("F1", StringComparison.Ordinal);
        if (data.StartsWith("F0", StringComparison.Ordinal) || nonZero) data = data[2..];
        using var path = SKPath.ParseSvgPathData(data.Trim()) ?? throw new InvalidOperationException("unparsable: " + data);
        path.FillType = nonZero ? SKPathFillType.Winding : SKPathFillType.EvenOdd;
        var bitmap = new SKBitmap(24 * scale, 24 * scale, SKColorType.Alpha8, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        if (strokeWidth < 0)
        {
            canvas.DrawPath(path, paint);
            strokeWidth = -strokeWidth;
        }
        if (strokeWidth > 0)
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)strokeWidth;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.StrokeJoin = SKStrokeJoin.Round;
        }
        canvas.DrawPath(path, paint);
        return bitmap;
    }

    private static Dictionary<string, string> DesktopGeometries()
    {
        var all = Geometries(Path.Combine("src", "Noctis.UI", "Assets", "Icons.axaml"));
        foreach (var (k, v) in Geometries(Path.Combine("src", "Noctis.UI", "Assets", "IconsMenuExtra.axaml")))
            all[k] = v;
        // The account card's own glyphs ("Account glyphs Icons.axaml has no shape for").
        foreach (var (k, v) in Geometries(Path.Combine("src", "Noctis", "Views", "SettingsView.axaml")))
            if (k.StartsWith("Account", StringComparison.Ordinal)) all.TryAdd(k, v);
        // The lossless waveform lives on the mini player (hoisted there, not in Icons.axaml).
        foreach (var (k, v) in Geometries(Path.Combine("src", "Noctis", "Views", "MiniPlayerWindow.axaml")))
            if (k == "LosslessWaveIcon") all.TryAdd(k, v);
        return all;
    }

    private static Dictionary<string, string> Geometries(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        return Regex.Matches(text, "<StreamGeometry x:Key=\"(\\w+)\"\\s*>(.*?)</StreamGeometry>", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => string.Join(' ', m.Groups[2].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
