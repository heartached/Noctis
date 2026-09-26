using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Send to Folder: typing a destination does not rebuild the plan (a file stat per track on
/// the destination drive) for every keystroke, and the plan is built off the UI thread.
/// </summary>
public class SendToFolderViewModelTests
{
    private sealed class RecordingService : ISendToFolderService
    {
        public List<string> Roots { get; } = new();
        public List<bool> OnUiThread { get; } = new();

        public IReadOnlyList<SendToFolderItem> Plan(IEnumerable<Track> tracks, string targetRoot, string? organizePattern, bool includeLyrics)
        {
            lock (Roots)
            {
                Roots.Add(targetRoot);
                OnUiThread.Add(Dispatcher.UIThread.CheckAccess());
            }
            return tracks.Select(t => new SendToFolderItem(t, t.FilePath, Path.Combine(targetRoot, Path.GetFileName(t.FilePath)),
                SendToFolderAction.Copy, null, null)).ToList();
        }

        public Task<SendToFolderResult> CopyAsync(IReadOnlyList<SendToFolderItem> plan, IProgress<SendToFolderProgress>? progress, CancellationToken ct)
            => Task.FromResult(new SendToFolderResult(0, 0, 0, Array.Empty<string>(), false));
    }

    private static Track T(string file) => new() { Id = Guid.NewGuid(), FilePath = TestPaths.Primary("Music", file), Title = file };

    [AvaloniaFact]
    public async Task TypingADestination_PlansOnceAfterThePause_OffTheUiThread()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var deepest = Path.Combine(root, "Music", "Phone");
        Directory.CreateDirectory(deepest);
        try
        {
            var service = new RecordingService();
            var vm = new SendToFolderViewModel(new[] { T("a.flac"), T("b.mp3") }, service, FileOrganizePlanner.DefaultPattern);

            // Every prefix is an existing folder, like typing through "E:\" and "E:\Music".
            vm.Destination = root;
            vm.Destination = Path.Combine(root, "Music");
            vm.Destination = deepest;
            Assert.Empty(service.Roots); // nothing planned while the user is still typing
            Assert.False(vm.CanStart);

            await vm.PlanRebuild;
            Assert.Equal(new[] { deepest }, service.Roots);
            Assert.Equal(new[] { false }, service.OnUiThread);
            Assert.Equal(2, vm.Rows.Count);
            Assert.True(vm.CanStart);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [AvaloniaFact]
    public async Task ChangingTheDestination_DisablesCopyUntilTheNewPlanIsReady()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "First");
        var second = Path.Combine(root, "Second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            var service = new RecordingService();
            var vm = new SendToFolderViewModel(new[] { T("a.flac") }, service, FileOrganizePlanner.DefaultPattern);
            vm.Destination = first;
            await vm.PlanRebuild;
            Assert.True(vm.CanStart);

            // Copy must not run the old plan against the folder the user has moved away from.
            vm.Destination = second;
            Assert.False(vm.CanStart);
            await vm.PlanRebuild;
            Assert.True(vm.CanStart);
            Assert.Equal(second, service.Roots[^1]);
            Assert.Equal("a.flac", Assert.Single(vm.Rows).Target);

            vm.Destination = Path.Combine(root, "Missing");
            await vm.PlanRebuild;
            Assert.False(vm.CanStart);
            Assert.Empty(vm.Rows);
            Assert.Equal("That folder doesn't exist.", vm.PlanSummary);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
