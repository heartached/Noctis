using System.Reflection;
using Avalonia.Threading;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Scan tests intermittently skipped a fresh MP3 with a NullReferenceException thrown before
/// the tag read, only with the dev log on. The scan's breadcrumb (CappedBreadcrumb ->
/// DebugLog.Write) raises DebugLog.Changed on the parallel scan workers; every SettingsViewModel
/// an earlier test built is still subscribed and calls Dispatcher.UIThread.Post. After an
/// [AvaloniaFact] the headless session leaves no UI dispatcher, so the first worker to ask
/// builds one on itself and publishes it (s_uiThread) before its _impl is set; a second worker
/// posting in that instant reaches _impl.Signal() on null.
/// </summary>
public class DebugLogChangedRaceTests
{
    private static readonly Action ResetDispatcher = typeof(Dispatcher)
        .GetMethod("ResetBeforeUnitTests", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<Action>();

    // Same body as SettingsViewModel's DebugLog.Changed handler.
    private sealed class FakeDevLogView
    {
        public int Queued;
        public void OnChanged()
        {
            if (Interlocked.Exchange(ref Queued, 1) == 1) return;
            Dispatcher.UIThread.Post(() => Volatile.Write(ref Queued, 0), DispatcherPriority.Background);
        }
    }

    [Fact]
    public void ConcurrentWrites_RightAfterTheUiDispatcherIsReset_NeverThrow()
    {
        var views = Enumerable.Range(0, 8).Select(_ => new FakeDevLogView()).ToArray();
        foreach (var v in views) DebugLog.Changed += v.OnChanged;
        var failures = new List<Exception>();
        try
        {
            const int threads = 4;
            for (var iteration = 0; iteration < 2000 && failures.Count == 0; iteration++)
            {
                ResetDispatcher();
                foreach (var v in views) v.Queued = 0;
                using var barrier = new Barrier(threads);
                var workers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
                {
                    barrier.SignalAndWait();
                    try { DebugLog.Write("RaceTest", $"tags: open {i}"); }
                    catch (Exception ex) { lock (failures) failures.Add(ex); }
                })).ToList();
                workers.ForEach(t => t.Start());
                workers.ForEach(t => t.Join());
            }
        }
        finally
        {
            foreach (var v in views) DebugLog.Changed -= v.OnChanged;
            // Leave no dispatcher owned by a finished thread for the next plain [Fact].
            ResetDispatcher();
        }

        Assert.True(failures.Count == 0, failures.Count == 0 ? "" : failures[0].ToString());
    }

    private sealed class NullAudit : IAuditTrailService
    {
        public Task AppendAsync(Noctis.Models.AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static void WriteMp3(string path, string title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.Album = "Fixture Album";
        f.Save();
    }

    /// <summary>The symptom: a scan run right after an [AvaloniaFact], dev log on, finds every song.</summary>
    [Fact]
    public async Task Scan_RightAfterTheUiDispatcherIsReset_WithDevLogOn_FindsEverySong()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var music = Path.Combine(root, "music");
        for (var i = 0; i < 8; i++) WriteMp3(Path.Combine(music, "Album", $"{i:00} - Song.mp3"), $"Song {i}");

        var loggerOn = DebugLogger.IsEnabled;
        var views = Enumerable.Range(0, 8).Select(_ => new FakeDevLogView()).ToArray();
        foreach (var v in views) DebugLog.Changed += v.OnChanged;
        try
        {
            DebugLogger.IsEnabled = true;
            for (var iteration = 0; iteration < 150; iteration++)
            {
                var persistence = new PersistenceService(Path.Combine(root, "data" + iteration));
                var settings = new Noctis.Models.AppSettings();
                settings.MusicFolders.Add(music);
                await persistence.SaveSettingsAsync(settings);
                var library = new LibraryService(new MetadataService(), persistence,
                    new SqliteLibraryIndexService(persistence), new NullAudit());

                ResetDispatcher();
                foreach (var v in views) v.Queued = 0;
                await library.ScanAsync(new[] { music });

                Assert.True(library.AllTracks.Count == 8,
                    $"iteration {iteration}: {ScanMissReport.Describe(library, music)}");
            }
        }
        finally
        {
            foreach (var v in views) DebugLog.Changed -= v.OnChanged;
            DebugLogger.IsEnabled = loggerOn;
            ResetDispatcher();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
