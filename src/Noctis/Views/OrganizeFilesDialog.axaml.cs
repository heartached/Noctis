using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class OrganizeFilesDialog : Window
{
    // Until the pattern has had the caret, a tag chip appends instead of landing at index 0.
    private bool _patternHadFocus;

    public OrganizeFilesDialog()
    {
        InitializeComponent();
        PatternBox.GotFocus += (_, _) => _patternHadFocus = true;
        TokenChips.AddHandler(Button.ClickEvent, OnTokenClick);
    }

    public OrganizeFilesDialog(OrganizeFilesViewModel vm) : this()
    {
        DataContext = vm;
        // PillDialogHost turns this into the animated close; nothing is returned through
        // Close(result), so the deferred close loses nothing.
        vm.Closed += (_, _) => Close();
    }

    /// <summary>A tag chip: its token goes in at the pattern's caret (replacing a selection).
    /// The two-way binding carries the new text to the view model.</summary>
    private void OnTokenClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Control { DataContext: OrganizeFilesViewModel.PatternToken chip }) return;
        e.Handled = true;
        InsertToken(chip.Token);
    }

    internal void InsertToken(string token)
    {
        var text = PatternBox.Text ?? string.Empty;
        var at = _patternHadFocus ? Math.Clamp(PatternBox.CaretIndex, 0, text.Length) : text.Length;
        var selStart = Math.Min(PatternBox.SelectionStart, PatternBox.SelectionEnd);
        var selEnd = Math.Max(PatternBox.SelectionStart, PatternBox.SelectionEnd);
        if (_patternHadFocus && selEnd > selStart && selEnd <= text.Length)
        {
            text = text.Remove(selStart, selEnd - selStart);
            at = selStart;
        }
        // A tag dropped right against another tag or word would glue the two values together
        // ("{Title}{Year}" → "PROMOTION2024", seen live 10-08): pad it with a space on that side.
        // Next to a separator (/ \ - _ space . brackets) it goes in as is.
        var insert = token;
        if (at > 0 && GluesTo(text[at - 1])) insert = " " + insert;
        if (at < text.Length && GluesTo(text[at])) insert += " ";
        PatternBox.Text = text.Insert(at, insert);
        PatternBox.Focus();
        PatternBox.CaretIndex = at + insert.Length;
    }

    private static bool GluesTo(char c) => char.IsLetterOrDigit(c) || c is '{' or '}';

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the same way the Close button does.
        if (e.Key == Key.Escape && DataContext is OrganizeFilesViewModel vm)
        {
            e.Handled = true;
            vm.CloseCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
