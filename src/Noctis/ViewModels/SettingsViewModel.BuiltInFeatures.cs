using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Localization;

namespace Noctis.ViewModels;

/// <summary>
/// Settings › Plugins › Built-in Features (Discord, Mistery 11-02): the parts of Noctis a
/// user may not want, each behind one switch as if it were an "enabled by default" plugin.
/// Where a setting already existed the row is that setting (Discord, scrobbling, Inbox,
/// animated artwork, NEW badge); the four new switches live here. Off removes the
/// feature's entry points and stops its work; on brings it back as it was.
/// </summary>
public partial class SettingsViewModel
{
    // ── New switches (AppSettings, all on by default) ──

    /// <summary>The mini player (island button, album-art click, shortcut).</summary>
    [ObservableProperty] private bool _miniPlayerEnabled = true;

    /// <summary>The album carousel and its cases (Albums page, artist page Albums tab).</summary>
    [ObservableProperty] private bool _albumCarouselEnabled = true;

    /// <summary>The audio visualizer: the Visualizer page and the lyrics-page visualizer.</summary>
    [ObservableProperty] private bool _visualizerEnabled = true;

    /// <summary>The Featured Artists / Suggested rail on a playlist page (Discord, DendyAly).</summary>
    [ObservableProperty] private bool _playlistShowSideRail = true;

    partial void OnMiniPlayerEnabledChanged(bool value)
    {
        ApplyPlayerSettings(); // the island button
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnAlbumCarouselEnabledChanged(bool value)
    {
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnVisualizerEnabledChanged(bool value)
    {
        ApplyPlayerSettings(); // the lyrics-page visualizer
        if (_settingsLoaded) _ = SaveAsync();
    }

    partial void OnPlaylistShowSideRailChanged(bool value)
    {
        if (_settingsLoaded) _ = SaveAsync();
    }

    /// <summary>Scrobbling as one switch over the Last.fm and ListenBrainz ones: off turns both
    /// off, on turns on each service that has an account connected.</summary>
    public bool ScrobblingFeatureEnabled
    {
        get => IsAnyScrobblingEnabled;
        set
        {
            if (value == IsAnyScrobblingEnabled) return;
            if (!value)
            {
                LastFmScrobblingEnabled = false;
                ListenBrainzScrobblingEnabled = false;
            }
            else
            {
                if (IsLastFmConnected) LastFmScrobblingEnabled = true;
                if (IsListenBrainzConnected) ListenBrainzScrobblingEnabled = true;
            }
            OnPropertyChanged();
        }
    }

    /// <summary>A Last.fm or ListenBrainz account is connected, so the switch has something to turn on.</summary>
    public bool HasScrobblingAccount => IsLastFmConnected || IsListenBrainzConnected;

    // ── The card's rows ──

    private IReadOnlyList<BuiltInFeatureRow>? _builtInFeatures;

    /// <summary>The Built-in Features rows, in the order the card shows them.</summary>
    public IReadOnlyList<BuiltInFeatureRow> BuiltInFeatures => _builtInFeatures ??= CreateBuiltInFeatures();

    /// <summary>The card hides while the Plugins search box has text: it searches plugins.</summary>
    public bool ShowBuiltInFeatures => !IsSearchingPlugins;

    private IReadOnlyList<BuiltInFeatureRow> CreateBuiltInFeatures()
    {
        var rows = new[]
        {
            new BuiltInFeatureRow("MiniPlayer", "MiniPlayerIcon", () => MiniPlayerEnabled, v => MiniPlayerEnabled = v,
                nameof(MiniPlayerEnabled)),
            new BuiltInFeatureRow("AnimatedArtwork", "CoverFlowIcon", () => EnableAnimatedCovers, v => EnableAnimatedCovers = v,
                nameof(EnableAnimatedCovers)),
            new BuiltInFeatureRow("AlbumCarousel", "AlbumCarouselIcon", () => AlbumCarouselEnabled, v => AlbumCarouselEnabled = v,
                nameof(AlbumCarouselEnabled)),
            new BuiltInFeatureRow("Visualizer", "SidebarVisualizerIcon", () => VisualizerEnabled, v => VisualizerEnabled = v,
                nameof(VisualizerEnabled)),
            new BuiltInFeatureRow("NewBadge", "StarIcon", () => PlaylistShowNewBadge, v => PlaylistShowNewBadge = v,
                nameof(PlaylistShowNewBadge)),
            new BuiltInFeatureRow("Inbox", "SidebarInboxIcon", () => InboxEnabled, v => InboxEnabled = v,
                nameof(InboxEnabled)),
            new BuiltInFeatureRow("Discord", "DiscordIcon", () => DiscordRichPresenceEnabled, v => DiscordRichPresenceEnabled = v,
                nameof(DiscordRichPresenceEnabled)),
            new BuiltInFeatureRow("Scrobbling", "StatisticsIcon", () => ScrobblingFeatureEnabled, v => ScrobblingFeatureEnabled = v,
                nameof(LastFmScrobblingEnabled), nameof(ListenBrainzScrobblingEnabled), nameof(IsLastFmConnected), nameof(IsListenBrainzConnected))
            {
                CanToggleProvider = () => HasScrobblingAccount || IsAnyScrobblingEnabled,
                UnavailableLineKey = "Features.Scrobbling.NeedsAccount",
            },
            new BuiltInFeatureRow("PlaylistSideRail", "PlaylistsIcon", () => PlaylistShowSideRail, v => PlaylistShowSideRail = v,
                nameof(PlaylistShowSideRail)),
        };
        PropertyChanged += OnBuiltInFeatureSourceChanged;
        return rows;
    }

    private void OnBuiltInFeatureSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginSearchText))
        {
            OnPropertyChanged(nameof(ShowBuiltInFeatures));
            return;
        }
        if (e.PropertyName is nameof(LastFmScrobblingEnabled) or nameof(ListenBrainzScrobblingEnabled))
            OnPropertyChanged(nameof(ScrobblingFeatureEnabled));
        if (e.PropertyName is nameof(IsLastFmConnected) or nameof(IsListenBrainzConnected))
            OnPropertyChanged(nameof(HasScrobblingAccount));
        if (_builtInFeatures is null || e.PropertyName is null) return;
        foreach (var row in _builtInFeatures)
            if (row.Watches(e.PropertyName)) row.Refresh();
    }

    // ── Load / save hooks (called from the main file next to the feature-batch ones) ──

    private void LoadBuiltInFeatureSettings()
    {
        MiniPlayerEnabled = _settings.MiniPlayerEnabled;
        AlbumCarouselEnabled = _settings.AlbumCarouselEnabled;
        VisualizerEnabled = _settings.VisualizerEnabled;
        PlaylistShowSideRail = _settings.PlaylistShowSideRail;
        LoadAnimatedArtworkCacheSettings();
    }

    private void SaveBuiltInFeatureSettings()
    {
        _settings.MiniPlayerEnabled = MiniPlayerEnabled;
        _settings.AlbumCarouselEnabled = AlbumCarouselEnabled;
        _settings.VisualizerEnabled = VisualizerEnabled;
        _settings.PlaylistShowSideRail = PlaylistShowSideRail;
        SaveAnimatedArtworkCacheSettings();
    }
}

/// <summary>One row of the Built-in Features card: icon, name, one line, switch. The switch
/// reads and writes the feature's own setting on <see cref="SettingsViewModel"/>.</summary>
public sealed class BuiltInFeatureRow : ObservableObject
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private readonly HashSet<string> _watched;

    public BuiltInFeatureRow(string key, string iconKey, Func<bool> get, Action<bool> set, params string[] watched)
    {
        Key = key;
        IconKey = iconKey;
        _get = get;
        _set = set;
        _watched = new HashSet<string>(watched, StringComparer.Ordinal);
        Loc.Instance.CultureChanged += (_, _) => { OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Line)); };
    }

    /// <summary>Stable id ("MiniPlayer", "Discord", …); the resx keys are Features.{Key}.Title / .Line.</summary>
    public string Key { get; }

    /// <summary>StreamGeometry resource key (Noctis.UI Assets/Icons.axaml).</summary>
    public string IconKey { get; }

    public string Title => Loc.T($"Features.{Key}.Title");

    public string Line => CanToggle || UnavailableLineKey is null ? Loc.T($"Features.{Key}.Line") : Loc.T(UnavailableLineKey);

    /// <summary>Set when the switch only works after setup (scrobbling needs an account).</summary>
    internal Func<bool>? CanToggleProvider { get; init; }

    internal string? UnavailableLineKey { get; init; }

    public bool CanToggle => CanToggleProvider?.Invoke() ?? true;

    public bool IsOn
    {
        get => _get();
        set
        {
            if (value != _get()) _set(value);
            // Raised even when the setter refused or reverted (Discord not running), so the
            // switch falls back to the real state instead of showing a value nobody holds.
            OnPropertyChanged();
        }
    }

    internal bool Watches(string propertyName) => _watched.Contains(propertyName);

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(Line));
    }
}
