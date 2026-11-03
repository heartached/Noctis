using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// The Account page's view-only behaviour: the header's hairline once the page scrolls under
/// it; the keyboard's Next / Go walking address → name → password → Sign In; the address
/// checked when its field is left; the focused field kept above the keyboard; an error that
/// appears scrolled into sight; and "Synced 5 min ago" re-read once a minute while the page shows.
/// </summary>
public partial class AccountPage : UserControl
{
    private IInputPane? _inputPane;
    private DispatcherTimer? _ticker;
    private DateTime _focusMovedUtc;
    /// <summary>How much of the window the keyboard covers (0 when it is down).</summary>
    private double _covered;
    private AccountPageViewModel? _vm;

    public AccountPage()
    {
        InitializeComponent();
        PageScroll.ScrollChanged += (_, _) => UpdateHairline();
        ServerAddressBox.KeyDown += OnFieldKeyDown;
        UserNameBox.KeyDown += OnFieldKeyDown;
        PasswordBox.KeyDown += OnFieldKeyDown;
        ServerAddressBox.LostFocus += (_, _) => (DataContext as AccountPageViewModel)?.CheckAddress();
        foreach (var box in new[] { ServerAddressBox, UserNameBox, PasswordBox })
            box.GotFocus += OnFieldGotFocus;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as AccountPageViewModel;
        if (_vm != null) _vm.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>A failed sign-in brings the form back at its top, and the error under the
    /// password sat below the fold, under the player bar: it is scrolled into the middle.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AccountPageViewModel.ErrorText) || _vm is not { HasError: true } vm) return;
        Control label = vm.HasAddressError ? AddressErrorLabel : vm.HasCredentialsError ? CredentialsErrorLabel : ErrorLabel;
        Dispatcher.UIThread.Post(() =>
        {
            if (!label.IsEffectivelyVisible || label.TranslatePoint(default, PageScroll) is not { } at) return;
            var visible = PageScroll.Bounds.Height - _covered;
            if (at.Y >= 0 && at.Y + label.Bounds.Height <= visible * 0.7) return;   // already in plain sight
            PageScroll.Offset = PageScroll.Offset.WithY(Math.Max(0, PageScroll.Offset.Y + at.Y - visible * 0.4));
        }, DispatcherPriority.Background);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _inputPane = TopLevel.GetTopLevel(this)?.InputPane;
        if (_inputPane != null) _inputPane.StateChanged += OnInputPaneChanged;
        _ticker ??= new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background,
            (_, _) => (DataContext as AccountPageViewModel)?.RefreshSyncTime());
        _ticker.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_inputPane != null) _inputPane.StateChanged -= OnInputPaneChanged;
        _inputPane = null;
        _ticker?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateHairline() => HeaderHairline.Opacity = PageScroll.Offset.Y > 0.5 ? 1 : 0;

    /// <summary>
    /// Enter (the keyboard's Next / Go) moves on: address → name → password, and on the password
    /// signs in. Android's input connection may move focus for Next itself and then send the
    /// Enter to the field that just took focus; an Enter landing within a moment of a field
    /// gaining focus is that same key press and is not acted on twice.
    /// </summary>
    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        e.Handled = true;
        if (DateTime.UtcNow - _focusMovedUtc < TimeSpan.FromMilliseconds(250)) return;
        var vm = DataContext as AccountPageViewModel;
        if (box == ServerAddressBox)
        {
            vm?.CheckAddress();
            UserNameBox.Focus(NavigationMethod.Tab);
        }
        else if (box == UserNameBox)
        {
            PasswordBox.Focus(NavigationMethod.Tab);
        }
        else if (vm?.CanSignIn == true)
        {
            // Focus on the button sends the keyboard away, so its progress and the answer show.
            SignInButton.Focus();
            vm.SignInCommand.Execute(null);
        }
    }

    private void OnFieldGotFocus(object? sender, FocusChangedEventArgs e)
    {
        _focusMovedUtc = DateTime.UtcNow;
        if (sender is Control field) KeepAboveKeyboard(field);
    }

    /// <summary>The keyboard covers the page's lower part, and the fields sit under the hero and
    /// the steps: the page gets room below to scroll the focused field up into the part left.</summary>
    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        var height = TopLevel.GetTopLevel(this)?.Bounds.Height ?? 0;
        var covered = e.NewState == InputPaneState.Open && height > 0 ? Math.Max(0, height - e.EndRect.Top) : 0;
        _covered = covered;
        var margin = PageContent.Margin;
        PageContent.Margin = new Thickness(margin.Left, margin.Top, margin.Right, 24 + covered);
        if (covered > 0 && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control focused
            && focused.FindAncestorOfType<AccountPage>() == this)
            KeepAboveKeyboard(focused);
    }

    /// <summary>Scrolls the field's row (label, field, its error) into the part of the page the
    /// keyboard leaves. BringIntoView alone does not: the page's viewport runs on under the keyboard.</summary>
    private void KeepAboveKeyboard(Control field) => Dispatcher.UIThread.Post(() =>
    {
        var row = field.Parent as Control ?? field;
        if (TopLevel.GetTopLevel(this) is not { } top || row.TranslatePoint(default, PageScroll) is not { } at
            || PageScroll.TranslatePoint(default, top) is not { } scrollAt) return;
        var visibleBottom = Math.Min(PageScroll.Bounds.Height, top.Bounds.Height - _covered - scrollAt.Y) - 12;
        var rowBottom = at.Y + row.Bounds.Height;
        if (rowBottom > visibleBottom) PageScroll.Offset = PageScroll.Offset.WithY(PageScroll.Offset.Y + rowBottom - visibleBottom);
        else if (at.Y < 0) PageScroll.Offset = PageScroll.Offset.WithY(Math.Max(0, PageScroll.Offset.Y + at.Y - 12));
    }, DispatcherPriority.Background);
}
