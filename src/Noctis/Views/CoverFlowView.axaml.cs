using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Noctis.Views;

public partial class CoverFlowView : UserControl
{
    public CoverFlowView()
    {
        InitializeComponent();
        ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyThemeBlur();
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        ApplyThemeBlur();
    }

    private void ApplyThemeBlur()
    {
        var isLight = ActualThemeVariant == ThemeVariant.Light;

        if (BackgroundArt.Effect is BlurEffect blur)
            blur.Radius = isLight ? 20 : 40;

        BackgroundOverlay.Opacity = isLight ? 0.35 : 0.45;
    }
}
