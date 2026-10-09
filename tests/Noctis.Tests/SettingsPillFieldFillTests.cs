using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: every rounded pill box in Settings (text fields, number fields, combo boxes,
/// greyed-out ones included) wears the metadata editor's filled pill: the PillFieldBackground
/// fill and no outline at rest. Mounts the REAL SettingsView styles (donor view, as
/// MediaServerOutlineParityTests does) and reads the resolved template chrome.
/// </summary>
public class SettingsPillFieldFillTests
{
    private static readonly Color Fill = Color.Parse("#1CFFFFFF");

    private readonly ITestOutputHelper _o;
    public SettingsPillFieldFillTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static Border Chrome(Control c, string name) =>
        c.GetVisualDescendants().OfType<Border>().First(b => b.Name == name);

    private void AssertFilled(string label, Border chrome)
    {
        var bg = AccentTestHarness.ColorOf(chrome.Background);
        var stroke = AccentTestHarness.ColorOf(chrome.BorderBrush);
        _o.WriteLine($"{label}: bg={bg} stroke={stroke} thickness={chrome.BorderThickness} r={chrome.CornerRadius}");
        Assert.Equal(Fill, bg);
        Assert.Equal(0, stroke.A);
    }

    [AvaloniaFact]
    public void EverySettingsPillBox_IsFilled_WithNoOutline()
    {
        EnsureAppStyles();
        var donor = new SettingsView();
        var window = new Window { Width = 700, Height = 700, Content = donor, RequestedThemeVariant = ThemeVariant.Dark };

        TextBox Field(params string[] classes)
        {
            var t = new TextBox { Text = "x", CornerRadius = new CornerRadius(999) };
            foreach (var c in classes) t.Classes.Add(c);
            return t;
        }
        ComboBox Combo(params string[] classes)
        {
            var c = new ComboBox { ItemsSource = new[] { "Immediately" }, SelectedIndex = 0 };
            foreach (var k in classes) c.Classes.Add(k);
            return c;
        }

        var plain = Field();
        var plainOff = Field(); plainOff.IsEnabled = false;
        var search = Field("settings-search");
        var info = Field("metadata-info-field");
        var infoOff = Field("metadata-info-field"); infoOff.IsEnabled = false;
        var profile = Field("profile-name");
        var list = Combo("pill-list");
        var listOff = Combo("pill-list"); listOff.IsEnabled = false;
        var plainCombo = Combo();
        var preset = Combo("pill-combo", "stable-popup");
        var eqNumber = new NumericUpDown { Value = 1000 };
        eqNumber.Classes.Add("eq-pill-number");
        var number = new NumericUpDown { Value = 3, CornerRadius = new CornerRadius(999) }; // as the plugin settings row
        var serverCombo = Combo("pill-list", "server-pill");
        var shell = new Border { Child = serverCombo };
        shell.Classes.Add("server-pill-shell");
        // A greyed-out row (parent toggle off), as the Settings sub-settings are.
        var rowOff = new StackPanel { IsEnabled = false, Children = { Field(), Combo("pill-list") } };
        rowOff.Classes.Add("sub-setting");

        donor.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 6,
                Children = { plain, plainOff, search, info, infoOff, profile, list, listOff, plainCombo, preset, eqNumber, number, shell, rowOff },
            },
        };
        window.Show();
        window.UpdateLayout();

        AssertFilled("TextBox", Chrome(plain, "PART_BorderElement"));
        AssertFilled("TextBox disabled", Chrome(plainOff, "PART_BorderElement"));
        AssertFilled("settings-search", Chrome(search, "PART_BorderElement"));
        AssertFilled("metadata-info-field", Chrome(info, "PART_BorderElement"));
        AssertFilled("metadata-info-field disabled", Chrome(infoOff, "PART_BorderElement"));
        AssertFilled("profile-name", Chrome(profile, "PART_BorderElement"));
        AssertFilled("ComboBox pill-list", Chrome(list, "Background"));
        AssertFilled("ComboBox pill-list disabled", Chrome(listOff, "Background"));
        AssertFilled("ComboBox plain", Chrome(plainCombo, "Background"));
        AssertFilled("ComboBox pill-combo", Chrome(preset, "Background"));
        AssertFilled("NumericUpDown eq-pill-number", Chrome(eqNumber, "PART_BorderElement"));
        AssertFilled("greyed row TextBox", Chrome((TextBox)rowOff.Children[0], "PART_BorderElement"));
        AssertFilled("greyed row ComboBox", Chrome((ComboBox)rowOff.Children[1], "Background"));

        // Music Server picker: the shell carries the fill; the combo's own chrome stays clear
        // so there is never a second fill or ring inside it.
        AssertFilled("server-pill-shell", shell);
        var serverChrome = Chrome(serverCombo, "Background");
        Assert.Equal(0, AccentTestHarness.ColorOf(serverChrome.Background).A);
        Assert.Equal(new Thickness(0), serverChrome.BorderThickness);

        // The plain number field (plugin settings): Fluent's spinner shell is the box, so it
        // carries the fill; the TextBox inside stays clear (no second fill).
        var spinner = number.GetVisualDescendants().OfType<ButtonSpinner>().First();
        var spinnerBg = AccentTestHarness.ColorOf(spinner.Background);
        var spinnerStroke = AccentTestHarness.ColorOf(spinner.BorderBrush);
        _o.WriteLine($"NumericUpDown spinner: bg={spinnerBg} stroke={spinnerStroke} thickness={spinner.BorderThickness} r={spinner.CornerRadius}");
        Assert.Equal(Fill, spinnerBg);
        Assert.Equal(0, spinnerStroke.A);
        Assert.Equal(new CornerRadius(999), spinner.CornerRadius);
        var numberInner = Chrome(number, "PART_BorderElement");
        _o.WriteLine($"NumericUpDown inner: bg={AccentTestHarness.ColorOf(numberInner.Background)} stroke={AccentTestHarness.ColorOf(numberInner.BorderBrush)}");
        Assert.Equal(0, AccentTestHarness.ColorOf(numberInner.Background).A);
        Assert.Equal(0, AccentTestHarness.ColorOf(numberInner.BorderBrush).A);
    }
}
