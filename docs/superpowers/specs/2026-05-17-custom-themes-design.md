# Custom Themes — Design

**Date:** 2026-05-17
**Status:** Approved for planning
**Branch:** cross-platform

## Goal

Let users create, save, name, edit, and delete their own themes from Settings → Themes. Every custom theme must meet the same readability bar as the built-in themes (Gray, Dark, Light, Midnight) across every page, panel, and dialog in Noctis.

## Non-goals

- Importing or exporting theme files.
- Sharing themes between users / cloud sync.
- Per-page overrides or gradient backgrounds.
- Exposing every theme key as a direct knob (full theme-editor style). The readability constraint rules this out — users must not be able to ship themselves an unreadable UI.

## Approach: curated palette + derivation

The user picks five inputs; a deterministic derivation function expands those into the same ~50-key `ResourceDictionary` that the built-in theme overlays define today.

### Five user inputs

| Knob | Type | What it controls |
|---|---|---|
| **Name** | string | Display name on the theme tile |
| **Base mode** | `Light` \| `Dark` | Avalonia `ThemeVariant` + text-color polarity |
| **Main background** | color (hex) | `AppMainBackground` / `AppMainBackgroundColor` |
| **Sidebar background** | color (hex) | `AppSidebarBackground` |
| **Accent** | color (hex) | `SystemAccentColor` family + `AccentColorBrush` |

Everything else (secondary text, stripes, hover, Island, sliders, sidebar selected/hover, home card pills, etc.) is derived. The user does not see those knobs.

## Architecture

### New types

```csharp
// Models/CustomThemeDefinition.cs
public sealed class CustomThemeDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string BaseMode { get; set; } = "Dark";  // "Dark" | "Light"
    public string MainBackgroundHex { get; set; } = "#121212";
    public string SidebarBackgroundHex { get; set; } = "#1C1C1C";
    public string AccentHex { get; set; } = "#E74856";
}
```

### Persistence

- Add `public List<CustomThemeDefinition> CustomThemes { get; set; } = new();` to [src/Noctis/Models/AppSettings.cs](src/Noctis/Models/AppSettings.cs).
- The existing `AppSettings.Theme` string now also accepts the form `"Custom:<id>"`. The built-in values (`Gray`, `Dark`, `Light`, `Midnight`, `System`) continue to work unchanged.
- Settings are already JSON-serialized; an old config loads with an empty list — no migration code required.

### Theme derivation

New `Services/ThemeDerivation.cs` with a single pure function:

```csharp
public static IDictionary<string, object> Derive(CustomThemeDefinition def);
```

Rules:

1. **Base variant.** Returned dictionary carries a `__BaseVariant` sentinel (`Light`/`Dark`) that `App.SetTheme` reads to set `RequestedThemeVariant`.
2. **Text colors.** Primary text picked so contrast against `MainBackground` is ≥ 4.5:1; secondary ≥ 3:1; tertiary ≥ 2:1. We compute WCAG relative luminance and clamp by lightening (dark base) or darkening (light base) until the threshold is met.
3. **Surface ramp.** Stripes / hover / selected derive as alpha overlays at fixed luminance steps — for dark base: `#08FFFFFF`, `#14FFFFFF`, `#1F1F1F`, `#2A2A2A` etc., matching the values currently in [src/Noctis/Assets/Themes/Dark.axaml](src/Noctis/Assets/Themes/Dark.axaml). For light base: equivalent black overlays at the same alpha steps.
4. **Island.** `IslandBackground` is an opaque variant of `MainBackground` (force alpha to 0xF0); foreground/secondary/tertiary derived by the same contrast rule against `IslandBackground`.
5. **Accent shades.** Reuse the existing accent-shade generator at [src/Noctis/App.axaml.cs:170](src/Noctis/App.axaml.cs#L170) (HSL lighten/darken) so custom themes get the same Dark1/2/3 + Light1/2/3 family the rest of the app already binds to.
6. **System chrome keys.** All `SystemChrome*`, `SystemAlt*`, `SystemControlBackground*`, and `SystemControlPageBackground*` keys resolve to `MainBackground` for dark base, or a near-white surface for light base — matching the pattern in `Dark.axaml`.

The full key list to populate is the union of keys defined in `Dark.axaml` and `Midnight.axaml`. Derivation must emit every one of them so no `DynamicResource` falls back to an Avalonia default mid-app.

### Wiring into the app

Extend `App.SetTheme(string themeName)` in [src/Noctis/App.axaml.cs:96](src/Noctis/App.axaml.cs#L96):

- If `themeName` starts with `"Custom:"`, parse the id, look up the definition from `SettingsService`, call `ThemeDerivation.Derive`, build a `ResourceDictionary` from the result, and merge it as the active overlay (replacing any prior overlay, same as today's file-based overlays).
- Re-apply the user's accent **after** the overlay merges, same as today (the existing code comment at [App.axaml.cs:123](src/Noctis/App.axaml.cs#L123) already documents this invariant). When a custom theme is active, the accent stored on the `CustomThemeDefinition` becomes the active accent.

### ViewModel changes

In [src/Noctis/ViewModels/SettingsViewModel.cs](src/Noctis/ViewModels/SettingsViewModel.cs):

- New `ObservableCollection<CustomThemeTile> CustomThemes` (tile = id, name, accent preview, is-active flag).
- New `[ObservableProperty] string? _activeCustomThemeId`.
- New commands:
  - `[RelayCommand] OpenThemeEditor(CustomThemeDefinition? existing)` — opens the editor dialog; `null` means new.
  - `[RelayCommand] ApplyCustomTheme(string id)` — sets `AppSettings.Theme = "Custom:" + id`, fires `ThemeChanged`.
  - `[RelayCommand] DeleteCustomTheme(string id)` — confirmation dialog, removes from list, falls back to Gray if it was active.
  - `[RelayCommand] RenameCustomTheme(string id)` — inline rename (small input dialog).
- `SetActiveThemeFlags` extended to clear all built-in flags when a custom theme is active.
- `ResolveActiveThemeKey` returns `"Custom:" + _activeCustomThemeId` when applicable.
- `SaveAsync` persists the `CustomThemes` list.
- Reset-to-defaults clears `CustomThemes` (matches existing reset semantics).

### Editor dialog

New `Views/ThemeEditorDialog.axaml(.cs)` + `ViewModels/ThemeEditorViewModel.cs`. Matches the visual style of existing dialogs like [CreatePlaylistDialog.axaml](src/Noctis/Views/CreatePlaylistDialog.axaml).

Layout:

- **Top:** Name input.
- **Row:** Base-mode toggle (Dark / Light), as two pill buttons.
- **Three color pickers:** Main background, Sidebar background, Accent. Each uses the same `ColorPicker` control already used for custom accent in [SettingsView.axaml](src/Noctis/Views/SettingsView.axaml).
- **Live preview card** (right side, fills remaining space): a miniature mock-up rendered with the *currently-edited* derived dictionary, showing:
  - Sidebar pill with two nav rows (one selected, one hover).
  - Main area with a section header and one fake track row (album-art placeholder, title, subtitle, EQ visualizer pip).
  - Mini Playback Island at the bottom with title, subtitle, slider, play button.
- **Footer:** Cancel / Save buttons. Save validates the name is non-empty and unique within `CustomThemes`.

The preview updates whenever any input changes (debounced ~50 ms). The preview swatches/text are bound to a *local* `ResourceDictionary` scoped to the preview surface — not the global app dictionary — so editing does not flicker the rest of the Settings view.

### View changes — Settings → Themes row

In [src/Noctis/Views/SettingsView.axaml](src/Noctis/Views/SettingsView.axaml):

- The themes row becomes a horizontal `ItemsControl` that renders five built-in tiles + `CustomThemes` tiles + a trailing **"+ Custom"** add tile.
- Each custom tile uses the same visual template as the built-in tiles (the small preview rectangle + label below). The preview rectangle inside the tile renders a two-tone swatch: left half `SidebarBackgroundHex`, right half `MainBackgroundHex`, with an accent-colored "title bar" stripe inside the right half. Selection ring uses the active-accent style already on built-in tiles.
- Right-click on a custom tile opens a context menu: **Edit**, **Rename**, **Delete**. Built-in tiles have no context menu.
- The "+ Custom" tile is the same size, with a dashed border and a centered "+" glyph. Clicking opens the editor with a new definition.

If the row would overflow, it wraps to a second line — `WrapPanel`, since horizontal scroll inside Settings is ugly and the user is likely to have ≤ 10 custom themes in practice.

## Data flow

1. User clicks "+ Custom" → `OpenThemeEditor(null)` → editor dialog opens with default values.
2. User edits inputs → `ThemeEditorViewModel` recomputes derived dictionary on each change → preview surface re-renders.
3. User clicks Save → editor returns the definition → `SettingsViewModel` adds it to `CustomThemes`, calls `SaveAsync`, calls `ApplyCustomTheme(id)`.
4. `ThemeChanged` fires → `App.SetTheme("Custom:<id>")` → derivation runs → overlay merged → accent re-applied → entire UI repaints under the new dictionary.

## Error handling

- Invalid hex input in the color picker is rejected by the existing picker control — no new validation needed there.
- Empty name → Save button disabled.
- Duplicate name → Save button disabled with inline message "A theme with this name already exists".
- A `CustomThemeDefinition` that fails to parse from JSON (corrupt settings) is skipped on load and a debug log entry written; the app falls back to Gray.
- If the user deletes the active custom theme, the app falls back to Gray and persists that immediately.

## Testing

Unit tests in `tests/Noctis.Tests/`:

- `ThemeDerivationTests`
  - Every key present in `Dark.axaml` is also present in the derived dictionary for a Dark-mode custom theme.
  - Every key present in the Light-variant baseline is present for a Light-mode custom theme.
  - For 6 representative input palettes (very dark, very light, low-contrast bg+accent, neon, pastel, monochrome): primary-text contrast ≥ 4.5, secondary ≥ 3, tertiary ≥ 2 against `MainBackground` and against `IslandBackground`.
  - Accent shade generator agrees with the existing in-app generator for a fixed input (regression guard).
- `CustomThemePersistenceTests`
  - Round-trip a `CustomThemeDefinition` through `AppSettings` JSON.
  - Loading an `AppSettings` JSON with `Theme = "Custom:<id>"` but no matching entry falls back to Gray.

Manual verification (golden path):

- Create a custom theme; verify every page (Home, Library Songs/Albums/Artists/Playlists, Favorites, AlbumDetail, Lyrics, Settings, the Playback Island) renders with readable text and visible separators.
- Edit it; verify changes propagate live.
- Delete the active one; verify fallback to Gray.
- Restart the app; verify the active custom theme reloads.

## Files

**New:**

- [src/Noctis/Models/CustomThemeDefinition.cs](src/Noctis/Models/CustomThemeDefinition.cs)
- [src/Noctis/Services/ThemeDerivation.cs](src/Noctis/Services/ThemeDerivation.cs)
- [src/Noctis/Views/ThemeEditorDialog.axaml](src/Noctis/Views/ThemeEditorDialog.axaml)
- [src/Noctis/Views/ThemeEditorDialog.axaml.cs](src/Noctis/Views/ThemeEditorDialog.axaml.cs)
- [src/Noctis/ViewModels/ThemeEditorViewModel.cs](src/Noctis/ViewModels/ThemeEditorViewModel.cs)
- `tests/Noctis.Tests/ThemeDerivationTests.cs`
- `tests/Noctis.Tests/CustomThemePersistenceTests.cs`

**Modified:**

- [src/Noctis/App.axaml.cs](src/Noctis/App.axaml.cs) — extend `SetTheme` to handle `Custom:<id>`.
- [src/Noctis/Models/AppSettings.cs](src/Noctis/Models/AppSettings.cs) — add `CustomThemes` list.
- [src/Noctis/ViewModels/SettingsViewModel.cs](src/Noctis/ViewModels/SettingsViewModel.cs) — tile collection, commands, persistence wiring.
- [src/Noctis/Views/SettingsView.axaml](src/Noctis/Views/SettingsView.axaml) — themes row becomes ItemsControl + add tile + context menus.

## Risks / unknowns

- **Avalonia resource flush.** When merging a freshly-built `ResourceDictionary` (vs. loading a baked `.axaml` file), some `DynamicResource` consumers may not re-evaluate without a nudge. Mitigation: the existing `SetTheme` already removes/re-adds an overlay and re-applies the accent dictionary; merging a runtime-built dictionary follows the same path. If a control fails to refresh, the fix is on the existing overlay-swap path and benefits all themes equally.
- **Live preview perf.** Building the full derived dictionary on every color-picker drag could allocate heavily. Mitigation: debounce 50 ms; the dictionary is ~50 entries — cheap.
- **Light-mode key coverage.** Today there is no Light overlay file (Light uses the bare Avalonia Light variant). The derivation must emit a complete dictionary for Light too. We will diff against a snapshot of the Light-variant keys actually consumed by the app (greppable from `DynamicResource` references) and ensure all are produced.
