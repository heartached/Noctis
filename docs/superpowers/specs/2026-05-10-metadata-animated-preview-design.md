# Metadata dialog — live animated-artwork preview

## Problem

The **Animated Artwork** tab in `MetadataWindow` shows a *static* album-art image
with a "✓ Animated artwork assigned / <filename>" badge overlaid on it — the
assigned video does not animate. `MetadataWindow` is a borderless transparent
window (`Background="Transparent"`, `TransparencyLevelHint="Transparent"`), and
LibVLC's `VideoView` is a native child window (HWND on Windows) that cannot
render inside a transparent/layered window. A prior workaround floated a `Popup`
(its own opaque window) over the dialog to host the `VideoView`; it never
composed cleanly (z-order, no rounded-corner clipping, drifts on move) and has
been added and removed twice (`befa06d` ↔ `4f491e2` ↔ current working copy).

## Goal

Make the Animated Artwork tab play the assigned animated cover *in place* inside
the 420×420 preview box, clipped to its rounded corners, with no second window.
Remove the badge text from the image.

Out of scope: the other five surfaces that already animate correctly (Now
Playing, Album Detail header, Playback Bar mini-art, Lyrics, Cover Flow) — those
live in the opaque `MainWindow` and keep using `AnimatedCoverView`.

## Approach

Render decoded video frames into a `WriteableBitmap` via LibVLC's video
callbacks, displayed in an ordinary `Image` element. A plain bitmap composes
correctly in a transparent window and is clipped by the surrounding `Border`.
Slightly more CPU than the native `VideoView` path, which is irrelevant for a
single muted ~square short loop at preview size.

## Components

### `AnimatedCoverImage` control — `src/Noctis/Controls/AnimatedCoverImage.axaml(.cs)`

- Public API mirrors `AnimatedCoverView`: `Source` (`string?`) and `IsActive`
  (`bool`) styled properties.
- On `Source`/`IsActive` change: if `IsActive && File.Exists(Source)`, create a
  `MediaPlayer` on the shared `LibVLC`, call
  `SetVideoFormat("RV32", w, h, w*4)` and `SetVideoCallbacks(lock, unlock,
  display)`; the lock callback hands VLC a pinned back buffer, display schedules
  a copy into a `WriteableBitmap` and `InvalidateVisual()` on the `Image` via the
  UI thread (`Dispatcher.UIThread.Post`). Play with `:no-audio` and
  `:input-repeat=65535` (same media options as `AnimatedCoverView` today).
- Picks the video's native dimensions for the bitmap; the `Image` uses
  `Stretch="UniformToFill"` so framing matches the static Artwork preview.
- Teardown on `DetachedFromVisualTree` and whenever `IsActive`/`Source` clears:
  stop + dispose the `MediaPlayer`, free the buffer, drop the bitmap. **Never**
  dispose the shared `LibVLC`.

### Shared `LibVLC` accessor

Extract `AnimatedCoverView.GetSharedLibVlc()` (and its `_sharedLibVlc` /
`_libVlcLock`) into a small internal helper (e.g. `SharedLibVlc.Instance`) so
`AnimatedCoverImage` reuses the *same* `LibVLC` instance. Multiple `LibVLC`
instances fight over global VLC state (audio device), which breaks the main
player — see `.claude/rules/audio.md`. `AnimatedCoverView` is otherwise
unchanged.

### `MetadataWindow.axaml` — Animated Artwork tab

- Inside `AnimatedCoverPreviewAnchor`, replace the static
  `<Image Source="{Binding ArtworkPreview}">` and the `#B0000000` badge
  `StackPanel` (the "✓ Animated artwork assigned" + filename block) with:
  `<controls:AnimatedCoverImage Source="{Binding AnimatedCoverPath}"
  IsActive="{Binding HasAnimatedCover}" />`.
- Keep the existing "No Animated Artwork" placeholder for the empty state
  (visible when `!HasAnimatedCover`).
- Show the assigned filename (`AnimatedCoverFileName`) as a small dim caption
  *below the preview box*, above the Add / Download / Remove buttons — no longer
  overlaid on the image.
- `IsActive` may additionally gate on the Animated Artwork tab being selected so
  no decoding happens while other tabs are shown (minor; include if cheap).

### ViewModel

No changes. `AnimatedCoverPath`, `HasAnimatedCover`, `AnimatedCoverFileName`
already exist and are wired.

## Files

- new: `src/Noctis/Controls/AnimatedCoverImage.axaml`
- new: `src/Noctis/Controls/AnimatedCoverImage.axaml.cs`
- modify: `src/Noctis/Controls/AnimatedCoverView.axaml.cs` (extract shared-LibVLC helper)
- new (maybe): `src/Noctis/Controls/SharedLibVlc.cs` (the extracted helper)
- modify: `src/Noctis/Views/MetadataWindow.axaml` (swap preview content, move filename caption)

## Verification

- `dotnet build src/Noctis/Noctis.csproj -v minimal` succeeds.
- Manual: right-click a track → Get Info → Animated Artwork tab → Add an MP4 →
  the preview animates in place, clipped to the rounded box, no floating window,
  no text over the image; filename shows below the box. Remove → placeholder
  returns, decoding stops. Switch tabs and back → preview resumes. Save → other
  surfaces still animate. Confirm main audio playback is unaffected.

## Risks / unknowns

- LibVLC video-callback marshaling details (buffer pitch, RV32 = BGRA byte
  order, lifetime of the pinned buffer vs. `WriteableBitmap.Lock()`); standard
  but fiddly — handle on the spike.
- Frame-rate / invalidation throttling: copy on the VLC display callback but
  coalesce UI invalidations so we don't flood the dispatcher.
