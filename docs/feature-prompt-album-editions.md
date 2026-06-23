# Feature request prompt: collapse album editions in the discography (opt-in)

Paste this whole file into a new chat to implement the feature. It is self-contained.

---

## Goal

In Noctis (an Avalonia/C# desktop music app), add an **optional setting** that collapses
multiple editions/issues of the same release into a **single "main" tile** in the album
grids, instead of showing every edition. The hidden editions stay reachable via the album
page's existing **"Other Versions"** section.

Example of the problem: *Pluto* by Future may exist as the main issue + iTunes bonus +
deluxe + "Pluto 3D" reissue + "Pluto 3D" iTunes bonus. Today all 5 show as separate tiles
and clutter the discography. With this feature on, only the main edition shows in the grid.

This is GitHub issue #6 ("[Feature] Different issues of album"). The requester explicitly
asked for it as a **setting that is OFF by default**.

## Requirements / design decisions (already agreed)

- **Opt-in. Default OFF.** Add a Settings toggle, e.g. "Collapse album editions" /
  "Group different editions of an album". When off, behavior is exactly as today.
- **Where to collapse:** the album grids in `LibraryAlbumsViewModel.BuildFilteredRows`
  (the Albums section and the artist-filtered discography). Do **not** change the album
  detail page — its "Other Versions" section already lists every edition and is how users
  reach the hidden ones.
- **Grouping key:** same album-artist credit + same *normalized base title*. Normalization
  must strip edition suffixes: `(Deluxe Edition)`, `(Reissue)`, `(Clean)`, `[Remastered]`,
  `(... Edition)`, ` - Single`, ` - EP`, and embedded `(feat. ...)` groups. This logic
  already exists (see below) — reuse it, don't reinvent it.
- **Representative ("main") edition selection,** in priority order:
  1. the plain/base edition (title equals its own normalized base), else
  2. the one with the most tracks (most complete), else
  3. the earliest release year.
  Preserve the grid's existing sort order by anchoring each group at its first occurrence.
- **Don't collapse while searching** — when a search filter is active, show every edition
  so a specific edition can be found.
- Keep the high-density virtualized grid performant (it builds `AlbumRow` chunks; don't
  break row virtualization or `BulkObservableCollection.ReplaceAll` usage).

## Existing infrastructure to REUSE (do not duplicate)

- `src/Noctis/ViewModels/AlbumDetailViewModel.cs`
  - Has `ObservableCollection<Album> OtherVersions`, `HasOtherVersions`, and a private
    `NormalizeAlbumTitle(string?)` that already strips all the edition suffixes above
    (regexes `s_featRegex`, `s_trailingParensRegex`, `s_trailingDashSuffixRegex`). This is
    the exact normalization the collapse should use.
  - Recommended: extract that normalization into a shared static helper
    `src/Noctis/Helpers/AlbumTitle.cs` (e.g. `NormalizeForEdition(string?)` and
    `IsBaseEdition(string?)`), have `AlbumDetailViewModel` call it (no behavior change),
    and reuse it from the albums grid.
- `src/Noctis/ViewModels/LibraryAlbumsViewModel.cs`
  - `BuildFilteredRows(List<Album> allAlbums, string artistFilter, string searchFilter,
    int columnsPerRow, ReleaseType? releaseTypeFilter)` is where filtering/sorting/row
    chunking happens. Insert the collapse step after `ordered` is computed and before the
    rows are chunked, gated on: setting enabled AND `string.IsNullOrWhiteSpace(searchFilter)`.
  - It already has `GetPrimaryArtist`, `ContainsArtistToken`, `GetArtistDiscographyRank`
    helpers you can reference for artist matching.
- `Album` model has `Name`, `Artist`, `TrackCount`, `Year`, `Id`, `Tracks`.

## Settings wiring (needed for the toggle)

- `src/Noctis/Models/AppSettings.cs` — add a `bool CollapseAlbumEditions` (default `false`).
- `src/Noctis/ViewModels/SettingsViewModel.cs` — expose an `[ObservableProperty]` bound to
  it, persist on change (follow how existing toggles like `EnableAnimatedCovers` are saved),
  and notify the albums grid to rebuild when it changes.
- `src/Noctis/Views/SettingsView.axaml` — add a toggle row in a sensible card (e.g. near
  other library/display options), matching the existing `setting-card` / ToggleSwitch style.
- `LibraryAlbumsViewModel` needs to read the setting and rebuild `FilteredAlbumRows` when it
  flips (it already rebuilds on filter changes; hook the same path).

## Reference implementation sketch (from an earlier prototype)

```csharp
// In LibraryAlbumsViewModel, after `ordered` is built, before chunking into AlbumRow:
if (collapseEditionsEnabled && string.IsNullOrWhiteSpace(searchFilter))
    ordered = CollapseEditions(ordered);

private static IEnumerable<Album> CollapseEditions(IEnumerable<Album> albums)
{
    var groups = new Dictionary<string, Album>(StringComparer.OrdinalIgnoreCase);
    var order = new List<string>();
    foreach (var a in albums)
    {
        var baseTitle = Helpers.AlbumTitle.NormalizeForEdition(a.Name);
        var key = string.IsNullOrEmpty(baseTitle)
            ? $" id:{a.Id}"                                   // never merge untitled
            : $"{(a.Artist ?? string.Empty).Trim()} {baseTitle}";
        if (!groups.TryGetValue(key, out var rep)) { groups[key] = a; order.Add(key); }
        else if (IsBetterEditionRepresentative(a, rep)) groups[key] = a;
    }
    return order.Select(k => groups[k]);
}

private static bool IsBetterEditionRepresentative(Album cand, Album cur)
{
    var cb = Helpers.AlbumTitle.IsBaseEdition(cand.Name);
    var ub = Helpers.AlbumTitle.IsBaseEdition(cur.Name);
    if (cb != ub) return cb;                                  // prefer plain edition
    if (cand.TrackCount != cur.TrackCount) return cand.TrackCount > cur.TrackCount; // most complete
    if (cand.Year != cur.Year) return cand.Year != 0 && (cur.Year == 0 || cand.Year < cur.Year); // earliest
    return false;
}
```

`AlbumTitle.NormalizeForEdition` should mirror `AlbumDetailViewModel.NormalizeAlbumTitle`
(strip trailing `(...)`/`[...]` segments iteratively, ` - Single`/` - EP`, and `(feat. ...)`).
`IsBaseEdition(name)` = `name.Trim()` equals its own `NormalizeForEdition` (case-insensitive).

## Acceptance criteria

- New Settings toggle exists, **default off**, persisted across restarts.
- With it **off**: album grids are byte-for-byte the same as before.
- With it **on**: in the Albums grid and artist discography, editions of one release show as a
  single tile; the chosen tile follows the representative rules above.
- Searching still shows all editions.
- The album detail page is unchanged; "Other Versions" still lists the editions.
- Album detail behavior unchanged after extracting the shared normalization helper.

## Build / test (this project)

- Build: `dotnet build src/Noctis/Noctis.csproj -v minimal`
  - Note: if the app is running, the post-build exe copy is locked; build to a temp output
    to compile-check: add `-p:UseAppHost=false -p:BaseOutputPath=obj/verifybuild/ -p:OutDir=obj/verifybuild/out/`.
- Tests: `dotnet test tests/Noctis.Tests/Noctis.Tests.csproj -v minimal`
- The author launches/verifies the app themselves — don't offer to run it.

## Constraints / house rules

- Minimal diffs; match existing MVVM patterns (CommunityToolkit `[ObservableProperty]` /
  `[RelayCommand]`, `x:DataType` compiled bindings).
- Don't introduce new dependencies.
- Reuse existing style resources (`setting-card`, ToggleSwitch styling) in `SettingsView`.
- Keep release hygiene: no AI/Claude mentions in commits; don't add `.claude`/CLAUDE.md to
  releases.
