using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Plugins.AlbumTown.Town;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>
/// The Apartments tab (Jafezy's design): the building grid, the cover shelf under it, each
/// room's menu and the "Choose songs" picker for playlist rooms. The building is saved in the
/// plugin's data folder (apartments.json) a moment after every change and when the window closes.
/// </summary>
public sealed partial class AlbumTownView
{
    /// <summary>Song picker rows shown at once (search narrows a long playlist).</summary>
    public const int PickerRows = 300;

    private CoverCache _softCovers = null!;
    private ApartmentGrid _grid = new();
    private ApartmentLife _life = null!;
    private ApartmentsCanvas _apartments = null!;
    private ShelfStrip _shelf = null!;
    private ScrollViewer _shelfScroll = null!;
    private TextBox _shelfSearch = null!;
    private TextBlock _shelfHint = null!;
    private Border _shelfBar = null!;
    private DockPanel _apartmentsPage = null!;
    private DispatcherTimer _saveTimer = null!;
    private bool _layoutReady;
    private IReadOnlyList<ShelfItem> _albumItems = Array.Empty<ShelfItem>();
    private IReadOnlyList<ShelfItem> _playlistItems = Array.Empty<ShelfItem>();
    private IReadOnlyList<PlaylistInfo> _playlists = Array.Empty<PlaylistInfo>();
    private Dictionary<string, PlaylistInfo> _playlistById = new(StringComparer.Ordinal);
    private Dictionary<string, TownAlbum> _townByKey = new(StringComparer.Ordinal);
    private Dictionary<string, TrackInfo> _tracksById = new(StringComparer.Ordinal);
    private Dictionary<string, string> _trackByFile = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _residentByTrack = new Dictionary<string, string>();
    private readonly Dictionary<string, TownAlbum> _songs = new(StringComparer.Ordinal);

    // The song picker.
    private Border _picker = null!;
    private TextBlock _pickerTitle = null!;
    private TextBlock _pickerCount = null!;
    private TextBox _pickerSearch = null!;
    private StackPanel _pickerList = null!;
    private Room? _pickerRoom;
    private readonly List<string> _pickerPicks = new();

    public ApartmentGrid Building => _grid;
    public ApartmentLife Life => _life;
    public ShelfStrip Shelf => _shelf;
    /// <summary>Soft backdrop covers of the rooms.</summary>
    public CoverCache SoftCovers => _softCovers;
    public bool IsPickerOpen => _picker.IsVisible;
    public Room? PickerRoom => _pickerRoom;
    public IReadOnlyList<PlaylistInfo> Playlists => _playlists;

    private Control BuildApartments()
    {
        _softCovers = new CoverCache(capacity: 200, thumbSize: 48);
        _life = new ApartmentLife(new Random(_random.Next())) { Chatty = _host.Chatty };
        _apartments = new ApartmentsCanvas(_residentCovers, _softCovers, _grid, _life)
        {
            Beat = () => _host.TryReadBeat(out var p) ? p : null,
            ShowNames = _host.ShowNames,
            Describe = Describe,
            Motion = Noctis.Plugins.AlbumTown.Town.Motion.Parse(_host.Motion),
        };
        _apartments.PetClicked += (_, pet) => ShowCard(pet.Album, null, PetNote(pet));
        _apartments.RoomMenuRequested += (_, room) => OpenRoomMenu(room);
        _grid.Changed += OnGridChanged;

        _shelf = new ShelfStrip(_residentCovers) { IsPlaced = i => _grid.FindSource(i.Kind, i.Source) is not null };
        _shelfScroll = new ScrollViewer
        {
            Content = _shelf,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _shelf.Viewport = () => new Rect(_shelfScroll.Offset.X, 0, _shelfScroll.Viewport.Width, ShelfStrip.StripHeight);
        _shelfScroll.ScrollChanged += (_, _) => _shelf.InvalidateVisual();
        // The wheel scrolls the shelf sideways.
        _shelfScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            _shelfScroll.Offset = new Vector(Math.Clamp(_shelfScroll.Offset.X - delta * 90, 0, Math.Max(0, _shelfScroll.Extent.Width - _shelfScroll.Viewport.Width)), 0);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _shelf.DragStarted += (_, d) =>
        {
            _apartments.BeginDrop(d.Item.Kind, d.Item.Source);
            _apartments.UpdateDrop(d.Args.GetPosition(_apartments));
        };
        _shelf.DragMoved += (_, e) => _apartments.UpdateDrop(e.GetPosition(_apartments));
        _shelf.DragEnded += (_, e) =>
        {
            if (e is null) _apartments.CancelDrop();
            else _apartments.EndDrop(e.GetPosition(_apartments));
        };
        _shelf.Activated += (_, item) => PlaceInFirstFree(item);

        _shelfSearch = new TextBox
        {
            PlaceholderText = "Search the shelf",
            Width = 200,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(14, 6),
            MinHeight = 0,
        };
        AutomationName(_shelfSearch, "Search the shelf");
        _shelfSearch.TextChanged += (_, _) => FillShelf();
        _shelfHint = new TextBlock
        {
            Text = "Drag a cover into the building to give it a room. Drag a room to move it, its corner to resize it.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Width = 200,
            Margin = new Thickness(2, 8, 0, 0),
        };
        var shelfSide = new StackPanel { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _shelfSearch, _shelfHint } };
        var shelfDock = new DockPanel();
        DockPanel.SetDock(shelfSide, Dock.Left);
        shelfDock.Children.Add(shelfSide);
        shelfDock.Children.Add(_shelfScroll);
        _shelfBar = new Border { Padding = new Thickness(18, 8, 18, 6), Child = shelfDock };

        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveApartmentsNow());
        BuildPicker();

        _apartmentsPage = new DockPanel { IsVisible = false };
        DockPanel.SetDock(_shelfBar, Dock.Bottom);
        _apartmentsPage.Children.Add(_shelfBar);
        _apartmentsPage.Children.Add(_apartments);
        return _apartmentsPage;
    }

    private void ApplyApartmentsTheme(TownTheme theme)
    {
        _apartments.Palette = theme;
        _shelf.Palette = theme;
        _shelfBar.Background = theme.Chrome;
        _shelfHint.Foreground = theme.Faint;
        _picker.Background = theme.Card;
        _pickerTitle.Foreground = theme.Text;
        _pickerCount.Foreground = theme.Muted;
        foreach (var child in _pickerList.Children)
            if (child is CheckBox { Content: StackPanel { Children: [TextBlock t, TextBlock s] } })
            {
                t.Foreground = theme.Text;
                s.Foreground = theme.Muted;
            }
        _apartments.Restyle();
    }

    /// <summary>
    /// The library, the playlists and the saved building (tests and the preview call it
    /// directly). Rooms whose album or playlist is gone are left out; with an empty library the
    /// saved building is kept untouched (nothing is saved over it).
    /// </summary>
    public void SetApartments(IReadOnlyList<TrackInfo> tracks, IReadOnlyList<PlaylistInfo> playlists, ApartmentLayout? layout,
        IReadOnlyDictionary<string, string>? residentByTrack = null)
    {
        _tracksById = new Dictionary<string, TrackInfo>(StringComparer.Ordinal);
        _trackByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
        {
            _tracksById[t.Id] = t;
            if (!string.IsNullOrEmpty(t.FilePath)) _trackByFile[t.FilePath] = t.Id;
        }
        if (residentByTrack is null) AlbumGrouping.Group(tracks, out _, out residentByTrack);
        _residentByTrack = residentByTrack;
        _songs.Clear();
        _playlists = playlists;
        _playlistById = playlists.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _playlistItems = playlists.Select(p => new ShelfItem(RoomKind.Playlist, p.Id, p.Name, Songs(p.TrackIds.Count), PlaylistCovers(p), ShelfStrip.PlaylistsGroup)).ToList();

        // An empty library may just not be loaded yet: keep what is saved, save nothing over it.
        _layoutReady = _town.Count > 0 || playlists.Count > 0;
        var grid = _layoutReady
            ? ApartmentGrid.FromLayout(layout, (kind, source) => kind == RoomKind.Album ? _townByKey.ContainsKey(source) : _playlistById.ContainsKey(source), out var dropped)
            : new ApartmentGrid();
        _grid.Changed -= OnGridChanged;
        _grid = grid;
        _grid.Changed += OnGridChanged;
        _apartments.SetGrid(_grid);
        RebuildLife();
        FillShelf();
        UpdateStatus();
    }

    /// <summary>The town changed (<see cref="SetTown"/>): album lookups and the shelf.</summary>
    private void OnTownChanged()
    {
        _townByKey = new Dictionary<string, TownAlbum>(StringComparer.Ordinal);
        foreach (var a in _town) _townByKey.TryAdd(a.Key, a);
        _albumItems = _town.Select(a => new ShelfItem(RoomKind.Album, a.Key, Or(a.Title, "Untitled"), Or(a.Artist, "Unknown artist"),
            a.ArtworkPath is { Length: > 0 } art ? new[] { art } : Array.Empty<string>(), a.Genre.Length == 0 ? ShelfStrip.OtherGenre : a.Genre)).ToList();
        if (_apartments is not null) FillShelf();
    }

    private void FillShelf()
    {
        _shelf.SetItems(ShelfStrip.Arrange(_playlistItems, _albumItems, _shelfSearch.Text));
        _shelfScroll.Offset = default;
    }

    private static string Songs(int n) => n == 1 ? "1 song" : n.ToString(CultureInfo.InvariantCulture) + " songs";

    /// <summary>The playlist's own cover, else the covers of its first four songs (a mosaic).</summary>
    private IReadOnlyList<string> PlaylistCovers(PlaylistInfo p)
    {
        if (p.CoverPath is { Length: > 0 } own) return new[] { own };
        var covers = new List<string>(4);
        foreach (var id in p.TrackIds)
        {
            if (SongCover(id) is { Length: > 0 } c && !covers.Contains(c, StringComparer.OrdinalIgnoreCase)) covers.Add(c);
            if (covers.Count == 4) break;
        }
        return covers;
    }

    /// <summary>A song's cover by the plaza's rule: its resident's (the album's, or its own as a single).</summary>
    private string? SongCover(string trackId)
        => _residentByTrack.TryGetValue(trackId, out var key) && _townByKey.TryGetValue(key, out var resident) && resident.ArtworkPath is { Length: > 0 } art
            ? art
            : _tracksById.TryGetValue(trackId, out var t) ? t.ArtworkPath : null;

    private RoomLook Describe(Room room)
    {
        if (room.Kind == RoomKind.Album)
        {
            if (!_townByKey.TryGetValue(room.Source, out var a)) return new RoomLook("Moved out", "", Array.Empty<string>(), null);
            return new RoomLook(Or(a.Title, "Untitled"), Or(a.Artist, "Unknown artist"), a.ArtworkPath is { Length: > 0 } art ? new[] { art } : Array.Empty<string>(), null);
        }
        if (!_playlistById.TryGetValue(room.Source, out var p)) return new RoomLook("Moved out", "", Array.Empty<string>(), null);
        var sub = room.Splits > 1 ? $"Playlist · {room.Splits.ToString(CultureInfo.InvariantCulture)} rooms" : "Playlist · " + Songs(p.TrackIds.Count);
        return new RoomLook(Or(p.Name, "Playlist"), sub, PlaylistCovers(p), CoverColor.Parse(p.Color));
    }

    private int Plays(string trackId) => _tracksById.TryGetValue(trackId, out var t) ? Math.Max(0, t.PlayCount) : -1;

    /// <summary>The songs living in a playlist room right now.</summary>
    public IReadOnlyList<string> PetsOf(Room room)
        => room.Kind == RoomKind.Playlist && _playlistById.TryGetValue(room.Source, out var p)
            ? PlaylistPicks.Resolve(room, p.TrackIds, Plays)
            : Array.Empty<string>();

    private TownAlbum? Song(string trackId)
    {
        if (_songs.TryGetValue(trackId, out var song)) return song;
        if (!_tracksById.TryGetValue(trackId, out var t)) return null;
        _residentByTrack.TryGetValue(trackId, out var key);
        var resident = key is not null && _townByKey.TryGetValue(key, out var r) ? r : null;
        song = new TownAlbum("song:" + t.Id, t.Title.Trim(), (string.IsNullOrWhiteSpace(t.Artist) ? t.AlbumArtist : t.Artist).Trim(), t.Year, t.Genre.Trim(),
            new[] { t.Id }, t.AlbumId, SongCover(trackId), Math.Max(0, t.PlayCount), t.LastPlayed, t.DateAdded, resident?.IsSingle ?? false, t.IsFavorite);
        _songs[trackId] = song;
        return song;
    }

    private void RebuildLife()
    {
        var plan = new List<PetPlan>();
        foreach (var room in _grid.Rooms)
        {
            if (room.Kind == RoomKind.Album)
            {
                if (_townByKey.TryGetValue(room.Source, out var album)) plan.Add(new PetPlan(room, album, false, album.Key, null));
                continue;
            }
            foreach (var id in PetsOf(room))
                if (Song(id) is { } song)
                    plan.Add(new PetPlan(room, song, true, _residentByTrack.TryGetValue(id, out var k) ? k : "", id));
        }
        _life.SetLayout(ApartmentMap.Build(_grid.Rooms), plan);
        ApplyNowPlayingToApartments();
        _apartments.Refresh();
    }

    private void ApplyNowPlayingToApartments()
    {
        var file = _host.NowPlaying?.FilePath;
        var trackId = file is { Length: > 0 } && _trackByFile.TryGetValue(file, out var id) ? id : null;
        _life.SetNowPlaying(NowPlayingKey(), trackId, _host.IsPlaying);
    }

    private void OnGridChanged(object? sender, EventArgs e)
    {
        RebuildLife();
        _shelf.InvalidateVisual();
        if (_pickerRoom is { } room && _grid.Find(room.Id) is null) HidePicker();
        ScheduleSave();
        UpdateStatus();
    }

    private void ScheduleSave()
    {
        if (!_layoutReady) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes the building now (the debounce timer, closing the window, tests).</summary>
    public void SaveApartmentsNow()
    {
        _saveTimer.Stop();
        if (_layoutReady) _host.SaveApartments(_grid.ToLayout());
    }

    private void FlushApartments()
    {
        if (_saveTimer.IsEnabled) SaveApartmentsNow();
    }

    private void PlaceInFirstFree(ShelfItem item)
    {
        if (_grid.FindSource(item.Kind, item.Source) is not null) return;
        if (_grid.FirstFree() is { } at) _grid.TryAdd(item.Kind, item.Source, at.X, at.Y);
    }

    private string? PetNote(Pet pet)
    {
        if (pet.IsSong && _playlistById.TryGetValue(pet.Home.Source, out var p)) return "Lives in the playlist " + Or(p.Name, "Playlist");
        if (pet.Visiting) return "Visiting the neighbours";
        return null;
    }

    private string ApartmentsStatus()
    {
        var rooms = _grid.Rooms.Count;
        var pets = _life.Pets.Count;
        return $"{rooms.ToString(CultureInfo.InvariantCulture)} {(rooms == 1 ? "room" : "rooms")} · {pets.ToString(CultureInfo.InvariantCulture)} {(pets == 1 ? "pet" : "pets")}";
    }

    // ── A room's menu ──

    /// <summary>A room's menu: label, action (null = disabled), checked.</summary>
    public IReadOnlyList<(string Label, Action? Run, bool Checked)> RoomMenu(Room room)
    {
        var items = new List<(string, Action?, bool)>();
        if (room.Kind == RoomKind.Album)
        {
            var album = _townByKey.GetValueOrDefault(room.Source);
            items.Add(("Play album", album is null ? null : () => _host.Play(album), false));
            items.Add(("Go to album", album is { AlbumId.Length: > 0 } ? () => _host.ShowAlbum(album) : null, false));
        }
        else
        {
            var playlist = _playlistById.GetValueOrDefault(room.Source);
            items.Add(("Play playlist", playlist is { TrackIds.Count: > 0 } ? () => _host.PlayTracks(playlist.TrackIds) : null, false));
            items.Add(("Choose songs...", playlist is null ? null : () => ShowPicker(room), false));
            for (var n = 1; n <= RoomShapes.MaxSplits; n++)
            {
                var splits = n;
                items.Add((n == 1 ? "One room" : n == 2 ? "Two rooms" : n == 3 ? "Three rooms" : "Four rooms", () => _grid.TrySplit(room, splits), room.Splits == n));
            }
        }
        items.Add(("Remove room", () => _grid.Remove(room), false));
        return items;
    }

    private void OpenRoomMenu(Room room)
    {
        var menu = new ContextMenu();
        foreach (var (label, run, isChecked) in RoomMenu(room))
        {
            var item = new MenuItem { Header = label, IsEnabled = run is not null };
            if (isChecked)
            {
                item.ToggleType = MenuItemToggleType.Radio;
                item.IsChecked = true;
            }
            if (label == "Remove room") menu.Items.Add(new Separator());
            if (run is not null) item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        menu.Open(_apartments);
    }

    // ── Choose songs ──

    private void BuildPicker()
    {
        _pickerTitle = new TextBlock { FontSize = 15, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        _pickerCount = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 8) };
        _pickerSearch = new TextBox
        {
            PlaceholderText = "Search this playlist",
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(14, 6),
            MinHeight = 0,
        };
        AutomationName(_pickerSearch, "Search this playlist");
        _pickerSearch.TextChanged += (_, _) => FillPicker();
        _pickerList = new StackPanel { Spacing = 2 };
        var scroll = new ScrollViewer { Content = _pickerList, MaxHeight = 380, Margin = new Thickness(0, 8, 0, 0) };
        var most = Pill("Most played", () =>
        {
            if (_pickerRoom is not { } room) return;
            _pickerPicks.Clear();
            _grid.SetPicks(room, _pickerPicks);
            _pickerPicks.AddRange(PetsOf(room));
            FillPicker();
        });
        var done = Pill("Done", HidePicker, accent: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, Children = { most, done } };
        _picker = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16),
            Margin = new Thickness(18),
            Width = 360,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            BoxShadow = BoxShadows.Parse("0 10 30 0 #70000000"),
            Child = new StackPanel { Children = { _pickerTitle, _pickerCount, _pickerSearch, scroll, buttons } },
            IsVisible = false,
        };
    }

    /// <summary>Opens "Choose songs" for a playlist room: tick up to five songs to live there.</summary>
    public void ShowPicker(Room room)
    {
        if (room.Kind != RoomKind.Playlist || !_playlistById.TryGetValue(room.Source, out var p)) return;
        _pickerRoom = room;
        _pickerPicks.Clear();
        _pickerPicks.AddRange(PetsOf(room));
        _pickerTitle.Text = "Choose songs for " + Or(p.Name, "Playlist");
        _pickerSearch.Text = "";
        FillPicker();
        _picker.IsVisible = true;
    }

    public void HidePicker()
    {
        _picker.IsVisible = false;
        _pickerRoom = null;
        _pickerList.Children.Clear();
    }

    /// <summary>Ticks or unticks a song in the open picker (the checkboxes call it). False when five are already chosen.</summary>
    public bool TogglePick(string trackId)
    {
        if (_pickerRoom is not { } room) return false;
        if (!PlaylistPicks.Toggle(_pickerPicks, trackId))
        {
            _pickerCount.Text = $"{PlaylistPicks.Max.ToString(CultureInfo.InvariantCulture)} of {PlaylistPicks.Max.ToString(CultureInfo.InvariantCulture)} chosen. Untick one to swap it.";
            return false;
        }
        _grid.SetPicks(room, _pickerPicks);
        UpdatePickerCount();
        return true;
    }

    private void UpdatePickerCount()
        => _pickerCount.Text = $"{_pickerPicks.Count.ToString(CultureInfo.InvariantCulture)} of {PlaylistPicks.Max.ToString(CultureInfo.InvariantCulture)} chosen"
            + (_pickerRoom is { Picks.Count: 0 } ? " (most played)" : "");

    private void FillPicker()
    {
        _pickerList.Children.Clear();
        if (_pickerRoom is not { } room || !_playlistById.TryGetValue(room.Source, out var p)) return;
        var words = (_pickerSearch.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var shown = 0;
        var matching = 0;
        foreach (var id in p.TrackIds.Distinct(StringComparer.Ordinal))
        {
            if (!_tracksById.TryGetValue(id, out var t)) continue;
            if (words.Length > 0 && !words.All(w => t.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || t.Artist.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            matching++;
            if (shown >= PickerRows) continue;
            shown++;
            var trackId = id;
            var box = new CheckBox
            {
                IsChecked = _pickerPicks.Contains(id),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = Or(t.Title, "Untitled"), FontSize = 13, Foreground = _theme.Text, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = $"{Or(t.Artist, "Unknown artist")} · {(t.PlayCount == 1 ? "1 play" : t.PlayCount.ToString(CultureInfo.InvariantCulture) + " plays")}",
                            FontSize = 11, Foreground = _theme.Muted, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            AutomationName(box, Or(t.Title, "Untitled"));
            box.IsCheckedChanged += (_, _) =>
            {
                var want = box.IsChecked == true;
                if (want == _pickerPicks.Contains(trackId)) return;
                if (!TogglePick(trackId)) box.IsChecked = false;
            };
            _pickerList.Children.Add(box);
        }
        if (matching > shown)
            _pickerList.Children.Add(new TextBlock
            {
                Text = $"Showing {shown.ToString(CultureInfo.InvariantCulture)} of {matching.ToString(CultureInfo.InvariantCulture)} songs. Search to find the rest.",
                FontSize = 11, Foreground = _theme.Faint, Margin = new Thickness(4, 6),
                TextWrapping = TextWrapping.Wrap,
            });
        UpdatePickerCount();
    }
}
