using System.Text;
using Avalonia.Logging;

namespace Noctis.Services;

/// <summary>
/// Mirrors Avalonia's own log stream — Warning and above — into
/// <see cref="DebugLog"/> under a [UI] tag, then hands every call on to the sink
/// LogToTrace installed, unchanged. Binding errors are the payoff: they are the
/// usual trail behind "this control renders weird" reports, and they previously
/// went only to the debugger's trace output, invisible in a user's Copy Logs.
///
/// They also repeat — once per recycled list container — so each distinct
/// message is logged once and the total is capped; past the cap the bridge goes
/// quiet for the session rather than eating the 500-line ring.
/// </summary>
internal sealed class AvaloniaLogBridge : ILogSink
{
    private const int MaxForwarded = 80;
    private const int MaxDistinctTracked = 512;

    private readonly ILogSink? _inner;
    private readonly object _lock = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _forwarded;

    public AvaloniaLogBridge(ILogSink? inner) => _inner = inner;

    public bool IsEnabled(LogEventLevel level, string area)
        => level >= LogEventLevel.Warning || (_inner?.IsEnabled(level, area) ?? false);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        => Log(level, area, source, messageTemplate, Array.Empty<object?>());

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate,
        params object?[] propertyValues)
    {
        if (level >= LogEventLevel.Warning)
            Forward(level, area, source, Format(messageTemplate, propertyValues));

        if (_inner != null && _inner.IsEnabled(level, area))
            _inner.Log(level, area, source, messageTemplate, propertyValues);
    }

    private void Forward(LogEventLevel level, string area, object? source, string message)
    {
        var line = source is null
            ? $"{area} {level}: {message}"
            : $"{area} {level}: {message} [{source.GetType().Name}]";

        bool write, capNotice;
        lock (_lock)
        {
            if (_forwarded >= MaxForwarded) return;
            if (_seen.Count >= MaxDistinctTracked || !_seen.Add(line)) return;
            _forwarded++;
            write = true;
            capNotice = _forwarded == MaxForwarded;
        }

        if (write)
            DebugLog.Write("UI", line);
        if (capNotice)
            DebugLog.Write("UI", $"further Avalonia warnings suppressed after {MaxForwarded} for this session");
    }

    /// <summary>
    /// Fills an Avalonia message template's {Placeholder} slots positionally.
    /// Good enough for log lines; unmatched braces pass through untouched.
    /// </summary>
    internal static string Format(string template, object?[] values)
    {
        if (values.Length == 0 || string.IsNullOrEmpty(template)) return template;

        var sb = new StringBuilder(template.Length + 32);
        var next = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{')
            {
                var close = template.IndexOf('}', i + 1);
                if (close > i + 1)
                {
                    sb.Append(next < values.Length
                        ? values[next++]?.ToString() ?? "(null)"
                        : template[i..(close + 1)]);
                    i = close;
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
