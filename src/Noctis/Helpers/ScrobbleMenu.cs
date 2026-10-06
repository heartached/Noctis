using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Helpers;

/// <summary>What a "don't scrobble" menu entry does when clicked: set or clear one key.</summary>
public sealed record ScrobbleToggle(string Key, bool Exclude);

/// <summary>
/// The "Don't Scrobble This Song / Album / Artist" menu entries (Discord aaron 2026-10-06).
/// One static command, like <see cref="LyricsBackgroundOverrides"/>, so every menu gets it
/// without per-view wiring. The header flips to "Scrobble … Again" once set, so each entry
/// re-binds whenever its menu opens. Hidden while neither Last.fm nor ListenBrainz
/// scrobbling is on. Keys: <see cref="ScrobbleExclusionKeys"/>.
/// </summary>
public static class ScrobbleMenu
{
    /// <summary>
    /// For menus declared in XAML: "album" or "artist". The entry binds itself to its
    /// DataContext (an Album, the album page's or the artist page's view model) each time
    /// the open menu attaches it. A ContextMenu's Opening event is too early for this: on
    /// the first open its DataContext binding has not resolved yet, so the album was null.
    /// </summary>
    public static readonly AttachedProperty<string?> EntryProperty =
        AvaloniaProperty.RegisterAttached<MenuItem, string?>("Entry", typeof(ScrobbleMenu));

    public static string? GetEntry(MenuItem item) => item.GetValue(EntryProperty);
    public static void SetEntry(MenuItem item, string? value) => item.SetValue(EntryProperty, value);

    public static ICommand ToggleCommand { get; } = new RelayCommand<object?>(p =>
    {
        if (p is ScrobbleToggle t) Settings?.SetScrobbleExcluded(t.Key, t.Exclude);
    });

    private static SettingsViewModel? Settings => App.Services?.GetService<MainWindowViewModel>()?.Settings;

    static ScrobbleMenu()
    {
        EntryProperty.Changed.AddClassHandler<MenuItem>((item, e) =>
        {
            item.AttachedToVisualTree -= OnEntryAttached;
            item.DataContextChanged -= OnEntryDataContextChanged;
            if (e.NewValue is string)
            {
                item.IsVisible = false;
                item.AttachedToVisualTree += OnEntryAttached;
                item.DataContextChanged += OnEntryDataContextChanged;
            }
        });
    }

    // Both: on one open the DataContext is already there when the entry attaches, on the
    // next it arrives just after (seen in the session log), so either can be the last word.
    private static void OnEntryAttached(object? sender, VisualTreeAttachmentEventArgs e) => Rebind(sender);
    private static void OnEntryDataContextChanged(object? sender, System.EventArgs e) => Rebind(sender);

    private static void Rebind(object? sender)
    {
        if (sender is not MenuItem item) return;
        switch (GetEntry(item), item.DataContext)
        {
            case ("album", Album album): BindAlbum(item, album); break;
            case ("album", AlbumDetailViewModel { Album: { } album }): BindAlbum(item, album); break;
            case ("artist", ArtistDetailViewModel vm) when !string.IsNullOrWhiteSpace(vm.ArtistName): BindArtist(item, vm.ArtistName); break;
            default: item.IsVisible = false; break;
        }
    }

    public static void BindTrack(MenuItem item, Track track)
    {
        var settings = Settings;
        item.Command = ToggleCommand;
        item.IsVisible = settings?.IsAnyScrobblingEnabled == true;
        if (settings == null) return;

        var key = ScrobbleExclusionKeys.ForTrack(track.Id);
        var own = settings.IsScrobbleExcluded(key);
        // Off through its album or an artist: the song's own entry can't turn it back on.
        var inherited = !own && settings.IsScrobbleExcluded(track);
        item.IsEnabled = !inherited;
        item.Header = Loc.T(inherited ? "Scrobble.OffByAlbumOrArtist" : own ? "Scrobble.ResumeSong" : "Scrobble.DontSong");
        item.CommandParameter = new ScrobbleToggle(key, !own);
    }

    public static void BindAlbum(MenuItem item, Album album)
        => Bind(item, ScrobbleExclusionKeys.ForAlbum(album.Id), "Scrobble.DontAlbum", "Scrobble.ResumeAlbum");

    public static void BindArtist(MenuItem item, string artistName)
        => Bind(item, ScrobbleExclusionKeys.ForArtist(artistName), "Scrobble.DontArtist", "Scrobble.ResumeArtist");

    private static void Bind(MenuItem item, string key, string dontKey, string resumeKey)
    {
        var settings = Settings;
        item.Command = ToggleCommand;
        item.IsVisible = settings?.IsAnyScrobblingEnabled == true;
        if (settings == null) return;

        var excluded = settings.IsScrobbleExcluded(key);
        item.Header = Loc.T(excluded ? resumeKey : dontKey);
        item.CommandParameter = new ScrobbleToggle(key, !excluded);
    }
}
