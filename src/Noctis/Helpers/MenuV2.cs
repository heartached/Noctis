using System;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Localization;
using Noctis.Models;
using Path = Avalonia.Controls.Shapes.Path;
using Track = Noctis.Models.Track;

namespace Noctis.Helpers;

/// <summary>
/// Building blocks of the v2 context menu (10-09 redesign): a <see cref="ContextMenu"/> with
/// the <see cref="MenuClass"/> class picks up the scoped styles in Styles.axaml (compact rows,
/// muted line icons, rounded-rect hover, hairline border), and these helpers add the parts a
/// plain MenuItem list can't express — a context header, a row of quick-action tiles and an
/// inline rating row. The builders opt in with <c>Build(..., v2: true)</c>, so every menu that
/// does not ask keeps the original look; switching a view over is that one argument.
/// </summary>
public static class MenuV2
{
    /// <summary>Class on the ContextMenu that scopes the v2 styles.</summary>
    public const string MenuClass = "v2";

    /// <summary>Class on a non-row item (header, tile row, rating row): no hover, no focus.</summary>
    public const string PanelClass = "mv2-panel";

    /// <summary>Class on a submenu that should hide itself when none of its entries is visible.</summary>
    public const string AutoHideClass = "mv2-autohide";

    /// <summary>Line icons are drawn on a 24 grid; 2.25 there is 1.5px at the 16px row size.</summary>
    private const double IconStroke = 2.25;

    // Panel items show their content as-is: no icon column, no hover chrome, no chevron.
    private static readonly IControlTemplate PanelTemplate = new FuncControlTemplate<MenuItem>((item, _) =>
        new ContentPresenter
        {
            Name = "PART_HeaderPresenter",
            [!ContentPresenter.ContentProperty] = item[!HeaderedSelectingItemsControl.HeaderProperty],
        });

    /// <summary>Resolves a geometry resource from the host, falling back to the application.</summary>
    public static Geometry? FindGeometry(Control host, string key)
    {
        if (host.TryFindResource(key, out var value) && value is Geometry g) return g;
        if (Application.Current?.TryGetResource(key, null, out value) == true && value is Geometry ag) return ag;
        return null;
    }

    /// <summary>
    /// A stroked line icon (see the "Menu line icons" block in Icons.axaml) at
    /// <paramref name="size"/> px. Its colour comes from the <c>mv2-icon</c> styles, so it
    /// follows the theme and turns red on a danger row.
    /// </summary>
    public static Viewbox LineIcon(Control host, string key, double size = 16)
    {
        var path = new Path
        {
            Data = FindGeometry(host, key),
            StrokeThickness = IconStroke,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        path.Classes.Add("mv2-icon");
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas };
    }

    /// <summary>The Path inside a <see cref="LineIcon"/>.</summary>
    public static Path? IconPath(object? icon)
        => (icon as Viewbox)?.Child is Canvas { Children.Count: > 0 } c ? c.Children[0] as Path : null;

    /// <summary>A list row: header text plus a line icon (or none).</summary>
    public static MenuItem Row(Control host, string header, string? iconKey, bool visible = true)
    {
        var item = new MenuItem { Header = header, IsVisible = visible };
        if (iconKey != null) item.Icon = LineIcon(host, iconKey);
        return item;
    }

    /// <summary>Size of the round artist picture in a View Artist ▸ row.</summary>
    public const double AvatarSize = 20;

    /// <summary>
    /// A round artist picture for a row's icon slot: the Artists page's placeholder (person
    /// glyph on the artwork-placeholder circle) with a <see cref="CachedImage"/> over it, which
    /// shows once the caller sets its <see cref="CachedImage.SourcePath"/>.
    ///
    /// The Fluent template's icon slot is a fixed 16×16 box (a Viewbox inside a ContentControl,
    /// both clipping) that would shrink a 20px picture to 16. So the picture measures as 16 (a
    /// -2 margin all round) and draws 2px past the slot on each side, with the clip lifted on
    /// the slot's parts once it is attached: same centre line as the line icons, and the name
    /// starts where every other row's label does.
    /// </summary>
    public static Border ArtistAvatar(out CachedImage photo)
    {
        var glyph = new Path { Stretch = Stretch.Uniform, Height = 11, Opacity = 0.35,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (Application.Current?.TryGetResource("ArtistsIcon", null, out var data) == true && data is Geometry g)
            glyph.Data = g;
        glyph[!Shape.FillProperty] = glyph.GetResourceObservable("SystemControlForegroundBaseHighBrush").ToBinding();
        photo = new CachedImage { Stretch = Stretch.UniformToFill, Width = AvatarSize, Height = AvatarSize, IsVisible = false };
        var panel = new Panel();
        panel.Children.Add(glyph);
        panel.Children.Add(photo);
        var circle = new Border
        {
            Width = AvatarSize, Height = AvatarSize, CornerRadius = new CornerRadius(AvatarSize / 2),
            ClipToBounds = true, Child = panel, Margin = new Thickness(-(AvatarSize - 16) / 2),
        };
        circle[!Border.BackgroundProperty] = circle.GetResourceObservable("ArtworkPlaceholderBackground").ToBinding();
        circle.Classes.Add("mv2-avatar");
        circle.AttachedToVisualTree += (_, _) =>
        {
            // Local values outrank the template's ClipToBounds; stops at the row.
            foreach (var part in circle.GetVisualAncestors().OfType<Control>().TakeWhile(c => c is not MenuItem))
                part.ClipToBounds = false;
        };
        return circle;
    }

    /// <summary>
    /// Wraps a non-row control (header, tiles, rating) in a MenuItem that is not focusable,
    /// has no hover chrome, and keeps pointer presses on its background from counting as a
    /// click (which would close the menu). Buttons inside still work.
    /// </summary>
    public static MenuItem Panel(Control content)
    {
        var surface = new Border { Background = Brushes.Transparent, Child = content };
        surface.PointerPressed += (_, e) => e.Handled = true;
        surface.PointerReleased += (_, e) => e.Handled = true;
        var item = new MenuItem { Header = surface, Focusable = false, Template = PanelTemplate };
        item.Classes.Add(PanelClass);
        return item;
    }

    /// <summary>
    /// A quick-action tile: icon over a short label. Clicking closes the menu, then the
    /// button runs its Command with its CommandParameter (set per Bind).
    /// </summary>
    public static Button Tile(Control host, ContextMenu menu, string iconKey, string label)
        => Tile(host, menu.Close, iconKey, label);

    /// <summary>A quick-action tile for a menu closed by <paramref name="close"/> (a MenuFlyout's Hide).</summary>
    public static Button Tile(Control host, Action close, string iconKey, string label)
    {
        var fullName = label;
        // Translations run long (es "Reproducir a continuación", fr "Ajouter à la file
        // d'attente"): the label wraps to two centred lines, then ellipsizes, inside its
        // quarter of the card — it never widens the card or spills out of the tile.
        var text = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxLines = 2,
        };
        var stack = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Stretch };
        var icon = LineIcon(host, iconKey, 15);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(icon);
        stack.Children.Add(text);
        var button = new Button { Content = stack, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
        button.Classes.Add("mv2-tile");
        ToolTip.SetTip(button, fullName);
        AutomationProperties.SetName(button, fullName);
        button.Click += (_, _) => close();
        return button;
    }

    /// <summary>Re-labels a tile in place (the player's Play tile reads Pause while playing).</summary>
    public static void SetTile(Control host, Button tile, string iconKey, string label)
    {
        if (tile.Content is not StackPanel { Children: [Viewbox icon, TextBlock text] }) return;
        if (IconPath(icon) is { } path) path.Data = FindGeometry(host, iconKey);
        text.Text = label;
        ToolTip.SetTip(tile, label);
        AutomationProperties.SetName(tile, label);
    }

    /// <summary>The tile row: four equal columns.</summary>
    public static MenuItem TileRow(params Button[] tiles)
    {
        var grid = new UniformGrid { Columns = tiles.Length, Rows = 1, Margin = new Thickness(0, 0, 0, 2) };
        foreach (var t in tiles) grid.Children.Add(t);
        return Panel(grid);
    }

    /// <summary>Points a tile at the same command and parameter as the row it replaces.</summary>
    public static void Sync(Button tile, MenuItem from)
    {
        tile.Command = from.Command;
        tile.CommandParameter = from.CommandParameter;
    }

    /// <summary>
    /// Shows a separator only between two visible groups, and hides an auto-hide submenu
    /// with nothing visible inside, so optional entries never leave an empty group, a
    /// doubled line or a trailing line behind.
    /// </summary>
    public static void RefreshLayout(ItemCollection items)
    {
        foreach (var sub in items.OfType<MenuItem>())
            if (sub.Classes.Contains(AutoHideClass))
                sub.IsVisible = sub.Items.OfType<MenuItem>().Any(i => i.IsVisible);

        Separator? pending = null;
        var seenContent = false;
        foreach (var o in items)
        {
            if (o is Separator s)
            {
                s.IsVisible = false;
                if (seenContent && pending == null) pending = s;
                continue;
            }
            if (o is Control { IsVisible: true })
            {
                if (pending != null) { pending.IsVisible = true; pending = null; }
                seenContent = true;
            }
        }
    }
}

/// <summary>
/// Inline rating row (Finder-tags style): five clickable stars and a clear button, all on
/// one row instead of a Rate ▸ submenu. Each star sends a <see cref="RateRequest"/> with its
/// count; clear sends 0. Hovering a star previews the rating.
/// </summary>
public sealed class MenuV2Rating
{
    public MenuItem Item { get; }
    /// <summary>Index 1..5 = that many stars; index 0 is the clear button.</summary>
    public Button[] Buttons { get; } = new Button[6];
    private int _current;

    public MenuV2Rating(Control host, ContextMenu menu)
    {
        var stars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 1; i <= 5; i++)
        {
            var star = MakeButton(menu, MenuV2.LineIcon(host, "MenuLineStar", 15), Loc.T("Menu.RateTip", i));
            var n = i;
            star.PointerEntered += (_, _) => Light(n);
            Buttons[i] = star;
            stars.Children.Add(star);
        }
        stars.PointerExited += (_, _) => Light(_current);

        var clear = MakeButton(menu, MenuV2.LineIcon(host, "MenuLineClose", 13), Loc.T("Menu.ClearRating"));
        clear.Margin = new Thickness(2, 0, 0, 0);
        Buttons[0] = clear;
        stars.Children.Add(clear);

        var icon = MenuV2.LineIcon(host, "MenuLineStar");
        icon.Margin = new Thickness(0, 0, 11, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var label = new TextBlock
        {
            Text = Loc.T("LibrarySongs.Rating"), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            MinHeight = 32,
            Margin = new Thickness(10, 0, 4, 0),
        };
        grid.Children.Add(icon);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        Grid.SetColumn(stars, 2);
        grid.Children.Add(stars);
        Item = MenuV2.Panel(grid);
        // The app-wide TextBlock style is semibold; take the row font so "Rating" matches its neighbours.
        label[!TextBlock.FontFamilyProperty] = Item[!TemplatedControl.FontFamilyProperty];
    }

    private static Button MakeButton(ContextMenu menu, Control icon, string tip)
    {
        var b = new Button { Content = icon };
        b.Classes.Add("mv2-star");
        ToolTip.SetTip(b, tip);
        AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => menu.Close();
        return b;
    }

    public void Bind(ICommand? command, Track track)
    {
        _current = Math.Clamp(track.Rating, 0, 5);
        for (var i = 0; i <= 5; i++)
        {
            Buttons[i].Command = command;
            Buttons[i].CommandParameter = new RateRequest(track, i);
        }
        Buttons[0].IsVisible = _current > 0;
        Light(_current);
    }

    private void Light(int upTo)
    {
        for (var i = 1; i <= 5; i++)
            Buttons[i].Classes.Set("lit", i <= upTo);
    }
}
