using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    // ── Tab selection ──
    [ObservableProperty] private int _selectedTabIndex;

    // ── Details tab ──
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _artist = string.Empty;
    [ObservableProperty] private string _albumArtist = string.Empty;
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

    // ── Artwork tab ──
    [ObservableProperty] private Bitmap? _artworkPreview;
    [ObservableProperty] private bool _hasArtwork;
    private byte[]? _newArtworkData;
    private bool _artworkRemoved;

    // ── Animated cover tab ──
    [ObservableProperty] private string? _animatedCoverPath;
    [ObservableProperty] private bool _hasAnimatedCover;
    [ObservableProperty] private bool _animatedCoverScopeIsAlbum = true;
    private string? _newAnimatedCoverSource;
    private bool _animatedCoverRemoved;

    // ── Lyrics tab ──
    [ObservableProperty] private string _lyrics = string.Empty;
    [ObservableProperty] private string _syncedLyrics = string.Empty;
    [ObservableProperty] private bool _hasCustomLyrics;
    [ObservableProperty] private bool _hasCustomSyncedLyrics;

    // ── Options tab ──
    [ObservableProperty] private bool _skipWhenShuffling;
    [ObservableProperty] private bool _rememberPlaybackPosition;
    [ObservableProperty] private string _mediaKind = "Music";
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
    public string HeaderTitle => _albumScoped && !string.IsNullOrWhiteSpace(_track.Album) ? _track.Album : _track.Title;
    public string HeaderArtist => _track.Artist;
    public string HeaderAlbum => _track.Album;
    public bool HeaderIsExplicit => _track.IsExplicit;
    public string HeaderAudioQualityBadge => _track.AudioQualityBadge;
    public string HeaderAudioQualityDetail => _track.AudioQualityDetailedInfo;

    public bool ShowLyricsTab => !_albumScoped;
    public bool ShowSyncedLyricsTab => !_albumScoped;
    public bool ShowFileTab => !_albumScoped;

    /// <summary>Formatted play count display with last played date.</summary>
    public string PlayCountDisplay
    {
        get
        {
            if (_track.LastPlayed.HasValue)
            {
                var lastPlayed = _track.LastPlayed.Value.ToLocalTime();
                return $"{_track.PlayCount} (Last Played {lastPlayed:M/d/yyyy, h:mm tt})";
            }
            return _track.PlayCount.ToString();
        }
    }

    /// <summary>Genre list bound to the genre ComboBox — includes the track's current genre if it's not in the built-in list.</summary>
    public List<string> GenreOptions { get; } = new();

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

    /// <summary>Available media kinds for the Options tab dropdown.</summary>
    public static string[] AvailableMediaKinds => Track.AvailableMediaKinds;

    /// <summary>EQ presets for the Options tab dropdown (None + all presets from Settings).</summary>
    public static readonly string[] OptionsEqPresets = BuildOptionsEqPresets();

    private static string[] BuildOptionsEqPresets()
    {
        var list = new List<string> { "None" };
        for (int i = 1; i < SettingsViewModel.EqPresetNames.Length; i++)
            list.Add(SettingsViewModel.EqPresetNames[i]);
        return list.ToArray();
    }

    /// <summary>Fires when the user clicks OK and changes were saved.</summary>
    public event EventHandler? ChangesSaved;

    /// <summary>Fires when the window should close.</summary>
    public event EventHandler? CloseRequested;

    public MetadataViewModel(Track track, IMetadataService metadata, ILibraryService library, IPersistenceService persistence, IAnimatedCoverService animatedCovers, bool albumScoped = false, List<Track>? albumTracks = null)
    {
        _track = track;
        _metadata = metadata;
        _library = library;
        _persistence = persistence;
        _animatedCovers = animatedCovers;
        _albumScoped = albumScoped;
        _albumTracks = albumTracks;

        LoadFromTrack();
        LoadFileInfo();
        LoadArtwork();
        LoadAnimatedCover();
        LoadAdvancedFields();
    }

    private void LoadAnimatedCover()
    {
        AnimatedCoverPath = _animatedCovers.Resolve(_track);
        HasAnimatedCover = !string.IsNullOrEmpty(AnimatedCoverPath);
    }

    private void LoadFromTrack()
    {
        // Details
        Title = _track.Title;
        Artist = _track.Artist;
        AlbumArtist = _track.AlbumArtist;
        Album = _track.Album;
        Genre = _track.Genre;
        GenreOptions.Clear();
        if (!string.IsNullOrWhiteSpace(_track.Genre) && !AvailableGenres.Contains(_track.Genre))
            GenreOptions.Add(_track.Genre);
        GenreOptions.AddRange(AvailableGenres);
        Composer = _track.Composer;
        TrackNumber = _track.TrackNumber > 0 ? _track.TrackNumber.ToString() : string.Empty;
        TrackCount = _track.TrackCount > 0 ? _track.TrackCount.ToString() : string.Empty;
        DiscNumber = _track.DiscNumber > 0 ? _track.DiscNumber.ToString() : string.Empty;
        DiscCount = _track.DiscCount > 0 ? _track.DiscCount.ToString() : string.Empty;
        Bpm = _track.Bpm > 0 ? _track.Bpm.ToString() : string.Empty;
        Year = _track.Year > 0 ? _track.Year.ToString() : string.Empty;
        IsCompilation = _track.IsCompilation;
        ShowComposerInAllViews = _track.ShowComposerInAllViews;
        Grouping = _track.Grouping;
        UseWorkAndMovement = _track.UseWorkAndMovement;
        WorkName = _track.WorkName;
        MovementName = _track.MovementName;
        MovementNumber = _track.MovementNumber > 0 ? _track.MovementNumber.ToString() : string.Empty;
        MovementCount = _track.MovementCount > 0 ? _track.MovementCount.ToString() : string.Empty;
        PlayCount = _track.PlayCount > 0 ? _track.PlayCount.ToString() : "0";
        Comment = _track.Comment;

        // Lyrics — synced from Track.SyncedLyrics or .lrc sidecar; plain from Track.Lyrics or .txt sidecar.
        // Plain tab must NEVER contain timestamps; if a legacy track stored synced text in Lyrics, strip it.
        var syncedFromTrack = _track.SyncedLyrics;
        if (string.IsNullOrWhiteSpace(syncedFromTrack))
        {
            try
            {
                var lrcPath = Path.ChangeExtension(_track.FilePath, ".lrc");
                if (File.Exists(lrcPath))
                    syncedFromTrack = File.ReadAllText(lrcPath);
            }
            catch { /* Non-fatal */ }
        }

        var plainFromTrack = _track.Lyrics;
        if (string.IsNullOrWhiteSpace(plainFromTrack))
        {
            try
            {
                var txtPath = Path.ChangeExtension(_track.FilePath, ".txt");
                if (File.Exists(txtPath))
                    plainFromTrack = File.ReadAllText(txtPath);
            }
            catch { /* Non-fatal */ }
        }

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

        SyncedLyrics = syncedFromTrack ?? string.Empty;
        Lyrics = plainFromTrack ?? string.Empty;
        HasCustomLyrics = !string.IsNullOrWhiteSpace(Lyrics);
        HasCustomSyncedLyrics = !string.IsNullOrWhiteSpace(SyncedLyrics);

        // Options
        SkipWhenShuffling = _track.SkipWhenShuffling;
        RememberPlaybackPosition = _track.RememberPlaybackPosition;

        // Options - extended
        MediaKind = string.IsNullOrEmpty(_track.MediaKind) ? "Music" : _track.MediaKind;
        HasStartTime = _track.StartTimeMs > 0;
        StartTime = _track.StartTimeMs > 0
            ? TimeSpan.FromMilliseconds(_track.StartTimeMs).ToString(@"m\:ss\.fff")
            : "0:00.000";
        HasStopTime = _track.StopTimeMs > 0;
        StopTime = _track.StopTimeMs > 0
            ? TimeSpan.FromMilliseconds(_track.StopTimeMs).ToString(@"m\:ss\.fff")
            : _track.Duration.ToString(@"m\:ss\.fff");
        VolumeAdjust = _track.VolumeAdjust;
        SelectedEqPreset = string.IsNullOrEmpty(_track.EqPreset) ? "None" : _track.EqPreset;
    }

    private void LoadFileInfo()
    {
        var info = _metadata.ReadFileInfo(_track.FilePath);
        if (info == null)
        {
            FileName = Path.GetFileName(_track.FilePath);
            FileLocation = _track.FilePath;
            FullFilePath = _track.FilePath;
            FolderName = Path.GetDirectoryName(_track.FilePath) ?? string.Empty;
            return;
        }

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
        Copyright = _track.Copyright;
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

    private void LoadArtwork()
    {
        var artPath = _persistence.GetArtworkPath(_track.AlbumId);

        byte[]? cachedData = null;
        if (File.Exists(artPath))
        {
            try { cachedData = File.ReadAllBytes(artPath); } catch { }
        }

        byte[]? extractedData = null;
        try { extractedData = _metadata.ExtractAlbumArt(_track.FilePath); } catch { }

        var preferredData = SelectPreferredArtworkData(cachedData, extractedData);
        if (preferredData == null || preferredData.Length == 0)
        {
            HasArtwork = false;
            return;
        }

        // If extraction found a better source than the cached file, refresh the on-disk cache.
        if (ReferenceEquals(preferredData, extractedData) &&
            extractedData != null && extractedData.Length > 0 &&
            (cachedData == null || extractedData.Length > cachedData.Length))
        {
            try { _persistence.SaveArtwork(_track.AlbumId, extractedData); } catch { }
        }

        try
        {
            using var ms = new MemoryStream(preferredData);
            ArtworkPreview = new Bitmap(ms);
            HasArtwork = true;
        }
        catch
        {
            HasArtwork = false;
        }
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
            ArtworkPreview = new Bitmap(ms);
            oldArt?.Dispose();
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
            Title = "Select Animated Cover",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video") { Patterns = new[] { "*.mp4", "*.webm" } }
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
        AnimatedCoverPath = null;
        HasAnimatedCover = false;
    }

    [RelayCommand]
    private void RemoveArtwork()
    {
        var oldArt = ArtworkPreview;
        ArtworkPreview = null;
        oldArt?.Dispose();
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

        var artPath = _persistence.GetArtworkPath(_track.AlbumId);
        if (File.Exists(artPath))
        {
            try { return File.ReadAllBytes(artPath); }
            catch { }
        }

        return _metadata.ExtractAlbumArt(_track.FilePath);
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

    [RelayCommand]
    private async Task Save()
    {
        // Apply Details changes to the track model
        _track.Title = Title;
        _track.Artist = Artist;
        _track.AlbumArtist = AlbumArtist;
        _track.Album = Album;
        _track.Genre = Genre;
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

        // Recalculate AlbumId if album or artist changed
        var oldAlbumId = _track.AlbumId;
        _track.AlbumId = Track.ComputeAlbumId(_track.AlbumArtist, _track.Album);

        // Apply Lyrics (plain + synced) — defensively strip timestamps from plain
        var plainToWrite = HasCustomLyrics
            ? (LyricsTextHelper.ContainsTimestamps(Lyrics) ? LyricsTextHelper.StripTimestamps(Lyrics) : Lyrics)
            : string.Empty;
        var syncedToWrite = HasCustomSyncedLyrics && LyricsTextHelper.ContainsTimestamps(SyncedLyrics)
            ? SyncedLyrics
            : string.Empty;
        _track.Lyrics = plainToWrite;
        _track.SyncedLyrics = syncedToWrite;

        // Apply Options
        _track.SkipWhenShuffling = SkipWhenShuffling;
        _track.RememberPlaybackPosition = RememberPlaybackPosition;
        _track.MediaKind = MediaKind;
        _track.StartTimeMs = HasStartTime ? ParseTimeToMs(StartTime) : 0;
        _track.StopTimeMs = HasStopTime ? ParseTimeToMs(StopTime) : 0;
        _track.VolumeAdjust = VolumeAdjust;
        _track.EqPreset = SelectedEqPreset == "None" ? string.Empty : SelectedEqPreset;

        // Fan out options to all album tracks when album-scoped
        if (_albumScoped && _albumTracks != null)
        {
            foreach (var t in _albumTracks)
            {
                t.SkipWhenShuffling = SkipWhenShuffling;
                t.RememberPlaybackPosition = RememberPlaybackPosition;
                t.MediaKind = MediaKind;
                t.StartTimeMs = HasStartTime ? ParseTimeToMs(StartTime) : 0;
                t.StopTimeMs = HasStopTime ? ParseTimeToMs(StopTime) : 0;
                t.VolumeAdjust = VolumeAdjust;
                t.EqPreset = SelectedEqPreset == "None" ? string.Empty : SelectedEqPreset;
            }
        }

        // Write metadata to file tags (plain lyrics go to USLT tag)
        _metadata.WriteTrackMetadata(_track);

        // Write advanced fields (sort, people, identifiers, custom tags)
        if (!_albumScoped && _originalAdvancedFields != null)
        {
            var advFields = BuildAdvancedFields();
            AdvancedTagIO.WriteAll(_track.FilePath, advFields, _originalAdvancedFields);

            // Sync advisory → IsExplicit for immediate badge update
            _track.IsExplicit = advFields.ItunesAdvisory == 1;
        }

        // Write synced lyrics to sidecar .lrc file
        try
        {
            var lrcPath = Path.ChangeExtension(_track.FilePath, ".lrc");
            if (!string.IsNullOrWhiteSpace(_track.SyncedLyrics))
                await File.WriteAllTextAsync(lrcPath, _track.SyncedLyrics);
            else if (File.Exists(lrcPath))
                File.Delete(lrcPath);
        }
        catch { /* Best effort — sidecar write is non-fatal */ }

        // Write plain lyrics to sidecar .txt file
        try
        {
            var txtPath = Path.ChangeExtension(_track.FilePath, ".txt");
            if (!string.IsNullOrWhiteSpace(_track.Lyrics))
                await File.WriteAllTextAsync(txtPath, _track.Lyrics);
            else if (File.Exists(txtPath))
                File.Delete(txtPath);
        }
        catch { /* Best effort — sidecar write is non-fatal */ }

        // Handle artwork changes
        if (_newArtworkData != null)
        {
            _metadata.WriteAlbumArt(_track.FilePath, _newArtworkData);
            _persistence.SaveArtwork(_track.AlbumId, _newArtworkData);
            ArtworkCache.Invalidate(_persistence.GetArtworkPath(_track.AlbumId));
        }
        else if (_artworkRemoved)
        {
            _metadata.WriteAlbumArt(_track.FilePath, null);
            ArtworkCache.Invalidate(_persistence.GetArtworkPath(_track.AlbumId));
        }
        else if (oldAlbumId != _track.AlbumId)
        {
            // AlbumId changed (artist/album edit) — copy cached artwork to new key
            var oldPath = _persistence.GetArtworkPath(oldAlbumId);
            if (File.Exists(oldPath))
            {
                try { File.Copy(oldPath, _persistence.GetArtworkPath(_track.AlbumId), overwrite: true); }
                catch { /* Non-fatal */ }
            }
            ArtworkCache.Invalidate(oldPath);
            ArtworkCache.Invalidate(_persistence.GetArtworkPath(_track.AlbumId));
        }

        // Animated cover handling
        var animScope = AnimatedCoverScopeIsAlbum ? AnimatedCoverScope.Album : AnimatedCoverScope.Track;
        if (_newAnimatedCoverSource != null)
        {
            try { await _animatedCovers.ImportAsync(_track, _newAnimatedCoverSource, animScope); }
            catch { /* Non-fatal — preview still showed source */ }
        }
        else if (_animatedCoverRemoved)
        {
            _animatedCovers.Remove(_track, animScope);
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

        // Persist library changes and notify UI
        await _library.SaveAsync();
        _library.NotifyMetadataChanged();

        ChangesSaved?.Invoke(this, EventArgs.Empty);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

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
        if (TimeSpan.TryParse(time, out var fallback))
            return (long)fallback.TotalMilliseconds;
        return 0;
    }

    [RelayCommand]
    private void ResetPlayCount()
    {
        PlayCount = "0";
        _track.PlayCount = 0;
        _track.LastPlayed = null;
        OnPropertyChanged(nameof(PlayCountDisplay));
    }

    // ── Advanced Details ──

    private void LoadAdvancedFields()
    {
        if (_albumScoped) return;
        try
        {
            var fields = AdvancedTagIO.ReadAll(_track.FilePath);
            _originalAdvancedFields = fields;

            TitleSort = fields.TitleSort;
            ArtistSort = fields.ArtistSort;
            AlbumSort = fields.AlbumSort;
            AlbumArtistSort = fields.AlbumArtistSort;
            ComposerSort = fields.ComposerSort;

            Conductor = fields.Conductor;
            Lyricist = fields.Lyricist;
            Publisher = fields.Publisher;
            EncodedBy = fields.EncodedBy;

            Isrc = fields.Isrc;
            CatalogNumber = fields.CatalogNumber;
            Barcode = fields.Barcode;

            SelectedAdvisory = fields.ItunesAdvisory switch { 1 => "Explicit", 2 => "Clean", _ => "None" };
            Language = fields.Language;
            Mood = fields.Mood;
            AdvDescription = fields.Description;
            AdvReleaseDate = fields.ReleaseDate;

            Encoder = fields.Encoder;
            ReplayGainTrackGain = fields.ReplayGainTrackGain;
            ReplayGainTrackPeak = fields.ReplayGainTrackPeak;
            ReplayGainAlbumGain = fields.ReplayGainAlbumGain;
            ReplayGainAlbumPeak = fields.ReplayGainAlbumPeak;

            CustomTags.Clear();
            foreach (var kv in fields.CustomTags)
                CustomTags.Add(new CustomTagItem { Key = kv.Key, Value = kv.Value });
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
}

/// <summary>
/// Observable key-value pair for custom tag editing in the Advanced Details tab.
/// </summary>
public partial class CustomTagItem : ObservableObject
{
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _value = string.Empty;
}
