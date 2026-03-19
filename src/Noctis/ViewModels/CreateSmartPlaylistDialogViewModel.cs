using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public RuleField[] AllFields { get; } = Enum.GetValues<RuleField>();
    public SmartPlaylistSortBy[] AllSortOptions { get; } = Enum.GetValues<SmartPlaylistSortBy>();

    public event EventHandler<Playlist>? SmartPlaylistCreated;
    public event EventHandler? CloseRequested;

    public CreateSmartPlaylistDialogViewModel(ILibraryService library)
    {
        _library = library;
        AddRule();
    }

    [RelayCommand]
    private void AddRule()
    {
        var ruleVm = new SmartPlaylistRuleViewModel
        {
            AvailableOperators = SmartPlaylistEvaluator.GetOperatorsForField(RuleField.Artist)
        };
        ruleVm.PropertyChanged += (_, _) => UpdatePreviewCount();
        Rules.Add(ruleVm);
        UpdatePreviewCount();
    }

    [RelayCommand]
    private void RemoveRule(SmartPlaylistRuleViewModel rule)
    {
        Rules.Remove(rule);
        UpdatePreviewCount();
    }

    [RelayCommand]
    private void Create()
    {
        if (string.IsNullOrWhiteSpace(PlaylistName))
        {
            ShowNameRequiredError = true;
            return;
        }
        if (Rules.Count == 0) return;

        var playlist = new Playlist
        {
            Name = PlaylistName.Trim(),
            Description = PlaylistDescription.Trim(),
            Color = Playlist.GetRandomColor(),
            IsSmartPlaylist = true,
            Rules = Rules.Select(r => r.ToModel()).ToList(),
            MatchAll = MatchAll,
            LimitCount = HasLimit ? LimitCount : null,
            SortBy = HasLimit ? (SortBy ?? SmartPlaylistSortBy.MostPlayed) : null
        };

        SmartPlaylistCreated?.Invoke(this, playlist);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePreviewCount()
    {
        if (Rules.Count == 0)
        {
            MatchingTrackCount = 0;
            return;
        }

        var tempPlaylist = new Playlist
        {
            IsSmartPlaylist = true,
            Rules = Rules.Select(r => r.ToModel()).ToList(),
            MatchAll = MatchAll,
            LimitCount = HasLimit ? LimitCount : null,
            SortBy = HasLimit ? (SortBy ?? SmartPlaylistSortBy.MostPlayed) : null
        };

        var matches = SmartPlaylistEvaluator.Evaluate(tempPlaylist, _library.Tracks);
        MatchingTrackCount = matches.Count;
    }

    partial void OnPlaylistNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            ShowNameRequiredError = false;
    }

    partial void OnMatchAllChanged(bool value) => UpdatePreviewCount();
    partial void OnHasLimitChanged(bool value) => UpdatePreviewCount();
    partial void OnLimitCountChanged(int value) => UpdatePreviewCount();
    partial void OnSortByChanged(SmartPlaylistSortBy? value) => UpdatePreviewCount();
}
