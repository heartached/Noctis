using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the Metadata window — edits track metadata across 5 tabs.
/// </summary>
public partial class MetadataViewModel : ViewModelBase
{
    private readonly IMetadataService _metadata;
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly IAnimatedCoverService _animatedCovers;
    private readonly Track _track;
    private readonly bool _albumScoped;
    private readonly List<Track>? _albumTracks;
    private readonly bool _multiSelect;

    // Album-scoped editing: fields whose value differs across the album show the
    // "Mixed" placeholder, and only fields the user actually edits are fanned out
    // to every track on save (so per-track values like track numbers survive).
    private readonly HashSet<string> _mixedFields = new();
    private readonly Dictionary<string, string> _albumLoaded = new();
    private bool _loadedIsCompilation;
    private bool _loadedShowComposerInAllViews;
    private int _loadedRating;
    private bool _loadedIsDisliked;
    private bool _loadedUseWorkAndMovement;
    // Album-scoped Options snapshot — only fields the user actually changes are
    // fanned out to every track, so per-track Options (volume, EQ, start/stop, …)
    // aren't clobbered with the first track's values on an unrelated save.
    private bool _loadedSkipWhenShuffling;
    private bool _loadedRememberPlaybackPosition;
    private string _loadedMediaKind = string.Empty;
    private string _loadedReleaseTypeOverride = "Auto";
    private long _loadedStartTimeMs;
    private long _loadedStopTimeMs;
    private int _loadedVolumeAdjust;
    private string _loadedEqPreset = string.Empty;

    // ── Tab selection ──
    [ObservableProperty] private int _selectedTabIndex;

    // ── Details tab ──
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _artist = string.Empty;
    [ObservableProperty] private string _albumArtist = string.Empty;
    [ObservableProperty] private string _performer = string.Empty;
    [ObservableProperty] private string _album = string.Empty;
    [ObservableProperty] private string _genre = string.Empty;
    [ObservableProperty] private string _composer = string.Empty;
    [ObservableProperty] private string _trackNumber = string.Empty;
    [ObservableProperty] private string _trackCount = string.Empty;
    [ObservableProperty] private string _discNumber = string.Empty;
    [ObservableProperty] private string _discCount = string.Empty;
    [ObservableProperty] private string _bpm = string.Empty;
    [ObservableProperty] private string _year = string.Empty;
    [ObservableProperty] private bool _isCompilation;
    [ObservableProperty] private bool _showComposerInAllViews;
    [ObservableProperty] private string _grouping = string.Empty;

    // Work & Movement (classical)
    [ObservableProperty] private bool _useWorkAndMovement;
    [ObservableProperty] private string _workName = string.Empty;
    [ObservableProperty] private string _movementName = string.Empty;
    [ObservableProperty] private string _movementNumber = string.Empty;
    [ObservableProperty] private string _movementCount = string.Empty;

    [ObservableProperty] private string _playCount = string.Empty;
    [ObservableProperty] private string _comment = string.Empty;
    [ObservableProperty] private int _rating;
    [ObservableProperty] private bool _isDisliked;

    // ── Artwork tab ──
    [ObservableProperty] private Bitmap? _artworkPreview;
    [ObservableProperty] private bool _hasArtwork;
    private byte[]? _newArtworkData;
    private bool _artworkRemoved;

    partial void OnHasArtworkChanged(bool value) => OnPropertyChanged(nameof(ShowAlbumArtPlaceholderText));

    // ── Rename-by-pattern (multi-select only) ──
    [ObservableProperty] private bool _applyRename;
    [ObservableProperty] private string _renamePattern = "%tracknumber2% - %title%";
    public ObservableCollection<RenamePreview> RenamePreviews { get; } = new();

    public sealed class RenamePreview
    {
        public string OriginalName { get; set; } = string.Empty;
        public string NewName { get; set; } = string.Empty;
        public bool Conflict { get; set; }
    }

    // ── Animated cover tab ──
    [ObservableProperty] private string? _animatedCoverPath;
    [ObservableProperty] private bool _hasAnimatedCover;
    private string? _newAnimatedCoverSource;
    private bool _animatedCoverRemoved;

    // ── iTunes artwork search ──
    public ObservableCollection<ArtworkSearchResult> ArtworkSearchResults { get; } = new();
    public ObservableCollection<AnimatedArtworkSearchResult> AnimatedArtworkSearchResults { get; } = new();
    [ObservableProperty] private bool _isSearchingArtwork;
    [ObservableProperty] private bool _isDownloadingArtwork;
    [ObservableProperty] private bool _hasArtworkSearchResults;
    [ObservableProperty] private string _artworkSearchStatus = string.Empty;
    [ObservableProperty] private bool _isArtworkSearchOpen;

    // ── iTunes animated-cover search ──
    [ObservableProperty] private bool _isSearchingAnimatedCover;
    [ObservableProperty] private bool _isDownloadingAnimatedCover;

    /// <summary>True while a Save is in flight — drives the "Saving…" spinner on the button.</summary>
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _hasAnimatedArtworkSearchResults;
    [ObservableProperty] private bool _isAnimatedArtworkSearchOpen;
    [ObservableProperty] private string _animatedSearchStatus = string.Empty;
    // What the user typed into the animated-artwork box: an album name to search for
    // instead of the loaded track's tags, or an Apple Music URL / album ID to go
    // straight to one album. Empty means "use this track's metadata".
    [ObservableProperty] private string _animatedSearchInput = string.Empty;
    // Path to a small variant downloaded only to drive the live preview inside
    // the search popup. Cleared/replaced on every search; deleted when no longer used.
    [ObservableProperty] private string? _animatedPreviewPath;

    public string AnimatedCoverFileName => string.IsNullOrEmpty(AnimatedCoverPath) ? string.Empty : Path.GetFileName(AnimatedCoverPath);
    partial void OnAnimatedCoverPathChanged(string? value) => OnPropertyChanged(nameof(AnimatedCoverFileName));

    // ── Lyrics tab ──
    [ObservableProperty] private string _lyrics = string.Empty;
    [ObservableProperty] private string _syncedLyrics = string.Empty;
    [ObservableProperty] private bool _hasCustomLyrics;
    [ObservableProperty] private bool _hasCustomSyncedLyrics;

    // ── Synced lyrics manual editor ──
    // Per-line timestamp editing (type/nudge/clear) presented instead of the raw
    // [mm:ss.xx] textarea. The list is the source the user touches; edits flow back
    // into SyncedLyrics (which Save still reads).
    public ObservableCollection<SyncedLyricEditorLine> SyncedLyricLines { get; } = new();
    [ObservableProperty] private bool _hasSyncedLines;
    /// <summary>True when any synced line carries inline word-timing tags (enhanced LRC).</summary>
    [ObservableProperty] private bool _hasWordTimedLines;

    // ── Options tab ──
    [ObservableProperty] private bool _skipWhenShuffling;
    [ObservableProperty] private bool _rememberPlaybackPosition;
    [ObservableProperty] private string _mediaKind = "Music";

    /// <summary>Override release type. "Auto" defers to the scanner-detected value.</summary>
    [ObservableProperty] private string _releaseTypeOverride = "Auto";
    [ObservableProperty] private bool _hasStartTime;
    [ObservableProperty] private string _startTime = "0:00.000";
    [ObservableProperty] private bool _hasStopTime;
    [ObservableProperty] private string _stopTime = "0:00.000";
    [ObservableProperty] private int _volumeAdjust;
    [ObservableProperty] private string _selectedEqPreset = "None";

    // ── File tab (read-only) ──
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _fileFormat = string.Empty;
    [ObservableProperty] private string _codec = string.Empty;
    [ObservableProperty] private string _losslessOrLossy = string.Empty;
    [ObservableProperty] private string _bitrate = string.Empty;
    [ObservableProperty] private string _sampleRate = string.Empty;
    [ObservableProperty] private string _bitsPerSample = string.Empty;
    [ObservableProperty] private string _channels = string.Empty;
    [ObservableProperty] private string _fileSize = string.Empty;
    [ObservableProperty] private string _duration = string.Empty;
    [ObservableProperty] private string _dateAdded = string.Empty;
    [ObservableProperty] private string _dateModified = string.Empty;
    [ObservableProperty] private string _fileLocation = string.Empty;
    [ObservableProperty] private string _folderName = string.Empty;
    [ObservableProperty] private string _fullFilePath = string.Empty;
    [ObservableProperty] private string _copyright = string.Empty;

    // ── Advanced Details tab ──
    [ObservableProperty] private string _titleSort = string.Empty;
    [ObservableProperty] private string _artistSort = string.Empty;
    [ObservableProperty] private string _albumSort = string.Empty;
    [ObservableProperty] private string _albumArtistSort = string.Empty;
    [ObservableProperty] private string _composerSort = string.Empty;

    [ObservableProperty] private string _conductor = string.Empty;
    [ObservableProperty] private string _lyricist = string.Empty;
    [ObservableProperty] private string _publisher = string.Empty;
    [ObservableProperty] private string _encodedBy = string.Empty;

    [ObservableProperty] private string _isrc = string.Empty;
    [ObservableProperty] private string _catalogNumber = string.Empty;
    [ObservableProperty] private string _barcode = string.Empty;

    [ObservableProperty] private string _selectedAdvisory = "None";

    // ── "Album is explicit" (Details tab, whole-album dialog only) ──
    // One checkbox marks every track of the album explicit (or clears it), written per
    // file as ITUNESADVISORY on Save. A single track keeps its advisory picker on the
    // Advanced tab; a multi-track selection is not an album, so it gets neither.
    [ObservableProperty] private bool _isExplicit;
    private bool _loadedIsExplicit;

    public bool ShowExplicitCheckbox => _albumScoped && !_multiSelect;
    public string ExplicitCheckboxLabel => "Album is explicit";

    [ObservableProperty] private string _language = string.Empty;
    [ObservableProperty] private string _mood = string.Empty;
    [ObservableProperty] private string _advDescription = string.Empty;
    [ObservableProperty] private string _advReleaseDate = string.Empty;

    [ObservableProperty] private string _encoder = string.Empty;
    [ObservableProperty] private string _replayGainTrackGain = string.Empty;
    [ObservableProperty] private string _replayGainTrackPeak = string.Empty;
    [ObservableProperty] private string _replayGainAlbumGain = string.Empty;
    [ObservableProperty] private string _replayGainAlbumPeak = string.Empty;

    public ObservableCollection<CustomTagItem> CustomTags { get; } = new();
    private AdvancedTagIO.AdvancedFields? _originalAdvancedFields;

    public static readonly string[] AdvisoryOptions = { "None", "Explicit", "Clean" };

    public bool ShowAdvancedTab => !_albumScoped;

    /// <summary>Track title and artist for the header.</summary>
    public string HeaderTitle => _multiSelect
        ? $"{DistinctArtistCount} artists selected"
        : (_albumScoped && !string.IsNullOrWhiteSpace(_track.Album) ? _track.Album : _track.Title);
    public string HeaderArtist => _multiSelect ? $"{SongCount} songs selected" : _track.Artist;
    public string HeaderAlbum => _multiSelect ? string.Empty : _track.Album;

    /// <summary>True when editing an arbitrary multi-track selection (blank art, "N selected" header).</summary>
    public bool IsMultiSelect => _multiSelect;
    /// <summary>Album-art placeholder text shows only for non-multi-select with no artwork.</summary>
    public bool ShowAlbumArtPlaceholderText => !HasArtwork && !_multiSelect;
    private int SongCount => _albumTracks?.Count ?? 1;
    private int DistinctArtistCount => _albumTracks == null
        ? 1
        : _albumTracks.Select(t => t.Artist ?? string.Empty).Distinct().Count();
    public bool HeaderIsExplicit => _track.IsExplicit;
    public bool HeaderIsFavorite => _track.IsFavorite;
    public string HeaderAudioQualityBadge => _track.AudioQualityBadge;
    public string HeaderAudioQualityDetail => _track.AudioQualityDetailedInfo;
    public string HeaderAudioQualityDescription => _track.AudioQualityDescription;

    public bool ShowLyricsTab => !_albumScoped;
    public bool ShowSyncedLyricsTab => !_albumScoped;
    public bool ShowFileTab => !_albumScoped;

    /// <summary>Animated Artwork is per-album; hide it for arbitrary multi-track selections.</summary>
    public bool ShowAnimatedArtworkTab => !_multiSelect;

    /// <summary>The rename-by-pattern section is offered only when editing a multi-track selection.</summary>
    public bool ShowRenameSection => _multiSelect;

    /// <summary>Per-track-only Details fields (Title, Performer) are hidden when editing a whole album.</summary>
    public bool ShowTrackFields => !_albumScoped;

    // "Mixed" placeholders for album-scoped Details fields that differ across tracks.
    public string AlbumWatermark => Watermark(nameof(Album));
    public string AlbumArtistWatermark => Watermark(nameof(AlbumArtist));
    public string ArtistWatermark => Watermark(nameof(Artist));
    public string ComposerWatermark => Watermark(nameof(Composer));
    public string GroupingWatermark => Watermark(nameof(Grouping));
    public string GenreWatermark => Watermark(nameof(Genre));
    public string YearWatermark => Watermark(nameof(Year));
    public string TrackNumberWatermark => Watermark(nameof(TrackNumber));
    public string TrackCountWatermark => Watermark(nameof(TrackCount));
    public string DiscNumberWatermark => Watermark(nameof(DiscNumber));
    public string DiscCountWatermark => Watermark(nameof(DiscCount));
    public string BpmWatermark => Watermark(nameof(Bpm));
    public string CommentWatermark => Watermark(nameof(Comment));
    public string WorkNameWatermark => Watermark(nameof(WorkName));
    public string MovementNameWatermark => Watermark(nameof(MovementName));
    public string MovementNumberWatermark => Watermark(nameof(MovementNumber));
    public string MovementCountWatermark => Watermark(nameof(MovementCount));

    /// <summary>Formatted play count display with last played date.</summary>
    public string PlayCountDisplay
    {
        get
        {
            // A staged (not yet saved) reset shows as zero.
            if (_resetPlayCountPending) return "0";

            // Album-scoped: aggregate across every track — total plays and the
            // most recent "last played" of the album.
            if (_albumScoped && _albumTracks != null && _albumTracks.Count > 0)
            {
                var total = _albumTracks.Sum(t => (long)t.PlayCount); // long: an int Sum throws on overflow
                var lastPlayed = _albumTracks
                    .Where(t => t.LastPlayed.HasValue)
                    .Select(t => t.LastPlayed!.Value)
                    .DefaultIfEmpty()
                    .Max();
                return lastPlayed != default
                    ? $"{total} (Last Played {lastPlayed.ToLocalTime():M/d/yyyy, h:mm tt})"
                    : total.ToString();
            }

            if (_track.LastPlayed.HasValue)
            {
                var lastPlayed = _track.LastPlayed.Value.ToLocalTime();
                return $"{_track.PlayCount} (Last Played {lastPlayed:M/d/yyyy, h:mm tt})";
            }
            return _track.PlayCount.ToString();
        }
    }

    /// <summary>Suggestions for the editable genre box: the edited track(s)' genres, every genre
    /// already in the library and the built-in list, trimmed, deduped case-insensitively (first
    /// spelling wins) and sorted. The box takes any typed text (GitHub #123, 2026-10-10), so a
    /// new genre is saved like a picked one and is suggested from the library from then on; an
    /// off-list value (typed, or applied by a metadata search) shows as the box's text, so it is
    /// no longer inserted here (that would add every keystroke).</summary>
    public ObservableCollection<string> GenreOptions { get; } = new();

    /// <summary>Available genres for the genre dropdown.</summary>
    public static readonly string[] AvailableGenres = new[]
    {
        "Afrobeats", "Alternative", "Baile Funk", "Blues/R&B", "Books & Spoken",
        "Children's Music", "Christian", "Classical", "Comedy", "Country",
        "Dance", "Easy Listening", "Electronic", "Folk", "Hip Hop/Rap",
        "Hip-Hop", "Hip-Hop/Rap", "Holiday", "House", "Indie Pop", "Industrial",
        "Jazz", "K-Pop", "Latin", "Latin Rap", "Música Mexicana",
        "Música tropical", "New Age", "Pop", "Pop Latino", "R&B/Soul",
        "Rap", "Reggae", "Religious", "Rock", "Rock y Alternativo",
        "Soundtrack", "Techno", "Trance", "Trap", "Turkish Alternative",
        "Unclassifiable", "Urbano latino", "World", "Worldwide"
    };

    /// <summary>Fills <see cref="GenreOptions"/> (see there). The edited tracks go first so
    /// their own spelling is the one listed.</summary>
    private void BuildGenreOptions()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Add(string? genre)
        {
            var g = genre?.Trim();
            if (!string.IsNullOrEmpty(g) && seen.Add(g)) list.Add(g);
        }

        Add(_track.Genre);
        if (_albumTracks != null)
            foreach (var t in _albumTracks) Add(t.Genre);
        if (_library != null)
            foreach (var t in _library.Tracks) Add(t.Genre);
        foreach (var g in AvailableGenres) Add(g);
        list.Sort(StringComparer.CurrentCultureIgnoreCase);

        GenreOptions.Clear();
        foreach (var g in list) GenreOptions.Add(g);
    }

    /// <summary>Available media kinds for the Options tab dropdown.</summary>
    public static string[] AvailableMediaKinds => Track.AvailableMediaKinds;

    /// <summary>Release-type override choices for the Options tab dropdown.
    /// "Auto" preserves the scanner-detected value.</summary>
    public static readonly string[] AvailableReleaseTypes =
    {
        "Auto", "Album", "Single", "EP", "Compilation", "Live", "Remix", "Soundtrack", "Other"
    };

    /// <summary>EQ presets for the Options tab dropdown (None + all presets from Settings).</summary>
    public static readonly string[] OptionsEqPresets = BuildOptionsEqPresets();

    private static string[] BuildOptionsEqPresets()
    {
        var list = new List<string> { "None" };
        for (int i = 1; i < SettingsViewModel.EqPresetNames.Length; i++)
            list.Add(SettingsViewModel.EqPresetNames[i]);
        return list.ToArray();
    }

    /// <summary>What the Options tab dropdown lists: <see cref="OptionsEqPresets"/> plus the
    /// user's saved presets (GitHub #95), set by <see cref="SetUserEqPresets"/> before the
    /// window opens.</summary>
    public IReadOnlyList<string> EqPresetOptions { get; private set; } = OptionsEqPresets;

    /// <summary>Adds the user's saved EQ presets to <see cref="EqPresetOptions"/>. A track tagged
    /// with a preset that no longer exists keeps it listed, so opening the editor can't blank it.</summary>
    public void SetUserEqPresets(IEnumerable<string> names)
    {
        var list = OptionsEqPresets.Concat(names).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!string.IsNullOrEmpty(SelectedEqPreset))
        {
            // Preset names match case-insensitively (as SettingsViewModel resolves them); the
            // ComboBox matches exactly, so point the selection at the listed spelling.
            var listed = list.FirstOrDefault(n => string.Equals(n, SelectedEqPreset, StringComparison.OrdinalIgnoreCase));
            if (listed == null) list.Add(SelectedEqPreset);
            else if (listed != SelectedEqPreset) SelectedEqPreset = listed;
        }
        EqPresetOptions = list;
        OnPropertyChanged(nameof(EqPresetOptions));
    }

    /// <summary>Fires when the user clicks OK and changes were saved.</summary>
    public event EventHandler? ChangesSaved;

    /// <summary>
    /// Fires just before Save replaces or deletes this track's animated cover, so the
    /// player can drop its reference and release the file handle LibVLC holds on the
    /// currently playing loop (deletes silently fail while the file is open).
    /// </summary>
    public event EventHandler? AnimatedCoverChanging;

    /// <summary>Fires when the window should close.</summary>
    public event EventHandler? CloseRequested;

    public MetadataViewModel(Track track, IMetadataService metadata, ILibraryService library, IPersistenceService persistence, IAnimatedCoverService animatedCovers, bool albumScoped = false, List<Track>? albumTracks = null, ITunesArtworkService? itunes = null, ILrcLibService? lrcLib = null, bool multiSelect = false, Services.MetadataSearch.IMetadataSearchService? metadataSearch = null)
    {
        _track = track;
        _metadata = metadata;
        _library = library;
        _persistence = persistence;
        _animatedCovers = animatedCovers;
        _itunes = itunes;
        _lrcLib = lrcLib;
        // Null-safe: until the search engine is registered (or in a build without it) the
        // panel shows "Search unavailable" instead of the old silent no-op.
        _metadataSearch = metadataSearch ?? App.Services?.GetService<Services.MetadataSearch.IMetadataSearchService>();
        _albumScoped = albumScoped;
        _albumTracks = albumTracks;
        _multiSelect = multiSelect;

        LoadFromTrack();
        // Cheap path-derived File-tab fields so the window isn't blank while
        // InitializeAsync loads the rest. Copyright must be seeded before any
        // Save (SaveInternalAsync writes it back to the track unconditionally).
        FileName = Path.GetFileName(_track.FilePath);
        FileLocation = _track.FilePath;
        FullFilePath = _track.FilePath;
        FolderName = Path.GetDirectoryName(_track.FilePath) ?? string.Empty;
        Copyright = _track.Copyright;
        // The animated cover, the lyric sidecars and the rename-preview conflict checks all
        // probe the track's folder; InitializeAsync does them off the UI thread (owner 10-08:
        // metadata window froze 230–880 ms on lyric sidecar reads and waited 1–2 s for tag
        // reads before showing). The ctor only copies in-memory Track state.
        SeedArtworkFromLibrary();
        // Seeded, or multi-select (never shows a cover): the window has its cover already.
        if (_multiSelect || ArtworkPreview != null) _coverShown.TrySetResult();

        if (_albumScoped && _albumTracks != null && _albumTracks.Count > 0)
            LoadAlbumScopedOverrides();

        CaptureLoadedTagSignatures();
        CaptureChangeBaseline();
    }

    /// <summary>
    /// True from construction until <see cref="InitializeAsync"/> has applied the
    /// file-backed state (sidecar lyrics, advanced tags, artwork, animated cover, File
    /// tab), on success or failure. The window is shown while this is true, so Save is
    /// disabled: it would otherwise write lyrics/advanced state that was never loaded.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isLoading = true;

    // Fields the user edited after the window opened but before InitializeAsync landed;
    // non-null only while the loaded values are being applied. Those fields keep the edit
    // instead of being overwritten under the user's cursor.
    private HashSet<string>? _editedWhileLoading;

    private bool KeepsEdit(string name) => _editedWhileLoading?.Contains(name) == true;

    /// <summary>
    /// Loads everything that opens or decodes files — two TagLib parses (file info,
    /// advanced tags), the cached-cover read + decode, the .lrc/.txt lyric sidecars, the
    /// animated-cover probe and the rename-preview conflict checks — off the UI thread.
    /// Started right before the window is shown, not awaited first (owner 10-08: metadata
    /// window froze 230–880 ms on lyric sidecar reads and waited 1–2 s for tag reads
    /// before showing), so the window opens with the in-memory Track fields and the
    /// file-backed ones fill in when this lands. Every file read inside is individually
    /// guarded, and <see cref="IsLoading"/> always ends false, even if something throws.
    /// </summary>
    public async Task InitializeAsync()
    {
        IsLoading = true;
        // What the window shows as the load starts (after AddUserEqPresets has respelled the
        // preset). Anything that differs when the load lands was typed by the user meanwhile.
        CaptureChangeBaseline();
        HashSet<string>? edited = null;
        string? loadedCustomTagsBaseline = null;
        var trackSynced = _track.SyncedLyrics;
        var trackPlain = _track.Lyrics;
        var renamePattern = RenamePattern;
        // The cover first, in its own task (owner 10-08: cover shows late in the metadata
        // window). It comes from the app's cover cache, but it used to wait behind the TagLib
        // parse of the audio file below, which takes 1–2 s on a busy or cold hard disk.
        var cover = Task.Run(LoadCachedCover);
        // Task.Run never throws synchronously: a failing read faults `load`, and the finally
        // below still ends IsLoading.
        var load = Task.Run(() =>
        {
            var fileInfo = _metadata.ReadFileInfo(_track.FilePath);

            AdvancedTagIO.AdvancedFields? advancedFields = null;
            if (!_albumScoped)
            {
                try { advancedFields = AdvancedTagIO.ReadAll(_track.FilePath); }
                catch { /* Non-fatal — advanced fields are best-effort */ }
            }

            // The album/multi-select dialog shows no lyrics and saves none: skip the reads.
            var lrc = ReadLyricSidecar(_track.FilePath, ".lrc", !_albumScoped && string.IsNullOrWhiteSpace(trackSynced));
            var txt = ReadLyricSidecar(_track.FilePath, ".txt", !_albumScoped && string.IsNullOrWhiteSpace(trackPlain));

            string? animated = null;
            try { animated = _animatedCovers.Resolve(_track); }
            catch { /* best effort, like the other probes */ }

            List<RenamePreview>? renamePreviews = null;
            if (_multiSelect)
            {
                try { renamePreviews = BuildRenamePreviews(renamePattern); }
                catch { }
            }

            return (fileInfo, advancedFields, lrc, txt, animated, renamePreviews);
        });
        var coverApplied = false;
        try
        {
            // The cover lands as soon as it is decoded, usually long before the tag read.
            // Both are applied here, one after the other, never from two threads at once.
            if (await Task.WhenAny(cover, load) == cover)
            {
                ApplyLoadedCover(await cover);
                coverApplied = true;
            }
            var loaded = await load;

            edited = TrackedFields.Select(f => f.Name)
                .Where(name => !Equals(ReadTracked(name), _changeBaseline.GetValueOrDefault(name)))
                .ToHashSet();
            var customTagsEdited = CustomTagsSignature() != _customTagsBaseline;
            _editedWhileLoading = edited;

            ApplyFileInfo(loaded.fileInfo);
            if (loaded.advancedFields != null)
            {
                ApplyAdvancedFields(loaded.advancedFields, mergeCustomTags: customTagsEdited);
                if (customTagsEdited)
                    loadedCustomTagsBaseline = CustomTagsSignature(
                        loaded.advancedFields.CustomTags.Select(kv => (kv.Key, kv.Value)));
            }

            var (synced, plain) = ResolveLyrics(trackSynced, loaded.lrc.Text, trackPlain, loaded.txt.Text);
            ApplyLyrics(synced, plain, loaded.lrc.Unreadable, loaded.txt.Unreadable);

            // A cover the user picked or removed while this was in flight wins.
            if (_newAnimatedCoverSource == null && !_animatedCoverRemoved)
            {
                AnimatedCoverPath = loaded.animated;
                HasAnimatedCover = !string.IsNullOrEmpty(AnimatedCoverPath);
            }

            // A pattern typed meanwhile already rebuilt the preview for itself.
            if (loaded.renamePreviews != null && RenamePattern == renamePattern)
            {
                RenamePreviews.Clear();
                foreach (var p in loaded.renamePreviews) RenamePreviews.Add(p);
            }
        }
        finally
        {
            _editedWhileLoading = null;
            // Slower than the tag read, or the tag read failed: the cover still lands.
            // LoadCachedCover never throws.
            if (!coverApplied) ApplyLoadedCover(await cover);
            // The loaded state is the baseline; a field the user typed into before it landed
            // keeps its pre-load baseline, so the edit still counts in the footer.
            CaptureChangeBaseline(keep: edited);
            if (loadedCustomTagsBaseline != null)
            {
                _customTagsBaseline = loadedCustomTagsBaseline;
                RecomputeChanges();
            }
            IsLoading = false;
        }
    }

    /// <summary>
    /// Reads a lyric sidecar beside the audio file, only when the Track field it backs is
    /// empty (<paramref name="needed"/>). <c>Unreadable</c> reports a read that THREW, which
    /// Save treats as "never delete this sidecar".
    /// </summary>
    private static (string? Text, bool Unreadable) ReadLyricSidecar(string audioPath, string extension, bool needed)
    {
        if (!needed) return (null, false);
        try
        {
            var path = Path.ChangeExtension(audioPath, extension);
            return (File.Exists(path) ? File.ReadAllText(path) : null, false);
        }
        catch
        {
            return (null, true);
        }
    }

    // ── Cover at open (owner 10-08: cover shows late in the metadata window) ────────
    // The window opens at once with the library's own decoded thumbnail of this cover when
    // there is one (no I/O); LoadCachedCover then decodes the cached cover file at 512 px in
    // its own task and replaces it, without waiting on the audio file's tag reads.

    /// <summary>What <see cref="LoadCachedCover"/> read: the 512-wide preview, the cover's real
    /// size, and whether it is this track's own cover rather than its album's.</summary>
    private readonly record struct LoadedCover(Bitmap? Bitmap, Avalonia.PixelSize? Size, bool OwnArtwork);

    /// <summary>The library's thumbnail the preview is showing, borrowed from
    /// <see cref="ArtworkCache"/>: the grid draws the same bitmap, so this editor releases it
    /// (<see cref="DisposePreview"/>) and never disposes it.</summary>
    private Bitmap? _sharedPreview;
    private bool _sharedPreviewHeld;

    private readonly TaskCompletionSource _coverShown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the editor shows the cover it opens with: at construction when the
    /// library's thumbnail was seeded (or for multi-select, which shows none), otherwise when
    /// <see cref="LoadCachedCover"/> has landed (a cover, or none). The window's
    /// <c>PillDialogHost</c> holds its fade-in on this (owner 10-08: with no thumbnail to seed
    /// from, the card faded in over an empty cover well, then the 85–145 ms decode popped in).
    /// </summary>
    internal Task CoverShown => _coverShown.Task;

    /// <summary>Reads and decodes the cached cover file. Runs on a pool thread; never throws.</summary>
    private LoadedCover LoadCachedCover()
    {
        // Multi-select can span albums; don't show one album's art as if shared.
        if (_multiSelect) return default;
        try
        {
            var artPath = DisplayedArtworkPath();

            // The persisted cache file is the source of truth for "does this album
            // have a cover in Noctis." If the user removed it, cachedData is null
            // on purpose. We must not silently fall back to extracting from the
            // audio file's embedded tag here — every track in the album still has
            // its own embedded copy, and WriteAlbumArt() during Remove can fail
            // silently for any one of them (file locked, AV, etc.), causing the
            // removed cover to come right back. Library scans/imports handle
            // initial extraction-to-cache; the dialog must not re-do that work.
            byte[]? cachedData = null;
            if (File.Exists(artPath))
            {
                try { cachedData = File.ReadAllBytes(artPath); } catch { }
            }
            if (cachedData == null || cachedData.Length == 0) return default;

            // Decode at display size — the preview renders at ~240px; a
            // full-res `new Bitmap` of a 3000x3000 cover costs ~36 MB.
            using var ms = new MemoryStream(cachedData);
            var bitmap = Bitmap.DecodeToWidth(ms, 512);
            // The chip reports the real cover, not this 512-wide preview.
            var size = SkiaArtworkDecoder.ReadPixelSize(cachedData);
            // DisplayedArtworkPath picks the track's own cover only when it exists.
            var own = !_albumScoped && artPath == _persistence.GetTrackArtworkPath(_track.Id);
            return new LoadedCover(bitmap, size, own);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>Shows the cover <see cref="LoadCachedCover"/> read. A cover the user picked or
    /// removed while it loaded wins; no cover file means no cover, even over a seeded thumbnail.</summary>
    private void ApplyLoadedCover(LoadedCover cover)
    {
        try
        {
            if (_newArtworkData != null || _artworkRemoved)
            {
                cover.Bitmap?.Dispose();
                return;
            }
            var old = ArtworkPreview;
            ShowsOwnTrackArtwork = cover.Bitmap != null && cover.OwnArtwork;
            _artworkSourceSize = cover.Size;
            ArtworkPreview = cover.Bitmap;
            HasArtwork = cover.Bitmap != null;
            if (!ReferenceEquals(old, cover.Bitmap)) DisposePreview(old);
        }
        finally
        {
            _coverShown.TrySetResult();
        }
    }

    /// <summary>
    /// Shows the library's already-decoded thumbnail of this cover, if <see cref="ArtworkCache"/>
    /// holds one, so the window opens with its cover instead of a placeholder. Memory only:
    /// <see cref="Track.AlbumArtworkPath"/> is the path the library views draw for this track
    /// (its own cover, or its album's); the album editor shows the album's. Not a change:
    /// the change count reads the staged artwork, not the preview.
    /// </summary>
    private void SeedArtworkFromLibrary()
    {
        if (_multiSelect) return;
        var path = _albumScoped ? _persistence.GetArtworkPath(_track.AlbumId) : _track.AlbumArtworkPath;
        if (string.IsNullOrEmpty(path)) return;
        // TryGetAnyWidth skips the requested bucket itself, so ask for that first.
        var bmp = ArtworkCache.TryGet(path, 512) ?? ArtworkCache.TryGetAnyWidth(path, 512);
        if (bmp == null) return;
        // Held while shown, so an eviction from the cache can't dispose it under the window.
        ArtworkCache.Acquire(bmp);
        _sharedPreview = bmp;
        _sharedPreviewHeld = true;
        ShowsOwnTrackArtwork = !_albumScoped && path == _persistence.GetTrackArtworkPath(_track.Id);
        ArtworkPreview = bmp;
        HasArtwork = true;
    }

    /// <summary>Frees a preview this editor no longer shows: the borrowed library thumbnail
    /// goes back to the cache (which disposes it once nothing else draws it), anything else
    /// this editor decoded is disposed.</summary>
    private void DisposePreview(Bitmap? bitmap)
    {
        if (bitmap == null) return;
        if (ReferenceEquals(bitmap, _sharedPreview))
        {
            ReleaseSharedPreview();
            return;
        }
        bitmap.Dispose();
    }

    private void ReleaseSharedPreview()
    {
        if (!_sharedPreviewHeld) return;
        _sharedPreviewHeld = false;
        ArtworkCache.Release(_sharedPreview);
    }

    /// <summary>The cover's real size: the decoded source's, or the bitmap's own when it was
    /// decoded full size. Unknown (null) while the borrowed thumbnail stands in — its size is
    /// a grid decode width, not the cover's.</summary>
    private Avalonia.PixelSize? ShownArtworkSize => ArtworkPreview is { } bmp
        ? _artworkSourceSize ?? (ReferenceEquals(bmp, _sharedPreview) ? null : bmp.PixelSize)
        : null;

    // ── Change tracking (rail dots + footer summary) ─────────────────────────
    // A snapshot of every editable field is taken once the dialog is fully loaded;
    // each later edit is compared against it, so the rail can mark the sections that
    // carry unsaved edits and the footer can say how many fields Save will write.
    // Reverting a field by hand takes it back out of the count.

    private static readonly (string Name, MetadataSection Section)[] TrackedFields =
    {
        (nameof(Title), MetadataSection.Details), (nameof(Artist), MetadataSection.Details),
        (nameof(AlbumArtist), MetadataSection.Details), (nameof(Performer), MetadataSection.Details),
        (nameof(Album), MetadataSection.Details), (nameof(Genre), MetadataSection.Details),
        (nameof(Composer), MetadataSection.Details), (nameof(TrackNumber), MetadataSection.Details),
        (nameof(TrackCount), MetadataSection.Details), (nameof(DiscNumber), MetadataSection.Details),
        (nameof(DiscCount), MetadataSection.Details), (nameof(Bpm), MetadataSection.Details),
        (nameof(Year), MetadataSection.Details), (nameof(IsCompilation), MetadataSection.Details),
        (nameof(IsExplicit), MetadataSection.Details), (nameof(ShowComposerInAllViews), MetadataSection.Details),
        (nameof(Grouping), MetadataSection.Details), (nameof(UseWorkAndMovement), MetadataSection.Details),
        (nameof(WorkName), MetadataSection.Details), (nameof(MovementName), MetadataSection.Details),
        (nameof(MovementNumber), MetadataSection.Details), (nameof(MovementCount), MetadataSection.Details),
        (nameof(Comment), MetadataSection.Details), (nameof(Rating), MetadataSection.Details),
        (nameof(IsDisliked), MetadataSection.Details), (nameof(ApplyRename), MetadataSection.Details),
        (nameof(RenamePattern), MetadataSection.Details),

        (nameof(TitleSort), MetadataSection.Advanced), (nameof(ArtistSort), MetadataSection.Advanced),
        (nameof(AlbumSort), MetadataSection.Advanced), (nameof(AlbumArtistSort), MetadataSection.Advanced),
        (nameof(ComposerSort), MetadataSection.Advanced), (nameof(Conductor), MetadataSection.Advanced),
        (nameof(Lyricist), MetadataSection.Advanced), (nameof(Publisher), MetadataSection.Advanced),
        (nameof(EncodedBy), MetadataSection.Advanced), (nameof(Isrc), MetadataSection.Advanced),
        (nameof(CatalogNumber), MetadataSection.Advanced), (nameof(Barcode), MetadataSection.Advanced),
        (nameof(SelectedAdvisory), MetadataSection.Advanced), (nameof(Language), MetadataSection.Advanced),
        (nameof(Mood), MetadataSection.Advanced), (nameof(AdvDescription), MetadataSection.Advanced),
        (nameof(AdvReleaseDate), MetadataSection.Advanced), (nameof(Copyright), MetadataSection.Advanced),

        (nameof(Lyrics), MetadataSection.PlainLyrics), (nameof(HasCustomLyrics), MetadataSection.PlainLyrics),
        (nameof(SyncedLyrics), MetadataSection.SyncedLyrics), (nameof(HasCustomSyncedLyrics), MetadataSection.SyncedLyrics),

        (nameof(SkipWhenShuffling), MetadataSection.Options), (nameof(RememberPlaybackPosition), MetadataSection.Options),
        (nameof(MediaKind), MetadataSection.Options), (nameof(ReleaseTypeOverride), MetadataSection.Options),
        (nameof(HasStartTime), MetadataSection.Options), (nameof(StartTime), MetadataSection.Options),
        (nameof(HasStopTime), MetadataSection.Options), (nameof(StopTime), MetadataSection.Options),
        (nameof(VolumeAdjust), MetadataSection.Options), (nameof(SelectedEqPreset), MetadataSection.Options),
    };

    // Properties that carry no value of their own but signal that staged state
    // (artwork bytes, animated cover source, play-count reset) moved.
    private static readonly HashSet<string> ChangeTriggers = new(
        TrackedFields.Select(f => f.Name).Concat(new[]
        {
            nameof(ArtworkPreview), nameof(HasArtwork),
            nameof(AnimatedCoverPath), nameof(HasAnimatedCover),
            nameof(PlayCountDisplay),
        }));

    private readonly Dictionary<string, object?> _changeBaseline = new();
    private string _customTagsBaseline = string.Empty;
    private bool _changeBaselineCaptured;
    private bool _customTagsHooked;

    public int ChangeCount { get; private set; }
    public bool HasChanges => ChangeCount > 0;
    public bool DetailsChanged { get; private set; }
    public bool AdvancedChanged { get; private set; }
    public bool ArtworkChanged { get; private set; }
    public bool AnimatedArtworkChanged { get; private set; }
    public bool PlainLyricsChanged { get; private set; }
    public bool SyncedLyricsChanged { get; private set; }
    public bool OptionsChanged { get; private set; }

    /// <summary>Footer line: "No changes yet" / "3 changes · applies to 22 files".</summary>
    public string ChangeSummary
    {
        get
        {
            if (ChangeCount == 0) return Localization.Loc.T("Metadata.NoChangesYet");
            var count = ChangeCount == 1
                ? Localization.Loc.T("Metadata.ChangesOne")
                : Localization.Loc.T("Metadata.ChangesMany", ChangeCount);
            var files = _albumTracks?.Count ?? 1;
            return files > 1 ? $"{count} · {Localization.Loc.T("Metadata.AppliesToFiles", files)}" : count;
        }
    }

    /// <param name="keep">Fields whose existing baseline stays (edited before the load landed).</param>
    private void CaptureChangeBaseline(IReadOnlySet<string>? keep = null)
    {
        foreach (var (name, _) in TrackedFields)
            if (keep == null || !keep.Contains(name) || !_changeBaseline.ContainsKey(name))
                _changeBaseline[name] = ReadTracked(name);
        _customTagsBaseline = CustomTagsSignature();
        if (!_customTagsHooked)
        {
            _customTagsHooked = true;
            CustomTags.CollectionChanged += OnCustomTagsChanged;
        }
        foreach (var tag in CustomTags)
        {
            tag.PropertyChanged -= OnCustomTagEdited;
            tag.PropertyChanged += OnCustomTagEdited;
        }
        _changeBaselineCaptured = true;
        RecomputeChanges();
    }

    private object? ReadTracked(string name) => GetType().GetProperty(name)!.GetValue(this);

    private string CustomTagsSignature() => CustomTagsSignature(CustomTags.Select(t => (t.Key, t.Value)));

    private static string CustomTagsSignature(IEnumerable<(string Key, string Value)> tags) =>
        string.Join("", tags.Select(t => t.Key + "" + t.Value));

    private void OnCustomTagsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (CustomTagItem tag in e.NewItems) tag.PropertyChanged += OnCustomTagEdited;
        if (e.OldItems != null)
            foreach (CustomTagItem tag in e.OldItems) tag.PropertyChanged -= OnCustomTagEdited;
        RecomputeChanges();
    }

    private void OnCustomTagEdited(object? sender, PropertyChangedEventArgs e) => RecomputeChanges();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_changeBaselineCaptured && e.PropertyName is { } name && ChangeTriggers.Contains(name))
            RecomputeChanges();
    }

    private void RecomputeChanges()
    {
        if (!_changeBaselineCaptured) return;

        var perSection = new int[Enum.GetValues<MetadataSection>().Length];
        foreach (var (name, section) in TrackedFields)
        {
            if (!Equals(ReadTracked(name), _changeBaseline.GetValueOrDefault(name)))
                perSection[(int)section]++;
        }
        if (_resetPlayCountPending) perSection[(int)MetadataSection.Details]++;
        // Per-track values staged by a Find online apply (album editor): one change per track.
        perSection[(int)MetadataSection.Details] += _stagedTrackChanges.Count;
        if (CustomTagsSignature() != _customTagsBaseline) perSection[(int)MetadataSection.Advanced]++;
        if (_newArtworkData != null || _artworkRemoved) perSection[(int)MetadataSection.Artwork]++;
        if (_newAnimatedCoverSource != null || _animatedCoverRemoved) perSection[(int)MetadataSection.AnimatedArtwork]++;

        DetailsChanged = Flag(DetailsChanged, perSection[(int)MetadataSection.Details], nameof(DetailsChanged), out var raiseDetails);
        AdvancedChanged = Flag(AdvancedChanged, perSection[(int)MetadataSection.Advanced], nameof(AdvancedChanged), out var raiseAdvanced);
        ArtworkChanged = Flag(ArtworkChanged, perSection[(int)MetadataSection.Artwork], nameof(ArtworkChanged), out var raiseArtwork);
        AnimatedArtworkChanged = Flag(AnimatedArtworkChanged, perSection[(int)MetadataSection.AnimatedArtwork], nameof(AnimatedArtworkChanged), out var raiseAnimated);
        PlainLyricsChanged = Flag(PlainLyricsChanged, perSection[(int)MetadataSection.PlainLyrics], nameof(PlainLyricsChanged), out var raisePlain);
        SyncedLyricsChanged = Flag(SyncedLyricsChanged, perSection[(int)MetadataSection.SyncedLyrics], nameof(SyncedLyricsChanged), out var raiseSynced);
        OptionsChanged = Flag(OptionsChanged, perSection[(int)MetadataSection.Options], nameof(OptionsChanged), out var raiseOptions);
        // Raise only after every flag holds its new value: bindings read the property
        // inside the PropertyChanged handler, so raising first would hand them the stale one.
        foreach (var name in new[] { raiseDetails, raiseAdvanced, raiseArtwork, raiseAnimated, raisePlain, raiseSynced, raiseOptions })
            if (name != null) OnPropertyChanged(name);

        var total = perSection.Sum();
        if (ChangeCount != total)
        {
            ChangeCount = total;
            OnPropertyChanged(nameof(ChangeCount));
            OnPropertyChanged(nameof(HasChanges));
            OnPropertyChanged(nameof(ChangeSummary));
        }
    }

    /// <summary>Returns the new flag value; <paramref name="raise"/> names the property when it flipped.</summary>
    private static bool Flag(bool current, int changedFields, string propertyName, out string? raise)
    {
        var next = changedFields > 0;
        raise = next != current ? propertyName : null;
        return next;
    }

    // What each track's tags looked like when the dialog opened, so Save can tell whether a
    // file actually needs rewriting. Save used to call WriteTrackMetadata on every album
    // track unconditionally: saving an animated cover — a separate sidecar that never touches
    // the audio tags — rewrote every FLAC on the album, and the one the player held open
    // failed, reporting "Couldn't write tags" for an edit that needed no tag write at all.
    private readonly Dictionary<Track, TagState> _loadedTagSignatures = new();

    private void CaptureLoadedTagSignatures()
    {
        _loadedTagSignatures[_track] = ComputeTagState(_track);
        foreach (var t in _albumTracks ?? Enumerable.Empty<Track>())
            _loadedTagSignatures[t] = ComputeTagState(t);
    }

    /// <summary>
    /// True when <paramref name="track"/>'s tag-bearing fields differ from what they were when
    /// the dialog opened. A track with no snapshot is treated as changed, so an unexpected
    /// path errs towards writing rather than silently dropping an edit.
    /// </summary>
    private bool NeedsTagWrite(Track track)
        => !_loadedTagSignatures.TryGetValue(track, out var loaded) ||
           loaded != ComputeTagState(track);

    /// <summary>
    /// True when the user emptied a year the track had when the dialog opened. Only then is
    /// the file's date removed: WriteTrackMetadata reads year 0 as unknown and keeps it.
    /// </summary>
    private bool YearWasCleared(Track track)
        => track.Year == 0 && _loadedTagSignatures.TryGetValue(track, out var loaded) && loaded.Year > 0;

    /// <summary>
    /// Every field <see cref="IMetadataService.WriteTrackMetadata"/> puts in the file, and only
    /// those. Journal-only state (play count, skip-when-shuffling, start/stop, volume, EQ) is
    /// deliberately absent: changing it must not trigger a file rewrite. Rating and disliked
    /// ARE here, because they are written into the tag.
    ///
    /// A record rather than a joined string: structural equality needs no separator, so no
    /// field value can collide with one.
    /// </summary>
    private sealed record TagState(
        string? Title, string? Artist, string? AlbumArtist, string? Album, string? Genre,
        string? Composer, int TrackNumber, int TrackCount, int DiscNumber, int DiscCount,
        int Bpm, int Year, string? Lyrics, string? Comment, string? Grouping, string? Copyright,
        bool IsCompilation, bool ShowComposerInAllViews, bool UseWorkAndMovement,
        string? WorkName, string? MovementName, int MovementNumber, int MovementCount,
        int Rating, bool IsDisliked, string ReleaseTypeOverride);

    private static TagState ComputeTagState(Track t) => new(
        t.Title, t.Artist, t.AlbumArtist, t.Album, t.Genre, t.Composer,
        t.TrackNumber, t.TrackCount, t.DiscNumber, t.DiscCount, t.Bpm, t.Year,
        t.Lyrics, t.Comment, t.Grouping, t.Copyright,
        t.IsCompilation, t.ShowComposerInAllViews, t.UseWorkAndMovement,
        t.WorkName, t.MovementName, t.MovementNumber, t.MovementCount,
        t.Rating, t.IsDisliked,
        t.IsReleaseTypeOverridden ? t.ReleaseType.ToString() : string.Empty);

    // ── Search metadata ── lives in MetadataViewModel.Search.cs (owner 10-08: Search
    // metadata revamp): the Find online panel, its field mapping, apply and undo.

    // Opens the Spotify-style share card for the current lyrics (plain, else synced
    // with timestamps stripped). Reuses the existing LyricShareDialog.
    [RelayCommand]
    private async Task ShareLyrics()
    {
        var source = !string.IsNullOrWhiteSpace(Lyrics)
            ? Lyrics
            : LyricsTextHelper.StripTimestamps(SyncedLyrics ?? string.Empty);
        var lines = (source ?? string.Empty)
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0) return;

        var vm = new LyricShareViewModel(_track, lines, 0);
        await Noctis.Views.LyricShareDialog.ShowAsync(vm);
    }

    // ── Album-scoped (whole-album) editing ──────────────────────────────────
    // Replaces the single-track values loaded from Tracks[0] with album-wide
    // values: a shared value is shown as-is; a field that differs across tracks
    // is shown blank with a "Mixed" placeholder.
    private void LoadAlbumScopedOverrides()
    {
        Artist = CommonStrOrMixed(nameof(Artist), t => t.Artist);
        Composer = CommonStrOrMixed(nameof(Composer), t => t.Composer);
        Grouping = CommonStrOrMixed(nameof(Grouping), t => t.Grouping);
        Comment = CommonStrOrMixed(nameof(Comment), t => t.Comment);
        Year = CommonIntOrMixed(nameof(Year), t => t.Year);
        TrackNumber = CommonIntOrMixed(nameof(TrackNumber), t => t.TrackNumber);
        TrackCount = CommonIntOrMixed(nameof(TrackCount), t => t.TrackCount);
        DiscNumber = CommonIntOrMixed(nameof(DiscNumber), t => t.DiscNumber);
        DiscCount = CommonIntOrMixed(nameof(DiscCount), t => t.DiscCount);
        Bpm = CommonIntOrMixed(nameof(Bpm), t => t.Bpm);

        // Work & Movement (classical): treat like the other shared fields —
        // a value common to every track shows as-is, otherwise blank/"Mixed".
        UseWorkAndMovement = _albumTracks!.All(t => t.UseWorkAndMovement);
        WorkName = CommonStrOrMixed(nameof(WorkName), t => t.WorkName);
        MovementName = CommonStrOrMixed(nameof(MovementName), t => t.MovementName);
        MovementNumber = CommonIntOrMixed(nameof(MovementNumber), t => t.MovementNumber);
        MovementCount = CommonIntOrMixed(nameof(MovementCount), t => t.MovementCount);

        var genre0 = _track.Genre ?? string.Empty;
        if (_albumTracks!.All(t => (t.Genre ?? string.Empty) == genre0))
            Genre = genre0;
        else { _mixedFields.Add(nameof(Genre)); Genre = string.Empty; }

        IsCompilation = _albumTracks!.All(t => t.IsCompilation);
        IsExplicit = _albumTracks!.All(t => t.IsExplicit);
        ShowComposerInAllViews = _albumTracks!.All(t => t.ShowComposerInAllViews);

        // Rating is album-wide here: show the shared value when every track agrees,
        // otherwise show none. "Not Liked" is checked only when the whole album is disliked.
        Rating = _albumTracks!.All(t => t.Rating == _track.Rating) ? _track.Rating : 0;
        IsDisliked = _albumTracks!.All(t => t.IsDisliked);

        // Album / Album Artist are shared within a single album, so they stay as-is in
        // album-scoped mode. A multi-select can span albums, so show Mixed when they differ.
        if (_multiSelect)
        {
            Album = CommonStrOrMixed(nameof(Album), t => t.Album);
            AlbumArtist = CommonStrOrMixed(nameof(AlbumArtist), t => t.AlbumArtist);
        }

        SnapshotAlbumOriginals();
    }

    private string CommonStrOrMixed(string key, Func<Track, string?> sel)
    {
        var first = sel(_track) ?? string.Empty;
        if (_albumTracks!.All(t => (sel(t) ?? string.Empty) == first)) return first;
        _mixedFields.Add(key);
        return string.Empty;
    }

    private string CommonIntOrMixed(string key, Func<Track, int> sel)
    {
        var first = sel(_track);
        if (_albumTracks!.All(t => sel(t) == first)) return first > 0 ? first.ToString() : string.Empty;
        _mixedFields.Add(key);
        return string.Empty;
    }

    private void SnapshotAlbumOriginals()
    {
        _albumLoaded[nameof(Artist)] = Artist;
        _albumLoaded[nameof(Album)] = Album;
        _albumLoaded[nameof(AlbumArtist)] = AlbumArtist;
        _albumLoaded[nameof(Composer)] = Composer;
        _albumLoaded[nameof(Grouping)] = Grouping;
        _albumLoaded[nameof(Genre)] = Genre;
        _albumLoaded[nameof(Year)] = Year;
        _albumLoaded[nameof(TrackNumber)] = TrackNumber;
        _albumLoaded[nameof(TrackCount)] = TrackCount;
        _albumLoaded[nameof(DiscNumber)] = DiscNumber;
        _albumLoaded[nameof(DiscCount)] = DiscCount;
        _albumLoaded[nameof(Bpm)] = Bpm;
        _albumLoaded[nameof(Comment)] = Comment;
        _albumLoaded[nameof(WorkName)] = WorkName;
        _albumLoaded[nameof(MovementName)] = MovementName;
        _albumLoaded[nameof(MovementNumber)] = MovementNumber;
        _albumLoaded[nameof(MovementCount)] = MovementCount;
        _loadedIsCompilation = IsCompilation;
        _loadedIsExplicit = IsExplicit;
        _loadedShowComposerInAllViews = ShowComposerInAllViews;
        _loadedRating = Rating;
        _loadedIsDisliked = IsDisliked;
        _loadedUseWorkAndMovement = UseWorkAndMovement;

        // Options (loaded from the first track) — snapshot for change detection.
        _loadedSkipWhenShuffling = SkipWhenShuffling;
        _loadedRememberPlaybackPosition = RememberPlaybackPosition;
        _loadedMediaKind = MediaKind;
        _loadedReleaseTypeOverride = ReleaseTypeOverride;
        _loadedStartTimeMs = HasStartTime ? ParseTimeToMs(StartTime) : 0;
        _loadedStopTimeMs = HasStopTime ? ParseTimeToMs(StopTime) : 0;
        _loadedVolumeAdjust = VolumeAdjust;
        _loadedEqPreset = SelectedEqPreset == "None" ? string.Empty : SelectedEqPreset;
    }

    private bool AlbumFieldChanged(string key, string current)
        => current != _albumLoaded.GetValueOrDefault(key, string.Empty);

    private string Watermark(string key) => _mixedFields.Contains(key) ? "Mixed" : string.Empty;

    /// <summary>Details tool-row hint for album / multi-track edits: why some fields read "Mixed".</summary>
    public bool ShowMixedHint => _albumTracks is { Count: > 1 };

    /// <summary>"3000 × 3000" chip on the Artwork tab; empty while there is no cover.</summary>
    public string ArtworkDimensions => ShownArtworkSize is { } s
        ? $"{s.Width} × {s.Height}"
        : string.Empty;

    /// <summary>The cover's real size when the preview was decoded smaller (the saved
    /// cover loads as a 512-wide preview); null when the preview is full size.</summary>
    private Avalonia.PixelSize? _artworkSourceSize;

    partial void OnArtworkPreviewChanged(Bitmap? value) => OnPropertyChanged(nameof(ArtworkDimensions));

    /// <summary>The preview is this track's OWN embedded cover (it differs from the rest of
    /// its album). Cleared once the user picks or removes a cover, which applies album-wide.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSharedByAlbumHint))]
    private bool _showsOwnTrackArtwork;

    public bool ShowSharedByAlbumHint => !_multiSelect && !ShowsOwnTrackArtwork;

    [RelayCommand]
    private void ShowInFolder() => PlatformHelper.ShowInFileManager(_track.FilePath);

    [RelayCommand]
    private async Task CopyPath(Avalonia.Controls.Window? window)
    {
        var clipboard = window?.Clipboard ?? Avalonia.Controls.TopLevel.GetTopLevel(window)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(_track.FilePath);
    }
    public string MixedHint => ShowMixedHint ? Localization.Loc.T("Metadata.MixedHint", _albumTracks!.Count) : string.Empty;

    // ── Rename-by-pattern (multi-select) ──
    partial void OnRenamePatternChanged(string value) => RebuildRenamePreview();

    private void RebuildRenamePreview()
    {
        RenamePreviews.Clear();
        foreach (var preview in BuildRenamePreviews(RenamePattern))
            RenamePreviews.Add(preview);
    }

    /// <summary>
    /// The first rows of the rename preview. Touches the disk (a File.Exists per row for
    /// the conflict flag), so the initial build runs in InitializeAsync's background load.
    /// </summary>
    private List<RenamePreview> BuildRenamePreviews(string pattern)
    {
        var previews = new List<RenamePreview>();
        if (!_multiSelect || _albumTracks == null) return previews;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _albumTracks.Take(8))
        {
            var newPath = ComputeRenamedPath(t, out var conflict, seen, pattern);
            previews.Add(new RenamePreview
            {
                OriginalName = Path.GetFileName(t.FilePath),
                NewName = newPath != null ? Path.GetFileName(newPath) : "(empty pattern)",
                Conflict = conflict,
            });
        }
        return previews;
    }

    private string? ComputeRenamedPath(Track t, out bool conflict, HashSet<string>? seenInBatch = null,
        string? pattern = null)
    {
        conflict = false;
        var expanded = TitleFormatter.Expand(pattern ?? RenamePattern, t, sanitizeForFilename: true);
        if (string.IsNullOrWhiteSpace(expanded)) return null;

        var dir = Path.GetDirectoryName(t.FilePath) ?? string.Empty;
        var ext = Path.GetExtension(t.FilePath);
        var newPath = Path.Combine(dir, expanded + ext);

        if (string.Equals(newPath, t.FilePath, StringComparison.OrdinalIgnoreCase)) return newPath;
        if (File.Exists(newPath)) conflict = true;
        if (seenInBatch != null && !seenInBatch.Add(newPath.ToLowerInvariant())) conflict = true;
        return newPath;
    }

    /// <summary>
    /// Applies the album-scoped Details edits: only fields the user changed are
    /// fanned out to every track of the album, so untouched ("Mixed") and
    /// per-track fields (titles, track numbers, …) keep their own values.
    /// </summary>
    private void ApplyAlbumScopedDetails()
    {
        if (_albumTracks == null || _albumTracks.Count == 0) return;

        bool artistChg = AlbumFieldChanged(nameof(Artist), Artist);
        bool albumChg = AlbumFieldChanged(nameof(Album), Album);
        bool albumArtistChg = AlbumFieldChanged(nameof(AlbumArtist), AlbumArtist);
        bool composerChg = AlbumFieldChanged(nameof(Composer), Composer);
        bool groupingChg = AlbumFieldChanged(nameof(Grouping), Grouping);
        // A typed genre is saved trimmed (GitHub #123, 2026-10-10).
        var genre = (Genre ?? string.Empty).Trim();
        bool genreChg = AlbumFieldChanged(nameof(Genre), genre);
        bool yearChg = AlbumFieldChanged(nameof(Year), Year);
        bool trackNumberChg = AlbumFieldChanged(nameof(TrackNumber), TrackNumber);
        bool trackCountChg = AlbumFieldChanged(nameof(TrackCount), TrackCount);
        bool discNumberChg = AlbumFieldChanged(nameof(DiscNumber), DiscNumber);
        bool discCountChg = AlbumFieldChanged(nameof(DiscCount), DiscCount);
        bool bpmChg = AlbumFieldChanged(nameof(Bpm), Bpm);
        bool commentChg = AlbumFieldChanged(nameof(Comment), Comment);
        bool workNameChg = AlbumFieldChanged(nameof(WorkName), WorkName);
        bool movementNameChg = AlbumFieldChanged(nameof(MovementName), MovementName);
        bool movementNumberChg = AlbumFieldChanged(nameof(MovementNumber), MovementNumber);
        bool movementCountChg = AlbumFieldChanged(nameof(MovementCount), MovementCount);
        bool useWorkChg = UseWorkAndMovement != _loadedUseWorkAndMovement;
        bool compChg = IsCompilation != _loadedIsCompilation;
        bool showComposerChg = ShowComposerInAllViews != _loadedShowComposerInAllViews;
        bool ratingChg = Rating != _loadedRating;
        bool dislikedChg = IsDisliked != _loadedIsDisliked;
        int ratingVal = Math.Clamp(Rating, 0, 5);

        int yearVal = int.TryParse(Year, out var yr) ? yr : 0;
        int trackNumberVal = int.TryParse(TrackNumber, out var tn) ? tn : 0;
        int trackCountVal = int.TryParse(TrackCount, out var tc) ? tc : 0;
        int discNumberVal = int.TryParse(DiscNumber, out var dn) ? Math.Max(1, dn) : 1;
        int discCountVal = int.TryParse(DiscCount, out var dc) ? Math.Max(1, dc) : 1;
        int bpmVal = int.TryParse(Bpm, out var bp) ? Math.Max(0, bp) : 0;
        int movementNumberVal = int.TryParse(MovementNumber, out var mvn) ? Math.Max(0, mvn) : 0;
        int movementCountVal = int.TryParse(MovementCount, out var mvc) ? Math.Max(0, mvc) : 0;

        foreach (var t in _albumTracks)
        {
            if (artistChg) t.Artist = Artist;
            if (albumArtistChg) t.AlbumArtist = AlbumArtist;
            if (albumChg) t.Album = Album;
            if (composerChg) t.Composer = Composer;
            if (groupingChg) t.Grouping = Grouping;
            if (genreChg) t.Genre = genre;
            if (yearChg) t.Year = yearVal;
            if (trackNumberChg) t.TrackNumber = trackNumberVal;
            if (trackCountChg) t.TrackCount = trackCountVal;
            if (discNumberChg) t.DiscNumber = discNumberVal;
            if (discCountChg) t.DiscCount = discCountVal;
            if (bpmChg) t.Bpm = bpmVal;
            if (commentChg) t.Comment = Comment;
            if (useWorkChg) t.UseWorkAndMovement = UseWorkAndMovement;
            if (workNameChg) t.WorkName = WorkName;
            if (movementNameChg) t.MovementName = MovementName;
            if (movementNumberChg) t.MovementNumber = movementNumberVal;
            if (movementCountChg) t.MovementCount = movementCountVal;
            if (compChg) t.IsCompilation = IsCompilation;
            if (showComposerChg) t.ShowComposerInAllViews = ShowComposerInAllViews;
            if (ratingChg) t.Rating = ratingVal;
            if (dislikedChg) t.IsDisliked = IsDisliked;
            if (albumChg || albumArtistChg) t.AlbumId = Track.ComputeAlbumId(t.AlbumArtist, t.Album);
        }
    }

    private readonly ITunesArtworkService? _itunes;
    private readonly ILrcLibService? _lrcLib;
    [ObservableProperty] private bool _isSearchingSyncedLyrics;
    [ObservableProperty] private string _syncedLyricsSearchStatus = string.Empty;

    /// <summary>
    /// Picks what the Plain and Timestamp tabs show from the Track fields and the sidecar
    /// texts (null = not read / absent). A sidecar only fills a Track field that is empty.
    /// Plain must NEVER contain timestamps; if a legacy track stored synced text in Lyrics,
    /// it moves to synced and plain is derived from it.
    /// </summary>
    private static (string Synced, string Plain) ResolveLyrics(string? trackSynced, string? lrcText, string? trackPlain, string? txtText)
    {
        var syncedFromTrack = string.IsNullOrWhiteSpace(trackSynced) && lrcText != null ? lrcText : trackSynced;
        var plainFromTrack = string.IsNullOrWhiteSpace(trackPlain) && txtText != null ? txtText : trackPlain;

        // If the plain field accidentally holds timestamped text, treat it as synced (legacy migration)
        // and derive plain from it.
        if (LyricsTextHelper.ContainsTimestamps(plainFromTrack))
        {
            if (string.IsNullOrWhiteSpace(syncedFromTrack))
                syncedFromTrack = plainFromTrack;
            plainFromTrack = LyricsTextHelper.StripTimestamps(plainFromTrack);
        }

        // Synced lyrics must actually contain timestamps. Some legacy/sidecar data
        // can put plain text into SyncedLyrics or .lrc; keep that out of the synced tab.
        if (!LyricsTextHelper.ContainsTimestamps(syncedFromTrack))
        {
            if (string.IsNullOrWhiteSpace(plainFromTrack))
                plainFromTrack = syncedFromTrack;
            syncedFromTrack = string.Empty;
        }

        return (syncedFromTrack ?? string.Empty, plainFromTrack ?? string.Empty);
    }

    /// <summary>
    /// Sets the lyric fields and what Save compares them against. A sidecar we failed to
    /// READ must never be deleted on save. Previously any transient failure (file locked,
    /// AV scan, permission blip, network share hiccup) left the field empty, and the save
    /// path read that emptiness as "the user cleared the lyrics" and hard-deleted a
    /// hand-timed .lrc the app cannot regenerate — triggered by something as innocuous as
    /// fixing a typo in the Year.
    /// Called again when InitializeAsync lands: a lyric field the user already edited while
    /// the load was in flight (KeepsEdit) stays as typed, while the loaded text still becomes
    /// the "loaded" reference, so Save sees the edit as a change against what is on disk.
    /// Only the edited field is kept: switching Custom Lyrics on before the .txt arrived
    /// must still show (and on Save keep) that .txt, not save an empty tab that trashes it.
    /// </summary>
    private void ApplyLyrics(string synced, string plain, bool syncedUnreadable, bool plainUnreadable)
    {
        _syncedSidecarUnreadable = syncedUnreadable;
        _plainSidecarUnreadable = plainUnreadable;

        var keepSynced = KeepsEdit(nameof(SyncedLyrics));
        if (!keepSynced)
        {
            SyncedLyrics = synced;
            // "Remove" on a synced tab that was still empty removed nothing; the loaded .lrc
            // is not the user's to have removed.
            _syncedLyricsRemovedByUser = false;
        }
        if (!KeepsEdit(nameof(Lyrics))) Lyrics = plain;
        // A Custom Lyrics switch the user flipped stays flipped (on: the loaded text shows
        // in the now-enabled box; off: the lyrics are being removed on purpose).
        if (!KeepsEdit(nameof(HasCustomLyrics))) HasCustomLyrics = !string.IsNullOrWhiteSpace(Lyrics);
        if (!KeepsEdit(nameof(HasCustomSyncedLyrics))) HasCustomSyncedLyrics = !string.IsNullOrWhiteSpace(SyncedLyrics);
        _loadedSyncedLyrics = synced;
        _loadedPlainLyrics = plain;
        if (!keepSynced) RebuildSyncedLinesFromText();
    }

    private void LoadFromTrack()
    {
        // Details
        Title = _track.Title;
        Artist = _track.Artist;
        AlbumArtist = _track.AlbumArtist;
        Album = _track.Album;
        // Build the genre options before assigning Genre so the ComboBox can bind the current value.
        BuildGenreOptions();
        Genre = _track.Genre;
        Composer = _track.Composer;
        TrackNumber = _track.TrackNumber > 0 ? _track.TrackNumber.ToString() : string.Empty;
        TrackCount = _track.TrackCount > 0 ? _track.TrackCount.ToString() : string.Empty;
        DiscNumber = _track.DiscNumber > 0 ? _track.DiscNumber.ToString() : string.Empty;
        DiscCount = _track.DiscCount > 0 ? _track.DiscCount.ToString() : string.Empty;
        Bpm = _track.Bpm > 0 ? _track.Bpm.ToString() : string.Empty;
        Year = _track.Year > 0 ? _track.Year.ToString() : string.Empty;
        IsCompilation = _track.IsCompilation;
        IsExplicit = _track.IsExplicit;
        _loadedIsExplicit = IsExplicit;
        ShowComposerInAllViews = _track.ShowComposerInAllViews;
        Grouping = _track.Grouping;
        UseWorkAndMovement = _track.UseWorkAndMovement;
        WorkName = _track.WorkName;
        MovementName = _track.MovementName;
        MovementNumber = _track.MovementNumber > 0 ? _track.MovementNumber.ToString() : string.Empty;
        MovementCount = _track.MovementCount > 0 ? _track.MovementCount.ToString() : string.Empty;
        PlayCount = _track.PlayCount > 0 ? _track.PlayCount.ToString() : "0";
        Comment = _track.Comment;
        Rating = _track.Rating;
        IsDisliked = _track.IsDisliked;

        // Lyrics — synced from Track.SyncedLyrics or .lrc sidecar; plain from Track.Lyrics or .txt sidecar.
        // Only the in-memory Track fields here: the sidecars sit next to the audio (often on a
        // spinning disk) and InitializeAsync reads them off the UI thread. Until it has, a
        // sidecar this dialog would consult counts as unreadable, so nothing can trash one it
        // never saw (Save is disabled while loading anyway; this holds even if load fails).
        var (synced, plain) = ResolveLyrics(_track.SyncedLyrics, null, _track.Lyrics, null);
        ApplyLyrics(synced, plain,
            syncedUnreadable: string.IsNullOrWhiteSpace(_track.SyncedLyrics),
            plainUnreadable: string.IsNullOrWhiteSpace(_track.Lyrics));

        // Options
        SkipWhenShuffling = _track.SkipWhenShuffling;
        RememberPlaybackPosition = _track.RememberPlaybackPosition;

        // Release-type override: "Auto" if no override, otherwise the enum name.
        ReleaseTypeOverride = _track.IsReleaseTypeOverridden
            ? _track.ReleaseType.ToString()
            : "Auto";

        // Options - extended
        MediaKind = string.IsNullOrEmpty(_track.MediaKind) ? "Music" : _track.MediaKind;
        HasStartTime = _track.StartTimeMs > 0;
        StartTime = _track.StartTimeMs > 0
            ? FormatTime(TimeSpan.FromMilliseconds(_track.StartTimeMs))
            : "0:00.000";
        HasStopTime = _track.StopTimeMs > 0;
        StopTime = _track.StopTimeMs > 0
            ? FormatTime(TimeSpan.FromMilliseconds(_track.StopTimeMs))
            : FormatTime(_track.Duration);
        VolumeAdjust = _track.VolumeAdjust;
        SelectedEqPreset = string.IsNullOrEmpty(_track.EqPreset) ? "None" : _track.EqPreset;
    }

    private void ApplyFileInfo(AudioFileInfo? info)
    {
        if (info == null)
            return; // ctor already seeded the path-derived fallback fields

        FileName = info.FileName;
        FileFormat = info.FileFormat;
        Codec = FormatCodecForFileTab(info.Codec);
        LosslessOrLossy = info.IsLossless ? "Lossless" : "Lossy";
        Bitrate = info.BitrateFormatted;
        SampleRate = info.SampleRateFormatted;
        BitsPerSample = info.BitsPerSampleFormatted;
        Channels = info.ChannelDescription;
        FileSize = info.FileSizeFormatted;
        Duration = info.DurationFormatted;
        DateAdded = _track.DateAdded.ToLocalTime().ToString("M/d/yyyy, h:mm tt");
        DateModified = info.DateModified.ToString("M/d/yyyy, h:mm tt");
        FileLocation = info.FilePath;
        FullFilePath = info.FilePath;
        FolderName = Path.GetDirectoryName(info.FilePath) ?? string.Empty;
    }

    private static string FormatCodecForFileTab(string codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return string.Empty;

        var normalized = codec.Trim();
        var lower = normalized.ToLowerInvariant();

        return lower.Contains("alac") || lower.Contains("apple lossless")
            ? "ALAC"
            : normalized;
    }

    private static byte[]? SelectPreferredArtworkData(byte[]? cachedData, byte[]? extractedData)
    {
        var hasCached = cachedData != null && cachedData.Length > 0;
        var hasExtracted = extractedData != null && extractedData.Length > 0;

        if (!hasCached && !hasExtracted)
            return null;
        if (!hasCached)
            return extractedData;
        if (!hasExtracted)
            return cachedData;

        // Use payload size as a quality proxy when dimensions aren't available.
        return extractedData!.Length > cachedData!.Length ? extractedData : cachedData;
    }

    [RelayCommand]
    private async Task AddArtwork(Avalonia.Visual visual)
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(visual);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Artwork",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png" } }
            }
        });

        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            _newArtworkData = ms.ToArray();
            _artworkRemoved = false;

            ms.Position = 0;
            var oldArt = ArtworkPreview;
            _artworkSourceSize = null; // decoded full size: the bitmap IS the source size
            ShowsOwnTrackArtwork = false; // a picked cover applies album-wide
            ArtworkPreview = new Bitmap(ms);
            DisposePreview(oldArt);
            HasArtwork = true;
        }
        catch { }
    }

    [RelayCommand]
    private async Task AddAnimatedCover(Avalonia.Visual visual)
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(visual);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Animated Artwork",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video") { Patterns = new[] { "*.mp4", "*.webm", "*.m4v", "*.mov" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });
        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        _newAnimatedCoverSource = path;
        _animatedCoverRemoved = false;
        AnimatedCoverPath = path;
        HasAnimatedCover = true;
    }

    [RelayCommand]
    private void RemoveAnimatedCover()
    {
        _newAnimatedCoverSource = null;
        _animatedCoverRemoved = true;
        AnimatedCoverPath = string.Empty;
        HasAnimatedCover = false;
        AnimatedSearchStatus = string.Empty;
        IsAnimatedArtworkSearchOpen = false;
    }

    [RelayCommand]
    private async Task DownloadAnimatedCover(Avalonia.Visual visual)
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(visual);
        if (topLevel == null || !topLevel.StorageProvider.CanSave) return;
        if (string.IsNullOrEmpty(AnimatedCoverPath) || !File.Exists(AnimatedCoverPath)) return;

        var ext = Path.GetExtension(AnimatedCoverPath);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Download Animated Artwork",
            SuggestedFileName = "animated-cover" + ext,
            DefaultExtension = ext.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType("Video") { Patterns = new[] { "*" + ext } } }
        });
        if (file == null) return;

        try
        {
            await using var src = File.OpenRead(AnimatedCoverPath);
            await using var dst = await file.OpenWriteAsync();
            await src.CopyToAsync(dst);
        }
        catch { /* Non-fatal */ }
    }

    // ── iTunes Search Artwork / Animated ───────────────────────────────

    private int _artworkSearchGeneration;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SearchArtwork()
    {
        if (_itunes == null) { ArtworkSearchStatus = "Search service unavailable."; return; }

        var gen = ++_artworkSearchGeneration;
        IsSearchingArtwork = true;
        ArtworkSearchStatus = "Searching…";
        ClearArtworkSearchResults();
        HasArtworkSearchResults = false;
        IsArtworkSearchOpen = true;
        try
        {
            var artist = !string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist : Artist;
            var album = GetArtworkSearchAlbumTerm();
            var results = await _itunes.SearchAlbumsAsync(artist, album);
            if (gen != _artworkSearchGeneration) return;

            if (!string.IsNullOrWhiteSpace(artist))
            {
                // Multi-artist tags ("Rema & Selena Gomez") rarely equal iTunes's
                // artist string exactly, so match loosely on normalized text —
                // and keep the ranked list untouched when nothing survives,
                // since the service already sorts best matches first.
                var wanted = NormalizeArtistForMatch(artist);
                var matched = results.Where(r =>
                {
                    var got = NormalizeArtistForMatch(r.ArtistName);
                    return got.Length > 0 &&
                           (got.Contains(wanted, StringComparison.Ordinal) ||
                            wanted.Contains(got, StringComparison.Ordinal));
                }).ToList();
                if (matched.Count > 0)
                    results = matched;
            }
            foreach (var r in results)
            {
                var item = await CreateArtworkSearchResultAsync(r);
                if (gen != _artworkSearchGeneration) return;
                ArtworkSearchResults.Add(item);
            }

            HasArtworkSearchResults = ArtworkSearchResults.Count > 0;
            ArtworkSearchStatus = HasArtworkSearchResults
                ? $"{ArtworkSearchResults.Count} result(s). Click one to apply."
                : "No matches found.";
        }
        catch (Exception ex)
        {
            if (gen == _artworkSearchGeneration)
                ArtworkSearchStatus = $"Search failed: {ex.Message}";
        }
        finally
        {
            if (gen == _artworkSearchGeneration)
                IsSearchingArtwork = false;
        }
    }

    [RelayCommand]
    private async Task SelectStandardArtworkResult(ArtworkSearchResult? result)
        => await SelectArtworkResultAsync(result, useHighestResolution: false);

    [RelayCommand]
    private async Task SelectHighestArtworkResult(ArtworkSearchResult? result)
        => await SelectArtworkResultAsync(result, useHighestResolution: true);

    private async Task SelectArtworkResultAsync(ArtworkSearchResult? result, bool useHighestResolution)
    {
        var candidate = result?.Candidate;
        if (candidate == null || _itunes == null) return;

        IsDownloadingArtwork = true;
        ArtworkSearchStatus = "Downloading…";
        try
        {
            var url = useHighestResolution ? candidate.HiResUrl : candidate.StandardUrl;
            var data = await _itunes.DownloadAsync(url)
                       ?? await _itunes.DownloadAsync(candidate.ThumbUrl);
            if (data is null or { Length: 0 } || !Services.HttpSafety.LooksLikeImage(data))
            {
                ArtworkSearchStatus = "Download failed.";
                return;
            }

            _newArtworkData = data;
            _artworkRemoved = false;

            using var ms = new MemoryStream(data);
            var oldArt = ArtworkPreview;
            _artworkSourceSize = null; // decoded full size: the bitmap IS the source size
            ShowsOwnTrackArtwork = false; // a picked cover applies album-wide
            ArtworkPreview = new Bitmap(ms);
            DisposePreview(oldArt);
            HasArtwork = true;
            ArtworkSearchStatus = "Applied. Click Save to keep.";
            IsArtworkSearchOpen = false;
        }
        catch (Exception ex)
        {
            ArtworkSearchStatus = $"Failed: {ex.Message}";
        }
        finally
        {
            IsDownloadingArtwork = false;
        }
    }

    [RelayCommand]
    private void CloseArtworkSearch() => IsArtworkSearchOpen = false;

    [RelayCommand]
    private async Task SearchAnimatedCover()
    {
        if (_itunes == null) { AnimatedSearchStatus = "Search service unavailable."; return; }
        if (IsSearchingAnimatedCover) return;

        IsSearchingAnimatedCover = true;
        AnimatedSearchStatus = "Searching…";
        AnimatedArtworkSearchResults.Clear();
        HasAnimatedArtworkSearchResults = false;
        IsAnimatedArtworkSearchOpen = true;
        ClearAnimatedPreview();
        try
        {
            var variants = new List<ITunesArtworkService.AnimatedArtworkVariant>();

            // Manual override: a pasted Apple Music URL or album ID names the album outright,
            // so it skips search on both sources.
            if (TryExtractAppleAlbumId(AnimatedSearchInput, out var pastedId))
            {
                var candidate = _itunes == null ? null : await _itunes.LookupAlbumByIdAsync(pastedId);
                if (candidate != null)
                    variants.AddRange(await _itunes!.SearchAnimatedArtworkVariantsAsync(candidate.ViewUrl));
            }
            else
            {
                // A typed album name steers the search away from the loaded track's tags.
                // The artist is then unknown, and IsLikelySameAlbum falls back to matching on
                // title alone — which is what the user asked for by typing it.
                var typed = (AnimatedSearchInput ?? string.Empty).Trim();
                var manual = typed.Length > 0;
                var trackArtist = !string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist : Artist;
                var album = manual ? typed : GetArtworkSearchAlbumTerm();
                // A typed name is the whole query, so there is no artist to corroborate with
                // and the match falls back to the title alone. That alone would hand over a
                // karaoke record: typing "YHLQMDLG" matches a cover act's identically titled
                // album just as well as Bad Bunny's. The track's artist can't be a requirement
                // (the user may be searching for someone else's album entirely), so it is
                // passed as a preference that only orders equally-titled matches.
                var artist = manual ? string.Empty : trackArtist;

                variants.AddRange(await SearchAppleMusicVariantsAsync(album, artist, trackArtist));
                if (variants.Count == 0)
                    variants.AddRange(await SearchITunesVariantsAsync(album, artist, trackArtist));
            }

            foreach (var variant in variants)
                AnimatedArtworkSearchResults.Add(new AnimatedArtworkSearchResult(variant));

            HasAnimatedArtworkSearchResults = AnimatedArtworkSearchResults.Count > 0;
            if (!HasAnimatedArtworkSearchResults)
            {
                // Deliberately source-agnostic: which catalogue was consulted, and how many,
                // is Noctis's business — the user only needs to know there is nothing to offer.
                AnimatedSearchStatus = "No animated artwork found.";
                return;
            }

            // No informational status once results are showing — the buttons speak for
            // themselves; the status line stays reserved for errors.
            AnimatedSearchStatus = string.Empty;

            // Download the lowest-bitrate variant in the background to drive the
            // live preview inside the popup. The user keeps a snappy UI; the
            // preview swaps in when ready.
            _ = LoadAnimatedPreviewAsync(variants);
        }
        catch (Exception ex)
        {
            AnimatedSearchStatus = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearchingAnimatedCover = false;
        }
    }

    // ── Animated-artwork sources, in fall-through order ───────────────────────
    // 1. The Apple Music catalogue, via its web search page. 2. The iTunes Search API.
    // Both end at the same album-page scrape; only the way the album is found differs.
    // iTunes stays as the second source because its index has holes — it finds nothing for
    // albums like Bad Bunny's "YHLQMDLG" — but it is a plain JSON API, so it keeps answering
    // if the web page's markup shifts under the first one.

    private async Task<IReadOnlyList<ITunesArtworkService.AnimatedArtworkVariant>> SearchAppleMusicVariantsAsync(
        string album, string artist, string preferArtist)
    {
        if (_itunes == null || string.IsNullOrWhiteSpace(album))
            return Array.Empty<ITunesArtworkService.AnimatedArtworkVariant>();

        var results = await _itunes.SearchAppleMusicAlbumsAsync(artist, album);
        return await FirstMatchingVariantsAsync(results, album, artist, preferArtist);
    }

    // 0 sorts ahead of 1: candidates by the artist we already know about win ties. Only a
    // tiebreak — a list with no such candidate is left in the source's own ranked order.
    private static int PreferredArtistRank(string? candidateArtist, string? preferArtist)
    {
        if (string.IsNullOrWhiteSpace(preferArtist) || string.IsNullOrWhiteSpace(candidateArtist))
            return 1;

        return ITunesArtworkService.IsLikelySameArtist(candidateArtist, preferArtist) ? 0 : 1;
    }

    private async Task<IReadOnlyList<ITunesArtworkService.AnimatedArtworkVariant>> SearchITunesVariantsAsync(
        string album, string artist, string preferArtist)
    {
        if (_itunes == null)
            return Array.Empty<ITunesArtworkService.AnimatedArtworkVariant>();

        // Ask for a wider list than we intend to open: ranking puts the right album first
        // only when the title is distinctive, and self-titled albums sit behind a wall of
        // karaoke and cover records.
        var results = await _itunes.SearchAlbumsAsync(artist, album, limit: 8);
        return await FirstMatchingVariantsAsync(results, album, artist, preferArtist);
    }

    /// <summary>
    /// Opens candidates in order and returns the first album that actually has a loop.
    /// Ranking orders candidates but never rejects one, so taking the first that happened to
    /// have a video handed people another album's cover — searching Drake's "Nothing Was the
    /// Same" offered Take Care's loop. Only candidates that really are this album are opened.
    /// </summary>
    private async Task<IReadOnlyList<ITunesArtworkService.AnimatedArtworkVariant>> FirstMatchingVariantsAsync(
        IReadOnlyList<ITunesArtworkService.ArtworkCandidate> results,
        string album, string artist, string preferArtist)
    {
        foreach (var r in results
                     .Where(r => ITunesArtworkService.IsLikelySameAlbum(
                         r.CollectionName, r.ArtistName, album, artist))
                     .OrderBy(r => PreferredArtistRank(r.ArtistName, preferArtist)))
        {
            var variants = await _itunes!.SearchAnimatedArtworkVariantsAsync(r.ViewUrl);
            if (variants.Count > 0)
                return variants;
        }

        return Array.Empty<ITunesArtworkService.AnimatedArtworkVariant>();
    }

    // Pulls a numeric album ID from a pasted Apple Music URL or a bare ID.
    // Accepts:  music.apple.com/us/album/<slug>/1710982865[?...]
    //           https://music.apple.com/.../id1710982865
    //           1710982865
    //
    // The box doubles as an album-name field, so this has to be a real classification. The
    // old rule took any 6+ digit run *anywhere* in the input, which would read a typed album
    // name that happens to contain a long number as an ID and search for the wrong thing.
    internal static bool TryExtractAppleAlbumId(string? input, out long id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();

        // A bare ID: nothing but digits.
        if (trimmed.All(char.IsAsciiDigit))
            return trimmed.Length >= 6 && long.TryParse(trimmed, out id);

        // Otherwise it only counts as an ID if it is actually an Apple Music link.
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("apple.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"\d{6,}");
        return match.Success && long.TryParse(match.Value, out id);
    }

    // Set only when the user explicitly clears synced lyrics via the Remove button.
    private bool _syncedLyricsRemovedByUser;

    // Set when a sidecar exists on disk but could not be read during load, so save
    // leaves it strictly alone.
    private bool _syncedSidecarUnreadable;
    private bool _plainSidecarUnreadable;

    // What load actually produced, so save can tell "the user emptied this field"
    // (a real removal) from "load never managed to populate it" (not a removal).
    private string _loadedSyncedLyrics = string.Empty;
    private string _loadedPlainLyrics = string.Empty;

    /// <summary>True when the user cleared synced lyrics that were genuinely loaded.</summary>
    private bool SyncedLyricsWereRemoved =>
        (_syncedLyricsRemovedByUser || !string.IsNullOrWhiteSpace(_loadedSyncedLyrics)) &&
        string.IsNullOrWhiteSpace(_track.SyncedLyrics) &&
        !_syncedSidecarUnreadable;

    /// <summary>Moves a lyrics sidecar to the OS trash; replaceable in tests (as on LyricsWriter).</summary>
    internal Func<string, bool> TrashFile { get; init; } = Helpers.RecycleBin.TryMoveToTrash;

    /// <summary>The synced lyrics being saved differ from the ones this dialog loaded (searched, imported, edited, shifted).</summary>
    private bool SyncedLyricsWereChanged =>
        !string.IsNullOrWhiteSpace(_track.SyncedLyrics) &&
        !string.Equals(NormalizeNewlines(_track.SyncedLyrics).Trim(), NormalizeNewlines(_loadedSyncedLyrics).Trim(), StringComparison.Ordinal);

    private static string NormalizeNewlines(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// The lyrics page reads .lyricsfile, .ttml and .elrc before .lrc. Synced lyrics changed or
    /// removed here went into the .lrc only, so a Lyrics Studio .elrc (or a .ttml) beside the song
    /// kept showing the old lyrics and the edit looked lost. Called only on a real change or
    /// removal — a Year fix must not cost a song its word timings. To the trash, like the .lrc.
    /// </summary>
    private void TrashSidecarsAboveLrc(string trackPath)
    {
        foreach (var ext in new[] { ".lyricsfile", ".ttml", ".elrc" })
            foreach (var path in Services.Lyrics.LyricsWriter.SidecarsOnDisk(trackPath, ext))
            {
                try
                {
                    if (TrashFile(path))
                        AppWrittenSidecarRegistry.Default.Remove(path);
                }
                catch { /* best effort, as the .lrc write */ }
            }
    }

    /// <summary>True when the user cleared plain lyrics that were genuinely loaded.</summary>
    private bool PlainLyricsWereRemoved =>
        !string.IsNullOrWhiteSpace(_loadedPlainLyrics) &&
        string.IsNullOrWhiteSpace(_track.Lyrics) &&
        !_plainSidecarUnreadable;

    [RelayCommand]
    private void RemoveSyncedLyrics()
    {
        SyncedLyrics = string.Empty;
        HasCustomSyncedLyrics = false;
        _syncedLyricsRemovedByUser = true;
        SyncedLyricsSearchStatus = string.Empty;
        RebuildSyncedLinesFromText();
    }

    [ObservableProperty] private bool _syncedLyricsCopied;

    [RelayCommand]
    private async Task CopySyncedLyricsAsync()
    {
        if (string.IsNullOrWhiteSpace(SyncedLyrics)) return;
        var clipboard = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?.Clipboard;
        if (clipboard is null) return;

        try { await clipboard.SetTextAsync(SyncedLyrics); } catch { return; }

        SyncedLyricsCopied = true;
        await Task.Delay(1500);
        SyncedLyricsCopied = false;
    }

    // ── Synced lyrics manual editor plumbing ──

    /// <summary>
    /// (Re)populates the editable line list from the current text. Prefers the
    /// synced LRC; when there is none, seeds from the plain lyrics so the user can
    /// manually time them. Untimestamped lines round-trip safely — Save only writes
    /// synced lyrics once they actually contain timestamps.
    /// </summary>
    private void RebuildSyncedLinesFromText()
    {
        foreach (var line in SyncedLyricLines)
            line.Changed -= OnSyncedLineChanged;
        SyncedLyricLines.Clear();

        var source = !string.IsNullOrWhiteSpace(SyncedLyrics) ? SyncedLyrics : Lyrics;
        foreach (var (time, text) in LrcEditorViewModel.ParseLrc(source ?? string.Empty))
        {
            var line = new SyncedLyricEditorLine(time, text);
            line.Changed += OnSyncedLineChanged;
            SyncedLyricLines.Add(line);
        }

        HasSyncedLines = SyncedLyricLines.Count > 0;
        HasWordTimedLines = SyncedLyricLines.Any(l => l.HasWordTiming);
    }

    private void OnSyncedLineChanged() => RebuildSyncedTextFromLines();

    /// <summary>Regenerates the SyncedLyrics LRC text from the edited lines, in list order.</summary>
    private void RebuildSyncedTextFromLines()
    {
        SyncedLyrics = string.Join("\n", SyncedLyricLines.Select(l =>
            l.Timestamp is { } t
                ? $"[{LrcEditorViewModel.FormatTimestamp(t)}]{l.Text}"
                : l.Text));

        // Manually timing lines counts as having custom synced lyrics. Save gates
        // the synced write on HasCustomSyncedLyrics, which stays false when the
        // lines were seeded from plain lyrics — without this, a first timing pass
        // on the Timestamp Lyrics tab would be silently dropped on Save.
        if (!HasCustomSyncedLyrics && LyricsTextHelper.ContainsTimestamps(SyncedLyrics))
            HasCustomSyncedLyrics = true;
    }

    /// <summary>Seconds the "Shift all" buttons move every timestamp by (GitHub #57).
    /// Free text so "1", "0.25" and "1,5" all work; anything unparseable is ignored.</summary>
    [ObservableProperty] private string _shiftAllSeconds = "0.5";

    [RelayCommand]
    private void ShiftAllEarlier() => ShiftAllTimestamps(-1);

    [RelayCommand]
    private void ShiftAllLater() => ShiftAllTimestamps(+1);

    private void ShiftAllTimestamps(int direction)
    {
        if (!TryParseShiftSeconds(ShiftAllSeconds, out var seconds) || seconds <= 0) return;
        if (!LyricsTextHelper.ContainsTimestamps(SyncedLyrics)) return;

        SyncedLyrics = LyricsTextHelper.ShiftAllTimestamps(SyncedLyrics, TimeSpan.FromSeconds(seconds * direction));
        HasCustomSyncedLyrics = true;
        RebuildSyncedLinesFromText();
    }

    internal static bool TryParseShiftSeconds(string? text, out double seconds)
    {
        var t = (text ?? string.Empty).Trim().Replace(',', '.');
        return double.TryParse(t, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out seconds)
               && double.IsFinite(seconds);
    }

    [RelayCommand]
    private void NudgeSyncedLineBack(SyncedLyricEditorLine? line) => line?.Nudge(forward: false);

    [RelayCommand]
    private void NudgeSyncedLineForward(SyncedLyricEditorLine? line) => line?.Nudge(forward: true);

    [RelayCommand]
    private void ClearSyncedLineTimestamp(SyncedLyricEditorLine? line) => line?.ClearTimestamp();

    /// <summary>
    /// "Import file" on the lyrics tabs (Discord ask, 2026-08-31): pick an .lrc or .txt
    /// and load it as the track's lyrics, for the many songs no provider has. Nothing is
    /// written until Save, which then takes the usual sidecar/tag path.
    /// </summary>
    [RelayCommand]
    private async Task ImportLyricsFile(Avalonia.Visual visual)
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(visual);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Lyrics",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Lyrics") { Patterns = new[] { "*.lrc", "*.txt" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });
        if (files.Count == 0) return;

        string text;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            // StreamReader strips a UTF-8/UTF-16 BOM, which a raw byte decode would leave
            // in front of the first timestamp and break the parser.
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            text = await reader.ReadToEndAsync();
        }
        catch (Exception ex)
        {
            SyncedLyricsSearchStatus = $"Couldn't read file: {ex.Message}";
            return;
        }

        ImportLyricsText(text, files[0].Name);
    }

    /// <summary>
    /// Routes imported text by content, not extension: timestamped lines become the synced
    /// lyrics (a .txt full of [mm:ss] tags is still LRC), anything else becomes the plain
    /// lyrics. Both tabs' Enable toggles flip on so the Save gate sees the import.
    /// </summary>
    internal void ImportLyricsText(string text, string fileName)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (normalized.Length == 0)
        {
            SyncedLyricsSearchStatus = $"{fileName} is empty.";
            return;
        }

        if (LyricsTextHelper.ContainsTimestamps(normalized))
        {
            SyncedLyrics = normalized;
            HasCustomSyncedLyrics = true;
            SyncedLyricsSearchStatus = $"Imported {fileName}";
            RebuildSyncedLinesFromText();
        }
        else
        {
            Lyrics = normalized;
            HasCustomLyrics = true;
            SyncedLyricsSearchStatus = $"Imported {fileName} as plain lyrics (no timestamps found).";
        }
    }

    [RelayCommand]
    private async Task SearchSyncedLyrics()
    {
        if (_lrcLib == null) { SyncedLyricsSearchStatus = "Search service unavailable."; return; }
        if (IsSearchingSyncedLyrics) return;
        if (string.IsNullOrWhiteSpace(Title) || string.IsNullOrWhiteSpace(Artist))
        {
            SyncedLyricsSearchStatus = "Title and artist required.";
            return;
        }

        IsSearchingSyncedLyrics = true;
        SyncedLyricsSearchStatus = "Searching…";
        try
        {
            // An error on /get (LRCLIB answers 503 "busy" there while /search works — live
            // check 10-05) falls through to /search; only both failing is an error.
            LrcLibResult? result = null;
            LyricsProviderException? getError = null;
            try { result = await _lrcLib.GetLyricsAsync(Artist, Title, _track.Duration.TotalSeconds); }
            catch (LyricsProviderException ex) { getError = ex; }
            if (result == null || !result.HasSyncedLyrics)
            {
                // /search is fuzzy — validate against the edited tags and the track's
                // duration so a different song's lyrics can't be picked up.
                List<LrcLibResult> alts;
                try { alts = await _lrcLib.SearchLyricsAsync(Artist, Title); }
                catch (LyricsProviderException) when (getError is not null) { throw getError; }
                result = LyricsSearchSelector.PickFromSearchResults(
                    alts, Artist, Title, _track.Duration.TotalSeconds, requireSynced: true);
                // Nothing from /search after a /get error is not "no lyrics": /get might have had them.
                if (result is null && getError is not null) throw getError;
            }

            if (result?.SyncedLyrics is { Length: > 0 } synced)
            {
                SyncedLyrics = synced;
                HasCustomSyncedLyrics = true;
                SyncedLyricsSearchStatus = "Lyrics found";
                RebuildSyncedLinesFromText();
            }
            else
            {
                SyncedLyricsSearchStatus = "No Lyrics found";
            }
        }
        catch (LyricsProviderException)
        {
            // Network failure / timeout / provider outage — not "no lyrics", and the
            // raw exception text ("The request was canceled…") helps nobody. Not "check your
            // internet connection" either: a busy LRCLIB (503) on an online machine said that.
            SyncedLyricsSearchStatus = "Search failed — LRCLIB didn't answer (busy or offline). Try again in a moment.";
        }
        catch (Exception ex)
        {
            SyncedLyricsSearchStatus = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearchingSyncedLyrics = false;
        }
    }

    private async Task LoadAnimatedPreviewAsync(IReadOnlyList<ITunesArtworkService.AnimatedArtworkVariant> variants)
    {
        if (variants.Count == 0) return;

        // Smallest variant by max pixel dimension, then by bandwidth — gives us
        // the fastest download for a preview-quality loop.
        var preview = variants
            .OrderBy(v => Math.Max(v.Width, v.Height))
            .ThenBy(v => v.Bandwidth)
            .First();

        try
        {
            var path = await DownloadAnimatedVariantAsync(preview);
            if (string.IsNullOrWhiteSpace(path) || !IsAnimatedArtworkSearchOpen)
            {
                if (!string.IsNullOrWhiteSpace(path)) TryDeleteFile(path);
                return;
            }

            ClearAnimatedPreview();
            AnimatedPreviewPath = path;
        }
        catch { /* preview is optional */ }
    }

    private void ClearAnimatedPreview()
    {
        var old = AnimatedPreviewPath;
        AnimatedPreviewPath = null;
        if (!string.IsNullOrWhiteSpace(old)) TryDeleteFile(old);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [RelayCommand]
    private async Task SelectAnimatedArtworkResult(AnimatedArtworkSearchResult? result)
    {
        if (result == null || _itunes == null)
            return;

        IsDownloadingAnimatedCover = true;
        // Clear any prior error; the spinner row carries its own "Downloading…" label.
        AnimatedSearchStatus = string.Empty;
        try
        {
            var tempPath = await DownloadAnimatedVariantAsync(result.Variant);
            if (string.IsNullOrWhiteSpace(tempPath))
                return;

            _newAnimatedCoverSource = tempPath;
            _animatedCoverRemoved = false;
            AnimatedCoverPath = string.Empty;
            AnimatedCoverPath = tempPath;
            HasAnimatedCover = true;
            AnimatedSearchStatus = "Applied. Click Save to keep.";
            IsAnimatedArtworkSearchOpen = false;
        }
        catch (Exception ex)
        {
            AnimatedSearchStatus = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsDownloadingAnimatedCover = false;
        }
    }

    private async Task<string?> DownloadAnimatedVariantAsync(ITunesArtworkService.AnimatedArtworkVariant variant)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"noctis-animatedcover-{Guid.NewGuid():N}.mp4");
        if (!variant.IsHls)
        {
            var data = await _itunes!.DownloadAsync(variant.Url,
                maxBytes: ITunesArtworkService.MaxAnimatedCoverBytes);
            if (data is null or { Length: 0 })
            {
                AnimatedSearchStatus = "Animated cover download failed.";
                return null;
            }

            await File.WriteAllBytesAsync(tempPath, data);
            return tempPath;
        }

        if (!await _itunes!.DownloadHlsVariantAsMp4Async(variant, tempPath))
        {
            AnimatedSearchStatus = "Could not download Apple Music animated artwork.";
            return null;
        }

        return tempPath;
    }

    /// <summary>Lowercases and strips punctuation/joiners so artist strings from
    /// tags and iTunes compare on words alone ("A & B" == "a b").</summary>
    private static string NormalizeArtistForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = System.Text.RegularExpressions.Regex.Replace(
            value.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ");
        return System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private string GetArtworkSearchAlbumTerm()
    {
        if (!string.IsNullOrWhiteSpace(Album) &&
            !string.Equals(Album.Trim(), "Unknown Album", StringComparison.OrdinalIgnoreCase))
        {
            return Album.Trim();
        }

        return Title.Trim();
    }

    private async Task<ArtworkSearchResult> CreateArtworkSearchResultAsync(ITunesArtworkService.ArtworkCandidate candidate)
    {
        Bitmap? thumbnail = null;
        if (_itunes != null)
        {
            var data = await _itunes.DownloadAsync(candidate.ThumbUrl);
            if (data is { Length: > 0 })
            {
                try
                {
                    using var ms = new MemoryStream(data);
                    thumbnail = new Bitmap(ms);
                }
                catch
                {
                    thumbnail?.Dispose();
                    thumbnail = null;
                }
            }
        }

        return new ArtworkSearchResult(candidate, thumbnail);
    }

    private void ClearArtworkSearchResults()
    {
        foreach (var result in ArtworkSearchResults)
            result.Dispose();
        ArtworkSearchResults.Clear();
    }

    [RelayCommand]
    private void RemoveArtwork()
    {
        ShowsOwnTrackArtwork = false;
        var oldArt = ArtworkPreview;
        _artworkSourceSize = null;
        ArtworkPreview = null;
        DisposePreview(oldArt);
        HasArtwork = false;
        _newArtworkData = null;
        _artworkRemoved = true;
    }

    [RelayCommand]
    private async Task DownloadArtwork(Avalonia.Visual visual)
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(visual);
        if (topLevel == null || !topLevel.StorageProvider.CanSave) return;

        var artworkData = GetCurrentArtworkData();
        if (artworkData == null || artworkData.Length == 0) return;

        var (extension, fileType) = GetArtworkSaveType(artworkData);
        var suggestedFileName = "cover-art" + extension;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Download Cover Art",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { fileType }
        });

        if (file == null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(artworkData);
        }
        catch { }
    }

    private byte[]? GetCurrentArtworkData()
    {
        if (_newArtworkData != null && _newArtworkData.Length > 0)
            return _newArtworkData;

        var artPath = DisplayedArtworkPath();
        if (File.Exists(artPath))
        {
            try { return File.ReadAllBytes(artPath); }
            catch { }
        }

        return _metadata.ExtractAlbumArt(_track.FilePath);
    }

    /// <summary>The cover this dialog shows: a single track with its own embedded cover
    /// (<see cref="TrackArtwork"/>) shows that; album and multi-track edits show the album's.</summary>
    private string DisplayedArtworkPath()
    {
        if (!_albumScoped && !_multiSelect)
        {
            var own = _persistence.GetTrackArtworkPath(_track.Id);
            if (File.Exists(own)) return own;
        }
        return _persistence.GetArtworkPath(_track.AlbumId);
    }

    /// <summary>A new or removed album cover was just written into every track's tag, so
    /// no track differs from its album any more: drop their own covers and record the
    /// new fingerprint (a later rescan would otherwise resurrect the old split).</summary>
    private void ClearOwnTrackArtwork(IEnumerable<Track> tracks, byte[]? albumArt)
    {
        var hash = TrackArtwork.Fingerprint(albumArt);
        foreach (var t in tracks)
        {
            t.ArtworkHash = hash;
            _persistence.DeleteTrackArtwork(t.Id);
        }
    }

    /// <summary>Records a file the save could not write, once: the playing track often
    /// fails both its tag and its cover write, and the error should count it as one file.</summary>
    private static void AddFailedWrite(List<string> failedWrites, string filePath)
    {
        var name = Path.GetFileName(filePath);
        lock (failedWrites)
            if (!failedWrites.Contains(name)) failedWrites.Add(name);
    }

    private static (string Extension, FilePickerFileType FileType) GetArtworkSaveType(byte[] data)
    {
        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return (".png", new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } });
        }

        return (".jpg", new FilePickerFileType("JPEG Image") { Patterns = new[] { "*.jpg", "*.jpeg" } });
    }

    private bool CanSave() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        // ExecuteAsync and key bindings skip CanExecute. Saving before InitializeAsync has
        // applied the sidecar lyrics and advanced tags would write state that was never
        // loaded (an empty plain field read as "the user cleared the lyrics").
        if (IsLoading) return;

        // Surface a "Saving…" state on the button while the (potentially slow) file
        // writes run; the work itself lives in SaveInternalAsync. try/finally so the
        // spinner always clears even if a write throws.
        IsSaving = true;
        try
        {
            await SaveInternalAsync();
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task SaveInternalAsync()
    {
        var oldAlbumId = _track.AlbumId;

        if (!_albumScoped)
        {
            // Apply Details changes to the track model
            _track.Title = Title;
            _track.Artist = Artist;
            _track.AlbumArtist = AlbumArtist;
            _track.Album = Album;
            _track.Genre = (Genre ?? string.Empty).Trim(); // typed genres save trimmed (GitHub #123)
            _track.Composer = Composer;
            _track.TrackNumber = int.TryParse(TrackNumber, out var tn) ? tn : 0;
            _track.TrackCount = int.TryParse(TrackCount, out var tc) ? tc : 0;
            _track.DiscNumber = int.TryParse(DiscNumber, out var dn) ? Math.Max(1, dn) : 1;
            _track.DiscCount = int.TryParse(DiscCount, out var dc) ? Math.Max(1, dc) : 1;
            _track.Bpm = int.TryParse(Bpm, out var bp) ? Math.Max(0, bp) : 0;
            _track.Year = int.TryParse(Year, out var yr) ? yr : 0;
            _track.IsCompilation = IsCompilation;
            _track.ShowComposerInAllViews = ShowComposerInAllViews;
            _track.Grouping = Grouping;
            _track.UseWorkAndMovement = UseWorkAndMovement;
            _track.WorkName = WorkName;
            _track.MovementName = MovementName;
            _track.MovementNumber = int.TryParse(MovementNumber, out var mn) ? Math.Max(0, mn) : 0;
            _track.MovementCount = int.TryParse(MovementCount, out var mc) ? Math.Max(0, mc) : 0;
            _track.PlayCount = int.TryParse(PlayCount, out var pc) ? Math.Max(0, pc) : 0;
            _track.Comment = Comment;
            _track.Copyright = Copyright ?? string.Empty;
            _track.Rating = Math.Clamp(Rating, 0, 5);
            _track.IsDisliked = IsDisliked;

            // Recalculate AlbumId if album or artist changed
            _track.AlbumId = Track.ComputeAlbumId(_track.AlbumArtist, _track.Album);
        }
        else
        {
            // Album-scoped: fan out only the Details fields the user actually
            // changed to every album track (per-track values are preserved).
            ApplyAlbumScopedDetails();
            ApplyStagedTrackChanges();
        }

        // Apply a staged play-count reset (kept pending so Cancel discards it).
        if (_resetPlayCountPending)
        {
            foreach (var t in _albumTracks ?? new List<Track> { _track })
            {
                t.PlayCount = 0;
                t.LastPlayed = null;
            }
        }

        // Apply Lyrics (plain + synced) — defensively strip timestamps from plain.
        // Single-track only: the album/multi-select dialog has no lyric tabs, and applying
        // the first track's sidecar text here changed its Lyrics, so an Options-only album
        // save rewrote that file's tags and sidecars (failing outright on the playing file).
        if (!_albumScoped)
        {
            var plainToWrite = HasCustomLyrics
                ? (LyricsTextHelper.ContainsTimestamps(Lyrics) ? LyricsTextHelper.StripTimestamps(Lyrics) : Lyrics)
                : string.Empty;
            var syncedToWrite = HasCustomSyncedLyrics && LyricsTextHelper.ContainsTimestamps(SyncedLyrics)
                ? SyncedLyrics
                : string.Empty;
            _track.Lyrics = plainToWrite;
            _track.SyncedLyrics = syncedToWrite;
        }

        // Apply Options
        _track.SkipWhenShuffling = SkipWhenShuffling;
        _track.RememberPlaybackPosition = RememberPlaybackPosition;
        _track.MediaKind = MediaKind;

        // Release type override (album-level concept; fanned out to all album
        // tracks below). "Auto" clears the override so auto-detection runs again.
        ApplyReleaseTypeOverride(_track);
        _track.StartTimeMs = HasStartTime ? ParseTimeToMs(StartTime) : 0;
        _track.StopTimeMs = HasStopTime ? ParseTimeToMs(StopTime) : 0;
        _track.VolumeAdjust = VolumeAdjust;
        _track.EqPreset = SelectedEqPreset == "None" ? string.Empty : SelectedEqPreset;

        // Fan out options to all album tracks when album-scoped — but only the
        // fields the user actually changed, so per-track Options on the other
        // tracks (volume, EQ, start/stop, …) aren't overwritten with the first
        // track's values on an unrelated save.
        if (_albumScoped && _albumTracks != null)
        {
            long startMs = HasStartTime ? ParseTimeToMs(StartTime) : 0;
            long stopMs = HasStopTime ? ParseTimeToMs(StopTime) : 0;
            string eqVal = SelectedEqPreset == "None" ? string.Empty : SelectedEqPreset;

            bool skipChg = SkipWhenShuffling != _loadedSkipWhenShuffling;
            bool rememberChg = RememberPlaybackPosition != _loadedRememberPlaybackPosition;
            bool mediaKindChg = MediaKind != _loadedMediaKind;
            bool releaseTypeChg = !string.Equals(ReleaseTypeOverride, _loadedReleaseTypeOverride, StringComparison.OrdinalIgnoreCase);
            bool startChg = startMs != _loadedStartTimeMs;
            bool stopChg = stopMs != _loadedStopTimeMs;
            bool volumeChg = VolumeAdjust != _loadedVolumeAdjust;
            // Case-insensitive like preset lookup: SetUserEqPresets respells the selection to
            // the listed name ("rock" -> "Rock"), which is not a change worth fanning out.
            bool eqChg = !string.Equals(eqVal, _loadedEqPreset, StringComparison.OrdinalIgnoreCase);

            foreach (var t in _albumTracks)
            {
                if (skipChg) t.SkipWhenShuffling = SkipWhenShuffling;
                if (rememberChg) t.RememberPlaybackPosition = RememberPlaybackPosition;
                if (mediaKindChg) t.MediaKind = MediaKind;
                if (startChg) t.StartTimeMs = startMs;
                if (stopChg) t.StopTimeMs = stopMs;
                if (volumeChg) t.VolumeAdjust = VolumeAdjust;
                if (eqChg) t.EqPreset = eqVal;
                if (releaseTypeChg) ApplyReleaseTypeOverride(t);
            }
        }

        // Write metadata to file tags (plain lyrics go to USLT tag). Album-scoped
        // edits must be written to every track, not just Tracks[0]. Each write
        // opens and rewrites the audio file, so run the batch on a worker thread —
        // doing it on the UI thread froze the app for seconds on large albums
        // (e.g. saving an animated cover for a 20+ track album).
        // Files whose tags could not be written. Collected rather than discarded: the
        // write result used to be dropped on the floor, so a failed save (most commonly
        // the currently-playing track, whose handle libVLC holds) closed the dialog
        // cleanly and showed the new tags while the file on disk was untouched — the
        // edit then silently vanished on the next rescan.
        var failedWrites = new List<string>();

        // Only files whose tags actually changed. Rewriting the rest is not merely wasted
        // work: the audio file the player holds open cannot be written at all, so an
        // unconditional pass made an animated-cover or rating save fail on the playing track
        // and report an error for an edit that never needed to touch it.
        if (_albumScoped && _albumTracks != null && _albumTracks.Count > 0)
        {
            var tagWriteTargets = _albumTracks.Where(NeedsTagWrite).ToList();
            var yearClearTargets = tagWriteTargets.Where(YearWasCleared).ToList();
            await Task.Run(() =>
            {
                foreach (var t in tagWriteTargets)
                    if (!_metadata.WriteTrackMetadata(t)
                        || (yearClearTargets.Contains(t) && !_metadata.ClearYear(t.FilePath)))
                        lock (failedWrites) failedWrites.Add(Path.GetFileName(t.FilePath));
            });

            // "Album is explicit": write ITUNESADVISORY to every track whose flag differs
            // from the checkbox. Only when the user actually toggled it — an untouched
            // checkbox on a mixed album must not silently stamp every track.
            if (IsExplicit != _loadedIsExplicit)
            {
                var advisoryTargets = _albumTracks.Where(t => t.IsExplicit != IsExplicit).ToList();
                var advisoryValue = IsExplicit ? 1 : 0;
                var flipped = new List<Track>();
                await Task.Run(() =>
                {
                    foreach (var t in advisoryTargets)
                    {
                        if (_metadata.WriteAdvisory(t.FilePath, advisoryValue))
                            lock (flipped) flipped.Add(t);
                        else
                            lock (failedWrites) failedWrites.Add(Path.GetFileName(t.FilePath));
                    }
                });
                // Track.IsExplicit is observable, so every "E" badge and the playback
                // filter see the change immediately; the library save below persists it.
                foreach (var t in flipped) t.IsExplicit = IsExplicit;
                _loadedIsExplicit = IsExplicit;
                OnPropertyChanged(nameof(HeaderIsExplicit));
            }
        }
        else if (NeedsTagWrite(_track))
        {
            var clearYear = YearWasCleared(_track);
            var ok = await Task.Run(() => _metadata.WriteTrackMetadata(_track)
                                          && (!clearYear || _metadata.ClearYear(_track.FilePath)));
            if (!ok) failedWrites.Add(Path.GetFileName(_track.FilePath));
        }

        // Write advanced fields (sort, people, identifiers, custom tags).
        // Runs on a worker thread for the same reason as the writes above — this opens
        // the file and rewrites every tag block, which for a large FLAC/WAV/DSD froze
        // the window for the duration when it ran inline.
        if (!_albumScoped && _originalAdvancedFields != null)
        {
            var advFields = BuildAdvancedFields();
            var originalAdv = _originalAdvancedFields;
            var advPath = _track.FilePath;
            // Routed through the metadata service's atomic path: AdvancedTagIO.WriteAll
            // does an in-place file.Save(), which on macOS/Linux corrupts the player's
            // open read of the track being tagged (the "audio silently stops on save"
            // bug) and is not crash-safe anywhere. Skipped when nothing changed: the
            // atomic path still copies and rename-replaces the whole file, and doing
            // that to the PLAYING track on every save (e.g. an animated-artwork-only
            // save) yanks the file out from under LibVLC's open read for nothing.
            if (!AdvancedTagIO.FieldsEqual(advFields, originalAdv))
            {
                if (!await Task.Run(() => _metadata.WriteAdvancedFields(advPath, advFields, originalAdv)))
                    failedWrites.Add(Path.GetFileName(advPath));
            }

            // Sync advisory → IsExplicit for immediate badge update
            _track.IsExplicit = advFields.ItunesAdvisory == 1;
        }

        // Write synced lyrics to sidecar .lrc file (single-track only, as above).
        // Deletion is gated on an explicit user removal and never happens when the
        // sidecar failed to load — and it goes to the trash, not File.Delete, so a
        // mistake is recoverable.
        if (!_albumScoped)
        {
            try
            {
                var lrcPath = Path.ChangeExtension(_track.FilePath, ".lrc");
                if (!string.IsNullOrWhiteSpace(_track.SyncedLyrics))
                {
                    await File.WriteAllTextAsync(lrcPath, _track.SyncedLyrics);
                    if (SyncedLyricsWereChanged)
                        await Task.Run(() => TrashSidecarsAboveLrc(_track.FilePath));
                }
                else if (SyncedLyricsWereRemoved)
                {
                    if (File.Exists(lrcPath))
                        await Task.Run(() => TrashFile(lrcPath));
                    await Task.Run(() => TrashSidecarsAboveLrc(_track.FilePath));
                }
            }
            catch { /* Best effort — sidecar write is non-fatal */ }
        }

        // Write plain lyrics to sidecar .txt file.
        // Only write when the plain text actually changed: LoadFromTrack fills this
        // field from the *embedded* tag when no .txt exists, so an unconditional write
        // created a new file in the user's music folder after editing an unrelated field.
        if (!_albumScoped)
        {
            try
            {
                var txtPath = Path.ChangeExtension(_track.FilePath, ".txt");
                var plainChanged = !string.Equals(_track.Lyrics, _loadedPlainLyrics, StringComparison.Ordinal);
                if (!string.IsNullOrWhiteSpace(_track.Lyrics) && (plainChanged || File.Exists(txtPath)))
                    await File.WriteAllTextAsync(txtPath, _track.Lyrics);
                else if (PlainLyricsWereRemoved && File.Exists(txtPath))
                    await Task.Run(() => Helpers.RecycleBin.TryMoveToTrash(txtPath));
            }
            catch { /* Best effort — sidecar write is non-fatal */ }
        }

        // Handle artwork changes. Artwork is per-album in Noctis (the persisted
        // PNG is keyed by AlbumId and every track carries its own embedded copy),
        // so embed the new cover in every track of the album and refresh each
        // affected album's cached PNG. For a single-track edit we resolve the
        // album's tracks from the library — this mirrors the Remove path, so the
        // other tracks don't keep stale embedded art that a later load resurrects.
        if (_newArtworkData != null)
        {
            var artTargets = _albumTracks
                ?? _library.Tracks.Where(t => t.AlbumId == _track.AlbumId).ToList();
            if (artTargets.Count == 0) artTargets = new List<Track> { _track };

            foreach (var albumId in artTargets.Select(t => t.AlbumId).Distinct())
            {
                _persistence.SaveArtwork(albumId, _newArtworkData);
                ArtworkCache.Invalidate(_persistence.GetArtworkPath(albumId));
            }
            // A failed cover write (the playing track libVLC holds, a read-only file) leaves
            // the old cover in that file: report it like a failed tag write so the dialog
            // stays open, and keep that track's fingerprint matching what is still on disk.
            await Task.Run(() =>
            {
                var written = new List<Track>();
                foreach (var t in artTargets)
                {
                    bool ok;
                    try { ok = _metadata.WriteAlbumArt(t.FilePath, _newArtworkData); } catch { ok = false; }
                    if (ok) written.Add(t);
                    else AddFailedWrite(failedWrites, t.FilePath);
                    t.AlbumArtworkPath = null;
                }
                ClearOwnTrackArtwork(written, _newArtworkData);
            });
            foreach (var t in artTargets)
                ArtworkCache.Invalidate(_persistence.GetTrackArtworkPath(t.Id));
        }
        else if (_artworkRemoved)
        {
            // Artwork in this app is per-album: the persisted PNG is keyed by
            // AlbumId, and every track of that album carries its own embedded
            // copy in the audio tag. Clearing only _track.FilePath leaves the
            // other album tracks with intact embedded art, so any later load
            // path (LoadArtwork, library import) re-extracts and resurrects
            // the cover. Strip the tag from every track of the album.
            var albumTracks = _albumTracks
                ?? _library.Tracks.Where(t => t.AlbumId == _track.AlbumId).ToList();
            if (albumTracks.Count == 0) albumTracks = new List<Track> { _track };
            // Failed strips are reported and keep their fingerprint, as for a new cover above.
            await Task.Run(() =>
            {
                var written = new List<Track>();
                foreach (var t in albumTracks)
                {
                    bool ok;
                    try { ok = _metadata.WriteAlbumArt(t.FilePath, null); } catch { ok = false; }
                    if (ok) written.Add(t);
                    else AddFailedWrite(failedWrites, t.FilePath);
                    t.AlbumArtworkPath = null;
                }
                ClearOwnTrackArtwork(written, null);
            });
            foreach (var t in albumTracks)
                ArtworkCache.Invalidate(_persistence.GetTrackArtworkPath(t.Id));

            // Delete the persisted cache file too — invalidating the in-memory
            // cache alone leaves the PNG on disk, so the next album load reads
            // it back and the cover appears to "un-remove" itself.
            var artPath = _persistence.GetArtworkPath(_track.AlbumId);
            try { if (File.Exists(artPath)) File.Delete(artPath); } catch { }
            ArtworkCache.Invalidate(artPath);
        }
        else if (oldAlbumId != _track.AlbumId)
        {
            // AlbumId changed (artist/album edit) — copy cached artwork to new key.
            // Never onto the shared Unknown-Album bucket (blanking the Album field
            // re-keys the track there, and art under that id shows on every
            // untagged track in the library) — same rule as SaveArtwork.
            var oldPath = _persistence.GetArtworkPath(oldAlbumId);
            if (File.Exists(oldPath) && _track.AlbumId != Track.UnknownAlbumBucketId)
            {
                try { File.Copy(oldPath, _persistence.GetArtworkPath(_track.AlbumId), overwrite: true); }
                catch { /* Non-fatal */ }
            }
            ArtworkCache.Invalidate(oldPath);
            ArtworkCache.Invalidate(_persistence.GetArtworkPath(_track.AlbumId));
        }

        // Animated cover handling — scope follows how the dialog was opened:
        // album right-click → whole album; individual track right-click → that track only.
        var animScope = _albumScoped ? AnimatedCoverScope.Album : AnimatedCoverScope.Track;
        if (_newAnimatedCoverSource != null)
        {
            // Let the player release the old cover's file handle first — LibVLC keeps
            // the playing loop's file open and the overwrite/delete would fail silently.
            AnimatedCoverChanging?.Invoke(this, EventArgs.Empty);
            try { await _animatedCovers.ImportAsync(_track, _newAnimatedCoverSource, animScope); }
            catch { /* Non-fatal — preview still showed source */ }
        }
        else if (_animatedCoverRemoved)
        {
            AnimatedCoverChanging?.Invoke(this, EventArgs.Empty);
            await _animatedCovers.RemoveAsync(_track, animScope);
        }
        else if (oldAlbumId != _track.AlbumId)
        {
            foreach (var ext in new[] { ".mp4", ".webm" })
            {
                var oldP = _persistence.GetAnimatedCoverPath(oldAlbumId, null, ext);
                if (File.Exists(oldP))
                {
                    try { File.Move(oldP, _persistence.GetAnimatedCoverPath(_track.AlbumId, null, ext), true); }
                    catch { }
                }
            }
        }

        // Rename files by pattern (multi-select only). Done after tag writes so the
        // new name can reflect just-applied tags. The moves run on a worker thread for
        // the same reason as the tag writes: a large selection, or any selection on a
        // network share or busy disk, froze the window for the whole batch when inline.
        if (_multiSelect && ApplyRename && _albumTracks != null)
        {
            var renameSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var watcher = App.Services?.GetService<ILibraryWatcherService>();
            var renameTargets = _albumTracks.ToList();
            var renamePattern = RenamePattern;
            var moved = new List<(Track track, string oldPath, string newPath)>();
            await Task.Run(() =>
            {
                foreach (var t in renameTargets)
                {
                    var newPath = ComputeRenamedPath(t, out var conflict, renameSeen, renamePattern);
                    if (newPath != null && !conflict
                        && !string.Equals(newPath, t.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        var oldPath = t.FilePath;
                        try
                        {
                            SuppressWatcherForRename(watcher, oldPath, newPath);
                            File.Move(oldPath, newPath);
                            MoveLyricSidecars(oldPath, newPath);
                            moved.Add((t, oldPath, newPath));
                        }
                        catch (Exception ex)
                        {
                            // Non-fatal — skip this file
                            DebugLog.Write("Metadata", $"Rename failed for '{oldPath}': {ex.Message}");
                        }
                    }
                }
            });

            var renamed = new List<(string oldPath, string newPath)>();
            foreach (var (t, oldPath, newPath) in moved)
            {
                t.FilePath = newPath;
                renamed.Add((oldPath, newPath));
            }

            // A track's id is the hash of its path. Re-key the renamed tracks the way
            // Organize Files does, keeping play counts, favorites and store-backed lyrics;
            // with the old id the watcher/next scan imported each file as a new track.
            if (renamed.Count > 0)
            {
                var remap = await _library.RelocateTracksAsync(renamed);
                App.Services?.GetService<IPlayHistoryService>()?.RemapTrackIds(remap);
                if (App.Services?.GetService<MainWindowViewModel>() is { } main)
                    await main.Sidebar.ApplyTrackIdRemapAsync(remap);
            }
        }

        // Refresh bindings on the edited track instances immediately — views bound
        // directly to Track properties (genre/title columns, lyrics-page info line)
        // would otherwise show stale values until their next full rebuild.
        _track.NotifyMetadataUpdated();
        if (_albumTracks != null)
        {
            foreach (var t in _albumTracks)
            {
                if (!ReferenceEquals(t, _track))
                    t.NotifyMetadataUpdated();
            }
        }

        // Persist library changes and notify UI
        await _library.SaveAsync();

        // The editor can also change journaled user state (rating, disliked,
        // play-count reset). The journal wins over library.json on load, so those
        // rows must be refreshed too or stale values would resurrect next launch.
        var journalTracks = _albumTracks != null
            ? _albumTracks.ToList()
            : new List<Track> { _track };
        if (!journalTracks.Contains(_track)) journalTracks.Add(_track);
        await _library.SaveTrackUserStateAsync(journalTracks);

        _library.NotifyMetadataChanged();

        ChangesSaved?.Invoke(this, EventArgs.Empty);

        // Keep the dialog open when the tags did not actually reach disk, so the edit
        // isn't silently lost on the next rescan. The most common cause is the file
        // being held open by the player or another app.
        if (failedWrites.Count > 0)
        {
            SaveErrorMessage = failedWrites.Count == 1
                ? $"Couldn't write tags to \"{failedWrites[0]}\". The file may be in use — " +
                  "stop playback or close other apps using it, then try again."
                : $"Couldn't write tags to {failedWrites.Count} files (e.g. \"{failedWrites[0]}\"). " +
                  "They may be in use — stop playback or close other apps using them, then try again.";
            return;
        }

        SaveErrorMessage = string.Empty;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Non-empty when the last save could not write one or more files. Bound by
    /// MetadataWindow to an inline error strip; the dialog stays open so the user's
    /// edits survive and can be retried.
    /// </summary>
    [ObservableProperty] private string _saveErrorMessage = string.Empty;

    /// <summary>Drives the visibility of the inline save-error strip.</summary>
    public bool HasSaveError => !string.IsNullOrEmpty(SaveErrorMessage);

    partial void OnSaveErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasSaveError));

    // Every lyric sidecar the lyrics page and Lyrics Studio find by the song's basename;
    // .elrc and .lyricsfile (word timings) were missing, so a rename left them behind.
    private static readonly string[] RenamedSidecarExtensions = { ".lrc", ".elrc", ".lyricsfile", ".ttml", ".txt" };

    /// <summary>Moves a renamed track's same-basename lyric sidecars with it — lyrics
    /// resolve sidecar-first by basename, so leaving them behind detaches them.</summary>
    private static void MoveLyricSidecars(string oldPath, string newPath)
    {
        foreach (var ext in RenamedSidecarExtensions)
        {
            try
            {
                var oldSidecar = Path.ChangeExtension(oldPath, ext);
                var newSidecar = Path.ChangeExtension(newPath, ext);
                if (File.Exists(oldSidecar) && !File.Exists(newSidecar))
                    File.Move(oldSidecar, newSidecar);
            }
            catch { /* Best effort — the audio rename already succeeded */ }
        }
    }

    /// <summary>Makes the folder watcher ignore a rename the editor is about to do (audio
    /// file and its sidecars), as Organize Files does: it would otherwise record the old
    /// path as deleted and import the new one before the track is relocated.</summary>
    private static void SuppressWatcherForRename(ILibraryWatcherService? watcher, string oldPath, string newPath)
    {
        if (watcher == null) return;
        var paths = new List<string> { oldPath, newPath };
        foreach (var ext in RenamedSidecarExtensions)
        {
            paths.Add(Path.ChangeExtension(oldPath, ext));
            paths.Add(Path.ChangeExtension(newPath, ext));
        }
        watcher.SuppressPaths(paths, TimeSpan.FromSeconds(30));
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Start/stop time text. m:ss.fff alone drops the hours (TimeSpan's "m" is the minutes
    /// component), so a 1:05:00 stop time showed as "5:00.000" and any save cut it to 5 min.
    /// </summary>
    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.fff") : time.ToString(@"m\:ss\.fff");

    /// <summary>Parses a time string like "1:23.456", "12:34.567", or "1:02:03.456" to milliseconds.</summary>
    private static long ParseTimeToMs(string time)
    {
        if (string.IsNullOrWhiteSpace(time)) return 0;
        // Try exact formats first, then general TimeSpan.TryParse as fallback
        string[] formats = { @"m\:ss\.fff", @"mm\:ss\.fff", @"m\:ss", @"mm\:ss",
                             @"h\:mm\:ss\.fff", @"h\:mm\:ss" };
        foreach (var fmt in formats)
        {
            if (TimeSpan.TryParseExact(time, fmt, null, out var ts))
                return (long)ts.TotalMilliseconds;
        }
        // Bare numbers ("45", "12.5") are seconds. Without this, TimeSpan.TryParse
        // below would read "45" as 45 DAYS.
        if (double.TryParse(time, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var bareSeconds))
            return bareSeconds > 0 ? (long)(bareSeconds * 1000) : 0;
        if (TimeSpan.TryParse(time, out var fallback))
            return (long)fallback.TotalMilliseconds;
        return 0;
    }

    // Staged like every other edit in this dialog: applied on Save, discarded on
    // Cancel. Resetting the Track models directly here leaked the reset past a
    // Cancel (any later library save persisted it).
    private bool _resetPlayCountPending;

    [RelayCommand]
    private void ResetPlayCount()
    {
        _resetPlayCountPending = true;
        PlayCount = "0";
        OnPropertyChanged(nameof(PlayCountDisplay));
    }

    // ── Advanced Details ──

    /// <param name="mergeCustomTags">The user added custom tags while the load was in
    /// flight: keep them and put the file's tags in front, instead of replacing the list.</param>
    private void ApplyAdvancedFields(AdvancedTagIO.AdvancedFields fields, bool mergeCustomTags = false)
    {
        try
        {
            _originalAdvancedFields = fields;

            // Editable fields the user already typed into while loading keep the edit
            // (KeepsEdit is only ever true during InitializeAsync's apply).
            if (!KeepsEdit(nameof(TitleSort))) TitleSort = fields.TitleSort;
            if (!KeepsEdit(nameof(ArtistSort))) ArtistSort = fields.ArtistSort;
            if (!KeepsEdit(nameof(AlbumSort))) AlbumSort = fields.AlbumSort;
            if (!KeepsEdit(nameof(AlbumArtistSort))) AlbumArtistSort = fields.AlbumArtistSort;
            if (!KeepsEdit(nameof(ComposerSort))) ComposerSort = fields.ComposerSort;

            if (!KeepsEdit(nameof(Performer))) Performer = fields.Performer;
            if (!KeepsEdit(nameof(Conductor))) Conductor = fields.Conductor;
            if (!KeepsEdit(nameof(Lyricist))) Lyricist = fields.Lyricist;
            if (!KeepsEdit(nameof(Publisher))) Publisher = fields.Publisher;
            if (!KeepsEdit(nameof(EncodedBy))) EncodedBy = fields.EncodedBy;

            if (!KeepsEdit(nameof(Isrc))) Isrc = fields.Isrc;
            if (!KeepsEdit(nameof(CatalogNumber))) CatalogNumber = fields.CatalogNumber;
            if (!KeepsEdit(nameof(Barcode))) Barcode = fields.Barcode;

            if (!KeepsEdit(nameof(SelectedAdvisory)))
                SelectedAdvisory = fields.ItunesAdvisory switch { 1 => "Explicit", 2 => "Clean", _ => "None" };
            if (!KeepsEdit(nameof(Language))) Language = fields.Language;
            if (!KeepsEdit(nameof(Mood))) Mood = fields.Mood;
            if (!KeepsEdit(nameof(AdvDescription))) AdvDescription = fields.Description;
            if (!KeepsEdit(nameof(AdvReleaseDate))) AdvReleaseDate = fields.ReleaseDate;

            Encoder = fields.Encoder;
            ReplayGainTrackGain = fields.ReplayGainTrackGain;
            ReplayGainTrackPeak = fields.ReplayGainTrackPeak;
            ReplayGainAlbumGain = fields.ReplayGainAlbumGain;
            ReplayGainAlbumPeak = fields.ReplayGainAlbumPeak;

            if (!mergeCustomTags) CustomTags.Clear();
            var at = 0;
            foreach (var kv in fields.CustomTags)
            {
                // A key the user already added while loading keeps the user's value.
                if (mergeCustomTags && CustomTags.Skip(at).Any(t => string.Equals(t.Key, kv.Key, StringComparison.OrdinalIgnoreCase)))
                    continue;
                CustomTags.Insert(at++, new CustomTagItem { Key = kv.Key, Value = kv.Value });
            }
        }
        catch { /* Non-fatal — advanced fields are best-effort */ }
    }

    private AdvancedTagIO.AdvancedFields BuildAdvancedFields()
    {
        return new AdvancedTagIO.AdvancedFields
        {
            TitleSort = TitleSort,
            ArtistSort = ArtistSort,
            AlbumSort = AlbumSort,
            AlbumArtistSort = AlbumArtistSort,
            ComposerSort = ComposerSort,

            Performer = Performer,
            Conductor = Conductor,
            Lyricist = Lyricist,
            Publisher = Publisher,
            EncodedBy = EncodedBy,

            Isrc = Isrc,
            CatalogNumber = CatalogNumber,
            Barcode = Barcode,

            ItunesAdvisory = SelectedAdvisory switch { "Explicit" => 1, "Clean" => 2, _ => 0 },
            Language = Language,
            Mood = Mood,
            Description = AdvDescription,
            ReleaseDate = AdvReleaseDate,

            Encoder = Encoder,
            ReplayGainTrackGain = ReplayGainTrackGain,
            ReplayGainTrackPeak = ReplayGainTrackPeak,
            ReplayGainAlbumGain = ReplayGainAlbumGain,
            ReplayGainAlbumPeak = ReplayGainAlbumPeak,

            CustomTags = CustomTags
                .Where(t => !string.IsNullOrWhiteSpace(t.Key))
                .Select(t => new KeyValuePair<string, string>(t.Key, t.Value ?? string.Empty))
                .ToList()
        };
    }

    [RelayCommand]
    private void AddCustomTag()
    {
        CustomTags.Add(new CustomTagItem());
    }

    [RelayCommand]
    private void RemoveCustomTag(CustomTagItem? item)
    {
        if (item != null)
            CustomTags.Remove(item);
    }

    /// <summary>
    /// Applies the <see cref="ReleaseTypeOverride"/> string selection to a track.
    /// "Auto" clears the override and lets auto-detection take over on next scan.
    /// </summary>
    private void ApplyReleaseTypeOverride(Track t)
    {
        if (string.Equals(ReleaseTypeOverride, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            t.IsReleaseTypeOverridden = false;
            return;
        }
        if (Enum.TryParse<ReleaseType>(ReleaseTypeOverride, true, out var parsed))
        {
            t.ReleaseType = parsed;
            t.IsReleaseTypeOverridden = true;
            t.ReleaseTypeFromTag = true;
        }
    }
}

/// <summary>
/// Observable key-value pair for custom tag editing in the Advanced Details tab.
/// </summary>
/// <summary>Rail sections of the metadata editor that can carry unsaved edits.</summary>
public enum MetadataSection { Details, Advanced, Artwork, AnimatedArtwork, PlainLyrics, SyncedLyrics, Options }

public partial class CustomTagItem : ObservableObject
{
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _value = string.Empty;
}

public sealed class ArtworkSearchResult : IDisposable
{
    public ArtworkSearchResult(ITunesArtworkService.ArtworkCandidate candidate, Bitmap? thumbnail)
    {
        Candidate = candidate;
        Thumbnail = thumbnail;
    }

    public ITunesArtworkService.ArtworkCandidate Candidate { get; }
    public Bitmap? Thumbnail { get; }
    public string CollectionName => Candidate.CollectionName;
    public string ArtistName => Candidate.ArtistName;

    public void Dispose() => Thumbnail?.Dispose();
}

public sealed class AnimatedArtworkSearchResult
{
    public AnimatedArtworkSearchResult(ITunesArtworkService.AnimatedArtworkVariant variant)
    {
        Variant = variant;
    }

    public ITunesArtworkService.AnimatedArtworkVariant Variant { get; }
    public string Label => Variant.Label;
    public string Detail
        => Variant.Bandwidth > 0
            ? $"{Variant.Bandwidth / 1_000_000.0:0.#} Mbps"
            : string.Empty;
}
