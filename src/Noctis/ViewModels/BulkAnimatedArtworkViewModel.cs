using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Find Animated Artwork for the library (Settings ▸ Tools; Discord, Cosmo 11-02): one row per
/// album, a run that keeps going in the background when hidden, and a summary at the end.
/// </summary>
public partial class BulkAnimatedArtworkViewModel : ViewModelBase
{
    private readonly IReadOnlyList<IReadOnlyList<Track>> _albums;
    private readonly AnimatedArtworkBulkService _service;
    private readonly Dictionary<Guid, Row> _rowById = new();
    private CancellationTokenSource? _cts;

    public string TitleText { get; }
    public ObservableCollection<Row> Rows { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusMessage = "Ready.";
    /// <summary>Ask again about albums an earlier run found nothing for.</summary>
    [ObservableProperty] private bool _recheck;

    public bool CanStart => !IsRunning && !IsDone && _albums.Count > 0;
    public bool CanChangeOptions => !IsRunning && !IsDone;
    public string CloseText => IsRunning ? "Hide" : "Close";

    public event EventHandler? Closed;

    public BulkAnimatedArtworkViewModel(IReadOnlyList<IReadOnlyList<Track>> albums, AnimatedArtworkBulkService service)
    {
        _albums = albums;
        _service = service;
        var n = albums.Count;
        TitleText = n == 1 ? "Find animated artwork for 1 album" : $"Find animated artwork for {n:N0} albums";
        foreach (var album in albums)
        {
            var row = new Row(album[0], album.Any(t => t.IsExplicit));
            Rows.Add(row);
            _rowById.TryAdd(album[0].AlbumId, row);
        }
        if (n == 0) StatusMessage = "No albums to look up.";
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanChangeOptions));
        OnPropertyChanged(nameof(CloseText));
    }

    partial void OnIsDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanChangeOptions));
    }

    [RelayCommand]
    private async Task Start()
    {
        if (!CanStart) return;
        IsRunning = true;
        Progress = 0;
        StatusMessage = "Looking up";
        _cts = new CancellationTokenSource();
        var progress = new Progress<AnimatedArtworkBulkProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Progress = p.Total == 0 ? 1 : p.Done / (double)p.Total;
            if (_rowById.GetValueOrDefault(p.AlbumId) is { } row)
            {
                row.Status = p.Outcome;
                row.Done = true;
                row.Found = p.Outcome == "animated artwork saved";
            }
            if (IsRunning) StatusMessage = $"Looking up {p.Done:N0} of {p.Total:N0}";
        }));
        try
        {
            // Off the UI thread: the cover checks and the ledger touch the disk.
            var s = await Task.Run(() => _service.RunAsync(_albums, Recheck, progress, _cts.Token));
            StatusMessage = (_cts.IsCancellationRequested ? "Stopped · " : "Done · ") + Summarize(s);
            IsDone = true;
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

    internal static string Summarize(AnimatedArtworkBulkSummary s)
    {
        var parts = new List<string>();
        if (s.Found > 0) parts.Add($"{s.Found:N0} saved");
        if (s.NotFound > 0) parts.Add($"{s.NotFound:N0} with no clear match");
        var had = s.Skipped - s.CheckedBefore;
        if (had > 0) parts.Add($"{had:N0} already had one");
        if (s.CheckedBefore > 0) parts.Add($"{s.CheckedBefore:N0} looked up before");
        if (s.Failed > 0) parts.Add($"{s.Failed:N0} failed");
        return parts.Count == 0 ? "nothing to do" : string.Join(" · ", parts);
    }

    [RelayCommand]
    private void Stop()
    {
        if (IsRunning) _cts?.Cancel();
    }

    /// <summary>Close, or while running hide: the run carries on in the background.</summary>
    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);

    public sealed partial class Row : ObservableObject
    {
        public Row(Track rep, bool isExplicit = false)
        {
            Title = rep.Album;
            IsExplicit = isExplicit;
            Subtitle = AnimatedArtworkBulkService.AlbumArtist(rep);
        }

        public string Title { get; }
        public bool IsExplicit { get; }
        public string Subtitle { get; }
        [ObservableProperty] private string _status = string.Empty;
        [ObservableProperty] private bool _done;
        [ObservableProperty] private bool _found;
    }
}
