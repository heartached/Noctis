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
        _extract = extract ?? (path => HeroFade
            ? DominantColorExtractor.ExtractHeroBottomColorFromFile(path)
            : DominantColorExtractor.ExtractEdgeBackgroundColorFromFile(path));
        _runBackground = runBackground ?? (work => Task.Run(work));
    }

    /// <summary>
    /// The page continues a full-bleed cover (the album hero): the colour comes from the
    /// cover's bottom rows, which the hero fades into, not its four edges; and the text flips
    /// by WCAG contrast (<see cref="PrefersDarkText"/>) instead of <see cref="LightTintThreshold"/>,
    /// which left white text at 1.8:1 on mid-light tints. Set before <see cref="Load"/>.
    /// </summary>
    public bool HeroFade { get; set; }

    /// <summary>The page background; null (the theme's surface shows through) without a cover.</summary>
    [ObservableProperty] private IBrush? _background;

    [ObservableProperty] private bool _hasTint;
    [ObservableProperty] private bool _isLight;

    /// <summary>The tint itself; null without one.</summary>
    [ObservableProperty] private Color? _tintColor;

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
            TintColor = null;
            HasTint = false;
            IsLight = false;
            return;
        }
        Background = new SolidColorBrush(c);
        TintColor = c;
        IsLight = HeroFade ? PrefersDarkText(c) : DominantColorExtractor.GetRelativeLuminance(c) > LightTintThreshold;
        HasTint = true;
    }

    /// <summary>WCAG 2 contrast ratio of two colours, 1 … 21.</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var la = DominantColorExtractor.GetRelativeLuminance(a);
        var lb = DominantColorExtractor.GetRelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Black text reads at least as well as white on <paramref name="background"/>.
    /// The crossover sits at luminance ~0.18, where both give ~4.6:1, so text in the better of
    /// the two always clears WCAG AA (4.5:1).</summary>
    public static bool PrefersDarkText(Color background) =>
        ContrastRatio(Colors.Black, background) >= ContrastRatio(Colors.White, background);

    /// <summary>Opacity floor of secondary text: as dim as Apple's grey labels when the tint allows.</summary>
    public const double SubtleTextMinOpacity = 0.6;

    /// <summary>
    /// Secondary text on <paramref name="background"/>: <paramref name="text"/> blended toward
    /// the background as far as <see cref="SubtleTextMinOpacity"/>, but no further than keeps
    /// 4.5:1 (full strength on the mid tints where even the primary text only just clears it).
    /// </summary>
    public static Color SubtleTextOn(Color background, Color text)
    {
        for (var a = SubtleTextMinOpacity; a < 1; a += 0.02)
        {
            var blended = Mix(background, text, a);
            if (ContrastRatio(blended, background) >= 4.5) return blended;
        }
        return text;
    }

    /// <summary><paramref name="over"/> at <paramref name="amount"/> over <paramref name="under"/>, opaque.</summary>
    public static Color Mix(Color under, Color over, double amount)
    {
        byte Lerp(byte u, byte o) => (byte)Math.Round(u + (o - u) * amount);
        return Color.FromRgb(Lerp(under.R, over.R), Lerp(under.G, over.G), Lerp(under.B, over.B));
    }
}
