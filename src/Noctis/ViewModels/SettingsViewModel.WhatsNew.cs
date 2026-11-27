using CommunityToolkit.Mvvm.Input;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Settings › About › What's new: the in-app release notes dialog and the small "new" dot on its
/// button. The dot rule (one source of truth, <see cref="SettingsViewModel.ShowWhatsNew"/>): it
/// shows while the running version is newer than <c>AppSettings.WhatsNewSeenVersion</c>, the
/// last version whose notes were opened. A brand-new profile starts at the running version (no
/// dot); a profile last saved by an older Noctis starts at that version, so the dot shows after
/// an update (automatic or not) until the dialog is opened once.
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>The release notes of the version that is running (About's "What's new").</summary>
    internal static string WhatsNewUrl =>
        $"https://github.com/heartached/Noctis/releases/tag/v{UpdateService.CurrentVersion.ToString(3)}";

    /// <summary>The version the dot compares with; tests set it (the test host is not Noctis 1.7.x).</summary>
    internal string RunningVersion { get; set; } = UpdateService.CurrentVersion.ToString(3);

    /// <summary>Opens the dialog; tests and the screenshot harness swap it.</summary>
    internal Func<WhatsNewViewModel, Task> ShowWhatsNewDialog { get; set; } = Views.WhatsNewDialog.ShowAsync;

    /// <summary>Where the dialog's notes come from; null = the app's UpdateService. Tests set a fake.</summary>
    internal IReleaseNotesSource? WhatsNewSource { get; set; }

    /// <summary>On load: give a profile without a seen version one, then set the dot.</summary>
    private void InitWhatsNewSeen()
    {
        if (string.IsNullOrEmpty(_settings.WhatsNewSeenVersion))
        {
            // Saved by an older Noctis = this run follows an update; nothing (a new profile) or
            // this same version = nothing new to point at.
            var seen = IsNewer(RunningVersion, _settings.WrittenByVersion) && !string.IsNullOrEmpty(_settings.WrittenByVersion)
                ? _settings.WrittenByVersion
                : RunningVersion;
            _settings.WhatsNewSeenVersion = seen;
            // Straight to the file: SaveAsync re-bases on the file first, which would undo it.
            _ = _persistence.UpdateSettingsAsync(s => s.WhatsNewSeenVersion = seen);
        }
        ShowWhatsNew = IsNewer(RunningVersion, _settings.WhatsNewSeenVersion);
    }

    /// <summary>True when <paramref name="candidate"/> parses and is later than <paramref name="baseline"/>
    /// (an unreadable baseline counts as older).</summary>
    private static bool IsNewer(string? candidate, string? baseline)
    {
        if (!Version.TryParse(candidate, out var c)) return false;
        return !Version.TryParse(baseline, out var b) || c > b;
    }

    /// <summary>Opening What's new clears the dot for this version.</summary>
    private void MarkWhatsNewSeen()
    {
        ShowWhatsNew = false;
        if (string.Equals(_settings.WhatsNewSeenVersion, RunningVersion, StringComparison.Ordinal)) return;
        var seen = RunningVersion;
        _settings.WhatsNewSeenVersion = seen;
        _ = _persistence.UpdateSettingsAsync(s => s.WhatsNewSeenVersion = seen);
    }

    /// <summary>The dialog for this version's notes (fetched only now, never at startup).</summary>
    internal WhatsNewViewModel CreateWhatsNewViewModel()
    {
        var vm = new WhatsNewViewModel(RunningVersion, WhatsNewSource ?? _updateService,
            new WhatsNewCache(_persistence.DataDirectory));
        return vm;
    }

    [RelayCommand]
    private async Task OpenWhatsNew()
    {
        MarkWhatsNewSeen();
        try
        {
            await ShowWhatsNewDialog(CreateWhatsNewViewModel());
        }
        catch (Exception ex)
        {
            DebugLog.Write("WhatsNew", ex);
        }
    }
}
