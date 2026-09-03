using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Helpers;

/// <summary>
/// One shared "Spectrogram" command for every track context menu. Self-contained (like
/// the Open-with item): the action is a pure function of the track, so no view has to
/// supply a command. Accepts a <see cref="Track"/>, a <see cref="TopSongRow"/> or a
/// <see cref="FavoriteItem"/> as the parameter.
/// </summary>
public static class SpectrogramLauncher
{
    public static ICommand OpenCommand { get; } = new AsyncRelayCommand<object?>(OpenAsync);

    private static Task OpenAsync(object? parameter)
    {
        var track = parameter switch
        {
            Track t => t,
            TopSongRow r => r.Track,
            FavoriteItem f => f.Track,
            _ => null,
        };
        return track == null ? Task.CompletedTask : MetadataHelper.OpenSpectrogramWindow(track);
    }
}
