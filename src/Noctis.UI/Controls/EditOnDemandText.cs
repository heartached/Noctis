using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Noctis.Controls;

/// <summary>
/// Text that turns into a <see cref="TextBox"/> the moment it is clicked or focused. Until then it
/// is a plain <see cref="TextBlock"/> in a border: a TextBox is several templated controls with
/// their own styles, and the Lyrics Studio's review builds one row per line (owner 11-26: the first
/// scroll through a song stuttered while each row was built). The editor is made once, in place,
/// with the caret where the text was clicked (at the end when tabbed into), and stays for the row.
/// </summary>
public class EditOnDemandText : Decorator
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<EditOnDemandText, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<TextWrapping> TextWrappingProperty =
        TextBlock.TextWrappingProperty.AddOwner<EditOnDemandText>();

    /// <summary>The text's inset, the editor's Padding (so the text does not move when it becomes editable).</summary>
    public static readonly StyledProperty<Thickness> TextPaddingProperty =
        AvaloniaProperty.Register<EditOnDemandText, Thickness>(nameof(TextPadding));

    public static readonly StyledProperty<ControlTheme?> EditorThemeProperty =
        AvaloniaProperty.Register<EditOnDemandText, ControlTheme?>(nameof(EditorTheme));

    /// <summary>A style class given to the editor (its look comes from the styles for that class).</summary>
    public static readonly StyledProperty<string?> EditorClassProperty =
        AvaloniaProperty.Register<EditOnDemandText, string?>(nameof(EditorClass));

    private readonly Border _display;
    private readonly TextBlock _text;

    public EditOnDemandText()
    {
        Focusable = true;
        _text = new TextBlock();
        _text.Bind(TextBlock.TextProperty, new Binding(nameof(Text)) { Source = this, Mode = BindingMode.OneWay });
        _text.Bind(TextBlock.TextWrappingProperty, new Binding(nameof(TextWrapping)) { Source = this, Mode = BindingMode.OneWay });
        _text.Bind(TextBlock.PaddingProperty, new Binding(nameof(TextPadding)) { Source = this, Mode = BindingMode.OneWay });
        // The text box's own font (the inherited one), not the app's TextBlock style: Inter SemiBold
        // wraps and spaces lines differently, so the row changed height when the text box came in.
        _text[!TextBlock.FontFamilyProperty] = this[!TextElement.FontFamilyProperty];
        _display = new Border { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Ibeam), Child = _text };
        Child = _display;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public Thickness TextPadding
    {
        get => GetValue(TextPaddingProperty);
        set => SetValue(TextPaddingProperty, value);
    }

    public ControlTheme? EditorTheme
    {
        get => GetValue(EditorThemeProperty);
        set => SetValue(EditorThemeProperty, value);
    }

    public string? EditorClass
    {
        get => GetValue(EditorClassProperty);
        set => SetValue(EditorClassProperty, value);
    }

    /// <summary>The editor, once made (null while the text is only shown).</summary>
    public TextBox? Editor { get; private set; }

    /// <summary>Raised when the editor is made, before it takes focus: the owner wires its handlers here.</summary>
    public event EventHandler<TextBox>? EditorCreated;

    /// <summary>Makes the editor (if not made yet) with the caret at <paramref name="caretIndex"/> (null: the end) and focuses it.</summary>
    public TextBox Edit(int? caretIndex = null, NavigationMethod method = NavigationMethod.Unspecified)
    {
        if (Editor is { } existing)
        {
            if (caretIndex is { } at) existing.CaretIndex = at;
            existing.Focus(method);
            return existing;
        }
        var editor = new TextBox { TextWrapping = TextWrapping, Padding = TextPadding };
        if (EditorTheme is { } theme) editor.Theme = theme;
        if (!string.IsNullOrEmpty(EditorClass)) editor.Classes.Add(EditorClass);
        editor.Bind(TextBox.TextProperty, new Binding(nameof(Text)) { Source = this, Mode = BindingMode.TwoWay });
        EditorCreated?.Invoke(this, editor);
        Editor = editor;
        Focusable = false;
        Child = editor;
        var caret = Math.Clamp(caretIndex ?? (Text?.Length ?? 0), 0, Text?.Length ?? 0);
        editor.CaretIndex = caret;
        editor.Focus(method);
        // A new text box takes the focus once it is laid out, and its text arriving in its template
        // puts the caret at the end: both are settled again after this layout pass (and after the
        // click that made it has finished its route).
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(Editor, editor)) return;
            if (!editor.IsFocused) editor.Focus(method);
            editor.CaretIndex = caret;
        }, DispatcherPriority.Loaded);
        return editor;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Editor is not null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // The caret goes where the text was clicked, as in a text box.
        var p = e.GetPosition(_text) - new Point(TextPadding.Left, TextPadding.Top);
        int? caret = null;
        try { caret = _text.TextLayout.HitTestPoint(p).TextPosition; } catch { /* no layout yet: the end */ }
        Edit(caret, NavigationMethod.Pointer);
        e.Handled = true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        // Tabbed or arrowed into: the editor takes the focus at once, so typing edits the line.
        if (!ReferenceEquals(e.Source, this)) return;
        // A click focuses this host before its press reaches it: the press makes the editor, with
        // the caret where it landed. Tab, arrows or code: the editor now, caret at the end.
        if (Editor is null) { if (e.NavigationMethod != NavigationMethod.Pointer) Edit(null, e.NavigationMethod); }
        // The window's click handling can still hand the focus to this host after the editor took it.
        else if (Editor is { } editor)
            Dispatcher.UIThread.Post(() => { if (!editor.IsFocused && ReferenceEquals(Editor, editor)) editor.Focus(e.NavigationMethod); }, DispatcherPriority.Background);
    }
}
