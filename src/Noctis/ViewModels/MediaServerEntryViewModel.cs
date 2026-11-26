using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Models;
using Noctis.Services.MediaServer;

namespace Noctis.ViewModels;

/// <summary>
/// One connected media server in Settings › Integrations › Music Server (GitHub #268:
/// several servers side by side). Wraps the persisted <see cref="SourceConnection"/>;
/// the Enabled switch writes straight through to it and tells the owner to re-apply
/// the server set and save.
/// </summary>
public partial class MediaServerEntryViewModel : ObservableObject
{
    private readonly Action<MediaServerEntryViewModel>? _enabledChanged;

    public MediaServerEntryViewModel(SourceConnection connection, string flavor, Action<MediaServerEntryViewModel>? enabledChanged = null)
    {
        Connection = connection;
        Flavor = flavor;
        _isEnabled = connection.Enabled;
        _enabledChanged = enabledChanged;
    }

    public SourceConnection Connection { get; }

    /// <summary>The picked preset ("Navidrome", "Ampache", "Jellyfin", …).</summary>
    public string Flavor { get; }

    /// <summary>The user's name for it, else preset and host.</summary>
    public string DisplayName => MediaServerService.DisplayName(Connection);

    public string Url => Connection.BaseUriOrPath;
    public string Username => Connection.Username;

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (Connection.Enabled == value) return;
        Connection.Enabled = value;
        _enabledChanged?.Invoke(this);
    }
}
