using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for a single rule row in the smart playlist rule builder.
/// </summary>
public partial class SmartPlaylistRuleViewModel : ViewModelBase
{
    [ObservableProperty] private RuleField? _selectedField = RuleField.Artist;
    [ObservableProperty] private RuleOperator? _selectedOperator = RuleOperator.Contains;
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private string _value2 = string.Empty;
    [ObservableProperty] private RuleOperator[] _availableOperators = [];

    /// <summary>
    /// Drives the row's <see cref="Noctis.Controls.CollapsibleContent"/> — the same glide
    /// the Settings ▸ Integrations "Show Album on Discord" block uses. A new rule is built
    /// shut and opened once its container has been laid out; removing one shuts it first
    /// and drops it from the collection when the fold has played out.
    /// </summary>
    [ObservableProperty] private bool _isRevealed;

    public bool ShowValueInput => SelectedOperator is not RuleOperator.IsTrue
                                  and not RuleOperator.IsFalse;
    public bool ShowValue2Input => SelectedOperator == RuleOperator.Between;

    public string FieldDisplayName =>
        SmartPlaylistEvaluator.GetFieldDisplayName(SelectedField ?? RuleField.Artist);

    public string OperatorDisplayName =>
        SmartPlaylistEvaluator.GetOperatorDisplayName(SelectedOperator ?? RuleOperator.Contains);

    partial void OnSelectedFieldChanged(RuleField? value)
    {
        var safeField = value ?? RuleField.Artist;
        if (value is null)
        {
            SelectedField = safeField;
            return;
        }

        AvailableOperators = SmartPlaylistEvaluator.GetOperatorsForField(safeField);
        if (AvailableOperators.Length == 0)
            AvailableOperators = [RuleOperator.Contains];

        if (SelectedOperator is null || !AvailableOperators.Contains(SelectedOperator.Value))
            SelectedOperator = AvailableOperators[0];

        OnPropertyChanged(nameof(ShowValueInput));
        OnPropertyChanged(nameof(ShowValue2Input));
        OnPropertyChanged(nameof(FieldDisplayName));
    }

    partial void OnSelectedOperatorChanged(RuleOperator? value)
    {
        if (value is null && AvailableOperators.Length > 0)
        {
            SelectedOperator = AvailableOperators[0];
            return;
        }

        OnPropertyChanged(nameof(ShowValueInput));
        OnPropertyChanged(nameof(ShowValue2Input));
        OnPropertyChanged(nameof(OperatorDisplayName));
    }

    public SmartPlaylistRule ToModel() => new()
    {
        Field = SelectedField ?? RuleField.Artist,
        Operator = SelectedOperator ?? RuleOperator.Contains,
        Value = Value,
        Value2 = ShowValue2Input ? Value2 : null
    };
}

/// <summary>
/// ViewModel for the Create Smart Playlist dialog.
/// </summary>
public partial class CreateSmartPlaylistDialogViewModel : ViewModelBase
{
    private readonly ILibraryService _library;

    [ObservableProperty] private string _playlistName = string.Empty;
    [ObservableProperty] private string _playlistDescription = string.Empty;
    [ObservableProperty] private bool _showNameRequiredError;
    [ObservableProperty] private bool _matchAll = true;
    [ObservableProperty] private bool _hasLimit;
    [ObservableProperty] private int _limitCount = 25;
    [ObservableProperty] private SmartPlaylistSortBy? _sortBy = SmartPlaylistSortBy.MostPlayed;
    [ObservableProperty] private int _matchingTrackCount;

    public ObservableCollection<SmartPlaylistRuleViewModel> Rules { get; } = new();

    /// <summary>
    /// Rules whose ✕ was clicked but whose row is still folding away (they stay in
    /// <see cref="Rules"/> until the glide ends). They no longer count: not in the preview,
    /// not in the playlist Create builds — Create inside the fold used to save them.
    /// </summary>
    private readonly HashSet<SmartPlaylistRuleViewModel> _removing = new();

    /// <summary>The rules that count: every row except the ones folding away.</summary>
    private IReadOnlyList<SmartPlaylistRuleViewModel> ActiveRules =>
        _removing.Count == 0 ? Rules : Rules.Where(r => !_removing.Contains(r)).ToList();

    /// <summary>The "Any" segment of the Match All / Any choice: the other side of
    /// <see cref="MatchAll"/>, so each segment binds two-way to a plain bool.</summary>
    public bool MatchAny
    {
        get => !MatchAll;
        set => MatchAll = !value;
    }

    /// <summary>At least one rule counts; Create needs one (it used to do nothing, silently,
    /// once every rule had been removed).</summary>
    public bool HasRules => ActiveRules.Count > 0;

    /// <summary>The footer's live count ("Matches 4,353 tracks"), localized, with the
    /// singular and the no-rules case.</summary>
    public string MatchCountText =>
        !HasRules ? Loc.T("CreateSmartPlaylist.NoRules")
        : MatchingTrackCount == 1 ? Loc.T("CreateSmartPlaylist.MatchesOne")
        : Loc.T("CreateSmartPlaylist.MatchesCount", MatchingTrackCount);

    public RuleField[] AllFields { get; } = Enum.GetValues<RuleField>();
    public SmartPlaylistSortBy[] AllSortOptions { get; } = Enum.GetValues<SmartPlaylistSortBy>();

    public event EventHandler<Playlist>? SmartPlaylistCreated;
    public event EventHandler? CloseRequested;

    /// <summary>
    /// How long the row's fold takes to shut — CollapsibleContent's Glide close. A removed
    /// rule leaves the collection only after this, so the row folds away instead of
    /// vanishing under the pointer.
    /// </summary>
    private static readonly TimeSpan RuleCloseDuration = TimeSpan.FromMilliseconds(240);

    public CreateSmartPlaylistDialogViewModel(ILibraryService library)
    {
        _library = library;
        // The dialog's own opening animation covers the first rule, so it starts open
        // rather than gliding in behind the card's fade.
        AddRule(revealed: true);
    }

    [RelayCommand]
    private void AddRule() => AddRule(revealed: false);

    private void AddRule(bool revealed)
    {
        var ruleVm = new SmartPlaylistRuleViewModel
        {
            AvailableOperators = SmartPlaylistEvaluator.GetOperatorsForField(RuleField.Artist),
            IsRevealed = revealed,
        };
        // Only what the rule says re-counts. Every other change (the reveal, the operator
        // list, the Show* flags) used to re-run the whole-library evaluation too — about six
        // times per field pick.
        ruleVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SmartPlaylistRuleViewModel.SelectedField)
                or nameof(SmartPlaylistRuleViewModel.SelectedOperator)
                or nameof(SmartPlaylistRuleViewModel.Value)
                or nameof(SmartPlaylistRuleViewModel.Value2))
                UpdatePreviewCount();
        };
        Rules.Add(ruleVm);
        UpdatePreviewCount();

        if (revealed) return;
        // Opened after the container exists and CollapsibleContent has armed its
        // transitions (it arms at Loaded priority on attach), so the row glides open
        // instead of snapping to full height on its first layout pass.
        Dispatcher.UIThread.Post(() => ruleVm.IsRevealed = true, DispatcherPriority.Background);
    }

    [RelayCommand]
    private void RemoveRule(SmartPlaylistRuleViewModel rule)
    {
        // A second click on a row that is already folding away does nothing.
        if (rule is null || !Rules.Contains(rule) || !_removing.Add(rule)) return;

        // Out of the count (and of Create) straight away — the count must not lag the
        // click — but the row stays in place, folding, until the glide has finished.
        rule.IsRevealed = false;
        UpdatePreviewCount();
        DispatcherTimer.RunOnce(() =>
        {
            Rules.Remove(rule);
            _removing.Remove(rule);
            UpdatePreviewCount();
        }, RuleCloseDuration);
    }

    /// <summary>Set once Create has handed the playlist over. Enter in the name field (a
    /// KeyBinding) still reached Create while the dialog animated out, building and firing
    /// a second playlist.</summary>
    private bool _created;

    [RelayCommand]
    private void Create()
    {
        if (_created) return;
        if (string.IsNullOrWhiteSpace(PlaylistName))
        {
            ShowNameRequiredError = true;
            return;
        }
        var rules = ActiveRules;
        if (rules.Count == 0) return;

        var playlist = new Playlist
        {
            Name = PlaylistName.Trim(),
            Description = PlaylistDescription.Trim(),
            Color = Playlist.GetRandomColor(),
            IsSmartPlaylist = true,
            Rules = rules.Select(r => r.ToModel()).ToList(),
            MatchAll = MatchAll,
            LimitCount = HasLimit ? LimitCount : null,
            SortBy = HasLimit ? (SortBy ?? SmartPlaylistSortBy.MostPlayed) : null
        };

        _created = true;
        SmartPlaylistCreated?.Invoke(this, playlist);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Counts matches for the rules that count (<see cref="ActiveRules"/>): a rule being
    /// removed is still in <see cref="Rules"/> while its row folds away, and used to come
    /// back into the count whenever anything else re-counted during the fold.
    /// </summary>
    private void UpdatePreviewCount()
    {
        var active = ActiveRules;
        OnPropertyChanged(nameof(HasRules));
        if (active.Count == 0)
        {
            MatchingTrackCount = 0;
            OnPropertyChanged(nameof(MatchCountText));
            return;
        }

        var tempPlaylist = new Playlist
        {
            IsSmartPlaylist = true,
            Rules = active.Select(r => r.ToModel()).ToList(),
            MatchAll = MatchAll,
            LimitCount = HasLimit ? LimitCount : null,
            SortBy = HasLimit ? (SortBy ?? SmartPlaylistSortBy.MostPlayed) : null
        };

        var matches = SmartPlaylistEvaluator.Evaluate(tempPlaylist, _library.Tracks);
        MatchingTrackCount = matches.Count;
        // Also when the number is unchanged: the no-rules text may have just gone away.
        OnPropertyChanged(nameof(MatchCountText));
    }

    partial void OnPlaylistNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            ShowNameRequiredError = false;
    }

    partial void OnMatchAllChanged(bool value)
    {
        OnPropertyChanged(nameof(MatchAny));
        UpdatePreviewCount();
    }
    partial void OnHasLimitChanged(bool value) => UpdatePreviewCount();
    partial void OnLimitCountChanged(int value) => UpdatePreviewCount();
    partial void OnSortByChanged(SmartPlaylistSortBy? value) => UpdatePreviewCount();
}
