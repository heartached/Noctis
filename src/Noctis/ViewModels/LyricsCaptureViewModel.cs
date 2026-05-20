using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// View model for full-screen Capture Mode. Renders the shared LyricsViewModel
/// line list and mirrors its active-line index. Runs no timer of its own.
/// </summary>
public partial class LyricsCaptureViewModel : ViewModelBase
{
    private readonly PlayerViewModel _player;
    private readonly LyricsViewModel _lyrics;

    public LyricsCaptureViewModel(PlayerViewModel player, LyricsViewModel lyrics)
    {
        _player = player;
        _lyrics = lyrics;
        _activeLineIndex = _lyrics.ActiveLineIndex;
        _lyrics.PropertyChanged += OnLyricsPropertyChanged;
        _player.PropertyChanged += OnPlayerPropertyChanged;
    }

    /// <summary>Raised when the user requests to leave Capture Mode (X button or Esc).</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty]
    private double _fontSize = 72;

    public double MinFontSize => 48;
    public double MaxFontSize => 96;

    /// <summary>All lyric lines for the current track (the shared LyricsViewModel collection).</summary>
    public BulkObservableCollection<LyricLine> LyricLines => _lyrics.LyricLines;

    /// <summary>Index of the active line; mirrors LyricsViewModel.ActiveLineIndex.</summary>
    [ObservableProperty]
    private int _activeLineIndex = -1;

    public IBrush BackgroundBrush => _lyrics.FullBackgroundBrush;
    public Bitmap? AlbumArt => _player.AlbumArt;
    public string TrackTitle => _player.CurrentTrack?.Title ?? string.Empty;
    public string TrackArtist => _player.CurrentTrack?.Artist ?? string.Empty;
    public bool IsPlaying => _player.IsPlaying;

    [RelayCommand]
    private void TogglePlayPause() => _player.PlayPauseCommand.Execute(null);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Snap the font size to the nearest preset (60/72/80) when within 4px.</summary>
    partial void OnFontSizeChanged(double value)
    {
        foreach (var preset in new[] { 60d, 72d, 80d })
        {
            if (Math.Abs(value - preset) <= 4 && value != preset)
            {
                FontSize = preset;
                return;
            }
        }
    }

    private void OnLyricsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.ActiveLineIndex))
            ActiveLineIndex = _lyrics.ActiveLineIndex;
        else if (e.PropertyName == nameof(LyricsViewModel.FullBackgroundBrush))
            OnPropertyChanged(nameof(BackgroundBrush));
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsPlaying))
        {
            OnPropertyChanged(nameof(IsPlaying));
        }
        else if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack)
                 || e.PropertyName == nameof(PlayerViewModel.AlbumArt))
        {
            OnPropertyChanged(nameof(TrackTitle));
            OnPropertyChanged(nameof(TrackArtist));
            OnPropertyChanged(nameof(AlbumArt));
        }
    }
}
