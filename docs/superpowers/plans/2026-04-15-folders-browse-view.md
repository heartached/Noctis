# Folders Browse View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Folders" library browse axis so users who organize music by subfolder (e.g. `Music\Rock`, `Music\Metal`) can navigate their library by folder hierarchy instead of having every file flattened into tag-based views.

**Architecture:** Library scanning is unchanged — every `Track` already stores an absolute `FilePath`. A new read-only `FolderNode` tree is computed on demand from `ILibraryService.Tracks` rooted at the user's configured `MusicFolders`. A new `LibraryFoldersViewModel` drives a new `LibraryFoldersView` that shows the folder tree on the left and tracks for the selected folder on the right. A "Folders" sidebar nav item routes to it. No persistence, no DB changes, no scanner changes.

**Tech Stack:** C# 10, Avalonia 11, CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), compiled bindings (`x:DataType`), existing `BulkObservableCollection` for bulk list updates. Follows patterns in [LibrarySongsViewModel.cs](src/Noctis/ViewModels/LibrarySongsViewModel.cs) and [LibrarySongsView.axaml](src/Noctis/Views/LibrarySongsView.axaml).

---

## File Structure

**New files:**
- `src/Noctis/Models/FolderNode.cs` — tree node (path, display name, child nodes, track list).
- `src/Noctis/Services/FolderTreeBuilder.cs` — pure function that builds a `FolderNode` forest from `IReadOnlyList<Track>` + configured roots.
- `src/Noctis/ViewModels/LibraryFoldersViewModel.cs` — view-model: owns root nodes, selected node, track list for selected node, search, context-menu commands (delegates to player/sidebar like other VMs).
- `src/Noctis/Views/LibraryFoldersView.axaml` — split view: `TreeView` on the left (folder hierarchy), `ListBox` of tracks on the right (reusing the track-list style from `LibrarySongsView`).
- `src/Noctis/Views/LibraryFoldersView.axaml.cs` — code-behind wiring (matches `LibrarySongsView.axaml.cs` pattern).
- `tests/Noctis.Tests/FolderTreeBuilderTests.cs` — unit tests for the builder.

**Modified files:**
- `src/Noctis/ViewModels/SidebarViewModel.cs:34-42` — add a `folders` nav item between `artists` and `playlists`.
- `src/Noctis/ViewModels/MainWindowViewModel.cs` — add `_foldersVm` field, construct it in the ctor, wire it into `Navigate()` switch (`"folders"` case) and the `TopBar.CurrentTabName` switch, add it to `GetCurrentViewKey()`, refresh it in the startup-load block.
- `src/Noctis/App.axaml:51-90` — add a `DataTemplate` for `LibraryFoldersViewModel` → `LibraryFoldersView`.
- `src/Noctis/Assets/Icons.axaml` — add a `FoldersIcon` geometry (or reuse an existing folder glyph if already present).

**Out of scope for this plan:**
- Drag-reordering folders.
- Watching the filesystem for folder changes (existing scan-on-startup / rescan is sufficient).
- Making Folders the default page (can be toggled in Settings manually later).
- Theming changes (separate task).

---

## Task 1: FolderNode model

**Files:**
- Create: `src/Noctis/Models/FolderNode.cs`

- [ ] **Step 1: Create the FolderNode model**

```csharp
using System.Collections.ObjectModel;
using Noctis.Models;

namespace Noctis.Models;

/// <summary>
/// A node in the folder browse tree. Computed on demand from library tracks;
/// not persisted. A node represents one directory under a configured music root.
/// </summary>
public sealed class FolderNode
{
    /// <summary>Absolute path of this folder on disk.</summary>
    public required string FullPath { get; init; }

    /// <summary>Leaf display name (e.g. "Rock"). For a root, the full root path.</summary>
    public required string DisplayName { get; init; }

    /// <summary>True if this node is one of the user's configured music roots.</summary>
    public bool IsRoot { get; init; }

    /// <summary>Child subfolders, alphabetically sorted.</summary>
    public ObservableCollection<FolderNode> Children { get; } = new();

    /// <summary>Tracks that live directly in this folder (not in subfolders).</summary>
    public List<Track> DirectTracks { get; } = new();

    /// <summary>Total track count including all descendants (computed at build time).</summary>
    public int TotalTrackCount { get; set; }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Models/FolderNode.cs
git commit -m "Add FolderNode model for folder browse tree"
```

---

## Task 2: FolderTreeBuilder — failing test

**Files:**
- Test: `tests/Noctis.Tests/FolderTreeBuilderTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class FolderTreeBuilderTests
{
    private static Track T(string path) => new() { FilePath = path, Title = System.IO.Path.GetFileNameWithoutExtension(path) };

    [Fact]
    public void Build_GroupsSubfoldersUnderRoot()
    {
        var tracks = new List<Track>
        {
            T(@"C:\Music\Rock\song1.mp3"),
            T(@"C:\Music\Rock\song2.mp3"),
            T(@"C:\Music\Metal\song3.mp3"),
            T(@"C:\Music\Metal\sub\song4.mp3"),
        };
        var roots = new[] { @"C:\Music" };

        var forest = FolderTreeBuilder.Build(tracks, roots);

        Assert.Single(forest);
        var root = forest[0];
        Assert.Equal(@"C:\Music", root.FullPath);
        Assert.True(root.IsRoot);
        Assert.Equal(4, root.TotalTrackCount);
        Assert.Equal(2, root.Children.Count);

        var rock = root.Children.First(c => c.DisplayName == "Rock");
        Assert.Equal(2, rock.TotalTrackCount);
        Assert.Equal(2, rock.DirectTracks.Count);
        Assert.Empty(rock.Children);

        var metal = root.Children.First(c => c.DisplayName == "Metal");
        Assert.Equal(2, metal.TotalTrackCount);
        Assert.Single(metal.DirectTracks);
        Assert.Single(metal.Children);
        Assert.Equal("sub", metal.Children[0].DisplayName);
    }

    [Fact]
    public void Build_TracksOutsideAnyRoot_AreIgnored()
    {
        var tracks = new List<Track> { T(@"D:\Other\song.mp3") };
        var roots = new[] { @"C:\Music" };

        var forest = FolderTreeBuilder.Build(tracks, roots);

        Assert.Single(forest);
        Assert.Equal(0, forest[0].TotalTrackCount);
    }

    [Fact]
    public void Build_SortsChildrenAlphabetically()
    {
        var tracks = new List<Track>
        {
            T(@"C:\Music\Zeta\a.mp3"),
            T(@"C:\Music\Alpha\a.mp3"),
            T(@"C:\Music\Mu\a.mp3"),
        };
        var roots = new[] { @"C:\Music" };

        var forest = FolderTreeBuilder.Build(tracks, roots);

        var names = forest[0].Children.Select(c => c.DisplayName).ToList();
        Assert.Equal(new[] { "Alpha", "Mu", "Zeta" }, names);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Noctis.Tests/Noctis.Tests.csproj --filter FullyQualifiedName~FolderTreeBuilderTests -v minimal`
Expected: Compile failure (`FolderTreeBuilder` does not exist).

Note: the test project may have pre-existing baseline compile failures unrelated to folders (see `.claude/rules/testing.md`). If the only new failure is `FolderTreeBuilder not found`, that is the expected state for this step.

- [ ] **Step 3: Commit the failing test**

```bash
git add tests/Noctis.Tests/FolderTreeBuilderTests.cs
git commit -m "Add failing tests for FolderTreeBuilder"
```

---

## Task 3: FolderTreeBuilder — implementation

**Files:**
- Create: `src/Noctis/Services/FolderTreeBuilder.cs`

- [ ] **Step 1: Implement the builder**

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Builds a folder-hierarchy forest (one tree per configured music root) from a flat track list.
/// Pure function — no I/O, no state. Tracks whose FilePath lies outside every root are ignored.
/// </summary>
public static class FolderTreeBuilder
{
    public static IReadOnlyList<FolderNode> Build(
        IReadOnlyList<Track> tracks,
        IReadOnlyList<string> roots)
    {
        var normalizedRoots = roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // One tree per root, keyed by normalized root path.
        var rootNodes = normalizedRoots.ToDictionary(
            r => r,
            r => new FolderNode
            {
                FullPath = r,
                DisplayName = r,
                IsRoot = true,
            },
            StringComparer.OrdinalIgnoreCase);

        // Child lookup: (parentFullPath → child name → child node).
        var childIndex = new Dictionary<string, Dictionary<string, FolderNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in rootNodes.Values)
            childIndex[root.FullPath] = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track.FilePath)) continue;
            var trackDir = NormalizePath(Path.GetDirectoryName(track.FilePath) ?? string.Empty);
            if (string.IsNullOrEmpty(trackDir)) continue;

            var root = normalizedRoots.FirstOrDefault(r =>
                trackDir.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                trackDir.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (root == null) continue;

            var node = EnsureNode(rootNodes[root], trackDir, root, childIndex);
            node.DirectTracks.Add(track);
        }

        // Compute TotalTrackCount bottom-up and sort children alphabetically.
        foreach (var root in rootNodes.Values)
            Finalize(root);

        return rootNodes.Values.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static FolderNode EnsureNode(
        FolderNode rootNode,
        string targetDir,
        string rootPath,
        Dictionary<string, Dictionary<string, FolderNode>> childIndex)
    {
        if (targetDir.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            return rootNode;

        var relative = targetDir.Substring(rootPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        var current = rootNode;
        var currentPath = rootPath;
        foreach (var segment in segments)
        {
            var nextPath = Path.Combine(currentPath, segment);
            var map = childIndex[currentPath];
            if (!map.TryGetValue(segment, out var child))
            {
                child = new FolderNode
                {
                    FullPath = nextPath,
                    DisplayName = segment,
                    IsRoot = false,
                };
                map[segment] = child;
                current.Children.Add(child);
                childIndex[nextPath] = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
            }
            current = child;
            currentPath = nextPath;
        }
        return current;
    }

    private static int Finalize(FolderNode node)
    {
        var total = node.DirectTracks.Count;
        var sortedChildren = node.Children.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        node.Children.Clear();
        foreach (var child in sortedChildren)
        {
            total += Finalize(child);
            node.Children.Add(child);
        }
        node.TotalTrackCount = total;
        return total;
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path; }
    }
}
```

- [ ] **Step 2: Run the tests to verify they pass**

Run: `dotnet test tests/Noctis.Tests/Noctis.Tests.csproj --filter FullyQualifiedName~FolderTreeBuilderTests -v minimal`
Expected: All 3 `FolderTreeBuilderTests` pass. (Pre-existing baseline failures from `TestPersistenceService.cs` may remain — unrelated.)

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Services/FolderTreeBuilder.cs
git commit -m "Implement FolderTreeBuilder for folder browse tree"
```

---

## Task 4: LibraryFoldersViewModel

**Files:**
- Create: `src/Noctis/ViewModels/LibraryFoldersViewModel.cs`

- [ ] **Step 1: Implement the view-model**

```csharp
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the "Folders" library view — browses tracks by on-disk folder hierarchy
/// under the user's configured music roots.
/// </summary>
public partial class LibraryFoldersViewModel : ViewModelBase, IDisposable
{
    private readonly ILibraryService _library;
    private readonly PlayerViewModel _player;
    private readonly IPersistenceService _persistence;

    private EventHandler? _libraryUpdatedHandler;
    private bool _isDirty = true;

    /// <summary>Root nodes of the folder forest (one per configured music root).</summary>
    public ObservableCollection<FolderNode> RootNodes { get; } = new();

    /// <summary>Tracks to display in the right-hand pane (direct + descendant tracks of SelectedNode).</summary>
    public BulkObservableCollection<Track> SelectedFolderTracks { get; } = new();

    [ObservableProperty] private FolderNode? _selectedNode;

    public LibraryFoldersViewModel(ILibraryService library, PlayerViewModel player, IPersistenceService persistence)
    {
        _library = library;
        _player = player;
        _persistence = persistence;

        _libraryUpdatedHandler = (_, _) =>
        {
            _isDirty = true;
            Dispatcher.UIThread.Post(Refresh);
        };
        _library.LibraryUpdated += _libraryUpdatedHandler;
    }

    public void MarkDirty() => _isDirty = true;

    public void Refresh()
    {
        if (!_isDirty && RootNodes.Count > 0)
            return;
        _isDirty = false;

        var settings = _persistence.LoadSettingsAsync().GetAwaiter().GetResult();
        var roots = settings.MusicFolders;

        var forest = FolderTreeBuilder.Build(_library.Tracks.ToList(), roots);

        RootNodes.Clear();
        foreach (var root in forest)
            RootNodes.Add(root);

        // Keep selection if possible; otherwise clear the right pane.
        var keep = SelectedNode;
        if (keep == null || !ContainsNode(forest, keep.FullPath))
        {
            SelectedNode = null;
            SelectedFolderTracks.ReplaceAll(Array.Empty<Track>());
        }
        else
        {
            OnSelectedNodeChanged(SelectedNode);
        }
    }

    partial void OnSelectedNodeChanged(FolderNode? value)
    {
        if (value == null)
        {
            SelectedFolderTracks.ReplaceAll(Array.Empty<Track>());
            return;
        }

        var tracks = new List<Track>();
        Collect(value, tracks);
        SelectedFolderTracks.ReplaceAll(tracks);
    }

    private static void Collect(FolderNode node, List<Track> sink)
    {
        sink.AddRange(node.DirectTracks);
        foreach (var child in node.Children)
            Collect(child, sink);
    }

    private static bool ContainsNode(IReadOnlyList<FolderNode> forest, string fullPath)
    {
        foreach (var n in forest)
        {
            if (string.Equals(n.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return true;
            if (ContainsNode(n.Children.ToList(), fullPath))
                return true;
        }
        return false;
    }

    [RelayCommand]
    private void PlayFolder()
    {
        if (SelectedFolderTracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(SelectedFolderTracks.ToList(), 0);
    }

    [RelayCommand]
    private void ShuffleFolder()
    {
        if (SelectedFolderTracks.Count == 0) return;
        var shuffled = SelectedFolderTracks.OrderBy(_ => Random.Shared.Next()).ToList();
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void PlayTrack(Track track)
    {
        var list = SelectedFolderTracks.ToList();
        var index = list.IndexOf(track);
        if (index < 0) index = 0;
        _player.ReplaceQueueAndPlay(list, index);
    }

    public void Dispose()
    {
        if (_libraryUpdatedHandler != null)
        {
            _library.LibraryUpdated -= _libraryUpdatedHandler;
            _libraryUpdatedHandler = null;
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/ViewModels/LibraryFoldersViewModel.cs
git commit -m "Add LibraryFoldersViewModel"
```

---

## Task 5: LibraryFoldersView (AXAML + code-behind)

**Files:**
- Create: `src/Noctis/Views/LibraryFoldersView.axaml`
- Create: `src/Noctis/Views/LibraryFoldersView.axaml.cs`

- [ ] **Step 1: Create the AXAML view**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Noctis.ViewModels"
             xmlns:m="using:Noctis.Models"
             x:Class="Noctis.Views.LibraryFoldersView"
             x:DataType="vm:LibraryFoldersViewModel"
             Background="{DynamicResource AppMainBackground}">

    <Grid ColumnDefinitions="280,*" Margin="16,8,16,16">

        <!-- Folder tree -->
        <Border Grid.Column="0" Padding="8" Background="{DynamicResource AppSidebarBackground}" CornerRadius="8">
            <TreeView ItemsSource="{Binding RootNodes}"
                      SelectedItem="{Binding SelectedNode, Mode=TwoWay}">
                <TreeView.ItemTemplate>
                    <TreeDataTemplate x:DataType="m:FolderNode" ItemsSource="{Binding Children}">
                        <StackPanel Orientation="Horizontal" Spacing="6">
                            <TextBlock Text="{Binding DisplayName}" FontWeight="SemiBold" />
                            <TextBlock Text="{Binding TotalTrackCount, StringFormat='({0})'}" Opacity="0.6" />
                        </StackPanel>
                    </TreeDataTemplate>
                </TreeView.ItemTemplate>
            </TreeView>
        </Border>

        <!-- Right pane: track list for selected folder -->
        <DockPanel Grid.Column="1" Margin="16,0,0,0">
            <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Spacing="8" Margin="0,0,0,8">
                <Button Content="Play" Command="{Binding PlayFolderCommand}" />
                <Button Content="Shuffle" Command="{Binding ShuffleFolderCommand}" />
                <TextBlock Text="{Binding SelectedNode.DisplayName, FallbackValue='Select a folder'}"
                           VerticalAlignment="Center" FontSize="14" Opacity="0.7" />
            </StackPanel>

            <ListBox Classes="track-list"
                     ItemsSource="{Binding SelectedFolderTracks}"
                     SelectionMode="Multiple">
                <ListBox.ItemTemplate>
                    <DataTemplate x:DataType="m:Track">
                        <Grid ColumnDefinitions="*,120,*" Margin="0,2">
                            <TextBlock Grid.Column="0" Text="{Binding TitleDisplay}" VerticalAlignment="Center" />
                            <TextBlock Grid.Column="1" Text="{Binding DurationFormatted}" VerticalAlignment="Center" Opacity="0.7" />
                            <TextBlock Grid.Column="2" Text="{Binding ArtistDisplay}" VerticalAlignment="Center" Opacity="0.7" />
                        </Grid>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
        </DockPanel>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Create the code-behind**

```csharp
using Avalonia.Controls;

namespace Noctis.Views;

public partial class LibraryFoldersView : UserControl
{
    public LibraryFoldersView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 3: Add DataTemplate in App.axaml**

Open [App.axaml](src/Noctis/App.axaml) and inside `<Application.DataTemplates>` add:

```xml
<DataTemplate DataType="{x:Type vm:LibraryFoldersViewModel}">
    <views:LibraryFoldersView />
</DataTemplate>
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/Noctis/Views/LibraryFoldersView.axaml src/Noctis/Views/LibraryFoldersView.axaml.cs src/Noctis/App.axaml
git commit -m "Add LibraryFoldersView and register DataTemplate"
```

---

## Task 6: Sidebar nav item

**Files:**
- Modify: `src/Noctis/ViewModels/SidebarViewModel.cs:34-42`

- [ ] **Step 1: Add the Folders nav item**

In the `NavItems` initializer (after `artists`, before `playlists`):

```csharp
public ObservableCollection<NavItem> NavItems { get; } = new()
{
    new NavItem { Key = "home", Label = "Home", IconGlyph = "HomeIcon" },
    new NavItem { Key = "songs", Label = "Songs", IconGlyph = "SongsIcon" },
    new NavItem { Key = "albums", Label = "Albums", IconGlyph = "AlbumsIcon" },
    new NavItem { Key = "artists", Label = "Artists", IconGlyph = "ArtistsIcon" },
    new NavItem { Key = "folders", Label = "Folders", IconGlyph = "FoldersIcon" },
    new NavItem { Key = "playlists", Label = "Playlists", IconGlyph = "PlaylistsIcon" },
    new NavItem { Key = "settings", Label = "Settings", IconGlyph = "SettingsIcon" },
};
```

- [ ] **Step 2: Add the FoldersIcon geometry**

Open [src/Noctis/Assets/Icons.axaml](src/Noctis/Assets/Icons.axaml). If an icon keyed `FoldersIcon` does not already exist, add one (a standard folder path). If Noctis already maps icon keys through [IconKeyToGeometryConverter.cs](src/Noctis/Converters/IconKeyToGeometryConverter.cs), add a matching case there.

```xml
<StreamGeometry x:Key="FoldersIcon">M4,6 L10,6 L12,8 L20,8 L20,18 L4,18 Z</StreamGeometry>
```

If the converter-based mapping is used instead of resource keys, add a `"FoldersIcon"` case that returns the same geometry.

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/ViewModels/SidebarViewModel.cs src/Noctis/Assets/Icons.axaml src/Noctis/Converters/IconKeyToGeometryConverter.cs
git commit -m "Add Folders nav item and icon"
```

---

## Task 7: Wire Folders into MainWindowViewModel

**Files:**
- Modify: `src/Noctis/ViewModels/MainWindowViewModel.cs`

- [ ] **Step 1: Add the field**

In the "Cached content ViewModels" region near line 102:

```csharp
private readonly LibraryFoldersViewModel _foldersVm;
```

- [ ] **Step 2: Construct it in the ctor near the other Library VM constructions**

Near [MainWindowViewModel.cs:153](src/Noctis/ViewModels/MainWindowViewModel.cs#L153) where `_songsVm = new LibrarySongsViewModel(...)`:

```csharp
_foldersVm = new LibraryFoldersViewModel(library, Player, persistence);
```

- [ ] **Step 3: Refresh it during startup load**

Near [MainWindowViewModel.cs:258](src/Noctis/ViewModels/MainWindowViewModel.cs#L258) (the block that refreshes `_songsVm`, `_albumsVm`, etc.):

```csharp
_foldersVm.Refresh();
await Task.Yield();
```

- [ ] **Step 4: Add the Navigate case**

In the `Navigate(string key)` switch at [MainWindowViewModel.cs:745](src/Noctis/ViewModels/MainWindowViewModel.cs#L745):

```csharp
"folders" => RefreshAndReturnFolders(_foldersVm),
```

And in the `TopBar.CurrentTabName` switch below it:

```csharp
"folders" => "Folders",
```

- [ ] **Step 5: Add the helper method**

Alongside the other `RefreshAndReturnX` helpers (e.g. near `RefreshAndReturnSongs`):

```csharp
private LibraryFoldersViewModel RefreshAndReturnFolders(LibraryFoldersViewModel vm)
{
    vm.Refresh();
    return vm;
}
```

- [ ] **Step 6: Update GetCurrentViewKey()**

At [MainWindowViewModel.cs:1057](src/Noctis/ViewModels/MainWindowViewModel.cs#L1057):

```csharp
if (CurrentView == _foldersVm) return "folders";
```

- [ ] **Step 7: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: `Build succeeded.`

- [ ] **Step 8: Commit**

```bash
git add src/Noctis/ViewModels/MainWindowViewModel.cs
git commit -m "Wire Folders view into MainWindowViewModel navigation"
```

---

## Task 8: Manual verification

- [ ] **Step 1: Run the app**

Run: `dotnet run --project src/Noctis/Noctis.csproj`
Expected: app launches without errors.

- [ ] **Step 2: Add a test music folder**

In Settings → Music Folders, add a folder that contains at least two subfolders with audio files (the user's example: `C:\Music` with `Rock/`, `Metal/`, `Trap/`, `Rap/`). Wait for scan to complete.

- [ ] **Step 3: Verify Folders view**

Click "Folders" in the sidebar. Expected:
- Left pane shows the music root expandable into its subfolders.
- Each folder shows its total track count in parentheses.
- Clicking a subfolder populates the right pane with only that folder's tracks (including descendants).
- "Play" button plays the selected folder; "Shuffle" shuffles it.
- Root folder shows all tracks under it when selected.

- [ ] **Step 4: Verify other views still work**

Click Songs, Albums, Artists, Playlists, Favorites, Home. Expected: all still behave exactly as before — folders view does not affect tag-based grouping.

- [ ] **Step 5: Report results to user**

Summarize: app launches, Folders view groups subfolders correctly, other views unaffected. If any manual step failed, stop and report before claiming completion.

---

## Self-Review Notes

- **Spec coverage:** All three user asks for folders are covered — drag-in parent folder no longer forces tag-based flattening; subfolders remain browsable; root selection still plays everything under it.
- **Type consistency:** `FolderNode` property names match across `FolderTreeBuilder`, `LibraryFoldersViewModel`, and `LibraryFoldersView.axaml` (`DisplayName`, `FullPath`, `Children`, `DirectTracks`, `TotalTrackCount`, `IsRoot`).
- **No placeholders:** every step contains full code or exact commands.
- **Deferred:** context-menu parity with `LibrarySongsView` (Add to playlist, Remove from library, etc.) is not in this plan — the Folders view gets a minimal track list + Play/Shuffle to keep scope tight. If the user wants full parity, add a follow-up task that copies the relevant `[RelayCommand]` methods and template columns from `LibrarySongsViewModel`/`LibrarySongsView`.
