using System;
using System.ComponentModel;
using Noctis.Localization;
using Noctis.ViewModels;

namespace Noctis.Helpers;

/// <summary>
/// The columns of the Songs list and the Artists list view (GitHub #151) and how their
/// <see cref="ColumnLayout"/> is kept in step with Settings.
/// </summary>
public static class LibraryColumns
{
    /// <summary>The rows' 3-dots button column, pinned to the right end of every list.</summary>
    public const string More = "More";

    /// <summary>
    /// Songs list columns, in the default order. Keys are the cells' ColumnsPanel.Column
    /// values; sort keys are LibrarySongsViewModel.BuildFilteredAndSortedTracks keys. Widths
    /// and the Title/Artist/Album stars are the header grid's from before #151.
    /// </summary>
    public static ColumnLayout CreateSongs() => new(FitLabels(new[]
    {
        new ColumnSpec { Key = "Title", Label = Loc.T("LibrarySongs.Title"), Star = 1, MinWidth = 140, CanHide = false, SortKey = "Title" },
        new ColumnSpec { Key = "Time", Label = Loc.T("LibrarySongs.Time"), Width = 80, MinWidth = 48, SortKey = "Time" },
        new ColumnSpec { Key = "Artist", Label = Loc.T("LibrarySongs.Artist"), Star = 1, MinWidth = 80, SortKey = "Artist" },
        new ColumnSpec { Key = "Album", Label = Loc.T("LibrarySongs.Album"), Star = 1, MinWidth = 80, SortKey = "Album" },
        new ColumnSpec { Key = "Genre", Label = Loc.T("LibrarySongs.Genre"), Width = 110, MinWidth = 60, SortKey = "Genre" },
        new ColumnSpec { Key = "Favorite", Label = Loc.T("LibrarySongs.Favorites"), Width = 80, MinWidth = 40, SortKey = "IsFavorite" },
        new ColumnSpec { Key = "Rating", Label = Loc.T("LibrarySongs.Rating"), Width = 84, MinWidth = 72, SortKey = "Rating" },
        new ColumnSpec { Key = "Plays", Label = Loc.T("LibrarySongs.Plays"), Width = 55, MinWidth = 40, SortKey = "Plays" },
        new ColumnSpec { Key = "Bpm", Label = Loc.T("LibrarySongs.BPM"), Width = 48, MinWidth = 40, DefaultVisible = false, SortKey = "Bpm" },
        new ColumnSpec { Key = "Bitrate", Label = Loc.T("LibrarySongs.Bitrate"), Width = 72, MinWidth = 50, DefaultVisible = false, SortKey = "Bitrate" },
        new ColumnSpec { Key = "SampleRate", Label = Loc.T("LibrarySongs.Sample"), Width = 68, MinWidth = 50, DefaultVisible = false, SortKey = "SampleRate" },
        new ColumnSpec { Key = "Year", Label = Loc.T("Main.Year"), Width = 56, MinWidth = 44, DefaultVisible = false, SortKey = "Year" },
        new ColumnSpec { Key = "AlbumArtist", Label = Loc.T("Main.AlbumArtist"), Width = 150, MinWidth = 80, DefaultVisible = false, SortKey = "Album Artist" },
        new ColumnSpec { Key = "Composer", Label = Loc.T("LibrarySongs.Composer"), Width = 140, MinWidth = 80, DefaultVisible = false },
        new ColumnSpec { Key = "Format", Label = Loc.T("LibrarySongs.Format"), Width = 64, MinWidth = 44, DefaultVisible = false },
        new ColumnSpec { Key = "ReplayGain", Label = Loc.T("LibrarySongs.ReplayGain"), Width = 84, MinWidth = 56, DefaultVisible = false, SortKey = "ReplayGain" },
        new ColumnSpec { Key = "DateAdded", Label = Loc.T("Main.DateAdded"), Width = 96, MinWidth = 70, DefaultVisible = false, SortKey = "Date Added" },
        new ColumnSpec { Key = More, Width = 42, Pinned = true },
    }));

    /// <summary>Artists list view columns, in the default order (sort keys: LibraryArtistsViewModel.SortMode).</summary>
    public static ColumnLayout CreateArtists() => new(FitLabels(new[]
    {
        new ColumnSpec { Key = "Name", Label = Loc.T("LibraryArtists.ColumnArtist"), Star = 1, MinWidth = 140, CanHide = false, SortKey = "name" },
        new ColumnSpec { Key = "Albums", Label = Loc.T("LibraryArtists.ColumnAlbums"), Width = 80, MinWidth = 50, SortKey = "albums" },
        new ColumnSpec { Key = "Songs", Label = Loc.T("LibraryArtists.ColumnSongs"), Width = 80, MinWidth = 50, SortKey = "songs" },
        new ColumnSpec { Key = "Time", Label = Loc.T("LibraryArtists.ColumnTime"), Width = 90, MinWidth = 56, SortKey = "time" },
        new ColumnSpec { Key = "Genre", Label = Loc.T("LibraryArtists.ColumnGenre"), Width = 170, MinWidth = 70, SortKey = "genre" },
        new ColumnSpec { Key = More, Width = 42, Pinned = true },
    }));

    /// <summary>
    /// A fixed-width column at least as wide as its header label, so a longer translation
    /// isn't cut off (German "Wiedergaben" showed as "Wieder…" in the 55 px Plays column,
    /// 11-04). Estimate: ~6.2 px per character of the 12 px header plus sort arrow and
    /// padding; the English labels already fit, so their widths don't change.
    /// </summary>
    internal static ColumnSpec[] FitLabels(ColumnSpec[] specs)
    {
        for (var i = 0; i < specs.Length; i++)
        {
            var s = specs[i];
            if (s.Star > 0 || string.IsNullOrEmpty(s.Label)) continue;
            var need = Math.Ceiling(s.Label.Length * 6.2 + 22);
            if (need > s.Width)
                specs[i] = new ColumnSpec
                {
                    Key = s.Key, Label = s.Label, Width = need, MinWidth = s.MinWidth, Star = s.Star,
                    DefaultVisible = s.DefaultVisible, CanHide = s.CanHide, Pinned = s.Pinned, SortKey = s.SortKey,
                };
        }
        return specs;
    }

    /// <summary>
    /// The Songs column's Settings flag (shared with View Options), or null for a column that
    /// is always shown. Getter and setter in one place so the two directions can't drift.
    /// </summary>
    private static bool? SongsFlag(SettingsViewModel s, string key, bool? set = null)
    {
        switch (key)
        {
            case "Time": if (set is { } a) s.ShowTimeColumn = a; return s.ShowTimeColumn;
            case "Artist": if (set is { } b) s.ShowArtistColumn = b; return s.ShowArtistColumn;
            case "Album": if (set is { } c) s.ShowAlbumColumn = c; return s.ShowAlbumColumn;
            case "Genre": if (set is { } d) s.ShowGenreColumn = d; return s.ShowGenreColumn;
            case "Favorite": if (set is { } e) s.ShowFavoritesColumn = e; return s.ShowFavoritesColumn;
            case "Rating": if (set is { } f) s.ShowRatingColumn = f; return s.ShowRatingColumn;
            case "Plays": if (set is { } g) s.ShowPlaysColumn = g; return s.ShowPlaysColumn;
            case "Bpm": if (set is { } h) s.ShowBpmColumn = h; return s.ShowBpmColumn;
            case "Bitrate": if (set is { } i) s.ShowBitrateColumn = i; return s.ShowBitrateColumn;
            case "SampleRate": if (set is { } j) s.ShowSampleRateColumn = j; return s.ShowSampleRateColumn;
            case "Year": if (set is { } k) s.ShowYearColumn = k; return s.ShowYearColumn;
            case "AlbumArtist": if (set is { } l) s.ShowAlbumArtistColumn = l; return s.ShowAlbumArtistColumn;
            case "Composer": if (set is { } m) s.ShowComposerColumn = m; return s.ShowComposerColumn;
            case "Format": if (set is { } n) s.ShowFormatColumn = n; return s.ShowFormatColumn;
            case "DateAdded": if (set is { } o) s.ShowDateAddedColumn = o; return s.ShowDateAddedColumn;
            case "ReplayGain": if (set is { } p) s.ShowReplayGainColumn = p; return s.ShowReplayGainColumn;
            default: return null;
        }
    }

    /// <summary>Settings property name → Songs column key, for PropertyChanged.</summary>
    private static string? SongsKeyFor(string? property) => property switch
    {
        nameof(SettingsViewModel.ShowTimeColumn) => "Time",
        nameof(SettingsViewModel.ShowArtistColumn) => "Artist",
        nameof(SettingsViewModel.ShowAlbumColumn) => "Album",
        nameof(SettingsViewModel.ShowGenreColumn) => "Genre",
        nameof(SettingsViewModel.ShowFavoritesColumn) => "Favorite",
        nameof(SettingsViewModel.ShowRatingColumn) => "Rating",
        nameof(SettingsViewModel.ShowPlaysColumn) => "Plays",
        nameof(SettingsViewModel.ShowBpmColumn) => "Bpm",
        nameof(SettingsViewModel.ShowBitrateColumn) => "Bitrate",
        nameof(SettingsViewModel.ShowSampleRateColumn) => "SampleRate",
        nameof(SettingsViewModel.ShowYearColumn) => "Year",
        nameof(SettingsViewModel.ShowAlbumArtistColumn) => "AlbumArtist",
        nameof(SettingsViewModel.ShowComposerColumn) => "Composer",
        nameof(SettingsViewModel.ShowFormatColumn) => "Format",
        nameof(SettingsViewModel.ShowDateAddedColumn) => "DateAdded",
        nameof(SettingsViewModel.ShowReplayGainColumn) => "ReplayGain",
        _ => null,
    };

    /// <summary>
    /// Keeps the Songs layout and Settings in step: visibility is the Show…Column flags (View
    /// Options edits the same ones), order and widths are saved when a drag or menu pick ends.
    /// Dispose to detach (the view does on leaving the window).
    /// </summary>
    public static IDisposable BindSongs(ColumnLayout layout, SettingsViewModel settings)
    {
        layout.Load(settings.SongsColumnOrder, settings.SongsColumnWidths);
        foreach (var spec in layout.Specs)
            if (SongsFlag(settings, spec.Key) is { } shown) layout.SetVisible(spec.Key, shown);

        // Writing order and widths raises PropertyChanged for each in turn; only an outside
        // change (Settings reset) may reload the layout, never our own half-written pair.
        var committing = false;
        PropertyChangedEventHandler onSettings = (_, e) =>
        {
            if (SongsKeyFor(e.PropertyName) is { } key && SongsFlag(settings, key) is { } shown)
                layout.SetVisible(key, shown);
            else if (!committing && e.PropertyName is nameof(SettingsViewModel.SongsColumnOrder) or nameof(SettingsViewModel.SongsColumnWidths)
                     && (settings.SongsColumnOrder != layout.SerializeOrder() || settings.SongsColumnWidths != layout.SerializeWidths()))
                layout.Load(settings.SongsColumnOrder, settings.SongsColumnWidths); // Settings reset
        };
        Action<string, bool> onRequest = (key, shown) =>
        {
            if (SongsFlag(settings, key, shown) == null) layout.SetVisible(key, shown);
        };
        EventHandler onCommit = (_, _) =>
        {
            committing = true;
            try
            {
                settings.SongsColumnOrder = layout.SerializeOrder();
                settings.SongsColumnWidths = layout.SerializeWidths();
            }
            finally { committing = false; }
        };
        settings.PropertyChanged += onSettings;
        layout.VisibilityRequested += onRequest;
        layout.Committed += onCommit;
        return new Unbind(() =>
        {
            settings.PropertyChanged -= onSettings;
            layout.VisibilityRequested -= onRequest;
            layout.Committed -= onCommit;
        });
    }

    /// <summary>The Artists list layout's Settings round trip (it owns its visibility).</summary>
    public static IDisposable BindArtists(ColumnLayout layout, SettingsViewModel settings)
    {
        layout.Load(settings.ArtistListColumnOrder, settings.ArtistListColumnWidths, settings.ArtistListHiddenColumns);
        var committing = false;
        PropertyChangedEventHandler onSettings = (_, e) =>
        {
            if (!committing && e.PropertyName is nameof(SettingsViewModel.ArtistListColumnOrder) or nameof(SettingsViewModel.ArtistListColumnWidths)
                    or nameof(SettingsViewModel.ArtistListHiddenColumns)
                && (settings.ArtistListColumnOrder != layout.SerializeOrder()
                    || settings.ArtistListColumnWidths != layout.SerializeWidths()
                    || settings.ArtistListHiddenColumns != layout.SerializeHidden()))
                layout.Load(settings.ArtistListColumnOrder, settings.ArtistListColumnWidths, settings.ArtistListHiddenColumns);
        };
        EventHandler onCommit = (_, _) =>
        {
            committing = true;
            try
            {
                settings.ArtistListColumnOrder = layout.SerializeOrder();
                settings.ArtistListColumnWidths = layout.SerializeWidths();
                settings.ArtistListHiddenColumns = layout.SerializeHidden();
            }
            finally { committing = false; }
        };
        settings.PropertyChanged += onSettings;
        layout.Committed += onCommit;
        return new Unbind(() =>
        {
            settings.PropertyChanged -= onSettings;
            layout.Committed -= onCommit;
        });
    }

    private sealed class Unbind(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}
