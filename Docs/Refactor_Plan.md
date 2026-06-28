# WiseSVN 重构计划

> 状态：已全部执行完成（2026-06-28）。
> 参照基准：`SVNOverlayIcons.cs`（emoji 渲染、BuildIconRect 统一布局、SVNStatusResolver 三层分离、WiseSVNGUIUtils 通用辅助）。

---

## 重构原则

SVNOverlayIcons 重构的核心做法：

1. **单一关注点拆分**：每个 private 方法只做一件事（数据合并 / 单层绘制 / 矩形计算）
2. **消除魔法数字**：用枚举和 const 替代内联数值
3. **Emoji 替代贴图**：远端状态已改 emoji；可逐步把其他"只需要一个视觉标记"的图标替换，减少贴图加载依赖
4. **提取通用辅助**：DrawEmoji / BuildIconRect 已做成模式，其他文件可以引用
5. **潜在 null-ref 一律收口**：string 操作前检查，LINQ FirstOrDefault 结果判空

---

## 我，顶级框架师的补充：

- 文件状态图标方案调整为 内置/Tortoise/Emoji 3大类
- WiseSVNIntegration.cs 评估下是否需要拆分为 多个文件部分类组成的完整类，现在文件太大，超级类了。
  - 其他超过1000行的实现，也可以评估下，评估完成后告诉我，不要自己急于调整。

---

## Phase 0 — 文件/文件夹状态图标显示架构重构（优先级最高）

> 此 Phase 是所有 GUI Phase 的前提，应在 Phase 1 之前完成。

### 问题描述

当前 `SVNOverlayIcons.ItemOnGUI` 在 **每帧每个可见资源** 上同时做三件事：

1. **数据合并**：调用 `MergeCliStatus` → 每帧都跨两个 Provider 做 merge 运算
2. **状态解析**：`ResolveFileStatus` 含冲突升级、属性修改等逻辑
3. **渲染**：`DrawFileStatusIcon` / `DrawLockStatusIcon` / `DrawRemoteStatusIcon`

混用的后果：

| 问题 | 具体表现 |
|---|---|
| **闪烁** | `TSVNCacheStatusProvider.TickStatusesChanged` 5s 无条件触发 repaint；恰好命中 CLI DB 的 `m_Data.Clear()` → `AddModifiedFolders` refill 之间的空窗期时，项目窗口看到"空"的 m_Data，文件夹瞬间显示 Normal 然后回到 Modified |
| **性能** | MergeCliStatus 在每次 paint 重复计算；两次 Dictionary 查找（TSVNCache cache + m_Data guid index）在 50 个可见资源 × 60fps 时放大 |
| **状态不一致** | TSVNCache（5s TTL, per-request）和 CLI DB（60s scan, batch）刷新时机不同；两者在中间状态时合并出错误的最终值 |
| **难维护** | 新增状态源（如 future 原生 SVN API）需要修改 ItemOnGUI；"数据从哪来"和"数据如何画"耦合在同一个函数里 |

### 目标架构：状态存储 / 状态解析 / 状态显示 三层分离

```
┌────────────────────────────────────────────────────────┐
│  Layer 1 — 状态存储（各自独立，事件驱动）               │
│  TSVNCacheStatusProvider  ←→  SVNStatusesDatabase       │
│  各自只维护自己的数据，不知道对方存在                   │
└────────────────────────┬───────────────────────────────┘
                         │  StatusesChanged / DatabaseChanged
                         ▼
┌────────────────────────────────────────────────────────┐
│  Layer 2 — SVNStatusResolver（新增，单例，事件驱动）    │
│  • 订阅两个数据源的 Changed 事件                        │
│  • 内部维护 guid → ResolvedStatusData 字典（合并结果）  │
│  • 合并规则集中在此处，不在 ItemOnGUI                   │
│  • 数据源变化时增量更新受影响的 guid（不是全量重算）    │
│  • 合并完成后 fire ResolvedChanged → RepaintProjectWindow│
└────────────────────────┬───────────────────────────────┘
                         │  ResolvedChanged
                         ▼
┌────────────────────────────────────────────────────────┐
│  Layer 3 — SVNOverlayIcons.ItemOnGUI（纯渲染）          │
│  • 只调用 SVNStatusResolver.GetResolved(guid) — O(1)   │
│  • 拿到已合并好的 ResolvedStatusData                    │
│  • DrawRemoteIcon / DrawLockIcon / DrawFileStatusIcon   │
│    全部是纯绘制，无任何数据逻辑                         │
└────────────────────────────────────────────────────────┘
```

### ResolvedStatusData 结构

```csharp
public readonly struct ResolvedStatusData
{
    // 文件状态（含冲突升级）
    public readonly VCFileStatus   FileStatus;
    // 锁（已合并 LockDetails）
    public readonly VCLockStatus   LockStatus;
    public readonly LockDetails    LockDetails;
    // 远端（仅 CLI 提供）
    public readonly VCRemoteFileStatus RemoteStatus;
    // 是否为 Junction 根（JunctionResolver 提供）
    public readonly bool IsJunctionRoot;
    // 来源路径（debug 用）
    public readonly string Path;
}
```

### SVNStatusResolver 职责

```
SVNStatusResolver : EditorPersistentSingleton<SVNStatusResolver>
  ├─ [InitializeOnLoad]
  │   ├─ Subscribe: TSVNCache.StatusesChanged → OnSourceChanged
  │   └─ Subscribe: SVNStatusesDatabase.DatabaseChanged → OnSourceChanged
  │
  ├─ OnSourceChanged()
  │   ├─ 只在非 DB-rebuilding 状态下触发（DatabaseChangeStarting 时 suppress）
  │   ├─ 增量合并：只重算在本次 Changed 中新增/更新/删除的 guid
  │   └─ 合并完成后 → ResolvedChanged?.Invoke()
  │
  ├─ Merge(guid) → ResolvedStatusData
  │   ├─ a = TSVNCache.GetStatus(assetPath)  // fast, may be stale
  │   ├─ b = SVNStatusesDatabase.GetKnownStatusData(guid)  // batch
  │   ├─ 合并规则：
  │   │   Status:       b.Status != None → use b; else use a  (CLI is ground truth)
  │   │   LockStatus:   b.LockStatus != NoLock → use b (CLI has full LockDetails)
  │   │   RemoteStatus: b.RemoteStatus != None → use b (TSVNCache never has this)
  │   │   Conflict:     PropConflict || TreeConflict → escalate to Conflicted
  │   │   NeedsLock:    TSVNCache.needsLock && !hasLock && Status==Normal → ReadOnly
  │   └─ IsJunctionRoot: JunctionResolver.IsJunctionRoot(assetPath)
  │
  └─ GetResolved(guid) → ResolvedStatusData   // O(1), pure cache read, called from ItemOnGUI
```

### DB 重建期间的 suppress 处理

```csharp
// SVNStatusResolver：
void OnDatabaseChangeStarting()  => m_SuppressFromDB = true;
void OnDatabaseChanged()         { m_SuppressFromDB = false; OnSourceChanged(); }

// 由此保证 m_Data.Clear() → AddModifiedFolders refill 之间的空窗期内，
// Resolver 持续保留上一次的合并结果 → 图标不闪烁。
```

### ItemOnGUI 简化后的形态

```csharp
private static void ItemOnGUI(string guid, Rect sel)
{
    if (IsSpecialGUID(guid)) { DrawIncompleteWarning(guid, sel); return; }

    var r = SVNStatusResolver.Instance.GetResolved(guid);

    DrawRemoteIcon   (sel, r);  // P2：远端状态
    DrawLockIcon     (sel, r);  // P1：锁状态
    DrawFileIcon     (sel, r);  // P3：文件状态
    DrawJunctionBadge(sel, r);  // P4：软连接标识
}
```

ItemOnGUI 内不含任何数据逻辑，纯渲染，可独立测试和替换。

### 文件变更清单

| 文件 | 变更内容 |
|---|---|
| 新建 `Editor/Providers/SVNStatusResolver.cs` | Layer 2 完整实现 |
| `SVNOverlayIcons.cs` | 删除 MergeCliStatus；将 ResolveFileStatus 迁移至 Resolver；ItemOnGUI 简化为纯渲染 |
| `SVNStatusBarOverlay.cs` | `CountStatuses()` 改用 `SVNStatusResolver.EnumerateResolved()` |
| `TSVNCacheStatusProvider.cs` | TickStatusesChanged 条件触发（已完成）；移除直接合并职责 |
| `ISVNStatusProvider.cs` | 保留 GetStatus；删除与合并相关的方法 |

**预计工作量**：2~3 天
**风险**：高（涉及核心渲染路径变更）—— 需按功能分步发布

---

### 即时修复（Phase 0 前的临时补丁，已应用）

| 修复内容 | 文件 |
|---|---|
| `TickStatusesChanged`：通过 `m_CacheHasNewData` 标志实现条件触发，避免无数据变化时的无效 repaint | TSVNCacheStatusProvider.cs |
| `RunCacheMissFill`：仅在数据实际变更时将 `m_CacheHasNewData` 置为 true | TSVNCacheStatusProvider.cs |
| 启动重放次数从 5 次缩减为 2 次 | SVNOverlayIcons.cs |
| 主扫描结果中移除软连接根目录条目，防止错误的 Unversioned 状态污染 | SVNStatusesDatabase.cs |

---

## 分阶段计划

---

### Phase 1 — 公共基础设施（其他 Phase 的前提）

**目标**：把 SVNOverlayIcons 中已有的 `DrawEmoji` / `BuildIconRect` / `FormatLockDate` 提取为可公用的 static 辅助类，让其他窗口也能调用，不重复实现。

**改动文件**：新建 `Editor/Utils/WiseSVNGUIUtils.cs`

**具体内容**：
```
WiseSVNGUIUtils (static class)
  ├── DrawEmoji(Rect, GUIContent, float scaleFactor=0.82f)    ← 从 SVNOverlayIcons 搬过来
  ├── FormatDate(string svnDate) : string                    ← 从 SVNOverlayIcons.FormatLockDate 泛化
  ├── StripMetaSuffix(string path) : string                  ← 供 SVNLockPromptDatabase / Window 共用
  ├── CreateIconWithTextFallback(string iconName, string fallback) : GUIContent
  └── MakeMiniButtonlessStyle() : GUIStyle                   ← 目前 SVNBranchSelectorWindow / SVNLockPromptWindow 各自重写
```

**预计工作量**：半天  
**风险**：低（纯抽取，无逻辑改动）



---

### Phase 2 — SVNStatusBarOverlay.cs

**问题**（探针结果 §7）：

| 问题 | 影响 |
|---|---|
| `MakeSceneViewBadgeStyle()` 每次 OnSceneGUI 都 `new GUIStyle(...)` | 无用 GC 压力 |
| `ParseBranchFromURL()` 两处 `IndexOf`返回 -1 时 Substring 崩溃潜在风险 | 运行时异常 |
| `GetBadgeColor()` 混合安全覆盖（offline/conflict）与 branch-pattern 颜色解析 | 逻辑耦合 |
| `BadgeStyle` get 每次 null-check 后写入临时变量，冗余结构 | 可读性 |

**改动**：

1. **缓存 SceneView badge style**：`s_SceneViewBadgeStyle` static 缓存，`PreferencesChanged` 时清除（字号变化才需重建）
2. **修 `ParseBranchFromURL` Substring 越界**：两处 `IndexOf('/')` 返回值要 `>= 0` 才 Substring
3. **拆 `GetBadgeColor`**：主颜色留给 `ResolveBranchColor`，safety override 单独一步（`if offline ... if conflict ...`，逻辑更清晰）
4. **`BadgeStyle` 改 static field + lazy init**，去掉 get 里的多余临时 style 写法

**预计工作量**：半天  
**风险**：低

---

### Phase 3 — SVNLockedOverlay.cs（SceneView 遮罩）

**问题**（探针结果 §1）：

| 问题 | 影响 |
|---|---|
| `SceneViewOnGUI()` 72 行混合状态查询 + 消息刷新 + GUI 绘制三种关注点 | 难以维护 |
| `messageRect / closeRect / iconRect` Rect 运算散布在 OnGUI 中 | 修布局要找多处 |
| `bool hasMessage` 运算符优先级不明确（`&&` / `\|\|` 未加括号） | 潜在逻辑 Bug |
| `const` 魔法数字 `closeSize=18f / closeOffset=6f / -4f` | 可读性 |

**改动**：

1. **拆 `SceneViewOnGUI`** 为：
   - `GetOverlayMessage() : (string message, bool isLocked, bool isOutdated)` —— 数据查询层
   - `DrawOverlayPanel(Rect, message, ...)` —— 纯渲染层，Rect 全部在此计算
2. **提取 `BuildOverlayRects(Rect panelRect)` 返回匿名结构**，集中 messageRect/closeRect/iconRect 计算
3. **修 `hasMessage` 歧义布尔**：加括号 `(... && ...) || ...`
4. **具名常量**：`const float CloseButtonSize = 18f; const float CloseButtonPadding = 6f;`

**预计工作量**：1 天  
**风险**：中（涉及 SceneView GUI，需要在 Unity 里验证渲染）

---

### Phase 4 — SVNLockPromptWindow.cs

**问题**（探针结果 §3）：

| 问题 | 影响 |
|---|---|
| `MiniIconButtonlessStyle.contentOffset` 在绘制前后手动 reset | 状态泄露风险 |
| `.meta` 后缀剥离逻辑在同文件出现 3 次 | 维护重复 |
| 嵌套 `BeginDisabledGroup` 共 6 处，难以追踪 enable 状态 | 可读性 |
| `const float RevertSize = 20f / 18f` 等宽高常量计算方式奇特 | 语义不清 |

**改动**：

1. **`contentOffset` 改用局部 style copy**：
   ```csharp
   // 替换 MiniIconButtonlessStyle.contentOffset = ... 的前后 reset 模式
   using var tempStyle = new GUIStyle(MiniIconButtonlessStyle);
   tempStyle.contentOffset = new Vector2(0f, -2f);
   GUILayout.Button(content, tempStyle, ...);
   ```
2. **`.meta` 剥离**：用 `WiseSVNGUIUtils.StripMetaSuffix()`（Phase 1 产出）
3. **`BeginDisabledGroup` 整理**：用局部布尔变量 `bool isEditable = ...` 替代多层嵌套
4. **常量语义命名**：`const float LockIconButtonSize = 20f;`（不再用除法表达）

**预计工作量**：半天  
**风险**：低

---

### Phase 5 — SVNLockPromptDatabase.cs

**问题**（探针结果 §2）：

| 问题 | 影响 |
|---|---|
| `OnStatusDatabaseChanged()` 145 行混合过滤 / 分类 / async 操作 / 通知 | 难以测试和维护 |
| `.meta` 后缀剥离重复两次 | 同 Phase 4 |
| `lockPromptParam` 来自 `FirstOrDefault` 但未 null 检查 | 潜在 NRE |

**改动**：

1. **拆 `OnStatusDatabaseChanged`**：
   - `FilterStatusesForLockPrompt(statuses)` → `IEnumerable<SVNStatusData>`
   - `ClassifyLockCandidates(filteredStatuses)` → `(toAsk, autoLock, autoUnlock)` 元组
   - `EnqueueLockOperations(...)` → 触发异步操作
2. **`.meta` 剥离**：用 `WiseSVNGUIUtils.StripMetaSuffix()`
3. **`FirstOrDefault` null 检查**：加 `if (lockPromptParam == null) continue;`

**预计工作量**：1 天  
**风险**：中（锁操作是核心流程，改动需在有锁文件场景下完整测试）

---

### Phase 6 — SVNBranchSelectorWindow.cs

**问题**（探针结果 §4）：

| 问题 | 影响 |
|---|---|
| `MiniIconButtonlessStyle` 初始化 8 行逐字段 null 赋值，与 SVNLockPromptWindow 几乎重复 | DRY 违反 |
| GUIContent icon/text fallback 重复 6 次（`if (content.image == null) content.text = "X"`） | DRY 违反 |
| `GatherConflicts` 115 行，日志抓取 + 路径解析 + 冲突检测混合 | 测试困难 |
| `DateTime.Now.AddDays/Months` 在 3 处重复拼 SVN 日期范围字符串 | DRY 违反 |

**改动**：

1. **`MiniIconButtonlessStyle` 工厂**：用 `WiseSVNGUIUtils.MakeMiniButtonlessStyle()` 替代两个文件里的重复手写
2. **icon fallback**：用 `WiseSVNGUIUtils.CreateIconWithTextFallback(iconName, fallback)` 替代 6 处 if 语句
3. **日期范围构建**：提取 `FormatSVNDateRange(DateLimitType type, int count) : (string start, string end)` 
4. **`GatherConflicts` 拆分**：`FetchBranchLog()` / `DetectPathConflicts()` / `BuildConflictReport()`

**预计工作量**：1 天  
**风险**：中（Branch 冲突扫描是 UI 关键路径，需要有远端 SVN 的场景验证）

---

### Phase 7 — 可选 / 低优先级

| 文件 | 改动 | 优先级 |
|---|---|---|
| `SVNPreferencesWindow.cs` | 缓存 `urlStyle`；`DrawProjectPreferences` 按 section 拆分；贴图生成方法独立到 Utils | P3 |
| `SVNContextMenusBase.cs` | `GetWorkingPath()` 中 LINQ 链可读性；无 emoji / Rect 问题 | P3 |
| `SVNStatusesDatabase.cs` | `GatherDataInThread` 内的 timings StringBuilder 可改为条件编译（DoTraceLogs 时才拼） | P3 |

---

## 各 Phase 依赖关系

```
Phase 1 (Utils)
    ├── Phase 2 (StatusBarOverlay)   — 独立，不依赖 Phase 1
    ├── Phase 3 (LockedOverlay)      — 独立，不依赖 Phase 1
    ├── Phase 4 (LockPromptWindow)   — 依赖 Phase 1 的 StripMetaSuffix
    ├── Phase 5 (LockPromptDatabase) — 依赖 Phase 1 的 StripMetaSuffix
    └── Phase 6 (BranchSelector)     — 依赖 Phase 1 的 MakeMiniButtonlessStyle / CreateIconWithTextFallback
```

Phase 2/3 可与 Phase 1 并行；Phase 4/5/6 建议 Phase 1 完成后进行。

---

## 不在计划内的事项

- `WiseSVNIntegration.cs`（2400 行核心）：属于 SVN 命令层，不是 GUI 问题，不在本轮重构范围
- `SVNStatusesDatabase.cs`：已完成性能优化（guid index），GUI 无关部分不动
- 新增功能（junction 以外）：不在重构范围内
