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
using Status = Noctis.ViewModels.LyricsStudioViewModel.StudioStatus;

namespace Noctis.Tests;

/// <summary>
/// Clear lyrics in the Studio (Discord, Mistery 2026-10-10): wrong lyrics — matched online for a
/// same-titled song — are dumped from the review, the empty lyrics box opens, and the model times
/// the user's own text. Nothing on disk changes until Save.
/// </summary>
public class LyricsStudioClearLyricsTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "noctis-studio-clear-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioClearLyricsTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private Track T(string title) => new() { Title = title, Artist = "A", FilePath = Path.Combine(_tmp, title + ".mp3") };

    private LyricsStudioViewModel Studio(FakeEngine engine, LyricsStudioDraftStore? drafts, params Track[] tracks) =>
        new(tracks, engine, new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { }, drafts);

    /// <summary>A song with wrong lyrics in a .lrc beside it and in its tags, as an online match leaves them.</summary>
    private Track WrongMatch(out string lrc)
    {
        var track = T("song");
        lrc = Path.ChangeExtension(track.FilePath!, ".lrc");
        File.WriteAllText(lrc, "[00:01.00]wrong words\n[00:04.00]from another song");
        track.Lyrics = "wrong words\nfrom another song";
        return track;
    }

    /// <summary>Times the lines it is given; with none, stands in for an LRCLIB match.</summary>
    private static FakeEngine Echo(string root) => new(root)
    {
        Handler = (track, o) => new LyricsStudioResult(track,
            (o.SourceLines ?? new[] { "lrclib match" }).Select((l, i) => new AlignedLine(l, TimeSpan.FromSeconds(i * 3), TimeSpan.FromSeconds(i * 3 + 2), Array.Empty<AlignedWord>(), 0.8, false)).ToList(),
            o.SourceLines is null ? LyricsStudioSource.Lrclib : LyricsStudioSource.ExistingLyrics, 0.8, "en", 2),
    };

    [AvaloniaFact]
    public async Task ClearLyrics_Confirmed_EmptiesTheReview_OpensAnEmptyBox_AndWritesNothing()
    {
        var track = WrongMatch(out var lrc);
        var engine = Echo(_tmp);
        var before = Directory.GetFiles(_tmp, "*", SearchOption.AllDirectories).OrderBy(f => f).ToArray();
        var vm = Studio(engine, null, track, T("other"));
        var asked = new List<string>();
        vm.Confirm = message => { asked.Add(message); return Task.FromResult(true); };
        vm.Selected = vm.Queue[0];
        Assert.Equal(Status.Loaded, vm.Queue[0].Status);
        Assert.Equal(2, vm.ReviewLines.Count);

        await vm.ClearLyricsCommand.ExecuteAsync(null);

        Assert.Contains("song", Assert.Single(asked));
        var item = vm.Queue[0];
        Assert.Empty(vm.ReviewLines);
        Assert.Null(item.Result);
        Assert.Equal(Status.NeedsLyrics, item.Status);
        Assert.False(vm.HasReview);
        Assert.True(vm.IsComposing, "the lyrics box opens for the user's own lyrics");
        Assert.Equal(string.Empty, item.DraftText);
        Assert.False(vm.CanSave);
        // Nothing on disk or in the track changed.
        Assert.Equal("[00:01.00]wrong words\n[00:04.00]from another song", File.ReadAllText(lrc));
        Assert.Equal(before, Directory.GetFiles(_tmp, "*", SearchOption.AllDirectories).OrderBy(f => f).ToArray());
        Assert.Empty(engine.Options);
        Assert.Equal("wrong words\nfrom another song", track.Lyrics);

        // Leaving and coming back neither reloads the .lrc nor refills the box with the tags' text.
        vm.Selected = vm.Queue[1];
        vm.Selected = vm.Queue[0];
        Assert.Equal(Status.NeedsLyrics, item.Status);
        Assert.Empty(vm.ReviewLines);
        Assert.Equal(string.Empty, item.DraftText);
    }

    [AvaloniaFact]
    public async Task ClearLyrics_Cancelled_KeepsTheReview()
    {
        var track = WrongMatch(out _);
        var vm = Studio(Echo(_tmp), null, track);
        vm.Confirm = _ => Task.FromResult(false);
        vm.Selected = vm.Queue[0];

        await vm.ClearLyricsCommand.ExecuteAsync(null);

        Assert.Equal(Status.Loaded, vm.Queue[0].Status);
        Assert.NotNull(vm.Queue[0].Result);
        Assert.Equal(new[] { "wrong words", "from another song" }, vm.ReviewLines.Select(l => l.Text));
        Assert.True(vm.HasReview);
    }

    [AvaloniaFact]
    public async Task ClearLyrics_ThenAlign_TimesOnlyTheUsersText()
    {
        var track = WrongMatch(out var lrc);
        var engine = Echo(_tmp);
        var vm = Studio(engine, null, track);
        vm.Confirm = _ => Task.FromResult(true);
        vm.Selected = vm.Queue[0];
        await vm.ClearLyricsCommand.ExecuteAsync(null);

        vm.Selected!.DraftText = "my first line\nmy second line";
        await vm.AlignDraftCommand.ExecuteAsync(null);

        var options = Assert.Single(engine.Options);
        Assert.Equal(new[] { "my first line", "my second line" }, options.SourceLines);
        Assert.Null(options.SourceLineStarts); // the wrong file's line starts are not anchors
        Assert.False(options.ForceTranscription);
        Assert.Equal(Status.Ready, vm.Queue[0].Status);
        Assert.Equal(LyricsStudioSource.PastedLyrics, vm.Queue[0].Result!.Source);
        Assert.Equal(new[] { "my first line", "my second line" }, vm.ReviewLines.Select(l => l.Text));
        Assert.Equal("[00:01.00]wrong words\n[00:04.00]from another song", File.ReadAllText(lrc));

        // Re-sync of the user's lyrics times their text again, not the song's .lrc.
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(2, engine.Options.Count);
        Assert.Equal(new[] { "my first line", "my second line" }, engine.Options[1].SourceLines);
    }

    [AvaloniaFact]
    public async Task ClearLyrics_OnAReadyResult_DropsItsDraft()
    {
        var track = T("fetched");
        track.Lyrics = "lyrics of a same-titled song";
        var drafts = new LyricsStudioDraftStore(Path.Combine(_tmp, "drafts"));
        var vm = Studio(Echo(_tmp), drafts, track);
        vm.Confirm = _ => Task.FromResult(true);
        await vm.StartCommand.ExecuteAsync(null);
        vm.Selected = vm.Queue[0];
        Assert.Equal(Status.Ready, vm.Queue[0].Status);
        Assert.Contains(track.Id, drafts.ListTrackIds());

        await vm.ClearLyricsCommand.ExecuteAsync(null);

        Assert.DoesNotContain(track.Id, drafts.ListTrackIds());
        Assert.Equal(Status.NeedsLyrics, vm.Queue[0].Status);
        Assert.True(vm.IsComposing);
    }

    [AvaloniaFact]
    public async Task Start_WithTheUsersLyricsInTheBox_TimesThem_NotTheSongsOwn()
    {
        // The box opens prefilled with the tags' plain lyrics; the user replaces them and
        // presses Start (not Align). Start used to ignore the box and time the tags' text,
        // or LRCLIB's match when the song had none.
        var track = T("song");
        track.Lyrics = "tag line one\ntag line two";
        var engine = Echo(_tmp);
        var vm = Studio(engine, null, track);
        vm.Selected = vm.Queue[0];
        Assert.True(vm.IsComposing);
        Assert.Equal("tag line one\ntag line two", vm.Selected!.DraftText);

        vm.Selected.DraftText = "my words";
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "my words" }, Assert.Single(engine.Options).SourceLines);
        Assert.Equal(LyricsStudioSource.PastedLyrics, vm.Queue[0].Result!.Source);
    }

    [AvaloniaFact]
    public async Task Start_WithTheBoxAsPrefilled_StillSaysTheLyricsAreTheSongsOwn()
    {
        var track = T("song");
        track.Lyrics = "tag line one\ntag line two";
        var engine = Echo(_tmp);
        var vm = Studio(engine, null, track);
        vm.Selected = vm.Queue[0];

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "tag line one", "tag line two" }, Assert.Single(engine.Options).SourceLines);
        Assert.Equal(LyricsStudioSource.ExistingLyrics, vm.Queue[0].Result!.Source);
    }

    private sealed class FakeEngine : ILyricsStudioEngine
    {
        public List<LyricsStudioOptions> Options { get; } = new();
        public Func<Track, LyricsStudioOptions, LyricsStudioResult> Handler { get; set; } =
            (t, _) => new LyricsStudioResult(t, Array.Empty<AlignedLine>(), LyricsStudioSource.ExistingLyrics, 0.9, "en", 0);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }

        public FakeEngine(string root)
        {
            Models = StudioTestModel.Installed(root);
        }

        public IDisposable OpenSession(WhisperModelSize model) => new Handle();

        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
        {
            Options.Add(options);
            return Task.FromResult(Handler(track, options));
        }

        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
