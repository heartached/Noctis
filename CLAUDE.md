# Noctis Claude Instructions

## Purpose
Keep changes small, verifiable, and grounded in this repository. Avoid guessing, wide refactors, and token-heavy exploration.

## Ground Truth (Architecture)
- Platform: Windows desktop app.
- Stack: C# 12, .NET 8, Avalonia UI 11, CommunityToolkit.Mvvm.
- Audio backend: LibVLCSharp + VideoLAN.LibVLC.Windows.
- Metadata: TagLibSharp (optional ffprobe fallback for missing metrics).
- Persistence: JSON files under `%APPDATA%\Noctis\` plus `library.db` SQLite track index.
- DI/composition root: `src/Noctis/Program.cs` + `src/Noctis/App.axaml.cs`.

## Folder Map
- UI: `src/Noctis/Views`, global styles in `src/Noctis/Assets/Styles.axaml`.
- ViewModels/state: `src/Noctis/ViewModels`.
- Playback/audio: `src/Noctis/Services/VlcAudioPlayer.cs`, `src/Noctis/ViewModels/PlayerViewModel.cs`.
- Library/indexing: `src/Noctis/Services/LibraryService.cs`, `src/Noctis/Services/SqliteLibraryIndexService.cs`.
- Lyrics: `src/Noctis/ViewModels/LyricsViewModel.cs`, `src/Noctis/Services/LrcLibService.cs`.
- Settings: `src/Noctis/ViewModels/SettingsViewModel.cs`, `src/Noctis/Models/AppSettings.cs`.
- Persistence/cache: `src/Noctis/Services/PersistenceService.cs`, `ArtworkCache`, `OfflineCacheService`.
- Tests: `tests/Noctis.Tests`.

## Non-Negotiable Rules
1. Do not invent symbols, files, APIs, or behavior.
2. If something is missing, run repo search first (`rg -n "<pattern>" src tests`), then report what was searched and what was not found.
3. Prefer minimal diffs in existing files over new abstractions.
4. Do not add new dependencies or change architecture unless explicitly requested.
5. Reuse established patterns (MVVM attributes, `Dispatcher.UIThread.Post`, `BulkObservableCollection.ReplaceAll`, existing style classes).
6. Never treat `bin/`, `obj/`, or `artifacts/` outputs as source of truth.
7. Fix root causes, not visual or one-off UI hacks.

## Workflow (Mandatory)
1. Explore
- Read only files needed for the task.
- Confirm current behavior in code before proposing changes.
2. Plan
- State the smallest viable change and impacted files.
- Call out risks/regressions up front.
3. Implement
- Keep edits scoped; avoid drive-by refactors.
- Preserve threading and event-subscription lifecycles.
4. Verify
- Run relevant build/tests.
- Report exact command results and any remaining risk.

## Commands
- Restore: `dotnet restore src/Noctis/Noctis.csproj`
- Build app: `dotnet build src/Noctis/Noctis.csproj -v minimal`
- Run app: `dotnet run --project src/Noctis/Noctis.csproj`
- Test: `dotnet test tests/Noctis.Tests/Noctis.Tests.csproj -v minimal`
- Publish (script): `publish-windows.bat`
- Publish (manual):
  - `dotnet publish src/Noctis/Noctis.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish\\win-x64`
- Lint/format: no dedicated lint command is configured in this repo.

## Verification Notes (Current Baseline)
- As of 2026-02-15, app build passes with `dotnet build src/Noctis/Noctis.csproj -v minimal`.
- As of 2026-02-15, test command fails to compile:
  - `tests/Noctis.Tests/TestPersistenceService.cs` does not implement
  - `IPersistenceService.LoadIndexCacheAsync()` and `IPersistenceService.SaveIndexCacheAsync(...)`.
- Do not claim full test pass unless this baseline issue is fixed or explicitly waived.

## Definition of Done
- Code compiles for impacted project(s).
- Tests for impacted area pass, or baseline failures are explicitly documented with unchanged/new status.
- No new threading deadlocks, UI-thread blocking, or event-leak regressions.
- No architecture/dependency drift unless requested.
- Behavior matches existing UX patterns (commands, bindings, styles, context menus).

## Performance Guardrails
- Keep heavy operations off UI thread (scan/filter/sort/metadata/network).
- Preserve list performance:
  - Use `BulkObservableCollection.ReplaceAll` for batch updates.
  - Keep album grid row virtualization pattern (`FilteredAlbumRows` -> outer `ListBox` + row `UniformGrid`).
- Keep async image loading via `CachedImage` + `ArtworkCache`; do not decode bitmaps synchronously in item templates.
- Preserve search debounce and generation guards in list/lyrics flows.
- Preserve VlcAudioPlayer threading rules: no `Play/Stop/Pause` calls from VLC event threads.
- Avoid global style changes that trigger per-item animation churn in virtualized lists.

## Root-Cause Policy
- For UI bugs, trace View <-> ViewModel <-> Service path and fix the state/event source.
- For data bugs, fix persistence/index logic before adding view-level compensations.
- For playback bugs, respect existing lock/timer/event orchestration before changing queue logic.

