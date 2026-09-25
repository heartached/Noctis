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
public sealed record LyricsStudioPrefs(string Model, string Language, bool WordTimings, bool SkipAlreadyTimed, bool EmbedTags, bool OnlineLyrics = true);

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
    /// <summary>Also write the plain lyrics into the audio file's tags on save (was a Settings toggle).</summary>
    [ObservableProperty] private bool _embedTags;
    /// <summary>Songs with no lyrics: fetch the plain text online (LRCLIB) so the model only has to time it.</summary>
    [ObservableProperty] private bool _onlineLyrics;

    // ── Model state ──
    [ObservableProperty] private bool _isModelInstalled;
    [ObservableProperty] private string _modelStatusText = string.Empty;
    [ObservableProperty] private bool _isDownloadingModel;
    [ObservableProperty] private double _modelProgress;

    // ── Run state ──
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _runStatusText = string.Empty;

    /// <summary>The format picker's second chip: line timings (LRC) = word timings off.</summary>
    public bool LineTimings
    {
        get => !WordTimings;
        set => WordTimings = !value;
    }

    /// <summary>Queue header note when every unrun song has the same format ("all LRC"); the rows then drop their own pill.</summary>
    [ObservableProperty] private string _queueFormatText = string.Empty;

    public bool HasFfmpeg => _engine.HasFfmpeg;
    public bool CanStart => !IsRunning && IsModelInstalled && HasFfmpeg
        && (Queue.Any(i => i.Status == StudioStatus.Waiting) || Selected is { Status: StudioStatus.Loaded or StudioStatus.Ready });
    /// <summary>"Start" runs the queue; with nothing queued the button re-times the song on screen.</summary>
    public string StartLabel => Queue.Any(i => i.Status == StudioStatus.Waiting) ? "Start" : Selected is { Status: StudioStatus.Loaded or StudioStatus.Ready } ? "Re-sync" : "Start";
    private void RaiseStartState() { OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(StartLabel)); RefreshQueuePills(); }

    /// <summary>
    /// The format pill only earns its place on the songs that differ: the page queues the songs
    /// missing one format, so most say the same thing. The most common format among the unrun
    /// songs is said once in the queue header ("all LRC" / "LRC unless marked") and only the
    /// other songs keep a pill; work state (Ready, Saved, Needs lyrics…) always shows.
    /// </summary>
    private void RefreshQueuePills()
    {
        var unrun = Queue.Where(i => i.Status is StudioStatus.Waiting or StudioStatus.Loaded).ToList();
        var common = unrun.GroupBy(i => i.ExistingFormat).OrderByDescending(g => g.Count()).FirstOrDefault();
        var hide = common is { } g && g.Count() > 1 ? g.Key : (LyricsFormat?)null;
        foreach (var i in Queue) i.HideFormatPill = hide is { } f && i.ExistingFormat == f;
        QueueFormatText = hide is not { } shared ? string.Empty
            : common!.Count() == unrun.Count ? $"· all {StudioItem.FormatTag(shared)}"
            : $"· {StudioItem.FormatTag(shared)} unless marked";
    }
    public bool ShowModelDownload => !IsModelInstalled && !IsDownloadingModel;

    // ── Review ──
    public bool HasReview => Selected is { Status: StudioStatus.Ready or StudioStatus.Saved or StudioStatus.Loaded, Result: not null };
    /// <summary>Line-level lyrics were loaded: offer to time every word.</summary>
    public bool ReviewCanUpgrade => !IsRunning && Selected is { Status: StudioStatus.Loaded, Existing.Format: LyricsFormat.Lrc };
    public string ReviewTitle => Selected?.Title ?? string.Empty;
    public string ReviewSubtitle => Selected?.Subtitle ?? string.Empty;
    public bool ReviewIsExplicit => Selected?.Track.IsExplicit == true;
    public string ReviewSourceText => Selected?.Result?.Source switch
    {
        LyricsStudioSource.ExistingLyrics => "From the song's lyrics",
        LyricsStudioSource.Lrclib => "From LRCLIB",
        LyricsStudioSource.Transcription => "Transcribed · check the words",
        LyricsStudioSource.PastedLyrics => "From your lyrics",
        LyricsStudioSource.ExistingFile => Selected?.Existing is { } e
            ? $"From {e.Origin} · {(e.Format == LyricsFormat.Elrc ? "word timings" : "line timings")}"
            : "Loaded",
        _ => string.Empty,
    };
    public string ReviewConfidenceText => Selected?.Result is { } r
        ? (r.Source == LyricsStudioSource.ExistingFile ? string.Empty : $"{Math.Round(r.Confidence * 100)}% heard · ")
          + $"{r.Lines.Count} lines · saves {(WordTimings ? "ELRC" : "LRC")}"
        : string.Empty;
    public bool ReviewIsTranscription => Selected?.Result?.Source == LyricsStudioSource.Transcription;
    public bool CanSave => Selected is { Status: StudioStatus.Ready or StudioStatus.Loaded } && ReviewLines.Count > 0;

    // ── Compose: a song with no timed lyrics yet (paste / import / experimental transcript) ──

    /// <summary>The selected song has nothing to review: show the lyrics box instead.</summary>
    public bool IsComposing => !HasReview && Selected is { Status: StudioStatus.Waiting or StudioStatus.NeedsLyrics or StudioStatus.Failed };
    /// <summary>The selected song is being run: the review pane shows its progress.</summary>
    public bool IsSelectedWorking => Selected is { Status: StudioStatus.Working };
    public bool ShowReviewEmpty => !HasReview && !IsComposing && !IsSelectedWorking;
    public bool CanAlignDraft => !IsRunning && Selected is { } s && !string.IsNullOrWhiteSpace(s.DraftText);
    public bool CanTranscribeDraft => !IsRunning && IsModelInstalled && HasFfmpeg;

    /// <summary>Set by the panel: a file picker for a lyrics file (.txt / .lrc / .elrc). Null = no import.</summary>
    public Func<Task<string?>>? PickLyricsFile { get; set; }

    /// <summary>
    /// Set by the panel: shows these synced lyrics (unsaved) for the track on the real lyrics page
    /// and opens it. Null = no preview (the per-song dialog has no lyrics page behind it).
    /// </summary>
    public Func<Track, string, Task>? ShowOnLyricsPage
    {
        get => _showOnLyricsPage;
        set { _showOnLyricsPage = value; OnPropertyChanged(nameof(CanPreview)); }
    }
    private Func<Track, string, Task>? _showOnLyricsPage;
    public bool CanPreview => _showOnLyricsPage is not null;
    /// <summary>Set with <see cref="ShowOnLyricsPage"/>: drops the preview so the lyrics page reloads the saved lyrics.</summary>
    public Action? ClearLyricsPagePreview { get; set; }
    public string SummaryText => _savedCount == 0 ? string.Empty : $"{_savedCount} saved";
    /// <summary>Lyrics written this session; the page rescans the library on its next visit when > 0.</summary>
    public int SavedCount => _savedCount;

    public event EventHandler? Closed;

    /// <summary>
    /// Appends songs the user picked (Choose songs) to a Studio that is mid-run or mid-review,
    /// so the work on screen is kept. Non-local songs and songs already queued are skipped;
    /// an unfinished review of a picked song comes back as it was, like at open.
    /// </summary>
    public int AddTracks(IEnumerable<Track> tracks)
    {
        var added = 0;
        foreach (var t in tracks)
        {
            if (t.SourceType != SourceType.Local || Queue.Any(i => i.Track.Id == t.Id)) continue;
            var item = new StudioItem(t);
            if (_drafts is not null && _drafts.TryLoad(t.Id, out var draft))
            {
                item.Result = draft.ToResult(t);
                item.Status = StudioStatus.Ready;
                item.StatusText = "Restored · review";
            }
            Queue.Add(item);
            added++;
        }
        if (added > 0)
        {
            var queued = Queue.Count(i => i.Status == StudioStatus.Waiting);
            RunStatusText = $"{added} added · {queued} queued";
        }
        return added;
    }

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
        _embedTags = s.LyricsStudioEmbedTags;
        _onlineLyrics = s.LyricsStudioOnlineLyrics;
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
                item.StatusText = "Restored · review";
                restored++;
            }
            Queue.Add(item);
        }
        Queue.CollectionChanged += (_, _) => RaiseStartState();
        RefreshQueuePills();

        RefreshModelState();
        var queued = Queue.Count(i => i.Status == StudioStatus.Waiting);
        RunStatusText = !HasFfmpeg
            ? "ffmpeg is needed to decode songs — set its path under Settings → Advanced → Helper programs."
            : Queue.Count == 0 ? "No local songs selected."
            : restored == 0 ? $"{queued} song{(queued == 1 ? "" : "s")} queued"
            : $"{restored} restored · {queued} queued";
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
    partial void OnWordTimingsChanged(bool value)
    {
        PersistPrefs();
        OnPropertyChanged(nameof(ReviewConfidenceText));
        OnPropertyChanged(nameof(LineTimings));
        // The rows show the chosen format: word tags for ELRC, plain text for LRC.
        foreach (var line in ReviewLines) line.ShowWordTags = value;
    }
    partial void OnSkipAlreadyTimedChanged(bool value) => PersistPrefs();
    partial void OnEmbedTagsChanged(bool value) => PersistPrefs();
    partial void OnOnlineLyricsChanged(bool value) => PersistPrefs();
    partial void OnIsRunningChanged(bool value)
    {
        RaiseStartState();
        OnPropertyChanged(nameof(ReviewCanUpgrade));
        OnPropertyChanged(nameof(CanAlignDraft));
        OnPropertyChanged(nameof(CanTranscribeDraft));
    }
    partial void OnIsModelInstalledChanged(bool value) { RaiseStartState(); OnPropertyChanged(nameof(ShowModelDownload)); OnPropertyChanged(nameof(CanTranscribeDraft)); }
    partial void OnIsDownloadingModelChanged(bool value) => OnPropertyChanged(nameof(ShowModelDownload));

    partial void OnSelectedChanging(StudioItem? value)
    {
        if (Selected is { } leaving && !ReferenceEquals(leaving, value))
        {
            PersistDraft(leaving);
            CancelTap();
            SelectedWord = null;
            leaving.PropertyChanged -= OnSelectedItemPropertyChanged;
        }
    }

    partial void OnSelectedChanged(StudioItem? oldValue, StudioItem? newValue)
    {
        // The one-argument overload (below) has already rebuilt the review.
        if (newValue is not null && !ReferenceEquals(oldValue, newValue))
            newValue.PropertyChanged += OnSelectedItemPropertyChanged;
    }

    private void OnSelectedItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StudioItem.DraftText)) OnPropertyChanged(nameof(CanAlignDraft));
        else if (e.PropertyName == nameof(StudioItem.Status)) RaiseReviewChanged();
    }

    partial void OnSelectedChanged(StudioItem? value)
    {
        if (value is { Status: StudioStatus.Waiting, Result: null } fresh)
            TryLoadExisting(fresh);
        if (value is { Result: null } compose)
            PrefillDraft(compose);
        ReviewLines.Clear();
        _reviewDirty = false;
        if (value?.Result is { } result)
        {
            foreach (var line in result.Lines)
            {
                var review = new ReviewLine(line) { ShowWordTags = WordTimings };
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
        var existing = LoadExistingOrNull(item.Track);
        if (existing is null) return;
        item.Existing = existing;
        item.Result = new LyricsStudioResult(item.Track, existing.Lines, LyricsStudioSource.ExistingFile, 1, string.Empty, 0);
        item.Status = StudioStatus.Loaded;
        // The pill and the review's source line already say what was loaded and from where.
        item.StatusText = string.Empty;
        RaiseStartState();
    }

    /// <summary>The song's own timed lyrics (.elrc, .lrc, embedded), or null when it has none or they can't be read.</summary>
    private static ExistingLyrics? LoadExistingOrNull(Track track)
    {
        try { return ExistingLyricsLoader.Load(track); }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.LoadExistingFailed", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Fills an empty lyrics box with the song's own untimed lyrics: the plain lyrics tag, else a
    /// .txt next to the audio file. Never overwrites what the user typed or a transcript.
    /// </summary>
    private static void PrefillDraft(StudioItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.DraftText)) return;
        try
        {
            var text = LyricsStudioEngine.FirstText(item.Track.Lyrics);
            if (text is null && !string.IsNullOrWhiteSpace(item.Track.FilePath))
            {
                var txt = Path.ChangeExtension(item.Track.FilePath, ".txt");
                if (File.Exists(txt)) text = LyricsStudioEngine.FirstText(File.ReadAllText(txt));
            }
            if (text is not null) item.DraftText = string.Join('\n', text);
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.PrefillFailed", ex.Message);
        }
    }

    private void RaiseReviewChanged()
    {
        OnPropertyChanged(nameof(ReviewCanUpgrade));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(HasReview));
        OnPropertyChanged(nameof(ReviewTitle));
        OnPropertyChanged(nameof(ReviewSubtitle));
        OnPropertyChanged(nameof(ReviewIsExplicit));
        OnPropertyChanged(nameof(ReviewSourceText));
        OnPropertyChanged(nameof(ReviewConfidenceText));
        OnPropertyChanged(nameof(ReviewIsTranscription));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(IsComposing));
        OnPropertyChanged(nameof(IsSelectedWorking));
        OnPropertyChanged(nameof(ShowReviewEmpty));
        OnPropertyChanged(nameof(CanAlignDraft));
    }

    private void PersistPrefs()
    {
        if (_loadingPrefs) return;
        try { _savePrefs(new LyricsStudioPrefs(SelectedModel.Size.ToString(), SelectedLanguage.Code, WordTimings, SkipAlreadyTimed, EmbedTags, OnlineLyrics)); }
        catch { /* preferences are a convenience */ }
    }

    private void RefreshModelState()
    {
        IsModelInstalled = _engine.Models.IsInstalled(SelectedModel.Size);
        ModelStatusText = IsModelInstalled
            ? $"{SelectedModel.DisplayName} installed"
            : $"{SelectedModel.DisplayName} not installed · {SelectedModel.SizeText}";
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
        // The song the user clicked goes first (user ask 09-19); the rest follow in queue order.
        if (Selected is { Status: StudioStatus.Waiting } chosen && items.Remove(chosen))
            items.Insert(0, chosen);
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
            RunStatusText = !HasFfmpeg ? "ffmpeg is needed to decode songs — set its path under Settings → Advanced → Helper programs." : $"Download the {SelectedModel.DisplayName} model first.";
            return;
        }
        WordTimings = true;
        // Time the lines on screen: a text fix or a whole-song shift made in the review is kept
        // (it used to re-read the file and silently drop them).
        if (_reviewDirty && item.Existing is { } loaded && ReviewLines.Count > 0)
            item.Existing = loaded with { Lines = ReviewLines.Select(l => l.ToAlignedLine()).Where(l => l.Text.Length > 0).OrderBy(l => l.Start).ToList() };
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
        // Session-log breadcrumbs (Settings > Advanced > Copy Logs): a native crash inside the
        // speech model leaves no managed trace, so the run's own steps are the only record.
        DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.RunStart",
            $"songs={total}, model={SelectedModel.Size}, language={SelectedLanguage.Code}, wordTimings={WordTimings}, transcribeOnly={TranscribeOnly}, online={OnlineLyrics}, skipDone={SkipAlreadyTimed}");
        try
        {
            using var session = _engine.OpenSession(SelectedModel.Size);
            DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.SessionOpen", SelectedModel.Size.ToString());
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                if (item.Status != StudioStatus.Waiting) continue;
                var forced = item.ForceRun;
                item.ForceRun = false;
                var transcribe = item.TranscribeNext || TranscribeOnly;
                var pasted = item.SourceOverride;
                item.TranscribeNext = false;
                item.SourceOverride = null;
                // A queued song that was never opened has not loaded its timed lyrics yet; without
                // them the engine re-reads only the text and every line start is lost (09-24: a
                // batch run placed Cherry Blossom's 0:16–0:28 lines at 0:00).
                if (!transcribe && pasted is null && item.Existing is null)
                    item.Existing = LoadExistingOrNull(item.Track);
                // Pasted lyrics win; else loaded lyrics (sidecar or embedded) are the text to
                // time, and the engine only looks online when the song has nothing at all. A song
                // with no lyrics anywhere stops at "Needs lyrics" rather than being guessed by ear.
                // Loaded line-level lyrics keep their line starts as anchors: words are placed inside each line's own window.
                var options = new LyricsStudioOptions(SelectedModel.Size, SelectedLanguage.Code, AllowOnlineLyrics: OnlineLyrics, ForceTranscription: transcribe,
                    SourceLines: transcribe ? null : pasted ?? item.Existing?.Lines.Select(l => l.Text).ToList(),
                    SourceLineStarts: transcribe || pasted is not null ? null : item.Existing?.Lines.Select(l => l.Start).ToList(),
                    AllowTranscription: false);
                if (!forced && SkipAlreadyTimed && !transcribe && LyricsFormatDetector.AlreadyHas(item.ExistingFormat, WordTimings))
                {
                    item.Status = StudioStatus.Skipped;
                    item.StatusText = $"Skipped · already {LyricsFormatDetector.Label(item.ExistingFormat)}";
                    DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.ItemSkipped", $"{item.Title} | {item.ExistingFormat}");
                    done++;
                    continue;
                }
                item.Status = StudioStatus.Working;
                item.StatusText = "Starting…";
                RunStatusText = $"Working on {item.Title} ({done + 1} of {total})";
                DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.ItemStart",
                    $"{item.Title} ({done + 1}/{total}) | existing={item.ExistingFormat}, sourceLines={(options.SourceLines?.Count.ToString() ?? "none")}");
                var progress = new Progress<LyricsStudioProgress>(p => Dispatcher.UIThread.Post(() =>
                {
                    // Reports are posted, so the last ones land after the run has already set the
                    // outcome; they must not turn "No lyrics found" back into "Finding lyrics".
                    if (item.Status != StudioStatus.Working) return;
                    item.Progress = p.Fraction;
                    item.StatusText = p.Stage;
                }));
                try
                {
                    var result = await Task.Run(() => _engine.ProcessAsync(item.Track, options, progress, ct), ct);
                    if (result.Source == LyricsStudioSource.Transcription)
                    {
                        // Experimental: the transcript goes into the lyrics box to be corrected;
                        // Align then re-times the corrected words against what was heard.
                        item.HeardWords = result.Heard;
                        item.DraftText = string.Join('\n', result.Lines.Select(l => l.Text));
                        item.IsTranscriptDraft = true;
                        item.Result = null;
                        item.Status = StudioStatus.NeedsLyrics;
                        item.StatusText = "Transcript · fix the words, then Align";
                        DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.ItemTranscribed",
                            $"{item.Title} | lines={result.Lines.Count}, heard={result.HeardWords}");
                        if (Selected is null || Selected.Status is not StudioStatus.Ready)
                            Selected = item;
                        done++;
                        continue;
                    }
                    if (pasted is not null) result = result with { Source = LyricsStudioSource.PastedLyrics };
                    item.Result = result;
                    item.Status = StudioStatus.Ready;
                    _drafts?.Save(item.Track.Id, LyricsStudioDraft.From(result));
                    item.StatusText = result.Source == LyricsStudioSource.Transcription ? "Transcribed · review" : $"{Math.Round(result.Confidence * 100)}% matched · review";
                    DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.ItemReady",
                        $"{item.Title} | source={result.Source}, lines={result.Lines.Count}, confidence={result.Confidence:0.00}, heard={result.HeardWords}");
                    if (Selected is null || Selected.Status is not (StudioStatus.Ready))
                        Selected = item;
                    else if (ReferenceEquals(Selected, item))
                        OnSelectedChanged(item);
                }
                catch (LyricsStudioNeedsLyricsException)
                {
                    item.Status = StudioStatus.NeedsLyrics;
                    item.StatusText = "No lyrics found · paste or import them";
                    DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.ItemNeedsLyrics", item.Title);
                }
                catch (OperationCanceledException)
                {
                    item.Status = StudioStatus.Waiting;
                    item.StatusText = "Stopped";
                    DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.ItemStopped", item.Title);
                    break;
                }
                catch (Exception ex)
                {
                    item.Status = StudioStatus.Failed;
                    item.StatusText = ex.Message;
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.ItemFailed", $"{item.Title} | {ex.GetType().Name}: {ex.Message}");
                }
                done++;
            }
            var needLyrics = Queue.Count(i => i.Status == StudioStatus.NeedsLyrics);
            RunStatusText = ct.IsCancellationRequested ? "Stopped."
                : $"Finished · {Queue.Count(i => i.Status == StudioStatus.Ready)} ready for review" + (needLyrics > 0 ? $" · {needLyrics} need lyrics" : string.Empty);
            DebugLogger.Info(DebugLogger.Category.Lyrics, ct.IsCancellationRequested ? "LyricsStudio.RunStopped" : "LyricsStudio.RunFinished",
                $"done={done}/{total}, ready={Queue.Count(i => i.Status == StudioStatus.Ready)}");
            DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.SessionClosing", "disposing the speech model");
        }
        catch (Exception ex)
        {
            RunStatusText = $"Couldn't start — {ex.Message}";
            DebugLogger.Error(DebugLogger.Category.Lyrics, "LyricsStudio.RunFailed", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            RaiseStartState();
            DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.RunEnd", "session disposed");
        }
    }

    [RelayCommand]
    private void Stop()
    {
        DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.StopRequested", RunStatusText);
        _runCts?.Cancel();
    }

    [RelayCommand]
    private async Task Save()
    {
        // Loaded = the song's own timed lyrics, possibly edited or shifted here: Save writes them too.
        if (Selected is not { Status: StudioStatus.Ready or StudioStatus.Loaded, Result: not null } item || ReviewLines.Count == 0) return;
        // Never replace lyrics without asking: sidecar files, and with "write into the file's tags"
        // on, the lyrics already stored in the audio file's tags too.
        var existing = ExistingLyricsFiles(item.Track);
        if (EmbedTags && (!string.IsNullOrWhiteSpace(item.Track.Lyrics) || !string.IsNullOrWhiteSpace(item.Track.SyncedLyrics)))
            existing.Add("lyrics in the audio file's tags");
        if (existing.Count > 0 && !await ConfirmAsync(
                $"“{item.Title}” already has {string.Join(" and ", existing)}.\n\nSaving replaces {(existing.Count == 1 ? "it" : "them")} with the lyrics on screen. A lyrics file Noctis didn't write goes to the Recycle Bin first."))
            return;
        if (!ReferenceEquals(Selected, item)) return;
        var lines = ReviewLines.Select(l => l.ToAlignedLine()).Where(l => l.Text.Length > 0).ToList();
        var plain = TimedLyricsBuilder.BuildPlain(lines);
        var synced = WordTimings ? TimedLyricsBuilder.BuildElrc(lines) : TimedLyricsBuilder.BuildLrc(lines);
        var embed = EmbedTags;
        try
        {
            // Off the UI thread: trashing the old file can wait on the OS (macOS asks Finder, up to 15 s).
            var outcome = await Task.Run(() => _writer.SaveDetailed(item.Track, plain, synced, embed, replaceForeignSidecar: true));
            item.Status = StudioStatus.Saved;
            _drafts?.Delete(item.Track.Id);
            item.Existing = null;
            item.RefreshExistingFormat();
            // ELRC with no word timed yet is written as plain LRC; say what actually landed.
            var format = !WordTimings ? "line timings (LRC)"
                : lines.Any(l => l.Words.Count > 0) ? "word timings (ELRC)"
                : "line timings (LRC) · no words timed yet";
            item.StatusText = outcome.KeptForeignSidecar ? $"Saved · {format} · old .lrc kept, couldn't move it to the recycle bin"
                : !outcome.SidecarWritten ? $"Saved · {format} · no .lrc written"
                : outcome.ReplacedForeignSidecar ? $"Saved · {format} · old .lrc moved to the recycle bin"
                : $"Saved · {format}";
            _savedCount++;
            OnPropertyChanged(nameof(SummaryText));
            // A preview of this review would now hide the file just written.
            try { ClearLyricsPagePreview?.Invoke(); } catch { }
        }
        catch (Exception ex)
        {
            item.StatusText = $"Save failed — {ex.Message}";
            return;
        }
        RefreshQueuePills();
        SelectNextReady(item);
        RaiseReviewChanged();
    }

    [RelayCommand]
    private void Skip()
    {
        if (Selected is not { } item) return;
        if (item.Status is StudioStatus.Ready or StudioStatus.NeedsLyrics)
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

    /// <summary>The review on screen, as the synced text Save would write in the chosen format (ELRC or LRC).</summary>
    internal string BuildReviewText()
    {
        var lines = ReviewLines.Select(l => l.ToAlignedLine()).Where(l => l.Text.Length > 0).ToList();
        return WordTimings ? TimedLyricsBuilder.BuildElrc(lines) : TimedLyricsBuilder.BuildLrc(lines);
    }

    /// <summary>
    /// Test the timings where they will be seen: plays the song and opens the lyrics page showing
    /// the review on screen, unsaved, in the chosen format (word-by-word for ELRC, line-by-line
    /// for LRC). Nothing is written; the song's real lyrics come back when the track changes.
    /// </summary>
    [RelayCommand]
    private async Task PreviewOnLyricsPage()
    {
        if (Selected is not { Result: not null } item || ReviewLines.Count == 0 || ShowOnLyricsPage is null) return;
        PersistDraft(item);
        var synced = BuildReviewText();
        if (_player is not null)
        {
            if (_player.CurrentTrack?.Id != item.Track.Id)
                await PlayFromTime(ReviewLines[0].Start - TimeSpan.FromSeconds(2));
            else if (_player.State != PlaybackState.Playing)
                _player.PlayPauseCommand.Execute(null);
        }
        try { await ShowOnLyricsPage(item.Track, synced); }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.PreviewFailed", ex.Message); }
    }

    /// <summary>The .elrc / .lrc files a save of this song would replace (names only, for the prompt).</summary>
    internal static List<string> ExistingLyricsFiles(Track track)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(track.FilePath)) return found;
        foreach (var ext in new[] { ".elrc", ".lrc" })
        {
            var path = Path.ChangeExtension(track.FilePath, ext);
            try { if (File.Exists(path)) found.Add($"“{Path.GetFileName(path)}”"); } catch { }
        }
        return found;
    }

    /// <summary>
    /// Experimental: re-run the selected song as pure transcription (when the source lyrics were
    /// wrong). The transcript lands in the lyrics box to be corrected before it is aligned.
    /// </summary>
    [RelayCommand]
    private Task RedoAsTranscription() => TranscribeDraft();

    /// <summary>
    /// Experimental: listen to the song with no lyrics to go on. Sung vocals often come out wrong,
    /// so the words go into the lyrics box for the user to fix, then Align times them.
    /// </summary>
    [RelayCommand]
    private async Task TranscribeDraft()
    {
        if (Selected is not { } item || IsRunning) return;
        if (!IsModelInstalled || !HasFfmpeg)
        {
            item.StatusText = !HasFfmpeg ? "ffmpeg is needed to decode songs — set its path under Settings → Advanced → Helper programs." : $"Download the {SelectedModel.DisplayName} model first (Settings).";
            return;
        }
        _drafts?.Delete(item.Track.Id);
        item.TranscribeNext = true;
        Requeue(item);
        item.StatusText = "Queued for transcription";
        await RunAsync(new List<StudioItem> { item });
    }

    /// <summary>Loads a lyrics file (.txt, or .lrc/.elrc whose timestamps are dropped) into the lyrics box.</summary>
    [RelayCommand]
    private async Task ImportLyricsFile()
    {
        if (Selected is not { } item || PickLyricsFile is null) return;
        string? path;
        try { path = await PickLyricsFile(); } catch { return; }
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var lines = LyricsStudioEngine.FirstText(await File.ReadAllTextAsync(path));
            if (lines is null) { item.StatusText = $"“{Path.GetFileName(path)}” has no lyrics in it."; return; }
            item.DraftText = string.Join('\n', lines);
            item.IsTranscriptDraft = false;
            item.StatusText = $"From {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            item.StatusText = $"Couldn't read the file — {ex.Message}";
        }
    }

    /// <summary>
    /// Times the lyrics in the box against the song (forced alignment), then opens the result for
    /// review. A song that was already transcribed is aligned against what the model heard, with
    /// no second listen; otherwise the model runs with these lyrics as its guide.
    /// </summary>
    [RelayCommand]
    private async Task AlignDraft()
    {
        if (Selected is not { } item || IsRunning) return;
        var lines = LyricsStudioEngine.FirstText(item.DraftText);
        if (lines is null)
        {
            item.StatusText = "Paste the lyrics first, one line per lyric line.";
            return;
        }
        if (item.HeardWords is { Count: > 0 } heard)
        {
            var duration = item.Track.Duration > TimeSpan.Zero ? item.Track.Duration : (TimeSpan?)null;
            var aligned = await Task.Run(() => LyricsAligner.Align(lines, heard, duration));
            var confidence = aligned.Count == 0 ? 0 : aligned.Average(l => l.Confidence);
            var result = new LyricsStudioResult(item.Track, aligned, LyricsStudioSource.PastedLyrics, confidence, string.Empty, heard.Count, heard);
            item.Result = result;
            item.Status = StudioStatus.Ready;
            item.IsTranscriptDraft = false;
            item.StatusText = $"{Math.Round(confidence * 100)}% matched · review";
            _drafts?.Save(item.Track.Id, LyricsStudioDraft.From(result));
            OnSelectedChanged(item);
            RaiseStartState();
            return;
        }
        if (!IsModelInstalled || !HasFfmpeg)
        {
            item.StatusText = !HasFfmpeg ? "ffmpeg is needed to decode songs — set its path under Settings → Advanced → Helper programs." : $"Download the {SelectedModel.DisplayName} model first (Settings).";
            return;
        }
        Requeue(item);
        item.SourceOverride = lines;
        item.StatusText = "Queued";
        await RunAsync(new List<StudioItem> { item });
    }

    [RelayCommand]
    private void NudgeEarlier() => Nudge(TimeSpan.FromMilliseconds(-100));

    [RelayCommand]
    private void NudgeLater() => Nudge(TimeSpan.FromMilliseconds(100));

    /// <summary>Shifts the whole song; an earlier shift stops when the first line reaches 0:00, so the gaps between lines never change.</summary>
    private void Nudge(TimeSpan delta)
    {
        if (ReviewLines.Count == 0) return;
        var first = ReviewLines.Min(l => l.Start);
        if (first + delta < TimeSpan.Zero) delta = -first;
        if (delta == TimeSpan.Zero) return;
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

    /// <summary>The time pill plays the line from its own timestamp, no pre-roll (user ask 09-17).</summary>
    [RelayCommand]
    private Task PlayFromLine(ReviewLine? line) =>
        line is null ? Task.CompletedTask : PlayFromTime(line.Start);

    private async Task PlayFromTime(TimeSpan target)
    {
        if (_player is null || Selected is null) return;
        var track = Selected.Track;
        if (_player.CurrentTrack?.Id != track.Id)
        {
            _player.ReplaceQueueAndPlay(new[] { track }, 0);
            // Wait for the engine to report the real length rather than a fixed delay: seeking
            // by a fraction of the tag's Duration against the engine's landed a little off.
            for (var i = 0; i < 40 && _player.Duration <= TimeSpan.Zero; i++)
                await Task.Delay(50);
        }
        if (_player.Duration <= TimeSpan.Zero) return;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        _player.SeekTo(target);
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

    /// <summary><see cref="Loaded"/> = existing timed lyrics shown for review without a model run.
    /// <see cref="NeedsLyrics"/> = no lyrics found, or a transcript waiting to be corrected: the lyrics box is shown.</summary>
    public enum StudioStatus { Waiting, Working, Ready, Saved, Skipped, Failed, Loaded, NeedsLyrics }

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

        /// <summary>Short state pill for the song list: the work state once the song has moved, else what it has now.</summary>
        public string StateText => Status switch
        {
            StudioStatus.Working => "Working",
            StudioStatus.Ready => "Ready to review",
            StudioStatus.Saved => "Saved",
            StudioStatus.Skipped => "Skipped",
            StudioStatus.Failed => "Failed",
            StudioStatus.NeedsLyrics => IsTranscriptDraft ? "Transcript" : "Needs lyrics",
            _ => FormatTag(ExistingFormat),
        };

        public static string FormatTag(LyricsFormat format) => format switch
        {
            LyricsFormat.Elrc => "ELRC",
            LyricsFormat.Lrc => "LRC",
            LyricsFormat.Plain => "Plain",
            _ => "No lyrics",
        };

        /// <summary>Set by the Studio when every unrun song shares one format (the queue header says it instead).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowStatePill))]
        private bool _hideFormatPill;

        public bool ShowStatePill => Status is not (StudioStatus.Waiting or StudioStatus.Loaded) || !HideFormatPill;
        public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

        /// <summary>The lyrics box: pasted, imported, prefilled from the song's plain lyrics, or a transcript to correct.</summary>
        [ObservableProperty] private string _draftText = string.Empty;
        /// <summary>The lyrics box holds an (experimental) transcript rather than the user's lyrics.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateText))]
        private bool _isTranscriptDraft;
        /// <summary>What the model heard on its last run of this song; Align re-times corrected text against it without a second listen.</summary>
        public IReadOnlyList<RecognizedWord>? HeardWords { get; set; }
        /// <summary>Lyrics to time on the next run instead of the song's own (Align).</summary>
        public List<string>? SourceOverride { get; set; }
        /// <summary>The next run transcribes this song (experimental), whatever the Studio's option says.</summary>
        public bool TranscribeNext { get; set; }

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
        [NotifyPropertyChangedFor(nameof(StateText))]
        [NotifyPropertyChangedFor(nameof(ShowStatePill))]
        private StudioStatus _status = StudioStatus.Waiting;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatusText))]
        private string _statusText = string.Empty;
        [ObservableProperty] private double _progress;
        [ObservableProperty] private LyricsStudioResult? _result;

        public bool IsWorking => Status == StudioStatus.Working;
        public bool IsReady => Status == StudioStatus.Ready;
        public bool IsSaved => Status == StudioStatus.Saved;
        public bool IsFailed => Status == StudioStatus.Failed;
    }
}
