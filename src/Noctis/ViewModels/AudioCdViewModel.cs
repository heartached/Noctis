using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services.AudioCd;

namespace Noctis.ViewModels;

/// <summary>
/// The "Audio CD" section: the disc in the drive as a plain track list. Nothing here
/// enters the library — tracks play through the regular queue with a
/// <c>cdda://</c> path the player knows how to open.
/// </summary>
public partial class AudioCdViewModel : ViewModelBase
{
    private readonly IAudioCdService _cd;
    private readonly PlayerViewModel _player;

    public ObservableCollection<Track> Tracks { get; } = new();

    [ObservableProperty] private bool _hasDrive;
    [ObservableProperty] private bool _hasDisc;
    [ObservableProperty] private bool _isReading;
    [ObservableProperty] private string _discTitle = "Audio CD";
    [ObservableProperty] private string _discMeta = "";
    [ObservableProperty] private string _statusText = "";

    /// <summary>Exactly one of the surfaces is visible: the list, or a one-line state.</summary>
    public bool ShowTracks => HasDisc && !IsReading;
    public bool ShowEmptyState => !ShowTracks;

    public AudioCdViewModel(IAudioCdService cd, PlayerViewModel player)
    {
        _cd = cd;
        _player = player;
        _cd.DriveStateChanged += (_, _) => Dispatcher.UIThread.Post(Sync);
        _cd.DiscChanged += (_, _) => Dispatcher.UIThread.Post(Sync);
        Sync();
    }

    /// <summary>Called by the shell every time the section is navigated to.</summary>
    public void OnNavigatedTo()
    {
        Sync();
        // Linux has no cheap "disc present" flag, so the first visit reads the drive.
        if (HasDrive && !HasDisc && !IsReading)
            _ = _cd.RefreshAsync();
    }

    /// <summary>Pull the service state onto the bindable surface. Idempotent.</summary>
    public void Sync()
    {
        HasDrive = _cd.HasDrive;
        IsReading = _cd.IsReading;
        var disc = _cd.CurrentDisc;
        HasDisc = disc != null && _cd.CurrentTracks.Count > 0;

        if (HasDisc)
        {
            var tracks = _cd.CurrentTracks;
            Tracks.Clear();
            foreach (var t in tracks) Tracks.Add(t);
            DiscTitle = tracks[0].Album;
            var total = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
            DiscMeta = $"{tracks[0].AlbumArtist} · {tracks.Count} track{(tracks.Count == 1 ? "" : "s")} · {FormatTotal(total)}";
        }
        else
        {
            Tracks.Clear();
            DiscTitle = "Audio CD";
            DiscMeta = "";
        }

        StatusText = !_cd.IsSupported ? "Audio CDs are not supported on this platform."
            : !HasDrive ? "No optical drive found."
            : IsReading ? "Reading disc…"
            : !HasDisc ? "No audio CD in the drive."
            : "";

        OnPropertyChanged(nameof(ShowTracks));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private static string FormatTotal(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    [RelayCommand]
    private Task Refresh() => _cd.RefreshAsync();

    [RelayCommand]
    private void PlayAll()
    {
        if (Tracks.Count > 0)
            _player.ReplaceQueueAndPlay(Tracks.ToList(), 0);
    }

    [RelayCommand]
    private void PlayTrack(Track track)
    {
        var index = Tracks.IndexOf(track);
        if (index >= 0)
            _player.ReplaceQueueAndPlay(Tracks.ToList(), index);
    }

    [RelayCommand]
    private void PlayTrackNext(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddTrackToQueue(Track track) => _player.AddToQueue(track);
}
