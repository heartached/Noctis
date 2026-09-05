using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Plugins;

namespace Noctis.SamplePlugin;

/// <summary>
/// Reference plugin: draws a soft ring behind the lyrics that swells on every beat and logs
/// track changes. Shows the three things most plugins need — the now-playing feed, the beat
/// tap, and a visual layer.
/// </summary>
public sealed class PulseRingPlugin : INoctisPlugin
{
    private IPluginHost? _host;

    public PluginInfo Info { get; } = new(
        Id: "dev.noctis.samples.pulsering",
        Name: "Pulse Ring",
        Version: "1.0.0",
        Author: "Noctis",
        Description: "A ring behind the lyrics that breathes with the beat. Reference plugin.");

    public void Initialize(IPluginHost host)
    {
        _host = host;
        host.NowPlaying.TrackChanged += OnTrackChanged;
        host.RegisterVisualLayer(new PulseRingLayer(host));
        host.Log("initialized");
    }

    public void Shutdown()
    {
        if (_host is not null) _host.NowPlaying.TrackChanged -= OnTrackChanged;
        _host = null;
    }

    private void OnTrackChanged(object? sender, EventArgs e)
    {
        var t = _host?.NowPlaying.Track;
        _host?.Log(t is null ? "stopped" : $"now playing {t.Artist} – {t.Title}");
    }

    private sealed class PulseRingLayer : IVisualLayerProvider
    {
        private readonly IPluginHost _host;
        public PulseRingLayer(IPluginHost host) => _host = host;
        public string Name => "Pulse ring";

        public Control CreateLayer()
        {
            var ring = new Ellipse
            {
                Width = 420, Height = 420,
                Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 3,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                RenderTransformOrigin = RelativePoint.Center,
                IsHitTestVisible = false,
            };
            var scale = new ScaleTransform(1, 1);
            ring.RenderTransform = scale;

            // A timer is fine for a sample; the app's own layers use RequestAnimationFrame.
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
            {
                var pulse = _host.Beat.TryRead(out var p) ? p : 0;
                var s = 1 + 0.12 * pulse;
                scale.ScaleX = s;
                scale.ScaleY = s;
                ring.Opacity = 0.5 + 0.5 * pulse;
            });
            ring.AttachedToVisualTree += (_, _) => timer.Start();
            ring.DetachedFromVisualTree += (_, _) => timer.Stop();
            return ring;
        }
    }
}

/// <summary>Deliberately broken plugin the host tests use: proves a throwing Initialize is
/// contained (status "Failed", message shown) and cannot take the app down.</summary>
public sealed class ThrowingPlugin : INoctisPlugin
{
    public PluginInfo Info { get; } = new("dev.noctis.samples.throwing", "Throwing", "1.0.0", "Noctis", "Throws on purpose.");
    public void Initialize(IPluginHost host) => throw new InvalidOperationException("boom from plugin");
    public void Shutdown() { }
}
