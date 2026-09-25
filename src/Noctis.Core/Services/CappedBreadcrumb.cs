namespace Noctis.Services;

/// <summary>
/// Developer Mode breadcrumb for a per-file loop: writes "<c>label detail</c>" to the
/// session log for the first <c>cap</c> calls, then one "(further suppressed)" line,
/// then nothing. Thread-safe, so a Parallel.ForEach can share one instance.
/// <para>
/// #97: a startup scan that found new albums ended the process with no managed
/// exception in the log. DebugLog mirrors every line to disk as it is written
/// (CrashJournal), so a line written right before each native file open (TagLib)
/// leaves the files that were in flight as the journal's last lines. Off unless
/// Developer Mode is on (<see cref="DebugLogger.IsEnabled"/>), and capped so a
/// first scan of a large library cannot flush the 500-line ring.
/// </para>
/// </summary>
internal sealed class CappedBreadcrumb
{
    private readonly string _source;
    private readonly string _label;
    private readonly int _cap;
    private readonly Func<bool> _isEnabled;
    private int _count;

    public CappedBreadcrumb(string source, string label, int cap = 50, Func<bool>? isEnabled = null)
    {
        _source = source;
        _label = label;
        _cap = cap;
        _isEnabled = isEnabled ?? (() => DebugLogger.IsEnabled);
    }

    public void Note(string detail)
    {
        if (!_isEnabled()) return;
        var n = Interlocked.Increment(ref _count);
        if (n <= _cap)
            DebugLog.Write(_source, $"{_label} {detail}");
        else if (n == _cap + 1)
            DebugLog.Write(_source, $"{_label} (further suppressed)");
    }
}
