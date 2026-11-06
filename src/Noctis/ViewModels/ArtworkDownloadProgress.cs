using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Services.LyricsStudio;

namespace Noctis.ViewModels;

/// <summary>
/// GitHub #185 (owner: "add a downloading bar to the artwork and animated artwork pop-ups"):
/// the bar under the Edit Info chooser while a picked cover or loop downloads. Percent, speed
/// and time left come from <see cref="DownloadMeter"/>, as on the plugin and Lyrics Studio
/// downloads; with no length from the server the bar runs indeterminate and shows the size so
/// far. Owns the download's cancellation: closing the pop-up (or Cancel) stops it.
/// <see cref="Report"/> may be called from any thread; it updates the bar on the thread that
/// called <see cref="Begin"/> (the UI thread in the app), at most ~4 times a second.
/// </summary>
public sealed partial class ArtworkDownloadProgress : ObservableObject
{
    /// <summary>The bar updates at most this often (the plugin rows' pace).</summary>
    internal static readonly TimeSpan PostInterval = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly Func<TimeSpan> _clock;
    private DownloadMeter _meter = new();
    private CancellationTokenSource? _cts;
    private SynchronizationContext? _context;
    private TimeSpan _lastPost;
    private (long Done, long Total) _latest;
    private bool _flushPending;
    private int _run;

    public ArtworkDownloadProgress(Func<TimeSpan>? clock = null)
    {
        if (clock is null)
        {
            var watch = Stopwatch.StartNew();
            clock = () => watch.Elapsed;
        }
        _clock = clock;
    }

    /// <summary>A download is running: the bar shows.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>0–1; never steps back within one download.</summary>
    [ObservableProperty] private double _fraction;

    /// <summary>The server sent no length (yet): the bar runs without a fill.</summary>
    [ObservableProperty] private bool _isIndeterminate = true;

    /// <summary>"42%", or empty while indeterminate.</summary>
    [ObservableProperty] private string _percentText = string.Empty;

    /// <summary>"42% · 4.2 MB/s · about 30 s left"; "12.3 MB · 4.2 MB/s" without a length.</summary>
    [ObservableProperty] private string _detail = string.Empty;

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    /// <summary>Starts a download's bar and returns its token. Any earlier download is cancelled.</summary>
    public CancellationToken Begin()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _context = SynchronizationContext.Current;
            _meter = new DownloadMeter();
            _lastPost = TimeSpan.MinValue;
            _flushPending = false;
            _run++;
        }
        Fraction = 0;
        IsIndeterminate = true;
        PercentText = string.Empty;
        Detail = string.Empty;
        IsActive = true;
        return _cts.Token;
    }

    /// <summary>Stops the running download (the pop-up closed, Cancel, the window closed).</summary>
    public void Cancel()
    {
        lock (_gate) _cts?.Cancel();
    }

    /// <summary>The bar's Cancel button.</summary>
    [RelayCommand]
    private void CancelDownload() => Cancel();

    /// <summary>The download is over (done, failed or cancelled): the bar hides.</summary>
    public void End()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _run++;
        }
        cts?.Dispose();
        IsActive = false;
    }

    /// <summary>Bytes so far and the expected total (0 or less when unknown); the
    /// <c>onProgress</c> callback of <see cref="Services.ITunesArtworkService"/>'s downloads.
    /// A report inside the update pace is held, not dropped: it shows once the pace allows, so a
    /// download that stalls right after it still shows where it got to.</summary>
    public void Report(long done, long total)
    {
        lock (_gate)
        {
            if (_cts is null) return;
            _latest = (done, total);
            var at = _clock();
            var finished = total > 0 && done >= total;
            if (!finished && _lastPost != TimeSpan.MinValue && at - _lastPost < PostInterval)
            {
                if (_flushPending) return;
                _flushPending = true;
                var run = _run;
                var wait = PostInterval - (at - _lastPost);
                _ = Task.Delay(wait).ContinueWith(_ => Flush(run), TaskScheduler.Default);
                return;
            }
            PostLocked(at);
        }
    }

    private void Flush(int run)
    {
        lock (_gate)
        {
            if (run != _run || !_flushPending) return;
            PostLocked(_clock());
        }
    }

    /// <summary>Shows <see cref="_latest"/> on the bar's thread. Under <see cref="_gate"/>.</summary>
    private void PostLocked(TimeSpan at)
    {
        _flushPending = false;
        _lastPost = at;
        var (done, total) = _latest;
        var run = _run;
        var context = _context;

        void Apply()
        {
            if (run != _run) return; // a later download (or End) owns the bar now
            _meter.Sample(new ModelDownloadProgress(ModelDownloadPhase.Downloading, done, Math.Max(0, total)), at);
            var d = _meter.Read(at);
            var known = total > 0;
            Fraction = d.Fraction;
            IsIndeterminate = !known;
            PercentText = known ? DownloadMeter.PercentText(d.Fraction) : string.Empty;
            var first = known ? PercentText : SizeText(done);
            var speed = DownloadMeter.SpeedText(d);
            Detail = speed.Length > 0 ? first + " · " + speed : first;
        }

        if (context is null) Apply();
        else context.Post(_ => Apply(), null);
    }

    /// <summary>"850 KB", "12.3 MB".</summary>
    internal static string SizeText(long bytes)
    {
        var c = System.Globalization.CultureInfo.CurrentCulture;
        var mb = bytes / (double)(1 << 20);
        return mb >= 1 ? mb.ToString("0.0", c) + " MB" : Math.Max(0, bytes / 1024).ToString("0", c) + " KB";
    }
}
