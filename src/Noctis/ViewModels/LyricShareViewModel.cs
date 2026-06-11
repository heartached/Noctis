using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>A lyric line the user can include on the share card.</summary>
public partial class SelectableLyricLine : ObservableObject
{
    public SelectableLyricLine(string text) => Text = text;

    public string Text { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Drives the lyric share-card dialog: line selection, format toggle,
/// live preview, and PNG export.
/// </summary>
public partial class LyricShareViewModel : ViewModelBase
{
    /// <summary>Spotify caps at ~5; we allow a little more before the card gets cramped.</summary>
    public const int MaxLines = 8;

    private readonly Track _track;
    private int _renderGeneration;

    public ObservableCollection<SelectableLyricLine> Lines { get; } = new();

    [ObservableProperty] private bool _isSquare = true;
    [ObservableProperty] private bool _isStory;
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>Last rendered PNG — what Save/Copy exports.</summary>
    public byte[]? CurrentPng { get; private set; }

    public string TrackTitle => _track.Title;
    public string TrackArtist => _track.ArtistDisplay;

    /// <summary>Suggested file name for the save dialog.</summary>
    public string SuggestedFileName
    {
        get
        {
            var name = $"{_track.Artist} - {_track.Title} lyrics";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name + ".png";
        }
    }

    public LyricShareViewModel(Track track, IReadOnlyList<string> lines, int preselectIndex = 0)
    {
        _track = track;
        foreach (var text in lines)
            Lines.Add(new SelectableLyricLine(text));

        // Pre-select the active line and the next few, like Spotify does.
        if (Lines.Count > 0)
        {
            int start = Math.Clamp(preselectIndex, 0, Lines.Count - 1);
            for (int i = start; i < Math.Min(start + 4, Lines.Count); i++)
                Lines[i].IsSelected = true;
        }

        foreach (var line in Lines)
            line.PropertyChanged += OnLineToggled;

        RefreshPreview();
    }

    private void OnLineToggled(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableLyricLine.IsSelected))
            return;

        if (sender is SelectableLyricLine line && line.IsSelected &&
            Lines.Count(l => l.IsSelected) > MaxLines)
        {
            // Revert the toggle that exceeded the cap.
            line.IsSelected = false;
            StatusText = $"Up to {MaxLines} lines";
            return;
        }

        StatusText = string.Empty;
        RefreshPreview();
    }

    partial void OnIsSquareChanged(bool value)
    {
        if (value) { IsStory = false; RefreshPreview(); }
    }

    partial void OnIsStoryChanged(bool value)
    {
        if (value) { IsSquare = false; RefreshPreview(); }
    }

    [RelayCommand]
    private void SelectSquare() => IsSquare = true;

    [RelayCommand]
    private void SelectStory() => IsStory = true;

    private void RefreshPreview()
    {
        var generation = ++_renderGeneration;
        var selected = Lines.Where(l => l.IsSelected).Select(l => l.Text).ToList();
        if (selected.Count == 0)
        {
            CurrentPng = null;
            Preview = null;
            return;
        }

        var spec = new LyricCardSpec
        {
            Title = _track.Title,
            Artist = _track.ArtistDisplay,
            ArtworkPath = _track.AlbumArtworkPath,
            Lines = selected,
            Format = IsStory ? ShareCardFormat.Story : ShareCardFormat.Square,
        };

        Task.Run(() =>
        {
            try
            {
                var png = ShareCardRenderer.RenderLyricCard(spec);
                using var ms = new MemoryStream(png);
                var bitmap = new Bitmap(ms);
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _renderGeneration)
                    {
                        bitmap.Dispose();
                        return;
                    }
                    var old = Preview;
                    CurrentPng = png;
                    Preview = bitmap;
                    old?.Dispose();
                });
            }
            catch (Exception ex)
            {
                DebugLogger.Log(DebugLogger.Category.Lyrics, DebugLogger.Level.Error,
                    "Share card render failed", ex.Message);
            }
        });
    }

    public void ReportStatus(string message) => StatusText = message;
}
