using System.Collections.Specialized;
using Avalonia.Media;
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
    public IBrush Brush { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse(Hex));
}

/// <summary>
/// Phone Settings. Changes apply at once (theme through the host, gapless on the player,
/// text size on the lyrics page) and are saved in order through one queued chain, so two
/// quick changes cannot interleave their load-modify-save of settings.json.
/// </summary>
public sealed partial class SettingsPageViewModel : MobilePage
{
    /// <summary>A phone-sized subset of the desktop presets.</summary>
    public static IReadOnlyList<AccentSwatch> Accents { get; } = new AccentSwatch[]
    {
        new("Crimson", "#E74856"), new("Scarlet", "#FF3B30"), new("Coral", "#FF6F61"), new("Rose", "#E754B5"),
        new("Violet", "#874CF2"), new("Indigo", "#4338CA"), new("Cerulean", "#2A7FCF"), new("Sky", "#39B5F0"),
        new("Teal", "#0FA3B1"), new("Emerald", "#12C76F"), new("Amber", "#FDB84D"), new("Tangerine", "#FF8547"),
        new("Silver", "#B8BCC4"),
    };

    private bool _loading;
    private string _accentHex = MobileTheme.DefaultAccent;

    public SettingsPageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        Shell.Library.Folders.CollectionChanged += OnFoldersChanged;
        if (Shell.Account != null) Shell.Account.StateChanged += OnAccountChanged;
    }

    public ShellViewModel Shell { get; }
    public override string Title => Loc.T("Nav.Settings");

    public IReadOnlyList<string> Appearances => MobileTheme.Appearances;
    public IReadOnlyList<string> DarkThemes => MobileTheme.DarkThemes;
    public IReadOnlyList<AccentSwatch> AccentChoices => Accents;

    /// <summary>The configured roots as the user would name them ("Music/Tones"), not SAF URIs.
    /// A folder whose access was lost says so: its songs stay listed but none of them plays.</summary>
    public IReadOnlyList<string> FolderLabels => Shell.Library.Folders
        .Select(f => Shell.Library.HasAccess(f) ? FolderDisplay(f) : $"{FolderDisplay(f)} (access lost: add it again)")
        .ToList();

    public string VersionText => Shell.VersionText;

    /// <summary>Settings → Account's row: who this phone is signed in as, or the invitation to sign in.</summary>
    public string AccountRowText => Shell.Account?.Account is { } account && Shell.Account.IsSignedIn
        ? $"Signed in as {account.UserName} · {AccountPageViewModel.ServerLabel(account)}"
        : "Sign in to your Noctis desktop";

    [ObservableProperty] private string _appearance = "System";
    [ObservableProperty] private string _darkTheme = "Ink";
    [ObservableProperty] private AccentSwatch? _selectedAccent;
    [ObservableProperty] private bool _gapless = true;
    [ObservableProperty] private double _lyricsTextScale = 1.0;
    [ObservableProperty] private string _exportStatus = string.Empty;

    /// <summary>The initial read of settings.json; tests await it.</summary>
    public Task Loaded { get; private set; } = Task.CompletedTask;

    /// <summary>The tail of the save chain; tests await it.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

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
            SelectedAccent = Accents.FirstOrDefault(a => string.Equals(a.Hex, _accentHex, StringComparison.OrdinalIgnoreCase));
            Gapless = settings.GaplessPlaybackEnabled;
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
        ThemeChanged();
    }

    partial void OnGaplessChanged(bool value)
    {
        if (_loading) return;
        Shell.Player.SetGapless(value);
        QueueSave();
    }

    partial void OnLyricsTextScaleChanged(double value)
    {
        Shell.Lyrics.TextScale = value;
        if (!_loading) QueueSave();
    }

    private void ThemeChanged()
    {
        if (_loading) return;
        Shell.Theme?.ApplyTheme(Appearance, DarkTheme, _accentHex);
        QueueSave();
    }

    private void QueueSave() =>
        PendingSave = PendingSave.ContinueWith(_ => SaveAsync(), TaskScheduler.Default).Unwrap();

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
            settings.AccentPresetName = Accents.FirstOrDefault(a => string.Equals(a.Hex, _accentHex, StringComparison.OrdinalIgnoreCase))?.Name ?? "Custom";
            settings.GaplessPlaybackEnabled = Gapless;
            settings.MobileLyricsTextScale = LyricsTextScale;
            await Shell.Library.Persistence.SaveSettingsAsync(settings);
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

    private void OnFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(FolderLabels));

    private void OnAccountChanged(object? sender, EventArgs e) => Shell.Marshal(() => OnPropertyChanged(nameof(AccountRowText)));

    public override void OnClosed()
    {
        Shell.Library.Folders.CollectionChanged -= OnFoldersChanged;
        if (Shell.Account != null) Shell.Account.StateChanged -= OnAccountChanged;
    }
}
