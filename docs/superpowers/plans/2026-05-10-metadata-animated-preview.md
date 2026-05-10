# Metadata Animated-Artwork Live Preview — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Animated Artwork tab in the metadata dialog play the assigned animated cover *in place* (instead of showing a static image with a "✓ Animated artwork assigned / filename" badge over it).

**Architecture:** `MetadataWindow` is a borderless transparent Avalonia window; LibVLC's native `VideoView` can't render in it. So add a new control, `AnimatedCoverImage`, that decodes video frames into a `WriteableBitmap` via LibVLC video callbacks and shows them in a plain `Image` element — which composes fine in a transparent window and is clipped by the surrounding rounded `Border`. The existing native-`VideoView` control (`AnimatedCoverView`, used on five surfaces inside the opaque `MainWindow`) is left alone except for extracting the shared-`LibVLC` accessor so both controls reuse one instance.

**Tech Stack:** C# / .NET 8, Avalonia 11, CommunityToolkit.Mvvm, LibVLCSharp 3.9.6.

**Spec:** `docs/superpowers/specs/2026-05-10-metadata-animated-preview-design.md`

**Build/verify commands:**
- Build: `dotnet build src/Noctis/Noctis.csproj -v minimal`
- (No automated tests for this change — it's a UI control wrapping native LibVLC callbacks, matching `AnimatedCoverView` which also has no tests; the test project is on a known-broken baseline per `.claude/rules/testing.md`. Verification is build + manual.)

---

## File Structure

- **New:** `src/Noctis/Controls/SharedLibVlc.cs` — the one process-wide `LibVLC` instance, extracted from `AnimatedCoverView`.
- **New:** `src/Noctis/Controls/AnimatedCoverImage.axaml` + `.axaml.cs` — bitmap-readback animated-cover control for transparent windows.
- **Modify:** `src/Noctis/Controls/AnimatedCoverView.axaml.cs` — drop its private shared-`LibVLC` plumbing, use `SharedLibVlc.Instance`.
- **Modify:** `src/Noctis/Views/MetadataWindow.axaml` — replace the static image + badge in the Animated Artwork tab with `AnimatedCoverImage`; move the filename to a caption below the preview box.

---

## Task 1: Extract the shared `LibVLC` instance

**Files:**
- Create: `src/Noctis/Controls/SharedLibVlc.cs`
- Modify: `src/Noctis/Controls/AnimatedCoverView.axaml.cs`

- [ ] **Step 1: Create `SharedLibVlc.cs`**

Create `src/Noctis/Controls/SharedLibVlc.cs` with exactly:

```csharp
using LibVLCSharp.Shared;

namespace Noctis.Controls;

/// <summary>
/// The single process-wide <see cref="LibVLC"/> instance for all animated-cover
/// surfaces. Multiple LibVLC instances fight over global VLC subsystem state
/// (notably the audio device, which breaks the main audio player), so every
/// animated-cover control must share this one. Never disposed.
/// </summary>
internal static class SharedLibVlc
{
    private static LibVLC? _instance;
    private static readonly object _lock = new();

    public static LibVLC Instance
    {
        get
        {
            if (_instance != null) return _instance;
            lock (_lock)
            {
                if (_instance == null)
                {
                    Core.Initialize();
                    // --aout=none guarantees this LibVLC never opens an audio device.
                    _instance = new LibVLC("--quiet", "--no-video-title-show", "--aout=none");
                }
                return _instance;
            }
        }
    }
}
```

- [ ] **Step 2: Rewrite `AnimatedCoverView.axaml.cs` to use it**

Replace the entire contents of `src/Noctis/Controls/AnimatedCoverView.axaml.cs` with:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LibVLCSharp.Shared;
using LibVLCSharp.Avalonia;

namespace Noctis.Controls;

public partial class AnimatedCoverView : UserControl
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<AnimatedCoverView, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<AnimatedCoverView, bool>(nameof(IsActive), defaultValue: false);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private MediaPlayer? _player;
    private VideoView? _videoHost;
    private Panel? _hostPanel;

    public AnimatedCoverView()
    {
        InitializeComponent();
        _hostPanel = this.FindControl<Panel>("HostPanel");
        DetachedFromVisualTree += (_, _) => Teardown();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == IsActiveProperty)
            Refresh();
    }

    private void Refresh()
    {
        var shouldPlay = IsActive && !string.IsNullOrEmpty(Source) && File.Exists(Source);
        if (!shouldPlay)
        {
            Teardown();
            return;
        }

        try
        {
            EnsurePlayer();
            using var media = new Media(SharedLibVlc.Instance, Source!, FromType.FromPath,
                ":no-audio", ":input-repeat=65535");
            _player!.Play(media);
        }
        catch
        {
            Teardown();
        }
    }

    private void EnsurePlayer()
    {
        if (_player == null)
        {
            _player = new MediaPlayer(SharedLibVlc.Instance) { EnableHardwareDecoding = true, Mute = true };
        }
        if (_videoHost == null && _hostPanel != null)
        {
            _videoHost = new VideoView { MediaPlayer = _player };
            _hostPanel.Children.Add(_videoHost);
        }
    }

    private void Teardown()
    {
        if (_videoHost != null && _hostPanel != null)
        {
            try { _hostPanel.Children.Remove(_videoHost); } catch { }
            try { _videoHost.MediaPlayer = null; } catch { }
            _videoHost = null;
        }

        try { _player?.Stop(); } catch { }
        try { _player?.Dispose(); } catch { }
        _player = null;

        // NEVER dispose the shared LibVLC — it's reused across surfaces.
    }
}
```

(This is the same file as before with the `_sharedLibVlc` / `_libVlcLock` / `GetSharedLibVlc()` block removed and the two `GetSharedLibVlc()` call sites changed to `SharedLibVlc.Instance`. Nothing else changed.)

- [ ] **Step 3: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Controls/SharedLibVlc.cs src/Noctis/Controls/AnimatedCoverView.axaml.cs
git commit -m "refactor(animated-cover): extract shared LibVLC instance"
```

---

## Task 2: Create the `AnimatedCoverImage` control

**Files:**
- Create: `src/Noctis/Controls/AnimatedCoverImage.axaml`
- Create: `src/Noctis/Controls/AnimatedCoverImage.axaml.cs`

- [ ] **Step 1: Create the XAML**

Create `src/Noctis/Controls/AnimatedCoverImage.axaml` with exactly:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="Noctis.Controls.AnimatedCoverImage"
             ClipToBounds="True">
    <Image x:Name="FrameImage"
           Stretch="UniformToFill"
           RenderOptions.BitmapInterpolationMode="HighQuality" />
</UserControl>
```

- [ ] **Step 2: Create the code-behind**

Create `src/Noctis/Controls/AnimatedCoverImage.axaml.cs` with exactly:

```csharp
using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace Noctis.Controls;

/// <summary>
/// Plays a looping animated cover by decoding frames into a <see cref="WriteableBitmap"/>.
/// Unlike <see cref="AnimatedCoverView"/> (native LibVLC <c>VideoView</c>), this is a plain
/// <c>Image</c> element, so it composes inside transparent Avalonia windows (the metadata
/// dialog) and is clipped by its parent. Slightly more CPU than the native path — fine for a
/// small muted preview loop. Minor frame tearing under load is accepted (single shared buffer).
/// </summary>
public partial class AnimatedCoverImage : UserControl
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<AnimatedCoverImage, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<AnimatedCoverImage, bool>(nameof(IsActive), defaultValue: false);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    // VLC scales every frame into a square RV32 (BGRA in memory) buffer of this size.
    // Animated covers are square; a non-square source gets scaled to fit this square.
    private const int RenderSize = 600;
    private const int Stride = RenderSize * 4;
    private const int BufferBytes = Stride * RenderSize;

    private readonly Image _image;
    private MediaPlayer? _player;
    private IntPtr _buffer = IntPtr.Zero;          // native frame buffer VLC writes into
    private byte[]? _scratch;                       // managed hop for the IntPtr->IntPtr copy
    private WriteableBitmap? _bitmap;
    private volatile bool _framePending;            // coalesce UI invalidations

    // Keep delegate instances alive for the player's lifetime — VLC stores raw
    // function pointers and will crash if these are garbage-collected.
    private MediaPlayer.LibVLCVideoLockCb? _lockCb;
    private MediaPlayer.LibVLCVideoDisplayCb? _displayCb;

    public AnimatedCoverImage()
    {
        InitializeComponent();
        _image = this.FindControl<Image>("FrameImage")!;
        DetachedFromVisualTree += (_, _) => Teardown();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == IsActiveProperty)
            Refresh();
    }

    private void Refresh()
    {
        var shouldPlay = IsActive && !string.IsNullOrEmpty(Source) && File.Exists(Source);
        if (!shouldPlay)
        {
            Teardown();
            return;
        }

        try
        {
            EnsurePlayer();
            using var media = new Media(SharedLibVlc.Instance, Source!, FromType.FromPath,
                ":no-audio", ":input-repeat=65535");
            _player!.Play(media);
        }
        catch
        {
            Teardown();
        }
    }

    private void EnsurePlayer()
    {
        if (_player != null) return;

        _buffer = Marshal.AllocHGlobal(BufferBytes);
        _scratch = new byte[BufferBytes];
        _bitmap = new WriteableBitmap(new PixelSize(RenderSize, RenderSize), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _image.Source = _bitmap;

        // Software decoding is required for the frame-callback path.
        _player = new MediaPlayer(SharedLibVlc.Instance) { EnableHardwareDecoding = false, Mute = true };
        _lockCb = OnLock;
        _displayCb = OnDisplay;
        _player.SetVideoFormat("RV32", RenderSize, RenderSize, Stride);
        _player.SetVideoCallbacks(_lockCb, null, _displayCb);
    }

    // VLC asks where to write the next frame; hand back the single shared buffer.
    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _buffer);
        return _buffer; // picture id — unused
    }

    // A decoded frame is in _buffer. Push it to the WriteableBitmap on the UI thread.
    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        if (_framePending) return;
        _framePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _framePending = false;
            var bmp = _bitmap;
            var scratch = _scratch;
            if (bmp == null || scratch == null || _buffer == IntPtr.Zero) return;
            Marshal.Copy(_buffer, scratch, 0, BufferBytes);
            using (var fb = bmp.Lock())
                Marshal.Copy(scratch, 0, fb.Address, BufferBytes);
            _image.InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    private void Teardown()
    {
        // Stop() is synchronous in LibVLC 3.x — after it returns, no more callbacks fire,
        // so it is safe to free the native buffer afterwards.
        var player = _player;
        _player = null;
        if (player != null)
        {
            try { player.Stop(); } catch { }
            try { player.SetVideoCallbacks(null, null, null); } catch { }
            try { player.Dispose(); } catch { }
        }
        _lockCb = null;
        _displayCb = null;

        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _scratch = null;

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }

        // NEVER dispose the shared LibVLC — it's reused across surfaces.
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds, 0 errors. (If it complains about `MediaPlayer.LibVLCVideoLockCb` / `LibVLCVideoDisplayCb` not being found, they are nested delegate types on `LibVLCSharp.Shared.MediaPlayer` — confirm the `using LibVLCSharp.Shared;` is present; no other namespace is needed.)

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Controls/AnimatedCoverImage.axaml src/Noctis/Controls/AnimatedCoverImage.axaml.cs
git commit -m "feat(animated-cover): AnimatedCoverImage control (bitmap readback for transparent windows)"
```

---

## Task 3: Use `AnimatedCoverImage` in the metadata dialog

**Files:**
- Modify: `src/Noctis/Views/MetadataWindow.axaml` (Animated Artwork tab, around lines 789–873)

- [ ] **Step 1: Add the filename caption below the preview box**

In `src/Noctis/Views/MetadataWindow.axaml`, inside the Animated Artwork tab's `<DockPanel Margin="24,16">`, immediately **after** the closing `</StackPanel>` of the Add/Download/Remove buttons panel (the one with `DockPanel.Dock="Bottom"` ... `Margin="0,16,0,0"`, ends near line 822) and **before** the `<Border x:Name="AnimatedCoverPreviewAnchor" ...>`, insert:

```xml
                    <TextBlock DockPanel.Dock="Bottom"
                               Text="{Binding AnimatedCoverFileName}"
                               IsVisible="{Binding HasAnimatedCover}"
                               FontSize="11"
                               Opacity="0.5"
                               HorizontalAlignment="Center"
                               TextAlignment="Center"
                               TextWrapping="Wrap"
                               MaxWidth="380"
                               Margin="0,12,0,0" />
```

(DockPanel docks children in order: the buttons panel is the first `Dock="Bottom"` child so it stays at the very bottom; this `TextBlock` is the second `Dock="Bottom"` child so it sits directly above the buttons; the `Border` is the fill child and takes the rest.)

- [ ] **Step 2: Replace the static image + badge with `AnimatedCoverImage`**

Still in `src/Noctis/Views/MetadataWindow.axaml`, replace the entire `<Panel>...</Panel>` that is the child of `<Border x:Name="AnimatedCoverPreviewAnchor" ...>` — i.e. replace this block (currently lines ~832–872):

```xml
                        <Panel>
                            <TextBlock Text="No Animated Artwork"
                                       FontSize="18"
                                       Opacity="0.3"
                                       HorizontalAlignment="Center"
                                       VerticalAlignment="Center"
                                       IsVisible="{Binding !HasAnimatedCover}" />

                            <!-- When an animated cover is assigned, show the album art as a still
                                 (the animation itself plays on the app's real surfaces, not in this dialog). -->
                            <Image Source="{Binding ArtworkPreview}"
                                   Stretch="UniformToFill"
                                   RenderOptions.BitmapInterpolationMode="HighQuality"
                                   IsVisible="{Binding HasAnimatedCover}" />

                            <StackPanel HorizontalAlignment="Center"
                                        VerticalAlignment="Bottom"
                                        Spacing="2"
                                        Margin="20"
                                        IsVisible="{Binding HasAnimatedCover}">
                                <Border Background="#B0000000"
                                        CornerRadius="8"
                                        Padding="14,8"
                                        HorizontalAlignment="Center">
                                    <StackPanel Spacing="2">
                                        <TextBlock Text="✓ Animated artwork assigned"
                                                   FontSize="13"
                                                   FontWeight="SemiBold"
                                                   HorizontalAlignment="Center"
                                                   Foreground="#E74856" />
                                        <TextBlock Text="{Binding AnimatedCoverFileName}"
                                                   FontSize="11"
                                                   Opacity="0.7"
                                                   HorizontalAlignment="Center"
                                                   TextWrapping="Wrap"
                                                   TextAlignment="Center"
                                                   MaxWidth="340" />
                                    </StackPanel>
                                </Border>
                            </StackPanel>
                        </Panel>
```

with:

```xml
                        <Panel>
                            <TextBlock Text="No Animated Artwork"
                                       FontSize="18"
                                       Opacity="0.3"
                                       HorizontalAlignment="Center"
                                       VerticalAlignment="Center"
                                       IsVisible="{Binding !HasAnimatedCover}" />

                            <controls:AnimatedCoverImage Source="{Binding AnimatedCoverPath}"
                                                         IsActive="{Binding HasAnimatedCover}"
                                                         IsVisible="{Binding HasAnimatedCover}" />
                        </Panel>
```

Notes:
- The `xmlns:controls="using:Noctis.Controls"` namespace is already declared on the `<Window>` element at the top of `MetadataWindow.axaml` — no new xmlns needed.
- The `x:Name="AnimatedCoverPreviewAnchor"` on the `<Border>` is now unused; leave it (harmless) — removing it is not required.
- The TabControl realizes only the selected tab's content, so switching away from this tab detaches `AnimatedCoverImage` (→ `Teardown`, decoder stops) and switching back re-creates it (→ plays). No extra "is tab selected" wiring needed.

- [ ] **Step 3: Build**

Run: `dotnet build src/Noctis/Noctis.csproj -v minimal`
Expected: Build succeeds, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Noctis/Views/MetadataWindow.axaml
git commit -m "feat(metadata): live animated-artwork preview in the dialog"
```

---

## Task 4: Manual verification

**Files:** none (verification only — commit only if a fix is needed)

- [ ] **Step 1: Run the app**

Run: `dotnet run --project src/Noctis/Noctis.csproj`

- [ ] **Step 2: Verify the preview animates**

1. Right-click a track that already has an animated cover (or any track) → **Get Info** → **Animated Artwork** tab.
2. If none assigned: click **Add Animated Artwork**, pick a short MP4. The preview box should **play the video, looping**, clipped to the rounded box — no floating window, no text overlaid on the image. The filename appears as a small dim caption **below** the box, above the buttons.
3. Click **Remove** → the box shows the "No Animated Artwork" placeholder, the caption disappears, decoding stops.
4. Click **Add Animated Artwork** again, pick a different MP4 → preview switches to the new clip and loops.
5. Switch to the **Details** tab and back to **Animated Artwork** → the preview resumes playing.
6. Click **Save** → dialog closes. Play that track → confirm Now Playing / Playback Bar mini-art still animate (the native-`VideoView` path is unaffected).
7. While all of the above happens, confirm normal audio playback is uninterrupted (no audio-device disruption from the second LibVLC consumer — there should be only one shared instance).

- [ ] **Step 3: If everything passes, done.** If a fix was needed, commit it with a descriptive message.

---

## Notes / accepted trade-offs

- **Minor frame tearing under load:** a single shared native buffer is used; if VLC writes the next frame while the UI thread is copying the current one, a frame may tear slightly. Acceptable for a small muted preview loop; not worth double-buffering.
- **Non-square sources:** VLC scales every frame into a 600×600 RV32 buffer, so a non-square source is squished to square. Animated covers are square by convention (sidecar / motion-artwork conventions), so this is not expected to bite; if it ever matters, switch to `SetVideoFormatCallbacks` to learn the source dimensions.
- **CPU vs. the native path:** software decode + per-frame ~1.4 MB copies at preview frame rates is negligible on any machine that runs this app; the native `VideoView` path is kept for the always-on surfaces where it matters.
