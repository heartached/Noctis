using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Noctis.Plugins.AlbumTown.Town;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>
/// Album Town's page: a header (Plaza / Apartments, call an album, new crowd), the plaza or the
/// building, a card for the clicked resident (Play, Go to album) and a credit line.
/// </summary>
public sealed class AlbumTownView : UserControl
{
    public const string Credit = "Album Town by Noctis · idea by Jafezy JD";
    public const int MaxCalled = 20;

    private static readonly IBrush HeaderBg = new ImmutableSolidColorBrush(Color.FromRgb(0x1B, 0x18, 0x22));
    private static readonly IBrush CardBg = new ImmutableSolidColorBrush(Color.FromRgb(0x24, 0x20, 0x2B));
    private static readonly IBrush Muted = new ImmutableSolidColorBrush(Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush Faint = new ImmutableSolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush White = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush TabOn = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush AccentBrush = new ImmutableSolidColorBrush(CreaturePainter.Accent);
    private static readonly IBrush Chip = new ImmutableSolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));

    private readonly ITownHost _host;
    private readonly CoverCache _covers = new(600);
    private readonly Random _random;
    private readonly PlazaCanvas _plaza;
    private readonly ApartmentsCanvas _apartments;
    private readonly ScrollViewer _apartmentsScroll;
    private readonly TextBlock _message;
    private readonly TextBlock _status;
    private readonly Button _plazaTab;
    private readonly Button _apartmentsTab;
    private readonly TextBox _search;
    private readonly StackPanel _suggestions;
    private readonly Border _suggestionsBox;
    private readonly Border _card;
    private readonly Image _cardCover;
    private readonly TextBlock _cardTitle;
    private readonly TextBlock _cardArtist;
    private readonly TextBlock _cardStats;
    private readonly TextBlock _cardReason;
    private readonly Button _cardShowAlbum;
    private TownAlbum? _cardAlbum;

    private IReadOnlyList<TownAlbum> _town = Array.Empty<TownAlbum>();
    private IReadOnlyDictionary<string, string> _byFile = new Dictionary<string, string>();
    private List<CastMember> _cast = new();
    private readonly List<string> _called = new();
    private bool _loadStarted;

    public AlbumTownView(ITownHost host, Random? random = null)
    {
        _host = host;
        _random = random ?? new Random();
        var world = new PlazaWorld(800, 500, _random);
        _plaza = new PlazaCanvas(world, _covers)
        {
            Beat = () => _host.TryReadBeat(out var p) ? p : null,
            ShowNames = host.ShowNames,
        };
        world.Chatty = host.Chatty;
        _plaza.CreatureClicked += (_, c) => ShowCard(c.Album, c.Member.Reason);

        _apartments = new ApartmentsCanvas(_covers);
        _apartmentsScroll = new ScrollViewer
        {
            Content = _apartments,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsVisible = false,
        };
        _apartments.Viewport = () => new Rect(_apartmentsScroll.Offset.X, _apartmentsScroll.Offset.Y, _apartmentsScroll.Viewport.Width, _apartmentsScroll.Viewport.Height);
        _apartmentsScroll.ScrollChanged += (_, _) => _apartments.InvalidateVisual();
        _apartments.ResidentClicked += (_, a) => ShowCard(a, null);

        _message = new TextBlock
        {
            Text = "Loading your town...",
            Foreground = new ImmutableSolidColorBrush(Color.FromRgb(0x5A, 0x52, 0x66)),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            IsHitTestVisible = false,
        };

        _plazaTab = Tab("Plaza", () => ShowTab(plaza: true));
        _apartmentsTab = Tab("Apartments", () => ShowTab(plaza: false));

        _search = new TextBox
        {
            PlaceholderText = "Call an album to the plaza",
            Width = 240,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(14, 6),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationName(_search, "Call an album to the plaza");
        _search.TextChanged += (_, _) => UpdateSuggestions();
        _search.KeyDown += OnSearchKey;

        var newCrowd = Pill("New crowd", () => Recast(fresh: true));
        newCrowd.Margin = new Thickness(8, 0, 0, 0);

        var title = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Album Town", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = White },
                new TextBlock { Text = "Idea by Jafezy JD", FontSize = 11, Foreground = Faint },
            },
        };
        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _plazaTab, _apartmentsTab },
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _search, newCrowd } };
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(tabs, 1);
        Grid.SetColumn(tools, 2);
        headerGrid.Children.Add(title);
        headerGrid.Children.Add(tabs);
        headerGrid.Children.Add(tools);
        var header = new Border { Background = HeaderBg, Padding = new Thickness(18, 10), Child = headerGrid };

        _status = new TextBlock { FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        var credit = new TextBlock { Text = Credit, FontSize = 11, Foreground = Faint, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var footerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(credit, 1);
        footerGrid.Children.Add(_status);
        footerGrid.Children.Add(credit);
        var footer = new Border { Background = HeaderBg, Padding = new Thickness(18, 7), Child = footerGrid };

        _suggestions = new StackPanel { Spacing = 2 };
        _suggestionsBox = new Border
        {
            Background = CardBg,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 8, 18, 0),
            Width = 300,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            BoxShadow = BoxShadows.Parse("0 8 24 0 #66000000"),
            Child = _suggestions,
            IsVisible = false,
        };

        _cardCover = new Image { Width = 88, Height = 88, Stretch = Stretch.UniformToFill };
        _cardTitle = new TextBlock { FontSize = 16, FontWeight = FontWeight.Bold, Foreground = White, TextWrapping = TextWrapping.Wrap, MaxWidth = 230 };
        _cardArtist = new TextBlock { FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 230 };
        _cardStats = new TextBlock { FontSize = 12, Foreground = Muted };
        _cardReason = new TextBlock { FontSize = 11, Foreground = AccentBrush };
        var play = Pill("Play", () => { if (_cardAlbum is { } a) _host.Play(a); }, accent: true);
        _cardShowAlbum = Pill("Go to album", () => { if (_cardAlbum is { } a) _host.ShowAlbum(a); });
        var close = Pill("Close", HideCard);
        var cardText = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(14, 0, 0, 0),
            Children = { _cardTitle, _cardArtist, _cardStats, _cardReason },
        };
        var cardTop = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = _cardCover, Width = 88, Height = 88 }, cardText },
        };
        var cardButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 14, 0, 0), Children = { play, _cardShowAlbum, close } };
        _card = new Border
        {
            Background = CardBg,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16),
            Margin = new Thickness(18),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            BoxShadow = BoxShadows.Parse("0 10 30 0 #70000000"),
            Child = new StackPanel { Children = { cardTop, cardButtons } },
            IsVisible = false,
        };

        var stage = new Panel { Children = { _plaza, _apartmentsScroll, _message, _card, _suggestionsBox } };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(stage);
        Content = root;

        ShowTab(plaza: true);
        UpdateStatus();
    }

    public PlazaCanvas Plaza => _plaza;
    public ApartmentsCanvas ApartmentsView => _apartments;
    public IReadOnlyList<TownAlbum> Town => _town;
    public IReadOnlyList<CastMember> Cast => _cast;
    public CoverCache Covers => _covers;
    public bool IsCardOpen => _card.IsVisible;
    public TownAlbum? CardAlbum => _cardAlbum;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _host.NowPlayingChanged += OnNowPlayingChanged;
        _host.SettingsChanged += OnSettingsChanged;
        if (!_loadStarted)
        {
            _loadStarted = true;
            _ = LoadAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _host.NowPlayingChanged -= OnNowPlayingChanged;
        _host.SettingsChanged -= OnSettingsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Reads the library off the UI thread, then fills the plaza and the building.</summary>
    public async Task LoadAsync()
    {
        IReadOnlyList<TownAlbum> town;
        IReadOnlyDictionary<string, string> byFile;
        IReadOnlyList<string> called;
        try
        {
            (town, byFile, called) = await Task.Run(() =>
            {
                var t = AlbumGrouping.Group(_host.LoadTracks(), out var map);
                return (t, map, _host.LoadCalled());
            });
        }
        catch (Exception ex)
        {
            _message.Text = "Album Town could not read your library: " + ex.Message;
            return;
        }
        SetTown(town, byFile, called);
    }

    /// <summary>Fills the town directly (tests, preview).</summary>
    public void SetTown(IReadOnlyList<TownAlbum> town, IReadOnlyDictionary<string, string> byFile, IReadOnlyList<string> called)
    {
        _loadStarted = true;
        _town = town;
        _byFile = byFile;
        _called.Clear();
        var known = town.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        _called.AddRange(called.Where(known.Contains).Take(MaxCalled));
        _apartments.Building = Apartments.Build(town);
        _message.Text = town.Count == 0 ? "Nobody lives here yet. Add music to your library and your albums will move in." : "";
        _message.IsVisible = town.Count == 0;
        Recast(fresh: false);
    }

    private string? NowPlayingKey()
        => _host.NowPlaying is { FilePath: { Length: > 0 } path } && _byFile.TryGetValue(path, out var key) ? key : null;

    /// <summary>Picks a new crowd (keeps the called ones and the one playing).</summary>
    public void Recast(bool fresh)
    {
        var random = fresh ? new Random(_random.Next()) : _random;
        _cast = PlazaCast.Choose(_town, _host.CrowdSize, random, NowPlayingKey(), _called).ToList();
        _plaza.World.SetCast(_cast);
        _plaza.World.SetNowPlaying(NowPlayingKey(), _host.IsPlaying);
        _plaza.InvalidateVisual();
        UpdateStatus();
    }

    /// <summary>Brings <paramref name="album"/> to the plaza (it walks in), remembering the call.</summary>
    public void Call(TownAlbum album)
    {
        _called.Remove(album.Key);
        _called.Insert(0, album.Key);
        if (_called.Count > MaxCalled) _called.RemoveRange(MaxCalled, _called.Count - MaxCalled);
        _host.SaveCalled(_called);

        var member = new CastMember(album, CastReason.Called);
        if (_cast.FindIndex(m => m.Album.Key == album.Key) is var at and >= 0)
        {
            _cast[at] = member;
            _plaza.World.SetCast(_cast);
            if (_plaza.World.Creatures.FirstOrDefault(c => c.Album.Key == album.Key) is { } here) _plaza.World.Wave(here);
            return;
        }
        if (_cast.Count >= Math.Clamp(_host.CrowdSize, PlazaCast.MinCrowd, PlazaCast.MaxCrowd))
        {
            // Someone who was only out for a walk goes home to make room.
            var leave = _cast.FindLastIndex(m => m.Reason == CastReason.Random);
            if (leave < 0) leave = _cast.FindLastIndex(m => m.Reason != CastReason.Called && m.Reason != CastReason.NowPlaying);
            if (leave >= 0)
            {
                _cast.RemoveAt(leave);
                _plaza.World.SetCast(_cast);
            }
        }
        _cast.Add(member);
        _plaza.World.Call(member);
        UpdateStatus();
    }

    private void OnNowPlayingChanged(object? sender, EventArgs e)
    {
        var key = NowPlayingKey();
        if (key is not null && _cast.All(m => m.Album.Key != key) && _town.FirstOrDefault(a => a.Key == key) is { } album)
        {
            // The album you just started walks into the plaza to dance.
            var member = new CastMember(album, CastReason.NowPlaying);
            if (_cast.Count >= Math.Clamp(_host.CrowdSize, PlazaCast.MinCrowd, PlazaCast.MaxCrowd))
            {
                var leave = _cast.FindLastIndex(m => m.Reason == CastReason.Random);
                if (leave >= 0) { _cast.RemoveAt(leave); _plaza.World.SetCast(_cast); }
            }
            _cast.Add(member);
            _plaza.World.Call(member);
        }
        _plaza.World.SetNowPlaying(key, _host.IsPlaying);
        UpdateStatus();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _plaza.ShowNames = _host.ShowNames;
        _plaza.World.Chatty = _host.Chatty;
        if (_cast.Count != Math.Clamp(_host.CrowdSize, PlazaCast.MinCrowd, Math.Min(PlazaCast.MaxCrowd, Math.Max(1, _town.Count))))
            Recast(fresh: false);
        _plaza.InvalidateVisual();
    }

    private void ShowTab(bool plaza)
    {
        _plaza.IsVisible = plaza;
        _apartmentsScroll.IsVisible = !plaza;
        _plazaTab.Background = plaza ? TabOn : Brushes.Transparent;
        _apartmentsTab.Background = plaza ? Brushes.Transparent : TabOn;
        _search.IsVisible = plaza;
        _suggestionsBox.IsVisible = false;
        if (!plaza) _apartments.InvalidateVisual();
        UpdateStatus();
    }

    /// <summary>Opens the resident's card: cover, numbers, Play and Go to album.</summary>
    public void ShowCard(TownAlbum album, CastReason? reason)
    {
        _cardAlbum = album;
        _cardCover.Source = _covers.Get(album.ArtworkPath)?.Image;
        _cardTitle.Text = album.Title.Length == 0 ? "Untitled" : album.Title;
        _cardArtist.Text = album.Year > 0 ? $"{Or(album.Artist, "Unknown artist")} · {album.Year.ToString(CultureInfo.InvariantCulture)}" : Or(album.Artist, "Unknown artist");
        var songs = album.IsSingle && album.TrackCount == 1 ? "Single" : album.TrackCount == 1 ? "1 song" : $"{album.TrackCount.ToString(CultureInfo.InvariantCulture)} songs";
        var plays = album.PlayCount == 1 ? "1 play" : $"{album.PlayCount.ToString(CultureInfo.InvariantCulture)} plays";
        _cardStats.Text = $"{songs} · {plays}";
        _cardReason.Text = reason switch
        {
            CastReason.Called => "You called this one to the plaza",
            CastReason.NowPlaying => "Playing now",
            CastReason.Recent => "Played recently",
            CastReason.MostPlayed => "One of your most played",
            CastReason.Random => "Out for a walk",
            _ => album.Genre.Length > 0 ? album.Genre : "",
        };
        _cardReason.IsVisible = _cardReason.Text.Length > 0;
        _cardShowAlbum.IsEnabled = album.AlbumId.Length > 0;
        _card.IsVisible = true;
    }

    public void HideCard()
    {
        _card.IsVisible = false;
        _cardAlbum = null;
        _cardCover.Source = null;
    }

    private void UpdateSuggestions()
    {
        _suggestions.Children.Clear();
        var found = PlazaCast.Find(_town, _search.Text ?? "", 6);
        foreach (var a in found)
        {
            var album = a;
            var row = new Button
            {
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6),
                CornerRadius = new CornerRadius(10),
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = Or(a.Title, "Untitled"), Foreground = White, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = Or(a.Artist, "Unknown artist"), Foreground = Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            AutomationName(row, "Call " + Or(a.Title, "Untitled"));
            row.Click += (_, _) => CallFromSearch(album);
            _suggestions.Children.Add(row);
        }
        _suggestionsBox.IsVisible = found.Count > 0 && _plaza.IsVisible;
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _search.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && PlazaCast.Find(_town, _search.Text ?? "", 1) is [var first])
        {
            CallFromSearch(first);
            e.Handled = true;
        }
    }

    private void CallFromSearch(TownAlbum album)
    {
        _search.Text = "";
        _suggestionsBox.IsVisible = false;
        Call(album);
    }

    private void UpdateStatus()
    {
        if (_town.Count == 0) { _status.Text = ""; return; }
        var albums = _town.Count == 1 ? "1 resident" : $"{_town.Count.ToString(CultureInfo.InvariantCulture)} residents";
        var artists = _apartments.Building.Apartments.Count;
        _status.Text = _plaza.IsVisible
            ? $"{_cast.Count.ToString(CultureInfo.InvariantCulture)} in the plaza · {albums} in town"
            : $"{artists.ToString(CultureInfo.InvariantCulture)} {(artists == 1 ? "apartment" : "apartments")} · {albums}";
    }

    private static string Or(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;

    private static Button Tab(string text, Action click)
    {
        var b = Pill(text, click);
        b.Background = Brushes.Transparent;
        return b;
    }

    private static Button Pill(string text, Action click, bool accent = false)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = White },
            Padding = new Thickness(14, 6),
            CornerRadius = new CornerRadius(999),
            Background = accent ? AccentBrush : Chip,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationName(b, text);
        b.Click += (_, _) => click();
        return b;
    }

    private static void AutomationName(Control c, string name)
        => Avalonia.Automation.AutomationProperties.SetName(c, name);
}
