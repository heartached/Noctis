using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class ThemeEditorDialog : Window
{
    public CustomThemeDefinition? Result { get; private set; }

    public ThemeEditorDialog()
    {
        InitializeComponent();
    }

    public ThemeEditorDialog(ThemeEditorViewModel vm) : this()
    {
        DataContext = vm;
        vm.Saved += def => { Result = def; Close(); };
        vm.Cancelled += () => { Result = null; Close(); };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
