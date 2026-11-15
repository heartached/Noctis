using System;
using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Helpers;

/// <summary>
/// Per-album carousel style (GitHub #233): an album can wear its own case (Cover / CD Jewel
/// Case / Vinyl / Cassette) whatever the global carousel style is, chosen from the album menu's
/// Carousel Style ▸ and kept in settings keyed by the album's stable id (artist + title, so it
/// survives rescans). Also hands both carousels the "Glow on the selected album" switch. The
/// command is static so every album menu gets it without wiring a command per view (same idea
/// as <see cref="LyricsBackgroundOverrides"/>).
/// </summary>
public static class AlbumCarouselStyleOverrides
{
    /// <summary>A Carousel Style ▸ row's parameter: the album and its pick (null = Default,
    /// follow the global style).</summary>
    public sealed record Choice(Album Album, AlbumCarouselStyle? Style);

    /// <summary>What the carousels need from settings, read once per change.</summary>
    public sealed record Snapshot(IReadOnlyDictionary<Guid, AlbumCarouselStyle> Overrides, bool Glow);

    public static ICommand SetCommand { get; } =
        new RelayCommand<object?>(p => { if (p is Choice c) Settings?.SetAlbumCarouselStyleOverride(c.Album.Id, c.Style); });

    /// <summary>Raised (UI thread) when an album's style or the glow switch changes.</summary>
    public static event EventHandler? Changed;

    internal static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);

    /// <summary>The album's own style, or null when it follows the global one.</summary>
    public static AlbumCarouselStyle? For(Album album) => Settings?.GetAlbumCarouselStyleOverride(album.Id);

    /// <summary>The current overrides and glow switch; null without the app's settings (tests).</summary>
    public static Snapshot? Current => Settings is { } s
        ? new Snapshot(s.AlbumCarouselStyleOverrideMap(), s.AlbumCarouselGlow)
        : null;

    private static SettingsViewModel? Settings => App.Services?.GetService<MainWindowViewModel>()?.Settings;
}
