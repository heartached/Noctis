using Avalonia.Threading;

namespace Noctis.Plugins.AlbumRoulette.Roulette;

/// <summary>
/// Runs the Album of the Day: looks at the clock when the pick hour comes (at most an hour
/// between looks, a one-shot timer, nothing while it waits), picks once per day and says so in
/// a notice. The library is read off the UI thread.
/// </summary>
public sealed class DailyPicker : IDisposable
{
    private readonly IRouletteHost _host;
    private readonly Func<DateTimeOffset> _clock;
    private DispatcherTimer? _timer;
    private bool _busy;
    private bool _disposed;

    public DailyPicker(IRouletteHost host, Func<DateTimeOffset>? clock = null)
    {
        _host = host;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Notices sent (tests).</summary>
    public int Sent { get; private set; }

    /// <summary>When the timer looks next (tests).</summary>
    public TimeSpan? NextCheck => _timer?.IsEnabled == true ? _timer.Interval : null;

    /// <summary>Starts looking (the first look waits a little, so startup is not slowed down).</summary>
    public void Start(TimeSpan? firstLook = null)
    {
        _host.SettingsChanged += OnSettingsChanged;
        Schedule(firstLook ?? TimeSpan.FromSeconds(20));
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Schedule(TimeSpan.FromSeconds(2));

    private void Schedule(TimeSpan wait)
    {
        if (_disposed) return;
        _timer?.Stop();
        if (!_host.AlbumOfTheDayEnabled) return; // nothing to wait for; a settings change starts it again
        _timer ??= new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Interval = wait;
        _timer.Start();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        try { await CheckAsync(); }
        catch (Exception ex) { _host.Log("Album of the Day: " + ex.Message); }
        if (_disposed) return;
        var now = _clock();
        var done = _host.LoadDaily()?.Day == AlbumOfTheDay.DayKey(now);
        Schedule(AlbumOfTheDay.UntilNextCheck(now, _host.DailyHour, done));
    }

    /// <summary>Picks and announces today's album when it is due. Returns the notice, or null.</summary>
    public async Task<string?> CheckAsync()
    {
        if (_disposed || _busy || !_host.AlbumOfTheDayEnabled) return null;
        var now = _clock();
        if (!AlbumOfTheDay.IsDue(now, _host.DailyHour, _host.LoadDaily())) return null;
        _busy = true;
        try
        {
            var albums = await Task.Run(() => AlbumCatalog.Group(_host.LoadTracks()));
            if (_disposed) return null;
            var pick = AlbumOfTheDay.Pick(albums, now, _host.SkipCompilations);
            if (pick is null) return null;
            _host.SaveDaily(new DailyState { Day = AlbumOfTheDay.DayKey(now), AlbumKey = pick.Key, Title = pick.Title });
            var message = AlbumOfTheDay.Message(pick, now.Year);
            _host.Notify(message);
            Sent++;
            return message;
        }
        finally { _busy = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        _host.SettingsChanged -= OnSettingsChanged;
        _timer?.Stop();
        _timer = null;
    }
}
