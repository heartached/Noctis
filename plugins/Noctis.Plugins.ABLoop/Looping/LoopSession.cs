using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;

namespace Noctis.Plugins.ABLoop.Looping;

/// <summary>What A-B Loop needs from Noctis: the player plus the song and the settings.
/// <see cref="PluginLoopHost"/> is the real one; tests use a fake.</summary>
public interface ILoopHost : ILoopPlayer
{
    /// <summary>File path of the playing song (the key its loops are saved under), or null.</summary>
    string? TrackPath { get; }
    string TrackTitle { get; }
    string TrackArtist { get; }
    /// <summary>A new song started, or playback was cleared (UI thread).</summary>
    event EventHandler? TrackChanged;

    /// <summary>The "Gap before each repeat" setting.</summary>
    TimeSpan GapSetting { get; }
    /// <summary>The "Keep loops for each song" setting.</summary>
    bool KeepLoops { get; }
    /// <summary>The "Nudge step" setting.</summary>
    TimeSpan NudgeSetting { get; }
    event EventHandler? SettingsChanged;

    /// <summary>The plugin's data folder (loops.json), or null to keep nothing on disk.</summary>
    string? DataDirectory { get; }

    void Log(string message);
}

/// <summary>
/// One A-B Loop for the whole app: the loop keeps running while its window is closed or
/// minimized (a ~15 ms timer, only while a loop is on), and clears when the song changes.
/// </summary>
public sealed class LoopSession : IDisposable
{
    /// <summary>The repeat counts the count button steps through (0 = until turned off).</summary>
    public static readonly int[] Counts = { 0, 2, 3, 5, 10 };
    /// <summary>The gap choices (the setting and the window's gap chip).</summary>
    public static readonly double[] Gaps = { 0, 0.25, 0.5, 1, 1.5, 2 };

    private readonly ILoopHost _host;
    private readonly DispatcherTimer? _timer;
    private bool _disposed;

    public LoopSession(ILoopHost host, Func<TimeSpan>? clock = null, bool useTimer = true)
    {
        _host = host;
        if (clock is null)
        {
            var sw = Stopwatch.StartNew();
            clock = () => sw.Elapsed;
        }
        Engine = new LoopEngine(host, clock) { Gap = host.GapSetting };
        Store = new LoopStore(host.DataDirectory, host.Log) { Persist = host.KeepLoops };
        NudgeStep = host.NudgeSetting;
        TrackPath = host.TrackPath;
        if (useTimer)
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Normal, (_, _) => Tick());
        Engine.Changed += OnEngineChanged;
        Engine.Finished += OnFinished;
        host.TrackChanged += OnTrackChanged;
        host.SettingsChanged += OnSettingsChanged;
    }

    public LoopEngine Engine { get; }
    public LoopStore Store { get; }

    /// <summary>The song the loop belongs to.</summary>
    public string? TrackPath { get; private set; }
    public string TrackTitle => _host.TrackTitle;
    public string TrackArtist => _host.TrackArtist;
    public TimeSpan Duration => _host.Duration;
    public bool HasTrack => !string.IsNullOrEmpty(TrackPath);

    /// <summary>How far the nudge arrows move A or B (Shift: the other step).</summary>
    public TimeSpan NudgeStep { get; set; }

    public IReadOnlyList<SavedLoop> Saved => Store.For(TrackPath);

    /// <summary>True while the loop timer runs.</summary>
    public bool TimerRunning => _timer?.IsEnabled == true;

    /// <summary>Anything the window shows changed (UI thread).</summary>
    public event EventHandler? Changed;

    /// <summary>One timer tick (tests drive it by hand).</summary>
    public void Tick() => Engine.Tick();

    public void ToggleLoop()
    {
        if (!Engine.HasPart) return;
        Engine.Enabled = !Engine.Enabled;
    }

    /// <summary>∞ → x2 → x3 → x5 → x10 → ∞.</summary>
    public void CycleCount()
    {
        var i = Array.IndexOf(Counts, Engine.Count);
        Engine.Count = Counts[(i + 1) % Counts.Length];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string CountText(int count) => count <= 0 ? "∞" : "x" + count.ToString(CultureInfo.InvariantCulture);

    /// <summary>None → 0.25 s → ... → 2 s → None.</summary>
    public void CycleGap()
    {
        var current = Engine.Gap.TotalSeconds;
        var i = Array.FindIndex(Gaps, g => Math.Abs(g - current) < 0.001);
        Engine.Gap = TimeSpan.FromSeconds(Gaps[(i + 1) % Gaps.Length]);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string GapText(TimeSpan gap) => gap <= TimeSpan.Zero ? "No gap" : "Gap " + Seconds(gap.TotalSeconds) + " s";

    public void Nudge(bool a, int direction, bool otherStep = false)
    {
        var step = NudgeStep;
        if (otherStep) step = step >= TimeSpan.FromSeconds(1) ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1);
        Engine.Nudge(a, direction < 0 ? -step : step);
    }

    /// <summary>Saves the current part for this song. Null when there is no part or no song.</summary>
    public SavedLoop? SaveCurrent(string? name = null)
    {
        if (!HasTrack || Engine.A is not { } a || Engine.B is not { } b || !Engine.HasPart) return null;
        var loop = Store.Add(TrackPath!, a, b, name);
        Changed?.Invoke(this, EventArgs.Empty);
        return loop;
    }

    /// <summary>Jumps to a saved loop: its part, looping on, playback at its A.</summary>
    public void Jump(SavedLoop loop)
    {
        if (!Engine.SetPart(loop.Start, loop.End)) return;
        Engine.Enabled = true;
        Engine.JumpToA();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Rename(SavedLoop loop, string name)
    {
        if (!HasTrack) return;
        Store.Rename(TrackPath!, loop, name);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(SavedLoop loop)
    {
        if (!HasTrack) return;
        Store.Remove(TrackPath!, loop);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnTrackChanged(object? sender, EventArgs e)
    {
        var path = _host.TrackPath;
        // The loop belongs to its song: a new song (or none) clears it.
        if (!string.Equals(path, TrackPath, StringComparison.Ordinal) || path is null) Engine.Clear();
        TrackPath = path;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Engine.Gap = _host.GapSetting;
        NudgeStep = _host.NudgeSetting;
        var keep = _host.KeepLoops;
        if (keep && !Store.Persist)
        {
            Store.Persist = true;
            Store.Save();
        }
        Store.Persist = keep;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnEngineChanged(object? sender, EventArgs e)
    {
        UpdateTimer();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnFinished(object? sender, EventArgs e) => UpdateTimer();

    private void UpdateTimer()
    {
        if (_timer is null || _disposed) return;
        var run = Engine.Enabled && Engine.HasPart || Engine.InGap;
        if (run && !_timer.IsEnabled) _timer.Start();
        else if (!run && _timer.IsEnabled) _timer.Stop();
    }

    /// <summary>"1:05.3".</summary>
    public static string Time(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var minutes = (int)t.TotalMinutes;
        var seconds = t.TotalSeconds - minutes * 60;
        // Tenths, never rounded up into "60.0".
        var tenths = Math.Floor(seconds * 10) / 10;
        return minutes.ToString(CultureInfo.InvariantCulture) + ":" + tenths.ToString("00.0", CultureInfo.InvariantCulture);
    }

    private static string Seconds(double s) => s.ToString("0.##", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Stop();
        Engine.Changed -= OnEngineChanged;
        Engine.Finished -= OnFinished;
        _host.TrackChanged -= OnTrackChanged;
        _host.SettingsChanged -= OnSettingsChanged;
        Changed = null;
    }
}
