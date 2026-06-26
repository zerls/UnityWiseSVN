# WiseSVN — Developer Guide

## Architecture

All source code lives under `Assets/DevLocker/VersionControl/WiseSVN/Editor/`. The plugin is purely a Unity Editor extension — no runtime (play-mode) code.

### Layers (bottom to top)

| Layer | File(s) | Purpose |
|-------|---------|---------|
| Shell / Process | `ShellUtils.cs` | Cross-platform CLI execution via `System.Diagnostics.Process` |
| Core Integration | `WiseSVNIntegration.cs` (2428 lines) | Central class: `[InitializeOnLoad]`, extends `AssetModificationProcessor`, wraps all SVN CLI commands |
| Async Operations | `SVNAsyncOperation.cs` | Promise-like class spawning background threads, dispatching `Completed` on Unity main thread |
| Data Types | `SVNDataTypes.cs` | All enums (`VCFileStatus`, `VCLockStatus`, etc.) and structs (`SVNStatusData`, `LockDetails`, etc.) |
| Status Database | `SVNStatusesDatabase.cs` | Worker-thread cache of `svn status` results, keyed by Unity GUID |
| Overlay Icons | `SVNOverlayIcons.cs` | Renders SVN status icons on Project window items |
| SceneView Overlay | `SVNLockedOverlay.cs` | Red warning banner when scene/prefab is outdated or locked |
| Context Menus | `ContextMenus/*.cs` | Platform-abstracted SVN menu commands (TortoiseSVN/SnailSVN/RabbitVCS/CLI) |
| Lock Prompting | `LockPrompting/*.cs` | Perforce-like lock-on-modify workflow |
| Branch Selector | `Branches/*.cs` | SVN repository branch scanner + selection UI |
| Preferences | `Preferences/*.cs` | Personal + project settings, JSON persistence |
| Ignore Manager | `Ignore/SVNIgnoreManagerWindow.cs` | EditorWindow for managing `svn:ignore` patterns |
| Localization | `Localization/LocalizationManager.cs` | Key=value text-based i18n system |

### Key Design Patterns

**1. EditorPersistentSingleton\<T\>**
Singletons survive Unity assembly reloads as hidden `ScriptableObject` instances (`HideFlags.HideAndDontSave`). Do NOT use standard C# static singletons for state that must outlast recompilation.

**2. DatabasePersistentSingleton\<T, D\>**
Extends the above with background-thread data gathering. Override `GatherDataInThread()` (runs on worker), process results in `WaitAndFinishDatabaseUpdate()` (runs on main thread). Call `InvalidateDatabase()` to trigger refresh.

**3. SVNAsyncOperation\<T\>**
Promise/future pattern. `SVNAsyncOperation<T>.Start(func)` spawns a thread, fires `Completed` event on the main thread. Background threads are aborted on assembly reload.

**4. AssetModificationProcessor (not AssetPostprocessor)**
`WiseSVNIntegration` intercepts Unity asset operations **before** they happen via `OnWillCreateAsset`, `OnWillDeleteAsset`, `OnWillMoveAsset`.

**5. Conditional Compilation**
`#if UNITY_2020_2_OR_NEWER` etc. for API compatibility across Unity versions (back to 2018.4).

**6. SVN CLI Convention**
SVN commands use `--xml` output, parsed via `XmlDocument`/`XPath`. Error checking uses `CheckForSVNErrors()` which parses `svn: E...` error codes.

---

## Public API Reference

### WiseSVNIntegration (static methods)

```csharp
// Status
GetStatuses(List<SVNStatusData>, ...) -> StatusOperationResult
GetStatusesAsync(...) -> SVNAsyncOperation<StatusOperationResult>

// Lock / Unlock
LockFile(string assetPath, ...) -> LockOperationResult
UnlockFile(string assetPath, ...) -> LockOperationResult
LockFilesAsync(...) / UnlockFilesAsync(...) -> SVNAsyncOperation<LockOperationResult>

// Core operations
Update(...) -> UpdateOperationResult
Commit(...) -> CommitOperationResult
Revert(...) -> RevertOperationResult
Add(string assetPath) -> bool
Delete(string assetPath, bool keepLocal, ...) -> bool

// Properties
Propget(string assetPath, string property, bool recursive, List<PropgetEntry>, ...) -> PropOperationResult
Propset(string assetPath, string property, string valueOverride, ...) -> PropOperationResult
Propdel(string assetPath, string property, ...) -> PropOperationResult  // new in 1.6.0

// Info
Log(...) -> LogOperationResult
ListURL(...) -> ListOperationResult

// Utilities
GetStatus(assetPath) -> SVNStatusData
AssetPathToURL(assetPath) -> string
GetWorkingCopyRootPath() -> string
CheckForSVNErrors() -> string
CheckForSVNAuthErrors() -> SVNAsyncOperation<StatusOperationResult>

// Control
RequestSilence() / ClearSilence()
RequestTemporaryDisable() / ClearTemporaryDisable()
CreateReporter() -> IShellMonitor
```

### SVNContextMenusManager (static methods)

```csharp
CheckChanges()
Update()
Commit()
Add()
Revert()
GetLocks()
ReleaseLocks()
ShowLog(string assetPath)
RepoBrowser(string assetPath)
Switch(string assetPath)
Blame(string assetPath)
Cleanup()
IgnoreToggle(string assetPath)      // new in 1.6.0
ShowIgnoreManager()                  // new in 1.6.0
```

---

## Adding a New Language

1. Create `Editor/Localization/locale_XX.txt` with `key=value` lines
2. Add the language to `WiseSVNLanguage` enum in `LocalizationManager.cs`
3. Add the locale file name mapping in `LocaleFileName()`
4. Add translations for the language selector labels in both locales

**Locale file format:**
```
# Comment
key.name=Value text with \n for line breaks
```

**Key convention:** `category.specific_name`

**Testing:** Set `Language = WiseSVNLanguage.YourLang` in preferences, then open any WiseSVN window. Missing keys will show as `key.name` literally — easy to spot and fix.

---

## Release Process

Run the packaging script:
```bash
./Scripts/pack.sh <version> [--dry-run] [--no-push]
```

Example:
```bash
./Scripts/pack.sh 1.6.0 --dry-run   # Preview
./Scripts/pack.sh 1.6.0              # Execute
```

The script:
1. Validates version format (semver)
2. Updates `package.json` version
3. Prepares CHANGELOG entry
4. Commits and tags
5. Pushes subtree to `upm` branch
6. Pushes tag to origin

---

## Project Conventions

- **Namespace:** `DevLocker.VersionControl.WiseSVN`
- **No external dependencies:** `package.json` keeps `dependencies: {}`
- **Unity 2018.4+ compatibility:** Use `#if UNITY_XXXX_X_OR_NEWER` for newer APIs
- **Personal vs Project prefs:** `UserSettings/WiseSVN.prefs` vs `ProjectSettings/WiseSVN.prefs`
- **GUID-based asset lookup:** Status database keys by Unity GUID, not path
