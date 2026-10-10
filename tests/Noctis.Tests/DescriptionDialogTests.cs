using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: the album and playlist "edit description" pop-ups redone as rounded pill
/// pop-ups (blurred app behind, compact header with cover + fact chips, a filled editor with
/// a live count, Cancel / Save in the footer, Ctrl+Enter saves, Esc cancels).
/// </summary>
public class DescriptionDialogTests
{
    private static readonly Color PillFill = Color.Parse("#1CFFFFFF");

    /// <summary>Last.fm stand-in whose description lookup finishes when the test says so.</summary>
    private sealed class SlowLastFm : ILastFmService
    {
        public readonly TaskCompletionSource<string?> Summary = new();
        public readonly TaskCompletionSource<string?> Full = new();
        public readonly List<string?> Overrides = new();
        public bool IsAuthenticated => false;
        public string? Username => null;
        public void Configure(string? sessionKey) { }
        public Task<string> GetAuthUrlAsync() => Task.FromResult(string.Empty);
        public Task<bool> CompleteAuthAsync() => Task.FromResult(false);
        public string? GetSessionKey() => null;
        public void Logout() { }
        public Task ScrobbleAsync(Track track, DateTime startedAt) => Task.CompletedTask;
        public Task UpdateNowPlayingAsync(Track track) => Task.CompletedTask;
        public Task<string?> GetAlbumDescriptionAsync(string a, string b, CancellationToken ct = default) => Summary.Task;
        public Task<string?> GetAlbumDescriptionFullAsync(string a, string b, CancellationToken ct = default) => Full.Task;
        public Task SetAlbumDescriptionOverrideAsync(string a, string b, string? d, CancellationToken ct = default)
        {
            Overrides.Add(d);
            return Task.CompletedTask;
        }
        public Task ClearAlbumDescriptionOverrideAsync(string a, string b, CancellationToken ct = default) => Task.CompletedTask;

        public static SlowLastFm Loaded(string? text)
        {
            var lastFm = new SlowLastFm();
            lastFm.Summary.SetResult(text);
            lastFm.Full.SetResult(text);
            return lastFm;
        }
    }

    private sealed class CountingPersistence : TestPersistenceService
    {
        public int PlaylistSaves;
        public override Task SavePlaylistsAsync(List<Playlist> playlists)
        {
            PlaylistSaves++;
            return Task.CompletedTask;
        }
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    private static Album NewAlbum(string name = "Hollow") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Artist = "Noctis Test",
        Year = 2025,
        Genre = "Hip-Hop/Rap",
        TrackCount = 1,
        Tracks = new List<Track>(),
    };

    private static AlbumDetailViewModel AlbumPage(SlowLastFm lastFm, Album? album = null, TestPersistenceService? persistence = null)
    {
        var lib = new FakeLibraryService();
        persistence ??= new TestPersistenceService();
        album ??= NewAlbum();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), lastFm);
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    private static (PlaylistViewModel vm, Playlist playlist, CountingPersistence persistence) PlaylistPage(string description = "")
    {
        var lib = new FakeLibraryService();
        var persistence = new CountingPersistence();
        var playlist = new Playlist
        {
            Name = "Late Nights",
            Description = description,
            ModifiedAt = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new PlaylistViewModel(playlist, player, lib, persistence, new SidebarViewModel(persistence, lib));
        Dispatcher.UIThread.RunJobs();
        return (vm, playlist, persistence);
    }

    /// <summary>Opens the album pop-up the way OpenAlbumDescription / EditAlbumDescription do.</summary>
    private static (AlbumDescriptionDialog win, PillDialogHost host) OpenAlbumDialog(AlbumDetailViewModel vm, bool forEditing)
    {
        vm.AlbumDescriptionEditorText = vm.AlbumDescriptionDialogText;
        vm.IsAlbumDescriptionEditing = forEditing;
        var win = new AlbumDescriptionDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        return (win, host);
    }

    private static (PlaylistDescriptionDialog win, PillDialogHost host) OpenPlaylistDialog(PlaylistViewModel vm, bool forEditing)
    {
        vm.DescriptionEditorText = forEditing && !vm.HasDescription ? string.Empty : vm.PlaylistDescription;
        vm.IsDescriptionEditing = forEditing;
        var win = new PlaylistDescriptionDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        return (win, host);
    }

    private static void CloseNow(Window win)
    {
        if (!win.IsVisible) return;
        win.Close();
        PumpUntil(() => !win.IsVisible);
    }

    // ── View-model fixes ────────────────────────────────────────────────

    /// <summary>
    /// Bug (found 10-08): "Edit Description" in the album menu is there before the Last.fm
    /// lookup lands. The editor opened empty (EditAlbumDescription copies the still-empty text),
    /// then the lookup filled AlbumDescription but left the editor alone because an edit was
    /// open — so the untouched, empty editor counted as a change, Save lit up, and saving it
    /// wrote an empty override that erased the album's description for good.
    /// </summary>
    [AvaloniaFact]
    public void Album_EditorOpenedBeforeLookupLands_PicksUpTheText_AndHasNoChanges()
    {
        var lastFm = new SlowLastFm();
        var vm = AlbumPage(lastFm);

        // What EditAlbumDescription does before showing the dialog (lookup still pending).
        vm.AlbumDescriptionEditorText = vm.AlbumDescriptionDialogText;
        vm.IsAlbumDescriptionEditing = true;
        Assert.Equal(string.Empty, vm.AlbumDescriptionEditorText);

        lastFm.Summary.SetResult("Short text.");
        lastFm.Full.SetResult("The full story of the album.");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("The full story of the album.", vm.AlbumDescriptionFull);
        Assert.Equal("The full story of the album.", vm.AlbumDescriptionEditorText);
        Assert.False(vm.HasAlbumDescriptionChanges);
        Assert.False(vm.SaveAlbumDescriptionEditCommand.CanExecute(null));
    }

    /// <summary>What the user already typed is never replaced by a lookup that lands later.</summary>
    [AvaloniaFact]
    public void Album_LookupLandingAfterTyping_KeepsTheTypedText()
    {
        var lastFm = new SlowLastFm();
        var vm = AlbumPage(lastFm);
        vm.AlbumDescriptionEditorText = string.Empty;
        vm.IsAlbumDescriptionEditing = true;
        vm.AlbumDescriptionEditorText = "My own words";

        lastFm.Summary.SetResult("Short text.");
        lastFm.Full.SetResult("The full story of the album.");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("My own words", vm.AlbumDescriptionEditorText);
        Assert.True(vm.HasAlbumDescriptionChanges);
    }

    /// <summary>Save runs only for a real change (Ctrl+Enter reaches the command without the
    /// button's disabled state), stores the trimmed text, refreshes the page and says so once.</summary>
    [AvaloniaFact]
    public async Task Album_Save_OnlyWhenChanged_Trimmed_RaisesSavedOnce()
    {
        var lastFm = SlowLastFm.Loaded("Old text.");
        var vm = AlbumPage(lastFm);
        var saved = 0;
        vm.AlbumDescriptionSaved += (_, _) => saved++;
        Assert.Equal("Old text.", vm.AlbumDescriptionEditorText);

        // Unchanged (whitespace only differs): nothing is written.
        vm.AlbumDescriptionEditorText = "  Old text.  ";
        Assert.False(vm.SaveAlbumDescriptionEditCommand.CanExecute(null));
        await vm.SaveAlbumDescriptionEditCommand.ExecuteAsync(null);
        Assert.Empty(lastFm.Overrides);
        Assert.Equal(0, saved);

        vm.AlbumDescriptionEditorText = "  New text\nsecond line \n";
        Assert.True(vm.SaveAlbumDescriptionEditCommand.CanExecute(null));
        await vm.SaveAlbumDescriptionEditCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "New text\nsecond line" }, lastFm.Overrides);
        Assert.Equal("New text\nsecond line", vm.AlbumDescription);
        Assert.Equal("New text\nsecond line", vm.AlbumDescriptionEditorText);
        Assert.Equal(1, saved);

        // A second run (double click, Ctrl+Enter during the close) has nothing left to save.
        Assert.False(vm.SaveAlbumDescriptionEditCommand.CanExecute(null));
        await vm.SaveAlbumDescriptionEditCommand.ExecuteAsync(null);
        Assert.Single(lastFm.Overrides);
        Assert.Equal(1, saved);
    }

    /// <summary>
    /// Bug (found 10-08): saving a playlist description stamps ModifiedAt but never told the
    /// page, so the header's "Updated …" line (PlaylistView.axaml binds ModifiedDateDisplay)
    /// kept the old date until the page was rebuilt. Renaming does raise it.
    /// </summary>
    [AvaloniaFact]
    public async Task Playlist_Save_RefreshesTheUpdatedDate()
    {
        var (vm, playlist, _) = PlaylistPage("Old");
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var before = vm.ModifiedDateDisplay;

        vm.DescriptionEditorText = "New";
        await vm.SaveDescriptionEditCommand.ExecuteAsync(null);

        Assert.True(playlist.ModifiedAt > new DateTime(2025, 1, 1));
        Assert.NotEqual(before, vm.ModifiedDateDisplay);
        Assert.Contains(nameof(PlaylistViewModel.ModifiedDateDisplay), raised);
    }

    [AvaloniaFact]
    public async Task Playlist_Save_OnlyWhenChanged_TrimmedAndPersistedOnce()
    {
        var (vm, playlist, persistence) = PlaylistPage("Old");
        var saved = 0;
        vm.DescriptionSaved += (_, _) => saved++;

        vm.DescriptionEditorText = " Old ";
        Assert.False(vm.SaveDescriptionEditCommand.CanExecute(null));
        await vm.SaveDescriptionEditCommand.ExecuteAsync(null);
        Assert.Equal(0, persistence.PlaylistSaves);
        Assert.Equal(new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc), playlist.ModifiedAt);

        vm.DescriptionEditorText = "  Rainy drives \n";
        await vm.SaveDescriptionEditCommand.ExecuteAsync(null);
        await vm.SaveDescriptionEditCommand.ExecuteAsync(null);
        Assert.Equal("Rainy drives", playlist.Description);
        Assert.Equal("Rainy drives", vm.PlaylistDescription);
        Assert.Equal(1, persistence.PlaylistSaves);
        Assert.Equal(1, saved);
    }

    // ── Shared helpers ──────────────────────────────────────────────────

    [AvaloniaFact]
    public void Count_IsWhatSaveWouldStore_SingularAndThousands()
    {
        Assert.Equal("No description", DescriptionDialogs.FormatCount("   ", removesOnSave: false));
        Assert.Equal("Saving removes the description", DescriptionDialogs.FormatCount("", removesOnSave: true));
        Assert.Equal("1 character · 1 word", DescriptionDialogs.FormatCount(" a ", removesOnSave: false));
        Assert.Equal("11 characters · 2 words", DescriptionDialogs.FormatCount("hello\nworld", removesOnSave: false));
        // 200 × 5 letters + 199 spaces; the trailing spaces are not saved, so not counted.
        Assert.Equal("1,199 characters · 200 words",
            DescriptionDialogs.FormatCount(string.Join(" ", Enumerable.Repeat("abcde", 200)) + "     ", removesOnSave: false));
        // An emoji is one character to the reader.
        Assert.Equal("2 characters · 1 word", DescriptionDialogs.FormatCount("a\U0001F3B5", removesOnSave: false));
    }

    [AvaloniaFact]
    public void AlbumChips_YearGenreSongsQualityLabel_OnlyWhenKnown()
    {
        var album = NewAlbum();
        Assert.Equal(new[] { "2025", "Hip-Hop/Rap", "1 song" }, DescriptionDialogs.AlbumChips(album));

        album.Year = 0;
        album.Genre = "Unknown";
        album.TrackCount = 1200;
        Assert.Equal(new[] { "1,200 songs" }, DescriptionDialogs.AlbumChips(album));
    }

    // ── The pop-ups ─────────────────────────────────────────────────────

    [AvaloniaFact]
    public void AlbumDialog_IsAPillPopUp_CompactHeader_FilledEditor_NoCloseX()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var album = NewAlbum("False Prophet (feat. 6ix9ine) - Single, an extra long title that would wrap");
            var vm = AlbumPage(SlowLastFm.Loaded("A short story."), album);
            var (win, host) = OpenAlbumDialog(vm, forEditing: false);
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                Assert.True(host.BlurBackdrop);
                Assert.Equal(new CornerRadius(30), host.CornerRadius);
                var all = win.GetVisualDescendants().ToList();

                // Title: one line, trimmed, full text in the tooltip, a sensible size.
                var title = all.OfType<TextBlock>().Single(t => t.Name == "TitleText");
                Assert.Equal(TextWrapping.NoWrap, title.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
                Assert.Equal(album.Name, ToolTip.GetTip(title));
                Assert.Equal(20, title.FontSize);
                Assert.True(title.Bounds.Height < 40, $"title should be one line, was {title.Bounds.Height}px tall");

                // Facts as chips.
                var chips = all.OfType<ItemsControl>().Single(i => i.Name == "FactChips");
                Assert.Equal(new[] { "2025", "Hip-Hop/Rap", "1 song" }, chips.ItemsSource!.Cast<string>());

                // The editor: a filled pill area (radius 20), no outline at rest, the text in it.
                var editor = all.OfType<TextBox>().Single(t => t.Name == "DescriptionEditor");
                Assert.Contains("pill-area", editor.Classes);
                Assert.True(editor.AcceptsReturn);
                Assert.Equal(TextWrapping.Wrap, editor.TextWrapping);
                Assert.Equal("A short story.", editor.Text);
                Assert.False(editor.IsFocused); // opened to read: no caret until clicked
                var chrome = editor.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                Assert.Equal(new CornerRadius(20), chrome.CornerRadius);
                Assert.Equal(PillFill, AccentTestHarness.ColorOf(chrome.Background));
                Assert.Equal(0, AccentTestHarness.ColorOf(chrome.BorderBrush).A);

                // Footer: count, Clear, quiet Cancel, solid accent Save (off until a change).
                Assert.Equal("14 characters · 3 words", all.OfType<TextBlock>().Single(t => t.Name == "CountText").Text);
                var save = all.OfType<Button>().Single(b => b.Name == "SaveButton");
                PillDialogHostTests.AssertSolidAccent(save);
                Assert.False(save.IsEffectivelyEnabled);
                var cancel = all.OfType<Button>().Single(b => b.Name == "CancelButton");
                Assert.Contains("pill-secondary", cancel.Classes);
                // Like the other pill pop-ups, the footer is the only way out: no round X.
                Assert.Equal(new[] { "CancelButton", "ClearButton", "SaveButton" },
                    all.OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Name).OrderBy(n => n));

                editor.Text = "A short story. More.";
                PumpUntil(() => false, 20);
                Assert.True(save.IsEffectivelyEnabled);
                Assert.Equal("20 characters · 4 words", all.OfType<TextBlock>().Single(t => t.Name == "CountText").Text);
            }
            finally { CloseNow(win); }
        });
    }

    [AvaloniaFact]
    public void BothDialogs_ScrollBar_SitsAtThePillsRightEdge_TextKeepsItsInset()
    {
        // Owner 10-09: the editor's scroll bar sat 18px inside the pill (the template's Padding
        // wraps the ScrollViewer); it now hugs the pill's right edge while the text keeps 18px.
        EnsureAppStyles();
        var longText = string.Join("\n\n", Enumerable.Repeat("A long paragraph that wraps across the editor several times over.", 20));

        var albumVm = AlbumPage(SlowLastFm.Loaded(longText), NewAlbum());
        var (albumWin, albumHost) = OpenAlbumDialog(albumVm, forEditing: false);
        try { AssertEdgeScrollBar(albumWin, albumHost); }
        finally { CloseNow(albumWin); }

        var (playlistVm, _, _) = PlaylistPage(longText);
        var (playlistWin, playlistHost) = OpenPlaylistDialog(playlistVm, forEditing: false);
        try { AssertEdgeScrollBar(playlistWin, playlistHost); }
        finally { CloseNow(playlistWin); }
    }

    private static void AssertEdgeScrollBar(Window win, PillDialogHost host)
    {
        Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
        var editor = win.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "DescriptionEditor");
        var parts = editor.GetVisualDescendants().ToList();
        var chrome = parts.OfType<Border>().First(b => b.Name == "PART_BorderElement");
        var bar = parts.OfType<Avalonia.Controls.Primitives.ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        var text = parts.OfType<Avalonia.Controls.Presenters.TextPresenter>().Single(t => t.Name == "PART_TextPresenter");
        Assert.True(bar.IsVisible, "long text should need the scroll bar");

        double RightGap(Visual v) => chrome.Bounds.Width - v.TranslatePoint(new Point(v.Bounds.Width, 0), chrome)!.Value.X;
        Assert.InRange(RightGap(bar), 0, 6.01);      // at the pill's edge (1.5 border + 4 padding)
        Assert.InRange(RightGap(text), 17.9, 20);    // text inset unchanged (18 + border)
    }

    [AvaloniaFact]
    public void AlbumDialog_OpenedToEdit_CaretInEditor_EnterIsANewLine_CtrlEnterSavesOnce_AndCloses()
    {
        EnsureAppStyles();
        var lastFm = SlowLastFm.Loaded("Old.");
        var vm = AlbumPage(lastFm);
        var (win, host) = OpenAlbumDialog(vm, forEditing: true);
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));
        var editor = win.FindControl<TextBox>("DescriptionEditor")!;
        Assert.True(PumpUntil(() => editor.IsFocused), "the editor should have the caret when opened to edit");
        Assert.Equal(editor.Text!.Length, editor.CaretIndex);

        // Ctrl+Enter with nothing changed does nothing.
        win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.False(host.IsClosing);
        Assert.Empty(lastFm.Overrides);

        // Enter is a new line, not a save.
        win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Contains("\n", editor.Text);
        Assert.False(host.IsClosing);
        Assert.Empty(lastFm.Overrides);

        editor.Text = "New words ";
        win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.True(PumpUntil(() => host.IsClosing, 1000), "Ctrl+Enter should save and close");
        Assert.Equal(new[] { "New words" }, lastFm.Overrides);
        Assert.Equal("New words", vm.AlbumDescription);
        // More while it closes: nothing more is written, one close.
        if (closed == 0) win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.Single(lastFm.Overrides);
        Assert.Equal("New words", vm.AlbumDescriptionEditorText);
    }

    [AvaloniaFact]
    public void AlbumDialog_EscAfterEdits_ClosesOnce_WithoutSaving_AndDropsTheEdit()
    {
        EnsureAppStyles();
        var lastFm = SlowLastFm.Loaded("Kept.");
        var vm = AlbumPage(lastFm);
        var (win, host) = OpenAlbumDialog(vm, forEditing: true);
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));
        var editor = win.FindControl<TextBox>("DescriptionEditor")!;
        Assert.True(PumpUntil(() => editor.IsFocused));
        editor.Text = "Half typed";

        // Esc with the caret in the editor (no confirm, like the other pill pop-ups).
        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.True(host.IsClosing);
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.Empty(lastFm.Overrides);
        Assert.Equal("Kept.", vm.AlbumDescription);
        Assert.Equal("Kept.", vm.AlbumDescriptionEditorText);
        Assert.False(vm.HasAlbumDescriptionChanges);
    }

    [AvaloniaFact]
    public void AlbumDialog_Clear_ThenSave_RemovesTheDescription()
    {
        EnsureAppStyles();
        var lastFm = SlowLastFm.Loaded("Something.");
        var vm = AlbumPage(lastFm);
        var (win, host) = OpenAlbumDialog(vm, forEditing: false);
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            var clear = win.FindControl<Button>("ClearButton")!;
            Assert.True(clear.IsVisible);
            clear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => false, 20);
            Assert.Equal(string.Empty, win.FindControl<TextBox>("DescriptionEditor")!.Text);
            Assert.False(clear.IsVisible);
            Assert.Equal("Saving removes the description", win.FindControl<TextBlock>("CountText")!.Text);

            var save = win.FindControl<Button>("SaveButton")!;
            Assert.True(save.IsEffectivelyEnabled);
            save.Command!.Execute(null);
            Assert.True(PumpUntil(() => host.IsClosing, 1000));
            Assert.Equal(new[] { "" }, lastFm.Overrides);
            Assert.False(vm.HasAlbumDescription);
            Assert.True(vm.ShowAddAlbumDescription); // the page offers "Add a description" again
        }
        finally { CloseNow(win); }
    }

    [AvaloniaFact]
    public void AlbumDialog_WaitsForTheCover_BeforeFadingIn()
    {
        EnsureAppStyles();
        var persistence = new TestPersistenceService();
        var album = NewAlbum();
        var artPath = persistence.GetArtworkPath(album.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(artPath)!);
        using (var bmp = new SKBitmap(300, 300))
        {
            using (var c = new SKCanvas(bmp)) c.Clear(new SKColor(0x30, 0x60, 0x90));
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 90);
            using var fs = File.Create(artPath);
            data.SaveTo(fs);
        }
        var vm = AlbumPage(SlowLastFm.Loaded(null), album, persistence);
        Assert.False(string.IsNullOrEmpty(vm.HeaderArtPath));

        var (win, host) = OpenAlbumDialog(vm, forEditing: true);
        try
        {
            var cover = win.FindControl<CachedImage>("CoverImage")!;
            Assert.True(PumpUntil(() => host.ContentReady != null, 1000), "the cover should be the content-ready signal");
            Assert.True(PumpUntil(() => host.ContentReady!.IsCompleted && cover.Source != null, 3000), "the cover never landed");
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            // No cover text: the empty editor shows the placeholder and the count says so.
            Assert.Equal("No description", win.FindControl<TextBlock>("CountText")!.Text);
        }
        finally { CloseNow(win); }
    }

    [AvaloniaFact]
    public void AlbumDialog_NoCover_ReadyAtOnce()
    {
        EnsureAppStyles();
        var vm = AlbumPage(SlowLastFm.Loaded("x"));
        Assert.True(string.IsNullOrEmpty(vm.HeaderArtPath));
        var (win, host) = OpenAlbumDialog(vm, forEditing: false);
        try
        {
            Assert.True(PumpUntil(() => host.ContentReady != null, 1000));
            Assert.True(host.ContentReady!.IsCompleted);
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
        }
        finally { CloseNow(win); }
    }

    [AvaloniaFact]
    public void PlaylistDialog_SameDesign_AddOpensToEdit_SaveClosesAndPersistsOnce()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (vm, playlist, persistence) = PlaylistPage();
            var (win, host) = OpenPlaylistDialog(vm, forEditing: true);
            var closed = 0;
            win.Closed += (_, _) => closed++;
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(host)));
                Assert.True(host.BlurBackdrop);
                var all = win.GetVisualDescendants().ToList();
                var title = all.OfType<TextBlock>().Single(t => t.Name == "TitleText");
                Assert.Equal("Late Nights", title.Text);
                Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
                Assert.Equal(new[] { "0 songs" }, all.OfType<ItemsControl>().Single(i => i.Name == "FactChips").ItemsSource!.Cast<string>());

                var editor = all.OfType<TextBox>().Single(t => t.Name == "DescriptionEditor");
                Assert.Contains("pill-area", editor.Classes);
                Assert.True(PumpUntil(() => editor.IsFocused), "Add a description opens with the caret in the editor");
                Assert.Equal("No description", all.OfType<TextBlock>().Single(t => t.Name == "CountText").Text);
                Assert.False(all.OfType<Button>().Single(b => b.Name == "ClearButton").IsVisible);

                var save = all.OfType<Button>().Single(b => b.Name == "SaveButton");
                PillDialogHostTests.AssertSolidAccent(save);
                Assert.False(save.IsEffectivelyEnabled);
                editor.Text = "  Rainy drives  ";
                PumpUntil(() => false, 20);
                Assert.True(save.IsEffectivelyEnabled);
                save.Command!.Execute(null);
                save.Command!.Execute(null); // a double click
                Assert.True(PumpUntil(() => closed > 0, 2000));
                PumpUntil(() => false, 250);
                Assert.Equal(1, closed);
                Assert.Equal(1, persistence.PlaylistSaves);
                Assert.Equal("Rainy drives", playlist.Description);
                Assert.True(vm.HasDescription);
            }
            finally { CloseNow(win); }
        });
    }

    [AvaloniaFact]
    public void PlaylistDialog_Cancel_ClosesOnce_WithoutSaving()
    {
        EnsureAppStyles();
        var (vm, playlist, persistence) = PlaylistPage("Old words");
        var (win, host) = OpenPlaylistDialog(vm, forEditing: false);
        var closed = 0;
        win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(host)));
        var editor = win.FindControl<TextBox>("DescriptionEditor")!;
        Assert.Equal("Old words", editor.Text);
        Assert.False(editor.IsFocused);
        editor.Text = "Changed";

        win.FindControl<Button>("CancelButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(host.IsClosing);
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.Equal(0, persistence.PlaylistSaves);
        Assert.Equal("Old words", playlist.Description);
        Assert.Equal("Old words", vm.DescriptionEditorText);
    }
}
