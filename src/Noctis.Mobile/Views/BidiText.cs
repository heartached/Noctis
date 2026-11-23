using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Controls;

namespace Noctis.Mobile.Views;

/// <summary>
/// A text reads in its own direction inside a layout that reads the other way (the phone in
/// Arabic, ShellViewModel.FlowDirection). Every TextBlock inherited the layout's right-to-left
/// direction, so a Latin title's leading and trailing neutrals took it too: "¿Cuál Es Tu Plan? -
/// Single" showed as "Cuál Es Tu Plan? - Single¿", "(Remix) …" and "#1 Hits" broke the same way.
/// With <see cref="IsAutoProperty"/> on (MobileStyles sets it on every phone TextBlock) the text's
/// first strong character (<see cref="TextDirection.FirstStrong"/>) sets its direction when it
/// differs from its parent's, and its alignment keeps to the layout's edges: a Latin title in an
/// Arabic row still sits against the right edge (Start becomes End, End becomes Start; centred,
/// left and right stay). Worked out when the text or the parent's direction changes, never per frame.
/// </summary>
public static class BidiText
{
    public static readonly AttachedProperty<bool> IsAutoProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("IsAuto", typeof(BidiText));

    /// <summary>The alignment the text had before this turned it (restored when the directions agree again).</summary>
    private static readonly AttachedProperty<TextAlignment?> IntendedAlignmentProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, TextAlignment?>("IntendedAlignment", typeof(BidiText));

    private static readonly AttachedProperty<IDisposable?> ParentWatchProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IDisposable?>("ParentWatch", typeof(BidiText));

    public static bool GetIsAuto(TextBlock t) => t.GetValue(IsAutoProperty);
    public static void SetIsAuto(TextBlock t, bool value) => t.SetValue(IsAutoProperty, value);

    static BidiText()
    {
        IsAutoProperty.Changed.AddClassHandler<TextBlock>((t, e) =>
        {
            if (e.NewValue is true)
            {
                t.AttachedToVisualTree += OnAttached;
                t.DetachedFromVisualTree += OnDetached;
                if (t.IsAttachedToVisualTree()) Watch(t);
                Update(t);
            }
            else
            {
                t.AttachedToVisualTree -= OnAttached;
                t.DetachedFromVisualTree -= OnDetached;
                Unwatch(t);
                Restore(t);
            }
        });
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((t, _) =>
        {
            if (t.GetValue(IsAutoProperty)) Update(t);
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBlock t) return;
        Watch(t);
        Update(t);
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBlock t) Unwatch(t);
    }

    /// <summary>The parent's direction changes with the language: the text is looked at again.</summary>
    private static void Watch(TextBlock t)
    {
        Unwatch(t);
        if (t.GetVisualParent() is not { } parent) return;
        t.SetValue(ParentWatchProperty, parent.GetPropertyChangedObservable(Visual.FlowDirectionProperty)
            .Subscribe(new Observer(() => Update(t))));
    }

    private static void Unwatch(TextBlock t)
    {
        t.GetValue(ParentWatchProperty)?.Dispose();
        t.ClearValue(ParentWatchProperty);
    }

    internal static void Update(TextBlock t)
    {
        var layout = t.GetVisualParent()?.FlowDirection ?? FlowDirection.LeftToRight;
        var own = TextDirection.FirstStrong(t.Text);
        if (own is not { } direction || direction == layout)
        {
            Restore(t);
            return;
        }
        if (t.GetValue(IntendedAlignmentProperty) is null) t.SetValue(IntendedAlignmentProperty, t.TextAlignment);
        t.FlowDirection = direction;
        t.SetCurrentValue(TextBlock.TextAlignmentProperty, t.GetValue(IntendedAlignmentProperty) switch
        {
            TextAlignment.Start => TextAlignment.End,
            TextAlignment.End => TextAlignment.Start,
            { } other => other,
            null => TextAlignment.End,
        });
    }

    private static void Restore(TextBlock t)
    {
        t.ClearValue(Visual.FlowDirectionProperty);
        if (t.GetValue(IntendedAlignmentProperty) is { } intended)
        {
            t.SetCurrentValue(TextBlock.TextAlignmentProperty, intended);
            t.ClearValue(IntendedAlignmentProperty);
        }
    }

    private sealed class Observer(Action onNext) : IObserver<AvaloniaPropertyChangedEventArgs>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(AvaloniaPropertyChangedEventArgs value) => onNext();
    }
}
