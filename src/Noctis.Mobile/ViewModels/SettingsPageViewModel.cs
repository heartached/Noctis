using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>One accent swatch; the names and colours are the desktop's (App.AccentPresets), but
/// for <see cref="Artwork"/>, Match Artwork, which has no colour of its own.</summary>
public sealed record AccentSwatch(string Name, string Hex)
{
    /// <summary>The preset's name in the current language; <see cref="Name"/> is the one saved.</summary>
    public string DisplayName => Loc.T("Mobile.Accent." + Name);

    /// <summary>Match Artwork: the accent follows the playing cover.</summary>
    public bool IsArtwork => Hex.Length == 0;

    // Immutable: not an AvaloniaObject, so the static Accents list can be built off the UI thread.
    public IBrush Brush { get; } = Hex.Length == 0 ? ArtworkBrush : new ImmutableSolidColorBrush(Color.Parse(Hex));

    /// <summary>Match Artwork's swatch: the hue wheel, a cover's every colour (the swatch carries
    /// a white note glyph on top, so it reads as "from the music" rather than a colour picker).
    /// Every stop keeps the white note and tick at 3:1 or more (3.45:1 the least, the green;
    /// a first draft's #FF5E62 red read 2.99:1 in the theme sweep), hence no yellow.</summary>
    public static IBrush ArtworkBrush { get; } = new ImmutableConicGradientBrush(
        new[]
        {
            new ImmutableGradientStop(0.00, Color.Parse("#E5383B")),
            new ImmutableGradientStop(0.17, Color.Parse("#E8590C")),
            new ImmutableGradientStop(0.33, Color.Parse("#2F9E44")),
            new ImmutableGradientStop(0.50, Color.Parse("#0C8599")),
            new ImmutableGradientStop(0.67, Color.Parse("#3B5BDB")),
            new ImmutableGradientStop(0.83, Color.Parse("#9C36B5")),
            new ImmutableGradientStop(1.00, Color.Parse("#E5383B")),
        });

    /// <summary>The Match Artwork swatch, last on the Accent page.</summary>
    public static AccentSwatch Artwork { get; } = new("MatchArtwork", "");
}

/// <summary>One configured music folder as Settings lists it: the root as saved (a SAF tree
/// URI), the name the user would give it ("Music/Tones") and whether the phone lost its access
/// (its songs stay listed, none plays).</summary>
public sealed record SettingsFolder(string Root, string Name, bool AccessLost)
{
    /// <summary>The row's label: the folder's own name ("Tones").</summary>
    public string Title
    {
        get
        {
            var path = Name.TrimEnd('/');
            var slash = path.LastIndexOf('/');
            return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : Name;
        }
    }

    /// <summary>The grey line under it: where the folder is ("Music/Tones"), when that says more
    /// than the label. A lost folder shows the warning there instead.</summary>
    public bool ShowsPath => !AccessLost && Title != Name;

    /// <summary>Readable as far as the phone's grants go, but the last scan could not reach it
    /// (its card was taken out, the folder was deleted or renamed).</summary>
    public bool Unavailable { get; init; }
}

/// <summary>One open-source component in Settings → About → Open-Source Licenses.</summary>
public sealed record LicenseEntry(string Name, string License, string Detail);

/// <summary>Settings is one sheet: its top level, or one of the pages pushed inside it.</summary>
public enum SettingsPane { Main, Folders, Lyrics, Background, Scrobbling, History, Accent, PlayerBar, Downloads, WhatsNew, Licenses, Language, Theme, Hidden }

/// <summary>One line of Settings › Theme: a small preview of the theme (its page, a card and the
/// chrome glass on it, its accent when it has one), its name, and a tick on the one in use.</summary>
public sealed partial class ThemeRow : ObservableObject
{
    internal ThemeRow(PhoneThemeChoice choice)
    {
        Choice = choice;
        PageBrush = new ImmutableSolidColorBrush(choice.Page);
        CardBrush = new ImmutableSolidColorBrush(choice.Card);
        ChromeBrush = new ImmutableSolidColorBrush(PhoneThemes.Over(choice.Chrome, choice.Page));
        AccentBrush = choice.Accent is { } a ? new ImmutableSolidColorBrush(a) : null;
    }

    internal PhoneThemeChoice Choice { get; }
    public string Id => Choice.Id;
    public string Name => Choice.Name;
    public IBrush PageBrush { get; }
    public IBrush CardBrush { get; }
    public IBrush ChromeBrush { get; }
    public IBrush? AccentBrush { get; }
    public bool HasAccent => AccentBrush != null;

    internal void Relabel() => OnPropertyChanged(nameof(Name));

    [ObservableProperty] private bool _isSelected;
}

/// <summary>One line of Settings › Language: "System language" (follow the phone) or a shipped
/// translation, its own name first ("Русский") and the English one under it.</summary>
public sealed partial class LanguageChoice : ObservableObject
{
    public LanguageChoice(string code, string label, string detail)
    {
        Code = code;
        Label = label;
        Detail = detail;
    }

    /// <summary>"" (<see cref="Loc.SystemLanguage"/>) or a culture name ("ru", "zh-Hant").</summary>
    public string Code { get; }
    public string Label { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Phone Settings. Changes apply at once (theme through the host, text size on the lyrics
/// page) and are saved in order through one queued chain, so two quick changes cannot
/// interleave their load-modify-save of settings.json. Gapless playback has no setting here:
/// the phone is always gapless (NowPlayingViewModel), and the saved desktop value is left as is.
/// The top level is Apple Music's account sheet: the profile card with its stats, then rows in
/// five sections (Appearance, Library, Listening, Help, About), each a value and a chevron into a
/// page pushed inside the sheet (<see cref="Pane"/>), a menu (Theme) or an action; Back leaves
/// a page before it closes Settings.
/// </summary>
public sealed partial class SettingsPageViewModel : MobilePage
{
    /// <summary>Seventeen of the desktop presets round the wheel, Crimson first as the default
    /// (beta feedback 11-07 asked for more): the first nine, then a pastel peach, lime, a deep
    /// forest, mint, sky, lilac, and two neutrals (silver, slate). With Match Artwork last they
    /// fill three rows of six (<see cref="AccentChoices"/>). Any two stay clearly apart (CIE76
    /// delta E 25 or more), which is why Coral (15 from Crimson) and Violet (17 from Indigo) are out.</summary>
    public static IReadOnlyList<AccentSwatch> Accents { get; } = new AccentSwatch[]
    {
        new("Crimson", "#E74856"), new("Tangerine", "#FF8547"), new("Peach", "#FFB59A"), new("Gold", "#F4D24B"),
        new("Lime", "#7ED957"), new("Emerald", "#12C76F"),
        new("Forest", "#22714A"), new("Mint", "#8FE3C4"), new("Teal", "#0FA3B1"), new("Sky", "#39B5F0"),
        new("Cerulean", "#2A7FCF"), new("Indigo", "#4338CA"),
        new("Lilac", "#B39DDB"), new("Orchid", "#C45CE0"), new("Rose", "#E754B5"), new("Silver", "#B8BCC4"),
        new("Slate", "#5B6C8F"),
    };

    /// <summary>The Accent page's swatches: the presets, then Match Artwork.</summary>
    public static IReadOnlyList<AccentSwatch> AccentChoiceList { get; } = Accents.Append(AccentSwatch.Artwork).ToArray();

    private static string SignInTitle => Loc.T("Mobile.Account.SignInTitle");

    private bool _loading;
    // Set once the saved values were read (or the read failed): a change before it is kept
    // and saved after the load instead of being overwritten by it.
    private bool _loaded;
    private SavedFields _setBeforeLoad;
    private string _accentHex = MobileTheme.DefaultAccent;
    // Settings › Accent › Match Artwork: the playing cover's colour stands in for _accentHex.
    private bool _accentFollowsArtwork;
    // The saved accent's name when it is not one of the presets ("Scarlet", picked on an earlier
    // phone build or on the desktop): kept, shown and saved back as it was.
    private string? _savedAccentName;

    public SettingsPageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        Background = new BackgroundSettingsViewModel(shell);
        Scrobbling = new ScrobblingSettingsViewModel(shell);
        History = new ListeningHistoryViewModel(shell);
        Shell.Library.Folders.CollectionChanged += OnFoldersChanged;
        Shell.Library.PropertyChanged += OnLibraryChanged;
        Shell.Library.Refreshed += OnLibraryRefreshed;
        Shell.Lyrics.PropertyChanged += OnLyricsChanged;
        Shell.PropertyChanged += OnShellChanged;
        Background.PropertyChanged += OnSubPageChanged;
        Scrobbling.PropertyChanged += OnSubPageChanged;
        History.PropertyChanged += OnSubPageChanged;
        if (Shell.Account != null) Shell.Account.StateChanged += OnAccountChanged;
        RefreshStats();
    }

    public ShellViewModel Shell { get; }
    public override string Title => Loc.T("Nav.Settings");

    /// <summary>The pages other agents' view models drive (Background, Scrobbling, Listening
    /// History); the sheet's rows show their <c>Summary</c>.</summary>
    public BackgroundSettingsViewModel Background { get; }
    public ScrobblingSettingsViewModel Scrobbling { get; }
    public ListeningHistoryViewModel History { get; }

    /// <summary>The clock behind the profile row's "Synced 5 min ago"; tests pin it.</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public IReadOnlyList<string> Appearances => MobileTheme.Appearances;
    public IReadOnlyList<string> DarkThemes => MobileTheme.DarkThemes;
    public IReadOnlyList<AccentSwatch> AccentChoices => AccentChoiceList;

    // ── The pages inside the sheet ──

    /// <summary>Which page of the sheet shows; the view slides a pushed one in from the right.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainPane), nameof(IsSubPane), nameof(HeaderTitle))]
    private SettingsPane _pane;

    /// <summary>The pushed page's content, kept while it slides back out after Back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFoldersPane), nameof(ShowsLyricsPane), nameof(ShowsBackgroundPane), nameof(ShowsScrobblingPane),
        nameof(ShowsHistoryPane), nameof(ShowsAccentPane), nameof(ShowsPlayerBarPane), nameof(ShowsDownloadsPane),
        nameof(ShowsWhatsNewPane), nameof(ShowsLicensesPane), nameof(ShowsLanguagePane), nameof(ShowsThemePane),
        nameof(ShowsHiddenPane), nameof(LanguageListSource))]
    private SettingsPane _shownSubPane = SettingsPane.Folders;

    public bool IsMainPane => Pane == SettingsPane.Main;
    public bool IsSubPane => !IsMainPane;
    public bool ShowsFoldersPane => ShownSubPane == SettingsPane.Folders;
    public bool ShowsLyricsPane => ShownSubPane == SettingsPane.Lyrics;
    public bool ShowsBackgroundPane => ShownSubPane == SettingsPane.Background;
    public bool ShowsScrobblingPane => ShownSubPane == SettingsPane.Scrobbling;
    public bool ShowsHistoryPane => ShownSubPane == SettingsPane.History;
    public bool ShowsAccentPane => ShownSubPane == SettingsPane.Accent;
    public bool ShowsPlayerBarPane => ShownSubPane == SettingsPane.PlayerBar;
    public bool ShowsDownloadsPane => ShownSubPane == SettingsPane.Downloads;
    public bool ShowsWhatsNewPane => ShownSubPane == SettingsPane.WhatsNew;
    public bool ShowsLicensesPane => ShownSubPane == SettingsPane.Licenses;
    public bool ShowsLanguagePane => ShownSubPane == SettingsPane.Language;
    public bool ShowsThemePane => ShownSubPane == SettingsPane.Theme;

    /// <summary>The header's title: "Settings", or the pushed page's name.</summary>
    public string HeaderTitle => Pane switch
    {
        SettingsPane.Folders => Loc.T("Mobile.Settings.MusicFolders"),
        SettingsPane.Lyrics => Loc.T("Tab.Lyrics"),
        SettingsPane.Background => Loc.T("Mobile.Settings.Background"),
        SettingsPane.Scrobbling => Loc.T("Mobile.Settings.Scrobbling"),
        SettingsPane.History => Loc.T("Mobile.Settings.ListeningHistory"),
        SettingsPane.Accent => Loc.T("Mobile.Settings.Accent"),
        SettingsPane.PlayerBar => Loc.T("Mobile.Settings.PlayerBar"),
        SettingsPane.Downloads => Loc.T("Mobile.Settings.Downloads"),
        SettingsPane.WhatsNew => Loc.T("Mobile.Settings.WhatsNew"),
        SettingsPane.Licenses => Loc.T("Mobile.Settings.Licenses"),
        SettingsPane.Language => Loc.T("Settings.Language.Title"),
        SettingsPane.Theme => Loc.T("Mobile.Settings.Theme"),
        SettingsPane.Hidden => Loc.T("Mobile.Settings.Hidden"),
        _ => Title,
    };

    partial void OnPaneChanged(SettingsPane value)
    {
        if (value != SettingsPane.Main) ShownSubPane = value;
        else
        {
            FolderToRemove = null;   // a question left open on Music Folders does not wait there
            DropQuestion();
        }
    }

    [RelayCommand] private void OpenPane(SettingsPane pane) => Pane = pane;

    [RelayCommand] private void ShowMain() => Pane = SettingsPane.Main;

    /// <summary>Android Back on a pushed page returns to the top level first.</summary>
    public override bool TryGoBackWithin()
    {
        if (Pane == SettingsPane.Main) return false;
        Pane = SettingsPane.Main;
        return true;
    }

    // ── Music Folders ──

    /// <summary>The configured roots as the user would name them ("Music/Tones"), not SAF URIs,
    /// each flagged when its access was lost: its songs stay listed but none of them plays.</summary>
    public IReadOnlyList<SettingsFolder> FolderRows => Shell.Library.Folders
        .Select(f => Shell.Library.HasAccess(f)
            ? new SettingsFolder(f, FolderName(f), false) { Unavailable = Shell.Library.UnavailableFolders.Contains(f) }
            : new SettingsFolder(f, FolderName(f), true))
        .ToList();

    /// <summary>Where the folder is, as the user would say it: "Music/Tones" on the phone's
    /// storage; on a card the card's name first ("SD card/Music"), or a whole card by its name
    /// alone, so a card's Music is not mistaken for the phone's.</summary>
    private string FolderName(string root)
    {
        var path = FolderDisplay(root);
        if (Shell.Library.StorageName(root) is not { } storage) return path;
        return path.EndsWith(':') ? storage : $"{storage}/{path}";
    }

    /// <summary>The folder whose Remove was tapped: the page asks before it goes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingRemoveFolder), nameof(RemoveFolderQuestion))]
    private SettingsFolder? _folderToRemove;

    public bool IsConfirmingRemoveFolder => FolderToRemove != null;

    public string RemoveFolderQuestion => FolderToRemove is { } folder
        ? Loc.T("Mobile.Folders.RemoveQuestionLong", folder.Title)
        : string.Empty;

    // The question up now (the shell's glass prompt).
    private PromptViewModel? _question;

    /// <summary>Asks on the glass prompt (owner 10-20: no more cards inside the page).</summary>
    [RelayCommand]
    private void AskRemoveFolder(SettingsFolder folder)
    {
        DropQuestion();
        FolderToRemove = folder;
        var question = new PromptViewModel(Shell, Loc.T("Mobile.Folders.RemoveQuestion", folder.Title),
            Loc.T("Mobile.Folders.RemoveDetail"),
            new[] { new PromptAction(Loc.T("Mobile.Folders.Remove"), _ => ConfirmRemoveFolderCommand.ExecuteAsync(null), isDestructive: true) },
            cancelled: () => { if (ReferenceEquals(FolderToRemove, folder)) FolderToRemove = null; });
        _question = question;
        Shell.ShowPrompt(question);
    }

    [RelayCommand]
    private void CancelRemoveFolder()
    {
        FolderToRemove = null;
        DropQuestion();
    }

    private void DropQuestion()
    {
        if (_question is { } q) Shell.ClosePrompt(q);
        _question = null;
    }

    /// <summary>Concurrent: it awaits the rescan, and the next folder's Remove stayed greyed out
    /// for all of it (the confirm runs once per ask: <see cref="FolderToRemove"/> clears first).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ConfirmRemoveFolderAsync()
    {
        if (FolderToRemove is not { } folder) return;
        FolderToRemove = null;
        DropQuestion();
        // Its songs leave the queue with it: the access goes now, and each would otherwise fail in
        // turn as "lost access, add the folder again" (a SAF song's path starts with its folder's).
        var root = folder.Root;
        Shell.Player.DropTracks(t => t.SourceType != SourceType.NoctisServer
            && (t.FilePath == root || t.FilePath.StartsWith(root + "/", StringComparison.Ordinal)));
        await Shell.Library.RemoveFolderAsync(root);
    }

    /// <summary>Under Add Folder: why a pick was refused, or the scan's status, else what the
    /// folders are for the app.</summary>
    public string FoldersFootnote => !string.IsNullOrEmpty(Shell.Library.StatusText)
        ? Shell.Library.StatusText
        : Shell.Library.AllMusic ? Loc.T("Mobile.Folders.WithAllMusic") : Loc.T("Mobile.Folders.ReadOnlyChosen");

    /// <summary>The Music Folders row's value: how many there are, or that all the phone's music
    /// is found when there are none besides.</summary>
    public string FoldersValue => Shell.Library.Folders.Count > 0
        ? Shell.Library.Folders.Count.ToString(Loc.Instance.Culture)
        : Shell.Library.AllMusic ? Loc.T("Mobile.Folders.AllMusicValue") : Loc.T("Mobile.Folders.NoneValue");

    /// <summary>A folder lost its access, or the last scan could not reach it: the Music Folders
    /// row carries a warning dot.</summary>
    public bool HasLostFolder => Shell.Library.ShowAllMusicLost || FolderRows.Any(f => f.AccessLost || f.Unavailable);

    /// <summary>The Music Folders row as the screen reader says it: the count, then the warning a dot shows.</summary>
    public string FoldersRowName => HasLostFolder
        ? Loc.T("Mobile.Folders.RowNameWarning", Loc.T("Mobile.Settings.MusicFolders"), FoldersValue)
        : Named(Loc.T("Mobile.Settings.MusicFolders"), FoldersValue);

    /// <summary>Rescan only with a folder to scan and no scan running.</summary>
    public bool CanRescan => (Shell.Library.HasFolders || Shell.Library.AllMusic) && !Shell.Library.IsScanning;

    /// <summary>The grey line in the Rescan Library row: when the last scan finished, else what it does.</summary>
    public string RescanSubtitle => Shell.Library.LastScanLocal is { } scanned
        ? Loc.T("Mobile.Settings.LastScan", ScanTime(scanned, UtcNow().ToLocalTime()))
        : Loc.T("Mobile.Settings.RescanHint");

    public string RescanRowName => Named(Loc.T("Mobile.RescanLibrary"), RescanSubtitle);

    /// <summary>"today, 10:42", "yesterday, 22:10", "4 Oct, 09:05".</summary>
    internal static string ScanTime(DateTime scanned, DateTime now)
    {
        var culture = Loc.Instance.Culture;
        var time = scanned.ToString("t", culture);
        if (scanned.Date == now.Date) return Loc.T("Mobile.Settings.ScanToday", time);
        if (scanned.Date == now.Date.AddDays(-1)) return Loc.T("Mobile.Settings.ScanYesterday", time);
        return Loc.T("Mobile.Settings.ScanOnDay", scanned.ToString(MonthDay(culture), culture), time);
    }

    /// <summary>Under Rescan Library: the scan's status ("Scanning…", a failure), else why it is off.</summary>
    public string RescanFootnote =>
        !string.IsNullOrEmpty(Shell.Library.StatusText) ? Shell.Library.StatusText
        : Shell.Library.IsLoaded && !Shell.Library.HasFolders && !Shell.Library.AllMusic ? Loc.T("Mobile.Settings.AddFolderToScan")
        : string.Empty;

    // ── Appearance ──

    /// <summary>The Theme row's value: Light, the dark theme ("Forest"), or with Match Device on
    /// "System, Forest" (Light on a light phone, the dark theme on a dark one).</summary>
    public string ThemeValue => Appearance switch
    {
        "Light" => ThemeLabel("Light"),
        "Dark" => PhoneThemes.DarkOrInk(DarkTheme).Name,
        _ => Loc.T("Mobile.Theme.SystemWith", PhoneThemes.DarkOrInk(DarkTheme).Name),
    };

    /// <summary>An appearance ("System", "Light", "Dark") or dark style ("Ink", "Smoke", "Midnight") as shown;
    /// the English name is the saved value.</summary>
    internal static string ThemeLabel(string name) => Loc.T("Mobile.Theme." + name);

    /// <summary>A short day and month in the language's own order: English keeps the phone's
    /// "4 Oct"; "4 окт.", "10月4日" elsewhere.</summary>
    internal static string MonthDay(System.Globalization.CultureInfo culture)
    {
        if (culture.TwoLetterISOLanguageName == "en") return "d MMM";
        var pattern = culture.DateTimeFormat.MonthDayPattern;
        return pattern.Contains("MMMM", StringComparison.Ordinal) ? pattern.Replace("MMMM", "MMM") : pattern;
    }

    /// <summary>A row as the screen reader says it: its label, then its value.</summary>
    private static string Named(string label, string? value) => string.IsNullOrEmpty(value) ? label : $"{label}, {value}";

    public string BackgroundRowName => Named(Loc.T("Mobile.Settings.Background"), Background.Summary);
    public string ThemeRowName => Named(Loc.T("Mobile.Settings.Theme"), ThemeValue);
    public string AccentRowName => Named(Loc.T("Mobile.Settings.Accent"), AccentName);

    /// <summary>The accent's dot on the Accent row (Match Artwork's own swatch while it is on).</summary>
    public IBrush AccentBrush => UsesThemeAccent && PhoneThemes.DarkOrInk(DarkTheme).Accent is { } own ? new ImmutableSolidColorBrush(own)
        : UsesArtworkAccent ? AccentSwatch.ArtworkBrush
        : Color.TryParse(_accentHex, out var colour) ? new ImmutableSolidColorBrush(colour) : Accents[0].Brush;

    /// <summary>The accent's preset name ("Crimson"); a saved accent outside the presets keeps the
    /// name it was saved with ("Scarlet"), else "Custom" (a colour picked on the desktop).</summary>
    public string AccentName => UsesThemeAccent ? Loc.T("Mobile.Accent.MatchTheme")
        : UsesArtworkAccent ? Loc.T("Mobile.Accent.MatchArtwork")
        : FixedAccentName;

    /// <summary>The chosen colour's name, Match Artwork or not: what shows when nothing plays.</summary>
    public string FixedAccentName =>
        AccentPresetName is var name && Accents.Any(a => a.Name == name) ? Loc.T("Mobile.Accent." + name)
        : name == CustomAccent ? Loc.T("Mobile.Accent.Custom")
        : name;

    /// <summary>Match Artwork is on (and Match Theme is not wearing a colour theme's accent).</summary>
    public bool UsesArtworkAccent => _accentFollowsArtwork && !UsesThemeAccent;

    /// <summary>Match Artwork is chosen: the setting as saved.</summary>
    public bool AccentFollowsArtwork => _accentFollowsArtwork;

    private const string CustomAccent = "Custom";

    /// <summary>The English preset name settings.json keeps (the desktop reads it): the preset's,
    /// the one it was saved with, or "Custom".</summary>
    private string AccentPresetName =>
        Accents.FirstOrDefault(a => string.Equals(a.Hex, _accentHex, StringComparison.OrdinalIgnoreCase))?.Name
        ?? (string.IsNullOrWhiteSpace(_savedAccentName) || Accents.Any(a => a.Name == _savedAccentName) ? CustomAccent : _savedAccentName);

    /// <summary>The Player Bar row's value: which progress the mini player shows.</summary>
    public string PlayerBarValue => (Shell.ShowMiniProgressFill, Shell.ShowMiniProgressLine) switch
    {
        (true, true) => Loc.T("Mobile.Settings.ColourAndLine"),
        (true, false) => Loc.T("Mobile.Settings.ProgressColour"),
        (false, true) => Loc.T("Mobile.Settings.ProgressLine"),
        _ => Loc.T("Mobile.Settings.Plain"),
    };

    public string PlayerBarRowName => Named(Loc.T("Mobile.Settings.PlayerBar"), PlayerBarValue);

    /// <summary>The Now Playing Artwork row's value: Cover, Vinyl or CD.</summary>
    public string NowPlayingArtworkValue => ArtworkStyleLabel(Shell.NowPlayingArtwork);

    internal static string ArtworkStyleLabel(NowPlayingArtworkStyle style) => Loc.T("Mobile.NowPlayingArtwork." + style);

    public string NowPlayingArtworkRowName => Named(Loc.T("Mobile.Settings.NowPlayingArtwork"), NowPlayingArtworkValue);

    /// <summary>The Now Playing Artwork row: Cover / Vinyl / CD on the glass pop menu, the one in
    /// use ticked; a pick applies at once (the player behind the sheet changes live).</summary>
    [RelayCommand]
    private void ShowNowPlayingArtworkMenu()
    {
        var items = new List<OptionsMenuItem>();
        foreach (var style in NowPlayingArtworkStyles.All)
            items.Add(new OptionsMenuItem(Shell, ArtworkStyleLabel(style), Shell.NowPlayingArtwork == style, () => Shell.NowPlayingArtwork = style));
        Shell.ShowOptions(OptionsMenuViewModel.ForItems(items));
    }


    // ── Theme ──

    /// <summary>Settings › Theme's first list: Light and the four dark styles.</summary>
    public IReadOnlyList<ThemeRow> StandardThemes => _standardThemes ??= BuildThemeRows(PhoneThemes.Standards);

    /// <summary>The colour themes, one per Colour background (same name).</summary>
    public IReadOnlyList<ThemeRow> ColourThemes => _colourThemes ??= BuildThemeRows(PhoneThemes.Colours);

    private IReadOnlyList<ThemeRow>? _standardThemes, _colourThemes;

    private IReadOnlyList<ThemeRow> BuildThemeRows(IEnumerable<PhoneThemeChoice> choices)
    {
        var rows = choices.Select(c => new ThemeRow(c)).ToList();
        foreach (var row in rows) row.IsSelected = IsThemeInUse(row.Choice);
        return rows;
    }

    private IEnumerable<ThemeRow> ThemeRows =>
        (_standardThemes ?? Array.Empty<ThemeRow>()).Concat(_colourThemes ?? Array.Empty<ThemeRow>());

    /// <summary>Light while Light is picked; the dark theme otherwise (alone, or Match Device's dark side).</summary>
    private bool IsThemeInUse(PhoneThemeChoice choice) =>
        choice.IsLight ? Appearance == "Light" : Appearance != "Light" && choice.Id == DarkTheme;

    private void SyncThemeTicks()
    {
        foreach (var row in ThemeRows) row.IsSelected = IsThemeInUse(row.Choice);
    }

    /// <summary>Match Device (Apple's Automatic): Light on a light phone, the dark theme on a dark
    /// one. Turned off it keeps what shows now.</summary>
    public bool MatchDevice
    {
        get => Appearance == "System";
        set
        {
            if (value == MatchDevice) return;
            Appearance = value ? "System" : Application.Current?.ActualThemeVariant == ThemeVariant.Light ? "Light" : "Dark";
        }
    }

    /// <summary>Under the Match Device switch: which theme each mode shows.</summary>
    public string MatchDeviceFootnote => Loc.T("Mobile.Theme.MatchDeviceFootnote", PhoneThemes.DarkOrInk(DarkTheme).Name);

    /// <summary>A tap applies the theme at once. Light turns Match Device off; a dark theme becomes
    /// the dark side (and shows now, unless Match Device is on and the phone is light).</summary>
    [RelayCommand]
    private void ChooseTheme(ThemeRow? row)
    {
        if (row == null) return;
        if (row.Choice.IsLight)
        {
            Appearance = "Light";
            return;
        }
        var fields = SavedFields.None;
        _themeBatch = true;
        try
        {
            if (DarkTheme != row.Id) { DarkTheme = row.Id; fields |= SavedFields.DarkTheme; }
            if (Appearance == "Light") { Appearance = "Dark"; fields |= SavedFields.Appearance; }
        }
        finally
        {
            _themeBatch = false;
        }
        if (fields != SavedFields.None) ThemeChanged(fields);
    }

    // Set while one pick moves two fields: one re-theme, one save.
    private bool _themeBatch;

    /// <summary>Settings › Accent › Match Theme: a colour theme's own accent instead of the chosen
    /// one. Picking a swatch turns it off.</summary>
    public bool AccentFollowsTheme
    {
        get => _accentFollowsTheme;
        set
        {
            if (!SetProperty(ref _accentFollowsTheme, value)) return;
            AccentChanged();
            ThemeChanged(SavedFields.AccentFollows);
        }
    }

    private bool _accentFollowsTheme = true;

    /// <summary>The dark theme has its own accent (a colour theme): the Match Theme switch shows.</summary>
    public bool ShowsMatchTheme => PhoneThemes.DarkOrInk(DarkTheme).Accent != null;

    /// <summary>The theme's accent is the one worn (Match Theme on, a colour theme, not Light).</summary>
    public bool UsesThemeAccent => AccentFollowsTheme && ShowsMatchTheme && Appearance != "Light";

    /// <summary>The swatch the Accent grid ticks: none while Match Theme wears the theme's own
    /// accent (Crimson ticked on Forest read as the colour in use), else the saved one. Setting
    /// one is a pick, so a tap on the saved swatch while nothing is ticked turns Match Theme off.</summary>
    public AccentSwatch? ShownAccent
    {
        get => UsesThemeAccent ? null : SelectedAccent;
        set
        {
            // The grid writes null back when Match Theme clears it: not a pick.
            if (value == null) return;
            if (Equals(value, SelectedAccent))
            {
                if (UsesThemeAccent) OnSelectedAccentChanged(value);
            }
            else SelectedAccent = value;
        }
    }

    /// <summary>Settings › Library › Show in Song Rows: Off, Ratings or Favorites on the glass menu.</summary>
    [RelayCommand]
    private void ShowSongRowsMenu()
    {
        var items = Enum.GetValues<SongRowMark>()
            .Select(mark => new OptionsMenuItem(Shell, ShellViewModel.SongRowMarkName(mark), Shell.SongRowMark == mark, () => Shell.SongRowMark = mark))
            .ToList();
        Shell.ShowOptions(OptionsMenuViewModel.ForItems(items));
    }

    public string SongRowsRowName => Named(Loc.T("Mobile.Settings.SongRows"), Shell.SongRowMarkLabel);


    // ── Lyrics ──

    /// <summary>The Lyrics row's value: which extra lines show.</summary>
    public string LyricsValue => (Shell.Lyrics.ShowTranslation, Shell.Lyrics.ShowRomanization) switch
    {
        (true, true) => Loc.T("Mobile.Lyrics.BothOn"),
        (true, false) => Loc.T("Mobile.Lyrics.TranslationsOn"),
        (false, true) => Loc.T("Mobile.Lyrics.RomanizationOn"),
        _ => Loc.T("Mobile.Off"),
    };

    public string LyricsRowName => Named(Loc.T("Tab.Lyrics"), LyricsValue);

    // ── Listening ──

    public string ScrobblingRowName => Named(Loc.T("Mobile.Settings.Scrobbling"), Scrobbling.Summary);
    public string HistoryRowName => Named(Loc.T("Mobile.Settings.ListeningHistory"), History.Summary);

    /// <summary>Pick Up on Computer and its footnote: only while signed in (the queue goes to the
    /// account's computer; signed out there is none, though the account service is there).</summary>
    public bool ShowPickUp => IsSignedIn;

    /// <summary>Under Pick Up on Computer: what the switch does, naming the computer.</summary>
    public string PickUpFootnote => IsSignedIn
        ? Loc.T("Mobile.Settings.PickUpFootnote", AccountPageViewModel.ServerLabel(Shell.Account!.Account!))
        : Loc.T("Mobile.Settings.PickUpFootnoteGeneric");

    // ── Smooth Motion ──

    /// <summary>Settings › Appearance › Smooth Motion (on by default): the screen's fastest refresh
    /// rate, up to 120 Hz, or 60 Hz (Standard) off. Applied through the host at once and saved on
    /// its own, as the Wi-Fi switch is; the app applies the saved value at start (AndroidApp).</summary>
    [ObservableProperty] private bool _smoothMotion = true;

    /// <summary>The settings.json write of the Smooth Motion switch; tests await it.</summary>
    internal Task PendingSmoothMotionSave { get; private set; } = Task.CompletedTask;

    private bool _smoothMotionSetBeforeLoad;

    partial void OnSmoothMotionChanged(bool value)
    {
        if (_loading) return;
        _smoothMotionSetBeforeLoad = true;
        Shell.FrameRate?.Request(value);
        PendingSmoothMotionSave = SaveSmoothMotionAsync(value);
    }

    private async Task SaveSmoothMotionAsync(bool value)
    {
        try
        {
            await Shell.Library.Persistence.UpdateSettingsAsync(settings => settings.MobileSmoothMotion = value);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Settings", $"Save failed: {ex.Message}");
        }
    }

    // ── Downloads ──

    /// <summary>Settings → Downloads → Download on Wi-Fi Only (on by default): a download asked for on
    /// mobile data is refused (NoctisAccountService.IsOnWifi). Saved at once, on its own, beside the
    /// theme's save chain.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadsValue), nameof(DownloadsRowName))]
    private bool _downloadWifiOnly = true;

    public string DownloadsValue => DownloadWifiOnly ? Loc.T("Mobile.Settings.WifiOnlyValue") : Loc.T("Mobile.Settings.AnyNetwork");
    public string DownloadsRowName => Named(Loc.T("Mobile.Settings.Downloads"), DownloadsValue);

    /// <summary>The settings.json write of the Wi-Fi switch; tests await it.</summary>
    internal Task PendingWifiSave { get; private set; } = Task.CompletedTask;

    // Set by a tap before the load read the file: the load then keeps it (as LoadCoreAsync does).
    private bool _wifiSetBeforeLoad;

    partial void OnDownloadWifiOnlyChanged(bool value)
    {
        if (_loading) return;
        _wifiSetBeforeLoad = true;
        PendingWifiSave = SaveWifiOnlyAsync(value);
    }

    private async Task SaveWifiOnlyAsync(bool value)
    {
        try
        {
            await Shell.Library.Persistence.UpdateSettingsAsync(settings => settings.MobileDownloadWifiOnly = value);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Settings", $"Save failed: {ex.Message}");
        }
    }

    private async Task LoadDownloadSettingsAsync()
    {
        Noctis.Models.AppSettings settings;
        try
        {
            settings = await Shell.Library.Persistence.LoadSettingsAsync();
        }
        catch (Exception ex)
        {
            // As LoadCoreAsync: the switch keeps its default, and the page still loads.
            DebugLog.Write("Settings", $"Load failed: {ex.Message}");
            return;
        }
        _loading = true;
        try
        {
            if (!_wifiSetBeforeLoad) DownloadWifiOnly = settings.MobileDownloadWifiOnly;
            // Shown only: the app applied the saved rate at start.
            if (!_smoothMotionSetBeforeLoad) SmoothMotion = settings.MobileSmoothMotion;
        }
        finally { _loading = false; }
    }

    /// <summary>The Downloads page's storage line: "312 songs · 4.2 GB".</summary>
    [ObservableProperty] private string _storageUsedText = "—";

    /// <summary>Remove All Downloads shows when there is a computer link and something downloaded.</summary>
    [ObservableProperty] private bool _canRemoveDownloads;

    [RelayCommand]
    private void AskRemoveDownloads()
    {
        if (!CanRemoveDownloads) return;
        DropQuestion();
        var question = new PromptViewModel(Shell, Loc.T("Mobile.Downloads.RemoveQuestion"),
            Loc.T("Mobile.Downloads.RemoveDetail", StorageUsedText),
            new[] { new PromptAction(Loc.T("Mobile.Downloads.RemoveAction"), _ => ConfirmRemoveDownloadsCommand.ExecuteAsync(null), isDestructive: true) });
        _question = question;
        Shell.ShowPrompt(question);
    }

    /// <summary>Concurrent: the prompt's action awaits it, and a second ask must not wait behind it.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ConfirmRemoveDownloadsAsync()
    {
        DropQuestion();
        if (Shell.Account is not { } account) return;
        try
        {
            await account.RemoveAllDownloadsAsync();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Settings", $"Remove downloads failed: {ex.Message}");
        }
        RefreshStats();
    }

    // ── The profile card's stats ──

    /// <summary>Songs, Downloaded and On Phone under the profile (Songs, Albums and Artists with no
    /// account, so no downloads). "—" until read: the counts are the library's; the downloads'
    /// count and bytes are summed off the UI thread.</summary>
    [ObservableProperty] private string _songsStat = "—";
    [ObservableProperty] private string _albumsStat = "—";
    [ObservableProperty] private string _artistsStat = "—";
    [ObservableProperty] private string _downloadedStat = "—";
    [ObservableProperty] private string _onPhoneStat = "—";

    /// <summary>The last downloads read; tests await it.</summary>
    internal Task StatsLoaded { get; private set; } = Task.CompletedTask;

    private int _statsBusy;
    private volatile bool _statsDirty;

    private void RefreshStats()
    {
        SongsStat = Shell.Library.IsLoaded ? Shell.Library.SongCount.ToString("N0", Loc.Instance.Culture) : "—";
        AlbumsStat = Shell.Library.IsLoaded ? Shell.Library.AlbumCount.ToString("N0", Loc.Instance.Culture) : "—";
        ArtistsStat = Shell.Library.IsLoaded ? Shell.Library.ArtistCount.ToString("N0", Loc.Instance.Culture) : "—";
        if (Shell.Account is not { } account)
        {
            ShowDownloadStats(0, 0);
            return;
        }
        if (Interlocked.Exchange(ref _statsBusy, 1) == 1)
        {
            _statsDirty = true;   // the running read starts over once it has landed
            return;
        }
        StatsLoaded = Task.Run(() =>
        {
            do
            {
                _statsDirty = false;
                var (count, bytes) = (account.DownloadedCount, account.DownloadedBytes);
                Shell.Marshal(() => ShowDownloadStats(count, bytes));
            }
            while (_statsDirty);
            Volatile.Write(ref _statsBusy, 0);
            if (_statsDirty) RefreshStats();
        });
    }

    private void ShowDownloadStats(int count, long bytes)
    {
        DownloadedStat = count.ToString("N0", Loc.Instance.Culture);
        OnPhoneStat = AccountPageViewModel.FormatBytes(bytes);
        StorageUsedText = $"{Loc.Plural("Mobile.Count.SongsN0", count)} · {OnPhoneStat}";
        CanRemoveDownloads = Shell.Account != null && count > 0;
    }

    // ── Help ──

    internal const string ReportProblemUrl = "https://github.com/heartached/Noctis/issues/new";
    internal const string WebsiteUrl = "https://noctisapp.cc/";
    /// <summary>The published copy of store/android/privacy-policy.md (the Play listing's link too).</summary>
    internal const string PrivacyPolicyUrl = "https://noctisapp.cc/privacy/android";

    [RelayCommand] private void ReportProblem() => Shell.Links?.Open(ReportProblemUrl);
    [RelayCommand] private void OpenWebsite() => Shell.Links?.Open(WebsiteUrl);
    [RelayCommand] private void OpenPrivacyPolicy() => Shell.Links?.Open(PrivacyPolicyUrl);

    // ── About ──

    /// <summary>"Noctis 1.2.3" → "1.2.3" for the Version row.</summary>
    public string VersionValue => VersionText.StartsWith("Noctis ", StringComparison.Ordinal) ? VersionText["Noctis ".Length..] : VersionText;
    public string VersionRowName => Named(Loc.T("Mobile.Settings.Version"), VersionValue);

    /// <summary>What's New: the phone has no release feed, so the page lists this build's changes;
    /// the lines about the computer only where there is an account.</summary>
    public IReadOnlyList<string> WhatsNewItems => new[]
    {
        Loc.T("Mobile.WhatsNew.Accent"),
        Loc.T("Mobile.WhatsNew.Listening"),
        Shell.HasAccount ? Loc.T("Mobile.WhatsNew.Playlists") : null,
        Loc.T("Mobile.WhatsNew.Queue"),
        Shell.HasAccount ? Loc.T("Mobile.WhatsNew.OneSong") : null,
        Loc.T("Mobile.WhatsNew.Smoother"),
    }.OfType<string>().ToArray();

    /// <summary>Open-Source Licenses: the bundled decoder and the main libraries the phone app is built on.</summary>
    /// <remarks>Names and licences stay as published; the descriptions are translated.</remarks>
    public IReadOnlyList<LicenseEntry> Licenses => new LicenseEntry[]
    {
        new("FFmpeg (libavcodec, libavutil, libswresample)", "LGPL-2.1-or-later", Loc.T("Mobile.License.FFmpeg")),
        new("AndroidX Media3 (ExoPlayer, Session, FFmpeg decoder extension)", "Apache-2.0", Loc.T("Mobile.License.Media3")),
        new("Avalonia", "MIT", Loc.T("Mobile.License.Avalonia")),
        new("Inter", "SIL Open Font License 1.1", Loc.T("Mobile.License.Inter")),
        new("CommunityToolkit.Mvvm", "MIT", Loc.T("Mobile.License.Mvvm")),
        new("TagLib#", "LGPL-2.1", Loc.T("Mobile.License.TagLib")),
        new("Microsoft.Data.Sqlite and SQLitePCLRaw", "MIT and Apache-2.0", Loc.T("Mobile.License.Sqlite")),
        new("SkiaSharp", "MIT", Loc.T("Mobile.License.Skia")),
        new("Xamarin.AndroidX bindings", "Apache-2.0", Loc.T("Mobile.License.AndroidX")),
        new("Google code scanner (ML Kit, Google Play services)", "Android Software Development Kit License; .NET binding MIT", Loc.T("Mobile.License.CodeScanner")),
    };

    // ── Logs, version ──

    /// <summary>The Export Logs row's grey line: the last export's outcome, else what it does.</summary>
    public string ExportSubtitle => ExportStatus.Length > 0 ? ExportStatus : Loc.T("Mobile.Settings.ExportHint");

    public string ExportRowName => Named(Loc.T("Mobile.Settings.ExportLogs"), ExportSubtitle);

    public string VersionText => Shell.VersionText;

    // ── The profile card ──

    /// <summary>Settings → Account's row: who this phone is signed in as, or the invitation to sign in.</summary>
    public string AccountRowText => Shell.Account?.Account is { } account && Shell.Account.IsSignedIn
        ? Loc.T("Mobile.Settings.SignedInAs", AccountPageViewModel.ShownName(account), AccountPageViewModel.ServerLabel(account))
        : SignInTitle;

    private bool IsSignedIn => Shell.Account is { IsSignedIn: true, Account: not null };

    /// <summary>The card's bold line: the account's name, or the invitation to sign in.</summary>
    public string AccountTitle => IsSignedIn ? AccountPageViewModel.ShownName(Shell.Account!.Account!) : SignInTitle;

    /// <summary>The card's grey line: "Synced with" the computer (the green dot's text), or what
    /// signing in brings; before the first sync, the computer and that it has not synced yet.</summary>
    public string AccountSubtitle => IsSignedIn
        ? Shell.Account!.Account!.LastSyncUtc is null
            ? $"{AccountPageViewModel.ServerLabel(Shell.Account!.Account!)} · {Loc.T("Mobile.Account.NotSynced")}"
            : Loc.T("Mobile.Settings.SyncedWith", AccountPageViewModel.ServerLabel(Shell.Account!.Account!))
        : Loc.T("Mobile.Settings.SyncPitch");

    /// <summary>The green dot before the grey line: signed in and synced at least once.</summary>
    public bool ShowSyncedDot => IsSignedIn && Shell.Account!.Account!.LastSyncUtc != null;

    /// <summary>The profile row as the screen reader says it: who, the computer, and how long ago.</summary>
    public string AccountRowName => IsSignedIn ? $"{AccountRowText}, {AccountPageViewModel.SyncedText(Shell.Account!.Account!.LastSyncUtc, UtcNow())}" : AccountRowText;

    /// <summary>"Synced 5 min ago" goes stale while the sheet stays open: the page re-reads it
    /// once a minute (SettingsPage's ticker, stopped when the page leaves).</summary>
    public void RefreshSyncTime() => OnPropertyChanged(nameof(AccountRowName));

    /// <summary>The desktop's profile picture (a cached local file), or null.</summary>
    public string? AccountAvatarPath => IsSignedIn ? Shell.Account!.AvatarPath : null;

    public bool HasAccountAvatar => !string.IsNullOrEmpty(AccountAvatarPath);

    /// <summary>No picture: initials on a neutral disc ("OW"); signed out, the person glyph.</summary>
    public string AccountInitials => IsSignedIn ? AccountPageViewModel.InitialsOf(AccountTitle) : string.Empty;

    public bool ShowAccountInitials => !HasAccountAvatar && AccountInitials.Length > 0;
    public bool ShowAccountGlyph => !HasAccountAvatar && AccountInitials.Length == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeValue), nameof(ThemeRowName), nameof(MatchDevice))]
    private string _appearance = "System";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeValue), nameof(ThemeRowName), nameof(MatchDeviceFootnote), nameof(ShowsMatchTheme))]
    private string _darkTheme = "Ink";
    [ObservableProperty] private AccentSwatch? _selectedAccent;
    [ObservableProperty] private double _lyricsTextScale = 1.0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportSubtitle), nameof(ExportRowName))]
    private string _exportStatus = string.Empty;

    /// <summary>The initial read of settings.json; tests await it.</summary>
    public Task Loaded { get; private set; } = Task.CompletedTask;

    /// <summary>The tail of the save chain; tests await it.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    /// <summary>settings.json writes so far; tests count them.</summary>
    internal int SaveCount;

    private int _textScaleSaveGeneration;
    private Task _saveChain = Task.CompletedTask;

    public Task LoadAsync() => Loaded = LoadAllAsync();

    private async Task LoadAllAsync()
    {
        await LoadCoreAsync();
        await LoadDownloadSettingsAsync();
        RefreshStats();
    }

    private async Task LoadCoreAsync()
    {
        Noctis.Models.AppSettings? settings = null;
        try
        {
            settings = await Shell.Library.Persistence.LoadSettingsAsync();
        }
        catch (Exception ex)
        {
            // The page keeps its defaults, and later changes still save (each only its own field).
            DebugLog.Write("Settings", $"Load failed: {ex.Message}");
        }
        if (settings != null)
        {
            // A value changed while this load ran (OpenSettings does not await it) is kept.
            var kept = _setBeforeLoad;
            _loading = true;
            try
            {
                if (!kept.HasFlag(SavedFields.Appearance))
                    Appearance = MobileTheme.Appearances.Contains(settings.MobileAppearance) ? settings.MobileAppearance : "System";
                if (!kept.HasFlag(SavedFields.DarkTheme))
                    DarkTheme = PhoneThemes.DarkThemeFor(settings);
                if (!kept.HasFlag(SavedFields.AccentFollows))
                {
                    _accentFollowsTheme = settings.MobileAccentFollowsTheme;
                    OnPropertyChanged(nameof(AccentFollowsTheme));
                    AccentChanged();
                }
                if (!kept.HasFlag(SavedFields.Accent))
                {
                    _accentHex = string.IsNullOrWhiteSpace(settings.AccentColorHex) ? MobileTheme.DefaultAccent : settings.AccentColorHex;
                    _savedAccentName = settings.AccentPresetName;
                    _accentFollowsArtwork = settings.MobileAccentFollowsArtwork;
                    AccentChanged();
                    SelectedAccent = _accentFollowsArtwork
                        ? AccentSwatch.Artwork
                        : Accents.FirstOrDefault(a => string.Equals(a.Hex, _accentHex, StringComparison.OrdinalIgnoreCase));
                }
                if (!kept.HasFlag(SavedFields.TextScale))
                    LyricsTextScale = Math.Clamp(settings.MobileLyricsTextScale, 0.8, 1.6);
                // The host applied it at start (AndroidApp.StartAsync); the page only shows it.
                if (!kept.HasFlag(SavedFields.Language))
                    Language = settings.Language?.Trim() ?? Loc.SystemLanguage;
            }
            finally
            {
                _loading = false;
            }
        }
        _loaded = true;
        if (_setBeforeLoad == SavedFields.None) return;
        // The early change re-themed over the defaults: once more with the loaded rest.
        if ((_setBeforeLoad & ~(SavedFields.TextScale | SavedFields.Language)) != SavedFields.None) ApplyTheme();
        QueueSave(_setBeforeLoad);
    }

    partial void OnAppearanceChanged(string value) => ThemeChanged(SavedFields.Appearance);

    // ── Language ──

    /// <summary>Settings › Language: "" follows the phone's language, else a shipped translation
    /// ("ru"). Saved as settings.Language, the desktop's field; the host applies it at start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageValue), nameof(LanguageRowName))]
    private string _language = Loc.SystemLanguage;

    /// <summary>System language first, then every translation the app ships (Loc.Supported).</summary>
    public IReadOnlyList<LanguageChoice> LanguageChoices => _languageChoices ??= BuildLanguageChoices();
    private IReadOnlyList<LanguageChoice>? _languageChoices;

    /// <summary>The Language page's list, built only once that page is shown. Building it reads
    /// every shipped culture's names (ICU data, about 470 ms cold), and bound to the hidden page
    /// it ran on the frame that first opened Settings, so the sheet rose ~0.5 s late.</summary>
    public IReadOnlyList<LanguageChoice>? LanguageListSource => ShowsLanguagePane ? LanguageChoices : null;

    private IReadOnlyList<LanguageChoice> BuildLanguageChoices()
    {
        var list = new List<LanguageChoice> { new(Loc.SystemLanguage, Loc.T("Settings.Language.System"), NativeName(Loc.Resolve(Loc.SystemLanguage))) };
        foreach (var code in Loc.Supported)
        {
            var english = System.Globalization.CultureInfo.GetCultureInfo(code).EnglishName;
            var native = NativeName(code);
            list.Add(new LanguageChoice(code, native, string.Equals(native, english, StringComparison.OrdinalIgnoreCase) ? string.Empty : english));
        }
        foreach (var choice in list) choice.IsSelected = choice.Code == Language;
        return list;
    }

    /// <summary>A language in its own words, capitalised: "Русский", "Español", "中文（简体）".</summary>
    internal static string NativeName(string code)
    {
        try
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(code);
            var name = culture.NativeName;
            return name.Length == 0 ? code : char.ToUpper(name[0], culture) + name[1..];
        }
        catch (System.Globalization.CultureNotFoundException) { return code; }
    }

    /// <summary>The Language row's value: "System", or the chosen language's own name.</summary>
    public string LanguageValue => string.IsNullOrEmpty(Language) ? Loc.T("Mobile.Language.System") : NativeName(Language);
    public string LanguageRowName => Named(Loc.T("Settings.Language.Title"), LanguageValue);

    [RelayCommand]
    private void ChooseLanguage(LanguageChoice? choice)
    {
        if (choice != null) Language = choice.Code;
    }

    /// <summary>A pick applies at once: the {loc:T} labels re-read on their own, and the shell
    /// re-reads the strings the view models build (<see cref="ShellViewModel.Relabel"/>).</summary>
    partial void OnLanguageChanged(string value)
    {
        if (_languageChoices != null)
            foreach (var choice in _languageChoices) choice.IsSelected = choice.Code == value;
        if (_loading) return;
        var before = Loc.Instance.Culture.Name;
        Loc.Instance.SetCulture(value);
        if (Loc.Instance.Culture.Name != before) Shell.Relabel();
        if (!_loaded) _setBeforeLoad |= SavedFields.Language;
        else QueueSave(SavedFields.Language);
    }

    /// <summary>The language changed: this page, its sub-pages' view models and the language list re-read.</summary>
    public override void Relabel()
    {
        _languageChoices = null;
        foreach (var row in ThemeRows) row.Relabel();
        base.Relabel();
        Background.Relabel();
        Scrobbling.Relabel();
        History.Relabel();
        // The account card's numbers carry the language too ("48 МБ", "5 201" stayed Russian
        // after picking English).
        RefreshStats();
    }
    partial void OnDarkThemeChanged(string value) => ThemeChanged(SavedFields.DarkTheme);

    partial void OnSelectedAccentChanged(AccentSwatch? value)
    {
        // Match Artwork keeps the chosen colour as its fallback; a colour turns it off.
        if (value != null) _accentFollowsArtwork = value.IsArtwork;
        if (value is { IsArtwork: false }) _accentHex = value.Hex;
        var fields = SavedFields.Accent;
        // A swatch picked while the theme's accent showed: the pick is what the user wants to see.
        if (value != null && !_loading && UsesThemeAccent)
        {
            SetProperty(ref _accentFollowsTheme, false, nameof(AccentFollowsTheme));
            fields |= SavedFields.AccentFollows;
        }
        AccentChanged();
        ThemeChanged(fields);
    }

    private void AccentChanged()
    {
        OnPropertyChanged(nameof(AccentName));
        OnPropertyChanged(nameof(FixedAccentName));
        OnPropertyChanged(nameof(AccentRowName));
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(UsesThemeAccent));
        OnPropertyChanged(nameof(UsesArtworkAccent));
        OnPropertyChanged(nameof(AccentFollowsArtwork));
        OnPropertyChanged(nameof(ShownAccent));
    }

    /// <summary>The lyrics follow each step at once; settings.json is written once the slider
    /// settles (<see cref="LyricsPageViewModel.SliderSaveDelay"/>), not once per step of a drag.</summary>
    partial void OnLyricsTextScaleChanged(double value)
    {
        Shell.Lyrics.TextScale = value;
        if (_loading) return;
        if (!_loaded)
        {
            _setBeforeLoad |= SavedFields.TextScale;
            return;
        }
        var generation = ++_textScaleSaveGeneration;
        PendingSave = SaveAfterSliderSettlesAsync(generation);
    }

    private async Task SaveAfterSliderSettlesAsync(int generation)
    {
        await Task.Delay(LyricsPageViewModel.SliderSaveDelay);
        if (generation != Volatile.Read(ref _textScaleSaveGeneration)) return;   // a later step saves
        await QueueSave(SavedFields.TextScale);
    }

    private void ThemeChanged(SavedFields field)
    {
        SyncThemeTicks();
        if ((field & (SavedFields.DarkTheme | SavedFields.Appearance)) != 0) AccentChanged();
        if (_loading || _themeBatch) return;
        ApplyTheme();
        if (!_loaded) _setBeforeLoad |= field;
        else QueueSave(field);
    }

    private void ApplyTheme()
    {
        Shell.Theme?.ApplyTheme(Appearance, DarkTheme, _accentHex, AccentFollowsTheme, _accentFollowsArtwork);
        // After the host: turned on mid-song, the playing cover's colour blends in from the chosen accent.
        Shell.AccentFollowsArtwork = _accentFollowsArtwork;
    }

    /// <summary>The settings.json fields a save writes: only those the user changed.</summary>
    [Flags]
    private enum SavedFields { None = 0, Appearance = 1, DarkTheme = 2, Accent = 4, TextScale = 8, Language = 16, AccentFollows = 32 }

    /// <summary>Saves one after another. Its own chain, not <see cref="PendingSave"/>: a slider's
    /// delayed save is PendingSave while it waits, and must not queue behind itself.</summary>
    private Task QueueSave(SavedFields fields) =>
        PendingSave = _saveChain = _saveChain.ContinueWith(_ => SaveAsync(fields), TaskScheduler.Default).Unwrap();

    private async Task SaveAsync(SavedFields fields)
    {
        try
        {
            // One update: a plain load-then-save let another phone save (a lyrics switch, a pin)
            // land in between, and this stale copy undid it. Only the changed fields: writing all
            // of them put "Ink" over a desktop-only theme ("Gray") when only the text size moved.
            await Shell.Library.Persistence.UpdateSettingsAsync(settings =>
            {
                if (fields.HasFlag(SavedFields.Appearance)) settings.MobileAppearance = Appearance;
                if (fields.HasFlag(SavedFields.DarkTheme))
                {
                    settings.MobileThemeName = DarkTheme;
                    // The desktop shares Theme: only its own four dark styles go there (a colour
                    // theme is the phone's alone, and the desktop keeps what it had).
                    if (MobileTheme.DarkThemes.Contains(DarkTheme))
                    {
                        settings.Theme = DarkTheme;
                        // An explicit pick: the desktop's v1→v2 migration must never rewrite a chosen "Dark".
                        settings.ThemeV2Migrated = true;
                    }
                }
                if (fields.HasFlag(SavedFields.AccentFollows)) settings.MobileAccentFollowsTheme = AccentFollowsTheme;
                if (fields.HasFlag(SavedFields.Accent))
                {
                    settings.AccentColorHex = _accentHex;
                    settings.AccentPresetName = AccentPresetName;
                    settings.MobileAccentFollowsArtwork = _accentFollowsArtwork;
                }
                if (fields.HasFlag(SavedFields.TextScale)) settings.MobileLyricsTextScale = LyricsTextScale;
                if (fields.HasFlag(SavedFields.Language)) settings.Language = Language;
            });
            Interlocked.Increment(ref SaveCount);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Settings", $"Save failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ExportLogsAsync()
    {
        if (Shell.Logs == null)
        {
            ExportStatus = Loc.T("Mobile.Settings.ExportUnavailable");
            return;
        }
        // Redacted: the file leaves the app, and account errors can carry key or password text.
        try
        {
            var saved = await Shell.Logs.ExportAsync($"noctis-log-{DateTime.Now:yyyyMMdd-HHmm}.txt", LogRedactor.Redact(DebugLog.Snapshot()));
            ExportStatus = saved ? Loc.T("Mobile.Settings.LogSaved") : Loc.T("Mobile.Settings.ExportCancelled");
        }
        catch (Exception ex)
        {
            // A failed write said "Export cancelled", as if the user had backed out.
            DebugLog.Write("Settings", $"Log export failed: {ex.Message}");
            ExportStatus = Loc.T("Mobile.Settings.LogFailed");
        }
    }

    /// <summary>"content://…/tree/primary%3AMusic%2FTones" → "Music/Tones": the tree document
    /// id after its volume prefix. Anything else is shown unescaped.</summary>
    internal static string FolderDisplay(string root)
    {
        var text = Uri.UnescapeDataString(root);
        var tree = text.LastIndexOf("/tree/", StringComparison.Ordinal);
        if (tree < 0) return text;
        text = text[(tree + "/tree/".Length)..];
        var colon = text.IndexOf(':');
        return colon >= 0 && colon < text.Length - 1 ? text[(colon + 1)..] : text;
    }

    private void OnFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e) => FoldersChanged();

    private void FoldersChanged()
    {
        OnPropertyChanged(nameof(FolderRows));
        OnPropertyChanged(nameof(FoldersValue));
        OnPropertyChanged(nameof(HasLostFolder));
        OnPropertyChanged(nameof(FoldersRowName));
    }

    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LibraryViewModel.HasFolders):
            case nameof(LibraryViewModel.IsScanning):
            case nameof(LibraryViewModel.IsLoaded):
            case nameof(LibraryViewModel.StatusText):
                OnPropertyChanged(nameof(CanRescan));
                OnPropertyChanged(nameof(RescanFootnote));
                OnPropertyChanged(nameof(FoldersFootnote));
                break;
            case nameof(LibraryViewModel.SongCount):
                RefreshStats();
                break;
            case nameof(LibraryViewModel.LastScanLocal):
                OnPropertyChanged(nameof(RescanSubtitle));
                OnPropertyChanged(nameof(RescanRowName));
                break;
            // A scan stopped on a dead grant: ask each folder again whether it is readable.
            case nameof(LibraryViewModel.NeedsReconnect):
            case nameof(LibraryViewModel.ShowAllMusicLost):
                FoldersChanged();
                break;
            case nameof(LibraryViewModel.AllMusic):
                FoldersChanged();
                OnPropertyChanged(nameof(CanRescan));
                OnPropertyChanged(nameof(RescanFootnote));
                OnPropertyChanged(nameof(FoldersFootnote));
                break;
        }
    }

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricsPageViewModel.ShowTranslation) or nameof(LyricsPageViewModel.ShowRomanization))
        {
            OnPropertyChanged(nameof(LyricsValue));
            OnPropertyChanged(nameof(LyricsRowName));
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.ShowMiniProgressFill) or nameof(ShellViewModel.ShowMiniProgressLine))
        {
            OnPropertyChanged(nameof(PlayerBarValue));
            OnPropertyChanged(nameof(PlayerBarRowName));
        }
        else if (e.PropertyName == nameof(ShellViewModel.SongRowMark))
        {
            OnPropertyChanged(nameof(SongRowsRowName));
        }
        else if (e.PropertyName == nameof(ShellViewModel.NowPlayingArtwork))
        {
            OnPropertyChanged(nameof(NowPlayingArtworkValue));
            OnPropertyChanged(nameof(NowPlayingArtworkRowName));
        }
    }

    /// <summary>A page's summary changed (the Background, Scrobbling and History view models): its row says the new value.</summary>
    private void OnSubPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Summary") return;
        OnPropertyChanged(nameof(BackgroundRowName));
        OnPropertyChanged(nameof(ScrobblingRowName));
        OnPropertyChanged(nameof(HistoryRowName));
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        RefreshStats();   // the downloads' read runs off the UI thread
        Shell.Marshal(() =>
        {
            foreach (var name in new[] { nameof(AccountRowText), nameof(AccountRowName), nameof(AccountTitle), nameof(AccountSubtitle),
                         nameof(ShowSyncedDot), nameof(PickUpFootnote), nameof(ShowPickUp), nameof(AccountAvatarPath), nameof(HasAccountAvatar), nameof(AccountInitials),
                         nameof(ShowAccountInitials), nameof(ShowAccountGlyph) })
                OnPropertyChanged(name);
        });
    }

    /// <summary>Settings' view is the biggest page there is (every pane, the Background,
    /// Scrobbling and History panes included): built on every open it froze the sheet's rise for
    /// 0.7-0.9 s on the emulator (frame stall monitor, 11-06).</summary>
    public override bool KeepsView => true;

    public override void OnClosed()
    {
        // A Remove Folder question does not outlive the sheet (DismissSheet does not close prompts).
        FolderToRemove = null;
        DropQuestion();
        Shell.Library.Folders.CollectionChanged -= OnFoldersChanged;
        Shell.Library.PropertyChanged -= OnLibraryChanged;
        Shell.Library.Refreshed -= OnLibraryRefreshed;
        Shell.Lyrics.PropertyChanged -= OnLyricsChanged;
        Shell.PropertyChanged -= OnShellChanged;
        Background.PropertyChanged -= OnSubPageChanged;
        Background.Detach();
        Scrobbling.PropertyChanged -= OnSubPageChanged;
        Scrobbling.Detach();
        History.PropertyChanged -= OnSubPageChanged;
        if (Shell.Account != null) Shell.Account.StateChanged -= OnAccountChanged;
    }
}
