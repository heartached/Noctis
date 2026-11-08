using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Plugins.AlbumRoulette.Roulette;

namespace Noctis.Plugins.AlbumRoulette.Views;

/// <summary>Album Roulette spins albums; Song Roulette spins single songs (GitHub #198).</summary>
public enum RouletteMode { Albums, Songs }

/// <summary>
/// Album Roulette's window: the Album Roulette / Song Roulette switch and the filter chips on top,
/// the stage (a large cover on a soft accent glow, title, artist and year under it), the round
/// Spin button, then Play, Shuffle and Open album (a song: Play, Play next, Add to queue, Open
/// album). An empty state offers to clear the filters when nothing matches.
/// </summary>
public sealed class RouletteView : UserControl
{
    public const double WindowWidth = 440;
    public const double WindowHeight = 700;
    public const double MinWindowWidth = 360;
    public const double MinWindowHeight = 560;
    /// <summary>Covers that warm up (decode) for the reel; a spin draws its strip from them.</summary>
    public const int PoolSize = 24;

    private readonly IRouletteHost _host;
    private readonly Random _random;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CoverCache _covers = new(capacity: 64, pixelSize: 320);
    private readonly RouletteFilter _filter = new();
    private readonly ReelCanvas _reel = new();
    private readonly List<RouletteAlbum> _pool = new();
    private readonly List<(Button Button, TextBlock Label, Func<bool> On)> _chips = new();
    private readonly List<(Button Button, TextBlock Label)> _pills = new();
    private readonly Button _genreChip;
    private readonly TextBlock _genreLabel;
    private readonly Button _decadeChip;
    private readonly TextBlock _decadeLabel;
    private readonly Button _artistChip;
    private readonly TextBlock _artistLabel;
    private readonly Control[] _kindChips;
    private readonly TextBlock _count;
    private readonly TextBlock _caption;
    private readonly TextBlock _title;
    private readonly TextBlock _artist;
    private readonly TextBlock _year;
    private readonly StackPanel _details;
    private readonly Border _empty;
    private readonly TextBlock _emptyText;
    private readonly Button _clearFilters;
    private readonly TextBlock _hint;
    private readonly Button _spin;
    private readonly TextBlock _spinLabel;
    private readonly Button _play;
    private readonly Button _shuffle;
    private readonly Button _playNext;
    private readonly Button _addToQueue;
    private readonly Button _open;
    private RouletteTheme _theme = RouletteTheme.Dark;
    private RouletteMode _mode = RouletteMode.Albums;
    /// <summary>What is on the reel now: <see cref="_albumList"/> or <see cref="_songList"/>.</summary>
    private IReadOnlyList<RouletteAlbum> _albums = Array.Empty<RouletteAlbum>();
    private IReadOnlyList<RouletteAlbum> _albumList = Array.Empty<RouletteAlbum>();
    private IReadOnlyList<RouletteAlbum> _songList = Array.Empty<RouletteAlbum>();
    private List<RouletteAlbum> _matches = new();
    private bool _loadStarted;
    private int _albumsVersion;
    private bool _loaded;
    private bool _spinWhenLoaded;

    public RouletteView(IRouletteHost host, Random? random = null, Func<DateTimeOffset>? clock = null)
    {
        _host = host;
        _random = random ?? new Random();
        _clock = clock ?? (() => DateTimeOffset.Now);
        _filter.SkipCompilations = host.SkipCompilations;

        // ── Album Roulette / Song Roulette ──
        var albumMode = ModeChip("Album Roulette", RouletteMode.Albums);
        var songMode = ModeChip("Song Roulette", RouletteMode.Songs);
        var modePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 6,
            Margin = new Thickness(0, 0, 0, 8), Children = { albumMode, songMode },
        };

        // ── Chips ──
        (_genreChip, _genreLabel) = MenuChip("Genre", "Genre", BuildGenreMenu);
        (_decadeChip, _decadeLabel) = MenuChip("Decade", "Decade", BuildDecadeMenu);
        var albums = ToggleChip("Albums", () => _filter.Albums, () => _filter.Albums = !_filter.Albums);
        var eps = ToggleChip("EPs", () => _filter.EPs, () => _filter.EPs = !_filter.EPs);
        var singles = ToggleChip("Singles", () => _filter.Singles, () => _filter.Singles = !_filter.Singles);
        var never = ToggleChip("Never played", () => _filter.Played == PlayedFilter.NeverPlayed,
            () => _filter.Played = _filter.Played == PlayedFilter.NeverPlayed ? PlayedFilter.Any : PlayedFilter.NeverPlayed);
        var stale = ToggleChip("Not played in 6 months", () => _filter.Played == PlayedFilter.NotIn6Months,
            () => _filter.Played = _filter.Played == PlayedFilter.NotIn6Months ? PlayedFilter.Any : PlayedFilter.NotIn6Months);
        (_artistChip, _artistLabel) = Chip("", () => { _filter.Artist = null; ApplyFilters(); });
        _chips.Add((_artistChip, _artistLabel, () => true));
        _artistChip.IsVisible = false;
        ToolTip.SetTip(_artistChip, "Remove the artist filter");

        _kindChips = new Control[] { albums, eps, singles };
        var chipPanel = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var c in new Control[] { _artistChip, _genreChip, _decadeChip, albums, eps, singles, never, stale })
        {
            c.Margin = new Thickness(3);
            chipPanel.Children.Add(c);
        }
        _count = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };

        // ── Stage ──
        _reel.Landed += (_, _) => OnLanded();
        _caption = new TextBlock { FontSize = 11.5, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
        _title = new TextBlock
        {
            FontSize = 19, FontWeight = FontWeight.Bold, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap,
        };
        _artist = new TextBlock { FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        _year = new TextBlock { FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        _details = new StackPanel { Children = { _caption, _title, _artist, _year }, Margin = new Thickness(24, 6, 24, 0), MinHeight = 74 };
        _hint = new TextBlock
        {
            Text = "Loading your albums...", FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, IsHitTestVisible = false,
        };

        _emptyText = new TextBlock
        {
            Text = "No albums match these filters", FontSize = 15, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
        };
        _clearFilters = Pill("Clear filters", ClearFilters);
        _clearFilters.HorizontalAlignment = HorizontalAlignment.Center;
        _clearFilters.Margin = new Thickness(0, 12, 0, 0);
        _empty = new Border
        {
            Child = new StackPanel { Children = { _emptyText, _clearFilters }, VerticalAlignment = VerticalAlignment.Center },
            IsVisible = false,
            Margin = new Thickness(24, 0),
        };
        var stage = new Grid { Children = { _reel, _hint, _empty }, MinHeight = 200 };

        // ── Spin and the three pills ──
        _spinLabel = new TextBlock
        {
            Text = "Spin", FontSize = 17, FontWeight = FontWeight.Bold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _spin = new Button
        {
            Content = _spinLabel,
            Width = 88, Height = 88,
            CornerRadius = new CornerRadius(44),
            Padding = new Thickness(8),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsEnabled = false,
            Margin = new Thickness(0, 10, 0, 12),
        };
        AutomationName(_spin, "Spin");
        _spin.Click += (_, _) => Spin();

        _play = Pill("Play", () => { if (Pick is { } a) _host.PlayTracks(a.TrackIds); });
        _shuffle = Pill("Shuffle", () => { if (Pick is { } a) _host.PlayTracks(Shuffled(a.TrackIds)); });
        _playNext = Pill("Play next", () => { if (Pick is { } a) _host.QueueTracks(a.TrackIds, playNext: true); });
        _addToQueue = Pill("Add to queue", () => { if (Pick is { } a) _host.QueueTracks(a.TrackIds, playNext: false); });
        _open = Pill("Open album", () => { if (Pick is { } a) _host.ShowAlbum(a.AlbumId); });
        // Wraps in a narrow window (a song has four pills).
        var pillRow = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var pill in new[] { _play, _shuffle, _playNext, _addToQueue, _open })
        {
            pill.Margin = new Thickness(4, 3);
            pillRow.Children.Add(pill);
        }
        UpdateModeControls();
        SetPicked(null);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"),
            Margin = new Thickness(16, 14, 16, 18),
        };
        var top = new StackPanel { Children = { modePanel, chipPanel, _count } };
        Grid.SetRow(top, 0);
        Grid.SetRow(stage, 1);
        Grid.SetRow(_details, 2);
        Grid.SetRow(_spin, 3);
        Grid.SetRow(pillRow, 4);
        stage.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(top);
        root.Children.Add(stage);
        root.Children.Add(_details);
        root.Children.Add(_spin);
        root.Children.Add(pillRow);
        Content = root;

        _covers.CoverReady += (_, path) => RefreshSlots(path);
        host.SettingsChanged += OnSettingsChanged;
        ApplyTheme(RouletteTheme.Dark);
    }

    // ── For the plugin, tests and the preview ──

    public ReelCanvas Reel => _reel;
    public CoverCache Covers => _covers;
    public RouletteFilter Filter => _filter;
    public IReadOnlyList<RouletteAlbum> Matches => _matches;
    public RouletteMode Mode => _mode;
    public RouletteAlbum? Pick { get; private set; }
    public bool IsEmptyStateShown => _empty.IsVisible;
    public bool HasLibrary => _loaded;
    public string SpinText => _spinLabel.Text ?? "";
    public string CountText => _count.Text ?? "";
    public string TitleText => _title.Text ?? "";
    public string CaptionText => _caption.IsVisible ? _caption.Text ?? "" : "";
    public bool PillsEnabled => _play.IsEnabled && _shuffle.IsEnabled;
    public bool OpenAlbumEnabled => _open.IsEnabled;
    public string SubtitleText => _year.Text ?? "";
    public string EmptyText => _emptyText.Text ?? "";
    public string HintText => _hint.Text ?? "";
    public bool SpinEnabled => _spin.IsEnabled;

    /// <summary>Raised when the view is gone (window closed).</summary>
    public event EventHandler? Closed;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyTheme(RouletteTheme.From(this));
        ActualThemeVariantChanged += OnThemeChanged;
        if (!_loadStarted) _ = LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Releases the covers and the settings subscription (the window closed).</summary>
    public void Release()
    {
        _host.SettingsChanged -= OnSettingsChanged;
        _covers.Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (_covers.EnsurePixelSize((int)Math.Ceiling(_reel.CoverSize * scaling))) Warm();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme(RouletteTheme.From(this));

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_filter.SkipCompilations == _host.SkipCompilations) return;
        _filter.SkipCompilations = _host.SkipCompilations;
        ApplyFilters();
    }

    /// <summary>Reads the library off the UI thread, then fills the chips and the stage.</summary>
    public async Task LoadAsync()
    {
        _loadStarted = true;
        var version = _albumsVersion;
        var now = _clock();
        IReadOnlyList<RouletteAlbum> albums, songs;
        try
        {
            (albums, songs) = await Task.Run(() =>
            {
                var tracks = _host.LoadTracks();
                return (AlbumCatalog.Group(tracks), SongCatalog.Build(tracks, now));
            });
        }
        catch (Exception ex)
        {
            _host.Log("could not read the library: " + ex.Message);
            albums = songs = Array.Empty<RouletteAlbum>();
        }
        // Albums set meanwhile (tests, the preview) win over this read.
        if (version == _albumsVersion) SetLibrary(albums, songs);
    }

    /// <summary>Puts <paramref name="albums"/> on the reel (tests and the preview call this directly).</summary>
    public void SetAlbums(IReadOnlyList<RouletteAlbum> albums) => SetLibrary(albums, _songList);

    /// <summary>The albums and the songs (Song Roulette) of the library; the mode picks which spin.</summary>
    public void SetLibrary(IReadOnlyList<RouletteAlbum> albums, IReadOnlyList<RouletteAlbum> songs)
    {
        _albumsVersion++;
        _albumList = albums;
        _songList = songs;
        _albums = _mode == RouletteMode.Songs ? songs : albums;
        _loadStarted = true;
        _loaded = true;
        ApplyFilters();
        ShowAlbumOfTheDay();
        if (_spinWhenLoaded)
        {
            _spinWhenLoaded = false;
            Spin();
        }
    }

    /// <summary>"Spin from this artist": only that artist's albums (or songs, in Song Roulette), then a spin.</summary>
    public void SpinFromArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return;
        SpinFor(null, artist);
    }

    /// <summary>Clears the chips, scopes the reel to <paramref name="artist"/> (null: the whole
    /// library), switches to <paramref name="mode"/> (null: keeps it) and spins, once loaded.</summary>
    public void SpinFor(RouletteMode? mode, string? artist)
    {
        _filter.Clear();
        _filter.Artist = string.IsNullOrWhiteSpace(artist) ? null : artist.Trim();
        if (mode is { } m && m != _mode)
        {
            _mode = m;
            _albums = m == RouletteMode.Songs ? _songList : _albumList;
            UpdateModeControls();
        }
        if (!_loaded)
        {
            _spinWhenLoaded = true;
            UpdateChips();
            return;
        }
        ApplyFilters();
        Spin();
    }

    /// <summary>Album Roulette or Song Roulette (the switch on top calls it). The chips stay;
    /// the stage clears, since an album is not a song.</summary>
    public void SetMode(RouletteMode mode)
    {
        if (mode == _mode) return;
        _mode = mode;
        _albums = mode == RouletteMode.Songs ? _songList : _albumList;
        UpdateModeControls();
        Pick = null;
        ApplyFilters();
        if (_loaded) ShowAlbumOfTheDay();
    }

    private string Noun => _mode == RouletteMode.Songs ? "song" : "album";

    /// <summary>Which pills and chips the mode shows (a song has no Shuffle, an album no queue pills;
    /// Albums / EPs / Singles only sort albums).</summary>
    private void UpdateModeControls()
    {
        var songs = _mode == RouletteMode.Songs;
        _shuffle.IsVisible = !songs;
        _playNext.IsVisible = songs;
        _addToQueue.IsVisible = songs;
        foreach (var c in _kindChips) c.IsVisible = !songs;
        UpdateChips();
    }

    /// <summary>Spins (or, while spinning, lands right away).</summary>
    public void Spin()
    {
        if (_matches.Count == 0) return;
        var motion = _reel.Motion;
        if (motion.IsSpinning)
        {
            motion.Finish();
            OnLanded();
            return;
        }
        var target = _matches[_random.Next(_matches.Count)];
        if (_matches.Count > 1 && Pick is { } previous && target.Key == previous.Key)
            target = _matches[(_matches.IndexOf(target) + 1 + _random.Next(_matches.Count - 1)) % _matches.Count];

        var showing = Pick is { } p ? _pool.FindIndex(a => a.Key == p.Key) : -1;
        var targetIndex = _pool.FindIndex(a => a.Key == target.Key);
        if (targetIndex < 0)
        {
            if (_pool.Count >= PoolSize)
            {
                var replace = _random.Next(_pool.Count);
                if (replace == showing) replace = (replace + 1) % _pool.Count;
                _pool[replace] = target;
                targetIndex = replace;
            }
            else
            {
                _pool.Add(target);
                targetIndex = _pool.Count - 1;
            }
        }
        var reduced = _host.ReducedMotion;
        motion.Start(_pool.Count, targetIndex,
            reduced ? ReelMotion.ReducedSlots : ReelMotion.FullSlots,
            reduced ? ReelMotion.ReducedSeconds : ReelMotion.FullSeconds,
            _random, showing >= 0 ? showing : null);
        for (var i = 0; i < motion.Length; i++) FillSlot(i);
        SetPicked(null, spinning: true);
        _reel.Run();
    }

    private void OnLanded()
    {
        var index = _reel.Motion.Current;
        SetPicked(index >= 0 && index < _pool.Count ? _pool[index] : null);
    }

    /// <summary>The filters changed (the chips call it, so can tests): recount, pick a new warm-up pool, show the empty state if nothing matches.</summary>
    public void ApplyFilters()
    {
        _reel.Motion.Finish();
        _matches = _filter.Apply(_albums, _clock());
        _pool.Clear();
        foreach (var i in SampleIndices(_matches.Count, PoolSize)) _pool.Add(_matches[i]);
        Warm();
        UpdateChips();
        var none = _loaded && _matches.Count == 0;
        _empty.IsVisible = none;
        _emptyText.Text = _filter.IsActive || _albums.Count > 0 ? "No " + Noun + "s match these filters" : "Your library has no " + Noun + "s yet";
        _clearFilters.IsVisible = _filter.IsActive;
        _hint.Text = !_loaded ? "Loading your " + Noun + "s..."
            : _mode == RouletteMode.Songs ? "Press Spin to let the reel pick a song" : "Press Spin to let the reel pick an album";
        _spin.IsEnabled = _loaded && _matches.Count > 0;
        _count.Text = !_loaded ? "" : _matches.Count == 1 ? "1 " + Noun : _matches.Count.ToString("N0", CultureInfo.CurrentCulture) + " " + Noun + "s";
        // The album on stage stays if it still matches (the pool changed under it, so it goes
        // back on as the only slot); otherwise the stage clears.
        var caption = _caption.IsVisible ? _caption.Text : null;
        if (Pick is { } pick && _matches.Any(a => a.Key == pick.Key))
        {
            ShowOnStage(pick);
            if (caption is not null) { _caption.Text = caption; _caption.IsVisible = true; }
        }
        else
        {
            _reel.Motion.Reset();
            _reel.InvalidateVisual();
            SetPicked(null);
        }
        _hint.IsVisible = !none && (!_loaded || Pick is null);
    }

    public void ClearFilters()
    {
        _filter.Clear();
        ApplyFilters();
    }

    /// <summary>Album of the Day on the stage when today's pick is in the library.</summary>
    private void ShowAlbumOfTheDay()
    {
        if (_mode != RouletteMode.Albums || !_host.AlbumOfTheDayEnabled || Pick is not null || _reel.Motion.IsSpinning) return;
        var today = _host.LoadDaily();
        if (today is null || today.Day != AlbumOfTheDay.DayKey(_clock())) return;
        var album = _albums.FirstOrDefault(a => a.Key == today.AlbumKey);
        if (album is null) return;
        ShowOnStage(album);
        _caption.Text = "Album of the Day";
        _caption.IsVisible = true;
    }

    /// <summary>Puts <paramref name="album"/> on the stage without a spin.</summary>
    public void ShowOnStage(RouletteAlbum album)
    {
        var index = _pool.FindIndex(a => a.Key == album.Key);
        if (index < 0)
        {
            _pool.Add(album);
            index = _pool.Count - 1;
        }
        _reel.Motion.Show(index);
        FillSlot(0);
        _reel.InvalidateVisual();
        SetPicked(album);
    }

    private void SetPicked(RouletteAlbum? album, bool spinning = false)
    {
        Pick = album;
        _caption.IsVisible = false;
        _title.Text = album?.Title ?? "";
        _artist.Text = album?.Artist ?? "";
        var year = album is { Year: > 0 } ? album.Year.ToString(CultureInfo.InvariantCulture) : "";
        // A song: "Album · 2006" under its artist.
        _year.Text = album is { IsSong: true, AlbumTitle.Length: > 0 } ? (year.Length > 0 ? album.AlbumTitle + " · " + year : album.AlbumTitle) : year;
        _details.Opacity = spinning ? 0 : 1;
        foreach (var b in new[] { _play, _shuffle, _playNext, _addToQueue }) b.IsEnabled = album is not null;
        _open.IsEnabled = album is { AlbumId.Length: > 0 };
        // The pills keep their own background, so a disabled one says so by fading.
        foreach (var b in new[] { _play, _shuffle, _playNext, _addToQueue, _open }) b.Opacity = b.IsEnabled ? 1 : 0.4;
        _spinLabel.Text = album is null ? "Spin" : "Spin again";
        _spinLabel.FontSize = album is null ? 17 : 13.5;
        AutomationName(_spin, _spinLabel.Text);
        _hint.IsVisible = album is null && !spinning && _loaded && _matches.Count > 0;
    }

    private void FillSlot(int slot)
    {
        var index = _reel.Motion.SlotAt(slot);
        if (index < 0 || index >= _pool.Count) return;
        var album = _pool[index];
        _reel.SetSlot(slot, _covers.Get(album.ArtworkPath), _theme.PlaceholderFor(album.Key));
    }

    private void RefreshSlots(string path)
    {
        var motion = _reel.Motion;
        for (var i = 0; i < motion.Length; i++)
        {
            var index = motion.SlotAt(i);
            if (index < _pool.Count && string.Equals(_pool[index].ArtworkPath, path, StringComparison.OrdinalIgnoreCase)) FillSlot(i);
        }
        _reel.InvalidateVisual();
    }

    /// <summary>Starts decoding the pool's covers so a spin finds them ready.</summary>
    private void Warm()
    {
        foreach (var a in _pool) _covers.Get(a.ArtworkPath);
        for (var i = 0; i < _reel.Motion.Length; i++) FillSlot(i);
    }

    private IEnumerable<int> SampleIndices(int count, int take)
    {
        if (count <= take)
        {
            for (var i = 0; i < count; i++) yield return i;
            yield break;
        }
        var chosen = new HashSet<int>();
        while (chosen.Count < take) chosen.Add(_random.Next(count));
        foreach (var i in chosen) yield return i;
    }

    private string[] Shuffled(IReadOnlyList<string> ids)
    {
        var copy = ids.ToArray();
        for (var i = copy.Length - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    // ── Chips ──

    private void BuildGenreMenu(MenuFlyout menu)
    {
        menu.Items.Add(MenuEntry("Any genre", _filter.Genre is null, () => { _filter.Genre = null; ApplyFilters(); }));
        foreach (var g in RouletteFilter.Genres(_albums))
        {
            var genre = g;
            menu.Items.Add(MenuEntry(genre, string.Equals(_filter.Genre, genre, StringComparison.OrdinalIgnoreCase),
                () => { _filter.Genre = genre; ApplyFilters(); }));
        }
    }

    private void BuildDecadeMenu(MenuFlyout menu)
    {
        menu.Items.Add(MenuEntry("Any decade", _filter.Decade is null, () => { _filter.Decade = null; ApplyFilters(); }));
        foreach (var d in RouletteFilter.Decades(_albums))
        {
            var decade = d;
            menu.Items.Add(MenuEntry(RouletteFilter.DecadeName(decade), _filter.Decade == decade, () => { _filter.Decade = decade; ApplyFilters(); }));
        }
    }

    private static MenuItem MenuEntry(string text, bool on, Action click)
    {
        var item = new MenuItem { Header = text, ToggleType = MenuItemToggleType.Radio, IsChecked = on };
        item.Click += (_, _) => click();
        return item;
    }

    private void UpdateChips()
    {
        _genreLabel.Text = _filter.Genre ?? "Genre";
        _decadeLabel.Text = _filter.Decade is { } d ? RouletteFilter.DecadeName(d) : "Decade";
        _artistChip.IsVisible = _filter.Artist is not null;
        _artistLabel.Text = _filter.Artist is { } a ? a + "  ×" : "";
        foreach (var (button, label, on) in _chips)
        {
            var isOn = on();
            button.Background = isOn ? _theme.ChipOn : _theme.Chip;
            label.Foreground = isOn ? _theme.ChipOnText : _theme.Text;
        }
    }

    private (Button, TextBlock) Chip(string text, Action click)
    {
        var label = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold };
        var b = new Button
        {
            Content = label,
            Padding = new Thickness(12, 5),
            CornerRadius = new CornerRadius(999),
            BorderThickness = new Thickness(0),
            MinHeight = 0,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        AutomationName(b, text);
        b.Click += (_, _) => click();
        return (b, label);
    }

    private Button ModeChip(string text, RouletteMode mode)
    {
        var (b, label) = Chip(text, () => SetMode(mode));
        _chips.Add((b, label, () => _mode == mode));
        return b;
    }

    private Button ToggleChip(string text, Func<bool> on, Action toggle)
    {
        var (b, label) = Chip(text, () => { toggle(); ApplyFilters(); });
        _chips.Add((b, label, on));
        return b;
    }

    private (Button, TextBlock) MenuChip(string text, string name, Action<MenuFlyout> build)
    {
        var (b, label) = Chip(text, () => { });
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            build(menu);
        };
        b.Flyout = menu;
        AutomationName(b, name);
        _chips.Add((b, label, () => name == "Genre" ? _filter.Genre is not null : _filter.Decade is not null));
        return (b, label);
    }

    private Button Pill(string text, Action click)
    {
        var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold };
        var b = new Button
        {
            Content = label,
            Padding = new Thickness(16, 7),
            CornerRadius = new CornerRadius(999),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        AutomationName(b, text);
        b.Click += (_, _) => click();
        _pills.Add((b, label));
        return b;
    }

    public void ApplyTheme(RouletteTheme theme)
    {
        _theme = theme;
        _reel.Palette = theme;
        Background = theme.Page;
        Foreground = theme.Text;
        _count.Foreground = theme.Faint;
        _caption.Foreground = theme.IsDark || RouletteTheme.Luminance(theme.Accent) < 0.6 ? theme.AccentBrush : theme.Muted;
        _title.Foreground = theme.Text;
        _artist.Foreground = theme.Muted;
        _year.Foreground = theme.Faint;
        _hint.Foreground = theme.Muted;
        _emptyText.Foreground = theme.Text;
        _spin.Background = theme.AccentBrush;
        _spinLabel.Foreground = theme.OnAccent;
        foreach (var (button, label) in _pills)
        {
            button.Background = theme.Chip;
            label.Foreground = theme.Text;
        }
        UpdateChips();
        for (var i = 0; i < _reel.Motion.Length; i++) FillSlot(i);
    }

    private static void AutomationName(Control c, string name)
        => Avalonia.Automation.AutomationProperties.SetName(c, name);
}
