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
public sealed record LyricsStudioPrefs(string Model, string Language, bool WordTimings);

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

    // ── Model state ──
    [ObservableProperty] private bool _isModelInstalled;
    [ObservableProperty] private string _modelStatusText = string.Empty;
    [ObservableProperty] private bool _isDownloadingModel;
    [ObservableProperty] private double _modelProgress;

    // ── Run state ──
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _runStatusText = string.Empty;

    public bool HasFfmpeg => _engine.HasFfmpeg;
    public bool CanStart => !IsRunning && IsModelInstalled && HasFfmpeg && Queue.Any(i => i.Status == StudioStatus.Waiting);
    public bool ShowModelDownload => !IsModelInstalled && !IsDownloadingModel;

    // ── Review ──
    public bool HasReview => Selected is { Status: StudioStatus.Ready or StudioStatus.Saved, Result: not null };
    public string ReviewTitle => Selected?.Title ?? string.Empty;
    public string ReviewSubtitle => Selected?.Subtitle ?? string.Empty;
    public string ReviewSourceText => Selected?.Result?.Source switch
    {
        LyricsStudioSource.ExistingLyrics => "Timed from the song's own lyrics",
        LyricsStudioSource.Lrclib => "Lyrics from LRCLIB, timed against the audio",
        LyricsStudioSource.Transcription => "Transcribed — no lyrics were available, so check the words",
        _ => string.Empty,
    };
    public string ReviewConfidenceText => Selected?.Result is { } r
        ? $"{Math.Round(r.Confidence * 100)}% of the words were heard · {r.Lines.Count} lines"
        : string.Empty;
    public bool ReviewIsTranscription => Selected?.Result?.Source == LyricsStudioSource.Transcription;
    public bool CanSave => Selected is { Status: StudioStatus.Ready } && ReviewLines.Count > 0;
    public string SummaryText => _savedCount == 0 ? string.Empty : $"{_savedCount} saved";

    public event EventHandler? Closed;

    public LyricsStudioViewModel(
        IReadOnlyList<Track> tracks,
        ILyricsStudioEngine engine,
        LyricsWriter writer,
        ILibraryService library,
        PlayerViewModel? player,
        Func<AppSettings> settings,
        Action<LyricsStudioPrefs> savePrefs)
    {
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
        _loadingPrefs = false;

        foreach (var t in tracks.Where(t => t.SourceType == SourceType.Local))
            Queue.Add(new StudioItem(t));
        Queue.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanStart));

        RefreshModelState();
        RunStatusText = !HasFfmpeg
            ? "ffmpeg is needed to decode songs — set its path under Settings → Audio → Audio tools."
            : Queue.Count == 0 ? "No local songs selected." : $"{Queue.Count} song{(Queue.Count == 1 ? "" : "s")} queued.";
    }

    partial void OnSelectedModelChanged(WhisperModelInfo value)
    {
        RefreshModelState();
        PersistPrefs();
    }

    partial void OnSelectedLanguageChanged(SpeechLanguageOption value) => PersistPrefs();
    partial void OnWordTimingsChanged(bool value) => PersistPrefs();
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanStart));
    partial void OnIsModelInstalledChanged(bool value) { OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(ShowModelDownload)); }
    partial void OnIsDownloadingModelChanged(bool value) => OnPropertyChanged(nameof(ShowModelDownload));

    partial void OnSelectedChanged(StudioItem? value)
    {
        ReviewLines.Clear();
        if (value?.Result is { } result)
            foreach (var line in result.Lines)
                ReviewLines.Add(new ReviewLine(line));
        RaiseReviewChanged();
    }

    private void RaiseReviewChanged()
    {
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
        try { _savePrefs(new LyricsStudioPrefs(SelectedModel.Size.ToString(), SelectedLanguage.Code, WordTimings)); }
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
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        var options = new LyricsStudioOptions(SelectedModel.Size, SelectedLanguage.Code, AllowOnlineLyrics: true, ForceTranscription: TranscribeOnly);
        var done = 0;
        var total = Queue.Count(i => i.Status == StudioStatus.Waiting);
        try
        {
            using var session = _engine.OpenSession(SelectedModel.Size);
            foreach (var item in Queue.Where(i => i.Status == StudioStatus.Waiting).ToList())
            {
                if (ct.IsCancellationRequested) break;
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
            OnPropertyChanged(nameof(CanStart));
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
            _writer.Save(item.Track, plain, synced, embed);
            item.Status = StudioStatus.Saved;
            item.StatusText = WordTimings ? "Saved · word timings" : "Saved · line timings";
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
        TranscribeOnly = true;
        OnPropertyChanged(nameof(CanStart));
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

    [RelayCommand]
    private async Task PlayFromLine(ReviewLine? line)
    {
        if (line is null || _player is null || Selected is null) return;
        var track = Selected.Track;
        if (_player.CurrentTrack?.Id != track.Id)
        {
            _player.ReplaceQueueAndPlay(new[] { track }, 0);
            await Task.Delay(500);
        }
        var duration = _player.Duration.TotalSeconds > 0 ? _player.Duration : track.Duration;
        if (duration.TotalSeconds <= 0) return;
        var target = line.Start - TimeSpan.FromSeconds(0.8);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        _player.SeekToPositionCommand.Execute(target.TotalSeconds / duration.TotalSeconds);
        if (_player.State != PlaybackState.Playing)
            _player.PlayPauseCommand.Execute(null);
    }

    [RelayCommand]
    private async Task Close()
    {
        _runCts?.Cancel();
        if (_savedCount > 0)
        {
            try { await _library.SaveAsync(); } catch { }
        }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public enum StudioStatus { Waiting, Working, Ready, Saved, Skipped, Failed }

    public sealed partial class StudioItem : ObservableObject
    {
        public StudioItem(Track track)
        {
            Track = track;
            var has = !string.IsNullOrWhiteSpace(track.SyncedLyrics) ? "has synced lyrics" : !string.IsNullOrWhiteSpace(track.Lyrics) ? "plain lyrics" : "no lyrics yet";
            StatusText = has;
        }

        public Track Track { get; }
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

    public sealed partial class ReviewLine : ObservableObject
    {
        private readonly IReadOnlyList<AlignedWord> _words;
        private readonly string _originalText;

        public ReviewLine(AlignedLine line)
        {
            _words = line.Words;
            _originalText = line.Text;
            _text = line.Text;
            _start = line.Start;
            End = line.End;
            Confidence = line.Confidence;
            Interpolated = line.Interpolated;
        }

        [ObservableProperty] private string _text;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TimeText))]
        private TimeSpan _start;
        public TimeSpan End { get; private set; }
        public double Confidence { get; }
        public bool Interpolated { get; }
        public bool IsLow => Interpolated || Confidence < 0.5;
        public string TimeText => TimedLyricsBuilder.FormatTimestamp(Start);

        public void Shift(TimeSpan delta)
        {
            var s = Start + delta;
            if (s < TimeSpan.Zero) delta -= s; // clamp at zero without breaking word order
            Start += delta;
            End += delta;
            _shift += delta;
        }

        private TimeSpan _shift;

        /// <summary>Edited text keeps its word times when the word count matches; otherwise words are spread evenly over the line.</summary>
        public AlignedLine ToAlignedLine()
        {
            var text = (Text ?? string.Empty).Trim();
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            IReadOnlyList<AlignedWord> timed;
            if (text == _originalText.Trim() || words.Length == _words.Count)
                timed = _words.Select((w, i) => new AlignedWord(words.Length == _words.Count ? words[i] : w.Text, w.Start + _shift, w.End + _shift)).ToList();
            else
            {
                var span = End > Start ? End - Start : TimeSpan.FromMilliseconds(400 * Math.Max(1, words.Length));
                var slice = words.Length == 0 ? span : span / words.Length;
                timed = words.Select((w, i) => new AlignedWord(w, Start + slice * i, Start + slice * (i + 1))).ToList();
            }
            return new AlignedLine(text, Start, End > Start ? End : Start, timed, Confidence, Interpolated);
        }
    }
}
