using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Controls;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Populates existing playlists as flat MenuItems inside an "Add to Playlist" parent MenuItem,
/// inserted after a named Separator. Skips repopulation if playlists haven't changed.
/// </summary>
public sealed class PlaylistMenuPopulator
{
    private readonly MenuItem _parentMenuItem;
    private readonly Separator _separator;
    private readonly List<MenuItem> _generated = new();
    private int _lastHash;

    public PlaylistMenuPopulator(MenuItem parentMenuItem, Separator separator)
    {
        _parentMenuItem = parentMenuItem;
        _separator = separator;
    }

    public void Populate(ObservableCollection<Playlist>? playlists, ICommand? addCommand)
    {
        var hash = ComputeHash(playlists);
        if (hash == _lastHash && _generated.Count > 0)
            return;

        foreach (var item in _generated)
            _parentMenuItem.Items.Remove(item);
        _generated.Clear();
        _lastHash = hash;

        if (playlists == null || playlists.Count == 0 || addCommand == null)
            return;

        var insertIndex = _parentMenuItem.Items.IndexOf(_separator);
        if (insertIndex < 0)
            insertIndex = _parentMenuItem.Items.Count;
        else
            insertIndex++;

        foreach (var playlist in playlists)
        {
            var item = new MenuItem
            {
                Header = playlist.Name,
                Command = addCommand,
                CommandParameter = playlist
            };
            _parentMenuItem.Items.Insert(insertIndex++, item);
            _generated.Add(item);
        }
    }

    /// <summary>
    /// Populate with a custom command parameter factory (e.g. for track+playlist pairs).
    /// </summary>
    public void Populate(ObservableCollection<Playlist>? playlists, ICommand? addCommand, Func<Playlist, object> parameterFactory)
    {
        var hash = ComputeHash(playlists);
        if (hash == _lastHash && _generated.Count > 0)
            return;

        foreach (var item in _generated)
            _parentMenuItem.Items.Remove(item);
        _generated.Clear();
        _lastHash = hash;

        if (playlists == null || playlists.Count == 0 || addCommand == null)
            return;

        var insertIndex = _parentMenuItem.Items.IndexOf(_separator);
        if (insertIndex < 0)
            insertIndex = _parentMenuItem.Items.Count;
        else
            insertIndex++;

        foreach (var playlist in playlists)
        {
            var item = new MenuItem
            {
                Header = playlist.Name,
                Command = addCommand,
                CommandParameter = parameterFactory(playlist)
            };
            _parentMenuItem.Items.Insert(insertIndex++, item);
            _generated.Add(item);
        }
    }

    private static int ComputeHash(IEnumerable<Playlist>? playlists)
    {
        if (playlists == null) return 0;
        var hash = new HashCode();
        foreach (var p in playlists)
        {
            hash.Add(p.Name);
            hash.Add(p.Id);
        }
        return hash.ToHashCode();
    }
}
