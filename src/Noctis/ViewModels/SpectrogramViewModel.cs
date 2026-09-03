using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.AudioAnalysis;

namespace Noctis.ViewModels;

/// <summary>
/// Drives the Spectrogram window: decodes + analyses the track off the UI thread, then
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

    private readonly Track _track;
    private readonly IAudioConverterService _converter;
    private readonly CancellationTokenSource _cts = new();

    public string Title => string.IsNullOrWhiteSpace(_track.Title) ? Path.GetFileName(_track.FilePath) : _track.Title;
    public string Subtitle => string.IsNullOrWhiteSpace(_track.Album) ? _track.Artist : $"{_track.Artist} · {_track.Album}";

    /// <summary>"FLAC · 44.1 kHz · 16-bit · 3:12" — the stream line Spek prints above the plot.</summary>
    public string InfoLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_track.Codec)) parts.Add(_track.Codec.ToUpperInvariant());
            if (_track.SampleRate > 0) parts.Add($"{_track.SampleRate / 1000.0:0.#} kHz");
            if (_track.BitsPerSample > 0) parts.Add($"{_track.BitsPerSample}-bit");
            if (_track.Bitrate > 0) parts.Add($"{_track.Bitrate} kbps");
            if (_track.Duration > TimeSpan.Zero) parts.Add(FormatTime(_track.Duration));
            parts.Add($"FFT {SpectrogramRenderer.FftSize} · Hann");
            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty] private IImage? _image;
    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "Decoding…";
    [ObservableProperty] private bool _hasError;

    /// <summary>Set by the view before <see cref="RunAsync"/>: theme text brush for the axes.</summary>
    public IBrush AxisForeground { get; set; } = Brushes.White;

    public event EventHandler? Closed;

    public SpectrogramViewModel(Track track, IAudioConverterService converter)
    {
        _track = track;
        _converter = converter;
    }

    public async Task RunAsync()
    {
        var ffmpeg = _converter.GetFfmpegPath();
        if (ffmpeg == null)
        {
            Fail("ffmpeg is required for the spectrogram. Point Noctis at ffmpeg in Settings (ffmpeg path) and try again.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_track.FilePath) || !File.Exists(_track.FilePath))
        {
            Fail("The file could not be found on disk.");
            return;
        }

        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress = p * 100;
                Status = p < 0.98 ? $"Decoding… {p * 100:0}%" : "Rendering…";
            });
            var data = await Task.Run(() => SpectrogramRenderer.ComputeAsync(
                ffmpeg, _track.FilePath, _track.SampleRate, _track.Duration, PlotWidth, progress, _cts.Token));
            if (_cts.IsCancellationRequested) return;

            var plot = await Task.Run(() => SpectrogramRenderer.Paint(data, PlotHeight), _cts.Token);
            Image = Compose(data, plot);
            Status = string.Empty;
            IsBusy = false;
        }
        catch (OperationCanceledException)
        {
            // Window closed mid-analysis.
        }
        catch (Exception ex)
        {
            Fail("Analysis failed: " + ex.Message);
        }
    }

    private void Fail(string message)
    {
        HasError = true;
        IsBusy = false;
        Status = message;
    }

    /// <summary>Total size of the composed image; the view sizes the card from it.</summary>
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

    [RelayCommand]
    private void Close()
    {
        _cts.Cancel();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        (Image as IDisposable)?.Dispose();
    }
}
