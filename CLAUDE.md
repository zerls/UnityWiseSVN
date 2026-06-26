# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is WiseSVN for Unity?

An SVN (Subversion) version control integration plugin for the Unity Editor. It hooks into Unity's asset pipeline to keep SVN operations in sync with file operations, provides overlay icons showing SVN status in the Project window, context menus for SVN commands, lock-prompting workflows, and a branch selector. Works on Windows (TortoiseSVN), macOS (SnailSVN), and Linux (RabbitVCS or CLI).

- Author: Filip Slavov (NibbleByte)
- License: MIT
- Unity target: 2022.3.8f1 (backward compatible to 2018.4 via `#if` directives)
- Published as: `devlocker.versioncontrol.wisesvn` on OpenUPM / Asset Store

## Commands / Scripts

| Command | Purpose |
|---|---|
| `makeupm.bat` | Publish plugin subtree to `upm` branch via `git subtree push --prefix Assets/DevLocker/VersionControl/WiseSVN origin upm` |

There is no build system — the plugin is a standard Unity project; Unity's asset pipeline compiles it as part of `Assembly-CSharp-Editor`. No tests or CI exist in the repo.

## Architecture

All source code lives under `Assets/DevLocker/VersionControl/WiseSVN/Editor/`. The plugin is purely an Editor extension — no runtime (play-mode) code.

### Layers (bottom to top)

**1. Shell / Process Layer** — [`ShellUtils.cs`](Assets/DevLocker/VersionControl/WiseSVN/Editor/ShellUtils.cs)
- Static utility class that executes CLI processes via `System.Diagnostics.Process`
- Cross-platform: Windows (`ProcessWindowStyle.Normal`), macOS (bash script + Terminal.app), Linux (xdg-terminal-exec)
- Defines `ShellResult`, `ShellArgs`, and the `IShellMonitor` interface for real-time stdout/stderr monitoring

**2. Core Integration** — [`WiseSVNIntegration.cs`](Assets/DevLocker/VersionControl/WiseSVN/Editor/WiseSVNIntegration.cs) (2428 lines)
- The central class. Decorated with `[InitializeOnLoad]`, extends `AssetModificationProcessor`.
- Hooks Unity asset lifecycle methods: `OnWillCreateAsset`, `OnWillDeleteAsset`, `OnWillMoveAsset`
- Wraps all SVN CLI commands as public static methods, both sync and async (suffixed `*Async`)
  - `GetStatuses`, `LockFile(s)`, `UnlockFile(s)`, `Update`, `Commit`, `Revert`, `Add`, `Delete`, `Log`, `ListURL`, `Propget`, `Propset`, `ChangelistAdd`, `ChangelistRemove`
  - Utility: `AssetPathToURL`, `AssetPathToRelativeURL`, `GetWorkingCopyRootPath`, `GetWorkingCopyRootURL`, `GetLastChangedRevision`, `CheckForSVNErrors`, `CheckForSVNAuthErrors`
- Manages silence counters (`RequestSilence`/`ClearSilence`) and temporary disable counters (`RequestTemporaryDisable`/`ClearTemporaryDisable`)
- Contains nested class `ResultConsoleReporter` for tracing SVN command output
- Uses `AssetModificationProcessor` — NOT `AssetPostprocessor` — to intercept moves/deletes before they happen

**3. Async Operation System** — [`SVNAsyncOperation.cs`](Assets/DevLocker/VersionControl/WiseSVN/Editor/SVNAsyncOperation.cs)
- A custom promise/future class that spawns a background thread to execute SVN operations
- On completion, invokes the `Completed` event on the Unity main thread via `EditorApplication.update`
- Handles assembly reload by aborting the background thread

**4. Data Types** — [`SVNDataTypes.cs`](Assets/DevLocker/VersionControl/WiseSVN/Editor/SVNDataTypes.cs)
- All enums (`VCFileStatus`, `VCPropertiesStatus`, `VCLockStatus`, etc.) and structs (`SVNStatusData`, `LockDetails`, `LogParams`, `LogEntry`, `LogPath`, `PropgetEntry`, `SVNMoveBehaviour`, etc.)

**5. Status Database** — [`SVNStatusesDatabase.cs`](Assets/DevLocker/VersionControl/WiseSVN/Editor/SVNStatusesDatabase.cs)
- Extends `DatabasePersistentSingleton<SVNStatusesDatabase, GuidStatusDatasBind>`
- Periodically runs `svn status` in a background worker thread, stores results keyed by Unity GUID
- Companion `SVNStatusesDatabaseAssetPostprocessor` hooks `OnPostprocessAllAssets` for incremental updates
- Sanity limits: 600 status entries, 250 unversioned folders, 250 ignore entries

**6. Overlay Icons** — [`SVNOverlayIcons.cs`](Assets/DevLocker/VersionControl/WiseSVN/Editor/SVNOverlayIcons.cs)
- Hooks `EditorApplication.projectWindowItemOnGUI` to draw SVN status icons (file status, lock status, remote status) on each asset in the Project window
- Handles list view and grid view at different zoom levels

**7. SceneView Overlay** — [`SVNLockedOverlay.cs`](Assets/DevLocker/VersionControl/WiseSVN/Editor/SVNLockedOverlay.cs)
- Shows a red warning banner in the Scene View when the current scene/prefab is out of date, locked by another user, or has a broken lock

**8. Context Menus** — [`ContextMenus/`](Assets/DevLocker/VersionControl/WiseSVN/Editor/ContextMenus/)
- [`SVNContextMenusManager`](Assets/DevLocker/VersionControl/WiseSVN/Editor/ContextMenus/SVNContextMenusManager.cs) — static menu API and `Assets/SVN/...` menu items
- [`SVNContextMenusBase`](Assets/DevLocker/VersionControl/WiseSVN/Editor/ContextMenus/SVNContextMenusBase.cs) — abstract base with `FileArgumentsSeparator`/`FileArgumentsSurroundQuotes`
- Platform implementations: [`TortoiseSVNContextMenus`](Assets/DevLocker/VersionControl/WiseSVN/Editor/ContextMenus/TortoiseSVNContextMenus.cs) (Windows), [`SnailSVNContextMenus`](Assets/DevLocker/VersionControl/WiseSVN/Editor/ContextMenus/SnailSVNContextMenus.cs) (macOS), [`RabbitSVNContextMenu`](Assets/DevLocker/VersionControl/WiseSVN/Editor/ContextMenus/RabbitSVNContextMenu.cs) (Linux), [`CLIContextMenus`](Assets/DevLocker/VersionControl/WiseSVN/Editor/ContextMenus/CLIContextMenus.cs) (fallback)

**9. Lock Prompting** — [`LockPrompting/`](Assets/DevLocker/VersionControl/WiseSVN/Editor/LockPrompting/)
- [`SVNLockPromptDatabase`](Assets/DevLocker/VersionControl/WiseSVN/Editor/LockPrompting/SVNLockPromptDatabase.cs) — monitors newly modified assets and prompts the user to lock them (Perforce-like checkout)
- [`SVNLockPromptWindow`](Assets/DevLocker/VersionControl/WiseSVN/Editor/LockPrompting/SVNLockPromptWindow.cs) — the prompt dialog UI
- Rules-based: target folders, asset types, exclusions; supports auto-lock and auto-unlock

**10. Branch Selector** — [`Branches/`](Assets/DevLocker/VersionControl/WiseSVN/Editor/Branches/)
- [`SVNBranchesDatabase`](Assets/DevLocker/VersionControl/WiseSVN/Editor/Branches/SVNBranchesDatabase.cs) — scans the SVN repository for branches using `svn list`
- [`SVNBranchSelectorWindow`](Assets/DevLocker/VersionControl/WiseSVN/Editor/Branches/SVNBranchSelectorWindow.cs) — UI listing branches and Unity projects in them

**11. Preferences** — [`Preferences/`](Assets/DevLocker/VersionControl/WiseSVN/Editor/Preferences/)
- [`SVNPreferencesManager`](Assets/DevLocker/VersionControl/WiseSVN/Editor/Preferences/SVNPreferencesManager.cs) — singleton managing personal prefs (`UserSettings/WiseSVN.prefs`) and project prefs (`ProjectSettings/WiseSVN.prefs`), stored as JSON via `JsonUtility`
- [`SVNPreferencesWindow`](Assets/DevLocker/VersionControl/WiseSVN/Editor/Preferences/SVNPreferencesWindow.cs) — custom `EditorWindow` for settings
- [`SVNPreferencesSettingsProvider`](Assets/DevLocker/VersionControl/WiseSVN/Editor/Preferences/SVNPreferencesSettingsProvider.cs) — Unity `SettingsProvider` integration

**12. Singleton Infrastructure** — [`Utils/`](Assets/DevLocker/VersionControl/WiseSVN/Editor/Utils/)
- [`EditorPersistentSingleton<T>`](Assets/DevLocker/VersionControl/WiseSVN/Editor/Utils/EditorPersistentSingleton.cs) — base class for singletons surviving assembly reloads (hidden `ScriptableObject` with `HideFlags.HideAndDontSave`)
- [`DatabasePersistentSingleton<T, DataType>`](Assets/DevLocker/VersionControl/WiseSVN/Editor/Utils/DatabasePersistentSingleton.cs) — extends `EditorPersistentSingleton` with threaded data gathering + periodic auto-refresh

## Key Design Patterns & Conventions

- **Singleton via `EditorPersistentSingleton<T>`**: singletons survive Unity assembly reloads as hidden `ScriptableObject` instances. Do NOT use standard C# static singletons for anything that needs to outlast recompilation.
- **Threaded database via `DatabasePersistentSingleton<T, D>`**: runs `GatherDataInThread()` in a background thread, processes results on the main thread in `WaitAndFinishDatabaseUpdate()`. Call `InvalidateDatabase()` to trigger refresh.
- **Async operations via `SVNAsyncOperation<T>`**: returns a promise-like object with `Completed` event dispatched on the main thread. Background threads are aborted on assembly reload.
- **`AssetModificationProcessor` (not `AssetPostprocessor`)**: WiseSVNIntegration intercepts moves/deletes *before* they happen via `OnWillCreateAsset`, `OnWillDeleteAsset`, `OnWillMoveAsset`. This is the core integration mechanism.
- **Platform abstraction**: `SVNContextMenusBase` subclass per platform. Selection determined by `Application.platform` at runtime.
- **Preferences personal vs project**: personal prefs (`UserSettings/WiseSVN.prefs`) for user-specific settings (enabled features, overlay settings, context menu client selection, trace logs); project prefs (`ProjectSettings/WiseSVN.prefs`) for team-shared settings (SVN CLI path, move behavior, lock prompt rules, branch scan parameters).
- **Conditional compilation**: `#if UNITY_2020_2_OR_NEWER` etc. for API compatibility across Unity versions.
- **SVN CLI conventions**: SVN commands use `--xml` output format which is parsed via `XmlDocument`/`XPath`. Error checking uses `CheckForSVNErrors()` which parses `svn: E...` error codes. Authentication errors are detected via `CheckForSVNAuthErrors()`.
- **Event-driven overlays**: overlay icons use `EditorApplication.projectWindowItemOnGUI`; SceneView overlay uses `SceneView.duringSceneGui`.
- **Localization** — [`LocalizationManager`](Assets/DevLocker/VersionControl/WiseSVN/Editor/Localization/LocalizationManager.cs): `key=value` text-file locale system. Uses `Tr()` shortcut via `using static LocalizationManager`. Language is a personal preference persisted in `UserSettings/WiseSVN.prefs`.
- **Ignore Manager** — [`SVNIgnoreManagerWindow`](Assets/DevLocker/VersionControl/WiseSVN/Editor/Ignore/SVNIgnoreManagerWindow.cs): `EditorWindow` for browsing/editing `svn:ignore` patterns with `Propget`/`Propset`/`Propdel`.

See [Docs/Developer_Guide.md](Docs/Developer_Guide.md) for the full architecture, public API reference, and how to add translations.
