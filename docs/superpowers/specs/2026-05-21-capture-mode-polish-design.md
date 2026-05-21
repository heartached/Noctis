# Capture Mode Polish — Design

**Date:** 2026-05-21
**Goal:** Make the in-app Lyrics Capture Mode feel cleaner and more polished — matching the lyrics page's scroll smoothness, tightening the header layout toward Apple Music's capture view, and giving the lyrics a bolder typeface.

## Background

Capture Mode (`LyricsCaptureView`) is a full-screen synced-lyrics overlay for OBS recording. It has two header layouts toggled by a button:

- **Top-left layout** (`IsArtCentered == false`) — horizontal artwork + metadata block in the top-left corner.
- **Top-center layout** (`IsArtCentered == true`) — artwork centered at the top with lyrics flowing under it.

The capture lyric list scrolls with its own eased animation. The lyrics page (`LyricsView`) has a separate, visibly smoother scroll animation. The two do not match.

## Scope

Four focused changes, all confined to Capture Mode (no global app changes):

### 1. Match scroll motion to the lyrics page

`LyricsCaptureView.axaml.cs` currently uses a different scroll animation than `LyricsView.axaml.cs`:

| | Lyrics page | Capture Mode (current) |
|---|---|---|
| Easing | `Easing.SmootherStep(t)` (single) | `Easing.SmootherStep(Easing.SmootherStep(t))` (doubled) |
| Duration | `min(1050, max(650, dist × 0.85))` | `min(1250, max(760, diff × 0.72))` |

**Change:** In `LyricsCaptureView.axaml.cs`:
- `AnimateScroll` easing → single `Easing.SmootherStep(t)`.
- Duration argument in `TryScrollToLine` → `(int)Math.Min(1050, Math.Max(650, diff * 0.85))`.

The active-line **centering** behavior in Capture Mode is intentional and stays unchanged (the lyrics page targets 22% from the top — that is a layout difference, not a smoothness difference).

### 2. Polish the top-left header layout

`LyricsCaptureView.axaml`, the `IsVisible="{Binding !IsArtCentered}"` `StackPanel` (currently lines ~60-88):

- Album art `Border`: `120×120` → `88×88`; `CornerRadius` `12` → `14`.
- Title `TextBlock`: `FontSize` `14` → `17`; `FontWeight` `SemiBold` → `Bold`.
- Artist `TextBlock`: `FontSize` `12` → `13`; `Foreground` `#999999` → `#B5B5B5`.
- Tighten spacing/margins around the block for a cleaner result.

No new controls (the star / more-options buttons in the Apple Music reference are out of scope).

### 3. Nudge the top-center layout left

`LyricsCaptureView.axaml`, the `IsVisible="{Binding IsArtCentered}"` top-center `StackPanel` (currently line ~200):

- Add `RenderTransform="translateX(-60px)"` so the centered art block sits ~60px left of center.

### 4. Use Inter Bold and tighten lyric tracking

Apple Music's typeface (SF Pro) is proprietary and cannot be shipped; Inter is the standard OFL-licensed substitute. The project already references the `Avalonia.Fonts.Inter` NuGet package (`Program.cs` calls `.WithInterFont()`), which bundles Inter at all weights under the family `Inter` addressed by the URI `avares://Avalonia.Fonts.Inter/Assets#Inter`. No new font file or download is needed.

The global `TextBlock` style currently forces `FontFamily` to the static single-weight `Inter-SemiBold.ttf` (`Styles.axaml`), so `FontWeight` has no effect on the capture lyrics today.

- Register a `FontFamily` resource `InterBold` in `App.axaml` next to the existing `InterSemiBold` resource, pointing at `avares://Avalonia.Fonts.Inter/Assets#Inter`.
- On the capture lyric `TextBlock` (the `Classes="capture-line"` element, currently lines ~104-110), set `FontFamily="{StaticResource InterBold}"`, `FontWeight="Bold"`, and `LetterSpacing="-0.5"`. Scope: capture lyrics only.

## Verification

- Build: `dotnet build src/Noctis/Noctis.csproj -v minimal` — must succeed.
- The test project does not compile at baseline (`.claude/rules/testing.md`); verification is build + manual run.
- Manual checks:
  - Capture Mode scroll glide feels identical to the lyrics page.
  - Top-left header: smaller art, bolder/larger title, brighter artist.
  - Top-center header: art block sits noticeably left of center.
  - Capture lyrics render in Inter Bold with slightly tighter tracking.

## Non-goals

- No changes to the lyrics page or any other view.
- No changes to the global app font.
- No new buttons in Capture Mode.
- No changes to the capture sync engine or `LyricsCaptureViewModel` logic.
