using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.ViewModels;

/// <summary>
/// Base class for all ViewModels. Provides INotifyPropertyChanged via
/// CommunityToolkit.Mvvm source generators.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
}

/// <summary>
/// Interface for ViewModels that support filtering via the search bar.
/// </summary>
public interface ISearchable
{
    /// <summary>
    /// Applies a search filter to the view's data.
    /// Empty string clears the filter.
    /// </summary>
    void ApplyFilter(string query);
}
