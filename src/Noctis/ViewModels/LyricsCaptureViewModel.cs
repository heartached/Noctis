using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// View model for full-screen Capture Mode. Observes the shared LyricsViewModel
/// sync engine and derives the lines to display. Runs no timer of its own.
/// </summary>
public partial class LyricsCaptureViewModel : ViewModelBase
{
    private readonly PlayerViewModel _player;
    private readonly LyricsViewModel _lyrics;

    public LyricsCaptureViewModel(PlayerViewModel player, LyricsViewModel lyrics)
    {
        _player = player;
        _lyrics = lyrics;
        _lyrics.PropertyChanged += OnLyricsPropertyChanged;
        _player.PropertyChanged += OnPlayerPropertyChanged;
        RefreshLines();
    }

    /// <summary>Raised when the user requests to leave Capture Mode (X button or Esc).</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty]
    private double _fontSize = 72;

    public double MinFontSize => 48;
    public double MaxFontSize => 96;

    [ObservableProperty]
    private LyricLine? _previousLine;

    [ObservableProperty]
    private LyricLine? _currentLine;

    /// <summary>Up to 3 lines after the current one.</summary>
    public ObservableCollection<LyricLine> UpcomingLines { get; } = new();

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
            RefreshLines();
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

    private void RefreshLines()
    {
        var lines = _lyrics.LyricLines;
        var idx = _lyrics.ActiveLineIndex;

        PreviousLine = (idx > 0 && idx - 1 < lines.Count) ? lines[idx - 1] : null;
        CurrentLine = (idx >= 0 && idx < lines.Count) ? lines[idx] : null;

        UpcomingLines.Clear();
        for (int i = idx + 1; i < lines.Count && UpcomingLines.Count < 3; i++)
            UpcomingLines.Add(lines[i]);
    }
}
