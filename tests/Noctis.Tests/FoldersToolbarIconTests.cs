using System.IO;
using System.Text.RegularExpressions;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26: the Folders page's Manage Folders corner button looked heavy and low quality
/// beside Sort (and the Filter / View buttons of the other pages). A PathIcon stretches its
/// geometry's bounds to the 15 px icon, so a glyph drawn bigger on the grid renders thinner and a
/// thick patch renders as a blob: the old folder (20 wide, a 2.5-unit tab band against 1.5 walls)
/// did both. It must be one even 1.5 ring at the neighbours' size. Measured on the path data in
/// Icons.axaml with Skia's own hit test.
/// </summary>
public class FoldersToolbarIconTests
{
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

    private static readonly string IconsXaml =
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Noctis.UI", "Assets", "Icons.axaml"));

    private static SKPath Icon(string key)
    {
        var m = Regex.Match(IconsXaml, $"<StreamGeometry x:Key=\"{key}\">(?<d>[^<]+)</StreamGeometry>");
        Assert.True(m.Success, key);
        var d = m.Groups["d"].Value.Trim();
        var fillRule = d.StartsWith("F0") ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
        if (d.StartsWith("F0") || d.StartsWith("F1")) d = d[2..];
        var path = SKPath.ParseSvgPathData(d);
        Assert.NotNull(path);
        path.FillType = fillRule;
        return path;
    }

    private static float Side(SKPath p) => Math.Max(p.TightBounds.Width, p.TightBounds.Height);

    /// <summary>Length of the first filled run met walking from (x, y) by (dx, dy).</summary>
    private static float FirstRun(SKPath g, float x, float y, float dx, float dy)
    {
        int i = 0, n = 0;
        while (i < 4000 && !g.Contains(x, y)) { x += dx; y += dy; i++; }
        while (i < 4000 && g.Contains(x, y)) { x += dx; y += dy; i++; n++; }
        return n * MathF.Sqrt(dx * dx + dy * dy);
    }

    [Fact]
    public void ManageFoldersIcon_IsAnEvenRing_AtTheNeighboursSize()
    {
        var folder = Icon("FolderIcon");
        var b = folder.TightBounds;
        var neighbours = new[] { "SortIcon", "FilterIcon", "ViewIcon" }.Select(k => Side(Icon(k))).ToList();
        Assert.InRange(Side(folder), neighbours.Min() - 0.5f, neighbours.Max() + 0.5f);

        // Walls: the tab top, the body's left and right sides and its bottom, all 1.5.
        const float step = 0.01f;
        var walls = new[]
        {
            ("tab top", FirstRun(folder, b.Left + 3, b.Top - 1, 0, step)),
            ("left", FirstRun(folder, b.Left - 1, b.MidY, step, 0)),
            ("right", FirstRun(folder, b.Right + 1, b.MidY, -step, 0)),
            ("bottom", FirstRun(folder, b.MidX, b.Bottom + 1, 0, -step)),
        };
        foreach (var (name, w) in walls)
            Assert.True(w is >= 1.45f and <= 1.56f, $"{name} wall is {w:0.00}");

        // Hollow inside, like the View glyph (not a filled folder).
        Assert.False(folder.Contains(b.MidX, b.MidY));
    }
}
