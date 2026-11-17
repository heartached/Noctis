using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

/// <summary>One row of Settings › Tab Bar: a tab, its switch (on the bar or not) and whether the
/// switch can move now (Library never; none past the limits).</summary>
public sealed partial class TabBarChoice : ObservableObject
{
    private readonly TabBarSettingsViewModel _owner;

    internal TabBarChoice(TabBarSettingsViewModel owner, MobileTab tab, bool isOn)
    {
        _owner = owner;
        Tab = tab;
        _isOn = isOn;
    }

    public MobileTab Tab { get; }

    /// <summary>What the view's icon styles match on (the tab's name).</summary>
    public string Key => Tab.ToString();

    public string Title => Loc.T(TabBarTabs.LabelKey(Tab));

    /// <summary>The handle's name for screen readers: "Reorder Songs".</summary>
    public string ReorderName => Loc.T("Mobile.TabBar.ReorderName", Title);

    [ObservableProperty] private bool _isOn;

    [ObservableProperty] private bool _canToggle = true;

    partial void OnIsOnChanged(bool value) => _owner.OnChoiceToggled(this);

    internal void Relabel()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ReorderName));
    }
}

/// <summary>
/// Settings › Tab Bar (Discord #mobile-beta 11-17: "a way to select the tabs at the bottom and maybe
/// even rearrange them"): every tab with a switch, the ones on the bar first in their order, a ≡
/// handle to drag a row into place (the Library rows' Edit drag), and Reset to Default. Each change
/// goes to the shell at once (ShellViewModel.SetBarTabs), which moves the bar and saves it.
/// </summary>
public sealed partial class TabBarSettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _syncing;

    public TabBarSettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Sync();
    }

    /// <summary>Every tab: those on the bar in its order, then the others.</summary>
    public ObservableCollection<TabBarChoice> Choices { get; } = new();

    /// <summary>The bar is the default one: Reset to Default has nothing to do.</summary>
    public bool IsDefault => _shell.BarTabs.SequenceEqual(TabBarTabs.Defaults);

    /// <summary>Rebuilds the rows from the shell's bar (opened, reset, or the saved bar read at start).</summary>
    public void Sync()
    {
        _syncing = true;
        try
        {
            var bar = _shell.BarTabs;
            var order = bar.Concat(TabBarTabs.All.Where(t => !bar.Contains(t))).ToList();
            if (!order.SequenceEqual(Choices.Select(c => c.Tab)))
            {
                Choices.Clear();
                foreach (var tab in order) Choices.Add(new TabBarChoice(this, tab, bar.Contains(tab)));
            }
            else
                foreach (var choice in Choices) choice.IsOn = bar.Contains(choice.Tab);
            RefreshLimits();
        }
        finally
        {
            _syncing = false;
        }
        OnPropertyChanged(nameof(IsDefault));
    }

    /// <summary>A switch flipped: within the limits it goes to the bar, past them it flips back.</summary>
    internal void OnChoiceToggled(TabBarChoice choice)
    {
        if (_syncing) return;
        var on = Choices.Count(c => c.IsOn);
        if (choice.Tab == TabBarTabs.Locked || on < TabBarTabs.Min || on > TabBarTabs.Max)
        {
            _syncing = true;
            choice.IsOn = !choice.IsOn;
            _syncing = false;
            return;
        }
        Apply();
    }

    /// <summary>Moves a row (a handle drag's drop); indices are clamped to the list. A tab on the bar
    /// takes its new place there.</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= Choices.Count) return;
        to = Math.Clamp(to, 0, Choices.Count - 1);
        if (to == from) return;
        Choices.Move(from, to);
        Apply();
    }

    [RelayCommand]
    private void Reset()
    {
        _shell.SetBarTabs(TabBarTabs.Defaults);
        Sync();
    }

    private void Apply()
    {
        _shell.SetBarTabs(Choices.Where(c => c.IsOn).Select(c => c.Tab));
        RefreshLimits();
        OnPropertyChanged(nameof(IsDefault));
    }

    /// <summary>Library stays on; nothing switches off at the minimum, nothing on at the maximum.</summary>
    private void RefreshLimits()
    {
        var on = Choices.Count(c => c.IsOn);
        foreach (var c in Choices)
            c.CanToggle = c.Tab != TabBarTabs.Locked && (c.IsOn ? on > TabBarTabs.Min : on < TabBarTabs.Max);
    }

    public void Relabel()
    {
        foreach (var c in Choices) c.Relabel();
    }
}
