using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Mobile.Views;

/// <summary>
/// The album page's folded description, shared with the artist page's biography: the text's
/// first <see cref="Lines"/> lines with MORE over the end of the last (Apple's), the whole text
/// on the glass sheet on a tap. Works on the page's own named controls: the full text (arranged
/// at its full height inside the clip), the clip, the folded stand-in (head and tail) and MORE.
/// </summary>
internal sealed class DescriptionFold
{
    private readonly TextBlock _text, _head, _tail;
    private readonly Border _clip, _more;
    private readonly Control _folded;

    /// <summary>Whether the text is long enough to fold at the current width.</summary>
    private bool _overflows;

    public DescriptionFold(TextBlock text, Border clip, Control folded, TextBlock head, TextBlock tail, Border more, int lines)
    {
        _text = text;
        _clip = clip;
        _folded = folded;
        _head = head;
        _tail = tail;
        _more = more;
        Lines = lines;
        _tail.TextTrimming = UnmarkedTrimming.Instance;
        _text.SizeChanged += (_, _) => Measure();
        _more.SizeChanged += (_, _) => UpdateTextFade();
    }

    /// <summary>
    /// The text itself fades out under MORE, with nothing painted behind MORE. For a page whose
    /// colour is not flat where the text sits: the artist page's biography lies on the photo's
    /// fade, where a block of the page colour showed as a darker box (S23, 11-27).
    /// </summary>
    public bool FadeTextUnderMore { get; set; }

    /// <summary>The folded height in lines.</summary>
    public int Lines { get; }

    /// <summary>
    /// The folded height at the width the text is laid out at: <see cref="Lines"/> lines with
    /// MORE over the end of the last. A text that fits shows whole, without MORE. Runs on each
    /// re-wrap (width, text).
    /// </summary>
    public void Measure()
    {
        var text = _text.Bounds.Height;
        if (text <= 0) return;
        var lineHeight = _text.LineHeight > 0 ? _text.LineHeight : _text.FontSize * 1.3;
        var folded = Math.Min(text, lineHeight * Lines);
        _overflows = text > folded + 0.5;
        SplitFolded();
        _clip.Height = folded;
        _folded.IsVisible = _overflows;
        _text.Opacity = _overflows ? 0 : 1;
        _more.IsVisible = _overflows;
        UpdateTextFade();
    }

    private void UpdateTextFade()
    {
        if (!FadeTextUnderMore) return;
        double width = _clip.Bounds.Width, more = _more.Bounds.Width;
        if (!_overflows || width <= 0 || more <= 0 || more >= width)
        {
            _tail.OpacityMask = null;
            return;
        }
        var solid = (width - more) / width;
        var clear = Math.Min(1, (width - more + _more.Padding.Left * 0.8) / width);
        _tail.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Black, 0),
                new GradientStop(Colors.Black, solid),
                new GradientStop(Colors.Transparent, clear),
                new GradientStop(Colors.Transparent, 1),
            },
        };
    }

    /// <summary>MORE sits on the page colour, fading in over the text it covers.</summary>
    public void SetMoreFade(Color? into)
    {
        if (FadeTextUnderMore)
        {
            _more.Background = null;
            return;
        }
        _more.Background = into is { } m
            ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, m.R, m.G, m.B), 0),
                    new GradientStop(Color.FromArgb(255, m.R, m.G, m.B), 0.42),
                },
            }
            : null;
    }

    /// <summary>
    /// The folded stand-in: the lines before the last folded one exactly as the full text wraps
    /// them, then everything after as one unwrapped line cut at the edge, so it runs on under
    /// MORE rather than stopping where the next word wrapped away.
    /// </summary>
    private void SplitFolded()
    {
        var full = _text.Text ?? "";
        var lines = _text.TextLayout.TextLines;
        if (!_overflows || lines.Count < Lines)
        {
            _head.Text = full;
            _tail.Text = null;
            return;
        }
        var split = Math.Clamp(lines[Lines - 1].FirstTextSourceIndex, 0, full.Length);
        _head.Text = full[..split].TrimEnd();
        _head.IsVisible = split > 0;
        _tail.Text = full[split..].Replace('\n', ' ');
    }
}
