using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Helpers;
using Noctis.Services;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>Sidebar Lyrics Studio page; the surface itself is <see cref="LyricsStudioPanel"/>.</summary>
public partial class LyricsStudioView : UserControl
{
    /// <summary>How many missing-format songs the picker lists before any search.</summary>
    private const int LyricsStudioPickerSuggestionCap = 80;

    public LyricsStudioView()
    {
        InitializeComponent();
        // Choose songs lives in the Studio's queue header (09-23); the panel bubbles the click here.
        AddHandler(LyricsStudioPanel.ChooseSongsRequestedEvent, (s, e) => OnChooseSongsClick(s, e));
    }

    /// <summary>Choose songs: search the library, tick songs or albums, pick ELRC or LRC, and
    /// the page runs the Studio over exactly those (user ask 09-19).</summary>
    private async void OnChooseSongsClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            if (DataContext is not LyricsStudioPageViewModel page) return;
            if (owner.DataContext is not MainWindowViewModel main) return;
            var library = App.Services?.GetService<ILibraryService>();
            if (library == null) return;

            var vm = new LyricsStudioPickerViewModel(library, main.Settings.GetSettings().LyricsStudioWordTimings,
                suggest: wordTimings => page.SuggestMissingAsync(wordTimings, LyricsStudioPickerSuggestionCap));
            vm.Confirmed += (_, pick) => page.UsePicked(pick.Tracks, pick.WordTimings);
            var dialog = new LyricsStudioPickerDialog { DataContext = vm };
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricsStudioView] Choose songs failed: {ex.Message}");
        }
    }

    /// <summary>Opens the Lyrics Background Video picker (the same dialog Settings used to host).</summary>
    private async void OnModifyLyricsBackgroundClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            if (owner.DataContext is not MainWindowViewModel main) return;
            var library = App.Services?.GetService<ILibraryService>();
            if (library == null) return;

            var ytDlp = App.Services?.GetService<Services.YouTube.YtDlpTool>();
            var ffmpeg = App.Services?.GetService<IAudioConverterService>();
            var dialog = new LyricsBackgroundPickerDialog
            {
                DataContext = new LyricsBackgroundPickerViewModel(main.Settings, library, ytDlp, () => ffmpeg?.GetFfmpegPath())
            };
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricsStudioView] Lyrics background picker failed: {ex.Message}");
        }
    }
}
