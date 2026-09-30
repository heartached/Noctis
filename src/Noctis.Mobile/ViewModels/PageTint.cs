using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// The Apple-Music-style page tint the desktop album page uses: the cover's edge colour as a
/// flat background, with the page text flipping dark on light covers. Extraction is a Skia
/// decode, so it runs off the UI thread, and a generation counter drops a result that lands
/// after the page moved to another cover.
/// </summary>
public sealed partial class PageTint : ObservableObject
{
    /// <summary>Luminance above which page text goes dark (the desktop's AlbumDetailViewModel.LightTintThreshold).</summary>
    public const double LightTintThreshold = 0.55;

    private readonly Func<string, Color?> _extract;
    private readonly Func<Func<Color?>, Task<Color?>> _runBackground;
    private int _generation;

    public PageTint(Func<string, Color?>? extract = null, Func<Func<Color?>, Task<Color?>>? runBackground = null)
    {
        _extract = extract ?? (path => DominantColorExtractor.ExtractEdgeBackgroundColorFromFile(path));
        _runBackground = runBackground ?? (work => Task.Run(work));
    }

    /// <summary>The page background; null (the theme's surface shows through) without a cover.</summary>
    [ObservableProperty] private IBrush? _background;

    [ObservableProperty] private bool _hasTint;
    [ObservableProperty] private bool _isLight;

    /// <summary>The latest extraction; tests await it.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    public void Load(string? artworkPath)
    {
        var generation = ++_generation;
        if (string.IsNullOrEmpty(artworkPath) || !File.Exists(artworkPath))
        {
            Apply(null);
            Ready = Task.CompletedTask;
            return;
        }
        Ready = LoadAsync(artworkPath, generation);
    }

    private async Task LoadAsync(string path, int generation)
    {
        Color? color;
        try
        {
            color = await _runBackground(() => _extract(path));
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artwork", $"Page tint failed: {ex.Message}");
            color = null;
        }
        if (generation != _generation) return;   // a newer cover won
        Apply(color);
    }

    public void Apply(Color? color)
    {
        if (color is not { } c)
        {
            Background = null;
            HasTint = false;
            IsLight = false;
            return;
        }
        Background = new SolidColorBrush(c);
        IsLight = DominantColorExtractor.GetRelativeLuminance(c) > LightTintThreshold;
        HasTint = true;
    }
}
