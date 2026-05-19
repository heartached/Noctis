# Custom Theme Dialog — Cleanup Design

Date: 2026-05-18
Scope: `ThemeEditorDialog` (Settings → Themes → Custom) and the reusable color-picker control shared with `SettingsView`.

## Goals

1. Make the Custom Theme popup cleaner, simpler, and easier to use.
2. Match the footer button styling/order used by `MetadataWindow` (Cancel left, accent-filled Save right).
3. Replace the three raw hex `TextBox` rows with the same HSV color-picker UX already used by the Accent Color section in `SettingsView`.

Non-goals: new theme tokens, preview-panel changes, picker validation beyond what exists today.

## Affected files

- New: `src/Noctis/Views/Controls/ColorPickerFlyout.axaml`
- New: `src/Noctis/Views/Controls/ColorPickerFlyout.axaml.cs`
- Edit: `src/Noctis/Views/ThemeEditorDialog.axaml` (footer + color rows)
- Edit: `src/Noctis/Views/SettingsView.axaml` (replace inline "+" custom-color flyout with the new control)
- Edit: `src/Noctis/Views/SettingsView.axaml.cs` (remove the HSV/hex pointer handlers now living in the control)

## 1. Footer buttons

Mirror the action bar in [MetadataWindow.axaml:357-389](src/Noctis/Views/MetadataWindow.axaml#L357-L389):

- Order: **Cancel** (left), **Save** (right).
- Both buttons: `Width=100`, `Padding=0,10`, `FontWeight=Bold`, `CornerRadius=999`, `HorizontalContentAlignment=Center`, `Cursor=Hand`.
- Save uses `Classes="accent-btn"`, `Background={DynamicResource AccentColorBrush}`, `Foreground=White`.
- Cancel keeps Avalonia default button background (plain pill).
- Footer wrapper gets a 1px top border (`BorderBrush={DynamicResource SystemControlForegroundBaseLowBrush}`, `BorderThickness=0,1,0,0`) and `Padding=24,12` so the bar visually separates from the form, matching MetadataWindow.
- `SaveCommand` / `CancelCommand` bindings on `ThemeEditorViewModel` stay unchanged.

## 2. New control: `ColorPickerFlyout`

A self-contained reusable `UserControl` that encapsulates the swatch + HSV flyout currently embedded in `SettingsView`.

### Public API
- StyledProperty `Hex` (string, two-way bindable, default `"#000000"`).
  - Setter parses `#RRGGBB`; invalid input leaves internal HSV unchanged but still updates the string (so a partially-typed hex in the flyout textbox doesn't snap-back mid-edit). Same tolerance the current Settings picker has.
- StyledProperty `SwatchSize` (double, default 28). Lets callers render different sizes (Theme Editor rows = 28; the Accent "+" tile uses its own visual override — see §3).
- StyledProperty `SwatchContent` (object, default null). If set, the swatch button hosts this content instead of a filled circle. This is what lets the Accent section keep its rainbow-ring "+" visual.

### Visuals (default)
- Root: `Button` with `Cursor=Hand`, no border, `Background=Transparent`, hosting either:
  - default content: an `Ellipse` of `SwatchSize` filled with the current `Hex`, with a 1px `#1FFFFFFF` stroke, OR
  - `SwatchContent` when supplied.
- `Button.Flyout`: identical XAML to the existing custom-color flyout — 360-wide HSV spectrum (`Border` with hue wash + black overlay + spectrum thumb), 360-wide hue track with thumb, hex text field with leading round swatch — wrapped in a `Border` with `Padding=16`, `CornerRadius=18`, `Background={DynamicResource SystemControlBackgroundChromeMediumBrush}`.

### Code-behind
- Move these handlers verbatim from `SettingsView.axaml.cs` into `ColorPickerFlyout.axaml.cs`:
  - `OnCustomColorSpectrumPointerPressed/Moved`
  - `OnCustomColorHuePointerPressed/Moved`
  - HSV ↔ RGB conversion helpers
  - Hue/spectrum thumb positioning logic
- Internal HSV state (`_hue`, `_sat`, `_val`) lives in the control.
- `Hex` ↔ HSV sync: when `Hex` changes externally, parse and refresh thumbs/wash; when the user drags or types in the flyout, update HSV state and push the new `#RRGGBB` back to `Hex` via `SetCurrentValue`.

## 3. Settings refactor (Accent picker)

In [SettingsView.axaml:741-874](src/Noctis/Views/SettingsView.axaml#L741-L874):
- Replace the inline `Button` + embedded `Flyout` XAML with `<controls:ColorPickerFlyout Hex="{Binding CustomAccentHex, Mode=TwoWay}" SwatchSize="44">` and put the existing rainbow-ring + "+" `Panel` into `SwatchContent`.
- The "active ring when Custom is selected" overlay (`IsVisible="{Binding IsCustomAccentSelected}"`) stays on the outer layout, wrapping the new control, since that state belongs to Settings.
- Remove the handlers listed in §2 from `SettingsView.axaml.cs`.
- `CustomAccentHex` two-way binding behavior is preserved: text edits in the picker still flow to `SettingsViewModel` and re-apply the accent, identical to today.

## 4. Theme Editor color rows

Replace the three `TextBox` rows in [ThemeEditorDialog.axaml:70-87](src/Noctis/Views/ThemeEditorDialog.axaml#L70-L87) with compact swatch rows:

```
Main background
[●]  #121212
```

For each of Main / Sidebar / Accent:
- `StackPanel Orientation="Horizontal" Spacing="10"`
  - `<controls:ColorPickerFlyout Hex="{Binding MainHex, Mode=TwoWay}" />`
  - `<TextBlock Text="{Binding MainHex}" FontFamily="Consolas, Courier New, monospace" FontSize="12" Foreground="{DynamicResource SecondaryTextBrush}" VerticalAlignment="Center"/>`
- The section label (`"Main background"` etc.) stays as the existing `FontSize=13 FontWeight=SemiBold` TextBlock above the row.

Effect: the left input column shrinks from ~5 stacked text fields to Name + Base mode + three small swatch rows. The dialog stays the same width (760×520) but feels less form-heavy.

## 5. ViewModel changes

`ThemeEditorViewModel` already exposes `MainHex` / `SidebarHex` / `AccentHex` as observable two-way strings driving `RebuildPreview()`. No new properties or commands needed.

## 6. Out of scope

- No changes to `ThemeDerivation` or `CustomThemeDefinition`.
- Preview panel (right side) and its bindings stay as-is.
- No new validation surfacing for malformed hex beyond today's silent-leave behavior.
- No keyboard shortcuts or accessibility work beyond reusing existing button styles.

## 7. Verification

- `dotnet build src/Noctis/Noctis.csproj -v minimal` succeeds.
- Manual: open Settings → Custom theme → confirm Cancel/Save match the Metadata window styling and order; confirm clicking each color swatch opens the HSV flyout; confirm dragging or editing hex updates the live preview panel.
- Manual: open Settings → Accent Color → "+" tile still opens the same picker and live-applies the accent.
- Manual regression: existing Dark/Gray/Midnight/Light/System tiles and accent swatches behave unchanged.
