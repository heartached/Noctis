# Playback Bar Icon Update & Button Sizing Fix

**Date:** 2026-03-28

## Summary

Replace the Lyrics Panel button icon with the custom PNG asset and normalize all right-side playback bar button icon sizes to 16x16.

## Scope

**File:** `src/Noctis/Views/PlaybackBarView.axaml`

**Buttons affected (right side of playback bar):**
- Options (three dots)
- Queue
- Lyrics Panel
- Lyrics
- Volume

## Changes

### 1. Replace Lyrics Panel icon

- Current: SVG `PathIcon` using `SidePanelIcon` geometry (lines 727-729)
- New: PNG-based icon using `Border` + `OpacityMask` pattern (same as Queue and Lyrics buttons)
- Source: `avares://Noctis/Assets/Icons/Lyrics%20Panel%20ICON.png`
- Background: `{DynamicResource IslandIconFill}` for automatic light/dark theme support

### 2. Normalize inner icon sizes to 16x16

All 5 buttons already use `Width="34" Height="34"` containers. Standardize inner icon elements:

| Button | Current inner size | Method | Change needed |
|---|---|---|---|
| Options | Viewbox 18x18 | Viewbox+Path | Resize Viewbox to 16x16 |
| Queue | Border 15x15 | Border+OpacityMask | Resize Border to 16x16 |
| Lyrics Panel | PathIcon 16x16 | PathIcon | Replace with Border+OpacityMask 16x16 |
| Lyrics | Border 16x16 | Border+OpacityMask | None |
| Volume | Viewbox 18x18 | Viewbox+Path | Resize Viewbox to 16x16 |

### 3. No behavior changes

- All click handlers, commands, flyouts, tooltips, hover/press states remain unchanged
- `secondary-btn` class and its opacity transitions stay as-is
- Volume slider expansion behavior untouched

## Theme support

The `Border` + `OpacityMask` technique uses `{DynamicResource IslandIconFill}` as the `Background`, which already adapts to light/dark themes. The PNG is black-on-transparent and acts purely as a mask shape. No additional theme wiring needed.
