using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Mobile.Services;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

/// <summary>One square in Settings › Background's grid: a mesh to pick, its name under it.</summary>
public sealed partial class BackgroundSwatch : ObservableObject
{
    internal BackgroundSwatch(BackgroundChoice choice, bool light)
    {
        Choice = choice;
        Id = choice.Id;
        Paint(light);
    }

    internal BackgroundChoice Choice { get; }
    private readonly MeshBrush _mesh = new();

    public string Id { get; }
    public string Name => Choice.Name;

    internal void Relabel() => OnPropertyChanged(nameof(Name));

    /// <summary>The mesh at full strength (its own, so the page's drift never moves it).</summary>
    public IBrush Brush => _mesh.Brush;

    /// <summary>Recolours the same brush for Light or Dark (the theme can change under the pane).</summary>
    internal void Paint(bool light) => _mesh.Set(BackgroundCatalog.For(Choice, light), 1);

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Settings › Background: the pane's view model over <see cref="PhoneBackground"/> (which holds
/// the choice, applies it to the app and saves it). Rows for the sheet and the pane: the current
/// choice's name and swatch (<see cref="Summary"/>, <see cref="SummarySwatch"/>), the swatch
/// grids, Slow Motion and Strength.
/// </summary>
public sealed partial class BackgroundSettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _light;
    private readonly List<BackgroundSwatch> _all = new();
    private readonly MeshBrush _artwork = new();

    public BackgroundSettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _light = IsLight();
        Neutrals = BackgroundCatalog.Neutrals.Select(c => new BackgroundSwatch(c, _light)).ToList();
        Colours = BackgroundCatalog.Colours.Select(c => new BackgroundSwatch(c, _light)).ToList();
        _all.AddRange(Neutrals);
        _all.AddRange(Colours);
        _artwork.Set(BackgroundCatalog.ForCover(shell.Background.Cover, _light), 1);
        shell.Background.ChoiceChanged += OnChoiceChanged;
        // Live: Settings opened in Light, Theme switched to Dark, and every swatch stayed pale.
        if (Application.Current is { } app) app.ActualThemeVariantChanged += OnThemeChanged;
        SyncSelection();
    }

    private static bool IsLight() => Application.Current?.ActualThemeVariant == ThemeVariant.Light;

    /// <summary>The sheet closed: stop following the choice and the theme.</summary>
    internal void Detach()
    {
        _shell.Background.ChoiceChanged -= OnChoiceChanged;
        if (Application.Current is { } app) app.ActualThemeVariantChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        var light = IsLight();
        if (light == _light) return;
        _light = light;
        foreach (var swatch in _all) swatch.Paint(light);
        _artwork.Set(BackgroundCatalog.ForCover(Background.Cover, light), 1);
        OnPropertyChanged(nameof(SummarySwatch));
    }

    private PhoneBackground Background => _shell.Background;

    public IReadOnlyList<BackgroundSwatch> Neutrals { get; }
    public IReadOnlyList<BackgroundSwatch> Colours { get; }

    public int NeutralCount => Neutrals.Count;
    public int ColourCount => Colours.Count;

    /// <summary>The Now Playing card's tile: the playing cover's mesh.</summary>
    public IBrush ArtworkBrush => _artwork.Brush;

    /// <summary>The current choice's name: "Off", "Now Playing" or a swatch's name.</summary>
    public string Summary => Background.Summary;

    /// <summary>The current choice as a small swatch, for the Settings row.</summary>
    public IBrush SummarySwatch => Background.Choice switch
    {
        PhoneBackground.Off => new ImmutableSolidColorBrush(Color.FromArgb(0x59, 0x80, 0x80, 0x80)),
        PhoneBackground.Artwork => Paint(BackgroundCatalog.ForCover(Background.Cover, _light)),
        var id => _all.FirstOrDefault(s => s.Id == id)?.Brush ?? new ImmutableSolidColorBrush(Colors.Gray),
    };

    public bool IsOff => Background.Choice == PhoneBackground.Off;
    public bool IsArtwork => Background.Choice == PhoneBackground.Artwork;

    /// <summary>The preview card's tag: "Preview · Ash".</summary>
    public string PreviewTag => Loc.T("Mobile.Background.PreviewTag", Summary);

    /// <summary>The song the preview's mini player shows: the playing one, or a stand-in.</summary>
    public string PreviewTitle => _shell.Player.CurrentTrack?.Title is { Length: > 0 } t ? t : Loc.T("Mobile.Background.SongTitle");
    public string PreviewArtist => _shell.Player.CurrentTrack?.Artist is { Length: > 0 } a ? a : Loc.T("Mobile.Artist");

    /// <summary>Slow Motion: the colours drift gently.</summary>
    public bool Motion
    {
        get => Background.Motion;
        set => Background.Motion = value;
    }

    /// <summary>How strong the wash is, 0.2 … 1.</summary>
    public double Strength
    {
        get => Background.Strength;
        set => Background.Strength = value;
    }

    public double MinStrength => PhoneBackground.MinStrength;

    /// <summary>The UI language changed: the names and lines re-read.</summary>
    public void Relabel()
    {
        foreach (var swatch in Neutrals.Concat(Colours)) swatch.Relabel();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>The line under Slow Motion and Strength.</summary>
    public string Footnote => Background.BatterySaverOn
        ? Loc.T("Mobile.Background.BatterySaverOn")
        : Loc.T("Mobile.Background.MotionFootnote");

    [RelayCommand]
    private void Pick(BackgroundSwatch? swatch)
    {
        if (swatch != null) Background.Choice = swatch.Id;
    }

    [RelayCommand]
    private void PickArtwork() => Background.Choice = PhoneBackground.Artwork;

    [RelayCommand]
    private void PickOff() => Background.Choice = PhoneBackground.Off;

    /// <summary>A swatch's brush: the mesh at full strength (its own, so the page's drift never moves it).</summary>
    private static IBrush Paint(BackgroundCatalog.Palette palette)
    {
        var mesh = new MeshBrush();
        mesh.Set(palette, 1);
        return mesh.Brush;
    }

    private void OnChoiceChanged(object? sender, EventArgs e)
    {
        SyncSelection();
        OnPropertyChanged(string.Empty);
    }

    private void SyncSelection()
    {
        foreach (var swatch in _all) swatch.IsSelected = swatch.Id == Background.Choice;
    }
}
