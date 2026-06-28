# Changelog

All notable changes to WiseSVN for Unity will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [1.8.0] - 2026-06-28

## [1.7.0] - 2026-06-27

### Added

- **图标风格偏好（Classic / Modern）**：个人偏好新增 `Icon Style` 下拉框
  - `Classic`：保留原 TortoiseSVN 风格 PNG 图标
  - `Modern`：使用 Unity 内置编辑器图标，自动适应深色/浅色主题
- **SVN 状态栏 Overlay**（需 Unity 2021.2+）：`SVNStatusBarOverlay` — 在 SceneView 工具栏显示当前分支名、本地改动数（M）和远端改动数（R）；点击「…」菜单可快捷触发 Update All / Commit All / Refresh Icons
- **键盘快捷键**：
  - `Ctrl+Alt+U` — Update All
  - `Ctrl+Alt+S` — Commit All
  - `Ctrl+Alt+R` — Refresh Icons & Locks

### Fixed

- `LocalizationManager`：为所有静态字典读写加锁（`lock(s_LockObj)`），修复后台线程（`SVNStatusesDatabase` 工作线程）并发调用 `Tr()` 时的潜在竞态条件
- `SVNIgnoreManagerWindow`：`RefreshDirectoryList` 和 `AddDirectoryToManage` 改为异步执行（`SVNAsyncOperation<T>`），大型仓库下不再阻塞 UI；刷新/添加按钮在操作进行中自动禁用
- `SVNIgnoreManagerWindow`：模式去重改用 `OrdinalIgnoreCase` 比较，匹配 Windows SVN ignore 大小写不敏感的实际行为
- `SVNIgnoreManagerWindow`：`ApplyAllChanges` 失败时在状态栏显示具体失败目录名，而非统一提示
- `SVNIgnoreManagerWindow`：硬编码字符串 `"(empty)"` 和 `"Refresh failed:"` 纳入 i18n

## [1.6.1] - 2026-06-27

### Fixed

- `LocalizationManager`：英文模式下 `s_Current = new Dictionary<string, string>(s_Fallback)` 改为值复制，避免引用共享导致 fallback 字典被污染
- `SVNPreferencesManager`：`OnLanguageChanged` 订阅改为先 `-=` 再 `+=`，防止 assembly reload 导致事件处理器累积
- `Scripts/pack.sh`：CHANGELOG 插入改用 `awk`，修复 macOS BSD sed 不兼容 GNU `a\` 语法的问题；新增 `rollback_tag()` 函数，subtree push 失败时自动回滚本地 tag
- `makeupm.bat`：自动探测 `%ProgramFiles%\Git\bin\bash.exe` 等标准路径，修复 Windows 环境下 bash 不在 PATH 时无法运行的问题
- 完成所有窗口的 i18n 覆盖：`SVNPreferencesWindow`（Project / About tab）、`CLIContextWindow`、`SVNBranchSelectorWindow`、`SVNLockPromptWindow`

## [1.6.0] - 2026-06-27

### Added

- 中英文国际化支持：基于 `LocalizationManager` 的简易 i18n 系统，支持语言偏好持久保存
  - 英文 locale 作为 fallback，中文简体作为可选语言
  - `Auto` 模式根据 Unity 系统语言自动检测 (`SystemLanguage.ChineseSimplified`)
- SVN Ignore Manager 窗口 (`Assets/SVN/Ignore Manager`)：可视化浏览、添加、删除 `svn:ignore` 模式
- `WiseSVNIntegration.Propdel()` — 移除 SVN 属性（用于删除空的 `svn:ignore`）
- `WiseSVNIntegration.PropsetAsync()` — `Propset` 的异步封装
- 启用之前隐藏的 Ignore Toggle 上下文菜单 (`Assets/SVN/Ignore Toggle`)
- 自动 UPM 打包脚本 `Scripts/pack.sh`，支持 `--dry-run` 模式验证

### Changed

- 偏好设置窗口新增语言选择下拉框（个人偏好最顶部）
- 覆盖图标 tooltip 随语言设置动态切换
- SceneView 覆盖文本、覆盖图标对话框、数据不完整提示均支持中英文

## [1.5.12] - (prior upstream release)

- Updated package version to 1.5.12
- Renamed some methods.
- Added "Switch Branch" context menu.
- Added warning message for partial branching.
- Simplified the PromptForAuth() -> ShellUtils.ExecutePrompt() flow for Linux.
