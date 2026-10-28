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

/// <summary>One open-source component in Settings → About → Open-Source Licenses.</summary>
public sealed record LicenseEntry(string Name, string License, string Detail);

/// <summary>Settings is one sheet: its top level, or one of the pages pushed inside it.</summary>
public enum SettingsPane { Main, Folders, Lyrics, Background, Scrobbling, History, Accent, PlayerBar, Downloads, WhatsNew, Licenses }

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
    // Set once the saved values were read (or the read failed): a change before it is kept
    // and saved after the load instead of being overwritten by it.
    private bool _loaded;
    private SavedFields _setBeforeLoad;
    private string _accentHex = MobileTheme.DefaultAccent;
    // The saved accent's name when it is not one of the nine ("Scarlet", picked on an earlier
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
    public IReadOnlyList<AccentSwatch> AccentChoices => Accents;

    // ── The pages inside the sheet ──

    /// <summary>Which page of the sheet shows; the view slides a pushed one in from the right.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainPane), nameof(IsSubPane), nameof(HeaderTitle))]
    private SettingsPane _pane;

    /// <summary>The pushed page's content, kept while it slides back out after Back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFoldersPane), nameof(ShowsLyricsPane), nameof(ShowsBackgroundPane), nameof(ShowsScrobblingPane),
        nameof(ShowsHistoryPane), nameof(ShowsAccentPane), nameof(ShowsPlayerBarPane), nameof(ShowsDownloadsPane),
        nameof(ShowsWhatsNewPane), nameof(ShowsLicensesPane))]
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

    /// <summary>The header's title: "Settings", or the pushed page's name.</summary>
    public string HeaderTitle => Pane switch
    {
        SettingsPane.Folders => "Music Folders",
        SettingsPane.Lyrics => "Lyrics",
        SettingsPane.Background => "Background",
        SettingsPane.Scrobbling => "Scrobbling",
        SettingsPane.History => "Listening History",
        SettingsPane.Accent => "Accent",
        SettingsPane.PlayerBar => "Player Bar",
        SettingsPane.Downloads => "Downloads",
        SettingsPane.WhatsNew => "What's New",
        SettingsPane.Licenses => "Open-Source Licenses",
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
        ? $"Remove “{folder.Title}”? Its songs leave your library on this phone. The files stay where they are."
        : string.Empty;

    // The question up now (the shell's glass prompt).
    private PromptViewModel? _question;

    /// <summary>Asks on the glass prompt (owner 10-20: no more cards inside the page).</summary>
    [RelayCommand]
    private void AskRemoveFolder(SettingsFolder folder)
    {
        DropQuestion();
        FolderToRemove = folder;
        var question = new PromptViewModel(Shell, $"Remove “{folder.Title}”?",
            "Its songs leave your library on this phone. The files stay where they are.",
            new[] { new PromptAction("Remove Folder", _ => ConfirmRemoveFolderCommand.ExecuteAsync(null), isDestructive: true) },
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

    /// <summary>The Music Folders row as the screen reader says it: the count, then the warning a dot shows.</summary>
    public string FoldersRowName => HasLostFolder
        ? $"Music Folders, {FoldersValue}, a folder lost its access or can't be reached"
        : $"Music Folders, {FoldersValue}";

    /// <summary>Rescan only with a folder to scan and no scan running.</summary>
    public bool CanRescan => Shell.Library.HasFolders && !Shell.Library.IsScanning;

    /// <summary>The grey line in the Rescan Library row: when the last scan finished, else what it does.</summary>
    public string RescanSubtitle => Shell.Library.LastScanLocal is { } scanned
        ? $"Last scan {ScanTime(scanned, UtcNow().ToLocalTime())}"
        : "Looks for new songs in your folders";

    public string RescanRowName => $"Rescan Library, {RescanSubtitle}";

    /// <summary>"today, 10:42", "yesterday, 22:10", "4 Oct, 09:05".</summary>
    internal static string ScanTime(DateTime scanned, DateTime now)
    {
        var time = scanned.ToString("t");
        if (scanned.Date == now.Date) return $"today, {time}";
        if (scanned.Date == now.Date.AddDays(-1)) return $"yesterday, {time}";
        return $"{scanned:d MMM}, {time}";
    }

    /// <summary>Under Rescan Library: the scan's status ("Scanning…", a failure), else why it is off.</summary>
    public string RescanFootnote =>
        !string.IsNullOrEmpty(Shell.Library.StatusText) ? Shell.Library.StatusText
        : Shell.Library.IsLoaded && !Shell.Library.HasFolders ? "Add a music folder to scan it."
        : string.Empty;

    // ── Appearance ──

    /// <summary>The Theme row's value: System, Light or Dark.</summary>
    public string ThemeValue => Appearance;

    /// <summary>A row as the screen reader says it: its label, then its value.</summary>
    private static string Named(string label, string? value) => string.IsNullOrEmpty(value) ? label : $"{label}, {value}";

    public string BackgroundRowName => Named("Background", Background.Summary);
    public string ThemeRowName => Named("Theme", ThemeValue);
    public string AccentRowName => Named("Accent", AccentName);

    /// <summary>The accent's dot on the Accent row.</summary>
    public IBrush AccentBrush => Color.TryParse(_accentHex, out var colour) ? new ImmutableSolidColorBrush(colour) : Accents[0].Brush;

    /// <summary>The accent's preset name ("Crimson"); a saved accent outside the nine keeps the
    /// name it was saved with ("Scarlet"), else "Custom" (a colour picked on the desktop).</summary>
    public string AccentName =>
        Accents.FirstOrDefault(a => string.Equals(a.Hex, _accentHex, StringComparison.OrdinalIgnoreCase))?.Name
        ?? (string.IsNullOrWhiteSpace(_savedAccentName) || Accents.Any(a => a.Name == _savedAccentName) ? "Custom" : _savedAccentName);

    /// <summary>The Player Bar row's value: which progress the mini player shows.</summary>
    public string PlayerBarValue => (Shell.ShowMiniProgressFill, Shell.ShowMiniProgressLine) switch
    {
        (true, true) => "Colour and Line",
        (true, false) => "Progress Colour",
        (false, true) => "Progress Line",
        _ => "Plain",
    };

    public string PlayerBarRowName => Named("Player Bar", PlayerBarValue);

    /// <summary>The Theme row: System / Light / Dark on the glass pop menu (the one ticked is
    /// exposed as selected), then the dark style, which a tap steps to the next.</summary>
    [RelayCommand]
    private void ShowThemeMenu()
    {
        var items = new List<OptionsMenuItem>();
        foreach (var look in new[] { "System", "Light", "Dark" })
            items.Add(new OptionsMenuItem(Shell, look, Appearance == look, () => Appearance = look));
        items.Add(new OptionsMenuItem(Shell, $"Dark style: {DarkTheme}", false, NextDarkTheme) { StartsGroup = true });
        Shell.ShowOptions(OptionsMenuViewModel.ForItems(items));
    }

    private void NextDarkTheme() => DarkTheme = DarkThemes[(DarkThemes.ToList().IndexOf(DarkTheme) + 1) % DarkThemes.Count];

    // ── Lyrics ──

    /// <summary>The Lyrics row's value: which extra lines show.</summary>
    public string LyricsValue => (Shell.Lyrics.ShowTranslation, Shell.Lyrics.ShowRomanization) switch
    {
        (true, true) => "Translations, Romanization",
        (true, false) => "Translations on",
        (false, true) => "Romanization on",
        _ => "Off",
    };

    public string LyricsRowName => Named("Lyrics", LyricsValue);

    // ── Listening ──

    public string ScrobblingRowName => Named("Scrobbling", Scrobbling.Summary);
    public string HistoryRowName => Named("Listening History", History.Summary);

    /// <summary>Under Pick Up on Computer: what the switch does, naming the computer.</summary>
    public string PickUpFootnote => $"When you open Noctis on {(IsSignedIn ? AccountPageViewModel.ServerLabel(Shell.Account!.Account!) : "your computer")}, it offers to carry on from the song you were playing here.";

    // ── Downloads ──

    /// <summary>Settings → Downloads → Download on Wi-Fi Only (on by default): a download asked for on
    /// mobile data is refused (NoctisAccountService.IsOnWifi). Saved at once, on its own, beside the
    /// theme's save chain.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadsValue), nameof(DownloadsRowName))]
    private bool _downloadWifiOnly = true;

    public string DownloadsValue => DownloadWifiOnly ? "Wi-Fi Only" : "Any Network";
    public string DownloadsRowName => Named("Downloads", DownloadsValue);

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
        if (_wifiSetBeforeLoad) return;
        _loading = true;
        try { DownloadWifiOnly = settings.MobileDownloadWifiOnly; }
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
        var question = new PromptViewModel(Shell, "Remove all downloads?",
            $"{StorageUsedText} leave this phone. They stay on your computer and still stream.",
            new[] { new PromptAction("Remove Downloads", _ => ConfirmRemoveDownloadsCommand.ExecuteAsync(null), isDestructive: true) });
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

    /// <summary>Songs, Downloaded and On Phone under the profile. "—" until read: the song count is
    /// the library's; the downloads' count and bytes are summed off the UI thread.</summary>
    [ObservableProperty] private string _songsStat = "—";
    [ObservableProperty] private string _downloadedStat = "—";
    [ObservableProperty] private string _onPhoneStat = "—";

    /// <summary>The last downloads read; tests await it.</summary>
    internal Task StatsLoaded { get; private set; } = Task.CompletedTask;

    private int _statsBusy;
    private volatile bool _statsDirty;

    private void RefreshStats()
    {
        SongsStat = Shell.Library.IsLoaded ? Shell.Library.SongCount.ToString("N0") : "—";
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
        DownloadedStat = count.ToString("N0");
        OnPhoneStat = AccountPageViewModel.FormatBytes(bytes);
        StorageUsedText = $"{count:N0} {(count == 1 ? "song" : "songs")} · {OnPhoneStat}";
        CanRemoveDownloads = Shell.Account != null && count > 0;
    }

    // ── Help ──

    internal const string ReportProblemUrl = "https://github.com/heartached/Noctis/issues/new";
    internal const string WebsiteUrl = "https://noctisapp.cc/";

    [RelayCommand] private void ReportProblem() => Shell.Links?.Open(ReportProblemUrl);
    [RelayCommand] private void OpenWebsite() => Shell.Links?.Open(WebsiteUrl);

    // ── About ──

    /// <summary>"Noctis 1.2.3" → "1.2.3" for the Version row.</summary>
    public string VersionValue => VersionText.StartsWith("Noctis ", StringComparison.Ordinal) ? VersionText["Noctis ".Length..] : VersionText;
    public string VersionRowName => Named("Version", VersionValue);

    /// <summary>What's New: the phone has no release feed, so the page says what this build's Settings holds.</summary>
    public IReadOnlyList<string> WhatsNewItems { get; } = new[]
    {
        "Settings is redesigned: your profile and library numbers on top, then Appearance, Library, Listening, Help and About.",
        "Downloads has its own page, with Download on Wi-Fi Only (on by default) and Remove All Downloads.",
        "Open-Source Licenses lists what Noctis is built with.",
    };

    /// <summary>Open-Source Licenses: the bundled decoder and the main libraries the phone app is built on.</summary>
    public IReadOnlyList<LicenseEntry> Licenses { get; } = new LicenseEntry[]
    {
        new("FFmpeg (libavcodec, libavutil, libswresample)", "LGPL-2.1-or-later",
            "The bundled ALAC decoder, built from the unmodified FFmpeg 6.0 source without GPL or non-free parts: git.ffmpeg.org/ffmpeg.git"),
        new("AndroidX Media3 (ExoPlayer, Session, FFmpeg decoder extension)", "Apache-2.0", "Playback, media session and the decoder's JNI wrapper."),
        new("Avalonia", "MIT", "The user interface framework."),
        new("Inter", "SIL Open Font License 1.1", "The typeface, through Avalonia.Fonts.Inter."),
        new("CommunityToolkit.Mvvm", "MIT", "The view models' plumbing."),
        new("TagLib#", "LGPL-2.1", "Reads song tags and covers."),
        new("Microsoft.Data.Sqlite and SQLitePCLRaw", "MIT and Apache-2.0", "The library's database; SQLite itself is public domain."),
        new("SkiaSharp", "MIT", "Drawing."),
        new("Xamarin.AndroidX bindings", "Apache-2.0", "Android's support libraries for .NET."),
    };

    // ── Logs, version ──

    /// <summary>The Export Logs row's grey line: the last export's outcome, else what it does.</summary>
    public string ExportSubtitle => ExportStatus.Length > 0 ? ExportStatus : "Keys and passwords removed";

    public string ExportRowName => Named("Export Logs", ExportSubtitle);

    public string VersionText => Shell.VersionText;

    // ── The profile card ──

    /// <summary>Settings → Account's row: who this phone is signed in as, or the invitation to sign in.</summary>
    public string AccountRowText => Shell.Account?.Account is { } account && Shell.Account.IsSignedIn
        ? $"Signed in as {AccountPageViewModel.ShownName(account)} · {AccountPageViewModel.ServerLabel(account)}"
        : SignInTitle;

    private bool IsSignedIn => Shell.Account is { IsSignedIn: true, Account: not null };

    /// <summary>The card's bold line: the account's name, or the invitation to sign in.</summary>
    public string AccountTitle => IsSignedIn ? AccountPageViewModel.ShownName(Shell.Account!.Account!) : SignInTitle;

    /// <summary>The card's grey line: "Synced with" the computer (the green dot's text), or what
    /// signing in brings; before the first sync, the computer and that it has not synced yet.</summary>
    public string AccountSubtitle => IsSignedIn
        ? Shell.Account!.Account!.LastSyncUtc is null
            ? $"{AccountPageViewModel.ServerLabel(Shell.Account!.Account!)} · Not synced yet"
            : $"Synced with {AccountPageViewModel.ServerLabel(Shell.Account!.Account!)}"
        : "Sync your library with Noctis on your PC";

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
    [NotifyPropertyChangedFor(nameof(ThemeValue), nameof(ThemeRowName))]
    private string _appearance = "System";
    [ObservableProperty] private string _darkTheme = "Ink";
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
                    DarkTheme = MobileTheme.DarkThemes.Contains(settings.Theme) ? settings.Theme : "Ink";
                if (!kept.HasFlag(SavedFields.Accent))
                {
                    _accentHex = string.IsNullOrWhiteSpace(settings.AccentColorHex) ? MobileTheme.DefaultAccent : settings.AccentColorHex;
                    _savedAccentName = settings.AccentPresetName;
                    AccentChanged();
                    SelectedAccent = Accents.FirstOrDefault(a => string.Equals(a.Hex, _accentHex, StringComparison.OrdinalIgnoreCase));
                }
                if (!kept.HasFlag(SavedFields.TextScale))
                    LyricsTextScale = Math.Clamp(settings.MobileLyricsTextScale, 0.8, 1.6);
            }
            finally
            {
                _loading = false;
            }
        }
        _loaded = true;
        if (_setBeforeLoad == SavedFields.None) return;
        // The early change re-themed over the defaults: once more with the loaded rest.
        if ((_setBeforeLoad & ~SavedFields.TextScale) != SavedFields.None) Shell.Theme?.ApplyTheme(Appearance, DarkTheme, _accentHex);
        QueueSave(_setBeforeLoad);
    }

    partial void OnAppearanceChanged(string value) => ThemeChanged(SavedFields.Appearance);
    partial void OnDarkThemeChanged(string value) => ThemeChanged(SavedFields.DarkTheme);

    partial void OnSelectedAccentChanged(AccentSwatch? value)
    {
        if (value != null) _accentHex = value.Hex;
        AccentChanged();
        ThemeChanged(SavedFields.Accent);
    }

    private void AccentChanged()
    {
        OnPropertyChanged(nameof(AccentName));
        OnPropertyChanged(nameof(AccentRowName));
        OnPropertyChanged(nameof(AccentBrush));
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
        if (_loading) return;
        Shell.Theme?.ApplyTheme(Appearance, DarkTheme, _accentHex);
        if (!_loaded) _setBeforeLoad |= field;
        else QueueSave(field);
    }

    /// <summary>The settings.json fields a save writes: only those the user changed.</summary>
    [Flags]
    private enum SavedFields { None = 0, Appearance = 1, DarkTheme = 2, Accent = 4, TextScale = 8 }

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
                    settings.Theme = DarkTheme;
                    // An explicit pick: the desktop's v1→v2 migration must never rewrite a chosen "Dark".
                    settings.ThemeV2Migrated = true;
                }
                if (fields.HasFlag(SavedFields.Accent))
                {
                    settings.AccentColorHex = _accentHex;
                    settings.AccentPresetName = AccentName;
                }
                if (fields.HasFlag(SavedFields.TextScale)) settings.MobileLyricsTextScale = LyricsTextScale;
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
            ExportStatus = "Export isn't available here";
            return;
        }
        // Redacted: the file leaves the app, and account errors can carry key or password text.
        try
        {
            var saved = await Shell.Logs.ExportAsync($"noctis-log-{DateTime.Now:yyyyMMdd-HHmm}.txt", LogRedactor.Redact(DebugLog.Snapshot()));
            ExportStatus = saved ? "Log saved" : "Export cancelled";
        }
        catch (Exception ex)
        {
            // A failed write said "Export cancelled", as if the user had backed out.
            DebugLog.Write("Settings", $"Log export failed: {ex.Message}");
            ExportStatus = "Couldn't save the log";
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
                FoldersChanged();
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
                         nameof(ShowSyncedDot), nameof(PickUpFootnote), nameof(AccountAvatarPath), nameof(HasAccountAvatar), nameof(AccountInitials),
                         nameof(ShowAccountInitials), nameof(ShowAccountGlyph) })
                OnPropertyChanged(name);
        });
    }

    public override void OnClosed()
    {
        // A Remove Folder question does not outlive the sheet (DismissSheet does not close prompts).
        FolderToRemove = null;
        DropQuestion();
        Shell.Library.Folders.CollectionChanged -= OnFoldersChanged;
        Shell.Library.PropertyChanged -= OnLibraryChanged;
        Shell.Lyrics.PropertyChanged -= OnLyricsChanged;
        Shell.PropertyChanged -= OnShellChanged;
        Background.PropertyChanged -= OnSubPageChanged;
        Background.Detach();
        Scrobbling.PropertyChanged -= OnSubPageChanged;
        History.PropertyChanged -= OnSubPageChanged;
        if (Shell.Account != null) Shell.Account.StateChanged -= OnAccountChanged;
    }
}
