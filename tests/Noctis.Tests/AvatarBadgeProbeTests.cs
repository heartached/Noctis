using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>Headless geometry probe for the profile avatar's X/camera badges.</summary>
public class AvatarBadgeProbeTests
{
    private readonly ITestOutputHelper _output;
    public AvatarBadgeProbeTests(ITestOutputHelper output) => _output = output;

    [AvaloniaFact]
    public async Task AvatarX_GlyphIsCentredInItsCircle_AndBadgesHideUntilHover()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
            await vm.LoadAsync();
            vm.SelectedSettingsTab = SettingsViewModel.TabAccountDevices;
            vm.ProfileAvatarPath = Path.Combine(root, "nope.png");
            var view = new SettingsView { DataContext = vm };
            var window = new Window { Width = 920, Height = 720, Content = view };
            window.Show();
            window.UpdateLayout();

            var x = view.GetLogicalDescendants().OfType<Button>().Single(b => b.Classes.Contains("avatar-x"));
            var wrap = x.GetVisualAncestors().OfType<Panel>().First(p => p.Classes.Contains("avatar-wrap"));
            var badge = wrap.GetLogicalDescendants().OfType<Border>().Single(b => b.Classes.Contains("avatar-badge"));
            var icon = x.GetVisualDescendants().OfType<Noctis.Controls.LineIcon>().Single(); // the shared line X (was a filled PathIcon)
            var tl = icon.TranslatePoint(new Point(0, 0), x)!.Value;
            var left = tl.X; var right = x.Bounds.Width - (tl.X + icon.Bounds.Width);
            var top = tl.Y; var bottom = x.Bounds.Height - (tl.Y + icon.Bounds.Height);
            _output.WriteLine($"x button {x.Bounds.Size} icon {icon.Bounds.Size} at {tl} margins L{left} R{right} T{top} B{bottom}");
            _output.WriteLine($"resting opacity: x={x.Opacity} badge={badge.Opacity}");
            Assert.True(Math.Abs(left - right) < 0.51, $"horizontal off by {left - right}");
            Assert.True(Math.Abs(top - bottom) < 0.51, $"vertical off by {top - bottom}");
            Assert.Equal(0, x.Opacity);
            Assert.Equal(0, badge.Opacity);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public System.Collections.Generic.IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => Array.Empty<Noctis.Models.PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
