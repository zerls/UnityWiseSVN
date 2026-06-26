# WiseSVN for Unity — User Guide

> **This fork** enhances [NibbleByte/UnityWiseSVN](https://github.com/NibbleByte/UnityWiseSVN) with Chinese/English i18n, SVN Ignore Manager, and automated packaging.

---

## 1. Overview

WiseSVN is a Subversion (SVN) version control integration plugin for the Unity Editor. It hooks into Unity's asset pipeline to keep SVN operations in sync with file operations, provides overlay icons in the Project window, context menus for SVN commands, lock-prompting workflows, and a branch selector.

**Platforms:** Windows (TortoiseSVN), macOS (SnailSVN), Linux (RabbitVCS or CLI)

---

## 2. Installation

### Option A: Unity Asset Store
Search "Wise SVN" or visit [Asset Store](https://assetstore.unity.com/packages/tools/version-control/wise-svn-162636).

### Option B: OpenUPM
```bash
npm install -g openupm-cli
openupm add devlocker.versioncontrol.wisesvn
```

### Option C: GitHub UPM (not recommended)
Add to `Packages/manifest.json`:
```json
{
  "dependencies": {
    "devlocker.versioncontrol.wisesvn": "https://github.com/NibbleByte/UnityWiseSVN.git#upm"
  }
}
```

---

## 3. Prerequisites

- **SVN CLI (Command Line Interface)** must be installed:
  - Windows: Select **"command line client tools"** when installing TortoiseSVN
  - macOS/Linux: See [Subversion packages](https://subversion.apache.org/packages.html)
- **GUI Client:** TortoiseSVN (Windows), SnailSVN (macOS), or RabbitVCS (Linux)

---

## 4. Basic Usage

### Menus
All commands are available via `Assets/SVN/` or right-click on an asset → `SVN/`:
- **Diff / Resolve**
- **Update All**
- **Commit All / Commit**
- **Add**
- **Revert All / Revert**
- **Get Locks / Release Locks**
- **Show Log All / Show Log**
- **Repo Browser**
- **Switch Branch**
- **Blame**
- **Cleanup**
- **Ignore Toggle** (new in 1.6.0)
- **Ignore Manager** (new in 1.6.0)

### Overlay Icons
Each asset in the Project window displays an SVN status icon:
| Icon | Meaning |
|------|---------|
| Green ✓ | Normal (no changes) |
| Red ✏ | Modified |
| Blue + | Added |
| Red ✕ | Deleted |
| Yellow ⚠ | Conflicted |
| Gray ○ | Ignored / Excluded |
| Blue ? | Unversioned |
| Lock icons | Locked (green=by you, red=by others) |
| Cloud icon | Server has updates |

### Automatic File Operation Sync
When you move/delete/create assets in Unity, WiseSVN automatically runs the corresponding `svn move`/`svn delete`/`svn add`.

---

## 5. Preferences

Open via `Assets/SVN/SVN Preferences`.

### Personal Tab (UserSettings/WiseSVN.prefs)
| Setting | Description |
|---------|-------------|
| Language | Auto / English / 简体中文. Auto follows system language |
| Enable SVN integration | Master toggle |
| Enable overlay icons | Show SVN status in Project window |
| Scan for svn-ignores | Detect ignored files |
| Refresh interval | Seconds between icon refreshes; -1 = on file change only |
| Check for repository changes | Query server for updates |
| Auto lock when modified | Auto-lock assets on modification |
| SceneView overlay for conflicts | Warn when scene/prefab is stale or locked |
| SVN CLI Path | Custom path to svn binary |
| Context menus client | TortoiseSVN / SnailSVN / RabbitVCS / CLI |

### Project Tab (ProjectSettings/WiseSVN.prefs — shareable via VCS)
| Setting | Description |
|---------|-------------|
| Check for repository changes | Team-level download check |
| SVN CLI Path | Platform-specific (Windows / macOS) |
| Assets move behaviour | Normal SVN move vs. Add+Delete |
| Enable Lock Prompts | Perforce-style lock-on-modify prompts |
| Lock Prompt Parameters | Target folders, asset types, exclusions |
| Enable Branches Database | Scan SVN repo for Unity project branches |
| Branches Scan Parameters | Entry point URLs, signature entries |

---

## 6. Lock Prompt

When a monitored asset or its `.meta` is modified, a popup prompts you to lock it:
- Shows if assets are locked by others or out-of-date
- Supports force-stealing locks
- Supports auto-lock on modify
- Skipped assets won't prompt again until Unity restarts

---

## 7. Branch Selector

`Assets/SVN/Branch Selector`:
- Scans all branches in the SVN repository for Unity projects
- Per-asset conflict scanning across branches
- Supports branch switching, Repo Browser, and Show Log per branch

---

## 8. SVN Ignore Management (new in 1.6.0)

### Ignore Toggle
Select an unversioned asset → `Assets/SVN/Ignore Toggle`. The asset and its `.meta` are added to the parent directory's `svn:ignore`.

For versioned assets, a dialog offers:
1. Add to `ignore-on-commit` changelist (TortoiseSVN convention)
2. Cancel
3. Mark for deletion + ignore

### Ignore Manager
`Assets/SVN/Ignore Manager` opens the management window:
- Left panel lists all directories with `svn:ignore` properties
- Right panel edits patterns for the selected directory
- `+` button adds the Project window's selected folder for management
- `-` button removes patterns
- **Apply Changes** writes all pending changes to SVN
- `svn:global-ignores` are displayed read-only

---

## 9. Language Switching (new in 1.6.0)

Preferences → Personal → Language dropdown:
- **Auto**: Follows Unity's system language
- **English**: Force English
- **简体中文**: Force Simplified Chinese

All WiseSVN UI updates immediately on switch.

---

## 10. FAQ

**Q: Overlay icons not showing?**
- Verify "Enable SVN integration" and "Enable overlay icons" in Personal preferences
- Verify the project is under SVN version control
- Try `Assets/SVN/Refresh Icons & Locks`

**Q: "SVN CLI missing" error?**
- Windows: Reinstall TortoiseSVN with command line tools
- macOS: `brew install svn`
- Or set the svn path manually in preferences

**Q: File move fails?**
- Check if the target directory is under version control
- Check for file conflicts

---

## 11. Developer API

WiseSVN exposes a public API for tool integration.

### Silence Mode
```csharp
WiseSVNIntegration.RequestSilence();
// ... do work ...
WiseSVNIntegration.ClearSilence();
```

### Temporary Disable
```csharp
WiseSVNIntegration.RequestTemporaryDisable();
// ... do work ...
WiseSVNIntegration.ClearTemporaryDisable();
```

### Context Menu Commands
```csharp
SVNContextMenusManager.Commit();
SVNContextMenusManager.Update();
SVNContextMenusManager.Revert();
SVNContextMenusManager.ShowLog(assetPath);
```

### Direct SVN Commands
```csharp
var statuses = new List<SVNStatusData>();
WiseSVNIntegration.GetStatuses(statuses, timeout: 30000);

// Async
var op = WiseSVNIntegration.UpdateAsync(timeout: 60000);
op.Completed += (o) => Debug.Log($"Update result: {o.Result}");
```

See `Developer_Guide.md` for more details.
