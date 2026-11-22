using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>One separator on Settings › Artist Separators, with its Remove button's spoken name.</summary>
public sealed record ArtistSeparatorRow(string Text)
{
    public string RemoveName => Loc.T("Mobile.Separators.RemoveName", Text);
}

/// <summary>
/// Settings › Artist Separators (Discord, beta tester: "an artist separator tag setting"): the
/// desktop's Settings › Library › Artist Tag Separators on the phone. The list of separators, each
/// with Remove, a field with Add, and Reset to Default; the desktop's rules (trimmed, no repeat
/// ignoring case, removing the last one puts the defaults back). The process-wide tokenizer
/// (<see cref="ArtistCredit"/>) is the state: each change goes there at once, the library regroups
/// its artists (as the desktop's ApplyArtistGrouping does) and the list is saved in the phone's
/// settings (AppSettings.ArtistTagSeparators), read back at start by <see cref="ApplySaved"/>.
/// </summary>
public sealed partial class ArtistSeparatorsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private Task _saveChain = Task.CompletedTask;

    public ArtistSeparatorsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Sync();
    }

    /// <summary>The tail of the separator saves; tests await it.</summary>
    internal Task PendingSave => _saveChain;

    /// <summary>The active separators, in their order.</summary>
    public ObservableCollection<ArtistSeparatorRow> Separators { get; } = new();

    /// <summary>The field under the list.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _newSeparator = string.Empty;

    /// <summary>The shipped separators are the ones in use: Reset to Default has nothing to do.</summary>
    public bool IsDefault => Separators.Select(r => r.Text).SequenceEqual(ArtistCredit.DefaultSeparators, StringComparer.Ordinal);

    /// <summary>The main sheet's row value: how many separators are in use ("6").</summary>
    public string Summary => Separators.Count.ToString(Loc.Instance.Culture);

    /// <summary>The settings the phone starts with, into the tokenizer: called before the library
    /// loads, so its artist list (and the index cache it restores, keyed by
    /// <see cref="ArtistCredit.Signature"/>) is built under the saved separators. The same call
    /// as LibraryService's startup read, which on the phone ran only after the list was built.</summary>
    public static void ApplySaved(AppSettings settings) =>
        ArtistCredit.Configure(ArtistGroupModes.Parse(settings.ArtistGroupMode), settings.ArtistTagSeparators,
            settings.ArtistProtectedNames ?? ArtistCredit.DefaultProtectedNames.ToList());

    /// <summary>Rebuilds the rows from the tokenizer (opened, changed, the language switched).</summary>
    public void Sync()
    {
        var active = ArtistCredit.Separators;
        if (!active.SequenceEqual(Separators.Select(r => r.Text), StringComparer.Ordinal))
        {
            Separators.Clear();
            foreach (var s in active) Separators.Add(new ArtistSeparatorRow(s));
        }
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(Summary));
    }

    public void Relabel()
    {
        Separators.Clear();
        Sync();
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewSeparator);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var value = NewSeparator?.Trim() ?? string.Empty;
        NewSeparator = string.Empty;
        if (value.Length == 0) return;
        var current = Separators.Select(r => r.Text).ToList();
        if (current.Any(s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase))) return;
        current.Add(value);
        Apply(current);
    }

    [RelayCommand]
    private void Remove(ArtistSeparatorRow? row)
    {
        if (row == null) return;
        var current = Separators.Select(r => r.Text).ToList();
        if (!current.Remove(row.Text)) return;
        // None left: ArtistCredit falls back to the defaults (an empty list would split
        // nothing), so the rows show the defaults too, as the desktop's chips do.
        Apply(current);
    }

    [RelayCommand]
    private void Reset() => Apply(ArtistCredit.DefaultSeparators);

    /// <summary>
    /// Into the tokenizer (keeping the grouping mode and the "never split" names, which the phone
    /// does not show), then the rows from what it took, the save, and a regroup when the split
    /// really changed: LibraryService rebuilds off the UI thread and raises LibraryUpdated, which
    /// the Library's lists and open artist pages already follow.
    /// </summary>
    private void Apply(IEnumerable<string> separators)
    {
        var before = ArtistCredit.Version;
        var joinBefore = ArtistCredit.JoinText;
        ArtistCredit.Configure(ArtistCredit.GroupMode, separators);
        Sync();
        if (ArtistCredit.Version == before) return;
        Save(ArtistCredit.Separators.ToList());
        var library = _shell.Library.Service;
        library.NotifyMetadataChanged();
        // GitHub #117 (the desktop's ApplyArtistGrouping): credits stored joined with the old join
        // text may no longer split; that pass re-reads them and raises LibraryUpdated itself.
        if (!string.Equals(joinBefore, ArtistCredit.JoinText, StringComparison.Ordinal))
            _ = ApplyJoinAsync(library);
    }

    private static async Task ApplyJoinAsync(ILibraryService library)
    {
        try { await library.ApplyArtistCreditJoinAsync(); }
        catch (Exception ex) { DebugLog.Write("Settings", $"Artist credit join pass failed: {ex.Message}"); }
    }

    /// <summary>One update (load-modify-save in one step) of this field only, chained so quick
    /// edits land in order.</summary>
    private void Save(List<string> separators)
    {
        _saveChain = _saveChain.ContinueWith(async _ =>
        {
            try { await _shell.Library.Persistence.UpdateSettingsAsync(s => s.ArtistTagSeparators = separators); }
            catch (Exception ex) { DebugLog.Write("Settings", $"Artist separators save failed: {ex.Message}"); }
        }, TaskScheduler.Default).Unwrap();
    }
}
