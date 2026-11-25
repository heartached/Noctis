using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>One separator on Settings › Genre Separators, with its Remove button's spoken name.</summary>
public sealed record GenreSeparatorRow(string Text)
{
    public string RemoveName => Loc.T("Mobile.Separators.RemoveName", Text);
}

/// <summary>
/// Settings › Genre Separators (GitHub #260 follow-up): the desktop's Settings › Library › Genre Tag
/// Separators on the phone, built like <see cref="ArtistSeparatorsViewModel"/>. The list of
/// separators, each with Remove, a field with Add, and Reset to Default; the desktop's rules
/// (<see cref="GenreSeparators.Normalize"/>: trimmed, no repeat ignoring case, ';' always splits so
/// it is never listed, an empty list is fine). The process-wide splitter is the state: each change
/// goes there at once, the library rebuilds its albums (their genre lines follow) and the list is
/// saved in the phone's settings (AppSettings.GenreTagSeparators), read back at start by
/// <see cref="ApplySaved"/>.
/// </summary>
public sealed partial class GenreSeparatorsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private Task _saveChain = Task.CompletedTask;

    public GenreSeparatorsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Sync();
    }

    /// <summary>The tail of the separator saves; tests await it.</summary>
    internal Task PendingSave => _saveChain;

    /// <summary>The separators in use besides ';', in their order.</summary>
    public ObservableCollection<GenreSeparatorRow> Separators { get; } = new();

    /// <summary>The field under the list.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _newSeparator = string.Empty;

    /// <summary>The shipped separators are the ones in use: Reset to Default has nothing to do.</summary>
    public bool IsDefault => Separators.Select(r => r.Text).SequenceEqual(GenreSeparators.Defaults, StringComparer.Ordinal);

    /// <summary>None listed: the card says only semicolons split.</summary>
    public bool IsEmpty => Separators.Count == 0;

    /// <summary>The main sheet's row value: how many separators are in use ("1").</summary>
    public string Summary => Separators.Count.ToString(Loc.Instance.Culture);

    /// <summary>The settings the phone starts with, into the splitter: called before the library
    /// loads, so the first genre read (an album page's genre line) already uses them. The same call
    /// as LibraryService's startup read, which on the phone runs only after the library is up.</summary>
    public static void ApplySaved(AppSettings settings) => GenreSeparators.Configure(settings.GenreTagSeparators);

    /// <summary>Rebuilds the rows from the splitter (opened, changed, the language switched).</summary>
    public void Sync()
    {
        var active = GenreSeparators.Configured;
        if (!active.SequenceEqual(Separators.Select(r => r.Text), StringComparer.Ordinal))
        {
            Separators.Clear();
            foreach (var s in active) Separators.Add(new GenreSeparatorRow(s));
        }
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(IsEmpty));
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
        var current = Separators.Select(r => r.Text).ToList();
        if (!GenreSeparators.IsAddable(value, current)) return;
        current.Add(value);
        Apply(current);
    }

    [RelayCommand]
    private void Remove(GenreSeparatorRow? row)
    {
        if (row == null) return;
        var current = Separators.Select(r => r.Text).ToList();
        // Unlike the artist separators the last one can go: ';' still splits.
        if (!current.Remove(row.Text)) return;
        Apply(current);
    }

    [RelayCommand]
    private void Reset() => Apply(GenreSeparators.Defaults);

    /// <summary>
    /// Into the splitter, then the rows from what it took, the save, and a regroup when the split
    /// really changed. Genres split when read, so no file or stored value changes: LibraryService
    /// rebuilds its albums off the UI thread and raises LibraryUpdated, and an open album page
    /// re-reads its fresh album (its "Genre · Year" line).
    /// </summary>
    private void Apply(IEnumerable<string> separators)
    {
        var before = GenreSeparators.Version;
        GenreSeparators.Configure(separators);
        Sync();
        if (GenreSeparators.Version == before) return;
        Save(GenreSeparators.Configured.ToList());
        _shell.Library.Service.NotifyMetadataChanged();
    }

    /// <summary>One update (load-modify-save in one step) of this field only, chained so quick
    /// edits land in order.</summary>
    private void Save(List<string> separators)
    {
        _saveChain = _saveChain.ContinueWith(async _ =>
        {
            try { await _shell.Library.Persistence.UpdateSettingsAsync(s => s.GenreTagSeparators = separators); }
            catch (Exception ex) { DebugLog.Write("Settings", $"Genre separators save failed: {ex.Message}"); }
        }, TaskScheduler.Default).Unwrap();
    }
}
