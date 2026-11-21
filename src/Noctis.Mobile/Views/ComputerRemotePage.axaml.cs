using Avalonia.Controls;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// The remote's view-only behaviour: the seek and volume bars send on release only (one command
/// per gesture, not one per pixel), and while a finger is on either the computer's answers leave
/// it alone (<see cref="ComputerRemotePageViewModel.IsSeeking"/>).
/// </summary>
public partial class ComputerRemotePage : UserControl
{
    public ComputerRemotePage()
    {
        InitializeComponent();
        RemoteSeekBar.ScrubStarted += (_, _) => { if (Vm is { } vm) vm.IsSeeking = true; };
        RemoteSeekBar.ScrubCanceled += (_, _) => { if (Vm is { } vm) vm.IsSeeking = false; };
        RemoteSeekBar.ScrubCompleted += (_, _) => _ = Vm?.SeekAsync(RemoteSeekBar.Value);
        RemoteVolume.ScrubStarted += (_, _) => { if (Vm is { } vm) vm.IsChangingVolume = true; };
        RemoteVolume.ScrubCanceled += (_, _) => { if (Vm is { } vm) vm.IsChangingVolume = false; };
        RemoteVolume.ScrubCompleted += (_, _) => _ = Vm?.SetVolumeAsync(RemoteVolume.Value);
    }

    private ComputerRemotePageViewModel? Vm => DataContext as ComputerRemotePageViewModel;
}
