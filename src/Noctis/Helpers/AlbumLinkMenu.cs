using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// The album page "…" menu's Link / Unlink Tracks From ▸ submenus (owner 11-14: the plain
/// "Name · 17" list "looks very cluttered" and ran off the screen). One row per album: its
/// cover, the title on one line with the explicit "E" right after it, and the song count as
/// quiet right-aligned text. Albums and EPs first, then singles (their " - Single" suffix left
/// to the tooltip), split by a hairline; the card is a fixed width and scrolls past ~9 rows.
/// </summary>
internal static class AlbumLinkMenu
{
    /// <summary>The card's width: titles get room before they trim, the card stays compact.</summary>
    public const double CardWidth = 332;

    /// <summary>Rows shown before the card scrolls (the half row hints that it does).</summary>
    public const double VisibleRows = 9.5;

    /// <summary>The menu row height (Styles.axaml MenuItem MinHeight) and the card's padding + border.</summary>
    private const double RowHeight = 32, CardChrome = 6 * 2 + 1 * 2;

    public static double CardMaxHeight => VisibleRows * RowHeight + CardChrome;

    private static readonly Regex SingleSuffix = new(@"\s+-\s+Single\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A single: one song, a "- Single" title, or a release the tags/count call a single.</summary>
    public static bool IsSingle(Album album) =>
        album.Tracks.Count <= 1 || SingleSuffix.IsMatch(album.Name) || album.ReleaseType == ReleaseType.Single;

    /// <summary>The row's title: a single drops the " - Single" its group already says.</summary>
    public static string DisplayName(Album album)
    {
        if (!IsSingle(album)) return album.Name;
        var name = SingleSuffix.Replace(album.Name, "");
        return name.Length > 0 ? name : album.Name;
    }

    /// <summary>Albums and EPs, then singles, each by title.</summary>
    public static (List<Album> albums, List<Album> singles) Group(IEnumerable<Album> albums)
    {
        var all = albums.ToList();
        static List<Album> Sorted(IEnumerable<Album> list) =>
            list.OrderBy(DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();
        return (Sorted(all.Where(a => !IsSingle(a))), Sorted(all.Where(IsSingle)));
    }

    /// <summary>"17 songs" / "1 song".</summary>
    public static string CountText(Album album) => Loc.Plural("Count.Songs", album.Tracks.Count);

    /// <summary>Refills <paramref name="item"/> with one row per album and hides it when there are none.</summary>
    public static void Fill(MenuItem item, IReadOnlyList<Album> albums, ICommand command)
    {
        item.Items.Clear();
        var (full, singles) = Group(albums);
        foreach (var album in full) item.Items.Add(Row(item, album, command));
        if (full.Count > 0 && singles.Count > 0) item.Items.Add(new Separator());
        foreach (var album in singles) item.Items.Add(Row(item, album, command));
        item.IsVisible = albums.Count > 0;
        if (!item.Classes.Contains("album-link-menu"))
        {
            item.Classes.Add("album-link-menu");
            item.SubmenuOpened += (_, e) =>
            {
                if (ReferenceEquals(e.Source, item)) SizeCard(item);
            };
        }
    }

    /// <summary>The submenu card (the Fluent template's popup Border): fixed width, capped height
    /// (its ScrollViewer scrolls the rest). Local values, as MenuChrome's 300 px cap is one.</summary>
    internal static void SizeCard(MenuItem item)
    {
        if (item.GetVisualDescendants().OfType<Popup>().FirstOrDefault(p => p.Name == "PART_Popup")?.Child is not Border card)
            return;
        card.MaxWidth = CardWidth;
        card.Width = CardWidth;
        card.MaxHeight = CardMaxHeight;
    }

    private static MenuItem Row(MenuItem owner, Album album, ICommand command)
    {
        var count = CountText(album);
        var item = new MenuItem { Command = command, CommandParameter = album };
        item.Classes.Add("album-link-row");

        // A TextBlock title: a plain string header would read "_" in an album name as an access key.
        var title = new TextBlock
        {
            Text = DisplayName(album),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Border
        {
            Margin = new Thickness(6, 0, 0, 0),
            IsVisible = album.IsExplicit,
            Child = new TextBlock { Text = "E", FontSize = 8, Opacity = 0.9, Classes = { "explicit-badge-text", "compact" } },
        };
        badge.Classes.Add("explicit-badge");
        badge.Classes.Add("compact");
        ToolTip.SetTip(badge, Loc.T("AlbumDetail.ExplicitTip"));

        // Left-aligned "*,Auto": the title column is only as wide as the title, so the badge
        // sits right after it, and only a long title trims (the badge stays).
        var titleLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), HorizontalAlignment = HorizontalAlignment.Left };
        titleLine.Children.Add(title);
        Grid.SetColumn(badge, 1);
        titleLine.Children.Add(badge);

        var countText = new TextBlock
        {
            Text = count,
            FontSize = 12,
            // Into the template's empty gesture/chevron lane (24 px, no submenu here), so the
            // counts end about as far from the card's edge as the covers start.
            Margin = new Thickness(14, 0, -14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        countText.Bind(TextBlock.ForegroundProperty, countText.GetResourceObservable("MenuV2SecondaryBrush"));

        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        line.Children.Add(titleLine);
        Grid.SetColumn(countText, 1);
        line.Children.Add(countText);
        item.Header = line;

        // The app-wide TextBlock style is semibold; take the row font so the titles read like
        // every other menu row.
        title[!TextBlock.FontFamilyProperty] = owner[!TemplatedControl.FontFamilyProperty];
        countText[!TextBlock.FontFamilyProperty] = owner[!TemplatedControl.FontFamilyProperty];

        item.Icon = MenuV2.AlbumThumb(out var cover);
        cover.DecodeWidth = 64;
        cover.SourcePath = album.ArtworkPath;
        cover.IsVisible = !string.IsNullOrEmpty(album.ArtworkPath);

        ToolTip.SetTip(item, album.Name);
        AutomationProperties.SetName(item, $"{album.Name}, {count}{(album.IsExplicit ? ", " + Loc.T("AlbumDetail.ExplicitTip") : "")}");
        return item;
    }
}
