using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;

namespace Noctis.ViewModels;

/// <summary>Lyrics Studio choices the user changes inside the dialog; persisted by Settings.</summary>
public sealed record LyricsStudioPrefs(string Model, string Language, bool WordTimings, bool SkipAlreadyTimed);

public sealed record SpeechLanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Lyrics Studio: a queue of songs run through the speech model — existing lyrics get
/// word-level timings, songs without lyrics get transcribed — each result reviewed and
/// edited before it is saved. Nothing touches disk until the user presses Save.
/// </summary>
public partial class LyricsStudioViewModel : ViewModelBase
{
    private readonly ILyricsStudioEngine _engine;
    private readonly LyricsWriter _writer;
    private readonly ILibraryService _library;
    private readonly PlayerViewModel? _player;
    private readonly Func<AppSettings> _settings;
    private readonly Action<LyricsStudioPrefs> _savePrefs;
    private readonly LyricsStudioDraftStore? _drafts;
    private CancellationTokenSource? _runCts;
    private int _savedCount;
    private bool _loadingPrefs;

    public static readonly IReadOnlyList<SpeechLanguageOption> Languages = new[]
    {
        new SpeechLanguageOption("auto", "Detect automatically"),
        new SpeechLanguageOption("en", "English"), new SpeechLanguageOption("es", "Spanish"), new SpeechLanguageOption("pt", "Portuguese"),
        new SpeechLanguageOption("fr", "French"), new SpeechLanguageOption("de", "German"), new SpeechLanguageOption("it", "Italian"),
        new SpeechLanguageOption("ja", "Japanese"), new SpeechLanguageOption("ko", "Korean"), new SpeechLanguageOption("zh", "Chinese"),
        new SpeechLanguageOption("ru", "Russian"), new SpeechLanguageOption("nl", "Dutch"), new SpeechLanguageOption("pl", "Polish"),
        new SpeechLanguageOption("tr", "Turkish"), new SpeechLanguageOption("sv", "Swedish"), new SpeechLanguageOption("ar", "Arabic"),
        new SpeechLanguageOption("hi", "Hindi"),
    };

    public IReadOnlyList<WhisperModelInfo> ModelOptions => WhisperModelManager.Catalog;
    public IReadOnlyList<SpeechLanguageOption> LanguageOptions => Languages;

    public ObservableCollection<StudioItem> Queue { get; } = new();
    [ObservableProperty] private StudioItem? _selected;

    public ObservableCollection<ReviewLine> ReviewLines { get; } = new();

    // ── Options ──
    [ObservableProperty] private WhisperModelInfo _selectedModel;
    [ObservableProperty] private SpeechLanguageOption _selectedLanguage;
    [ObservableProperty] private bool _wordTimings;
    [ObservableProperty] private bool _transcribeOnly;
    /// <summary>Leave alone songs that already carry the format being written (ELRC when word timings are on; LRC or ELRC when off).</summary>
    [ObservableProperty] private bool _skipAlreadyTimed;

    // ── Model state ──
    [ObservableProperty] private bool _isModelInstalled;
    [ObservableProperty] private string _modelStatusText = string.Empty;
    [ObservableProperty] private bool _isDownloadingModel;
    [ObservableProperty] private double _modelProgress;

    // ── Run state ──
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _runStatusText = string.Empty;

    public bool HasFfmpeg => _engine.HasFfmpeg;
    public bool CanStart => !IsRunning && IsModelInstalled && HasFfmpeg
        && (Queue.Any(i => i.Status == StudioStatus.Waiting) || Selected is { Status: StudioStatus.Loaded or StudioStatus.Ready });
    /// <summary>"Start" runs the queue; with nothing queued the button re-times the song on screen.</summary>
    public string StartLabel => Queue.Any(i => i.Status == StudioStatus.Waiting) ? "Start" : Selected is { Status: StudioStatus.Loaded or StudioStatus.Ready } ? "Re-sync" : "Start";
    private void RaiseStartState() { OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(StartLabel)); }
    public bool ShowModelDownload => !IsModelInstalled && !IsDownloadingModel;

    // ── Review ──
    public bool HasReview => Selected is { Status: StudioStatus.Ready or StudioStatus.Saved or StudioStatus.Loaded, Result: not null };
    /// <summary>Line-level lyrics were loaded: offer to time every word.</summary>
    public bool ReviewCanUpgrade => !IsRunning && Selected is { Status: StudioStatus.Loaded, Existing.Format: LyricsFormat.Lrc };
    public string ReviewTitle => Selected?.Title ?? string.Empty;
    public string ReviewSubtitle => Selected?.Subtitle ?? string.Empty;
    public string ReviewSourceText => Selected?.Result?.Source switch
    {
        LyricsStudioSource.ExistingLyrics => "Timed from the song's own lyrics",
        LyricsStudioSource.Lrclib => "Lyrics from LRCLIB, timed against the audio",
        LyricsStudioSource.Transcription => "Transcribed — no lyrics were available, so check the words",
        LyricsStudioSource.ExistingFile => Selected?.Existing is { } e
            ? $"Loaded from {e.Origin} · {(e.Format == LyricsFormat.Elrc ? "word timings" : "line timings only")}"
            : "Loaded",
        _ => string.Empty,
    };
    public string ReviewConfidenceText => Selected?.Result is { } r
        ? (r.Source == LyricsStudioSource.ExistingFile ? string.Empty : $"{Math.Round(r.Confidence * 100)}% of the words were heard · ")
          + $"{r.Lines.Count} lines · saves as {(WordTimings ? "ELRC, a time for every word" : "LRC, one time per line")}"
        : string.Empty;
    public bool ReviewIsTranscription => Selected?.Result?.Source == LyricsStudioSource.Transcription;
    public bool CanSave => Selected is { Status: StudioStatus.Ready or StudioStatus.Loaded } && ReviewLines.Count > 0;
    public string SummaryText => _savedCount == 0 ? string.Empty : $"{_savedCount} saved";

    public event EventHandler? Closed;

    /// <summary>Set by the dialog: asks the user before a re-sync replaces loaded timings. Null = no prompt.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    public LyricsStudioViewModel(
        IReadOnlyList<Track> tracks,
        ILyricsStudioEngine engine,
        LyricsWriter writer,
        ILibraryService library,
        PlayerViewModel? player,
        Func<AppSettings> settings,
        Action<LyricsStudioPrefs> savePrefs,
        LyricsStudioDraftStore? drafts = null)
    {
        _drafts = drafts;
        _engine = engine;
        _writer = writer;
        _library = library;
        _player = player;
        _settings = settings;
        _savePrefs = savePrefs;

        var s = settings();
        _loadingPrefs = true;
        _selectedModel = WhisperModelManager.Info(WhisperModelManager.Parse(s.LyricsStudioModel));
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code.Equals(s.LyricsStudioLanguage, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];
        _wordTimings = s.LyricsStudioWordTimings;
        _skipAlreadyTimed = s.LyricsStudioSkipAlreadyTimed;
        _loadingPrefs = false;

        var restored = 0;
        foreach (var t in tracks.Where(t => t.SourceType == SourceType.Local))
        {
            var item = new StudioItem(t);
            // A review left unfinished when the app closed comes back as it was — no re-run.
            if (_drafts is not null && _drafts.TryLoad(t.Id, out var draft))
            {
                item.Result = draft.ToResult(t);
                item.Status = StudioStatus.Ready;
                item.StatusText = "Restored from last time · review";
                restored++;
            }
            Queue.Add(item);
        }
        Queue.CollectionChanged += (_, _) => RaiseStartState();

        RefreshModelState();
        var queued = Queue.Count(i => i.Status == StudioStatus.Waiting);
        RunStatusText = !HasFfmpeg
            ? "ffmpeg is needed to decode songs — set its path under Settings → Audio → Audio tools."
            : Queue.Count == 0 ? "No local songs selected."
            : restored == 0 ? $"{queued} song{(queued == 1 ? "" : "s")} queued."
            : $"{restored} restored from last time · {queued} queued.";
        if (restored > 0)
            Selected = Queue.First(i => i.Status == StudioStatus.Ready);
    }

    /// <summary>
    /// Writes the item's current review to the draft store. When the item is the one on
    /// screen, the edited text and nudged times are what get kept.
    /// </summary>
    private void PersistDraft(StudioItem item)
    {
        if (_drafts is null || item.Result is not { } result) return;
        var onScreen = ReferenceEquals(Selected, item) && ReviewLines.Count > 0;
        // Model results are always kept; lyrics loaded from the song itself only once edited.
        if (item.Status == StudioStatus.Loaded ? !(onScreen && _reviewDirty) : item.Status != StudioStatus.Ready) return;
        IReadOnlyList<AlignedLine>? lines = null;
        if (onScreen)
        {
            lines = ReviewLines.Select(l => l.ToAlignedLine()).Where(l => l.Text.Length > 0).ToList();
            item.Result = result with { Lines = lines };
        }
        _drafts.Save(item.Track.Id, LyricsStudioDraft.From(item.Result, lines));
    }

    partial void OnSelectedModelChanged(WhisperModelInfo value)
    {
        RefreshModelState();
        PersistPrefs();
    }

    partial void OnSelectedLanguageChanged(SpeechLanguageOption value) => PersistPrefs();
    partial void OnWordTimingsChanged(bool value) { PersistPrefs(); OnPropertyChanged(nameof(ReviewConfidenceText)); }
    partial void OnSkipAlreadyTimedChanged(bool value) => PersistPrefs();
    partial void OnIsRunningChanged(bool value) { RaiseStartState(); OnPropertyChanged(nameof(ReviewCanUpgrade)); }
    partial void OnIsModelInstalledChanged(bool value) { RaiseStartState(); OnPropertyChanged(nameof(ShowModelDownload)); }
    partial void OnIsDownloadingModelChanged(bool value) => OnPropertyChanged(nameof(ShowModelDownload));

    partial void OnSelectedChanging(StudioItem? value)
    {
        if (Selected is { } leaving && !ReferenceEquals(leaving, value))
        {
            PersistDraft(leaving);
            CancelTap();
            SelectedWord = null;
        }
    }

    partial void OnSelectedChanged(StudioItem? value)
    {
        if (value is { Status: StudioStatus.Waiting, Result: null } fresh)
            TryLoadExisting(fresh);
        ReviewLines.Clear();
        _reviewDirty = false;
        if (value?.Result is { } result)
        {
            foreach (var line in result.Lines)
            {
                var review = new ReviewLine(line);
                review.Changed += MarkReviewDirty;
                ReviewLines.Add(review);
            }
        }
        RaiseReviewChanged();
    }

    /// <summary>Any text, time or word edit on the review on screen; drafts of loaded songs are only written when this is set.</summary>
    private bool _reviewDirty;
    private void MarkReviewDirty() => _reviewDirty = true;

    /// <summary>
    /// Selecting a song shows what it already has — .elrc, then .lrc, then embedded — with no
    /// Start press. A restored draft (already Ready) wins over the file.
    /// </summary>
    private void TryLoadExisting(StudioItem item)
    {
        ExistingLyrics? existing;
        try { existing = ExistingLyricsLoader.Load(item.Track); }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.LoadExistingFailed", ex.Message); return; }
        if (existing is null) return;
        item.Existing = existing;
        item.Result = new LyricsStudioResult(item.Track, existing.Lines, LyricsStudioSource.ExistingFile, 1, string.Empty, 0);
        item.Status = StudioStatus.Loaded;
        item.StatusText = existing.Format == LyricsFormat.Elrc
            ? $"Word timings loaded from {existing.Origin}"
            : $"Line timings loaded from {existing.Origin} · upgrade for word timings";
        RaiseStartState();
    }

    private void RaiseReviewChanged()
    {
        OnPropertyChanged(nameof(ReviewCanUpgrade));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(HasReview));
        OnPropertyChanged(nameof(ReviewTitle));
        OnPropertyChanged(nameof(ReviewSubtitle));
        OnPropertyChanged(nameof(ReviewSourceText));
        OnPropertyChanged(nameof(ReviewConfidenceText));
        OnPropertyChanged(nameof(ReviewIsTranscription));
        OnPropertyChanged(nameof(CanSave));
    }

    private void PersistPrefs()
    {
        if (_loadingPrefs) return;
        try { _savePrefs(new LyricsStudioPrefs(SelectedModel.Size.ToString(), SelectedLanguage.Code, WordTimings, SkipAlreadyTimed)); }
        catch { /* preferences are a convenience */ }
    }

    private void RefreshModelState()
    {
        IsModelInstalled = _engine.Models.IsInstalled(SelectedModel.Size);
        ModelStatusText = IsModelInstalled
            ? $"{SelectedModel.DisplayName} model installed · {SelectedModel.Description}"
            : $"{SelectedModel.DisplayName} model not installed ({SelectedModel.SizeText}) · {SelectedModel.Description}";
    }

    [RelayCommand]
    private async Task DownloadModel()
    {
        if (IsDownloadingModel) return;
        IsDownloadingModel = true;
        ModelProgress = 0;
        var model = SelectedModel;
        ModelStatusText = $"Downloading the {model.DisplayName} model ({model.SizeText})…";
        try
        {
            await _engine.Models.DownloadAsync(model.Size, new Progress<double>(p => Dispatcher.UIThread.Post(() => ModelProgress = p)), CancellationToken.None);
        }
        catch (Exception ex)
        {
            ModelStatusText = $"Download failed — {ex.Message}";
            IsDownloadingModel = false;
            return;
        }
        IsDownloadingModel = false;
        RefreshModelState();
    }

    [RelayCommand]
    private async Task Start()
    {
        if (!CanStart) return;
        var items = Queue.Where(i => i.Status == StudioStatus.Waiting).ToList();
        if (items.Count == 0)
        {
            // Nothing queued: "Re-sync" re-times the song on screen, after a warning.
            if (Selected is not { Status: StudioStatus.Loaded or StudioStatus.Ready } current) return;
            if (!await ConfirmAsync($"Re-sync will replace the timings shown for “{current.Title}” with a fresh run of the speech model.\n\nNothing is written to disk until you press Save lyrics."))
                return;
            Requeue(current);
            items.Add(current);
        }
        await RunAsync(items);
    }

    /// <summary>Times every word of the loaded line-level lyrics, using their text as the source.</summary>
    [RelayCommand]
    private async Task UpgradeToWordTimings()
    {
        if (!ReviewCanUpgrade || Selected is not { } item) return;
        if (!IsModelInstalled || !HasFfmpeg)
        {
            RunStatusText = !HasFfmpeg ? "ffmpeg is needed to decode songs — set its path under Settings → Audio → Audio tools." : $"Download the {SelectedModel.DisplayName} model first.";
            return;
        }
        WordTimings = true;
        Requeue(item);
        await RunAsync(new List<StudioItem> { item });
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        if (Confirm is null) return true;
        try { return await Confirm(message); } catch { return true; }
    }

    private void Requeue(StudioItem item)
    {
        item.ForceRun = true;
        item.Status = StudioStatus.Waiting;
        item.Result = null;
        if (ReferenceEquals(Selected, item)) ReviewLines.Clear();
        RaiseReviewChanged();
    }

    private async Task RunAsync(List<StudioItem> items)
    {
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        var done = 0;
        var total = items.Count;
        try
        {
            using var session = _engine.OpenSession(SelectedModel.Size);
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                if (item.Status != StudioStatus.Waiting) continue;
                var forced = item.ForceRun;
                item.ForceRun = false;
                // Loaded lyrics (sidecar or embedded) are the text to time; the engine only
                // looks online when the song has nothing at all.
                // Loaded line-level lyrics keep their line starts as anchors: words are placed inside each line's own window.
                var options = new LyricsStudioOptions(SelectedModel.Size, SelectedLanguage.Code, AllowOnlineLyrics: true, ForceTranscription: TranscribeOnly,
                    SourceLines: TranscribeOnly ? null : item.Existing?.Lines.Select(l => l.Text).ToList(),
                    SourceLineStarts: TranscribeOnly ? null : item.Existing?.Lines.Select(l => l.Start).ToList());
                if (!forced && SkipAlreadyTimed && !TranscribeOnly && LyricsFormatDetector.AlreadyHas(item.ExistingFormat, WordTimings))
                {
                    item.Status = StudioStatus.Skipped;
                    item.StatusText = $"Skipped · already {LyricsFormatDetector.Label(item.ExistingFormat)}";
                    done++;
                    continue;
                }
                item.Status = StudioStatus.Working;
                item.StatusText = "Starting…";
                RunStatusText = $"Working on {item.Title} ({done + 1} of {total})";
                var progress = new Progress<LyricsStudioProgress>(p => Dispatcher.UIThread.Post(() =>
                {
                    item.Progress = p.Fraction;
                    item.StatusText = p.Stage;
                }));
                try
                {
                    var result = await Task.Run(() => _engine.ProcessAsync(item.Track, options, progress, ct), ct);
                    item.Result = result;
                    item.Status = StudioStatus.Ready;
                    _drafts?.Save(item.Track.Id, LyricsStudioDraft.From(result));
                    item.StatusText = result.Source == LyricsStudioSource.Transcription ? "Transcribed · review" : $"{Math.Round(result.Confidence * 100)}% matched · review";
                    if (Selected is null || Selected.Status is not (StudioStatus.Ready))
                        Selected = item;
                    else if (ReferenceEquals(Selected, item))
                        OnSelectedChanged(item);
                }
                catch (OperationCanceledException)
                {
                    item.Status = StudioStatus.Waiting;
                    item.StatusText = "Stopped";
                    break;
                }
                catch (Exception ex)
                {
                    item.Status = StudioStatus.Failed;
                    item.StatusText = ex.Message;
                }
                done++;
            }
            RunStatusText = ct.IsCancellationRequested ? "Stopped." : $"Finished · {Queue.Count(i => i.Status == StudioStatus.Ready)} ready for review";
        }
        catch (Exception ex)
        {
            RunStatusText = $"Couldn't start — {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            RaiseStartState();
        }
    }

    [RelayCommand]
    private void Stop() => _runCts?.Cancel();

    [RelayCommand]
    private void Save()
    {
        if (Selected is not { Status: StudioStatus.Ready, Result: { } result } item || ReviewLines.Count == 0) return;
        var lines = ReviewLines.Select(l => l.ToAlignedLine()).Where(l => l.Text.Length > 0).ToList();
        var plain = TimedLyricsBuilder.BuildPlain(lines);
        var synced = WordTimings ? TimedLyricsBuilder.BuildElrc(lines) : TimedLyricsBuilder.BuildLrc(lines);
        bool embed;
        try { embed = _settings().LyricsStudioEmbedTags; } catch { embed = false; }
        try
        {
            var outcome = _writer.SaveDetailed(item.Track, plain, synced, embed, replaceForeignSidecar: true);
            item.Status = StudioStatus.Saved;
            _drafts?.Delete(item.Track.Id);
            item.Existing = null;
            item.RefreshExistingFormat();
            var format = WordTimings ? "word timings (ELRC)" : "line timings (LRC)";
            item.StatusText = !outcome.SidecarWritten ? $"Saved · {format} · no .lrc written"
                : outcome.ReplacedForeignSidecar ? $"Saved · {format} · old .lrc moved to the recycle bin"
                : $"Saved · {format}";
            _savedCount++;
            OnPropertyChanged(nameof(SummaryText));
        }
        catch (Exception ex)
        {
            item.StatusText = $"Save failed — {ex.Message}";
            return;
        }
        SelectNextReady(item);
        RaiseReviewChanged();
    }

    [RelayCommand]
    private void Skip()
    {
        if (Selected is not { } item) return;
        if (item.Status == StudioStatus.Ready)
        {
            item.Status = StudioStatus.Skipped;
            item.StatusText = "Skipped";
            _drafts?.Delete(item.Track.Id);
        }
        SelectNextReady(item);
        RaiseReviewChanged();
    }

    private void SelectNextReady(StudioItem after)
    {
        var idx = Queue.IndexOf(after);
        var next = Queue.Skip(idx + 1).FirstOrDefault(i => i.Status == StudioStatus.Ready)
                   ?? Queue.FirstOrDefault(i => i.Status == StudioStatus.Ready);
        Selected = next ?? after;
    }

    /// <summary>Re-run the selected song as pure transcription (when the source lyrics were wrong).</summary>
    [RelayCommand]
    private void RedoAsTranscription()
    {
        if (Selected is not { } item || IsRunning) return;
        item.Status = StudioStatus.Waiting;
        item.Result = null;
        item.StatusText = "Queued for transcription";
        _drafts?.Delete(item.Track.Id);
        TranscribeOnly = true;
        RaiseStartState();
        _ = Start();
    }

    [RelayCommand]
    private void NudgeEarlier() => Nudge(TimeSpan.FromMilliseconds(-100));

    [RelayCommand]
    private void NudgeLater() => Nudge(TimeSpan.FromMilliseconds(100));

    private void Nudge(TimeSpan delta)
    {
        foreach (var line in ReviewLines) line.Shift(delta);
    }

    // ── Words: selection, nudging, tap-to-time ─────────────────────────────

    /// <summary>The highlighted word chip; the ± buttons and Space (outside tap mode) act on it.</summary>
    [ObservableProperty] private ReviewWord? _selectedWord;

    partial void OnSelectedWordChanged(ReviewWord? oldValue, ReviewWord? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        OnPropertyChanged(nameof(HasSelectedWord));
    }

    public bool HasSelectedWord => SelectedWord is not null;

    /// <summary>Chip click: select the word and seek a little ahead of it so you hear it land.</summary>
    [RelayCommand]
    private Task SelectWord(ReviewWord? word)
    {
        if (word is null) return Task.CompletedTask;
        SelectedWord = word;
        return IsTapping ? Task.CompletedTask : PlayFromTime(word.Start - TimeSpan.FromSeconds(0.3));
    }

    [RelayCommand]
    private void NudgeWordEarlier() => SelectedWord?.Line.NudgeWord(SelectedWord, TimeSpan.FromMilliseconds(-50));

    [RelayCommand]
    private void NudgeWordLater() => SelectedWord?.Line.NudgeWord(SelectedWord, TimeSpan.FromMilliseconds(50));

    [RelayCommand]
    private void ToggleWords(ReviewLine? line)
    {
        if (line is null) return;
        line.IsExpanded = !line.IsExpanded;
        if (!line.IsExpanded && SelectedWord?.Line == line) SelectedWord = null;
        if (!line.IsExpanded && TapLine == line) CancelTap();
    }

    /// <summary>The line being tapped, or null. One line at a time: tap mode is a repair tool.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTapping))]
    [NotifyPropertyChangedFor(nameof(TapHint))]
    private ReviewLine? _tapLine;
    private int _tapIndex;

    public bool IsTapping => TapLine is not null;
    public string TapHint => TapLine is { } line
        ? (_tapIndex < line.Words.Count
            ? $"Tap for “{line.Words[_tapIndex].Text}” ({_tapIndex + 1} of {line.Words.Count}) · Space or the Tap button · Esc cancels"
            : "Tap once more where the line ends")
        : string.Empty;

    /// <summary>Starts (or restarts) tapping the line: plays from just before it and waits for the first word.</summary>
    [RelayCommand]
    private async Task StartTap(ReviewLine? line)
    {
        if (line is null || line.Words.Count == 0) return;
        if (TapLine == line) { CancelTap(); return; }
        if (TapLine is not null) CancelTap();
        line.IsExpanded = true;
        line.ClearTapMarks();
        TapLine = line;
        _tapIndex = 0;
        line.Words[0].IsTapTarget = true;
        SelectedWord = line.Words[0];
        OnPropertyChanged(nameof(TapHint));
        await PlayFromTime(line.Start - TimeSpan.FromSeconds(1.0));
    }

    /// <summary>Stamps the player's position on the next word; after the last word, one more tap sets the line end.</summary>
    [RelayCommand]
    private void Tap()
    {
        if (TapLine is not { } line || _player is null) return;
        var now = _player.Position;
        if (_tapIndex < line.Words.Count)
        {
            line.Words[_tapIndex].IsTapTarget = false;
            line.TapWord(_tapIndex, now);
            _tapIndex++;
            if (_tapIndex < line.Words.Count)
            {
                line.Words[_tapIndex].IsTapTarget = true;
                SelectedWord = line.Words[_tapIndex];
            }
            OnPropertyChanged(nameof(TapHint));
            return;
        }
        line.SetEnd(now);
        FinishTap();
    }

    [RelayCommand]
    private void CancelTap()
    {
        if (TapLine is { } line)
            foreach (var w in line.Words) w.IsTapTarget = false;
        TapLine = null;
        _tapIndex = 0;
    }

    private void FinishTap()
    {
        CancelTap();
        if (_player is { State: PlaybackState.Playing }) _player.PlayPauseCommand.Execute(null);
    }

    [RelayCommand]
    private Task PlayFromLine(ReviewLine? line) =>
        line is null ? Task.CompletedTask : PlayFromTime(line.Start - TimeSpan.FromSeconds(0.8));

    private async Task PlayFromTime(TimeSpan target)
    {
        if (_player is null || Selected is null) return;
        var track = Selected.Track;
        if (_player.CurrentTrack?.Id != track.Id)
        {
            _player.ReplaceQueueAndPlay(new[] { track }, 0);
            await Task.Delay(500);
        }
        var duration = _player.Duration.TotalSeconds > 0 ? _player.Duration : track.Duration;
        if (duration.TotalSeconds <= 0) return;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        _player.SeekToPositionCommand.Execute(target.TotalSeconds / duration.TotalSeconds);
        if (_player.State != PlaybackState.Playing)
            _player.PlayPauseCommand.Execute(null);
    }

    [RelayCommand]
    private async Task Close()
    {
        _runCts?.Cancel();
        if (Selected is { } current) PersistDraft(current);
        if (_savedCount > 0)
        {
            try { await _library.SaveAsync(); } catch { }
        }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary><see cref="Loaded"/> = existing timed lyrics shown for review without a model run.</summary>
    public enum StudioStatus { Waiting, Working, Ready, Saved, Skipped, Failed, Loaded }

    public sealed partial class StudioItem : ObservableObject
    {
        public StudioItem(Track track)
        {
            Track = track;
            _existingFormat = LyricsFormatDetector.Detect(track);
        }

        public Track Track { get; }

        /// <summary>What the song has right now — .elrc / .lrc sidecars and embedded tags, LRC and ELRC told apart.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateText))]
        private LyricsFormat _existingFormat;

        /// <summary>Short state pill for the song list.</summary>
        public string StateText => ExistingFormat switch
        {
            LyricsFormat.Elrc => "word-level",
            LyricsFormat.Lrc => "line-level",
            LyricsFormat.Plain => "plain only",
            _ => "no lyrics",
        };

        public void RefreshExistingFormat()
        {
            try { ExistingFormat = LyricsFormatDetector.Detect(Track); } catch { }
        }

        /// <summary>Timed lyrics loaded from the song itself (null until selected, or when it has none).</summary>
        public ExistingLyrics? Existing { get; set; }

        /// <summary>Set by Re-sync / Upgrade so the "skip songs that already have this format" rule does not apply.</summary>
        public bool ForceRun { get; set; }
        public string Title => Track.Title;
        public string Subtitle => Track.ArtistDisplay;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsWorking))]
        [NotifyPropertyChangedFor(nameof(IsReady))]
        [NotifyPropertyChangedFor(nameof(IsSaved))]
        [NotifyPropertyChangedFor(nameof(IsFailed))]
        private StudioStatus _status = StudioStatus.Waiting;
        [ObservableProperty] private string _statusText = string.Empty;
        [ObservableProperty] private double _progress;
        [ObservableProperty] private LyricsStudioResult? _result;

        public bool IsWorking => Status == StudioStatus.Working;
        public bool IsReady => Status == StudioStatus.Ready;
        public bool IsSaved => Status == StudioStatus.Saved;
        public bool IsFailed => Status == StudioStatus.Failed;
    }
}
