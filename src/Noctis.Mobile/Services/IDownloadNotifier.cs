namespace Noctis.Mobile.Services;

/// <summary>The system notification for downloads from the computer (AndroidDownloadNotifier):
/// an ongoing, silent progress card while songs download, then a short "Downloaded 12 songs".
/// Driven by <see cref="ViewModels.DownloadActivity"/> on the UI thread; null in tests unless injected.</summary>
public interface IDownloadNotifier
{
    /// <summary>Downloads run: <paramref name="title"/> (the album, or "Downloading from your
    /// computer"), <paramref name="text"/> ("Downloading 3 of 12") and a bar at <paramref name="done"/>
    /// of <paramref name="total"/>.</summary>
    void ShowProgress(string title, string text, int done, int total);

    /// <summary>The downloads ended: the progress card gives way to <paramref name="title"/>
    /// ("Downloaded 12 songs"), with <paramref name="text"/> ("2 failed") when not empty.</summary>
    void ShowFinished(string title, string text);

    /// <summary>Nothing to report (every download was stopped or removed): the card goes.</summary>
    void Clear();
}
