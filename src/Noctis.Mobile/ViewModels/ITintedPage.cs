namespace Noctis.Mobile.ViewModels;

/// <summary>A pushed page painted in its cover's colour (album, artist): the shell fades the
/// bottom of the screen into that colour instead of the theme's.</summary>
public interface ITintedPage
{
    PageTint Tint { get; }
}
