namespace Noctis.Mobile.Services;

/// <summary>The system audio-output picker (Bluetooth, wired, speaker). Returns false when none could be shown.</summary>
public interface IOutputSwitcher
{
    bool Show();
}
