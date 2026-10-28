using System;
using System.IO;
using System.Threading.Tasks;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Phone Settings' saves: only what changed is written, and a change made before the
/// saved values were read is neither lost nor overwritten by them.</summary>
public class MobileSettingsSaveTests
{
    private sealed class FakeThemeHost : IThemeHost
    {
        public (string Appearance, string DarkTheme, string Accent)? Last { get; private set; }
        public void ApplyTheme(string appearance, string darkTheme, string accentHex) => Last = (appearance, darkTheme, accentHex);
    }

    /// <summary>LoadSettingsAsync fails (a storage error); everything else reaches the real file.</summary>
    public class FailingSettingsReads : System.Reflection.DispatchProxy
    {
        private IPersistenceService _inner = null!;

        public static IPersistenceService Wrap(IPersistenceService inner)
        {
            var proxy = Create<IPersistenceService, FailingSettingsReads>();
            ((FailingSettingsReads)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IPersistenceService.LoadSettingsAsync))
                return Task.FromException<AppSettings>(new IOException("storage unavailable"));
            try { return method.Invoke(_inner, args); }
            catch (System.Reflection.TargetInvocationException ex) { throw ex.InnerException!; }
        }
    }

    /// <summary>The desktop's "Gray" (shown as Ink on the phone), its un-migrated v1 flag and its
    /// custom accent name stay as they were when only the lyrics text size moves.</summary>
    [Fact]
    public async Task OnlyTheTextSize_IsWritten_WhenOnlyTheTextSizeMoved()
    {
        using var rig = MobileFixtures.MakeRig(seed: p => p.UpdateSettingsAsync(s =>
        {
            s.Theme = "Gray";
            s.ThemeV2Migrated = false;
            s.AccentColorHex = "#123456";
            s.AccentPresetName = "Custom Blue";
            s.MobileAppearance = "Dark";
        }));
        rig.Shell.OpenSettingsCommand.Execute(null);
        var page = Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);
        await page.Loaded;
        Assert.Equal("Ink", page.DarkTheme);

        page.LyricsTextScale = 1.3;
        await page.PendingSave;

        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal(1.3, saved.MobileLyricsTextScale, 6);
        Assert.Equal("Gray", saved.Theme);                     // not "Ink"
        Assert.False(saved.ThemeV2Migrated);
        Assert.Equal("#123456", saved.AccentColorHex);
        Assert.Equal("Custom Blue", saved.AccentPresetName);
        Assert.Equal("Dark", saved.MobileAppearance);
    }

    /// <summary>OpenSettings does not await the load: a Look tile tapped before it finishes is
    /// kept (the load used to put the saved value back), saved, and themed with the loaded rest.</summary>
    [Fact]
    public async Task AChangeBeforeTheLoadFinished_IsKept_AndSavedAlone()
    {
        var host = new FakeThemeHost();
        using var rig = MobileFixtures.MakeRig(theme: host, seed: p => p.UpdateSettingsAsync(s =>
        {
            s.MobileAppearance = "Dark";
            s.Theme = "Midnight";
            s.MobileLyricsTextScale = 1.2;
        }));
        var page = new SettingsPageViewModel(rig.Shell);

        page.Appearance = "Light";                             // before the saved values are read
        Assert.Equal(0, page.SaveCount);                        // the defaults are not saved
        await page.LoadAsync();
        await page.PendingSave;

        Assert.Equal("Light", page.Appearance);
        Assert.Equal("Midnight", page.DarkTheme);
        Assert.Equal(1.2, page.LyricsTextScale, 6);
        Assert.Equal(("Light", "Midnight", MobileTheme.DefaultAccent), host.Last);
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("Light", saved.MobileAppearance);
        Assert.Equal("Midnight", saved.Theme);
        Assert.Equal(1.2, saved.MobileLyricsTextScale, 6);
    }

    /// <summary>A failed read leaves the page on its defaults, but a later change still saves,
    /// and only that change: the defaults never overwrite the file.</summary>
    [Fact]
    public async Task AFailedLoad_StillSavesLaterChanges_WithoutTheDefaults()
    {
        using var rig = MobileFixtures.MakeRig(seed: p => p.UpdateSettingsAsync(s =>
        {
            s.Theme = "Smoke";
            s.MobileLyricsTextScale = 1.5;
        }));
        var persistence = FailingSettingsReads.Wrap(rig.Persistence);
        var shell = new ShellViewModel(
            new LibraryViewModel(rig.Library, persistence, new MobileFixtures.NoPicker(), rig.History, marshal: a => a()),
            rig.Shell.Player, rig.Shell.Lyrics);
        var page = new SettingsPageViewModel(shell);
        await page.LoadAsync();                                 // does not throw

        page.Appearance = "Light";
        await page.PendingSave;

        Assert.Equal(1, page.SaveCount);
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("Light", saved.MobileAppearance);
        Assert.Equal("Smoke", saved.Theme);
        Assert.Equal(1.5, saved.MobileLyricsTextScale, 6);
    }
}
