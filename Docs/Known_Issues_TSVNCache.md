# Status Provider — Architecture & Known Issues

This document tracks the architecture of the overlay-icon status pipeline and the
known limits of TSVNCache, with the issues fixed and the issues still open.

---

## Architecture decision (2026-06-28): CLI is ground truth

After tracking down a series of "icon missing" / "wrong icon" reports, the rule
for the rendering pipeline is now:

> **CLI database (`SVNStatusesDatabase`) is the ground truth. TSVNCache is a
> low-latency front cache for the subset of fields it covers.**

Concretely [SVNOverlayIcons.ItemOnGUI](../Assets/DevLocker/VersionControl/WiseSVN/Editor/SVNOverlayIcons.cs):

1. Call `StatusProvider.GetStatus(path)` — fast path; TSVNCache when available
   on Windows, CLI elsewhere.
2. **Unconditionally** overlay `SVNStatusesDatabase.GetKnownStatusData(guid)` on
   top, field-by-field, with CLI winning whenever it has a non-default value.

The previous "merge only when Status==None or LockDetails empty" gate was
broken in two ways:
- `LockDetails.Equals(LockDetails.Empty)` was false for the CLI path (where
  LockDetails is `default(LockDetails)`, OperationResult=Unknown ≠ Success),
  so the gate accidentally worked only in TSVNCache mode and only because TSVN
  explicitly assigns `LockDetails.Empty`.
- It missed cases where TSVNCache returned a status (e.g. parent's rolled-up
  Normal for an unversioned file inside an unversioned folder) but the CLI had
  the correct one.

Cost of the unconditional merge: one dictionary lookup per ItemOnGUI call.

---

## TSVNCache 协议固有限制

TortoiseSVN's TSVNCache.exe exposes a **16-byte fixed response header** via the
`\\.\pipe\TSVNCache` named pipe (`TSVNCacheResponse`):

| 字段 | 类型 | 说明 |
|---|---|---|
| `m_kind` | INT8 | svn_node_kind (1=file, 2=dir) |
| `m_needsLock` | bool | 是否设置了 `svn:needs-lock` 属性 |
| `m_treeConflict` | bool | 是否处于树冲突 |
| `m_hasLockOwner` | bool | 工作副本中是否持有锁（**不区分自己/他人**） |
| `m_textStatus` | INT8 | svn_wc_status_kind 1..14 |
| `m_propStatus` | INT8 | svn_wc_status_kind 1..14 |
| `m_status` | INT8 | TortoiseSVN's recursive-rollup status (parent state bubbles down) |
| `m_cmtRev` | INT64 | 上次提交版本号 |

Source: [TortoiseSVN/src/TSVNCache/CacheInterface.h](https://sourceforge.net/p/tortoisesvn/code/HEAD/tree/trunk/src/TSVNCache/CacheInterface.h),
`CachedDirectory.cpp` — `svn_client_status6(..., check_out_of_date=FALSE, ...)`.

### TSVNCache 完全无法提供的字段（永远默认值）

- `RemoteStatus`（本地落后于服务器）— check_out_of_date=FALSE by design
- `LockDetails`（owner / date / message）
- LockedHere vs LockedOther vs BrokenLock vs LockedButStolen 区分
- `SwitchedExternalStatus`
- `MovedTo` / `MovedFrom`
- `svn:needs-lock` 文件的"需上锁"语义

All of these MUST come from the CLI database. This is not optional fallback —
it is the only source.

---

## 12 状态权威映射表（disk PNG → enum → producer）

WiseSVN 共 **12 张图**对应 12 类可见状态。VCFileStatus enum 有 14 个有效值
（+ Incomplete/Merged 两个 libsvn 过渡态），通过别名映射到这 12 张图。每行后面
"Producer" 列说明这一状态的真正生成路径——避免再出现"图标在但永远不会被产生"的死分支。

### File-status 层（9 张 PNG）

| Disk PNG | enum 主用 | enum 别名 | Tooltip | Producer |
|---|---|---|---|---|
| `SVNNormalIcon` | Normal | **External** | external 时 i18n tooltip 区分 | svn status / TSVN m_status=3, textStatus=3; External 来自 svn `X` 或 TSVN textStatus=13 |
| `SVNAddedIcon` | Added | — | — | svn status `A` / TSVN textStatus=4 |
| `SVNModifiedIcon` | Modified | Replaced, **Merged** | merged 时 i18n tooltip 区分 | svn status `M`/`R`, escalate from PropStatus=Modified, TSVN textStatus=7/8 |
| `SVNDeletedIcon` | Deleted | Missing | missing 时 i18n tooltip 区分 | svn status `D`/`!`, TSVN textStatus=5/6 |
| `SVNConflictIcon` | Conflicted | **Obstructed**, **Incomplete** | obstructed/incomplete 时 i18n tooltip 区分 | escalate from PropConflict / TreeConflict; svn `C`/`~`; libsvn 14 |
| `SVNIgnoredIcon` | Ignored | — | — | svn status `I`, svn:ignore / svn:global-ignores 合成, TSVN textStatus=11 |
| `SVNUnversionedIcon` | Unversioned | — | — | svn status `?`, m_UnversionedFolders 合成, TSVN textStatus=2 |
| `SVNReadOnlyIcon` | ReadOnly | Excluded | excluded 时 i18n tooltip 区分 | **NEW**: TSVN needsLock=true && !hasLockOwner; Excluded 来自 WiseSVN 偏好排除规则 |

### Lock 层（2 张 PNG）

| Disk PNG | enum 值 | Producer |
|---|---|---|
| `Locks/SVNLockedHereIcon` | LockedHere | CLI: `svn status --xml -u` 解析 `K` 列 + `<lock><owner>` 匹配自己 |
| `Locks/SVNLockedOtherIcon` | LockedOther, BrokenLock, LockedButStolen | CLI 解析 `O`/`B`/`T` 列；TSVN 不区分自己/他人，仅产 LockedOther |

注：根目录的 `SVNLockedIcon.png` **未被代码引用**，是历史遗留资产。建议保留（作为 lock 大图备用）或在下次清理时移除 meta。

### Remote 层（1 张 PNG）

| Disk PNG | enum 值 | Producer |
|---|---|---|
| `Others/SVNRemoteChangesIcon` | RemoteStatus.Modified | CLI: `svn status -u` 解析 `*` 列；TSVN 永远 None |

### Enum 槽位完整性

`FileStatusIcons` 数组按 `VCFileStatus.None`(15) 长度分配 16 槽位。**14 个有效 enum 全部赋值**：

```
0 Normal       → SVNNormalIcon
1 Added        → SVNAddedIcon
2 Conflicted   → SVNConflictIcon
3 Deleted      → SVNDeletedIcon
4 Ignored      → SVNIgnoredIcon
5 Modified     → SVNModifiedIcon
6 Replaced     → SVNModifiedIcon   (别名)
7 Unversioned  → SVNUnversionedIcon
8 Missing      → SVNDeletedIcon    (别名)
9 External     → SVNNormalIcon     (别名 — 之前糊弄写成 ReadOnly，已修)
10 Incomplete  → SVNConflictIcon   (别名 — 之前缺失，已补)
11 Merged      → SVNModifiedIcon   (别名 — 之前缺失，已补)
12 Obstructed  → SVNConflictIcon   (别名)
13 ReadOnly    → SVNReadOnlyIcon   (新加 producer：TSVN needsLock && !hasLockOwner)
14 Excluded    → SVNReadOnlyIcon   (别名)
15 None        → null              (intentional：no data, no icon)
```

---

## Rendering priority (overlay icons)

Each entry below describes a discrete signal layer drawn on top of the asset
icon. They sit in different sub-rects of the slot, so layers stack without
occluding each other. Priority refers to **business importance** (escalation
semantics inside the data, not Z-order):

| 优先级 | 信号 | 触发数据 | Rect |
|---|---|---|---|
| P0 Conflicted | Status / PropStatus / TreeConflict 任一冲突 | from CLI | 左下 file-status slot, escalates to Conflicted icon |
| P1 Lock      | LockStatus != NoLock | from CLI | 右上 #2 |
| P2 Remote out-of-date | RemoteStatus != None | from CLI | 右上 #1 |
| P3 File state | Status (Modified/Added/Deleted/Unversioned/Ignored/External/Obstructed/ReadOnly) | TSVNCache primary, CLI overlay | 左下 |

### Lock / Remote display rules

Both layers render **whenever the data is present**, regardless of the
`DownloadRepositoryChanges` / `EnableLockPrompt` preferences. Those toggles
control whether WiseSVN actively *queries* the server / prompts on edit;
they do not gate whether already-known state is displayed.

The previous gates (`if (downloadRepositoryChanges) ...` for remote, and
`if (downloadRepositoryChanges || lockPrompt) ...` for lock) silently hid lock
icons in projects that intentionally ran without remote checks — a critical
workflow bug for teams using locks but not auto-update.

---

## 已知缺陷与对策

### 1. 锁状态无法区分 LockedHere / LockedOther / Broken / Stolen
- **现象**：TSVNCache 仅返回 `hasLockOwner` bool；不暴露 owner / token / broken / stolen
- **现状**：CLI 覆盖层无条件提供这些字段（`svn status --xml -u`）
- **遗留**：如果用户禁用 CLI 数据库（PopulateStatusesDatabase=false），锁信息退化为单一 LockedOther

### 2. 远程过期 (`RemoteStatus`) 永远为 None
- TortoiseSVN 调用 `svn_client_status6(..., check_out_of_date=FALSE, ...)` —— 协议设计上不查服务器
- CLI 覆盖在 `DownloadRepositoryChanges=Enabled` 时跑 `svn status -u` 提供

### 3. svn:needs-lock 文件不显示"需上锁"图标
- **现象**：TSVNCache 返回 `needsLock=true && hasLockOwner=false` 表示该文件需要锁
- **现状**：完全忽略 `needsLock` 字段；CLI 也未暴露此语义到 VCFileStatus / VCLockStatus
- **建议**：在 VCLockStatus 增加 `NeedsLock` 槽位（与 ReadOnly file status 区分）

### 4. `cmtRev`（上次提交版本号）被丢弃
- **现状**：完全没读出来，分支选择器的 "上次提交版本号" 列仍走 CLI

### 5. `SwitchedExternalStatus`、`MovedTo`/`MovedFrom` 协议不支持
- 必须靠 CLI；目前 CLI 已通过 `--xml` 提供

### 6. `m_status` 在嵌套 unversioned 路径下被父状态污染
- **现象**：unversioned/Foo/bar.txt 的 `m_status` 可能等于父目录的递归综合状态（如 Normal）
- **修复 (2026-06-28)**：`ToSVNStatusData` 改为优先使用 `textStatus`（每文件字面状态），
  `m_status` 仅当 textStatus 是 0/None/Merged 时作 fallback

### 7. File status 槽位的图标映射不完整
- **历史**：`External / Obstructed / Incomplete / Merged / ReadOnly` 槽位未在 `LoadTextures` 中赋值，
  `GetFileStatusIconContent` 返回 `default(GUIContent)`，`.image == null` → 静默不画
- **修复 (2026-06-28)**：补齐 External / Obstructed / ReadOnly 三个槽位，复用 ReadOnly / Conflict art + 独立 tooltip

---

## 数据流架构（修复后）

```
┌─────────────────────────────────────────────────────────────┐
│                  SVNOverlayIcons.ItemOnGUI                  │
└──────────────────────────┬──────────────────────────────────┘
                           │
                           ▼
        ┌──────────────────────────────────────┐
        │  StatusProvider.GetStatus(path)      │  ← fast path
        │   (TSVNCache or CLI)                 │     (5s TTL cache)
        └──────────────────────────────────────┘
                           │
                           ▼
        ┌──────────────────────────────────────┐
        │  ALWAYS overlay CLI data per field   │  ← ground truth
        │   SVNStatusesDatabase.GetKnown…      │
        └──────────────────────────────────────┘
                           │
                           ▼
              [Field-by-field merge — CLI wins on non-default]
            • Status (CLI knows synthesized Unversioned/Ignored)
            • PropertiesStatus / TreeConflictStatus
            • LockStatus + LockDetails (full attribution)
            • RemoteStatus (out-of-date)
            • SwitchedExternalStatus
                           │
                           ▼
              [Conflict escalation P0]
              [Lock render P1] (no gate)
              [Remote render P2] (no gate)
              [File-status render P3]
```

---

## 待修复 / 待评估

- [ ] `svn:needs-lock` 覆盖图标（新 VCLockStatus.NeedsLock？设计 + 实现）
- [ ] 暴露 `cmtRev` 给上层 API（Branch Selector / Commit dialog）
- [ ] TSVNCache 异常恢复 — 如果 TSVNCache.exe 在运行期崩溃，应自动降级到 CLI
- [ ] 验证 Mac/Linux 平台 fallback 路径（CLI 总是主，但需回归）
- [ ] 文件夹（kind=dir）`m_status` 的语义：是否应走 textStatus-first 策略？目前一并改了，需观察

---

## 已修复的关键缺陷

| 日期 | 问题 | 修复 |
|---|---|---|
| 2026-06-28 | 80GB monorepo 规模：m_Data 线性扫描 + sanity limit 太低 | m_Data 加 guid→index Dictionary 加速器（O(N) → O(1)）；SanityStatusesLimit 600→4000、UnversionedFolders 250→2000、Ignores 250→2000 |
| 2026-06-28 | NTFS junction（mklink /J）下的文件 SVN 看不见 / 状态错乱 | 新增 `JunctionResolver`（GetFinalPathNameByHandle 解析、prefix-match 翻译、focus-debounce 重扫）；`SVNFormatPath` 统一拦截 link→real 翻译，所有 svn 命令自动走真实路径；`GatherJunctionStatusesInThread` 单独扫描每个 junction 并 ToAssetPath 反向翻译 |
| 2026-06-28 | Junction 文件夹用户分辨不出 | 新增 `ShowJunctionOverlayIcon` 偏好（默认 on），在 junction 根目录绘制 ⇗ 标识 + tooltip 说明 |
| 2026-06-28 | Unity 重启 / 编译后 Unversioned 图标不出现，必须切换任何偏好才回来（数据明明在数据库中） | 两段修复：① `SVNStatusesDatabase.m_UnversionedFolders / m_IgnoredEntries / m_GlobalIgnoredEntries / m_NestedRepositories / m_GlobalIgnoresCollected` 全部加 `[SerializeField]`，去掉 `volatile`（Unity 序列化忽略 volatile，volatile 反而吞掉序列化）。 ② `SVNOverlayIcons` 静态构造结束时 `EditorApplication.delayCall += EditorApplication.RepaintProjectWindow` 强制 reload 后首次 paint —— 之前数据序列化保留下来了但没有事件触发 Repaint，Unity 已用空的 overlay handler 完成首次 paint。 |
| 2026-06-28 | 远程过期的父文件夹错误地显示 Modified 图标，纯远程更新的文件夹不显示任何信号 | `AddModifiedFolders` 增加 remote-only 分支：当叶子是 Normal+RemoteStatus=Modified 时不再 early-return，向上传播 `Status=Normal, RemoteStatus=Modified` —— 父文件夹在 P2 层渲染绿色下箭头远程图标，不再误报本地修改。 |
| 2026-06-28 | Lock 图标在禁用 DownloadRepoChanges 且禁用 LockPrompt 时不显示 | 移除 lock 绘制的双门控；有数据即画 |
| 2026-06-28 | Remote out-of-date 图标在禁用 DownloadRepoChanges 时不显示（即使数据已有） | 移除 remote 绘制门控；有数据即画 |
| 2026-06-28 | Unversioned 文件位于 unversioned 父目录下时被 `m_status` 上卷为 Normal，图标错失 | TSVNCacheStatusProvider 改为 textStatus 优先，m_status fallback |
| 2026-06-28 | TreeConflict 未升级到 Conflicted 图标（仅 PropConflict 升级了） | ItemOnGUI 的 escalate 分支加入 TreeConflictStatus 判断 |
| 2026-06-28 | Fallback 合并条件 `LockDetails.Equals(LockDetails.Empty)` 在 CLI 主源时永真，合并语义脏 | 改为无条件 CLI 覆盖，CLI is ground truth |
| 2026-06-28 | External / Obstructed / ReadOnly 文件状态槽位无图标（默认 GUIContent，渲染静默跳过） | LoadTextures 补齐三槽位，复用 ReadOnly / Conflict art + 独立 tooltip + i18n |
| 2026-06-27 | Unversioned 图标默认不显示，必须切换 ShowNormal toggle 才出现 | SVNOverlayIcons 启动时显式订阅 SVNStatusesDatabase.DatabaseChanged |
| 2026-06-27 | 锁状态图标不更新（永远 LockedOther） | fallback 合并 CLI 的 LockStatus + LockDetails |
| 2026-06-27 | TSVNCache 缓存永不被填充 — GetStatus 总返回 None | RunCacheMissFill 后台真正调用 QueryPipe 并写入缓存 |
| 2026-06-27 | NamedPipeClientStream.ReadTimeout 抛 InvalidOperationException | 全部改用 BeginRead/EndRead + WaitOne(deadline) 模式 |
| 2026-06-27 | TSVNCacheResponseHeader 结构布局错误（32 字节 vs 实际 16 字节） | 重写 struct 严格按 CacheInterface.h 布局，加 Pack=8 |
