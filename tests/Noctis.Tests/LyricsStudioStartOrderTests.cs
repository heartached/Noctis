using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Start runs the song the user clicked first, then the rest in queue order (09-19).</summary>
public class LyricsStudioStartOrderTests
{
    [AvaloniaFact]
    public async Task Start_RunsTheSelectedSongFirst_ThenTheRestInQueueOrder()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "noctis-studio-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var engine = new FakeEngine(tmp);
            Track T(string title) => new() { Title = title, Artist = "A", FilePath = Path.Combine(tmp, title + ".mp3") };
            var tracks = new[] { T("first"), T("second"), T("third") };
            var vm = new LyricsStudioViewModel(tracks, engine, new LyricsWriter(null!, null), new FakeLibraryService(),
                null, () => new AppSettings(), _ => { });
            Assert.True(vm.CanStart, "fake model must count as installed");

            vm.Selected = vm.Queue[1];
            await vm.StartCommand.ExecuteAsync(null);

            Assert.Equal(new[] { "second", "first", "third" }, engine.Processed);
            Assert.All(vm.Queue, i => Assert.Equal(LyricsStudioViewModel.StudioStatus.Ready, i.Status));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private sealed class FakeEngine : ILyricsStudioEngine
    {
        public List<string> Processed { get; } = new();
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }

        public FakeEngine(string root)
        {
            Models = StudioTestModel.Installed(root);
        }

        public IDisposable OpenSession(WhisperModelSize model) => new Handle();

        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
        {
            Processed.Add(track.Title);
            return Task.FromResult(new LyricsStudioResult(track, Array.Empty<AlignedLine>(), LyricsStudioSource.ExistingLyrics, 0.9, "es", 0));
        }

        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
