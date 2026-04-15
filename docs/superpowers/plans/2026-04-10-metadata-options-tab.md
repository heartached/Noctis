# Metadata Options Tab - Apple Music Style Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a fully functional Apple Music-style Options tab to the Metadata dialog with media kind, start/stop time, volume adjust, equalizer preset, and playback behavior toggles - all applying at track or album scope with real-time playback effects.

**Architecture:** New per-track properties on the `Track` model store options (start/stop time, volume adjust, EQ preset, media kind, saved playback position). The `MetadataViewModel` exposes these for the Options tab UI. The `PlayerViewModel.PlayTrack` method applies per-track settings (volume adjust, EQ, start time) when a track begins, and monitors stop time during playback. Album-scoped edits fan out changes to all tracks in the album via the existing `_albumScoped` path. The `MetadataHelper` is updated to pass the album's full track list when album-scoped.

**Tech Stack:** C# / .NET 8, Avalonia UI, CommunityToolkit.Mvvm, LibVLC

---

### Task 1: Add Per-Track Option Properties to Track Model

**Files:**
- Modify: `src/Noctis/Models/Track.cs:121-135`

- [ ] **Step 1: Add new properties after existing options block**

Add these properties after `RememberPlaybackPosition` (line 125):

```csharp
/// <summary>Media kind classification for this track.</summary>
public string MediaKind { get; set; } = "Music";

/// <summary>Custom start time in milliseconds. 0 = disabled (play from beginning).</summary>
public long StartTimeMs { get; set; }

/// <summary>Custom stop time in milliseconds. 0 = disabled (play to end).</summary>
public long StopTimeMs { get; set; }

/// <summary>Per-track volume adjustment in dB (-100 to +100 mapped from slider). 0 = no adjustment.</summary>
public int VolumeAdjust { get; set; }

/// <summary>Per-track EQ preset name. Empty = use global setting.</summary>
public string EqPreset { get; set; } = string.Empty;

/// <summary>Saved playback position in milliseconds (for RememberPlaybackPosition).</summary>
public long SavedPositionMs { get; set; }
```

- [ ] **Step 2: Add MediaKind constants**

Add a static array for available media kinds (after the `ParseArtistTokens` method):

```csharp
/// <summary>Available media kind values for the Options tab dropdown.</summary>
public static readonly string[] AvailableMediaKinds = { "Music", "Podcast", "Audiobook", "Voice Memo", "Music Video" };
```

- [ ] **Step 3: Add formatted start/stop time helpers**

```csharp
/// <summary>Formatted start time string (m:ss.fff).</summary>
[JsonIgnore]
public string StartTimeFormatted => StartTimeMs > 0
    ? TimeSpan.FromMilliseconds(StartTimeMs).ToString(@"m\:ss\.fff")
    : "0:00.000";

/// <summary>Formatted stop time string (m:ss.fff), showing track duration when disabled.</summary>
[JsonIgnore]
public string StopTimeFormatted => StopTimeMs > 0
    ? TimeSpan.FromMilliseconds(StopTimeMs).ToString(@"m\:ss\.fff")
    : Duration.ToString(@"m\:ss\.fff");
```

- [ ] **Step 4: Update LibraryService merge to include new properties**

In `src/Noctis/Services/LibraryService.cs`, find the block near line 634 where `SkipWhenShuffling` and `RememberPlaybackPosition` are copied during merge, and add:

```csharp
target.MediaKind = source.MediaKind;
target.StartTimeMs = source.StartTimeMs;
target.StopTimeMs = source.StopTimeMs;
target.VolumeAdjust = source.VolumeAdjust;
target.EqPreset = source.EqPreset;
target.SavedPositionMs = source.SavedPositionMs;
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds

- [ ] **Step 6: Commit**

```bash
git add src/Noctis/Models/Track.cs src/Noctis/Services/LibraryService.cs
git commit -m "Add per-track options properties (media kind, start/stop time, volume adjust, EQ preset, saved position)"
```

---

### Task 2: Update MetadataViewModel with Options Tab Properties

**Files:**
- Modify: `src/Noctis/ViewModels/MetadataViewModel.cs`

- [ ] **Step 1: Add observable properties for Options tab**

After the existing Options tab properties (line 64), add:

```csharp
[ObservableProperty] private string _mediaKind = "Music";
[ObservableProperty] private bool _hasStartTime;
[ObservableProperty] private string _startTime = "0:00.000";
[ObservableProperty] private bool _hasStopTime;
[ObservableProperty] private string _stopTime = "0:00.000";
[ObservableProperty] private int _volumeAdjust; // -100 to +100
[ObservableProperty] private string _selectedEqPreset = "None";

/// <summary>EQ presets for the Options tab dropdown (None + all presets from Settings).</summary>
public static readonly string[] OptionsEqPresets = BuildOptionsEqPresets();

private static string[] BuildOptionsEqPresets()
{
    var list = new List<string> { "None" };
    // Skip "Custom" (index 0), add Flat + all VLC presets
    for (int i = 1; i < SettingsViewModel.EqPresetNames.Length; i++)
        list.Add(SettingsViewModel.EqPresetNames[i]);
    return list.ToArray();
}
```

- [ ] **Step 2: Update LoadFromTrack to load new properties**

In the `LoadFromTrack()` method, after line 202, add:

```csharp
// Options - extended
MediaKind = string.IsNullOrEmpty(_track.MediaKind) ? "Music" : _track.MediaKind;
HasStartTime = _track.StartTimeMs > 0;
StartTime = _track.StartTimeMs > 0
    ? TimeSpan.FromMilliseconds(_track.StartTimeMs).ToString(@"m\:ss\.fff")
    : "0:00.000";
HasStopTime = _track.StopTimeMs > 0;
StopTime = _track.StopTimeMs > 0
    ? TimeSpan.FromMilliseconds(_track.StopTimeMs).ToString(@"m\:ss\.fff")
    : _track.Duration.ToString(@"m\:ss\.fff");
VolumeAdjust = _track.VolumeAdjust;
SelectedEqPreset = string.IsNullOrEmpty(_track.EqPreset) ? "None" : _track.EqPreset;
```

- [ ] **Step 3: Update Save to persist new properties**

In the `Save()` method, after line 426, add:

```csharp
_track.MediaKind = MediaKind;
_track.StartTimeMs = HasStartTime ? ParseTimeToMs(StartTime) : 0;
_track.StopTimeMs = HasStopTime ? ParseTimeToMs(StopTime) : 0;
_track.VolumeAdjust = VolumeAdjust;
_track.EqPreset = SelectedEqPreset == "None" ? string.Empty : SelectedEqPreset;
```

- [ ] **Step 4: Add time parsing helper**

Add this private method to MetadataViewModel:

```csharp
/// <summary>Parses a time string like "1:23.456" or "0:05.000" to milliseconds.</summary>
private static long ParseTimeToMs(string time)
{
    if (string.IsNullOrWhiteSpace(time)) return 0;
    if (TimeSpan.TryParseExact(time, @"m\:ss\.fff", null, out var ts))
        return (long)ts.TotalMilliseconds;
    if (TimeSpan.TryParseExact(time, @"m\:ss", null, out var ts2))
        return (long)ts2.TotalMilliseconds;
    return 0;
}
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds

- [ ] **Step 6: Commit**

```bash
git add src/Noctis/ViewModels/MetadataViewModel.cs
git commit -m "Add Options tab ViewModel properties for media kind, start/stop time, volume adjust, EQ preset"
```

---

### Task 3: Update MetadataWindow Options Tab XAML

**Files:**
- Modify: `src/Noctis/Views/MetadataWindow.axaml:422-435`

- [ ] **Step 1: Replace the Options tab content**

Replace the entire Options TabItem (lines 422-435) with:

```xml
<!-- OPTIONS TAB -->
<TabItem Header="Options" FontSize="14" FontWeight="SemiBold">
    <ScrollViewer VerticalScrollBarVisibility="Auto"
                  Padding="24,16">
        <StackPanel Spacing="20">

            <!-- Media Kind -->
            <StackPanel Spacing="4">
                <TextBlock Text="media kind" FontSize="12" Opacity="0.5" />
                <ComboBox ItemsSource="{x:Static vm:Track.AvailableMediaKinds}"
                          SelectedItem="{Binding MediaKind}"
                          MinWidth="200"
                          CornerRadius="999"
                          Padding="14,8" />
            </StackPanel>

            <!-- Start / Stop Time -->
            <Grid ColumnDefinitions="*,24,*">
                <StackPanel Grid.Column="0" Spacing="4">
                    <StackPanel Orientation="Horizontal" Spacing="8">
                        <TextBlock Text="start" FontSize="12" Opacity="0.5"
                                   VerticalAlignment="Center" />
                        <CheckBox IsChecked="{Binding HasStartTime}"
                                  Padding="0" Margin="0"
                                  MinWidth="0" />
                    </StackPanel>
                    <TextBox Classes="metadata-info-field"
                             Text="{Binding StartTime}"
                             IsEnabled="{Binding HasStartTime}"
                             Width="140"
                             HorizontalAlignment="Left" />
                </StackPanel>
                <StackPanel Grid.Column="2" Spacing="4">
                    <StackPanel Orientation="Horizontal" Spacing="8">
                        <TextBlock Text="stop" FontSize="12" Opacity="0.5"
                                   VerticalAlignment="Center" />
                        <CheckBox IsChecked="{Binding HasStopTime}"
                                  Padding="0" Margin="0"
                                  MinWidth="0" />
                    </StackPanel>
                    <TextBox Classes="metadata-info-field"
                             Text="{Binding StopTime}"
                             IsEnabled="{Binding HasStopTime}"
                             Width="140"
                             HorizontalAlignment="Left" />
                </StackPanel>
            </Grid>

            <Separator Opacity="0.15" Margin="0,4" />

            <!-- Remember playback position -->
            <CheckBox Classes="metadata-pill-checkbox"
                      Content="Remember playback position"
                      IsChecked="{Binding RememberPlaybackPosition}" />

            <!-- Skip when shuffling -->
            <CheckBox Classes="metadata-pill-checkbox"
                      Content="Skip when shuffling"
                      IsChecked="{Binding SkipWhenShuffling}" />

            <Separator Opacity="0.15" Margin="0,4" />

            <!-- Volume Adjust -->
            <StackPanel Spacing="4">
                <TextBlock Text="volume adjust" FontSize="12" Opacity="0.5" />
                <Grid ColumnDefinitions="*,Auto">
                    <Slider Grid.Column="0"
                            Classes="volume-horizontal"
                            Value="{Binding VolumeAdjust, Mode=TwoWay}"
                            Minimum="-100" Maximum="100"
                            Width="300"
                            HorizontalAlignment="Left" />
                    <TextBlock Grid.Column="1"
                               Text="{Binding VolumeAdjust, StringFormat='{}{0}%'}"
                               FontSize="12"
                               Opacity="0.6"
                               VerticalAlignment="Center"
                               Margin="12,0,0,0" />
                </Grid>
            </StackPanel>

            <!-- Equalizer Preset -->
            <StackPanel Spacing="4">
                <TextBlock Text="equalizer" FontSize="12" Opacity="0.5" />
                <ComboBox ItemsSource="{x:Static vm:MetadataViewModel.OptionsEqPresets}"
                          SelectedItem="{Binding SelectedEqPreset}"
                          MinWidth="200"
                          CornerRadius="999"
                          Padding="14,8" />
            </StackPanel>

        </StackPanel>
    </ScrollViewer>
</TabItem>
```

Note: The `x:Static vm:Track.AvailableMediaKinds` reference requires adding `xmlns:models="using:Noctis.Models"` to the Window if `Track` is in a different namespace. Since the existing XAML already uses `xmlns:vm="using:Noctis.ViewModels"`, change the reference to use a static property on MetadataViewModel instead. Add to MetadataViewModel:

```csharp
public static string[] AvailableMediaKinds => Track.AvailableMediaKinds;
```

And use `{x:Static vm:MetadataViewModel.AvailableMediaKinds}` in the XAML.

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds

- [ ] **Step 3: Commit**

```bash
git add src/Noctis/Views/MetadataWindow.axaml src/Noctis/ViewModels/MetadataViewModel.cs
git commit -m "Redesign Options tab UI with media kind, start/stop time, volume adjust, and EQ preset"
```

---

### Task 4: Album-Scoped Save (Fan Out to All Tracks)

**Files:**
- Modify: `src/Noctis/ViewModels/MetadataViewModel.cs`
- Modify: `src/Noctis/ViewModels/MetadataHelper.cs`

- [ ] **Step 1: Pass album tracks to MetadataViewModel**

Update `MetadataHelper.OpenMetadataWindow` to also resolve the full track list when album-scoped:

```csharp
public static async Task OpenMetadataWindow(Track track, bool albumScoped = false)
{
    var metadata = App.Services!.GetRequiredService<IMetadataService>();
    var library = App.Services!.GetRequiredService<ILibraryService>();
    var persistence = App.Services!.GetRequiredService<IPersistenceService>();

    List<Track>? albumTracks = null;
    if (albumScoped)
    {
        albumTracks = library.Tracks
            .Where(t => t.AlbumId == track.AlbumId)
            .ToList();
    }

    var vm = new MetadataViewModel(track, metadata, library, persistence, albumScoped, albumTracks);
    var window = new MetadataWindow(vm);

    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
        && desktop.MainWindow != null)
    {
        await window.ShowDialog(desktop.MainWindow);
    }
    else
    {
        window.Show();
    }
}
```

- [ ] **Step 2: Update MetadataViewModel constructor and Save for album fan-out**

Add `_albumTracks` field:

```csharp
private readonly List<Track>? _albumTracks;
```

Update constructor signature:

```csharp
public MetadataViewModel(Track track, IMetadataService metadata, ILibraryService library,
    IPersistenceService persistence, bool albumScoped = false, List<Track>? albumTracks = null)
```

Assign in constructor body:
```csharp
_albumTracks = albumTracks;
```

In `Save()`, after applying options to `_track` but before writing metadata, fan out to album tracks:

```csharp
// Fan out options to all album tracks when album-scoped
if (_albumScoped && _albumTracks != null)
{
    foreach (var t in _albumTracks)
    {
        t.SkipWhenShuffling = SkipWhenShuffling;
        t.RememberPlaybackPosition = RememberPlaybackPosition;
        t.MediaKind = MediaKind;
        t.StartTimeMs = HasStartTime ? ParseTimeToMs(StartTime) : 0;
        t.StopTimeMs = HasStopTime ? ParseTimeToMs(StopTime) : 0;
        t.VolumeAdjust = VolumeAdjust;
        t.EqPreset = SelectedEqPreset == "None" ? string.Empty : SelectedEqPreset;
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/ViewModels/MetadataHelper.cs src/Noctis/ViewModels/MetadataViewModel.cs
git commit -m "Fan out Options tab settings to all album tracks when album-scoped"
```

---

### Task 5: Apply Per-Track Volume Adjust During Playback

**Files:**
- Modify: `src/Noctis/Services/IAudioPlayer.cs`
- Modify: `src/Noctis/Services/VlcAudioPlayer.cs`
- Modify: `src/Noctis/ViewModels/PlayerViewModel.cs`

- [ ] **Step 1: Add VolumeAdjust to IAudioPlayer**

Add to the interface:

```csharp
/// <summary>Per-track volume adjustment (-100 to +100). Applied on top of the user volume.</summary>
int VolumeAdjust { get; set; }
```

- [ ] **Step 2: Implement VolumeAdjust in VlcAudioPlayer**

Add field and property. The adjustment modifies the effective volume: `effectiveVolume = clamp(userVolume + adjust, 0, 100)`.

```csharp
private int _volumeAdjust;

public int VolumeAdjust
{
    get => _volumeAdjust;
    set
    {
        _volumeAdjust = Math.Clamp(value, -100, 100);
        // Re-apply volume with the new adjustment
        var effective = Math.Clamp(_userVolume + _volumeAdjust, 0, 100);
        var target = ApplyVolumeCurve(effective);
        ScheduleVolumeWrite(target);
    }
}
```

Update the `Volume` setter to also account for `_volumeAdjust`:

In the existing `Volume` setter, change:
```csharp
var target = ApplyVolumeCurve(_userVolume);
```
to:
```csharp
var effective = Math.Clamp(_userVolume + _volumeAdjust, 0, 100);
var target = ApplyVolumeCurve(effective);
```

Similarly update `CommitVolume()`:
```csharp
public void CommitVolume()
{
    var effective = Math.Clamp(_userVolume + _volumeAdjust, 0, 100);
    var target = ApplyVolumeCurve(effective);
    _player.Volume = target;
}
```

- [ ] **Step 3: Apply per-track volume adjust in PlayerViewModel.PlayTrack**

In `PlayTrack()` (line 683), before `_audioPlayer.Play(track.FilePath)`, add:

```csharp
// Apply per-track volume adjustment
_audioPlayer.VolumeAdjust = track.VolumeAdjust;
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds

- [ ] **Step 5: Commit**

```bash
git add src/Noctis/Services/IAudioPlayer.cs src/Noctis/Services/VlcAudioPlayer.cs src/Noctis/ViewModels/PlayerViewModel.cs
git commit -m "Apply per-track volume adjustment during playback"
```

---

### Task 6: Apply Per-Track EQ Preset During Playback

**Files:**
- Modify: `src/Noctis/ViewModels/PlayerViewModel.cs`
- Modify: `src/Noctis/ViewModels/SettingsViewModel.cs`

- [ ] **Step 1: Expose a method to apply EQ by preset name**

Add a public method to `SettingsViewModel` that applies an EQ preset by name:

```csharp
/// <summary>
/// Applies an EQ preset by name. Used for per-track EQ overrides.
/// Pass empty/null to restore the global EQ setting.
/// </summary>
public void ApplyEqPresetByName(string? presetName)
{
    if (string.IsNullOrEmpty(presetName))
    {
        // Restore global EQ setting
        ApplyEqualizer();
        return;
    }

    var index = Array.IndexOf(EqPresetNames, presetName);
    if (index < 0) { ApplyEqualizer(); return; }

    // VLC preset index is our index - 1 (because index 0 = Custom, not a VLC preset)
    int vlcPresetIndex = index - 1;
    _audioPlayer?.SetAdvancedEqualizer(true, vlcPresetIndex, GetEqBands());
}
```

- [ ] **Step 2: Apply per-track EQ in PlayerViewModel.PlayTrack**

The PlayerViewModel needs access to SettingsViewModel. Check if it already has it; if not, add it via constructor injection or a setter.

Add field:
```csharp
private SettingsViewModel? _settings;
```

Add public setter (to be called during app initialization):
```csharp
public void SetSettingsViewModel(SettingsViewModel settings) => _settings = settings;
```

In `PlayTrack()`, after the volume adjust line, add:

```csharp
// Apply per-track EQ preset (or restore global)
_settings?.ApplyEqPresetByName(
    string.IsNullOrEmpty(track.EqPreset) ? null : track.EqPreset);
```

- [ ] **Step 3: Wire up SettingsViewModel in app initialization**

Find where PlayerViewModel and SettingsViewModel are created/registered and call `SetSettingsViewModel`. Search for where the player is initialized:

```csharp
player.SetSettingsViewModel(settingsVm);
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds

- [ ] **Step 5: Commit**

```bash
git add src/Noctis/ViewModels/PlayerViewModel.cs src/Noctis/ViewModels/SettingsViewModel.cs
git commit -m "Apply per-track EQ preset during playback, restore global EQ when track has none"
```

---

### Task 7: Start Time / Stop Time Playback Enforcement

**Files:**
- Modify: `src/Noctis/ViewModels/PlayerViewModel.cs`

- [ ] **Step 1: Seek to start time after play begins**

In `PlayTrack()`, after `_audioPlayer.Play(track.FilePath)` (line 683), add:

```csharp
// Seek to custom start time if set
if (track.StartTimeMs > 0)
{
    var startPos = TimeSpan.FromMilliseconds(track.StartTimeMs);
    if (startPos < track.Duration)
    {
        _audioPlayer.Seek(startPos);
        Position = startPos;
        PositionText = FormatTime(startPos);
        PositionFraction = track.Duration.TotalSeconds > 0
            ? startPos.TotalSeconds / track.Duration.TotalSeconds
            : 0;
    }
}
```

- [ ] **Step 2: Monitor stop time during position updates**

Find the position update handler (where `PositionChanged` event is handled). Add a stop time check:

In the method that handles `_audioPlayer.PositionChanged`, add after updating position:

```csharp
// Check per-track stop time
if (CurrentTrack?.StopTimeMs > 0)
{
    var stopTime = TimeSpan.FromMilliseconds(CurrentTrack.StopTimeMs);
    if (position >= stopTime)
    {
        AdvanceQueue();
        return;
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/ViewModels/PlayerViewModel.cs
git commit -m "Enforce per-track start and stop times during playback"
```

---

### Task 8: Remember Playback Position

**Files:**
- Modify: `src/Noctis/ViewModels/PlayerViewModel.cs`

- [ ] **Step 1: Save position when switching away from a track with RememberPlaybackPosition**

In `PlayTrack()`, before setting `CurrentTrack = track`, save the current track's position:

```csharp
// Save playback position for the outgoing track if it has RememberPlaybackPosition
if (CurrentTrack?.RememberPlaybackPosition == true)
{
    CurrentTrack.SavedPositionMs = (long)Position.TotalMilliseconds;
}
```

- [ ] **Step 2: Restore saved position when playing a track**

In `PlayTrack()`, after the start time seek block, add (only if no start time override):

```csharp
// Restore saved position if RememberPlaybackPosition is set and no custom start time
else if (track.RememberPlaybackPosition && track.SavedPositionMs > 0)
{
    var savedPos = TimeSpan.FromMilliseconds(track.SavedPositionMs);
    if (savedPos < track.Duration)
    {
        _audioPlayer.Seek(savedPos);
        Position = savedPos;
        PositionText = FormatTime(savedPos);
        PositionFraction = track.Duration.TotalSeconds > 0
            ? savedPos.TotalSeconds / track.Duration.TotalSeconds
            : 0;
    }
}
```

- [ ] **Step 3: Clear saved position when track finishes naturally**

In `AdvanceQueueCore()`, before moving the track to history, clear the saved position if it played to completion:

```csharp
// Clear saved position since the track finished naturally
if (CurrentTrack?.RememberPlaybackPosition == true)
{
    CurrentTrack.SavedPositionMs = 0;
}
```

- [ ] **Step 4: Save position on app exit / stop**

In `StopAndClear()`, save position before stopping:

```csharp
if (CurrentTrack?.RememberPlaybackPosition == true)
{
    CurrentTrack.SavedPositionMs = (long)Position.TotalMilliseconds;
}
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds

- [ ] **Step 6: Commit**

```bash
git add src/Noctis/ViewModels/PlayerViewModel.cs
git commit -m "Implement remember/restore playback position per track"
```

---

### Task 9: Wire Up SettingsViewModel in App Initialization

**Files:**
- Modify: `src/Noctis/ViewModels/MainWindowViewModel.cs` (or wherever PlayerViewModel is created)

- [ ] **Step 1: Find where PlayerViewModel and SettingsViewModel are both accessible**

Search for where both are created or available. Wire them:

```csharp
_player.SetSettingsViewModel(_settings);
```

This depends on the actual initialization code. Search for `new PlayerViewModel` or DI registration.

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "Wire SettingsViewModel into PlayerViewModel for per-track EQ"
```

---

### Task 10: Final Integration Build and Test

- [ ] **Step 1: Full build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds with no warnings related to new code

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/Noctis.Tests/Noctis.Tests.csproj -v minimal`
Expected: Tests pass (or only pre-existing baseline failures)

- [ ] **Step 3: Commit any remaining fixes**

```bash
git add -A
git commit -m "Final integration: metadata Options tab with full playback support"
```
