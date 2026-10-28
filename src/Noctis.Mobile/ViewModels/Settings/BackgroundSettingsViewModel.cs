using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Mobile.Services;

namespace Noctis.Mobile.ViewModels.Settings;

/// <summary>One square in Settings › Background's grid: a mesh to pick, its name under it.</summary>
public sealed partial class BackgroundSwatch : ObservableObject
{
    public BackgroundSwatch(string id, string name, IBrush brush)
    {
        Id = id;
        Name = name;
        Brush = brush;
    }

    public string Id { get; }
    public string Name { get; }
    public IBrush Brush { get; }

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
    private readonly bool _light;
    private readonly List<BackgroundSwatch> _all = new();

    public BackgroundSettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _light = Application.Current?.ActualThemeVariant == ThemeVariant.Light;
        Neutrals = BackgroundCatalog.Neutrals.Select(Swatch).ToList();
        Colours = BackgroundCatalog.Colours.Select(Swatch).ToList();
        _all.AddRange(Neutrals);
        _all.AddRange(Colours);
        ArtworkBrush = Paint(BackgroundCatalog.ForCover(shell.Background.Cover, _light));
        shell.Background.ChoiceChanged += OnChoiceChanged;
        SyncSelection();
    }

    private PhoneBackground Background => _shell.Background;

    public IReadOnlyList<BackgroundSwatch> Neutrals { get; }
    public IReadOnlyList<BackgroundSwatch> Colours { get; }

    public int NeutralCount => Neutrals.Count;
    public int ColourCount => Colours.Count;

    /// <summary>The Now Playing card's tile: the playing cover's mesh.</summary>
    public IBrush ArtworkBrush { get; }

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
    public string PreviewTag => "Preview · " + Summary;

    /// <summary>The song the preview's mini player shows: the playing one, or a stand-in.</summary>
    public string PreviewTitle => _shell.Player.CurrentTrack?.Title is { Length: > 0 } t ? t : "Song Title";
    public string PreviewArtist => _shell.Player.CurrentTrack?.Artist is { Length: > 0 } a ? a : "Artist";

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

    /// <summary>The line under Slow Motion and Strength.</summary>
    public string Footnote => Background.BatterySaverOn
        ? "Battery Saver is on, so the colours stay still."
        : "Motion uses a little more battery. It pauses when Battery Saver is on.";

    [RelayCommand]
    private void Pick(BackgroundSwatch? swatch)
    {
        if (swatch != null) Background.Choice = swatch.Id;
    }

    [RelayCommand]
    private void PickArtwork() => Background.Choice = PhoneBackground.Artwork;

    [RelayCommand]
    private void PickOff() => Background.Choice = PhoneBackground.Off;

    private BackgroundSwatch Swatch(BackgroundChoice choice) =>
        new(choice.Id, choice.Name, Paint(BackgroundCatalog.For(choice, _light)));

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
