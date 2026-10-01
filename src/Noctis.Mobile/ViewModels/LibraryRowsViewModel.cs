using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

/// <summary>The lists the Library tab's rows open. The names are the persisted keys
/// (AppSettings.PhoneLibraryRows), so they must not be renamed.</summary>
public enum LibraryRowKind { Playlists, Artists, Albums, Songs, Favorites, Downloaded }

/// <summary>One Library row: an accent icon, the list's name and a ›; under Edit a round
/// check (shown or not) and a ≡ handle instead.</summary>
public sealed partial class LibraryRowItem : ObservableObject
{
    public LibraryRowItem(LibraryRowKind kind, bool isShown)
    {
        Kind = kind;
        _isShown = isShown;
    }

    public LibraryRowKind Kind { get; }

    /// <summary>The persisted key, and what the view's icon styles match on.</summary>
    public string Key => Kind.ToString();

    public string Title => Kind switch
    {
        LibraryRowKind.Playlists => Loc.T("Nav.Playlists"),
        LibraryRowKind.Artists => Loc.T("Nav.Artists"),
        LibraryRowKind.Albums => Loc.T("Nav.Albums"),
        LibraryRowKind.Songs => Loc.T("Nav.Songs"),
        LibraryRowKind.Favorites => Loc.T("Nav.Favorites"),
        _ => "Downloaded",
    };

    /// <summary>Switched on under Edit: listed on the Library tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListed))]
    private bool _isShown;

    /// <summary>Mirrors <see cref="LibraryRowsViewModel.IsEditing"/> so the row template can
    /// switch its chevron for the check and handle.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListed))]
    private bool _isEditing;

    /// <summary>On screen: a shown row, or any row while editing (to switch it back on).</summary>
    public bool IsListed => IsShown || IsEditing;
}

/// <summary>
/// The Library tab's list rows (Apple Music's Library: Playlists, Artists, Albums, Songs…) and
/// their Edit mode, where each row is switched on or off and dragged into order; Done saves the
/// arrangement to AppSettings.PhoneLibraryRows. Favorites and Downloaded start off. Downloaded
/// (the desktop's songs saved on the phone) exists only with an account service: without one
/// it is kept out of <see cref="Rows"/> but its saved state is carried through each save.
/// </summary>
public sealed partial class LibraryRowsViewModel : ObservableObject
{
    /// <summary>Every row kind in its default place, and whether it starts switched on.</summary>
    internal static readonly IReadOnlyList<(LibraryRowKind Kind, bool Shown)> Defaults = new[]
    {
        (LibraryRowKind.Playlists, true),
        (LibraryRowKind.Artists, true),
        (LibraryRowKind.Albums, true),
        (LibraryRowKind.Songs, true),
        (LibraryRowKind.Favorites, false),
        (LibraryRowKind.Downloaded, false),
    };

    private const char OffMark = '-';

    // Rows this host cannot show (Downloaded without an account), kept so a save does not drop them.
    private readonly List<(LibraryRowKind Kind, bool Shown)> _parked = new();

    public LibraryRowsViewModel(ShellViewModel shell)
    {
        Shell = shell;
        Load(shell.Library.LibraryRowKeys);
        shell.Library.PropertyChanged += OnLibraryChanged;
        shell.PropertyChanged += OnShellChanged;
    }

    public ShellViewModel Shell { get; }

    /// <summary>The rows this host can show, in the user's order, shown or not.</summary>
    public ObservableCollection<LibraryRowItem> Rows { get; } = new();

    [ObservableProperty] private bool _isEditing;

    /// <summary>The save started by the last Done (tests await it).</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The persisted list read back: listed rows in their saved order and state, unknown keys
    /// skipped, a repeated key taken once, then any row kind the list lacks in its default place
    /// and state. Empty (never edited) = <see cref="Defaults"/>.
    /// </summary>
    internal static List<(LibraryRowKind Kind, bool Shown)> Decode(IReadOnlyList<string>? keys)
    {
        var rows = new List<(LibraryRowKind Kind, bool Shown)>();
        foreach (var raw in keys ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var key = raw.Trim();
            var shown = key[0] != OffMark;
            if (!shown) key = key[1..];
            // Names only: Enum.TryParse would also take "3" or "Songs,Albums".
            if (!Enum.GetNames<LibraryRowKind>().Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            var kind = Enum.Parse<LibraryRowKind>(key, ignoreCase: true);
            if (rows.Any(r => r.Kind == kind)) continue;
            rows.Add((kind, shown));
        }
        rows.AddRange(Defaults.Where(d => rows.All(r => r.Kind != d.Kind)));
        return rows;
    }

    internal static List<string> Encode(IEnumerable<(LibraryRowKind Kind, bool Shown)> rows) =>
        rows.Select(r => r.Shown ? r.Kind.ToString() : OffMark + r.Kind.ToString()).ToList();

    /// <summary>Whether this host can open <paramref name="kind"/>: Downloaded needs the account service.</summary>
    private bool IsAvailable(LibraryRowKind kind) => kind != LibraryRowKind.Downloaded || Shell.HasAccount;

    private List<(LibraryRowKind Kind, bool Shown)> Current() =>
        Rows.Select(r => (r.Kind, r.IsShown)).Concat(_parked).ToList();

    private void Load(IReadOnlyList<string> keys)
    {
        var rows = Decode(keys);
        _parked.Clear();
        _parked.AddRange(rows.Where(r => !IsAvailable(r.Kind)));
        Rows.Clear();
        foreach (var (kind, shown) in rows.Where(r => IsAvailable(r.Kind)))
            Rows.Add(new LibraryRowItem(kind, shown) { IsEditing = IsEditing });
    }

    partial void OnIsEditingChanged(bool value)
    {
        foreach (var row in Rows) row.IsEditing = value;
    }

    /// <summary>A row tap: under Edit it switches the row on or off, otherwise it opens the list.</summary>
    [RelayCommand]
    private void Activate(LibraryRowItem? row)
    {
        if (row == null) return;
        if (IsEditing)
        {
            row.IsShown = !row.IsShown;
            return;
        }
        Open(row.Kind);
    }

    private void Open(LibraryRowKind kind)
    {
        switch (kind)
        {
            case LibraryRowKind.Playlists: Shell.OpenPlaylistsCommand.Execute(null); break;
            case LibraryRowKind.Artists: Shell.OpenArtistsCommand.Execute(null); break;
            case LibraryRowKind.Albums: Shell.OpenAlbumsCommand.Execute(null); break;
            case LibraryRowKind.Songs: Shell.OpenSongsCommand.Execute(null); break;
            case LibraryRowKind.Favorites: Shell.OpenFavouritesCommand.Execute(null); break;
            case LibraryRowKind.Downloaded:
                if (Shell.Account is { } account)
                    Shell.Navigate(new SongListPageViewModel(Shell, "Downloaded", () => Shell.Library.Songs.Where(account.IsDownloaded)));
                break;
        }
    }

    [RelayCommand] private void Edit() => IsEditing = true;

    [RelayCommand] private void Done() => FinishEditing();

    /// <summary>Leaves Edit and saves the arrangement if it changed. Also run by Back, a tab
    /// switch and a pushed page, so an edit is never left half-open behind another view.</summary>
    public void FinishEditing()
    {
        if (!IsEditing) return;
        IsEditing = false;
        var keys = Encode(Current());
        if (keys.SequenceEqual(Encode(Decode(Shell.Library.LibraryRowKeys)))) return;
        PendingSave = Shell.Library.SaveLibraryRowKeysAsync(keys);
    }

    /// <summary>Moves a row (a handle drag's drop). Indices are clamped to the list.</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= Rows.Count) return;
        to = Math.Clamp(to, 0, Rows.Count - 1);
        if (to != from) Rows.Move(from, to);
    }

    /// <summary>The settings were read (start) or saved: rebuild unless that is this list's own
    /// arrangement, so a save does not tear the rows down and re-create them.</summary>
    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryViewModel.LibraryRowKeys) || IsEditing) return;
        var next = Decode(Shell.Library.LibraryRowKeys);
        if (Encode(next).SequenceEqual(Encode(Current()))) return;
        Load(Shell.Library.LibraryRowKeys);
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.CurrentPage) or nameof(ShellViewModel.SelectedTab)) FinishEditing();
    }
}
