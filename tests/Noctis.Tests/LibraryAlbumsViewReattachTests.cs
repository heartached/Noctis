using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Albums view is cached (CachedViewLocator) and PageDeck keeps at most 8 sections attached;
/// past that the view is detached and, when the user comes back, the same instance is attached
/// again with the same DataContext. OnDetachedFromVisualTree unhooked the album-rows and
/// "selection used" events but nothing hooked them again, so after a revisit a library rebuild
/// kept stale selections (GitHub #178/#225) and a menu command no longer cleared them.
/// </summary>
public class LibraryAlbumsViewReattachTests
{
    private sealed class NoOpPlayHistory : Noctis.Services.IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static Delegate[] Handlers(object owner, Type declaring, string field)
    {
        var f = declaring.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (f.GetValue(owner) as Delegate)?.GetInvocationList() ?? Array.Empty<Delegate>();
    }

    private static Type ObservableBase(Type t)
    {
        while (t is not null && !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ObservableCollection<>)))
            t = t.BaseType!;
        return t!;
    }

    [AvaloniaFact]
    public void Reattached_View_ListensToTheAlbumRowsAndSelectionUsed_Again()
    {
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).Add(new Album { Id = Guid.NewGuid(), Name = "A", Artist = "Artist", Tracks = new List<Track>() });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1200, Height = 800, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        int Hooked()
        {
            var rows = Handlers(vm.FilteredAlbumRows, ObservableBase(vm.FilteredAlbumRows.GetType()), "CollectionChanged")
                .Count(d => ReferenceEquals(d.Target, view));
            var used = Handlers(vm, typeof(LibraryAlbumsViewModel), "CtrlSelectionConsumed")
                .Count(d => ReferenceEquals(d.Target, view));
            return rows * 10 + used;
        }

        Assert.Equal(11, Hooked());

        // PageDeck drops it (more than 8 sections visited), then shows the same instance again.
        win.Content = null;
        Dispatcher.UIThread.RunJobs();
        win.Content = view;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(11, Hooked());

        // And again: never hooked twice.
        win.Content = null;
        Dispatcher.UIThread.RunJobs();
        win.Content = view;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(11, Hooked());
        win.Close();
    }
}
