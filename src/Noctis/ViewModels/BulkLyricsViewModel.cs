using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services.Lyrics;

namespace Noctis.ViewModels;

/// <summary>Bulk lyrics over a selection: fetch from LRCLIB and save, or remove what the app wrote.</summary>
public partial class BulkLyricsViewModel : ViewModelBase
{
    private readonly IReadOnlyList<Track> _tracks;
    private readonly ILyricsBulkService _service;
    private CancellationTokenSource? _cts;

    public bool IsRemove { get; }
    public string TitleText { get; }
    public string SubtitleText { get; }
    public string ActionText => IsRemove ? "Remove" : "Fetch";

    public ObservableCollection<Row> Rows { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public bool CanStart => !IsRunning && !IsDone;

    public event EventHandler? Closed;

    public BulkLyricsViewModel(IReadOnlyList<Track> tracks, ILyricsBulkService service, bool remove)
    {
        _tracks = tracks;
        _service = service;
        IsRemove = remove;
        var n = tracks.Count;
        var songs = n == 1 ? "1 song" : $"{n} songs";
        TitleText = remove ? $"Remove lyrics from {songs}" : $"Fetch lyrics for {songs}";
        SubtitleText = remove
            ? "Deletes the lyrics Noctis saved for these songs (its .lrc files and cached lyrics). Lyrics files you made yourself are left alone."
            : "Looks each song up on LRCLIB and saves synced lyrics next to the file. Songs that already have synced lyrics are skipped.";
        foreach (var t in tracks)
            Rows.Add(new Row(t));
        StatusMessage = remove ? "Nothing removed yet." : "Ready.";
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanStart));
    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(CanStart));

    [RelayCommand]
    private async Task Start()
    {
        if (!CanStart) return;
        IsRunning = true;
        Progress = 0;
        StatusMessage = IsRemove ? "Removing…" : "Fetching…";
        _cts = new CancellationTokenSource();
        var progress = new Progress<LyricsBulkProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Progress = p.Total == 0 ? 1 : p.Done / (double)p.Total;
            var row = Rows.FirstOrDefault(r => r.Track.Title == p.CurrentTitle && !r.Done) ?? Rows.FirstOrDefault(r => !r.Done);
            if (row is not null) { row.Status = p.Outcome; row.Done = true; row.Failed = p.Outcome is "failed" or "not found"; }
        }));
        try
        {
            if (IsRemove)
            {
                var removed = await _service.RemoveAsync(_tracks, progress, _cts.Token);
                StatusMessage = removed == 0 ? "No lyrics to remove." : $"Removed lyrics from {removed} song{(removed == 1 ? "" : "s")}.";
            }
            else
            {
                var s = await _service.FetchAsync(_tracks, progress, _cts.Token);
                var parts = new List<string>();
                if (s.Synced > 0) parts.Add($"{s.Synced} synced");
                if (s.PlainOnly > 0) parts.Add($"{s.PlainOnly} plain only");
                if (s.NotFound > 0) parts.Add($"{s.NotFound} not found");
                if (s.Skipped > 0) parts.Add($"{s.Skipped} already had lyrics");
                if (s.Failed > 0) parts.Add($"{s.Failed} failed");
                StatusMessage = _cts.IsCancellationRequested ? "Stopped · " + string.Join(" · ", parts) : "Done · " + string.Join(" · ", parts);
            }
            IsDone = true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Stopped.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed — {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            Progress = 1;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsRunning) { _cts?.Cancel(); return; }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public sealed partial class Row : ObservableObject
    {
        public Row(Track track)
        {
            Track = track;
            Status = string.IsNullOrWhiteSpace(track.SyncedLyrics) ? (string.IsNullOrWhiteSpace(track.Lyrics) ? "no lyrics" : "plain lyrics") : "synced";
        }

        public Track Track { get; }
        public string Title => Track.Title;
        public string Subtitle => Track.ArtistDisplay;
        [ObservableProperty] private string _status = string.Empty;
        [ObservableProperty] private bool _done;
        [ObservableProperty] private bool _failed;
    }
}
