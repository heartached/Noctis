using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>Available genres for the genre dropdown.</summary>
    public static readonly string[] AvailableGenres = new[]
    {
        "Afrobeats", "Alternative", "Baile Funk", "Blues/R&B", "Books & Spoken",
        "Children's Music", "Christian", "Classical", "Comedy", "Country",
        "Dance", "Easy Listening", "Electronic", "Folk", "Hip Hop/Rap",
        "Hip-Hop", "Holiday", "House", "Indie Pop", "Industrial",
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

    public MetadataViewModel(Track track, IMetadataService metadata, ILibraryService library, IPersistenceService persistence, bool albumScoped = false, List<Track>? albumTracks = null)
    {
        _track = track;
        _metadata = metadata;
        _library = library;
        _persistence = persistence;
        _albumScoped = albumScoped;
        _albumTracks = albumTracks;

        LoadFromTrack();
        LoadFileInfo();
        LoadArtwork();
    }

    private void LoadFromTrack()
    {
        // Details
        Title = _track.Title;
        Artist = _track.Artist;
        AlbumArtist = _track.AlbumArtist;
        Album = _track.Album;
        Genre = _track.Genre;
        Composer = _track.Composer;
        TrackNumber = _track.TrackNumber > 0 ? _track.TrackNumber.ToString() : string.Empty;
        TrackCount = _track.TrackCount > 0 ? _track.TrackCount.ToString() : string.Empty;
        DiscNumber = _track.DiscNumber > 0 ? _track.DiscNumber.ToString() : string.Empty;
        DiscCount = _track.DiscCount > 0 ? _track.DiscCount.ToString() : string.Empty;
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

        // Lyrics — load synced from Track or sidecar .lrc file, migrate legacy data
        var syncedFromTrack = _track.SyncedLyrics;
        if (string.IsNullOrWhiteSpace(syncedFromTrack))
        {
            // Try sidecar .lrc file
            try
            {
                var lrcPath = Path.ChangeExtension(_track.FilePath, ".lrc");
                if (File.Exists(lrcPath))
                    syncedFromTrack = File.ReadAllText(lrcPath);
            }
            catch { /* Non-fatal */ }
        }

        if (!string.IsNullOrWhiteSpace(syncedFromTrack))
        {
            SyncedLyrics = syncedFromTrack;
            Lyrics = _track.Lyrics;
        }
        else if (!string.IsNullOrWhiteSpace(_track.Lyrics)
                 && _track.Lyrics.Contains("[")
                 && System.Text.RegularExpressions.Regex.IsMatch(_track.Lyrics, @"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]"))
        {
            // Legacy: synced lyrics stored in Lyrics field — move to SyncedLyrics
            SyncedLyrics = _track.Lyrics;
            Lyrics = string.Empty;
        }
        else
        {
            Lyrics = _track.Lyrics;
            SyncedLyrics = string.Empty;
        }
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
            return;
        }

        FileName = info.FileName;
        FileFormat = info.FileFormat;
        Codec = info.Codec;
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

        // Recalculate AlbumId if album or artist changed
        var oldAlbumId = _track.AlbumId;
        _track.AlbumId = Track.ComputeAlbumId(_track.AlbumArtist, _track.Album);

        // Apply Lyrics (plain + synced)
        _track.Lyrics = HasCustomLyrics ? Lyrics : string.Empty;
        _track.SyncedLyrics = HasCustomSyncedLyrics ? SyncedLyrics : string.Empty;

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

    /// <summary>Parses a time string like "1:23.456" or "0:05.000" to milliseconds.</summary>
    private static long ParseTimeToMs(string time)
    {
        if (string.IsNullOrWhiteSpace(time)) return 0;
        if (TimeSpan.TryParseExact(time, @"m\:ss\.fff", null, out var ts))
            return (long)ts.TotalMilliseconds;
        if (TimeSpan.TryParseExact(time, @"m\:ss", null, out var ts2))
            return (long)ts2.TotalMilliseconds;
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
}
