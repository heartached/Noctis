using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// Settings › Playlist page › NEW badge: how many days a track added to a playlist keeps its
/// NEW badge (11-23; it was always 7). The playlist rows read <see cref="PlaylistNewBadgeDays"/>
/// through one more binding on the badge's converter, so a change shows at once.
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>One of <see cref="AppSettings.PlaylistNewBadgeDayChoices"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPlaylistNewBadgeDaysOption))]
    [NotifyPropertyChangedFor(nameof(PlaylistNewBadgeTip))]
    private int _playlistNewBadgeDays = 7;

    /// <summary>One length in the picker: "7 days".</summary>
    public sealed record NewBadgeDaysOption(int Days, string Label)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<NewBadgeDaysOption> PlaylistNewBadgeDayOptions
    {
        get => _playlistNewBadgeDayOptions;
        private set => SetProperty(ref _playlistNewBadgeDayOptions, value);
    }
    private IReadOnlyList<NewBadgeDaysOption> _playlistNewBadgeDayOptions = BuildPlaylistNewBadgeDayOptions();

    private static IReadOnlyList<NewBadgeDaysOption> BuildPlaylistNewBadgeDayOptions() =>
        AppSettings.PlaylistNewBadgeDayChoices
            .Select(d => new NewBadgeDaysOption(d, Loc.T($"Settings.NewBadgeDays{d}")))
            .ToList();

    public NewBadgeDaysOption? SelectedPlaylistNewBadgeDaysOption
    {
        get => PlaylistNewBadgeDayOptions.FirstOrDefault(o => o.Days == PlaylistNewBadgeDays);
        set
        {
            // A ComboBox nulls its selection while items are rebuilt; ignore that, keep the state.
            if (value is null) return;
            PlaylistNewBadgeDays = value.Days;
        }
    }

    /// <summary>The badge's tooltip: "Added in the last 7 days".</summary>
    public string PlaylistNewBadgeTip => Loc.T("Playlist.AddedInLastTip",
        SelectedPlaylistNewBadgeDaysOption?.Label ?? Loc.T("Settings.NewBadgeDays7"));

    partial void OnPlaylistNewBadgeDaysChanged(int value)
    {
        if (_settingsLoaded) _ = SaveAsync();
    }

    /// <summary>The UI language changed: the lengths and the tooltip read in it.</summary>
    private void RelabelPlaylistNewBadgeDays()
    {
        PlaylistNewBadgeDayOptions = BuildPlaylistNewBadgeDayOptions();
        OnPropertyChanged(nameof(SelectedPlaylistNewBadgeDaysOption));
        OnPropertyChanged(nameof(PlaylistNewBadgeTip));
    }
}
