namespace Noctis.ViewModels;

/// <summary>
/// The Visualizer section: a full-pane live spectrum of what is being heard (Poweramp-style,
/// as asked on Discord), drawn by <c>SpectrumVisualizer</c> over the current cover. The look
/// picker binds the same Settings flags as the lyrics-page visualizer so both surfaces agree
/// and the choice persists.
/// </summary>
public sealed class VisualizerViewModel : ViewModelBase
{
    public VisualizerViewModel(PlayerViewModel player, SettingsViewModel settings)
    {
        Player = player;
        Settings = settings;
    }

    public PlayerViewModel Player { get; }
    public SettingsViewModel Settings { get; }
}
