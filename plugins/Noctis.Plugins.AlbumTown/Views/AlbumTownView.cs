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
/// apartments (AlbumTownView.Apartments.cs), a card for the clicked resident (Play, Go to album)
/// and a credit line. Colours follow the app (<see cref="TownTheme.From"/>), dark by default.
/// </summary>
public sealed partial class AlbumTownView : UserControl
{
    public const string Byline = "by Jafezy JD";
    public const string Credit = "Album Town " + Byline;
    public const int MaxCalled = 20;

    private readonly ITownHost _host;
    // The plaza draws ~100 px covers for at most 60 residents; the apartments' pets and the shelf
    // show smaller ones (up to ~84 px at 125%).
    private readonly CoverCache _covers = new(capacity: 160, thumbSize: 192);
    private readonly CoverCache _residentCovers = new(capacity: 400, thumbSize: 128);
    private readonly List<(Button Button, TextBlock Label, bool Accent)> _pills = new();
    private readonly Border _header;
    private readonly Border _footer;
    private readonly TextBlock _titleText;
    private readonly TextBlock _byline;
    private readonly TextBlock _credit;
    private readonly Slider _zoom;
    private readonly StackPanel _zoomBox;
    private readonly DispatcherTimer _zoomSave;
    private TownTheme _theme = TownTheme.Dark;
    private readonly Random _random;
    private readonly PlazaCanvas _plaza;
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
            Motion = Noctis.Plugins.AlbumTown.Town.Motion.Parse(host.Motion),
        };
        world.Chatty = host.Chatty;
        _plaza.CreatureClicked += (_, c) => ShowCard(c.Album, c.Member.Reason);
        _plaza.Zoom = host.LoadZoom();
        _covers.CoverReady += (_, _) =>
        {
            // The card opened before its cover was decoded: show it now.
            if (_cardAlbum is { } a && _cardCover!.Source is null) _cardCover.Source = _covers.Get(a.ArtworkPath)?.Image;
        };

        _message = new TextBlock
        {
            Text = "Loading your town...",
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

        // Creature size: the same zoom as Ctrl+wheel on the plaza, remembered between sessions.
        _zoom = new Slider
        {
            Minimum = PlazaCanvas.MinZoom,
            Maximum = PlazaCanvas.MaxZoom,
            SmallChange = 0.1,
            LargeChange = 0.2,
            Value = _plaza.Zoom,
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationName(_zoom, "Creature size");
        ToolTip.SetTip(_zoom, "Creature size (Ctrl + mouse wheel on the plaza)");
        _zoom.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) _plaza.Zoom = _zoom.Value;
        };
        _zoomSave = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _zoomSave!.Stop();
            _host.SaveZoom(_plaza.Zoom);
        });
        _plaza.ZoomChanged += (_, _) =>
        {
            if (Math.Abs(_zoom.Value - _plaza.Zoom) > 0.001) _zoom.Value = _plaza.Zoom;
            _zoomSave.Stop();
            _zoomSave.Start();
        };
        _zoomBox = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = "Size", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, _zoom },
        };

        _titleText = new TextBlock { Text = "Album Town", FontSize = 18, FontWeight = FontWeight.Bold };
        _byline = new TextBlock { Text = Byline, FontSize = 11 };
        var title = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _titleText, _byline },
        };
        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _plazaTab, _apartmentsTab },
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _zoomBox, _search, newCrowd } };
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(tabs, 1);
        Grid.SetColumn(tools, 2);
        headerGrid.Children.Add(title);
        headerGrid.Children.Add(tabs);
        headerGrid.Children.Add(tools);
        _header = new Border { Padding = new Thickness(18, 10), Child = headerGrid };

        _status = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        _credit = new TextBlock { Text = Credit, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var footerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_credit, 1);
        footerGrid.Children.Add(_status);
        footerGrid.Children.Add(_credit);
        _footer = new Border { Padding = new Thickness(18, 7), Child = footerGrid };

        _suggestions = new StackPanel { Spacing = 2 };
        _suggestionsBox = new Border
        {
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
        _cardTitle = new TextBlock { FontSize = 16, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, MaxWidth = 230 };
        _cardArtist = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 230 };
        _cardStats = new TextBlock { FontSize = 12 };
        _cardReason = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 230 };
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
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16),
            Margin = new Thickness(18),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            BoxShadow = BoxShadows.Parse("0 10 30 0 #70000000"),
            Child = new StackPanel { Children = { cardTop, cardButtons } },
            IsVisible = false,
        };

        var apartments = BuildApartments();
        var stage = new Panel { Children = { _plaza, apartments, _message, _card, _suggestionsBox, _pickerLayer } };
        var root = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(_footer, Dock.Bottom);
        root.Children.Add(_header);
        root.Children.Add(_footer);
        root.Children.Add(stage);
        Content = root;

        ApplyTheme(TownTheme.Dark);
        ShowTab(plaza: true);
        UpdateStatus();
    }

    public PlazaCanvas Plaza => _plaza;
    public ApartmentsCanvas ApartmentsView => _apartments;
    public IReadOnlyList<TownAlbum> Town => _town;
    public IReadOnlyList<CastMember> Cast => _cast;
    /// <summary>Big covers for the plaza and the card.</summary>
    public CoverCache Covers => _covers;
    /// <summary>Small covers for the Apartments.</summary>
    public CoverCache ResidentCovers => _residentCovers;
    public TownTheme Palette => _theme;
    public Slider ZoomSlider => _zoom;
    public bool IsCardOpen => _card.IsVisible;
    public TownAlbum? CardAlbum => _cardAlbum;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _host.NowPlayingChanged += OnNowPlayingChanged;
        _host.SettingsChanged += OnSettingsChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        ApplyTheme(TownTheme.From(this));
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
        ActualThemeVariantChanged -= OnThemeChanged;
        if (_zoomSave.IsEnabled)
        {
            _zoomSave.Stop();
            _host.SaveZoom(_plaza.Zoom);
        }
        FlushApartments();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Frees the decoded covers (the window closed).</summary>
    public void ReleaseCovers()
    {
        _covers.Dispose();
        _residentCovers.Dispose();
        _softCovers.Dispose();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme(TownTheme.From(this));

    /// <summary>Paints the header, footer, card and plaza in <paramref name="theme"/>.</summary>
    public void ApplyTheme(TownTheme theme)
    {
        _theme = theme;
        _plaza.Palette = theme;
        Foreground = theme.Text;
        _header.Background = theme.Chrome;
        _footer.Background = theme.Chrome;
        _card.Background = theme.Card;
        _suggestionsBox.Background = theme.Card;
        _titleText.Foreground = theme.Text;
        _byline.Foreground = theme.Faint;
        _credit.Foreground = theme.Faint;
        _status.Foreground = theme.Muted;
        _message.Foreground = theme.Muted;
        _cardTitle.Foreground = theme.Text;
        _cardArtist.Foreground = theme.Muted;
        _cardStats.Foreground = theme.Muted;
        _cardReason.Foreground = theme.IsDark || TownTheme.Luminance(theme.Accent) < 0.6 ? theme.AccentBrush : theme.Muted;
        foreach (var (button, label, accent) in _pills)
        {
            button.Background = accent ? theme.AccentBrush : theme.Chip;
            label.Foreground = accent ? theme.OnAccent : theme.Text;
        }
        if (_zoomBox.Children[0] is TextBlock sizeLabel) sizeLabel.Foreground = theme.Muted;
        _plazaTab.Background = _plaza.IsVisible ? theme.TabOn : Brushes.Transparent;
        _apartmentsTab.Background = _plaza.IsVisible ? Brushes.Transparent : theme.TabOn;
        ApplyApartmentsTheme(theme);
    }

    /// <summary>Reads the library, the playlists and the saved building off the UI thread, then
    /// fills the plaza and the apartments.</summary>
    public async Task LoadAsync()
    {
        IReadOnlyList<TownAlbum> town;
        IReadOnlyDictionary<string, string> byFile;
        IReadOnlyDictionary<string, string> byTrack;
        IReadOnlyList<string> called;
        IReadOnlyList<TrackInfo> tracks;
        IReadOnlyList<PlaylistInfo> playlists;
        ApartmentLayout? layout;
        try
        {
            (town, byFile, byTrack, called, tracks, playlists, layout) = await Task.Run(() =>
            {
                var all = _host.LoadTracks();
                var t = AlbumGrouping.Group(all, out var map, out var ids);
                return (t, map, ids, _host.LoadCalled(), all, _host.LoadPlaylists(), _host.LoadApartments());
            });
        }
        catch (Exception ex)
        {
            _message.Text = "Album Town could not read your library: " + ex.Message;
            return;
        }
        SetTown(town, byFile, called);
        SetApartments(tracks, playlists, layout, byTrack);
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
        OnTownChanged();
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
        _plaza.Wake();
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
            _plaza.Wake();
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
        _plaza.Wake();
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
                // Make room: someone out for a walk, else an earlier "now playing" visitor, else
                // anyone the user did not call. (Only Random ones used to leave, so a long album
                // shuffle grew the plaza past its size, one dancer per album.)
                var leave = _cast.FindLastIndex(m => m.Reason == CastReason.Random);
                if (leave < 0) leave = _cast.FindIndex(m => m.Reason == CastReason.NowPlaying);
                if (leave < 0) leave = _cast.FindLastIndex(m => m.Reason != CastReason.Called);
                if (leave >= 0) { _cast.RemoveAt(leave); _plaza.World.SetCast(_cast); }
            }
            _cast.Add(member);
            _plaza.World.Call(member);
        }
        _plaza.World.SetNowPlaying(key, _host.IsPlaying);
        _plaza.Wake();
        ApplyNowPlayingToApartments();
        UpdateStatus();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _plaza.ShowNames = _host.ShowNames;
        _plaza.World.Chatty = _host.Chatty;
        _apartments.ShowNames = _host.ShowNames;
        _life.Chatty = _host.Chatty;
        _plaza.Motion = _apartments.Motion = Noctis.Plugins.AlbumTown.Town.Motion.Parse(_host.Motion);
        if (_cast.Count != Math.Clamp(_host.CrowdSize, PlazaCast.MinCrowd, Math.Min(PlazaCast.MaxCrowd, Math.Max(1, _town.Count))))
            Recast(fresh: false);
        _plaza.Wake();
    }

    private void ShowTab(bool plaza)
    {
        _plaza.IsVisible = plaza;
        _apartmentsPage.IsVisible = !plaza;
        if (plaza) HidePicker();
        _plazaTab.Background = plaza ? _theme.TabOn : Brushes.Transparent;
        _apartmentsTab.Background = plaza ? Brushes.Transparent : _theme.TabOn;
        _search.IsVisible = plaza;
        _zoomBox.IsVisible = plaza;
        _suggestionsBox.IsVisible = false;
        if (!plaza) _apartments.Refresh();
        UpdateStatus();
    }

    /// <summary>Opens the resident's card: cover, numbers, Play and Go to album.</summary>
    public void ShowCard(TownAlbum album, CastReason? reason, string? note = null)
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
            _ => note ?? (album.Genre.Length > 0 ? album.Genre : ""),
        };
        _cardReason.IsVisible = _cardReason.Text.Length > 0;
        _cardShowAlbum.IsEnabled = album.AlbumId.Length > 0;
        // In the Apartments the card sits above the shelf, not on it.
        _card.Margin = new Thickness(18, 18, 18, 18 + (_apartmentsPage.IsVisible ? _shelfBar.Bounds.Height : 0));
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
                        new TextBlock { Text = Or(a.Title, "Untitled"), Foreground = _theme.Text, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = Or(a.Artist, "Unknown artist"), Foreground = _theme.Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
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
        _status.Text = _plaza.IsVisible
            ? $"{_cast.Count.ToString(CultureInfo.InvariantCulture)} in the plaza · {albums} in town"
            : ApartmentsStatus();
    }

    private static string Or(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;

    private Button Tab(string text, Action click)
    {
        var b = Pill(text, click);
        b.Background = Brushes.Transparent;
        return b;
    }

    private Button Pill(string text, Action click, bool accent = false)
    {
        var label = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold };
        var b = new Button
        {
            Content = label,
            Padding = new Thickness(14, 6),
            CornerRadius = new CornerRadius(999),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationName(b, text);
        b.Click += (_, _) => click();
        _pills.Add((b, label, accent));
        return b;
    }

    private static void AutomationName(Control c, string name)
        => Avalonia.Automation.AutomationProperties.SetName(c, name);
}
