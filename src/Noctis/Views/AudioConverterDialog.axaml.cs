using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Noctis.Services;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class AudioConverterDialog : Window
{
    /// <summary>Format keys as people read them ("flac" → "FLAC", "alac" → "ALAC (.m4a)").</summary>
    public static readonly IValueConverter FormatLabel =
        new FuncValueConverter<string?, string>(key => key switch
        {
            "mp3" => "MP3",
            "m4a" => "AAC (.m4a)",
            "aac" => "AAC (.aac)",
            "opus" => "Opus",
            "ogg" => "Ogg Vorbis",
            "wma" => "WMA",
            "flac" => "FLAC",
            "alac" => "ALAC (.m4a)",
            "wav" => "WAV",
            "aiff" => "AIFF",
            "wavpack" => "WavPack",
            null => string.Empty,
            _ => key.ToUpperInvariant(),
        });

    /// <summary>"Auto" → "Same as source", "24" → "24-bit".</summary>
    public static readonly IValueConverter BitDepthLabel =
        new FuncValueConverter<string?, string>(depth => depth switch
        {
            "Auto" => Localization.Loc.T("AudioConverter.DepthSource"),
            null => string.Empty,
            _ => Localization.Loc.T("AudioConverter.DepthBits", depth),
        });

    // Until the pattern has had the caret, a tag chip appends instead of landing at index 0.
    private bool _patternHadFocus;

    public AudioConverterDialog()
    {
        InitializeComponent();
        PatternBox.GotFocus += (_, _) => _patternHadFocus = true;
        TokenChips.AddHandler(Button.ClickEvent, OnTokenClick);
    }

    public AudioConverterDialog(AudioConverterViewModel vm) : this()
    {
        DataContext = vm;
        // PillDialogHost turns this into the animated close; nothing is returned through
        // Close(result), so the deferred close loses nothing.
        vm.Closed += (_, _) => Close();
    }

    /// <summary>A tag chip: its token goes in at the pattern's caret (replacing a selection).</summary>
    private void OnTokenClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Control { DataContext: AudioConverterViewModel.PatternToken chip }) return;
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
        // A token right against another token or word would glue the values together
        // ("%title%%year%" → "Song2024"): pad it with a space on that side. Next to a
        // separator (space - _ . brackets) it goes in as is.
        var insert = token;
        if (at > 0 && GluesTo(text[at - 1])) insert = " " + insert;
        if (at < text.Length && GluesTo(text[at])) insert += " ";
        PatternBox.Text = text.Insert(at, insert);
        PatternBox.Focus();
        PatternBox.CaretIndex = at + insert.Length;
    }

    private static bool GluesTo(char c) => char.IsLetterOrDigit(c) || c == '%';

    private async void OnBrowseOutputClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;

            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Localization.Loc.T("AudioConverter.OutputFolder"),
                AllowMultiple = false,
            });

            // Cancelled picker: keep whatever was there.
            if (folders.Count > 0 && DataContext is AudioConverterViewModel { IsIdle: true } vm
                && folders[0].TryGetLocalPath() is { Length: > 0 } path)
                vm.OutputFolder = path;
        }
        catch (Exception ex)
        {
            DebugLog.Write("AudioConverter", $"Folder pick failed: {ex.Message}");
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape is Cancel: it stops a running conversion first, and closes when idle.
        if (e.Key == Key.Escape && DataContext is AudioConverterViewModel vm)
        {
            e.Handled = true;
            vm.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Stops a running conversion when the window closes by any route (Alt+F4,
    /// the owner going away), not only through Cancel.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        (DataContext as AudioConverterViewModel)?.CancelForClose();
        base.OnClosing(e);
    }
}
