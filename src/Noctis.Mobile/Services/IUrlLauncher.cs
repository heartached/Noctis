namespace Noctis.Mobile.Services;

/// <summary>Opens a web page in the browser (Android: an ACTION_VIEW intent). False when nothing
/// could open it; never throws.</summary>
public interface IUrlLauncher
{
    bool Open(string url);
}
