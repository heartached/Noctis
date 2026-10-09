using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-09: Edit Playlist as the rounded pill pop-up (blurred app behind, filled pill
/// fields instead of white outlines, cover header with Change / Remove custom cover, the Star
/// as a toggle row, Cancel / Save pills). The dialog is wired exactly as
/// SidebarViewModel.EditPlaylistAsync wires it, and saves go through
/// SidebarViewModel.ApplyPlaylistEditAsync, the code the real Save runs.
/// </summary>
[Collection("ArtworkCache")]
public class EditPlaylistDialogTests
{
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

    private sealed class Store : TestPersistenceService
    {
        public List<Playlist> Saved { get; } = new();
        public int Saves;
        public Store(params Playlist[] playlists) => Saved.AddRange(playlists);
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(Saved.ToList());
        public override Task SavePlaylistsAsync(List<Playlist> playlists) { Saves++; return Task.CompletedTask; }
    }

    private static async Task<(SidebarViewModel sidebar, Store store)> SidebarWith(params Playlist[] playlists)
    {
        var store = new Store(playlists);
        var sidebar = new SidebarViewModel(store, new FakeLibraryService());
        await sidebar.LoadPlaylistsAsync();
        return (sidebar, store);
    }

    private static EditPlaylistDialogViewModel EditorFor(SidebarViewModel sidebar, Playlist playlist)
        => EditPlaylistDialogViewModel.ForPlaylist(playlist,
            sidebar.PlaylistItems.FirstOrDefault(n => n.PlaylistId == playlist.Id), sidebar.GetFolderNames());

    /// <summary>Opens the dialog wired as SidebarViewModel.EditPlaylistAsync does.</summary>
    private static (EditPlaylistDialog win, List<(string Name, string Description)> saves) Open(EditPlaylistDialogViewModel vm)
    {
        var win = new EditPlaylistDialog { DataContext = vm, RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        var saves = new List<(string, string)>();
        vm.PlaylistSaved += (_, args) => saves.Add(args);
        vm.CloseRequested += (_, _) => _ = win.CloseAnimatedAsync();
        win.Show();
        return (win, saves);
    }

    private static readonly Color PillFill = Color.Parse("#1CFFFFFF");

    private static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    /// <summary>Filled pill chrome, no outline at rest (the owner's screenshot: white outlines).</summary>
    private static void AssertFilledNoOutline(TextBox box, CornerRadius radius)
    {
        var chrome = box.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
        Assert.Equal(radius, chrome.CornerRadius);
        if (box.IsFocused) return;
        Assert.Equal(PillFill, AccentTestHarness.ColorOf(chrome.Background));
        Assert.Equal(0, AccentTestHarness.ColorOf(chrome.BorderBrush).A);
    }

    private static T Named<T>(Window win, string name) where T : Control =>
        win.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "noctis-edit-playlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── Bugs ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Live check 10-09: starred, the Star row turned into a square accent rectangle that hid
    /// the star and the check. Fluent's ToggleButton:checked style painted the row template's
    /// PART_ContentPresenter. The presenter stays clear; the star is the artist page's
    /// animated star (HeartIcon, ZoomOnToggle) and is on; the row has a spoken name.
    /// </summary>
    [AvaloniaFact]
    public void StarredRow_KeepsItsRoundedFill_AndShowsTheAnimatedStar()
    {
        EnsureAppStyles();
        var vm = EditPlaylistDialogViewModel.ForPlaylist(
            new Playlist { Name = "Gym", IsPinned = true }, null, Array.Empty<string>());
        var (win, _) = Open(vm);
        try
        {
            PumpUntil(() => false, 300);
            var row = Named<ToggleButton>(win, "StarToggle");
            Assert.True(row.IsChecked);
            var presenter = row.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Assert.Equal(0, AccentTestHarness.ColorOf(presenter.Background).A);

            var star = row.GetVisualDescendants().OfType<Noctis.Controls.HeartIcon>().Single();
            Assert.True(star.IsFavorite);
            Assert.True(star.ZoomOnToggle);
            Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(row)));
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void Escape_Closes_WithoutSaving()
    {
        EnsureAppStyles();
        var vm = EditPlaylistDialogViewModel.ForPlaylist(new Playlist { Name = "Gym" }, null, Array.Empty<string>());
        var (win, saves) = Open(vm);
        try
        {
            PumpUntil(() => false, 300);
            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(PumpUntil(() => !win.IsVisible, 2000), "Esc should close the dialog like Cancel");
            Assert.Empty(saves);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    [AvaloniaFact]
    public void Save_WithNothingChanged_NeverReachesTheCaller()
    {
        // The caller stamps ModifiedAt on every save, so an untouched Save moved the
        // playlist's "Updated" date.
        var vm = EditPlaylistDialogViewModel.ForPlaylist(
            new Playlist { Name = "Gym", Description = "Lifting", Folder = "Sport", IsPinned = true }, null, new[] { "Sport" });
        var saves = 0;
        vm.PlaylistSaved += (_, _) => saves++;
        vm.SaveCommand.Execute(null);
        Assert.Equal(0, saves);
    }

    [AvaloniaFact]
    public async Task SavedEdit_RefreshesTheOpenPlaylistPage_NameStarCoverAndUpdated()
    {
        var dir = TempDir();
        var cover = Path.Combine(dir, "custom.jpg");
        File.WriteAllBytes(cover, new byte[] { 1, 2, 3 });
        var playlist = new Playlist
        {
            Id = Guid.NewGuid(), Name = "Gym", CoverArtPath = cover,
            ModifiedAt = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        var (sidebar, store) = await SidebarWith(playlist);
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, store, new FakeAnimatedCoverService());
        var page = new PlaylistViewModel(playlist, player, lib, store, sidebar);
        Dispatcher.UIThread.RunJobs();
        Assert.True(page.HasCustomArt);
        Assert.False(page.IsPinned);
        var raised = new List<string?>();
        page.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Edited from the sidebar's context menu while the page is open, or from the page.
        var edit = EditorFor(sidebar, playlist);
        edit.PlaylistName = "Night Drive";
        edit.IsPinned = true;
        edit.RemoveCoverArtCommand.Execute(null);
        await sidebar.ApplyPlaylistEditAsync(playlist, edit, "Night Drive", "");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Night Drive", page.Name);
        Assert.True(page.IsPinned);
        Assert.False(page.HasCustomArt);
        Assert.Contains(nameof(PlaylistViewModel.IsPinned), raised);
        Assert.Contains(nameof(PlaylistViewModel.StarTip), raised);
        Assert.Contains(nameof(PlaylistViewModel.HasCustomArt), raised);
        Assert.Contains(nameof(PlaylistViewModel.ShowFallbackIcon), raised);
        Assert.Contains(nameof(PlaylistViewModel.ModifiedDateDisplay), raised);
        page.Dispose();
    }

    [AvaloniaFact]
    public async Task ChangingTheCoverAgain_SameFileType_DropsTheStaleDecode()
    {
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = (_, width) => new WriteableBitmap(new PixelSize(width, width), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        var invalidated = new List<string>();
        void OnInvalidated(string p) => invalidated.Add(p);
        ArtworkCache.Invalidated += OnInvalidated;
        try
        {
            var dir = TempDir();
            var a = Path.Combine(dir, "a.jpg");
            var b = Path.Combine(dir, "b.jpg");
            File.WriteAllBytes(a, new byte[] { 1 });
            File.WriteAllBytes(b, new byte[] { 2, 2 });
            var playlist = new Playlist { Id = Guid.NewGuid(), Name = "Gym" };
            var (sidebar, _) = await SidebarWith(playlist);

            var first = EditorFor(sidebar, playlist);
            first.UseCoverFile(a);
            await sidebar.ApplyPlaylistEditAsync(playlist, first, "Gym", "");
            var dest = playlist.CoverArtPath!;
            Assert.NotNull(ArtworkCache.LoadAndCache(dest, 256)); // the sidebar row shows it

            var second = EditorFor(sidebar, playlist);
            second.UseCoverFile(b);
            await sidebar.ApplyPlaylistEditAsync(playlist, second, "Gym", "");

            // Same name on disk (<id>.jpg), new bytes: the decode of the old picture must go,
            // or every CachedImage on that path keeps drawing it.
            Assert.Equal(dest, playlist.CoverArtPath);
            Assert.Equal(File.ReadAllBytes(b), File.ReadAllBytes(dest));
            Assert.Contains(dest, invalidated);
            Assert.Null(ArtworkCache.TryGet(dest, 256));
            File.Delete(dest);
        }
        finally
        {
            ArtworkCache.Invalidated -= OnInvalidated;
            ArtworkCache.ClearForTests();
            ArtworkCache.DecoderOverride = null;
            ArtworkCache.DisposeGrace = TimeSpan.FromSeconds(2);
        }
    }

    [AvaloniaFact]
    public async Task PickingTheCurrentCoverFileItself_SavesTheRest()
    {
        var dir = TempDir();
        var a = Path.Combine(dir, "a.png");
        File.WriteAllBytes(a, new byte[] { 7, 7 });
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "Gym" };
        var (sidebar, _) = await SidebarWith(playlist);
        var first = EditorFor(sidebar, playlist);
        first.UseCoverFile(a);
        await sidebar.ApplyPlaylistEditAsync(playlist, first, "Gym", "");
        var dest = playlist.CoverArtPath!;

        // The picker opened in playlist_covers and the user chose the cover already in use.
        var again = EditorFor(sidebar, playlist);
        again.UseCoverFile(dest);
        again.PlaylistName = "Gym 2";
        var error = await Record.ExceptionAsync(() => sidebar.ApplyPlaylistEditAsync(playlist, again, "Gym 2", ""));

        Assert.Null(error);
        Assert.Equal(dest, playlist.CoverArtPath);
        Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(dest));
        Assert.Equal("Gym 2", sidebar.PlaylistItems.Single(n => n.PlaylistId == playlist.Id).Label);
        File.Delete(dest);
    }

    [AvaloniaFact]
    public async Task TypingAnExistingFolderInAnotherCase_JoinsIt_WithoutRenamingItsHeader()
    {
        var loose = new Playlist { Id = Guid.NewGuid(), Name = "Loose" };
        var member = new Playlist { Id = Guid.NewGuid(), Name = "Member", Folder = "Rock" };
        var (sidebar, _) = await SidebarWith(loose, member);
        Assert.Equal("Rock", sidebar.SidebarRows.Single(r => r.IsFolder).Label);

        var edit = EditorFor(sidebar, loose);
        edit.PlaylistFolder = "rock";
        await sidebar.ApplyPlaylistEditAsync(loose, edit, "Loose", "");

        Assert.Equal("Rock", sidebar.SidebarRows.Single(r => r.IsFolder).Label);
        Assert.Equal("Rock", loose.Folder);
    }

    // ── The pill pop-up ──────────────────────────────────────────────────

    [AvaloniaFact]
    public void OpensInPillHost_FilledFields_CoverHeader_StarRow_SaveGreyedUntilAChange()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var vm = EditPlaylistDialogViewModel.ForPlaylist(
                new Playlist { Name = "Gym", Description = "Lifting", Folder = "Sport" }, null, new[] { "Chill", "Sport" });
            var (win, _) = Open(vm);
            try
            {
                var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
                Assert.True(PumpUntil(() => CardSettledOpen(host)), "open animation never settled");
                Assert.Equal(new CornerRadius(30), host.CornerRadius);

                var name = Named<TextBox>(win, "NameTextBox");
                Assert.Contains("pill-field", name.Classes);
                Assert.True(PumpUntil(() => name.IsFocused), "the name field should have the caret on open");
                AssertFilledNoOutline(name, new CornerRadius(999));

                var description = Named<TextBox>(win, "DescriptionTextBox");
                Assert.Contains("pill-area", description.Classes);
                Assert.True(description.AcceptsReturn);
                AssertFilledNoOutline(description, new CornerRadius(20));

                var folder = Named<AutoCompleteBox>(win, "FolderBox");
                Assert.Contains("pill-field", folder.Classes);
                Assert.Equal("Sport", folder.Text);
                AssertFilledNoOutline(folder.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox"), new CornerRadius(999));
                Assert.True(Named<Button>(win, "ShowFoldersButton").IsVisible);

                // No custom picture: Change cover only (the collage/colour cannot be removed).
                Assert.True(Named<Button>(win, "ChangeCoverButton").IsEffectivelyVisible);
                Assert.Contains("pill-secondary", Named<Button>(win, "ChangeCoverButton").Classes);
                Assert.False(Named<Button>(win, "RemoveCoverButton").IsVisible);

                // Star: one row, the whole row toggles, the check follows.
                var star = Named<ToggleButton>(win, "StarToggle");
                Assert.False(star.IsChecked);
                star.IsChecked = true;
                Assert.True(vm.IsPinned);

                var save = Named<Button>(win, "SaveButton");
                PillDialogHostTests.AssertSolidAccent(save);
                Assert.Contains("pill-secondary", Named<Button>(win, "CancelButton").Classes);
                PumpUntil(() => false, 30);
                Assert.True(save.IsEffectivelyEnabled); // the star changed
                star.IsChecked = false;
                PumpUntil(() => false, 30);
                Assert.False(save.IsEffectivelyEnabled); // back to what it opened with
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        });
    }

    [AvaloniaFact]
    public void Save_IsOffered_OnlyForARealChange_AndANonBlankName()
    {
        var vm = EditPlaylistDialogViewModel.ForPlaylist(
            new Playlist { Name = "Gym", Description = "Lifting", Folder = "Sport" }, null, new[] { "Sport" });
        Assert.False(vm.CanSave);

        vm.PlaylistName = "Gym ";                 // trailing space only: what Save writes is the same
        Assert.False(vm.CanSave);
        vm.PlaylistFolder = "  sport ";           // the same folder, other case and spaces
        Assert.False(vm.CanSave);
        Assert.Equal("Sport", vm.ResolvedFolder);

        vm.PlaylistName = "   ";                  // blank: never, and it says why
        Assert.False(vm.CanSave);
        Assert.True(vm.ShowNameRequiredError);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.PlaylistName = "Gym Days";
        Assert.False(vm.ShowNameRequiredError);
        Assert.True(vm.CanSave);
        Assert.True(vm.SaveCommand.CanExecute(null));

        vm.PlaylistName = "Gym";
        vm.PlaylistDescription = "Lifting heavy";
        Assert.True(vm.CanSave);
        vm.PlaylistDescription = "Lifting";
        vm.PlaylistFolder = "   ";                // out of the folder
        Assert.Equal(string.Empty, vm.ResolvedFolder);
        Assert.True(vm.CanSave);
        vm.PlaylistFolder = "Brand New";          // a new folder is typed, not picked
        Assert.Equal("Brand New", vm.ResolvedFolder);
        Assert.True(vm.CanSave);
    }

    [AvaloniaFact]
    public void Save_AnimatesOut_TrimmedEditReachesTheCaller_Once_EvenWithEnterMidClose()
    {
        EnsureAppStyles();
        var vm = EditPlaylistDialogViewModel.ForPlaylist(new Playlist { Name = "Gym" }, null, Array.Empty<string>());
        var (win, saves) = Open(vm);
        var closed = 0;
        win.Closed += (_, _) => closed++;
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        Assert.True(PumpUntil(() => CardSettledOpen(host)));
        var name = Named<TextBox>(win, "NameTextBox");
        Assert.True(PumpUntil(() => name.IsFocused));

        vm.PlaylistName = "  Night Drive  ";
        vm.PlaylistDescription = "  windows down ";
        win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);   // Enter in the name saves
        Assert.True(host.IsClosing);
        Assert.True(win.IsVisible);
        vm.SaveCommand.Execute(null);                                     // a second Save meanwhile
        Assert.False(vm.CanSave);
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.Single(saves);
        Assert.Equal(("Night Drive", "windows down"), saves[0]);
    }

    [AvaloniaFact]
    public void Cover_RemoveCustom_ShowsTheCollage_ChangeCover_ShowsThePicture()
    {
        EnsureAppStyles();
        var playlist = new Playlist { Name = "Gym", CoverArtPath = Path.Combine(TempDir(), "custom.jpg") };
        var nav = new PlaylistNavItem { Art1 = "a.jpg", Art2 = "b.jpg", Art3 = "c.jpg", Art4 = "d.jpg", MetaText = "12 tracks · 45 min" };
        var vm = EditPlaylistDialogViewModel.ForPlaylist(playlist, nav, Array.Empty<string>());
        var (win, _) = Open(vm);
        try
        {
            PumpUntil(() => false, 300);
            Assert.Equal("12 tracks · 45 min", vm.MetaText);
            Assert.True(vm.HasCustomArt);
            Assert.False(vm.HasCollageArt);
            var remove = Named<Button>(win, "RemoveCoverButton");
            Assert.True(remove.IsVisible);
            Assert.False(vm.CanSave);

            vm.RemoveCoverArtCommand.Execute(null);
            PumpUntil(() => false, 30);
            Assert.False(vm.HasCustomArt);
            Assert.True(vm.HasCollageArt);   // back to the automatic album collage
            Assert.False(remove.IsVisible);
            Assert.True(vm.CoverArtRemoved);
            Assert.True(vm.CanSave);

            vm.UseCoverFile(Path.Combine(TempDir(), "new.png"));
            PumpUntil(() => false, 30);
            Assert.True(vm.HasCustomArt);
            Assert.True(remove.IsVisible);
            Assert.False(vm.CoverArtRemoved);
            Assert.True(vm.CanSave);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    [AvaloniaFact]
    public void Folder_Chevron_ListsEveryFolder_FirstEscClosesTheList_SecondTheDialog()
    {
        EnsureAppStyles();
        var vm = EditPlaylistDialogViewModel.ForPlaylist(new Playlist { Name = "Gym" }, null, new[] { "Chill", "Rock", "Sport" });
        var (win, saves) = Open(vm);
        var closed = 0;
        win.Closed += (_, _) => closed++;
        try
        {
            var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
            Assert.True(PumpUntil(() => CardSettledOpen(host)));
            var folder = Named<AutoCompleteBox>(win, "FolderBox");
            Named<Button>(win, "ShowFoldersButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(PumpUntil(() => folder.IsDropDownOpen), "the chevron should open the folder list");
            // The list (popup content, outside the box's visual tree) is a rounded card.
            var popup = folder.GetVisualDescendants().OfType<Popup>().Single();
            var card = Assert.IsType<Border>(popup.Child);
            Assert.Equal("PART_SuggestionsContainer", card.Name);
            Assert.Equal(new CornerRadius(18), card.CornerRadius);
            var list = Assert.IsType<ListBox>(card.Child);
            Assert.True(PumpUntil(() => list.ItemCount == 3), $"expected every folder, got {list.ItemCount}");

            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            PumpUntil(() => false, 50);
            Assert.False(folder.IsDropDownOpen);
            Assert.False(host.IsClosing);

            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(PumpUntil(() => closed > 0, 2000));
            Assert.Empty(saves);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    [AvaloniaFact]
    public void Folder_TypingANewName_KeepsIt()
    {
        EnsureAppStyles();
        var vm = EditPlaylistDialogViewModel.ForPlaylist(new Playlist { Name = "Gym" }, null, new[] { "Chill" });
        var (win, saves) = Open(vm);
        try
        {
            PumpUntil(() => false, 300);
            var folder = Named<AutoCompleteBox>(win, "FolderBox");
            folder.Text = "Workout";
            PumpUntil(() => false, 30);
            Assert.Equal("Workout", vm.PlaylistFolder);
            Assert.Equal("Workout", vm.ResolvedFolder);
            vm.SaveCommand.Execute(null);
            Assert.Single(saves);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }
}
