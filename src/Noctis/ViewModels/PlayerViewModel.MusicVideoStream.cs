using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.YouTube;

namespace Noctis.ViewModels;

/// <summary>
/// Music videos streamed from YouTube (Discord #feedback 11-25): when the song has no clip of
/// its own, the user's chosen video — or, with "Find music videos automatically" on, the best
/// search match — plays muted behind the lyrics, through the same synced layer as a local
/// music video (position, pause, speed, track change). A local clip next to the song always
/// wins; an automatic search also stays away from a song or album that has its own lyrics
/// background video. Everything runs off the UI thread and is dropped on a track change.
/// </summary>
public partial class PlayerViewModel
{
    private MusicVideoStreamService? _musicVideoStreams;
    private CancellationTokenSource? _streamCts;
    private int _streamGeneration;
    private MusicVideoStream? _stream;
    private bool _streamRefreshing;

    /// <summary>Set once at start-up (MainWindowViewModel); null keeps the feature inert.</summary>
    public MusicVideoStreamService? MusicVideoStreams
    {
        get => _musicVideoStreams;
        set
        {
            if (ReferenceEquals(_musicVideoStreams, value)) return;
            if (_musicVideoStreams != null) _musicVideoStreams.ChoiceChanged -= OnMusicVideoChoiceChanged;
            _musicVideoStreams = value;
            if (value != null) value.ChoiceChanged += OnMusicVideoChoiceChanged;
            OnPropertyChanged(nameof(CanPickMusicVideo));
            ResolveMusicVideoStream();
        }
    }

    /// <summary>Settings › Lyrics › Music videos › Find music videos automatically (off by default).</summary>
    [ObservableProperty] private bool _musicVideoAutoFindEnabled;

    /// <summary>The music video on screen is a YouTube stream (it plays behind the lyrics,
    /// and the page offers "Wrong video?").</summary>
    [ObservableProperty] private bool _isStreamingMusicVideo;

    /// <summary>The song can be given a YouTube music video (a local song, the feature wired).</summary>
    public bool CanPickMusicVideo => _musicVideoStreams != null && CurrentTrack is { IsRemoteStream: false };

    /// <summary>The last stream lookup (tests await it).</summary>
    internal Task MusicVideoStreamTask { get; private set; } = Task.CompletedTask;

    /// <summary>Clock for the stream URL's expiry (tests move it).</summary>
    internal Func<DateTime> StreamClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Opens the "Music Video from YouTube" dialog for a song (set by the app).</summary>
    public static Func<Track, Task>? OpenMusicVideoPicker { get; set; }

    partial void OnMusicVideoAutoFindEnabledChanged(bool value) => ResolveMusicVideoStream();

    partial void OnIsStreamingMusicVideoChanged(bool value) => RaiseMusicVideoPlacement();

    /// <summary>The island ⋯ menu's / the page's "Wrong video?": the dialog for the current song.</summary>
    [RelayCommand]
    private async Task PickMusicVideo()
    {
        if (CurrentTrack is { IsRemoteStream: false } track && OpenMusicVideoPicker is { } open)
        {
            try { await open(track); }
            catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.UI, "MusicVideo.PickerFailed", ex.Message); }
        }
    }

    private void OnMusicVideoChoiceChanged(object? sender, Guid trackId)
    {
        void Apply()
        {
            if (CurrentTrack?.Id == trackId) ResolveMusicVideoStream();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>A song with no local clip and no lyrics background of its own (song or album).</summary>
    private bool HasOwnLyricsBackground(Track track)
    {
        foreach (var key in new[] { Helpers.LyricsBackgroundOverrides.KeyForTrack(track), Helpers.LyricsBackgroundOverrides.KeyForAlbumId(track.AlbumId) })
            if (_lyricsBackgroundOverrides.TryGetValue(key, out var p) && !string.IsNullOrEmpty(p) && File.Exists(p))
                return true;
        return false;
    }

    /// <summary>
    /// Re-decides the stream for the current song (track change, setting, a choice in the
    /// dialog). Called after <see cref="ResolveMusicVideo"/> has set the local clip, if any.
    /// </summary>
    private void ResolveMusicVideoStream()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        var generation = ++_streamGeneration;
        var wasStreaming = IsStreamingMusicVideo;
        _stream = null;
        _streamRefreshing = false;
        IsStreamingMusicVideo = false;
        if (wasStreaming && _resolvedMusicVideoPath == null) CurrentMusicVideoPath = null;
        OnPropertyChanged(nameof(CanPickMusicVideo));

        var track = CurrentTrack;
        var service = _musicVideoStreams;
        if (track == null || track.IsRemoteStream || service == null || _resolvedMusicVideoPath != null)
        {
            MusicVideoStreamTask = Task.CompletedTask;
            return;
        }
        var autoFind = MusicVideoAutoFindEnabled && !HasOwnLyricsBackground(track);
        var cts = new CancellationTokenSource();
        _streamCts = cts;
        MusicVideoStreamTask = LoadMusicVideoStreamAsync(service, track, autoFind, generation, cts.Token);
    }

    private async Task LoadMusicVideoStreamAsync(MusicVideoStreamService service, Track track, bool autoFind, int generation, CancellationToken ct)
    {
        MusicVideoStream? stream;
        try
        {
            stream = await Task.Run(() => service.FindForTrackAsync(track, autoFind, ct), ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "MusicVideo.StreamFailed", ex.Message);
            return;
        }
        if (stream == null || ct.IsCancellationRequested || generation != _streamGeneration) return;
        ShowStream(stream);
    }

    private void ShowStream(MusicVideoStream stream)
    {
        _stream = stream;
        CurrentMusicVideoPath = stream.Url;
        IsStreamingMusicVideo = true;
        DebugLogger.Info(DebugLogger.Category.Playback, "MusicVideo.Stream",
            $"{stream.VideoId} user={stream.IsUserChoice} expires={stream.ExpiresUtc:HH:mm}Z");
    }

    /// <summary>
    /// Called with each position tick while a stream plays: shortly before its URL expires
    /// (a long pause, a song on repeat), it is resolved again and swapped in; the layer then
    /// lines the new stream up with the song.
    /// </summary>
    private void CheckMusicVideoStreamExpiry()
    {
        var stream = _stream;
        var service = _musicVideoStreams;
        if (stream == null || service == null || _streamRefreshing || !stream.NeedsRefresh(StreamClock())) return;
        _streamRefreshing = true;
        var generation = _streamGeneration;
        var ct = _streamCts?.Token ?? CancellationToken.None;
        MusicVideoStreamTask = RefreshMusicVideoStreamAsync(service, stream, generation, ct);
    }

    private async Task RefreshMusicVideoStreamAsync(MusicVideoStreamService service, MusicVideoStream stream, int generation, CancellationToken ct)
    {
        MusicVideoStream? fresh = null;
        try
        {
            fresh = await Task.Run(() => service.RefreshAsync(stream, ct), ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "MusicVideo.RefreshFailed", ex.Message);
        }
        if (generation != _streamGeneration) return;
        if (fresh == null)
        {
            // Retried on a later tick, not on every one: wait a minute before the next try.
            _stream = stream with { ExpiresUtc = StreamClock() + MusicVideoStream.RefreshMargin + TimeSpan.FromMinutes(1) };
            _streamRefreshing = false;
            return;
        }
        _streamRefreshing = false;
        ShowStream(fresh);
    }
}
