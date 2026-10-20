using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>One accent swatch; the names and colours are the desktop's (App.AccentPresets).</summary>
public sealed record AccentSwatch(string Name, string Hex)
{
    // Immutable: not an AvaloniaObject, so the static Accents list can be built off the UI thread.
    public IBrush Brush { get; } = new ImmutableSolidColorBrush(Color.Parse(Hex));
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

/// <summary>Settings is one sheet: its top level, or one of the pages pushed inside it.</summary>
public enum SettingsPane { Main, Folders, Appearance, Lyrics }

/// <summary>What a Look tile's mini preview paints: a theme's grouped page, its card and its
/// hairline grey (MobileTheme.BuildPalette's PhoneGroupedBackgroundBrush, PhoneCardBrush and
/// PhoneSeparatorBrush; a test keeps the two in step).</summary>
public sealed record LookPreview(IBrush Page, IBrush Card, IBrush Line)
{
    private static LookPreview Of(string page, string card, string line) => new(
        new ImmutableSolidColorBrush(Color.Parse(page)),
        new ImmutableSolidColorBrush(Color.Parse(card)),
        new ImmutableSolidColorBrush(Color.Parse(line)));

    public static LookPreview Light { get; } = Of("#F2F2F7", "#FFFFFF", "#D8D8DC");

    private static readonly LookPreview Ink = Of("#1C1C1E", "#2C2C2E", "#38383A");
    private static readonly LookPreview Smoke = Of("#2A2D38", "#343844", "#3E4250");
    private static readonly LookPreview Midnight = Of("#161D31", "#1E2740", "#2A3552");

    /// <summary>A dark style's preview: Smoke and Midnight have their own greys; Ink and Dark share Apple's.</summary>
    public static LookPreview Dark(string? darkTheme) => darkTheme switch
    {
        "Smoke" => Smoke,
        "Midnight" => Midnight,
        _ => Ink,
    };
}

/// <summary>
/// Phone Settings. Changes apply at once (theme through the host, text size on the lyrics
/// page) and are saved in order through one queued chain, so two quick changes cannot
/// interleave their load-modify-save of settings.json. Gapless playback has no setting here:
/// the phone is always gapless (NowPlayingViewModel), and the saved desktop value is left as is.
/// The top level holds little (the profile, the Look tiles, three rows into the pages, two
/// actions); Music Folders, Appearance and Lyrics are pages pushed inside the sheet
/// (<see cref="Pane"/>), and Back leaves one of them before it closes Settings.
/// </summary>
public sealed partial class SettingsPageViewModel : MobilePage
{
    /// <summary>Nine of the desktop presets, one per hue around the wheel (red, orange, yellow,
    /// green, teal, blue, indigo, purple, pink), Crimson first as the default: five over four.</summary>
    public static IReadOnlyList<AccentSwatch> Accents { get; } = new AccentSwatch[]
    {
        new("Crimson", "#E74856"), new("Tangerine", "#FF8547"), new("Gold", "#F4D24B"), new("Emerald", "#12C76F"),
        new("Teal", "#0FA3B1"), new("Cerulean", "#2A7FCF"), new("Indigo", "#4338CA"), new("Orchid", "#C45CE0"),
        new("Rose", "#E754B5"),
    };

    private const string SignInTitle = "Sign in to your computer";

    private bool _loading;
    private string _accentHex = MobileTheme.DefaultAccent;
    // The saved accent's name when it is not one of the nine ("Scarlet", picked on an earlier
    // phone build or on the desktop): kept, shown and saved back as it was.
    private string? _savedAccentName;

    public SettingsPageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        Shell.Library.Folders.CollectionChanged += OnFoldersChanged;
        Shell.Library.PropertyChanged += OnLibraryChanged;
        Shell.Lyrics.PropertyChanged += OnLyricsChanged;
        if (Shell.Account != null) Shell.Account.StateChanged += OnAccountChanged;
    }

    public ShellViewModel Shell { get; }
    public override string Title => Loc.T("Nav.Settings");

    /// <summary>The clock behind the profile card's "synced 5 min ago"; tests pin it.</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public IReadOnlyList<string> Appearances => MobileTheme.Appearances;
    public IReadOnlyList<string> DarkThemes => MobileTheme.DarkThemes;
    public IReadOnlyList<AccentSwatch> AccentChoices => Accents;

    // ── The pages inside the sheet ──

    /// <summary>Which page of the sheet shows; the view slides a pushed one in from the right.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainPane), nameof(IsSubPane), nameof(HeaderTitle))]
    private SettingsPane _pane;

    /// <summary>The pushed page's content, kept while it slides back out after Back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFoldersPane), nameof(ShowsAppearancePane), nameof(ShowsLyricsPane))]
    private SettingsPane _shownSubPane = SettingsPane.Folders;

    public bool IsMainPane => Pane == SettingsPane.Main;
    public bool IsSubPane => !IsMainPane;
    public bool ShowsFoldersPane => ShownSubPane == SettingsPane.Folders;
    public bool ShowsAppearancePane => ShownSubPane == SettingsPane.Appearance;
    public bool ShowsLyricsPane => ShownSubPane == SettingsPane.Lyrics;

    /// <summary>The header's title: "Settings", or the pushed page's name.</summary>
    public string HeaderTitle => Pane switch
    {
        SettingsPane.Folders => "Music Folders",
        SettingsPane.Appearance => "Appearance",
        SettingsPane.Lyrics => "Lyrics",
        _ => Title,
    };

    partial void OnPaneChanged(SettingsPane value)
    {
        if (value != SettingsPane.Main) ShownSubPane = value;
        else FolderToRemove = null;   // a question left open on Music Folders does not wait there
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
        ? $"Remove “{folder.Title}”? Its songs leave your library on this phone. The files stay where they are."
        : string.Empty;

    [RelayCommand] private void AskRemoveFolder(SettingsFolder folder) => FolderToRemove = folder;

    [RelayCommand] private void CancelRemoveFolder() => FolderToRemove = null;

    /// <summary>Concurrent: it awaits the rescan, and the next folder's Remove stayed greyed out
    /// for all of it (the confirm runs once per ask: <see cref="FolderToRemove"/> clears first).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ConfirmRemoveFolderAsync()
    {
        if (FolderToRemove is not { } folder) return;
        FolderToRemove = null;
        await Shell.Library.RemoveFolderAsync(folder.Root);
    }

    /// <summary>Under Add Folder: why a pick was refused, or the scan's status, else what the
    /// folders are for the app.</summary>
    public string FoldersFootnote => !string.IsNullOrEmpty(Shell.Library.StatusText)
        ? Shell.Library.StatusText
        : "Noctis can read only the folders you choose here.";

    /// <summary>The Music Folders row's value: how many there are.</summary>
    public string FoldersValue => Shell.Library.Folders.Count == 0 ? "None" : Shell.Library.Folders.Count.ToString();

    /// <summary>A folder lost its access, or the last scan could not reach it: the Music Folders
    /// row carries a warning dot.</summary>
    public bool HasLostFolder => FolderRows.Any(f => f.AccessLost || f.Unavailable);

    /// <summary>Rescan only with a folder to scan and no scan running.</summary>
    public bool CanRescan => Shell.Library.HasFolders && !Shell.Library.IsScanning;

    /// <summary>Under Rescan Library: the scan's status ("Scanning…", a failure), else why it is off.</summary>
    public string RescanFootnote =>
        !string.IsNullOrEmpty(Shell.Library.StatusText) ? Shell.Library.StatusText
        : Shell.Library.IsLoaded && !Shell.Library.HasFolders ? "Add a music folder to scan it."
        : string.Empty;

    // ── Appearance ──

    /// <summary>The dark style only matters when the app can go dark: greyed under Light.</summary>
    public bool ShowsDarkThemes => Appearance != "Light";

    /// <summary>The note under Dark Style: when it is used, or why it is greyed.</summary>
    public string DarkStyleFootnote => ShowsDarkThemes
        ? "Used whenever Noctis is dark."
        : "Dark Style applies when Look is System or Dark.";

    /// <summary>The accent's preset name ("Crimson"); a saved accent outside the nine keeps the
    /// name it was saved with ("Scarlet"), else "Custom" (a colour picked on the desktop).</summary>
    public string AccentName =>
        Accents.FirstOrDefault(a => string.Equals(a.Hex, _accentHex, StringComparison.OrdinalIgnoreCase))?.Name
        ?? (string.IsNullOrWhiteSpace(_savedAccentName) || Accents.Any(a => a.Name == _savedAccentName) ? "Custom" : _savedAccentName);

    /// <summary>The Appearance row's value: the dark style where it applies, then the accent ("Ink · Crimson").</summary>
    public string AppearanceValue => ShowsDarkThemes ? $"{DarkTheme} · {AccentName}" : AccentName;

    /// <summary>The Look tiles: which one is chosen.</summary>
    public bool IsSystemLook => Appearance == "System";
    public bool IsLightLook => Appearance == "Light";
    public bool IsDarkLook => Appearance == "Dark";

    public LookPreview LightPreview => LookPreview.Light;

    /// <summary>The Dark tile (and the System tile's dark half), in the chosen dark style.</summary>
    public LookPreview DarkPreview => LookPreview.Dark(DarkTheme);

    [RelayCommand] private void ChooseAppearance(string appearance) => Appearance = appearance;

    // ── Lyrics ──

    /// <summary>The Lyrics row's value: which extra lines show.</summary>
    public string LyricsValue => (Shell.Lyrics.ShowTranslation, Shell.Lyrics.ShowRomanization) switch
    {
        (true, true) => "Translations, Romanization",
        (true, false) => "Translations on",
        (false, true) => "Romanization on",
        _ => "Off",
    };

    // ── Logs, version ──

    /// <summary>Under Export Logs: the last export's outcome, else what it does.</summary>
    public string ExportFootnote => ExportStatus.Length > 0
        ? ExportStatus
        : "Saves the app's log for a bug report, with keys and passwords removed.";

    public string VersionText => Shell.VersionText;

    // ── The profile card ──

    /// <summary>Settings → Account's row: who this phone is signed in as, or the invitation to sign in.</summary>
    public string AccountRowText => Shell.Account?.Account is { } account && Shell.Account.IsSignedIn
        ? $"Signed in as {AccountPageViewModel.ShownName(account)} · {AccountPageViewModel.ServerLabel(account)}"
        : SignInTitle;

    private bool IsSignedIn => Shell.Account is { IsSignedIn: true, Account: not null };

    /// <summary>The card's bold line: the account's name, or the invitation to sign in.</summary>
    public string AccountTitle => IsSignedIn ? AccountPageViewModel.ShownName(Shell.Account!.Account!) : SignInTitle;

    /// <summary>The card's grey line: the computer and when it last synced, or what signing in brings.</summary>
    public string AccountSubtitle => IsSignedIn
        ? $"{AccountPageViewModel.ServerLabel(Shell.Account!.Account!)} · {SyncedText(Shell.Account!.Account!.LastSyncUtc, UtcNow())}"
        : "Sync your library with Noctis on your PC";

    /// <summary>The grey line under the card: what syncing does.</summary>
    public string AccountFootnote => IsSignedIn
        ? "Playlists, favourites and plays stay in step with your computer."
        : "Your computer's songs and playlists on this phone, to stream or download.";

    /// <summary>The desktop's profile picture (a cached local file), or null.</summary>
    public string? AccountAvatarPath => IsSignedIn ? Shell.Account!.AvatarPath : null;

    public bool HasAccountAvatar => !string.IsNullOrEmpty(AccountAvatarPath);

    /// <summary>No picture: initials on a neutral disc ("OW"); signed out, the person glyph.</summary>
    public string AccountInitials => IsSignedIn ? AccountPageViewModel.InitialsOf(AccountTitle) : string.Empty;

    public bool ShowAccountInitials => !HasAccountAvatar && AccountInitials.Length > 0;
    public bool ShowAccountGlyph => !HasAccountAvatar && AccountInitials.Length == 0;

    /// <summary>"synced just now", "synced 5 min ago", "synced 3 hr ago", "synced yesterday",
    /// "synced 4 days ago", "synced 2 Oct"; "not synced yet" before the first sync.</summary>
    internal static string SyncedText(DateTime? syncedUtc, DateTime nowUtc)
    {
        if (syncedUtc is not { } synced) return "not synced yet";
        var ago = nowUtc - synced;
        if (ago < TimeSpan.FromMinutes(1)) return "synced just now";
        if (ago < TimeSpan.FromHours(1)) return $"synced {(int)ago.TotalMinutes} min ago";
        var day = synced.ToLocalTime().Date;
        var today = nowUtc.ToLocalTime().Date;
        if (day == today) return $"synced {(int)ago.TotalHours} hr ago";
        if (day == today.AddDays(-1)) return "synced yesterday";
        if (day > today.AddDays(-7)) return $"synced {(today - day).Days} days ago";
        return $"synced {day:d MMM}";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDarkThemes), nameof(DarkStyleFootnote), nameof(AppearanceValue),
        nameof(IsSystemLook), nameof(IsLightLook), nameof(IsDarkLook))]
    private string _appearance = "System";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppearanceValue), nameof(DarkPreview))]
    private string _darkTheme = "Ink";
    [ObservableProperty] private AccentSwatch? _selectedAccent;
    [ObservableProperty] private double _lyricsTextScale = 1.0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportFootnote))]
    private string _exportStatus = string.Empty;

    /// <summary>The initial read of settings.json; tests await it.</summary>
    public Task Loaded { get; private set; } = Task.CompletedTask;

    /// <summary>The tail of the save chain; tests await it.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    /// <summary>settings.json writes so far; tests count them.</summary>
    internal int SaveCount;

    private int _textScaleSaveGeneration;
    private Task _saveChain = Task.CompletedTask;

    public Task LoadAsync() => Loaded = LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        var settings = await Shell.Library.Persistence.LoadSettingsAsync();
        _loading = true;
        try
        {
            Appearance = MobileTheme.Appearances.Contains(settings.MobileAppearance) ? settings.MobileAppearance : "System";
            DarkTheme = MobileTheme.DarkThemes.Contains(settings.Theme) ? settings.Theme : "Ink";
            _accentHex = string.IsNullOrWhiteSpace(settings.AccentColorHex) ? MobileTheme.DefaultAccent : settings.AccentColorHex;
            _savedAccentName = settings.AccentPresetName;
            AccentChanged();
            SelectedAccent = Accents.FirstOrDefault(a => string.Equals(a.Hex, _accentHex, StringComparison.OrdinalIgnoreCase));
            LyricsTextScale = Math.Clamp(settings.MobileLyricsTextScale, 0.8, 1.6);
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnAppearanceChanged(string value) => ThemeChanged();
    partial void OnDarkThemeChanged(string value) => ThemeChanged();

    partial void OnSelectedAccentChanged(AccentSwatch? value)
    {
        if (value != null) _accentHex = value.Hex;
        AccentChanged();
        ThemeChanged();
    }

    private void AccentChanged()
    {
        OnPropertyChanged(nameof(AccentName));
        OnPropertyChanged(nameof(AppearanceValue));
    }

    /// <summary>The lyrics follow each step at once; settings.json is written once the slider
    /// settles (<see cref="LyricsPageViewModel.SliderSaveDelay"/>), not once per step of a drag.</summary>
    partial void OnLyricsTextScaleChanged(double value)
    {
        Shell.Lyrics.TextScale = value;
        if (_loading) return;
        var generation = ++_textScaleSaveGeneration;
        PendingSave = SaveAfterSliderSettlesAsync(generation);
    }

    private async Task SaveAfterSliderSettlesAsync(int generation)
    {
        await Task.Delay(LyricsPageViewModel.SliderSaveDelay);
        if (generation != Volatile.Read(ref _textScaleSaveGeneration)) return;   // a later step saves
        await QueueSave();
    }

    private void ThemeChanged()
    {
        if (_loading) return;
        Shell.Theme?.ApplyTheme(Appearance, DarkTheme, _accentHex);
        QueueSave();
    }

    /// <summary>Saves one after another. Its own chain, not <see cref="PendingSave"/>: a slider's
    /// delayed save is PendingSave while it waits, and must not queue behind itself.</summary>
    private Task QueueSave() =>
        PendingSave = _saveChain = _saveChain.ContinueWith(_ => SaveAsync(), TaskScheduler.Default).Unwrap();

    private async Task SaveAsync()
    {
        try
        {
            var settings = await Shell.Library.Persistence.LoadSettingsAsync();
            settings.MobileAppearance = Appearance;
            settings.Theme = DarkTheme;
            // An explicit pick: the desktop's v1→v2 migration must never rewrite a chosen "Dark".
            settings.ThemeV2Migrated = true;
            settings.AccentColorHex = _accentHex;
            settings.AccentPresetName = AccentName;
            settings.MobileLyricsTextScale = LyricsTextScale;
            await Shell.Library.Persistence.SaveSettingsAsync(settings);
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
            ExportStatus = "Export isn't available here";
            return;
        }
        // Redacted: the file leaves the app, and account errors can carry key or password text.
        var saved = await Shell.Logs.ExportAsync($"noctis-log-{DateTime.Now:yyyyMMdd-HHmm}.txt", LogRedactor.Redact(DebugLog.Snapshot()));
        ExportStatus = saved ? "Log saved" : "Export cancelled";
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
            // A scan stopped on a dead grant: ask each folder again whether it is readable.
            case nameof(LibraryViewModel.NeedsReconnect):
                FoldersChanged();
                break;
        }
    }

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricsPageViewModel.ShowTranslation) or nameof(LyricsPageViewModel.ShowRomanization))
            OnPropertyChanged(nameof(LyricsValue));
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Shell.Marshal(() =>
    {
        foreach (var name in new[] { nameof(AccountRowText), nameof(AccountTitle), nameof(AccountSubtitle), nameof(AccountFootnote),
                     nameof(AccountAvatarPath), nameof(HasAccountAvatar), nameof(AccountInitials), nameof(ShowAccountInitials), nameof(ShowAccountGlyph) })
            OnPropertyChanged(name);
    });

    public override void OnClosed()
    {
        Shell.Library.Folders.CollectionChanged -= OnFoldersChanged;
        Shell.Library.PropertyChanged -= OnLibraryChanged;
        Shell.Lyrics.PropertyChanged -= OnLyricsChanged;
        if (Shell.Account != null) Shell.Account.StateChanged -= OnAccountChanged;
    }
}
