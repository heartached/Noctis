using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings ▸ Mini Player Design: the picker drives the mini player's form while a fixed
/// design is selected (window size no longer does), the window drops its dark glass for
/// the light designs, Lyrics can still be opened and hands the design back, and Classic
/// restores the size-driven forms.
/// </summary>
[Collection("MetadataServiceStatics")]
public class MiniPlayerDesignTests
{
    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("SearchIcon", null, out _)) return;
        app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null)
        {
            Source = new Uri("avares://Noctis/Assets/Icons.axaml"),
        });
    }

    private static MiniPlayerViewModel MakeViewModel()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), library,
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), library);
        var settings = new SettingsViewModel(
            new TestPersistenceService(), library, new NoOpPlayHistoryService());
        return new MiniPlayerViewModel(player, lyrics, settings, library);
    }

    private static async Task PumpFor(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    [Theory]
    [InlineData("Pill", MiniPlayerStyle.Pill)]
    [InlineData("sleeve", MiniPlayerStyle.Sleeve)]
    [InlineData("Classic", MiniPlayerStyle.Classic)]
    [InlineData("", MiniPlayerStyle.Classic)]
    [InlineData(null, MiniPlayerStyle.Classic)]
    [InlineData("2", MiniPlayerStyle.Classic)]
    [InlineData("Hologram", MiniPlayerStyle.Classic)]
    public void Parse_ByNameOnly_FallsBackToClassic(string? setting, MiniPlayerStyle expected)
        => Assert.Equal(expected, MiniPlayerStyles.Parse(setting));

    [AvaloniaFact]
    public async Task PickingADesign_LocksTheForm_AndClassicHandsItBackToTheSize()
    {
        EnsureAppResources();
        var vm = MakeViewModel();
        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        await PumpFor(150);
        try
        {
            Assert.Equal(MiniPlayerForm.Card, vm.Form);
            var root = win.FindControl<Border>("RootBorder")!;
            Assert.Equal(1.5, root.BorderThickness.Top, 3);

            vm.Settings.IsMiniStylePill = true;
            await PumpFor(500);
            Assert.Equal(MiniPlayerForm.Pill, vm.Form);
            Assert.True(vm.IsStyleLocked);
            // Search / Queue / Volume drawers work under a design; the card and the sheet
            // share the DesignGround slab (the root glass they normally sit on is hidden).
            Assert.True(vm.SupportsDrawer);
            Assert.True(vm.IsDesignForm);
            Assert.True(win.FindControl<Grid>("PillFormRoot")!.IsVisible);
            Assert.Equal(0, root.BorderThickness.Top, 3);
            Assert.False(win.FindControl<Border>("GlassSheen")!.IsVisible);
            Assert.True(win.FindControl<Border>("PillGround")!.IsVisible);
            Assert.Equal(24, win.FindControl<Border>("PillGround")!.CornerRadius.TopLeft, 3);
            // The window took the design's canonical size.
            var (pw, ph) = MiniPlayerViewModel.CanonicalSize(MiniPlayerForm.Pill);
            Assert.Equal(pw, win.Width, 1.0);
            Assert.Equal(ph, win.Height, 1.0);

            // Resizing no longer changes the form while a design is selected.
            win.Width = 640; win.Height = 420;
            await PumpFor(150);
            Assert.Equal(MiniPlayerForm.Pill, vm.Form);

            vm.Settings.IsMiniStyleSleeve = true;
            await PumpFor(500);
            Assert.Equal(MiniPlayerForm.Sleeve, vm.Form);
            Assert.True(win.FindControl<Grid>("SleeveFormRoot")!.IsVisible);
            Assert.NotNull(win.FindControl<Noctis.Controls.MediaArtwork>("SleeveDisc"));
            // The app's animated bars replaced the mockup's static grip, and the pill's
            // round cover is rigged to turn like a disc.
            Assert.NotNull(win.FindControl<Noctis.Controls.EqVisualizer>("SleeveEq"));
            Assert.IsType<Avalonia.Media.RotateTransform>(win.FindControl<Panel>("PillCoverSpin")!.RenderTransform);

            // Back to Classic: the card comes back as the form and exact size it left
            // from (340x432 Card), not the design's size run through the thresholds.
            vm.Settings.IsMiniStyleClassic = true;
            await PumpFor(700);
            Assert.False(vm.IsStyleLocked);
            Assert.False(vm.IsDesignForm);
            Assert.Equal(1.5, root.BorderThickness.Top, 3);
            Assert.True(win.FindControl<Border>("GlassSheen")!.IsVisible);
            Assert.Equal(Avalonia.Media.Brushes.Transparent, win.FindControl<Border>("DrawerSheet")!.Background);
            Assert.Equal(340, win.Width, 1.0);
            Assert.Equal(432, win.Height, 1.0);
            Assert.Equal(MiniPlayerForm.Card, vm.Form);
        }
        finally { win.Close(); }
    }

    // AvaloniaFact: the view models touch Avalonia objects, which are thread-bound once
    // any headless test has initialised the platform (passes alone, fails in-suite as a Fact).
    [AvaloniaFact]
    public void MenuDesignSegment_WritesTheSameSettingAsThePicker()
    {
        var vm = MakeViewModel();
        Assert.Equal(MiniPlayerStyle.Classic, vm.Settings.MiniPlayerStyleMode);
        vm.SetDesignCommand.Execute("Sleeve");
        Assert.Equal(MiniPlayerStyle.Sleeve, vm.Settings.MiniPlayerStyleMode);
        Assert.True(vm.Settings.IsMiniStyleSleeve);
        Assert.Equal(MiniPlayerForm.Sleeve, vm.Form);
        vm.SetDesignCommand.Execute("nonsense");
        Assert.Equal(MiniPlayerStyle.Classic, vm.Settings.MiniPlayerStyleMode);

        // Wheel volume: 5 per notch, clamped.
        vm.Player.Volume = 98;
        vm.NudgeVolume(1);
        Assert.Equal(100, vm.Player.Volume);
        vm.NudgeVolume(-2);
        Assert.Equal(90, vm.Player.Volume);
    }

    private readonly Xunit.Abstractions.ITestOutputHelper? _out;
    public MiniPlayerDesignTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [AvaloniaTheory]
    [InlineData("Classic", "SeekSlider")]
    [InlineData("Pill", "PillSeekSlider")]
    [InlineData("Sleeve", "SleeveSeekSlider")]
    public async Task SeekBar_PressDragRelease_SeeksThePlayer(string style, string sliderName)
    {
        // Pointer events are raised on the slider directly: the headless window's hit
        // test stops at a full-window layer above every form (the classic bar works in
        // the app), so a synthetic click through the window proves nothing. What this
        // checks is the wiring the designs were missing — press → BeginSeek, value →
        // PositionFraction, release → EndSeek commits the position.
        EnsureAppResources();
        var vm = MakeViewModel();
        vm.Player.CurrentTrack = new Track { Title = "T", Artist = "A", FilePath = @"C:	.flac" };
        vm.Player.Duration = TimeSpan.FromMinutes(4);
        vm.SetDesignCommand.Execute(style);
        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        await PumpFor(300);
        try
        {
            var slider = win.FindControl<Slider>(sliderName)!;
            Assert.True(slider.IsEffectivelyVisible);
            // The whole 16px slider is the target, not a 4px line.
            Assert.True(slider.Bounds.Height >= 16, $"slider height {slider.Bounds.Height}");

            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var at = new Point(slider.Bounds.Width * 0.75, slider.Bounds.Height / 2);
            slider.RaiseEvent(new PointerPressedEventArgs(slider, pointer, slider, at, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            Assert.True(vm.Player.IsSeeking, "press should begin a seek");

            slider.Value = 0.75;
            Assert.Equal(0.75, vm.Player.PositionFraction, 3);

            slider.RaiseEvent(new PointerReleasedEventArgs(slider, pointer, slider, at, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            await PumpFor(100);
            Assert.False(vm.Player.IsSeeking, "release should commit the seek");
            Assert.InRange(vm.Player.Position.TotalSeconds, 0.74 * 240, 0.76 * 240);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public async Task MenuActionsUnderADesign_DriveTheRealPlayer()
    {
        EnsureAppResources();
        var vm = MakeViewModel();
        var track = new Track { Title = "T", Artist = "A", FilePath = @"C:\t.flac" };
        vm.Player.CurrentTrack = track;
        vm.SetDesignCommand.Execute("Pill");
        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        await PumpFor(300);
        try
        {
            vm.ToggleSearchDrawerCommand.Execute(null);
            await PumpFor(400);
            Assert.Equal(MiniDrawer.Search, vm.Drawer);
            Assert.True(win.FindControl<Border>("DrawerSheet")!.IsVisible);
            vm.ToggleQueueDrawerCommand.Execute(null);
            await PumpFor(400);
            Assert.Equal(MiniDrawer.Queue, vm.Drawer);
            vm.ToggleVolumeDrawerCommand.Execute(null);
            await PumpFor(400);
            Assert.Equal(MiniDrawer.Volume, vm.Drawer);
            vm.CloseDrawerCommand.Execute(null);
            await PumpFor(400);

            var wasFavorite = track.IsFavorite;
            vm.Player.ToggleCurrentTrackFavoriteCommand.Execute(null);
            Assert.NotEqual(wasFavorite, track.IsFavorite);

            vm.ToggleLyricsFormCommand.Execute(null);
            await PumpFor(700);
            Assert.Equal(MiniPlayerForm.Lyrics, vm.Form);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public async Task SmallClassic_ComesBackAtTheSizeItWasLeft_EvenAfterAReopen()
    {
        EnsureAppResources();
        var vm = MakeViewModel();
        // A small classic card (a Bar), placed like a real session would persist it.
        var win = new MiniPlayerWindow { DataContext = vm, Width = 300, Height = 180 };
        win.Show();
        await PumpFor(200);
        try
        {
            Assert.Equal(MiniPlayerForm.Bar, vm.Form);
            Assert.Equal((300, 180), vm.Settings.StoredMiniPlayerSize);

            vm.SetDesignCommand.Execute("Pill");
            await PumpFor(600);
            Assert.Equal(MiniPlayerForm.Pill, vm.Form);
            // The design's size must NOT overwrite the stored classic size.
            Assert.Equal((300, 180), vm.Settings.StoredMiniPlayerSize);

            vm.SetDesignCommand.Execute("Classic");
            await PumpFor(700);
            Assert.Equal(300, win.Width, 1.0);
            Assert.Equal(180, win.Height, 1.0);
            Assert.Equal(MiniPlayerForm.Bar, vm.Form);
        }
        finally { win.Close(); }

        // Same again across a reopen: a fresh window opened in the design (the
        // in-session capture is gone) still hands Classic the persisted 300x180.
        vm.SetDesignCommand.Execute("Sleeve");
        var reopened = new MiniPlayerWindow { DataContext = vm, Width = 300, Height = 180 };
        reopened.Show();
        await PumpFor(300);
        try
        {
            Assert.Equal(MiniPlayerForm.Sleeve, vm.Form);
            var (sw, sh) = MiniPlayerViewModel.CanonicalSize(MiniPlayerForm.Sleeve);
            Assert.Equal(sw, reopened.Width, 1.0);
            Assert.Equal(sh, reopened.Height, 1.0);
            Assert.Equal((300, 180), vm.Settings.StoredMiniPlayerSize);

            vm.SetDesignCommand.Execute("Classic");
            await PumpFor(700);
            Assert.Equal(300, reopened.Width, 1.0);
            Assert.Equal(180, reopened.Height, 1.0);
            Assert.Equal(MiniPlayerForm.Bar, vm.Form);
        }
        finally { reopened.Close(); }
    }

    [AvaloniaFact]
    public async Task SwitchingDesignWithADrawerOpen_DoesNotLeaveTheSheetInsideTheNewForm()
    {
        EnsureAppResources();
        var vm = MakeViewModel();
        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        await PumpFor(200);
        try
        {
            vm.ToggleVolumeDrawerCommand.Execute(null);
            await PumpFor(150); // mid-slide
            vm.SetDesignCommand.Execute("Pill");
            await PumpFor(700);
            var (pw, ph) = MiniPlayerViewModel.CanonicalSize(MiniPlayerForm.Pill);
            Assert.Equal(MiniDrawer.None, vm.Drawer);
            Assert.Equal(0, win.FindControl<Border>("DrawerSheet")!.Height, 1);
            Assert.False(win.FindControl<Border>("DrawerSheet")!.IsVisible);
            Assert.Equal(pw, win.Width, 1.0);
            Assert.Equal(ph, win.Height, 1.0);
            // The pill's card gets the whole window: nothing squashed.
            var card = win.FindControl<Border>("PillCard")!;
            Assert.True(card.Bounds.Height >= 116, $"pill card height {card.Bounds.Height}");
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public async Task DrawerUnderADesign_JoinsTheCard()
    {
        // The design card and its drawer share ONE painted slab (DesignGround): it hugs the
        // card's outline while closed, and when a drawer opens it simply grows with the
        // window — nothing about the card (position, corners) changes at the moment the
        // sheet appears, and the sheet's rows stay inside the slab's rounded bottom.
        EnsureAppResources();
        var vm = MakeViewModel();
        vm.SetDesignCommand.Execute("Sleeve");
        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        await PumpFor(300);
        try
        {
            var card = win.FindControl<Border>("SleeveCard")!;
            var sheet = win.FindControl<Border>("DrawerSheet")!;
            var ground = win.FindControl<Border>("SleeveGround")!;
            Assert.True(ground.IsVisible);
            Assert.Equal(34, ground.CornerRadius.BottomLeft, 3);
            Assert.Equal(34, card.CornerRadius.BottomLeft, 3);

            Rect InWindow(Visual v) => new Rect(v.Bounds.Size).TransformToAABB(v.TransformToVisual(win)!.Value);
            var cardClosed = InWindow(card);
            var groundClosed = InWindow(ground);
            Assert.Equal(cardClosed.Left, groundClosed.Left, 1.0);
            Assert.Equal(cardClosed.Top, groundClosed.Top, 1.0);
            Assert.Equal(cardClosed.Right, groundClosed.Right, 1.0);
            Assert.Equal(cardClosed.Bottom, groundClosed.Bottom, 1.0);
            var bottomInset = win.ClientSize.Height - groundClosed.Bottom;

            vm.ToggleQueueDrawerCommand.Execute(null);
            await PumpFor(120); // mid-slide
            var cardMid = InWindow(card);
            var groundMid = InWindow(ground);
            Assert.Equal(cardClosed.Top, cardMid.Top, 1.0);
            Assert.Equal(cardClosed.Bottom, cardMid.Bottom, 1.0);
            Assert.Equal(34, card.CornerRadius.BottomLeft, 3);
            Assert.Equal(groundClosed.Left, groundMid.Left, 1.0);
            Assert.Equal(groundClosed.Top, groundMid.Top, 1.0);
            Assert.True(groundMid.Bottom > groundClosed.Bottom + 10, $"ground not growing: {groundMid.Bottom} vs {groundClosed.Bottom}");
            Assert.Equal(win.ClientSize.Height - bottomInset, groundMid.Bottom, 1.5);

            await PumpFor(500);
            var groundOpen = InWindow(ground);
            var sheetOpen = InWindow(sheet);
            Assert.Equal(win.ClientSize.Height - bottomInset, groundOpen.Bottom, 1.0);
            // Sheet content box: inside the slab's sides, rows kept above its bottom inset.
            Assert.Equal(groundOpen.Left, sheetOpen.Left, 1.0);
            Assert.Equal(groundOpen.Right, sheetOpen.Right, 1.0);
            Assert.InRange(sheetOpen.Top - cardClosed.Bottom, -1, bottomInset + 1);
            Assert.Equal(12 + bottomInset, sheet.Padding.Bottom, 1.0);
            Assert.Equal(0, sheet.BorderThickness.Top, 3);
            Assert.Equal(Brushes.Transparent, card.Background);

            vm.CloseDrawerCommand.Execute(null);
            await PumpFor(500);
            var groundBack = InWindow(ground);
            Assert.Equal(groundClosed.Bottom, groundBack.Bottom, 1.0);
            Assert.Equal(cardClosed.Bottom, InWindow(card).Bottom, 1.0);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public async Task DesignGround_StaysSolid_WhateverTheOpacitySetting()
    {
        // Mini Player Opacity is the classic glass's knob; the designs are solid cards.
        EnsureAppResources();
        var vm = MakeViewModel();
        vm.SetDesignCommand.Execute("Pill");
        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        await PumpFor(300);
        try
        {
            var ground = win.FindControl<Border>("PillGround")!;
            vm.Settings.MiniPlayerBackgroundOpacity = 0;
            await PumpFor(50);
            // AppMainBackground is a theme resource (not merged in the headless app); what
            // matters is that whatever brush is there ignores the setting.
            Assert.True(ground.Background is null || Math.Abs(ground.Background.Opacity - 1) < 0.001);
            Assert.Equal(0x33, ground.BoxShadow[0].Color.A);
            // The classic root fill is hidden under a design, so the ground is the only slab.
            Assert.Equal(Brushes.Transparent, win.FindControl<Border>("RootBorder")!.Background);
        }
        finally { win.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Sleeve", "SleeveCard")]
    [InlineData("Pill", "PillCard")]
    [InlineData("Classic", "RootBorder")]
    public async Task HoldingTheMouseStill_DoesNotStartTheWindowDrag(string design, string target)
    {
        // BeginMoveDrag hands the window to the OS move loop, which starves the frame
        // callbacks the disc / cover spinners ride on. A press only ARMS the drag; it
        // starts once the pointer has actually travelled.
        EnsureAppResources();
        var vm = MakeViewModel();
        if (design != "Classic") vm.SetDesignCommand.Execute(design);
        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        await PumpFor(300);
        try
        {
            var card = win.FindControl<Border>(target)!;
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var at = new Point(card.Bounds.Width / 2, card.Bounds.Height / 2);
            PointerPressedEventArgs Press() => new(card, pointer, card, at, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
            PointerEventArgs Move(double dx) => new(InputElement.PointerMovedEvent, card, pointer, card, at + new Vector(dx, 0), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None);

            card.RaiseEvent(Press());
            Assert.True(win.IsDragArmed, "press arms the drag");
            card.RaiseEvent(Move(2));
            Assert.True(win.IsDragArmed, "a 2px wobble is still a hold");
            card.RaiseEvent(new PointerReleasedEventArgs(card, pointer, card, at, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            Assert.False(win.IsDragArmed, "release disarms without ever dragging");

            card.RaiseEvent(Press());
            card.RaiseEvent(Move(12));
            Assert.False(win.IsDragArmed, "real movement hands the window to the OS drag");
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public async Task LyricsFromADesign_OpensAndHandsTheDesignBack()
    {
        EnsureAppResources();
        var vm = MakeViewModel();
        vm.Settings.IsMiniStyleSleeve = true;
        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        await PumpFor(200);
        try
        {
            Assert.Equal(MiniPlayerForm.Sleeve, vm.Form);
            var (sw, sh) = MiniPlayerViewModel.CanonicalSize(MiniPlayerForm.Sleeve);
            Assert.Equal(sw, win.Width, 1.0);
            Assert.Equal(sh, win.Height, 1.0);

            vm.ToggleLyricsFormCommand.Execute(null);
            await PumpFor(700);
            Assert.Equal(MiniPlayerForm.Lyrics, vm.Form);
            Assert.True(win.FindControl<Grid>("LyricsFormRoot")!.IsVisible);

            vm.ToggleLyricsFormCommand.Execute(null);
            await PumpFor(900);
            Assert.Equal(MiniPlayerForm.Sleeve, vm.Form);
            Assert.Equal(sw, win.Width, 1.0);
            Assert.Equal(sh, win.Height, 1.0);
        }
        finally { win.Close(); }
    }
}
