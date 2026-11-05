using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Apple's action sheet on the phone's glass (owner 10-20: "confirm prompts → glass pop-ups"): a
/// title, a line on what follows, one or more actions (a destructive one in red), and Cancel. The
/// one question every "are you sure?" and every name entry asks through (<see cref="ShellViewModel.ShowPrompt"/>,
/// shown by PromptSheet): Sign Out, Remove Downloads, Remove Folder, Delete Playlist, and Rename
/// with its text field. A tap off the card, Back, or Cancel cancels; an action closes it first,
/// then runs.
/// </summary>
public sealed partial class PromptViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly Action? _cancelled;
    private bool _done;

    public PromptViewModel(ShellViewModel shell, string title, string message, IReadOnlyList<PromptAction> actions,
        Action? cancelled = null)
    {
        _shell = shell;
        _cancelled = cancelled;
        Title = title;
        Message = message;
        Actions = actions;
        foreach (var a in actions) a.Prompt = this;
    }

    public string Title { get; }
    public string Message { get; }
    public bool HasMessage => Message.Length > 0;

    /// <summary>Lines listed under the message in a short scrolling box (an import's lines
    /// that matched nothing).</summary>
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

    public bool HasDetails => Details.Count > 0;

    /// <summary>The last button's label: Cancel, or Done / OK on a prompt that only reports.</summary>
    public string CancelLabel { get; init; } = Noctis.Localization.Loc.T("Mobile.Cancel");
    public IReadOnlyList<PromptAction> Actions { get; }

    /// <summary>A name to type (Rename): the field shows, and the actions wait for a name.</summary>
    public bool HasField { get; init; }
    public string FieldPlaceholder { get; init; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _fieldText = string.Empty;

    /// <summary>The actions are live: always, or with a field, once it holds more than spaces.</summary>
    public bool CanConfirm => !HasField || FieldText.Trim().Length > 0;

    /// <summary>The field's text as the actions get it: trimmed.</summary>
    public string Entered => FieldText.Trim();

    [RelayCommand]
    private void Cancel() => _shell.ClosePrompt(this);

    /// <summary>Enter in the field: the first action, as its button would.</summary>
    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (Actions.Count > 0) await Actions[0].RunCommand.ExecuteAsync(null);
    }

    /// <summary>Closed without an action (Cancel, a tap off the card, Back, a page opening):
    /// the asker hears it once.</summary>
    internal void NotifyCancelled()
    {
        if (_done) return;
        _done = true;
        _cancelled?.Invoke();
    }

    /// <summary>An action ran: closing after it is not a cancel.</summary>
    internal bool TryTake()
    {
        if (_done || !CanConfirm) return false;
        _done = true;
        return true;
    }

    internal ShellViewModel Shell => _shell;
}

/// <summary>One of a prompt's buttons: a destructive one is the red pill, the rest the accent's.</summary>
public sealed partial class PromptAction
{
    private readonly Func<PromptViewModel, Task> _run;

    public PromptAction(string label, Func<PromptViewModel, Task> run, bool isDestructive = false)
    {
        Label = label;
        _run = run;
        IsDestructive = isDestructive;
    }

    public PromptAction(string label, Action run, bool isDestructive = false)
        : this(label, _ => { run(); return Task.CompletedTask; }, isDestructive) { }

    public string Label { get; }
    public bool IsDestructive { get; }
    public bool IsPrimary => !IsDestructive;

    internal PromptViewModel? Prompt { get; set; }

    /// <summary>The prompt closes first (an action that opens a page or another prompt must not
    /// land under it), then the action runs, once.</summary>
    [RelayCommand]
    private async Task RunAsync()
    {
        if (Prompt is not { } prompt || !prompt.TryTake()) return;
        prompt.Shell.ClosePrompt(prompt);
        await _run(prompt);
    }
}
