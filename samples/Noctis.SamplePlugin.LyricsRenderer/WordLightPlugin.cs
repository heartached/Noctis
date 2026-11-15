using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Noctis.Plugins;

namespace Noctis.SamplePlugin.LyricsRenderer;

/// <summary>
/// Reference plugin for API 1.8: a lyrics renderer. Noctis keeps the list (layout, scrolling,
/// which line is active, click-to-seek); this plugin only measures and draws the inside of each
/// line. Sung words are drawn in the text colour, the word being sung in the accent colour, and
/// the words still to come dimmed.
/// </summary>
public sealed class WordLightPlugin : INoctisPlugin
{
    private IDisposable? _registration;

    public PluginInfo Info { get; } = new(
        Id: "dev.noctis.samples.lyricsrenderer",
        Name: "Word Light",
        Version: "1.0.0",
        Author: "Noctis",
        Description: "Reference for plugin API 1.8: a lyrics renderer.");

    public void Initialize(IPluginHost host)
    {
        // Registering never switches to the renderer: the user picks it in
        // Settings → Lyrics → Lyrics Renderer. Keep the registration to undo it on Shutdown.
        _registration = host.RegisterLyricsRenderer(new WordLightRenderer());
        host.Log("Word Light renderer registered");
    }

    public void Shutdown()
    {
        _registration?.Dispose();
        _registration = null;
    }
}

internal sealed class WordLightRenderer : ILyricsRenderer
{
    public string Name => "Word Light";

    // Leave the "…" of an intro or an instrumental gap to Noctis.
    public ILyricsLineView? CreateLine(LyricsLineInfo line) => line.IsInterlude ? null : new WordLightLine(line);
}

internal sealed class WordLightLine(LyricsLineInfo line) : ILyricsLineView
{
    private FormattedText? _text;
    private IBrush _sung = Brushes.White, _current = Brushes.White, _ahead = Brushes.Gray;
    // What the text is painted for right now, so Render only repaints the spans that moved.
    private int _paintedSung = -1, _paintedCurrent = -1;

    public Size Measure(double availableWidth, in LyricsLineStyle style)
    {
        // Measure runs only when the width, the font or a colour changes: build everything
        // Render needs here, so Render itself allocates nothing.
        _sung = new ImmutableSolidColorBrush(style.Foreground);
        _current = new ImmutableSolidColorBrush(style.Accent);
        _ahead = new ImmutableSolidColorBrush(style.Foreground, 0.35);
        _text = new FormattedText(
            line.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(style.FontFamily, FontStyle.Normal, FontWeight.Bold),
            Math.Max(1, style.FontSize), _ahead)
        {
            MaxTextWidth = Math.Max(1, availableWidth),
        };
        _paintedSung = _paintedCurrent = -1;
        return new Size(Math.Min(availableWidth, _text.WidthIncludingTrailingWhitespace), _text.Height);
    }

    public bool Render(DrawingContext context, Size size, in LyricsLineFrame frame)
    {
        if (_text is null) return false;

        // How many characters are sung, and how long the word being sung is.
        var length = line.Text.Length;
        int sung, current = 0;
        switch (frame.State)
        {
            case LyricsLineState.Upcoming:
                sung = 0;
                break;
            case LyricsLineState.Active when line.Words.Count > 0:
                sung = 0;
                foreach (var word in line.Words)
                {
                    if (frame.PositionMs < word.StartMs) break;
                    var ended = word.EndMs is { } end && frame.PositionMs >= end;
                    if (ended) sung += word.Text.Length;
                    else { current = word.Text.Length; break; }
                }
                break;
            default: // Past, Unsynced, or an active line timed only as a whole
                sung = length;
                break;
        }
        sung = Math.Min(sung, length);
        current = Math.Min(current, length - sung);

        if (sung != _paintedSung || current != _paintedCurrent)
        {
            _text.SetForegroundBrush(_ahead, 0, length);
            if (sung > 0) _text.SetForegroundBrush(_sung, 0, sung);
            if (current > 0) _text.SetForegroundBrush(_current, sung, current);
            _paintedSung = sung;
            _paintedCurrent = current;
        }

        context.DrawText(_text, default);

        // Ask for another frame only while words are still lighting up.
        return frame.State == LyricsLineState.Active && frame.IsPlaying && line.Words.Count > 0 && sung < length;
    }
}
