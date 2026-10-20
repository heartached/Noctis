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
public sealed record LyricsStudioPrefs(string Model, string Language, bool WordTimings, bool SkipAlreadyTimed, bool EmbedTags, bool OnlineLyrics = true, bool SaveTtml = false, bool LivePreview = true);

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

    public IReadOnlyList<SpeechLanguageOption> LanguageOptions => Languages;

    public ObservableCollection<StudioItem> Queue { get; } = new();
    [ObservableProperty] private StudioItem? _selected;

    public ObservableCollection<ReviewLine> ReviewLines { get; } = new();

    // ── Options ──
    [ObservableProperty] private SpeechLanguageOption _selectedLanguage;
    [ObservableProperty] private bool _wordTimings;
    [ObservableProperty] private bool _transcribeOnly;
    /// <summary>Leave alone songs that already carry the format being written (ELRC when word timings are on; LRC or ELRC when off).</summary>
    [ObservableProperty] private bool _skipAlreadyTimed;
    /// <summary>Also write the plain lyrics into the audio file's tags on save (was a Settings toggle).</summary>
    [ObservableProperty] private bool _embedTags;
    /// <summary>Songs with no lyrics: fetch the plain text online (LRCLIB) so the model only has to time it.</summary>
    [ObservableProperty] private bool _onlineLyrics;
    /// <summary>Also save a .ttml next to the song: players that read TTML and LRC but not ELRC get the word timings (Discord: Light Cone).</summary>
    [ObservableProperty] private bool _saveTtml;
    /// <summary>The live preview pane beside the review is open (remembered like the options).</summary>
    [ObservableProperty] private bool _showLivePreview;

    // ── Model state ──
    [ObservableProperty] private bool _isModelInstalled;
    [ObservableProperty] private bool _isDownloadingModel;

    /// <summary>What the model banner above the queue says; Hidden once the model is ready.</summary>
    public enum ModelBannerState { Hidden, NotInstalled, Paused, Connecting, Downloading, Retrying, Verifying, Checking, Loading, Failed, Damaged }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModelBanner), nameof(ModelBannerIsError), nameof(ShowModelBannerBar), nameof(ShowModelDownload), nameof(ShowModelCancel))]
    private ModelBannerState _modelBanner;
    [ObservableProperty] private string _modelBannerTitle = string.Empty;
    [ObservableProperty] private string _modelBannerDetail = string.Empty;
    /// <summary>"42%" beside the title while there is a percentage to show.</summary>
    [ObservableProperty] private string _modelBannerPercent = string.Empty;
    [ObservableProperty] private double _modelBannerProgress;
    [ObservableProperty] private bool _modelBannerIndeterminate;
    /// <summary>The banner's one action: Download (size) / Resume download / Retry / Download again.</summary>
    [ObservableProperty] private string _modelActionText = string.Empty;

    public bool ShowModelBanner => ModelBanner != ModelBannerState.Hidden;
    public bool ModelBannerIsError => ModelBanner is ModelBannerState.Failed or ModelBannerState.Damaged;
    public bool ShowModelBannerBar => ModelBanner is ModelBannerState.Paused or ModelBannerState.Connecting or ModelBannerState.Downloading
        or ModelBannerState.Retrying or ModelBannerState.Verifying or ModelBannerState.Checking or ModelBannerState.Loading;
    public bool ShowModelDownload => ModelBanner is ModelBannerState.NotInstalled or ModelBannerState.Paused or ModelBannerState.Failed or ModelBannerState.Damaged;
    public bool ShowModelCancel => ModelBanner is ModelBannerState.Connecting or ModelBannerState.Downloading or ModelBannerState.Retrying;

    /// <summary>The manager the banner follows (the panel listens to its <see cref="WhisperModelManager.StateChanged"/>).</summary>
    internal WhisperModelManager Models => _engine.Models;

    // ── Run state ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBatchProgress))]
    private bool _isRunning;
    [ObservableProperty] private string _runStatusText = string.Empty;
    /// <summary>The whole run, 0–1: songs finished plus the share of the one being worked on.</summary>
    [ObservableProperty] private double _batchProgress;
    /// <summary>"Song 2 of 5" while a run is going.</summary>
    [ObservableProperty] private string _batchText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBatchProgress))]
    private int _batchTotal;
    public bool ShowBatchProgress => IsRunning && BatchTotal > 1;
    private int _batchDone;
    private StudioItem? _working;
    /// <summary>Whisper's pace on the last song of this Studio, seconds per 30 s window: paces the next song's first window.</summary>
    private double? _secondsPerWindow;

    /// <summary>The format picker's second chip: line timings (LRC) = word timings off.</summary>
    public bool LineTimings
    {
        get => !WordTimings;
        set => WordTimings = !value;
    }

    /// <summary>Queue header note when every unrun song has the same format ("all LRC"); the rows then drop their own pill.</summary>
    [ObservableProperty] private string _queueFormatText = string.Empty;

    public bool HasFfmpeg => _engine.HasFfmpeg;
    public bool CanStart => !IsRunning && !IsLrcRedSyncing && IsModelInstalled && HasFfmpeg
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
        LyricsStudioSource.LrcRed => Loc("LyricsStudio.LrcRedSource"),
        LyricsStudioSource.ExistingFile => Selected?.Existing is { } e
            ? $"From {e.Origin} · {(e.Format == LyricsFormat.Elrc ? "word timings" : "line timings")}"
            : "Loaded",
        _ => string.Empty,
    };
    public string ReviewConfidenceText => Selected?.Result is { } r
        ? (r.Source is LyricsStudioSource.ExistingFile or LyricsStudioSource.LrcRed ? string.Empty : $"{Math.Round(r.Confidence * 100)}% heard · ")
          + $"{r.Lines.Count} lines · saves {(WordTimings ? "ELRC" : "LRC")}{(SaveTtml ? " + TTML" : string.Empty)}"
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
    /// <summary>A song's lyrics box holds typed, imported or transcribed lyrics that a new Studio would lose.</summary>
    public bool HasUnsavedLyricsBox => Queue.Any(i => i.HasUnsavedLyricsBox);

    public event EventHandler? Closed;

    /// <summary>
    /// Appends songs the user picked (Choose songs) to a Studio that is mid-run or mid-review,
    /// so the work on screen is kept. Non-local songs and songs already queued are skipped;
    /// an unfinished review of a picked song comes back as it was, like at open.
    /// </summary>
    public int AddTracks(IEnumerable<Track> tracks)
    {
        var added = 0;
        var withDrafts = _drafts?.ListTrackIds();
        foreach (var t in tracks)
        {
            if (t.SourceType != SourceType.Local || Queue.Any(i => i.Track.Id == t.Id)) continue;
            var item = new StudioItem(t);
            if (withDrafts is not null && withDrafts.Contains(t.Id) && _drafts!.TryLoad(t.Id, out var draft))
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
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code.Equals(s.LyricsStudioLanguage, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];
        _wordTimings = s.LyricsStudioWordTimings;
        _skipAlreadyTimed = s.LyricsStudioSkipAlreadyTimed;
        _embedTags = s.LyricsStudioEmbedTags;
        _onlineLyrics = s.LyricsStudioOnlineLyrics;
        _saveTtml = s.LyricsStudioSaveTtml;
        _showLivePreview = s.LyricsStudioLivePreview;
        _loadingPrefs = false;
        Clock = () => _clock.Elapsed;

        var restored = 0;
        // One listing of the drafts folder instead of a file probe per selected track (UI thread).
        var withDrafts = _drafts?.ListTrackIds();
        foreach (var t in tracks.Where(t => t.SourceType == SourceType.Local))
        {
            var item = new StudioItem(t);
            // A review left unfinished when the app closed comes back as it was — no re-run.
            if (withDrafts is not null && withDrafts.Contains(t.Id) && _drafts!.TryLoad(t.Id, out var draft))
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

    partial void OnSelectedLanguageChanged(SpeechLanguageOption value) => PersistPrefs();
    partial void OnWordTimingsChanged(bool value)
    {
        PersistPrefs();
        OnPropertyChanged(nameof(ReviewConfidenceText));
        OnPropertyChanged(nameof(LineTimings));
        // The rows show the chosen format: word tags for ELRC, plain text for LRC.
        foreach (var line in ReviewLines) line.ShowWordTags = value;
        ScheduleLivePreview();
    }
    partial void OnSkipAlreadyTimedChanged(bool value) => PersistPrefs();
    partial void OnEmbedTagsChanged(bool value) => PersistPrefs();
    partial void OnOnlineLyricsChanged(bool value) => PersistPrefs();
    partial void OnSaveTtmlChanged(bool value)
    {
        PersistPrefs();
        OnPropertyChanged(nameof(ReviewConfidenceText));
    }
    partial void OnIsRunningChanged(bool value)
    {
        RaiseStartState();
        RaiseLrcRedState();
        OnPropertyChanged(nameof(ReviewCanUpgrade));
        OnPropertyChanged(nameof(CanAlignDraft));
        OnPropertyChanged(nameof(CanTranscribeDraft));
    }
    partial void OnIsModelInstalledChanged(bool value) { RaiseStartState(); OnPropertyChanged(nameof(CanTranscribeDraft)); }

    partial void OnSelectedChanging(StudioItem? value)
    {
        if (Selected is { } leaving && !ReferenceEquals(leaving, value))
        {
            PersistDraft(leaving);
            CancelTap();
            SelectedWord = null;
            SelectedLine = null;
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
        ResetReviewSelection();
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
        _ = LookUpLrcRedAsync(value);
    }

    /// <summary>Any text, time or word edit on the review on screen; drafts of loaded songs are only written when this is set.</summary>
    private bool _reviewDirty;
    private void MarkReviewDirty()
    {
        _reviewDirty = true;
        ScheduleDraftSave();
        ScheduleLivePreview();
    }

    /// <summary>
    /// The review on screen is rebuilt (a run's result, a stopped re-run, Clear lyrics): tap mode
    /// and the selected word belonged to the lines just dropped. Tap mode used to outlive a
    /// Re-sync, so Space kept stamping times on a line no longer on screen.
    /// </summary>
    private void ResetReviewSelection()
    {
        CancelTap();
        SelectedWord = null;
        SelectedLine = null;
    }

    private CancellationTokenSource? _draftSaveCts;
    /// <summary>How long after the last edit the review on screen is written to its draft.</summary>
    internal const int DraftSaveDelayMs = 1000;

    /// <summary>
    /// Edits are kept as they are made, a second after the last one. Drafts were only written
    /// when the user moved to another song, previewed or closed the dialog; the sidebar page's
    /// Studio is never closed, so quitting the app lost every fix made on the song on screen.
    /// </summary>
    private void ScheduleDraftSave()
    {
        if (_drafts is null || Selected is not { } item) return;
        _draftSaveCts?.Cancel();
        var cts = _draftSaveCts = new CancellationTokenSource();
        _ = SaveDraftSoonAsync(item, cts.Token);
    }

    private async Task SaveDraftSoonAsync(StudioItem item, CancellationToken token)
    {
        try { await Task.Delay(DraftSaveDelayMs, token); }
        catch (OperationCanceledException) { return; }
        // Saved, skipped or re-run since: PersistDraft keeps only reviews still open.
        if (ReferenceEquals(Selected, item)) PersistDraft(item);
    }

    /// <summary>
    /// Selecting a song shows what it already has — .elrc, then .lrc, then embedded — with no
    /// Start press. A restored draft (already Ready) wins over the file.
    /// </summary>
    private void TryLoadExisting(StudioItem item)
    {
        // Lyrics the user cleared stay cleared until they save their own.
        if (item.LyricsCleared) return;
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
        if (item.LyricsCleared || !string.IsNullOrWhiteSpace(item.DraftText)) return;
        try
        {
            var text = LyricsStudioEngine.FirstText(item.Track.Lyrics);
            if (text is null && !string.IsNullOrWhiteSpace(item.Track.FilePath))
            {
                var txt = Path.ChangeExtension(item.Track.FilePath, ".txt");
                if (File.Exists(txt)) text = LyricsStudioEngine.FirstText(File.ReadAllText(txt));
            }
            if (text is not null) item.DraftText = item.PrefilledText = string.Join('\n', text);
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
        RaiseLrcRedState();
        RefreshLivePreview();
    }

    // ── Live preview: the review rendered by the lyrics view, beside the editor (10-14) ──
    // Discord "Manual lyrics editing": switching between Preview and the editor was the hassle.
    // The pane is the lyrics side panel's own view over the app's lyrics view model, fed the
    // review through the same unsaved-preview path as Preview: memory only, nothing saved.

    /// <summary>Set by the panel: shows (or updates in place) these synced lyrics, unsaved, for the track in the preview pane. Null = no pane.</summary>
    public Action<Track, string>? UpdateLivePreview
    {
        get => _updateLivePreview;
        set { _updateLivePreview = value; RefreshLivePreview(); }
    }
    private Action<Track, string>? _updateLivePreview;
    /// <summary>Set with <see cref="UpdateLivePreview"/>: drops the preview so the song's own lyrics come back.</summary>
    public Action? ClearLivePreview { get; set; }

    public bool CanLivePreview => _updateLivePreview is not null;
    /// <summary>The pane is on screen: open, and an editable review to show.</summary>
    public bool IsLivePreviewOpen => ShowLivePreview && CanLivePreview && CanSave;
    /// <summary>The song under review is the one playing (from the song file, not a music video's audio).</summary>
    public bool LivePreviewOnSong => _player is { } p && Selected is { } s && p.CurrentTrack?.Id == s.Track.Id && !p.IsPlayingMusicVideoAudio;
    /// <summary>The pane is open on a song that is not playing: it offers Play instead of lyrics of another song.</summary>
    public bool ShowLivePreviewIdle => IsLivePreviewOpen && !LivePreviewOnSong;

    /// <summary>What the pane last showed was pushed by it: closing it, or leaving the review, puts the real lyrics back.</summary>
    private bool _livePreviewShown;
    private bool _livePreviewPending;
    /// <summary>Edits reach the pane at most this often (a held , or . repeats ~30 times a second).</summary>
    internal const int LivePreviewDelayMs = 120;

    partial void OnShowLivePreviewChanged(bool value)
    {
        PersistPrefs();
        RefreshLivePreview();
    }

    [RelayCommand]
    private void ToggleLivePreview() => ShowLivePreview = !ShowLivePreview;

    /// <summary>The pane's Play: the song under review from just before the selected line (else its first), like Preview's lead-in.</summary>
    [RelayCommand]
    private Task PlayLivePreview()
    {
        if (ReviewLines.Count == 0) return Task.CompletedTask;
        var from = SelectedLine?.Start ?? ReviewLines[0].Start;
        return PlayFromTime(from - TimeSpan.FromSeconds(2));
    }

    /// <summary>The pane's state changed (the review, the song playing, the pane itself): say so and bring it up to date.</summary>
    /// <summary>
    /// The panel left the screen (another page, or the dialog closed): the pane's preview lives in
    /// the app's lyrics view model, so it stayed on the lyrics page and side panel, with Save and
    /// Search off, until the song changed. Drop it now and push nothing until
    /// <see cref="ResumeLivePreview"/>.
    /// </summary>
    internal void SuspendLivePreview()
    {
        _livePreviewSuspended = true;
        if (!_livePreviewShown) return;
        _livePreviewShown = false;
        try { ClearLivePreview?.Invoke(); } catch { }
    }

    /// <summary>The panel is on screen again: the pane shows the review again.</summary>
    internal void ResumeLivePreview()
    {
        _livePreviewSuspended = false;
        RefreshLivePreview();
    }

    private bool _livePreviewSuspended;

    internal void RefreshLivePreview()
    {
        OnPropertyChanged(nameof(CanLivePreview));
        OnPropertyChanged(nameof(IsLivePreviewOpen));
        OnPropertyChanged(nameof(LivePreviewOnSong));
        OnPropertyChanged(nameof(ShowLivePreviewIdle));
        ScheduleLivePreview();
    }

    private void ScheduleLivePreview()
    {
        if (_livePreviewPending || (_updateLivePreview is null && !_livePreviewShown)) return;
        _livePreviewPending = true;
        _ = PushLivePreviewSoonAsync();
    }

    private async Task PushLivePreviewSoonAsync()
    {
        await Task.Delay(LivePreviewDelayMs);
        _livePreviewPending = false;
        PushLivePreview();
    }

    private void PushLivePreview()
    {
        if (IsLivePreviewOpen && !_livePreviewSuspended && Selected is { } item && ReviewLines.Count > 0)
        {
            try
            {
                _updateLivePreview!(item.Track, BuildReviewText());
                _livePreviewShown = true;
            }
            catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.LivePreviewFailed", ex.Message); }
        }
        else if (_livePreviewShown)
        {
            _livePreviewShown = false;
            try { ClearLivePreview?.Invoke(); } catch { }
        }
    }

    private void PersistPrefs()
    {
        if (_loadingPrefs) return;
        try { _savePrefs(new LyricsStudioPrefs(WhisperModelManager.Lullaby.Size.ToString(), SelectedLanguage.Code, WordTimings, SkipAlreadyTimed, EmbedTags, OnlineLyrics, SaveTtml, ShowLivePreview)); }
        catch { /* preferences are a convenience */ }
    }

    /// <summary>
    /// Reads the model's state into the banner. Called at open, when the panel comes on screen and
    /// whenever the model manager reports a change (a download started or ended in another Studio).
    /// A download already running (started in the dialog, or by a page Studio since replaced) is
    /// followed here instead of offering a second one.
    /// </summary>
    public void RefreshModelState()
    {
        IsModelInstalled = _engine.Models.IsInstalled();
        if (IsDownloadingModel || _checkingModel) return;
        if (_engine.Models.IsDownloading)
        {
            _ = DownloadModel();
            return;
        }
        var model = _engine.Models.Model;
        var state = _engine.Models.State;
        if (_downloadFailure is { } failure && state is WhisperModelState.Missing or WhisperModelState.Partial)
        {
            // Still not installed: keep saying why the last try failed (a re-attached panel used
            // to replace it with the plain "Download" card, hiding the error).
            SetBanner(ModelBannerState.Failed, Loc("LyricsStudio.ModelFailedTitle"), failure,
                action: Loc(state == WhisperModelState.Partial ? "LyricsStudio.ModelResume" : "LyricsStudio.ModelRetry"));
            return;
        }
        _downloadFailure = null;
        switch (state)
        {
            case WhisperModelState.Ready or WhisperModelState.Unverified:
                SetBanner(ModelBannerState.Hidden);
                break;
            case WhisperModelState.Damaged:
                SetBanner(ModelBannerState.Damaged, Loc("LyricsStudio.ModelDamagedTitle"), Loc("LyricsStudio.ModelDamagedBody", model.SizeText),
                    action: Loc("LyricsStudio.ModelDownloadAgain"));
                break;
            case WhisperModelState.Partial:
                var part = _engine.Models.PartialBytes;
                var fraction = part / (double)Math.Max(1, model.Bytes);
                SetBanner(ModelBannerState.Paused, Loc("LyricsStudio.ModelPausedTitle"),
                    Loc("LyricsStudio.ModelPausedBody", ByteText.Format(part, precise: true), ByteText.Format(model.Bytes, precise: true)),
                    fraction, DownloadMeter.PercentText(fraction), action: Loc("LyricsStudio.ModelResume"));
                break;
            default:
                SetBanner(ModelBannerState.NotInstalled, Loc("LyricsStudio.ModelNeededTitle"), Loc("LyricsStudio.ModelNeededBody", model.SizeText),
                    action: Loc("LyricsStudio.ModelDownloadSize", model.SizeText));
                break;
        }
    }

    private static string Loc(string key) => Localization.Loc.T(key);
    private static string Loc(string key, params object[] args) => Localization.Loc.T(key, args);

    private void SetBanner(ModelBannerState state, string title = "", string detail = "", double progress = 0, string percent = "",
        bool indeterminate = false, string action = "")
    {
        ModelBanner = state;
        ModelBannerTitle = title;
        ModelBannerDetail = detail;
        ModelBannerProgress = progress;
        ModelBannerPercent = percent;
        ModelBannerIndeterminate = indeterminate;
        ModelActionText = action;
    }

    private DownloadMeter _downloadMeter = new();
    /// <summary>Why the last download failed, shown until the next try or until the model is installed.</summary>
    private string? _downloadFailure;

    /// <summary>
    /// Downloads the model (or resumes it, or follows the download already running). Progress is
    /// sampled on the UI tick (~15 Hz) from the manager rather than posted per 64 KB chunk.
    /// </summary>
    [RelayCommand]
    private async Task DownloadModel()
    {
        if (IsDownloadingModel) return;
        IsDownloadingModel = true;
        _downloadFailure = null;
        _downloadMeter = new DownloadMeter();
        SetBanner(ModelBannerState.Connecting, Loc("LyricsStudio.ModelDownloadingTitle"), Loc("LyricsStudio.ModelConnecting"), indeterminate: true);
        EnsureTicking();
        try
        {
            await _engine.Models.DownloadAsync(WhisperModelSize.Medium, null, CancellationToken.None);
            IsDownloadingModel = false;
            RefreshModelState();
        }
        catch (OperationCanceledException)
        {
            IsDownloadingModel = false;
            RefreshModelState(); // Paused, with Resume
        }
        catch (Exception ex)
        {
            IsDownloadingModel = false;
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.ModelDownloadFailed", $"{ex.GetType().Name}: {ex.Message}");
            var reason = ex.Message.TrimEnd('.', ' ');
            _downloadFailure = ex is WhisperModelIntegrityException ? Loc("LyricsStudio.ModelChecksumFailedBody")
                : _engine.Models.PartialBytes > 0 ? Loc("LyricsStudio.ModelFailedBody", reason)
                : reason + ".";
            RefreshModelState();
        }
    }

    /// <summary>Stops the download; what arrived is kept and Resume continues from there.</summary>
    [RelayCommand]
    private void CancelModelDownload() => _engine.Models.CancelDownload();

    /// <summary>The download on screen, from the manager's latest report.</summary>
    private void UpdateDownloadBanner(TimeSpan now)
    {
        _downloadMeter.Sample(_engine.Models.CurrentDownload, now);
        var d = _downloadMeter.Read(now);
        var title = Loc("LyricsStudio.ModelDownloadingTitle");
        switch (d.Phase)
        {
            case ModelDownloadPhase.Connecting:
                if (d.BytesDone > 0)
                    SetBanner(ModelBannerState.Connecting, title, Loc("LyricsStudio.ModelResuming", ByteText.Format(d.BytesDone, precise: true)),
                        d.Fraction, DownloadMeter.PercentText(d.Fraction));
                else
                    SetBanner(ModelBannerState.Connecting, title, Loc("LyricsStudio.ModelConnecting"), indeterminate: true);
                break;
            case ModelDownloadPhase.Downloading:
                var parts = new List<string> { Loc("LyricsStudio.ModelBytesOf", ByteText.Format(d.BytesDone, precise: true), ByteText.Format(d.BytesTotal, precise: true)) };
                if (d.BytesPerSecond is { } speed) parts.Add(DownloadMeter.RateText(speed));
                if (d.Remaining is { } left) parts.Add(DownloadMeter.RemainingText(left));
                SetBanner(ModelBannerState.Downloading, title, string.Join(" · ", parts), d.Fraction, DownloadMeter.PercentText(d.Fraction));
                break;
            case ModelDownloadPhase.Retrying:
                SetBanner(ModelBannerState.Retrying, title,
                    d.RetryIn > TimeSpan.Zero
                        ? Loc("LyricsStudio.ModelRetrying", (int)Math.Ceiling(d.RetryIn.TotalSeconds), d.Attempt)
                        : Loc("LyricsStudio.ModelReconnecting", d.Attempt),
                    d.Fraction, DownloadMeter.PercentText(d.Fraction));
                break;
            default: // Verifying, Done
                SetBanner(ModelBannerState.Verifying, Loc("LyricsStudio.ModelVerifyingTitle"), Loc("LyricsStudio.ModelVerifyingBody"),
                    1, DownloadMeter.PercentText(d.VerifyFraction));
                break;
        }
    }

    private bool _checkingModel;
    private double _checkFraction;

    /// <summary>
    /// A model installed before checksums existed is checked once before its first run (~1.5 GB
    /// read, a few seconds). False when it failed: the banner then asks for a fresh download.
    /// </summary>
    private async Task<bool> EnsureModelVerifiedAsync(CancellationToken ct)
    {
        if (_engine.Models.State != WhisperModelState.Unverified) return true;
        _checkingModel = true;
        _checkFraction = 0;
        SetBanner(ModelBannerState.Checking, Loc("LyricsStudio.ModelCheckingTitle"), Loc("LyricsStudio.ModelCheckingBody"), percent: DownloadMeter.PercentText(0));
        RunStatusText = Loc("LyricsStudio.ModelCheckingTitle");
        EnsureTicking();
        bool ok;
        try { ok = await _engine.Models.VerifyAsync(new InlineProgress<double>(f => Volatile.Write(ref _checkFraction, f)), ct); }
        catch (OperationCanceledException) { ok = false; }
        finally { _checkingModel = false; }
        RefreshModelState();
        if (!ok) RunStatusText = ct.IsCancellationRequested ? "Stopped." : Loc("LyricsStudio.ModelDamagedTitle");
        return ok;
    }

    // ── UI tick: throttled progress (~15 Hz) while something is running ──

    private DispatcherTimer? _tick;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    /// <summary>Time source for the progress meters (tests replace it).</summary>
    internal Func<TimeSpan> Clock { get; set; }

    /// <summary>False in tests that call <see cref="Tick"/> themselves.</summary>
    internal bool AutoTick { get; set; } = true;

    private void EnsureTicking()
    {
        if (!AutoTick) return;
        if (_tick is null)
        {
            _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(66) };
            _tick.Tick += (_, _) => Tick();
        }
        if (!_tick.IsEnabled) _tick.Start();
    }

    /// <summary>
    /// One progress refresh: the download banner, the model check and the songs being run. Only
    /// here do the bound values change, so bars and labels update at most ~15 times a second
    /// whatever rate the work reports at. Stops itself when nothing is running. Internal for tests.
    /// </summary>
    internal void Tick()
    {
        var now = Clock();
        if (IsDownloadingModel) UpdateDownloadBanner(now);
        if (_checkingModel)
        {
            var f = Volatile.Read(ref _checkFraction);
            ModelBannerProgress = f;
            ModelBannerPercent = DownloadMeter.PercentText(f);
        }
        // Late reports are harmless: only the song still marked Working is shown.
        if (_working is { Status: StudioStatus.Working, Meter: { } meter } item)
        {
            var (stage, overall) = meter.Read(now);
            item.ShowStage(stage, overall);
            BatchProgress = Math.Max(BatchProgress, (_batchDone + overall) / Math.Max(1, BatchTotal));
        }
        if (!IsDownloadingModel && !_checkingModel && !IsRunning) _tick?.Stop();
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
            if (!await ConfirmAsync($"Re-sync will replace the timings shown for “{current.Title}” with a fresh run of Lullaby.\n\nNothing is written to disk until you press Save lyrics."))
                return;
            Requeue(current);
            // The user's own lyrics (pasted, typed, a corrected transcript) are re-timed as the
            // review shows them now, edits included: Re-sync used to time the lyrics box, or the
            // song's .lrc the user had replaced (Discord, Mistery 2026-10-10). So are lrc.red's,
            // which replaced the song's own on screen.
            if (current.BeforeRerun?.Result is { Source: LyricsStudioSource.PastedLyrics or LyricsStudioSource.LrcRed } shown)
                current.SourceOverride = shown.Lines.Select(l => l.Text).ToList();
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
            RunStatusText = !HasFfmpeg ? "ffmpeg is needed to decode songs — set its path under Settings → Advanced → Helper programs." : "Download Lullaby first.";
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

    // ── lrc.red's model: word timings in seconds instead of Lullaby's minutes ──

    /// <summary>lrc.red, for "Sync with lrc.red's model"; set by the app. Null: never offered.</summary>
    internal ILrcRedLyricsService? LrcRed
    {
        get => _lrcRed;
        set
        {
            _lrcRed = value;
            _ = LookUpLrcRedAsync(Selected);
            RaiseLrcRedState();
        }
    }
    private ILrcRedLyricsService? _lrcRed;
    private CancellationTokenSource? _lrcRedCts;

    [ObservableProperty] private bool _isLrcRedSyncing;

    partial void OnIsLrcRedSyncingChanged(bool value)
    {
        RaiseStartState();
        RaiseLrcRedState();
    }

    private bool LrcRedOn()
    {
        try { return _settings().LrcRedEnabled; }
        catch { return false; }
    }

    /// <summary>
    /// The selected song is one lrc.red has only line-synced or plain (its lookup matched it), lrc.red
    /// is on in Settings, and the review on screen has no word timing yet; nothing else is running.
    /// </summary>
    public bool CanLrcRedSync => _lrcRed != null && !IsRunning && !IsLrcRedSyncing && LrcRedOn()
        && Selected is { LrcRedMatch.IsWordSynced: false, Status: not (StudioStatus.Working or StudioStatus.Saved) } item
        && item.ExistingFormat != LyricsFormat.Elrc
        && !(item.Result is { } shown && ExistingLyricsLoader.HasWordTimings(shown.Lines));

    /// <summary>The button stays while lrc.red's model runs, saying so.</summary>
    public bool ShowLrcRedSync => IsLrcRedSyncing || CanLrcRedSync;
    public string LrcRedSyncLabel => Loc(IsLrcRedSyncing ? "LyricsStudio.LrcRedSyncWorking" : "LyricsStudio.LrcRedSync");

    private void RaiseLrcRedState()
    {
        OnPropertyChanged(nameof(CanLrcRedSync));
        OnPropertyChanged(nameof(ShowLrcRedSync));
        OnPropertyChanged(nameof(LrcRedSyncLabel));
    }

    /// <summary>
    /// Asks lrc.red's lookup once per song whether it has it (the service remembers the answer), so
    /// the button only shows for a match. Only the lookup: the model runs on the button's press.
    /// </summary>
    private async Task LookUpLrcRedAsync(StudioItem? item)
    {
        if (item is null || _lrcRed is not { } lrcRed || item.LrcRedLooked || !LrcRedOn()) return;
        item.LrcRedLooked = true;
        try
        {
            item.LrcRedMatch = await lrcRed.FindMatchAsync(item.Track.Artist ?? string.Empty, item.Track.Title ?? string.Empty, item.Track.Duration.TotalSeconds);
        }
        catch (Exception ex)
        {
            // Asked again the next time the song is selected.
            item.LrcRedLooked = false;
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.LrcRedLookupFailed", ex.Message);
        }
        if (ReferenceEquals(Selected, item)) RaiseLrcRedState();
    }

    /// <summary>
    /// "Sync with lrc.red's model": lrc.red's in-house model times every word of its own copy of the
    /// song, in seconds where Lullaby takes minutes, and its lyrics open for review like a run's
    /// result. As Re-sync does, a review on screen (or lyrics typed in the box) is replaced only after
    /// asking; nothing is written until Save. One request per press, never retried on its own.
    /// </summary>
    [RelayCommand]
    private async Task SyncWithLrcRed()
    {
        if (!CanLrcRedSync || Selected is not { LrcRedMatch: { } match } item || _lrcRed is not { } lrcRed) return;
        if ((item.Result is not null && item.Status is StudioStatus.Loaded or StudioStatus.Ready || item.HasUnsavedLyricsBox)
            && !await ConfirmAsync(Loc("LyricsStudio.LrcRedSyncConfirm", item.Title)))
            return;
        if (!CanLrcRedSync || !ReferenceEquals(Selected, item)) return;

        var cts = new CancellationTokenSource();
        _lrcRedCts = cts;
        var before = item.StatusText;
        IsLrcRedSyncing = true;
        item.StatusText = RunStatusText = Loc("LyricsStudio.LrcRedSyncWorking");
        DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.LrcRedSync", $"{item.Title} | isrc={match.Isrc}");
        try
        {
            var synced = await lrcRed.SyncWithModelAsync(match.Isrc, cts.Token);
            var lines = ExistingLyricsLoader.ParseTimed(LyricsViewModel.SyncedToWrite(synced) ?? string.Empty);
            if (!ExistingLyricsLoader.HasWordTimings(lines))
                throw new LrcRedSyncException(LrcRedSyncFailure.NotWordSynced);
            var result = new LyricsStudioResult(item.Track, lines, LyricsStudioSource.LrcRed, 1, string.Empty, 0);
            // Word timings are what was asked for: Save writes ELRC, as after Time every word.
            WordTimings = true;
            item.BeforeRerun = null;
            item.Result = result;
            item.Status = StudioStatus.Ready;
            item.StatusText = RunStatusText = Loc("LyricsStudio.LrcRedSynced");
            _drafts?.Save(item.Track.Id, LyricsStudioDraft.From(result));
            DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.LrcRedSynced", $"{item.Title} | lines={lines.Count}");
            if (ReferenceEquals(Selected, item))
                OnSelectedChanged(item);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            item.StatusText = before;
        }
        catch (Exception ex)
        {
            item.StatusText = RunStatusText = LyricsViewModel.LrcRedSyncMessage(ex);
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.LrcRedSyncFailed", $"{item.Title} | {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_lrcRedCts, cts)) _lrcRedCts = null;
            cts.Dispose();
            IsLrcRedSyncing = false;
        }
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        if (Confirm is null) return true;
        try { return await Confirm(message); } catch { return true; }
    }

    private void Requeue(StudioItem item)
    {
        // The review goes away while the song re-runs; keep it (as on screen, edits included) so
        // a Stop, a failure or a run that cannot start puts it back instead of an empty song.
        if (item.Result is { } result)
        {
            var onScreen = ReferenceEquals(Selected, item) && ReviewLines.Count > 0;
            var shown = onScreen ? result with { Lines = ReviewLines.Select(l => l.ToAlignedLine()).Where(l => l.Text.Length > 0).ToList() } : result;
            item.BeforeRerun = new StudioItem.Kept(shown, item.Status, item.StatusText, onScreen && _reviewDirty);
        }
        item.ForceRun = true;
        item.Status = StudioStatus.Waiting;
        item.Result = null;
        if (ReferenceEquals(Selected, item))
        {
            ResetReviewSelection();
            ReviewLines.Clear();
        }
        RaiseReviewChanged();
    }

    /// <summary>A re-run that was stopped, failed or never started: the song gets its review back.</summary>
    private void RestoreBeforeRerun(StudioItem item, string? statusText)
    {
        if (item.BeforeRerun is not { } kept) return;
        item.BeforeRerun = null;
        item.ForceRun = false;
        item.TranscribeNext = false;
        item.SourceOverride = null;
        item.Result = kept.Result;
        item.Status = kept.Status;
        item.StatusText = statusText ?? kept.StatusText;
        if (ReferenceEquals(Selected, item))
        {
            OnSelectedChanged(item);
            _reviewDirty = kept.Dirty;
        }
        RaiseStartState();
    }

    /// <summary>
    /// The song on screen is being worked on — a result under review, or the song's own lyrics
    /// being edited or tapped — so a song that finishes waits in the list instead of taking the
    /// pane (it used to take it from an edited or tapped .lrc, ending tap mode mid-line).
    /// </summary>
    private bool KeepsSelection => Selected is { Status: StudioStatus.Ready }
        || Selected is { Status: StudioStatus.Loaded } && (_reviewDirty || IsTapping);

    private async Task RunAsync(List<StudioItem> items)
    {
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        var done = 0;
        var total = items.Count;
        BatchTotal = total;
        BatchProgress = 0;
        _batchDone = 0;
        EnsureTicking();
        // Session-log breadcrumbs (Settings > Advanced > Copy Logs): a native crash inside the
        // speech model leaves no managed trace, so the run's own steps are the only record.
        DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.RunStart",
            $"songs={total}, model={WhisperModelManager.Lullaby.FileName}, language={SelectedLanguage.Code}, wordTimings={WordTimings}, transcribeOnly={TranscribeOnly}, online={OnlineLyrics}, skipDone={SkipAlreadyTimed}");
        try
        {
            if (!await EnsureModelVerifiedAsync(ct)) return;
            // Loading the speech model takes seconds (6-46 s measured 09-29, a cold disk the slowest).
            // It ran on the UI thread and froze the whole window; now the card says it is loading.
            SetBanner(ModelBannerState.Loading, Loc("LyricsStudio.ModelLoadingTitle"), Loc("LyricsStudio.ModelLoadingBody"), indeterminate: true);
            RunStatusText = Loc("LyricsStudio.ModelLoadingTitle");
            IDisposable opened;
            try { opened = await Task.Run(() => _engine.OpenSession(WhisperModelSize.Medium)); }
            finally { RefreshModelState(); }
            using var session = opened;
            DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.SessionOpen", _engine.Models.Model.FileName);
            foreach (var item in items)
            {
                _batchDone = done;
                BatchProgress = Math.Max(BatchProgress, done / (double)Math.Max(1, total));
                _secondsPerWindow = _working?.Meter?.SecondsPerWindow ?? _secondsPerWindow;
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
                if (!transcribe && pasted is null && item.Existing is null && !item.LyricsCleared)
                    item.Existing = LoadExistingOrNull(item.Track);
                // No timed lyrics: the lyrics box on screen is the text, Start and Re-sync alike. They
                // ignored it and the engine went back to the song's own text or LRCLIB's match —
                // the wrong lyrics a user had just replaced (Discord, Mistery 2026-10-10).
                var prefilledBox = false;
                if (!transcribe && pasted is null && item.Existing is null && LyricsStudioEngine.FirstText(item.DraftText) is { } boxed)
                {
                    pasted = boxed;
                    prefilledBox = !item.LyricsCleared && item.DraftText == item.PrefilledText;
                }
                if (!transcribe && pasted is null && item.LyricsCleared)
                {
                    // Cleared and the box emptied: never fall back to the lyrics the user dumped.
                    if (item.BeforeRerun is not null) RestoreBeforeRerun(item, null);
                    else
                    {
                        item.Status = StudioStatus.NeedsLyrics;
                        item.StatusText = Loc("LyricsStudio.ClearedStatus");
                    }
                    done++;
                    continue;
                }
                // Pasted lyrics win; else loaded lyrics (sidecar or embedded) are the text to
                // time, and the engine only looks online when the song has nothing at all. A song
                // with no lyrics anywhere stops at "Needs lyrics" rather than being guessed by ear.
                // Loaded line-level lyrics keep their line starts as anchors: words are placed inside each line's own window.
                var options = new LyricsStudioOptions(WhisperModelSize.Medium, SelectedLanguage.Code, AllowOnlineLyrics: OnlineLyrics, ForceTranscription: transcribe,
                    SourceLines: transcribe ? null : pasted ?? item.Existing?.Lines.Select(l => l.Text).ToList(),
                    SourceLineStarts: transcribe || pasted is not null ? null : item.Existing?.Lines.Select(l => l.Start).ToList(),
                    AllowTranscription: false);
                if (!forced && SkipAlreadyTimed && !transcribe && !item.LyricsCleared && LyricsFormatDetector.AlreadyHas(item.ExistingFormat, WordTimings))
                {
                    item.Status = StudioStatus.Skipped;
                    item.StatusText = $"Skipped · already {LyricsFormatDetector.Label(item.ExistingFormat)}";
                    DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.ItemSkipped", $"{item.Title} | {item.ExistingFormat}");
                    done++;
                    continue;
                }
                var meter = new SongProgressMeter(item.Track.Duration, Clock(), _secondsPerWindow);
                item.BeginRun(meter, transcribe, WordTimings);
                item.Status = StudioStatus.Working;
                _working = item;
                BatchText = Loc("LyricsStudio.BatchProgress", done + 1, total);
                RunStatusText = $"Working on {item.Title} ({done + 1} of {total})";
                DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.ItemStart",
                    $"{item.Title} ({done + 1}/{total}) | existing={item.ExistingFormat}, sourceLines={(options.SourceLines?.Count.ToString() ?? "none")}");
                // Reports only feed the song's meter (any thread, any rate); the UI tick shows it at
                // ~15 Hz. The old per-report post let late reports land after the outcome, and
                // Progress<T> could deliver them out of order, walking the bar backwards.
                var clock = Clock;
                var progress = new InlineProgress<LyricsStudioProgress>(p => meter.Report(p.Stage, p.StageFraction, clock()));
                try
                {
                    var result = await Task.Run(() => _engine.ProcessAsync(item.Track, options, progress, ct), ct);
                    if (result.Source == LyricsStudioSource.Transcription)
                    {
                        // Experimental: the transcript goes into the lyrics box to be corrected;
                        // Align then re-times the corrected words against what was heard.
                        item.HeardWords = result.Heard;
                        // The transcript replaces any review the song had: its draft goes now, not
                        // when the transcription was asked for (a Stop kept the review, not its draft).
                        _drafts?.Delete(item.Track.Id);
                        item.DraftText = string.Join('\n', result.Lines.Select(l => l.Text));
                        item.IsTranscriptDraft = true;
                        item.Result = null;
                        item.Status = StudioStatus.NeedsLyrics;
                        item.StatusText = "Transcript · fix the words, then Align";
                        DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.ItemTranscribed",
                            $"{item.Title} | lines={result.Lines.Count}, heard={result.HeardWords}");
                        item.BeforeRerun = null;
                        if (!ReferenceEquals(Selected, item) && !KeepsSelection)
                            Selected = item;
                        done++;
                        continue;
                    }
                    if (pasted is not null && !prefilledBox)
                    {
                        result = result with { Source = LyricsStudioSource.PastedLyrics };
                        // lrc.red's lyrics re-timed: their romaji / translation lines ride along.
                        if (item.BeforeRerun?.Result is { Source: LyricsStudioSource.LrcRed } fromLrcRed)
                            result = result with { Lines = ExistingLyricsLoader.CarryCompanions(fromLrcRed.Lines, result.Lines) };
                    }
                    // Timed from the song's own lyrics: their romaji / translation lines ride along.
                    else if (item.Existing is { } source)
                        result = result with { Lines = ExistingLyricsLoader.CarryCompanions(source.Lines, result.Lines) };
                    item.Result = result;
                    item.Status = StudioStatus.Ready;
                    _drafts?.Save(item.Track.Id, LyricsStudioDraft.From(result));
                    item.StatusText = result.Source == LyricsStudioSource.Transcription ? "Transcribed · review" : $"{Math.Round(result.Confidence * 100)}% matched · review";
                    DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.ItemReady",
                        $"{item.Title} | source={result.Source}, lines={result.Lines.Count}, confidence={result.Confidence:0.00}, heard={result.HeardWords}");
                    item.BeforeRerun = null;
                    if (ReferenceEquals(Selected, item))
                        OnSelectedChanged(item);
                    else if (!KeepsSelection)
                        Selected = item;
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
                    RestoreBeforeRerun(item, Loc("LyricsStudio.RerunStopped"));
                    DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.ItemStopped", item.Title);
                    break;
                }
                catch (Exception ex) when (item.BeforeRerun is not null)
                {
                    RestoreBeforeRerun(item, ex.Message);
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.ItemFailed", $"{item.Title} (re-run, review kept) | {ex.GetType().Name}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    item.Status = StudioStatus.Failed;
                    item.StatusText = ex.Message;
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.ItemFailed", $"{item.Title} | {ex.GetType().Name}: {ex.Message}");
                }
                done++;
            }
            _secondsPerWindow = _working?.Meter?.SecondsPerWindow ?? _secondsPerWindow;
            _working = null;
            var needLyrics = Queue.Count(i => i.Status == StudioStatus.NeedsLyrics);
            RunStatusText = ct.IsCancellationRequested ? "Stopped."
                : $"Finished · {Queue.Count(i => i.Status == StudioStatus.Ready)} ready for review" + (needLyrics > 0 ? $" · {needLyrics} need lyrics" : string.Empty);
            DebugLogger.Info(DebugLogger.Category.Lyrics, ct.IsCancellationRequested ? "LyricsStudio.RunStopped" : "LyricsStudio.RunFinished",
                $"done={done}/{total}, ready={Queue.Count(i => i.Status == StudioStatus.Ready)}");
            DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.SessionClosing", "disposing the speech model");
        }
        catch (WhisperEngineUnavailableException ex)
        {
            // Whisper.net's own text names NuGet packages; the user gets what to do instead.
            RunStatusText = Loc(OperatingSystem.IsWindows() ? "LyricsStudio.EngineUnavailableWindows" : "LyricsStudio.EngineUnavailable");
            DebugLogger.Error(DebugLogger.Category.Lyrics, "LyricsStudio.EngineUnavailable", $"{ex.InnerException?.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            RunStatusText = $"Couldn't start — {ex.Message}";
            DebugLogger.Error(DebugLogger.Category.Lyrics, "LyricsStudio.RunFailed", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _working = null;
            // Songs the run never reached (stopped first, or it could not start) keep their review.
            foreach (var item in items.Where(i => i.Status == StudioStatus.Waiting))
                RestoreBeforeRerun(item, null);
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

    private LyricsSaveTarget SafeSaveTarget()
    {
        try { return _writer.SaveTarget(); }
        catch { return LyricsSaveTarget.Sidecar; }
    }

    [RelayCommand]
    private async Task Save()
    {
        // Loaded = the song's own timed lyrics, possibly edited or shifted here: Save writes them too.
        if (Selected is not { Status: StudioStatus.Ready or StudioStatus.Loaded, Result: not null } item || ReviewLines.Count == 0) return;
        // Never replace lyrics without asking: sidecar files, and with "write into the file's tags"
        // on, the lyrics already stored in the audio file's tags too.
        var existing = ExistingLyricsFiles(item.Track);
        // Settings ▸ Lyrics ▸ Save lyrics to (GitHub #135) may put them inside the file too.
        var intoTags = EmbedTags || SafeSaveTarget().WritesTags();
        if (intoTags && (!string.IsNullOrWhiteSpace(item.Track.Lyrics) || !string.IsNullOrWhiteSpace(item.Track.SyncedLyrics)))
            existing.Add("lyrics in the audio file's tags");
        if (existing.Count > 0 && !await ConfirmAsync(
                $"“{item.Title}” already has {string.Join(" and ", existing)}.\n\nSaving replaces {(existing.Count == 1 ? "it" : "them")} with the lyrics on screen. A lyrics file Noctis didn't write goes to the Recycle Bin first."))
            return;
        if (!ReferenceEquals(Selected, item)) return;
        var lines = ReviewLines.Select(l => l.ToAlignedLine()).Where(l => l.Text.Length > 0).ToList();
        var plain = TimedLyricsBuilder.BuildPlain(lines);
        var synced = WordTimings ? TimedLyricsBuilder.BuildElrc(lines) : TimedLyricsBuilder.BuildLrc(lines);
        // Same timings as the ELRC/LRC: word spans with word timings on, line-level otherwise.
        var ttml = SaveTtml
            ? TimedLyricsBuilder.BuildTtml(WordTimings ? lines : lines.Select(l => l with { Words = Array.Empty<AlignedWord>() }))
            : null;
        var embed = EmbedTags;
        try
        {
            // Off the UI thread: trashing the old file can wait on the OS (macOS asks Finder, up to 15 s).
            var outcome = await Task.Run(() => _writer.SaveDetailed(item.Track, plain, synced, embed, replaceForeignSidecar: true, ttml));
            // What was written, edits included: reopening the saved song showed the result from
            // before the edits (drafts stop at Save, so nothing else carried them).
            item.Result = item.Result! with { Lines = lines };
            item.Status = StudioStatus.Saved;
            _drafts?.Delete(item.Track.Id);
            item.Existing = null;
            item.LyricsCleared = false;
            item.RefreshExistingFormat();
            // ELRC with no word timed yet is written as plain LRC; say what actually landed.
            var format = !WordTimings ? (ttml is null ? "line timings (LRC)" : "line timings (LRC + TTML)")
                : lines.Any(l => l.Words.Count > 0) ? (ttml is null ? "word timings (ELRC)" : "word timings (ELRC + TTML)")
                : "line timings (LRC) · no words timed yet";
            item.StatusText = outcome.KeptForeignSidecar ? $"Saved · {format} · an old lyrics file was kept, couldn't move it to the recycle bin"
                : !outcome.SidecarWritten ? $"Saved · {format} · no lyrics file written"
                : outcome.ReplacedForeignSidecar ? $"Saved · {format} · old lyrics file moved to the recycle bin"
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
            LogPlayerBefore("LyricsStudio.Preview", item.Track, ReviewLines[0].Start - TimeSpan.FromSeconds(2));
            if (_player.CurrentTrack?.Id != item.Track.Id || _player.IsPlayingMusicVideoAudio)
                await PlayFromTime(ReviewLines[0].Start - TimeSpan.FromSeconds(2));
            else if (_player.State != PlaybackState.Playing)
                _player.PlayPauseCommand.Execute(null);
        }
        // The lyrics page now shows Preview's own copy: leaving the Studio for it must not drop it.
        _livePreviewShown = false;
        try { await ShowOnLyricsPage(item.Track, synced); }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.PreviewFailed", ex.Message); }
    }

    /// <summary>
    /// The lyrics files a save of this song would replace (names only, for the prompt): .elrc
    /// and .lrc, and the .ttml and LRCGET .lyricsfile the lyrics page reads before them — a save
    /// replaces those too, or they would hide it (it used to leave them and say nothing).
    /// </summary>
    internal static List<string> ExistingLyricsFiles(Track track)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(track.FilePath)) return found;
        foreach (var ext in new[] { ".lyricsfile", ".ttml", ".elrc", ".lrc" })
            foreach (var path in LyricsWriter.SidecarsOnDisk(track.FilePath, ext))
                found.Add($"“{Path.GetFileName(path)}”");
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
            item.StatusText = !HasFfmpeg ? "ffmpeg is needed to decode songs — set its path under Settings → Advanced → Helper programs." : "Download Lullaby first.";
            return;
        }
        item.TranscribeNext = true;
        Requeue(item);
        item.StatusText = "Queued for transcription";
        await RunAsync(new List<StudioItem> { item });
    }

    /// <summary>
    /// Dumps the lyrics on screen — say, wrong ones matched online for a more popular song with
    /// the same title — and opens the empty lyrics box for the user's own; Align then times those.
    /// The song's files and tags are untouched until Save (Discord, Mistery 2026-10-10).
    /// </summary>
    [RelayCommand]
    private async Task ClearLyrics()
    {
        if (IsRunning || !HasReview || Selected is not { } item) return;
        if (!await ConfirmAsync(Loc("LyricsStudio.ClearLyricsConfirm", item.Title))) return;
        if (IsRunning || !ReferenceEquals(Selected, item)) return;
        CancelTap();
        SelectedWord = null;
        // A restored review would bring the dumped lyrics back at the next open.
        _drafts?.Delete(item.Track.Id);
        item.LyricsCleared = true;
        item.Existing = null;
        item.BeforeRerun = null;
        item.SourceOverride = null;
        item.TranscribeNext = false;
        item.IsTranscriptDraft = false;
        item.PrefilledText = null;
        item.DraftText = string.Empty;
        item.Result = null;
        item.Status = StudioStatus.NeedsLyrics;
        item.StatusText = Loc("LyricsStudio.ClearedStatus");
        OnSelectedChanged(item);
        RaiseStartState();
    }

    /// <summary>Loads a lyrics file (.txt, or .lrc/.elrc/.ttml whose timestamps are dropped) into the lyrics box.</summary>
    [RelayCommand]
    private async Task ImportLyricsFile()
    {
        if (Selected is not { } item || PickLyricsFile is null) return;
        string? path;
        try { path = await PickLyricsFile(); } catch { return; }
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var lines = LyricsStudioEngine.ImportText(await File.ReadAllTextAsync(path), path);
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
            item.StatusText = !HasFfmpeg ? "ffmpeg is needed to decode songs — set its path under Settings → Advanced → Helper programs." : "Download Lullaby first.";
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
        // A word belongs to its line: the bar's line is the word's.
        if (newValue is not null) SelectedLine = newValue.Line;
        OnPropertyChanged(nameof(HasSelectedWord));
        RaiseNudgeTarget();
    }

    public bool HasSelectedWord => SelectedWord is not null;

    // ── Nudge bar: the selected line or word, earlier / later by 10 or 100 ms ──

    /// <summary>The line the nudge bar acts on: its time pill, text box or one of its words was clicked.</summary>
    [ObservableProperty] private ReviewLine? _selectedLine;

    partial void OnSelectedLineChanged(ReviewLine? oldValue, ReviewLine? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        if (SelectedWord is { } word && !ReferenceEquals(word.Line, newValue)) SelectedWord = null;
        RaiseNudgeTarget();
    }

    /// <summary>What a nudge moves: the whole line or word (start and end together), or one edge.</summary>
    [ObservableProperty] private NudgeEdge _nudgeEdge = NudgeEdge.Whole;

    partial void OnNudgeEdgeChanged(NudgeEdge value)
    {
        OnPropertyChanged(nameof(NudgeWhole));
        OnPropertyChanged(nameof(NudgeStart));
        OnPropertyChanged(nameof(NudgeEnd));
    }

    // The edge picker's three segments (radio buttons); a segment being unchecked changes nothing.
    public bool NudgeWhole { get => NudgeEdge == NudgeEdge.Whole; set { if (value) NudgeEdge = NudgeEdge.Whole; } }
    public bool NudgeStart { get => NudgeEdge == NudgeEdge.Start; set { if (value) NudgeEdge = NudgeEdge.Start; } }
    public bool NudgeEnd { get => NudgeEdge == NudgeEdge.End; set { if (value) NudgeEdge = NudgeEdge.End; } }

    public bool HasNudgeTarget => SelectedLine is not null;

    /// <summary>The bar's label: what a nudge moves now, with its times.</summary>
    public string NudgeTargetText => SelectedWord is { } word
        ? Loc("LyricsStudio.NudgeTargetWord", word.Text, word.TimeText, TimedLyricsBuilder.FormatTimestamp(word.End))
        : SelectedLine is { } line
            ? Loc("LyricsStudio.NudgeTargetLine", line.TimeText, TimedLyricsBuilder.FormatTimestamp(line.End))
            : Loc("LyricsStudio.NudgeNoTarget");

    private void RaiseNudgeTarget()
    {
        OnPropertyChanged(nameof(HasNudgeTarget));
        OnPropertyChanged(nameof(NudgeTargetText));
    }

    /// <summary>Focus on a line's text box (or a click on its row): the bar acts on that line, keeping a word of it.</summary>
    internal void SelectLine(ReviewLine? line)
    {
        if (line is null || !ReviewLines.Contains(line)) return;
        SelectedLine = line;
    }

    /// <summary>The bar's steps and the , and . keys: "-100", "-10", "10" or "100" ms.</summary>
    [RelayCommand]
    private void NudgeSelection(string? milliseconds)
    {
        if (int.TryParse(milliseconds, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var ms))
            NudgeSelectionBy(TimeSpan.FromMilliseconds(ms));
    }

    /// <summary>
    /// Moves the selected word (else the selected line) by <paramref name="delta"/> along the
    /// chosen edge, by the <see cref="TimingNudge"/> rules: the neighbouring lines' starts and the
    /// song's length are the limits. One nudge is one change of the review (and of its draft).
    /// </summary>
    internal bool NudgeSelectionBy(TimeSpan delta)
    {
        if (IsRunning || SelectedLine is not { } line) return false;
        var i = ReviewLines.IndexOf(line);
        if (i < 0) return false;
        // A word dropped by a text edit no longer has a chip: the line is what is left selected.
        if (SelectedWord is { } gone && !line.Words.Contains(gone)) SelectedWord = null;
        TimeSpan? previous = i > 0 ? ReviewLines[i - 1].Start : null;
        TimeSpan? next = i + 1 < ReviewLines.Count ? ReviewLines[i + 1].Start : null;
        var duration = Selected?.Track.Duration ?? TimeSpan.Zero;
        var moved = line.Nudge(SelectedWord, NudgeEdge, delta, previous, next, duration);
        if (moved) OnPropertyChanged(nameof(NudgeTargetText));
        return moved;
    }

    /// <summary>
    /// "Hear it" and the / key: plays from a second before the time being nudged (the start, or
    /// the end with End chosen), so you hear it land. The time pill still plays from the line
    /// itself, with no lead-in (user ask 09-17).
    /// </summary>
    [RelayCommand]
    private Task Audition()
    {
        if (SelectedLine is not { } line) return Task.CompletedTask;
        var at = SelectedWord is { } word
            ? (NudgeEdge == NudgeEdge.End ? word.End : word.Start)
            : (NudgeEdge == NudgeEdge.End ? line.End : line.Start);
        return PlayFromTime(at - TimeSpan.FromSeconds(1));
    }

    /// <summary>Chip click: select the word and seek a little ahead of it so you hear it land.</summary>
    [RelayCommand]
    private Task SelectWord(ReviewWord? word)
    {
        if (word is null) return Task.CompletedTask;
        SelectedWord = word;
        return IsTapping ? Task.CompletedTask : PlayFromTime(word.Start - TimeSpan.FromSeconds(0.3));
    }

    [RelayCommand]
    private void ToggleWords(ReviewLine? line)
    {
        if (line is null) return;
        SelectLine(line);
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
    private bool _tapBlocked; // the last tap landed while a music video's audio played
    private bool _tapOffSong; // the last tap landed while this song was not the one playing

    public bool IsTapping => TapLine is not null;
    public string TapHint => TapLine is { } line
        ? (_tapBlocked
            ? "Tapping is off while the music video's audio plays · click the line's time to hear the song file"
            : _tapOffSong
            ? "Tapping needs this song playing · click the line's time to play it"
            : _tapIndex < line.Words.Count
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
        // Words are timed against the song file; a music video's audio runs on the clip's clock.
        _tapBlocked = _player.IsPlayingMusicVideoAudio;
        // The player's clock is this song's only while it plays it: once the song ended (stopped,
        // at 0:00) or another one started, a tap stamped that clock on the word — 0:00 took a
        // late line ahead of every line before it, and a save wrote the lyrics out of order.
        _tapOffSong = _player.CurrentTrack?.Id != Selected?.Track.Id || _player.State == PlaybackState.Stopped;
        if (_tapBlocked || _tapOffSong)
        {
            OnPropertyChanged(nameof(TapHint));
            return;
        }
        var now = _player.Position;
        if (_tapIndex < line.Words.Count)
        {
            line.Words[_tapIndex].IsTapTarget = false;
            // The first word stays after the line before, as a nudge keeps it (TimingNudge):
            // tapped earlier, the line went ahead of it and the save wrote the lyrics out of order.
            var i = ReviewLines.IndexOf(line);
            if (_tapIndex == 0 && i > 0 && now < ReviewLines[i - 1].Start + TimingNudge.Gap)
                now = ReviewLines[i - 1].Start + TimingNudge.Gap;
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
        _tapBlocked = false;
        _tapOffSong = false;
        TapLine = null;
        _tapIndex = 0;
    }

    private void FinishTap()
    {
        CancelTap();
        if (_player is { State: PlaybackState.Playing }) _player.PlayPauseCommand.Execute(null);
    }

    /// <summary>The time pill plays the line from its own timestamp, no pre-roll (user ask 09-17), and selects the line for the nudge bar.</summary>
    [RelayCommand]
    private Task PlayFromLine(ReviewLine? line)
    {
        if (line is null) return Task.CompletedTask;
        // The pill picks the line itself: a word of it selected before is let go.
        SelectedWord = null;
        SelectLine(line);
        return PlayFromTime(line.Start);
    }

    private async Task PlayFromTime(TimeSpan target)
    {
        if (_player is null || Selected is null) return;
        var track = Selected.Track;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        LogPlayerBefore("LyricsStudio.PlayFrom", track, target);
        if (_player.CurrentTrack?.Id != track.Id || _player.IsPlayingMusicVideoAudio)
        {
            // Lines are timed against the song file: never play them over a music video's audio.
            _player.RequestOriginalAudio(track);
            if (track.Duration > TimeSpan.Zero)
            {
                // Opened at the time. It used to start the song and seek at once: PlayTrack sets
                // the tag's length synchronously, so the wait below never waited, and the seek
                // reached the engine while it was still opening the song (dropped, or applied to
                // the song before it; owner 10-01, Preview after "Time every word").
                _player.PlayFrom(track, target);
                return;
            }
            // No length in the tags: start it, and seek once the engine reports one.
            _player.ReplaceQueueAndPlay(new[] { track }, 0);
            for (var i = 0; i < 40 && _player.Duration <= TimeSpan.Zero; i++)
                await Task.Delay(50);
        }
        if (_player.Duration <= TimeSpan.Zero) return;
        _player.SeekTo(target);
        if (_player.State != PlaybackState.Playing)
            _player.PlayPauseCommand.Execute(null);
    }

    /// <summary>
    /// Session-log line (Settings > Advanced > Copy Logs) with the player as the Studio found it
    /// before starting the song: owner 10-01, a Preview after "Time every word" moved the
    /// timeline with no sound, and only the player's state at that moment can say why.
    /// </summary>
    private void LogPlayerBefore(string what, Track track, TimeSpan target)
    {
        if (_player is null) return;
        var current = _player.CurrentTrack is null ? "none" : _player.CurrentTrack.Id == track.Id ? "this song" : "another song";
        DebugLogger.Info(DebugLogger.Category.Lyrics, what,
            $"{track.Title} at {target.TotalSeconds:0.00}s | player: current={current}, state={_player.State}, " +
            $"pos={_player.Position.TotalSeconds:0.0}s, volume={_player.Volume}, muted={_player.IsMuted}, videoAudio={_player.IsPlayingMusicVideoAudio}");
    }

    [RelayCommand]
    private async Task Close()
    {
        _runCts?.Cancel();
        _lrcRedCts?.Cancel();
        if (Selected is { } current) PersistDraft(current);
        // The dialog's pane leaves with it: the lyrics page and side panel show the song's own lyrics
        // again, and an edit still waiting for the pane is not pushed after the dialog closed.
        SuspendLivePreview();
        if (_savedCount > 0)
        {
            try { await _library.SaveAsync(); } catch { }
        }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary><see cref="Loaded"/> = existing timed lyrics shown for review without a model run.
    /// <see cref="NeedsLyrics"/> = no lyrics found, or a transcript waiting to be corrected: the lyrics box is shown.</summary>
    public enum StudioStatus { Waiting, Working, Ready, Saved, Skipped, Failed, Loaded, NeedsLyrics }

    /// <summary>One step of a song's run in the review pane's step row.</summary>
    public sealed partial class StudioStageStep(string label) : ObservableObject
    {
        [ObservableProperty] private string _label = label;
        [ObservableProperty] private bool _isActive;
        [ObservableProperty] private bool _isDone;
    }

    public sealed partial class StudioItem : ObservableObject
    {
        public StudioItem(Track track)
        {
            Track = track;
            _existingFormat = LyricsFormatDetector.Detect(track);
        }

        public Track Track { get; }

        /// <summary>A queue row reads its song, "title, artist" (it read
        /// "Noctis.ViewModels.LyricsStudioViewModel+StudioItem").</summary>
        public override string ToString() => Track.ToString();

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
        /// <summary>What the lyrics box was filled with from the song itself (not the user's work).</summary>
        internal string? PrefilledText { get; set; }
        /// <summary>The user cleared the song's lyrics (Clear lyrics): its .lrc / tags are not loaded, prefilled or timed again until Save.</summary>
        public bool LyricsCleared { get; set; }

        /// <summary>
        /// The lyrics box holds work that lives nowhere else: text typed, pasted or imported, or a
        /// transcript (minutes of model time). Drafts only keep finished reviews.
        /// </summary>
        public bool HasUnsavedLyricsBox => Status is StudioStatus.Waiting or StudioStatus.NeedsLyrics or StudioStatus.Failed
            && (IsTranscriptDraft || HeardWords is { Count: > 0 }
                || !string.IsNullOrWhiteSpace(DraftText) && DraftText != PrefilledText);

        public void RefreshExistingFormat()
        {
            try { ExistingFormat = LyricsFormatDetector.Detect(Track); } catch { }
        }

        /// <summary>Timed lyrics loaded from the song itself (null until selected, or when it has none).</summary>
        public ExistingLyrics? Existing { get; set; }

        /// <summary>lrc.red's recording of the song (null: none, or not asked yet — see <see cref="LrcRedLooked"/>).</summary>
        internal LrcRedMatch? LrcRedMatch { get; set; }
        /// <summary>lrc.red's lookup has answered for this song (or is asking).</summary>
        internal bool LrcRedLooked { get; set; }

        /// <summary>Set by Re-sync / Upgrade so the "skip songs that already have this format" rule does not apply.</summary>
        public bool ForceRun { get; set; }

        /// <summary>The review a re-run replaces, as it was on screen: restored if the re-run does not finish.</summary>
        internal Kept? BeforeRerun { get; set; }
        internal sealed record Kept(LyricsStudioResult Result, StudioStatus Status, string StatusText, bool Dirty);
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
        /// <summary>"42%" while the song is worked on.</summary>
        [ObservableProperty] private string _progressText = string.Empty;
        /// <summary>What the run is doing now, in words ("Listening to the vocals…"), for the review pane.</summary>
        [ObservableProperty] private string _stageText = string.Empty;
        [ObservableProperty] private LyricsStudioResult? _result;

        /// <summary>The three steps the review pane shows while the song runs: decode, listen, time.</summary>
        public IReadOnlyList<StudioStageStep> Steps { get; } = new[]
        {
            new StudioStageStep(Localization.Loc.T("LyricsStudio.StepDecode")),
            new StudioStageStep(Localization.Loc.T("LyricsStudio.StepListen")),
            new StudioStageStep(Localization.Loc.T("LyricsStudio.StepTime")),
        };

        /// <summary>The run's progress for this song (set while it is worked on).</summary>
        internal SongProgressMeter? Meter { get; private set; }
        private bool _transcribing;
        private bool _wordTimings;

        /// <summary>A run starts on this song: fresh meter, steps and labels.</summary>
        internal void BeginRun(SongProgressMeter meter, bool transcribing, bool wordTimings)
        {
            Meter = meter;
            _transcribing = transcribing;
            _wordTimings = wordTimings;
            Steps[1].Label = Localization.Loc.T(transcribing ? "LyricsStudio.StepTranscribe" : "LyricsStudio.StepListen");
            Progress = 0;
            ShowStage(LyricsStudioStage.FindingLyrics, 0);
        }

        /// <summary>Shows a step and the song's progress (called on the Studio's UI tick).</summary>
        internal void ShowStage(LyricsStudioStage stage, double overall)
        {
            Progress = overall;
            ProgressText = DownloadMeter.PercentText(overall);
            var (longKey, shortKey) = stage switch
            {
                LyricsStudioStage.FindingLyrics => ("LyricsStudio.StageFindingLyrics", "LyricsStudio.StageShortFinding"),
                LyricsStudioStage.Decoding => ("LyricsStudio.StageDecoding", "LyricsStudio.StageShortDecoding"),
                LyricsStudioStage.Listening => _transcribing
                    ? ("LyricsStudio.StageTranscribing", "LyricsStudio.StageShortTranscribing")
                    : ("LyricsStudio.StageListening", "LyricsStudio.StageShortListening"),
                _ => (_wordTimings ? "LyricsStudio.StageTimingWords" : "LyricsStudio.StageTimingLines", "LyricsStudio.StageShortTiming"),
            };
            StageText = Localization.Loc.T(longKey);
            StatusText = Localization.Loc.T("LyricsStudio.StagePercent", Localization.Loc.T(shortKey), ProgressText);
            var current = stage switch
            {
                LyricsStudioStage.FindingLyrics or LyricsStudioStage.Decoding => 0,
                LyricsStudioStage.Listening => 1,
                LyricsStudioStage.Aligning => 2,
                _ => 3,
            };
            for (var i = 0; i < Steps.Count; i++)
            {
                Steps[i].IsDone = i < current;
                Steps[i].IsActive = i == current;
            }
        }

        public bool IsWorking => Status == StudioStatus.Working;
        public bool IsReady => Status == StudioStatus.Ready;
        public bool IsSaved => Status == StudioStatus.Saved;
        public bool IsFailed => Status == StudioStatus.Failed;
    }
}
