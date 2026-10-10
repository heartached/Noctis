using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.AudioAnalysis;

namespace Noctis.ViewModels;

/// <summary>
/// Drives the Spectrogram pop-up: decodes + analyses the track off the UI thread, then
/// composes the plot with frequency / time axes and a dB scale (Spek layout) into one
/// image the view shows. The axis text colour comes from the view (theme resource).
/// </summary>
public sealed partial class SpectrogramViewModel : ObservableObject, IDisposable
{
    // Plot geometry (device-independent pixels). The composed image is PlotWidth +
    // margins wide; the card sizes itself to it.
    public const int PlotWidth = 1000;
    public const int PlotHeight = 480;
    private const int LeftAxis = 58;
    private const int RightScale = 74;
    private const int TopPad = 10;
    private const int BottomAxis = 30;

    /// <summary>The decode + STFT step: ffmpeg path, track, columns, progress, token.
    /// <see cref="SpectrogramRenderer.ComputeAsync"/> in the app; tests swap in a fake.</summary>
    internal delegate Task<SpectrogramData> Analyzer(
        string ffmpegPath, Track track, int columns, IProgress<double> progress, CancellationToken ct);

    private static readonly Analyzer DefaultAnalyzer = (ffmpeg, track, columns, progress, ct) =>
        SpectrogramRenderer.ComputeAsync(ffmpeg, track.FilePath, track.SampleRate, track.Duration, columns, progress, ct);

    private readonly Track _track;
    private readonly IAudioConverterService _converter;
    private readonly Analyzer _analyze;
    private readonly CancellationTokenSource _cts = new();
    private bool _started;
    private bool _disposed;

    public string Title => string.IsNullOrWhiteSpace(_track.Title) ? Path.GetFileName(_track.FilePath) : _track.Title;

    /// <summary>"Artist · Album", leaving out whichever part is blank (an untagged file used
    /// to read " · Album" with a dangling separator).</summary>
    public string Subtitle => string.Join(" · ",
        new[] { _track.Artist, _track.Album }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

    /// <summary>The stream facts as short chips: format, sample rate, bit depth, bitrate,
    /// duration — each only when known.</summary>
    public IReadOnlyList<string> TechChips { get; }

    /// <summary>How the plot is made, de-emphasised in the footer (was the tail of the
    /// header's one run-on info line).</summary>
    public string AnalysisCaption => Loc.T("Spectrogram.AnalysisCaption", SpectrogramRenderer.FftSize);

    [ObservableProperty] private IImage? _image;
    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = Loc.T("Spectrogram.AnalyzingAudio");
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorText = string.Empty;

    /// <summary>No duration to measure against (some streams, broken headers): the decoder
    /// never reports a fraction (SpectrogramRenderer only reports when it knows the expected
    /// length), so the bar runs indeterminate instead of sitting at 0 %.</summary>
    public bool IsProgressIndeterminate => _track.Duration <= TimeSpan.Zero;

    /// <summary>Set by the view before <see cref="RunAsync"/>: theme text brush for the axes.</summary>
    public IBrush AxisForeground { get; set; } = Brushes.White;

    /// <summary>True once the analysis was cancelled (close by any route).</summary>
    internal bool IsCancelled => _cts.IsCancellationRequested;

    public event EventHandler? Closed;

    public SpectrogramViewModel(Track track, IAudioConverterService converter)
        : this(track, converter, null) { }

    internal SpectrogramViewModel(Track track, IAudioConverterService converter, Analyzer? analyze)
    {
        _track = track;
        _converter = converter;
        _analyze = analyze ?? DefaultAnalyzer;
        TechChips = BuildChips(track);
    }

    internal static IReadOnlyList<string> BuildChips(Track track)
    {
        var chips = new List<string>();
        var format = ShortFormat(track.Codec, track.FilePath);
        if (format.Length > 0) chips.Add(format);
        if (track.SampleRate > 0)
            chips.Add((track.SampleRate / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + " kHz");
        if (track.BitsPerSample > 0) chips.Add($"{track.BitsPerSample}-bit");
        if (track.Bitrate > 0) chips.Add($"{track.Bitrate} kbps");
        if (track.Duration > TimeSpan.Zero) chips.Add(FormatTime(track.Duration));
        return chips;
    }

    /// <summary>
    /// The codec as a chip word: TagLib's description is long ("MPEG-4 Audio (alac)", "Flac
    /// Audio", "MPEG Version 1 Audio, Layer 3"), so the common ones collapse to their usual
    /// name and anything else keeps its own text. Blank falls back to the file extension.
    /// </summary>
    internal static string ShortFormat(string? codec, string? filePath)
    {
        var raw = (codec ?? string.Empty).Trim();
        if (raw.Length == 0)
            return (Path.GetExtension(filePath ?? string.Empty) ?? string.Empty).TrimStart('.').ToUpperInvariant();
        var lower = raw.ToLowerInvariant();
        if (lower.Contains("alac") || lower.Contains("apple lossless")) return "ALAC";
        if (lower.Contains("flac")) return "FLAC";
        if (lower.Contains("layer 3") || lower == "mp3") return "MP3";
        if (lower.Contains("opus")) return "Opus";
        if (lower.Contains("vorbis")) return "Vorbis";
        if (lower.Contains("aac") || lower.Contains("mp4a")) return "AAC"; // TagLib: "MPEG-4 Audio (mp4a)"
        // "Something (xyz)": the bracketed codec is the useful part.
        var open = raw.LastIndexOf('(');
        var close = raw.LastIndexOf(')');
        if (open >= 0 && close > open + 1) return raw.Substring(open + 1, close - open - 1).Trim().ToUpperInvariant();
        return raw.Length <= 12 ? raw.ToUpperInvariant() : raw;
    }

    public async Task RunAsync()
    {
        if (_started || _disposed) return;
        _started = true;
        // Read once: Dispose may dispose the source while this is still running.
        var ct = _cts.Token;

        var ffmpeg = _converter.GetFfmpegPath();
        if (ffmpeg == null)
        {
            Fail(Loc.T("Spectrogram.NeedsFfmpeg"));
            return;
        }
        if (string.IsNullOrWhiteSpace(_track.FilePath) || !File.Exists(_track.FilePath))
        {
            Fail(Loc.T("Spectrogram.FileMissing"));
            return;
        }

        try
        {
            var progress = new Progress<double>(p =>
            {
                if (ct.IsCancellationRequested || !IsBusy) return;
                Progress = p * 100;
                ProgressText = Loc.T("Spectrogram.AnalyzingPercent", Math.Floor(p * 100));
            });
            var data = await Task.Run(() => _analyze(ffmpeg, _track, PlotWidth, progress, ct), ct);
            ct.ThrowIfCancellationRequested();

            // Decode done: the bar fills and says so while the plot is painted.
            Progress = 100;
            ProgressText = Loc.T("Spectrogram.Drawing");

            // The painted plot is only drawn into the composed image, then released (it was
            // never disposed, ~1.9 MB of native bitmap per open).
            using var plot = await Task.Run(() => SpectrogramRenderer.Paint(data, PlotHeight), ct);
            // Closed while painting: don't compose an image nobody will see or dispose.
            ct.ThrowIfCancellationRequested();
            var composed = Compose(data, plot);
            if (ct.IsCancellationRequested || _disposed)
            {
                composed.Dispose();
                return;
            }
            Image = composed;
            IsBusy = false;
            IsReady = true;
        }
        catch (OperationCanceledException)
        {
            // Closed mid-analysis.
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Fail(Loc.T("Spectrogram.Failed", ex.Message));
        }
    }

    private void Fail(string message)
    {
        ErrorText = message;
        HasError = true;
        IsBusy = false;
    }

    /// <summary>Total size of the composed image; the view sizes the plot frame from it.</summary>
    public static Size ComposedSize => new(LeftAxis + PlotWidth + RightScale, TopPad + PlotHeight + BottomAxis);

    private RenderTargetBitmap Compose(SpectrogramData data, WriteableBitmap plot)
    {
        var size = ComposedSize;
        var rtb = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
        using var ctx = rtb.CreateDrawingContext();

        var plotRect = new Rect(LeftAxis, TopPad, PlotWidth, PlotHeight);
        ctx.FillRectangle(Brushes.Black, plotRect);
        ctx.DrawImage(plot, new Rect(0, 0, plot.PixelSize.Width, plot.PixelSize.Height), plotRect);

        var text = AxisForeground;
        var tick = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1);
        var faint = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)), 1);
        var typeface = new Typeface(FontFamily.Default);

        // Frequency axis (left): 0 … Nyquist, a label every 2 kHz (4 kHz above 48 kHz).
        double nyquist = data.NyquistHz;
        double stepHz = nyquist > 48000 ? 4000 : 2000;
        for (double hz = 0; hz <= nyquist + 1; hz += stepHz)
        {
            var y = plotRect.Bottom - hz / nyquist * plotRect.Height;
            ctx.DrawLine(tick, new Point(plotRect.Left - 4, y), new Point(plotRect.Left, y));
            var label = new FormattedText($"{hz / 1000:0} kHz", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 11, text);
            ctx.DrawText(label, new Point(plotRect.Left - 8 - label.Width, y - label.Height / 2));
        }

        // Time axis (bottom): ticks at a round interval that yields ~10 labels.
        var total = data.Duration.TotalSeconds;
        if (total > 0)
        {
            double stepS = PickTimeStep(total);
            for (double s = 0; s <= total + 0.001; s += stepS)
            {
                var x = plotRect.Left + s / total * plotRect.Width;
                ctx.DrawLine(tick, new Point(x, plotRect.Bottom), new Point(x, plotRect.Bottom + 4));
                var label = new FormattedText(FormatTime(TimeSpan.FromSeconds(s)), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 11, text);
                var lx = Math.Clamp(x - label.Width / 2, plotRect.Left - 4, plotRect.Right - label.Width);
                ctx.DrawText(label, new Point(lx, plotRect.Bottom + 7));
            }
        }

        // dB scale (right): the palette as a vertical bar, labelled every 20 dB.
        var barRect = new Rect(plotRect.Right + 12, plotRect.Top, 14, plotRect.Height);
        int rows = (int)barRect.Height;
        for (int i = 0; i < rows; i++)
        {
            double db = 0 + SpectrogramRenderer.MinDb * i / (double)(rows - 1);
            var (r, g, b) = SpectrogramRenderer.SamplePalette(db);
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(r, g, b)),
                new Rect(barRect.X, barRect.Y + i, barRect.Width, 1));
        }
        ctx.DrawRectangle(faint, barRect);
        for (double db = 0; db >= SpectrogramRenderer.MinDb; db -= 20)
        {
            var y = barRect.Top + (0 - db) / (0 - SpectrogramRenderer.MinDb) * barRect.Height;
            ctx.DrawLine(tick, new Point(barRect.Right, y), new Point(barRect.Right + 4, y));
            var label = new FormattedText($"{db:0} dB", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 11, text);
            ctx.DrawText(label, new Point(barRect.Right + 7, y - label.Height / 2));
        }

        return rtb;
    }

    private static double PickTimeStep(double totalSeconds)
    {
        double[] candidates = { 1, 2, 5, 10, 15, 20, 30, 60, 120, 300, 600, 900, 1800, 3600 };
        foreach (var c in candidates)
            if (totalSeconds / c <= 12) return c;
        return 3600;
    }

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>Stops the analysis (kills ffmpeg through the token) without closing. The view
    /// calls it as soon as a close starts, so nothing keeps decoding while the card animates out.</summary>
    public void Cancel()
    {
        if (_disposed) return;
        _cts.Cancel();
    }

    [RelayCommand]
    private void Close()
    {
        Cancel();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        // Unhook from the view before freeing the bitmap it shows.
        var image = Image;
        Image = null;
        (image as IDisposable)?.Dispose();
    }
}
